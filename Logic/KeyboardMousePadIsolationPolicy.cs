using System;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Keyboard/Mouse storage (<c>MouseLeft</c>, not <c>GP:MouseLeft</c>) must never keep <c>Pad:*</c>.
	/// Pollution makes Advanced ownership replace shared Triggers and kills physical click + Pad UI confirm
	/// (confirm injects MouseLeft). Display/input GamePad bindings stay under <c>GP:</c> only.
	/// <para>
	/// Default sole mouse-button binds (MouseLeft=Mouse1, MouseRight=Mouse2, …) must stay vanilla
	/// KeyStatus — AKAdv markers break Main.mouseRight / UI OnRightClick (Magic Storage recipe switch).
	/// </para>
	/// </summary>
	public static class KeyboardMousePadIsolationPolicy
	{
		public const string MouseLeftAction = "MouseLeft";
		public const string DefaultMouseLeftToken = "Mouse1";

		public const string MouseRightAction = "MouseRight";
		public const string DefaultMouseRightToken = "Mouse2";

		public const string MouseMiddleAction = "MouseMiddle";
		public const string DefaultMouseMiddleToken = "Mouse3";

		public const string MouseXButton1Action = "MouseXButton1";
		public const string DefaultMouseXButton1Token = "Mouse4";

		public const string MouseXButton2Action = "MouseXButton2";
		public const string DefaultMouseXButton2Token = "Mouse5";

		/// <summary>
		/// True when a non-GP storage key holds any Pad slot or Alternate Pad token.
		/// </summary>
		public static bool HasPadPollution(string storageKey, ComboBindingData data)
		{
			if (data == null || ControlsEditTargetPolicy.IsGamePadStorageKey(storageKey))
				return false;

			if (GamePadToken.IsPadToken(data.Slot1)
				|| GamePadToken.IsPadToken(data.Slot2)
				|| GamePadToken.IsPadToken(data.Slot3))
				return true;

			return GamePadToken.IsPadToken(data.AlternateGamePadToken);
		}

		/// <summary>
		/// Strips Pad:* from Keyboard/Mouse storage. Does not invent Mouse1 into Combos —
		/// empty MouseLeft must leave vanilla KeyStatus (<c>Mouse1</c>) alone.
		/// (AKAdv:Mouse1 ownership + BindingInputEngine skipping <c>Main.gameMenu</c> kills title clicks.)
		/// Returns true when <paramref name="data"/> was mutated. Does not touch GP: keys.
		/// </summary>
		public static bool TrySanitizeKeyboardMouseBinding(string storageKey, ComboBindingData data)
		{
			if (data == null || ControlsEditTargetPolicy.IsGamePadStorageKey(storageKey))
				return false;

			bool changed = false;
			for (int i = 1; i <= 3; i++)
			{
				string slot = data.GetSlot(i);
				if (!GamePadToken.IsPadToken(slot))
					continue;
				data.SetSlot(i, null);
				data.SetSlotDisplay(i, null);
				changed = true;
			}

			if (GamePadToken.IsPadToken(data.AlternateGamePadToken))
			{
				data.AlternateGamePadToken = null;
				changed = true;
			}

			return changed;
		}

		/// <summary>
		/// Default use-item binding: sole Mouse1, no HOLD. Must use vanilla KeyStatus, not AKAdv markers.
		/// </summary>
		public static bool IsDefaultVanillaMouseLeft(string storageKey, bool hold, string slot1, string slot2, string slot3, string alternate)
			=> TryGetDefaultVanillaMouseToken(storageKey, hold, slot1, slot2, slot3, alternate, out string token)
				&& string.Equals(token, DefaultMouseLeftToken, StringComparison.Ordinal);

		/// <summary>
		/// Default interact binding: sole Mouse2, no HOLD. Must use vanilla KeyStatus, not AKAdv markers.
		/// AKAdv:Mouse2 breaks Main.mouseRight → Magic Storage UI OnRightClick / recipe switch.
		/// </summary>
		public static bool IsDefaultVanillaMouseRight(string storageKey, bool hold, string slot1, string slot2, string slot3, string alternate)
			=> TryGetDefaultVanillaMouseToken(storageKey, hold, slot1, slot2, slot3, alternate, out string token)
				&& string.Equals(token, DefaultMouseRightToken, StringComparison.Ordinal);

		/// <summary>
		/// Sole default mouse-button bind for a vanilla mouse action (no HOLD / no alternate / one slot).
		/// Combos must not keep these — write plain Keys name to KeyStatus instead of AKAdv markers.
		/// </summary>
		public static bool TryGetDefaultVanillaMouseToken(
			string storageKey,
			bool hold,
			string slot1,
			string slot2,
			string slot3,
			string alternate,
			out string vanillaMouseToken)
		{
			vanillaMouseToken = null;
			if (string.IsNullOrEmpty(storageKey) || hold)
				return false;
			if (!string.IsNullOrEmpty(alternate))
				return false;
			if (ControlsEditTargetPolicy.IsGamePadStorageKey(storageKey))
				return false;

			if (!TryMapMouseActionToDefaultToken(storageKey, out string expected))
				return false;

			if (!IsSoleFilledSlot(slot1, slot2, slot3, out string sole))
				return false;

			if (!string.Equals(sole, expected, StringComparison.Ordinal))
				return false;

			vanillaMouseToken = expected;
			return true;
		}

		public static bool TryMapMouseActionToDefaultToken(string actionOrStorageKey, out string defaultToken)
		{
			defaultToken = null;
			if (string.IsNullOrEmpty(actionOrStorageKey))
				return false;

			string trigger = ControlsEditTargetPolicy.ToTriggerActionId(actionOrStorageKey);
			if (string.Equals(trigger, MouseLeftAction, StringComparison.Ordinal))
			{
				defaultToken = DefaultMouseLeftToken;
				return true;
			}

			if (string.Equals(trigger, MouseRightAction, StringComparison.Ordinal))
			{
				defaultToken = DefaultMouseRightToken;
				return true;
			}

			if (string.Equals(trigger, MouseMiddleAction, StringComparison.Ordinal))
			{
				defaultToken = DefaultMouseMiddleToken;
				return true;
			}

			if (string.Equals(trigger, MouseXButton1Action, StringComparison.Ordinal))
			{
				defaultToken = DefaultMouseXButton1Token;
				return true;
			}

			if (string.Equals(trigger, MouseXButton2Action, StringComparison.Ordinal))
			{
				defaultToken = DefaultMouseXButton2Token;
				return true;
			}

			return false;
		}

		private static bool IsSoleFilledSlot(string slot1, string slot2, string slot3, out string sole)
		{
			sole = null;
			int n = 0;
			if (!string.IsNullOrEmpty(slot1))
			{
				n++;
				sole = slot1;
			}

			if (!string.IsNullOrEmpty(slot2))
			{
				n++;
				sole = slot2;
			}

			if (!string.IsNullOrEmpty(slot3))
			{
				n++;
				sole = slot3;
			}

			return n == 1;
		}
	}
}
