using System;
using System.Runtime.InteropServices;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Parses WM_KEYDOWN / WM_KEYUP / WM_SYSKEYDOWN / WM_SYSKEYUP exactly as documented in
	/// Microsoft Learn: "Keyboard Input Overview" (Keystroke Message Flags).
	/// https://learn.microsoft.com/windows/win32/inputdev/about-keyboard-input
	/// </summary>
	public static class Win32KeystrokeMessage
	{
		public const int KF_EXTENDED = 0x0100;
		public const int KF_REPEAT = 0x4000;
		public const int KF_UP = 0x8000;

		private const uint VK_SHIFT = 0x10;
		private const uint VK_CONTROL = 0x11;
		private const uint VK_MENU = 0x12;
		private const uint MapVscToVkEx = 3;

		[DllImport("user32.dll")]
		private static extern uint MapVirtualKeyW(uint uCode, uint uMapType);

		public readonly struct Parsed
		{
			public uint Vk { get; init; }
			public ushort Scan { get; init; }
			/// <summary>True when KF_EXTENDED is set (Scan 1 Make has 0xE0 prefix).</summary>
			public bool Extended { get; init; }
			/// <summary>Scan with high byte 0xE0 when extended (MAKEWORD(scan, 0xE0)).</summary>
			public ushort ScanWithE0Prefix { get; init; }
			public bool WasKeyDown { get; init; }
			public bool IsKeyReleased { get; init; }
			public ushort RepeatCount { get; init; }
			public bool IsDown => !IsKeyReleased;
		}

		/// <summary>
		/// Parse wParam/lParam of a keystroke message using the Microsoft sample layout.
		/// Distinguishes L/R Shift, Control, and Menu via MAPVK_VSC_TO_VK_EX.
		/// </summary>
		public static Parsed Parse(IntPtr wParam, IntPtr lParam)
		{
			uint vkCode = (uint)(wParam.ToInt64() & 0xFFFF);
			uint l = unchecked((uint)lParam.ToInt64());
			ushort keyFlags = (ushort)((l >> 16) & 0xFFFF);
			ushort scanCode = (ushort)(keyFlags & 0xFF);
			bool isExtendedKey = (keyFlags & KF_EXTENDED) == KF_EXTENDED;

			ushort scanWithPrefix = scanCode;
			if (isExtendedKey)
				scanWithPrefix = (ushort)(scanCode | 0xE000);

			bool wasKeyDown = (keyFlags & KF_REPEAT) == KF_REPEAT;
			ushort repeatCount = (ushort)(l & 0xFFFF);
			bool isKeyReleased = (keyFlags & KF_UP) == KF_UP;

			if (OperatingSystem.IsWindows())
			{
				switch (vkCode)
				{
					case VK_SHIFT:
					case VK_CONTROL:
					case VK_MENU:
					{
						uint mapped = MapVirtualKeyW(scanWithPrefix, MapVscToVkEx);
						if (mapped != 0)
							vkCode = mapped & 0xFF;
						break;
					}
				}
			}

			return new Parsed {
				Vk = vkCode,
				Scan = scanCode,
				Extended = isExtendedKey,
				ScanWithE0Prefix = scanWithPrefix,
				WasKeyDown = wasKeyDown,
				IsKeyReleased = isKeyReleased,
				RepeatCount = repeatCount
			};
		}
	}

	/// <summary>
	/// Single acquisition policy: Microsoft keystroke messages only.
	/// </summary>
	public static class Win32KeyboardAcquisitionPolicy
	{
		public const bool KeystrokeMessagesOnly = true;

		public static bool IsKeystrokeMessageSource(string source)
		{
			if (string.IsNullOrEmpty(source))
				return false;
			return source.StartsWith("WM_KEY", StringComparison.OrdinalIgnoreCase)
				|| source.StartsWith("WM_SYSKEY", StringComparison.OrdinalIgnoreCase)
				|| source.StartsWith("WndProc", StringComparison.OrdinalIgnoreCase);
		}

		public static bool AllowAcquisitionFrom(string source)
			=> !KeystrokeMessagesOnly || IsKeystrokeMessageSource(source);
	}
}
