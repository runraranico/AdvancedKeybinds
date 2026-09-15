using System;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Pure helpers for ComboPanel registration-wait ↔ HOLD interaction diagnostics.
	/// No Terraria / FNA / file I/O — safe for Core.Tests.
	/// Does not change product capture / HOLD behavior; call sites decide whether to apply results.
	/// </summary>
	public static class ComboPanelRegistrationWaitDiagPolicy
	{
		public const int StuckWaitingForReleaseInputTickThreshold = 120;

		public static bool IsRegistrationWaitActive(bool awaitingSlotKey, bool slotCaptureIsCapturing)
			=> awaitingSlotKey || slotCaptureIsCapturing;

		/// <summary>
		/// Same gate ComboPanel uses before HandleClicks / pad ConfirmFocus HOLD toggles.
		/// </summary>
		public static bool ShouldAllowHoldToggle(
			bool registrationWaitActive,
			bool supportsHold,
			bool hasWorkingBinding)
		{
			if (!supportsHold)
				return false;
			if (!hasWorkingBinding)
				return false;
			if (registrationWaitActive)
				return false;
			return true;
		}

		public static string HoldToggleDeniedReason(
			bool registrationWaitActive,
			bool supportsHold,
			bool hasWorkingBinding)
		{
			if (!supportsHold)
				return "UnsupportedBinding";
			if (!hasWorkingBinding)
				return "NoWorkingBinding";
			if (registrationWaitActive)
				return "RegistrationWaitActive";
			return null;
		}

		/// <summary>
		/// Post-FinishKeyCapture invariants expected after a successful Pad capture completes.
		/// </summary>
		public static bool CaptureCompletedInvariantViolation(
			int capturingSlotAfter,
			bool awaitingSlotKeyAfter,
			bool slotCaptureIsCapturingAfter)
			=> capturingSlotAfter != 0
				|| awaitingSlotKeyAfter
				|| slotCaptureIsCapturingAfter;

		/// <summary>
		/// Warn only when the *same* WaitingForRelease remaining-set has spanned enough unique
		/// input ticks (not per-Poll increments inside one tick).
		/// </summary>
		public static bool ShouldWarnStuckWaitingForRelease(
			int uniqueInputTicksSameRemaining,
			int threshold = StuckWaitingForReleaseInputTickThreshold)
			=> uniqueInputTicksSameRemaining >= threshold;

		/// <summary>
		/// Advance unique-input-tick counter for stuck detection.
		/// Same remaining key + new input tick → +1. Remaining change → reset to 0.
		/// Same tick re-poll → no change (returns previous unique count).
		/// </summary>
		public static int NextUniqueInputTicksForStuckWarn(
			string remainingKey,
			string previousRemainingKey,
			int previousUniqueInputTicks,
			int inputTickEpoch,
			int previousInputTickEpoch,
			out string nextRemainingKey,
			out int nextInputTickEpoch)
		{
			if (!string.Equals(remainingKey ?? "", previousRemainingKey ?? "", StringComparison.Ordinal))
			{
				nextRemainingKey = remainingKey ?? "";
				nextInputTickEpoch = inputTickEpoch;
				return 0;
			}

			nextRemainingKey = previousRemainingKey ?? "";
			if (inputTickEpoch == previousInputTickEpoch)
			{
				nextInputTickEpoch = previousInputTickEpoch;
				return previousUniqueInputTicks;
			}

			nextInputTickEpoch = inputTickEpoch;
			unchecked
			{
				return previousUniqueInputTicks + 1;
			}
		}

		public static bool IsUnexpectedSameSlotRestart(
			bool hadRecentPadCaptureComplete,
			int previousCompletedSlot,
			int newSlot,
			bool registrationWaitJustEntered)
			=> registrationWaitJustEntered
				&& hadRecentPadCaptureComplete
				&& previousCompletedSlot >= 1
				&& previousCompletedSlot <= 3
				&& newSlot == previousCompletedSlot;
	}
}
