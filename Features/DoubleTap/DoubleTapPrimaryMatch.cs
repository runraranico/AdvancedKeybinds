using System;
using AdvancedKeybinds.Core;
using Microsoft.Xna.Framework.Input;

namespace AdvancedKeybinds.Features.DoubleTap
{
	/// <summary>
	/// Pure primaryToken ↔ ObservedEvent.KeyId matcher for DT.
	/// No OS / I/O / shared state / poll. Not Engine-wired.
	/// </summary>
	public static class DoubleTapPrimaryMatch
	{
		const int KeyIdBitMask = 0x1FFFF; // vk8 + scan8 + ext1

		const uint VkPause = 0x13;
		const uint VkCancel = 0x03;
		const uint VkCapital = 0x14;
		const uint VkKana = 0x15;
		const uint VkKanji = 0x19;
		const uint VkLWin = 0x5B;
		const uint VkRWin = 0x5C;
		const uint VkNumLock = 0x90;
		const uint VkScroll = 0x91;
		const uint VkOemCopy = 0xF2;
		const uint VkOemAuto = 0xF3;
		const uint VkOemEnlw = 0xF4;
		const uint VkOemMinus = 0xBD;
		const uint VkOemQuotes = 0xDE;
		const uint VkOemPipe = 0xDC;
		const uint VkOemBackslash = 0xE2;
		const uint VkConvert = 0x1C;
		const uint VkNonConvert = 0x1D;

		const ushort ScCapital = 0x3A;
		const ushort ScNumOrPause = 0x45;
		const ushort ScScroll = 0x46;
		const ushort ScIso102 = 0x56;
		const ushort ScHankaku = 0x29;
		const ushort ScHiragana = 0x70;
		const ushort ScRo = 0x73;
		const ushort ScYen = 0x7D;
		const ushort ScConvert = 0x79;
		const ushort ScNonConvert = 0x7B;
		const ushort ScJisMinus = 0x0C;
		const ushort ScJisEqual = 0x0D;

		/// <summary>
		/// Returns true when <paramref name="primaryToken"/> matches <paramref name="keyId"/>.
		/// <paramref name="japanesePhysical"/> gates JIS scan 0x0C/0x0D named aliases.
		/// <paramref name="japaneseLayout"/> with physical ORs for Hankaku Kanji/scan0x29 rules.
		/// </summary>
		public static bool Matches(
			string primaryToken,
			int keyId,
			bool japanesePhysical,
			bool japaneseLayout)
		{
			if (string.IsNullOrEmpty(primaryToken))
				return false;
			if (!TryUnpackKeyId(keyId, out uint vk, out ushort scan, out bool extended))
				return false;

			string token = PhysicalKeyToken.StripKeyAliasPrefix(primaryToken.Trim());
			if (token.Length == 0)
				return false;

			if (StableKeyToken.IsMouseToken(token)
				|| StableKeyToken.IsMouseWheelToken(token)
				|| GamePadToken.IsPadToken(token))
				return false;

			if (StableKeyToken.IsHankakuZenkakuToken(token))
				return IsHankakuEvent(vk, scan, japanesePhysical, japaneseLayout);

			if (IsKanaToken(token))
				return IsKanaEvent(vk, scan);

			if (PhysicalKeyToken.TryParseScan(token, out ushort tokenScan, out bool tokenExt))
				return MatchScanToken(tokenScan, tokenExt, vk, scan, extended);

			if (PhysicalKeyToken.TryParseVk(token, out uint tokenVk))
				return vk == tokenVk;

			return MatchNamed(token, vk, scan, japanesePhysical);
		}

		static bool TryUnpackKeyId(int keyId, out uint vk, out ushort scan, out bool extended)
		{
			vk = 0;
			scan = 0;
			extended = false;
			if (keyId < 0)
				return false;
			if ((keyId & ~KeyIdBitMask) != 0)
				return false;

			vk = (uint)(keyId & 0xFF);
			scan = (ushort)((keyId >> 8) & 0xFF);
			extended = (keyId & (1 << 16)) != 0;

			// Match the accepted source contract: VK must be valid even for SC tokens.
			if (vk < 1u || vk > 0xFEu)
				return false;

			return true;
		}

		static bool MatchScanToken(
			ushort tokenScan,
			bool tokenExt,
			uint eventVk,
			ushort eventScan,
			bool eventExt)
		{
			if (tokenScan != eventScan || tokenExt != eventExt)
				return false;

			// Pause/Break share make 0x45 with NumLock — never treat those VKs as SC:0x45.
			if (tokenScan == ScNumOrPause
				&& (eventVk == VkPause || eventVk == VkCancel))
				return false;

			return true;
		}

		static bool MatchNamed(string token, uint vk, ushort scan, bool japanesePhysical)
		{
			if (PhysicalKeyToken.IsKeyAliasPrefix(token))
			{
				token = PhysicalKeyToken.StripKeyAliasPrefix(token);
				if (string.IsNullOrEmpty(token))
					return false;
			}

			if (string.Equals(token, StableKeyToken.ScrollLockToken, StringComparison.OrdinalIgnoreCase))
				token = "Scroll";

			if (IsAllDigits(token) || token[0] == '+' || token[0] == '-' || token.IndexOf(',') >= 0)
				return false;

			if (!TryResolveNamedVk(token, out uint namedVk))
				return false;

			// ISO's shared VK must not bypass the physical-key distinction.
			if (namedVk == VkOemBackslash && scan == ScIso102)
				return false;

			if (vk == namedVk)
				return true;

			return MatchNamedJisScanAlias(token, namedVk, scan, japanesePhysical);
		}

		static bool TryResolveNamedVk(string token, out uint namedVk)
		{
			namedVk = 0;
			if (Enum.TryParse(token, ignoreCase: true, out Keys keys)
				&& keys != Keys.None
				&& Enum.IsDefined(typeof(Keys), keys))
			{
				namedVk = (uint)(int)keys;
				return namedVk >= 1u && namedVk <= 0xFEu;
			}

			return PhysicalKeyIdentity.TryGetVkForKeysName(token, out namedVk)
				&& namedVk >= 1u
				&& namedVk <= 0xFEu;
		}

		static bool MatchNamedJisScanAlias(
			string token,
			uint namedVk,
			ushort scan,
			bool japanesePhysical)
		{
			// Named/JIS compatibility: scan identity even when VK was remapped.
			if (namedVk == VkOemBackslash && scan == ScRo)
				return true;
			if (namedVk == VkOemPipe && scan == ScYen)
				return true;
			if (namedVk == VkConvert && scan == ScConvert)
				return true;
			if (namedVk == VkNonConvert && scan == ScNonConvert)
				return true;

			// ISO 102 must never alias to OemBackslash (ろ).
			if (namedVk == VkOemBackslash && scan == ScIso102)
				return false;

			// JIS - / ^ : physical keyboard only (not layout alone).
			if (japanesePhysical)
			{
				if (namedVk == VkOemMinus && scan == ScJisMinus)
					return true;
				if (namedVk == VkOemQuotes && scan == ScJisEqual)
					return true;
			}

			_ = token;
			return false;
		}

		static bool IsKanaToken(string token)
			=> string.Equals(token, "Kana", StringComparison.OrdinalIgnoreCase);

		static bool IsKanaEvent(uint vk, ushort scan)
		{
			if (scan == ScCapital)
				return false;
			return scan == ScHiragana
				|| vk == VkOemCopy
				|| vk == VkKana;
		}

		static bool IsHankakuEvent(
			uint vk,
			ushort scan,
			bool japanesePhysical,
			bool japaneseLayout)
		{
			if (vk == VkOemAuto || vk == VkOemEnlw)
				return true;

			bool jp = japanesePhysical || japaneseLayout;
			if (vk == VkKanji && jp)
				return true;

			if (scan != ScHankaku)
				return false;

			// Existing acquisition treats this physical position as Hankaku in JP context.
			return jp;
		}

		static bool IsAllDigits(string token)
		{
			if (token.Length == 0)
				return false;
			for (int i = 0; i < token.Length; i++)
			{
				if (!char.IsDigit(token[i]))
					return false;
			}

			return true;
		}
	}
}
