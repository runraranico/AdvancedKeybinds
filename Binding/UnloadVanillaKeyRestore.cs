using System;
using System.Collections.Generic;
using AdvancedKeybinds.Core;
using AdvancedKeybinds.Features.DoubleTap;
using Terraria.GameInput;

namespace AdvancedKeybinds.Binding
{
	/// <summary>
	/// Unload/disable only: persist proven normal-key restoration, never a key guessed from a combo.
	/// Does not rewrite bindings.json.
	/// </summary>
	internal static class UnloadVanillaKeyRestore
	{
		private static readonly List<string> RestoreScratch = new(4);

		/// <summary>
		/// After the profile ledger restores known normal keys, handles existing DT/OR cleanup
		/// and saves input profiles only when no unprojectable AK ownership remains.
		/// Call while BindingStorage still holds combos (before Combos.Clear).
		/// </summary>
		public static int RestoreAdvancedOwnedFromCombos(
			IEnumerable<KeyValuePair<string, ComboBinding>> combos,
			out bool savedProfiles)
		{
			savedProfiles = false;
			if (combos == null)
			{
				savedProfiles = VanillaKeyProfileOverlay.CanPersistProfiles() && TrySavePlayerInputProfiles();
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

				// Shared UI-back plain KeyStatus OR (Escape + Tab) has no AKAdv marker — strip OR leg on unload.
				if (!KeyStatusHasOwnershipMarker(actionId)
					&& TryRestorePlainSharedUiBackOr(actionId, combo))
				{
					restored++;
					continue;
				}

				// Non-DT A is restored by the ownership ledger before this method.
				// A leftover marker does not prove what the original normal key was.
			}

			savedProfiles = VanillaKeyProfileOverlay.CanPersistProfiles() && TrySavePlayerInputProfiles();
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

		/// <summary>
		/// Unload: when KeyStatus still matches enabled-sync plain OR (e.g. Escape + Tab on Inventory),
		/// restore vanilla to the AND key only (e.g. Escape). No AKAdv marker required.
		/// </summary>
		private static bool TryRestorePlainSharedUiBackOr(string storageKey, ComboBinding combo)
		{
			if (combo == null || !combo.HasAdditionalOr)
				return false;

			ControlsGameUiSeparationPolicy.SyncModeKind kind =
				ControlsGameUiSeparationPolicy.ResolveSyncKind(storageKey, out int ordinal);
			if (kind != ControlsGameUiSeparationPolicy.SyncModeKind.SingleInputMode)
				return false;
			if (ordinal != ControlsGameUiSeparationPolicy.ModeKeyboard
				&& ordinal != ControlsGameUiSeparationPolicy.ModeKeyboardUi)
				return false;

			var expected = new List<string>(2);
			if (!KeyboardAdditionalOrPolicy.TryBuildVanillaOrKeyStatusEntries(
				    storageKey,
				    combo.Hold,
				    combo.Keys,
				    combo.AdditionalOrToken,
				    expected,
				    ordinal)
				|| expected.Count != 2)
				return false;

			string trigger = ControlsGameUiSeparationPolicy.ToTriggerActionId(storageKey);
			InputMode mode = (InputMode)ordinal;
			return global::AdvancedKeybinds.Patches.AdditionalOrPersistencePatches.TryRestoreCurrentPlainOr(trigger, mode, expected);
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
