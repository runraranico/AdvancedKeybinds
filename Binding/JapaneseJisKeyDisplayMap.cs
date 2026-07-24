using System;
using System.Collections.Generic;
using AdvancedKeybinds.Core;
using Microsoft.Xna.Framework.Input;

namespace AdvancedKeybinds.Binding
{
	/// <summary>
	/// UI-only JIS (kbd106 / physical type 7) keycap legends.
	/// Does not change capture tokens, save format, or gameplay resolution.
	/// Verified on layout 0x0411 + physical JIS via Win32 VK / scan probes.
	/// </summary>
	internal static class JapaneseJisKeyDisplayMap
	{
		private const ushort ScIso102 = 0x56;

		private static readonly Dictionary<string, string> TokenLabels =
			new(StringComparer.OrdinalIgnoreCase) {
				[StableKeyToken.HankakuZenkakuToken] = "全角／半角",
				["Kana"] = "かな",
				["ImeConvert"] = "変換",
				["ImeNoConvert"] = "無変換",
				["OemMinus"] = "-",
				["OemQuotes"] = "^",
				["OemPipe"] = "￥",
				["OemTilde"] = "@",
				["OemOpenBrackets"] = "[",
				["OemCloseBrackets"] = "]",
				["OemPlus"] = ";",
				["OemSemicolon"] = ":",
				["OemComma"] = ",",
				["OemPeriod"] = ".",
				["OemQuestion"] = "/",
				["OemBackslash"] = "ろ",
			};

		private static readonly Dictionary<uint, string> VkLabels =
			new() {
				[0xC0] = "@",
				[0xBD] = "-",
				[0xDE] = "^",
				[0xDC] = "￥",
				[0xDB] = "[",
				[0xDD] = "]",
				[0xBB] = ";",
				[0xBA] = ":",
				[0xBC] = ",",
				[0xBE] = ".",
				[0xBF] = "/",
				[0xE2] = "ろ",
				[0x15] = "かな",
				[0x1C] = "変換",
				[0x1D] = "無変換",
			};

		private static readonly Dictionary<(ushort Scan, bool Extended), string> ScanLabels =
			BuildScanLabels();

		/// <summary>True on JIS physical hardware (Win32 GetKeyboardType(0) == 7).</summary>
		public static bool IsActive()
			=> KeyboardLayoutHelper.IsJapanesePhysicalKeyboard();

		/// <summary>Priority-1 display: JIS keycap legend for a saved/captured token.</summary>
		public static bool TryGetDisplay(string token, out string display)
		{
			display = null;
			if (!IsActive() || string.IsNullOrWhiteSpace(token))
				return false;

			token = token.Trim();

			if (TokenLabels.TryGetValue(token, out display))
				return true;

			if (PhysicalKeyToken.TryParseVk(token, out uint vk) && VkLabels.TryGetValue(vk, out display))
				return true;

			if (PhysicalKeyToken.TryParseScan(token, out ushort scan, out bool extended)
				&& TryGetScanDisplay(scan, extended, out display))
				return true;

			if (StableKeyToken.TryParse(token, out bool isMouse, out _, out string canonical)
				&& !isMouse
				&& TokenLabels.TryGetValue(canonical, out display))
				return true;

			return false;
		}

		public static bool TryGetScanDisplay(ushort scan, bool extended, out string display)
		{
			display = null;
			if (!IsActive())
				return false;

			// ISO102 (0x56) is not a JIS 106 keycap — leave to VK / internal / scan-id fallback.
			if (scan == ScIso102 && !extended)
				return false;

			return ScanLabels.TryGetValue((scan, extended), out display);
		}

		/// <summary>AKDIAG: human-readable JIS legend with Win32 ids.</summary>
		internal static string FormatDiagSnapshot(Keys key)
		{
			uint vk = (uint)key;
			KeyboardLayoutHelper.TryGetVkScanEx(vk, KeyboardLayoutHelper.GetActiveLayoutHandle(),
				out uint scan, out bool extended);
			TryGetDisplay(key.ToString(), out string jisLabel);
			return "keys=" + key
				+ " vk=0x" + vk.ToString("X2")
				+ " scan=0x" + scan.ToString("X2")
				+ " extended=" + extended
				+ " jisLabel=" + (jisLabel ?? "-");
		}

		private static Dictionary<(ushort Scan, bool Extended), string> BuildScanLabels()
		{
			var labels = new Dictionary<(ushort, bool), string>();

			void Put(ushort scan, bool extended, string label)
				=> labels[(scan, extended)] = label;

			// Digits (scan 0x02–0x0B).
			for (ushort i = 0; i < 10; i++)
				Put((ushort)(0x02 + i), false, i == 9 ? "0" : (i + 1).ToString());

			// Number row / IME (JIS probes).
			Put(0x0C, false, "-");
			Put(0x0D, false, "^");
			Put(0x29, false, "全角／半角");
			Put(0x7D, false, "￥");
			Put(0x73, false, "ろ");
			Put(0x79, false, "変換");
			Put(0x7B, false, "無変換");
			Put(0x70, false, "かな");

			// QWERTY row.
			Put(0x10, false, "Q");
			Put(0x11, false, "W");
			Put(0x12, false, "E");
			Put(0x13, false, "R");
			Put(0x14, false, "T");
			Put(0x15, false, "Y");
			Put(0x16, false, "U");
			Put(0x17, false, "I");
			Put(0x18, false, "O");
			Put(0x19, false, "P");
			Put(0x1A, false, "@");
			Put(0x1B, false, "[");

			// ASDF row.
			Put(0x1E, false, "A");
			Put(0x1F, false, "S");
			Put(0x20, false, "D");
			Put(0x21, false, "F");
			Put(0x22, false, "G");
			Put(0x23, false, "H");
			Put(0x24, false, "J");
			Put(0x25, false, "K");
			Put(0x26, false, "L");
			Put(0x27, false, ";");
			Put(0x28, false, ":");
			Put(0x2B, false, "]");

			// ZXCV row.
			Put(0x2C, false, "Z");
			Put(0x2D, false, "X");
			Put(0x2E, false, "C");
			Put(0x2F, false, "V");
			Put(0x30, false, "B");
			Put(0x31, false, "N");
			Put(0x32, false, "M");
			Put(0x33, false, ",");
			Put(0x34, false, ".");
			Put(0x35, false, "/");

			// Extended make-codes that may register as SC:…:E0.
			Put(0x1D, true, "R-Ctrl");
			Put(0x38, true, "R-Alt");
			Put(0x35, true, "/");
			Put(0x1C, true, "Enter");
			Put(0x47, true, "Home");
			Put(0x48, true, "Up");
			Put(0x49, true, "PageUp");
			Put(0x4B, true, "Left");
			Put(0x4D, true, "Right");
			Put(0x4F, true, "End");
			Put(0x50, true, "Down");
			Put(0x51, true, "PageDown");
			Put(0x52, true, "Insert");
			Put(0x53, true, "Delete");
			Put(0x37, true, "PrintScreen");
			Put(0x46, true, "Pause");

			// Numpad (non-extended scans) — keycap glyphs.
			Put(0x52, false, "0");
			Put(0x4F, false, "1");
			Put(0x50, false, "2");
			Put(0x51, false, "3");
			Put(0x4B, false, "4");
			Put(0x4C, false, "5");
			Put(0x4D, false, "6");
			Put(0x47, false, "7");
			Put(0x48, false, "8");
			Put(0x49, false, "9");
			Put(0x37, false, "*");
			Put(0x4E, false, "+");
			Put(0x4A, false, "-");
			Put(0x53, false, ".");
			Put(0x45, false, "NumLock");

			return labels;
		}
	}
}
