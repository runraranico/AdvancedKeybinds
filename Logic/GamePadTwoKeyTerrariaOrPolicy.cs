using System;
using System.Collections.Generic;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// GamePad two-key records that sync Slot1/Slot2 into Terraria KeyStatus (OR) and never
	/// evaluate through BindingInputEngine. Exact allowlist is 14 ActionIds × GAME/UI = 28 triples.
	/// Storage keys use GP:G: / GP:U: so GAME and UI never collide; ControlsEditTargetPolicy.ToTriggerActionId
	/// is intentionally not used for these keys (product strip of GP: alone would yield G:/U: prefixes).
	/// </summary>
	public static class GamePadTwoKeyTerrariaOrPolicy
	{
		public const string DeviceGamepad = "Gamepad";
		public const string ModeGame = "GAME";
		public const string ModeUi = "UI";

		public const string GameStoragePrefix = "GP:G:";
		public const string UiStoragePrefix = "GP:U:";

		public const int InputModeXBoxGamepad = 3;
		public const int InputModeXBoxGamepadUi = 4;

		/// <summary>The 14 ActionIds (not 14 records — each has GAME and UI).</summary>
		public static readonly string[] ActionIds = {
			"MouseLeft",
			"MouseRight",
			"MouseMiddle",
			"MouseXButton1",
			"MouseXButton2",
			"Up",
			"Down",
			"Left",
			"Right",
			"Jump",
			"Grapple",
			"SmartSelect",
			"SmartCursor",
			"QuickMount",
		};

		/// <summary>Excluded from this path (normal combo panel). Not in <see cref="ActionIds"/>.</summary>
		public static readonly string[] ExcludedActionIds = {
			"QuickHeal",
			"QuickMana",
			"QuickBuff",
			"LockOn",
			"Throw",
			"Inventory",
		};

		/// <summary>Exact triples encoded as Device|Mode|ActionId (28 entries, built once).</summary>
		private static readonly HashSet<string> ExactAllowlist;

		static GamePadTwoKeyTerrariaOrPolicy()
		{
			ExactAllowlist = new HashSet<string>(28, StringComparer.Ordinal);
			for (int i = 0; i < ActionIds.Length; i++)
			{
				ExactAllowlist.Add(ComposeExactKey(DeviceGamepad, ModeGame, ActionIds[i]));
				ExactAllowlist.Add(ComposeExactKey(DeviceGamepad, ModeUi, ActionIds[i]));
			}
		}

		public static int ExactAllowlistCount => ExactAllowlist.Count;

		public static string ComposeExactKey(string device, string mode, string actionId)
			=> device + "|" + mode + "|" + actionId;

		public static bool IsExactAllowlisted(string device, string mode, string actionId)
		{
			if (string.IsNullOrEmpty(device) || string.IsNullOrEmpty(mode) || string.IsNullOrEmpty(actionId))
				return false;
			return ExactAllowlist.Contains(ComposeExactKey(device, mode, actionId));
		}

		public static bool IsAllowlistedActionId(string actionId)
		{
			if (string.IsNullOrEmpty(actionId))
				return false;
			for (int i = 0; i < ActionIds.Length; i++)
			{
				if (string.Equals(actionId, ActionIds[i], StringComparison.Ordinal))
					return true;
			}
			return false;
		}

		public static bool IsExcludedActionId(string actionId)
		{
			if (string.IsNullOrEmpty(actionId))
				return false;
			for (int i = 0; i < ExcludedActionIds.Length; i++)
			{
				if (string.Equals(actionId, ExcludedActionIds[i], StringComparison.Ordinal))
					return true;
			}
			return false;
		}

		public static bool TryMapInputModeOrdinal(int inputModeOrdinal, out string mode)
		{
			if (inputModeOrdinal == InputModeXBoxGamepad)
			{
				mode = ModeGame;
				return true;
			}

			if (inputModeOrdinal == InputModeXBoxGamepadUi)
			{
				mode = ModeUi;
				return true;
			}

			mode = null;
			return false;
		}

		/// <summary>True for (Gamepad, GAME|UI, allowlisted ActionId) only.</summary>
		public static bool IsTarget(int inputModeOrdinal, string actionId)
		{
			if (!TryMapInputModeOrdinal(inputModeOrdinal, out string mode))
				return false;
			if (!IsAllowlistedActionId(actionId))
				return false;
			return IsExactAllowlisted(DeviceGamepad, mode, actionId);
		}

		public static bool IsTargetStorageKey(string storageKey)
		{
			if (!TryParseStorageKey(storageKey, out string mode, out string actionId))
				return false;
			return IsExactAllowlisted(DeviceGamepad, mode, actionId);
		}

		public static string ToStorageKey(int inputModeOrdinal, string actionId)
		{
			if (!TryMapInputModeOrdinal(inputModeOrdinal, out string mode)
				|| string.IsNullOrEmpty(actionId))
				return null;

			return mode == ModeUi
				? UiStoragePrefix + actionId
				: GameStoragePrefix + actionId;
		}

		/// <summary>
		/// Mode-aware storage: delegates to <see cref="ControlsGameUiSeparationPolicy"/>
		/// (all vanilla GamePad GAME/UI; 28 Terraria-OR targets use same GP:G:/GP:U: keys).
		/// </summary>
		public static string ResolveBindingStorageKey(int inputModeOrdinal, string actionId)
			=> ControlsGameUiSeparationPolicy.ResolveStorageKey(inputModeOrdinal, actionId);

		public static bool TryParseStorageKey(string storageKey, out string mode, out string actionId)
		{
			mode = null;
			actionId = null;
			if (string.IsNullOrEmpty(storageKey))
				return false;

			if (storageKey.StartsWith(UiStoragePrefix, StringComparison.Ordinal))
			{
				mode = ModeUi;
				actionId = storageKey.Substring(UiStoragePrefix.Length);
				return actionId.Length > 0;
			}

			if (storageKey.StartsWith(GameStoragePrefix, StringComparison.Ordinal))
			{
				mode = ModeGame;
				actionId = storageKey.Substring(GameStoragePrefix.Length);
				return actionId.Length > 0;
			}

			return false;
		}

		/// <summary>Strip GP:G: / GP:U: to bare ActionId. Does not use ControlsEditTargetPolicy.ToTriggerActionId.</summary>
		public static string ToTriggerActionId(string storageKey)
		{
			if (TryParseStorageKey(storageKey, out _, out string actionId))
				return actionId;
			return ControlsEditTargetPolicy.ToTriggerActionId(storageKey);
		}

		public static int ResolveSyncInputModeOrdinal(string storageKey)
		{
			if (!TryParseStorageKey(storageKey, out string mode, out _))
				return -1;
			return mode == ModeUi ? InputModeXBoxGamepadUi : InputModeXBoxGamepad;
		}

		/// <summary>
		/// Builds Buttons-name list for KeyStatus OR (deduped). Empty when both slots unset.
		/// Face/DPad/triggers use ownership-safe Buttons names; stick directions use
		/// LeftThumbstick* / RightThumbstick* (TwoKey-only — does not change
		/// <see cref="ControlsEditTargetPolicy.TryPadTokenToVanillaButtonsName"/>).
		/// </summary>
		public static int BuildKeyStatusEntries(string slot1, string slot2, string[] destination)
		{
			if (destination == null || destination.Length < 2)
				return 0;

			int written = 0;
			written = AppendConvertible(slot1, destination, written);
			written = AppendConvertible(slot2, destination, written);
			return written;
		}

		/// <summary>
		/// Read-only TwoKey sync/evaluate inputs. Does not mutate slots or storage.
		/// Takes non-empty slots in display order (Slot1→Slot2→Slot3), at most two, as OR inputs.
		/// Not for 3-key AND paths.
		/// </summary>
		public static bool TryGetEffectiveTwoKeyInputs(
			string slot1,
			string slot2,
			string slot3,
			out string input1,
			out string input2)
		{
			input1 = null;
			input2 = null;

			if (!string.IsNullOrEmpty(slot1))
			{
				input1 = slot1;
				if (!string.IsNullOrEmpty(slot2))
				{
					input2 = slot2;
					return true;
				}

				if (!string.IsNullOrEmpty(slot3))
				{
					input2 = slot3;
					return true;
				}

				return true;
			}

			if (!string.IsNullOrEmpty(slot2))
			{
				input1 = slot2;
				if (!string.IsNullOrEmpty(slot3))
				{
					input2 = slot3;
					return true;
				}

				return true;
			}

			if (!string.IsNullOrEmpty(slot3))
			{
				input1 = slot3;
				return true;
			}

			return false;
		}

		/// <summary>
		/// TwoKey KeyStatus write: Pad:* → Buttons.ToString including stick directions.
		/// Does not invent face names from sticks (DPad vs LS/RS stay distinct).
		/// </summary>
		public static bool TryPadTokenToTerrariaOrButtonsName(string padOrButtons, out string buttonsName)
		{
			buttonsName = null;
			if (string.IsNullOrEmpty(padOrButtons))
				return false;

			if (ControlsEditTargetPolicy.TryPadTokenToVanillaButtonsName(padOrButtons, out buttonsName))
				return true;

			if (!GamePadToken.TryCanonicalize(padOrButtons, out string canon))
			{
				// Already a Buttons name (including LeftThumbstick*)?
				if (TryKeyStatusButtonsNameToPadToken(padOrButtons.Trim(), out _))
				{
					buttonsName = padOrButtons.Trim();
					return true;
				}
				return false;
			}

			if (ControlsEditTargetPolicy.TryPadTokenToVanillaButtonsName(canon, out buttonsName))
				return true;

			// Stick directions: Glyph/KeyStatus names (not ownership helper).
			return ControlsGamePadGlyphDisplayPolicy.TryResolveGlyphButtonsName(canon, out buttonsName)
				&& GamePadToken.IsStickDirectionToken(canon);
		}

		/// <summary>LeftThumbstickUp ↔ Pad:LS:Up (and RS / face Buttons names).</summary>
		public static bool TryKeyStatusButtonsNameToPadToken(string buttonsName, out string padToken)
		{
			padToken = null;
			if (string.IsNullOrWhiteSpace(buttonsName))
				return false;

			buttonsName = buttonsName.Trim();
			switch (buttonsName)
			{
				case "LeftThumbstickUp":
					padToken = GamePadToken.LeftStickUp;
					return true;
				case "LeftThumbstickDown":
					padToken = GamePadToken.LeftStickDown;
					return true;
				case "LeftThumbstickLeft":
					padToken = GamePadToken.LeftStickLeft;
					return true;
				case "LeftThumbstickRight":
					padToken = GamePadToken.LeftStickRight;
					return true;
				case "RightThumbstickUp":
					padToken = GamePadToken.RightStickUp;
					return true;
				case "RightThumbstickDown":
					padToken = GamePadToken.RightStickDown;
					return true;
				case "RightThumbstickLeft":
					padToken = GamePadToken.RightStickLeft;
					return true;
				case "RightThumbstickRight":
					padToken = GamePadToken.RightStickRight;
					return true;
			}

			return ControlsEditTargetPolicy.TryVanillaButtonsNameToPadToken(buttonsName, out padToken);
		}

		/// <summary>
		/// Exact (Gamepad, GAME, Up|Down|Left|Right) only — UI / other actions return false.
		/// </summary>
		public static bool TryGetGameMovementLeftStickDefault(
			int inputModeOrdinal,
			string actionId,
			out string leftStickPadToken)
		{
			leftStickPadToken = null;
			if (inputModeOrdinal != InputModeXBoxGamepad)
				return false;
			if (!IsExactAllowlisted(DeviceGamepad, ModeGame, actionId))
				return false;

			leftStickPadToken = actionId switch {
				"Up" => GamePadToken.LeftStickUp,
				"Down" => GamePadToken.LeftStickDown,
				"Left" => GamePadToken.LeftStickLeft,
				"Right" => GamePadToken.LeftStickRight,
				_ => null
			};
			return leftStickPadToken != null;
		}

		private static int AppendConvertible(string padOrButtons, string[] destination, int written)
		{
			if (string.IsNullOrEmpty(padOrButtons) || written >= destination.Length)
				return written;

			string buttons;
			if (!TryPadTokenToTerrariaOrButtonsName(padOrButtons, out buttons)
				|| string.IsNullOrEmpty(buttons))
			{
				// Keyboard / mouse / Escape etc. stored in TwoKey slots → KeyStatus as-is.
				buttons = padOrButtons.Trim();
				if (buttons.StartsWith("AKAdv:", StringComparison.Ordinal))
					buttons = buttons.Substring(6);
				if (string.IsNullOrEmpty(buttons))
					return written;
			}

			for (int i = 0; i < written; i++)
			{
				if (string.Equals(destination[i], buttons, StringComparison.OrdinalIgnoreCase))
					return written;
			}

			destination[written] = buttons;
			return written + 1;
		}

		/// <summary>Seed Slot1 only from the first convertible KeyStatus entry (open display, no save).</summary>
		public static bool TrySeedSlot1FromKeyStatusEntries(
			IReadOnlyList<string> rawEntries,
			out string slot1PadToken)
		{
			slot1PadToken = null;
			if (rawEntries == null || rawEntries.Count == 0)
				return false;

			for (int i = 0; i < rawEntries.Count; i++)
			{
				string raw = rawEntries[i];
				if (string.IsNullOrWhiteSpace(raw))
					continue;
				raw = raw.Trim();
				if (raw.StartsWith("AKAdv:", StringComparison.Ordinal))
					raw = raw.Substring(6);

				if (TryKeyStatusButtonsNameToPadToken(raw, out slot1PadToken))
					return true;
				if (GamePadToken.TryCanonicalize(raw, out slot1PadToken) && GamePadToken.IsPadToken(slot1PadToken))
					return true;
			}

			return false;
		}

		/// <summary>
		/// ComboPanel TwoKey UI boxes: 1=Slot1 (left), 2=OR explanation (not a slot), 3=Slot2 (right).
		/// </summary>
		public static bool TryMapTwoKeyUiBoxToStorageSlot(int uiBoxIndex, out int storageSlot)
		{
			if (uiBoxIndex == 1)
			{
				storageSlot = 1;
				return true;
			}

			if (uiBoxIndex == 3)
			{
				storageSlot = 2;
				return true;
			}

			storageSlot = 0;
			return false;
		}

		/// <summary>
		/// Right-click clear on one TwoKey storage slot. Keeps the record shape
		/// (both empty → PersistAsEmptyRecord). Does not delete the binding key.
		/// </summary>
		public static void ClearOneStorageSlot(ComboBindingData data, int storageSlot)
		{
			if (data == null || (storageSlot != 1 && storageSlot != 2))
				return;

			if (storageSlot == 1)
			{
				data.Slot1 = null;
				data.Slot1Display = null;
			}
			else
			{
				data.Slot2 = null;
				data.Slot2Display = null;
			}

			NormalizeWorkingShape(
				data,
				persistAsEmptyRecord: string.IsNullOrEmpty(data.Slot1)
					&& string.IsNullOrEmpty(data.Slot2)
					&& string.IsNullOrEmpty(data.Slot3));
		}

		/// <summary>
		/// Working combo shape for this path: no Alternate / HOLD.
		/// Slot1/Slot2 keep priority; Slot3-only is kept (not moved to Slot1, not discarded).
		/// When Slot1 or Slot2 is set, Slot3 is cleared as TwoKey OR working shape (unchanged vs prior).
		/// </summary>
		public static void NormalizeWorkingShape(ComboBindingData data, bool persistAsEmptyRecord)
		{
			if (data == null)
				return;

			bool slot12Empty = string.IsNullOrEmpty(data.Slot1) && string.IsNullOrEmpty(data.Slot2);
			bool slot3Only = slot12Empty && !string.IsNullOrEmpty(data.Slot3);
			if (!slot3Only)
			{
				data.Slot3 = null;
				data.Slot3Display = null;
			}

			data.AlternateGamePadToken = null;
			data.Hold = false;
			data.HoldOnRelease = false;
			data.UseTerrariaKeyStatusOr = true;
			data.PersistAsEmptyRecord = persistAsEmptyRecord
				|| (slot12Empty && string.IsNullOrEmpty(data.Slot3));
		}
	}
}
