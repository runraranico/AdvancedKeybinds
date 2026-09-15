using System;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Once-only / pending rules for legacy GP migration phases (Terraria-free).
	/// Combos migrate may run when plan requires it; orphan strip may be deferred until profile ready.
	/// </summary>
	public static class LegacyGamePadMigrationRetryPolicy
	{
		public enum Phase
		{
			None = 0,
			Full = 1,
			OrphanStripOnly = 2
		}

		/// <summary>
		/// Choose next production phase. Never schedules every-frame work.
		/// </summary>
		public static Phase NextPhase(
			bool combosNeedMutation,
			bool orphanMarkersPresent,
			bool profileReady,
			bool combosMigrationCompletedOnce,
			bool orphanStripPending,
			bool orphanStripCompletedOnce)
		{
			if (orphanStripCompletedOnce && !orphanMarkersPresent && !combosNeedMutation)
				return Phase.None;

			if (combosNeedMutation && !combosMigrationCompletedOnce)
				return Phase.Full;

			if (!profileReady)
			{
				// Combos already done or not needed — wait; do not strip.
				return Phase.None;
			}

			if (orphanStripPending || orphanMarkersPresent)
			{
				if (orphanStripCompletedOnce && !orphanMarkersPresent)
					return Phase.None;
				return Phase.OrphanStripOnly;
			}

			return Phase.None;
		}

		public static bool ShouldMarkOrphanPending(
			bool profileReady,
			bool orphanMarkersPresentOrExpected,
			bool combosPhaseRan)
		{
			if (profileReady)
				return false;
			return orphanMarkersPresentOrExpected || combosPhaseRan;
		}

		public static bool ShouldClearOrphanPending(
			bool stripAttempted,
			bool profilesSaveSucceeded,
			bool aborted,
			int remainingOrphans)
		{
			if (aborted || !stripAttempted || !profilesSaveSucceeded)
				return false;
			return remainingOrphans <= 0;
		}

		public static bool ShouldMarkOrphanCompleted(
			bool stripAttempted,
			bool profilesSaveSucceeded,
			bool aborted,
			int remainingOrphans)
			=> ShouldClearOrphanPending(stripAttempted, profilesSaveSucceeded, aborted, remainingOrphans);
	}
}
