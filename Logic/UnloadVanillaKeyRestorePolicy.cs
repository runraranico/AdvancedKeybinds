using System;
using System.Collections.Generic;
using System.Globalization;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Unload-only: pick one vanilla-fireable KeyStatus token from a combo, without rewriting bindings.json.
	/// Selection prefers non-modifier Slot3 → Slot2 → Slot1, then any convertible slot in the same order.
	/// SC:/VK: → Keys/Mouse names are for KeyStatus restore only (not BindingStorage persistence).
	/// </summary>
	public static class UnloadVanillaKeyRestorePolicy
	{
		/// <summary>US Set-1 scan+E0 → XNA Keys name (unload KeyStatus only).</summary>
		private static readonly Dictionary<string, string> ScanE0ToKeysName =
			new(StringComparer.OrdinalIgnoreCase) {
				["01:0"] = "Escape",
				["02:0"] = "D1", ["03:0"] = "D2", ["04:0"] = "D3", ["05:0"] = "D4",
				["06:0"] = "D5", ["07:0"] = "D6", ["08:0"] = "D7", ["09:0"] = "D8",
				["0A:0"] = "D9", ["0B:0"] = "D0",
				["0C:0"] = "OemMinus", ["0D:0"] = "OemPlus",
				["0E:0"] = "Back", ["0F:0"] = "Tab",
				["10:0"] = "Q", ["11:0"] = "W", ["12:0"] = "E", ["13:0"] = "R",
				["14:0"] = "T", ["15:0"] = "Y", ["16:0"] = "U", ["17:0"] = "I",
				["18:0"] = "O", ["19:0"] = "P",
				["1A:0"] = "OemOpenBrackets", ["1B:0"] = "OemCloseBrackets",
				["1C:0"] = "Enter", ["1D:0"] = "LeftControl",
				["1E:0"] = "A", ["1F:0"] = "S", ["20:0"] = "D", ["21:0"] = "F",
				["22:0"] = "G", ["23:0"] = "H", ["24:0"] = "J", ["25:0"] = "K",
				["26:0"] = "L",
				["27:0"] = "OemSemicolon", ["28:0"] = "OemQuotes", ["29:0"] = "OemTilde",
				["2A:0"] = "LeftShift", ["2B:0"] = "OemPipe",
				["2C:0"] = "Z", ["2D:0"] = "X", ["2E:0"] = "C", ["2F:0"] = "V",
				["30:0"] = "B", ["31:0"] = "N", ["32:0"] = "M",
				["33:0"] = "OemComma", ["34:0"] = "OemPeriod", ["35:0"] = "OemQuestion",
				["36:0"] = "RightShift", ["37:0"] = "Multiply",
				["38:0"] = "LeftAlt", ["39:0"] = "Space", ["3A:0"] = "CapsLock",
				["3B:0"] = "F1", ["3C:0"] = "F2", ["3D:0"] = "F3", ["3E:0"] = "F4",
				["3F:0"] = "F5", ["40:0"] = "F6", ["41:0"] = "F7", ["42:0"] = "F8",
				["43:0"] = "F9", ["44:0"] = "F10",
				["45:0"] = "NumLock", ["46:0"] = "Scroll",
				["47:0"] = "NumPad7", ["48:0"] = "NumPad8", ["49:0"] = "NumPad9",
				["4A:0"] = "Subtract",
				["4B:0"] = "NumPad4", ["4C:0"] = "NumPad5", ["4D:0"] = "NumPad6",
				["4E:0"] = "Add",
				["4F:0"] = "NumPad1", ["50:0"] = "NumPad2", ["51:0"] = "NumPad3",
				["52:0"] = "NumPad0", ["53:0"] = "Decimal",
				["57:0"] = "F11", ["58:0"] = "F12",
				["73:0"] = "OemBackslash",
				["1D:1"] = "RightControl",
				["35:1"] = "Divide",
				["37:1"] = "PrintScreen",
				["38:1"] = "RightAlt",
				["47:1"] = "Home", ["48:1"] = "Up", ["49:1"] = "PageUp",
				["4B:1"] = "Left", ["4D:1"] = "Right",
				["4F:1"] = "End", ["50:1"] = "Down", ["51:1"] = "PageDown",
				["52:1"] = "Insert", ["53:1"] = "Delete",
				["5B:1"] = "LeftWindows", ["5C:1"] = "RightWindows", ["5D:1"] = "Apps",
			};

		/// <summary>Windows VK → Keys name (subset used by AK; unload KeyStatus only).</summary>
		private static readonly Dictionary<uint, string> VkToKeysName = new() {
			[0x08] = "Back", [0x09] = "Tab", [0x0D] = "Enter", [0x13] = "Pause",
			[0x14] = "CapsLock", [0x15] = "Kana", [0x19] = "Kanji", [0x1B] = "Escape",
			[0x1C] = "ImeConvert", [0x1D] = "ImeNoConvert", [0x20] = "Space",
			[0x21] = "PageUp", [0x22] = "PageDown", [0x23] = "End", [0x24] = "Home",
			[0x25] = "Left", [0x26] = "Up", [0x27] = "Right", [0x28] = "Down",
			[0x2C] = "PrintScreen", [0x2D] = "Insert", [0x2E] = "Delete",
			[0x30] = "D0", [0x31] = "D1", [0x32] = "D2", [0x33] = "D3", [0x34] = "D4",
			[0x35] = "D5", [0x36] = "D6", [0x37] = "D7", [0x38] = "D8", [0x39] = "D9",
			[0x41] = "A", [0x42] = "B", [0x43] = "C", [0x44] = "D", [0x45] = "E",
			[0x46] = "F", [0x47] = "G", [0x48] = "H", [0x49] = "I", [0x4A] = "J",
			[0x4B] = "K", [0x4C] = "L", [0x4D] = "M", [0x4E] = "N", [0x4F] = "O",
			[0x50] = "P", [0x51] = "Q", [0x52] = "R", [0x53] = "S", [0x54] = "T",
			[0x55] = "U", [0x56] = "V", [0x57] = "W", [0x58] = "X", [0x59] = "Y",
			[0x5A] = "Z",
			[0x5B] = "LeftWindows", [0x5C] = "RightWindows", [0x5D] = "Apps",
			[0x60] = "NumPad0", [0x61] = "NumPad1", [0x62] = "NumPad2", [0x63] = "NumPad3",
			[0x64] = "NumPad4", [0x65] = "NumPad5", [0x66] = "NumPad6", [0x67] = "NumPad7",
			[0x68] = "NumPad8", [0x69] = "NumPad9",
			[0x6A] = "Multiply", [0x6B] = "Add", [0x6C] = "Separator", [0x6D] = "Subtract",
			[0x6E] = "Decimal", [0x6F] = "Divide",
			[0x70] = "F1", [0x71] = "F2", [0x72] = "F3", [0x73] = "F4", [0x74] = "F5",
			[0x75] = "F6", [0x76] = "F7", [0x77] = "F8", [0x78] = "F9", [0x79] = "F10",
			[0x7A] = "F11", [0x7B] = "F12",
			[0x90] = "NumLock", [0x91] = "Scroll",
			[0xA0] = "LeftShift", [0xA1] = "RightShift",
			[0xA2] = "LeftControl", [0xA3] = "RightControl",
			[0xA4] = "LeftAlt", [0xA5] = "RightAlt",
			[0xBA] = "OemSemicolon", [0xBB] = "OemPlus", [0xBC] = "OemComma",
			[0xBD] = "OemMinus", [0xBE] = "OemPeriod", [0xBF] = "OemQuestion",
			[0xC0] = "OemTilde", [0xDB] = "OemOpenBrackets", [0xDC] = "OemPipe",
			[0xDD] = "OemCloseBrackets", [0xDE] = "OemQuotes", [0xDF] = "Oem8",
			[0xE2] = "OemBackslash",
		};

		/// <summary>
		/// Selects one vanilla KeyStatus token from slots. Returns false when none are convertible.
		/// </summary>
		public static bool TrySelectPrimaryVanillaKey(
			string slot1,
			string slot2,
			string slot3,
			out string vanillaKey)
		{
			vanillaKey = null;
			if (TryPick(slot1, slot2, slot3, preferNonModifier: true, out vanillaKey))
				return true;
			return TryPick(slot1, slot2, slot3, preferNonModifier: false, out vanillaKey);
		}

		/// <inheritdoc cref="TrySelectPrimaryVanillaKey(string,string,string,out string)"/>
		public static bool TrySelectPrimaryVanillaKey(ComboBindingData combo, out string vanillaKey)
		{
			vanillaKey = null;
			if (combo == null || combo.IsEmpty)
				return false;
			return TrySelectPrimaryVanillaKey(combo.Slot1, combo.Slot2, combo.Slot3, out vanillaKey);
		}

		/// <summary>
		/// Converts an AK save/capture token to a string vanilla PlayerInput can match.
		/// Does not rewrite BindingStorage. Returns false for unconvertible tokens.
		/// </summary>
		public static bool TryConvertToVanillaKeyStatusToken(string token, out string vanillaKey)
		{
			vanillaKey = null;
			if (string.IsNullOrWhiteSpace(token))
				return false;

			token = token.Trim();
			if (AdvancedVanillaOwnershipMarker.IsMarker(token))
			{
				token = token.Substring(AdvancedVanillaOwnershipMarker.Prefix.Length);
				if (string.IsNullOrWhiteSpace(token))
					return false;
			}

			if (PhysicalKeyToken.IsKeyAliasPrefix(token))
				token = PhysicalKeyToken.StripKeyAliasPrefix(token);

			if (StableKeyToken.IsDisplayAlias(token))
				return false;

			if (StableKeyToken.IsMouseButtonToken(token))
			{
				vanillaKey = StableKeyToken.CanonicalizeOrNull(token) ?? token;
				return true;
			}

			if (StableKeyToken.IsMouseWheelToken(token))
			{
				vanillaKey = StableKeyToken.CanonicalizeOrNull(token) ?? token;
				return true;
			}

			if (string.Equals(token, StableKeyToken.ScrollLockToken, StringComparison.OrdinalIgnoreCase))
			{
				vanillaKey = "Scroll";
				return true;
			}

			// HankakuZenkaku / other non-Keys specials: vanilla cannot fire them.
			if (StableKeyToken.IsHankakuZenkakuToken(token))
				return false;

			if (PhysicalKeyToken.TryParseScan(token, out ushort scan, out bool extended))
			{
				string key = FormatScanLookupKey(scan, extended);
				if (ScanE0ToKeysName.TryGetValue(key, out string fromScan)
					&& !string.IsNullOrEmpty(fromScan))
				{
					vanillaKey = fromScan;
					return true;
				}

				return false;
			}

			if (PhysicalKeyToken.TryParseVk(token, out uint vk))
			{
				if (VkToKeysName.TryGetValue(vk, out string fromVk) && !string.IsNullOrEmpty(fromVk))
				{
					vanillaKey = fromVk;
					return true;
				}

				return false;
			}

			if (StableKeyToken.TryParse(token, out bool isMouse, out _, out string canonical)
				&& !isMouse
				&& !string.IsNullOrEmpty(canonical)
				&& !string.Equals(canonical, "None", StringComparison.OrdinalIgnoreCase)
				&& !StableKeyToken.IsHankakuZenkakuToken(canonical)
				&& !StableKeyToken.IsMouseWheelToken(canonical))
			{
				vanillaKey = canonical;
				return true;
			}

			return false;
		}

		public static bool IsModifierVanillaOrBindingToken(string token)
		{
			if (string.IsNullOrEmpty(token))
				return false;

			if (StableKeyToken.IsModifierToken(token))
				return true;

			if (!TryConvertToVanillaKeyStatusToken(token, out string vanilla))
				return false;

			return StableKeyToken.IsModifierToken(vanilla);
		}

		private static bool TryPick(
			string slot1,
			string slot2,
			string slot3,
			bool preferNonModifier,
			out string vanillaKey)
		{
			vanillaKey = null;
			// Slot3 → Slot2 → Slot1
			if (TryAccept(slot3, preferNonModifier, out vanillaKey))
				return true;
			if (TryAccept(slot2, preferNonModifier, out vanillaKey))
				return true;
			return TryAccept(slot1, preferNonModifier, out vanillaKey);
		}

		private static bool TryAccept(string slotToken, bool preferNonModifier, out string vanillaKey)
		{
			vanillaKey = null;
			if (string.IsNullOrEmpty(slotToken))
				return false;
			if (!TryConvertToVanillaKeyStatusToken(slotToken, out string converted))
				return false;
			if (preferNonModifier && StableKeyToken.IsModifierToken(converted))
				return false;
			vanillaKey = converted;
			return true;
		}

		private static string FormatScanLookupKey(ushort scan, bool e0)
			=> (scan & 0xFF).ToString("X2", CultureInfo.InvariantCulture) + ":" + (e0 ? "1" : "0");
	}
}
