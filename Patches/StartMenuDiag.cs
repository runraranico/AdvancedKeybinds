using System;
using AdvancedKeybinds.Binding;

namespace AdvancedKeybinds.Patches
{
	/// <summary>
	/// Start-menu / Win / Esc diagnostics. Gated by <see cref="AkDiag.Enabled"/>.
	/// </summary>
	internal static class StartMenuDiag
	{
		private const uint WM_KEYDOWN = 0x0100;
		private const uint WM_SYSKEYDOWN = 0x0104;
		private const uint WM_SYSCOMMAND = 0x0112;
		private const int SC_KEYMENU = 0xF100;
		private const int SC_TASKLIST = 0xF130;
		private const uint VK_LWIN = 0x5B;
		private const uint VK_RWIN = 0x5C;
		private const uint VK_ESCAPE = 0x1B;

		private static long _lastDedupeMs;
		private static string _lastDedupeKey;
		private const int DedupeMs = 40;

		private static int _winPressSeq;
		private static int _activeWinPressSeq;

		public static void LogHook(uint vk, bool isDown, bool blocked, string note)
			=> LogPath("hook", vk, isDown, blocked, note);

		public static void LogLl(uint vk, bool isDown, bool blocked, string note)
			=> LogPath("ll", vk, isDown, blocked, note);

		public static void LogThread(uint vk, bool isDown, bool blocked, string note)
			=> LogPath("thread", vk, isDown, blocked, note);

		public static void LogRaw(uint vk, bool isDown, bool blocked, string note)
		{
			if (!AkDiag.Enabled)
				return;
			if (vk != VK_LWIN && vk != VK_RWIN)
				return;

			int seq = TrackWinPressSeq(vk, isDown, blocked);
			Write(
				FormatEvent("raw", vk, isDown, blocked, seq, note),
				$"raw:{vk}:{isDown}:{blocked}:{note}");
		}

		public static void LogWndProc(uint msg, uint vk, bool blocked, string note)
		{
			if (!AkDiag.Enabled)
				return;
			if (!IsRelevantWndProc(msg, vk))
				return;

			int seq = TrackWinPressSeq(vk, IsKeyDown(msg), blocked);
			Write(
				FormatEvent("wndproc", vk, IsKeyDown(msg), blocked, seq, note, "msg=0x" + msg.ToString("X4")),
				$"wnd:{msg}:{vk}:{blocked}:{note}");
		}

		public static void LogSyscmd(int cmd, bool blocked, string note)
		{
			if (!AkDiag.Enabled)
				return;
			cmd &= 0xFFF0;
			if (cmd != SC_KEYMENU && cmd != SC_TASKLIST)
				return;

			Write(
				$"path=syscmd cmd=0x{cmd:X4} blocked={blocked} {note}{CommonSuffix()}",
				$"sys:{cmd}:{blocked}:{note}");
		}

		/// <summary>Event reached DefWindowProc 窶・suppression did not swallow it here.</summary>
		public static void LogForwarded(uint msg, IntPtr wParam)
		{
			if (!AkDiag.Enabled)
				return;
			if (msg == WM_SYSCOMMAND)
			{
				int cmd = wParam.ToInt32() & 0xFFF0;
				if (cmd == SC_KEYMENU || cmd == SC_TASKLIST)
				{
					Write(
						$"LEAK path=syscmd cmd=0x{cmd:X4} forwarded to DefWindowProc{CommonSuffix()}",
						$"leak:sys:{cmd}");
				}

				return;
			}

			if (msg != WM_KEYDOWN && msg != WM_SYSKEYDOWN)
				return;

			uint vk = (uint)(wParam.ToInt64() & 0xFF);
			if (!IsRelevantVk(vk))
				return;

			bool ctrl = WindowsKeySuppressor.IsControlDownForDiag();
			int seq = vk == VK_LWIN || vk == VK_RWIN ? _activeWinPressSeq : 0;
			string seqPart = seq > 0 ? $" seq={seq}" : string.Empty;
			Write(
				$"LEAK path=wndproc msg=0x{msg:X4} vk=0x{vk:X2} ctrl={ctrl}{seqPart} forwarded to DefWindowProc{CommonSuffix()}",
				$"leak:{msg}:{vk}:{ctrl}");
		}

		public static void LogInstall(string note)
		{
			if (!AkDiag.Enabled)
				return;
			Write($"{note}{CommonSuffix()}", "install:" + note);
		}

		private static void LogPath(string path, uint vk, bool isDown, bool blocked, string note)
		{
			if (!IsRelevantVk(vk))
				return;

			int seq = TrackWinPressSeq(vk, isDown, blocked);
			Write(
				FormatEvent(path, vk, isDown, blocked, seq, note),
				$"{path}:{vk}:{isDown}:{blocked}:{note}");
		}

		private static string FormatEvent(string path, uint vk, bool isDown, bool blocked, int seq, string note, string extra = null)
		{
			string seqPart = seq > 0 ? $" seq={seq}" : string.Empty;
			string extraPart = string.IsNullOrEmpty(extra) ? string.Empty : " " + extra;
			return $"path={path} vk=0x{vk:X2} down={isDown} blocked={blocked}{seqPart}{extraPart} {note}{CommonSuffix()}";
		}

		private static string CommonSuffix()
		{
			long fgHwnd = WindowsKeySuppressor.ForegroundHwndForDiag;
			long hooked = GameWindowInputHook.HookedHwndForDiag;
			bool hwndMatch = fgHwnd != 0 && hooked != 0 && fgHwnd == hooked;
			return " fgHwnd=0x" + fgHwnd.ToString("X")
				+ " hooked=0x" + hooked.ToString("X")
				+ " hwndMatch=" + hwndMatch
				+ " raw=" + GameWindowInputHook.RawInputModeForDiag
				+ " state=[" + WindowsKeySuppressor.DescribeSuppressState() + "]";
		}

		private static int TrackWinPressSeq(uint vk, bool isDown, bool blocked)
		{
			if (vk != VK_LWIN && vk != VK_RWIN)
				return 0;

			if (isDown && blocked)
				_activeWinPressSeq = ++_winPressSeq;

			return _activeWinPressSeq;
		}

		private static bool IsKeyDown(uint msg)
			=> msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;

		private static bool IsRelevantVk(uint vk)
			=> vk == VK_LWIN || vk == VK_RWIN || vk == VK_ESCAPE;

		private static bool IsRelevantWndProc(uint msg, uint vk)
		{
			if (msg == WM_SYSCOMMAND)
				return true;

			if (msg != WM_KEYDOWN && msg != WM_SYSKEYDOWN)
				return false;

			return IsRelevantVk(vk);
		}

		private static void Write(string message, string dedupeKey)
		{
			if (!AkDiag.Enabled)
				return;

			long now = Environment.TickCount64;
			if (dedupeKey == _lastDedupeKey && now - _lastDedupeMs < DedupeMs)
				return;

			_lastDedupeMs = now;
			_lastDedupeKey = dedupeKey;
			if (AkDiag.Enabled) AkDiag.Log("StartMenuDiag " + message);
		}
	}
}
