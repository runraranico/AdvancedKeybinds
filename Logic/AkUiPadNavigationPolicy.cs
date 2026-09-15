using System;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// AK-managed GamePad UI navigation: fixed face positions, not gameplay bindings.
	/// Stable tokens remain FNA <c>Pad:A/B/X/Y</c> (South/East/West/North). Display uses
	/// <see cref="GamePadToken.FormatForDisplay"/> only — never reverse-map from labels.
	/// </summary>
	public static class AkUiPadNavigationPolicy
	{
		/// <summary>Face South — Xbox A / PS Cross / Nintendo B. UI confirm.</summary>
		public const string ConfirmToken = GamePadToken.A;

		/// <summary>Face East — Xbox B / PS Circle / Nintendo A. UI cancel / back.</summary>
		public const string CancelToken = GamePadToken.B;

		public const string ConfirmDisplay = "Pad \u2193";
		public const string CancelDisplay = "Pad \u2192";
		public const string WestDisplay = "Pad \u2190";
		public const string NorthDisplay = "Pad \u2191";

		public static bool IsConfirmToken(string padToken)
		{
			if (!GamePadToken.TryCanonicalize(padToken, out string canonical))
				return false;
			return string.Equals(canonical, ConfirmToken, StringComparison.OrdinalIgnoreCase);
		}

		public static bool IsCancelToken(string padToken)
		{
			if (!GamePadToken.TryCanonicalize(padToken, out string canonical))
				return false;
			return string.Equals(canonical, CancelToken, StringComparison.OrdinalIgnoreCase);
		}

		/// <summary>
		/// Gameplay Item Use (or any other action) assignments must never become UI confirm/cancel.
		/// </summary>
		public static bool IsGameplayPadTokenAllowedAsUiConfirm(string itemUsePrimaryOrNull, string itemUseAlternateOrNull)
		{
			// Explicitly forbid: UI confirm is only ConfirmToken, never Item Use contents.
			_ = itemUsePrimaryOrNull;
			_ = itemUseAlternateOrNull;
			return false;
		}

		/// <summary>
		/// True when a JustPressed token may arm Controls row open / ComboPanel pad confirm.
		/// </summary>
		public static bool ShouldArmUiConfirmFromEdge(string pressedPadToken, bool alreadyPendingActive)
		{
			if (alreadyPendingActive)
				return false;
			return IsConfirmToken(pressedPadToken);
		}

		/// <summary>
		/// Long-hold must not re-fire confirm every frame — only a fresh edge.
		/// </summary>
		public static bool ShouldAcceptConfirmEdge(bool wasJustPressed, bool isDownOnly)
		{
			if (wasJustPressed)
				return true;
			if (isDownOnly)
				return false;
			return false;
		}
	}
}
