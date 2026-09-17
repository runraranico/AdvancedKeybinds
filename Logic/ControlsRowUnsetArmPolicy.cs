using System;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Display/Controls-only: when a keybind row may arm the two-step right-click unset confirm.
	/// Does not clear bindings itself.
	/// </summary>
	public static class ControlsRowUnsetArmPolicy
	{
		/// <summary>
		/// True when the row shows something that unset should clear (AK record and/or vanilla KeyStatus).
		/// </summary>
		public static bool ShouldArmConfirm(bool hasAdvancedRecord, bool hasVanillaAssigned)
			=> hasAdvancedRecord || hasVanillaAssigned;

		/// <summary>
		/// Keyboard / KeyboardUI sibling: GenInput may show keys from the other mode while the row's
		/// constructor mode is empty — still treat as assigned for arming.
		/// </summary>
		public static bool IncludeKeyboardSiblingMode(int terrariaInputModeOrdinal)
			=> terrariaInputModeOrdinal == 0 || terrariaInputModeOrdinal == 1;
	}
}
