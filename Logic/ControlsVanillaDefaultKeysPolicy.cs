using System;
using System.Collections.Generic;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Mirrors <c>PlayerInput.ResetKeyBinding</c> / <c>UIManageControls.GetCopyableProfileName</c>
	/// profile selection and InputMode isolation for middle-click restore.
	/// </summary>
	public static class ControlsVanillaDefaultKeysPolicy
	{
		public const string FallbackOriginalProfileName = "Redigit's Pick";

		public const int ModeKeyboard = 0;
		public const int ModeKeyboardUi = 1;
		public const int ModeGamePad = 3;
		public const int ModeGamePadUi = 4;

		/// <summary>
		/// Same rule as vanilla: use selected profile only when it exists in OriginalProfiles;
		/// otherwise Redigit's Pick (Custom → Redigit).
		/// </summary>
		public static string ResolveOriginalProfileName(
			string selectedProfileName,
			bool originalProfilesContainsSelected)
		{
			if (originalProfilesContainsSelected
				&& !string.IsNullOrEmpty(selectedProfileName))
				return selectedProfileName;
			return FallbackOriginalProfileName;
		}

		public static bool IsKeyboardFamilyMode(int inputModeOrdinal)
			=> inputModeOrdinal == ModeKeyboard || inputModeOrdinal == ModeKeyboardUi;

		public static bool IsGamePadFamilyMode(int inputModeOrdinal)
			=> inputModeOrdinal == ModeGamePad || inputModeOrdinal == ModeGamePadUi;

		/// <summary>
		/// True when a KeyStatus token must not be written into Keyboard / KeyboardUI
		/// (GamePad Buttons / AK Pad markers / thumbstick / DPad names).
		/// Arrow Keys named Up/Down/Left/Right are NOT rejected here — those are official
		/// KeyboardUI defaults from PlayerInput.Reset (Keys enum), not GamePad tokens.
		/// </summary>
		public static bool IsForbiddenOnKeyboardFamily(string token)
		{
			if (string.IsNullOrEmpty(token))
				return true;
			if (token.StartsWith("AKAdv:Pad:", StringComparison.Ordinal)
				|| token.StartsWith("Pad:", StringComparison.Ordinal))
				return true;
			if (token.StartsWith("LeftThumbstick", StringComparison.Ordinal)
				|| token.StartsWith("RightThumbstick", StringComparison.Ordinal)
				|| token.StartsWith("DPad", StringComparison.Ordinal)
				|| token.StartsWith("LeftShoulder", StringComparison.Ordinal)
				|| token.StartsWith("RightShoulder", StringComparison.Ordinal)
				|| token.StartsWith("LeftTrigger", StringComparison.Ordinal)
				|| token.StartsWith("RightTrigger", StringComparison.Ordinal))
				return true;
			// Note: numeric Buttons.ToString() values are official on KeyboardUI Menu* triggers
			// (PlayerInput.Reset) — do not reject all-digit tokens here.
			return false;
		}

		/// <summary>
		/// True when a token must not be written into XBoxGamepad / XBoxGamepadUI
		/// (plain keyboard letter / mouse click names that belong to Keyboard family).
		/// </summary>
		public static bool IsForbiddenOnGamePadFamily(string token)
		{
			if (string.IsNullOrEmpty(token))
				return true;
			if (token.StartsWith("Mouse", StringComparison.Ordinal))
				return true;
			if (token.StartsWith("AKAdv:", StringComparison.Ordinal)
				&& !token.StartsWith("AKAdv:Pad:", StringComparison.Ordinal))
				return true;
			// Single-letter / common Keys names that are never Buttons.ToString() numerics.
			if (token.Length == 1 && char.IsLetter(token[0]))
				return true;
			if (token.Length == 2
				&& (token[0] == 'D' || token[0] == 'F')
				&& char.IsDigit(token[1]))
				return true;
			switch (token)
			{
				case "Space":
				case "Escape":
				case "LeftShift":
				case "LeftControl":
				case "LeftAlt":
				case "RightShift":
				case "RightControl":
				case "RightAlt":
				case "Tab":
				case "Enter":
				case "Add":
				case "Subtract":
				case "PageUp":
				case "PageDown":
				case "OemMinus":
				case "OemPlus":
					return true;
				default:
					return false;
			}
		}

		/// <summary>
		/// Copy defaults for one InputMode: drop cross-device tokens, drop empties, dedupe (order kept).
		/// Does not invent keys when <paramref name="rawDefaults"/> is null/empty.
		/// </summary>
		public static List<string> SanitizeDefaultsForMode(
			int inputModeOrdinal,
			IReadOnlyList<string> rawDefaults)
		{
			var result = new List<string>();
			if (rawDefaults == null || rawDefaults.Count == 0)
				return result;

			bool keyboardFamily = IsKeyboardFamilyMode(inputModeOrdinal);
			bool gamePadFamily = IsGamePadFamilyMode(inputModeOrdinal);

			for (int i = 0; i < rawDefaults.Count; i++)
			{
				string token = rawDefaults[i];
				if (string.IsNullOrEmpty(token))
					continue;
				if (keyboardFamily && IsForbiddenOnKeyboardFamily(token))
					continue;
				if (gamePadFamily && IsForbiddenOnGamePadFamily(token))
					continue;
				if (ContainsOrdinal(result, token))
					continue;
				result.Add(token);
			}

			return result;
		}

		/// <summary>
		/// Keyboard (ゲーム操作) must not receive KeyboardUI's secondary arrow Keys
		/// when those equal the movement ActionId (Up/Down/Left/Right).
		/// Official Redigit Keyboard already omits them; this guards against merged/wrong-mode lists.
		/// </summary>
		public static List<string> StripActionIdEchoTokensOnKeyboardGame(
			int inputModeOrdinal,
			string actionId,
			List<string> keys)
		{
			if (inputModeOrdinal != ModeKeyboard
				|| string.IsNullOrEmpty(actionId)
				|| keys == null
				|| keys.Count == 0)
				return keys ?? new List<string>();

			if (!IsMovementActionId(actionId))
				return keys;

			var filtered = new List<string>(keys.Count);
			for (int i = 0; i < keys.Count; i++)
			{
				string token = keys[i];
				if (string.Equals(token, actionId, StringComparison.Ordinal))
					continue;
				filtered.Add(token);
			}

			return filtered;
		}

		public static bool IsMovementActionId(string actionId)
		{
			return string.Equals(actionId, "Up", StringComparison.Ordinal)
				|| string.Equals(actionId, "Down", StringComparison.Ordinal)
				|| string.Equals(actionId, "Left", StringComparison.Ordinal)
				|| string.Equals(actionId, "Right", StringComparison.Ordinal);
		}

		private static bool ContainsOrdinal(List<string> list, string token)
		{
			for (int i = 0; i < list.Count; i++)
			{
				if (string.Equals(list[i], token, StringComparison.Ordinal))
					return true;
			}

			return false;
		}
	}
}
