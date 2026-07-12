using System.Diagnostics;

namespace AdvancedKeybinds.Binding
{
	/// <summary>
	/// Diagnostic logging for the keybinding UI/engine. Off for players by default:
	/// <see cref="Log"/> is Conditional("AKDIAG") so call sites are compiled out unless
	/// the AKDIAG symbol is defined. Define AKDIAG in the project only when debugging.
	/// </summary>
	internal static class AkDiag
	{
#if AKDIAG
		public const bool Enabled = true;
#else
		public const bool Enabled = false;
#endif

		[Conditional("AKDIAG")]
		public static void Log(string message)
		{
			AdvancedKeybinds.Instance?.Logger.Info("[AKDiag] " + message);
		}
	}
}
