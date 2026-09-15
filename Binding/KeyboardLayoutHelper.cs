using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace AdvancedKeybinds.Binding
{
	/// <summary>Detects Windows keyboard layout and physical keyboard type for Caps Hold viability.</summary>
	internal static class KeyboardLayoutHelper
	{
		private const uint JapaneseKeyboardLayoutId = 0x0411;
		private const uint KoreanKeyboardLayoutId = 0x0412;
		/// <summary>Win32 <c>GetKeyboardType(0)</c> value for a Japanese (JIS) physical keyboard.</summary>
		private const int JapanesePhysicalKeyboardType = 7;

		/// <summary>Win10+: ToUnicodeEx must not mutate the thread dead-key state.</summary>
		private const uint ToUnicodeDontChangeKeyboardState = 0x4;
		private const uint MapVkToVsc = 0;
		private const uint MapVkToVscEx = 4;
		private const uint MapVscToVkEx = 3;

		[DllImport("user32.dll")]
		private static extern IntPtr GetKeyboardLayout(int idThread);

		[DllImport("user32.dll")]
		private static extern int GetKeyboardLayoutList(int nBuff, [Out] IntPtr[] lpList);

		[DllImport("user32.dll")]
		private static extern int GetKeyboardType(int nTypeFlag);

		[DllImport("user32.dll")]
		private static extern uint MapVirtualKeyEx(uint uCode, uint uMapType, IntPtr dwhkl);

		[DllImport("user32.dll", CharSet = CharSet.Unicode)]
		private static extern int ToUnicodeEx(
			uint wVirtKey,
			uint wScanCode,
			byte[] lpKeyState,
			[Out] StringBuilder pwszBuff,
			int cchBuff,
			uint wFlags,
			IntPtr dwhkl);

		public static bool IsJapaneseKeyboardLayout()
		{
			// Windows layout APIs live in user32; off Windows there is no HKL to inspect.
			if (!OperatingSystem.IsWindows())
				return false;

			uint lang = (uint)GetKeyboardLayout(0).ToInt64() & 0xFFFF;
			return lang == JapaneseKeyboardLayoutId;
		}

		public static bool IsKoreanKeyboardLayout()
		{
			if (!OperatingSystem.IsWindows())
				return false;

			uint lang = (uint)GetKeyboardLayout(0).ToInt64() & 0xFFFF;
			return lang == KoreanKeyboardLayoutId;
		}

		/// <summary>True when Windows reports a JIS physical keyboard (independent of the active layout).</summary>
		public static bool IsJapanesePhysicalKeyboard()
			=> OperatingSystem.IsWindows() && GetKeyboardType(0) == JapanesePhysicalKeyboardType;

		/// <summary>
		/// True when 「半角／全角」 can appear on scan 0x29 (JIS hardware and/or Japanese layout).
		/// USANSI + US layout stays false so grave/` OemTilde remains a normal bind.
		/// </summary>
		public static bool IsJapaneseHankakuKeyContext()
			=> IsJapanesePhysicalKeyboard() || IsJapaneseKeyboardLayout();

		/// <summary>
		/// Caps Lock can only be held on ANSI-style hardware. JIS keyboards expose 英数 as a momentary key
		/// even when the user switches Windows to English (US) layout.
		/// </summary>
		public static bool IsCapsLockHoldViable()
			=> !IsJapaneseKeyboardLayout() && !IsJapanesePhysicalKeyboard();

		/// <summary>Active thread keyboard layout HKL (for capture layout-change recovery).</summary>
		public static IntPtr GetActiveLayoutHandle()
		{
			if (!OperatingSystem.IsWindows())
				return IntPtr.Zero;

			try
			{
				return GetKeyboardLayout(0);
			}
			catch
			{
				return IntPtr.Zero;
			}
		}

		internal sealed class InstalledLayoutInfo
		{
			public IntPtr Hkl { get; init; }
			public ushort LangId { get; init; }
			public string LayoutIdentifier { get; init; }
			public string CultureName { get; init; }
			public bool IsActive { get; init; }
			public bool IsInstalled { get; init; }
		}

		/// <summary>
		/// Enumerates only HKLs currently installed in this Windows session. It never activates or switches
		/// a layout. Culture is derived from LANGID and is not a physical-keyboard assertion.
		/// </summary>
		internal static IReadOnlyList<InstalledLayoutInfo> GetInstalledLayouts()
		{
			var result = new List<InstalledLayoutInfo>();
			if (!OperatingSystem.IsWindows())
				return result;

			int count = GetKeyboardLayoutList(0, null);
			if (count <= 0)
				return result;

			var handles = new IntPtr[count];
			int written = GetKeyboardLayoutList(handles.Length, handles);
			IntPtr active = GetActiveLayoutHandle();
			var seen = new HashSet<long>();
			for (int i = 0; i < written && i < handles.Length; i++)
			{
				IntPtr hkl = handles[i];
				if (hkl == IntPtr.Zero || !seen.Add(hkl.ToInt64()))
					continue;

				ushort langId = unchecked((ushort)(hkl.ToInt64() & 0xFFFF));
				string culture;
				try
				{
					culture = CultureInfo.GetCultureInfo(langId).Name;
				}
				catch (CultureNotFoundException)
				{
					culture = "unknown";
				}

				result.Add(new InstalledLayoutInfo {
					Hkl = hkl,
					LangId = langId,
					LayoutIdentifier = unchecked((ulong)hkl.ToInt64()).ToString("X16"),
					CultureName = culture,
					IsActive = hkl == active,
					IsInstalled = true
				});
			}

			return result;
		}

		/// <summary>
		/// Windows keyboard-type evidence. GetKeyboardType is not enough to prove keycaps, ANSI/ISO,
		/// or the exact attached device, so the result is explicitly estimated/unknown.
		/// </summary>
		internal static string DescribePhysicalLayoutEvidence()
		{
			if (!OperatingSystem.IsWindows())
				return "PhysicalLayoutUnknown reason=non-Windows";

			int type = GetKeyboardType(0);
			int subType = GetKeyboardType(1);
			int functionKeys = GetKeyboardType(2);
			if (type == JapanesePhysicalKeyboardType)
			{
				return "PhysicalLayoutEstimated family=JIS evidence=GetKeyboardType"
					+ " keyboardType=" + type
					+ " keyboardSubType=" + subType
					+ " functionKeys=" + functionKeys
					+ " note=does-not-prove-keycaps-or-specific-device";
			}

			return "PhysicalLayoutUnknown"
				+ " keyboardType=" + type
				+ " keyboardSubType=" + subType
				+ " functionKeys=" + functionKeys
				+ " reason=Windows-logical-layout-and-keyboard-type-do-not-prove-ANSI-ISO-JIS-keycaps";
		}

		/// <summary>One-line layout snapshot for capture-begin diagnostics.</summary>
		public static string FormatCaptureLayoutDiag()
		{
			if (!OperatingSystem.IsWindows())
				return "layout=unavailable reason=non-Windows";

			uint lang = (uint)GetKeyboardLayout(0).ToInt64() & 0xFFFF;
			string jpTag = lang == JapaneseKeyboardLayoutId ? "(JP)" : string.Empty;
			return "layout=0x" + lang.ToString("X4") + jpTag
				+ " physicalJIS=" + IsJapanesePhysicalKeyboard()
				+ " CapsHoldViable=" + IsCapsLockHoldViable();
		}

		/// <summary>
		/// Unshifted base glyph for a virtual key under the active layout (letter/digit/symbol legend).
		/// Does not mutate sticky/dead-key state: uses a zeroed key-state buffer and non-consuming flags.
		/// Returns false for dead keys (<c>written &lt; 0</c>), empty, or control results.
		/// </summary>
		public static bool TryGetUnshiftedVkGlyph(uint vkCode, out string glyph)
			=> TryGetUnshiftedVkGlyph(vkCode, GetActiveLayoutHandle(), out glyph);

		public static bool TryGetUnshiftedVkGlyph(uint vkCode, IntPtr layout, out string glyph)
		{
			glyph = null;
			if (!OperatingSystem.IsWindows() || vkCode == 0)
				return false;

			if (layout == IntPtr.Zero)
				layout = GetActiveLayoutHandle();

			uint scan = MapVirtualKeyEx(vkCode, 0 /* MAPVK_VK_TO_VSC */, layout);
			byte[] keyState = new byte[256];
			var buffer = new StringBuilder(8);

			int written = ToUnicodeEx(
				vkCode,
				scan,
				keyState,
				buffer,
				buffer.Capacity,
				ToUnicodeDontChangeKeyboardState,
				layout);
			// Dead key / no translation: never surface an empty or composed accent as a legend.
			if (written <= 0)
				return false;

			string text = buffer.ToString();
			if (text.Length == 0)
				return false;

			char ch = text[0];
			if (char.IsControl(ch) || ch == ' ')
				return false;

			glyph = ch.ToString();
			return true;
		}

		/// <summary>Scan code and extended flag for a virtual key (layout-aware).</summary>
		public static bool TryGetVkScanEx(uint vkCode, IntPtr layout, out uint scanCode, out bool extended)
		{
			scanCode = 0;
			extended = false;
			if (!OperatingSystem.IsWindows() || vkCode == 0)
				return false;

			if (layout == IntPtr.Zero)
				layout = GetActiveLayoutHandle();

			uint vscEx = MapVirtualKeyEx(vkCode, MapVkToVscEx, layout);
			if (vscEx != 0)
			{
				extended = (vscEx & 0xE000) != 0;
				scanCode = vscEx & 0xFF;
				return scanCode != 0;
			}

			scanCode = MapVirtualKeyEx(vkCode, MapVkToVsc, layout) & 0xFF;
			return scanCode != 0;
		}

		/// <summary>Virtual key for a make-code + extended prefix (layout-aware).</summary>
		public static bool TryGetVkFromScanEx(uint scanCode, bool extended, IntPtr layout, out uint vkCode)
		{
			vkCode = 0;
			if (!OperatingSystem.IsWindows() || scanCode == 0)
				return false;

			if (layout == IntPtr.Zero)
				layout = GetActiveLayoutHandle();

			uint vsc = scanCode & 0xFF;
			if (extended)
				vsc |= 0xE000;

			vkCode = MapVirtualKeyEx(vsc, MapVscToVkEx, layout);
			return vkCode != 0;
		}

		/// <summary>Unshifted layout glyph for a scan make-code (UI display only).</summary>
		public static bool TryGetUnshiftedScanGlyph(uint scanCode, bool extended, out string glyph)
			=> TryGetUnshiftedScanGlyph(scanCode, extended, GetActiveLayoutHandle(), out glyph);

		public static bool TryGetUnshiftedScanGlyph(
			uint scanCode,
			bool extended,
			IntPtr layout,
			out string glyph)
		{
			glyph = null;
			if (!TryGetVkFromScanEx(scanCode, extended, layout, out uint vk))
				return false;

			return TryGetUnshiftedVkGlyph(vk, layout, out glyph);
		}

		/// <summary>Raw ToUnicodeEx result (includes dead-key / empty — for diagnostics only).</summary>
		public static int QueryToUnicodeEx(uint vkCode, uint scanCode, IntPtr layout, out string text)
		{
			text = string.Empty;
			if (!OperatingSystem.IsWindows() || vkCode == 0)
				return 0;

			if (layout == IntPtr.Zero)
				layout = GetActiveLayoutHandle();

			if (scanCode == 0)
				TryGetVkScanEx(vkCode, layout, out scanCode, out _);

			byte[] keyState = new byte[256];
			var buffer = new StringBuilder(8);
			int written = ToUnicodeEx(
				vkCode,
				scanCode,
				keyState,
				buffer,
				buffer.Capacity,
				ToUnicodeDontChangeKeyboardState,
				layout);
			text = buffer.ToString();
			return written;
		}

		/// <summary>AKDIAG: physical key snapshot during slot capture (registration must not depend on glyph).</summary>
		public static string FormatPhysicalKeyCaptureDiag(
			uint vkCode,
			string adoptedToken = null,
			uint? eventScan = null,
			bool? eventExtended = null)
		{
			IntPtr layout = GetActiveLayoutHandle();
			uint lang = layout != IntPtr.Zero ? (uint)layout.ToInt64() & 0xFFFF : 0;
			TryGetVkScanEx(vkCode, layout, out uint scan, out bool extended);
			if (eventScan.HasValue)
				scan = eventScan.Value;
			if (eventExtended.HasValue)
				extended = eventExtended.Value;

			string keysName = Enum.IsDefined(typeof(Microsoft.Xna.Framework.Input.Keys), (int)vkCode)
				? ((Microsoft.Xna.Framework.Input.Keys)vkCode).ToString()
				: "-";
			int written = QueryToUnicodeEx(vkCode, scan, layout, out string unicodeRaw);
			bool hasGlyph = TryGetUnshiftedVkGlyph(vkCode, layout, out string glyph);
			string unicode = string.IsNullOrEmpty(unicodeRaw)
				? "(empty)"
				: unicodeRaw.Replace("\\", "\\\\").Replace("\"", "\\\"");

			return "PhysicalKeyCapture keys=" + keysName
				+ " vk=0x" + vkCode.ToString("X2")
				+ " scan=0x" + scan.ToString("X2")
				+ " extended=" + extended
				+ " layout=0x" + lang.ToString("X4")
				+ " toUnicodeWritten=" + written
				+ " toUnicode=\"" + unicode + "\""
				+ " glyph=" + (hasGlyph ? "\"" + glyph + "\"" : "none")
				+ " adopted=" + (string.IsNullOrEmpty(adoptedToken) ? "-" : adoptedToken);
		}
	}
}
