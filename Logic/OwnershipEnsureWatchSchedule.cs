using System;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Sparse ownership-ensure watch used by BindingInputEngine (no Terraria types).
	/// Permanent every-frame scans are forbidden — including while profileReady is false.
	/// </summary>
	public static class OwnershipEnsureWatchSchedule
	{
		public const int WatchFrames = 180;
		public const int CheckIntervalFrames = 10;

		public static bool ShouldCheck(int elapsedAfterIncrement)
		{
			if (elapsedAfterIncrement <= 0 || elapsedAfterIncrement > WatchFrames)
				return false;
			if (elapsedAfterIncrement == 1 || elapsedAfterIncrement == WatchFrames)
				return true;
			return elapsedAfterIncrement % CheckIntervalFrames == 0;
		}

		/// <summary>
		/// After an Ensure attempt: repair → restart watch once more; profile never ready after a full
		/// window → stop (no unbounded restart). Otherwise keep elapsed.
		/// Late readiness is covered by Initialize / PostSetupContent / OnWorldLoad Ensure + Request.
		/// </summary>
		public static int NextElapsedAfterEnsure(int elapsed, bool profileReady, int repaired)
		{
			if (repaired > 0)
				return 0;

			// Do not loop forever when the profile never becomes ready.
			if (!profileReady && elapsed >= WatchFrames)
				return WatchFrames;

			return elapsed;
		}

		/// <summary>
		/// True while the watch should keep ticking (active short window, or awaiting profile via restarts).
		/// Caller increments elapsed only when this is true, then may call Ensure on <see cref="ShouldCheck"/>.
		/// </summary>
		public static bool IsWatchActive(int elapsed)
			=> elapsed < WatchFrames;

		/// <summary>Simulate N frames; returns how many times Ensure would run.</summary>
		public static int CountEnsureCalls(int frames, Func<int, (bool profileReady, int repaired)> onEnsure)
		{
			if (frames <= 0 || onEnsure == null)
				return 0;

			int elapsed = 0;
			int calls = 0;
			for (int f = 0; f < frames; f++)
			{
				if (!IsWatchActive(elapsed))
					break;

				elapsed++;
				if (!ShouldCheck(elapsed))
					continue;

				calls++;
				(bool profileReady, int repaired) = onEnsure(elapsed);
				elapsed = NextElapsedAfterEnsure(elapsed, profileReady, repaired);
			}

			return calls;
		}
	}
}
