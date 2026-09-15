namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Session-scoped Controls list vertical scroll (UIList.ViewPosition).
	/// Valid only while ManageControls is open — cleared on screen close.
	/// </summary>
	public static class ControlsListScrollSessionPolicy
	{
		/// <summary>
		/// Clamp ViewPosition into [0, max]. Invalid inputs become 0.
		/// </summary>
		public static float ClampViewPosition(float viewPosition, float maxViewPosition)
		{
			if (float.IsNaN(viewPosition) || float.IsInfinity(viewPosition))
				return 0f;
			if (maxViewPosition < 0f || float.IsNaN(maxViewPosition) || float.IsInfinity(maxViewPosition))
				maxViewPosition = 0f;
			if (viewPosition < 0f)
				return 0f;
			if (viewPosition > maxViewPosition)
				return maxViewPosition;
			return viewPosition;
		}

		/// <summary>
		/// Do not learn ViewPosition from a wiped list on the rebuild frame (often 0).
		/// </summary>
		public static bool ShouldCaptureSessionViewPosition(bool rebuildThisFrame, bool listAvailable)
		{
			return listAvailable && !rebuildThisFrame;
		}

		/// <summary>
		/// Restore remembered ViewPosition after list rebuild / ComboPanel return.
		/// </summary>
		public static bool ShouldRestoreSessionViewPosition(bool sessionValid, bool rebuildThisFrame)
		{
			return sessionValid && rebuildThisFrame;
		}

		/// <summary>
		/// Center-follow is for intentional focus / Reset-column moves only.
		/// Rebuild + focus restore must not recenter (that jumps away from RS / prior scroll).
		/// </summary>
		public static bool ShouldAllowCenterFollow(
			bool manualScrollActive,
			bool focusChanged,
			bool restoredThisFrame,
			bool rebuildThisFrame,
			bool pendingResetVerticalScroll)
		{
			if (manualScrollActive)
				return false;
			if (rebuildThisFrame || restoredThisFrame)
				return false;
			return focusChanged || pendingResetVerticalScroll;
		}

		/// <summary>
		/// After rebuild / restore: keep selection on-screen with edge scroll only.
		/// </summary>
		public static bool ShouldEnsureEdgeVisibilityAfterRebuild(
			bool rebuildThisFrame,
			bool restoredThisFrame)
		{
			return rebuildThisFrame || restoredThisFrame;
		}
	}
}
