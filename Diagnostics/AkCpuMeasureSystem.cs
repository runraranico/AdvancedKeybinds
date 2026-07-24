using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using AdvancedKeybinds.Binding;
using Terraria;
using Terraria.ModLoader;

namespace AdvancedKeybinds.Diagnostics
{
	/// <summary>
	/// Investigation CPU sampler. Runs timed 30s windows for title and in-game, then self-disables.
	/// Output: Saves/AdvancedKeybinds/ak-cpu-measure-title.txt and ak-cpu-measure-ingame.txt
	/// </summary>
	public sealed class AkCpuMeasureSystem : ModSystem
	{
		/// <summary>Armed for measurement session. Cleared automatically after both windows complete.</summary>
		public static bool ForceEnabled = false;

		public const int IdPlayerInputDetour = 0;
		public const int IdBindingInputEngine = 1;
		public const int IdWinKeySuppressorBlock = 2;
		public const int IdKeybindShortcut = 3;
		public const int IdHoldHudPostUpdateInput = 4;
		public const int IdHoldHudUpdateUI = 5;
		public const int IdHoldHudPostDraw = 6;
		public const int IdCaptureSurfaces = 7;
		public const int IdRefreshGamePixelSurface = 8;
		public const int IdZoomTick = 9;
		public const int IdHoldHudProbeLog = 10;
		public const int Count = 11;

		private static readonly string[] Names =
		{
			"PlayerInputDetour",
			"BindingInputEngine.Update",
			"WindowsKeySuppressor",
			"KeybindShortcutSystem",
			"HoldTierHud.PostUpdateInput",
			"HoldTierHud.UpdateUI",
			"HoldTierHud.PostDrawInterface",
			"CaptureSurfaces",
			"RefreshGamePixelSurface",
			"ZoomKeyLiveVerify.Tick",
			"HoldHudProbe.Log"
		};

		// Indices requested in the ranking report (exclude Zoom/Probe from primary table but still measure).
		private static readonly int[] RankIds =
		{
			IdPlayerInputDetour,
			IdBindingInputEngine,
			IdWinKeySuppressorBlock,
			IdKeybindShortcut,
			IdHoldHudPostUpdateInput,
			IdHoldHudUpdateUI,
			IdHoldHudPostDraw,
			IdRefreshGamePixelSurface,
			IdCaptureSurfaces
		};

		private enum Phase
		{
			WaitTitle,
			MeasureTitle,
			WaitIngame,
			MeasureIngame,
			Done
		}

		private static Phase _phase = Phase.WaitTitle;
		private static readonly Stopwatch WindowClock = new();
		private static readonly long[] SumTicks = new long[Count];
		private static readonly long[] MaxTicks = new long[Count];
		private static readonly int[] Calls = new int[Count];
		private static readonly long[] NestStart = new long[Count];
		private static readonly int[] NestDepth = new int[Count];

		private static int _gc0Start;
		private static int _gc1Start;
		private static int _gc2Start;
		private static int _framesInWindow;
		private static bool _statusWritten;
		private static long _evalBindSum;
		private static int _evalBindMax;
		private static long _mouseGetStateStart;

		private const double WindowSeconds = 30.0;

		private static bool IsOn => ForceEnabled && _phase != Phase.Done;

		public override void OnModLoad()
		{
			if (!ForceEnabled)
				return;
			_phase = Phase.WaitTitle;
			WriteStatus("ARMED waiting title (30s) then ingame (30s); will self-disable");
		}

		public override void PostUpdateEverything()
			=> TickPhase("PostUpdateEverything");

		/// <summary>Also driven from PlayerInput / Hold UpdateUI so title menu is covered.</summary>
		public static void TickPhase(string source)
		{
			if (!ForceEnabled)
				return;

			if (_phase == Phase.Done)
				return;

			bool menu = Main.gameMenu;

			switch (_phase)
			{
				case Phase.WaitTitle:
					if (!menu)
						return;
					StartWindow();
					_phase = Phase.MeasureTitle;
					WriteStatus("MEASURE_TITLE start src=" + source);
					break;

				case Phase.MeasureTitle:
					if (!menu)
					{
						// Left title early — discard partial and wait for title again.
						ResetAccumulators();
						_phase = Phase.WaitTitle;
						WriteStatus("MEASURE_TITLE aborted (left menu); waiting title again src=" + source);
						return;
					}
					_framesInWindow++;
					SampleEngineExtras();
					if (WindowClock.Elapsed.TotalSeconds >= WindowSeconds)
					{
						FlushWindow("title", "ak-cpu-measure-title.txt");
						_phase = Phase.WaitIngame;
						WriteStatus("MEASURE_TITLE done; waiting ingame src=" + source);
					}
					break;

				case Phase.WaitIngame:
					if (menu)
						return;
					StartWindow();
					_phase = Phase.MeasureIngame;
					WriteStatus("MEASURE_INGAME start src=" + source);
					break;

				case Phase.MeasureIngame:
					if (menu)
					{
						ResetAccumulators();
						_phase = Phase.WaitIngame;
						WriteStatus("MEASURE_INGAME aborted (returned to menu); waiting ingame again src=" + source);
						return;
					}
					_framesInWindow++;
					SampleEngineExtras();
					if (WindowClock.Elapsed.TotalSeconds >= WindowSeconds)
					{
						FlushWindow("ingame", "ak-cpu-measure-ingame.txt");
						_phase = Phase.Done;
						ForceEnabled = false;
						WriteStatus("DONE both windows; ForceEnabled=false (self-disabled) src=" + source);
					}
					break;
			}
		}

		private static void StartWindow()
		{
			ResetAccumulators();
			_gc0Start = GC.CollectionCount(0);
			_gc1Start = GC.CollectionCount(1);
			_gc2Start = GC.CollectionCount(2);
			_framesInWindow = 0;
			_evalBindSum = 0;
			_evalBindMax = 0;
			_mouseGetStateStart = MouseInputHelper.MouseGetStateCount;
			WindowClock.Restart();
		}

		private static void SampleEngineExtras()
		{
			int eval = BindingInputEngine.LastEvaluatedBindingCount;
			_evalBindSum += eval;
			if (eval > _evalBindMax)
				_evalBindMax = eval;
		}

		private static void ResetAccumulators()
		{
			Array.Clear(SumTicks, 0, Count);
			Array.Clear(MaxTicks, 0, Count);
			Array.Clear(Calls, 0, Count);
			Array.Clear(NestDepth, 0, Count);
			_evalBindSum = 0;
			_evalBindMax = 0;
			WindowClock.Reset();
		}

		public static void Begin(int id)
		{
			if (!IsOn || id < 0 || id >= Count)
				return;
			if (_phase != Phase.MeasureTitle && _phase != Phase.MeasureIngame)
				return;
			if (NestDepth[id]++ == 0)
				NestStart[id] = Stopwatch.GetTimestamp();
		}

		public static void End(int id)
		{
			if (!IsOn || id < 0 || id >= Count)
				return;
			if (_phase != Phase.MeasureTitle && _phase != Phase.MeasureIngame)
				return;
			if (--NestDepth[id] != 0)
			{
				if (NestDepth[id] < 0)
					NestDepth[id] = 0;
				return;
			}

			long dt = Stopwatch.GetTimestamp() - NestStart[id];
			if (dt < 0)
				return;
			SumTicks[id] += dt;
			Calls[id]++;
			if (dt > MaxTicks[id])
				MaxTicks[id] = dt;
		}

		private static void FlushWindow(string label, string fileName)
		{
			try
			{
				string dir = Path.Combine(Main.SavePath, "AdvancedKeybinds");
				Directory.CreateDirectory(dir);
				string path = Path.Combine(dir, fileName);
				double freq = Stopwatch.Frequency;
				double elapsed = WindowClock.Elapsed.TotalSeconds;
				int gc0 = GC.CollectionCount(0) - _gc0Start;
				int gc1 = GC.CollectionCount(1) - _gc1Start;
				int gc2 = GC.CollectionCount(2) - _gc2Start;

				var sb = new StringBuilder(8192);
				sb.AppendLine("=== AkCpuMeasure window=" + label + " ===");
				sb.AppendLine("time=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
				sb.AppendLine("elapsed_sec=" + elapsed.ToString("0.###"));
				sb.AppendLine("frames=" + _framesInWindow);
				sb.AppendLine("menu=" + Main.gameMenu);
				sb.AppendLine("HoldHudProbe.Enabled=" + HoldHudProbe.Enabled);
				sb.AppendLine("ZoomForceEnabled=" + ZoomKeyLiveVerifySystem.ForceEnabled);
				sb.AppendLine("GC_delta gen0=" + gc0 + " gen1=" + gc1 + " gen2=" + gc2);
				double avgEval = _framesInWindow > 0 ? (double)_evalBindSum / _framesInWindow : 0;
				sb.AppendLine("BindingInputEngine.eval_binds_avg=" + avgEval.ToString("0.###"));
				sb.AppendLine("BindingInputEngine.eval_binds_max=" + _evalBindMax);
				sb.AppendLine("Mouse.GetState_delta=" + (MouseInputHelper.MouseGetStateCount - _mouseGetStateStart));
				sb.AppendLine();
				sb.AppendLine("rank\tname\tcalls\tavg_ms\tmax_ms\tsum_ms\tavg_us\tmax_us");

				var order = (int[])RankIds.Clone();
				Array.Sort(order, (a, b) =>
				{
					double sa = SumTicks[a] * 1000.0 / freq;
					double sbMs = SumTicks[b] * 1000.0 / freq;
					return sbMs.CompareTo(sa);
				});

				sb.AppendLine("--- RANKING by sum_ms (requested items) ---");
				int rank = 1;
				foreach (int i in order)
					AppendRow(sb, rank++, Names[i], i, freq);

				sb.AppendLine();
				sb.AppendLine("--- ALL buckets ---");
				for (int i = 0; i < Count; i++)
					AppendRow(sb, i + 1, Names[i], i, freq);

				File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
			}
			catch
			{
				// never break gameplay
			}
		}

		private static void AppendRow(StringBuilder sb, int rank, string name, int id, double freq)
		{
			int c = Calls[id];
			double sumMs = SumTicks[id] * 1000.0 / freq;
			double maxMs = MaxTicks[id] * 1000.0 / freq;
			double avgMs = c > 0 ? sumMs / c : 0;
			double avgUs = avgMs * 1000.0;
			double maxUs = maxMs * 1000.0;
			sb.Append(rank).Append('\t')
				.Append(name).Append('\t')
				.Append(c).Append('\t')
				.Append(avgMs.ToString("0.######")).Append('\t')
				.Append(maxMs.ToString("0.######")).Append('\t')
				.Append(sumMs.ToString("0.###")).Append('\t')
				.Append(avgUs.ToString("0.###")).Append('\t')
				.Append(maxUs.ToString("0.###"))
				.AppendLine();
		}

		private static void WriteStatus(string text)
		{
			try
			{
				string dir = Path.Combine(Main.SavePath, "AdvancedKeybinds");
				Directory.CreateDirectory(dir);
				string path = Path.Combine(dir, "ak-cpu-measure-status.txt");
				File.AppendAllText(
					path,
					DateTime.Now.ToString("HH:mm:ss.fff") + " " + text + Environment.NewLine,
					new UTF8Encoding(true));
				_statusWritten = true;
			}
			catch
			{
				// ignore
			}
		}
	}
}
