using System;
using System.Globalization;
using AdvancedKeybinds.Core;

namespace AdvancedKeybinds.Features.ListView
{
	/// <summary>
	/// List group ordering. Single-letter / digit / F-key headings only for those keys;
	/// named keys (Escape, Tab, Left, …), Mouse, and Pad stay in their own groups.
	/// </summary>
	internal static class BindingListSort
	{
		public static string BuildGroupSortKey(string primaryToken, string primaryDisplay)
		{
			string label = !string.IsNullOrWhiteSpace(primaryDisplay)
				? primaryDisplay.Trim()
				: (primaryToken ?? string.Empty).Trim();
			if (label.Length == 0)
				return "99|zzz";

			string upper = label.ToUpperInvariant();

			if (TryParseFKey(upper, out int fn))
				return "30|" + fn.ToString("00", CultureInfo.InvariantCulture) + "|" + upper;

			if (IsSingleLetterLabel(upper))
				return "10|" + upper[0] + "|" + upper;

			if (IsSingleDigitLabel(upper))
				return "20|" + upper[0] + "|" + upper;

			if (StableKeyToken.IsMouseToken(primaryToken))
				return "40|Mouse|" + upper;

			if (!string.IsNullOrEmpty(primaryToken)
				&& primaryToken.StartsWith("Pad:", StringComparison.OrdinalIgnoreCase))
				return "50|Pad|" + upper;

			// Named keys: Escape, Tab, L-Ctrl, Left, CapsLock, 全角/半角, …
			return "60|" + upper;
		}

		public static string GroupHeading(string primaryToken, string primaryDisplay)
		{
			string label = !string.IsNullOrWhiteSpace(primaryDisplay)
				? primaryDisplay.Trim()
				: (primaryToken ?? string.Empty).Trim();
			if (label.Length == 0)
				return "?";

			string upper = label.ToUpperInvariant();

			if (TryParseFKey(upper, out _))
			{
				int i = 1;
				while (i < label.Length && char.IsDigit(label[i]))
					i++;
				return label.Substring(0, i).ToUpperInvariant();
			}

			if (IsSingleLetterLabel(upper))
				return upper;

			if (IsSingleDigitLabel(upper))
				return upper;

			if (StableKeyToken.IsMouseToken(primaryToken))
				return "Mouse";

			if (!string.IsNullOrEmpty(primaryToken)
				&& primaryToken.StartsWith("Pad:", StringComparison.OrdinalIgnoreCase))
				return "Pad";

			// Keep full display name as heading (Escape / Tab / Left / L-Ctrl / …).
			return label;
		}

		private static bool IsSingleLetterLabel(string upper)
			=> upper.Length == 1 && upper[0] >= 'A' && upper[0] <= 'Z';

		private static bool IsSingleDigitLabel(string upper)
			=> upper.Length == 1 && upper[0] >= '0' && upper[0] <= '9';

		private static bool TryParseFKey(string upper, out int fn)
		{
			fn = 0;
			if (upper.Length < 2 || upper[0] != 'F' || !char.IsDigit(upper[1]))
				return false;
			return int.TryParse(upper.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out fn)
				&& fn >= 1 && fn <= 24;
		}
	}
}
