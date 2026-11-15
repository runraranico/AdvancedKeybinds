#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using AdvancedKeybinds.Core;
using Terraria.GameInput;

namespace AdvancedKeybinds.Binding
{
    // A is the actual pre-AK profile, not a default or a key selected from combo B.
    // B stays live and in bindings.json. Only vanilla's save result and unload use A.
    internal static class VanillaKeyProfileOverlay
    {
        internal enum Status { Pending, Owned, Protected, Restored, Unknown }
        internal sealed class Record
        {
            internal PlayerInputProfile Profile;
            internal KeyConfiguration Config;
            internal Dictionary<string, List<string>> Dictionary;
            internal string Trigger;
            internal InputMode[] Modes;
            internal List<string> List;
            internal string[] BaseKeys, ExpectedKeys;
            internal bool Existed, Known;
            internal long Version;
            internal Status State;
            internal Record Clone() => new Record {
                Profile=Profile, Config=Config, Dictionary=Dictionary, Trigger=Trigger,
                Modes=(InputMode[])Modes.Clone(), List=List, BaseKeys=(string[])BaseKeys.Clone(),
                ExpectedKeys=ExpectedKeys == null ? null : (string[])ExpectedKeys.Clone(),
                Existed=Existed, Known=Known, Version=Version, State=State
            };
        }
        private static ConditionalWeakTable<PlayerInputProfile, List<Record>> _profiles = new();
        private static bool _unloading;
        private static long _version;

        internal sealed class WriteBatch : IDisposable
        {
            internal readonly List<Record> Records = new();
            internal readonly List<UnloadedEntry> UnloadedEntries = new();
            private bool _complete;
            internal void Complete()
            {
                foreach (var r in Records)
                {
                    if (!ReferenceEquals(PlayerInput.CurrentProfile, r.Profile)
                        || !ReferenceEquals(r.Config.KeyStatus, r.Dictionary)
                        || !r.Dictionary.TryGetValue(r.Trigger, out var list) || list == null)
                    { r.State=Status.Protected; continue; }
                    r.List=list;
                    r.ExpectedKeys=list.ToArray();
                    r.State=r.Known ? Status.Owned : Status.Unknown;
                }
                foreach (var entry in UnloadedEntries) ReconcileUnloadedEntry(entry);
                _complete=true;
            }
            public void Dispose()
            {
                if (!_complete)
                    foreach (var r in Records) r.State=Status.Protected;
            }
        }

        internal static WriteBatch BeginWrite(IReadOnlyList<KeyConfiguration> configs, string trigger)
        {
            var batch=new WriteBatch();
            var profile=PlayerInput.CurrentProfile;
            if (_unloading || profile?.InputModes == null || string.IsNullOrEmpty(trigger)) return batch;
            var state=_profiles.GetOrCreateValue(profile);
            // Capture ALL before-images before the shared writer changes its first list.
            foreach (var config in configs)
            {
                if (config?.KeyStatus == null) continue;
                var modes=profile.InputModes.Where(p=>ReferenceEquals(p.Value,config)).Select(p=>p.Key).ToArray();
                if (modes.Length == 0) continue;
                var r=state.FirstOrDefault(x=>ReferenceEquals(x.Config,config) && x.Trigger==trigger);
                if (r != null)
                {
                    if (r.State==Status.Protected || r.State==Status.Restored) continue;
                    if (r.State!=Status.Unknown && !MatchesCurrent(r))
                    { r.State=Status.Protected; continue; }
                }
                else
                {
                    bool existed=config.KeyStatus.TryGetValue(trigger,out var before);
                    bool known=(!existed || before!=null) && !(before?.Any(AdvancedVanillaOwnershipMarker.IsMarker) ?? false);
                    r=new Record { Profile=profile, Config=config, Dictionary=config.KeyStatus, Trigger=trigger,
                        Modes=modes, List=before, BaseKeys=before?.ToArray() ?? Array.Empty<string>(),
                        Existed=existed, Known=known, State=known ? Status.Pending : Status.Unknown };
                    state.Add(r);
                    // Capture only the first proven A, before any writer changes B.
                    // Unloaded-only actions and unknown before-images remain untouched.
                    var unloaded=known && existed ? CaptureUnloadedEntry(r,config,trigger,before) : null;
                    if (unloaded!=null) batch.UnloadedEntries.Add(unloaded);
                }
                r.Version=++_version;
                if (r.Known) r.State=Status.Pending;
                batch.Records.Add(r);
            }
            return batch;
        }

        internal sealed class UnloadedEntry
        {
            internal Record Record;
            internal Dictionary<string,List<string>> Dictionary;
            internal List<string> List;
            internal string[] Keys;
        }
        private static UnloadedEntry CaptureUnloadedEntry(Record record, KeyConfiguration config,
            string trigger, List<string> before)
        {
            var unloaded=ReadUnloadedKeys(config);
            return unloaded!=null && unloaded.TryGetValue(trigger,out var old) && old!=null
                && !ReferenceEquals(old,before) && !SharesUnloadedKeys(config,unloaded)
                ? new UnloadedEntry { Record=record, Dictionary=unloaded, List=old, Keys=old.ToArray() } : null;
        }
        private static void ReconcileUnloadedEntry(UnloadedEntry entry)
        {
            var r=entry.Record;
            // Native WritePreferences lets an obsolete unloaded duplicate overwrite active A/B.
            // Remove only this unchanged entry, never a later/foreign value or a shared map/list.
            if (r.State==Status.Owned && MatchesCurrent(r) && OwnsAllAliases(r)
                && ReferenceEquals(ReadUnloadedKeys(r.Config),entry.Dictionary)
                && !SharesUnloadedKeys(r.Config,entry.Dictionary)
                && entry.Dictionary.TryGetValue(r.Trigger,out var list)
                && ReferenceEquals(list,entry.List) && Equal(entry.Keys,list))
                entry.Dictionary.Remove(r.Trigger);
        }
        // Optional native member: a future rename/type change must not become a load dependency.
        private static Dictionary<string,List<string>> ReadUnloadedKeys(KeyConfiguration config)
        {
            try
            {
                var field=typeof(KeyConfiguration).GetField("UnloadedModKeyStatus",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                return field?.FieldType==typeof(Dictionary<string,List<string>>)
                    ? field.GetValue(config) as Dictionary<string,List<string>> : null;
            }
            catch { return null; }
        }
        private static bool SharesUnloadedKeys(KeyConfiguration config, Dictionary<string,List<string>> keys)
            => _profiles.Select(p=>p.Key).Concat(PlayerInput.Profiles?.Values ?? Enumerable.Empty<PlayerInputProfile>())
                .Append(PlayerInput.CurrentProfile).Where(p=>p?.InputModes!=null).Distinct()
                .SelectMany(p=>p.InputModes.Values).Any(c=>c!=null && !ReferenceEquals(c,config)
                    && (ReferenceEquals(c.KeyStatus,keys) || ReferenceEquals(ReadUnloadedKeys(c),keys)));

        private static bool Equal(IReadOnlyList<string> a, IReadOnlyList<string> b)
            => AdditionalOrPersistencePolicy.Matches(a,b);
        private static bool MatchesCurrent(Record r)
            => r.Profile?.InputModes != null && r.Modes.All(m=>r.Profile.InputModes.TryGetValue(m,out var c) && ReferenceEquals(c,r.Config))
                && r.Profile.InputModes.Where(p=>ReferenceEquals(p.Value,r.Config)).All(p=>r.Modes.Contains(p.Key))
                && ReferenceEquals(r.Config.KeyStatus,r.Dictionary)
                && r.Dictionary.TryGetValue(r.Trigger,out var list) && ReferenceEquals(list,r.List)
                && Equal(r.ExpectedKeys,list);
        private static Record[] AllRecords() => _profiles.SelectMany(p=>p.Value).ToArray();

        internal sealed class CopySnapshot
        {
            internal Record Source, Before;
            internal PlayerInputProfile Target;
            internal KeyConfiguration Config;
            internal Dictionary<string, List<string>> Dictionary;
            internal List<string> List;
            internal InputMode[] Modes;
            internal bool UntrackedSource;
            internal UnloadedEntry Unloaded;
        }

        // Copy the NORMAL meaning of an explicit profile copy, not a key chosen from B.
        internal static CopySnapshot[] CaptureCopy(PlayerInputProfile target, PlayerInputProfile source,
            InputMode mode, string[] triggers)
        {
            if (_unloading || target?.InputModes == null || source?.InputModes == null || triggers == null
                || !target.InputModes.TryGetValue(mode,out var config) || config?.KeyStatus == null)
                return Array.Empty<CopySnapshot>();
            _profiles.TryGetValue(source,out var records);
            var result=new List<CopySnapshot>();
            foreach (var trigger in triggers.Distinct(StringComparer.Ordinal))
            {
                if (string.IsNullOrEmpty(trigger)
                    || !config.KeyStatus.TryGetValue(trigger,out var list) || list == null) continue;
                var r=records?.FirstOrDefault(x=>x.Trigger==trigger && x.Modes.Contains(mode)
                    && ((x.Known && x.State==Status.Owned) || (!x.Known && x.State==Status.Unknown))
                    && MatchesCurrent(x));
                bool untracked=r==null;
                if (untracked)
                {
                    // An explicit copy of normal keys also supersedes an equal-valued target B.
                    // A prior protected/changed source record is not evidence of normal ownership.
                    if ((records?.Any(x=>x.Trigger==trigger && x.Modes.Contains(mode)) ?? false)
                        || !source.InputModes.TryGetValue(mode,out var sourceConfig) || sourceConfig?.KeyStatus==null
                        || !sourceConfig.KeyStatus.TryGetValue(trigger,out var sourceList) || sourceList==null) continue;
                    bool known=!sourceList.Any(AdvancedVanillaOwnershipMarker.IsMarker)
                        && !AllRecords().Any(x=>ReferenceEquals(x.List,sourceList));
                    r=new Record { Profile=source, Config=sourceConfig, Dictionary=sourceConfig.KeyStatus,
                        Trigger=trigger, Modes=source.InputModes.Where(p=>ReferenceEquals(p.Value,sourceConfig)).Select(p=>p.Key).ToArray(),
                        List=sourceList, BaseKeys=sourceList.ToArray(), ExpectedKeys=sourceList.ToArray(),
                        Existed=true, Known=known, State=known ? Status.Owned : Status.Unknown };
                }
                else if (!OwnsAllAliases(r,allowUnknown:true)) continue;
                result.Add(new CopySnapshot { Source=r, Before=r.Clone(), Target=target, Config=config,
                    Dictionary=config.KeyStatus, List=list, UntrackedSource=untracked,
                    Unloaded=r.Known && r.Existed ? CaptureUnloadedEntry(null,config,trigger,list) : null,
                    Modes=target.InputModes.Where(p=>ReferenceEquals(p.Value,config)).Select(p=>p.Key).ToArray() });
            }
            return result.ToArray();
        }

        internal static void CompleteCopy(CopySnapshot[] snapshots)
        {
            foreach (var s in snapshots)
            {
                var r=s.Source;
                if (_unloading || r.Version!=s.Before.Version || r.State!=s.Before.State || r.Known!=s.Before.Known
                    || !MatchesCurrent(r)
                    || (s.UntrackedSource
                        ? (_profiles.TryGetValue(r.Profile,out var sourceRecords)
                            && sourceRecords.Any(x=>x.Trigger==r.Trigger && x.Modes.Intersect(r.Modes).Any()))
                        : !OwnsAllAliases(r,allowUnknown:true))
                    || s.Target?.InputModes == null
                    || !s.Modes.All(m=>s.Target.InputModes.TryGetValue(m,out var c) && ReferenceEquals(c,s.Config))
                    || !ReferenceEquals(s.Config.KeyStatus,s.Dictionary)
                    || !s.Dictionary.TryGetValue(r.Trigger,out var list) || !ReferenceEquals(list,s.List)
                    || !Equal(s.Before.ExpectedKeys,list)) continue;
                var state=_profiles.GetOrCreateValue(s.Target);
                state.RemoveAll(x=>ReferenceEquals(x.Config,s.Config) && x.Trigger==r.Trigger);
                var copy=s.Before.Clone();
                copy.Profile=s.Target; copy.Config=s.Config; copy.Dictionary=s.Dictionary;
                copy.Modes=(InputMode[])s.Modes.Clone(); copy.List=list;
                copy.ExpectedKeys=list.ToArray(); copy.Version=++_version;
                copy.State=copy.Known ? Status.Owned : Status.Unknown;
                state.Add(copy);
                if (s.Unloaded!=null)
                {
                    s.Unloaded.Record=copy;
                    ReconcileUnloadedEntry(s.Unloaded);
                }
            }
        }

        // Called only at the input-profile persistence boundary, never on input ticks.
        internal static bool CanPersistProfiles()
        {
            var records=AllRecords();
            var profiles=(PlayerInput.Profiles?.Values ?? Enumerable.Empty<PlayerInputProfile>())
                .Append(PlayerInput.CurrentProfile).Where(p=>p!=null).Distinct().ToArray();
            if (records.Any(r=>!r.Known && r.State==Status.Unknown && profiles.Contains(r.Profile)
                && MatchesCurrent(r))) return false;
            foreach (var profile in profiles)
                foreach (var pair in profile.InputModes ?? new Dictionary<InputMode,KeyConfiguration>())
                    foreach (var item in pair.Value?.KeyStatus ?? new Dictionary<string,List<string>>())
                        if (item.Value!=null && item.Value.Any(AdvancedVanillaOwnershipMarker.IsMarker)
                            && !records.Any(r=>ReferenceEquals(r.Profile,profile) && ReferenceEquals(r.Config,pair.Value)
                                && r.Trigger==item.Key && r.Known && r.State==Status.Owned
                                && MatchesCurrent(r) && OwnsAllAliases(r))) return false;
            return true;
        }

        private static bool OwnsAllAliases(Record record, bool allowUnknown=false)
        {
            var records=AllRecords();
            var profiles=_profiles.Select(p=>p.Key).Concat(PlayerInput.Profiles?.Values ?? Enumerable.Empty<PlayerInputProfile>())
                .Append(PlayerInput.CurrentProfile).Where(p=>p!=null).Distinct();
            foreach (var profile in profiles)
                foreach (var pair in profile.InputModes ?? new Dictionary<InputMode,KeyConfiguration>())
                    foreach (var item in pair.Value?.KeyStatus ?? new Dictionary<string,List<string>>())
                        if (ReferenceEquals(item.Value,record.List) && !records.Any(r=>
                            ((r.Known && r.State==Status.Owned) || (allowUnknown && !r.Known && r.State==Status.Unknown))
                            && r.Known==record.Known && MatchesCurrent(r)
                            && ReferenceEquals(r.Profile,profile) && ReferenceEquals(r.Config,pair.Value)
                            && r.Modes.Contains(pair.Key) && r.Trigger==item.Key
                            && r.Existed==record.Existed && Equal(r.BaseKeys,record.BaseKeys))) return false;
            return true;
        }

        // Veto only while unloading: ordinary binding writes and input evaluation stay unchanged.
        internal static bool PreserveDuringUnload(KeyConfiguration config, string trigger)
        {
            if (!_unloading) return false;
            config.KeyStatus.TryGetValue(trigger,out var current);
            return AllRecords().Any(r=>r.Known &&
                ((r.Trigger==trigger && (ReferenceEquals(r.Config,config)
                    || (r.Profile.InputModes?.Any(p=>ReferenceEquals(p.Value,config) && r.Modes.Contains(p.Key)) ?? false)))
                || (current!=null && ReferenceEquals(current,r.List))));
        }

        internal static void BeginUnloadAndRestore()
        {
            _unloading=true;
            var records=AllRecords();
            foreach (var r in records)
                if (r.Known && (r.State==Status.Pending || (r.State==Status.Owned && !MatchesCurrent(r)))) r.State=Status.Protected;
            var groups=records.Where(r=>r.Known && r.State==Status.Owned).GroupBy(r=>r.List).ToArray();
            foreach (var group in groups)
            {
                var members=group.ToArray();
                bool safe=members.All(r=>MatchesCurrent(r) && OwnsAllAliases(r)
                    && r.Existed==members[0].Existed && Equal(r.BaseKeys,members[0].BaseKeys));
                // Do not mutate a list borrowed by an unowned action, mode, or profile.
                if (!safe) { foreach(var r in members) r.State=Status.Protected; continue; }
                foreach (var r in members)
                {
                    if (r.Existed) { r.List.Clear(); r.List.AddRange(r.BaseKeys); }
                    else r.Dictionary.Remove(r.Trigger);
                    r.State=Status.Restored;
                }
            }
        }

        internal static void EndUnload()
        {
            _profiles=new ConditionalWeakTable<PlayerInputProfile,List<Record>>();
            _unloading=false;
        }

        internal sealed class SaveSnapshot
        {
            internal Record Original, Copy;
            internal PlayerInputProfile Current;
        }
        internal static SaveSnapshot[] CaptureSave(PlayerInputProfile profile)
        {
            if (profile==null || !_profiles.TryGetValue(profile,out var state)) return Array.Empty<SaveSnapshot>();
            var snapshots=new List<SaveSnapshot>();
            foreach (var r in state.ToArray())
            {
                if (!r.Known || r.State!=Status.Owned) continue;
                if (!MatchesCurrent(r) || !OwnsAllAliases(r)) { r.State=Status.Protected; continue; }
                snapshots.Add(new SaveSnapshot { Original=r, Copy=r.Clone(), Current=PlayerInput.CurrentProfile });
            }
            return snapshots.ToArray();
        }
        internal static Dictionary<string,object> ProjectSave(SaveSnapshot[] snapshots, Dictionary<string,object> result)
        {
            if (result==null || snapshots.Length==0) return result;
            Dictionary<string,object> top=null;
            var clones=new Dictionary<string,Dictionary<string,List<string>>>();
            foreach (var snap in snapshots)
            {
                var r=snap.Original;
                if (!ReferenceEquals(snap.Current,PlayerInput.CurrentProfile) || r.Version!=snap.Copy.Version
                    || r.State!=Status.Owned
                    || !_profiles.TryGetValue(r.Profile,out var state) || !state.Contains(r)) continue;
                if (!MatchesCurrent(r) || !OwnsAllAliases(r)) { r.State=Status.Protected; continue; }
                foreach (var mode in snap.Copy.Modes)
                {
                    string modeKey=mode switch {
                        InputMode.Keyboard=>"Mouse And Keyboard", InputMode.KeyboardUI=>"Mouse And Keyboard UI",
                        InputMode.XBoxGamepad=>"Gamepad", InputMode.XBoxGamepadUI=>"Gamepad UI", _=>null };
                    if (modeKey==null || !r.Profile.InputModes.TryGetValue(mode,out var config) || !ReferenceEquals(config,r.Config)
                        || !result.TryGetValue(modeKey,out var value)
                        || value is not Dictionary<string,List<string>> saved) continue;
                    if (saved.TryGetValue(r.Trigger,out var keys)
                        ? !Equal(snap.Copy.ExpectedKeys,keys) : snap.Copy.ExpectedKeys.Length!=0) continue;
                    top ??= new Dictionary<string,object>(result,result.Comparer);
                    if (!clones.TryGetValue(modeKey,out var clone))
                    { clone=new Dictionary<string,List<string>>(saved,saved.Comparer); clones.Add(modeKey,clone); top[modeKey]=clone; }
                    if (snap.Copy.Existed) clone[r.Trigger]=new List<string>(snap.Copy.BaseKeys);
                    else clone.Remove(r.Trigger);
                }
            }
            return top ?? result;
        }

        // A callback may invalidate projection after native Save has already captured B.
        // Reject demonstrable B in that output; never rewrite a foreign callback's value.
        internal static bool HasUnprojectedSave(PlayerInputProfile profile, SaveSnapshot[] snapshots,
            Dictionary<string,object> result)
        {
            if (result==null) return false;
            var records=_profiles.TryGetValue(profile,out var state) ? state.ToArray() : Array.Empty<Record>();
            foreach (var r in records.Concat(snapshots.Select(s=>s.Copy)))
            {
                if (r.ExpectedKeys==null || (r.Known && Equal(r.BaseKeys,r.ExpectedKeys))) continue;
                foreach (var mode in r.Modes)
                {
                    string modeKey=mode switch {
                        InputMode.Keyboard=>"Mouse And Keyboard", InputMode.KeyboardUI=>"Mouse And Keyboard UI",
                        InputMode.XBoxGamepad=>"Gamepad", InputMode.XBoxGamepadUI=>"Gamepad UI", _=>null };
                    if (modeKey!=null && result.TryGetValue(modeKey,out var value)
                        && value is Dictionary<string,List<string>> saved
                        && (saved.TryGetValue(r.Trigger,out var keys)
                            ? Equal(r.ExpectedKeys,keys) : r.ExpectedKeys.Length==0)) return true;
                }
            }
            return false;
        }

        internal sealed class RollbackSnapshot
        {
            internal PlayerInputProfile Profile;
            internal Record[] Records;
        }
        private static bool IsPad(Record r) => r.Modes.Any(m=>m==InputMode.XBoxGamepad || m==InputMode.XBoxGamepadUI);
        internal static RollbackSnapshot CaptureRollback()
        {
            var profile=PlayerInput.CurrentProfile;
            return new RollbackSnapshot { Profile=profile, Records=profile!=null && _profiles.TryGetValue(profile,out var state)
                ? state.Where(IsPad).Select(r=>r.Clone()).ToArray() : Array.Empty<Record>() };
        }
        // ONLY the existing Pad transaction's confirmed memory-rollback callback may use this.
        internal static void RestoreRollback(RollbackSnapshot snapshot)
        {
            if (snapshot?.Profile==null || !ReferenceEquals(snapshot.Profile,PlayerInput.CurrentProfile)) return;
            var state=_profiles.GetOrCreateValue(snapshot.Profile);
            state.RemoveAll(IsPad);
            foreach (var copy in snapshot.Records)
            {
                var r=copy.Clone();
                r.Version=++_version;
                if (!ReferenceEquals(r.Config.KeyStatus,r.Dictionary)
                    || !r.Dictionary.TryGetValue(r.Trigger,out var list) || !Equal(r.ExpectedKeys,list)) r.State=Status.Protected;
                else r.List=list;
                state.Add(r);
            }
        }
    }
}
