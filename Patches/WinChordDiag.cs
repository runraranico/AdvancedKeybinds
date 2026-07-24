using AdvancedKeybinds.Binding;

namespace AdvancedKeybinds.Patches
{
	/// <summary>Win chord diagnostics. Gated by <see cref="AkDiag.Enabled"/>.</summary>
	internal static class WinChordDiag
	{
		private static long _lastDedupeMs;
		private static string _lastDedupeKey;
		private const int DedupeMs = 60;

		public static void Log(string path, uint vk, string action, string detail)
		{
			if (!AkDiag.Enabled)
				return;

			string key = $"{path}:{vk}:{action}:{detail}";
			long now = System.Environment.TickCount64;
			if (key == _lastDedupeKey && now - _lastDedupeMs < DedupeMs)
				return;

			_lastDedupeMs = now;
			_lastDedupeKey = key;
			if (AkDiag.Enabled)
			{
				AkDiag.Log(
				"WinChordDiag path=" + path
				+ " vk=0x" + vk.ToString("X2")
				+ " action=" + action
				+ (string.IsNullOrEmpty(detail) ? string.Empty : " " + detail)
				+ WindowsKeySuppressor.DescribeWinChordState());
			}
		}
	}
}
