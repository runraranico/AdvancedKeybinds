namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// LIVE 2026-09-16 character ESC: two Player.ToggleInv on the same GameUpdateCount
	/// (before=0 after=1 twice, frame identical). Coalesce to one real toggle per frame.
	/// </summary>
	public static class ToggleInvSameFrameCoalescePolicy
	{
		/// <summary>
		/// Returns true when this ToggleInv call should run <c>orig</c>.
		/// False = duplicate same-frame call (skip orig).
		/// </summary>
		public static bool ShouldInvokeOrig(
			int gameUpdateCount,
			ref int lastSeenGameUpdate,
			ref bool alreadyInvokedThisUpdate)
		{
			if (lastSeenGameUpdate != gameUpdateCount)
			{
				lastSeenGameUpdate = gameUpdateCount;
				alreadyInvokedThisUpdate = false;
			}

			if (alreadyInvokedThisUpdate)
				return false;

			alreadyInvokedThisUpdate = true;
			return true;
		}

		public static bool ShouldInvokeOrigForContext(
			int gameUpdateCount,
			int inputTickEpoch,
			bool isMenu,
			ref int lastSeenGameUpdate,
			ref bool alreadyInvokedThisUpdate,
			ref int lastSeenInputTickEpoch,
			ref bool alreadyInvokedThisInputTick,
			ref bool menuInvokedThisInputTick)
		{
			if (inputTickEpoch != lastSeenInputTickEpoch)
			{
				lastSeenInputTickEpoch = inputTickEpoch;
				alreadyInvokedThisInputTick = false;
				menuInvokedThisInputTick = false;
			}

			if (isMenu)
			{
				if (alreadyInvokedThisInputTick)
					return false;
				alreadyInvokedThisInputTick = true;
				menuInvokedThisInputTick = true;
				return true;
			}

			if (menuInvokedThisInputTick)
				return false;

			bool allowed = ShouldInvokeOrig(gameUpdateCount, ref lastSeenGameUpdate, ref alreadyInvokedThisUpdate);
			if (allowed)
				alreadyInvokedThisInputTick = true;
			return allowed;
		}

		/// <summary>
		/// LIVE stroke oracle (legacy): ToggleInv count must not exceed Escape rising edges.
		/// Insufficient alone — flicker can produce edges=20 / toggles=20 and still "pass".
		/// Prefer <see cref="IsSingleTogglePerPhysicalEscapeStroke"/>.
		/// </summary>
		public static bool IsSingleTogglePerEscapeEdge(
			int escapeRisingEdgeCount,
			int toggleInvCallCount)
		{
			if (escapeRisingEdgeCount <= 0)
				return toggleInvCallCount == 0;
			return toggleInvCallCount <= escapeRisingEdgeCount;
		}

		/// <summary>
		/// One physical Escape stroke (ESC_DOWN…sustained ESC_UP) must ToggleInv at most once,
		/// even when KeyState flicker reports many rising edges mid-hold.
		/// </summary>
		public static bool IsSingleTogglePerPhysicalEscapeStroke(
			int escapeRisingEdgeCount,
			int toggleInvCallCount)
		{
			if (escapeRisingEdgeCount <= 0)
				return toggleInvCallCount == 0;
			return toggleInvCallCount <= 1;
		}
	}
}
