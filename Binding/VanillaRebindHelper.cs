using System;
using System.Collections.Generic;
using System.Reflection;
using AdvancedKeybinds.Core;
using AdvancedKeybinds.Diagnostics;
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
		private static readonly List<KeyConfiguration> ModeWritableScratch = new(4);

		private static void WriteKeyStatusListToConfigs(
			List<KeyConfiguration> configs,
			string trigger,
			IReadOnlyList<string> entries)
		{
			for (int c = 0; c < configs.Count; c++)
			{
				KeyConfiguration config = configs[c];
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
		}

		private static bool TryGetWritableConfigsForModes(List<KeyConfiguration> configs, params InputMode[] modes)
		{
			configs.Clear();
			PlayerInputProfile profile = PlayerInput.CurrentProfile;
			if (profile?.InputModes == null || modes == null || modes.Length == 0)
				return false;

			for (int i = 0; i < modes.Length; i++)
			{
				if (!profile.InputModes.TryGetValue(modes[i], out KeyConfiguration config)
					|| config?.KeyStatus == null)
					continue;

				bool already = false;
				for (int c = 0; c < configs.Count; c++)
				{
					if (ReferenceEquals(configs[c], config))
					{
						already = true;
						break;
					}
				}
				if (!already)
					configs.Add(config);
			}

			return configs.Count > 0;
		}

		/// <summary>First assigned key for a specific InputMode, or null.</summary>
		public static string GetAssignedKeyForInputMode(string trigger, InputMode mode)
		{
			IReadOnlyList<string> keys = GetAssignedKeysForInputMode(trigger, mode);
			return keys != null && keys.Count > 0 ? keys[0] : null;
		}

		/// <summary>All KeyStatus entries for a trigger under one InputMode (list display source).</summary>
		public static IReadOnlyList<string> GetAssignedKeysForInputMode(string trigger, InputMode mode)
		{
			if (string.IsNullOrEmpty(trigger))
				return null;
			PlayerInputProfile profile = PlayerInput.CurrentProfile;
			if (profile?.InputModes == null
				|| !profile.InputModes.TryGetValue(mode, out KeyConfiguration config)
				|| config?.KeyStatus == null)
				return null;
			if (config.KeyStatus.TryGetValue(trigger, out List<string> keys) && keys != null && keys.Count > 0)
				return keys;
			return null;
		}

		/// <summary>
		/// Removes AKAdv markers from one mode when they are orphaned (caller decides ownership).
		/// Keeps non-marker tokens. Returns true when KeyStatus changed.
		/// </summary>
		public static bool TryStripAdvancedMarkersOnly(string trigger, InputMode mode)
		{
			if (string.IsNullOrEmpty(trigger))
				return false;
			PlayerInputProfile profile = PlayerInput.CurrentProfile;
			if (profile?.InputModes == null
				|| !profile.InputModes.TryGetValue(mode, out KeyConfiguration config)
				|| config?.KeyStatus == null)
				return false;
			if (!config.KeyStatus.TryGetValue(trigger, out List<string> keys) || keys == null || keys.Count == 0)
				return false;

			bool hadMarker = false;
			var kept = new List<string>(keys.Count);
			for (int i = 0; i < keys.Count; i++)
			{
				string token = keys[i];
				if (AdvancedVanillaOwnershipMarker.IsMarker(token))
				{
					hadMarker = true;
					continue;
				}

				if (!string.IsNullOrEmpty(token))
					kept.Add(token);
			}

			if (!hadMarker)
				return false;

			WriteKeyStatusForInputModes(trigger, kept, mode);
			return true;
		}

		/// <summary>Trigger ids that currently hold at least one AKAdv marker under <paramref name="mode"/>.</summary>
		public static List<string> ListTriggersWithAdvancedMarkers(InputMode mode)
		{
			var result = new List<string>();
			PlayerInputProfile profile = PlayerInput.CurrentProfile;
			if (profile?.InputModes == null
				|| !profile.InputModes.TryGetValue(mode, out KeyConfiguration config)
				|| config?.KeyStatus == null)
				return result;

			foreach (KeyValuePair<string, List<string>> pair in config.KeyStatus)
			{
				if (string.IsNullOrEmpty(pair.Key) || pair.Value == null)
					continue;
				for (int i = 0; i < pair.Value.Count; i++)
				{
					if (!AdvancedVanillaOwnershipMarker.IsMarker(pair.Value[i]))
						continue;
					result.Add(pair.Key);
					break;
				}
			}

			return result;
		}

		/// <summary>
		/// Token suitable for ComboPanel slots: GamePad Buttons name becomes Pad:Y etc.
		/// </summary>
		public static string GetAssignedPanelTokenForInputMode(string trigger, InputMode mode)
		{
			string raw = GetAssignedKeyForInputMode(trigger, mode);
			if (string.IsNullOrEmpty(raw))
				return null;
			if (ControlsEditTargetPolicy.TryVanillaButtonsNameToPadToken(raw, out string pad))
				return pad;
			if (GamePadToken.TryCanonicalize(raw, out string canonicalPad))
				return canonicalPad;
			return raw;
		}

		public static void WriteKeyStatusForInputModes(
			string trigger,
			IReadOnlyList<string> entries,
			params InputMode[] modes)
		{
			if (string.IsNullOrEmpty(trigger)
				|| !TryGetWritableConfigsForModes(ModeWritableScratch, modes))
				return;

			WriteKeyStatusListToConfigs(ModeWritableScratch, trigger, entries);
			NotifyBindingChanged();
		}

		private static void WriteKeyStatusList(string trigger, IReadOnlyList<string> entries)
		{
			if (!TryGetWritableKeyboardConfigs(WritableScratch))
				return;

			WriteKeyStatusListToConfigs(WritableScratch, trigger, entries);
			NotifyBindingChanged();
		}

		private static string FormatModes(InputMode[] modes)
		{
			if (modes == null || modes.Length == 0)
				return "-";
			var sb = new System.Text.StringBuilder();
			for (int i = 0; i < modes.Length; i++)
			{
				if (i > 0)
					sb.Append('+');
				sb.Append(modes[i].ToString());
			}
			return sb.ToString();
		}

		public static void ClearVanillaBindingForInputModes(
			string trigger,
			string reason,
			params InputMode[] modes)
		{
			if (string.IsNullOrEmpty(trigger))
				return;
			WriteKeyStatusForInputModes(trigger, Array.Empty<string>(), modes);
		}

		public static void AssignVanillaBindingForInputModes(
			string trigger,
			string keyOrPadToken,
			string reason,
			params InputMode[] modes)
		{
			if (string.IsNullOrEmpty(trigger) || string.IsNullOrEmpty(keyOrPadToken))
				return;

			string write = keyOrPadToken;
			if (ControlsEditTargetPolicy.TryPadTokenToVanillaButtonsName(keyOrPadToken, out string buttons))
				write = buttons;
			WriteKeyStatusForInputModes(trigger, new[] { write }, modes);
		}

		/// <summary>Writes multiple Buttons names (OR) for GamePad KeyStatus.</summary>
		public static void AssignVanillaBindingListForInputModes(
			string trigger,
			IReadOnlyList<string> keyOrPadTokens,
			string reason,
			params InputMode[] modes)
		{
			if (string.IsNullOrEmpty(trigger) || keyOrPadTokens == null || keyOrPadTokens.Count == 0)
				return;

			var writes = new List<string>(keyOrPadTokens.Count);
			for (int i = 0; i < keyOrPadTokens.Count; i++)
			{
				string raw = keyOrPadTokens[i];
				if (string.IsNullOrEmpty(raw))
					continue;
				string write = raw;
				if (ControlsEditTargetPolicy.TryPadTokenToVanillaButtonsName(raw, out string buttons))
					write = buttons;
				if (!writes.Contains(write))
					writes.Add(write);
			}

			if (writes.Count == 0)
				return;
			WriteKeyStatusForInputModes(trigger, writes, modes);
		}

		public static void AssignAdvancedOwnershipMarkersForInputModes(
			string trigger,
			IReadOnlyList<string> bindingKeys,
			string reason,
			params InputMode[] modes)
		{
			if (string.IsNullOrEmpty(trigger))
				return;
			List<string> markers = AdvancedVanillaOwnershipMarker.BuildMarkers(bindingKeys);
			WriteKeyStatusForInputModes(trigger, markers, modes);
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

		/// <summary>Ownership match for a single InputMode only (GAME/UI separation).</summary>
		public static bool AssignedOwnershipMatchesSlotsForInputMode(
			string trigger,
			InputMode mode,
			bool hold,
			string slot1,
			string slot2,
			string slot3)
		{
			if (string.IsNullOrEmpty(trigger)
				|| !TryGetWritableConfigsForModes(ModeWritableScratch, mode))
				return false;

			KeyConfiguration config = ModeWritableScratch[0];
			config.KeyStatus.TryGetValue(trigger, out List<string> keys);
			return BindingVanillaOwnershipSync.MatchesOwnershipFromSlots(keys, hold, slot1, slot2, slot3);
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

		/// <summary>
		/// Independent copy of KeyStatus for one InputMode (includes empty list when the trigger
		/// exists with Count==0). Does not fall back to other modes.
		/// </summary>
		public static List<string> GetAssignedKeysSnapshotForInputMode(string trigger, InputMode mode)
		{
			if (string.IsNullOrEmpty(trigger))
				return new List<string>();

			PlayerInputProfile profile = PlayerInput.CurrentProfile;
			if (profile?.InputModes == null
				|| !profile.InputModes.TryGetValue(mode, out KeyConfiguration config)
				|| config?.KeyStatus == null)
				return new List<string>();

			if (!config.KeyStatus.TryGetValue(trigger, out List<string> keys) || keys == null)
				return new List<string>();

			return new List<string>(keys);
		}

		/// <summary>
		/// Restores <paramref name="trigger"/> KeyStatus for exactly one InputMode.
		/// Does not write Keyboard+KeyboardUI dual lists.
		/// </summary>
		public static void RestoreAssignedKeysForInputMode(
			string trigger,
			IReadOnlyList<string> restoreKeys,
			InputMode mode,
			string reason = null)
		{
			if (string.IsNullOrEmpty(trigger))
				return;

			WriteKeyStatusForInputModes(trigger, restoreKeys ?? Array.Empty<string>(), mode);
		}

		/// <summary>Deep-copy every KeyStatus entry for one InputMode (Lists cloned).</summary>
		public static Dictionary<string, List<string>> CloneKeyStatusDictionary(InputMode mode)
		{
			var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
			PlayerInputProfile profile = PlayerInput.CurrentProfile;
			if (profile?.InputModes == null
				|| !profile.InputModes.TryGetValue(mode, out KeyConfiguration config)
				|| config?.KeyStatus == null)
				return result;

			foreach (KeyValuePair<string, List<string>> pair in config.KeyStatus)
				result[pair.Key] = pair.Value == null ? null : new List<string>(pair.Value);
			return result;
		}

		/// <summary>
		/// Replace one InputMode KeyStatus entirely from a deep snapshot
		/// (Clear then restore every key + list).
		/// </summary>
		public static bool ReplaceKeyStatusDictionary(InputMode mode, Dictionary<string, List<string>> snapshot)
		{
			PlayerInputProfile profile = PlayerInput.CurrentProfile;
			if (profile?.InputModes == null
				|| !profile.InputModes.TryGetValue(mode, out KeyConfiguration config)
				|| config?.KeyStatus == null)
				return false;

			config.KeyStatus.Clear();
			if (snapshot != null)
			{
				foreach (KeyValuePair<string, List<string>> pair in snapshot)
				{
					if (string.IsNullOrEmpty(pair.Key))
						continue;
					config.KeyStatus[pair.Key] = pair.Value == null
						? new List<string>()
						: new List<string>(pair.Value);
				}
			}

			NotifyBindingChanged();
			return true;
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

			WriteKeyStatusList(trigger, Array.Empty<string>());
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

			List<string> markers = AdvancedVanillaOwnershipMarker.BuildMarkers(bindingKeys);
			WriteKeyStatusList(trigger, markers);
		}

		public static void AssignVanillaBinding(string trigger, string key, string reason = null)
		{
			if (string.IsNullOrEmpty(trigger) || string.IsNullOrEmpty(key))
				return;

			WriteKeyStatusList(trigger, new[] { key });
		}

		/// <summary>Restores the complete vanilla key list captured before a controls-screen edit.</summary>
		public static void RestoreAssignedKeys(string trigger, IReadOnlyList<string> restoreKeys, string reason = null)
		{
			if (string.IsNullOrEmpty(trigger))
				return;

			WriteKeyStatusList(trigger, restoreKeys ?? Array.Empty<string>());
		}

		/// <summary>
		/// Restores <paramref name="trigger"/> to the keys stored in <see cref="PlayerInput.OriginalProfiles"/>
		/// (same source as the per-row / profile defaults). Clears the list when the original had none.
		/// Writes Keyboard + KeyboardUI (legacy dual write) — prefer
		/// <see cref="ResetVanillaBindingToDefaultForInputModes"/> for GAME/UI separation.
		/// </summary>
		public static void ResetVanillaBindingToDefault(string trigger, string reason = null)
		{
			if (string.IsNullOrEmpty(trigger) || !IsProfileReady())
				return;

			List<string> defaults = TryGetOriginalKeys(trigger);
			WriteKeyStatusList(trigger, defaults ?? (IReadOnlyList<string>)Array.Empty<string>());
		}

		/// <summary>
		/// Restores <paramref name="trigger"/> for each InputMode independently (no GAME↔UI copy).
		/// Source: OriginalProfiles via the same profile pick as <c>PlayerInput.ResetKeyBinding</c>
		/// (Custom → Redigit's Pick). Writes that mode's official KeyStatus exactly
		/// (<see cref="OfficialDefaultKeyRestoreLogic"/>) — no cross-mode fallback and no
		/// guesswork Sanitize (Sanitize destroyed runtime Buttons.ToString face names A/B/X/Y
		/// and KeyboardUI Menu DPad* tokens). Empty Original entry → write empty (no invented keys).
		/// </summary>
		public static void ResetVanillaBindingToDefaultForInputModes(
			string trigger,
			string reason,
			params InputMode[] modes)
		{
			if (string.IsNullOrEmpty(trigger) || !IsProfileReady() || modes == null || modes.Length == 0)
				return;

			for (int i = 0; i < modes.Length; i++)
			{
				InputMode mode = modes[i];
				// OfficialProfiles[mode][trigger] only — never Keyboard↔KeyboardUI fallback.
				List<string> defaults = TryGetOriginalKeysForInputMode(trigger, mode)
					?? new List<string>();
				// Keyboard + KeyboardUI product contract: WASD / Mouse1 only (drop Space on
				// MouseLeft, drop ActionId-named arrow echoes). No cross-device Sanitize.
				defaults = OfficialDefaultKeyRestoreLogic.ApplyKeyboardGameMovementWriteContract(
					(int)mode,
					trigger,
					defaults);

				WriteKeyStatusForInputModes(trigger, defaults, mode);
			}

		}

		/// <summary>
		/// Game→UI one-way: copy CurrentProfile KeyStatus from <paramref name="fromMode"/> to
		/// <paramref name="toMode"/> (same token list). Used after middle-click restores Game
		/// defaults so Menu matches Game — not Menu's separate OriginalProfiles entry.
		/// </summary>
		public static void CopyAssignedKeysBetweenInputModes(
			string trigger,
			InputMode fromMode,
			InputMode toMode,
			string reason = null)
		{
			if (string.IsNullOrEmpty(trigger) || !IsProfileReady() || fromMode == toMode)
				return;

			IReadOnlyList<string> source = GetAssignedKeysForInputMode(trigger, fromMode);
			var copy = source == null || source.Count == 0
				? new List<string>()
				: new List<string>(source);
			WriteKeyStatusForInputModes(trigger, copy, toMode);
		}

		private static List<string> TryGetOriginalKeys(string trigger)
		{
			// Legacy dual-write path: Keyboard only (never fall back to KeyboardUI — that mixed arrows into GAME).
			return TryGetOriginalKeysForInputMode(trigger, InputMode.Keyboard);
		}

		/// <summary>
		/// Keys from OriginalProfiles for one InputMode, using the same profile pick as
		/// <c>PlayerInput.ResetKeyBinding</c> (Custom → Redigit's Pick).
		/// Null when profile/mode/trigger is missing (caller may write empty — must not invent keys).
		/// </summary>
		public static List<string> TryGetOriginalKeysForInputMode(string trigger, InputMode mode)
		{
			Dictionary<string, PlayerInputProfile> originals = PlayerInput.OriginalProfiles;
			if (originals == null || originals.Count == 0 || string.IsNullOrEmpty(trigger))
				return null;

			string selected = PlayerInput.CurrentProfile?.Name;
			bool containsSelected = !string.IsNullOrEmpty(selected) && originals.ContainsKey(selected);
			string profileName = ControlsVanillaDefaultKeysPolicy.ResolveOriginalProfileName(
				selected,
				containsSelected);

			if (originals.TryGetValue(profileName, out PlayerInputProfile profile)
				&& TryReadKeysForMode(profile, trigger, mode, out List<string> fromNamed))
			{
				return fromNamed;
			}

			// Exact mode only — never read another InputMode as fallback (that produced S/Down on GAME).
			return null;
		}

		private static bool TryReadKeysForMode(
			PlayerInputProfile profile,
			string trigger,
			InputMode mode,
			out List<string> keys)
		{
			keys = null;
			if (profile?.InputModes == null)
				return false;
			if (!profile.InputModes.TryGetValue(mode, out KeyConfiguration config)
				|| config?.KeyStatus == null)
				return false;
			if (!config.KeyStatus.TryGetValue(trigger, out List<string> source) || source == null)
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
