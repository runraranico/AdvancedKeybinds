namespace AdvancedKeybinds.Features.ListView
{
	/// <summary>
	/// TEMP gates for ListView diagnostics. Hooks may stay registered; heavy work / File I/O is gated.
	/// Default OFF for normal play. Set true explicitly during Localization / Regression QA.
	/// Does not change input Baseline. Final removal is a later task.
	/// </summary>
	internal static class BindingListViewLiveDiagGate
	{
		/// <summary>
		/// CultureSweep TextFitAudit / HeaderPosDiag (SetLanguage × all cultures on first draw).
		/// Default OFF — was ~2s of first Open after Build+Reload.
		/// </summary>
		public static bool HeavyDiagEnabled = false;

		/// <summary>
		/// HitDiag / ClickPath / HoverSoundDiag File.AppendAllText and residual diag bookkeeping.
		/// Default OFF — large sync I/O while list stays open.
		/// </summary>
		public static bool FileOutputEnabled = false;

		/// <summary>
		/// Read-only Inventory Mouse Item hold-vs-draw LIVE (<see cref="BindingListViewMouseItemDiag"/>).
		/// Default OFF. Set true only while capturing STATE A/B/C for the draw regression.
		/// Does not mutate mouseItem / buttons / layers / SpriteBatch.
		/// </summary>
		public static bool MouseItemDiagEnabled = false;

		/// <summary>
		/// Read-only keyboard/mouse sticky consistency audit (<see cref="BindingListViewInputRegressionAudit"/>).
		/// Default OFF. Set true only for a full input regression audit session.
		/// Does not mutate input state. Does not inject keys.
		/// </summary>
		public static bool InputRegressionAuditEnabled = false;

		/// <summary>
		/// Open timing TSV (<see cref="BindingListViewOpenPerfDiag"/>). Default OFF for normal play.
		/// </summary>
		public static bool OpenPerfEnabled = false;

		/// <summary>
		/// One-shot path compare TSV (<see cref="BindingListViewPathCompareDiag"/>). Default OFF for normal play.
		/// </summary>
		public static bool PathCompareEnabled = false;

		/// <summary>
		/// Hover-leak LIVE capture (<see cref="BindingListViewHoverLeakDiag"/>). Default OFF for normal play.
		/// Bypasses <see cref="FileOutputEnabled"/> when on — keep false unless investigating.
		/// </summary>
		public static bool HoverLeakDiagEnabled = false;

		/// <summary>
		/// Click-path LIVE capture (<see cref="BindingListViewClickPathDiag"/>). Default OFF for normal play.
		/// When false (or FileOutput off), skip residual mouse/item snapshots as well as Append.
		/// </summary>
		public static bool ClickPathDiagEnabled = false;

		public static long AppendCalls;
		public static long AppendBytes;
		public static double AppendMsTotal;
		public static int AppendCallsThisSecond;
		public static int LastSecondUtc;
		public static int PeakAppendCallsPerSecond;

		public static void ResetSessionCounters()
		{
			AppendCalls = 0;
			AppendBytes = 0;
			AppendMsTotal = 0;
			AppendCallsThisSecond = 0;
			LastSecondUtc = -1;
			PeakAppendCallsPerSecond = 0;
		}

		public static void NoteAppend(int byteCount, double elapsedMs)
		{
			AppendCalls++;
			AppendBytes += byteCount;
			AppendMsTotal += elapsedMs;

			int sec = (int)(System.DateTime.UtcNow.Ticks / System.TimeSpan.TicksPerSecond);
			if (sec != LastSecondUtc)
			{
				if (AppendCallsThisSecond > PeakAppendCallsPerSecond)
					PeakAppendCallsPerSecond = AppendCallsThisSecond;
				AppendCallsThisSecond = 0;
				LastSecondUtc = sec;
			}

			AppendCallsThisSecond++;
		}
	}
}
