using System;
using System.Collections.Generic;
using System.Linq;
using AdvancedKeybinds.Patches;
using Microsoft.Xna.Framework.Input;
using Terraria;

namespace AdvancedKeybinds.Binding
{
	public static class KeyToken
	{
		public static bool TryParse(string token, out Keys key, out bool isMouse, out int mouseButton)
		{
			key = Keys.None;
			isMouse = false;
			mouseButton = -1;

			if (string.IsNullOrWhiteSpace(token))
				return false;

			if (token.StartsWith("Mouse", StringComparison.OrdinalIgnoreCase))
			{
				isMouse = true;
				return int.TryParse(token.AsSpan(5), out mouseButton) && mouseButton >= 1 && mouseButton <= 5;
			}

			return Enum.TryParse(token, true, out key);
		}

		public static bool IsDown(string token)
		{
			if (string.Equals(token, BindingRestrictions.ScrollLockToken, StringComparison.OrdinalIgnoreCase))
				return WindowsKeySuppressor.IsScrollLockPhysicallyDown;

			if (!TryParse(token, out Keys key, out bool isMouse, out int mouseButton))
				return false;

			if (isMouse)
				return IsMouseDown(mouseButton);

			if (key == Keys.LeftWindows)
				return WindowsKeySuppressor.IsLeftWindowsDown;

			if (key == Keys.RightWindows)
				return WindowsKeySuppressor.IsRightWindowsDown;

			if (key == Keys.CapsLock)
				return WindowsKeySuppressor.IsCapsLockPhysicallyDown;

			if (key == Keys.NumLock)
				return WindowsKeySuppressor.IsNumLockPhysicallyDown;

			if (key == Keys.Escape)
				return Main.keyState.IsKeyDown(key) || WindowsKeySuppressor.IsShellBlockedEscapeDown;

			return Main.keyState.IsKeyDown(key);
		}

		public static bool IsMouseDown(int mouseButton)
		{
			return mouseButton switch {
				1 => MouseInputHelper.IsPhysicalLeftDown(),
				2 => MouseInputHelper.IsPhysicalRightDown(),
				3 => MouseInputHelper.IsPhysicalMiddleDown(),
				4 => MouseInputHelper.IsMouseX1Down(),
				5 => MouseInputHelper.IsMouseX2Down(),
				_ => false
			};
		}

		public static string FormatToken(string token)
		{
			if (string.IsNullOrWhiteSpace(token))
				return string.Empty;

			if (token.StartsWith("Mouse", StringComparison.OrdinalIgnoreCase)
				&& int.TryParse(token.AsSpan(5), out int mouseButton))
			{
				return mouseButton switch {
					1 => "LeftClick",
					2 => "RightClick",
					3 => "MiddleClick",
					4 => "Mouse4",
					5 => "Mouse5",
					_ => token
				};
			}

			return token switch {
				"LeftControl" => "L-Ctrl",
				"RightControl" => "R-Ctrl",
				"LeftShift" => "L-Shift",
				"RightShift" => "R-Shift",
				"LeftAlt" => "L-Alt",
				"RightAlt" => "R-Alt",
				"LeftWindows" => "L-Win",
				"RightWindows" => "R-Win",
				_ => token
			};
		}

		public static string FormatSet(IReadOnlyList<string> keys, bool compact)
		{
			if (keys == null || keys.Count == 0)
				return string.Empty;

			string separator = compact ? "+" : " + ";
			return string.Join(separator, keys.Select(KeyToken.FormatToken));
		}

		public static HashSet<string> SnapshotPressedTokens()
		{
			HashSet<string> pressed = new(StringComparer.OrdinalIgnoreCase);
			KeyboardState keyboard = Main.keyState;

			foreach (Keys key in Enum.GetValues<Keys>())
			{
				if (key == Keys.None)
					continue;

				if (BindingRestrictions.IsToggleLockKey(key))
					continue;

				if (keyboard.IsKeyDown(key))
					pressed.Add(key.ToString());
			}

			// Toggle-lock gameplay taps are edge-driven (see BindingInputEngine); latch covers combos.
			if (WindowsKeySuppressor.IsSlotCapturePassthrough && !WindowsKeySuppressor.IsInstalled)
			{
				if (keyboard.IsKeyDown(Keys.CapsLock))
					pressed.Add("CapsLock");
				if (keyboard.IsKeyDown(Keys.NumLock))
					pressed.Add("NumLock");
			}

			// The Windows keys are swallowed by the low-level hook, so pull their state from there.
			if (WindowsKeySuppressor.IsLeftWindowsDown)
				pressed.Add("LeftWindows");
			if (WindowsKeySuppressor.IsRightWindowsDown)
				pressed.Add("RightWindows");

			if (WindowsKeySuppressor.IsShellBlockedEscapeDown)
				pressed.Add("Escape");

			for (int i = 1; i <= 5; i++)
			{
				if (IsMouseDown(i))
					pressed.Add($"Mouse{i}");
			}

			return pressed;
		}

		public static bool IsMouseToken(string token)
			=> token != null && token.StartsWith("Mouse", System.StringComparison.OrdinalIgnoreCase);
	}
}
