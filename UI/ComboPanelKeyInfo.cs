using System;
using System.Globalization;
using System.Runtime.InteropServices;
using AdvancedKeybinds.Binding;
using AdvancedKeybinds.Core;
using Microsoft.Xna.Framework.Input;

namespace AdvancedKeybinds.UI
{
	/// <summary>
	/// Right-column key info for ComboPanel: describe a selected token (no live Raw Input).
	/// </summary>
	internal static class ComboPanelKeyInfo
	{
		private const uint MapVkToVscEx = 4;

		[DllImport("user32.dll")]
		private static extern uint MapVirtualKeyEx(uint uCode, uint uMapType, IntPtr dwhkl);

		[DllImport("user32.dll")]
		private static extern IntPtr GetKeyboardLayout(int idThread);

		public struct Detail
		{
			/// <summary>Original key identity (FormatToken), never the user display alias.</summary>
			public string CurrentKey;
			public string DisplayName;
			public string DefaultName;
			public string ScanCode;
			/// <summary>null = unknown/n/a, true = E0 present, false = none.</summary>
			public bool? E0;
			public bool? E1;
		}

		public static Detail Describe(string token)
		{
			var d = new Detail {
				CurrentKey = "—",
				DisplayName = "—",
				DefaultName = "—",
				ScanCode = "—",
				E0 = null,
				E1 = false
			};

			if (string.IsNullOrWhiteSpace(token))
				return d;

			token = token.Trim();
			d.DefaultName = KeyToken.FormatToken(token);
			if (string.IsNullOrEmpty(d.DefaultName))
				d.DefaultName = token;

			// Original / technical identity (never the user alias).
			d.CurrentKey = d.DefaultName;

			d.DisplayName = KeyToken.FormatDisplayForToken(token);
			if (string.IsNullOrEmpty(d.DisplayName))
				d.DisplayName = d.DefaultName;

			if (PhysicalKeyToken.TryParseScan(token, out ushort scan, out bool extended))
			{
				d.ScanCode = FormatScLabel(scan);
				d.E0 = extended;
				d.E1 = false;
				return d;
			}

			if (PhysicalKeyToken.TryParseVk(token, out uint vk))
			{
				FillScanFromVk(ref d, vk);
				return d;
			}

			if (StableKeyToken.TryParse(token, out bool isMouse, out _, out string canonical))
			{
				if (isMouse || StableKeyToken.IsMouseWheelToken(canonical))
				{
					d.ScanCode = "—";
					d.E0 = null;
					d.E1 = null;
					return d;
				}

				if (Enum.TryParse(canonical, ignoreCase: true, out Keys key) && key != Keys.None)
					FillScanFromVk(ref d, (uint)(int)key);

				return d;
			}

			if (StableKeyToken.IsHankakuZenkakuToken(token))
			{
				d.ScanCode = "—";
				d.E0 = null;
				d.E1 = null;
			}

			return d;
		}

		private static void FillScanFromVk(ref Detail d, uint vk)
		{
			if (!OperatingSystem.IsWindows() || vk == 0)
			{
				d.ScanCode = "—";
				d.E0 = null;
				d.E1 = false;
				return;
			}

			try
			{
				IntPtr hkl = GetKeyboardLayout(0);
				uint mapped = MapVirtualKeyEx(vk, MapVkToVscEx, hkl);
				if (mapped == 0)
				{
					d.ScanCode = "—";
					d.E0 = null;
					d.E1 = false;
					return;
				}

				d.ScanCode = FormatScLabel((ushort)(mapped & 0xFF));
				bool e0 = (mapped & 0x100) != 0 || ((mapped >> 8) & 0xFF) == 0xE0;
				d.E0 = e0;
				d.E1 = false;
			}
			catch
			{
				d.ScanCode = "—";
				d.E0 = null;
				d.E1 = false;
			}
		}

		private static string FormatScLabel(ushort scan)
			=> "SC" + (scan & 0xFF).ToString("X2", CultureInfo.InvariantCulture);
	}
}
