using System;
using System.Collections.Generic;
using AdvancedKeybinds.Core;
using AdvancedKeybinds.Diagnostics;
using AdvancedKeybinds.Patches;
using Microsoft.Xna.Framework.Input;
using Terraria;

namespace AdvancedKeybinds.Binding
{
	/// <summary>
	/// Captures a single key or mouse button for one combo slot.
	/// Input acquisition (FNA / hooks / latches) is separate from register decisions
	/// (<see cref="RegisterCaptureSession"/>): start-held keys/mouse must fully release
	/// before any Up竊奪own edge is accepted. No timed warmup for acceptance.
	/// </summary>
	internal static class SlotCapture
	{
		public enum PollResult
		{
			Waiting,
			Captured
		}

		public static bool IsCapturing => _session.CurrentPhase is RegisterCaptureSession.Phase.WaitingForRelease
			or RegisterCaptureSession.Phase.WaitingForNewInput
			or RegisterCaptureSession.Phase.RecoveringFromInvalidState;

		private static readonly RegisterCaptureSession _session = new();
		private static readonly CaptureLatencyClock _latency = new();
		private static bool _wasInactiveDuringCapture;
		/// <summary>Previous-frame JP OemTilde-masquerade-as-hankaku bit (edge only on false竊稚rue).</summary>
		private static bool _prevHankakuMasqueradeDown;
		private static RegisterCaptureSession.Phase _prevPhaseForLatency;
		private static IntPtr _captureLayoutAtBegin;
		private static bool _prevImeComposingDuringCapture;

		private static RegisterCaptureSession.Phase _prevPhaseForDig;
		private static string _prevBlockedDig = "";
		private static string _prevReasonDig = "";
		private static string _prevNlsDig = "";

		/// <summary>Previous accepted token 窶・next Begin logs whether blocked cleared after that accept.</summary>
		private static string _lastAcceptedToken;
		private static int _beginSeq;
		private static int _waitingReleasePollCount;
		private static int _waitingReleaseUniqueInputTicks;
		private static int _waitingReleaseLastInputTick = int.MinValue;
		private static long _waitingReleaseRemainingSinceMs;
		private static string _waitingReleaseRemainingKey = "";
		private static bool _stuckReleaseWarned;
		private static int _rejectStreak;
		private static bool _rejectStreakWarned;
		private const int RejectStreakWarnCount = 45;

		/// <summary>AKDIAG / tests: latest capture latency markers (T0–T8).</summary>
		internal static CaptureLatencyClock LatencyClock => _latency;

		/// <summary>Diag-only: current session phase name (no side effects).</summary>
		internal static string DiagCapturePhase => _session.CurrentPhase.ToString();

		/// <summary>Diag-only: blocked-at-begin remaining tokens.</summary>
		internal static string DiagBlockedAtBegin => string.Join(",", _session.BlockedAtBegin);

		/// <summary>Diag-only: last accepted token (may be null after Cancel).</summary>
		internal static string DiagAcceptedToken => _session.AcceptedToken;

		/// <param name="forceBlockMouse1">
		/// When true (slot opened with left-click), always treat Mouse1 as start-held even if the
		/// FNA/OS snapshot races and misses it for one frame 窶・open-click must not ACCEPTED.
		/// </param>
		public static void Begin(bool forceBlockMouse1 = false)
		{
			// Drop stale latches from before the click 窶・they are not a fresh Up竊奪own.
			WindowsKeySuppressor.DiscardAllPendingCaptureEdges();
			WindowsKeySuppressor.SyncWindowsKeyStateFromOs();
			WindowsKeySuppressor.SetSlotCapturePassthrough(true);

			_prevHankakuMasqueradeDown = IsJpHankakuMasqueradeDown();
			var snapBegin = FnaGamePadProvider.Current;
			GamePadStickCaptureArming.BeginSession(
				snapBegin.LeftStickX, snapBegin.LeftStickY,
				snapBegin.RightStickX, snapBegin.RightStickY);
			HashSet<string> heldAtBegin = SnapshotHeldForCapture();
			// Slot open is always a left-click: never let a missed Mouse1 snapshot skip WaitingForRelease.
			if (forceBlockMouse1 || MouseInputHelper.IsPhysicalLeftDown())
				heldAtBegin.Add("Mouse1");
			_latency.Reset();
			_latency.MarkT0_Click();
			_session.Begin(heldAtBegin);
			_prevPhaseForLatency = _session.CurrentPhase;
			_captureLayoutAtBegin = KeyboardLayoutHelper.GetActiveLayoutHandle();
			_prevImeComposingDuringCapture = IsImeComposingActive();
			if (_session.CurrentPhase == RegisterCaptureSession.Phase.WaitingForRelease)
				_latency.MarkT1_WaitingForRelease();
			else if (_session.CurrentPhase == RegisterCaptureSession.Phase.WaitingForNewInput)
			{
				_latency.MarkT1_WaitingForRelease();
				_latency.MarkT2_AllReleased();
				_latency.MarkT3_WaitingForNewInput();
			}

			_wasInactiveDuringCapture = !Main.hasFocus;
			_waitingReleasePollCount = 0;
			_waitingReleaseUniqueInputTicks = 0;
			_waitingReleaseLastInputTick = int.MinValue;
			_waitingReleaseRemainingSinceMs = 0;
			_waitingReleaseRemainingKey = "";
			_stuckReleaseWarned = false;
			_rejectStreak = 0;
			_rejectStreakWarned = false;
			_beginSeq++;
			MouseInputHelper.SyncScrollWheelBaseline();
			KeyToken.ResetWin32SupplementCaptureDiag();
			// Dig / CaptureResponsiveness / JisOem: call-site gated only (no dig work when OFF).
			if (AkDiag.Enabled)
			{
				CaptureResponsivenessDiag.Begin(heldAtBegin, _session.CurrentPhase);
				DigBegin(heldAtBegin);
			}
		}

		public static void Cancel()
		{
			if (AkDiag.Enabled)
				CaptureResponsivenessDiag.Cancel("SlotCapture.Cancel");
			if (JisOemInGameDiagnostic.IsSessionActive)
				JisOemInGameDiagnostic.EndCaptureSessionAndFlush("SlotCapture.Cancel");
			_session.Cancel();
			GamePadStickCaptureArming.EndSession();
			_wasInactiveDuringCapture = false;
			_prevHankakuMasqueradeDown = false;
			_prevPhaseForLatency = RegisterCaptureSession.Phase.Inactive;
			_captureLayoutAtBegin = IntPtr.Zero;
			_prevImeComposingDuringCapture = false;
			_waitingReleasePollCount = 0;
			_waitingReleaseUniqueInputTicks = 0;
			_waitingReleaseLastInputTick = int.MinValue;
			_waitingReleaseRemainingSinceMs = 0;
			_stuckReleaseWarned = false;
			_rejectStreak = 0;
			WindowsKeySuppressor.SetSlotCapturePassthrough(false);
			DrainAllLatchEdges();
			if (AkDiag.Enabled)
				DigPhase("Cancel");
		}

		/// <summary>
		/// Drops latch edges that arrived while Terraria was inactive so focus-loss cannot complete capture.
		/// </summary>
		public static void DiscardPendingEdgesWhileInactive()
		{
			if (!IsCapturing)
				return;

			_wasInactiveDuringCapture = true;
			DrainAllLatchEdges();
			MouseInputHelper.SyncScrollWheelBaseline();
			if (AkDiag.Enabled)
				AkDiag.Log("SlotCapture discard latches while inactive");
		}

		private static void DrainAllLatchEdges()
		{
			WindowsKeySuppressor.TryConsumeWinKeyCapture(out _);
			WindowsKeySuppressor.TryConsumeEscChordCapture(out _);
			WindowsKeySuppressor.TryConsumeToggleLockPress(out _);
			WindowsKeySuppressor.TryConsumeKanaCapture(out _);
			WindowsKeySuppressor.TryConsumeHankakuZenkakuCapture(out _);
			WindowsKeySuppressor.TryConsumeJisIntlCapture(out _);
			WindowsKeySuppressor.TryConsumePhysicalCapture(out _);
		}

		/// <summary>
		/// After focus returns, fold currently held keys into a fresh "must release" set by restarting
		/// the release wait with an updated begin snapshot (panel stays in purple wait).
		/// </summary>
		private static void AbsorbPressedIntoBaselineAfterFocusRegain()
		{
			if (!_wasInactiveDuringCapture)
				return;

			_wasInactiveDuringCapture = false;
			DrainAllLatchEdges();

			if (_session.CurrentPhase == RegisterCaptureSession.Phase.InputAccepted
				|| _session.CurrentPhase == RegisterCaptureSession.Phase.Inactive)
				return;

			_prevHankakuMasqueradeDown = IsJpHankakuMasqueradeDown();
			HashSet<string> held = SnapshotHeldForCapture();
			_session.Begin(held);
			if (AkDiag.Enabled)
			{
				CaptureResponsivenessDiag.Begin(held, _session.CurrentPhase);
				if (AkDiag.Enabled)
				{
					AkDiag.Log(
					"SlotCapture focus-regain re-begin blocked=[" + string.Join(",", held) + "]"
					+ " phase=" + _session.CurrentPhase);
				}
				DigPhase("focus-regain");
			}
		}

		public static PollResult Poll(out string token)
		{
			token = null;
			if (!IsCapturing)
				return PollResult.Waiting;

			try
			{
				return PollCore(out token);
			}
			catch (Exception ex)
			{
				if (AkDiag.Enabled)
				{
					AkDiag.Log(
						"SlotCapture Poll exception type=" + ex.GetType().Name
						+ " msg=" + ex.Message
						+ " -> RecoveringFromInvalidState");
				}

				RecoverAfterException();
				token = null;
				return PollResult.Waiting;
			}
		}

		private static PollResult PollCore(out string token)
		{
			token = null;
			if (!Main.hasFocus)
			{
				DiscardPendingEdgesWhileInactive();
				return PollResult.Waiting;
			}

			AbsorbPressedIntoBaselineAfterFocusRegain();

			KeyToken.PollRealOemTildeActivity();
			bool masqueradeDown = IsJpHankakuMasqueradeDown();
			// Fresh masquerade Up→Down only while listening for new input (not during release-wait).
			// Never invent hankaku from a real @ (VK_OEM_3) release sticky FNA OemTilde frame.
			if (_session.CurrentPhase == RegisterCaptureSession.Phase.WaitingForNewInput
				&& masqueradeDown
				&& !_prevHankakuMasqueradeDown
				&& !KeyToken.WasRecentRealOemTilde())
			{
				WindowsKeySuppressor.NoteHankakuZenkakuEdge("fna-OemTilde-up-to-down");
			}

			_prevHankakuMasqueradeDown = masqueradeDown;

			_latency.AdvanceFrame();
			HashSet<string> held = SnapshotHeldForCapture();
			HashSet<string> confirmedHeld = SnapshotConfirmedHeldForCapture();
			CaptureRecoverySignals recovery = BuildRecoverySignals(held, confirmedHeld);

			// Always collect latches 窶・session rejects edges while WaitingForRelease still blocked.
			// Do NOT drain here: same-frame Mouse1-up + NLS edge must reach TryAcceptNewPresses.
			List<string> edges = CollectLatchEdgesForFrame();

			RegisterCaptureSession.Phase phaseBefore = _session.CurrentPhase;
			bool accepted = _session.Poll(held, edges, recovery, out token);
			// LastDiagReason may be "ambiguous:…" or "collapseBefore=[…] resolve=Ambiguous:… reject=ambiguous".
			if (!accepted
				&& JisOemInGameDiagnostic.IsSessionActive
				&& !string.IsNullOrEmpty(_session.LastDiagReason)
				&& (_session.LastDiagReason.StartsWith("ambiguous", StringComparison.OrdinalIgnoreCase)
					|| _session.LastDiagReason.Contains("reject=ambiguous", StringComparison.OrdinalIgnoreCase)
					|| _session.LastDiagReason.Contains("resolve=Ambiguous", StringComparison.OrdinalIgnoreCase)))
			{
				JisOemInGameDiagnostic.NoteCaptureReject(_session.LastCollapseDiag, _session.LastDiagReason);
			}

			if (LiveInputDiagnosticSession.IsActive && (accepted || (edges != null && edges.Count > 0)))
				LiveInputDiagnosticSession.ObserveCaptureDecision(accepted, token, _session.LastDiagReason);

			if (_session.LastPollRecovered)
			{
				WindowsKeySuppressor.ClearCaptureGhostHeldTokens(_session.LastRecoveredGhostTokens);
				if (AkDiag.Enabled)
					DigRecovery(_session.LastDiagReason, held, confirmedHeld, recovery);
			}

			NoteLatencyAfterPoll(held, edges, accepted, token);
			if (AkDiag.Enabled)
			{
				DigPoll(phaseBefore, held, edges, accepted, _session.LastDiagReason, confirmedHeld, recovery);
				CaptureResponsivenessDiag.Poll(
					phaseBefore,
					_session.CurrentPhase,
					held,
					edges,
					accepted,
					token,
					_session.LastDiagReason,
					_session.BlockedAtBegin);
			}

			NoteAnomalyWarns(held, edges, accepted);

			bool padInEdges = ContainsPadToken(edges);
			bool padInHeld = ContainsPadToken(held);

			if (!accepted || string.IsNullOrEmpty(token))
			{
				if (padInEdges || padInHeld)
				{
					GamePadProbeSystem.LogCapture(
						slotNumber: -1,
						gamePadToken: FirstPadToken(edges) ?? FirstPadToken(held),
						accepted: false,
						purpleCleared: false,
						reason: _session.LastDiagReason);
				}

				return PollResult.Waiting;
			}

			token = KeyToken.NormalizeCapturedToken(token);
			// CAPTURE Accepted=True is logged from ComboPanel after purple clear (avoid duplicate lines).
			return FinishCapture(token, _session.LastDiagReason);
		}

		private static bool ContainsPadToken(IEnumerable<string> tokens)
		{
			if (tokens == null)
				return false;
			foreach (string t in tokens)
			{
				if (GamePadToken.IsPadToken(t))
					return true;
			}

			return false;
		}

		private static string FirstPadToken(IEnumerable<string> tokens)
		{
			if (tokens == null)
				return null;
			foreach (string t in tokens)
			{
				if (GamePadToken.TryCanonicalize(t, out string c))
					return c;
			}

			return null;
		}

		private static void RecoverAfterException()
		{
			DrainAllLatchEdges();
			WindowsKeySuppressor.ClearUnconfirmedScanHeldFlags("exception-residue");
			HashSet<string> confirmed = SnapshotConfirmedHeldForCapture();
			_session.Begin(confirmed);
			if (AkDiag.Enabled)
			{
				CaptureResponsivenessDiag.Begin(confirmed, _session.CurrentPhase);
				if (AkDiag.Enabled)
				{
					AkDiag.Log(
					"SlotCapture exception-residue re-begin phase=" + _session.CurrentPhase
					+ " blocked=[" + string.Join(",", confirmed) + "]");
				}
				DigPhase("exception-residue");
			}

			_prevPhaseForLatency = _session.CurrentPhase;
			_captureLayoutAtBegin = KeyboardLayoutHelper.GetActiveLayoutHandle();
			_prevImeComposingDuringCapture = IsImeComposingActive();
		}

		private static CaptureRecoverySignals BuildRecoverySignals(
			HashSet<string> held,
			HashSet<string> confirmedHeld)
		{
			IntPtr layoutNow = KeyboardLayoutHelper.GetActiveLayoutHandle();
			bool layoutChanged = _captureLayoutAtBegin != IntPtr.Zero
				&& layoutNow != IntPtr.Zero
				&& layoutNow != _captureLayoutAtBegin;
			if (layoutChanged)
				_captureLayoutAtBegin = layoutNow;

			bool imeNow = IsImeComposingActive();
			bool imeEnded = _prevImeComposingDuringCapture && !imeNow;
			_prevImeComposingDuringCapture = imeNow;

			bool imeConversionToggled = WindowsKeySuppressor.TryConsumeImeConversionToggleForCaptureRecovery();
			bool apiMismatch = WindowsKeySuppressor.HasCaptureHeldApiMismatch(held, confirmedHeld);

			string hint = layoutChanged
				? "layout"
				: apiMismatch
					? "api-mismatch"
					: imeConversionToggled
						? "ime-conversion"
						: imeEnded
							? "ime-ended"
							: null;

			return new CaptureRecoverySignals {
				ConfirmedHeld = confirmedHeld,
				LayoutChanged = layoutChanged,
				ApiMismatch = apiMismatch,
				ImeComposingEnded = imeEnded,
				ImeConversionToggled = imeConversionToggled,
				DiagHint = hint
			};
		}

		private static bool IsImeComposingActive()
		{
			// Best-effort: Terraria chat / edit signs absorb keys. OS IME composing is not fully exposed.
			try
			{
				return Main.drawingPlayerChat;
			}
			catch
			{
				return false;
			}
		}

		/// <summary>
		/// Full held snapshot for register mode: keyboard + mouse + NLS / toggle physical holds.
		/// Must include everything that would otherwise arrive only as a latch (so WaitingForRelease works).
		/// Toggle/LED ON must never count as held 窶・FNA IsKeyDown(Caps/Num) follows the LED, so it is forbidden here.
		/// </summary>
		private static HashSet<string> SnapshotHeldForCapture()
			=> SnapshotHeldForCaptureCore(includeUnconfirmedScanHeld: true);

		/// <summary>
		/// Ordinary-API held set for recovery: excludes scan-flag / hankaku latch ghosts that lack
		/// FNA or Win32 VK corroboration. Used only for contradiction recovery 窶・never for acceptance alone.
		/// </summary>
		private static HashSet<string> SnapshotConfirmedHeldForCapture()
			=> SnapshotHeldForCaptureCore(includeUnconfirmedScanHeld: false);

		private static HashSet<string> SnapshotHeldForCaptureCore(bool includeUnconfirmedScanHeld)
		{
			HashSet<string> held = includeUnconfirmedScanHeld
				? KeyToken.SnapshotPressedTokens()
				: KeyToken.SnapshotConfirmedPressedTokens();

			// Physical Down only (hook make/break and/or Win32 high bit). Never Main.keyState.IsKeyDown for locks.
			if (WindowsKeySuppressor.IsToggleLockPhysicallyDownForCapture("CapsLock"))
				held.Add("CapsLock");
			if (WindowsKeySuppressor.IsToggleLockPhysicallyDownForCapture("NumLock"))
				held.Add("NumLock");
			if (WindowsKeySuppressor.IsToggleLockPhysicallyDownForCapture(BindingRestrictions.ScrollLockToken))
				held.Add(BindingRestrictions.ScrollLockToken);

			// OemCopy is the kbd106 VK for kana 窶・never keep it as a distinct held/block token.
			held.Remove("OemCopy");
			held.Remove("OemAuto");
			held.Remove("OemEnlW");

			if (includeUnconfirmedScanHeld)
			{
				// Prefer make/break over GetAsyncKeyState for NLS kana (AsyncKeyState ghost blocks release wait).
				if (WindowsKeySuppressor.IsKanaPhysicallyDown)
					held.Add(nameof(Keys.Kana));
				else
					held.Remove(nameof(Keys.Kana));

				if (WindowsKeySuppressor.IsHankakuZenkakuPhysicallyDown)
					held.Add(BindingRestrictions.HankakuZenkakuToken);
			}
			else
			{
				// Confirmed must never count NLS make/break-only or FNA OemTilde masquerade as held.
				held.Remove(nameof(Keys.Kana));
				held.Remove(BindingRestrictions.HankakuZenkakuToken);
			}

			// JP: never treat FNA OemTilde masquerade as held "OemTilde" (@).
			// Count it as held hankaku only in the full snapshot while physically down or during release-wait,
			// so WaitingForRelease can clear it 窶・acceptance of hankaku still needs Up竊奪own / latch edge.
			// Confirmed snapshot must NOT include masquerade (that blocked contradiction recovery).
			if (KeyboardLayoutHelper.IsJapaneseHankakuKeyContext()
				&& !KeyToken.IsRealOemTildeVirtualKeyDown())
			{
				held.Remove("OemTilde");
				if (includeUnconfirmedScanHeld
					&& IsJpHankakuMasqueradeDown()
					&& (_session.CurrentPhase == RegisterCaptureSession.Phase.WaitingForRelease
						|| WindowsKeySuppressor.IsHankakuZenkakuPhysicallyDown))
				{
					held.Add(BindingRestrictions.HankakuZenkakuToken);
				}
			}

			// Pad:* must appear in Held so RegisterCaptureSession.TryAcceptPreferredGamePadNewcomer
			// can accept a face/DPad/trigger while Mouse1 is still blocked from the slot-open click.
			// Stick directions are still ignored from Held (CaptureArming edges only).
			FnaGamePadProvider.SupplementHeld(held);

			return held;
		}

		private static void NoteLatencyAfterPoll(HashSet<string> held, List<string> edges, bool accepted, string token)
		{
			RegisterCaptureSession.Phase phase = _session.CurrentPhase;
			if (_prevPhaseForLatency == RegisterCaptureSession.Phase.WaitingForRelease
				&& phase == RegisterCaptureSession.Phase.WaitingForNewInput)
			{
				_latency.MarkT2_AllReleased();
				_latency.MarkT3_WaitingForNewInput();
			}
			else if (_prevPhaseForLatency != RegisterCaptureSession.Phase.WaitingForNewInput
				&& phase == RegisterCaptureSession.Phase.WaitingForNewInput)
			{
				_latency.MarkT3_WaitingForNewInput();
			}

			if (accepted && !string.IsNullOrEmpty(token))
			{
				_latency.MarkT4_NewDownDetected();
				_latency.MarkT5_Accepted(token);
			}
			else if (phase == RegisterCaptureSession.Phase.WaitingForNewInput
				&& edges != null && edges.Count > 0
				&& _latency.F4 == null)
			{
				_latency.MarkT4_NewDownDetected();
			}

			_prevPhaseForLatency = phase;
			if (accepted && AkDiag.Enabled)
				DigLatency(held, edges, accepted);
		}

		private static bool IsJpHankakuMasqueradeDown()
			=> KeyboardLayoutHelper.IsJapaneseHankakuKeyContext()
				&& Main.keyState.IsKeyDown(Keys.OemTilde)
				&& !KeyToken.IsRealOemTildeVirtualKeyDown()
				&& !KeyToken.WasRecentRealOemTilde();

		private static List<string> CollectLatchEdgesForFrame()
		{
			var edges = new List<string>(4);

			void Take(bool ok, string t)
			{
				if (ok && !string.IsNullOrEmpty(t) && !ContainsIgnoreCase(edges, t))
					edges.Add(t);
			}

			Take(WindowsKeySuppressor.TryConsumeWinKeyCapture(out string win), win);
			Take(WindowsKeySuppressor.TryConsumeEscChordCapture(out string esc), esc);
			Take(WindowsKeySuppressor.TryConsumeKanaCapture(out string kana), kana);
			Take(WindowsKeySuppressor.TryConsumeHankakuZenkakuCapture(out string hankaku), hankaku);
			Take(WindowsKeySuppressor.TryConsumeJisIntlCapture(out string jis), jis);
			Take(WindowsKeySuppressor.TryConsumePhysicalCapture(out string physical), physical);

			KeyToken.AppendWin32SupplementCaptureEdges(edges);

			MouseInputHelper.AppendFrameWheelEdges(edges);
			DigWheelLatchEdges(edges);

			// Pad:* edges via FNA GamePad (Steam Input API path quarantined / paused).
			// Stick-direction Runtime edges are excluded; CaptureArming supplies CapturePress edges.
			FnaGamePadProvider.AppendFrameEdges(edges);
			if (IsCapturing)
			{
				var snap = FnaGamePadProvider.Current;
				GamePadStickCaptureArming.AppendCaptureEdges(
					snap.LeftStickX, snap.LeftStickY,
					snap.RightStickX, snap.RightStickY,
					edges);
			}

			// Toggle-lock: prefer pending hook edge; also allow physical edge without time warmup gate.
			if (WindowsKeySuppressor.TryConsumeToggleLockPress(out string toggle))
				Take(true, toggle);
			else if (WindowsKeySuppressor.TryPollToggleLockEdge(out toggle))
				Take(true, toggle);

			return edges;
		}

		private static PollResult FinishCapture(string token, string reason)
		{
			token = KeyToken.NormalizeCapturedToken(token);
			string prev = _lastAcceptedToken;
			_lastAcceptedToken = token;
			_session.Cancel();
			GamePadStickCaptureArming.EndSession();
			WindowsKeySuppressor.SetSlotCapturePassthrough(false);
			if (string.Equals(token, "LeftWindows", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(token, "RightWindows", StringComparison.OrdinalIgnoreCase))
			{
				WindowsKeySuppressor.ArmWinKeyReleaseGuard();
			}

			// After NLS accept, drop latch residue so the next slot cannot stick on a ghost edge.
			// Physical make/break bits clear on KEYUP; missed KEYUP uses timeout + recovery.
			if (BindingRestrictions.IsHankakuZenkakuToken(token)
				|| string.Equals(token, "Kana", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(token, "ImeConvert", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(token, "ImeNoConvert", StringComparison.OrdinalIgnoreCase))
			{
				DrainAllLatchEdges();
			}

			if (AkDiag.Enabled)
				DigCaptureAccepted(token, reason, prev);
			if (JisOemInGameDiagnostic.IsSessionActive)
			{
				JisOemInGameDiagnostic.NoteCaptureAccept(token, _session.LastCollapseDiag, reason);
				JisOemInGameDiagnostic.EndCaptureSessionAndFlush("accepted");
			}

			return PollResult.Captured;
		}

		/// <summary>
		/// AKDIAG-off safe: one Warn when WaitingForRelease stays on the same remaining set across
		/// enough *unique input ticks* (not per-Poll increments inside one tick), or when reject
		/// edges keep failing without accept. Does not force-release inputs.
		/// </summary>
		private static void NoteAnomalyWarns(HashSet<string> held, List<string> edges, bool accepted)
		{
			_ = held;
			RegisterCaptureSession.Phase phase = _session.CurrentPhase;

			// Avoid string.Join every frame unless we are in a path that needs the remaining set.
			if (phase != RegisterCaptureSession.Phase.WaitingForRelease
				&& !(edges != null && edges.Count > 0 && !accepted))
			{
				_rejectStreak = 0;
				return;
			}

			string remaining = string.Join(",", _session.BlockedAtBegin);

			if (phase == RegisterCaptureSession.Phase.WaitingForRelease)
			{
				_waitingReleasePollCount++;
				int epoch = FnaGamePadProvider.InputTickEpoch;
				_waitingReleaseUniqueInputTicks =
					ComboPanelRegistrationWaitDiagPolicy.NextUniqueInputTicksForStuckWarn(
						remaining,
						_waitingReleaseRemainingKey,
						_waitingReleaseUniqueInputTicks,
						epoch,
						_waitingReleaseLastInputTick,
						out _waitingReleaseRemainingKey,
						out _waitingReleaseLastInputTick);

				if (_waitingReleaseUniqueInputTicks == 0)
				{
					_waitingReleaseRemainingSinceMs = Environment.TickCount64;
					_stuckReleaseWarned = false;
					_waitingReleasePollCount = 1;
				}

				if (!_stuckReleaseWarned
					&& ComboPanelRegistrationWaitDiagPolicy.ShouldWarnStuckWaitingForRelease(
						_waitingReleaseUniqueInputTicks))
				{
					_stuckReleaseWarned = true;
					long elapsedMs = Environment.TickCount64 - _waitingReleaseRemainingSinceMs;
					if (elapsedMs < 0)
						elapsedMs = 0;
					AkDiag.WarnRare(
						"SlotCapture stuck WaitingForRelease"
						+ " pollCount=" + _waitingReleasePollCount
						+ " inputTicks=" + _waitingReleaseUniqueInputTicks
						+ " elapsedMs=" + elapsedMs
						+ " remaining=[" + remaining + "]"
						+ " held=[" + string.Join(",", held) + "]"
						+ " " + WindowsKeySuppressor.FormatNlsCaptureDiag()
						+ " (AKDIAG digs diagnose reject/recover; check client.log)");
				}
			}
			else
			{
				_waitingReleasePollCount = 0;
				_waitingReleaseUniqueInputTicks = 0;
				_waitingReleaseLastInputTick = int.MinValue;
				_waitingReleaseRemainingKey = "";
			}

			if (accepted)
			{
				_rejectStreak = 0;
				_rejectStreakWarned = false;
				return;
			}

			if (edges != null && edges.Count > 0)
			{
				_rejectStreak++;
				if (!_rejectStreakWarned && _rejectStreak >= RejectStreakWarnCount)
				{
					_rejectStreakWarned = true;
					AkDiag.WarnRare(
						"SlotCapture reject-streak count>=" + RejectStreakWarnCount
						+ " phase=" + phase
						+ " lastEdges=[" + string.Join(",", edges) + "]"
						+ " remaining=[" + remaining + "]"
						+ " " + WindowsKeySuppressor.FormatNlsCaptureDiag());
				}
			}
			else
				_rejectStreak = 0;
		}

		/// <summary>Called by ComboPanel after TrySetSlot / redraw / save under AKDIAG.</summary>
		internal static void NoteLatencyInternalUpdated()
		{
			_latency.MarkT6_InternalValueUpdated();
			if (AkDiag.Enabled)
				CaptureResponsivenessDiag.NoteInternalUpdated();
		}

		internal static void NoteLatencyUiUpdated()
		{
			_latency.MarkT7_UiUpdated();
			if (AkDiag.Enabled)
				CaptureResponsivenessDiag.NoteUiUpdated();
		}

		internal static void NoteLatencySaveCompleted() => _latency.MarkT8_SaveCompleted();

		private static bool ContainsIgnoreCase(List<string> list, string value)
		{
			foreach (string item in list)
			{
				if (string.Equals(item, value, StringComparison.OrdinalIgnoreCase))
					return true;
			}

			return false;
		}

		private static void DigBegin(HashSet<string> heldAtBegin)
		{
			if (!AkDiag.Enabled)
				return;

			_prevPhaseForDig = _session.CurrentPhase;
			_prevBlockedDig = string.Join(",", heldAtBegin);
			_prevReasonDig = _session.LastDiagReason ?? "";
			_prevNlsDig = WindowsKeySuppressor.FormatNlsCaptureDiag();
			bool blockedEmpty = heldAtBegin == null || heldAtBegin.Count == 0;
			if (AkDiag.Enabled)
			{
				AkDiag.AnnounceCaptureBegin(
				"seq=" + _beginSeq
				+ " phase=" + _session.CurrentPhase
				+ " blockedEmpty=" + blockedEmpty);
			}
			if (AkDiag.Enabled)
			{
				AkDiag.Log(
				"SlotCapture begin seq=" + _beginSeq
				+ " phase=" + _session.CurrentPhase
				+ " blocked=[" + string.Join(",", heldAtBegin) + "]"
				+ " blockedEmpty=" + blockedEmpty
				+ " prevAccepted=" + (_lastAcceptedToken ?? "none")
				+ " unlockTrail=" + (_lastAcceptedToken == null
				? "first-or-none"
				: blockedEmpty
				? "prevAccepted-then-blocked-cleared"
				: "prevAccepted-but-still-blocked")
				+ " hook=" + WindowsKeySuppressor.IsInstalled
				+ " wndHook=" + GameWindowInputHook.IsInstalledForDiag
				+ " " + MouseInputHelper.FormatWheelDiag()
				+ " " + KeyboardLayoutHelper.FormatCaptureLayoutDiag()
				+ " " + WindowsKeySuppressor.FormatNlsCaptureDiag());
			}
		}

		private static void DigPhase(string why)
		{
			if (!AkDiag.Enabled)
				return;

			if (AkDiag.Enabled)
			{
				AkDiag.Log(
				"SlotCapture phase=" + _session.CurrentPhase
				+ " why=" + why
				+ " blocked=[" + string.Join(",", _session.BlockedAtBegin) + "]"
				+ " " + WindowsKeySuppressor.FormatNlsCaptureDiag());
			}
			_prevPhaseForDig = _session.CurrentPhase;
			_prevBlockedDig = string.Join(",", _session.BlockedAtBegin);
		}

		private static void DigPoll(
			RegisterCaptureSession.Phase phaseBefore,
			HashSet<string> held,
			List<string> edges,
			bool accepted,
			string reason,
			HashSet<string> confirmedHeld,
			CaptureRecoverySignals recovery)
		{
			if (!AkDiag.Enabled)
				return;

			RegisterCaptureSession.Phase phase = _session.CurrentPhase;
			string blockedNow = string.Join(",", _session.BlockedAtBegin);
			string nls = WindowsKeySuppressor.FormatNlsCaptureDiag();
			bool allReleased = confirmedHeld == null || confirmedHeld.Count == 0;
			bool phaseChanged = phaseBefore != phase;
			bool blockedChanged;
			bool reasonChanged;
			bool nlsChanged;
			blockedChanged = !string.Equals(blockedNow, _prevBlockedDig, StringComparison.Ordinal);
			reasonChanged = !string.Equals(reason ?? "", _prevReasonDig, StringComparison.Ordinal);
			nlsChanged = !string.Equals(nls, _prevNlsDig, StringComparison.Ordinal);

			bool edgesPresent = edges != null && edges.Count > 0;
			bool interesting = accepted
				|| phaseChanged
				|| blockedChanged
				|| edgesPresent
				|| _session.LastPollRecovered
				|| (recovery != null && (recovery.ApiMismatch || recovery.LayoutChanged
					|| recovery.ImeConversionToggled || recovery.ImeComposingEnded))
				|| (phase == RegisterCaptureSession.Phase.WaitingForRelease && reasonChanged)
				|| nlsChanged;

			if (!interesting)
				return;

			if (phaseChanged)
			{
				if (AkDiag.Enabled)
				{
					AkDiag.Log(
					"SlotCapture phase " + phaseBefore + "->" + phase
					+ (phase == RegisterCaptureSession.Phase.WaitingForNewInput
					? " AllInputsReleased=true"
					: "")
					+ " remaining=[" + blockedNow + "]"
					+ " reason=" + reason);
				}
			}

			if (phase == RegisterCaptureSession.Phase.WaitingForRelease
				&& (blockedChanged || edgesPresent || reasonChanged))
			{
				if (AkDiag.Enabled)
				{
					AkDiag.Log(
					"SlotCapture waiting-for-release remaining=[" + blockedNow + "]"
					+ " held=[" + string.Join(",", held) + "]"
					+ " confirmed=[" + string.Join(",", confirmedHeld) + "]"
					+ " AllInputsReleased=" + allReleased
					+ (edgesPresent ? " rejected-edges=[" + string.Join(",", edges) + "]" : "")
					+ (ContainsWheelEdge(edges) ? " wheel=rejected-during-release" : string.Empty)
					+ " reason=" + reason
					+ (ContainsWheelEdge(edges) ? " " + MouseInputHelper.FormatWheelDiag() : string.Empty)
					+ " " + nls);
				}
			}

			if (edgesPresent && !accepted)
			{
				bool hasHankaku = ContainsIgnoreCase(edges, BindingRestrictions.HankakuZenkakuToken);
				bool hasKana = ContainsIgnoreCase(edges, "Kana");
				bool hasWheel = ContainsWheelEdge(edges);
				if (AkDiag.Enabled)
				{
					AkDiag.Log(
					"SlotCapture REJECTED path=ordinary edges=[" + string.Join(",", edges) + "]"
					+ (hasHankaku ? " hankaku=rejected" : string.Empty)
					+ (hasKana ? " kana=rejected" : string.Empty)
					+ (hasWheel ? " wheel=rejected" : string.Empty)
					+ " phase=" + phase
					+ " remaining=[" + blockedNow + "]"
					+ " reason=" + reason
					+ (hasWheel ? " " + MouseInputHelper.FormatWheelDiag() : string.Empty)
					+ " (Esc bypass is ComboPanel ESC-BYPASS 窶・not this path)");
				}
			}

			if (accepted)
			{
				string tok = _session.AcceptedToken ?? "?";
				bool wheelAccepted = StableKeyToken.IsMouseWheelToken(tok);
				if (AkDiag.Enabled)
				{
					AkDiag.Log(
					"SlotCapture ACCEPTED path=ordinary token=" + tok
					+ (BindingRestrictions.IsHankakuZenkakuToken(tok) ? " hankaku=accepted" : string.Empty)
					+ (string.Equals(tok, "Kana", StringComparison.OrdinalIgnoreCase) ? " kana=accepted" : string.Empty)
					+ (wheelAccepted ? " wheel=accepted display=" + KeyToken.FormatToken(tok) : string.Empty)
					+ " reason=" + reason
					+ (wheelAccepted ? " " + MouseInputHelper.FormatWheelDiag() : string.Empty)
					+ " (Esc bypass is ComboPanel ESC-BYPASS 窶・not this path)");
				}
			}

			if (recovery != null
				&& (recovery.ApiMismatch || recovery.LayoutChanged
					|| recovery.ImeConversionToggled || recovery.ImeComposingEnded
					|| nlsChanged))
			{
				if (AkDiag.Enabled)
				{
					AkDiag.Log(
					"SlotCapture nls/recoverySignals phase=" + phase
					+ " held=[" + string.Join(",", held) + "]"
					+ " confirmed=[" + string.Join(",", confirmedHeld) + "]"
					+ " layoutChanged=" + recovery.LayoutChanged
					+ " apiMismatch=" + recovery.ApiMismatch
					+ " imeConv=" + recovery.ImeConversionToggled
					+ " imeEnded=" + recovery.ImeComposingEnded
					+ " " + nls);
				}
			}

			_prevPhaseForDig = phase;
			_prevBlockedDig = blockedNow;
			_prevReasonDig = reason ?? "";
			_prevNlsDig = nls;
		}

		private static void DigWheelLatchEdges(List<string> edges)
		{
			if (!AkDiag.Enabled || edges == null || edges.Count == 0)
				return;

			bool hasWheel = false;
			for (int i = 0; i < edges.Count; i++)
			{
				if (StableKeyToken.IsMouseWheelToken(edges[i]))
				{
					hasWheel = true;
					break;
				}
			}

			if (!hasWheel)
				return;

			if (AkDiag.Enabled)
			{
				AkDiag.Log(
				"SlotCapture wheel-latch phase=" + _session.CurrentPhase
				+ " edges=[" + string.Join(",", edges) + "]"
				+ " blocked=[" + string.Join(",", _session.BlockedAtBegin) + "]"
				+ " " + MouseInputHelper.FormatWheelDiag());
			}
		}

		private static bool ContainsWheelEdge(IEnumerable<string> tokens)
		{
			if (tokens == null)
				return false;

			foreach (string token in tokens)
			{
				if (StableKeyToken.IsMouseWheelToken(token))
					return true;
			}

			return false;
		}

		private static void DigRecovery(
			string reason,
			HashSet<string> held,
			HashSet<string> confirmedHeld,
			CaptureRecoverySignals recovery)
		{
			if (!AkDiag.Enabled)
				return;

			if (AkDiag.Enabled)
			{
				AkDiag.Log(
				"SlotCapture RECOVER phase=" + _session.CurrentPhase
				+ " ghosts=[" + string.Join(",", _session.LastRecoveredGhostTokens) + "]"
				+ " held=[" + string.Join(",", held) + "]"
				+ " confirmed=[" + string.Join(",", confirmedHeld) + "]"
				+ " layoutChanged=" + (recovery?.LayoutChanged ?? false)
				+ " apiMismatch=" + (recovery?.ApiMismatch ?? false)
				+ " imeConv=" + (recovery?.ImeConversionToggled ?? false)
				+ " reason=" + reason
				+ " " + WindowsKeySuppressor.FormatNlsCaptureDiag());
			}
		}

		private static void DigCaptureAccepted(string token, string reason, string prevAccepted)
		{
			if (!AkDiag.Enabled)
				return;

			bool wheel = StableKeyToken.IsMouseWheelToken(token);
			bool parsed = KeyToken.TryParse(token, out Keys key, out bool isMouse, out int mouseButton);
			if (AkDiag.Enabled)
			{
				AkDiag.Log(
				"SlotCapture captured=" + token
				+ " via=" + reason
				+ " prevAccepted=" + (prevAccepted ?? "none")
				+ " parsed=" + parsed
				+ " keysEnum=" + (parsed && !isMouse ? key.ToString() + "(" + (int)key + ")" : "-")
				+ " mouse=" + (isMouse ? mouseButton.ToString() : "-")
				+ (wheel ? " wheel=accepted" : string.Empty)
				+ " display=" + KeyToken.FormatToken(token)
				+ (parsed && !isMouse
				? " " + KeyboardLayoutHelper.FormatPhysicalKeyCaptureDiag((uint)key, adoptedToken: token)
				: string.Empty)
				+ (wheel ? " " + MouseInputHelper.FormatWheelDiag() : string.Empty)
				+ " " + KeyboardLayoutHelper.FormatCaptureLayoutDiag()
				+ " " + WindowsKeySuppressor.FormatNlsCaptureDiag());
			}
			DisplayResolveDiag.LogCapture(token);
			if (AkDiag.Enabled)
			{
				AkDiag.Log(
				"SlotCapture unlock-trail note: next Begin should show prevAccepted=" + token
				+ " and blockedEmpty=true if WaitingForRelease ghosts cleared");
			}
			if (AkDiag.Enabled) AkDiag.Log("SlotCapture latency " + _latency.FormatSummary());
		}

		private static void DigLatency(HashSet<string> held, List<string> edges, bool accepted)
		{
			if (!AkDiag.Enabled)
				return;

			_ = accepted;
			if (AkDiag.Enabled)
			{
				AkDiag.Log(
				"SlotCapture latencyMark frame=" + _latency.Frame
				+ " phase=" + _session.CurrentPhase
				+ " physCaps=" + WindowsKeySuppressor.IsCapsLockPhysicallyDown
				+ " physNum=" + WindowsKeySuppressor.IsNumLockPhysicallyDown
				+ " physScroll=" + WindowsKeySuppressor.IsScrollLockPhysicallyDown
				+ " ledCaps=" + WindowsKeySuppressor.IsToggleLockLedOn("CapsLock")
				+ " ledNum=" + WindowsKeySuppressor.IsToggleLockLedOn("NumLock")
				+ " ledScroll=" + WindowsKeySuppressor.IsToggleLockLedOn(BindingRestrictions.ScrollLockToken)
				+ " held=[" + string.Join(",", held) + "]"
				+ " edges=[" + string.Join(",", edges ?? new List<string>(0)) + "]"
				+ " " + _latency.FormatSummary());
			}
		}
	}
}
