using System;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Policy for suppressing Terraria's F7/F8/F10/F11 special UI toggles while still allowing
	/// those keys to flow through Advanced Keybinds capture and BindingInputEngine.
	/// </summary>
	public static class TerrariaDebugUiHotkeyPolicy
	{
		/// <summary>
		/// When true, vanilla <c>DoUpdate_F7/F8/F10/F11_*</c> toggles are skipped.
		/// Default false. Synced from <c>AdvancedKeybindsConfig.SuppressTerrariaDebugUiHotkeys</c>.
		/// </summary>
		public static bool SuppressVanillaSpecialUi { get; set; }

		public static bool IsTargetKeysName(string keysName)
		{
			if (string.IsNullOrWhiteSpace(keysName))
				return false;

			return keysName.Equals("F7", StringComparison.OrdinalIgnoreCase)
				|| keysName.Equals("F8", StringComparison.OrdinalIgnoreCase)
				|| keysName.Equals("F10", StringComparison.OrdinalIgnoreCase)
				|| keysName.Equals("F11", StringComparison.OrdinalIgnoreCase);
		}

		/// <summary>
		/// F11 remains registration-blocked in Advanced Keybinds (existing policy / tests).
		/// This suppress flag only blocks Terraria's hideUI toggle — it does not lift AK's F11 ban.
		/// </summary>
		public static bool AllowF11RegistrationInAdvancedKeybinds => false;

		public static bool ShouldSkipVanillaToggle()
			=> SuppressVanillaSpecialUi;
	}
}
