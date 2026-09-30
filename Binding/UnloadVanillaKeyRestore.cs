using System;
using System.Collections.Generic;
using AdvancedKeybinds.Core;
using AdvancedKeybinds.Features.DoubleTap;
using Terraria.GameInput;

namespace AdvancedKeybinds.Binding
{
	/// <summary>
	/// Unload/disable only: replace AKAdv ownership markers with one vanilla-fireable key from each
	/// advanced combo, then persist via <see cref="PlayerInput.Save"/>. Does not rewrite bindings.json.
	/// </summary>
	internal static class UnloadVanillaKeyRestore
	{
		private static readonly List<string> RestoreScratch = new(4);

		/// <summary>
		/// Restores Keyboard + KeyboardUI KeyStatus for advanced-owned combos that still carry AKAdv
		/// markers, then saves input profiles.
		/// Call while BindingStorage still holds combos (before Combos.Clear).
		/// </summary>
		public static int RestoreAdvancedOwnedFromCombos(
			IEnumerable<KeyValuePair<string, ComboBinding>> combos,
			out bool savedProfiles)
		{
			savedProfiles = false;
			if (combos == null)
			{
				savedProfiles = TrySavePlayerInputProfiles();
				return 0;
			}

			int restored = 0;
			foreach (KeyValuePair<string, ComboBinding> pair in combos)
			{
				ComboBinding combo = pair.Value;
				if (combo == null || (combo.IsEmpty && !combo.DoubleTap))
					continue;

				string actionId = pair.Key;
				if (string.IsNullOrEmpty(actionId))
					continue;

				// DT: dedicated path only. Never fall through to primary-key restore,
				// even when TryRestore returns false (wrong form / no marker on DT modes).
				if (combo.DoubleTap)
				{
					if (TryRestoreDoubleTapStorage(actionId, combo))
						restored++;
					continue;
				}

				// Only touch actions that still carry AKAdv markers (AK wrote them).
				if (!KeyStatusHasOwnershipMarker(actionId))
					continue;

				if (combo.RequiresAdvancedStorage())
				{
					if (UnloadVanillaKeyRestorePolicy.TrySelectPrimaryVanillaKey(
						combo.Slot1, combo.Slot2, combo.Slot3, out string vanillaKey)
						&& !string.IsNullOrEmpty(vanillaKey))
					{
						VanillaRebindHelper.AssignVanillaBinding(
							actionId, vanillaKey, "unload-restore-combo-key");
					}
					else
					{
						VanillaRebindHelper.ClearVanillaBinding(
							actionId, "unload-clear-unconvertible");
					}
				}
				else
				{
					// Plain single-tap in Combos but marker residue left — write the Keys/Mouse name.
					IReadOnlyList<string> keys = combo.Keys;
					string plain = keys != null && keys.Count > 0 ? keys[0] : null;
					if (!string.IsNullOrEmpty(plain)
						&& UnloadVanillaKeyRestorePolicy.TryConvertToVanillaKeyStatusToken(
							plain, out string vanillaPlain)
						&& !string.IsNullOrEmpty(vanillaPlain))
					{
						VanillaRebindHelper.AssignVanillaBinding(
							actionId, vanillaPlain, "unload-restore-plain-residue");
					}
					else
					{
						VanillaRebindHelper.ClearVanillaBinding(
							actionId, "unload-clear-plain-residue");
					}
				}

				restored++;
			}

			savedProfiles = TrySavePlayerInputProfiles();
			return restored;
		}

		/// <summary>
		/// DT unload: bare MOD or GP:MOD storage only; keep non-marker tokens; restore convertible OR
		/// (and alternate pad OR) once — never primary Slot1.
		/// Registration may already be cleared at unload — do not require IsEligibleStorage.
		/// </summary>
		private static bool TryRestoreDoubleTapStorage(string storageKey, ComboBinding combo)
		{
			if (!IsDoubleTapUnloadStorageForm(storageKey))
				return false;

			string trigger = ControlsGameUiSeparationPolicy.ToTriggerActionId(storageKey);
			bool gamePadStorage = ControlsEditTargetPolicy.IsGamePadStorageKey(storageKey)
				|| ControlsGameUiSeparationPolicy.IsGamePadLegacySharedStorageKey(storageKey);
			InputMode[] modes = gamePadStorage
				? new[] { InputMode.XBoxGamepad, InputMode.XBoxGamepadUI }
				: new[] { InputMode.Keyboard, InputMode.KeyboardUI };
			bool anyModeChanged = false;

			for (int m = 0; m < modes.Length; m++)
			{
				InputMode mode = modes[m];
				if (!ModeHasMarkerForTrigger(trigger, mode))
					continue;

				RestoreScratch.Clear();
				IReadOnlyList<string> assigned =
					VanillaRebindHelper.GetAssignedKeysForInputMode(trigger, mode);
				if (assigned != null)
				{
					for (int i = 0; i < assigned.Count; i++)
					{
						string token = assigned[i];
						if (!AdvancedVanillaOwnershipMarker.IsMarker(token))
							RestoreScratch.Add(token);
					}
				}

				TryAppendDoubleTapOrRestoreToken(combo, gamePadStorage, RestoreScratch);
				VanillaRebindHelper.RestoreAssignedKeysForInputMode(
					trigger,
					RestoreScratch,
					mode,
					"unload-restore-dt-or-only");
				anyModeChanged = true;
			}

			return anyModeChanged;
		}

		private static void TryAppendDoubleTapOrRestoreToken(
			ComboBinding combo,
			bool gamePadStorage,
			List<string> restoreList)
		{
			// AdditionalOr and Alternate are independent — do not early-return after OR.
			if (combo.HasAdditionalOr)
				TryAppendConvertedOrToken(combo.AdditionalOrToken, gamePadStorage, restoreList);
			if (combo.HasAlternateGamePad)
				TryAppendConvertedOrToken(combo.AlternateGamePadToken, gamePadStorage, restoreList);
		}

		private static void TryAppendConvertedOrToken(
			string token,
			bool gamePadStorage,
			List<string> restoreList)
		{
			if (string.IsNullOrEmpty(token))
				return;

			string vanilla = null;
			if (gamePadStorage)
			{
				if (!ControlsEditTargetPolicy.TryPadTokenToVanillaButtonsName(token, out vanilla)
					|| string.IsNullOrEmpty(vanilla))
					return;
			}
			else if (!UnloadVanillaKeyRestorePolicy.TryConvertToVanillaKeyStatusToken(token, out vanilla)
				|| string.IsNullOrEmpty(vanilla))
			{
				return;
			}

			for (int i = 0; i < restoreList.Count; i++)
			{
				if (string.Equals(restoreList[i], vanilla, StringComparison.OrdinalIgnoreCase))
					return;
			}

			restoreList.Add(vanilla);
		}

		private static bool ModeHasMarkerForTrigger(string trigger, InputMode mode)
		{
			PlayerInputProfile profile = PlayerInput.CurrentProfile;
			if (profile?.InputModes == null || string.IsNullOrEmpty(trigger))
				return false;
			if (!profile.InputModes.TryGetValue(mode, out KeyConfiguration config)
				|| config?.KeyStatus == null)
				return false;
			if (!config.KeyStatus.TryGetValue(trigger, out List<string> keys) || keys == null)
				return false;

			for (int i = 0; i < keys.Count; i++)
			{
				if (AdvancedVanillaOwnershipMarker.IsMarker(keys[i]))
					return true;
			}

			return false;
		}

		/// <summary>
		/// Unload-time form check: bare ModName/Key or GP:ModName/Key only (no KU:/GP:G:/GP:U:).
		/// Does not consult the registration table (may already be empty).
		/// </summary>
		private static bool IsDoubleTapUnloadStorageForm(string storageKey)
		{
			if (string.IsNullOrEmpty(storageKey))
				return false;
			if (storageKey.StartsWith("KU:", StringComparison.Ordinal)
				|| storageKey.StartsWith("GP:G:", StringComparison.Ordinal)
				|| storageKey.StartsWith("GP:U:", StringComparison.Ordinal))
				return false;
			string bare = storageKey.StartsWith("GP:", StringComparison.Ordinal)
				? storageKey.Substring(3)
				: storageKey;
			return ControlsGameUiSeparationPolicy.IsModKeybindActionId(bare);
		}

		/// <summary>Official tML path used by <c>Main.SaveSettings</c> — writes input profiles.json.</summary>
		public static bool TrySavePlayerInputProfiles()
		{
			try
			{
				return PlayerInput.Save();
			}
			catch
			{
				return false;
			}
		}

		/// <summary>
		/// True when Keyboard or KeyboardUI KeyStatus still contains an AKAdv marker for the action.
		/// Non-DT unload only — do not treat opposite-device (GamePad) markers as Keyboard ownership.
		/// </summary>
		private static bool KeyStatusHasOwnershipMarker(string actionId)
		{
			PlayerInputProfile profile = PlayerInput.CurrentProfile;
			if (profile?.InputModes == null || string.IsNullOrEmpty(actionId))
				return false;

			if (ModeHasMarkerForTrigger(actionId, InputMode.Keyboard))
				return true;
			return ModeHasMarkerForTrigger(actionId, InputMode.KeyboardUI);
		}
	}
}
