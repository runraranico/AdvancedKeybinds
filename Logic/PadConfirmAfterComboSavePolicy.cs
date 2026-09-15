using System;
using System.Collections.Generic;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Product contract for Controls GamePad row confirm after ComboPanel save.
	/// UI confirm is fixed <see cref="GamePadToken.A"/> and must open without MouseLeft / ItemUse.
	/// ItemUse KeyStatus (e.g. Pad:X + R2) must never become UI confirm or complete a stale pending.
	/// </summary>
	public static class PadConfirmAfterComboSavePolicy
	{
		public const int CloseGateMinEpochsWhenNeverDown = 3;

		public const int PendingTimeoutEpochs = 120;

		// Diagnostic event names (read-only logging).
		public const string EventBeforeComboSave = "BeforeComboSave";
		public const string EventComboSaveAccepted = "ComboSaveAccepted";
		public const string EventAfterComboSave = "AfterComboSave";
		public const string EventReturnedToKeybindList = "ReturnedToKeybindList";
		public const string EventFocusChanged = "FocusChanged";
		public const string EventSuppressionEntered = "SuppressionEntered";
		public const string EventSuppressionReleased = "SuppressionReleased";
		public const string EventPadAObserved = "PadAObserved";
		public const string EventPadAConfirmAccepted = "PadAConfirmAccepted";
		public const string EventPadAConfirmBlocked = "PadAConfirmBlocked";
		public const string EventUnexpectedComboPanelReopened = "UnexpectedComboPanelReopened";

		/// <summary>UI confirm token never follows ItemUse KeyStatus.</summary>
		public static string UiConfirmTokenRegardlessOfItemUse(IReadOnlyList<string> itemUseStyleTokens)
			=> AkUiPadNavigationPolicy.ConfirmToken;

		public static bool WouldItemUseOrRaiseMouseLeft(
			IReadOnlyList<string> itemUseStyleTokens,
			string pressedToken)
		{
			if (itemUseStyleTokens == null || string.IsNullOrEmpty(pressedToken))
				return false;

			for (int i = 0; i < itemUseStyleTokens.Count; i++)
			{
				if (string.Equals(itemUseStyleTokens[i], pressedToken, StringComparison.OrdinalIgnoreCase))
					return true;
			}

			return false;
		}

		public static void CollectItemUseStyleTokens(
			string alternateToken,
			string primaryToken,
			List<string> destination)
		{
			if (destination == null)
				return;
			destination.Clear();
			if (!string.IsNullOrEmpty(alternateToken))
				destination.Add(alternateToken);
			if (!string.IsNullOrEmpty(primaryToken)
				&& (destination.Count == 0
					|| !string.Equals(destination[0], primaryToken, StringComparison.OrdinalIgnoreCase)))
			{
				destination.Add(primaryToken);
			}
		}

		/// <summary>
		/// Product open path: Pad:A JustPressed alone is enough (no MouseLeft OnClick required).
		/// </summary>
		public static string ClassifyDirectOpenAttempt(
			bool closeInputConsumeGateBlocking,
			bool saveCloseEpochGateBlocking,
			bool padAJustPressed,
			bool focusIsBindingRow,
			bool comboPanelAlreadyOpen,
			bool gamePadSubsystemEnabled,
			bool alreadyOpenedThisConfirmHold)
		{
			if (!gamePadSubsystemEnabled)
				return "blockedBy=GamePadSubsystemOff";
			if (closeInputConsumeGateBlocking)
				return "blockedBy=CloseInputConsumeGate";
			if (saveCloseEpochGateBlocking)
				return "blockedBy=SaveCloseEpochGate";
			if (comboPanelAlreadyOpen)
				return "blockedBy=ComboPanelAlreadyOpen";
			if (!focusIsBindingRow)
				return "blockedBy=FocusNotBindingRow";
			if (alreadyOpenedThisConfirmHold)
				return "blockedBy=AlreadyOpenedThisHold";
			if (!padAJustPressed)
				return "blockedBy=NoPadAJustPressed";
			return "accepted=DirectPadAOpen";
		}

		/// <summary>
		/// Legacy classifier kept for gate/telemetry tests. Direct open is preferred product path.
		/// </summary>
		public static string ClassifyOpenAttempt(
			bool closeInputConsumeGateBlocking,
			bool saveCloseEpochGateBlocking,
			bool padAJustPressed,
			bool pendingActive,
			bool mouseLeftClickArrived,
			bool focusIsBindingRow,
			bool comboPanelAlreadyOpen)
		{
			if (closeInputConsumeGateBlocking)
				return "blockedBy=CloseInputConsumeGate";
			if (saveCloseEpochGateBlocking)
				return "blockedBy=SaveCloseEpochGate";
			if (comboPanelAlreadyOpen)
				return "blockedBy=ComboPanelAlreadyOpen";
			if (!focusIsBindingRow)
				return "blockedBy=FocusNotBindingRow";

			// Product: Pad:A edge opens directly — MouseLeft is not required.
			if (padAJustPressed)
				return "accepted=DirectPadAOpen";

			// Stale pending + ItemUse MouseLeft must never complete a Pad:A confirm.
			if (pendingActive && mouseLeftClickArrived && !padAJustPressed)
				return "blockedBy=StalePendingItemUseMouseLeftRejected";

			if (pendingActive && !mouseLeftClickArrived)
				return "blockedBy=PendingWithoutConfirmEdge";

			return "blockedBy=NoTrustedConfirmEdge";
		}

		/// <summary>
		/// OnClick / MouseLeft must never finish a Pad:A pending that is waiting for ItemUse.
		/// Direct-open design clears pending immediately; if any pending remains, require Pad:A gesture.
		/// </summary>
		public static bool ShouldAcceptPendingConsumeFromOnClick(
			bool pendingActive,
			bool padAConfirmGestureActive,
			bool mouseLeftArrived)
		{
			if (!pendingActive || !mouseLeftArrived)
				return false;
			// Never accept ItemUse-only MouseLeft as Pad:A confirm completion.
			return padAConfirmGestureActive;
		}

		public static bool ShouldDiscardPending(
			bool padAReleased,
			bool cancelPressed,
			bool panelClosedOrClosing,
			bool focusChanged,
			bool targetUiInvalid,
			bool timedOut,
			bool modUnloadOrReload)
		{
			return padAReleased
				|| cancelPressed
				|| panelClosedOrClosing
				|| focusChanged
				|| targetUiInvalid
				|| timedOut
				|| modUnloadOrReload;
		}

		public static bool CloseGateShouldReleaseAfterPadAUp(
			bool gateActive,
			bool padADown,
			bool seenDownAfterArm,
			int epochsSinceArm)
		{
			if (!gateActive)
				return true;
			if (seenDownAfterArm && !padADown)
				return true;
			if (!seenDownAfterArm && epochsSinceArm >= CloseGateMinEpochsWhenNeverDown)
				return true;
			return false;
		}

		public static bool PostReleaseBlockStillActive(int epochsSincePadAUp)
			=> epochsSincePadAUp < 0;

		public static bool IsPadXUiConfirm() => false;

		public static bool DoItemUseTokensPromoteToUiConfirm(IReadOnlyList<string> itemUseStyleTokens)
			=> false;

		public static int CountUiConfirmEdgesThisTick(
			bool padAJustPressed,
			bool padXJustPressed,
			bool r2JustPressed)
			=> padAJustPressed ? 1 : 0;

		public static bool ShouldOpenFromPadXOrR2Alone(bool padXJustPressed, bool r2JustPressed)
			=> false;

		public static bool KeyboardMouseConfirmUnchanged() => true;

		/// <summary>
		/// After the product fix, stale pending + ItemUse MouseLeft must never open.
		/// Kept for diagnostic classification (always false under the fixed contract).
		/// </summary>
		public static bool CanOpenViaStalePendingAndItemUseMouseLeft(
			bool pendingActive,
			bool mouseLeftFromItemUse)
			=> false;
	}
}
