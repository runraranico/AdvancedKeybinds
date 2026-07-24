using System;
using System.Collections.Generic;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Resolves multi-token edges during single-slot capture (AltGr, OEM mixed with letters, …).
	/// Does not invent tokens when the situation is ambiguous — callers should log and wait/pick safely.
	/// </summary>
	public static class CaptureTokenResolver
	{
		public enum ResolveKind
		{
			/// <summary>Use <see cref="ResolveResult.Token"/> as the captured key.</summary>
			Resolved,
			/// <summary>Could not safely collapse; leave to caller policy / diagnostics.</summary>
			Ambiguous,
			/// <summary>Empty input.</summary>
			Empty
		}

		public readonly struct ResolveResult
		{
			public ResolveKind Kind { get; init; }
			public string Token { get; init; }
			public string Reason { get; init; }
		}

		/// <summary>
		/// Collapse a set of simultaneous newcomers from one physical press into at most one slot token.
		/// AltGr on Windows commonly appears as LeftControl+RightAlt — treat as RightAlt alone.
		/// </summary>
		public static ResolveResult ResolveNewcomers(IReadOnlyList<string> newcomers)
		{
			if (newcomers == null || newcomers.Count == 0)
				return new ResolveResult { Kind = ResolveKind.Empty, Reason = "empty" };

			var canonical = new List<string>(newcomers.Count);
			foreach (string raw in newcomers)
			{
				string c = StableKeyToken.CanonicalizeOrNull(raw);
				if (c == null)
					continue;
				if (!ContainsOrdinalIgnoreCase(canonical, c))
					canonical.Add(c);
			}

			if (canonical.Count == 0)
				return new ResolveResult { Kind = ResolveKind.Empty, Reason = "no-valid-tokens" };

			if (canonical.Count == 1)
			{
				return new ResolveResult {
					Kind = ResolveKind.Resolved,
					Token = canonical[0],
					Reason = "single"
				};
			}

			// AltGr: LeftControl + RightAlt (+ nothing else) → store RightAlt (one key).
			if (IsExactAltGrPair(canonical))
			{
				return new ResolveResult {
					Kind = ResolveKind.Resolved,
					Token = "RightAlt",
					Reason = "altgr-as-rightalt"
				};
			}

			// AltGr + printable: LeftControl + RightAlt + X → prefer X (the intended character key).
			if (TryStripAltGrPair(canonical, out List<string> withoutAltGr) && withoutAltGr.Count == 1)
			{
				return new ResolveResult {
					Kind = ResolveKind.Resolved,
					Token = withoutAltGr[0],
					Reason = "altgr-chord-prefer-nonmodifier"
				};
			}

			// CapsLock (scan 0x3A) + spurious Kana: never bind Caps as かな.
			if (TryPreferCapsLockOverKana(canonical, out string preferred))
			{
				return new ResolveResult {
					Kind = ResolveKind.Resolved,
					Token = preferred,
					Reason = "prefer-capslock-over-kana"
				};
			}

			// JIS ; (0x27 / OemPlus) vs : (0x28 / OemSemicolon): prefer scan / matching VK.
			if (TryPreferJisSemicolonColon(canonical, out preferred))
			{
				return new ResolveResult {
					Kind = ResolveKind.Resolved,
					Token = preferred,
					Reason = "prefer-jis-semicolon-colon"
				};
			}

			// Collapse only exact alias families for the same physical OEM key.
			// DEPRECATION CANDIDATE: MeasuredPhysicalKeyNormalizer now collapses Keys+VK+SC
			// before ResolveNewcomers for measured-scan normal keys. Keep these families as a
			// safety net until live JIS/foreign OEM validation confirms they are unused.
			if (TryCollapseSamePhysicalOemFamily(
					canonical,
					PhysicalKeyToken.FormatScan(0x35, extended: false),
					"OemQuestion",
					PhysicalKeyToken.FormatVk(0xBF),
					out preferred)
				|| TryCollapseSamePhysicalOemFamily(
					canonical,
					PhysicalKeyToken.FormatScan(0x2B, extended: false),
					"OemCloseBrackets",
					PhysicalKeyToken.FormatVk(0xDD),
					out preferred)
				// SC 0x1A: US "[" / JIS "@" — FNA may report OemOpenBrackets or OemTilde; pin by scan.
				|| TryCollapseSamePhysicalOemFamilyMulti(
					canonical,
					PhysicalKeyToken.FormatScan(0x1A, extended: false),
					out preferred,
					"OemOpenBrackets",
					"OemTilde",
					PhysicalKeyToken.FormatVk(0xDB),
					PhysicalKeyToken.FormatVk(0xC0))
				// US grave / tilde position (distinct from 0x1A when only OemTilde+VK without SC:0x1A).
				|| TryCollapseSamePhysicalOemFamily(
					canonical,
					PhysicalKeyToken.FormatScan(0x29, extended: false),
					"OemTilde",
					PhysicalKeyToken.FormatVk(0xC0),
					out preferred)
				// JIS ろ (R-Shift left): SC:0x73 only — never ISO SC:0x56 (same VK_OEM_102, different key).
				|| TryCollapseSamePhysicalOemFamily(
					canonical,
					PhysicalKeyToken.FormatScan(0x73, extended: false),
					"OemBackslash",
					PhysicalKeyToken.FormatVk(0xE2),
					out preferred))
			{
				return new ResolveResult {
					Kind = ResolveKind.Resolved,
					Token = preferred,
					Reason = "collapse-same-physical-oem-family"
				};
			}

			// Mixed OEM glyph + letter/function: prefer non-OEM (existing SlotCapture policy).
			if (TryPreferNonOem(canonical, out preferred))
			{
				return new ResolveResult {
					Kind = ResolveKind.Resolved,
					Token = preferred,
					Reason = "prefer-non-oem"
				};
			}

			// JIS physical -/^/¥ scan tokens vs DE-layout FNA brackets/ß: prefer scan-pinned OEM.
			if (TryPreferJisNumberRowOem(canonical, out preferred))
			{
				return new ResolveResult {
					Kind = ResolveKind.Resolved,
					Token = preferred,
					Reason = "prefer-jis-number-row-oem"
				};
			}

			// ISO102 / ABNT2 scan 0x56 vs OemBackslash (ろ) / VK_OEM_102: prefer SC identity.
			if (TryPreferIso102Scan(canonical, out preferred))
			{
				return new ResolveResult {
					Kind = ResolveKind.Resolved,
					Token = preferred,
					Reason = "prefer-iso102-scan"
				};
			}

			return new ResolveResult {
				Kind = ResolveKind.Ambiguous,
				Reason = "ambiguous=[" + string.Join(",", canonical) + "]"
			};
		}

		private static bool IsExactAltGrPair(List<string> tokens)
			=> tokens.Count == 2
				&& ContainsOrdinalIgnoreCase(tokens, "LeftControl")
				&& ContainsOrdinalIgnoreCase(tokens, "RightAlt");

		private static bool TryStripAltGrPair(List<string> tokens, out List<string> rest)
		{
			rest = null;
			if (!ContainsOrdinalIgnoreCase(tokens, "LeftControl")
				|| !ContainsOrdinalIgnoreCase(tokens, "RightAlt"))
				return false;

			rest = new List<string>(tokens.Count);
			foreach (string t in tokens)
			{
				if (string.Equals(t, "LeftControl", StringComparison.OrdinalIgnoreCase)
					|| string.Equals(t, "RightAlt", StringComparison.OrdinalIgnoreCase))
					continue;
				rest.Add(t);
			}

			return rest.Count > 0 && rest.Count < tokens.Count;
		}

		/// <summary>
		/// Caps scan 0x3A can arrive with a phantom Kana edge; CapsLock wins over Kana.
		/// </summary>
		private static bool TryPreferCapsLockOverKana(List<string> tokens, out string preferred)
		{
			preferred = null;
			bool hasCaps = ContainsOrdinalIgnoreCase(tokens, "CapsLock");
			bool hasKana = ContainsOrdinalIgnoreCase(tokens, "Kana");
			if (!hasCaps || !hasKana)
				return false;

			preferred = "CapsLock";
			return true;
		}

		/// <summary>
		/// FNA may keep OemSemicolon sticky after ':' while ';' reports OemPlus / SC:0x27 / VK:0xBB.
		/// Prefer the scan (or matching OEM name) that matches the physical row key.
		/// Also collapses SC+Oem+VK for the same ; or : key (avoids one-frame ambiguous reject).
		/// </summary>
		private static bool TryPreferJisSemicolonColon(List<string> tokens, out string preferred)
		{
			preferred = null;
			string scSemi = PhysicalKeyToken.FormatScan(0x27, extended: false);
			string scColon = PhysicalKeyToken.FormatScan(0x28, extended: false);
			string vkSemi = PhysicalKeyToken.FormatVk(0xBB);
			string vkColon = PhysicalKeyToken.FormatVk(0xBA);

			bool hasSemiEvidence = ContainsOrdinalIgnoreCase(tokens, scSemi)
				|| ContainsOrdinalIgnoreCase(tokens, "OemPlus")
				|| ContainsOrdinalIgnoreCase(tokens, vkSemi);
			bool hasColonEvidence = ContainsOrdinalIgnoreCase(tokens, scColon)
				|| ContainsOrdinalIgnoreCase(tokens, "OemSemicolon")
				|| ContainsOrdinalIgnoreCase(tokens, vkColon);

			if (hasSemiEvidence && hasColonEvidence)
			{
				// Conflict: scan wins, then VK/OEM name for that scan's family.
				if (ContainsOrdinalIgnoreCase(tokens, scSemi))
				{
					preferred = scSemi;
					return true;
				}

				if (ContainsOrdinalIgnoreCase(tokens, scColon))
				{
					preferred = scColon;
					return true;
				}

				if (ContainsOrdinalIgnoreCase(tokens, vkSemi) || ContainsOrdinalIgnoreCase(tokens, "OemPlus"))
				{
					preferred = ContainsOrdinalIgnoreCase(tokens, "OemPlus") ? "OemPlus" : vkSemi;
					return true;
				}

				preferred = ContainsOrdinalIgnoreCase(tokens, "OemSemicolon") ? "OemSemicolon" : vkColon;
				return true;
			}

			if (TryCollapseSamePhysicalOemFamily(tokens, scSemi, "OemPlus", vkSemi, out preferred))
				return true;
			if (TryCollapseSamePhysicalOemFamily(tokens, scColon, "OemSemicolon", vkColon, out preferred))
				return true;

			return false;
		}

		/// <summary>
		/// When newcomers are only aliases of one physical OEM key (SC + Keys name + VK), pick one.
		/// Does not collapse across different physical keys.
		/// </summary>
		private static bool TryCollapseSamePhysicalOemFamily(
			List<string> tokens,
			string scanToken,
			string oemName,
			string vkToken,
			out string preferred)
			=> TryCollapseSamePhysicalOemFamilyMulti(tokens, scanToken, out preferred, oemName, vkToken);

		/// <summary>
		/// Same as <see cref="TryCollapseSamePhysicalOemFamily"/> but allows multiple OEM/VK aliases
		/// for one scan (e.g. FNA reporting OemTilde vs OemOpenBrackets for SC:0x1A).
		/// </summary>
		private static bool TryCollapseSamePhysicalOemFamilyMulti(
			List<string> tokens,
			string scanToken,
			out string preferred,
			params string[] aliases)
		{
			preferred = null;
			if (tokens == null || tokens.Count < 2 || aliases == null || aliases.Length == 0)
				return false;

			bool hasScan = false;
			string matchedOemOrVk = null;
			int aliasHits = 0;
			foreach (string c in tokens)
			{
				if (string.Equals(c, scanToken, StringComparison.OrdinalIgnoreCase))
				{
					hasScan = true;
					continue;
				}

				bool matchedAlias = false;
				foreach (string alias in aliases)
				{
					if (!string.Equals(c, alias, StringComparison.OrdinalIgnoreCase))
						continue;
					matchedAlias = true;
					aliasHits++;
					matchedOemOrVk ??= c;
					break;
				}

				if (!matchedAlias)
					return false;
			}

			int hits = (hasScan ? 1 : 0) + aliasHits;
			if (hits < 2)
				return false;

			preferred = hasScan ? scanToken : matchedOemOrVk;
			return preferred != null;
		}

		private static bool TryPreferNonOem(List<string> tokens, out string preferred)
		{
			preferred = null;
			string oem = null;
			string other = null;
			foreach (string c in tokens)
			{
				if (StableKeyToken.IsOemToken(c))
				{
					oem ??= c;
					continue;
				}

				other ??= c;
			}

			if (other != null && oem != null)
			{
				preferred = other;
				return true;
			}

			return false;
		}

		/// <summary>
		/// When FNA reports layout-remapped OEM together with JIS scan-pinned -/^/¥ tokens, keep the latter.
		/// </summary>
		private static bool TryPreferJisNumberRowOem(List<string> tokens, out string preferred)
		{
			preferred = null;
			foreach (string candidate in new[] { "OemMinus", "OemQuotes", "OemPipe" })
			{
				if (!ContainsOrdinalIgnoreCase(tokens, candidate))
					continue;

				bool hasOtherOem = false;
				foreach (string c in tokens)
				{
					if (string.Equals(c, candidate, StringComparison.OrdinalIgnoreCase))
						continue;
					if (StableKeyToken.IsOemToken(c))
					{
						hasOtherOem = true;
						break;
					}
				}

				if (hasOtherOem)
				{
					preferred = candidate;
					return true;
				}
			}

			return false;
		}

		/// <summary>
		/// When scan 0x56 (ISO102/ABNT2) arrives together with OemBackslash or VK_OEM_102, keep SC:0x56:Normal.
		/// </summary>
		private static bool TryPreferIso102Scan(List<string> tokens, out string preferred)
		{
			preferred = null;
			string isoScan = PhysicalKeyToken.FormatScan(0x56, extended: false);
			if (!ContainsOrdinalIgnoreCase(tokens, isoScan))
				return false;

			bool hasCollision = false;
			foreach (string c in tokens)
			{
				if (string.Equals(c, isoScan, StringComparison.OrdinalIgnoreCase))
					continue;
				if (string.Equals(c, "OemBackslash", StringComparison.OrdinalIgnoreCase)
					|| string.Equals(c, PhysicalKeyToken.FormatVk(0xE2), StringComparison.OrdinalIgnoreCase))
				{
					hasCollision = true;
					break;
				}
			}

			if (!hasCollision)
				return false;

			preferred = isoScan;
			return true;
		}

		private static bool ContainsOrdinalIgnoreCase(List<string> list, string value)
		{
			foreach (string item in list)
			{
				if (string.Equals(item, value, StringComparison.OrdinalIgnoreCase))
					return true;
			}

			return false;
		}
	}
}
