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
#if AK_DIAGNOSTICS
		public static bool Enabled => AkDiagSwitch.On;
#else
		public static bool Enabled => false;
#endif

		private static bool _loggedNullLoggerOnce;

		public static void AnnounceLoad()
		{
#if AK_DIAGNOSTICS
			_loggedNullLoggerOnce = false;
			WriteInfo(
				"diag=" + (Enabled ? "ON" : "OFF")
				+ " (AkDiagSwitch); SlotCapture + DisplayResolve digs "
				+ (Enabled ? "compiled-in" : "compiled-out")
				+ " logger=" + DescribeLogger());
#endif
		}

		public static void AnnounceCaptureBegin(string detail)
		{
#if AK_DIAGNOSTICS
			if (!Enabled)
				return;

			WriteInfo(
				"[AKDiag] diag=ON capture-begin "
				+ (string.IsNullOrEmpty(detail) ? string.Empty : detail)
				+ " logger=" + DescribeLogger());
#endif
		}

		public static void Log(string message)
		{
#if AK_DIAGNOSTICS
			if (!Enabled)
				return;

			WriteInfo("[AKDiag] " + message);
#endif
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

		public static void ResetAnnounceState()
		{
			_loggedNullLoggerOnce = false;
		}

#if AK_DIAGNOSTICS
		private static void WriteInfo(string message)
		{
			try
			{
				if (TryWriteInfo(message))
					return;

				if (!_loggedNullLoggerOnce)
				{
					_loggedNullLoggerOnce = true;
					TryWriteWarn(
						"[AdvancedKeybinds] diag logger unavailable (Instance null); message was: " + message);
				}
			}
			catch
			{
			}
		}

		private static bool TryWriteInfo(string message)
		{
			Mod mod = ResolveMod();
			if (mod?.Logger == null)
				return false;

			mod.Logger.Info(message);
			return true;
		}

		private static string DescribeLogger()
		{
			if (AdvancedKeybinds.Instance?.Logger != null)
				return "Instance";
			try
			{
				if (ModLoader.TryGetMod("AdvancedKeybinds", out Mod mod) && mod?.Logger != null)
					return "ModLoader.TryGetMod";
			}
			catch
			{
			}

			return "unavailable";
		}
#endif

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
