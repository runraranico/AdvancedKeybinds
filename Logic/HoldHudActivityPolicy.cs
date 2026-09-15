namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// How much Hold HUD work a frame actually needs.
	/// </summary>
	public enum HoldHudActivityState : byte
	{
		/// <summary>Nothing to update, hit-test or draw. Coordinate upkeep is skipped entirely.</summary>
		Off = 0,

		/// <summary>Feature on but nothing to show yet — keep coordinate state warm, draw nothing.</summary>
		UpdateOnly = 1,

		/// <summary>At least one release-hold group is visible.</summary>
		Draw = 2,

		/// <summary>Custom placement is draggable / being dragged, so hit-test coordinates are required.</summary>
		DragEdit = 3
	}

	/// <summary>
	/// Pure Hold HUD activity classification — no Terraria, no coordinates, no drawing.
	/// Only decides how much work the frame needs; every coordinate formula stays where it was.
	/// </summary>
	public static class HoldHudActivityPolicy
	{
		public static HoldHudActivityState Resolve(
			bool inWorld,
			bool hudFeatureEnabled,
			bool dragAllowed,
			bool dragInProgress,
			bool hasVisibleHoldGroups)
		{
			// A drag still in flight must be able to finish / cancel even if the feature was just
			// switched off mid-drag, so it outranks the feature gate.
			if (dragInProgress)
				return HoldHudActivityState.DragEdit;

			if (!inWorld || !hudFeatureEnabled)
				return HoldHudActivityState.Off;

			if (dragAllowed)
				return HoldHudActivityState.DragEdit;

			return hasVisibleHoldGroups
				? HoldHudActivityState.Draw
				: HoldHudActivityState.UpdateOnly;
		}

		/// <summary>
		/// Gate used by the per-frame input hook, which cannot know yet whether any hold group will
		/// be visible. Off is the only state that skips surface refresh / custom-position sync.
		/// </summary>
		public static bool RequiresCoordinateUpkeep(
			bool inWorld,
			bool hudFeatureEnabled,
			bool dragInProgress)
			=> Resolve(inWorld, hudFeatureEnabled, dragAllowed: false, dragInProgress, hasVisibleHoldGroups: false)
				!= HoldHudActivityState.Off;

		/// <summary>
		/// True on the first frame that leaves <see cref="HoldHudActivityState.Off"/>. The cached
		/// game-pixel surface went stale while skipping, so it must be re-captured before any
		/// placement math runs (never reuse a surface captured before a resolution change).
		/// </summary>
		public static bool RequiresSurfaceRecaptureOnResume(bool wasOff, bool isOff)
			=> wasOff && !isOff;
	}
}
