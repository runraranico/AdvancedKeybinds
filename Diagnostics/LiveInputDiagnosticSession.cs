using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using AdvancedKeybinds.Binding;
using AdvancedKeybinds.Core;
using AdvancedKeybinds.Patches;
using Microsoft.Xna.Framework.Input;
using Terraria;

namespace AdvancedKeybinds.Diagnostics
{
	/// <summary>
	/// Opt-in, read-only live observation session. It subscribes to existing diagnostic events and
	/// receives observation calls at capture/action/HOLD sites. It never changes input state or bindings.
	/// </summary>
	internal static class LiveInputDiagnosticSession
	{
		private const double RouteCorrelationMs = 35.0;
		private const double ReleaseCorrelationMs = 60.0;
		private const double SessionStartBoundaryGraceMs = 250.0;

		private sealed class InputEvent
		{
			public long Sequence;
			public long Tick;
			public DateTime Utc;
			public string Device;
			public string Source;
			public bool Down;
			public bool Repeat;
			public bool Injected;
			public uint Vk;
			public ushort Scan;
			public bool Extended;
			public string MonoGameKey;
			public string Glyph;
			public bool DeadKey;
			public int? ToUnicodeResult;
			public string GlyphAvailability;
			public string ResolvedTier;
			public string ResolvedToken;
			public string CaptureToken;
			public bool CaptureOk;
			public string FailReason;
			public string LayoutCulture;
			public string Hkl;
			public string Modifiers;
			public string SideAndEnterResolution;
			public int CandidateId;
		}

		private sealed class PendingHookEvent
		{
			public string Source;
			public string Device;
			public uint Vk;
			public ushort Scan;
			public bool Extended;
			public bool Down;
			public bool Repeat;
			public bool Injected;
			public long Tick;
		}

		private sealed class Candidate
		{
			public int Id;
			public ushort Scan;
			public bool Extended;
			public uint Vk;
			public long FirstDownTick;
			public long FirstUpTick;
			public long LastRouteTick;
			public string Device;
			public readonly List<InputEvent> Events = new();
			public readonly HashSet<string> Sources = new(StringComparer.Ordinal);
			public readonly HashSet<string> ResolvedTokens = new(StringComparer.OrdinalIgnoreCase);
			public int RawEventCount;
			public int RoutedEventCount;
			public int AcceptedCaptureCount;
			public int ActionExecutionCount;
			public bool CaptureDecisionObserved;
			public int RepeatCount;
			public long FirstCaptureTick;
			public long FirstActionTick;
			public string AcceptedToken;
			public string CaptureReason;
			public readonly List<string> Actions = new();
			public readonly Dictionary<string, int> ActionPulseCounts = new(StringComparer.Ordinal);
			public readonly List<string> HoldObservations = new();
		}

		private static readonly object Sync = new();
		private static readonly List<InputEvent> Events = new();
		private static readonly List<Candidate> Candidates = new();
		private static readonly Dictionary<string, int> PressCounts = new(StringComparer.OrdinalIgnoreCase);
		private static readonly ConcurrentQueue<PendingHookEvent> PendingHookEvents = new();
		private static readonly HashSet<Keys> KeysDownAtSessionStart = new();
		private static IReadOnlyList<KeyboardLayoutHelper.InstalledLayoutInfo> _layouts;
		private static long _sessionStartTick;
		private static DateTime _sessionStartUtc;
		private static long _sequence;
		private static int _candidateSequence;
		private static bool _subscribed;

		public static bool IsActive { get; private set; }

		public static string LastLivePath { get; private set; }
		public static string LastRoutePath { get; private set; }
		public static string LastTimingPath { get; private set; }

		public static string Start()
		{
			lock (Sync)
			{
				if (IsActive)
					return "already-active";

				Events.Clear();
				Candidates.Clear();
				PressCounts.Clear();
				while (PendingHookEvents.TryDequeue(out _))
				{
				}
				_sequence = 0;
				_candidateSequence = 0;
				KeysDownAtSessionStart.Clear();
				foreach (Keys key in Main.keyState.GetPressedKeys())
					KeysDownAtSessionStart.Add(key);
				_sessionStartTick = DiagnosticReportSupport.Timestamp();
				_sessionStartUtc = DateTime.UtcNow;
				_layouts = KeyboardLayoutHelper.GetInstalledLayouts();
				LastLivePath = LastRoutePath = LastTimingPath = null;
				Subscribe();
				IsActive = true;
				return "started";
			}
		}

		public static DiagnosticRunOutcome Stop()
		{
			lock (Sync)
			{
				if (!IsActive)
				return new DiagnosticRunOutcome { Ok = false, Message = "not-active" };

				Poll();
				IsActive = false;
				Unsubscribe();
				return WriteReports();
			}
		}

		public static void Shutdown()
		{
			lock (Sync)
			{
				IsActive = false;
				Unsubscribe();
			}
		}

		/// <summary>Moves lightweight LL-hook observations to the main thread for enrichment.</summary>
		public static void Poll()
		{
			if (!IsActive)
				return;
			while (PendingHookEvents.TryDequeue(out PendingHookEvent e))
			{
				Record(e.Source, e.Device, e.Vk, e.Scan, e.Extended, e.Down, e.Repeat, e.Injected, e.Tick);
			}
		}

		public static DiagnosticRunOutcome WriteCurrentRouteSnapshot()
		{
			lock (Sync)
			{
				if (Candidates.Count == 0)
					return new DiagnosticRunOutcome { Ok = false, Message = "no-live-input-observations" };
				LastRoutePath = WriteRouteReport();
				return BuildOutcome(LastRoutePath, CountRouteVerdicts());
			}
		}

		public static DiagnosticRunOutcome WriteCurrentTimingSnapshot()
		{
			lock (Sync)
			{
				if (Candidates.Count == 0)
					return new DiagnosticRunOutcome { Ok = false, Message = "no-live-input-observations" };
				LastTimingPath = WriteTimingReport();
				return BuildOutcome(LastTimingPath, CountTimingVerdicts());
			}
		}

		public static void ObserveCaptureDecision(
			bool accepted,
			string token,
			string reason)
		{
			if (!IsActive)
				return;

			try
			{
				lock (Sync)
				{
					Candidate candidate = FindMostRecentCandidate(token);
					if (candidate == null)
						return;
					candidate.CaptureDecisionObserved = true;
					if (accepted)
					{
						candidate.AcceptedCaptureCount++;
						if (candidate.FirstCaptureTick == 0)
							candidate.FirstCaptureTick = DiagnosticReportSupport.Timestamp();
						candidate.AcceptedToken = token;
					}
					candidate.CaptureReason = reason;
				}
			}
			catch
			{
				// Observation must never affect capture.
			}
		}

		/// <summary>
		/// Counts an AdvancedKeybinds JustPressed pulse injected into PlayerInput. This proves the MOD
		/// published one trigger, not that an unknown downstream game/mod handler executed once.
		/// </summary>
		public static void ObserveActionExecution(string actionId)
		{
			if (!IsActive)
				return;

			try
			{
				lock (Sync)
				{
					Candidate candidate = FindCandidateForAction(actionId);
					if (candidate == null)
						return;
					candidate.ActionExecutionCount++;
					if (candidate.FirstActionTick == 0)
						candidate.FirstActionTick = DiagnosticReportSupport.Timestamp();
					candidate.Actions.Add(actionId ?? "(null)");
					string actionKey = actionId ?? "(null)";
					candidate.ActionPulseCounts[actionKey] =
						candidate.ActionPulseCounts.TryGetValue(actionKey, out int count) ? count + 1 : 1;
				}
			}
			catch
			{
			}
		}

		public static void ObserveHold(
			string actionId,
			string phase,
			double engineHeldMs,
			int thresholdMs,
			bool holdOnRelease)
		{
			if (!IsActive)
				return;

			try
			{
				lock (Sync)
				{
					Candidate candidate = FindCandidateForAction(actionId);
					if (candidate == null)
						return;
					double sinceDown = candidate.FirstDownTick == 0
						? -1
						: DiagnosticReportSupport.ElapsedMs(candidate.FirstDownTick, DiagnosticReportSupport.Timestamp());
					candidate.HoldObservations.Add(
						"phase=" + phase
						+ " action=" + (actionId ?? "-")
						+ " engineHeldMs=" + engineHeldMs.ToString("0.###", CultureInfo.InvariantCulture)
						+ " monotonicSinceFirstDownMs=" + sinceDown.ToString("0.###", CultureInfo.InvariantCulture)
						+ " thresholdMs=" + thresholdMs
						+ " holdOnRelease=" + holdOnRelease);
				}
			}
			catch
			{
			}
		}

		private static void Subscribe()
		{
			if (_subscribed)
				return;
			AdvancedKeybindsDiagnosticEvents.KeyMessageObserved += OnKeyMessage;
			AdvancedKeybindsDiagnosticEvents.RawInputObserved += OnRawInput;
			_subscribed = true;
		}

		private static void Unsubscribe()
		{
			if (!_subscribed)
				return;
			AdvancedKeybindsDiagnosticEvents.KeyMessageObserved -= OnKeyMessage;
			AdvancedKeybindsDiagnosticEvents.RawInputObserved -= OnRawInput;
			_subscribed = false;
		}

		private static void OnKeyMessage(DiagnosticKeyMessageEventArgs e)
		{
			if (string.Equals(e.MessageName, "WH_KEYBOARD_LL", StringComparison.Ordinal))
			{
				PendingHookEvents.Enqueue(new PendingHookEvent {
					Source = e.MessageName,
					Device = e.DeviceIdentifier,
					Vk = e.VirtualKey,
					Scan = e.ScanCode,
					Extended = e.Extended,
					Down = e.IsKeyDown,
					Repeat = e.IsRepeat,
					Injected = e.Injected,
					Tick = e.MonotonicTimestamp
				});
				return;
			}

			Record(
				e.MessageName,
				e.DeviceIdentifier,
				e.VirtualKey,
				e.ScanCode,
				e.Extended,
				e.IsKeyDown,
				e.IsRepeat,
				e.Injected,
				e.MonotonicTimestamp);
		}

		private static void OnRawInput(DiagnosticRawInputEventArgs e)
			=> Record(
				"WM_INPUT",
				e.DeviceIdentifier,
				e.VirtualKey,
				e.MakeCode,
				e.Extended,
				!e.IsKeyUp,
				isRepeat: false,
				e.Injected,
				e.MonotonicTimestamp);

		private static void Record(
			string source,
			string device,
			uint vk,
			ushort scan,
			bool extended,
			bool down,
			bool isRepeat,
			bool injected,
			long tick)
		{
			if (!IsActive)
				return;

			try
			{
				lock (Sync)
				{
					IntPtr hkl = KeyboardLayoutHelper.GetActiveLayoutHandle();
					string culture = FindCulture(hkl);
					string mono = Enum.IsDefined(typeof(Keys), (int)vk) && (Keys)vk != Keys.None
						? ((Keys)vk).ToString()
						: "-";
					bool deferredFromHook = string.Equals(source, "WH_KEYBOARD_LL", StringComparison.Ordinal);
					int written = KeyboardLayoutHelper.QueryToUnicodeEx(vk, scan, hkl, out string rawGlyph);
					string glyph = written > 0 && !string.IsNullOrEmpty(rawGlyph)
						? rawGlyph.Substring(0, 1)
						: string.Empty;
					PhysicalKeyResolver.ResolveResult resolved = PhysicalKeyResolver.ResolveForCapture(
						vk,
						scan,
						extended,
						Main.keyState);

					Candidate candidate = CorrelateCandidate(vk, scan, extended, down, tick, device);
					bool repeat = isRepeat
						|| (down && candidate.Events.Any(x =>
							x.Down && string.Equals(x.Source, source, StringComparison.Ordinal)));
					var item = new InputEvent {
						Sequence = ++_sequence,
						Tick = tick,
						Utc = DateTime.UtcNow,
						Device = device ?? "UNAVAILABLE",
						Source = source ?? "unknown",
						Down = down,
						Repeat = repeat,
						Injected = injected,
						Vk = vk,
						Scan = scan,
						Extended = extended,
						MonoGameKey = mono,
						Glyph = glyph,
						DeadKey = written < 0,
						ToUnicodeResult = written,
						GlyphAvailability = deferredFromHook
							? "deferred-from-hook-and-queried-on-main-thread-with-ToUnicodeEx"
							: "queried-with-ToUnicodeEx",
						ResolvedTier = resolved.Success ? resolved.Tier : "Unknown",
						ResolvedToken = resolved.Success ? resolved.Token : null,
						CaptureToken = resolved.Success ? resolved.Token : null,
						CaptureOk = resolved.Success,
						FailReason = resolved.Success ? null : resolved.FailReason,
						LayoutCulture = culture,
						Hkl = "0x" + unchecked((ulong)hkl.ToInt64()).ToString("X16"),
						Modifiers = SnapshotModifiers(),
						SideAndEnterResolution = DescribeSideAndEnter(vk, scan, extended),
						CandidateId = candidate.Id
					};

					Events.Add(item);
					candidate.Events.Add(item);
					candidate.Sources.Add(item.Source);
					candidate.RoutedEventCount++;
					if (string.Equals(item.Source, "WM_INPUT", StringComparison.Ordinal))
						candidate.RawEventCount++;
					if (repeat)
						candidate.RepeatCount++;
					if (!string.IsNullOrEmpty(item.ResolvedToken))
						candidate.ResolvedTokens.Add(item.ResolvedToken);
					if (down && (candidate.FirstDownTick == 0 || tick < candidate.FirstDownTick))
						candidate.FirstDownTick = tick;
					if (!down && (candidate.FirstUpTick == 0 || tick < candidate.FirstUpTick))
						candidate.FirstUpTick = tick;
					candidate.LastRouteTick = Math.Max(candidate.LastRouteTick, tick);

					if (down && !repeat)
					{
						string identity = PhysicalIdentity(item);
						PressCounts[identity] = PressCounts.TryGetValue(identity, out int count) ? count + 1 : 1;
					}
				}
			}
			catch
			{
				// Never affect input.
			}
		}

		private static Candidate CorrelateCandidate(
			uint vk,
			ushort scan,
			bool extended,
			bool down,
			long tick,
			string device)
		{
			double window = down ? RouteCorrelationMs : ReleaseCorrelationMs;
			for (int i = Candidates.Count - 1; i >= 0; i--)
			{
				Candidate candidate = Candidates[i];
				if (candidate.Scan != scan || candidate.Extended != extended)
					continue;
				if (candidate.Vk != vk && candidate.Vk != 0 && vk != 0)
					continue;
				double delta = Math.Abs(DiagnosticReportSupport.ElapsedMs(candidate.LastRouteTick, tick));
				bool heldRepeatCandidate = down
					&& candidate.FirstDownTick != 0
					&& candidate.FirstUpTick == 0;
				if (delta > window && !heldRepeatCandidate)
					continue;
				if (down && candidate.FirstUpTick != 0)
					continue;
				if (!down && candidate.FirstUpTick == 0)
					candidate.FirstUpTick = tick;
				return candidate;
			}

			var created = new Candidate {
				Id = ++_candidateSequence,
				Scan = scan,
				Extended = extended,
				Vk = vk,
				FirstDownTick = down ? tick : 0,
				FirstUpTick = down ? 0 : tick,
				LastRouteTick = tick,
				Device = device ?? "UNAVAILABLE"
			};
			Candidates.Add(created);
			return created;
		}

		private static Candidate FindMostRecentCandidate(string token)
		{
			for (int i = Candidates.Count - 1; i >= 0; i--)
			{
				Candidate c = Candidates[i];
				if (string.IsNullOrEmpty(token) || c.ResolvedTokens.Contains(token))
					return c;
			}
			return Candidates.Count == 0 ? null : Candidates[^1];
		}

		private static Candidate FindCandidateForAction(string actionId)
		{
			ComboBinding binding = BindingStorage.Get(actionId);
			if (binding != null)
			{
				for (int i = Candidates.Count - 1; i >= 0; i--)
				{
					Candidate c = Candidates[i];
					foreach (string key in binding.Keys)
						if (c.ResolvedTokens.Contains(key) || string.Equals(c.AcceptedToken, key, StringComparison.OrdinalIgnoreCase))
							return c;
				}
			}

			return Candidates.Count == 0 ? null : Candidates[^1];
		}

		private static DiagnosticRunOutcome WriteReports()
		{
			LastLivePath = WriteLiveReport();
			LastRoutePath = WriteRouteReport();
			LastTimingPath = WriteTimingReport();
			DiagnosticSummary summary = CountLiveVerdicts();
			return BuildOutcome(LastLivePath, summary);
		}

		private static string WriteLiveReport()
		{
			string path = DiagnosticReportSupport.CreatePath("AdvancedKeybinds_LiveInputDiagnostic");
			var sb = new StringBuilder(Events.Count * 340 + 8192);
			sb.AppendLine("=== C. Live Physical Input Diagnostic ===");
			DiagnosticReportSupport.AppendCommonHeader(sb, "Live Physical Input Diagnostic", _layouts);
			sb.AppendLine("OutputPath=" + DiagnosticReportSupport.PublicPath(path));
			sb.AppendLine("SessionStartedUtc=" + _sessionStartUtc.ToString("o", CultureInfo.InvariantCulture));
			sb.AppendLine("SessionDurationMs=" + DiagnosticReportSupport.ElapsedMs(_sessionStartTick, DiagnosticReportSupport.Timestamp()).ToString("0.###", CultureInfo.InvariantCulture));
			sb.AppendLine("SyntheticInputGeneratorAdded=False");
			sb.AppendLine("EmergencyStop=/akdiag live-stop (mod unload also stops observation)");
			sb.AppendLine();

			foreach (InputEvent e in Events)
			{
				sb.AppendLine("---");
				sb.AppendLine("TimestampUtc=" + e.Utc.ToString("o", CultureInfo.InvariantCulture));
				sb.AppendLine("MonotonicTimestamp=" + e.Tick);
				sb.AppendLine("EventSequence=" + e.Sequence);
				sb.AppendLine("PhysicalCandidateId=" + e.CandidateId);
				sb.AppendLine("DeviceIdentifier=" + e.Device);
				sb.AppendLine("Source=" + e.Source);
				sb.AppendLine("EventType=" + (e.Down ? "KeyDown" : "KeyUp"));
				sb.AppendLine("VirtualKey=0x" + e.Vk.ToString("X2"));
				sb.AppendLine("ScanCode=0x" + e.Scan.ToString("X2"));
				sb.AppendLine("Extended=" + e.Extended);
				sb.AppendLine("MonoGameKeys=" + e.MonoGameKey);
				sb.AppendLine("Glyph=" + DiagnosticReportSupport.EscapeSingleGlyph(e.Glyph));
				sb.AppendLine("DeadKey=" + e.DeadKey);
				sb.AppendLine("ToUnicodeExResult=" + (e.ToUnicodeResult?.ToString(CultureInfo.InvariantCulture) ?? "UNAVAILABLE"));
				sb.AppendLine("GlyphAvailability=" + e.GlyphAvailability);
				sb.AppendLine("ResolvedTier=" + e.ResolvedTier);
				sb.AppendLine("ResolvedToken=" + (e.ResolvedToken ?? "-"));
				sb.AppendLine("CaptureToken=" + (e.CaptureToken ?? "-"));
				sb.AppendLine("CaptureOk=" + e.CaptureOk);
				sb.AppendLine("FailReason=" + (e.FailReason ?? "-"));
				sb.AppendLine("ActiveLayout=" + e.LayoutCulture);
				sb.AppendLine("HKL=" + e.Hkl);
				sb.AppendLine("Modifiers=" + e.Modifiers);
				sb.AppendLine("SideAndEnterResolution=" + e.SideAndEnterResolution);
				sb.AppendLine("AutoRepeat=" + e.Repeat);
				sb.AppendLine("SyntheticInput=" + e.Injected);
				sb.AppendLine("Injected=" + e.Injected);
			}

			AppendLiveSummary(sb);
			DiagnosticReportSupport.WriteAllText(path, sb.ToString());
			return path;
		}

		private static string WriteRouteReport()
		{
			string path = DiagnosticReportSupport.CreatePath("AdvancedKeybinds_InputRouteDiagnostic");
			var sb = new StringBuilder(Candidates.Count * 360 + 4096);
			sb.AppendLine("=== D. Input Route Diagnostic ===");
			DiagnosticReportSupport.AppendCommonHeader(sb, "Input Route Diagnostic", _layouts);
			sb.AppendLine("OutputPath=" + DiagnosticReportSupport.PublicPath(path));
			sb.AppendLine("CorrelationWindowMs=" + RouteCorrelationMs);
			sb.AppendLine("ActionExecutionScope=AdvancedKeybinds JustPressed pulse published to PlayerInput; downstream game/mod handler execution is not observable");
			sb.AppendLine();

			foreach (Candidate c in Candidates)
			{
				DiagnosticVerdict verdict = RouteVerdict(c, out string reason, out string routeClassification);
				bool duplicateSuppressionWorked = IsDuplicateSuppressionWorked(c);
				sb.AppendLine("---");
				sb.AppendLine("PhysicalCandidateId=" + c.Id);
				sb.AppendLine("Identity=scan:0x" + c.Scan.ToString("X2") + ",extended:" + c.Extended + ",vk:0x" + c.Vk.ToString("X2"));
				sb.AppendLine("DeviceIdentifier=" + c.Device);
				sb.AppendLine("Sources=" + string.Join(",", c.Sources));
				sb.AppendLine("RawEventCount=" + c.RawEventCount);
				sb.AppendLine("RoutedEventCount=" + c.RoutedEventCount);
				sb.AppendLine("AcceptedCaptureCount=" + FormatObservedCount(c.AcceptedCaptureCount, IsCaptureCountObserved(c)));
				sb.AppendLine("ActionExecutionCount=" + FormatObservedCount(c.ActionExecutionCount, IsActionCountObserved(c)));
				sb.AppendLine("SameKeyDownCount=" + c.Events.Count(e => e.Down));
				sb.AppendLine("ActionPulseCounts=" + string.Join(",", c.ActionPulseCounts.Select(x => x.Key + ":" + x.Value)));
				sb.AppendLine("DuplicateSuppressionWorked=" + duplicateSuppressionWorked);
				sb.AppendLine("DuplicateSuppressionReason=edge/held sets and one-frame JustPressed; downstream execution unavailable");
				sb.AppendLine("RouteDeltasMs=" + FormatRouteDeltas(c));
				sb.AppendLine("Verdict=" + FormatRouteVerdict(verdict, routeClassification));
				sb.AppendLine("Reason=" + reason);
			}

			AppendRouteSummary(sb);
			DiagnosticReportSupport.WriteAllText(path, sb.ToString());
			return path;
		}

		private static string WriteTimingReport()
		{
			string path = DiagnosticReportSupport.CreatePath("AdvancedKeybinds_TimingDiagnostic");
			long reportTick = DiagnosticReportSupport.Timestamp();
			HashSet<Keys> keysDownAtReport = SnapshotKeysDown();
			var sb = new StringBuilder(Candidates.Count * 360 + 4096);
			sb.AppendLine("=== E. Timing Diagnostic ===");
			DiagnosticReportSupport.AppendCommonHeader(sb, "Timing Diagnostic", _layouts);
			sb.AppendLine("OutputPath=" + DiagnosticReportSupport.PublicPath(path));
			sb.AppendLine("Clock=Stopwatch.GetTimestamp monotonic high-resolution");
			sb.AppendLine("MeasurementBoundary=Windows/hook delivery to MOD capture/trigger; keyboard USB/Bluetooth and pre-hook OS latency are not measured");
			sb.AppendLine("SessionStartBoundaryGraceMs=" + SessionStartBoundaryGraceMs.ToString("0.###", CultureInfo.InvariantCulture));
			sb.AppendLine();

			foreach (Candidate c in Candidates)
			{
				DiagnosticVerdict verdict = TimingVerdict(
					c,
					reportTick,
					keysDownAtReport,
					out string reason,
					out string boundaryClassification,
					out string boundaryEvidence);
				long firstRoute = c.Events.Count == 0 ? 0 : c.Events.Min(e => e.Tick);
				sb.AppendLine("---");
				sb.AppendLine("PhysicalCandidateId=" + c.Id);
				sb.AppendLine("KeyDownTimestamp=" + c.FirstDownTick);
				sb.AppendLine("KeyUpTimestamp=" + c.FirstUpTick);
				sb.AppendLine("SessionBoundaryClassification=" + boundaryClassification);
				sb.AppendLine("SessionBoundaryEvidence=" + boundaryEvidence);
				sb.AppendLine("PressDurationMs=" + (c.FirstDownTick != 0 && c.FirstUpTick != 0
					? DiagnosticReportSupport.ElapsedMs(c.FirstDownTick, c.FirstUpTick).ToString("0.###", CultureInfo.InvariantCulture)
					: "UNAVAILABLE"));
				sb.AppendLine("FirstRouteTimestamp=" + firstRoute);
				sb.AppendLine("RouteDeltasMs=" + FormatRouteDeltas(c));
				sb.AppendLine("CaptureAdoptionLatencyMs=" + (c.FirstCaptureTick != 0 && firstRoute != 0
					? DiagnosticReportSupport.ElapsedMs(firstRoute, c.FirstCaptureTick).ToString("0.###", CultureInfo.InvariantCulture)
					: "UNAVAILABLE(no accepted capture timestamp correlation)"));
				sb.AppendLine("ActionTriggerPulseLatencyMs=" + (c.FirstActionTick != 0 && firstRoute != 0
					? DiagnosticReportSupport.ElapsedMs(firstRoute, c.FirstActionTick).ToString("0.###", CultureInfo.InvariantCulture)
					: "UNAVAILABLE(no AdvancedKeybinds trigger pulse correlated)"));
				sb.AppendLine("ActionExecutionLatencyMs=UNAVAILABLE(exact downstream handler execution not observable)");
				sb.AppendLine("AutoRepeatCount=" + c.RepeatCount);
				sb.AppendLine("AutoRepeatIntervalsMs=" + FormatRepeatIntervals(c));
				sb.AppendLine("HoldObservations=" + (c.HoldObservations.Count == 0 ? "none" : string.Join(" || ", c.HoldObservations)));
				sb.AppendLine("Verdict=" + FormatTimingVerdict(verdict, boundaryClassification));
				sb.AppendLine("Reason=" + reason);
			}

			AppendTimingSummary(sb, reportTick, keysDownAtReport);
			DiagnosticReportSupport.WriteAllText(path, sb.ToString());
			return path;
		}

		private static void AppendLiveSummary(StringBuilder sb)
		{
			DiagnosticSummary summary = CountLiveVerdicts();
			sb.AppendLine();
			sb.AppendLine("=== SUMMARY ===");
			summary.AppendTo(sb);
			sb.AppendLine("=== DETECTED KEYS / PRESS COUNTS ===");
			foreach (KeyValuePair<string, int> pair in PressCounts.OrderBy(x => x.Key, StringComparer.Ordinal))
				sb.AppendLine(pair.Key + " Count=" + pair.Value);
			sb.AppendLine("=== KEYDOWN ONLY ===");
			foreach (Candidate c in Candidates.Where(x => x.FirstDownTick != 0 && x.FirstUpTick == 0))
				sb.AppendLine("WARNING | candidate=" + c.Id + " scan=0x" + c.Scan.ToString("X2") + " ext=" + c.Extended);
			sb.AppendLine("=== KEYUP ONLY ===");
			foreach (Candidate c in Candidates.Where(x => x.FirstDownTick == 0 && x.FirstUpTick != 0))
				sb.AppendLine("WARNING | candidate=" + c.Id + " scan=0x" + c.Scan.ToString("X2") + " ext=" + c.Extended);
			sb.AppendLine("=== SPLIT PHYSICAL KEYS ===");
			foreach (Candidate c in Candidates.Where(x => x.ResolvedTokens.Count > 1))
				sb.AppendLine("WARNING | candidate=" + c.Id + " tokens=[" + string.Join(",", c.ResolvedTokens) + "]");
			sb.AppendLine("=== MULTIPLE PHYSICAL POSITIONS TO SAME INTERNAL VALUE CANDIDATES ===");
			foreach (IGrouping<string, Candidate> group in Candidates
				.SelectMany(c => c.ResolvedTokens.Select(t => (Token: t, Candidate: c)))
				.GroupBy(x => x.Token, x => x.Candidate, StringComparer.OrdinalIgnoreCase)
				.Where(g => g.Select(c => c.Scan.ToString("X2") + ":" + c.Extended).Distinct().Count() > 1))
				sb.AppendLine("WARNING | token=" + group.Key + " candidates=[" + string.Join(",", group.Select(c => c.Id)) + "]");
			sb.AppendLine("=== UNTESTED INTERNAL KEYS ===");
			var seen = new HashSet<string>(Candidates.SelectMany(c => c.ResolvedTokens), StringComparer.OrdinalIgnoreCase);
			foreach (string key in AllKeyDiagnosticCore.EnumerateDiagnosticTargets())
				if (!seen.Contains(key))
					sb.AppendLine("UNTESTED | " + key + " | reason=no corresponding live physical event observed; key may not exist on this keyboard");
			sb.AppendLine("=== UNAVAILABLE ===");
			sb.AppendLine("UNAVAILABLE | Fn | reason=no independent OS event observed/commonly not exposed");
		}

		private static void AppendRouteSummary(StringBuilder sb)
		{
			DiagnosticSummary summary = CountRouteVerdicts();
			int limitedObservationCount = Candidates.Count(IsLimitedObservation);
			sb.AppendLine();
			sb.AppendLine("=== SUMMARY ===");
			sb.AppendLine("Total=" + summary.Total);
			sb.AppendLine("PASS=" + summary.Get(DiagnosticVerdict.Pass));
			sb.AppendLine("INFO=" + Math.Max(0, summary.Get(DiagnosticVerdict.Info) - limitedObservationCount));
			sb.AppendLine("LIMITED_OBSERVATION=" + limitedObservationCount);
			sb.AppendLine("EXPECTEDDIFFERENCE=" + summary.Get(DiagnosticVerdict.ExpectedDifference));
			sb.AppendLine("WARNING=" + summary.Get(DiagnosticVerdict.Warning));
			sb.AppendLine("FAIL=" + summary.Get(DiagnosticVerdict.Fail));
			sb.AppendLine("SKIPPED=" + summary.Get(DiagnosticVerdict.Skipped));
			sb.AppendLine("UNTESTED=" + summary.Get(DiagnosticVerdict.Untested));
			sb.AppendLine("UNAVAILABLE=" + summary.Get(DiagnosticVerdict.Unavailable));
			sb.AppendLine("=== INPUT ROUTE DUPLICATES ===");
			foreach (Candidate c in Candidates.Where(x => x.Sources.Count > 1))
				sb.AppendLine("INFO | candidate=" + c.Id + " routes=[" + string.Join(",", c.Sources) + "] routed=" + c.RoutedEventCount);
			sb.AppendLine("=== LIMITED OBSERVATION ===");
			foreach (Candidate c in Candidates.Where(IsLimitedObservation))
				sb.AppendLine(
					"LIMITED_OBSERVATION | candidate=" + c.Id
					+ " AcceptedCaptureCount=" + FormatObservedCount(c.AcceptedCaptureCount, IsCaptureCountObserved(c))
					+ " ActionExecutionCount=" + FormatObservedCount(c.ActionExecutionCount, IsActionCountObserved(c)));
			sb.AppendLine("=== DOUBLE EXECUTION CANDIDATES ===");
			foreach (Candidate c in Candidates.Where(x => x.AcceptedCaptureCount > 1 || x.ActionExecutionCount > 1 || HasDuplicateAction(x)))
				sb.AppendLine("FAIL | candidate=" + c.Id + " captures=" + c.AcceptedCaptureCount + " actions=" + c.ActionExecutionCount);
			sb.AppendLine("=== MEASUREMENT UNAVAILABLE ===");
			sb.AppendLine("UNAVAILABLE | downstream handler execution | only PlayerInput trigger publication is observable");
		}

		private static void AppendTimingSummary(
			StringBuilder sb,
			long reportTick,
			HashSet<Keys> keysDownAtReport)
		{
			DiagnosticSummary summary = CountTimingVerdicts(reportTick, keysDownAtReport);
			int possibleBoundaryCount = Candidates.Count(c =>
				IsPossibleSessionBoundary(c, reportTick, keysDownAtReport));
			sb.AppendLine();
			sb.AppendLine("=== SUMMARY ===");
			sb.AppendLine("Total=" + summary.Total);
			sb.AppendLine("PASS=" + summary.Get(DiagnosticVerdict.Pass));
			sb.AppendLine("INFO=" + Math.Max(0, summary.Get(DiagnosticVerdict.Info) - possibleBoundaryCount));
			sb.AppendLine("POSSIBLE_SESSION_BOUNDARY=" + possibleBoundaryCount);
			sb.AppendLine("EXPECTEDDIFFERENCE=" + summary.Get(DiagnosticVerdict.ExpectedDifference));
			sb.AppendLine("WARNING=" + summary.Get(DiagnosticVerdict.Warning));
			sb.AppendLine("FAIL=" + summary.Get(DiagnosticVerdict.Fail));
			sb.AppendLine("SKIPPED=" + summary.Get(DiagnosticVerdict.Skipped));
			sb.AppendLine("UNTESTED=" + summary.Get(DiagnosticVerdict.Untested));
			sb.AppendLine("UNAVAILABLE=" + summary.Get(DiagnosticVerdict.Unavailable));
			sb.AppendLine("=== SESSION BOUNDARY ===");
			foreach (Candidate c in Candidates)
			{
				DiagnosticVerdict verdict = TimingVerdict(
					c,
					reportTick,
					keysDownAtReport,
					out string reason,
					out string boundaryClassification,
					out string boundaryEvidence);
				if (verdict == DiagnosticVerdict.Info)
					sb.AppendLine(
						FormatTimingVerdict(verdict, boundaryClassification) + " | candidate=" + c.Id
						+ " classification=" + boundaryClassification
						+ " evidence=" + boundaryEvidence
						+ " reason=" + reason);
			}
			sb.AppendLine("=== MEASUREMENT UNAVAILABLE ===");
			sb.AppendLine("UNAVAILABLE | USB/Bluetooth device-internal latency");
			sb.AppendLine("UNAVAILABLE | time before Windows/hook delivery");
			sb.AppendLine("UNAVAILABLE | exact downstream action handler completion");
		}

		private static DiagnosticSummary CountLiveVerdicts()
		{
			var summary = new DiagnosticSummary();
			foreach (Candidate c in Candidates)
			{
				if (c.FirstDownTick == 0 || c.FirstUpTick == 0 || c.ResolvedTokens.Count > 1)
					summary.Add(DiagnosticVerdict.Warning);
				else
					summary.Add(DiagnosticVerdict.Pass);
			}

			var seen = new HashSet<string>(Candidates.SelectMany(c => c.ResolvedTokens), StringComparer.OrdinalIgnoreCase);
			foreach (string key in AllKeyDiagnosticCore.EnumerateDiagnosticTargets())
				if (!seen.Contains(key))
					summary.Add(DiagnosticVerdict.Untested);
			summary.Add(DiagnosticVerdict.Unavailable); // Fn
			return summary;
		}

		private static DiagnosticSummary CountRouteVerdicts()
		{
			var summary = new DiagnosticSummary();
			foreach (Candidate c in Candidates)
			{
				DiagnosticVerdict verdict = RouteVerdict(c, out _, out _);
				summary.Add(verdict);
			}
			summary.Add(DiagnosticVerdict.Unavailable);
			return summary;
		}

		private static DiagnosticSummary CountTimingVerdicts()
			=> CountTimingVerdicts(DiagnosticReportSupport.Timestamp(), SnapshotKeysDown());

		private static DiagnosticSummary CountTimingVerdicts(
			long reportTick,
			HashSet<Keys> keysDownAtReport)
		{
			var summary = new DiagnosticSummary();
			foreach (Candidate c in Candidates)
				summary.Add(TimingVerdict(c, reportTick, keysDownAtReport, out _, out _, out _));
			summary.Add(DiagnosticVerdict.Unavailable);
			summary.Add(DiagnosticVerdict.Unavailable);
			summary.Add(DiagnosticVerdict.Unavailable);
			return summary;
		}

		private static DiagnosticVerdict RouteVerdict(
			Candidate c,
			out string reason,
			out string routeClassification)
		{
			bool duplicateSuppressionWorked = IsDuplicateSuppressionWorked(c);
			if (c.AcceptedCaptureCount > 1 || c.ActionExecutionCount > 1 || HasDuplicateAction(c))
			{
				routeClassification = "NONE";
				reason = "physical-candidate-produced-multiple-captures-or-trigger-pulses";
				return DiagnosticVerdict.Fail;
			}

			if (!duplicateSuppressionWorked)
			{
				routeClassification = "NONE";
				reason = "duplicate-suppression-did-not-keep-single-capture-or-action";
				return DiagnosticVerdict.Warning;
			}

			if (!IsCaptureCountObserved(c) && !IsActionCountObserved(c))
			{
				routeClassification = "LIMITED_OBSERVATION";
				reason = "capture-and-action-execution-not-observed; no-duplicate-execution-confirmed; diagnostic-observation-limit";
				return DiagnosticVerdict.Info;
			}

			if (c.Sources.Count > 1)
			{
				routeClassification = "NONE";
				reason = "multiple-routes-observed; no-duplicate-capture-or-action-confirmed";
				return DiagnosticVerdict.Pass;
			}

			routeClassification = "NONE";
			reason = "single-route-observed; no duplicate capture/trigger-pulse";
			return DiagnosticVerdict.Pass;
		}

		private static bool IsDuplicateSuppressionWorked(Candidate candidate)
			=> candidate.AcceptedCaptureCount <= 1
				&& candidate.ActionExecutionCount <= 1
				&& !HasDuplicateAction(candidate);

		private static string FormatRouteVerdict(
			DiagnosticVerdict verdict,
			string routeClassification)
			=> string.Equals(routeClassification, "LIMITED_OBSERVATION", StringComparison.Ordinal)
				? "LIMITED_OBSERVATION"
				: verdict.ToString().ToUpperInvariant();

		private static bool IsLimitedObservation(Candidate candidate)
		{
			_ = RouteVerdict(candidate, out _, out string routeClassification);
			return string.Equals(routeClassification, "LIMITED_OBSERVATION", StringComparison.Ordinal);
		}

		private static DiagnosticVerdict TimingVerdict(
			Candidate c,
			long reportTick,
			HashSet<Keys> keysDownAtReport,
			out string reason,
			out string boundaryClassification,
			out string boundaryEvidence)
		{
			if (c.FirstDownTick != 0 && c.FirstUpTick != 0 && c.FirstUpTick < c.FirstDownTick)
			{
				boundaryClassification = "NONE";
				boundaryEvidence = "keyup-timestamp-precedes-keydown-timestamp";
				reason = "input-timeline-is-chronologically-invalid";
				return DiagnosticVerdict.Fail;
			}

			if (c.FirstDownTick == 0)
			{
				bool? downAtStart = IsCandidateDown(c, KeysDownAtSessionStart);
				double sinceStartMs = DiagnosticReportSupport.ElapsedMs(_sessionStartTick, c.FirstUpTick);
				if (downAtStart == true || (sinceStartMs >= 0 && sinceStartMs <= SessionStartBoundaryGraceMs))
				{
					boundaryClassification = "SESSION_BOUNDARY";
					boundaryEvidence = downAtStart == true
						? "key-was-down-at-session-start"
						: "keyup-observed-within-start-grace-ms:" + sinceStartMs.ToString("0.###", CultureInfo.InvariantCulture);
					reason = "keyup-only-is-consistent-with-a-key-pressed-before-diagnostic-start";
					return DiagnosticVerdict.Info;
				}

				boundaryClassification = "POSSIBLE_SESSION_BOUNDARY";
				boundaryEvidence = downAtStart.HasValue
					? "key-was-not-down-at-session-start"
					: "start-state-unavailable-and-keyup-not-within-start-grace";
				reason = "keyup-without-keydown; session-boundary-or-missing-source-event-cannot-be-distinguished";
				return DiagnosticVerdict.Info;
			}
			if (c.FirstUpTick == 0)
			{
				bool? downAtReport = IsCandidateDown(c, keysDownAtReport);
				if (downAtReport != false)
				{
					double openDurationMs = DiagnosticReportSupport.ElapsedMs(c.FirstDownTick, reportTick);
					boundaryClassification = "SESSION_BOUNDARY";
					boundaryEvidence = downAtReport == true
						? "key-remained-down-at-diagnostic-end-or-snapshot; open-duration-ms:"
							+ openDurationMs.ToString("0.###", CultureInfo.InvariantCulture)
						: "end-state-unavailable; missing-keyup-cannot-be-distinguished-from-release-after-diagnostic-end; open-duration-ms:"
							+ openDurationMs.ToString("0.###", CultureInfo.InvariantCulture);
					reason = "keydown-only-is-consistent-with-a-key-released-after-diagnostic-end";
					return DiagnosticVerdict.Info;
				}

				boundaryClassification = "POSSIBLE_SESSION_BOUNDARY";
				boundaryEvidence = "key-was-up-at-diagnostic-end-or-snapshot; keyup-event-may-have-been-lost";
				reason = "keydown-without-keyup; session-boundary-or-missing-source-event-cannot-be-distinguished";
				return DiagnosticVerdict.Info;
			}

			boundaryClassification = "NONE";
			boundaryEvidence = "paired-keydown-and-keyup-within-session";
			reason = "keydown-and-keyup-paired; post-delivery duration measured";
			return DiagnosticVerdict.Pass;
		}

		private static string FormatTimingVerdict(
			DiagnosticVerdict verdict,
			string boundaryClassification)
			=> string.Equals(
				boundaryClassification,
				"POSSIBLE_SESSION_BOUNDARY",
				StringComparison.Ordinal)
				? "POSSIBLE_SESSION_BOUNDARY"
				: verdict.ToString().ToUpperInvariant();

		private static bool IsPossibleSessionBoundary(
			Candidate candidate,
			long reportTick,
			HashSet<Keys> keysDownAtReport)
		{
			_ = TimingVerdict(
				candidate,
				reportTick,
				keysDownAtReport,
				out _,
				out string boundaryClassification,
				out _);
			return string.Equals(
				boundaryClassification,
				"POSSIBLE_SESSION_BOUNDARY",
				StringComparison.Ordinal);
		}

		private static bool HasDuplicateAction(Candidate candidate)
			=> candidate.ActionPulseCounts.Values.Any(count => count > 1);

		private static bool IsCaptureCountObserved(Candidate candidate)
			=> candidate.CaptureDecisionObserved || candidate.AcceptedCaptureCount > 0;

		private static bool IsActionCountObserved(Candidate candidate)
			=> candidate.ActionExecutionCount > 0;

		private static string FormatObservedCount(int count, bool observed)
			=> observed ? count.ToString(CultureInfo.InvariantCulture) : "NOT_OBSERVED";

		private static HashSet<Keys> SnapshotKeysDown()
			=> new(Main.keyState.GetPressedKeys());

		private static bool? IsCandidateDown(Candidate candidate, HashSet<Keys> keysDown)
		{
			if (candidate.Vk == 0 || !Enum.IsDefined(typeof(Keys), (int)candidate.Vk))
				return null;

			Keys key = (Keys)candidate.Vk;
			return key == Keys.None ? null : keysDown.Contains(key);
		}

		private static string FormatRouteDeltas(Candidate c)
		{
			if (c.Events.Count < 2)
				return "none";
			long first = c.Events.Min(e => e.Tick);
			return string.Join(
				",",
				c.Events.OrderBy(e => e.Tick).Select(e =>
					e.Source + ":" + DiagnosticReportSupport.ElapsedMs(first, e.Tick).ToString("0.###", CultureInfo.InvariantCulture)));
		}

		private static string FormatRepeatIntervals(Candidate c)
		{
			List<InputEvent> repeats = c.Events.Where(e => e.Down && e.Repeat).OrderBy(e => e.Tick).ToList();
			if (repeats.Count == 0)
				return "none";
			var values = new List<string>();
			long previous = c.FirstDownTick;
			foreach (InputEvent repeat in repeats)
			{
				values.Add(DiagnosticReportSupport.ElapsedMs(previous, repeat.Tick).ToString("0.###", CultureInfo.InvariantCulture));
				previous = repeat.Tick;
			}
			return string.Join(",", values);
		}

		private static string FindCulture(IntPtr hkl)
		{
			if (_layouts != null)
				foreach (KeyboardLayoutHelper.InstalledLayoutInfo layout in _layouts)
					if (layout.Hkl == hkl)
						return layout.CultureName;
			return "unknown";
		}

		private static string SnapshotModifiers()
		{
			KeyboardState state = Main.keyState;
			return "LShift=" + state.IsKeyDown(Keys.LeftShift)
				+ ",RShift=" + state.IsKeyDown(Keys.RightShift)
				+ ",LCtrl=" + state.IsKeyDown(Keys.LeftControl)
				+ ",RCtrl=" + state.IsKeyDown(Keys.RightControl)
				+ ",LAlt=" + state.IsKeyDown(Keys.LeftAlt)
				+ ",RAlt=" + state.IsKeyDown(Keys.RightAlt)
				+ ",LWin=" + WindowsKeySuppressor.IsLeftWindowsDown
				+ ",RWin=" + WindowsKeySuppressor.IsRightWindowsDown
				+ ",Caps=" + WindowsKeySuppressor.IsCapsLockPhysicallyDown
				+ ",Num=" + WindowsKeySuppressor.IsNumLockPhysicallyDown
				+ ",Scroll=" + WindowsKeySuppressor.IsScrollLockPhysicallyDown;
		}

		private static string PhysicalIdentity(InputEvent e)
			=> "scan=0x" + e.Scan.ToString("X2")
				+ ",extended=" + e.Extended
				+ ",vk=0x" + e.Vk.ToString("X2")
				+ ",token=" + (e.ResolvedToken ?? "-");

		private static string DescribeSideAndEnter(uint vk, ushort scan, bool extended)
			=> vk switch {
				0x10 => "UNKNOWN_SIDE(VK_SHIFT generic; scan=0x" + scan.ToString("X2") + ")",
				0x11 => extended ? "RightControlByExtended" : "LeftControlByNonExtended",
				0x12 => extended ? "RightAltByExtended" : "LeftAltByNonExtended",
				0xA0 => "LeftShift",
				0xA1 => "RightShift",
				0xA2 => "LeftControl",
				0xA3 => "RightControl",
				0xA4 => "LeftAlt",
				0xA5 => "RightAlt",
				0x0D => extended ? "NumpadEnterByExtended" : "MainEnterByNonExtended",
				_ => "not-applicable"
			};

		private static DiagnosticRunOutcome BuildOutcome(string path, DiagnosticSummary summary)
			=> new() {
				Ok = true,
				ReportPath = path,
				Message = "ok",
				Total = summary.Total,
				Pass = summary.Get(DiagnosticVerdict.Pass),
				Info = summary.Get(DiagnosticVerdict.Info),
				ExpectedDifference = summary.Get(DiagnosticVerdict.ExpectedDifference),
				Warning = summary.Get(DiagnosticVerdict.Warning),
				Fail = summary.Get(DiagnosticVerdict.Fail),
				Skipped = summary.Get(DiagnosticVerdict.Skipped),
				Untested = summary.Get(DiagnosticVerdict.Untested),
				Unavailable = summary.Get(DiagnosticVerdict.Unavailable)
			};
	}
}
