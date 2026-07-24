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
