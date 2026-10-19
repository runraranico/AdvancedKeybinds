using System;
using AdvancedKeybinds.Binding;
using AdvancedKeybinds.Diagnostics;
using Microsoft.Xna.Framework;
using Terraria;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// ComboPanel-only GamePad focus navigation. Does not move OS/Main mouse.
	/// Independent of Keyboard providers and of mouse hover / PhysicalHoverLock.
	/// </summary>
	internal static class ComboPanelGamePadNavigation
	{
		public const float StickNavigateThreshold = 0.65f;
		public const float StickReleaseThreshold = 0.35f;
		public const int InitialRepeatDelayMs = 350;
		public const int RepeatIntervalMs = 120;

		private static ComboPanelPadFocus _focus = ComboPanelPadFocus.None;
		private static bool _padNavVisible;
		/// <summary>True after GamePad open or any pad nav — parent UI suppress without forcing Slot1 focus.</summary>
		private static bool _padSessionActive;
		private static bool _openGateActive;
		private static string _openGateToken;
		private static bool _numberEditActive;
		private static int _numberEditBaselineMs;
		private static bool _clearConfirmActive;
		/// <summary>
		/// After ClearConfirm Arm (or Commit), ignore Pad:A until RELEASE so the same physical
		/// press cannot Arm+Commit (Tick may run multiple times per InputTickEpoch).
		/// </summary>
		private static bool _clearConfirmPadAHoldGate;
		private static int _clearConfirmArmEpoch = int.MinValue;
		/// <summary>
		/// Last input tick that applied HoldWhileHeld / HoldOnRelease via pad Confirm.
		/// Same-tick multi-Draw Confirm must not toggle HOLD more than once.
		/// </summary>
		private static int _lastHoldModeToggleEpoch = int.MinValue;
		private static int _stickAxisLatched; // 0 none, 1 H, 2 V
		private static long _nextRepeatMs;
		private static bool _repeatArmed;
		private static int _dpadLatched; // 0 none, -1 L, +1 R, -2 D, +2 U (signed axis codes)
		private static long _dpadNextRepeatMs;
		private static bool _dpadRepeatArmed;
		private static int _lastPhysicalMouseX = int.MinValue;
		private static int _lastPhysicalMouseY = int.MinValue;
		private static bool _hasPhysicalMouseSample;
		private static readonly HoldSliderConfirmEditPolicy.State HoldSliderEdit = new();
		private static bool _skipSlot2Focus;

		/// <summary>Pad token consumed by SlotCapture (accepted or rejected); blocks nav until RELEASE.</summary>
		private static string _captureReleaseToken;
		private static bool _captureReleaseGateActive;
		private static int _captureConsumedEpoch = int.MinValue;
		private static int _navSkipLoggedEpoch = int.MinValue;
		private static string _navSkipLoggedToken;

		public static ComboPanelPadFocus Focus => _focus;
		public static bool IsPadNavVisible => _padNavVisible && _focus != ComboPanelPadFocus.None;
		public static bool IsOpenGateActive => _openGateActive;
		public static bool IsNumberEditActive => _numberEditActive;
		public static bool IsClearConfirmActive => _clearConfirmActive;
		public static bool IsPadSessionActive => _padSessionActive;
		public static bool IsHoldSliderEditing
			=> HoldSliderEdit.Phase == HoldSliderConfirmEditPolicy.Phase.Editing;
		public static string OpenGateToken => _openGateToken;
		public static bool IsCaptureReleaseGateActive => _captureReleaseGateActive;
		/// <summary>
		/// Field read only — no release poll, no clear, no diagnostics.
		/// Safe for diagnostic snapshots (must not call <see cref="IsCaptureInputBlocked"/>).
		/// </summary>
		public static bool CaptureReleaseGateActiveRaw => _captureReleaseGateActive;
		public static string CaptureReleaseToken => _captureReleaseToken;

		/// <summary>True while pad UI mode should suppress parent menu/nav triggers.</summary>
		public static bool ShouldSuppressParentNavTriggers
			=> _padSessionActive
				|| _padNavVisible
				|| _openGateActive
				|| _numberEditActive
				|| _clearConfirmActive
				|| IsCaptureInputBlocked();

		public static void Reset()
		{
			ForceExitHoldSliderEdit();
			_focus = ComboPanelPadFocus.None;
			_padNavVisible = false;
			_padSessionActive = false;
			_openGateActive = false;
			_openGateToken = null;
			_numberEditActive = false;
			_numberEditBaselineMs = 0;
			_clearConfirmActive = false;
			_clearConfirmPadAHoldGate = false;
			_clearConfirmArmEpoch = int.MinValue;
			_lastHoldModeToggleEpoch = int.MinValue;
			_stickAxisLatched = 0;
			_nextRepeatMs = 0;
			_repeatArmed = false;
			_dpadLatched = 0;
			_dpadNextRepeatMs = 0;
			_dpadRepeatArmed = false;
			_hasPhysicalMouseSample = false;
			_skipSlot2Focus = false;
			ClearCaptureReleaseGate(logEnd: false);
		}

		private static void ForceExitHoldSliderEdit()
		{
			HoldSliderConfirmEditPolicy.Observe(
				HoldSliderEdit,
				new HoldSliderConfirmEditPolicy.FrameInput(
					sliderFocused: false,
					holdEditable: false,
					forceExit: true));
			HoldSliderEdit.Reset();
		}

		/// <summary>
		/// SlotCapture accepted or rejected a Pad:* token. Consume it for this InputTickEpoch and
		/// hold a RELEASE gate so the same press cannot move focus / confirm / cancel / re-capture.
		/// </summary>
		public static void NotifyPadCaptureConsumed(string token, int slot, bool accepted)
		{
			if (!GamePadToken.TryCanonicalize(token, out string canonical))
				return;

			_captureReleaseToken = canonical;
			_captureReleaseGateActive = true;
			_captureConsumedEpoch = FnaGamePadProvider.InputTickEpoch;
			_navSkipLoggedEpoch = int.MinValue;
			_navSkipLoggedToken = null;

			// Do not treat the held capture press as a sticky DPad/stick nav edge after purple ends.
			_stickAxisLatched = 0;
			_repeatArmed = false;
			_dpadLatched = 0;
			_dpadRepeatArmed = false;

			if (accepted && slot >= 1 && slot <= 3)
			{
				_focus = slot switch
				{
					1 => ComboPanelPadFocus.Slot1,
					2 => ComboPanelPadFocus.Slot2,
					_ => ComboPanelPadFocus.Slot3
				};
				_padNavVisible = true;
			}

			GamePadProbeSystem.LogPanelPadCaptureConsumeBegin(canonical, slot, accepted);
		}

		/// <summary>
		/// True while this frame's capture-consumed epoch is active, or the captured token is still Down.
		/// Blocks ComboPanel pad navigation / confirm / same-token Cancel only — not mouse/keyboard UI.
		/// May clear the gate on release. Diagnostics must use <see cref="CaptureReleaseGateActiveRaw"/> instead.
		/// </summary>
		public static bool IsCaptureInputBlocked()
		{
			if (!_captureReleaseGateActive)
				return false;

			int epoch = FnaGamePadProvider.InputTickEpoch;
			if (_captureConsumedEpoch == epoch)
				return true;

			if (string.IsNullOrEmpty(_captureReleaseToken) || !FnaGamePadProvider.IsDown(_captureReleaseToken))
			{
				string tok = _captureReleaseToken;
				string reason = string.IsNullOrEmpty(tok) ? "token-empty" : "token-up";
				// Clear BEFORE any diagnostic: NoteReleaseGate snapshots must not re-enter this method
				// while the gate is still marked active (stack overflow 0xc00000fd).
				ClearCaptureReleaseGate(logEnd: true);
				return false;
			}

			return true;
		}

		/// <summary>
		/// During capture RELEASE gate: allow Pad:B / Pad:Back cancel when that button was not the captured token.
		/// Same-token Cancel stays blocked until RELEASE (no double-fire after registering B itself).
		/// </summary>
		private static bool TryPadCancelWhileCaptureReleaseGate(IComboPanelPadHost host)
		{
			if (host == null || string.IsNullOrEmpty(_captureReleaseToken))
				return false;

			if (FnaGamePadProvider.WasJustPressed(GamePadToken.B)
				&& !string.Equals(_captureReleaseToken, GamePadToken.B, StringComparison.OrdinalIgnoreCase))
			{
				host.PadCancelOrBack(GamePadToken.B);
				return true;
			}

			if (FnaGamePadProvider.WasJustPressed(GamePadToken.Back)
				&& !string.Equals(_captureReleaseToken, GamePadToken.Back, StringComparison.OrdinalIgnoreCase))
			{
				host.PadCancelOrBack(GamePadToken.Back);
				return true;
			}

			return false;
		}

		private static void ClearCaptureReleaseGate(bool logEnd)
		{
			string tok = _captureReleaseToken;
			bool was = _captureReleaseGateActive;
			_captureReleaseGateActive = false;
			_captureReleaseToken = null;
			_captureConsumedEpoch = int.MinValue;
			if (logEnd && was && !string.IsNullOrEmpty(tok))
				GamePadProbeSystem.LogPanelPadCaptureReleaseGateEnd(tok);
		}

		private static void LogNavSkippedOnce(string reason)
		{
			int epoch = FnaGamePadProvider.InputTickEpoch;
			string tok = _captureReleaseToken ?? "-";
			if (_navSkipLoggedEpoch == epoch
				&& string.Equals(_navSkipLoggedToken, tok, StringComparison.OrdinalIgnoreCase))
				return;

			_navSkipLoggedEpoch = epoch;
			_navSkipLoggedToken = tok;
			GamePadProbeSystem.LogPanelPadNavSkipped(tok, reason);
		}

		/// <summary>
		/// Called from <see cref="UI.ComboPanel.Open"/> when the panel was opened by GamePad confirm.
		/// Focus stays None (no Slot auto-select / no purple wait); blocks open-token reuse until RELEASE.
		/// </summary>
		public static void BeginAfterGamePadOpen(string openToken)
		{
			if (!GamePadToken.TryCanonicalize(openToken, out string canonical))
				canonical = AkUiPadNavigationPolicy.ConfirmToken;

			_openGateToken = canonical;
			_openGateActive = true;
			_focus = ComboPanelPadFocus.None;
			_padNavVisible = false;
			_padSessionActive = true;
			_numberEditActive = false;
			_clearConfirmActive = false;
			_clearConfirmPadAHoldGate = false;
			_clearConfirmArmEpoch = int.MinValue;
			_lastHoldModeToggleEpoch = int.MinValue;
			_stickAxisLatched = 0;
			_repeatArmed = false;
			_dpadLatched = 0;
			_dpadRepeatArmed = false;
			ForceExitHoldSliderEdit();
			GamePadProbeSystem.LogPanelPadOpen(ComboPanelPadFocus.None, canonical);
		}

		public static void OnPhysicalMouseMoved(int clientX, int clientY)
		{
			if (!_hasPhysicalMouseSample)
			{
				_lastPhysicalMouseX = clientX;
				_lastPhysicalMouseY = clientY;
				_hasPhysicalMouseSample = true;
				return;
			}

			int dx = clientX - _lastPhysicalMouseX;
			int dy = clientY - _lastPhysicalMouseY;
			_lastPhysicalMouseX = clientX;
			_lastPhysicalMouseY = clientY;
			if (dx < 0) dx = -dx;
			if (dy < 0) dy = -dy;
			if (dx > 1 || dy > 1)
			{
				if (_padNavVisible)
					_padNavVisible = false;
			}
		}

		/// <summary>
		/// Tick from ComboPanel.DrawAndHandle while open and game-active.
		/// Returns true only when this frame consumed a pad edge/modal action
		/// (caller skips mouse HandleClicks that frame to avoid double-fire).
		/// Idle pad session / visible chrome without input must return false so hybrid mouse works.
		/// </summary>
		public static bool Tick(
			bool registrationWait,
			bool holdEditable,
			Func<ComboPanelPadFocus, Rectangle> resolveRect,
			IComboPanelPadHost host,
			bool twoKeySetupEligible = false)
		{
			if (host == null)
				return false;

			_skipSlot2Focus = twoKeySetupEligible;
			if (_skipSlot2Focus
				&& (_focus == ComboPanelPadFocus.HoldWhileHeld
					|| _focus == ComboPanelPadFocus.HoldOnRelease
					|| _focus == ComboPanelPadFocus.DoubleTap))
			{
				_focus = ComboPanelPadFocus.Slot1;
			}

			// 1) Open-button RELEASE gate
			PollOpenGate();

			// 2) Post-capture token RELEASE gate (accepted or rejected) — before pad nav/confirm.
			// Scope: only the captured Pad token's re-use for ComboPanel pad navigation / confirm / same-token Cancel.
			// Does not block physical mouse, keyboard, or Cancel(B) when B was not the captured token.
			if (IsCaptureInputBlocked())
			{
				if (TryPadCancelWhileCaptureReleaseGate(host))
					return true;

				LogNavSkippedOnce(
					_captureConsumedEpoch == FnaGamePadProvider.InputTickEpoch
						? "captured-this-epoch"
						: "capture-release-gate");
				return true;
			}

			var snap = FnaGamePadProvider.Current;
			if (snap.IsConnected
				&& !_openGateActive
				&& !_padSessionActive
				&& HasAnyPadNavigateOrConfirmEdge())
			{
				// Mouse-opened panel: first pad input starts a pad session without forcing Slot1.
				_padSessionActive = true;
			}

			if (_openGateActive)
				return true; // swallow all pad UI until release

			PollClearConfirmPadAHoldGate();

			// 3–5) Registration wait: capture only (chord cancel). Do not run normal nav here.
			// Ownership: while a slot is waiting for input, LS stick directions are Capture-only
			// (Pad:LS:* via CaptureArming). StickNavigate must not also move focus this frame.
			if (registrationWait)
			{
				if (_clearConfirmActive)
				{
					_clearConfirmActive = false;
					GamePadProbeSystem.LogPanelPadClearConfirmEnd(confirmed: false, focus: _focus);
					GamePadProbeSystem.LogPanelClearConfirmCancel("RegistrationWait");
				}

				if (TryChordCancelCapture(host))
					return true;
				return false;
			}

			// Clear confirm / number edit before Focus=None handling (exclusive).
			if (_clearConfirmActive)
			{
				HandleClearConfirm(host);
				return true;
			}

			if (_numberEditActive)
			{
				HandleNumberEdit(host);
				return true;
			}

			// Leaving HoldSlider while Editing: exit edit phase (keep current ms per policy).
			if (_focus != ComboPanelPadFocus.HoldSlider
				&& HoldSliderEdit.Phase != HoldSliderConfirmEditPolicy.Phase.Idle)
			{
				HoldSliderConfirmEditPolicy.Observe(
					HoldSliderEdit,
					new HoldSliderConfirmEditPolicy.FrameInput(
						sliderFocused: false,
						holdEditable: holdEditable));
			}

			// Focus=None: first direction enters focus; Pad:A must NOT start SlotCapture.
			if (_focus == ComboPanelPadFocus.None)
			{
				if (TryEnterFocusFromNone(snap))
					return true;

				if (FnaGamePadProvider.WasJustPressed(GamePadToken.A))
				{
					host.PlayDeniedTick();
					return true;
				}

				if (FnaGamePadProvider.WasJustPressed(GamePadToken.B))
				{
					host.PadCancelOrBack(GamePadToken.B);
					return true;
				}

				// Pad session idle (incl. GamePad-opened): do NOT block physical mouse.
				return false;
			}

			if (!_padNavVisible)
			{
				// Focus retained but chrome hidden by mouse move — direction/A re-shows chrome only.
				if (HasAnyPadNavigateOrConfirmEdge() || HasStickNavigateEdge(snap))
					_padNavVisible = true;
				else
					return false;
			}

			// HoldSlider confirm-edit: value changes only while Editing (A to enter/commit).
			if (_focus == ComboPanelPadFocus.HoldSlider && holdEditable)
			{
				if (TryTickHoldSliderConfirmEdit(snap, host, out bool blockFocusMove))
					return true;

				if (blockFocusMove)
				{
					// Editing: horizontal is value-only; still allow vertical focus leave via TryMoveFocus.
					if (TryMoveFocus(snap, holdEditable, lockHorizontal: true))
						return true;
					return false;
				}
			}

			if (TryMoveFocus(snap, holdEditable))
				return true;

			if (FnaGamePadProvider.WasJustPressed(GamePadToken.A))
			{
				// Same physical Pad:A that armed/committed Clear must not re-enter ConfirmFocus.
				if (_clearConfirmPadAHoldGate)
				{
					GamePadProbeSystem.LogPanelClearDoubleUseBlocked(GamePadToken.A);
					return true;
				}

				ConfirmFocus(host, holdEditable);
				return true;
			}

			if (FnaGamePadProvider.WasJustPressed(GamePadToken.B))
			{
				host.PadCancelOrBack(GamePadToken.B);
				return true;
			}

			_ = resolveRect;
			// Pad chrome may be visible, but idle frames must allow hybrid mouse (Save / outside / slots).
			return false;
		}

		private static bool TryTickHoldSliderConfirmEdit(
			GamePadDigitalSnapshot snap,
			IComboPanelPadHost host,
			out bool blockFocusMove)
		{
			blockFocusMove = false;
			HoldSliderEdit.CurrentMs = host.CurrentHoldMs;
			HoldSliderEdit.MinMs = host.DurationSliderMinMs;
			HoldSliderEdit.MaxMs = host.DurationSliderMaxMs;

			int hDir = ReadDpadHorizontalWithRepeat();
			if (hDir == 0)
				hDir = ReadRepeatableHorizontal(snap);

			bool confirm = FnaGamePadProvider.WasJustPressed(GamePadToken.A);
			bool confirmHeld = FnaGamePadProvider.IsDown(GamePadToken.A);
			bool cancel = FnaGamePadProvider.WasJustPressed(GamePadToken.B)
				|| FnaGamePadProvider.WasJustPressed(GamePadToken.Back);

			var result = HoldSliderConfirmEditPolicy.Observe(
				HoldSliderEdit,
				new HoldSliderConfirmEditPolicy.FrameInput(
					sliderFocused: true,
					holdEditable: true,
					confirmPressed: confirm,
					cancelPressed: cancel,
					horizontalDir: hDir,
					verticalDir: 0,
					horizontalAnalogAboveDeadzone: hDir != 0,
					inputEpoch: FnaGamePadProvider.InputTickEpoch,
					nowMs: Environment.TickCount64,
					confirmHeld: confirmHeld));

			blockFocusMove = result.BlockFocusMove;

			switch (result.Action)
			{
				case HoldSliderConfirmEditPolicy.Action.EnterEdit:
				case HoldSliderConfirmEditPolicy.Action.Commit:
					return true;
				case HoldSliderConfirmEditPolicy.Action.AdjustValue:
				{
					int before = host.CurrentHoldMs;
					host.SetSliderMs(result.NewMs);
					GamePadProbeSystem.LogPanelPadNumberChange(
						before,
						host.CurrentHoldMs,
						hDir < 0 ? "SliderLeft" : "SliderRight");
					return true;
				}
				case HoldSliderConfirmEditPolicy.Action.CancelRestore:
					host.SetHoldMs(result.NewMs);
					return true;
				case HoldSliderConfirmEditPolicy.Action.MoveFocusHorizontal:
					if (hDir != 0)
					{
						ComboPanelPadFocus from = _focus;
						ComboPanelPadFocus to = StepFocus(from, hDir, 0, holdEditable: true);
						if (to != from)
						{
							_focus = to;
							GamePadProbeSystem.LogPanelPadFocus(
								from, to, hDir < 0 ? "Left" : "Right");
						}

						return true;
					}

					return false;
				default:
					if (result.BlockPanelCancel && cancel)
						return true;
					return HoldSliderEdit.ConsumedConfirmThisFrame || HoldSliderEdit.ConsumedCancelThisFrame;
			}
		}

		public static Color? TryGetFocusBorder(ComboPanelPadFocus target, Color capturingBorder, bool capturing)
		{
			if (capturing)
				return capturingBorder;
			if (!IsPadNavVisible || _focus != target)
				return null;
			if (_clearConfirmActive && target == ComboPanelPadFocus.Clear)
				return new Color(255, 120, 80);
			if (_numberEditActive && target == ComboPanelPadFocus.HoldNumber)
				return new Color(120, 220, 255);
			if (target == ComboPanelPadFocus.HoldSlider && IsHoldSliderEditing)
				return Main.OurFavoriteColor;
			return new Color(80, 200, 255);
		}

		public static bool IsFocused(ComboPanelPadFocus target)
			=> IsPadNavVisible && _focus == target;

		private static void PollOpenGate()
		{
			if (!_openGateActive)
				return;
			if (string.IsNullOrEmpty(_openGateToken) || !FnaGamePadProvider.IsDown(_openGateToken))
			{
				string tok = _openGateToken;
				_openGateActive = false;
				_openGateToken = null;
				GamePadProbeSystem.LogPanelPadOpenGateEnd(tok ?? "Pad:A");
			}
		}

		private static bool HasAnyPadNavigateOrConfirmEdge()
		{
			return FnaGamePadProvider.WasJustPressed(GamePadToken.A)
				|| FnaGamePadProvider.WasJustPressed(GamePadToken.B)
				|| FnaGamePadProvider.WasJustPressed(GamePadToken.DPadUp)
				|| FnaGamePadProvider.WasJustPressed(GamePadToken.DPadDown)
				|| FnaGamePadProvider.WasJustPressed(GamePadToken.DPadLeft)
				|| FnaGamePadProvider.WasJustPressed(GamePadToken.DPadRight)
				|| FnaGamePadProvider.WasJustPressed(GamePadToken.LeftShoulder)
				|| FnaGamePadProvider.WasJustPressed(GamePadToken.RightShoulder);
		}

		private static bool TryChordCancelCapture(IComboPanelPadHost host)
		{
			// Dedicated: Back + Start held together (same tick both down, either just pressed).
			bool back = FnaGamePadProvider.IsDown(GamePadToken.Back);
			bool start = FnaGamePadProvider.IsDown(GamePadToken.Start);
			if (!(back && start))
				return false;
			if (!FnaGamePadProvider.WasJustPressed(GamePadToken.Back)
				&& !FnaGamePadProvider.WasJustPressed(GamePadToken.Start))
				return false;

			host.CancelSlotCaptureKeepFocus();
			_clearConfirmActive = false;
			_numberEditActive = false;
			// Keep current Slot focus if capturing a slot; do not invent Slot1 from None/Clear/etc.
			if (_focus is >= ComboPanelPadFocus.Slot1 and <= ComboPanelPadFocus.Slot3)
				_padNavVisible = true;
			GamePadProbeSystem.LogPanelPadCaptureEnd(SlotIndexFromFocus(_focus), null, accepted: false, note: "chord-Back+Start-cancel");
			return true;
		}

		private static void PollClearConfirmPadAHoldGate()
		{
			if (!_clearConfirmPadAHoldGate)
				return;
			if (FnaGamePadProvider.IsDown(GamePadToken.A)
				|| FnaGamePadProvider.WasJustPressed(GamePadToken.A))
				return;

			_clearConfirmPadAHoldGate = false;
			if (_clearConfirmActive)
				GamePadProbeSystem.LogPanelClearConfirmReleaseReady(FnaGamePadProvider.InputTickEpoch);
		}

		private static void HandleClearConfirm(IComboPanelPadHost host)
		{
			// Pad:B cancels confirm only — never mutates bindings.
			if (FnaGamePadProvider.WasJustPressed(GamePadToken.B))
			{
				_clearConfirmActive = false;
				_clearConfirmPadAHoldGate = false;
				_clearConfirmArmEpoch = int.MinValue;
				_focus = ComboPanelPadFocus.Clear;
				_padNavVisible = true;
				GamePadProbeSystem.LogPanelClearConfirmCancel(GamePadToken.B);
				GamePadProbeSystem.LogPanelPadClearConfirmEnd(confirmed: false, focus: ComboPanelPadFocus.Clear);
				return;
			}

			// Arming press must RELEASE before Commit is allowed.
			if (_clearConfirmPadAHoldGate)
			{
				if (FnaGamePadProvider.WasJustPressed(GamePadToken.A)
					|| FnaGamePadProvider.IsDown(GamePadToken.A))
				{
					if (FnaGamePadProvider.WasJustPressed(GamePadToken.A)
						&& FnaGamePadProvider.InputTickEpoch == _clearConfirmArmEpoch)
						GamePadProbeSystem.LogPanelClearDoubleUseBlocked(GamePadToken.A);
					return;
				}

				_clearConfirmPadAHoldGate = false;
				GamePadProbeSystem.LogPanelClearConfirmReleaseReady(FnaGamePadProvider.InputTickEpoch);
			}

			if (FnaGamePadProvider.WasJustPressed(GamePadToken.A))
			{
				host.ExecuteClear();
				_clearConfirmActive = false;
				_clearConfirmPadAHoldGate = true; // swallow until RELEASE so Tick cannot re-Arm
				_clearConfirmArmEpoch = FnaGamePadProvider.InputTickEpoch;
				_focus = ComboPanelPadFocus.Clear;
				_padNavVisible = true;
				GamePadProbeSystem.LogPanelClearConfirmCommit(
					GamePadToken.A,
					bindingChanged: true,
					FnaGamePadProvider.InputTickEpoch);
				GamePadProbeSystem.LogPanelPadClearConfirmEnd(confirmed: true, focus: ComboPanelPadFocus.Clear);
			}
		}

		private static void HandleNumberEdit(IComboPanelPadHost host)
		{
			var snap = FnaGamePadProvider.Current;
			int before = host.CurrentHoldMs;

			if (FnaGamePadProvider.WasJustPressed(GamePadToken.A))
			{
				_numberEditActive = false;
				GamePadProbeSystem.LogPanelPadNumberEditEnd(host.CurrentHoldMs, committed: true);
				return;
			}

			if (FnaGamePadProvider.WasJustPressed(GamePadToken.B))
			{
				host.SetHoldMs(_numberEditBaselineMs);
				_numberEditActive = false;
				GamePadProbeSystem.LogPanelPadNumberEditEnd(_numberEditBaselineMs, committed: false);
				return;
			}

			int delta = 0;
			if (FnaGamePadProvider.WasJustPressed(GamePadToken.LeftShoulder))
				delta = -500;
			else if (FnaGamePadProvider.WasJustPressed(GamePadToken.RightShoulder))
				delta = 500;
			else
			{
				int h = ReadDpadHorizontalWithRepeat();
				int v = 0;
				if (h == 0)
					v = ReadDpadVerticalWithRepeat();
				if (h == 0 && v == 0)
				{
					h = ReadRepeatableHorizontal(snap);
					v = ReadRepeatableVertical(snap);
				}

				if (h != 0)
					delta = h * 10;
				else if (v != 0)
					delta = v * 100;
			}

			if (delta != 0)
			{
				host.AdjustHoldMs(delta);
				GamePadProbeSystem.LogPanelPadNumberChange(before, host.CurrentHoldMs, "nav");
			}
		}

		private static void ConfirmFocus(IComboPanelPadHost host, bool holdEditable)
		{
			GamePadProbeSystem.LogPanelPadConfirm(_focus);
			switch (_focus)
			{
				case ComboPanelPadFocus.Slot1:
				case ComboPanelPadFocus.Slot2:
				case ComboPanelPadFocus.Slot3:
					if (_clearConfirmActive || _numberEditActive)
					{
						GamePadProbeSystem.LogPanelPadUnexpectedCaptureBegin(_focus);
						break;
					}
					if (_skipSlot2Focus && _focus == ComboPanelPadFocus.Slot2)
					{
						host.PlayDeniedTick();
						break;
					}
					int slot = SlotIndexFromFocus(_focus);
					host.StartSlotCapture(slot);
					GamePadProbeSystem.LogPanelPadCaptureBegin(slot);
					break;
				case ComboPanelPadFocus.HoldWhileHeld:
					if (_skipSlot2Focus)
						break;
					if (!ComboPanelHoldPadConfirmPolicy.ShouldApplyHoldModeToggle(
						    FnaGamePadProvider.InputTickEpoch, _lastHoldModeToggleEpoch))
						break;
					_lastHoldModeToggleEpoch = FnaGamePadProvider.InputTickEpoch;
					host.ToggleHoldWhileHeld();
					break;
				case ComboPanelPadFocus.HoldOnRelease:
					if (_skipSlot2Focus)
						break;
					if (!ComboPanelHoldPadConfirmPolicy.ShouldApplyHoldModeToggle(
					    FnaGamePadProvider.InputTickEpoch, _lastHoldModeToggleEpoch))
						break;
					_lastHoldModeToggleEpoch = FnaGamePadProvider.InputTickEpoch;
					host.ToggleHoldOnRelease();
					break;
				case ComboPanelPadFocus.DoubleTap:
					if (_skipSlot2Focus)
						break;
					if (!ComboPanelHoldPadConfirmPolicy.ShouldApplyHoldModeToggle(
						    FnaGamePadProvider.InputTickEpoch, _lastHoldModeToggleEpoch))
						break;
					_lastHoldModeToggleEpoch = FnaGamePadProvider.InputTickEpoch;
					host.ToggleDoubleTap();
					break;
				case ComboPanelPadFocus.HoldDefault:
					if (holdEditable)
						host.ResetHoldToDefault();
					else
						host.PlayDeniedTick();
					break;
				case ComboPanelPadFocus.HoldSlider:
					// Confirm-edit Enter/Commit is handled in TryTickHoldSliderConfirmEdit.
					break;
				case ComboPanelPadFocus.HoldNumber:
					if (holdEditable)
					{
						_numberEditBaselineMs = host.CurrentHoldMs;
						_numberEditActive = true;
						GamePadProbeSystem.LogPanelPadNumberEditBegin(_numberEditBaselineMs);
					}
					else
						host.PlayDeniedTick();
					break;
				case ComboPanelPadFocus.Clear:
					// Display-only confirm — do not mutate Slot/Hold/BindingStorage here.
					// Second Pad:A (after release) commits via HandleClearConfirm → ExecuteClear.
					_clearConfirmActive = true;
					_clearConfirmPadAHoldGate = true;
					_clearConfirmArmEpoch = FnaGamePadProvider.InputTickEpoch;
					_focus = ComboPanelPadFocus.Clear;
					_padNavVisible = true;
					GamePadProbeSystem.LogPanelClearConfirmArm(
						GamePadToken.A,
						bindingChanged: false,
						_clearConfirmArmEpoch);
					GamePadProbeSystem.LogPanelPadClearConfirmBegin(ComboPanelPadFocus.Clear);
					break;
				case ComboPanelPadFocus.Save:
					host.PadSaveAndClose();
					break;
				case ComboPanelPadFocus.Cancel:
					host.PadUndoLocalEdit();
					break;
			}
		}

		/// <summary>
		/// First DPad/stick direction from Focus=None places focus (no purple wait).
		/// Right→Slot1, Left→HoldWhileHeld, Up→Cancel, Down→Clear.
		/// </summary>
		private static bool TryEnterFocusFromNone(GamePadDigitalSnapshot snap)
		{
			int h = 0;
			int v = 0;

			if (FnaGamePadProvider.WasJustPressed(GamePadToken.DPadLeft))
				h = -1;
			else if (FnaGamePadProvider.WasJustPressed(GamePadToken.DPadRight))
				h = 1;
			else if (FnaGamePadProvider.WasJustPressed(GamePadToken.DPadUp))
				v = 1;
			else if (FnaGamePadProvider.WasJustPressed(GamePadToken.DPadDown))
				v = -1;
			else
			{
				h = ReadRepeatableHorizontal(snap);
				v = ReadRepeatableVertical(snap);
			}

			if (h == 0 && v == 0)
				return false;

			ComboPanelPadFocus to;
			string input;
			if (h > 0)
			{
				to = ComboPanelPadFocus.Slot1;
				input = "Right";
			}
			else if (h < 0)
			{
				to = TwoKeyPadHoldFocusPolicy.EnterFromNoneLeft(_skipSlot2Focus);
				input = "Left";
			}
			else if (v > 0)
			{
				to = ComboPanelPadFocus.Cancel;
				input = "Up";
			}
			else
			{
				to = ComboPanelPadFocus.Clear;
				input = "Down";
			}

			_focus = to;
			_padNavVisible = true;
			_padSessionActive = true;
			GamePadProbeSystem.LogPanelPadFocus(ComboPanelPadFocus.None, to, input);
			ComboPanelBackgroundInputBlock.LogOwnership(
				"Pad:Nav:" + input,
				"ComboPanelNavigation",
				parentBlocked: true,
				gameplayBlocked: true);
			return true;
		}

		private static bool HasStickNavigateEdge(GamePadDigitalSnapshot snap)
		{
			float x = snap?.LeftStickX ?? 0f;
			float y = snap?.LeftStickY ?? 0f;
			if (x < 0) x = -x;
			if (y < 0) y = -y;
			return x >= StickNavigateThreshold || y >= StickNavigateThreshold;
		}

		private static bool TryMoveFocus(GamePadDigitalSnapshot snap, bool holdEditable, bool lockHorizontal = false)
		{
			int h = 0;
			int v = 0;

			// On HoldSlider while Editing, horizontal is value-adjust; only vertical moves focus.
			bool sliderLocksHorizontal = lockHorizontal
				|| (_focus == ComboPanelPadFocus.HoldSlider && holdEditable && IsHoldSliderEditing);

			if (!sliderLocksHorizontal)
				h = ReadDpadHorizontalWithRepeat();
			if (h == 0)
				v = ReadDpadVerticalWithRepeat();

			if (h == 0 && v == 0)
			{
				if (!sliderLocksHorizontal)
					h = ReadRepeatableHorizontal(snap);
				v = ReadRepeatableVertical(snap);
			}

			if (h == 0 && v == 0)
				return false;

			ComboPanelPadFocus from = _focus;
			ComboPanelPadFocus to = StepFocus(from, h, v, holdEditable);
			if (to == from)
				return true;

			_focus = to;
			string moveInput = h != 0 ? (h < 0 ? "Left" : "Right") : (v > 0 ? "Up" : "Down");
			GamePadProbeSystem.LogPanelPadFocus(from, to, moveInput);
			ComboPanelBackgroundInputBlock.LogOwnership(
				"Pad:Nav:" + moveInput,
				"ComboPanelNavigation",
				parentBlocked: true,
				gameplayBlocked: true);
			return true;
		}

		/// <summary>DPad L/R: 1 press = 1 step; hold repeats after InitialRepeatDelayMs.</summary>
		private static int ReadDpadHorizontalWithRepeat()
		{
			bool left = FnaGamePadProvider.IsDown(GamePadToken.DPadLeft);
			bool right = FnaGamePadProvider.IsDown(GamePadToken.DPadRight);
			if (left == right)
			{
				if (_dpadLatched == -1 || _dpadLatched == 1)
				{
					_dpadLatched = 0;
					_dpadRepeatArmed = false;
				}
				return 0;
			}

			int dir = left ? -1 : 1;
			return PulseDigitalLatch(dir, latchCode: dir);
		}

		/// <summary>DPad U/D in screen space (+up / −down).</summary>
		private static int ReadDpadVerticalWithRepeat()
		{
			bool up = FnaGamePadProvider.IsDown(GamePadToken.DPadUp);
			bool down = FnaGamePadProvider.IsDown(GamePadToken.DPadDown);
			if (up == down)
			{
				if (_dpadLatched == 2 || _dpadLatched == -2)
				{
					_dpadLatched = 0;
					_dpadRepeatArmed = false;
				}
				return 0;
			}

			int dir = up ? 1 : -1;
			return PulseDigitalLatch(dir, latchCode: dir * 2);
		}

		private static int PulseDigitalLatch(int dir, int latchCode)
		{
			long now = Environment.TickCount64;
			if (_dpadLatched != latchCode)
			{
				_dpadLatched = latchCode;
				_dpadRepeatArmed = true;
				_dpadNextRepeatMs = now + InitialRepeatDelayMs;
				return dir;
			}

			if (!_dpadRepeatArmed || now < _dpadNextRepeatMs)
				return 0;

			_dpadNextRepeatMs = now + RepeatIntervalMs;
			return dir;
		}

		private static ComboPanelPadFocus StepFocus(ComboPanelPadFocus from, int h, int v, bool holdEditable)
		{
			bool skipSlot2 = _skipSlot2Focus;
			bool skipHoldMode = TwoKeyPadHoldFocusPolicy.ShouldSkipHoldModeBoxes(_skipSlot2Focus);

			// h: -1 left, +1 right; v: +1 up, -1 down (screen space)
			if (h < 0)
			{
				return from switch
				{
					ComboPanelPadFocus.Slot1 => TwoKeyPadHoldFocusPolicy.NeighborLeft(from, skipHoldMode),
					ComboPanelPadFocus.Slot2 => ComboPanelPadFocus.Slot1,
					ComboPanelPadFocus.Slot3 => skipSlot2 ? ComboPanelPadFocus.Slot1 : ComboPanelPadFocus.Slot2,
					ComboPanelPadFocus.HoldSlider => ComboPanelPadFocus.HoldDefault,
					ComboPanelPadFocus.HoldNumber => ComboPanelPadFocus.HoldSlider,
					ComboPanelPadFocus.Save => ComboPanelPadFocus.Clear,
					ComboPanelPadFocus.HoldWhileHeld => ComboPanelPadFocus.HoldWhileHeld,
					ComboPanelPadFocus.HoldOnRelease => ComboPanelPadFocus.HoldOnRelease,
					ComboPanelPadFocus.DoubleTap => ComboPanelPadFocus.DoubleTap,
					_ => from
				};
			}

			if (h > 0)
			{
				return from switch
				{
					ComboPanelPadFocus.HoldWhileHeld => ComboPanelPadFocus.Slot1,
					ComboPanelPadFocus.HoldOnRelease => ComboPanelPadFocus.Slot1,
					ComboPanelPadFocus.DoubleTap => ComboPanelPadFocus.Slot1,
					ComboPanelPadFocus.Slot1 => skipSlot2 ? ComboPanelPadFocus.Slot3 : ComboPanelPadFocus.Slot2,
					ComboPanelPadFocus.Slot2 => ComboPanelPadFocus.Slot3,
					ComboPanelPadFocus.Slot3 => holdEditable ? ComboPanelPadFocus.HoldNumber : ComboPanelPadFocus.Save,
					ComboPanelPadFocus.HoldDefault => ComboPanelPadFocus.HoldSlider,
					ComboPanelPadFocus.HoldSlider => ComboPanelPadFocus.HoldNumber,
					ComboPanelPadFocus.Clear => ComboPanelPadFocus.Save,
					_ => from
				};
			}

			if (v > 0) // up
			{
				return from switch
				{
					ComboPanelPadFocus.HoldWhileHeld => ComboPanelPadFocus.HoldDefault,
					ComboPanelPadFocus.HoldOnRelease => skipHoldMode ? ComboPanelPadFocus.Slot1 : ComboPanelPadFocus.HoldWhileHeld,
					ComboPanelPadFocus.DoubleTap => skipHoldMode ? ComboPanelPadFocus.Slot1 : ComboPanelPadFocus.HoldOnRelease,
					ComboPanelPadFocus.Slot1 => ComboPanelPadFocus.HoldSlider,
					ComboPanelPadFocus.Slot2 => ComboPanelPadFocus.HoldSlider,
					ComboPanelPadFocus.Slot3 => ComboPanelPadFocus.HoldNumber,
					ComboPanelPadFocus.HoldDefault => ComboPanelPadFocus.Cancel,
					ComboPanelPadFocus.HoldSlider => ComboPanelPadFocus.Cancel,
					ComboPanelPadFocus.HoldNumber => ComboPanelPadFocus.Cancel,
					// Bottom of mode column is DoubleTap (not HoldOnRelease). Policy NeighborUp unchanged.
					ComboPanelPadFocus.Clear => skipHoldMode ? ComboPanelPadFocus.Slot1 : ComboPanelPadFocus.DoubleTap,
					ComboPanelPadFocus.Save => ComboPanelPadFocus.Slot3,
					_ => from
				};
			}

			if (v < 0) // down
			{
				return from switch
				{
					ComboPanelPadFocus.Cancel => ComboPanelPadFocus.HoldDefault,
					ComboPanelPadFocus.HoldDefault => TwoKeyPadHoldFocusPolicy.NeighborDown(from, skipHoldMode),
					ComboPanelPadFocus.HoldSlider => skipSlot2 ? ComboPanelPadFocus.Slot1 : ComboPanelPadFocus.Slot2,
					ComboPanelPadFocus.HoldNumber => ComboPanelPadFocus.Slot3,
					ComboPanelPadFocus.HoldWhileHeld => skipHoldMode ? ComboPanelPadFocus.Clear : ComboPanelPadFocus.HoldOnRelease,
					ComboPanelPadFocus.HoldOnRelease => skipHoldMode ? ComboPanelPadFocus.Clear : ComboPanelPadFocus.DoubleTap,
					ComboPanelPadFocus.DoubleTap => ComboPanelPadFocus.Clear,
					ComboPanelPadFocus.Slot1 => ComboPanelPadFocus.Clear,
					ComboPanelPadFocus.Slot2 => ComboPanelPadFocus.Clear,
					ComboPanelPadFocus.Slot3 => ComboPanelPadFocus.Save,
					_ => from
				};
			}

			return from;
		}

		/// <summary>Horizontal: −1 / 0 / +1 with stick latch + repeat.</summary>
		private static int ReadRepeatableHorizontal(GamePadDigitalSnapshot snap)
		{
			float x = snap?.LeftStickX ?? 0f;
			return ReadRepeatableAxis(x, horizontal: true);
		}

		private static int _lastLoggedStickVDir;

		/// <summary>Vertical: FNA ThumbStick Y positive = stick up → screen Up (+1), matching DPad.</summary>
		private static int ReadRepeatableVertical(GamePadDigitalSnapshot snap)
		{
			float rawY = snap?.LeftStickY ?? 0f;
			int dir = ReadRepeatableAxis(rawY, horizontal: false);
			if (dir == 0)
			{
				_lastLoggedStickVDir = 0;
				return 0;
			}

			if (dir != _lastLoggedStickVDir)
			{
				_lastLoggedStickVDir = dir;
				string logical = dir > 0 ? "Up" : "Down";
				bool mismatch = (rawY > StickNavigateThreshold && dir < 0)
					|| (rawY < -StickNavigateThreshold && dir > 0);
				GamePadProbeSystem.LogPanelLeftStickNav(rawY, rawY, logical, _focus.ToString(), null);
				if (mismatch)
				{
					GamePadProbeSystem.LogPanelLeftStickDirectionMismatch(
						rawY,
						expected: rawY > 0 ? "Up" : "Down",
						actual: logical);
				}
			}

			return dir;
		}

		private static int ReadRepeatableAxis(float value, bool horizontal)
		{
			int axisId = horizontal ? 1 : 2;
			float abs = value < 0 ? -value : value;

			if (abs < StickReleaseThreshold)
			{
				if (_stickAxisLatched == axisId)
				{
					_stickAxisLatched = 0;
					_repeatArmed = false;
				}
				return 0;
			}

			if (abs < StickNavigateThreshold)
				return 0;

			int dir = value < 0 ? -1 : 1;
			long now = Environment.TickCount64;

			if (_stickAxisLatched != axisId)
			{
				_stickAxisLatched = axisId;
				_repeatArmed = true;
				_nextRepeatMs = now + InitialRepeatDelayMs;
				return dir;
			}

			if (!_repeatArmed)
				return 0;

			if (now < _nextRepeatMs)
				return 0;

			_nextRepeatMs = now + RepeatIntervalMs;
			return dir;
		}

		private static int SlotIndexFromFocus(ComboPanelPadFocus focus)
			=> focus switch
			{
				ComboPanelPadFocus.Slot1 => 1,
				ComboPanelPadFocus.Slot2 => 2,
				ComboPanelPadFocus.Slot3 => 3,
				_ => 1
			};
	}

	/// <summary>Host actions implemented by ComboPanel (shared with mouse handlers).</summary>
	internal interface IComboPanelPadHost
	{
		int CurrentHoldMs { get; }
		/// <summary>Active duration slider minimum (Hold or Double Tap mode).</summary>
		int DurationSliderMinMs { get; }
		/// <summary>Active duration slider maximum (Hold or Double Tap mode).</summary>
		int DurationSliderMaxMs { get; }
		void StartSlotCapture(int slot);
		void CancelSlotCaptureKeepFocus();
		void ToggleHoldWhileHeld();
		void ToggleHoldOnRelease();
		void ToggleDoubleTap();
		void ResetHoldToDefault();
		void SetHoldMs(int ms);
		void SetSliderMs(int ms);
		void AdjustHoldMs(int delta);
		void ExecuteClear();
		void PadSaveAndClose();
		void PadCancelOrBack(string consumeToken = null);
		void PadUndoLocalEdit();
		void PlayDeniedTick();
	}
}
