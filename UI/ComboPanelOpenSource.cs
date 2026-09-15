namespace AdvancedKeybinds.UI
{
	/// <summary>
	/// Trusted input that opened ComboPanel. Set once in <see cref="ComboPanel.Open"/> and
	/// cleared on Close — never re-polled from live InputMode each frame.
	/// </summary>
	internal enum ComboPanelOpenSource
	{
		None = 0,
		GamePad,
		Keyboard,
		Mouse
	}
}
