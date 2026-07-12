using System;
using System.Diagnostics;
using AdvancedKeybinds.Binding;

namespace AdvancedKeybinds.Patches
{
	/// <summary>Win chord diagnostics. Stripped unless AKDIAG is defined.</summary>
	internal static class WinChordDiag
	{
		[Conditional("AKDIAG")]
		public static void Log(string path, uint vk, string action, string detail)
		{
#if AKDIAG
			string key = $"{path}:{vk}:{action}:{detail}";
			long now = Environment.TickCount64;
			if (key == _lastDedupeKey && now - _lastDedupeMs < DedupeMs)
				return;

			_lastDedupeMs = now;
			_lastDedupeKey = key;
			AkDiag.Log(
				"WinChordDiag path=" + path
				+ " vk=0x" + vk.ToString("X2")
				+ " action=" + action
				+ (string.IsNullOrEmpty(detail) ? string.Empty : " " + detail)
				+ WindowsKeySuppressor.DescribeWinChordState());
#else
			_ = path;
			_ = vk;
			_ = action;
			_ = detail;
#endif
		}

#if AKDIAG
		private static long _lastDedupeMs;
		private static string _lastDedupeKey;
		private const int DedupeMs = 60;
#endif
	}
}
