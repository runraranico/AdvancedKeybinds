using System;
using System.Collections.Generic;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// World-portable rule: one WM_KEY* EventSequence → one registration newcomer.
	/// EventSequence identity is primary; AreSamePhysical is last-resort only.
	/// </summary>
	public static class SameEventAliasPolicy
	{
		public const string PolicyVersion = "same-event-sc-v2-event-sequence";

		/// <summary>
		/// Same-press decision priority (registration / orphan suppress):
		/// 1. EventSequence identical
		/// 2. Same MeasuredPhysicalKeyEvent origin (caller flag)
		/// 3. Measured Scan+Extended identical / recorded for VK
		/// 4. Normalized SC already in pressed
		/// 5. PhysicalKeyIdentity.AreSamePhysical (legacy / no event info only)
		/// </summary>
		public static bool ShouldSuppressOrphanKeysOrVk(
			uint vk,
			ushort measuredScanForVk,
			bool measuredExtendedForVk,
			string keysName,
			IEnumerable<string> pressed,
			bool sameMeasuredEventOrigin = false,
			long eventSequence = 0,
			long existingEventSequence = 0)
		{
			if (vk == 0 || MeasuredPhysicalKeyNormalizer.IsSpecialHandledVirtualKey(vk))
				return false;

			measuredScanForVk = (ushort)(measuredScanForVk & 0xFF);

			// 1: EventSequence identical → same press aliases (ignore AreSamePhysical).
			if (eventSequence != 0 && eventSequence == existingEventSequence)
				return true;

			// 2: Same measured-event origin flag from KEYDOWN path.
			if (sameMeasuredEventOrigin)
				return true;

			// 3: VK has a recorded measured scan from KEYDOWN — SC owns the press.
			if (measuredScanForVk != 0)
				return true;

			if (pressed == null)
				return false;

			string vkToken = PhysicalKeyToken.FormatVk(vk);

			// 4: Normalized SC already in pressed (exact FormatScan when scan known via caller).
			foreach (string existing in pressed)
			{
				if (string.IsNullOrEmpty(existing))
					continue;
				if (!PhysicalKeyToken.TryParseScan(existing, out ushort existingScan, out bool existingExt))
					continue;
				if (measuredScanForVk != 0
					&& existingScan == measuredScanForVk
					&& existingExt == measuredExtendedForVk)
					return true;
			}

			// 5: layout AreSamePhysical — only when EventSequence / measured event absent.
			foreach (string existing in pressed)
			{
				if (string.IsNullOrEmpty(existing))
					continue;
				if (!PhysicalKeyToken.TryParseScan(existing, out _, out _))
					continue;
				if (PhysicalKeyIdentity.AreSamePhysical(vkToken, existing))
					return true;
				if (!string.IsNullOrEmpty(keysName)
					&& !MeasuredPhysicalKeyNormalizer.IsSpecialHandledToken(keysName)
					&& PhysicalKeyIdentity.AreSamePhysical(keysName, existing))
					return true;
			}

			return false;
		}

		/// <summary>
		/// Final Collapse defense: one measured SC: + non-special Keys/VK aliases → keep SC only.
		/// Never merges two different SC: (or SC Normal vs Extended).
		/// </summary>
		public static List<string> PreferSingleMeasuredScanOverAliases(List<string> tokens)
		{
			if (tokens == null || tokens.Count <= 1)
				return tokens ?? new List<string>();

			int scCount = 0;
			string sc = null;
			int aliasCount = 0;
			foreach (string t in tokens)
			{
				if (PhysicalKeyToken.TryParseScan(t, out _, out _))
				{
					scCount++;
					sc ??= t;
					continue;
				}

				if (!MeasuredPhysicalKeyNormalizer.IsSpecialHandledToken(t)
					&& !StableKeyToken.IsMouseToken(t))
					aliasCount++;
			}

			if (scCount == 1 && aliasCount > 0 && scCount + aliasCount == tokens.Count)
				return new List<string>(1) { sc };

			return tokens;
		}

		/// <summary>
		/// Registration newcomers for one measured event: measured SC only when available.
		/// </summary>
		public static List<string> BuildRegistrationNewcomers(MeasuredPhysicalKeyEvent ev)
		{
			var list = new List<string>(1);
			if (string.IsNullOrEmpty(ev.CanonicalToken))
				return list;

			list.Add(ev.CanonicalToken);
			return list;
		}

		/// <summary>
		/// EventSequence-first collapse for tagged newcomers (preferred registration path).
		/// </summary>
		public static List<string> CollapseByEventSequence(IReadOnlyList<SequencedNewcomer> newcomers)
			=> EventSequenceUnifier.CollapseSequencedNewcomers(newcomers);
	}
}
