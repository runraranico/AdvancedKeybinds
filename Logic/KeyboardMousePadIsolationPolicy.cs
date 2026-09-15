using System;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Keyboard/Mouse storage (<c>MouseLeft</c>, not <c>GP:MouseLeft</c>) must never keep <c>Pad:*</c>.
	/// Pollution makes Advanced ownership replace shared Triggers and kills physical click + Pad UI confirm
	/// (confirm injects MouseLeft). Display/input GamePad bindings stay under <c>GP:</c> only.
	/// </summary>
	public static class KeyboardMousePadIsolationPolicy
	{
		public const string MouseLeftAction = "MouseLeft";
		public const string DefaultMouseLeftToken = "Mouse1";

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
		{
			if (!string.Equals(storageKey, MouseLeftAction, StringComparison.Ordinal))
				return false;
			if (hold)
				return false;
			if (!string.IsNullOrEmpty(slot1) || !string.IsNullOrEmpty(slot2))
				return false;
			if (!string.IsNullOrEmpty(alternate))
				return false;
			return string.Equals(slot3, DefaultMouseLeftToken, StringComparison.Ordinal);
		}
	}
}
