namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// ComboPanel Clear gate: working content includes AlternateGamePadToken (two-key left slot),
	/// not only Slot1–3. Mouse and Pad Clear must share this gate.
	/// </summary>
	public static class ComboPanelClearPolicy
	{
		public static bool ShouldClearWorkingBinding(
			bool isEmpty,
			bool hasAlternateGamePad,
			bool hold,
			bool holdOnRelease)
		{
			if (!isEmpty)
				return true;
			if (hasAlternateGamePad)
				return true;
			if (hold || holdOnRelease)
				return true;
			return false;
		}

		public static bool ShouldClearWorkingBinding(ComboBindingData working)
		{
			if (working == null)
				return false;
			return ShouldClearWorkingBinding(
				working.IsEmpty,
				working.HasAlternateGamePad,
				working.Hold,
				working.HoldOnRelease);
		}
	}
}
