namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Controller TwoKey OR ComboPanel: HoldWhileHeld / HoldOnRelease are not pad-focusable.
	/// Pure neighbor helpers — keep in sync with ComboPanelGamePadNavigation.StepFocus skipHoldMode branches.
	/// </summary>
	public static class TwoKeyPadHoldFocusPolicy
	{
		public static bool ShouldSkipHoldModeBoxes(bool twoKeySetupEligible)
			=> twoKeySetupEligible;

		public static bool IsHoldModeFocus(ComboPanelPadFocus focus)
			=> focus == ComboPanelPadFocus.HoldWhileHeld
				|| focus == ComboPanelPadFocus.HoldOnRelease;

		public static ComboPanelPadFocus EnterFromNoneLeft(bool skipHoldMode)
			=> skipHoldMode ? ComboPanelPadFocus.Slot1 : ComboPanelPadFocus.HoldWhileHeld;

		public static ComboPanelPadFocus NeighborLeft(ComboPanelPadFocus from, bool skipHoldMode)
		{
			if (from == ComboPanelPadFocus.Slot1)
				return skipHoldMode ? ComboPanelPadFocus.Slot1 : ComboPanelPadFocus.HoldWhileHeld;
			return from;
		}

		public static ComboPanelPadFocus NeighborUp(ComboPanelPadFocus from, bool skipHoldMode)
		{
			if (from == ComboPanelPadFocus.Clear)
				return skipHoldMode ? ComboPanelPadFocus.Slot1 : ComboPanelPadFocus.HoldOnRelease;
			return from;
		}

		public static ComboPanelPadFocus NeighborDown(ComboPanelPadFocus from, bool skipHoldMode)
		{
			if (from == ComboPanelPadFocus.HoldDefault)
				return skipHoldMode ? ComboPanelPadFocus.Slot1 : ComboPanelPadFocus.HoldWhileHeld;
			return from;
		}
	}
}
