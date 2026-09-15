using System;
using System.Collections.Generic;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Normal keyboard keys: one physical press → one measured <c>SC:</c> token (Scan + Extended).
	/// Special keys keep dedicated tokens (modifiers, Win, Pause, IME, …).
	/// Does not invent scan codes via MapVirtualKey for new registrations.
	/// </summary>
	public static class MeasuredPhysicalKeyNormalizer
	{
		public const string PolicyVersion = "sc-measured-v3-event-sequence";

		/// <summary>
		/// Keys that must NOT be reduced to a plain SC: registration token.
		/// </summary>
		public static bool IsSpecialHandledVirtualKey(uint vk)
		{
			return vk switch {
				0x10 or 0xA0 or 0xA1 => true, // Shift / L / R
				0x11 or 0xA2 or 0xA3 => true, // Ctrl
				0x12 or 0xA4 or 0xA5 => true, // Alt
				0x5B or 0x5C => true, // Win
				0x5D => true, // Apps
				0x13 or 0x03 => true, // Pause / Break(Cancel)
				0x2C => true, // PrintScreen / Snapshot
				0x14 or 0x90 or 0x91 => true, // Caps / Num / Scroll
				0x15 or 0x19 => true, // Kana / Kanji
				0x1C or 0x1D => true, // Convert / NonConvert (also overlap risks — keep special)
				0xF2 or 0xF3 or 0xF4 => true, // OEM_COPY / hankaku-ish NLS
				0x1B => true, // Escape (shell / Ctrl+Esc)
				_ => false
			};
		}

		public static bool IsSpecialHandledToken(string token)
		{
			if (string.IsNullOrEmpty(token))
				return false;

			if (StableKeyToken.IsHankakuZenkakuToken(token)
				|| string.Equals(token, StableKeyToken.ScrollLockToken, StringComparison.OrdinalIgnoreCase)
				|| StableKeyToken.IsMouseToken(token)
				|| GamePadToken.IsPadToken(token))
				return true;

			if (string.Equals(token, "LeftWindows", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(token, "RightWindows", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(token, "Apps", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(token, "Pause", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(token, "PrintScreen", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(token, "CapsLock", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(token, "NumLock", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(token, "Kana", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(token, "Kanji", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(token, "ImeConvert", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(token, "ImeNoConvert", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(token, "LeftShift", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(token, "RightShift", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(token, "LeftControl", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(token, "RightControl", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(token, "LeftAlt", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(token, "RightAlt", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(token, "Escape", StringComparison.OrdinalIgnoreCase))
				return true;

			if (PhysicalKeyToken.TryParseVk(token, out uint vk))
				return IsSpecialHandledVirtualKey(vk);

			return false;
		}

		/// <summary>
		/// Choose the single capture/gameplay token for a measured WM_KEY* event.
		/// </summary>
		public static string ChooseMeasuredCaptureToken(
			uint vk,
			ushort measuredScan,
			bool measuredExtended,
			string resolverFallbackToken)
		{
			measuredScan = (ushort)(measuredScan & 0xFF);

			if (measuredScan == 0 || IsSpecialHandledVirtualKey(vk))
				return string.IsNullOrEmpty(resolverFallbackToken)
					? (PhysicalKeyToken.IsValidVirtualKey(vk) ? PhysicalKeyToken.FormatVk(vk) : null)
					: resolverFallbackToken;

			return PhysicalKeyToken.FormatScan(measuredScan, measuredExtended);
		}

		/// <summary>
		/// Collapse Keys + VK: + SC: aliases. Prefer EventSequence tagging when available.
		/// Untagged path: PreferSingleMeasuredScan, then AreSamePhysical only as last resort
		/// (legacy / no EventSequence — not the primary same-press test for new registration).
		/// </summary>
		public static List<string> CollapseNewcomers(IReadOnlyList<string> newcomers)
		{
			var result = new List<string>();
			if (newcomers == null || newcomers.Count == 0)
				return result;

			var canonical = new List<string>(newcomers.Count);
			foreach (string raw in newcomers)
			{
				string c = StableKeyToken.CanonicalizeOrNull(raw) ?? raw?.Trim();
				if (string.IsNullOrEmpty(c))
					continue;
				if (!ContainsIgnoreCase(canonical, c))
					canonical.Add(c);
			}

			if (canonical.Count <= 1)
			{
				result.AddRange(canonical);
				return result;
			}

			// Fast path: one SC: + only non-special aliases → SC only (layout-table mismatch safe).
			List<string> prefer = SameEventAliasPolicy.PreferSingleMeasuredScanOverAliases(canonical);
			if (prefer.Count == 1)
				return prefer;

			// Last resort for untagged tokens (old saves / no EventSequence): AreSamePhysical groups.
			var used = new bool[canonical.Count];
			for (int i = 0; i < canonical.Count; i++)
			{
				if (used[i])
					continue;

				var group = new List<int> { i };
				used[i] = true;
				for (int j = i + 1; j < canonical.Count; j++)
				{
					if (used[j])
						continue;
					if (!PhysicalKeyIdentity.AreSamePhysical(canonical[i], canonical[j]))
						continue;
					group.Add(j);
					used[j] = true;
				}

				string pick = PickCanonicalFromGroup(canonical, group);
				if (pick != null && !ContainsIgnoreCase(result, pick))
					result.Add(pick);
			}

			return SameEventAliasPolicy.PreferSingleMeasuredScanOverAliases(result);
		}

		/// <summary>EventSequence-first collapse (preferred for live WM_KEY* registration).</summary>
		public static List<string> CollapseSequencedNewcomers(IReadOnlyList<SequencedNewcomer> newcomers)
			=> EventSequenceUnifier.CollapseSequencedNewcomers(newcomers);

		private static string PickCanonicalFromGroup(List<string> tokens, List<int> indices)
		{
			string sc = null;
			string special = null;
			string other = null;
			foreach (int idx in indices)
			{
				string t = tokens[idx];
				if (PhysicalKeyToken.TryParseScan(t, out _, out _))
				{
					sc ??= t;
					continue;
				}

				if (IsSpecialHandledToken(t))
				{
					special ??= t;
					continue;
				}

				other ??= t;
			}

			// Measured SC wins for normal keys.
			if (sc != null && special == null)
				return sc;

			// Special keys keep dedicated token even if an SC alias slipped in.
			if (special != null)
				return special;

			return sc ?? other;
		}

		private static bool ContainsIgnoreCase(List<string> list, string value)
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
