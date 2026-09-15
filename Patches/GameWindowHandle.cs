using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Terraria;

namespace AdvancedKeybinds.Patches
{
	/// <summary>Resolves the Win32 HWND for the running game (FNA/SDL/tModLoader layouts differ).</summary>
	internal static class GameWindowHandle
	{
		private static IntPtr _cached;
		private static long _cachedMs;

		public static IntPtr TryGet(bool preferForeground = false)
		{
			// No HWND concept off Windows (Linux / other non-Windows runtimes).
			if (!OperatingSystem.IsWindows())
				return IntPtr.Zero;

			long now = Environment.TickCount64;
			if (_cached != IntPtr.Zero && IsWindow(_cached) && now - _cachedMs < 3000)
				return _cached;

			uint pid = GetCurrentProcessId();
			IntPtr hwnd;

			if (preferForeground || WindowsKeySuppressor.IsSlotCapturePassthrough)
			{
				if (TryCache(GetForegroundWindow(), pid, now, out hwnd))
					return hwnd;

				if (TryCache(GetFocus(), pid, now, out hwnd))
					return hwnd;
			}

			foreach (IntPtr candidate in EnumerateStaticCandidates())
			{
				if (TryCache(candidate, pid, now, out hwnd))
					return hwnd;
			}

			IntPtr main = Process.GetCurrentProcess().MainWindowHandle;
			if (TryCache(main, pid, now, out hwnd))
				return hwnd;

			if (TryCache(GetForegroundWindow(), pid, now, out hwnd))
				return hwnd;

			return IntPtr.Zero;
		}

		public static void InvalidateCache()
		{
			_cached = IntPtr.Zero;
			_cachedMs = 0;
		}

		/// <summary>
		/// OS cursor in game-window client pixels (backbuffer space).
		/// Used when the window is unfocused and <see cref="Main.mouseX"/> is frozen —
		/// callers ÷ UIScale to reach the same UI space as focused Main.mouse.
		/// Unlike FNA <c>Mouse.GetState</c>, this is not clamped to the client when the
		/// cursor is over another window (deactivate / screenshot clicks).
		/// </summary>
		public static bool TryGetClientCursor(out int x, out int y)
		{
			x = 0;
			y = 0;
			if (!OperatingSystem.IsWindows())
				return false;

			IntPtr hwnd = TryGet(preferForeground: false);
			if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
				return false;

			if (!GetCursorPos(out POINT screen) || !ScreenToClient(hwnd, ref screen))
				return false;

			x = screen.X;
			y = screen.Y;
			return true;
		}

		/// <summary>Win32 client rect size (same space as <see cref="TryGetClientCursor"/>).</summary>
		public static bool TryGetClientSize(out int width, out int height)
		{
			width = 0;
			height = 0;
			if (!OperatingSystem.IsWindows())
				return false;

			IntPtr hwnd = TryGet(preferForeground: false);
			if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
				return false;

			if (!GetClientRect(hwnd, out RECT client))
				return false;

			width = Math.Max(0, client.Right - client.Left);
			height = Math.Max(0, client.Bottom - client.Top);
			return width > 0 && height > 0;
		}

		/// <summary>True when the OS cursor lies inside the game HWND client rect (not FNA-clamped).</summary>
		public static bool IsOsCursorInsideClient()
		{
			if (!OperatingSystem.IsWindows())
				return false;

			IntPtr hwnd = TryGet(preferForeground: false);
			if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
				return false;

			if (!GetCursorPos(out POINT screen) || !ScreenToClient(hwnd, ref screen))
				return false;

			if (!GetClientRect(hwnd, out RECT client))
				return false;

			int w = client.Right - client.Left;
			int h = client.Bottom - client.Top;
			return screen.X >= 0 && screen.Y >= 0 && screen.X < w && screen.Y < h;
		}

		/// <summary>
		/// True when this process owns the foreground window. Deactivate clicks often flip
		/// foreground before <see cref="Main.hasFocus"/> clears — used to keep purple wait.
		/// </summary>
		public static bool IsGameProcessForeground()
		{
			if (!OperatingSystem.IsWindows())
				return Main.hasFocus;

			IntPtr fg = GetForegroundWindow();
			if (fg == IntPtr.Zero || !IsWindow(fg))
				return Main.hasFocus;

			GetWindowThreadProcessId(fg, out uint fgPid);
			return fgPid == GetCurrentProcessId();
		}

		private static bool TryCache(IntPtr hwnd, uint pid, long now, out IntPtr result)
		{
			result = IntPtr.Zero;
			if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
				return false;

			GetWindowThreadProcessId(hwnd, out uint windowPid);
			if (windowPid != pid)
				return false;

			_cached = hwnd;
			_cachedMs = now;
			result = hwnd;
			return true;
		}

		private static System.Collections.Generic.IEnumerable<IntPtr> EnumerateStaticCandidates()
		{
			yield return TryWindowMember();

			foreach (string name in new[] { "handle", "_handle", "hwnd", "_hwnd", "wnd", "_wnd" })
			{
				IntPtr fromWindow = TryInstanceMember(Main.instance?.Window, name);
				if (fromWindow != IntPtr.Zero)
					yield return fromWindow;
			}

			if (Main.instance != null)
			{
				foreach (FieldInfo field in Main.instance.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
				{
					if (!LooksLikeHandleName(field.Name))
						continue;

					IntPtr ptr = TryGetFieldIntPtr(field, Main.instance);
					if (ptr != IntPtr.Zero)
						yield return ptr;
				}
			}

			foreach (FieldInfo field in typeof(Main).GetFields(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
			{
				if (!LooksLikeHandleName(field.Name))
					continue;

				foreach (IntPtr candidate in EnumerateFieldHandleCandidates(field))
				{
					if (candidate != IntPtr.Zero)
						yield return candidate;
				}
			}
		}

		private static IntPtr TryGetFieldIntPtr(FieldInfo field, object instance)
		{
			try
			{
				if (field.GetValue(instance) is IntPtr ptr && ptr != IntPtr.Zero)
					return ptr;
			}
			catch
			{
			}

			return IntPtr.Zero;
		}

		private static object TryGetFieldValue(FieldInfo field)
		{
			try
			{
				return field.IsStatic ? field.GetValue(null) : Main.instance != null ? field.GetValue(Main.instance) : null;
			}
			catch
			{
				return null;
			}
		}

		private static System.Collections.Generic.IEnumerable<IntPtr> EnumerateFieldHandleCandidates(FieldInfo field)
		{
			object value = TryGetFieldValue(field);
			if (value is IntPtr ptr && ptr != IntPtr.Zero)
			{
				yield return ptr;
				yield break;
			}

			if (value == null)
				yield break;

			IntPtr nested = TryInstanceMember(value, "Handle");
			if (nested == IntPtr.Zero)
				nested = TryInstanceMember(value, "handle");

			if (nested != IntPtr.Zero)
				yield return nested;
		}

		private static IntPtr TryWindowMember()
		{
			IntPtr hwnd = TryInstanceMember(Main.instance?.Window, "Handle");
			return hwnd != IntPtr.Zero ? hwnd : TryInstanceMember(Main.instance?.Window, "handle");
		}

		private static IntPtr TryInstanceMember(object instance, string name)
		{
			if (instance == null)
				return IntPtr.Zero;

			try
			{
				Type type = instance.GetType();
				PropertyInfo property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
				if (property?.GetValue(instance) is IntPtr hwnd && hwnd != IntPtr.Zero)
					return hwnd;

				FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
				if (field?.GetValue(instance) is IntPtr fieldHwnd && fieldHwnd != IntPtr.Zero)
					return fieldHwnd;
			}
			catch
			{
			}

			return IntPtr.Zero;
		}

		private static bool LooksLikeHandleName(string name)
			=> name.Contains("wnd", StringComparison.OrdinalIgnoreCase)
				|| name.Contains("hwnd", StringComparison.OrdinalIgnoreCase)
				|| name.Contains("handle", StringComparison.OrdinalIgnoreCase);

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

		[DllImport("user32.dll")]
		private static extern bool IsWindow(IntPtr hWnd);

		[DllImport("user32.dll")]
		private static extern IntPtr GetForegroundWindow();

		[DllImport("user32.dll")]
		private static extern IntPtr GetFocus();

		[DllImport("user32.dll")]
		private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

		[DllImport("user32.dll")]
		private static extern bool GetCursorPos(out POINT lpPoint);

		[DllImport("user32.dll")]
		private static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

		[DllImport("user32.dll")]
		private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

		[DllImport("kernel32.dll")]
		private static extern uint GetCurrentProcessId();
	}
}
