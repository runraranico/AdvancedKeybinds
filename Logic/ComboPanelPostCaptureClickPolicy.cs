namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Post Pad:* capture click policy for ComboPanel.
	/// Blocks Steam Input / game-processed MouseLeft from re-opening the same slot while the
	/// capture RELEASE gate or UI reuse suppress is still active. Pure — no Terraria types.
	/// </summary>
	public static class ComboPanelPostCaptureClickPolicy
	{
		/// <summary>
		/// Whether a left-edge on a slot should start purple wait again after a Pad capture.
		/// Physical mouse is allowed only after both reuse-suppress and capture RELEASE gate clear.
		/// </summary>
		public static bool ShouldBlockSlotCaptureRestart(
			bool physicalLeftEdge,
			bool gameLeftEdge,
			bool uiReuseSuppressBlockingIdle,
			bool captureReleaseGateActive)
		{
			if (!physicalLeftEdge && !gameLeftEdge)
				return false;

			if (uiReuseSuppressBlockingIdle)
				return true;

			if (captureReleaseGateActive)
				return true;

			return false;
		}

		/// <summary>
		/// Historical DrawAndHandle padModal formula (investigation). Kept for regression docs.
		/// </summary>
		public static bool ProductionAllowsHandleClicksDuringCaptureReleaseGate(
			bool captureReleaseGateActive,
			bool openGateActive,
			bool clearConfirmActive,
			bool numberEditActive,
			bool padTickConsumed)
		{
			bool padModal = clearConfirmActive
				|| numberEditActive
				|| (padTickConsumed && !captureReleaseGateActive && !openGateActive);
			return !padModal;
		}

		public static bool IntendedBlocksIdleSlotClickWhilePadTokenHeld(
			bool uiReuseSuppressActive,
			bool registeredPadTokenStillDown)
			=> ComboPanelCaptureUiReuse.ShouldSkipIdleUiInput(
				uiReuseSuppressActive,
				registeredPadTokenStillDown);

		public static bool IntendedAllowsMouseEdgeToRestartCapture(
			bool captureReleaseGateActive,
			bool uiReuseSuppressActive,
			bool registeredPadTokenStillDown,
			bool mouse1OrGameLeftJustPressed,
			bool cursorInsideSameSlot)
		{
			if (!mouse1OrGameLeftJustPressed || !cursorInsideSameSlot)
				return false;

			if (ShouldBlockSlotCaptureRestart(
				physicalLeftEdge: mouse1OrGameLeftJustPressed,
				gameLeftEdge: mouse1OrGameLeftJustPressed,
				uiReuseSuppressBlockingIdle: IntendedBlocksIdleSlotClickWhilePadTokenHeld(
					uiReuseSuppressActive,
					registeredPadTokenStillDown),
				captureReleaseGateActive: captureReleaseGateActive))
				return false;

			return true;
		}

		/// <summary>
		/// Snapshot / poll recursion model: calling a poll that logs while building a log string recurses
		/// when the gate is active, token is up, and epoch differs (end path).
		/// </summary>
		public static bool WouldDiagRecurseIfSnapshotCallsIsCaptureInputBlocked(
			bool gateActive,
			bool tokenDown,
			bool sameConsumedEpoch,
			bool snapshotCallsIsCaptureInputBlocked)
		{
			if (!snapshotCallsIsCaptureInputBlocked)
				return false;
			if (!gateActive)
				return false;
			if (sameConsumedEpoch)
				return false;
			if (tokenDown)
				return false;
			// Poll end path would NoteReleaseGate → Snapshot → Poll again.
			return true;
		}
	}
}
