namespace AdvancedKeybinds.Core
{
	/// <summary>ComboPanel GamePad focus targets (mouse hover is independent).</summary>
	public enum ComboPanelPadFocus
	{
		None = 0,
		Slot1,
		Slot2,
		Slot3,
		HoldWhileHeld,
		HoldOnRelease,
		HoldDefault,
		HoldSlider,
		HoldNumber,
		Clear,
		Save,
		Cancel,
		/// <summary>Double Tap mode box (below HoldOnRelease). Appended so existing ordinals stay stable.</summary>
		DoubleTap
	}
}
