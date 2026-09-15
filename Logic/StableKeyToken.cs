using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Stable internal key tokens (XNA/FNA <c>Keys</c> names, Mouse1–5, mouse wheel, HankakuZenkaku).
	/// Display labels are never used as save values.
	/// </summary>
	public static class StableKeyToken
	{
		public const string HankakuZenkakuToken = "HankakuZenkaku";
		public const string MouseWheelUpToken = "MouseWheelUp";
		public const string MouseWheelDownToken = "MouseWheelDown";
		public const string MouseWheelLeftToken = "MouseWheelLeft";
		public const string MouseWheelRightToken = "MouseWheelRight";
		/// <summary>Hook-managed Scroll Lock token (not <c>Keys.Scroll</c> / "Scroll").</summary>
		public const string ScrollLockToken = "ScrollLock";

		/// <summary>
		/// Canonical XNA/FNA <c>Keys</c> names Terraria/tModLoader persist as strings (via <c>ToString()</c>).
		/// Source of truth for round-trip tests; must not include display aliases (LeftClick, L-Ctrl, MB4, …).
		/// </summary>
		private static readonly string[] KnownKeysNames = {
			"None",
			"Back", "Tab", "Enter", "CapsLock", "Escape", "Space",
			"PageUp", "PageDown", "End", "Home",
			"Left", "Up", "Right", "Down",
			"Select", "Print", "Execute", "PrintScreen", "Insert", "Delete", "Help",
			"D0", "D1", "D2", "D3", "D4", "D5", "D6", "D7", "D8", "D9",
			"A", "B", "C", "D", "E", "F", "G", "H", "I", "J", "K", "L", "M",
			"N", "O", "P", "Q", "R", "S", "T", "U", "V", "W", "X", "Y", "Z",
			"LeftWindows", "RightWindows", "Apps", "Sleep",
			"NumPad0", "NumPad1", "NumPad2", "NumPad3", "NumPad4",
			"NumPad5", "NumPad6", "NumPad7", "NumPad8", "NumPad9",
			"Multiply", "Add", "Separator", "Subtract", "Decimal", "Divide",
			"F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12",
			"F13", "F14", "F15", "F16", "F17", "F18", "F19", "F20", "F21", "F22", "F23", "F24",
			"NumLock", "Scroll",
			"LeftShift", "RightShift", "LeftControl", "RightControl", "LeftAlt", "RightAlt",
			"BrowserBack", "BrowserForward", "BrowserRefresh", "BrowserStop",
			"BrowserSearch", "BrowserFavorites", "BrowserHome",
			"VolumeMute", "VolumeDown", "VolumeUp",
			"MediaNextTrack", "MediaPreviousTrack", "MediaStop", "MediaPlayPause",
			"LaunchMail", "SelectMedia", "LaunchApplication1", "LaunchApplication2",
			"OemSemicolon", "OemPlus", "OemComma", "OemMinus", "OemPeriod", "OemQuestion",
			"OemTilde", "OemOpenBrackets", "OemPipe", "OemCloseBrackets", "OemQuotes", "Oem8",
			"OemBackslash", "ProcessKey", "Attn", "Crsel", "Exsel", "EraseEof", "Play", "Zoom",
			"Pa1", "OemClear", "ChatPadGreen", "ChatPadOrange",
			"Pause",
			"ImeConvert", "ImeNoConvert", "Kana", "Kanji",
			"OemCopy", "OemAuto", "OemEnlW"
		};

		private static readonly HashSet<string> KnownKeysSet =
			new(KnownKeysNames, StringComparer.OrdinalIgnoreCase);

		private static readonly HashSet<string> OemKeyNames = new(StringComparer.OrdinalIgnoreCase) {
			"OemSemicolon", "OemPlus", "OemComma", "OemMinus", "OemPeriod", "OemQuestion",
			"OemTilde", "OemOpenBrackets", "OemPipe", "OemCloseBrackets", "OemQuotes",
			"OemBackslash", "Oem8", "OemClear", "OemCopy", "OemAuto", "OemEnlW"
		};

		private static readonly HashSet<string> DisplayAliasNames = new(StringComparer.OrdinalIgnoreCase) {
			"LeftClick", "RightClick", "MiddleClick", "MB4", "MB5",
			"L-Ctrl", "R-Ctrl", "L-Shift", "R-Shift", "L-Alt", "R-Alt",
			"L-Win", "R-Win",
			"無変換", "変換", "かな", "カタカナ/ひらがな", "半角/全角", "全角/半角", "全角／半角",
			// Korean layout labels for VK_KANA / VK_KANJI (display only; never save tokens).
			"Hangul", "Hanja",
			"WheelUp", "WheelDown", "Wheel UP", "Wheel Up", "Wheel Down",
			"WheelLeft", "WheelRight", "Wheel Left", "Wheel Right",
			"手前回し", "奥回し",
			// Legacy loc labels (pre unified Wheel Up/Down) — display only; never save tokens.
			"Toward you", "Away from you",
			"Zu sich", "Weg von sich",
			"Hacia ti", "Lejos de ti",
			"Vers soi", "Loin de soi",
			"Verso di sé", "Lontano da sé",
			"몸 쪽으로", "바깥쪽으로",
			"Do siebie", "Od siebie",
			"Na sua direção", "Para longe",
			"К себе", "От себя",
			"朝自己滚", "向外滚",
			"朝自己滾", "向外滾"
		};

		public static IReadOnlyList<string> AllKnownKeysNames { get; } =
			new ReadOnlyCollection<string>(KnownKeysNames);

		public static bool IsMouseToken(string token)
			=> IsMouseButtonToken(token) || IsMouseWheelToken(token);

		public static bool IsMouseButtonToken(string token)
		{
			if (!TryParse(token, out bool isMouse, out int mouseButton, out _))
				return false;
			return isMouse && mouseButton >= 1 && mouseButton <= 5;
		}

		public static bool IsMouseWheelToken(string token)
			=> IsMouseWheelUpToken(token)
				|| IsMouseWheelDownToken(token)
				|| IsMouseWheelLeftToken(token)
				|| IsMouseWheelRightToken(token);

		public static bool IsMouseWheelUpToken(string token)
			=> string.Equals(token, MouseWheelUpToken, StringComparison.OrdinalIgnoreCase);

		public static bool IsMouseWheelDownToken(string token)
			=> string.Equals(token, MouseWheelDownToken, StringComparison.OrdinalIgnoreCase);

		public static bool IsMouseWheelLeftToken(string token)
			=> string.Equals(token, MouseWheelLeftToken, StringComparison.OrdinalIgnoreCase);

		public static bool IsMouseWheelRightToken(string token)
			=> string.Equals(token, MouseWheelRightToken, StringComparison.OrdinalIgnoreCase);

		public static bool IsMouseWheelVerticalToken(string token)
			=> IsMouseWheelUpToken(token) || IsMouseWheelDownToken(token);

		public static bool IsMouseWheelHorizontalToken(string token)
			=> IsMouseWheelLeftToken(token) || IsMouseWheelRightToken(token);

		public static bool IsHankakuZenkakuToken(string token)
			=> string.Equals(token, HankakuZenkakuToken, StringComparison.OrdinalIgnoreCase);

		public static bool TryParse(string token, out bool isMouse, out int mouseButton, out string canonical)
		{
			isMouse = false;
			mouseButton = -1;
			canonical = null;

			if (string.IsNullOrWhiteSpace(token))
				return false;

			token = token.Trim();

			// Display labels must never parse as save tokens.
			if (DisplayAliasNames.Contains(token))
				return false;

			if (IsMouseWheelUpToken(token))
			{
				canonical = MouseWheelUpToken;
				return true;
			}

			if (IsMouseWheelDownToken(token))
			{
				canonical = MouseWheelDownToken;
				return true;
			}

			if (IsMouseWheelLeftToken(token))
			{
				canonical = MouseWheelLeftToken;
				return true;
			}

			if (IsMouseWheelRightToken(token))
			{
				canonical = MouseWheelRightToken;
				return true;
			}

			if (token.StartsWith("Mouse", StringComparison.OrdinalIgnoreCase))
			{
				if (!int.TryParse(token.AsSpan(5), out mouseButton) || mouseButton < 1 || mouseButton > 5)
					return false;

				isMouse = true;
				canonical = "Mouse" + mouseButton;
				return true;
			}

			if (IsHankakuZenkakuToken(token))
			{
				canonical = HankakuZenkakuToken;
				return true;
			}

			if (GamePadToken.TryCanonicalize(token, out string pad))
			{
				canonical = pad;
				return true;
			}

			if (PhysicalKeyToken.TryCanonicalize(token, out string physical))
			{
				canonical = physical;
				return true;
			}

			if (PhysicalKeyToken.IsKeyAliasPrefix(token))
			{
				token = PhysicalKeyToken.StripKeyAliasPrefix(token);
				if (string.IsNullOrEmpty(token))
					return false;
			}

			// Custom stable id used by the hook layer (distinct from Keys.Scroll → "Scroll").
			if (string.Equals(token, ScrollLockToken, StringComparison.OrdinalIgnoreCase))
			{
				canonical = ScrollLockToken;
				return true;
			}

			if (!KnownKeysSet.Contains(token))
				return false;

			// Preserve canonical casing from the known list.
			foreach (string name in KnownKeysNames)
			{
				if (!string.Equals(name, token, StringComparison.OrdinalIgnoreCase))
					continue;

				// Keys.None is never a bindable slot token.
				if (string.Equals(name, "None", StringComparison.OrdinalIgnoreCase))
					return false;

				canonical = name;
				return true;
			}

			return false;
		}

		/// <summary>
		/// Normalizes a captured/saved token to the stable internal id, or null when unknown/invalid.
		/// Does not apply layout-specific FNA masquerade rules (those stay in the game layer).
		/// </summary>
		public static string CanonicalizeOrNull(string token)
		{
			if (!TryParse(token, out _, out _, out string canonical))
				return null;
			return canonical;
		}

		public static bool IsOemToken(string token)
		{
			if (PhysicalKeyToken.IsFallbackToken(token))
				return true;

			return TryParse(token, out bool isMouse, out _, out string canonical)
				&& !isMouse
				&& OemKeyNames.Contains(canonical);
		}

		public static bool IsModifierToken(string token)
		{
			if (PhysicalKeyToken.TryParseVk(token, out uint vk))
			{
				return vk is 0xA2 or 0xA3 or 0xA0 or 0xA1 or 0xA4 or 0xA5 or 0x5B or 0x5C;
			}

			if (!TryParse(token, out bool isMouse, out _, out string canonical) || isMouse)
				return false;

			return canonical is "LeftControl" or "RightControl" or "LeftShift" or "RightShift"
				or "LeftAlt" or "RightAlt" or "LeftWindows" or "RightWindows";
		}

		/// <summary>UI display only — never persist the returned string.</summary>
		public static string FormatForDisplay(string token)
		{
			if (string.IsNullOrWhiteSpace(token))
				return string.Empty;

			if (GamePadToken.IsPadToken(token))
				return GamePadToken.FormatForDisplay(token);

			if (!TryParse(token, out bool isMouse, out int mouseButton, out string canonical))
			{
				if (PhysicalKeyToken.TryParseVk(token, out uint vk))
					return PhysicalKeyToken.FormatVk(vk);
				if (PhysicalKeyToken.TryParseScan(token, out ushort scan, out bool extended))
					return PhysicalKeyToken.FormatScan(scan, extended);
				return token;
			}

			if (GamePadToken.IsPadToken(canonical))
				return GamePadToken.FormatForDisplay(canonical);

			if (isMouse)
			{
				return mouseButton switch {
					1 => "LeftClick",
					2 => "RightClick",
					3 => "MiddleClick",
					4 => "MB4",
					5 => "MB5",
					_ => canonical
				};
			}

			return canonical switch {
				"LeftControl" => "L-Ctrl",
				"RightControl" => "R-Ctrl",
				"LeftShift" => "L-Shift",
				"RightShift" => "R-Shift",
				"LeftAlt" => "L-Alt",
				"RightAlt" => "R-Alt",
				"LeftWindows" => "L-Win",
				"RightWindows" => "R-Win",
				"ImeNoConvert" => "無変換",
				"ImeConvert" => "変換",
				"Kana" => "カタカナ/ひらがな",
				// Physical key legend on kbd106 is often 「半角／全角」; UI shows 「全角/半角」 (token stays HankakuZenkaku).
				HankakuZenkakuToken => "全角/半角",
				MouseWheelUpToken => "Wheel Up",
				MouseWheelDownToken => "Wheel Down",
				MouseWheelLeftToken => "Wheel Left",
				MouseWheelRightToken => "Wheel Right",
				_ => canonical
			};
		}

		/// <summary>True when display text must never be written back as a binding token.</summary>
		public static bool IsDisplayAlias(string value)
			=> !string.IsNullOrEmpty(value) && DisplayAliasNames.Contains(value.Trim());

		public static bool TokensEqual(string a, string b)
			=> string.Equals(CanonicalizeOrNull(a) ?? a, CanonicalizeOrNull(b) ?? b, StringComparison.OrdinalIgnoreCase);
	}
}
