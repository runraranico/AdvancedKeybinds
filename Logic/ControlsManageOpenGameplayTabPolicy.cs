namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// UIManageControls keeps <c>OnGameplay</c> on the reused <c>Main.ManageControlsMenu</c> instance.
	/// Closing on Menu Controls (list icon) therefore reopens on Menu; product requires Gameplay (sword).
	/// </summary>
	public static class ControlsManageOpenGameplayTabPolicy
	{
		/// <summary>Every ManageControls activate must select the Gameplay tab before FillList.</summary>
		public static bool ShouldForceGameplayTabOnActivate() => true;

		/// <summary>
		/// True when the persisted flag would show Menu Controls and must be flipped to Gameplay.
		/// </summary>
		public static bool NeedsForceGameplayTab(bool currentOnGameplay)
			=> ShouldForceGameplayTabOnActivate() && !currentOnGameplay;
	}
}
