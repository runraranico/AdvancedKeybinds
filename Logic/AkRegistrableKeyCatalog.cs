using System;
using System.Collections.Generic;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Registrable advanced-path tokens for exhaustive combo regression.
	/// Prefers measured <c>SC:</c> forms so 1-key bindings are Advanced-owned
	/// (plain Keys-name singles stay vanilla and are listed separately).
	/// </summary>
	public static class AkRegistrableKeyCatalog
	{
		public sealed class KeyEntry
		{
			public string Id { get; init; }
			public string AdvancedToken { get; init; }
			public string KeysAlias { get; init; }
			public bool IsModifier { get; init; }
			public bool IsOem { get; init; }
			public bool IsNumPad { get; init; }
			public bool IsLayoutSpecificJis { get; init; }
			public bool PreferRiskThreeKey { get; init; }
		}

		private static readonly KeyEntry[] AdvancedInternal = BuildAdvanced();
		private static readonly string[] PlainKeysInternal = BuildPlainKeysNames();
		private static readonly string[] PadDigitalInternal = BuildPadDigital();

		public static IReadOnlyList<KeyEntry> AdvancedKeys => AdvancedInternal;
		public static IReadOnlyList<string> PlainKeysNames => PlainKeysInternal;
		public static IReadOnlyList<string> PadDigitalTokens => PadDigitalInternal;

		public static int AdvancedCount => AdvancedInternal.Length;

		private static KeyEntry[] BuildAdvanced()
		{
			var list = new List<KeyEntry>(128);

			void Sc(string id, ushort scan, bool e0 = false, string keys = null,
				bool mod = false, bool oem = false, bool num = false, bool jis = false, bool risk = false)
			{
				list.Add(new KeyEntry {
					Id = id,
					AdvancedToken = PhysicalKeyToken.FormatScan(scan, e0),
					KeysAlias = keys,
					IsModifier = mod,
					IsOem = oem,
					IsNumPad = num,
					IsLayoutSpecificJis = jis,
					PreferRiskThreeKey = risk || mod || oem || num
				});
			}

			// Letters A–Z (Set-1)
			Sc("A", 0x1E, keys: "A"); Sc("B", 0x30, keys: "B"); Sc("C", 0x2E, keys: "C");
			Sc("D", 0x20, keys: "D"); Sc("E", 0x12, keys: "E"); Sc("F", 0x21, keys: "F");
			Sc("G", 0x22, keys: "G"); Sc("H", 0x23, keys: "H"); Sc("I", 0x17, keys: "I");
			Sc("J", 0x24, keys: "J"); Sc("K", 0x25, keys: "K"); Sc("L", 0x26, keys: "L");
			Sc("M", 0x32, keys: "M"); Sc("N", 0x31, keys: "N"); Sc("O", 0x18, keys: "O");
			Sc("P", 0x19, keys: "P"); Sc("Q", 0x10, keys: "Q"); Sc("R", 0x13, keys: "R");
			Sc("S", 0x1F, keys: "S"); Sc("T", 0x14, keys: "T"); Sc("U", 0x16, keys: "U");
			Sc("V", 0x2F, keys: "V"); Sc("W", 0x11, keys: "W"); Sc("X", 0x2D, keys: "X");
			Sc("Y", 0x15, keys: "Y"); Sc("Z", 0x2C, keys: "Z");

			// Number row
			Sc("D1", 0x02, keys: "D1"); Sc("D2", 0x03, keys: "D2"); Sc("D3", 0x04, keys: "D3");
			Sc("D4", 0x05, keys: "D4"); Sc("D5", 0x06, keys: "D5"); Sc("D6", 0x07, keys: "D6");
			Sc("D7", 0x08, keys: "D7"); Sc("D8", 0x09, keys: "D8"); Sc("D9", 0x0A, keys: "D9");
			Sc("D0", 0x0B, keys: "D0");

			// OEM / punctuation
			Sc("OemMinus", 0x0C, keys: "OemMinus", oem: true, risk: true);
			Sc("OemPlus", 0x0D, keys: "OemPlus", oem: true, risk: true);
			Sc("OemOpenBrackets", 0x1A, keys: "OemOpenBrackets", oem: true, risk: true);
			Sc("OemCloseBrackets", 0x1B, keys: "OemCloseBrackets", oem: true, risk: true);
			Sc("OemPipe", 0x2B, keys: "OemPipe", oem: true, risk: true);
			Sc("OemSemicolon", 0x27, keys: "OemSemicolon", oem: true, risk: true);
			Sc("OemQuotes", 0x28, keys: "OemQuotes", oem: true, risk: true);
			Sc("OemTilde", 0x29, keys: "OemTilde", oem: true, risk: true);
			Sc("OemComma", 0x33, keys: "OemComma", oem: true, risk: true);
			Sc("OemPeriod", 0x34, keys: "OemPeriod", oem: true, risk: true);
			Sc("OemQuestion", 0x35, keys: "OemQuestion", oem: true, risk: true);
			Sc("OemBackslash", 0x56, keys: "OemBackslash", oem: true, risk: true); // ISO 102

			// Editing / space / escape
			Sc("Space", 0x39, keys: "Space");
			Sc("Tab", 0x0F, keys: "Tab");
			Sc("Enter", 0x1C, keys: "Enter");
			Sc("Back", 0x0E, keys: "Back");
			Sc("Escape", 0x01, keys: "Escape", risk: true);

			// F-keys (F11 often UI-restricted at register — still in catalog; gate rejects)
			Sc("F1", 0x3B, keys: "F1"); Sc("F2", 0x3C, keys: "F2"); Sc("F3", 0x3D, keys: "F3");
			Sc("F4", 0x3E, keys: "F4"); Sc("F5", 0x3F, keys: "F5"); Sc("F6", 0x40, keys: "F6");
			Sc("F7", 0x41, keys: "F7"); Sc("F8", 0x42, keys: "F8"); Sc("F9", 0x43, keys: "F9");
			Sc("F10", 0x44, keys: "F10"); Sc("F12", 0x58, keys: "F12");

			// Modifiers (left/right)
			Sc("LeftControl", 0x1D, false, "LeftControl", mod: true, risk: true);
			Sc("RightControl", 0x1D, true, "RightControl", mod: true, risk: true);
			Sc("LeftShift", 0x2A, false, "LeftShift", mod: true, risk: true);
			Sc("RightShift", 0x36, false, "RightShift", mod: true, risk: true);
			Sc("LeftAlt", 0x38, false, "LeftAlt", mod: true, risk: true);
			Sc("RightAlt", 0x38, true, "RightAlt", mod: true, risk: true);

			// Cursor / nav (E0)
			Sc("Left", 0x4B, true, "Left", risk: true);
			Sc("Right", 0x4D, true, "Right", risk: true);
			Sc("Up", 0x48, true, "Up", risk: true);
			Sc("Down", 0x50, true, "Down", risk: true);
			Sc("Insert", 0x52, true, "Insert");
			Sc("Delete", 0x53, true, "Delete");
			Sc("Home", 0x47, true, "Home");
			Sc("End", 0x4F, true, "End");
			Sc("PageUp", 0x49, true, "PageUp");
			Sc("PageDown", 0x51, true, "PageDown");

			// Numpad
			Sc("NumPad0", 0x52, false, "NumPad0", num: true, risk: true);
			Sc("NumPad1", 0x4F, false, "NumPad1", num: true);
			Sc("NumPad2", 0x50, false, "NumPad2", num: true);
			Sc("NumPad3", 0x51, false, "NumPad3", num: true);
			Sc("NumPad4", 0x4B, false, "NumPad4", num: true);
			Sc("NumPad5", 0x4C, false, "NumPad5", num: true);
			Sc("NumPad6", 0x4D, false, "NumPad6", num: true);
			Sc("NumPad7", 0x47, false, "NumPad7", num: true);
			Sc("NumPad8", 0x48, false, "NumPad8", num: true);
			Sc("NumPad9", 0x49, false, "NumPad9", num: true);
			Sc("NumPadDivide", 0x35, true, "Divide", num: true, risk: true);
			Sc("NumPadMultiply", 0x37, false, "Multiply", num: true);
			Sc("NumPadSubtract", 0x4A, false, "Subtract", num: true);
			Sc("NumPadAdd", 0x4E, false, "Add", num: true);
			Sc("NumPadDecimal", 0x53, false, "Decimal", num: true);

			// Mouse (advanced singles)
			list.Add(new KeyEntry { Id = "Mouse1", AdvancedToken = "Mouse1", PreferRiskThreeKey = true });
			list.Add(new KeyEntry { Id = "Mouse2", AdvancedToken = "Mouse2", PreferRiskThreeKey = true });
			list.Add(new KeyEntry { Id = "Mouse3", AdvancedToken = "Mouse3", PreferRiskThreeKey = true });
			list.Add(new KeyEntry { Id = "Mouse4", AdvancedToken = "Mouse4", PreferRiskThreeKey = true });
			list.Add(new KeyEntry { Id = "Mouse5", AdvancedToken = "Mouse5", PreferRiskThreeKey = true });

			// JIS-specific (layout-gated in tests)
			Sc("SC_0x73_Ro", 0x73, false, null, jis: true, risk: true);
			list.Add(new KeyEntry {
				Id = "Kana",
				AdvancedToken = "Kana",
				KeysAlias = "Kana",
				IsLayoutSpecificJis = true,
				PreferRiskThreeKey = true
			});

			return list.ToArray();
		}

		private static string[] BuildPlainKeysNames()
		{
			// Vanilla-owned plain singles (AK skips fire) — still registerable as Keys names.
			var letters = new List<string>(26);
			for (char c = 'A'; c <= 'Z'; c++)
				letters.Add(c.ToString());
			return letters.ToArray();
		}

		private static string[] BuildPadDigital()
		{
			return new[] {
				GamePadToken.A, GamePadToken.B, GamePadToken.X, GamePadToken.Y,
				GamePadToken.Back, GamePadToken.Start,
				GamePadToken.LeftShoulder, GamePadToken.RightShoulder,
				GamePadToken.LeftTrigger, GamePadToken.RightTrigger,
				GamePadToken.LeftStick, GamePadToken.RightStick,
				GamePadToken.DPadUp, GamePadToken.DPadDown, GamePadToken.DPadLeft, GamePadToken.DPadRight,
				GamePadToken.LeftStickUp, GamePadToken.LeftStickDown,
				GamePadToken.LeftStickLeft, GamePadToken.LeftStickRight,
				GamePadToken.RightStickUp, GamePadToken.RightStickDown,
				GamePadToken.RightStickLeft, GamePadToken.RightStickRight
			};
		}
	}
}
