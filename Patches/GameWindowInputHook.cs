using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using AdvancedKeybinds.Binding;
using AdvancedKeybinds.UI;
using Terraria;

namespace AdvancedKeybinds.Patches
{
	/// <summary>
	/// Chains the game window WndProc to observe WM_KEYDOWN, WM_INPUT, and WM_IME_NOTIFY.
	/// JP Caps Lock alone often never reaches WH_KEYBOARD_LL but may still arrive here.
	/// </summary>
	internal static class GameWindowInputHook
	{
		private const int WM_KEYDOWN = 0x0100;
		private const int WM_KEYUP = 0x0101;
		private const int WM_SYSKEYDOWN = 0x0104;
		private const int WM_SYSKEYUP = 0x0105;
		private const int WM_SYSCOMMAND = 0x0112;
		private const int WM_HOTKEY = 0x0312;
		private const int SC_KEYMENU = 0xF100;
		private const int WM_INPUT = 0x00FF;
		private const int WM_IME_NOTIFY = 0x0282;
		private const int IMN_SETCONVERSIONMODE = 0x0006;
		private const uint RID_INPUT = 0x10000003;
		private const uint RIDEV_REMOVE = 0x00000001;
		private const uint RIDEV_INPUT = 0x00000001;
		private const uint RIDEV_NOHOTKEYS = 0x00000200;
		private const ushort RIM_TYPEKEYBOARD = 1;
		private const ushort RI_KEY_BREAK = 0x0001;
		private const ushort SC_CAPITAL = 0x3A;
		private const ushort VK_CAPITAL = 0x14;
		private const ushort SC_NUMLOCK = 0x45;
		private const ushort SC_SCROLL = 0x46;
		private const uint VK_LWIN = 0x5B;
		private const uint VK_RWIN = 0x5C;
		private const uint VK_SHIFT = 0x10;
		private const uint VK_ESCAPE = 0x1B;
		private const int GWLP_WNDPROC = -4;

		private static readonly HashSet<string> PendingEdges = new(StringComparer.OrdinalIgnoreCase);
		private static readonly object InstallLock = new();

		private static WndProcDelegate _wndProc;
		private static IntPtr _hookedHwnd;
		private static IntPtr _originalWndProc;
		private static bool _wndProcInstalled;
		private static bool _rawInputRegistered;
		private static string _rawInputMode = "none";
		private static long _lastImeCapsEdgeMs;

		internal static bool IsInstalledForDiag => _wndProcInstalled;
		internal static long HookedHwndForDiag => _hookedHwnd.ToInt64();
		internal static string RawInputModeForDiag => _rawInputMode;
		private static ushort _lastKeydownScan;
		private static long _lastKeydownScanMs;

		private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
		private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

		[StructLayout(LayoutKind.Sequential)]
		private struct RAWINPUTDEVICE
		{
			public ushort usUsagePage;
			public ushort usUsage;
			public uint dwFlags;
			public IntPtr hwndTarget;
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct RAWINPUTHEADER
		{
			public uint dwType;
			public uint dwSize;
			public IntPtr hDevice;
			public IntPtr wParam;
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct RAWKEYBOARD
		{
			public ushort MakeCode;
			public ushort Flags;
			public ushort Reserved;
			public ushort VKey;
			public uint Message;
			public uint ExtraInformation;
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct RawInputData
		{
			public RAWINPUTHEADER header;
			public RAWKEYBOARD keyboard;
		}

		[DllImport("user32.dll")]
		private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

		[DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowLongPtrW")]
		private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

		[DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowLongW")]
		private static extern IntPtr SetWindowLongPtr32(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

		[DllImport("user32.dll", SetLastError = true)]
		private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] pRawInputDevices, uint uiNumDevices, int cbSize);

		[DllImport("user32.dll", SetLastError = true)]
		private static extern uint GetRawInputData(IntPtr hRawInput, uint uiCommand, IntPtr pData, ref uint pcbSize, uint cbSizeHeader);

		[DllImport("user32.dll")]
		private static extern bool IsWindow(IntPtr hWnd);

		[DllImport("user32.dll")]
		private static extern bool IsWindowVisible(IntPtr hWnd);

		[DllImport("user32.dll")]
		private static extern IntPtr GetForegroundWindow();

		[DllImport("user32.dll")]
		private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

		[DllImport("user32.dll")]
		private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

		[DllImport("user32.dll")]
		private static extern short GetAsyncKeyState(int vKey);

		[DllImport("kernel32.dll")]
		private static extern uint GetCurrentProcessId();

		private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
			=> IntPtr.Size == 8
				? SetWindowLongPtr64(hWnd, nIndex, dwNewLong)
				: SetWindowLongPtr32(hWnd, nIndex, dwNewLong);

		/// <summary>
		/// Installs the WndProc hook only while Caps Lock capture/gameplay needs it. Avoids chaining
		/// arbitrary visible HWNDs on the title screen, which can make Escape feel intermittent.
		/// </summary>
		private static long _lastDeferredLogMs;

		public static void EnsureInstalledIfNeeded()
		{
			if (!OperatingSystem.IsWindows())
				return;

			lock (InstallLock)
			{
				if (!ShouldKeepHook())
				{
					if (_wndProcInstalled)
						Uninstall();
					return;
				}

				if (_wndProcInstalled && ShouldRehookForeground())
				{
					AkDiag.Log("GameWindowInputHook rehooking foreground hwnd was=0x" + _hookedHwnd.ToInt64().ToString("X"));
					RestoreWndProc();
					_wndProcInstalled = false;
					_rawInputRegistered = false;
					_rawInputMode = "none";
					GameWindowHandle.InvalidateCache();
				}

				if (_wndProcInstalled)
					return;

				bool preferForeground = true;
				IntPtr hwnd = GameWindowHandle.TryGet(preferForeground);
				if (hwnd != IntPtr.Zero && TryInstallOnWindow(hwnd))
					return;

				foreach (IntPtr candidate in CollectWindowCandidates())
				{
					if (TryInstallOnWindow(candidate))
						return;
				}

				long now = Environment.TickCount64;
				if (now - _lastDeferredLogMs > 5000)
				{
					_lastDeferredLogMs = now;
					AkDiag.Log("GameWindowInputHook deferred: game window handle not ready.");
				}
			}
		}

		private static bool ShouldRehookForeground()
		{
			if (_hookedHwnd == IntPtr.Zero)
				return false;

			IntPtr fg = GetForegroundWindow();
			if (fg == IntPtr.Zero || fg == _hookedHwnd)
				return false;

			GetWindowThreadProcessId(fg, out uint fgPid);
			if (fgPid != GetCurrentProcessId())
				return false;

			return IsWindowVisible(fg);
		}

		private static bool ShouldKeepHook()
		{
			if (WindowsKeySuppressor.IsSlotCapturePassthrough)
				return true;

			if (ComboPanel.IsOpen)
				return true;

			if (WindowsKeySuppressor.IsInstalled)
				return true;

			return Main.LocalPlayer != null && Main.LocalPlayer.active;
		}

		public static void Uninstall()
		{
			lock (InstallLock)
			{
				PendingEdges.Clear();
				RestoreWndProc();
				_wndProcInstalled = false;
				_rawInputRegistered = false;
				_rawInputMode = "none";
				GameWindowHandle.InvalidateCache();
			}
		}

		public static void Poll()
		{
			EnsureInstalledIfNeeded();
			TryRefreshNoHotkeys();
		}

		public static IReadOnlyCollection<string> DrainEdges()
		{
			if (PendingEdges.Count == 0)
				return Array.Empty<string>();

			string[] edges = new string[PendingEdges.Count];
			PendingEdges.CopyTo(edges);
			PendingEdges.Clear();
			return edges;
		}

		private static List<IntPtr> CollectWindowCandidates()
		{
			List<IntPtr> candidates = new();
			HashSet<long> seen = new();
			uint pid = GetCurrentProcessId();

			void TryAdd(IntPtr hwnd)
			{
				if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
					return;

				if (!seen.Add(hwnd.ToInt64()))
					return;

				GetWindowThreadProcessId(hwnd, out uint windowPid);
				if (windowPid != pid)
					return;

				candidates.Add(hwnd);
			}

			TryAdd(GetForegroundWindow());
			TryAdd(GameWindowHandle.TryGet(preferForeground: WindowsKeySuppressor.IsSlotCapturePassthrough));

			try
			{
				if (Main.instance?.Window != null)
				{
					PropertyInfo handle = Main.instance.Window.GetType().GetProperty("Handle");
					if (handle?.GetValue(Main.instance.Window) is IntPtr windowHandle)
						TryAdd(windowHandle);
				}
			}
			catch
			{
			}

			foreach (FieldInfo field in typeof(Main).GetFields(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
			{
				if (!field.Name.Contains("wnd", StringComparison.OrdinalIgnoreCase)
					&& !field.Name.Contains("hwnd", StringComparison.OrdinalIgnoreCase)
					&& !field.Name.Contains("handle", StringComparison.OrdinalIgnoreCase))
					continue;

				try
				{
					object value = field.IsStatic ? field.GetValue(null) : Main.instance != null ? field.GetValue(Main.instance) : null;
					if (value is IntPtr ptr)
						TryAdd(ptr);
					else if (value != null)
					{
						PropertyInfo nested = value.GetType().GetProperty("Handle");
						if (nested?.GetValue(value) is IntPtr nestedHwnd)
							TryAdd(nestedHwnd);
					}
				}
				catch
				{
				}
			}

			TryAdd(Process.GetCurrentProcess().MainWindowHandle);

			EnumWindows((hwnd, _) => {
				if (!IsWindowVisible(hwnd))
					return true;

				GetWindowThreadProcessId(hwnd, out uint windowPid);
				if (windowPid == pid)
					TryAdd(hwnd);

				return true;
			}, IntPtr.Zero);

			return candidates;
		}

		private static bool TryInstallOnWindow(IntPtr hwnd)
		{
			if (hwnd != IntPtr.Zero && !IsWindow(hwnd))
				return false;

			try
			{
				_wndProc = ChainedWndProc;
				IntPtr proc = Marshal.GetFunctionPointerForDelegate(_wndProc);
				IntPtr target = hwnd == IntPtr.Zero ? GetForegroundWindow() : hwnd;
				if (target == IntPtr.Zero)
					return false;

				IntPtr previous = SetWindowLongPtr(target, GWLP_WNDPROC, proc);
				if (previous == IntPtr.Zero)
				{
					AkDiag.Log("GameWindowInputHook SetWindowLongPtr failed hwnd=" + target + " err=" + Marshal.GetLastWin32Error());
					return false;
				}

				_hookedHwnd = target;
				_originalWndProc = previous;
				_wndProcInstalled = true;
				TryRegisterRawInput(target);
				AkDiag.Log("GameWindowInputHook installed hwnd=" + target + " raw=" + _rawInputRegistered);
				StartMenuDiag.LogInstall("wndproc installed hwnd=0x" + target.ToInt64().ToString("X") + " rawMode=" + _rawInputMode);
				return true;
			}
			catch (Exception ex)
			{
				AkDiag.Log("GameWindowInputHook install failed hwnd=" + hwnd + ": " + ex.Message);
				RestoreWndProc();
				return false;
			}
		}

		private static long _lastNoHotkeysRetryMs;

		private static void RemoveKeyboardRawInput(int cbSize)
		{
			// RIDEV_REMOVE requires hwndTarget == NULL (otherwise error 87).
			RAWINPUTDEVICE[] remove = {
				new() {
					usUsagePage = 0x01,
					usUsage = 0x06,
					dwFlags = RIDEV_REMOVE,
					hwndTarget = IntPtr.Zero,
				},
			};

			RegisterRawInputDevices(remove, 1, cbSize);
		}

		private static bool TryRegisterRawInputDevice(RAWINPUTDEVICE device, int cbSize, out int err)
		{
			RAWINPUTDEVICE[] devices = { device };
			if (RegisterRawInputDevices(devices, 1, cbSize))
			{
				err = 0;
				return true;
			}

			err = Marshal.GetLastWin32Error();
			return false;
		}

		private static void TryRegisterRawInput(IntPtr hwnd)
		{
			int cbSize = Marshal.SizeOf<RAWINPUTDEVICE>();
			RemoveKeyboardRawInput(cbSize);

			(string mode, uint flags, IntPtr target)[] strategies = {
				("INPUT+NOHOTKEYS", RIDEV_INPUT | RIDEV_NOHOTKEYS, hwnd),
				("NOHOTKEYS", RIDEV_NOHOTKEYS, hwnd),
				("INPUT+NOHOTKEYS@global", RIDEV_INPUT | RIDEV_NOHOTKEYS, IntPtr.Zero),
				("NOHOTKEYS@global", RIDEV_NOHOTKEYS, IntPtr.Zero),
				("INPUT", RIDEV_INPUT, hwnd),
				("GLOBAL", 0, IntPtr.Zero),
			};

			List<int> errors = new();
			foreach ((string mode, uint flags, IntPtr target) in strategies)
			{
				if (hwnd == IntPtr.Zero && target != IntPtr.Zero)
					continue;

				RAWINPUTDEVICE device = new() {
					usUsagePage = 0x01,
					usUsage = 0x06,
					dwFlags = flags,
					hwndTarget = target,
				};

				if (TryRegisterRawInputDevice(device, cbSize, out int err))
				{
					_rawInputRegistered = true;
					_rawInputMode = mode;
					AkDiag.Log("GameWindowInputHook raw input registered mode=" + mode + " hwnd=" + target + " cb=" + cbSize);
					return;
				}

				errors.Add(err);
				AkDiag.Log("GameWindowInputHook raw input failed mode=" + mode + " err=" + err);
			}

			_rawInputRegistered = false;
			_rawInputMode = "FAILED:" + string.Join("/", errors);
			AkDiag.Log("GameWindowInputHook raw input registration failed err=" + _rawInputMode);
		}

		/// <summary>
		/// <c>RIDEV_NOHOTKEYS</c> disables OS Win hotkeys (including Win+Ctrl+←/→ virtual-desktop switch).
		/// Temporarily register without that flag, run <paramref name="action"/> (e.g. SendInput chord),
		/// then restore the normal raw-input strategy. Physical Win stays swallowed by the LL hook.
		/// </summary>
		internal static void RunWithOsWinHotkeysEnabled(Action action)
		{
			if (action == null)
				return;

			bool hadNoHotkeys = _rawInputMode.IndexOf("NOHOTKEYS", StringComparison.OrdinalIgnoreCase) >= 0;
			if (!hadNoHotkeys || !_rawInputRegistered)
			{
				action();
				return;
			}

			IntPtr hwnd = _hookedHwnd;
			if (hwnd == IntPtr.Zero)
				hwnd = GameWindowHandle.TryGet(preferForeground: true);

			int cbSize = Marshal.SizeOf<RAWINPUTDEVICE>();
			RemoveKeyboardRawInput(cbSize);

			RAWINPUTDEVICE inputOnly = new() {
				usUsagePage = 0x01,
				usUsage = 0x06,
				dwFlags = hwnd != IntPtr.Zero ? RIDEV_INPUT : 0,
				hwndTarget = hwnd,
			};

			if (!TryRegisterRawInputDevice(inputOnly, cbSize, out int err))
			{
				inputOnly.dwFlags = 0;
				inputOnly.hwndTarget = IntPtr.Zero;
				if (!TryRegisterRawInputDevice(inputOnly, cbSize, out err))
					AkDiag.Log("GameWindowInputHook temp hotkeys enable failed err=" + err);
			}
			else
			{
				_rawInputRegistered = true;
				_rawInputMode = "INPUT(temp-os-hotkeys)";
				AkDiag.Log("GameWindowInputHook temporarily enabled OS Win hotkeys for virtual-desktop chord.");
			}

			try
			{
				action();
			}
			finally
			{
				TryRegisterRawInput(hwnd);
			}
		}

		private static void TryRefreshNoHotkeys()
		{
			if (!_wndProcInstalled || _hookedHwnd == IntPtr.Zero)
				return;

			if (_rawInputMode.Contains("NOHOTKEYS", StringComparison.Ordinal))
				return;

			long now = Environment.TickCount64;
			if (now - _lastNoHotkeysRetryMs < 2000)
				return;

			_lastNoHotkeysRetryMs = now;
			int cbSize = Marshal.SizeOf<RAWINPUTDEVICE>();
			RAWINPUTDEVICE device = new() {
				usUsagePage = 0x01,
				usUsage = 0x06,
				dwFlags = RIDEV_INPUT | RIDEV_NOHOTKEYS,
				hwndTarget = _hookedHwnd,
			};

			if (TryRegisterRawInputDevice(device, cbSize, out int err))
			{
				_rawInputMode = _rawInputMode + "+NOHOTKEYS";
				AkDiag.Log("GameWindowInputHook refreshed NOHOTKEYS hwnd=" + _hookedHwnd);
			}
			else
			{
				AkDiag.Log("GameWindowInputHook NOHOTKEYS refresh failed err=" + err + " mode=" + _rawInputMode);
			}
		}

		private static void RestoreWndProc()
		{
			if (_hookedHwnd == IntPtr.Zero || _originalWndProc == IntPtr.Zero)
				return;

			try
			{
				SetWindowLongPtr(_hookedHwnd, GWLP_WNDPROC, _originalWndProc);
			}
			catch
			{
			}

			_hookedHwnd = IntPtr.Zero;
			_originalWndProc = IntPtr.Zero;
			_wndProc = null;
		}

		private static IntPtr ChainedWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
		{
			try
			{
				if (TryBlockShellStartMenuMessage(msg, wParam, out IntPtr blockResult))
					return blockResult;

				if (TryBlockHotkeyMessage(msg, out blockResult))
					return blockResult;

				if (TryBlockStartMenuShortcut(msg, wParam, out blockResult))
					return blockResult;

				if (TryBlockWindowsKeyMessage(msg, wParam, out blockResult))
					return blockResult;

				if (TryBlockWinChordMessage(msg, wParam, out blockResult))
					return blockResult;

				if (msg == WM_INPUT && TrySwallowWinRawInput(lParam, out blockResult))
					return blockResult;

				switch (msg)
				{
					case WM_KEYDOWN:
					case WM_SYSKEYDOWN:
						HandleKeyDown(wParam, lParam);
						break;
					case WM_INPUT:
						HandleRawInput(lParam);
						break;
					case WM_IME_NOTIFY:
						HandleImeNotify(wParam);
						break;
				}
			}
			catch
			{
			}

			StartMenuDiag.LogForwarded(msg, wParam);
			return CallWindowProc(_originalWndProc, hWnd, msg, wParam, lParam);
		}

		private static bool TryBlockHotkeyMessage(uint msg, out IntPtr result)
		{
			result = IntPtr.Zero;
			if (msg != WM_HOTKEY)
				return false;

			if (!WindowsKeySuppressor.ShouldBlockStartMenuShortcuts())
				return false;

			StartMenuDiag.LogInstall("WndProc WM_HOTKEY swallowed");
			return true;
		}

		private static bool TryBlockShellStartMenuMessage(uint msg, IntPtr wParam, out IntPtr result)
		{
			result = IntPtr.Zero;

			if (msg != WM_SYSCOMMAND)
				return false;

			int cmd = wParam.ToInt32() & 0xFFF0;
			bool block = WindowsKeySuppressor.ShouldBlockShellStartMenuCommand(cmd);
			StartMenuDiag.LogSyscmd(cmd, block, block ? "swallowed" : "suppression-off");

			if (!block)
				return false;

			AkDiag.Log("WndProc WM_SYSCOMMAND blocked cmd=0x" + cmd.ToString("X4"));
			return true;
		}

		private static bool TryBlockStartMenuShortcut(uint msg, IntPtr wParam, out IntPtr result)
		{
			result = IntPtr.Zero;

			bool isDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
			if (!isDown)
				return false;

			uint vk = (uint)(wParam.ToInt64() & 0xFF);
			bool ctrl = WindowsKeySuppressor.IsControlDownForDiag();
			if (WindowsKeySuppressor.TryApplyCtrlEscCaptureOrBlock(vk, isDown, out bool swallowCtrlEsc))
			{
				if (vk == VK_ESCAPE && ctrl)
				{
					string reason = swallowCtrlEsc ? "ctrl+esc" : "ctrl+esc reason=" + WindowsKeySuppressor.DescribeWhyNotBlocking();
					StartMenuDiag.LogWndProc(msg, vk, swallowCtrlEsc, reason);
				}

				if (swallowCtrlEsc)
				{
					AkDiag.Log("WndProc Ctrl+Esc blocked");
					return true;
				}
			}

			return false;
		}

		private static bool TryBlockWinChordMessage(uint msg, IntPtr wParam, out IntPtr result)
		{
			result = IntPtr.Zero;
			bool isDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
			bool isUp = msg == WM_KEYUP || msg == WM_SYSKEYUP;
			if (!isDown && !isUp)
				return false;

			uint vk = (uint)(wParam.ToInt64() & 0xFF);
			if (WindowsKeySuppressor.TryHandleWinChordPassthrough(vk, isDown, "wndproc", out bool swallowed) && swallowed)
				return true;

			if (WindowsKeySuppressor.TryFinishMonitorMoveArrowUp(vk, isUp, out bool swallowUp) && swallowUp)
				return true;

			return false;
		}

		private static bool TryBlockWindowsKeyMessage(uint msg, IntPtr wParam, out IntPtr result)
		{
			result = IntPtr.Zero;

			if (msg == WM_SYSCOMMAND)
			{
				int cmd = wParam.ToInt32() & 0xFFF0;
				if (WindowsKeySuppressor.ShouldBlockShellStartMenuCommand(cmd))
				{
					result = IntPtr.Zero;
					return true;
				}

				return false;
			}

			bool isDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
			bool isUp = msg == WM_KEYUP || msg == WM_SYSKEYUP;
			if (!isDown && !isUp)
				return false;

			uint vk = (uint)(wParam.ToInt64() & 0xFF);
			if (isUp && vk == VK_ESCAPE)
				WindowsKeySuppressor.ClearShellBlockedEscapeDown();

			if (!WindowsKeySuppressor.TryApplyWindowsKeyBlock(vk, isDown, isUp, out bool blocked))
				return false;

			if (!blocked)
				return false;

			result = IntPtr.Zero;
			return true;
		}

		private static bool TrySwallowWinRawInput(IntPtr lParam, out IntPtr result)
		{
			result = IntPtr.Zero;
			if (!TryReadRawKeyboard(lParam, out RawInputData raw))
				return false;

			uint vk = raw.keyboard.VKey;
			if (vk != VK_LWIN && vk != VK_RWIN)
				return false;

			bool isUp = (raw.keyboard.Flags & RI_KEY_BREAK) != 0;
			bool isDown = !isUp;
			if (!WindowsKeySuppressor.TryApplyWindowsKeyBlock(vk, isDown, isUp, out bool blocked))
				return false;

			StartMenuDiag.LogRaw(vk, isDown, blocked, blocked ? "win" : "reason=" + WindowsKeySuppressor.DescribeWhyNotBlocking());

			if (!blocked)
				return false;

			result = IntPtr.Zero;
			return true;
		}

		private static bool TryReadRawKeyboard(IntPtr lParam, out RawInputData raw)
		{
			raw = default;
			uint size = 0;
			GetRawInputData(lParam, RID_INPUT, IntPtr.Zero, ref size, (uint)Marshal.SizeOf<RAWINPUTHEADER>());
			if (size == 0)
				return false;

			IntPtr buffer = Marshal.AllocHGlobal((int)size);
			try
			{
				if (GetRawInputData(lParam, RID_INPUT, buffer, ref size, (uint)Marshal.SizeOf<RAWINPUTHEADER>()) == uint.MaxValue)
					return false;

				raw = Marshal.PtrToStructure<RawInputData>(buffer);
				return raw.header.dwType == RIM_TYPEKEYBOARD;
			}
			finally
			{
				Marshal.FreeHGlobal(buffer);
			}
		}

		private static void HandleKeyDown(IntPtr wParam, IntPtr lParam)
		{
			uint vk = (uint)(wParam.ToInt64() & 0xFF);
			uint scan = (uint)(((ulong)lParam.ToInt64() >> 16) & 0xFF);

			_lastKeydownScan = (ushort)scan;
			_lastKeydownScanMs = Environment.TickCount64;

			if (WindowsKeySuppressor.IsSlotCapturePassthrough)
				AkDiag.Log($"WndProc keydown vk=0x{vk:X2} scan=0x{scan:X2}");

			QueueScanEdge((ushort)scan, (ushort)vk, "WndProc");
		}

		private static void HandleRawInput(IntPtr lParam)
		{
			if (!TryReadRawKeyboard(lParam, out RawInputData raw))
				return;

			if ((raw.keyboard.Flags & RI_KEY_BREAK) != 0)
				return;

			QueueScanEdge(raw.keyboard.MakeCode, raw.keyboard.VKey, "WM_INPUT");
		}

		private static void HandleImeNotify(IntPtr wParam)
		{
			int notify = wParam.ToInt32();
			if (notify != IMN_SETCONVERSIONMODE)
				return;

			if (!KeyboardLayoutHelper.IsJapaneseKeyboardLayout() || IsShiftDown())
				return;

			long now = Environment.TickCount64;
			if (now - _lastImeCapsEdgeMs < 100)
				return;

			if (now - _lastKeydownScanMs < 60 && _lastKeydownScan != 0 && _lastKeydownScan != SC_CAPITAL)
				return;

			_lastImeCapsEdgeMs = now;
			QueueToken("CapsLock", "WM_IME_NOTIFY");
		}

		private static void QueueScanEdge(ushort makeCode, ushort vKey, string source)
		{
			string token = makeCode switch {
				SC_CAPITAL => "CapsLock",
				SC_NUMLOCK => "NumLock",
				SC_SCROLL => BindingRestrictions.ScrollLockToken,
				_ => vKey == 0x14 ? "CapsLock" : null,
			};

			if (token == null)
				return;

			// Shift+Caps on JP keyboards toggles OS Caps Lock (vk=0x14). Do not treat that LED-on
			// state as a held combo key on later frames.
			if (string.Equals(token, "CapsLock", StringComparison.OrdinalIgnoreCase) && vKey == VK_CAPITAL)
				WindowsKeySuppressor.ReleaseToggleLockPhysicalState("CapsLock");

			QueueToken(token, source + " make=0x" + makeCode.ToString("X2") + " vk=0x" + vKey.ToString("X2"));
		}

		private static void QueueToken(string token, string source)
		{
			if (!PendingEdges.Add(token))
				return;

			AkDiag.Log("GameWindowInputHook edge " + source + " token=" + token);
		}

		private static bool IsShiftDown()
			=> (GetAsyncKeyState((int)VK_SHIFT) & 0x8000) != 0;

	}
}
