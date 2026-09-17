namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Steam Deck: ManageControls should open on the controller device tab (not keyboard icon).
	/// PC keyboard/mouse sessions must keep vanilla device-tab behaviour.
	/// </summary>
	public static class ControlsManageOpenGamepadDeviceTabPolicy
	{
		/// <summary>Only Steam Deck forces the controller device tab on activate.</summary>
		public static bool ShouldForceGamepadDeviceTab(bool isSteamDeck)
			=> isSteamDeck;

		/// <summary>
		/// True when Deck would otherwise show the keyboard device tab (<c>OnKeyboard==true</c>).
		/// </summary>
		public static bool NeedsForceGamepadDeviceTab(bool isSteamDeck, bool currentOnKeyboard)
			=> ShouldForceGamepadDeviceTab(isSteamDeck) && currentOnKeyboard;
	}
}
