using System;
using System.Runtime.InteropServices;

namespace AdvancedKeybinds.Binding
{
	/// <summary>Detects Windows keyboard layout and physical keyboard type for Caps Hold viability.</summary>
	internal static class KeyboardLayoutHelper
	{
		private const uint JapaneseKeyboardLayoutId = 0x0411;
		/// <summary>Win32 <c>GetKeyboardType(0)</c> value for a Japanese (JIS) physical keyboard.</summary>
		private const int JapanesePhysicalKeyboardType = 7;

		[DllImport("user32.dll")]
		private static extern IntPtr GetKeyboardLayout(int idThread);

		[DllImport("user32.dll")]
		private static extern int GetKeyboardType(int nTypeFlag);

		public static bool IsJapaneseKeyboardLayout()
		{
			uint lang = (uint)GetKeyboardLayout(0).ToInt64() & 0xFFFF;
			return lang == JapaneseKeyboardLayoutId;
		}

		/// <summary>True when Windows reports a JIS physical keyboard (independent of the active layout).</summary>
		public static bool IsJapanesePhysicalKeyboard()
			=> GetKeyboardType(0) == JapanesePhysicalKeyboardType;

		/// <summary>
		/// Caps Lock can only be held on ANSI-style hardware. JIS keyboards expose 英数 as a momentary key
		/// even when the user switches Windows to English (US) layout.
		/// </summary>
		public static bool IsCapsLockHoldViable()
			=> !IsJapaneseKeyboardLayout() && !IsJapanesePhysicalKeyboard();
	}
}
