namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Middle-click / per-row "Reset to default" intent for Controls rows.
	/// Separates clear-to-unset (right-click×2) from restore-original (middle-click).
	/// </summary>
	public enum ControlsRowResetDefaultAction
	{
		/// <summary>Two-key Terraria-OR GamePad: keep AK empty record; product Reset = unset.</summary>
		ClearTwoKeyTerrariaOrRecord = 0,
		/// <summary>Mod keybind: registered default / ModKeybind default / OriginalProfiles (both KB modes via helper).</summary>
		RestoreModDefault = 1,
		/// <summary>
		/// Vanilla Keyboard/KeyboardUI/GamePad/GamePadUI: restore PlayerInput.OriginalProfiles
		/// for the edited InputMode only (no GAME↔UI copy).
		/// </summary>
		RestoreOriginalForEditedMode = 2
	}

	public static class ControlsRowResetDefaultPolicy
	{
		public static ControlsRowResetDefaultAction Resolve(
			bool isModKeybind,
			bool isTwoKeyTerrariaOr)
		{
			if (isTwoKeyTerrariaOr)
				return ControlsRowResetDefaultAction.ClearTwoKeyTerrariaOrRecord;
			if (isModKeybind)
				return ControlsRowResetDefaultAction.RestoreModDefault;
			return ControlsRowResetDefaultAction.RestoreOriginalForEditedMode;
		}

		/// <summary>
		/// Middle-click must restore defaults — never clear-only for vanilla Keyboard/UI.
		/// </summary>
		public static bool MustWriteOriginalProfileKeys(ControlsRowResetDefaultAction action)
			=> action == ControlsRowResetDefaultAction.RestoreOriginalForEditedMode
				|| action == ControlsRowResetDefaultAction.RestoreModDefault;
	}
}
