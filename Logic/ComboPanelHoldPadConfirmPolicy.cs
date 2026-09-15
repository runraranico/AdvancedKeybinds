namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// ComboPanel pad Confirm → HOLD mode toggle (WhileHeld / OnRelease) only.
	/// One apply per GamePad input tick epoch — DrawAndHandle may run Confirm multiple
	/// times while <c>WasJustPressed</c> stays true for the whole tick.
	/// Pure logic; no Terraria / file I/O.
	/// </summary>
	public static class ComboPanelHoldPadConfirmPolicy
	{
		/// <summary>
		/// True when this Confirm should run ToggleHold*. False = same input tick already applied.
		/// </summary>
		public static bool ShouldApplyHoldModeToggle(int currentInputTickEpoch, int lastAppliedInputTickEpoch)
			=> currentInputTickEpoch != lastAppliedInputTickEpoch;
	}
}
