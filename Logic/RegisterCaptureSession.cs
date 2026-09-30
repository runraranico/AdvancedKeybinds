using System;
using System.Collections.Generic;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Pure register-mode input state machine for a single combo slot.
	/// Separates "should this register?" from OS/FNA input acquisition so tests can inject snapshots.
	/// Phases: Inactive → WaitingForRelease → WaitingForNewInput → InputAccepted,
	/// with RecoveringFromInvalidState for ghost-held / KeyUp-miss / layout / API mismatch recovery.
	/// </summary>
	public sealed class RegisterCaptureSession
	{
		public enum Phase
		{
			Inactive = 0,
			WaitingForRelease = 1,
			WaitingForNewInput = 2,
			InputAccepted = 3,
			RecoveringFromInvalidState = 4
		}

		public Phase CurrentPhase { get; private set; } = Phase.Inactive;

		/// <summary>Start-held tokens that have not fully released yet.</summary>
		public IReadOnlyCollection<string> BlockedAtBegin => _blockedAtBegin;

		public string AcceptedToken { get; private set; }
		public string LastDiagReason { get; private set; }

		/// <summary>
		/// Structured outcome of the latest Poll. Waiting/recovery/accept stay None;
		/// only CaptureTokenResolver Ambiguous/Empty/Unresolved set a reject code.
		/// </summary>
		public AkDecisionReason LastPollDecisionReason { get; private set; }

		/// <summary>Ghost tokens cleared by the last recovery (runtime should drop matching held flags).</summary>
		public IReadOnlyCollection<string> LastRecoveredGhostTokens => _lastRecoveredGhosts;

		/// <summary>True when the previous Poll performed a recovery (no accept that frame).</summary>
		public bool LastPollRecovered { get; private set; }

		/// <summary>Diagnostic: newcomers before/after MeasuredPhysicalKeyNormalizer.CollapseNewcomers.</summary>
		public string LastCollapseDiag { get; private set; }

		private readonly HashSet<string> _blockedAtBegin = new(StringComparer.OrdinalIgnoreCase);
		private readonly HashSet<string> _prevHeld = new(StringComparer.OrdinalIgnoreCase);
		private readonly List<string> _lastRecoveredGhosts = new(4);

		public void Begin(IEnumerable<string> heldAtBegin)
		{
			Cancel();
			LastPollDecisionReason = AkDecisionReason.None;
			AddCanonical(heldAtBegin, _blockedAtBegin);
			AddCanonical(heldAtBegin, _prevHeld);
			LastDiagReason = "begin blocked=[" + string.Join(",", _blockedAtBegin) + "]";

			if (_blockedAtBegin.Count == 0)
			{
				CurrentPhase = Phase.WaitingForNewInput;
				LastDiagReason = "begin-immediate-waiting-for-new-input";
				return;
			}

			CurrentPhase = Phase.WaitingForRelease;
		}

		public void Cancel()
		{
			CurrentPhase = Phase.Inactive;
			AcceptedToken = null;
			LastDiagReason = "cancelled";
			LastPollDecisionReason = AkDecisionReason.None;
			LastPollRecovered = false;
			_blockedAtBegin.Clear();
			_prevHeld.Clear();
			_lastRecoveredGhosts.Clear();
		}

		/// <summary>
		/// Advance one frame from injectable held + one-shot KEYDOWN edges.
		/// Returns true when a token was accepted this frame.
		/// </summary>
		public bool Poll(IEnumerable<string> heldNow, IEnumerable<string> edgeTokens, out string acceptedToken)
			=> Poll(heldNow, edgeTokens, CaptureRecoverySignals.None, out acceptedToken);

		/// <summary>
		/// Same as <see cref="Poll(IEnumerable{string}, IEnumerable{string}, out string)"/> with
		/// contradiction / layout / API mismatch recovery evidence.
		/// </summary>
		public bool Poll(
			IEnumerable<string> heldNow,
			IEnumerable<string> edgeTokens,
			CaptureRecoverySignals recovery,
			out string acceptedToken)
		{
			acceptedToken = null;
			LastPollRecovered = false;
			_lastRecoveredGhosts.Clear();
			LastPollDecisionReason = AkDecisionReason.None;

			if (CurrentPhase == Phase.Inactive || CurrentPhase == Phase.InputAccepted)
				return false;

			var held = ToCanonicalSet(heldNow);
			var edges = ToCanonicalSet(edgeTokens);

			// Snapshot last frame before we mutate — required so Mouse1-up + new key same frame works.
			var prevSnapshot = new HashSet<string>(_prevHeld, StringComparer.OrdinalIgnoreCase);

			// GamePad digital press wins over SI-injected Mouse/Keyboard still held from slot-open click.
			// Completes the current slot only (does not close ComboPanel).
			if (CurrentPhase == Phase.WaitingForRelease
				|| CurrentPhase == Phase.WaitingForNewInput
				|| CurrentPhase == Phase.RecoveringFromInvalidState)
			{
				if (TryAcceptPreferredGamePadNewcomer(held, edges, prevSnapshot, out acceptedToken))
					return true;
			}

			if (CurrentPhase == Phase.RecoveringFromInvalidState)
			{
				// One-frame recovery gate: never accept on the transition out of invalid state.
				CurrentPhase = _blockedAtBegin.Count == 0
					? Phase.WaitingForNewInput
					: Phase.WaitingForRelease;
				_prevHeld.Clear();
				foreach (string token in held)
					_prevHeld.Add(token);
				LastDiagReason = CurrentPhase == Phase.WaitingForNewInput
					? "post-recover-waiting-for-new-input"
					: "post-recover-waiting-for-release remaining=[" + string.Join(",", _blockedAtBegin) + "]";
				return false;
			}

			if (CurrentPhase == Phase.WaitingForRelease)
			{
				_blockedAtBegin.RemoveWhere(token => !held.Contains(token));

				if (_blockedAtBegin.Count > 0)
				{
					if (TryRecoverGhostBlocked(held, recovery ?? CaptureRecoverySignals.None))
					{
						LastPollRecovered = true;
						return false;
					}

					_prevHeld.Clear();
					foreach (string token in held)
						_prevHeld.Add(token);

					LastDiagReason = edges.Count > 0
						? "blocked-edge-during-release edges=[" + string.Join(",", edges) + "] remaining=[" + string.Join(",", _blockedAtBegin) + "]"
						: "waiting-for-release remaining=[" + string.Join(",", _blockedAtBegin) + "]";
					return false;
				}

				CurrentPhase = Phase.WaitingForNewInput;
				LastDiagReason = "enter-waiting-for-new-input";
			}

			return TryAcceptNewPresses(held, edges, prevSnapshot, out acceptedToken);
		}

		/// <summary>
		/// Accept a single new Pad:* token even while WaitingForRelease (Mouse1 still blocked from UI click).
		/// </summary>
		private bool TryAcceptPreferredGamePadNewcomer(
			HashSet<string> held,
			HashSet<string> edges,
			HashSet<string> prevSnapshot,
			out string acceptedToken)
		{
			acceptedToken = null;
			var pads = new List<string>(2);

			void Consider(string token, bool fromHeld)
			{
				if (!GamePadToken.IsPadToken(token))
					return;
				// Stick-direction Held uses RuntimePress (0.50); Capture must only accept CaptureArming edges (0.65).
				if (fromHeld && GamePadToken.IsStickDirectionToken(token))
					return;
				// CaptureArming edges may fire while Runtime already Held the same direction token.
				if (prevSnapshot != null && prevSnapshot.Contains(token))
				{
					if (fromHeld || !GamePadToken.IsStickDirectionToken(token))
						return;
				}
				if (!ContainsOrdinalIgnoreCase(pads, token))
					pads.Add(token);
			}

			foreach (string token in held)
				Consider(token, fromHeld: true);
			foreach (string token in edges)
				Consider(token, fromHeld: false);

			if (pads.Count != 1)
				return false;

			Phase phaseWas = CurrentPhase;
			string blockedDiag = string.Join(",", _blockedAtBegin);
			Accept(pads[0], "prefer-gamepad-newcomer");
			LastDiagReason = "prefer-gamepad-newcomer AcceptedToken=" + AcceptedToken
				+ " phaseWas=" + phaseWas
				+ " blocked=[" + blockedDiag + "]";
			acceptedToken = AcceptedToken;
			return true;
		}

		/// <summary>
		/// Clears contradicted blocked ghosts when ordinary APIs report empty / layout change / mismatch.
		/// Never clears solely by elapsed time; never clears blocked that ConfirmedHeld still sees.
		/// </summary>
		private bool TryRecoverGhostBlocked(HashSet<string> held, CaptureRecoverySignals signals)
		{
			if (!signals.HasRecoveryEvidence)
				return false;

			HashSet<string> confirmed = signals.ConfirmedHeld != null
				? ToCanonicalSet(signals.ConfirmedHeld)
				: null;

			var ghosts = new List<string>(4);
			foreach (string blocked in _blockedAtBegin)
			{
				if (confirmed != null && confirmed.Contains(blocked))
					continue;
				ghosts.Add(blocked);
			}

			if (ghosts.Count == 0)
				return false;

			bool confirmedEmpty = confirmed != null && confirmed.Count == 0;
			bool shouldRecover = signals.ExceptionResidue
				|| signals.LayoutChanged
				|| signals.ApiMismatch
				|| signals.ImeComposingEnded
				|| signals.ImeConversionToggled
				|| confirmedEmpty;

			if (!shouldRecover)
				return false;

			CurrentPhase = Phase.RecoveringFromInvalidState;
			foreach (string ghost in ghosts)
			{
				_blockedAtBegin.Remove(ghost);
				_lastRecoveredGhosts.Add(ghost);
			}

			string reason = BuildRecoveryReason(signals, confirmedEmpty);
			LastDiagReason = "recovering-from-invalid-state ghosts=[" + string.Join(",", ghosts) + "] via=" + reason
				+ (string.IsNullOrEmpty(signals.DiagHint) ? string.Empty : " hint=" + signals.DiagHint)
				+ (_blockedAtBegin.Count == 0
					? "; next=WaitingForNewInput"
					: "; next=WaitingForRelease remaining=[" + string.Join(",", _blockedAtBegin) + "]");

			// Keep ghosts in prevHeld so a residual held bit cannot Up→Down accept them next frame.
			_prevHeld.Clear();
			foreach (string ghost in ghosts)
				_prevHeld.Add(ghost);
			if (confirmed != null)
			{
				foreach (string token in confirmed)
					_prevHeld.Add(token);
			}

			_ = held;
			return true;
		}

		private static string BuildRecoveryReason(CaptureRecoverySignals signals, bool confirmedEmpty)
		{
			var parts = new List<string>(4);
			if (confirmedEmpty)
				parts.Add("confirmed-empty");
			if (signals.LayoutChanged)
				parts.Add("layout-changed");
			if (signals.ApiMismatch)
				parts.Add("api-mismatch");
			if (signals.ImeComposingEnded)
				parts.Add("ime-composing-ended");
			if (signals.ImeConversionToggled)
				parts.Add("ime-conversion-toggled");
			if (signals.ExceptionResidue)
				parts.Add("exception-residue");
			return parts.Count == 0 ? "unspecified" : string.Join("+", parts);
		}

		private bool TryAcceptNewPresses(
			HashSet<string> held,
			HashSet<string> edges,
			HashSet<string> prevSnapshot,
			out string acceptedToken)
		{
			acceptedToken = null;
			if (CurrentPhase != Phase.WaitingForNewInput)
				return false;

			var newcomers = new List<string>(4);
			foreach (string token in held)
			{
				if (prevSnapshot.Contains(token))
					continue;
				// Stick directions: Runtime Held must not register; CaptureArming edges only.
				if (GamePadToken.IsStickDirectionToken(token))
					continue;
				if (!ContainsOrdinalIgnoreCase(newcomers, token))
					newcomers.Add(token);
			}

			foreach (string token in edges)
			{
				// Stick-direction Capture edges are authored by CaptureArming (0.65).
				// Runtime may already have held the same token from 0.50 — still accept the Capture edge.
				if (prevSnapshot.Contains(token) && !GamePadToken.IsStickDirectionToken(token))
					continue;
				if (!ContainsOrdinalIgnoreCase(newcomers, token))
					newcomers.Add(token);
			}

			_prevHeld.Clear();
			foreach (string token in held)
				_prevHeld.Add(token);

			if (newcomers.Count == 0)
			{
				LastDiagReason = edges.Count > 0
					? "waiting-for-new-input pulse-already-seen=[" + string.Join(",", edges) + "]"
					: "waiting-for-new-input";
				return false;
			}

			List<string> beforeCollapse = new(newcomers);
			List<string> collapsed = MeasuredPhysicalKeyNormalizer.CollapseNewcomers(newcomers);
			LastCollapseDiag = "before=[" + string.Join(",", beforeCollapse)
				+ "] after=[" + string.Join(",", collapsed) + "]";

			CaptureTokenResolver.ResolveResult resolved = CaptureTokenResolver.ResolveNewcomers(collapsed);
			if (resolved.Kind == CaptureTokenResolver.ResolveKind.Resolved)
			{
				Accept(resolved.Token, "resolved:" + resolved.Reason);
				LastDiagReason = "collapseBefore=[" + string.Join(",", beforeCollapse)
					+ "] collapseAfter=[" + string.Join(",", collapsed)
					+ "] resolve=Resolved:" + resolved.Reason
					+ " AcceptedToken=" + AcceptedToken;
				acceptedToken = AcceptedToken;
				return true;
			}

			if (resolved.Kind == CaptureTokenResolver.ResolveKind.Ambiguous)
			{
				foreach (string token in newcomers)
					_prevHeld.Add(token);
				LastDiagReason = "collapseBefore=[" + string.Join(",", beforeCollapse)
					+ "] collapseAfter=[" + string.Join(",", collapsed)
					+ "] resolve=Ambiguous:" + resolved.Reason
					+ " AcceptedToken=(none) reject=ambiguous";
				LastPollDecisionReason = AkDecisionReason.AmbiguousCapture;
				LogRejectNewcomers(newcomers, LastDiagReason);
				return false;
			}

			if (resolved.Kind == CaptureTokenResolver.ResolveKind.Empty)
			{
				LastDiagReason = "collapseBefore=[" + string.Join(",", beforeCollapse)
					+ "] collapseAfter=[" + string.Join(",", collapsed)
					+ "] resolve=Empty AcceptedToken=(none) reject=empty-resolve";
				LastPollDecisionReason = AkDecisionReason.EmptyResolution;
				LogRejectNewcomers(newcomers, LastDiagReason);
				return false;
			}

			LastDiagReason = "collapseBefore=[" + string.Join(",", beforeCollapse)
				+ "] collapseAfter=[" + string.Join(",", collapsed)
				+ "] resolve=Unresolved:" + resolved.Reason
				+ " AcceptedToken=(none) reject=unresolved";
			LastPollDecisionReason = AkDecisionReason.UnresolvedCapture;
			LogRejectNewcomers(newcomers, LastDiagReason);
			return false;
		}

		private static void LogRejectNewcomers(IReadOnlyList<string> newcomers, string reason)
		{
			// SlotCapture digs cover reject details; keep this Core-safe (no Binding/XNA deps).
			_ = newcomers;
			_ = reason;
		}

		private void Accept(string token, string reason)
		{
			AcceptedToken = StableKeyToken.CanonicalizeOrNull(token) ?? token;
			CurrentPhase = Phase.InputAccepted;
			LastDiagReason = "accepted=" + AcceptedToken + " via=" + reason;
			_blockedAtBegin.Clear();
			_prevHeld.Clear();
			_lastRecoveredGhosts.Clear();
		}

		private static HashSet<string> ToCanonicalSet(IEnumerable<string> source)
		{
			var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			AddCanonical(source, set);
			return set;
		}

		private static void AddCanonical(IEnumerable<string> source, HashSet<string> dest)
		{
			if (source == null)
				return;

			foreach (string raw in source)
			{
				string c = StableKeyToken.CanonicalizeOrNull(raw);
				if (c != null)
					dest.Add(c);
			}
		}

		private static bool ContainsOrdinalIgnoreCase(List<string> list, string value)
		{
			foreach (string item in list)
			{
				if (string.Equals(item, value, StringComparison.OrdinalIgnoreCase))
					return true;
			}

			return false;
		}
	}
}
