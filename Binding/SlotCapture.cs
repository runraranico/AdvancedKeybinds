using System;
using System.Collections.Generic;
using AdvancedKeybinds.Patches;
using Terraria;

namespace AdvancedKeybinds.Binding
{
	/// <summary>
	/// Captures a single key or mouse button for one combo slot, mimicking vanilla's "click then press"
	/// flow without touching vanilla's listen state. On begin we snapshot everything already held (the
	/// click that opened capture, held modifiers, etc.) as a baseline; the first NEW press after that
	/// becomes the captured token. Toggle-lock keys (CapsLock, etc.) are detected by physical KEYDOWN
	/// edges only, not LED latch state (JP keyboards often need Shift+CapsLock to toggle the LED).
	/// </summary>
	internal static class SlotCapture
	{
		public enum PollResult
		{
			Waiting,
			Captured
		}

		public static bool IsCapturing { get; private set; }

		private static HashSet<string> _baseline = new(System.StringComparer.OrdinalIgnoreCase);
		private static bool _wasInactiveDuringCapture;

		public static void Begin()
		{
			WindowsKeySuppressor.DiscardStaleToggleLockCapture();
			WindowsKeySuppressor.SyncWindowsKeyStateFromOs();
			WindowsKeySuppressor.SetSlotCapturePassthrough(true);
			_baseline = KeyToken.SnapshotPressedTokens();
			_baseline.RemoveWhere(BindingRestrictions.IsToggleLockToken);
			_baseline.Remove("LeftWindows");
			_baseline.Remove("RightWindows");
			_wasInactiveDuringCapture = !Main.hasFocus;
			IsCapturing = true;
			AkDiag.Log("SlotCapture begin baseline=[" + string.Join(",", _baseline) + "] hook=" + WindowsKeySuppressor.IsInstalled);
		}

		public static void Cancel()
		{
			IsCapturing = false;
			_wasInactiveDuringCapture = false;
			_baseline.Clear();
			WindowsKeySuppressor.SetSlotCapturePassthrough(false);
			WindowsKeySuppressor.TryConsumeToggleLockPress(out _);
			WindowsKeySuppressor.TryConsumeEscChordCapture(out _);
			WindowsKeySuppressor.TryConsumeWinKeyCapture(out _);
		}

		/// <summary>
		/// Drops latch edges that arrived while Terraria was inactive so focus-loss (screenshot,
		/// other desktop click) cannot complete a capture. Leaves the waiting state intact.
		/// </summary>
		public static void DiscardPendingEdgesWhileInactive()
		{
			if (!IsCapturing)
				return;

			_wasInactiveDuringCapture = true;
			DrainPendingLatchEdges();
		}

		private static void DrainPendingLatchEdges()
		{
			WindowsKeySuppressor.TryConsumeWinKeyCapture(out _);
			WindowsKeySuppressor.TryConsumeEscChordCapture(out _);
			WindowsKeySuppressor.TryConsumeToggleLockPress(out _);
		}

		/// <summary>
		/// After focus returns, treat whatever is currently held (refocus click, etc.) as baseline
		/// so that click does not finish the purple "press a key" wait.
		/// </summary>
		private static void AbsorbPressedIntoBaselineAfterFocusRegain()
		{
			if (!_wasInactiveDuringCapture)
				return;

			_wasInactiveDuringCapture = false;
			DrainPendingLatchEdges();

			foreach (string held in KeyToken.SnapshotPressedTokens())
			{
				if (BindingRestrictions.IsToggleLockToken(held))
					continue;
				if (string.Equals(held, "LeftWindows", StringComparison.OrdinalIgnoreCase)
					|| string.Equals(held, "RightWindows", StringComparison.OrdinalIgnoreCase))
					continue;

				_baseline.Add(held);
			}

			AkDiag.Log("SlotCapture focus-regain absorbed baseline=[" + string.Join(",", _baseline) + "]");
		}

		public static PollResult Poll(out string token)
		{
			token = null;
			if (!IsCapturing)
				return PollResult.Waiting;

			// Never bind while the game window is inactive — physical keys/clicks on other
			// windows would otherwise complete capture (often as LeftWindows / mouse).
			if (!Main.hasFocus)
			{
				DiscardPendingEdgesWhileInactive();
				return PollResult.Waiting;
			}

			AbsorbPressedIntoBaselineAfterFocusRegain();

			if (WindowsKeySuppressor.TryConsumeWinKeyCapture(out token))
			{
				IsCapturing = false;
				_baseline.Clear();
				WindowsKeySuppressor.SetSlotCapturePassthrough(false);
				WindowsKeySuppressor.ArmWinKeyReleaseGuard();
				AkDiag.Log("SlotCapture captured win-key edge=" + token);
				return PollResult.Captured;
			}

			if (WindowsKeySuppressor.TryConsumeEscChordCapture(out token))
			{
				IsCapturing = false;
				_baseline.Clear();
				WindowsKeySuppressor.SetSlotCapturePassthrough(false);
				AkDiag.Log("SlotCapture captured ctrl+esc edge=" + token);
				return PollResult.Captured;
			}

			if (WindowsKeySuppressor.TryPollToggleLockEdge(out token))
			{
				IsCapturing = false;
				_baseline.Clear();
				WindowsKeySuppressor.SetSlotCapturePassthrough(false);
				AkDiag.Log("SlotCapture captured toggle-lock edge=" + token);
				return PollResult.Captured;
			}

			HashSet<string> pressed = KeyToken.SnapshotPressedTokens();

			// Drop baseline keys as they are released so they can be pressed fresh to bind.
			_baseline.RemoveWhere(k => !pressed.Contains(k));

			foreach (string candidate in pressed)
			{
				if (_baseline.Contains(candidate))
					continue;

				// Toggle-lock keys are only accepted via TryPollToggleLockEdge (physical press).
				if (BindingRestrictions.IsToggleLockToken(candidate))
					continue;

				// Physical mouse still reports clicks on other windows/desktops. Only accept
				// mouse tokens when the cursor is over the game client (not Main.mouseLeft —
				// the combo panel often clears that even for in-window clicks).
				if (KeyToken.TryParse(candidate, out _, out bool isMouse, out _)
					&& isMouse
					&& !MouseInputHelper.IsCursorInsideGameClient())
				{
					_baseline.Add(candidate);
					AkDiag.Log("SlotCapture ignored out-of-client mouse=" + candidate);
					continue;
				}

				token = candidate;
				IsCapturing = false;
				_baseline.Clear();
				WindowsKeySuppressor.SetSlotCapturePassthrough(false);
				if (string.Equals(candidate, "LeftWindows", StringComparison.OrdinalIgnoreCase)
					|| string.Equals(candidate, "RightWindows", StringComparison.OrdinalIgnoreCase))
				{
					WindowsKeySuppressor.ArmWinKeyReleaseGuard();
				}
				AkDiag.Log("SlotCapture captured=" + candidate);
				return PollResult.Captured;
			}

			return PollResult.Waiting;
		}
	}
}
