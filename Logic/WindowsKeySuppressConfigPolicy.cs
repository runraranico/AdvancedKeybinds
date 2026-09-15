namespace AdvancedKeybinds.Logic
{
	/// <summary>
	/// Display / execute gates for the Windows-only ModConfig setting SuppressWindowsKey.
	/// Host OS is injected so tests can cover Windows / Linux / macOS without calling OperatingSystem.
	/// Never rewrites the saved SuppressWindowsKey value based on host OS.
	/// </summary>
	public static class WindowsKeySuppressConfigPolicy
	{
		/// <summary>True when the ModConfig row for SuppressWindowsKey should be shown and editable.</summary>
		public static bool ShouldDisplaySuppressWindowsKey(bool isWindowsHost)
			=> isWindowsHost;

		/// <summary>
		/// True when Win-key hook / RawInput NOHOTKEYS / shell-block paths may run.
		/// Saved value true on Linux/macOS must still yield false.
		/// </summary>
		public static bool ShouldExecuteWindowsKeySuppression(bool isWindowsHost, bool suppressWindowsKeySavedValue)
			=> isWindowsHost && suppressWindowsKeySavedValue;

		/// <summary>
		/// Product code must never assign false (or true) to SuppressWindowsKey solely because the host OS changed.
		/// Restore Defaults remains the only intentional reset path.
		/// </summary>
		public static bool ShouldRewriteSavedValueForHostOs(bool isWindowsHost)
			=> false;
	}
}
