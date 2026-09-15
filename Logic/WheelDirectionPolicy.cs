using System;
using System.Text.RegularExpressions;

namespace AdvancedKeybinds.Core
{
	/// <summary>Vertical wheel naming policy and its one-time persisted-data migration.</summary>
	public static class WheelDirectionPolicy
	{
		private static readonly Regex BindingSlotRegex = new(
			"(?<prefix>\"Slot[123]\"\\s*:\\s*\")(?<token>MouseWheelUp|MouseWheelDown)(?<suffix>\")",
			RegexOptions.CultureInvariant);

		private static readonly Regex AliasKeyRegex = new(
			"(?<prefix>\")(?<token>PHYS:WHEEL=Up|PHYS:WHEEL=Down|MouseWheelUp|MouseWheelDown)(?<suffix>\"\\s*:)",
			RegexOptions.CultureInvariant);

		/// <summary>Windows vertical-wheel convention: positive=Up, negative=Down.</summary>
		public static string VerticalDeltaToToken(int wheelDelta)
			=> wheelDelta > 0 ? StableKeyToken.MouseWheelUpToken : StableKeyToken.MouseWheelDownToken;

		/// <summary>
		/// Microsoft <c>WM_MOUSEHWHEEL</c>: positive = rotated to the right;
		/// negative = rotated to the left.
		/// </summary>
		public static string HorizontalDeltaToToken(int wheelDelta)
			=> wheelDelta > 0 ? StableKeyToken.MouseWheelRightToken : StableKeyToken.MouseWheelLeftToken;

		/// <summary>
		/// Swaps only exact Slot1/Slot2/Slot3 wheel-token values. Formatting, display strings,
		/// action ids, and all non-wheel data remain byte-for-byte equivalent after text encoding.
		/// </summary>
		public static string MigrateBindingsJson(
			string json,
			bool migrationAlreadyCompleted,
			out int swappedSlotCount)
		{
			swappedSlotCount = 0;
			if (migrationAlreadyCompleted || string.IsNullOrEmpty(json))
				return json;

			int count = 0;
			string migrated = BindingSlotRegex.Replace(json, match => {
				count++;
				return match.Groups["prefix"].Value
					+ SwapBindingToken(match.Groups["token"].Value)
					+ match.Groups["suffix"].Value;
			});
			swappedSlotCount = count;
			return migrated;
		}

		/// <summary>
		/// Swaps only exact wheel alias dictionary keys. Alias values and non-wheel keys are retained.
		/// Both current PHYS keys and legacy raw token keys are supported.
		/// </summary>
		public static string MigrateAliasesJson(
			string json,
			bool migrationAlreadyCompleted,
			out int swappedAliasKeyCount)
		{
			swappedAliasKeyCount = 0;
			if (migrationAlreadyCompleted || string.IsNullOrEmpty(json))
				return json;

			int count = 0;
			string migrated = AliasKeyRegex.Replace(json, match => {
				count++;
				return match.Groups["prefix"].Value
					+ SwapAliasKey(match.Groups["token"].Value)
					+ match.Groups["suffix"].Value;
			});
			swappedAliasKeyCount = count;
			return migrated;
		}

		private static string SwapBindingToken(string token)
			=> string.Equals(token, StableKeyToken.MouseWheelUpToken, StringComparison.Ordinal)
				? StableKeyToken.MouseWheelDownToken
				: StableKeyToken.MouseWheelUpToken;

		private static string SwapAliasKey(string key)
		{
			if (string.Equals(key, "PHYS:WHEEL=Up", StringComparison.Ordinal))
				return "PHYS:WHEEL=Down";
			if (string.Equals(key, "PHYS:WHEEL=Down", StringComparison.Ordinal))
				return "PHYS:WHEEL=Up";

			return SwapBindingToken(key);
		}
	}
}
