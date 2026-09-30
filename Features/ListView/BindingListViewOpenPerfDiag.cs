using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Terraria.Localization;

namespace AdvancedKeybinds.Features.ListView
{
	/// <summary>
	/// TEMP: times hotkey→open→catalog→layout→first draw. No layout/input changes.
	/// Log: deck log\Keyboard\BindingList_OpenPerf.tsv
	/// Gate: <see cref="BindingListViewLiveDiagGate.OpenPerfEnabled"/> (default OFF).
	/// </summary>
	internal static class BindingListViewOpenPerfDiag
	{
		private static bool _armed;
		private static bool _waitingFirstDraw;
		private static bool _wrote;
		private static long _tHotkey;
		private static long _tOpenStart;
		private static long _tCatalogStart;
		private static long _tCatalogEnd;
		private static long _tRebuildStart;
		private static long _tRebuildEnd;
		private static long _tFirstDrawEnter;
		private static long _tCultureAuditStart;
		private static long _tCultureAuditEnd;
		private static long _tLayoutRectDiagStart;
		private static long _tLayoutRectDiagEnd;
		private static long _tFirstDrawExit;
		private static int _measureCallsBeforeDraw;
		private static int _measureCallsAfterDraw;
		private static int _drawFittedCalls;
		private static bool _liveWasOn;

		public static int MeasureCallCount;
		public static int DrawFittedCallCount;

		public static void NoteMeasureCall()
		{
			if (!BindingListViewLiveDiagGate.OpenPerfEnabled)
				return;
			if (_waitingFirstDraw || _armed)
				MeasureCallCount++;
		}

		public static void NoteDrawFittedCall()
		{
			if (!BindingListViewLiveDiagGate.OpenPerfEnabled)
				return;
			if (_waitingFirstDraw)
				DrawFittedCallCount++;
		}

		public static void OnHotkeyOpenDetected()
		{
			if (!BindingListViewLiveDiagGate.OpenPerfEnabled)
				return;
			_armed = true;
			_waitingFirstDraw = false;
			_wrote = false;
			_tHotkey = Stopwatch.GetTimestamp();
			_tOpenStart = 0;
			_tCatalogStart = _tCatalogEnd = 0;
			_tRebuildStart = _tRebuildEnd = 0;
			_tFirstDrawEnter = _tFirstDrawExit = 0;
			_tCultureAuditStart = _tCultureAuditEnd = 0;
			_tLayoutRectDiagStart = _tLayoutRectDiagEnd = 0;
			MeasureCallCount = 0;
			DrawFittedCallCount = 0;
			_liveWasOn = BindingListViewLiveDiagGate.FileOutputEnabled;
			BindingListViewLiveDiagGate.ResetSessionCounters();
		}

		public static void OnOpenStateTrue()
		{
			if (!BindingListViewLiveDiagGate.OpenPerfEnabled || !_armed)
				return;
			_tOpenStart = Stopwatch.GetTimestamp();
			_waitingFirstDraw = true;
		}

		public static void OnCatalogBuildStart()
		{
			if (!BindingListViewLiveDiagGate.OpenPerfEnabled || !_armed)
				return;
			_tCatalogStart = Stopwatch.GetTimestamp();
		}

		public static void OnCatalogBuildEnd()
		{
			if (!BindingListViewLiveDiagGate.OpenPerfEnabled || !_armed)
				return;
			_tCatalogEnd = Stopwatch.GetTimestamp();
		}

		public static void OnRebuildVisibleStart()
		{
			if (!BindingListViewLiveDiagGate.OpenPerfEnabled || !_armed)
				return;
			_tRebuildStart = Stopwatch.GetTimestamp();
		}

		public static void OnRebuildVisibleEnd()
		{
			if (!BindingListViewLiveDiagGate.OpenPerfEnabled || !_armed)
				return;
			_tRebuildEnd = Stopwatch.GetTimestamp();
		}

		public static void OnCultureAuditStart()
		{
			if (!BindingListViewLiveDiagGate.OpenPerfEnabled || !_waitingFirstDraw)
				return;
			_tCultureAuditStart = Stopwatch.GetTimestamp();
			_measureCallsBeforeDraw = MeasureCallCount;
		}

		public static void OnCultureAuditEnd()
		{
			if (!BindingListViewLiveDiagGate.OpenPerfEnabled || !_waitingFirstDraw)
				return;
			_tCultureAuditEnd = Stopwatch.GetTimestamp();
		}

		public static void OnLayoutRectDiagStart()
		{
			if (!BindingListViewLiveDiagGate.OpenPerfEnabled || !_waitingFirstDraw)
				return;
			_tLayoutRectDiagStart = Stopwatch.GetTimestamp();
		}

		public static void OnLayoutRectDiagEnd()
		{
			if (!BindingListViewLiveDiagGate.OpenPerfEnabled || !_waitingFirstDraw)
				return;
			_tLayoutRectDiagEnd = Stopwatch.GetTimestamp();
		}

		public static void OnFirstDrawEnter()
		{
			if (!BindingListViewLiveDiagGate.OpenPerfEnabled || !_waitingFirstDraw || _wrote)
				return;
			_tFirstDrawEnter = Stopwatch.GetTimestamp();
			_measureCallsBeforeDraw = MeasureCallCount;
			_drawFittedCalls = 0;
			DrawFittedCallCount = 0;
		}

		public static void OnFirstDrawExit()
		{
			if (!BindingListViewLiveDiagGate.OpenPerfEnabled || !_waitingFirstDraw || _wrote)
				return;
			_tFirstDrawExit = Stopwatch.GetTimestamp();
			_measureCallsAfterDraw = MeasureCallCount;
			_drawFittedCalls = DrawFittedCallCount;
			WriteReport();
			_waitingFirstDraw = false;
			_armed = false;
			_wrote = true;
		}

		private static double Ms(long from, long to)
		{
			if (from <= 0 || to <= 0 || to < from)
				return -1;
			return (to - from) * 1000.0 / Stopwatch.Frequency;
		}

		private static void WriteReport()
		{
			if (!BindingListViewLiveDiagGate.OpenPerfEnabled)
				return;
			try
			{
				string dir = Path.Combine(
					Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
					"My Games", "Terraria", "tModLoader", "deck log", "Keyboard");
				Directory.CreateDirectory(dir);
				string path = Path.Combine(dir, "BindingList_OpenPerf.tsv");

				double hotkeyToOpen = Ms(_tHotkey, _tOpenStart);
				double catalog = Ms(_tCatalogStart, _tCatalogEnd);
				double rebuild = Ms(_tRebuildStart, _tRebuildEnd);
				double cultureAudit = Ms(_tCultureAuditStart, _tCultureAuditEnd);
				double layoutRectDiag = Ms(_tLayoutRectDiagStart, _tLayoutRectDiagEnd);
				double firstDraw = Ms(_tFirstDrawEnter, _tFirstDrawExit);
				double openToFirstDrawEnd = Ms(_tOpenStart > 0 ? _tOpenStart : _tHotkey, _tFirstDrawExit);
				double hotkeyToReady = Ms(_tHotkey, _tFirstDrawExit);
				int measuresInFirstDraw = Math.Max(0, _measureCallsAfterDraw - _measureCallsBeforeDraw);

				var sb = new StringBuilder();
				if (!File.Exists(path))
				{
					sb.AppendLine(
						"Utc\tLiveFileOut\tLang\tHotkeyToOpenMs\tCatalogMs\tRebuildLayoutMs\t"
						+ "CultureSweepAuditMs\tLayoutRectDiagMs\tFirstDrawBodyMs\tOpenToFirstDrawEndMs\tHotkeyToReadyMs\t"
						+ "MeasureCallsTotal\tMeasureCallsFirstDrawBody\tDrawFittedCallsFirstDraw\t"
						+ "LiveAppendCalls\tLiveAppendBytes\tLiveAppendMs\tLivePeakAppendPerSec");
				}

				sb.Append(DateTime.UtcNow.ToString("o")).Append('\t')
					.Append(_liveWasOn ? "ON" : "OFF").Append('\t')
					.Append(Language.ActiveCulture != null ? Language.ActiveCulture.Name : "?").Append('\t')
					.Append(hotkeyToOpen.ToString("0.###")).Append('\t')
					.Append(catalog.ToString("0.###")).Append('\t')
					.Append(rebuild.ToString("0.###")).Append('\t')
					.Append(cultureAudit.ToString("0.###")).Append('\t')
					.Append(layoutRectDiag.ToString("0.###")).Append('\t')
					.Append(firstDraw.ToString("0.###")).Append('\t')
					.Append(openToFirstDrawEnd.ToString("0.###")).Append('\t')
					.Append(hotkeyToReady.ToString("0.###")).Append('\t')
					.Append(MeasureCallCount).Append('\t')
					.Append(measuresInFirstDraw).Append('\t')
					.Append(_drawFittedCalls).Append('\t')
					.Append(BindingListViewLiveDiagGate.AppendCalls).Append('\t')
					.Append(BindingListViewLiveDiagGate.AppendBytes).Append('\t')
					.Append(BindingListViewLiveDiagGate.AppendMsTotal.ToString("0.###")).Append('\t')
					.Append(BindingListViewLiveDiagGate.PeakAppendCallsPerSecond)
					.AppendLine();

				File.AppendAllText(path, sb.ToString(), Encoding.UTF8);

				// Human summary beside TSV
				string summary = Path.Combine(dir, "BindingList_OpenPerf_LAST.txt");
				var sum = new StringBuilder();
				sum.AppendLine("AK ALL KEY OpenPerf (TEMP diagnosis — not a fix)");
				sum.AppendLine("LiveFileOutput=" + (_liveWasOn ? "ON" : "OFF"));
				sum.AppendLine("ActiveCulture=" + (Language.ActiveCulture != null ? Language.ActiveCulture.Name : "?"));
				sum.AppendLine();
				sum.AppendLine("Hotkey→OpenState:     " + Fmt(hotkeyToOpen));
				sum.AppendLine("Catalog BuildAll:     " + Fmt(catalog));
				sum.AppendLine("Rebuild+ColWidths:    " + Fmt(rebuild));
				sum.AppendLine("CultureSweep audits:  " + Fmt(cultureAudit) + "  << HeavyDiag only (default OFF)");
				sum.AppendLine("LayoutRectDiag:       " + Fmt(layoutRectDiag) + "  << HeavyDiag only (default OFF)");
				sum.AppendLine("FirstDraw body:       " + Fmt(firstDraw));
				sum.AppendLine("Open→FirstDrawEnd:    " + Fmt(openToFirstDrawEnd));
				sum.AppendLine("Hotkey→Ready:         " + Fmt(hotkeyToReady));
				sum.AppendLine();
				sum.AppendLine("MeasureCalls (session to first draw end): " + MeasureCallCount);
				sum.AppendLine("MeasureCalls during first-draw body:      " + measuresInFirstDraw);
				sum.AppendLine("DrawFittedText calls first draw:          " + _drawFittedCalls);
				sum.AppendLine();
				sum.AppendLine("LIVE AppendCalls: " + BindingListViewLiveDiagGate.AppendCalls);
				sum.AppendLine("LIVE AppendBytes: " + BindingListViewLiveDiagGate.AppendBytes);
				sum.AppendLine("LIVE AppendMs:    " + BindingListViewLiveDiagGate.AppendMsTotal.ToString("0.###"));
				sum.AppendLine("LIVE Peak/sec:    " + BindingListViewLiveDiagGate.PeakAppendCallsPerSecond);
				sum.AppendLine();
				sum.AppendLine("Gates: BindingListViewLiveDiagGate.HeavyDiagEnabled / FileOutputEnabled (both default false).");
				sum.AppendLine("Compare: leave both OFF for normal play; set true only for Localization / Regression QA.");
				File.WriteAllText(summary, sum.ToString(), Encoding.UTF8);
			}
			catch
			{
				// never break open
			}
		}

		private static string Fmt(double ms)
			=> ms < 0 ? "n/a" : ms.ToString("0.###") + " ms";
	}
}
