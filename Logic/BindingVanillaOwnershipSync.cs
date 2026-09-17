using System;
using System.Collections.Generic;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Single source of truth for how BindingStorage ownership maps to vanilla KeyStatus entries.
	/// Used by production <c>BindingStorage</c> and by <see cref="FakeBindingSession"/> tests.
	/// <list type="bullet">
	/// <item>Empty / cleared → KeyStatus empty (user unset; do not restore defaults here)</item>
	/// <item>Plain single tap → raw Keys name</item>
	/// <item>Advanced → <see cref="AdvancedVanillaOwnershipMarker"/> entries</item>
	/// </list>
	/// </summary>
	public static class BindingVanillaOwnershipSync
	{
		public enum OwnershipKind
		{
			Empty,
			Plain,
			Advanced
		}

		public static OwnershipKind Classify(bool hold, IReadOnlyList<string> keys, bool isEmpty)
		{
			if (isEmpty || keys == null || keys.Count == 0)
				return OwnershipKind.Empty;

			if (PlainSingleTapPolicy.IsPlainSingleTap(hold, keys))
				return OwnershipKind.Plain;

			return OwnershipKind.Advanced;
		}

		public static OwnershipKind Classify(ComboBindingData combo)
		{
			if (combo == null || combo.IsEmpty)
				return OwnershipKind.Empty;
			return Classify(combo.Hold, combo.Keys, combo.IsEmpty);
		}

		/// <summary>
		/// KeyStatus entries to write. Empty list means clear / unbound (not default restore).
		/// </summary>
		public static List<string> BuildKeyStatusEntries(bool hold, IReadOnlyList<string> keys, bool isEmpty)
		{
			OwnershipKind kind = Classify(hold, keys, isEmpty);
			switch (kind)
			{
				case OwnershipKind.Plain:
					return new List<string> { keys[0] };
				case OwnershipKind.Advanced:
					return AdvancedVanillaOwnershipMarker.BuildMarkers(keys);
				default:
					return new List<string>();
			}
		}

		public static List<string> BuildKeyStatusEntries(ComboBindingData combo)
		{
			if (combo == null || combo.IsEmpty)
				return new List<string>();
			return BuildKeyStatusEntries(combo.Hold, combo.Keys, combo.IsEmpty);
		}

		/// <summary>
		/// KeyStatus for a stored combo including Inventory/MenuInquiry AdditionalOr vanilla OR lists
		/// (Escape, Tab). Action id is required so AdditionalOr is not dropped to markers / Escape-only.
		/// </summary>
		public static List<string> BuildKeyStatusEntries(string storageOrTriggerId, ComboBindingData combo)
		{
			if (combo == null || combo.IsEmpty)
				return new List<string>();

			KeyboardAdditionalOrPolicy.NormalizeSharedUiBackAdditionalOr(storageOrTriggerId, combo);

			if (KeyboardMousePadIsolationPolicy.TryGetDefaultVanillaMouseToken(
				    storageOrTriggerId,
				    combo.Hold,
				    combo.Slot1,
				    combo.Slot2,
				    combo.Slot3,
				    combo.AlternateGamePadToken,
				    out string defaultMouseToken))
				return new List<string> { defaultMouseToken };

			var orScratch = new List<string>(2);
			int inputModeOrdinal = ResolveBuildKeyStatusInputModeOrdinal(storageOrTriggerId);
			if (KeyboardAdditionalOrPolicy.TryBuildVanillaOrKeyStatusEntries(
				    storageOrTriggerId, combo.Hold, combo.Keys, combo.AdditionalOrToken, orScratch,
				    inputModeOrdinal))
				return new List<string>(orScratch);

			return BuildKeyStatusEntries(combo.Hold, combo.Keys, combo.IsEmpty);
		}

		/// <summary>
		/// True when KeyStatus already matches what <see cref="BuildKeyStatusEntries(string, ComboBindingData)"/>
		/// would write (includes AdditionalOr vanilla OR).
		/// </summary>
		public static bool MatchesExpectedKeyStatus(
			IReadOnlyList<string> current,
			string storageOrTriggerId,
			ComboBindingData combo)
		{
			if (combo == null || combo.IsEmpty)
				return MatchesOwnershipFromSlots(current, false, null, null, null);

			List<string> expected = BuildKeyStatusEntries(storageOrTriggerId, combo);
			return MatchesExpectedKeyStatus(current, expected);
		}

		/// <summary>
		/// Zero-alloc ownership compare against slot fields (no <c>Keys</c> list, no marker List).
		/// Does not call <c>StableKeyToken.TryParse</c> / <c>IsMouseToken</c> (those allocate).
		/// </summary>
		public static bool MatchesOwnershipFromSlots(
			IReadOnlyList<string> current,
			bool hold,
			string slot1,
			string slot2,
			string slot3)
		{
			bool empty1 = string.IsNullOrEmpty(slot1);
			bool empty2 = string.IsNullOrEmpty(slot2);
			bool empty3 = string.IsNullOrEmpty(slot3);
			if (empty1 && empty2 && empty3)
				return !AdvancedVanillaOwnershipMarker.IsEffectivelyBound(current);

			int filled = 0;
			string sole = null;
			if (!empty1) { filled++; sole = slot1; }
			if (!empty2) { filled++; sole = slot2; }
			if (!empty3) { filled++; sole = slot3; }

			// Plain = sole Keys-name tap (VK/SC Escape equivalents stay Advanced — not remapped).
			if (filled == 1 && IsPlainOwnershipSole(hold, sole))
				return AdvancedVanillaOwnershipMarker.IsSoleRawToken(current, sole);

			if (filled == 0)
				return MatchesOwnedFallbackOnly(current);

			if (current == null || current.Count != filled)
				return false;

			int i = 0;
			if (!empty1)
			{
				if (!AdvancedVanillaOwnershipMarker.MarkerMatchesBindingToken(current[i], slot1))
					return false;
				i++;
			}

			if (!empty2)
			{
				if (!AdvancedVanillaOwnershipMarker.MarkerMatchesBindingToken(current[i], slot2))
					return false;
				i++;
			}

			if (!empty3)
			{
				if (!AdvancedVanillaOwnershipMarker.MarkerMatchesBindingToken(current[i], slot3))
					return false;
			}

			return true;
		}

		/// <summary>
		/// Same classification as <see cref="PlainSingleTapPolicy.IsPlainSingleTapSole"/> but without
		/// <c>StableKeyToken.IsMouseToken</c>/<c>TryParse</c> (those allocate canonical strings).
		/// </summary>
		private static bool IsPlainOwnershipSole(bool hold, string key)
		{
			if (hold || string.IsNullOrEmpty(key))
				return false;

			if (IsMouseTokenFastNoAlloc(key))
				return false;

			if (PlainSingleTapPolicy.IsHookManagedTapToken(key))
				return false;

			if (PlainSingleTapPolicy.IsNonVanillaConfigurationToken(key))
				return false;

			return true;
		}

		/// <summary>Mouse1–5 / MouseWheel Up|Down|Left|Right via fixed compares only (no TryParse).</summary>
		private static bool IsMouseTokenFastNoAlloc(string token)
		{
			if (string.Equals(token, StableKeyToken.MouseWheelUpToken, StringComparison.OrdinalIgnoreCase)
				|| string.Equals(token, StableKeyToken.MouseWheelDownToken, StringComparison.OrdinalIgnoreCase)
				|| string.Equals(token, StableKeyToken.MouseWheelLeftToken, StringComparison.OrdinalIgnoreCase)
				|| string.Equals(token, StableKeyToken.MouseWheelRightToken, StringComparison.OrdinalIgnoreCase))
				return true;

			// "Mouse1".."Mouse5"
			if (token.Length != 6)
				return false;
			if (!token.StartsWith("Mouse", StringComparison.OrdinalIgnoreCase))
				return false;
			char d = token[5];
			return d >= '1' && d <= '5';
		}

		private static bool MatchesOwnedFallbackOnly(IReadOnlyList<string> current)
		{
			return current != null
				&& current.Count == 1
				&& string.Equals(
					current[0],
					AdvancedVanillaOwnershipMarker.OwnedFallback,
					StringComparison.OrdinalIgnoreCase);
		}

		/// <summary>
		/// True when KeyStatus already matches what <see cref="BuildKeyStatusEntries"/> would write.
		/// Empty expected means unbound (missing / empty / None-only lists all count as matching).
		/// Prefer <see cref="MatchesOwnershipFromSlots"/> on hot paths (no List alloc).
		/// </summary>
		public static bool MatchesExpectedKeyStatus(
			IReadOnlyList<string> current,
			bool hold,
			IReadOnlyList<string> keys,
			bool isEmpty)
		{
			if (isEmpty || keys == null || keys.Count == 0)
				return MatchesOwnershipFromSlots(current, hold, null, null, null);

			string s1 = null, s2 = null, s3 = null;
			int n = 0;
			for (int i = 0; i < keys.Count; i++)
			{
				string k = keys[i];
				if (string.IsNullOrEmpty(k))
					continue;
				if (n == 0) s1 = k;
				else if (n == 1) s2 = k;
				else if (n == 2) { s3 = k; n++; break; }
				n++;
			}

			return MatchesOwnershipFromSlots(current, hold, s1, s2, s3);
		}

		public static bool MatchesExpectedKeyStatus(IReadOnlyList<string> current, ComboBindingData combo)
		{
			if (combo == null || combo.IsEmpty)
				return MatchesOwnershipFromSlots(current, false, null, null, null);
			return MatchesOwnershipFromSlots(current, combo.Hold, combo.Slot1, combo.Slot2, combo.Slot3);
		}

		/// <summary>Direct list compare (empty expected ≡ effectively unbound current).</summary>
		public static bool MatchesExpectedKeyStatus(IReadOnlyList<string> current, IReadOnlyList<string> expected)
			=> ListsEqualIgnoreCase(current, expected);

		/// <summary>
		/// True when KeyStatus must be rewritten from BindingStorage ownership (empty, stale default,
		/// wrong markers, mixed raw+advanced residue, etc.). Does not mutate.
		/// </summary>
		public static bool NeedsKeyStatusRepair(
			IReadOnlyList<string> current,
			bool hold,
			IReadOnlyList<string> keys,
			bool isEmpty)
			=> !MatchesExpectedKeyStatus(current, hold, keys, isEmpty);

		public static bool NeedsKeyStatusRepair(IReadOnlyList<string> current, ComboBindingData combo)
			=> !MatchesExpectedKeyStatus(current, combo);

		private static bool ListsEqualIgnoreCase(IReadOnlyList<string> current, IReadOnlyList<string> expected)
		{
			expected ??= Array.Empty<string>();
			if (expected.Count == 0)
				return !AdvancedVanillaOwnershipMarker.IsEffectivelyBound(current);

			if (current == null || current.Count != expected.Count)
				return false;

			for (int i = 0; i < expected.Count; i++)
			{
				if (!string.Equals(current[i], expected[i], StringComparison.OrdinalIgnoreCase))
					return false;
			}

			return true;
		}

		/// <summary>
		/// Promote a sole hook-managed raw KeyStatus token into an advanced combo + markers.
		/// Returns false when the action already has a combo or the token is not hook-managed.
		/// </summary>
		public static bool TryBuildPromoteFromRawHookToken(
			string rawToken,
			out ComboBindingData advancedCombo,
			out List<string> keyStatusAfter)
		{
			advancedCombo = null;
			keyStatusAfter = null;
			if (string.IsNullOrEmpty(rawToken) || !PlainSingleTapPolicy.IsHookManagedTapToken(rawToken))
				return false;

			advancedCombo = new ComboBindingData { Slot3 = rawToken };
			keyStatusAfter = AdvancedVanillaOwnershipMarker.BuildMarkers(advancedCombo.Keys);
			return true;
		}

		private static int ResolveBuildKeyStatusInputModeOrdinal(string storageOrTriggerId)
		{
			var kind = ControlsGameUiSeparationPolicy.ResolveSyncKind(
				storageOrTriggerId, out int singleOrdinal);
			if (kind == ControlsGameUiSeparationPolicy.SyncModeKind.SingleInputMode
				&& singleOrdinal >= 0)
				return singleOrdinal;

			return KeyboardAdditionalOrPolicy.InputModeKeyboard;
		}
	}
}
