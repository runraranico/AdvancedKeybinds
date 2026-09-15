using System;
using System.Collections.Generic;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Pure restore resolution for vanilla Controls middle-click defaults.
	/// Product must call <see cref="ResolveForWrite"/> (not raw OriginalProfiles alone)
	/// so Keyboard / KeyboardUI never keep Redigit UI dual bindings that AK treats as wrong
	/// for the keyboard device (MouseLeft+Space, W+Up, etc.).
	/// </summary>
	public static class OfficialDefaultKeyRestoreLogic
	{
		public const int ModeKeyboard = 0;
		public const int ModeKeyboardUi = 1;
		public const int ModeGamePad = 3;
		public const int ModeGamePadUi = 4;

		public enum FailReason
		{
			None = 0,
			NullProfile = 1,
			MissingMode = 2,
			MissingActionId = 3,
			NullKeyList = 4,
			EmptyActionId = 5
		}

		/// <summary>
		/// Exact ActionId + InputMode lookup only (no cross-mode fallback).
		/// Does not invent keys. Does not strip. Prefer <see cref="ResolveForWrite"/> for product writes.
		/// </summary>
		public static List<string> Resolve(
			IReadOnlyDictionary<int, IReadOnlyDictionary<string, IReadOnlyList<string>>> originalProfileByMode,
			int inputModeOrdinal,
			string actionId,
			out FailReason failReason)
		{
			failReason = FailReason.None;
			var result = new List<string>();

			if (string.IsNullOrEmpty(actionId))
			{
				failReason = FailReason.EmptyActionId;
				return result;
			}

			if (originalProfileByMode == null)
			{
				failReason = FailReason.NullProfile;
				return result;
			}

			if (!originalProfileByMode.TryGetValue(inputModeOrdinal, out IReadOnlyDictionary<string, IReadOnlyList<string>> modeMap)
				|| modeMap == null)
			{
				failReason = FailReason.MissingMode;
				return result;
			}

			if (!modeMap.TryGetValue(actionId, out IReadOnlyList<string> keys) || keys == null)
			{
				failReason = keys == null && modeMap.ContainsKey(actionId)
					? FailReason.NullKeyList
					: FailReason.MissingActionId;
				return result;
			}

			for (int i = 0; i < keys.Count; i++)
			{
				string token = keys[i];
				if (!string.IsNullOrEmpty(token))
					result.Add(token);
			}

			return result;
		}

		/// <summary>
		/// Product write path: resolve official keys for the edited mode, then apply
		/// Keyboard-device contract (WASD / Mouse1 only on Keyboard and KeyboardUI).
		/// GamePad modes are unchanged copies of official lists.
		/// </summary>
		public static List<string> ResolveForWrite(
			IReadOnlyDictionary<int, IReadOnlyDictionary<string, IReadOnlyList<string>>> originalProfileByMode,
			int inputModeOrdinal,
			string actionId,
			out FailReason failReason)
		{
			List<string> resolved = Resolve(
				originalProfileByMode,
				inputModeOrdinal,
				actionId,
				out failReason);
			return ApplyKeyboardGameMovementWriteContract(inputModeOrdinal, actionId, resolved);
		}

		/// <summary>
		/// AK Keyboard-device product contract (Keyboard GAME and KeyboardUI):
		/// - Movement Up/Down/Left/Right → WASD only (drop Keys.Up etc. ActionId echoes)
		/// - MouseLeft → Mouse1 only (drop Space; Space is Jump on Keyboard GAME, not item use)
		/// Does not invent replacements. Does not copy across GAME↔UI. GamePad untouched.
		/// </summary>
		public static List<string> ApplyKeyboardGameMovementWriteContract(
			int inputModeOrdinal,
			string actionId,
			IReadOnlyList<string> keys)
		{
			var result = new List<string>();
			if (keys == null || keys.Count == 0)
				return result;

			bool keyboardDevice = inputModeOrdinal == ModeKeyboard
				|| inputModeOrdinal == ModeKeyboardUi;
			bool stripActionIdEcho = keyboardDevice && IsMovementActionId(actionId);
			bool stripMouseLeftSpace = keyboardDevice
				&& string.Equals(actionId, "MouseLeft", StringComparison.Ordinal);

			for (int i = 0; i < keys.Count; i++)
			{
				string token = keys[i];
				if (string.IsNullOrEmpty(token))
					continue;
				if (stripActionIdEcho
					&& string.Equals(token, actionId, StringComparison.Ordinal))
					continue;
				if (stripMouseLeftSpace
					&& string.Equals(token, "Space", StringComparison.Ordinal))
					continue;
				if (ContainsOrdinal(result, token))
					continue;
				result.Add(token);
			}

			return result;
		}

		public static bool IsMovementActionId(string actionId)
		{
			return string.Equals(actionId, "Up", StringComparison.Ordinal)
				|| string.Equals(actionId, "Down", StringComparison.Ordinal)
				|| string.Equals(actionId, "Left", StringComparison.Ordinal)
				|| string.Equals(actionId, "Right", StringComparison.Ordinal);
		}

		/// <summary>
		/// Same profile pick as PlayerInput.ResetKeyBinding / UIManageControls.GetCopyableProfileName.
		/// </summary>
		public static string ResolveOriginalProfileName(
			string selectedProfileName,
			bool originalProfilesContainsSelected)
		{
			if (originalProfilesContainsSelected
				&& !string.IsNullOrEmpty(selectedProfileName))
				return selectedProfileName;
			return "Redigit's Pick";
		}

		/// <summary>
		/// Simulates restoring one mode into a mutable CurrentProfile-shaped map.
		/// Used by machine tests for the product write contract (GAME restore must not touch UI).
		/// </summary>
		public static void ApplyRestoreToCurrent(
			Dictionary<int, Dictionary<string, List<string>>> currentByMode,
			IReadOnlyDictionary<int, IReadOnlyDictionary<string, IReadOnlyList<string>>> originalByMode,
			int inputModeOrdinal,
			string actionId)
		{
			List<string> write = ResolveForWrite(
				originalByMode,
				inputModeOrdinal,
				actionId,
				out _);
			if (!currentByMode.TryGetValue(inputModeOrdinal, out Dictionary<string, List<string>> modeMap)
				|| modeMap == null)
			{
				modeMap = new Dictionary<string, List<string>>(StringComparer.Ordinal);
				currentByMode[inputModeOrdinal] = modeMap;
			}

			modeMap[actionId] = new List<string>(write);
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
