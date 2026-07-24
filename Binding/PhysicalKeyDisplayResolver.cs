using System;
using AdvancedKeybinds.Core;
using Microsoft.Xna.Framework.Input;

namespace AdvancedKeybinds.Binding
{
	/// <summary>
	/// UI-only display resolver for physical fallback tokens (<c>VK:</c> / <c>SC:</c>).
	/// Scan Code / Virtual-Key strings are the last resort, not the default.
	/// </summary>
	internal static class PhysicalKeyDisplayResolver
	{
		public static bool TryGetDisplay(string token, out string display)
		{
			display = null;
			if (string.IsNullOrWhiteSpace(token))
				return false;

			token = token.Trim();

			if (PhysicalKeyToken.TryParseVk(token, out uint vk))
				return TryGetDisplayForVk(vk, out display);

			if (PhysicalKeyToken.TryParseScan(token, out ushort scan, out bool extended))
				return TryGetDisplayForScan(scan, extended, out display);

			return false;
		}

		public static bool TryGetDisplayForVk(uint vk, out string display)
		{
			display = null;
			if (vk == 0)
				return false;

			if (JapaneseJisKeyDisplayMap.IsActive()
				&& JapaneseJisKeyDisplayMap.TryGetDisplay(PhysicalKeyToken.FormatVk(vk), out display))
				return true;

			if (OperatingSystem.IsWindows()
				&& KeyboardLayoutHelper.TryGetUnshiftedVkGlyph(vk, out string glyph))
			{
				display = KeyLayoutDisplayResolver.FormatDisplayGlyph(glyph);
				return true;
			}

			if (Enum.IsDefined(typeof(Keys), (int)vk)
				&& TryGetDisplayForKeys((Keys)vk, out display))
				return true;

			return false;
		}

		public static bool TryGetDisplayForScan(ushort scan, bool extended, out string display)
		{
			display = null;
			if (scan == 0)
				return false;

			if (JapaneseJisKeyDisplayMap.TryGetScanDisplay(scan, extended, out display))
				return true;

			if (OperatingSystem.IsWindows()
				&& KeyboardLayoutHelper.TryGetUnshiftedScanGlyph(scan, extended, out string glyph))
			{
				display = KeyLayoutDisplayResolver.FormatDisplayGlyph(glyph);
				return true;
			}

			if (OperatingSystem.IsWindows()
				&& KeyboardLayoutHelper.TryGetVkFromScanEx(scan, extended,
					KeyboardLayoutHelper.GetActiveLayoutHandle(), out uint vk)
				&& Enum.IsDefined(typeof(Keys), (int)vk)
				&& TryGetDisplayForKeys((Keys)vk, out display))
				return true;

			return false;
		}

		private static bool TryGetDisplayForKeys(Keys key, out string display)
		{
			display = null;
			if (key == Keys.None)
				return false;

			if (JapaneseJisKeyDisplayMap.IsActive()
				&& JapaneseJisKeyDisplayMap.TryGetDisplay(key.ToString(), out display))
				return true;

			if (TryGetFixedLocaleLabel(key, out display))
				return true;

			if (KeyToken.IsInternalNameOnlyKeyForDisplay(key))
			{
				display = StableKeyToken.FormatForDisplay(key.ToString());
				return true;
			}

			if (KeyLayoutDisplayResolver.TryGetLayoutLegend(key, out display))
				return true;

			display = StableKeyToken.FormatForDisplay(key.ToString());
			return true;
		}

		private static bool TryGetFixedLocaleLabel(Keys key, out string label)
		{
			label = null;
			switch (key)
			{
				case Keys.Kana:
					if (KeyboardLayoutHelper.IsKoreanKeyboardLayout())
						label = "Hangul";
					else if (KeyboardLayoutHelper.IsJapaneseHankakuKeyContext())
						label = JapaneseJisKeyDisplayMap.IsActive() ? "かな" : "カタカナ/ひらがな";
					else
						label = "Kana";
					return true;
				case Keys.Kanji:
					if (KeyboardLayoutHelper.IsKoreanKeyboardLayout())
						label = "Hanja";
					else
						label = "Kanji";
					return true;
				case Keys.ImeConvert:
					label = KeyboardLayoutHelper.IsJapaneseHankakuKeyContext() ? "変換" : "ImeConvert";
					return true;
				case Keys.ImeNoConvert:
					label = KeyboardLayoutHelper.IsJapaneseHankakuKeyContext() ? "無変換" : "ImeNoConvert";
					return true;
				default:
					return false;
			}
		}
	}
}
