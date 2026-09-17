using System;
using System.Collections.Generic;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Pure display-join helpers shared by production <c>BindingDisplayResolver</c> and Core.Tests.
	/// Does not touch KeyStatus / ownership / input.
	/// </summary>
	public static class BindingDisplayText
	{
		/// <summary>
		/// Returns a local display copy without reserved ownership prefixes.
		/// Callers must never persist the returned value back to KeyStatus or ownership state.
		/// </summary>
		public static string UnwrapOwnershipMarkerForDisplay(string token)
		{
			if (string.IsNullOrWhiteSpace(token))
				return string.Empty;

			string displayToken = token.Trim();
			while (AdvancedVanillaOwnershipMarker.IsMarker(displayToken))
				displayToken = displayToken.Substring(AdvancedVanillaOwnershipMarker.Prefix.Length);

			return displayToken;
		}

		/// <summary>True when any assigned KeyStatus entry is an AKAdv ownership marker.</summary>
		public static bool NeedsOverrideFromAssignedKeys(IReadOnlyList<string> assignedKeys)
		{
			if (assignedKeys == null || assignedKeys.Count == 0)
				return false;

			for (int i = 0; i < assignedKeys.Count; i++)
			{
				if (AdvancedVanillaOwnershipMarker.IsMarker(assignedKeys[i]))
					return true;
			}

			return false;
		}

		/// <summary>
		/// Display-only: replace embedded <c>AKAdv:…</c> tokens in tooltip / UI strings.
		/// Does not mutate KeyStatus. <paramref name="resolveDisplayName"/> must be display-safe.
		/// <para>
		/// Many mods (Calamity accessories, etc.) join <c>GetAssignedKeys()</c> with
		/// <c>" / "</c>. Multi-slot AND ownership is multiple markers — collapse those runs to
		/// Controls-style <c>+</c> (A+S+D), not slash (which reads as OR).
		/// Plain <c>Escape / Tab</c> without markers is left unchanged.
		/// </para>
		/// </summary>
		public static string ReplaceOwnershipMarkersInDisplayText(
			string text,
			Func<string, string> resolveDisplayName)
		{
			if (string.IsNullOrEmpty(text) || resolveDisplayName == null)
				return text ?? string.Empty;

			const string prefix = AdvancedVanillaOwnershipMarker.Prefix;
			int searchFrom = 0;
			System.Text.StringBuilder sb = null;
			var runDisplays = new List<string>(3);

			while (true)
			{
				int at = text.IndexOf(prefix, searchFrom, StringComparison.Ordinal);
				if (at < 0)
				{
					if (sb == null)
						return text;
					sb.Append(text, searchFrom, text.Length - searchFrom);
					return sb.ToString();
				}

				sb ??= new System.Text.StringBuilder(text.Length);
				sb.Append(text, searchFrom, at - searchFrom);

				runDisplays.Clear();
				int cursor = at;
				while (TryReadOwnershipMarker(text, cursor, out int markerEnd, out string marker))
				{
					string display = resolveDisplayName(marker);
					if (string.IsNullOrEmpty(display))
						display = UnwrapOwnershipMarkerForDisplay(marker);
					runDisplays.Add(display);
					cursor = markerEnd;

					if (!TrySkipAssignedKeysSlashSeparator(text, ref cursor))
						break;
					if (cursor >= text.Length
						|| !text.AsSpan(cursor).StartsWith(prefix, StringComparison.Ordinal))
						break;
				}

				if (runDisplays.Count == 0)
				{
					sb.Append(prefix);
					searchFrom = at + prefix.Length;
					continue;
				}

				sb.Append(string.Join("+", runDisplays));
				searchFrom = cursor;
			}
		}

		/// <summary>
		/// Join KeyStatus / GetAssignedKeys entries for display. Ownership markers use
		/// Controls AND separator <c>+</c> (not slash).
		/// </summary>
		public static string JoinAssignedKeyDisplays(
			IReadOnlyList<string> assignedKeys,
			Func<string, string> resolveDisplayName)
		{
			if (assignedKeys == null || assignedKeys.Count == 0 || resolveDisplayName == null)
				return string.Empty;

			var parts = new List<string>(assignedKeys.Count);
			for (int i = 0; i < assignedKeys.Count; i++)
			{
				string token = assignedKeys[i];
				if (string.IsNullOrEmpty(token))
					continue;
				string display = resolveDisplayName(token);
				if (string.IsNullOrEmpty(display))
					display = UnwrapOwnershipMarkerForDisplay(token);
				if (!string.IsNullOrEmpty(display))
					parts.Add(display);
			}

			return parts.Count == 0 ? string.Empty : string.Join("+", parts);
		}

		private static bool TryReadOwnershipMarker(
			string text,
			int start,
			out int endExclusive,
			out string marker)
		{
			marker = null;
			endExclusive = start;
			const string prefix = AdvancedVanillaOwnershipMarker.Prefix;
			if (start < 0 || start >= text.Length)
				return false;
			if (!text.AsSpan(start).StartsWith(prefix, StringComparison.Ordinal))
				return false;

			int tokenEnd = start + prefix.Length;
			while (tokenEnd < text.Length && IsOwnershipMarkerBodyChar(text[tokenEnd]))
				tokenEnd++;

			if (tokenEnd <= start + prefix.Length)
				return false;

			marker = text.Substring(start, tokenEnd - start);
			endExclusive = tokenEnd;
			return true;
		}

		/// <summary>
		/// Skip optional whitespace + '/' + optional whitespace between joined GetAssignedKeys.
		/// </summary>
		private static bool TrySkipAssignedKeysSlashSeparator(string text, ref int index)
		{
			int i = index;
			while (i < text.Length && (text[i] == ' ' || text[i] == '\t'))
				i++;
			if (i >= text.Length || text[i] != '/')
				return false;
			i++;
			while (i < text.Length && (text[i] == ' ' || text[i] == '\t'))
				i++;
			index = i;
			return true;
		}

		private static bool IsOwnershipMarkerBodyChar(char c)
		{
			if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9')
				return true;
			return c is ':' or '_' or '.' or '-' or '+';
		}

		/// <summary>
		/// Same join rules as <c>ComboBinding.FormatJoinedLabel(compact: true)</c>:
		/// Slot1→3 order, skip empty, <c>+</c> separator. Display via <paramref name="formatDisplay"/>.
		/// </summary>
		public static string FormatJoinedCompact(
			string slot1,
			string slot2,
			string slot3,
			string slot1Display,
			string slot2Display,
			string slot3Display,
			Func<string, string, string> formatDisplay)
		{
			if (formatDisplay == null)
				throw new ArgumentNullException(nameof(formatDisplay));

			var parts = new List<string>(3);
			AppendSlot(parts, slot1, slot1Display, formatDisplay);
			AppendSlot(parts, slot2, slot2Display, formatDisplay);
			AppendSlot(parts, slot3, slot3Display, formatDisplay);
			if (parts.Count == 0)
				return string.Empty;
			return string.Join("+", parts);
		}

		/// <summary>HOLD single-slot prefix used by Controls / ComboPanel (caller supplies localized strings).</summary>
		public static string ApplyHoldPrefix(
			string joined,
			bool hold,
			bool holdOnRelease,
			int filledSlotCount,
			string whileHeldPrefix,
			string onReleasePrefix)
		{
			if (string.IsNullOrEmpty(joined))
				return joined ?? string.Empty;

			if (!hold || filledSlotCount != 1)
				return joined;

			string prefix = holdOnRelease ? onReleasePrefix : whileHeldPrefix;
			if (string.IsNullOrEmpty(prefix))
				return joined;
			return prefix + joined;
		}

		public static int CountFilledSlots(string slot1, string slot2, string slot3)
		{
			int n = 0;
			if (!string.IsNullOrEmpty(slot1)) n++;
			if (!string.IsNullOrEmpty(slot2)) n++;
			if (!string.IsNullOrEmpty(slot3)) n++;
			return n;
		}

		private static void AppendSlot(
			List<string> parts,
			string token,
			string slotDisplay,
			Func<string, string, string> formatDisplay)
		{
			if (string.IsNullOrEmpty(token))
				return;
			string label = formatDisplay(token, slotDisplay);
			if (!string.IsNullOrEmpty(label))
				parts.Add(label);
		}
	}
}
