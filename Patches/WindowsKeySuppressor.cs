using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using AdvancedKeybinds.Binding;
using Terraria;

namespace AdvancedKeybinds.Patches
{
	/// <summary>
	/// Installs a low-level keyboard hook (WH_KEYBOARD_LL) that swallows the Windows key, Ctrl+Esc
	/// (Start-menu equivalent), and toggle-lock keys (CapsLock / NumLock / ScrollLock) while tModLoader
	/// owns the foreground window. This stops the Start menu / focus loss and, because the key never
	/// key never reaches the OS or the game, we track its state ourselves so it can still be used as a
	/// combo binding key (see <see cref="AdvancedKeybinds.Binding.KeyToken"/>).
	///
	/// Swallowing the Windows key also breaks Windows' own screenshot shortcuts. Windows deliberately
	/// ignores *synthetic* (SendInput) key events for its system screenshot hotkeys, so re-injecting a
	/// Win+Shift+S chord never triggers the snip overlay. Instead we detect Win+Shift+S and launch the
	/// snip tool directly through its shell protocol (ms-screenclip:) - exactly what the hotkey does
	/// internally - which is not subject to the synthetic-input restriction. The launch is deferred to
	/// the game's main update tick via <see cref="FlushDeferredWinActions"/> so we never call
	/// Process.Start from inside the hook callback.
	///
	/// Win+Shift+Left / Win+Shift+Right cannot be re-triggered via SendInput (Windows ignores injected
	/// system hotkeys, same as Win+Shift+S). Instead we move the game window across monitors ourselves.
	/// In borderless mode we also call Main.SetResolution to match the destination display size.
	/// Win+Ctrl+Left / Win+Ctrl+Right briefly drop RIDEV_NOHOTKEYS and re-inject so OS virtual-desktop
	/// switching works while physical Win remains swallowed.
	/// </summary>
	internal static class WindowsKeySuppressor
	{
		private const int WH_KEYBOARD_LL = 13;
		private const int WH_KEYBOARD = 2;
		private const int WM_KEYDOWN = 0x0100;
		private const int WM_KEYUP = 0x0101;
		private const int WM_SYSKEYDOWN = 0x0104;
		private const int WM_SYSKEYUP = 0x0105;

		private const uint VK_LWIN = 0x5B;
		private const uint VK_RWIN = 0x5C;
		private const uint VK_SHIFT = 0x10;
		private const uint VK_LSHIFT = 0xA0;
		private const uint VK_RSHIFT = 0xA1;
		private const uint VK_CONTROL = 0x11;
		private const uint VK_LCONTROL = 0xA2;
		private const uint VK_RCONTROL = 0xA3;
		private const uint VK_ESCAPE = 0x1B;
		private const uint VK_S = 0x53;
		private const uint VK_LEFT = 0x25;
		private const uint VK_RIGHT = 0x27;
		private const uint SC_LWIN = 0x5B;
		private const uint SC_RWIN = 0x5C;
		private const int LLKHF_EXTENDED = 0x01;
		private const uint VK_CAPITAL = 0x14;
		private const uint VK_NUMLOCK = 0x90;
		private const uint VK_SCROLL = 0x91;
		private const uint SC_CAPITAL = 0x3A;
		private const uint SC_NUMLOCK = 0x45;
		private const uint SC_SCROLL = 0x46;

		private const uint INPUT_KEYBOARD = 1;
		private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
		private const uint KEYEVENTF_KEYUP = 0x0002;

		// Marker stored in dwExtraInfo on the events we inject, so the hook can recognise and ignore them.
		private static readonly IntPtr InjectedMarker = new IntPtr(0x11AC0DE5);

		/// <summary>
		/// While SendInput is synthesizing a Win chord, do not swallow those keys again in the thread
		/// hook / WndProc (LL hook already skips via <see cref="InjectedMarker"/>).
		/// </summary>
		private static int _syntheticChordDepth;

		private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

		// Keep managed references so delegates passed to native code aren't garbage collected.
		private static LowLevelKeyboardProc _proc;
		private static LowLevelKeyboardProc _threadProc;
		private static IntPtr _hookId = IntPtr.Zero;
		private static IntPtr _threadHookId = IntPtr.Zero;
		private static bool _attempted;
		private static uint _hookThreadId;

		public static bool IsLeftWindowsDown { get; private set; }
		public static bool IsRightWindowsDown { get; private set; }
		public static bool AnyWindowsDown => IsLeftWindowsDown || IsRightWindowsDown;

		/// <summary>Escape down swallowed as Ctrl+Esc (Start-menu block); exposed for combo bindings.</summary>
		public static bool IsShellBlockedEscapeDown { get; private set; }

		public static bool IsCapsLockPhysicallyDown { get; private set; }
		public static bool IsNumLockPhysicallyDown { get; private set; }
		public static bool IsScrollLockPhysicallyDown { get; private set; }
		public static bool IsInstalled => _hookId != IntPtr.Zero;

		/// <summary>While a combo slot is listening, toggle-lock keys pass through instead of being swallowed.</summary>
		public static bool IsSlotCapturePassthrough { get; private set; }

		/// <summary>While the combo keybind panel is open, always swallow Win keys (even if Start stole focus).</summary>
		private static bool _modalWindowsKeySuppress;

		/// <summary>Keep swallowing Win until the physical key is released (prevents Start on key-up after capture).</summary>
		private static bool _swallowWinUntilPhysicalUp;

		// Latched Win KEYDOWN for slot capture (edge, not held-state polling).
		private static volatile string _pendingWinCaptureToken;
		private static long _pendingWinCaptureTick;

		// Latched on KEYDOWN so SlotCapture can bind toggle keys even when the press is shorter than a frame.
		private static volatile string _pendingToggleLockCaptureToken;
		private static long _pendingToggleLockCaptureTick;

		// Physical-key edge detection during slot capture (high bit / hook state; not LED latch).
		private static bool _prevCapsHighBit;
		private static bool _prevNumHighBit;
		private static bool _prevScrollHighBit;
		private static bool _prevCapsPhysicallyDown;
		private static bool _prevNumPhysicallyDown;
		private static bool _prevScrollPhysicallyDown;
		private static long _captureWarmupUntilMs;
		private const long CaptureWarmupMs = 150;

		// Gameplay toggle-lock edges (same physical detection as slot capture; hook alone misses JP Caps).
		private static readonly HashSet<string> _gameplayToggleEdges = new(StringComparer.OrdinalIgnoreCase);
		private static readonly HashSet<string> _gameplayWinEdges = new(StringComparer.OrdinalIgnoreCase);
		private static readonly Dictionary<string, long> _toggleComboLatchUntilMs = new(StringComparer.OrdinalIgnoreCase);
		private static readonly Dictionary<string, long> _lastToggleGameplayEdgeMs = new(StringComparer.OrdinalIgnoreCase);
		private static readonly Dictionary<string, long> _lastWinGameplayEdgeMs = new(StringComparer.OrdinalIgnoreCase);
		private static readonly Dictionary<string, bool> _toggleRepeatArmed = new(StringComparer.OrdinalIgnoreCase);
		private static HashSet<string> _prevLatchedToggleTokens = new(StringComparer.OrdinalIgnoreCase);
		private const int ToggleComboLatchMs = 1200;
		/// <summary>JP Caps often never delivers KEYUP; drop stale hook physical-down after this window.</summary>
		private const int TogglePhysicalDownMaxMs = 300;
		/// <summary>Collapses multi-detector duplicate edges from one physical press (not repeat-rate limiting).</summary>
		private const int ToggleDuplicateCoalesceMs = 45;
		private const int GameplayWinEdgeCoalesceMs = 45;
		private static bool _leftWinRepeatArmed = true;
		private static bool _rightWinRepeatArmed = true;
		private static volatile string _pendingEscChordCaptureToken;
		private static long _pendingEscChordCaptureTick;
		private static bool _gpPrevCapsHighBit;
		private static bool _gpPrevNumHighBit;
		private static bool _gpPrevScrollHighBit;
		private static bool _gpPrevCapsPhysicallyDown;
		private static bool _gpPrevNumPhysicallyDown;
		private static bool _gpPrevScrollPhysicallyDown;
		private static long _capsPhysicalDownSinceMs;
		private static long _numPhysicalDownSinceMs;
		private static long _scrollPhysicalDownSinceMs;
		/// <summary>Win can steal focus within one message; keep suppressing briefly after we had fg.</summary>
		private static long _lastGameForegroundMs;
		private const int ForegroundGraceMs = 1200;
		/// <summary>True while a Win down that we swallowed is still physically held (fg may flicker).</summary>
		private static bool _winPressActiveWhileSuppressing;
		/// <summary>Win press began while Terraria truly owned fg (not grace-only).</summary>
		private static bool _winPressStartedWhileFgCore;
		private static int _winPhysicalUpFrames;

		// Win+Shift+S sets this from the hook; the main tick launches the snip overlay and clears it.
		// A short cooldown stops keyboard auto-repeat from opening the overlay several times.
		private static volatile bool _snipRequested;
		private static long _lastSnipRequestTick;
		private const long SnipCooldownMs = 1200;

		// Debounce latches so a held Win+Shift+Arrow (monitor) or Win+Ctrl+Arrow (virtual desktop)
		// fires once per press (not once per keyboard auto-repeat).
		private static bool _leftMoveActive;
		private static bool _rightMoveActive;
		private static bool _leftVirtualDesktopActive;
		private static bool _rightVirtualDesktopActive;
		private static volatile uint _pendingArrowChordVk;
		private static volatile uint _pendingArrowChordWinVk;
		private static volatile bool _pendingArrowChordWithShift;
		private static volatile bool _pendingArrowChordVirtualDesktop;

		[StructLayout(LayoutKind.Sequential)]
		private struct KBDLLHOOKSTRUCT
		{
			public uint vkCode;
			public uint scanCode;
			public uint flags;
			public uint time;
			public IntPtr dwExtraInfo;
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct KEYBDINPUT
		{
			public ushort wVk;
			public ushort wScan;
			public uint dwFlags;
			public uint time;
			public IntPtr dwExtraInfo;
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct MOUSEINPUT
		{
			public int dx;
			public int dy;
			public uint mouseData;
			public uint dwFlags;
			public uint time;
			public IntPtr dwExtraInfo;
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct HARDWAREINPUT
		{
			public uint uMsg;
			public ushort wParamL;
			public ushort wParamH;
		}

		[StructLayout(LayoutKind.Explicit)]
		private struct INPUTUNION
		{
			[FieldOffset(0)] public MOUSEINPUT mi;
			[FieldOffset(0)] public KEYBDINPUT ki;
			[FieldOffset(0)] public HARDWAREINPUT hi;
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct INPUT
		{
			public uint type;
			public INPUTUNION u;
		}

		[DllImport("user32.dll", SetLastError = true)]
		private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

		[DllImport("user32.dll", SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static extern bool UnhookWindowsHookEx(IntPtr hhk);

		[DllImport("user32.dll")]
		private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

		[DllImport("kernel32.dll", CharSet = CharSet.Auto)]
		private static extern IntPtr GetModuleHandle(string lpModuleName);

		[DllImport("user32.dll")]
		private static extern IntPtr GetForegroundWindow();

		[DllImport("user32.dll")]
		private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

		[DllImport("kernel32.dll")]
		private static extern uint GetCurrentProcessId();

		[DllImport("user32.dll")]
		private static extern short GetAsyncKeyState(int vKey);

		[DllImport("user32.dll", SetLastError = true)]
		private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

		[DllImport("kernel32.dll")]
		private static extern uint GetCurrentThreadId();

		[DllImport("user32.dll")]
		private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

		[DllImport("user32.dll")]
		private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

		[DllImport("user32.dll", CharSet = CharSet.Auto)]
		private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

		[DllImport("user32.dll")]
		private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

		[DllImport("user32.dll")]
		private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

		[DllImport("user32.dll")]
		private static extern bool IsZoomed(IntPtr hWnd);

		[DllImport("user32.dll")]
		private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

		[DllImport("user32.dll")]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static extern bool IsWindow(IntPtr hWnd);

		[DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
		private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

		[DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
		private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

		[DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
		private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

		[DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
		private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

		private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

		private const uint MONITOR_DEFAULTTONEAREST = 2;
		private const uint SWP_NOSIZE = 0x0001;
		private const uint SWP_NOMOVE = 0x0002;
		private const uint SWP_NOZORDER = 0x0004;
		private const uint SWP_NOACTIVATE = 0x0010;
		private const uint SWP_FRAMECHANGED = 0x0020;
		private const uint SWP_SHOWWINDOW = 0x0040;
		private const int SW_RESTORE = 9;
		private const int SW_MAXIMIZE = 3;

		private const int GWL_STYLE = -16;
		private const int WS_CAPTION = 0x00C00000;
		private const int WS_THICKFRAME = 0x00040000;
		private const int WS_MINIMIZE = 0x20000000;
		private const int WS_MAXIMIZE = 0x01000000;
		private const int WS_SYSMENU = 0x00080000;

		[StructLayout(LayoutKind.Sequential)]
		private struct RECT
		{
			public int Left;
			public int Top;
			public int Right;
			public int Bottom;

			public int Width => Right - Left;
			public int Height => Bottom - Top;
			public int CenterX => Left + Width / 2;
			public int CenterY => Top + Height / 2;
		}

		[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
		private struct MONITORINFO
		{
			public int cbSize;
			public RECT rcMonitor;
			public RECT rcWork;
			public uint dwFlags;
		}

		private readonly struct MonitorEntry
		{
			public readonly IntPtr Handle;
			public readonly RECT Monitor;
			public readonly RECT Work;

			public MonitorEntry(IntPtr handle, RECT monitor, RECT work)
			{
				Handle = handle;
				Monitor = monitor;
				Work = work;
			}
		}

		/// <summary>
		/// Installs the hook on the message-pumping thread. Mod.Load runs on a worker thread in
		/// tModLoader, so a hook installed there does not reliably swallow Win / Ctrl+Esc for the shell.
		/// </summary>
		public static void EnsureInstalled()
		{
			if (!OperatingSystem.IsWindows())
				return;

			uint threadId = GetCurrentThreadId();

			if (_hookId != IntPtr.Zero && _hookThreadId != threadId)
			{
				StartMenuDiag.LogInstall(
					"hook wrong thread hookTid=0x" + _hookThreadId.ToString("X")
					+ " currentTid=0x" + threadId.ToString("X") + " reinstalling");
				UninstallHookCore();
			}

			if (_hookId == IntPtr.Zero && _attempted && _hookThreadId != 0 && _hookThreadId != threadId)
			{
				StartMenuDiag.LogInstall("hook prior attempt on worker thread; retrying on main");
				_attempted = false;
			}

			if (_hookId != IntPtr.Zero)
			{
				EnsureThreadKeyboardHook(threadId);
				return;
			}

			if (_attempted)
				return;

			_attempted = true;

			try
			{
				IntPtr module = GetModuleHandle(null);
				_proc = HookCallback;
				_hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, module, 0);

				if (_hookId == IntPtr.Zero)
				{
					_proc = null;
					AdvancedKeybinds.Instance?.Logger.Warn(
						"Advanced Keybinds could not install the Windows-key hook (error " + Marshal.GetLastWin32Error() + ").");
				}
				else
				{
					_hookThreadId = threadId;
					AdvancedKeybinds.Instance?.Logger.Info(
						"Advanced Keybinds installed the keyboard suppression hook (Windows + toggle-lock keys).");
					EnsureThreadKeyboardHook(threadId);
					string threadNote = _threadHookId != IntPtr.Zero ? " threadHook=ok" : " threadHook=fail:" + Marshal.GetLastWin32Error();
					StartMenuDiag.LogInstall("keyboard hook installed tid=0x" + threadId.ToString("X") + threadNote);
					GameWindowInputHook.EnsureInstalledIfNeeded();
				}
			}
			catch (Exception ex)
			{
				_proc = null;
				AdvancedKeybinds.Instance?.Logger.Warn("Advanced Keybinds failed to install the Windows-key hook: " + ex.Message);
			}
		}

		private static void EnsureThreadKeyboardHook(uint threadId)
		{
			if (_threadHookId != IntPtr.Zero)
				return;

			_threadProc = ThreadKeyboardHookCallback;
			_threadHookId = SetWindowsHookEx(WH_KEYBOARD, _threadProc, GetModuleHandle(null), threadId);
		}

		private static void UninstallHookCore()
		{
			if (_threadHookId != IntPtr.Zero)
			{
				try
				{
					UnhookWindowsHookEx(_threadHookId);
				}
				catch
				{
				}

				_threadHookId = IntPtr.Zero;
			}

			if (_hookId != IntPtr.Zero)
			{
				try
				{
					UnhookWindowsHookEx(_hookId);
				}
				catch
				{
				}

				_hookId = IntPtr.Zero;
			}

			_proc = null;
			_threadProc = null;
			_attempted = false;
			_hookThreadId = 0;
		}

		public static void Uninstall()
		{
			IsLeftWindowsDown = false;
			IsRightWindowsDown = false;
			IsShellBlockedEscapeDown = false;
			IsCapsLockPhysicallyDown = false;
			IsNumLockPhysicallyDown = false;
			IsScrollLockPhysicallyDown = false;
			_capsPhysicalDownSinceMs = 0;
			_numPhysicalDownSinceMs = 0;
			_scrollPhysicalDownSinceMs = 0;
			_leftMoveActive = false;
			_rightMoveActive = false;
			_leftVirtualDesktopActive = false;
			_rightVirtualDesktopActive = false;

			if (_hookId != IntPtr.Zero)
			{
				UninstallHookCore();
			}
			else
			{
				_proc = null;
				_attempted = false;
				_hookThreadId = 0;
			}
			_pendingToggleLockCaptureToken = null;
			_pendingToggleLockCaptureTick = 0;
			IsSlotCapturePassthrough = false;
			_modalWindowsKeySuppress = false;
			_swallowWinUntilPhysicalUp = false;
			_winPressActiveWhileSuppressing = false;
			_winPressStartedWhileFgCore = false;
			_pendingWinCaptureToken = null;
			_pendingWinCaptureTick = 0;
			_prevCapsHighBit = false;
			_prevNumHighBit = false;
			_prevScrollHighBit = false;
			_prevCapsPhysicallyDown = false;
			_prevNumPhysicallyDown = false;
			_prevScrollPhysicallyDown = false;
			_captureWarmupUntilMs = 0;
			_gameplayToggleEdges.Clear();
			_toggleComboLatchUntilMs.Clear();
			_lastToggleGameplayEdgeMs.Clear();
			_toggleRepeatArmed.Clear();
			_prevLatchedToggleTokens.Clear();
			_gpPrevCapsHighBit = false;
			_gpPrevNumHighBit = false;
			_gpPrevScrollHighBit = false;
			_gpPrevCapsPhysicallyDown = false;
			_gpPrevNumPhysicallyDown = false;
			_gpPrevScrollPhysicallyDown = false;
			JpCapsLockDetector.Reset();
		}

		private static void SyncSlotCapturePhysicalState()
		{
			_prevCapsHighBit = IsPhysicalKeyDown(VK_CAPITAL);
			_prevNumHighBit = IsPhysicalKeyDown(VK_NUMLOCK);
			_prevScrollHighBit = IsPhysicalKeyDown(VK_SCROLL);
			_prevCapsPhysicallyDown = IsCapsLockPhysicallyDown;
			_prevNumPhysicallyDown = IsNumLockPhysicallyDown;
			_prevScrollPhysicallyDown = IsScrollLockPhysicallyDown;
		}

		private static bool IsPhysicalKeyDown(uint vkCode) => (GetAsyncKeyState((int)vkCode) & 0x8000) != 0;

		public static void SetModalWindowsKeySuppress(bool enabled)
		{
			_modalWindowsKeySuppress = enabled;
			if (!enabled)
				SyncWindowsKeyStateFromOs();
		}

		/// <summary>Re-sync hook-tracked Win state from the OS (avoids stale LeftWindows in slot capture).</summary>
		public static void SyncWindowsKeyStateFromOs()
		{
			IsLeftWindowsDown = IsPhysicalKeyDown(VK_LWIN);
			IsRightWindowsDown = IsPhysicalKeyDown(VK_RWIN);
		}

		private static bool ShouldSuppressShellInput()
		{
			// Non-active: never swallow Win — OS and other apps keep full Win access.
			if (!IsGameForegroundCore())
				return false;

			// Active: suppress Win for gameplay, combo panel, and slot capture.
			return true;
		}

		private static bool IsAnyWinPhysicallyDown()
			=> IsPhysicalKeyDown(VK_LWIN) || IsPhysicalKeyDown(VK_RWIN);

		private static void BeginWinPressSwallow()
		{
			_winPressActiveWhileSuppressing = true;
			_winPhysicalUpFrames = 0;
			_winPressStartedWhileFgCore = IsGameForegroundCore();
		}

		private static void ReleaseWinSuppressionState()
		{
			EndWinPressSwallow();
			IsLeftWindowsDown = false;
			IsRightWindowsDown = false;
			_swallowWinUntilPhysicalUp = false;
			_leftMoveActive = false;
			_rightMoveActive = false;
			_leftVirtualDesktopActive = false;
			_rightVirtualDesktopActive = false;
			_snipRequested = false;
			_pendingArrowChordVk = 0;
			_pendingArrowChordWinVk = 0;
			_pendingArrowChordWithShift = false;
			_pendingArrowChordVirtualDesktop = false;
			IsShellBlockedEscapeDown = false;
		}

		private static void EndWinPressSwallow()
		{
			_winPressActiveWhileSuppressing = false;
			_winPressStartedWhileFgCore = false;
			_swallowWinUntilPhysicalUp = false;
			_winPhysicalUpFrames = 0;
		}

		private static bool ShouldBlockWindowsKey()
		{
			// Our own SendInput chord must reach the OS (thread hook / WndProc have no InjectedMarker).
			if (_syntheticChordDepth > 0)
				return false;

			if (ShouldSuppressShellInput())
				return true;

			// Finish the same physical Win press if it began while fgCore (Start stole fg mid-press).
			// Do not require GetAsyncKeyState here — swallowing can clear the OS down-bit early.
			if (_winPressActiveWhileSuppressing && _winPressStartedWhileFgCore)
				return true;

			return false;
		}

		/// <summary>True when shell Start-menu shortcuts (e.g. Ctrl+Esc) should be swallowed.</summary>
		public static bool ShouldBlockStartMenuShortcuts() => ShouldBlockWindowsKey();

		/// <summary>Block Ctrl+Esc from opening the shell, but not while a slot is listening for keys.</summary>
		internal static bool ShouldBlockCtrlEscForShell()
			=> ShouldBlockStartMenuShortcuts() && !IsSlotCapturePassthrough;

		/// <summary>Custom Win chord handling only while we swallow the Win key (Terraria is fgCore).</summary>
		private static bool ShouldUseCustomWinChordHandling()
			=> IsGameForegroundCore();

		private static bool IsWinHeldForChord()
			=> AnyWindowsDown || IsAnyWinPhysicallyDown() || _winPressActiveWhileSuppressing;

		internal static bool IsShiftDownForDiag()
			=> (GetAsyncKeyState((int)VK_SHIFT) & 0x8000) != 0;

		internal static string DescribeWinChordState()
		{
			return " custom=" + ShouldUseCustomWinChordHandling()
				+ " winHeld=" + IsWinHeldForChord()
				+ " trackedWin=" + AnyWindowsDown
				+ " physWin=" + IsAnyWinPhysicallyDown()
				+ " shift=" + IsShiftDownForDiag()
				+ " snipPending=" + _snipRequested
				+ " arrowPending=0x" + _pendingArrowChordVk.ToString("X")
				+ " state=[" + DescribeSuppressState() + "]";
		}

		/// <summary>
		/// Win+Shift+S / Win+Shift+Arrow (monitor) / Win+Ctrl+Arrow (virtual desktop) while Terraria
		/// is active and swallowing Win. When inactive, Win reaches the OS natively — do not intercept.
		/// </summary>
		public static bool TryHandleWinChordPassthrough(uint vk, bool isDown, string path, out bool swallowed)
		{
			swallowed = false;
			if (!isDown)
				return false;

			// Do not re-intercept keys we are currently synthesizing for the shell.
			if (_syntheticChordDepth > 0)
				return false;

			if (!ShouldUseCustomWinChordHandling())
			{
				WinChordDiag.Log(path, vk, "native", "reason=inactive");
				return false;
			}

			if (!IsWinHeldForChord())
			{
				WinChordDiag.Log(path, vk, "skip", "reason=no-win-held");
				return false;
			}

			if (vk == VK_S && IsShiftDownForDiag())
				return TryRequestSnip(path, out swallowed);

			if (vk == VK_LEFT || vk == VK_RIGHT)
			{
				// Shift takes priority over Ctrl when both are held.
				if (IsShiftDownForDiag())
					return TryRequestMonitorMove(vk, path, out swallowed);

				if (IsControlDown())
					return TryRequestVirtualDesktopSwitch(vk, path, out swallowed);

				return false;
			}

			return false;
		}

		private static bool TryRequestSnip(string path, out bool swallowed)
		{
			swallowed = false;
			long now = Environment.TickCount64;
			if (now - _lastSnipRequestTick < SnipCooldownMs)
			{
				WinChordDiag.Log(path, VK_S, "snip-cooldown", "ms=" + (now - _lastSnipRequestTick));
				return false;
			}

			_lastSnipRequestTick = now;
			_snipRequested = true;
			swallowed = true;
			WinChordDiag.Log(path, VK_S, "snip-request", "queued");
			return true;
		}

		private static bool TryRequestMonitorMove(uint vk, string path, out bool swallowed)
		{
			swallowed = false;
			if (!IsShiftDownForDiag())
				return false;

			bool isLeft = vk == VK_LEFT;
			bool latch = isLeft ? _leftMoveActive : _rightMoveActive;
			if (latch)
			{
				swallowed = true;
				return true;
			}

			if (isLeft)
				_leftMoveActive = true;
			else
				_rightMoveActive = true;

			uint chordWinVk = ResolveChordWinVk();
			RequestArrowChord(vk, chordWinVk, monitorMove: true, virtualDesktop: false);
			swallowed = true;
			WinChordDiag.Log(path, vk, "move-request", "win=0x" + chordWinVk.ToString("X"));
			return true;
		}

		private static bool TryRequestVirtualDesktopSwitch(uint vk, string path, out bool swallowed)
		{
			swallowed = false;
			if (!IsControlDown())
				return false;

			bool isLeft = vk == VK_LEFT;
			bool latch = isLeft ? _leftVirtualDesktopActive : _rightVirtualDesktopActive;
			if (latch)
			{
				swallowed = true;
				return true;
			}

			if (isLeft)
				_leftVirtualDesktopActive = true;
			else
				_rightVirtualDesktopActive = true;

			uint chordWinVk = ResolveChordWinVk();
			RequestArrowChord(vk, chordWinVk, monitorMove: false, virtualDesktop: true);
			swallowed = true;
			WinChordDiag.Log(path, vk, "vd-request", "win=0x" + chordWinVk.ToString("X"));
			return true;
		}

		private static uint ResolveChordWinVk()
		{
			uint chordWinVk = IsLeftWindowsDown ? VK_LWIN : VK_RWIN;
			if (!IsLeftWindowsDown && IsRightWindowsDown)
				chordWinVk = VK_RWIN;
			else if (!AnyWindowsDown && IsAnyWinPhysicallyDown())
				chordWinVk = IsPhysicalKeyDown(VK_RWIN) ? VK_RWIN : VK_LWIN;
			return chordWinVk;
		}

		public static bool TryFinishMonitorMoveArrowUp(uint vk, bool isUp, out bool swallowed)
		{
			swallowed = false;
			if (!ShouldUseCustomWinChordHandling())
				return false;

			if (!isUp || (vk != VK_LEFT && vk != VK_RIGHT))
				return false;

			bool isLeft = vk == VK_LEFT;
			bool moveLatch = isLeft ? _leftMoveActive : _rightMoveActive;
			bool vdLatch = isLeft ? _leftVirtualDesktopActive : _rightVirtualDesktopActive;
			if (!moveLatch && !vdLatch)
				return false;

			if (isLeft)
			{
				_leftMoveActive = false;
				_leftVirtualDesktopActive = false;
			}
			else
			{
				_rightMoveActive = false;
				_rightVirtualDesktopActive = false;
			}

			swallowed = true;
			return true;
		}

		private static void SetShellBlockedEscapeDown(bool down)
		{
			IsShellBlockedEscapeDown = down;
		}

		internal static void ClearShellBlockedEscapeDown()
			=> SetShellBlockedEscapeDown(false);

		private static void HandleShellBlockedEscapeKeyUp(uint vk, bool isUp)
		{
			if (isUp && vk == VK_ESCAPE && IsShellBlockedEscapeDown)
				ClearShellBlockedEscapeDown();
		}

		private static bool IsControlDown()
			=> (GetAsyncKeyState((int)VK_CONTROL) & 0x8000) != 0;

		/// <summary>Ctrl+Esc is treated like Win by the shell and opens the Start menu.</summary>
		public static bool IsStartMenuChord(uint vkCode, bool isDown)
			=> isDown && vkCode == VK_ESCAPE && IsControlDown();

		/// <summary>
		/// Shell Start-menu requests delivered as WM_SYSCOMMAND (Ctrl+Esc uses SC_TASKLIST).
		/// Blocking keyboard alone is not enough; DefWindowProc must never see these commands.
		/// </summary>
		public static bool ShouldBlockShellStartMenuCommand(int cmd)
		{
			cmd &= 0xFFF0;
			if (cmd != SC_KEYMENU && cmd != SC_TASKLIST)
				return false;

			if (!IsGameForegroundCore())
				return false;

			return ShouldBlockStartMenuShortcuts();
		}

		internal static bool IsControlDownForDiag()
			=> IsControlDown();

		internal static long ForegroundHwndForDiag
			=> GetForegroundWindow().ToInt64();

		/// <summary>Why suppression is off (for StartMenuDiag).</summary>
		internal static string DescribeWhyNotBlocking()
		{
			if (_modalWindowsKeySuppress || IsSlotCapturePassthrough)
				return "would-block-ui";

			if (IsGameForegroundCore())
				return "fgCore-true";

			if (_winPressActiveWhileSuppressing && _winPressStartedWhileFgCore)
				return "winPress-finishing";

			if (_winPressActiveWhileSuppressing)
				return "winPress-released-needed";

			return "not-foreground";
		}

		internal static string DescribeSuppressState()
		{
			return "fg=" + IsGameForeground()
				+ " fgCore=" + IsGameForegroundCore()
				+ " hook=" + IsInstalled
				+ " wndProc=" + GameWindowInputHook.IsInstalledForDiag
				+ " winPress=" + _winPressActiveWhileSuppressing
				+ " winPressFg=" + _winPressStartedWhileFgCore
				+ " latchWin=" + _swallowWinUntilPhysicalUp
				+ " modal=" + _modalWindowsKeySuppress
				+ " capture=" + IsSlotCapturePassthrough;
		}

		private static bool TryResolveWindowsVk(KBDLLHOOKSTRUCT data, out uint vk)
		{
			vk = data.vkCode;
			if (vk == VK_LWIN || vk == VK_RWIN)
				return true;

			uint scan = data.scanCode & 0xFF;
			if (scan == SC_LWIN)
			{
				vk = VK_LWIN;
				return true;
			}

			if (scan == SC_RWIN)
			{
				vk = VK_RWIN;
				return true;
			}

			return false;
		}

		private const int SC_KEYMENU = 0xF100;
		private const int SC_TASKLIST = 0xF170;

		/// <summary>WndProc fallback when WH_KEYBOARD_LL never sees Win (common on Win11).</summary>
		public static bool TryApplyWindowsKeyBlock(uint vk, bool isDown, bool isUp, out bool blocked)
		{
			blocked = false;
			if (vk != VK_LWIN && vk != VK_RWIN)
				return false;

			if (!ShouldBlockWindowsKey())
			{
				if (vk == VK_LWIN)
					IsLeftWindowsDown = false;
				else
					IsRightWindowsDown = false;

				StartMenuDiag.LogWndProc((uint)(isUp ? WM_KEYUP : WM_KEYDOWN), vk, blocked: false, "win reason=" + DescribeWhyNotBlocking());
				return true;
			}

			blocked = true;
			ApplyWindowsKeyState(vk, isDown, isUp);

			if (IsSlotCapturePassthrough && isDown)
				QueueWinKeyCapture(vk);
			else if (isDown && IsGameForeground())
				QueueGameplayWinEdge(vk);

			StartMenuDiag.LogWndProc((uint)(isUp ? WM_KEYUP : WM_KEYDOWN), vk, blocked: true, "win");

			return true;
		}

		private static void ApplyWindowsKeyState(uint vk, bool isDown, bool isUp)
		{
			if (vk == VK_LWIN)
				IsLeftWindowsDown = isDown || (!isUp && IsLeftWindowsDown);
			else
				IsRightWindowsDown = isDown || (!isUp && IsRightWindowsDown);

			if (isDown)
			{
				_swallowWinUntilPhysicalUp = true;
				BeginWinPressSwallow();
			}
			else if (isUp)
			{
				if (vk == VK_LWIN)
					_leftWinRepeatArmed = true;
				else if (vk == VK_RWIN)
					_rightWinRepeatArmed = true;
				EndWinPressSwallow();
			}
		}

		/// <summary>After binding Win, keep swallowing until the user physically releases the key.</summary>
		public static void ArmWinKeyReleaseGuard()
		{
			if (IsPhysicalKeyDown(VK_LWIN) || IsPhysicalKeyDown(VK_RWIN) || IsLeftWindowsDown || IsRightWindowsDown)
				_swallowWinUntilPhysicalUp = true;
		}

		public static void PollWinKeyReleaseGuard()
		{
			GameWindowInputHook.EnsureInstalledIfNeeded();

			if (!IsGameForegroundCore())
			{
				// After virtual-desktop switch, focus leaves the game while physical Win may still be held.
				// Keep swallowing that press so the shell does not see a half-held Win on the new desktop.
				if (_winPressActiveWhileSuppressing && _winPressStartedWhileFgCore && IsAnyWinPhysicallyDown())
				{
					_winPhysicalUpFrames = 0;
					_snipRequested = false;
					_pendingArrowChordVk = 0;
					_pendingArrowChordWinVk = 0;
					_pendingArrowChordWithShift = false;
					_pendingArrowChordVirtualDesktop = false;
					return;
				}

				if (AnyWindowsDown || _winPressActiveWhileSuppressing || _swallowWinUntilPhysicalUp
					|| _snipRequested || _pendingArrowChordVk != 0)
				{
					ReleaseWinSuppressionState();
					WinChordDiag.Log("poll", 0, "released", "reason=inactive");
				}

				return;
			}

			bool leftDown = IsPhysicalKeyDown(VK_LWIN);
			bool rightDown = IsPhysicalKeyDown(VK_RWIN);
			bool anyPhysicalDown = leftDown || rightDown;

			if (anyPhysicalDown)
				_winPhysicalUpFrames = 0;
			else if (_winPressActiveWhileSuppressing)
				_winPhysicalUpFrames++;

			// Require a few frames of physical-up before ending — GetAsyncKeyState can flicker after swallow.
			if (_winPressActiveWhileSuppressing && !anyPhysicalDown && _winPhysicalUpFrames >= 3)
				EndWinPressSwallow();

			if (_swallowWinUntilPhysicalUp && !leftDown && !rightDown)
				SyncWindowsKeyStateFromOs();
		}

		public static bool TryConsumeEscChordCapture(out string token)
		{
			token = _pendingEscChordCaptureToken;
			if (token == null)
				return false;

			if (Environment.TickCount64 - _pendingEscChordCaptureTick > 300)
			{
				_pendingEscChordCaptureToken = null;
				_pendingEscChordCaptureTick = 0;
				return false;
			}

			_pendingEscChordCaptureToken = null;
			_pendingEscChordCaptureTick = 0;
			return true;
		}

		public static bool TryConsumeWinKeyCapture(out string token)
		{
			token = _pendingWinCaptureToken;
			if (token == null)
				return false;

			if (Environment.TickCount64 - _pendingWinCaptureTick > 300)
			{
				_pendingWinCaptureToken = null;
				_pendingWinCaptureTick = 0;
				return false;
			}

			_pendingWinCaptureToken = null;
			_pendingWinCaptureTick = 0;
			return true;
		}

		private static void QueueWinKeyCapture(uint vkCode)
		{
			_pendingWinCaptureToken = vkCode == VK_LWIN ? "LeftWindows" : "RightWindows";
			_pendingWinCaptureTick = Environment.TickCount64;
			AkDiag.Log("WinKey KEYDOWN queued for capture: " + _pendingWinCaptureToken);
		}

		public static void SetSlotCapturePassthrough(bool enabled)
		{
			IsSlotCapturePassthrough = enabled;
			if (enabled)
			{
				_captureWarmupUntilMs = Environment.TickCount64 + CaptureWarmupMs;
				SyncWindowsKeyStateFromOs();
				SyncSlotCapturePhysicalState();
				JpCapsLockDetector.Reset();
				AkDiag.Log("Slot capture physical baseline capsDown=" + _prevCapsHighBit + " hook=" + IsInstalled);
			}
			else
			{
				_captureWarmupUntilMs = 0;
				_prevCapsHighBit = false;
				_prevNumHighBit = false;
				_prevScrollHighBit = false;
				_prevCapsPhysicallyDown = false;
				_prevNumPhysicallyDown = false;
				_prevScrollPhysicallyDown = false;
				SyncGameplayPhysicalState();
			}
		}

		private static void SyncGameplayPhysicalState()
		{
			_gpPrevCapsHighBit = IsPhysicalKeyDown(VK_CAPITAL);
			_gpPrevNumHighBit = IsPhysicalKeyDown(VK_NUMLOCK);
			_gpPrevScrollHighBit = IsPhysicalKeyDown(VK_SCROLL);
			_gpPrevCapsPhysicallyDown = IsCapsLockPhysicallyDown;
			_gpPrevNumPhysicallyDown = IsNumLockPhysicallyDown;
			_gpPrevScrollPhysicallyDown = IsScrollLockPhysicallyDown;
		}

		/// <summary>
		/// Polls toggle-lock physical KEYDOWN edges during gameplay. Slot capture already does this;
		/// gameplay must too because the hook may not latch CapsLock on JP keyboards.
		/// </summary>
		public static void PollGameplayToggleLockEdges()
		{
			if (Main.gameMenu || !IsGameForeground() || IsSlotCapturePassthrough)
				return;

			ExpireStuckTogglePhysicalDown();

			PollAlternateToggleSources(allowImeEdges: true);

			if (TryPhysicalKeyEdge(VK_CAPITAL, "CapsLock", ref _gpPrevCapsHighBit, out string token))
				QueueGameplayToggleLockEdge(token);

			if (TryPhysicalKeyEdge(VK_NUMLOCK, "NumLock", ref _gpPrevNumHighBit, out token))
				QueueGameplayToggleLockEdge(token);

			if (TryPhysicalKeyEdge(VK_SCROLL, BindingRestrictions.ScrollLockToken, ref _gpPrevScrollHighBit, out token))
				QueueGameplayToggleLockEdge(token);

			if (TryBoolEdge(IsCapsLockPhysicallyDown, ref _gpPrevCapsPhysicallyDown, "CapsLock", out token))
				QueueGameplayToggleLockEdge(token);

			if (TryBoolEdge(IsNumLockPhysicallyDown, ref _gpPrevNumPhysicallyDown, "NumLock", out token))
				QueueGameplayToggleLockEdge(token);

			if (TryBoolEdge(IsScrollLockPhysicallyDown, ref _gpPrevScrollPhysicallyDown, BindingRestrictions.ScrollLockToken, out token))
				QueueGameplayToggleLockEdge(token);

			RefreshToggleComboLatchesFromPhysicalState();
			RefreshToggleRepeatArming();
		}

		/// <summary>
		/// Re-arms toggle-lock repeat after the physical key is released. While held, only the first edge
		/// of a press is accepted; JP Caps re-arms on the next poll once the momentary down ends.
		/// </summary>
		private static void RefreshToggleRepeatArming()
		{
			TryRearmToggleRepeat("CapsLock", IsCapsLockPhysicallyDown);
			TryRearmToggleRepeat("NumLock", IsNumLockPhysicallyDown);
			TryRearmToggleRepeat(BindingRestrictions.ScrollLockToken, IsScrollLockPhysicallyDown);
		}

		private static void TryRearmToggleRepeat(string token, bool physicallyDown)
		{
			if (_toggleRepeatArmed.GetValueOrDefault(token, true))
				return;

			if (!physicallyDown)
				_toggleRepeatArmed[token] = true;
		}

		/// <summary>
		/// Toggle-lock keys in multi-key combos (JP Caps) are often a short edge, not a held key.
		/// Latch them briefly so chord keys pressed right after still satisfy the combo.
		/// </summary>
		public static void LatchToggleTokenForCombo(string token, int ms = ToggleComboLatchMs)
		{
			if (string.IsNullOrEmpty(token) || !BindingRestrictions.IsToggleLockToken(token))
				return;

			_toggleComboLatchUntilMs[token] = Environment.TickCount64 + ms;
		}

		public static void ClearToggleComboLatch(string token)
		{
			if (string.IsNullOrEmpty(token))
				return;

			_toggleComboLatchUntilMs.Remove(token);
		}

		/// <summary>Clears combo latch and hook-tracked physical-down for a toggle-lock token.</summary>
		public static void ReleaseToggleLockComboState(string token)
		{
			ClearToggleComboLatch(token);
			ReleaseToggleLockPhysicalState(token);
		}

		/// <summary>
		/// Drops hook physical-down without clearing the short combo latch. Used when the OS toggles
		/// Caps Lock (Shift+Caps on JP) so LED-on state is not treated as a held combo key.
		/// </summary>
		public static void ReleaseToggleLockPhysicalState(string token)
		{
			if (string.IsNullOrEmpty(token))
				return;

			switch (token)
			{
				case "CapsLock":
					if (IsCapsLockPhysicallyDown)
						AkDiag.Log("Toggle-lock releasing CapsLock physical state.");
					IsCapsLockPhysicallyDown = false;
					_gpPrevCapsPhysicallyDown = false;
					_prevCapsPhysicallyDown = false;
					_capsPhysicalDownSinceMs = 0;
					break;
				case "NumLock":
					IsNumLockPhysicallyDown = false;
					_gpPrevNumPhysicallyDown = false;
					_prevNumPhysicallyDown = false;
					_numPhysicalDownSinceMs = 0;
					break;
				default:
					if (!string.Equals(token, BindingRestrictions.ScrollLockToken, StringComparison.OrdinalIgnoreCase))
						return;

					IsScrollLockPhysicallyDown = false;
					_gpPrevScrollPhysicallyDown = false;
					_prevScrollPhysicallyDown = false;
					_scrollPhysicalDownSinceMs = 0;
					break;
			}
		}

		public static bool IsToggleTokenActiveForCombo(string token)
		{
			if (!BindingRestrictions.IsToggleLockToken(token))
				return false;

			if (IsTogglePhysicallyDown(token))
				return true;

			return _toggleComboLatchUntilMs.TryGetValue(token, out long until)
				&& Environment.TickCount64 < until;
		}

		public static HashSet<string> SnapshotLatchedToggleTokens()
		{
			HashSet<string> active = new(StringComparer.OrdinalIgnoreCase);
			long now = Environment.TickCount64;

			foreach (KeyValuePair<string, long> pair in _toggleComboLatchUntilMs)
			{
				if (now < pair.Value)
					active.Add(pair.Key);
			}

			if (IsCapsLockPhysicallyDown)
				active.Add("CapsLock");
			if (IsNumLockPhysicallyDown)
				active.Add("NumLock");
			if (IsScrollLockPhysicallyDown)
				active.Add(BindingRestrictions.ScrollLockToken);

			return active;
		}

		public static void EndToggleComboLatchFrame(HashSet<string> currentLatched)
		{
			_prevLatchedToggleTokens = new HashSet<string>(currentLatched, StringComparer.OrdinalIgnoreCase);

			long now = Environment.TickCount64;
			List<string> expired = null;
			foreach (KeyValuePair<string, long> pair in _toggleComboLatchUntilMs)
			{
				if (now >= pair.Value && !IsTogglePhysicallyDown(pair.Key))
					(expired ??= new List<string>()).Add(pair.Key);
			}

			if (expired == null)
				return;

			foreach (string token in expired)
				_toggleComboLatchUntilMs.Remove(token);
		}

		public static bool WasLatchedToggleToken(string token)
			=> _prevLatchedToggleTokens.Contains(token);

		private static bool IsTogglePhysicallyDown(string token) => token switch {
			"CapsLock" => IsCapsLockPhysicallyDown,
			"NumLock" => IsNumLockPhysicallyDown,
			_ when string.Equals(token, BindingRestrictions.ScrollLockToken, StringComparison.OrdinalIgnoreCase) => IsScrollLockPhysicallyDown,
			_ => false,
		};

		private static void RefreshToggleComboLatchesFromPhysicalState()
		{
			if (IsTogglePhysicalDownCredible("CapsLock"))
				LatchToggleTokenForCombo("CapsLock");
			if (IsTogglePhysicalDownCredible("NumLock"))
				LatchToggleTokenForCombo("NumLock");
			if (IsTogglePhysicalDownCredible(BindingRestrictions.ScrollLockToken))
				LatchToggleTokenForCombo(BindingRestrictions.ScrollLockToken);
		}

		private static bool IsTogglePhysicalDownCredible(string token)
		{
			if (!IsTogglePhysicallyDown(token))
				return false;

			long sinceMs = token switch {
				"CapsLock" => _capsPhysicalDownSinceMs,
				"NumLock" => _numPhysicalDownSinceMs,
				_ => _scrollPhysicalDownSinceMs,
			};

			if (sinceMs == 0)
				return true;

			long elapsed = Environment.TickCount64 - sinceMs;
			if (elapsed <= TogglePhysicalDownMaxMs)
				return true;

			uint vk = token switch {
				"CapsLock" => VK_CAPITAL,
				"NumLock" => VK_NUMLOCK,
				_ => VK_SCROLL,
			};

			return IsPhysicalKeyDown(vk);
		}

		private static void ExpireStuckTogglePhysicalDown()
		{
			long now = Environment.TickCount64;

			if (IsCapsLockPhysicallyDown && _capsPhysicalDownSinceMs != 0
				&& now - _capsPhysicalDownSinceMs > TogglePhysicalDownMaxMs
				&& !IsPhysicalKeyDown(VK_CAPITAL))
			{
				ReleaseToggleLockPhysicalState("CapsLock");
			}

			if (IsNumLockPhysicallyDown && _numPhysicalDownSinceMs != 0
				&& now - _numPhysicalDownSinceMs > TogglePhysicalDownMaxMs
				&& !IsPhysicalKeyDown(VK_NUMLOCK))
			{
				ReleaseToggleLockPhysicalState("NumLock");
			}

			if (IsScrollLockPhysicallyDown && _scrollPhysicalDownSinceMs != 0
				&& now - _scrollPhysicalDownSinceMs > TogglePhysicalDownMaxMs
				&& !IsPhysicalKeyDown(VK_SCROLL))
			{
				ReleaseToggleLockPhysicalState(BindingRestrictions.ScrollLockToken);
			}
		}

		private static void ApplyTogglePhysicalDown(uint toggleVk, bool isDown, bool isUp)
		{
			long now = Environment.TickCount64;

			switch (toggleVk)
			{
				case VK_CAPITAL:
					if (isDown)
					{
						IsCapsLockPhysicallyDown = true;
						_capsPhysicalDownSinceMs = now;
					}
					else if (isUp)
					{
						IsCapsLockPhysicallyDown = false;
						_capsPhysicalDownSinceMs = 0;
					}

					break;
				case VK_NUMLOCK:
					if (isDown)
					{
						IsNumLockPhysicallyDown = true;
						_numPhysicalDownSinceMs = now;
					}
					else if (isUp)
					{
						IsNumLockPhysicallyDown = false;
						_numPhysicalDownSinceMs = 0;
					}

					break;
				case VK_SCROLL:
					if (isDown)
					{
						IsScrollLockPhysicallyDown = true;
						_scrollPhysicalDownSinceMs = now;
					}
					else if (isUp)
					{
						IsScrollLockPhysicallyDown = false;
						_scrollPhysicalDownSinceMs = 0;
					}

					break;
			}
		}

		/// <summary>Returns toggle-lock edges detected this frame and clears the pending set.</summary>
		public static IReadOnlyCollection<string> DrainGameplayToggleLockEdges()
		{
			if (_gameplayToggleEdges.Count == 0)
				return Array.Empty<string>();

			string[] edges = new string[_gameplayToggleEdges.Count];
			_gameplayToggleEdges.CopyTo(edges);
			_gameplayToggleEdges.Clear();
			return edges;
		}

		/// <summary>Returns Win-key KEYDOWN edges detected this frame and clears the pending set.</summary>
		public static IReadOnlyCollection<string> DrainGameplayWinEdges()
		{
			if (_gameplayWinEdges.Count == 0)
				return Array.Empty<string>();

			string[] edges = new string[_gameplayWinEdges.Count];
			_gameplayWinEdges.CopyTo(edges);
			_gameplayWinEdges.Clear();
			return edges;
		}

		private static void QueueGameplayToggleLockEdge(string token)
		{
			if (string.IsNullOrEmpty(token))
				return;

			long now = Environment.TickCount64;
			if (_lastToggleGameplayEdgeMs.TryGetValue(token, out long lastToggleEdge)
				&& now - lastToggleEdge < ToggleDuplicateCoalesceMs)
				return;

			if (!_toggleRepeatArmed.GetValueOrDefault(token, true))
				return;

			if (!_gameplayToggleEdges.Add(token))
				return;

			_lastToggleGameplayEdgeMs[token] = now;
			_toggleRepeatArmed[token] = false;
			LatchToggleTokenForCombo(token);
			AkDiag.Log("Toggle-lock gameplay edge: " + token);
		}

		private static void QueueGameplayWinEdge(uint vkCode)
		{
			string token = vkCode == VK_LWIN ? "LeftWindows" : "RightWindows";
			bool armed = vkCode == VK_LWIN ? _leftWinRepeatArmed : _rightWinRepeatArmed;
			if (!armed)
				return;

			long now = Environment.TickCount64;
			if (_lastWinGameplayEdgeMs.TryGetValue(token, out long lastWinEdge)
				&& now - lastWinEdge < GameplayWinEdgeCoalesceMs)
				return;

			if (!_gameplayWinEdges.Add(token))
				return;

			_lastWinGameplayEdgeMs[token] = now;
			if (vkCode == VK_LWIN)
				_leftWinRepeatArmed = false;
			else
				_rightWinRepeatArmed = false;
			AkDiag.Log("WinKey gameplay edge: " + token);
		}

		internal static bool TryApplyCtrlEscCaptureOrBlock(uint vk, bool isDown, out bool blocked)
			=> TryHandleCtrlEscChord(vk, isDown, out blocked);

		private static void QueueEscChordCapture()
		{
			_pendingEscChordCaptureToken = "Escape";
			_pendingEscChordCaptureTick = Environment.TickCount64;
			AkDiag.Log("Ctrl+Esc KEYDOWN queued for slot capture.");
		}

		private static bool TryHandleCtrlEscChord(uint vk, bool isDown, out bool blocked)
		{
			blocked = false;
			if (!isDown || !IsStartMenuChord(vk, isDown))
				return false;

			// Capture Ctrl+Esc as Escape only while Terraria is foreground. When the user
			// clicked away (purple wait still open), let the shell open Start normally.
			if (IsSlotCapturePassthrough)
			{
				if (!IsGameForegroundCore())
					return false;

				QueueEscChordCapture();
				blocked = true;
				return true;
			}

			if (ShouldBlockCtrlEscForShell())
			{
				if (isDown)
					SetShellBlockedEscapeDown(true);

				blocked = true;
				return true;
			}

			return false;
		}

		private static void QueueGameplayToggleLockEdge(uint vkCode)
		{
			string token = vkCode switch {
				VK_CAPITAL => "CapsLock",
				VK_NUMLOCK => "NumLock",
				VK_SCROLL => BindingRestrictions.ScrollLockToken,
				_ => null,
			};

			QueueGameplayToggleLockEdge(token);
		}

		/// <summary>Drops a toggle-lock edge that is too old to belong to the current slot capture.</summary>
		public static void DiscardStaleToggleLockCapture(long maxAgeMs = 500)
		{
			if (_pendingToggleLockCaptureToken == null)
				return;

			if (Environment.TickCount64 - _pendingToggleLockCaptureTick <= maxAgeMs)
				return;

			_pendingToggleLockCaptureToken = null;
			_pendingToggleLockCaptureTick = 0;
		}

		/// <summary>Consumes a toggle-lock KEYDOWN edge raised by the hook (used by slot capture).</summary>
		public static bool TryConsumeToggleLockPress(out string token)
		{
			token = _pendingToggleLockCaptureToken;
			if (token == null)
				return false;

			_pendingToggleLockCaptureToken = null;
			_pendingToggleLockCaptureTick = 0;
			return true;
		}

		/// <summary>
		/// Detects a fresh toggle-lock key press for slot capture. Uses physical KEYDOWN edges only
		/// (not the Caps/Num/Scroll LED latch), which is required for JP keyboards where Caps Lock
		/// alone does not toggle the LED.
		/// </summary>
		public static bool TryPollToggleLockEdge(out string token)
		{
			if (TryConsumeToggleLockPress(out token))
				return true;

			if (IsSlotCapturePassthrough && Environment.TickCount64 >= _captureWarmupUntilMs
				&& TryDrainAlternateToggleEdges(out token))
				return true;

			if (!IsSlotCapturePassthrough || Environment.TickCount64 < _captureWarmupUntilMs)
			{
				token = null;
				return false;
			}

			if (TryPhysicalKeyEdge(VK_CAPITAL, "CapsLock", ref _prevCapsHighBit, out token))
			{
				AkDiag.Log("Toggle-lock physical edge for capture: " + token);
				return true;
			}

			if (TryPhysicalKeyEdge(VK_NUMLOCK, "NumLock", ref _prevNumHighBit, out token))
			{
				AkDiag.Log("Toggle-lock physical edge for capture: " + token);
				return true;
			}

			if (TryPhysicalKeyEdge(VK_SCROLL, BindingRestrictions.ScrollLockToken, ref _prevScrollHighBit, out token))
			{
				AkDiag.Log("Toggle-lock physical edge for capture: " + token);
				return true;
			}

			if (TryBoolEdge(IsCapsLockPhysicallyDown, ref _prevCapsPhysicallyDown, "CapsLock", out token))
			{
				AkDiag.Log("Toggle-lock hook edge for capture: " + token);
				return true;
			}

			if (TryBoolEdge(IsNumLockPhysicallyDown, ref _prevNumPhysicallyDown, "NumLock", out token))
			{
				AkDiag.Log("Toggle-lock hook edge for capture: " + token);
				return true;
			}

			if (TryBoolEdge(IsScrollLockPhysicallyDown, ref _prevScrollPhysicallyDown, BindingRestrictions.ScrollLockToken, out token))
			{
				AkDiag.Log("Toggle-lock hook edge for capture: " + token);
				return true;
			}

			token = null;
			return false;
		}

		private static bool TryDrainAlternateToggleEdges(out string token, bool allowImeEdges = true)
		{
			GameWindowInputHook.Poll();

			foreach (string edge in GameWindowInputHook.DrainEdges())
			{
				token = edge;
				AkDiag.Log("Toggle-lock window edge: " + token);
				return true;
			}

			JpCapsLockDetector.Poll(allowImeEdges);

			foreach (string edge in JpCapsLockDetector.DrainEdges())
			{
				token = edge;
				AkDiag.Log("Toggle-lock JP edge: " + token);
				return true;
			}

			token = null;
			return false;
		}

		private static void PollAlternateToggleSources(bool allowImeEdges)
		{
			if (!TryDrainAlternateToggleEdges(out string token, allowImeEdges))
				return;

			QueueGameplayToggleLockEdge(token);
		}

		private static bool TryPhysicalKeyEdge(uint vkCode, string tokenName, ref bool prevDown, out string token)
		{
			bool down = IsPhysicalKeyDown(vkCode);
			if (!down || prevDown)
			{
				prevDown = down;
				token = null;
				return false;
			}

			prevDown = down;
			token = tokenName;
			return true;
		}

		private static bool TryBoolEdge(bool current, ref bool previous, string tokenName, out string token)
		{
			if (!current || previous)
			{
				previous = current;
				token = null;
				return false;
			}

			previous = current;
			token = tokenName;
			return true;
		}

		private static void QueueToggleLockCapture(uint vkCode)
		{
			_pendingToggleLockCaptureToken = vkCode switch {
				VK_CAPITAL => "CapsLock",
				VK_NUMLOCK => "NumLock",
				VK_SCROLL => BindingRestrictions.ScrollLockToken,
				_ => null,
			};

			if (_pendingToggleLockCaptureToken != null)
			{
				_pendingToggleLockCaptureTick = Environment.TickCount64;
				AkDiag.Log("Toggle-lock KEYDOWN queued for capture: " + _pendingToggleLockCaptureToken);
			}
		}

		private static bool TryResolveToggleLockEvent(KBDLLHOOKSTRUCT data, out uint vkCode)
		{
			uint scan = data.scanCode & 0xFF;

			if (data.vkCode == VK_CAPITAL || scan == SC_CAPITAL)
			{
				vkCode = VK_CAPITAL;
				return true;
			}

			if (data.vkCode == VK_NUMLOCK || scan == SC_NUMLOCK)
			{
				vkCode = VK_NUMLOCK;
				return true;
			}

			if (data.vkCode == VK_SCROLL || scan == SC_SCROLL)
			{
				vkCode = VK_SCROLL;
				return true;
			}

			vkCode = 0;
			return false;
		}

		private static IntPtr ThreadKeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
		{
			if (nCode >= 0)
			{
				uint vk = (uint)wParam.ToInt32();
				long lp = lParam.ToInt64();
				bool isUp = (lp & 0x80000000) != 0;
				bool isDown = !isUp;

				HandleShellBlockedEscapeKeyUp(vk, isUp);

				if (IsGameForegroundCore())
					_lastGameForegroundMs = Environment.TickCount64;

				if (isDown && vk == VK_ESCAPE && IsControlDown())
				{
					bool suppress = ShouldBlockCtrlEscForShell()
						|| (IsSlotCapturePassthrough && IsGameForegroundCore());
					StartMenuDiag.LogThread(vk, isDown, suppress, suppress ? "ctrl+esc" : "ctrl+esc reason=" + DescribeWhyNotBlocking());
				}

				if (TryHandleCtrlEscChord(vk, isDown, out bool blockCtrlEsc) && blockCtrlEsc)
				{
					StartMenuDiag.LogThread(vk, isDown, blocked: true, IsSlotCapturePassthrough ? "ctrl+esc-capture" : "ctrl+esc-chord");
					return (IntPtr)1;
				}

				if (vk == VK_LWIN || vk == VK_RWIN)
				{
					if (ShouldBlockWindowsKey())
					{
						if (isDown)
						{
							if (IsSlotCapturePassthrough)
								QueueWinKeyCapture(vk);
							else
								QueueGameplayWinEdge(vk);
						}

						ApplyWindowsKeyState(vk, isDown, isUp);
						StartMenuDiag.LogThread(vk, isDown, blocked: true, "win");
						return (IntPtr)1;
					}

					StartMenuDiag.LogThread(vk, isDown, blocked: false, "win reason=" + DescribeWhyNotBlocking());
				}
				else if (TryHandleWinChordPassthrough(vk, isDown, "thread", out bool swallowChord) && swallowChord)
					return (IntPtr)1;
				else if (TryFinishMonitorMoveArrowUp(vk, isUp, out bool swallowArrowUp) && swallowArrowUp)
					return (IntPtr)1;
			}

			return CallNextHookEx(_threadHookId, nCode, wParam, lParam);
		}

		private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
		{
			if (nCode >= 0)
			{
				int msg = wParam.ToInt32();
				KBDLLHOOKSTRUCT data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);

				// Let our own injected events straight through, untouched.
				if (data.dwExtraInfo == InjectedMarker)
					return CallNextHookEx(_hookId, nCode, wParam, lParam);

				if (IsGameForegroundCore())
					_lastGameForegroundMs = Environment.TickCount64;

				bool isDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
				bool isUp = msg == WM_KEYUP || msg == WM_SYSKEYUP;

				HandleShellBlockedEscapeKeyUp(data.vkCode, isUp);

				if (IsSlotCapturePassthrough && isDown)
				{
					uint scan = data.scanCode & 0xFF;
					AkDiag.Log($"Slot capture hook keydown vk=0x{data.vkCode:X2} scan=0x{scan:X2}");
				}

				if (isDown && data.vkCode == VK_ESCAPE && IsControlDown())
				{
					bool suppress = ShouldBlockCtrlEscForShell()
						|| (IsSlotCapturePassthrough && IsGameForegroundCore());
					string reason = suppress ? "ctrl+esc" : "ctrl+esc reason=" + DescribeWhyNotBlocking();
					StartMenuDiag.LogHook(VK_ESCAPE, true, suppress, reason);
				}

				if (TryHandleCtrlEscChord(data.vkCode, isDown, out bool blockCtrlEsc) && blockCtrlEsc)
				{
					StartMenuDiag.LogHook(data.vkCode, isDown, blocked: true, IsSlotCapturePassthrough ? "ctrl+esc-capture" : "ctrl+esc-chord");
					return (IntPtr)1;
				}

				if (TryResolveWindowsVk(data, out uint winVk))
				{
					if (ShouldBlockWindowsKey())
					{
						if (isDown)
						{
							if (IsSlotCapturePassthrough)
								QueueWinKeyCapture(winVk);
							else
								QueueGameplayWinEdge(winVk);
						}

						ApplyWindowsKeyState(winVk, isDown, isUp);
						StartMenuDiag.LogHook(winVk, isDown, blocked: true, "win");
						return (IntPtr)1;
					}

					StartMenuDiag.LogHook(winVk, isDown, blocked: false, "win reason=" + DescribeWhyNotBlocking());
					if (winVk == VK_LWIN)
						IsLeftWindowsDown = false;
					else
						IsRightWindowsDown = false;
				}
				else if (TryResolveToggleLockEvent(data, out uint toggleVk))
				{
					if (IsGameForeground())
					{
						if (isDown)
						{
							QueueToggleLockCapture(toggleVk);
							if (!IsSlotCapturePassthrough)
								QueueGameplayToggleLockEdge(toggleVk);
						}

						ApplyTogglePhysicalDown(toggleVk, isDown, isUp);

						// During slot capture let the key reach XNA/OS so binding can detect it.
						if (IsSlotCapturePassthrough)
							return CallNextHookEx(_hookId, nCode, wParam, lParam);

						// Swallow so the OS LED state does not fight shortcut edge detection.
						return (IntPtr)1;
					}

					IsCapsLockPhysicallyDown = false;
					IsNumLockPhysicallyDown = false;
					IsScrollLockPhysicallyDown = false;
				}
				else if (TryHandleWinChordPassthrough(data.vkCode, isDown, "hook", out bool swallowChord) && swallowChord)
					return (IntPtr)1;
				else if (TryFinishMonitorMoveArrowUp(data.vkCode, isUp, out bool swallowArrowUp) && swallowArrowUp)
					return (IntPtr)1;
			}

			return CallNextHookEx(_hookId, nCode, wParam, lParam);
		}

		/// <summary>
		/// Runs deferred Win chord work on the main thread (snip overlay, window move/snap).
		/// Must not call SendInput from inside the keyboard hook callback.
		/// </summary>
		public static void FlushDeferredWinActions()
		{
			FlushScreenshotRequests();
			FlushPendingArrowChords();
		}

		private static void RequestArrowChord(uint arrowVk, uint winVk, bool monitorMove, bool virtualDesktop)
		{
			_pendingArrowChordVk = arrowVk;
			_pendingArrowChordWinVk = winVk;
			_pendingArrowChordWithShift = monitorMove;
			_pendingArrowChordVirtualDesktop = virtualDesktop;
			WinChordDiag.Log(
				"request",
				arrowVk,
				"arrow-queued",
				"win=0x" + winVk.ToString("X")
					+ " monitor=" + monitorMove
					+ " vd=" + virtualDesktop);
		}

		private static void FlushPendingArrowChords()
		{
			uint arrowVk = _pendingArrowChordVk;
			if (arrowVk == 0)
				return;

			uint winVk = _pendingArrowChordWinVk;
			bool monitorMove = _pendingArrowChordWithShift;
			bool virtualDesktop = _pendingArrowChordVirtualDesktop;
			_pendingArrowChordVk = 0;
			_pendingArrowChordWinVk = 0;
			_pendingArrowChordWithShift = false;
			_pendingArrowChordVirtualDesktop = false;

			if (monitorMove)
			{
				bool toRight = arrowVk == VK_RIGHT;
				bool moved = TryMoveGameWindowToAdjacentMonitor(toRight);
				WinChordDiag.Log(
					"flush",
					arrowVk,
					moved ? "monitor-move-ok" : "monitor-move-fail",
					"dir=" + (toRight ? "right" : "left"));
				return;
			}

			if (virtualDesktop)
			{
				uint sent = 0;
				// RIDEV_NOHOTKEYS blocks OS Win+Ctrl+Arrow (virtual desktop). Drop it briefly for inject.
				GameWindowInputHook.RunWithOsWinHotkeysEnabled(() => {
					sent = EmitWinCtrlArrowChord(arrowVk, winVk);
				});
				WinChordDiag.Log(
					"flush",
					arrowVk,
					sent > 0 ? "vd-inject-ok" : "vd-inject-fail",
					"win=0x" + winVk.ToString("X") + " sent=" + sent);
			}
		}

		/// <summary>
		/// Moves the game window to the next/previous monitor (Win+Shift+←/→ equivalent).
		/// When Terraria is in borderless / fullscreen-like mode, also retargets resolution to that
		/// monitor so mixed-DPI / mixed-resolution setups fill the destination display.
		/// </summary>
		private static bool TryMoveGameWindowToAdjacentMonitor(bool toRight)
		{
			IntPtr hwnd = GameWindowHandle.TryGet(preferForeground: true);
			if (hwnd == IntPtr.Zero)
				hwnd = GetForegroundWindow();

			if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
			{
				AkDiag.Log("MonitorMove aborted: no game HWND.");
				return false;
			}

			List<MonitorEntry> monitors = EnumerateMonitors();
			if (monitors.Count < 2)
			{
				AkDiag.Log("MonitorMove aborted: monitorCount=" + monitors.Count);
				return false;
			}

			IntPtr currentHandle = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
			int currentIndex = monitors.FindIndex(m => m.Handle == currentHandle);
			if (currentIndex < 0)
			{
				AkDiag.Log("MonitorMove aborted: current monitor not in enum list.");
				return false;
			}

			MonitorEntry current = monitors[currentIndex];
			MonitorEntry? target = FindAdjacentMonitor(monitors, current, toRight);
			if (target == null || target.Value.Handle == current.Handle)
			{
				AkDiag.Log("MonitorMove aborted: no adjacent monitor dir=" + (toRight ? "right" : "left"));
				return false;
			}

			MonitorEntry dest = target.Value;
			bool fitBorderless = ShouldFitBorderlessToDestinationMonitor();

			if (IsZoomed(hwnd))
				ShowWindow(hwnd, SW_RESTORE);

			if (fitBorderless)
				return MoveAndFitBorderlessToMonitor(hwnd, dest);

			return MoveWindowKeepSize(hwnd, current, dest);
		}

		/// <summary>True when the game is already running as borderless / fullscreen-like.</summary>
		private static bool ShouldFitBorderlessToDestinationMonitor()
		{
			try
			{
				if (Main.screenBorderless || Main.PendingBorderlessState)
					return true;

				if (Main.graphics != null && Main.graphics.IsFullScreen)
					return true;
			}
			catch
			{
				// Main may be unavailable during early load.
			}

			return false;
		}

		private static bool MoveAndFitBorderlessToMonitor(IntPtr hwnd, MonitorEntry dest)
		{
			int destW = dest.Monitor.Width;
			int destH = dest.Monitor.Height;
			if (destW < 640 || destH < 480)
			{
				AkDiag.Log("MonitorMove borderless-fit aborted: dest too small " + destW + "x" + destH);
				return false;
			}

			try
			{
				// Match Terraria's render buffer to the destination display.
				Main.screenBorderless = true;
				Main.PendingBorderlessState = true;
				Main.SetResolution(destW, destH);
				Main.screenBorderlessPendingResizes = 6;
			}
			catch (Exception ex)
			{
				AkDiag.Log("MonitorMove SetResolution failed: " + ex.Message);
				return false;
			}

			TryApplyBorderlessWindowStyle(hwnd);

			bool ok = SetWindowPos(
				hwnd,
				IntPtr.Zero,
				dest.Monitor.Left,
				dest.Monitor.Top,
				destW,
				destH,
				SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED | SWP_SHOWWINDOW);

			AkDiag.Log(
				"MonitorMove borderless-fit " + (ok ? "ok" : "SetWindowPos-fail")
				+ " to=(" + dest.Monitor.Left + "," + dest.Monitor.Top + ")"
				+ " size=" + destW + "x" + destH
				+ " monitors=" + EnumerateMonitors().Count);

			return ok;
		}

		private static bool MoveWindowKeepSize(IntPtr hwnd, MonitorEntry current, MonitorEntry dest)
		{
			if (!GetWindowRect(hwnd, out RECT windowRect))
			{
				AkDiag.Log("MonitorMove aborted: GetWindowRect failed.");
				return false;
			}

			int width = windowRect.Width;
			int height = windowRect.Height;
			int newX = windowRect.Left + (dest.Monitor.Left - current.Monitor.Left);
			int newY = windowRect.Top + (dest.Monitor.Top - current.Monitor.Top);

			RECT work = dest.Work;
			if (newX + width > work.Right)
				newX = work.Right - width;
			if (newY + height > work.Bottom)
				newY = work.Bottom - height;
			if (newX < work.Left)
				newX = work.Left;
			if (newY < work.Top)
				newY = work.Top;

			bool ok = SetWindowPos(hwnd, IntPtr.Zero, newX, newY, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);

			AkDiag.Log(
				"MonitorMove keep-size " + (ok ? "ok" : "SetWindowPos-fail")
				+ " from=(" + current.Monitor.Left + "," + current.Monitor.Top + ")"
				+ " to=(" + dest.Monitor.Left + "," + dest.Monitor.Top + ")"
				+ " monitors=" + EnumerateMonitors().Count);

			return ok;
		}

		private static void TryApplyBorderlessWindowStyle(IntPtr hwnd)
		{
			try
			{
				long style = GetWindowLongValue(hwnd, GWL_STYLE);
				style &= ~(WS_CAPTION | WS_THICKFRAME | WS_MINIMIZE | WS_MAXIMIZE | WS_SYSMENU);
				SetWindowLongValue(hwnd, GWL_STYLE, style);
			}
			catch (Exception ex)
			{
				AkDiag.Log("MonitorMove borderless style skipped: " + ex.Message);
			}
		}

		private static long GetWindowLongValue(IntPtr hwnd, int index)
		{
			if (IntPtr.Size == 8)
				return GetWindowLongPtr64(hwnd, index).ToInt64();
			return GetWindowLong32(hwnd, index);
		}

		private static void SetWindowLongValue(IntPtr hwnd, int index, long value)
		{
			if (IntPtr.Size == 8)
				SetWindowLongPtr64(hwnd, index, new IntPtr(value));
			else
				SetWindowLong32(hwnd, index, (int)value);
		}

		private static MonitorEntry? FindAdjacentMonitor(List<MonitorEntry> monitors, MonitorEntry current, bool toRight)
		{
			if (monitors.Count < 2)
				return null;

			// Workshop / native Win+Shift+←→ visits every display in a stable order.
			// Strict "edge neighbor" only reached the bottom row (1-3-2) on multi-row layouts.
			List<MonitorEntry> ordered = new(monitors);
			ordered.Sort(CompareMonitorReadingOrder);

			int index = ordered.FindIndex(m => m.Handle == current.Handle);
			if (index < 0)
				return null;

			int next = toRight
				? (index + 1) % ordered.Count
				: (index - 1 + ordered.Count) % ordered.Count;

			return ordered[next];
		}

		/// <summary>Left-to-right, then top-to-bottom — stable for any monitor count/layout.</summary>
		private static int CompareMonitorReadingOrder(MonitorEntry a, MonitorEntry b)
		{
			const int columnBucket = 64;
			int aCol = a.Monitor.Left / columnBucket;
			int bCol = b.Monitor.Left / columnBucket;
			int cmp = aCol.CompareTo(bCol);
			if (cmp != 0)
				return cmp;

			cmp = a.Monitor.Top.CompareTo(b.Monitor.Top);
			if (cmp != 0)
				return cmp;

			cmp = a.Monitor.Left.CompareTo(b.Monitor.Left);
			if (cmp != 0)
				return cmp;

			return a.Handle.ToInt64().CompareTo(b.Handle.ToInt64());
		}

		private static List<MonitorEntry> EnumerateMonitors()
		{
			List<MonitorEntry> list = new();
			EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data) => {
				MONITORINFO info = new() { cbSize = Marshal.SizeOf<MONITORINFO>() };
				if (GetMonitorInfo(hMonitor, ref info))
					list.Add(new MonitorEntry(hMonitor, info.rcMonitor, info.rcWork));
				return true;
			}, IntPtr.Zero);
			return list;
		}

		private static void FlushScreenshotRequests()
		{
			if (!_snipRequested)
				return;

			_snipRequested = false;

			try
			{
				Process.Start(new ProcessStartInfo("ms-screenclip:") { UseShellExecute = true });
				WinChordDiag.Log("flush", VK_S, "snip-launched", "ok");
				AkDiag.Log("Launched ms-screenclip: for Win+Shift+S.");
			}
			catch (Exception ex)
			{
				AdvancedKeybinds.Instance?.Logger.Warn("Advanced Keybinds failed to launch the snip overlay: " + ex.Message);
			}
		}

		/// <summary>
		/// Re-inject Win+Ctrl+←/→ so the OS virtual-desktop switch runs while our hook is swallowing Win.
		/// </summary>
		private static uint EmitWinCtrlArrowChord(uint arrowVk, uint winVk)
		{
			uint ctrlVk = (GetAsyncKeyState((int)VK_RCONTROL) & 0x8000) != 0 ? VK_RCONTROL : VK_LCONTROL;
			INPUT[] batch = {
				KeyEvent(winVk, false),
				KeyEvent(ctrlVk, false),
				KeyEvent(arrowVk, false),
				KeyEvent(arrowVk, true),
				KeyEvent(ctrlVk, true),
				KeyEvent(winVk, true),
			};

			_syntheticChordDepth++;
			try
			{
				uint sent = SendInput((uint)batch.Length, batch, Marshal.SizeOf<INPUT>());
				if (sent != batch.Length)
					AkDiag.Log($"Win+Ctrl+Arrow SendInput sent {sent}/{batch.Length} events (error {Marshal.GetLastWin32Error()}).");
				else
					AkDiag.Log($"Win+Ctrl+Arrow injected {sent} events arrow=0x{arrowVk:X2} ctrl=0x{ctrlVk:X2}.");

				return sent;
			}
			catch (Exception ex)
			{
				AdvancedKeybinds.Instance?.Logger.Warn("Advanced Keybinds failed to inject Win+Ctrl+Arrow: " + ex.Message);
				return 0;
			}
			finally
			{
				_syntheticChordDepth--;
			}
		}

		private static INPUT KeyEvent(uint vk, bool up)
		{
			uint flags = 0;
			if (up)
				flags |= KEYEVENTF_KEYUP;
			if (vk == VK_LWIN || vk == VK_RWIN || vk == VK_LEFT || vk == VK_RIGHT
				|| vk == VK_RCONTROL)
				flags |= KEYEVENTF_EXTENDEDKEY;

			return new INPUT
			{
				type = INPUT_KEYBOARD,
				u = new INPUTUNION
				{
					ki = new KEYBDINPUT
					{
						wVk = (ushort)vk,
						wScan = 0,
						dwFlags = flags,
						time = 0,
						dwExtraInfo = InjectedMarker,
					},
				},
			};
		}

		private static bool IsGameForegroundCore()
		{
			IntPtr foreground = GetForegroundWindow();
			if (foreground == IntPtr.Zero)
				return false;

			GetWindowThreadProcessId(foreground, out uint processId);
			return processId == GetCurrentProcessId();
		}

		private static bool IsGameForeground()
		{
			if (IsGameForegroundCore())
			{
				_lastGameForegroundMs = Environment.TickCount64;
				return true;
			}

			// Start menu can take fg between hook and WndProc on the same Win press.
			return Environment.TickCount64 - _lastGameForegroundMs < ForegroundGraceMs;
		}
	}
}
