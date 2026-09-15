using System;
using System.Runtime.InteropServices;
using Terraria;

namespace AdvancedKeybinds.Patches
{
	/// <summary>
	/// Session cursor lock via ClipCursor. Session on/off is not saved; auto on/off at mod load
	/// is stored in ClientPrefs.EnableCursorConfineOnLoad.
	/// When enabled, confines the OS cursor to the game window client area so it cannot leave
	/// for other monitors. Releases when toggled off, focus is lost, or the mod unloads.
	/// </summary>
	internal static class CursorConfineHelper
	{
		[StructLayout(LayoutKind.Sequential)]
		private struct POINT
		{
			public int X;
			public int Y;
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct RECT
		{
			public int Left;
			public int Top;
			public int Right;
			public int Bottom;
		}

		private static bool _clipApplied;
		private static RECT _lastClip;
		private static long _lastApplyMs;

		/// <summary>Session on/off. Not persisted; forced from ClientPrefs.EnableCursorConfineOnLoad at mod load.</summary>
		public static bool SessionEnabled { get; private set; }

		public static bool IsApplied => _clipApplied;

		public static void SetSessionEnabled(bool enabled)
		{
			SessionEnabled = enabled;
			if (!enabled)
				Release();
			else
				Poll();
		}

		public static void ResetSession()
		{
			SessionEnabled = false;
			Release();
		}

		/// <summary>
		/// Forces session on/off from the persisted load preference.
		/// ON → start confined; OFF → start unconfined. Does not write prefs.
		/// </summary>
		public static void ApplyOnLoadPreference(bool enableOnLoad)
		{
			SetSessionEnabled(enableOnLoad);
		}

		public static void Poll()
		{
			if (!OperatingSystem.IsWindows())
				return;

			bool want = ShouldConfine();
			if (!want)
			{
				Release();
				return;
			}

			ApplyToGameWindow(force: false);
		}

		public static void Release()
		{
			if (!OperatingSystem.IsWindows())
				return;

			if (!_clipApplied)
				return;

			ClipCursor(IntPtr.Zero);
			_clipApplied = false;
			_lastClip = default;
		}

		private static bool ShouldConfine()
		{
			if (!SessionEnabled)
				return false;

			if (!Main.hasFocus)
				return false;

			return true;
		}

		private static void ApplyToGameWindow(bool force)
		{
			if (!OperatingSystem.IsWindows())
				return;

			IntPtr hwnd = GameWindowHandle.TryGet(preferForeground: true);
			if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
			{
				Release();
				return;
			}

			if (!GetClientRect(hwnd, out RECT client) || client.Right <= client.Left || client.Bottom <= client.Top)
			{
				Release();
				return;
			}

			POINT topLeft = new() { X = client.Left, Y = client.Top };
			POINT bottomRight = new() { X = client.Right, Y = client.Bottom };
			if (!ClientToScreen(hwnd, ref topLeft) || !ClientToScreen(hwnd, ref bottomRight))
			{
				Release();
				return;
			}

			RECT screen = new() {
				Left = topLeft.X,
				Top = topLeft.Y,
				Right = bottomRight.X,
				Bottom = bottomRight.Y,
			};

			long now = Environment.TickCount64;
			if (!force
				&& _clipApplied
				&& RectEquals(screen, _lastClip)
				&& now - _lastApplyMs < 100)
			{
				return;
			}

			if (!ClipCursor(ref screen))
			{
				Release();
				return;
			}

			_lastClip = screen;
			_lastApplyMs = now;
			_clipApplied = true;
		}

		private static bool RectEquals(RECT a, RECT b)
			=> a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;

		[DllImport("user32.dll")]
		private static extern bool ClipCursor(ref RECT lpRect);

		[DllImport("user32.dll")]
		private static extern bool ClipCursor(IntPtr lpRect);

		[DllImport("user32.dll")]
		private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

		[DllImport("user32.dll")]
		private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

		[DllImport("user32.dll")]
		private static extern bool IsWindow(IntPtr hWnd);
	}
}
