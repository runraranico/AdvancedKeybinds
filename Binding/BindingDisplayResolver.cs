using System.Collections.Generic;
using AdvancedKeybinds.Core;
using AdvancedKeybinds.Features.DoubleTap;
using Terraria.Localization;

namespace AdvancedKeybinds.Binding
{
	/// <summary>
	/// Public display-only API for legacy shared labels. Controls DT decoration is opt-in.
	/// Does not modify KeyStatus, GetAssignedKeys, ownership, or input.
	/// Other mods may call this when they would otherwise show raw <c>AKAdv:</c> markers.
	/// </summary>
	public static class BindingDisplayResolver
	{
		/// <summary>
		/// Resolves one token for user-facing display only.
		/// Ownership markers remain unchanged in KeyStatus; only this local display value is unwrapped.
		/// Priority: global custom alias → AK Controls-equivalent label → unwrapped raw token.
		/// </summary>
		public static string ResolveDisplayName(string token)
		{
			string displayToken = BindingDisplayText.UnwrapOwnershipMarkerForDisplay(token);
			if (string.IsNullOrEmpty(displayToken))
				return string.Empty;

			string display = KeyToken.FormatDisplayForToken(displayToken);
			return string.IsNullOrEmpty(display) ? displayToken : display;
		}

		/// <summary>
		/// Resolves the legacy shared display for <paramref name="actionId"/> from BindingStorage.
		/// </summary>
		public static bool TryGetBindingDisplayText(string actionId, out string displayText)
		{
			displayText = null;
			if (string.IsNullOrEmpty(actionId))
				return false;

			ComboBinding combo = BindingStorage.Get(actionId);
			if (combo == null || combo.IsEmpty)
				return false;

			displayText = FormatComboDisplayLabel(combo);
			return !string.IsNullOrEmpty(displayText);
		}

		/// <summary>
		/// When <paramref name="assignedKeys"/> contains ownership markers, resolves AK display text.
		/// Otherwise returns false so callers keep their normal GetAssignedKeys rendering.
		/// </summary>
		public static bool TryGetBindingDisplayText(
			string actionId,
			IReadOnlyList<string> assignedKeys,
			out string displayText)
		{
			displayText = null;
			if (!BindingDisplayText.NeedsOverrideFromAssignedKeys(assignedKeys))
				return false;

			return TryGetBindingDisplayText(actionId, out displayText);
		}

		/// <summary>
		/// Shared legacy label (<see cref="ComboBinding.FormatJoinedLabel"/> + HOLD prefix).
		/// Deliberately excludes the Controls-only DT prefix.
		/// </summary>
		public static string FormatComboDisplayLabel(ComboBinding combo)
			=> FormatComboDisplayLabelCore(combo, controlsDoubleTap: false);

		/// <summary>Controls-row opt-in. Other shared API callers retain their existing labels.</summary>
		public static string FormatControlsDisplayLabel(ComboBinding combo)
			=> FormatComboDisplayLabelCore(combo, controlsDoubleTap: true);

		private static string FormatComboDisplayLabelCore(ComboBinding combo, bool controlsDoubleTap)
		{
			if (combo == null || combo.IsEmpty)
				return string.Empty;

			string joined;
			if (combo.HasAlternateGamePad && combo.FilledSlotCount == 1)
			{
				string primaryDisplay = KeyToken.FormatDisplayForToken(combo.Keys[0]);
				string alternateDisplay = KeyToken.FormatDisplayForToken(combo.AlternateGamePadToken);
				joined = AlternateGamePadBindingPolicy.FormatAlternateOverPrimaryDisplay(
					alternateDisplay,
					primaryDisplay);
			}
			else
				joined = combo.FormatJoinedLabel(compact: true);

			string whileHeld = Language.GetTextValue("Mods.AdvancedKeybinds.UI.Combo.HoldPreviewWhileHeld");
			string onRelease = Language.GetTextValue("Mods.AdvancedKeybinds.UI.Combo.HoldPreviewOnRelease");
			string withHold = BindingDisplayText.ApplyHoldPrefix(
				joined,
				combo.Hold,
				combo.HoldOnRelease,
				combo.FilledSlotCount,
				whileHeld,
				onRelease);

			if (controlsDoubleTap)
				withHold = DoubleTapControlsDisplay.ApplyPrefix(withHold, combo);

			if (!combo.HasAdditionalOr)
				return withHold;

			string orDisplay = KeyToken.FormatDisplayForToken(combo.AdditionalOrToken);
			return KeyboardAdditionalOrPolicy.FormatAndWithAdditionalOrDisplay(withHold, orDisplay);
		}
	}
}
