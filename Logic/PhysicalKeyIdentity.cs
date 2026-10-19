using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Physical key identity for duplicate detection only (does not change saved tokens).
	/// Identity = make-code Scan + E0 + E1, formatted as <c>PHYS:SC=0x1A:E0=0:E1=0</c>.
	/// Keys / VK / SC forms of the same physical key resolve to the same id when possible.
	/// </summary>
	public static class PhysicalKeyIdentity
	{
		public const string Prefix = "PHYS:";

		private const uint MapVkToVsc = 0;
		private const uint MapVkToVscEx = 4;

		/// <summary>XNA/FNA Keys name → Windows VK (values match the enum integers).</summary>
		private static readonly Dictionary<string, uint> KeysNameToVk =
			new(StringComparer.OrdinalIgnoreCase) {
				["Back"] = 0x08, ["Tab"] = 0x09, ["Enter"] = 0x0D, ["Pause"] = 0x13,
				["CapsLock"] = 0x14, ["Kana"] = 0x15, ["Kanji"] = 0x19, ["Escape"] = 0x1B,
				["ImeConvert"] = 0x1C, ["ImeNoConvert"] = 0x1D, ["Space"] = 0x20,
				["PageUp"] = 0x21, ["PageDown"] = 0x22, ["End"] = 0x23, ["Home"] = 0x24,
				["Left"] = 0x25, ["Up"] = 0x26, ["Right"] = 0x27, ["Down"] = 0x28,
				["PrintScreen"] = 0x2C, ["Insert"] = 0x2D, ["Delete"] = 0x2E,
				["D0"] = 0x30, ["D1"] = 0x31, ["D2"] = 0x32, ["D3"] = 0x33, ["D4"] = 0x34,
				["D5"] = 0x35, ["D6"] = 0x36, ["D7"] = 0x37, ["D8"] = 0x38, ["D9"] = 0x39,
				["A"] = 0x41, ["B"] = 0x42, ["C"] = 0x43, ["D"] = 0x44, ["E"] = 0x45,
				["F"] = 0x46, ["G"] = 0x47, ["H"] = 0x48, ["I"] = 0x49, ["J"] = 0x4A,
				["K"] = 0x4B, ["L"] = 0x4C, ["M"] = 0x4D, ["N"] = 0x4E, ["O"] = 0x4F,
				["P"] = 0x50, ["Q"] = 0x51, ["R"] = 0x52, ["S"] = 0x53, ["T"] = 0x54,
				["U"] = 0x55, ["V"] = 0x56, ["W"] = 0x57, ["X"] = 0x58, ["Y"] = 0x59, ["Z"] = 0x5A,
				["LeftWindows"] = 0x5B, ["RightWindows"] = 0x5C, ["Apps"] = 0x5D,
				["NumPad0"] = 0x60, ["NumPad1"] = 0x61, ["NumPad2"] = 0x62, ["NumPad3"] = 0x63,
				["NumPad4"] = 0x64, ["NumPad5"] = 0x65, ["NumPad6"] = 0x66, ["NumPad7"] = 0x67,
				["NumPad8"] = 0x68, ["NumPad9"] = 0x69,
				["Multiply"] = 0x6A, ["Add"] = 0x6B, ["Separator"] = 0x6C, ["Subtract"] = 0x6D,
				["Decimal"] = 0x6E, ["Divide"] = 0x6F,
				["F1"] = 0x70, ["F2"] = 0x71, ["F3"] = 0x72, ["F4"] = 0x73, ["F5"] = 0x74,
				["F6"] = 0x75, ["F7"] = 0x76, ["F8"] = 0x77, ["F9"] = 0x78, ["F10"] = 0x79,
				["F11"] = 0x7A, ["F12"] = 0x7B,
				["NumLock"] = 0x90, ["Scroll"] = 0x91,
				["LeftShift"] = 0xA0, ["RightShift"] = 0xA1,
				["LeftControl"] = 0xA2, ["RightControl"] = 0xA3,
				["LeftAlt"] = 0xA4, ["RightAlt"] = 0xA5,
				["OemSemicolon"] = 0xBA, ["OemPlus"] = 0xBB, ["OemComma"] = 0xBC,
				["OemMinus"] = 0xBD, ["OemPeriod"] = 0xBE, ["OemQuestion"] = 0xBF,
				["OemTilde"] = 0xC0, ["OemOpenBrackets"] = 0xDB, ["OemPipe"] = 0xDC,
				["OemCloseBrackets"] = 0xDD, ["OemQuotes"] = 0xDE, ["Oem8"] = 0xDF,
				["OemBackslash"] = 0xE2,
			};

		/// <summary>US Set-1 make codes for known Keys names when Win32 map is unavailable.</summary>
		private static readonly Dictionary<string, (ushort Scan, bool E0, bool E1)> UsSet1Fallback =
			new(StringComparer.OrdinalIgnoreCase) {
				["Escape"] = (0x01, false, false),
				["D1"] = (0x02, false, false),
				["D2"] = (0x03, false, false),
				["D3"] = (0x04, false, false),
				["D4"] = (0x05, false, false),
				["D5"] = (0x06, false, false),
				["D6"] = (0x07, false, false),
				["D7"] = (0x08, false, false),
				["D8"] = (0x09, false, false),
				["D9"] = (0x0A, false, false),
				["D0"] = (0x0B, false, false),
				["OemMinus"] = (0x0C, false, false),
				["OemPlus"] = (0x0D, false, false),
				["Back"] = (0x0E, false, false),
				["Tab"] = (0x0F, false, false),
				["Q"] = (0x10, false, false),
				["W"] = (0x11, false, false),
				["E"] = (0x12, false, false),
				["R"] = (0x13, false, false),
				["T"] = (0x14, false, false),
				["Y"] = (0x15, false, false),
				["U"] = (0x16, false, false),
				["I"] = (0x17, false, false),
				["O"] = (0x18, false, false),
				["P"] = (0x19, false, false),
				["OemOpenBrackets"] = (0x1A, false, false),
				["OemCloseBrackets"] = (0x1B, false, false),
				["Enter"] = (0x1C, false, false),
				["LeftControl"] = (0x1D, false, false),
				["A"] = (0x1E, false, false),
				["S"] = (0x1F, false, false),
				["D"] = (0x20, false, false),
				["F"] = (0x21, false, false),
				["G"] = (0x22, false, false),
				["H"] = (0x23, false, false),
				["J"] = (0x24, false, false),
				["K"] = (0x25, false, false),
				["L"] = (0x26, false, false),
				["OemSemicolon"] = (0x27, false, false),
				["OemQuotes"] = (0x28, false, false),
				["OemTilde"] = (0x29, false, false),
				["LeftShift"] = (0x2A, false, false),
				["OemPipe"] = (0x2B, false, false),
				["Z"] = (0x2C, false, false),
				["X"] = (0x2D, false, false),
				["C"] = (0x2E, false, false),
				["V"] = (0x2F, false, false),
				["B"] = (0x30, false, false),
				["N"] = (0x31, false, false),
				["M"] = (0x32, false, false),
				["OemComma"] = (0x33, false, false),
				["OemPeriod"] = (0x34, false, false),
				["OemQuestion"] = (0x35, false, false),
				["RightShift"] = (0x36, false, false),
				["Multiply"] = (0x37, false, false),
				["LeftAlt"] = (0x38, false, false),
				["Space"] = (0x39, false, false),
				["CapsLock"] = (0x3A, false, false),
				["F1"] = (0x3B, false, false),
				["F2"] = (0x3C, false, false),
				["F3"] = (0x3D, false, false),
				["F4"] = (0x3E, false, false),
				["F5"] = (0x3F, false, false),
				["F6"] = (0x40, false, false),
				["F7"] = (0x41, false, false),
				["F8"] = (0x42, false, false),
				["F9"] = (0x43, false, false),
				["F10"] = (0x44, false, false),
				["NumLock"] = (0x45, false, false),
				["Scroll"] = (0x46, false, false),
				["ScrollLock"] = (0x46, false, false),
				["NumPad7"] = (0x47, false, false),
				["NumPad8"] = (0x48, false, false),
				["NumPad9"] = (0x49, false, false),
				["Subtract"] = (0x4A, false, false),
				["NumPad4"] = (0x4B, false, false),
				["NumPad5"] = (0x4C, false, false),
				["NumPad6"] = (0x4D, false, false),
				["Add"] = (0x4E, false, false),
				["NumPad1"] = (0x4F, false, false),
				["NumPad2"] = (0x50, false, false),
				["NumPad3"] = (0x51, false, false),
				["NumPad0"] = (0x52, false, false),
				["Decimal"] = (0x53, false, false),
				["F11"] = (0x57, false, false),
				["F12"] = (0x58, false, false),
				["OemBackslash"] = (0x73, false, false),
				["RightControl"] = (0x1D, true, false),
				["Divide"] = (0x35, true, false),
				["RightAlt"] = (0x38, true, false),
				["Home"] = (0x47, true, false),
				["Up"] = (0x48, true, false),
				["PageUp"] = (0x49, true, false),
				["Left"] = (0x4B, true, false),
				["Right"] = (0x4D, true, false),
				["End"] = (0x4F, true, false),
				["Down"] = (0x50, true, false),
				["PageDown"] = (0x51, true, false),
				["Insert"] = (0x52, true, false),
				["Delete"] = (0x53, true, false),
				["LeftWindows"] = (0x5B, true, false),
				["RightWindows"] = (0x5C, true, false),
				["Apps"] = (0x5D, true, false),
				["PrintScreen"] = (0x37, true, false),
				["Pause"] = (0x45, false, true),
				["HankakuZenkaku"] = (0x29, false, false),
			};

		[DllImport("user32.dll")]
		private static extern IntPtr GetKeyboardLayout(int idThread);

		[DllImport("user32.dll")]
		private static extern uint MapVirtualKeyEx(uint uCode, uint uMapType, IntPtr dwhkl);

		public static string Format(ushort scan, bool e0, bool e1)
			=> Prefix
				+ "SC=0x" + (scan & 0xFF).ToString("X2", CultureInfo.InvariantCulture)
				+ ":E0=" + (e0 ? "1" : "0")
				+ ":E1=" + (e1 ? "1" : "0");

		public static string FormatMouse(int mouseButton)
			=> Prefix + "MOUSE=" + mouseButton.ToString(CultureInfo.InvariantCulture);

		public static string FormatWheel(bool up)
			=> Prefix + "WHEEL=" + (up ? "Up" : "Down");

		public static string FormatHorizontalWheel(bool right)
			=> Prefix + "WHEEL=" + (right ? "Right" : "Left");

		/// <summary>Lookup Windows VK for a known Keys name (diagnostics / DUP-CHECK).</summary>
		public static bool TryGetVkForKeysName(string keysName, out uint vk)
			=> KeysNameToVk.TryGetValue(keysName ?? string.Empty, out vk);

		/// <summary>
		/// True when both tokens refer to the same physical key (or same mouse button / wheel).
		/// Display aliases are ignored — callers must pass save tokens only.
		/// </summary>
		public static bool AreSamePhysical(string a, string b)
		{
			if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
				return false;

			if (StableKeyToken.TokensEqual(a, b))
				return true;

			if (!TryResolve(a, out string idA) || !TryResolve(b, out string idB))
				return false;

			return string.Equals(idA, idB, StringComparison.OrdinalIgnoreCase);
		}

		/// <summary>
		/// Resolves a save/capture token to a physical id. Does not rewrite the token for persistence.
		/// </summary>
		public static bool TryResolve(string token, out string physicalId)
			=> TryResolveDetailed(token, out physicalId, out _, out _, out _, out _, out _, out _);

		/// <summary>
		/// Detailed resolve for diagnostics. <paramref name="scan"/>/<paramref name="e0"/>/<paramref name="e1"/>
		/// are filled for SC-based PHYS ids; mouse/wheel leave scan=0.
		/// </summary>
		public static bool TryResolveDetailed(
			string token,
			out string physicalId,
			out string branch,
			out ushort scan,
			out bool e0,
			out bool e1,
			out uint vk,
			out string keysName)
		{
			physicalId = null;
			branch = "fail:empty";
			scan = 0;
			e0 = false;
			e1 = false;
			vk = 0;
			keysName = "-";

			if (string.IsNullOrWhiteSpace(token))
			{
				return false;
			}

			string input = token.Trim();
			if (StableKeyToken.IsDisplayAlias(input))
			{
				branch = "fail:display-alias";
				return false;
			}

			string canonical = StableKeyToken.CanonicalizeOrNull(input) ?? input;

			if (StableKeyToken.IsMouseButtonToken(canonical)
				&& StableKeyToken.TryParse(canonical, out bool isMouse, out int mouseButton, out _)
				&& isMouse)
			{
				physicalId = FormatMouse(mouseButton);
				branch = "special:mouse-button";
				return true;
			}

			if (StableKeyToken.IsMouseWheelUpToken(canonical))
			{
				physicalId = FormatWheel(up: true);
				branch = "special:mouse-wheel-up";
				return true;
			}

			if (StableKeyToken.IsMouseWheelDownToken(canonical))
			{
				physicalId = FormatWheel(up: false);
				branch = "special:mouse-wheel-down";
				return true;
			}

			if (StableKeyToken.IsMouseWheelLeftToken(canonical))
			{
				physicalId = FormatHorizontalWheel(right: false);
				branch = "special:mouse-wheel-left";
				return true;
			}

			if (StableKeyToken.IsMouseWheelRightToken(canonical))
			{
				physicalId = FormatHorizontalWheel(right: true);
				branch = "special:mouse-wheel-right";
				return true;
			}

			if (PhysicalKeyToken.TryParseScan(canonical, out scan, out bool extended))
			{
				e0 = extended;
				e1 = false;
				physicalId = Format(scan, e0, e1);
				branch = "ScanToken";
				return true;
			}

			if (PhysicalKeyToken.TryParseVk(canonical, out vk))
			{
				if (TryResolveFromVirtualKeyDetailed(vk, canonical, out physicalId, out branch, out scan, out e0, out e1, out keysName))
				{
					return true;
				}

				branch = "fail:vk-unresolved";
				return false;
			}

			if (string.Equals(canonical, StableKeyToken.HankakuZenkakuToken, StringComparison.OrdinalIgnoreCase))
			{
				scan = 0x29;
				physicalId = Format(scan, false, false);
				keysName = StableKeyToken.HankakuZenkakuToken;
				branch = "special:HankakuZenkaku";
				return true;
			}

			if (string.Equals(canonical, StableKeyToken.ScrollLockToken, StringComparison.OrdinalIgnoreCase))
			{
				scan = 0x46;
				physicalId = Format(scan, false, false);
				keysName = StableKeyToken.ScrollLockToken;
				branch = "special:ScrollLock";
				return true;
			}

			if (KeysNameToVk.TryGetValue(canonical, out vk))
			{
				keysName = canonical;
				if (UsSet1Fallback.TryGetValue(canonical, out var fbKnown))
				{
					scan = fbKnown.Scan;
					e0 = fbKnown.E0;
					e1 = fbKnown.E1;
					physicalId = Format(scan, e0, e1);
					branch = "KeysName+UsSet1Fallback";
					return true;
				}

				if (TryResolveFromVirtualKeyDetailed(vk, canonical, out physicalId, out branch, out scan, out e0, out e1, out keysName))
				{
					return true;
				}
			}

			if (UsSet1Fallback.TryGetValue(canonical, out var fallback))
			{
				keysName = canonical;
				scan = fallback.Scan;
				e0 = fallback.E0;
				e1 = fallback.E1;
				physicalId = Format(scan, e0, e1);
				branch = "UsSet1Fallback-only";
				return true;
			}

			branch = "fail:unresolved";
			return false;
		}

		private static bool TryResolveFromVirtualKeyDetailed(
			uint vkCode,
			string keysNameHint,
			out string physicalId,
			out string branch,
			out ushort scan,
			out bool e0,
			out bool e1,
			out string keysName)
		{
			physicalId = null;
			branch = "fail:vk";
			scan = 0;
			e0 = false;
			e1 = false;
			keysName = string.IsNullOrEmpty(keysNameHint) ? "-" : keysNameHint;

			if (vkCode == 0)
				return false;

			if (vkCode == 0x13 /* VK_PAUSE */)
			{
				scan = 0x45;
				e1 = true;
				physicalId = Format(scan, false, true);
				branch = "special:Pause-E1";
				keysName = "Pause";
				return true;
			}

			if (!string.IsNullOrEmpty(keysNameHint)
				&& UsSet1Fallback.TryGetValue(keysNameHint, out var fbByName))
			{
				scan = fbByName.Scan;
				e0 = fbByName.E0;
				e1 = fbByName.E1;
				physicalId = Format(scan, e0, e1);
				branch = "VkToken+UsSet1Fallback-byHint";
				keysName = keysNameHint;
				return true;
			}

			foreach (KeyValuePair<string, uint> pair in KeysNameToVk)
			{
				if (pair.Value != vkCode)
					continue;
				if (!UsSet1Fallback.TryGetValue(pair.Key, out var fb))
					continue;
				scan = fb.Scan;
				e0 = fb.E0;
				e1 = fb.E1;
				physicalId = Format(scan, e0, e1);
				branch = "VkToken+UsSet1Fallback-byVkLookup";
				keysName = pair.Key;
				return true;
			}

			if (OperatingSystem.IsWindows() && TryMapVirtualKey(vkCode, out scan, out e0))
			{
				e1 = false;
				physicalId = Format(scan, e0, false);
				branch = "MapVirtualKeyEx";
				return true;
			}

			return false;
		}

		private static bool TryMapVirtualKey(uint vk, out ushort scan, out bool e0)
		{
			scan = 0;
			e0 = false;
			try
			{
				IntPtr hkl = GetKeyboardLayout(0);
				uint mapped = MapVirtualKeyEx(vk, MapVkToVscEx, hkl);
				if (mapped != 0)
				{
					scan = (ushort)(mapped & 0xFF);
					e0 = (mapped & 0xE000) != 0 || ((mapped >> 8) & 0xFF) == 0xE0;
					return scan != 0;
				}

				mapped = MapVirtualKeyEx(vk, MapVkToVsc, hkl);
				scan = (ushort)(mapped & 0xFF);
				return scan != 0;
			}
			catch
			{
				return false;
			}
		}
	}
}
