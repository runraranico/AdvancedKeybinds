using System;
using Terraria.ModLoader;

namespace AdvancedKeybinds.Binding
{
	/// <summary>
	/// Diagnostic logging. Full Log/Announce bodies compile only with AK_DIAGNOSTICS.
	/// <see cref="WarnRare"/> remains in public builds for rare anomaly warnings (not dig dumps).
	/// Call sites for Log must use <c>if (AkDiag.Enabled)</c> (DCE when false) and ideally
	/// sit under development builds with AK_DIAGNOSTICS defined.
	/// </summary>
	internal static class AkDiag
	{
		public static bool Enabled => false;

		public static void AnnounceCaptureBegin(string detail)
		{
		}

		public static void Log(string message)
		{
		}

		/// <summary>
		/// Rare anomaly — available in public builds. Caller must throttle.
		/// </summary>
		public static void WarnRare(string message)
		{
			try
			{
				TryWriteWarn("[AdvancedKeybinds] " + message);
			}
			catch
			{
				// Never throw from diagnostics.
			}
		}



		private static bool TryWriteWarn(string message)
		{
			Mod mod = ResolveMod();
			if (mod?.Logger == null)
				return false;

			mod.Logger.Warn(message);
			return true;
		}

		private static Mod ResolveMod()
		{
			try
			{
				if (AdvancedKeybinds.Instance != null)
					return AdvancedKeybinds.Instance;

				if (ModLoader.TryGetMod("AdvancedKeybinds", out Mod mod))
					return mod;
			}
			catch
			{
			}

			return null;
		}
	}
}
