#nullable disable
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using AdvancedKeybinds.Core;
using MonoMod.RuntimeDetour;
using Terraria.GameInput;

namespace AdvancedKeybinds.Patches
{
    internal static class AdditionalOrPersistencePatches
    {
        private static ConditionalWeakTable<PlayerInputProfile, ProfileState> _profiles = new();
        private static readonly object Gate = new object();
        private static long _nextGeneration;
        private static Hook _saveHook;
        private static Hook _copyHook, _inputSaveHook, _reinitializeHook;
        [ThreadStatic] private static int _inputSaveDepth;
        [ThreadStatic] private static bool _reinitializeSaving;

        private delegate Dictionary<string, object> SaveDetour(OrigProfileSave orig, PlayerInputProfile self);
        private delegate void OrigProfileCopy(PlayerInputProfile self, PlayerInputProfile source, InputMode mode, string[] keys);
        private delegate void CopyDetour(OrigProfileCopy orig, PlayerInputProfile self, PlayerInputProfile source, InputMode mode, string[] keys);
        private delegate bool OrigInputSave();
        private delegate bool InputSaveDetour(OrigInputSave orig);
        private delegate void OrigReinitialize();
        private delegate void ReinitializeDetour(OrigReinitialize orig);
        private sealed class UnprojectableProfileSave : Exception { }

        private sealed class ProfileState
        {
            internal Dictionary<(InputMode Mode, string Trigger), SyncRecord> Records = new();
        }

        private enum RecordState
        {
            Protected,
            Pending,
            Owned,
            RestoredReceipt,
            Invalidated
        }

        private sealed class SyncRecord
        {
            internal KeyConfiguration Config;
            internal Dictionary<string, List<string>> Dictionary;
            internal List<string> List;
            internal string[] BaseKeys;
            internal string[] ExpectedKeys;
            internal string[] BeforeKeys;
            internal long Generation;
            internal RecordState State;
        }

        internal sealed class SyncLease : IDisposable
        {
            internal PlayerInputProfile Profile;
            internal object StateIdentity;
            internal List<(InputMode Mode, string Trigger, long Generation)> Entries;
            internal bool Completed;
            internal bool Disposed;

            public void Dispose()
            {
                DisposeLease(this);
            }
        }

        internal static void Dispose()
        {
            Hook hook, copyHook, inputSaveHook, reinitializeHook;
            lock (Gate)
            {
                hook = _saveHook;
                _saveHook = null;
                copyHook = _copyHook; _copyHook = null;
                inputSaveHook = _inputSaveHook; _inputSaveHook = null;
                reinitializeHook = _reinitializeHook; _reinitializeHook = null;
            }

            try
            {
                hook?.Dispose();
            }
            finally
            {
                try { copyHook?.Dispose(); }
                finally
                {
                    try { inputSaveHook?.Dispose(); }
                    finally
                    {
                        try { reinitializeHook?.Dispose(); }
                        finally { lock (Gate) { _profiles = new ConditionalWeakTable<PlayerInputProfile, ProfileState>(); } }
                    }
                }
            }
        }

        internal static SyncLease BeginPlainOrSync(
            string trigger,
            IReadOnlyList<string> expectedKeys,
            ControlsGameUiSeparationPolicy.SyncModeKind kind,
            InputMode[] modes)
        {
            if (!IsAllowedTrigger(trigger) || !IsValidExpected(expectedKeys))
            {
                return NoOpLease();
            }

            if (kind == ControlsGameUiSeparationPolicy.SyncModeKind.GamePadBoth)
            {
                return NoOpLease();
            }

            var resolved = ResolveModes(kind, modes);
            if (resolved.Length == 0)
            {
                return NoOpLease();
            }

            var profile = PlayerInput.CurrentProfile;
            if (profile == null)
            {
                return NoOpLease();
            }

            lock (Gate)
            {
                var state = GetOrCreate(profile);
                var lease = new SyncLease
                {
                    Profile = profile,
                    StateIdentity = state,
                    Entries = new List<(InputMode, string, long)>(),
                };

                var expectedCopy = Copy(expectedKeys);
                var markers = HasMarkers(profile, trigger);
                foreach (var mode in resolved)
                {
                    var key = (mode, trigger);
                    if (!TryGetSlot(profile, mode, trigger, out var config, out var dict, out var list, out var before))
                    {
                        Invalidate(state, key);
                        continue;
                    }

                    if (markers)
                    {
                        Invalidate(state, key);
                        continue;
                    }

                    state.Records.TryGetValue(key, out var existing);
                    var beforeCopy = Copy(before);
                    SyncRecord record;
                    if (existing != null
                        && (existing.State == RecordState.Owned || existing.State == RecordState.Pending)
                        && ReferencesMatchCurrent(profile, mode, trigger, existing)
                        && AdditionalOrPersistencePolicy.CanContinue(
                            existing.BaseKeys,
                            existing.ExpectedKeys,
                            beforeCopy,
                            expectedCopy,
                            true,
                            false))
                    {
                        record = existing;
                        record.Config = config;
                        record.Dictionary = dict;
                        record.List = list;
                        record.ExpectedKeys = Copy(expectedCopy);
                        record.BeforeKeys = beforeCopy;
                        record.Generation = ++_nextGeneration;
                        record.State = RecordState.Pending;
                    }
                    else
                    {
                        if (existing != null && existing.State == RecordState.Owned)
                        {
                            existing.State = RecordState.Invalidated;
                        }

                        if (!AdditionalOrPersistencePolicy.CanAcquire(beforeCopy, expectedCopy, false))
                        {
                            if (existing != null)
                            {
                                existing.State = RecordState.Protected;
                            }

                            continue;
                        }

                        record = new SyncRecord
                        {
                            Config = config,
                            Dictionary = dict,
                            List = list,
                            BaseKeys = beforeCopy,
                            ExpectedKeys = Copy(expectedCopy),
                            BeforeKeys = beforeCopy,
                            Generation = ++_nextGeneration,
                            State = RecordState.Pending
                        };
                        state.Records[key] = record;
                    }

                    lease.Entries.Add((mode, trigger, record.Generation));
                }

                return lease;
            }
        }

        internal static void CompletePlainOrSync(SyncLease lease)
        {
            if (lease == null || lease.Disposed || lease.Completed)
            {
                return;
            }

            lock (Gate)
            {
                if (lease.Profile != PlayerInput.CurrentProfile)
                {
                    InvalidateLeaseEntries(lease);
                    lease.Completed = true;
                    return;
                }

                if (!TryGetState(lease.Profile, out var state) || !ReferenceEquals(state, lease.StateIdentity))
                {
                    InvalidateLeaseEntries(lease);
                    lease.Completed = true;
                    return;
                }

                foreach (var entry in lease.Entries)
                {
                    var key = (entry.Mode, entry.Trigger);
                    if (!state.Records.TryGetValue(key, out var record)
                        || record.Generation != entry.Generation
                        || record.State != RecordState.Pending)
                    {
                        continue;
                    }

                    if (ReferencesMatchCurrent(lease.Profile, entry.Mode, entry.Trigger, record)
                        && AdditionalOrPersistencePolicy.Matches(record.ExpectedKeys, record.List)
                        && !HasMarkers(lease.Profile, entry.Trigger))
                    {
                        record.State = RecordState.Owned;
                    }
                    else
                    {
                        record.State = RecordState.Invalidated;
                    }
                }

                lease.Completed = true;
            }
        }

        internal static void Apply()
        {
            lock (Gate)
            {
                if (_saveHook != null && _saveHook.IsApplied)
                {
                    ApplyRestoreBoundaries();
                    return;
                }

                if (_saveHook != null)
                {
                    var stale = _saveHook;
                    _saveHook = null;
                    stale.Dispose();
                }

                _profiles = new ConditionalWeakTable<PlayerInputProfile, ProfileState>();
            }

            var saveMethod = typeof(PlayerInputProfile).GetMethod(
                "Save",
                BindingFlags.Instance | BindingFlags.Public,
                null,
                Type.EmptyTypes,
                null);
            if (saveMethod == null)
            {
                throw new MissingMethodException(typeof(PlayerInputProfile).FullName, "Save");
            }

            if (saveMethod.IsStatic || saveMethod.IsGenericMethod
                || saveMethod.ReturnType != typeof(Dictionary<string, object>))
            {
                throw new InvalidOperationException("save-hook-invalid");
            }

            SaveDetour detour = SaveProfile;
            var hook = new Hook(saveMethod, detour);
            if (!hook.IsApplied)
            {
                hook.Dispose();
                throw new InvalidOperationException("save-hook-not-applied");
            }

            lock (Gate)
            {
                _saveHook = hook;
            }
            ApplyRestoreBoundaries();
        }

        // Optional boundaries: an unavailable/changed API must not prevent AK loading.
        private static Hook TryCreateRestoreHook(Type type, string name, BindingFlags flags,
            Type[] arguments, Type returns, Delegate detour)
        {
            Hook hook=null;
            try
            {
                var method=type.GetMethod(name,flags,null,arguments,null);
                if (method==null || method.IsGenericMethod || method.ReturnType!=returns) return null;
                hook=new Hook(method,detour);
                if (hook.IsApplied) return hook;
            }
            catch { }
            try { hook?.Dispose(); } catch { }
            return null;
        }

        private static void ApplyRestoreBoundaries()
        {
            lock (Gate)
            {
                if (_copyHook==null)
                    _copyHook=TryCreateRestoreHook(typeof(PlayerInputProfile),"CopyKeysFrom",
                        BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,
                        new[] { typeof(PlayerInputProfile),typeof(InputMode),typeof(string[]) },typeof(void),
                        (CopyDetour)CopyProfileKeys);
                if (_inputSaveHook==null)
                    _inputSaveHook=TryCreateRestoreHook(typeof(PlayerInput),"Save",
                        BindingFlags.Static|BindingFlags.Public,Type.EmptyTypes,typeof(bool),
                        (InputSaveDetour)SaveInputProfiles);
                if (_reinitializeHook==null)
                    _reinitializeHook=TryCreateRestoreHook(typeof(PlayerInput),"ReInitialize",
                        BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic,Type.EmptyTypes,typeof(void),
                        (ReinitializeDetour)ReinitializeProfiles);
            }
        }

        private static void ReinitializeProfiles(OrigReinitialize orig)
        {
            // Rebuild discards profile objects. Persist the existing A projection before native Load reads disk.
            // Do not save startup defaults over preferences which have not been loaded yet.
            if (!_reinitializeSaving && _inputSaveHook?.IsApplied==true && _saveHook?.IsApplied==true)
            {
                try
                {
                    _reinitializeSaving=true;
                    var preferences=typeof(Terraria.Main).GetField("InputProfiles",BindingFlags.Public|BindingFlags.Static)?.GetValue(null);
                    var getKeys=preferences?.GetType().GetMethod("GetAllKeys",BindingFlags.Public|BindingFlags.Instance,null,Type.EmptyTypes,null);
                    if (getKeys?.Invoke(preferences,null) is IEnumerable<string> keys)
                        foreach (var key in keys)
                            if (key=="Selected Profile")
                            {
                                var save=typeof(PlayerInput).GetMethod("Save",BindingFlags.Public|BindingFlags.Static,null,Type.EmptyTypes,null);
                                if (save?.ReturnType==typeof(bool)) save.Invoke(null,null);
                                break;
                            }
                }
                catch { } // Disk/API failure must not block native input initialization.
                finally { _reinitializeSaving=false; }
            }
            orig(); // Preserve native execution and its original exception behavior.
        }

        private static void CopyProfileKeys(OrigProfileCopy orig, PlayerInputProfile self,
            PlayerInputProfile source, InputMode mode, string[] keys)
        {
            var snapshots=Array.Empty<global::AdvancedKeybinds.Binding.VanillaKeyProfileOverlay.CopySnapshot>();
            try { snapshots=global::AdvancedKeybinds.Binding.VanillaKeyProfileOverlay.CaptureCopy(self,source,mode,keys); }
            catch { }
            orig(self,source,mode,keys); // Preserve the native operation, including its exception behavior.
            try { global::AdvancedKeybinds.Binding.VanillaKeyProfileOverlay.CompleteCopy(snapshots); }
            catch { }
        }

        private static bool SaveInputProfiles(OrigInputSave orig)
        {
            if (!global::AdvancedKeybinds.Binding.VanillaKeyProfileOverlay.CanPersistProfiles()) return false;
            try { _inputSaveDepth++; return orig(); }
            catch (UnprojectableProfileSave) { return false; }
            finally { _inputSaveDepth--; }
        }

        private static Dictionary<string,object> GuardProfileSave(PlayerInputProfile profile,
            global::AdvancedKeybinds.Binding.VanillaKeyProfileOverlay.SaveSnapshot[] snapshots,
            Dictionary<string,object> result)
        {
            if (_inputSaveDepth>0 && result!=null)
            {
                if (!global::AdvancedKeybinds.Binding.VanillaKeyProfileOverlay.CanPersistProfiles()
                    || global::AdvancedKeybinds.Binding.VanillaKeyProfileOverlay.HasUnprojectedSave(profile,snapshots,result))
                    throw new UnprojectableProfileSave();
                foreach (var value in result.Values)
                    if (value is Dictionary<string,List<string>> mode)
                        foreach (var keys in mode.Values)
                            if (keys!=null && keys.Exists(AdvancedVanillaOwnershipMarker.IsMarker))
                                throw new UnprojectableProfileSave();
            }
            return result;
        }

        internal static void RestoreOwnedProfiles()
        {
            lock (Gate)
            {
                var profiles = CollectProfilesForRestore();
                for (var i = 0; i < profiles.Count; i++)
                {
                    Validate(profiles[i]);
                }

                var slots = new List<RestoreSlot>();
                for (var i = 0; i < profiles.Count; i++)
                {
                    var profile = profiles[i];
                    if (!TryGetState(profile, out var state))
                    {
                        continue;
                    }

                    foreach (var pair in state.Records)
                    {
                        var record = pair.Value;
                        if (record == null || record.State != RecordState.Owned)
                        {
                            continue;
                        }

                        var mode = pair.Key.Mode;
                        var trigger = pair.Key.Trigger;
                        if (!ReferencesMatchCurrent(profile, mode, trigger, record)
                            || !AdditionalOrPersistencePolicy.Matches(record.ExpectedKeys, record.List)
                            || HasMarkers(profile, trigger))
                        {
                            continue;
                        }

                        slots.Add(new RestoreSlot(profile, mode, trigger, record));
                    }
                }

                var groups = new Dictionary<List<string>, List<RestoreSlot>>(ReferenceEqualityComparer.Instance);
                for (var i = 0; i < slots.Count; i++)
                {
                    var slot = slots[i];
                    if (!groups.TryGetValue(slot.Record.List, out var members))
                    {
                        members = new List<RestoreSlot>();
                        groups.Add(slot.Record.List, members);
                    }

                    members.Add(slot);
                }

                var safeGroups = new List<KeyValuePair<List<string>, List<RestoreSlot>>>();
                foreach (var group in groups)
                {
                    var sharedList = group.Key;
                    var members = group.Value;
                    if (!IsGroupSafe(profiles, sharedList, members))
                    {
                        MarkGroupProtected(members);
                        continue;
                    }

                    safeGroups.Add(new KeyValuePair<List<string>, List<RestoreSlot>>(sharedList, members));
                }

                for (var g = 0; g < safeGroups.Count; g++)
                {
                    var sharedList = safeGroups[g].Key;
                    var members = safeGroups[g].Value;
                    sharedList.Clear();
                    sharedList.AddRange(members[0].Record.BaseKeys);
                    for (var i = 0; i < members.Count; i++)
                    {
                        members[i].Record.State = RecordState.RestoredReceipt;
                    }
                }
            }
        }

        internal static bool TryRestoreCurrentPlainOr(
            string trigger,
            InputMode mode,
            IReadOnlyList<string> expectedKeys)
        {
            if (!IsAllowedTrigger(trigger)
                || (mode != InputMode.Keyboard && mode != InputMode.KeyboardUI)
                || !IsValidExpected(expectedKeys))
            {
                return false;
            }

            RestoreOwnedProfiles();

            lock (Gate)
            {
                var profile = PlayerInput.CurrentProfile;
                if (profile == null || !TryGetState(profile, out var state))
                {
                    return false;
                }

                if (!state.Records.TryGetValue((mode, trigger), out var record) || record == null)
                {
                    return false;
                }

                if (record.State != RecordState.RestoredReceipt)
                {
                    return false;
                }

                if (!ReferencesMatchCurrent(profile, mode, trigger, record)
                    || !AdditionalOrPersistencePolicy.Matches(record.BaseKeys, record.List)
                    || !AdditionalOrPersistencePolicy.Matches(record.ExpectedKeys, expectedKeys)
                    || HasMarkers(profile, trigger))
                {
                    return false;
                }

                record.State = RecordState.Invalidated;
                return true;
            }
        }

        internal delegate Dictionary<string, object> OrigProfileSave(PlayerInputProfile self);

        private sealed class SaveSnapshot
        {
            internal ProfileState StateIdentity;
            internal SyncRecord Record;
            internal long Generation;
            internal InputMode Mode;
            internal string Trigger;
            internal KeyConfiguration Config;
            internal Dictionary<string, List<string>> Dictionary;
            internal List<string> List;
            internal string[] BaseKeys;
            internal string[] ExpectedKeys;
        }

        internal static Dictionary<string, object> SaveProfile(OrigProfileSave orig, PlayerInputProfile self)
        {
            Validate(self);
            var snapshots = new List<SaveSnapshot>(4);
            lock (Gate)
            {
                if (self != null && TryGetState(self, out var state))
                {
                    foreach (var pair in state.Records)
                    {
                        var r = pair.Value;
                        if (r == null || (r.State != RecordState.Owned && r.State != RecordState.Pending)
                            || !ReferencesMatchCurrent(self, pair.Key.Mode, pair.Key.Trigger, r)
                            || HasMarkers(self, pair.Key.Trigger)
                            || !AdditionalOrPersistencePolicy.Matches(r.ExpectedKeys, r.List))
                            continue;
                        snapshots.Add(new SaveSnapshot
                        {
                            StateIdentity = state, Record = r, Generation = r.Generation,
                            Mode = pair.Key.Mode, Trigger = pair.Key.Trigger,
                            Config = r.Config, Dictionary = r.Dictionary, List = r.List,
                            BaseKeys = Copy(r.BaseKeys), ExpectedKeys = Copy(r.ExpectedKeys)
                        });
                    }
                }
            }

            var overlaySave = global::AdvancedKeybinds.Binding.VanillaKeyProfileOverlay.CaptureSave(self);
            var result = global::AdvancedKeybinds.Binding.VanillaKeyProfileOverlay.ProjectSave(overlaySave, orig(self));
            Validate(self);
            if (result == null || snapshots.Count == 0)
                return GuardProfileSave(self,overlaySave,result);
            lock (Gate)
            {
                if (!TryGetState(self, out var state))
                    return GuardProfileSave(self,overlaySave,result);
                Dictionary<string, object> top = null;
                var clones = new Dictionary<string, Dictionary<string, List<string>>>(result.Comparer);
                foreach (var snap in snapshots)
                {
                    if (!ReferenceEquals(snap.StateIdentity, state)
                        || !state.Records.TryGetValue((snap.Mode, snap.Trigger), out var r)
                        || !ReferenceEquals(r, snap.Record) || r.Generation != snap.Generation
                        || !ReferenceEquals(r.Config, snap.Config)
                        || !ReferenceEquals(r.Dictionary, snap.Dictionary)
                        || !ReferenceEquals(r.List, snap.List)
                        || !ReferencesMatchCurrent(self, snap.Mode, snap.Trigger, r)
                        || !AdditionalOrPersistencePolicy.Matches(snap.BaseKeys, r.BaseKeys)
                        || !AdditionalOrPersistencePolicy.Matches(snap.ExpectedKeys, r.ExpectedKeys))
                        continue;
                    var modeKey = snap.Mode == InputMode.Keyboard
                        ? "Mouse And Keyboard" : "Mouse And Keyboard UI";
                    if (!result.TryGetValue(modeKey, out var modeValue)
                        || modeValue is not Dictionary<string, List<string>> mode
                        || !mode.TryGetValue(snap.Trigger, out var saved)
                        || !AdditionalOrPersistencePolicy.ShouldProject(
                            r.State == RecordState.Owned || r.State == RecordState.Pending,
                            true, HasMarkers(self, snap.Trigger), snap.ExpectedKeys, r.List, saved))
                        continue;
                    if (top == null)
                        top = new Dictionary<string, object>(result, result.Comparer);
                    if (!clones.TryGetValue(modeKey, out var clone))
                    {
                        clone = new Dictionary<string, List<string>>(mode, mode.Comparer);
                        clones.Add(modeKey, clone);
                        top[modeKey] = clone;
                    }
                    clone[snap.Trigger] = new List<string>(snap.BaseKeys);
                }
                return GuardProfileSave(self,overlaySave,top ?? result);
            }
        }

        private static void DisposeLease(SyncLease lease)
        {
            if (lease == null || lease.Disposed)
            {
                return;
            }

            lock (Gate)
            {
                if (!lease.Completed
                    && lease.Profile != null
                    && TryGetState(lease.Profile, out var state)
                    && ReferenceEquals(state, lease.StateIdentity))
                {
                    InvalidateLeaseEntries(lease, state);
                }

                lease.Profile = null;
                lease.StateIdentity = null;
                lease.Entries = null;
                lease.Disposed = true;
            }
        }

        private static void InvalidateLeaseEntries(SyncLease lease, ProfileState state = null)
        {
            if (lease?.Entries == null)
            {
                return;
            }

            if (state == null && (lease.Profile == null || !TryGetState(lease.Profile, out state)))
            {
                return;
            }

            foreach (var entry in lease.Entries)
            {
                var key = (entry.Mode, entry.Trigger);
                if (state.Records.TryGetValue(key, out var record)
                    && record.Generation == entry.Generation
                    && record.State == RecordState.Pending)
                {
                    record.State = RecordState.Invalidated;
                }
            }
        }

        private static void Validate(PlayerInputProfile profile)
        {
            if (profile == null)
            {
                return;
            }

            lock (Gate)
            {
                if (!TryGetState(profile, out var state))
                {
                    return;
                }

                foreach (var pair in state.Records)
                {
                    var record = pair.Value;
                    if (record == null)
                    {
                        continue;
                    }

                    var mode = pair.Key.Mode;
                    var trigger = pair.Key.Trigger;
                    if (record.State == RecordState.Owned)
                    {
                        if (!ReferencesMatchCurrent(profile, mode, trigger, record)
                            || !AdditionalOrPersistencePolicy.Matches(record.ExpectedKeys, record.List)
                            || HasMarkers(profile, trigger))
                        {
                            record.State = RecordState.Invalidated;
                        }
                    }
                    else if (record.State == RecordState.Pending)
                    {
                        if (!ReferencesMatchCurrent(profile, mode, trigger, record) || HasMarkers(profile, trigger))
                        {
                            record.State = RecordState.Invalidated;
                            continue;
                        }

                        if (!AdditionalOrPersistencePolicy.Matches(record.ExpectedKeys, record.List)
                            && !AdditionalOrPersistencePolicy.Matches(record.BeforeKeys, record.List))
                        {
                            record.State = RecordState.Invalidated;
                        }
                    }
                }
            }
        }

        private static SyncLease NoOpLease()
        {
            return new SyncLease { Entries = new List<(InputMode, string, long)>(), Completed = true };
        }

        private static ProfileState GetOrCreate(PlayerInputProfile profile)
        {
            if (!_profiles.TryGetValue(profile, out var state))
            {
                state = new ProfileState();
                _profiles.Add(profile, state);
            }

            return state;
        }

        private static bool TryGetState(PlayerInputProfile profile, out ProfileState state)
        {
            return _profiles.TryGetValue(profile, out state);
        }

        private static void Invalidate(ProfileState state, (InputMode Mode, string Trigger) key)
        {
            if (state.Records.TryGetValue(key, out var record) && record != null)
            {
                record.State = RecordState.Invalidated;
            }
        }

        private static bool IsAllowedTrigger(string trigger)
        {
            return string.Equals(trigger, "Inventory", StringComparison.Ordinal)
                || string.Equals(trigger, "MenuInquiry", StringComparison.Ordinal);
        }

        private static bool IsValidExpected(IReadOnlyList<string> expectedKeys)
        {
            return expectedKeys != null
                && expectedKeys.Count == 2
                && !string.IsNullOrEmpty(expectedKeys[0])
                && !string.IsNullOrEmpty(expectedKeys[1])
                && !string.Equals(expectedKeys[0], expectedKeys[1], StringComparison.Ordinal);
        }

        private static InputMode[] ResolveModes(ControlsGameUiSeparationPolicy.SyncModeKind kind, InputMode[] modes)
        {
            if (kind == ControlsGameUiSeparationPolicy.SyncModeKind.KeyboardBoth)
            {
                return new[] { InputMode.Keyboard, InputMode.KeyboardUI };
            }

            if (kind != ControlsGameUiSeparationPolicy.SyncModeKind.SingleInputMode)
            {
                return Array.Empty<InputMode>();
            }

            if (modes == null || modes.Length == 0)
            {
                return Array.Empty<InputMode>();
            }

            var list = new List<InputMode>();
            for (var i = 0; i < modes.Length; i++)
            {
                var mode = modes[i];
                if (mode != InputMode.Keyboard && mode != InputMode.KeyboardUI)
                {
                    return Array.Empty<InputMode>();
                }

                if (!list.Contains(mode))
                {
                    list.Add(mode);
                }
            }

            return list.ToArray();
        }

        private static bool TryGetSlot(
            PlayerInputProfile profile,
            InputMode mode,
            string trigger,
            out KeyConfiguration config,
            out Dictionary<string, List<string>> dict,
            out List<string> list,
            out IReadOnlyList<string> before)
        {
            config = null;
            dict = null;
            list = null;
            before = null;
            if (profile?.InputModes == null
                || !profile.InputModes.TryGetValue(mode, out config)
                || config?.KeyStatus == null)
            {
                return false;
            }

            dict = config.KeyStatus;
            if (!dict.TryGetValue(trigger, out list) || list == null)
            {
                return false;
            }

            before = list;
            return true;
        }

        private static bool ReferencesMatchCurrent(
            PlayerInputProfile profile,
            InputMode mode,
            string trigger,
            SyncRecord record)
        {
            if (!TryGetSlot(profile, mode, trigger, out var config, out var dict, out var list, out _))
            {
                return false;
            }

            return ReferenceEquals(record.Config, config)
                && ReferenceEquals(record.Dictionary, dict)
                && ReferenceEquals(record.List, list);
        }

        private static bool HasMarkers(PlayerInputProfile profile, string trigger)
        {
            if (TryGetSlot(profile, InputMode.Keyboard, trigger, out _, out _, out var kb, out _))
            {
                for (var i = 0; i < kb.Count; i++)
                {
                    if (AdvancedVanillaOwnershipMarker.IsMarker(kb[i]))
                    {
                        return true;
                    }
                }
            }

            if (TryGetSlot(profile, InputMode.KeyboardUI, trigger, out _, out _, out var ku, out _))
            {
                for (var i = 0; i < ku.Count; i++)
                {
                    if (AdvancedVanillaOwnershipMarker.IsMarker(ku[i]))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static string[] Copy(IReadOnlyList<string> source)
        {
            if (source == null)
            {
                return Array.Empty<string>();
            }

            var copy = new string[source.Count];
            for (var i = 0; i < source.Count; i++)
            {
                copy[i] = source[i];
            }

            return copy;
        }

        private sealed class RestoreSlot
        {
            internal readonly PlayerInputProfile Profile;
            internal readonly InputMode Mode;
            internal readonly string Trigger;
            internal readonly SyncRecord Record;

            internal RestoreSlot(PlayerInputProfile profile, InputMode mode, string trigger, SyncRecord record)
            {
                Profile = profile;
                Mode = mode;
                Trigger = trigger;
                Record = record;
            }
        }

        private static List<PlayerInputProfile> CollectProfilesForRestore()
        {
            var profiles = new List<PlayerInputProfile>();
            if (PlayerInput.Profiles != null)
            {
                foreach (var profile in PlayerInput.Profiles.Values)
                {
                    if (profile == null)
                    {
                        continue;
                    }

                    var exists = false;
                    for (var i = 0; i < profiles.Count; i++)
                    {
                        if (ReferenceEquals(profiles[i], profile))
                        {
                            exists = true;
                            break;
                        }
                    }

                    if (!exists)
                    {
                        profiles.Add(profile);
                    }
                }
            }

            var current = PlayerInput.CurrentProfile;
            if (current != null)
            {
                var exists = false;
                for (var i = 0; i < profiles.Count; i++)
                {
                    if (ReferenceEquals(profiles[i], current))
                    {
                        exists = true;
                        break;
                    }
                }

                if (!exists)
                {
                    profiles.Add(current);
                }
            }

            return profiles;
        }

        private static bool OrdinalSequenceEqual(string[] left, string[] right)
        {
            if (ReferenceEquals(left, right))
            {
                return true;
            }

            if (left == null || right == null || left.Length != right.Length)
            {
                return false;
            }

            for (var i = 0; i < left.Length; i++)
            {
                if (!string.Equals(left[i], right[i], StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsKnownGroupMember(
            IReadOnlyList<RestoreSlot> members,
            PlayerInputProfile profile,
            InputMode mode,
            string trigger)
        {
            for (var i = 0; i < members.Count; i++)
            {
                var member = members[i];
                if (ReferenceEquals(member.Profile, profile)
                    && member.Mode == mode
                    && string.Equals(member.Trigger, trigger, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsGroupSafe(
            IReadOnlyList<PlayerInputProfile> profiles,
            List<string> sharedList,
            IReadOnlyList<RestoreSlot> members)
        {
            if (members.Count == 0)
            {
                return false;
            }

            for (var i = 1; i < members.Count; i++)
            {
                if (!OrdinalSequenceEqual(members[0].Record.BaseKeys, members[i].Record.BaseKeys)
                    || !OrdinalSequenceEqual(members[0].Record.ExpectedKeys, members[i].Record.ExpectedKeys))
                {
                    return false;
                }
            }

            for (var p = 0; p < profiles.Count; p++)
            {
                var profile = profiles[p];
                if (profile?.InputModes == null)
                {
                    continue;
                }

                foreach (var modeEntry in profile.InputModes)
                {
                    var config = modeEntry.Value;
                    if (config?.KeyStatus == null)
                    {
                        continue;
                    }

                    foreach (var triggerEntry in config.KeyStatus)
                    {
                        if (!ReferenceEquals(triggerEntry.Value, sharedList))
                        {
                            continue;
                        }

                        if (!IsKnownGroupMember(members, profile, modeEntry.Key, triggerEntry.Key))
                        {
                            return false;
                        }
                    }
                }
            }

            return true;
        }

        private static void MarkGroupProtected(IReadOnlyList<RestoreSlot> members)
        {
            for (var i = 0; i < members.Count; i++)
            {
                members[i].Record.State = RecordState.Protected;
            }
        }
    }
}
