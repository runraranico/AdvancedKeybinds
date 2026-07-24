using System;
using System.Collections.Generic;
using System.Reflection;
using AdvancedKeybinds.Core;
using Terraria.GameInput;

namespace AdvancedKeybinds.Binding
{
	/// <summary>
	/// Reads and writes vanilla's own key configuration for the right slot's plain single-key taps.
	/// The new combo panel never starts a vanilla listen, so no listen-state juggling is needed here.
	/// Advanced-owned bindings write non-firing <see cref="AdvancedVanillaOwnershipMarker"/> entries
	/// so ModKeybind.GetAssignedKeys stays non-empty without dual-firing through vanilla PlayerInput.
	/// Mutations always touch Keyboard and KeyboardUI when present — other mods often EnsureDefault
	/// both modes; writing only Keyboard left UI-mode Mouse3/empty residues after load.
	/// </summary>
	internal static class VanillaRebindHelper
	{
		private static bool _onBindingChangeResolved;
		private static FieldInfo _onBindingChangeField;

		/// <summary>
		/// Resolves the KeyConfiguration that stores gameplay key bindings (the Keyboard profile).
		/// </summary>
		private static bool TryGetBindingConfiguration(out KeyConfiguration config)
		{
			config = null;
			PlayerInputProfile profile = PlayerInput.CurrentProfile;
			if (profile?.InputModes == null)
				return false;

			if (profile.InputModes.TryGetValue(InputMode.Keyboard, out config))
				return true;

			if (profile.InputModes.TryGetValue(InputMode.KeyboardUI, out config))
				return true;

			foreach (KeyValuePair<InputMode, KeyConfiguration> pair in profile.InputModes)
			{
				config = pair.Value;
				return true;
			}

			return false;
		}

		/// <summary>
		/// Keyboard + KeyboardUI configs (when present). Used for writes so both modes stay consistent.
		/// </summary>
		private static bool TryGetWritableKeyboardConfigs(List<KeyConfiguration> configs)
		{
			configs.Clear();
			PlayerInputProfile profile = PlayerInput.CurrentProfile;
			if (profile?.InputModes == null)
				return false;

			KeyConfiguration keyboard = null;
			if (profile.InputModes.TryGetValue(InputMode.Keyboard, out keyboard)
				&& keyboard?.KeyStatus != null)
				configs.Add(keyboard);

			if (profile.InputModes.TryGetValue(InputMode.KeyboardUI, out KeyConfiguration keyboardUi)
				&& keyboardUi?.KeyStatus != null
				&& !ReferenceEquals(keyboardUi, keyboard))
				configs.Add(keyboardUi);

			if (configs.Count > 0)
				return true;

			foreach (KeyValuePair<InputMode, KeyConfiguration> pair in profile.InputModes)
			{
				if (pair.Value?.KeyStatus == null)
					continue;
				configs.Add(pair.Value);
				return true;
			}

			return false;
		}

		private static readonly List<KeyConfiguration> WritableScratch = new(2);

		private static void WriteKeyStatusList(string trigger, IReadOnlyList<string> entries)
		{
			if (!TryGetWritableKeyboardConfigs(WritableScratch))
				return;

			for (int c = 0; c < WritableScratch.Count; c++)
			{
				KeyConfiguration config = WritableScratch[c];
				if (!config.KeyStatus.TryGetValue(trigger, out List<string> keys) || keys == null)
				{
					keys = new List<string>();
					config.KeyStatus[trigger] = keys;
				}

				keys.Clear();
				if (entries == null)
					continue;
				for (int i = 0; i < entries.Count; i++)
				{
					string entry = entries[i];
					if (!string.IsNullOrEmpty(entry))
						keys.Add(entry);
				}
			}

			NotifyBindingChanged();
		}

		/// <summary>True when CurrentProfile has at least one writable keyboard KeyConfiguration.</summary>
		public static bool IsProfileReady()
			=> TryGetWritableKeyboardConfigs(WritableScratch);

		/// <summary>
		/// Zero-alloc: Keyboard (+ KeyboardUI when present) KeyStatus matches ownership for the given slots.
		/// </summary>
		public static bool AssignedOwnershipMatchesSlots(
			string trigger,
			bool hold,
			string slot1,
			string slot2,
			string slot3)
		{
			if (string.IsNullOrEmpty(trigger) || !TryGetWritableKeyboardConfigs(WritableScratch))
				return false;

			for (int c = 0; c < WritableScratch.Count; c++)
			{
				KeyConfiguration config = WritableScratch[c];
				config.KeyStatus.TryGetValue(trigger, out List<string> keys);
				if (!BindingVanillaOwnershipSync.MatchesOwnershipFromSlots(keys, hold, slot1, slot2, slot3))
					return false;
			}

			return true;
		}

		/// <summary>
		/// Compares Keyboard KeyStatus to expected entries without allocating (missing/empty/None ≡ empty expected).
		/// </summary>
		public static bool AssignedKeysMatchExpected(string trigger, IReadOnlyList<string> expected)
		{
			if (string.IsNullOrEmpty(trigger) || !TryGetBindingConfiguration(out KeyConfiguration config))
				return false;

			config.KeyStatus.TryGetValue(trigger, out List<string> keys);
			return BindingVanillaOwnershipSync.MatchesExpectedKeyStatus(keys, expected);
		}

		/// <summary>Returns the first vanilla-assigned key for an action, or null if unset.</summary>
		public static string GetAssignedKey(string trigger)
		{
			if (string.IsNullOrEmpty(trigger) || !TryGetBindingConfiguration(out KeyConfiguration config))
				return null;

			if (config.KeyStatus.TryGetValue(trigger, out List<string> keys) && keys.Count > 0)
				return keys[0];

			return null;
		}

		/// <summary>Returns an independent copy of every vanilla key assigned to an action.</summary>
		public static List<string> GetAssignedKeysSnapshot(string trigger)
		{
			if (string.IsNullOrEmpty(trigger) || !TryGetBindingConfiguration(out KeyConfiguration config))
				return new List<string>();

			if (!config.KeyStatus.TryGetValue(trigger, out List<string> keys) || keys == null)
				return new List<string>();

			return new List<string>(keys);
		}

		/// <summary>Human-readable vanilla KeyStatus list for diagnostics; "(unset)" when empty/missing.</summary>
		public static string FormatAssignedKeys(string trigger)
		{
			if (string.IsNullOrEmpty(trigger) || !TryGetBindingConfiguration(out KeyConfiguration config))
				return "(unset)";

			if (!config.KeyStatus.TryGetValue(trigger, out List<string> keys) || keys == null || keys.Count == 0)
				return "(unset)";

			return string.Join("+", keys);
		}

		public static void ClearVanillaBinding(string trigger, string reason = null)
		{
			if (string.IsNullOrEmpty(trigger))
				return;

#if AK_DIAGNOSTICS
			string before = FormatAssignedKeys(trigger);
#endif
			WriteKeyStatusList(trigger, Array.Empty<string>());
#if AK_DIAGNOSTICS
			BindingMutationDiag.LogVanillaMutation(trigger, before, FormatAssignedKeys(trigger), reason ?? "ClearVanillaBinding");
#endif
		}

		/// <summary>
		/// Writes advanced-ownership markers into KeyStatus (creates the entry if missing).
		/// Markers are not Keys/Mouse names, so vanilla will not activate them; AK owns firing.
		/// </summary>
		public static void AssignAdvancedOwnershipMarkers(
			string trigger,
			IReadOnlyList<string> bindingKeys,
			string reason = null)
		{
			if (string.IsNullOrEmpty(trigger))
				return;

#if AK_DIAGNOSTICS
			string before = FormatAssignedKeys(trigger);
#endif
			List<string> markers = AdvancedVanillaOwnershipMarker.BuildMarkers(bindingKeys);
			WriteKeyStatusList(trigger, markers);
#if AK_DIAGNOSTICS
			BindingMutationDiag.LogVanillaMutation(
				trigger,
				before,
				FormatAssignedKeys(trigger),
				reason ?? "AssignAdvancedOwnershipMarkers");
#endif
		}

		public static void AssignVanillaBinding(string trigger, string key, string reason = null)
		{
			if (string.IsNullOrEmpty(trigger) || string.IsNullOrEmpty(key))
				return;

#if AK_DIAGNOSTICS
			string before = FormatAssignedKeys(trigger);
#endif
			WriteKeyStatusList(trigger, new[] { key });
#if AK_DIAGNOSTICS
			BindingMutationDiag.LogVanillaMutation(trigger, before, FormatAssignedKeys(trigger), reason ?? "AssignVanillaBinding");
#endif
		}

		/// <summary>Restores the complete vanilla key list captured before a controls-screen edit.</summary>
		public static void RestoreAssignedKeys(string trigger, IReadOnlyList<string> restoreKeys, string reason = null)
		{
			if (string.IsNullOrEmpty(trigger))
				return;

#if AK_DIAGNOSTICS
			string before = FormatAssignedKeys(trigger);
#endif
			WriteKeyStatusList(trigger, restoreKeys ?? Array.Empty<string>());
#if AK_DIAGNOSTICS
			BindingMutationDiag.LogVanillaMutation(trigger, before, FormatAssignedKeys(trigger), reason ?? "RestoreAssignedKeys");
#endif
		}

		/// <summary>
		/// Restores <paramref name="trigger"/> to the keys stored in <see cref="PlayerInput.OriginalProfiles"/>
		/// (same source as the per-row / profile defaults). Clears the list when the original had none.
		/// </summary>
		public static void ResetVanillaBindingToDefault(string trigger, string reason = null)
		{
			if (string.IsNullOrEmpty(trigger) || !IsProfileReady())
				return;

#if AK_DIAGNOSTICS
			string before = FormatAssignedKeys(trigger);
#endif
			List<string> defaults = TryGetOriginalKeys(trigger);
			WriteKeyStatusList(trigger, defaults ?? (IReadOnlyList<string>)Array.Empty<string>());
#if AK_DIAGNOSTICS
			BindingMutationDiag.LogVanillaMutation(trigger, before, FormatAssignedKeys(trigger), reason ?? "ResetVanillaBindingToDefault");
#endif
		}

		private static List<string> TryGetOriginalKeys(string trigger)
		{
			Dictionary<string, PlayerInputProfile> originals = PlayerInput.OriginalProfiles;
			if (originals == null || originals.Count == 0)
				return null;

			PlayerInputProfile current = PlayerInput.CurrentProfile;
			if (current != null
				&& !string.IsNullOrEmpty(current.Name)
				&& originals.TryGetValue(current.Name, out PlayerInputProfile named)
				&& TryReadKeys(named, trigger, out List<string> fromNamed))
			{
				return fromNamed;
			}

			foreach (KeyValuePair<string, PlayerInputProfile> pair in originals)
			{
				if (TryReadKeys(pair.Value, trigger, out List<string> found))
					return found;
			}

			return null;
		}

		private static bool TryReadKeys(PlayerInputProfile profile, string trigger, out List<string> keys)
		{
			keys = null;
			if (profile?.InputModes == null)
				return false;

			if (!profile.InputModes.TryGetValue(InputMode.Keyboard, out KeyConfiguration config)
				&& !profile.InputModes.TryGetValue(InputMode.KeyboardUI, out config))
			{
				foreach (KeyValuePair<InputMode, KeyConfiguration> pair in profile.InputModes)
				{
					config = pair.Value;
					break;
				}
			}

			if (config?.KeyStatus == null || !config.KeyStatus.TryGetValue(trigger, out List<string> source) || source == null)
				return false;

			keys = new List<string>(source);
			return true;
		}

		private static void NotifyBindingChanged()
		{
			if (!_onBindingChangeResolved)
			{
				_onBindingChangeResolved = true;
				_onBindingChangeField = typeof(PlayerInput).GetField("OnBindingChange", BindingFlags.Static | BindingFlags.NonPublic);
			}

			if (_onBindingChangeField?.GetValue(null) is System.Action callback)
				callback();
		}
	}
}
