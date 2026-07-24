using System;
using System.Collections.Generic;
using AdvancedKeybinds.Core;
using Terraria.GameInput;

namespace AdvancedKeybinds.Binding
{
	/// <summary>
	/// Unload/disable only: replace AKAdv ownership markers with one vanilla-fireable key from each
	/// advanced combo, then persist via <see cref="PlayerInput.Save"/>. Does not rewrite bindings.json.
	/// </summary>
	internal static class UnloadVanillaKeyRestore
	{
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
				if (combo == null || combo.IsEmpty)
					continue;

				string actionId = pair.Key;
				if (string.IsNullOrEmpty(actionId))
					continue;

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
		/// </summary>
		private static bool KeyStatusHasOwnershipMarker(string actionId)
		{
			PlayerInputProfile profile = PlayerInput.CurrentProfile;
			if (profile?.InputModes == null || string.IsNullOrEmpty(actionId))
				return false;

			if (ModeHasMarker(profile, InputMode.Keyboard, actionId))
				return true;
			return ModeHasMarker(profile, InputMode.KeyboardUI, actionId);
		}

		private static bool ModeHasMarker(PlayerInputProfile profile, InputMode mode, string actionId)
		{
			if (!profile.InputModes.TryGetValue(mode, out KeyConfiguration config)
				|| config?.KeyStatus == null)
				return false;
			if (!config.KeyStatus.TryGetValue(actionId, out List<string> keys) || keys == null)
				return false;

			for (int i = 0; i < keys.Count; i++)
			{
				if (AdvancedVanillaOwnershipMarker.IsMarker(keys[i]))
					return true;
			}

			return false;
		}
	}
}
