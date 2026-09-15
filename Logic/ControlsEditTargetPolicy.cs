using System;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Separates Controls-menu edit targets by input device.
	/// Same action name (e.g. Inventory) must not share KeyboardMouse and GamePad state.
	/// Does not invent new button token forms — Pad:Y remains the pad token; storage uses GP: prefix only.
	/// </summary>
	public static class ControlsEditTargetPolicy
	{
		public const string GamePadStoragePrefix = "GP:";

		public enum DeviceKind
		{
			KeyboardMouse = 0,
			GamePad = 1
		}

		/// <summary>
		/// Terraria InputMode ordinal values used by tests without referencing Terraria.dll.
		/// Live tModLoader (tModLoader.dll): Keyboard=0, KeyboardUI=1, Mouse=2,
		/// XBoxGamepad=3, XBoxGamepadUI=4. Maps to profiles.json sections
		/// Mouse And Keyboard / Mouse And Keyboard UI / Gamepad / Gamepad UI.
		/// </summary>
		public static DeviceKind FromTerrariaInputModeOrdinal(int inputModeOrdinal)
		{
			// Gamepad + Gamepad UI only — KeyboardUI (1) must stay KeyboardMouse.
			return inputModeOrdinal is 3 or 4
				? DeviceKind.GamePad
				: DeviceKind.KeyboardMouse;
		}

		public static bool IsGamePadModeOrdinal(int inputModeOrdinal)
			=> FromTerrariaInputModeOrdinal(inputModeOrdinal) == DeviceKind.GamePad;

		public static string ToStorageKey(DeviceKind kind, string actionId)
		{
			if (string.IsNullOrEmpty(actionId))
				return actionId;

			if (kind == DeviceKind.GamePad)
			{
				if (IsGamePadStorageKey(actionId))
					return actionId;
				return GamePadStoragePrefix + actionId;
			}

			return ToTriggerActionId(actionId);
		}

		public static bool IsGamePadStorageKey(string storageKey)
			=> !string.IsNullOrEmpty(storageKey)
				&& storageKey.StartsWith(GamePadStoragePrefix, StringComparison.Ordinal);

		public static string ToTriggerActionId(string storageKey)
		{
			if (string.IsNullOrEmpty(storageKey))
				return storageKey;
			if (IsGamePadStorageKey(storageKey))
				return storageKey.Substring(GamePadStoragePrefix.Length);
			return storageKey;
		}

		public static string CacheKey(DeviceKind kind, string actionId)
			=> ToStorageKey(kind, actionId);

		/// <summary>
		/// Single convertible Pad button without HOLD — GamePad KeyStatus (Buttons name) owns firing.
		/// BindingInputEngine must skip these to avoid dual-fire with vanilla.
		/// </summary>
		public static bool IsVanillaOwnedGamePadSingle(bool hold, System.Collections.Generic.IReadOnlyList<string> keys)
		{
			if (hold || keys == null || keys.Count != 1)
				return false;
			return TryPadTokenToVanillaButtonsName(keys[0], out _);
		}

		/// <summary>
		/// Non-HOLD single AND slot without Alternate — vanilla KeyStatus owns firing.
		/// When Alternate is present, Advanced engine always owns (OR edges → Triggers).
		/// </summary>
		public static bool IsVanillaOwnedGamePadOrPair(
			bool hold,
			System.Collections.Generic.IReadOnlyList<string> keys,
			string alternateGamePadToken)
		{
			if (AlternateGamePadBindingPolicy.HasAlternate(alternateGamePadToken))
				return false;
			return IsVanillaOwnedGamePadSingle(hold, keys);
		}

		/// <summary>Pad:Y → Y (Buttons.ToString). Stick directions return false.</summary>
		public static bool TryPadTokenToVanillaButtonsName(string padToken, out string buttonsName)
		{
			buttonsName = null;
			if (!GamePadToken.TryCanonicalize(padToken, out string canonical))
				return false;
			if (GamePadToken.IsStickDirectionToken(canonical))
				return false;
			if (!canonical.StartsWith(GamePadToken.Prefix, StringComparison.OrdinalIgnoreCase))
				return false;
			buttonsName = canonical.Substring(GamePadToken.Prefix.Length);
			return buttonsName.Length > 0;
		}

		/// <summary>Y / LeftShoulder → Pad:Y / Pad:LeftShoulder.</summary>
		public static bool TryVanillaButtonsNameToPadToken(string buttonsName, out string padToken)
		{
			padToken = null;
			if (string.IsNullOrWhiteSpace(buttonsName))
				return false;

			buttonsName = buttonsName.Trim();
			if (GamePadToken.TryCanonicalize(buttonsName, out padToken))
				return true;
			return GamePadToken.TryCanonicalize(GamePadToken.Prefix + buttonsName, out padToken);
		}

		/// <summary>
		/// Seeds a working combo for open: prefer stored advanced binding; else vanilla seed token.
		/// </summary>
		public static ComboBindingData CreateOpenWorking(
			ComboBindingData storedClone,
			string vanillaAssignedTokenOrNull)
		{
			if (storedClone != null && !storedClone.IsEmpty)
				return storedClone.Clone();

			var working = new ComboBindingData();
			if (!string.IsNullOrEmpty(vanillaAssignedTokenOrNull))
				working.Slot3 = vanillaAssignedTokenOrNull;
			return working;
		}

		public static bool WouldCorruptOtherDevice(
			DeviceKind editingKind,
			string writtenStorageKey,
			DeviceKind otherKind,
			string otherActionId)
		{
			string otherKey = ToStorageKey(otherKind, otherActionId);
			return string.Equals(writtenStorageKey, otherKey, StringComparison.Ordinal);
		}
	}
}
