using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using AdvancedKeybinds.Binding;
using AdvancedKeybinds.Core;
using AdvancedKeybinds.Diagnostics;
using AdvancedKeybinds.UI;
using Terraria;

namespace AdvancedKeybinds.Patches
{
	/// <summary>
	/// Chains the game window WndProc. Keyboard acquisition uses only Microsoft keystroke
	/// messages (WM_KEYDOWN/UP, WM_SYSKEYDOWN/UP) parsed per Keyboard Input Overview.
	/// WM_INPUT / IME notify are not used as key-acquisition sources when
	/// <see cref="Win32KeyboardAcquisitionPolicy.KeystrokeMessagesOnly"/> is true.
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
		private const int WM_MOUSEWHEEL = 0x020A;
		private const int WM_MOUSEHWHEEL = 0x020E;
		private const uint RID_INPUT = 0x10000003;
		private const uint RIDEV_REMOVE = 0x00000001;
		private const uint RIDEV_INPUT = 0x00000001;
		private const ushort RIM_TYPEKEYBOARD = 1;
		private const ushort RI_KEY_BREAK = 0x0001;
		private const ushort SC_CAPITAL = 0x3A;
		private const ushort VK_CAPITAL = 0x14;
		private const ushort SC_NUMLOCK = 0x45;
		private const ushort VK_NUMLOCK = 0x90;
		private const ushort SC_SCROLL = 0x46;
		private const ushort SC_HIRAGANA = 0x70;
		private const ushort VK_KANA = 0x15;
		private const ushort VK_OEM_COPY = 0xF2;
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
				if (OperatingSystem.IsWindows())
					RestoreWndProc();
				_wndProcInstalled = false;
				_rawInputRegistered = false;
				_rawInputMode = "none";
				GameWindowHandle.InvalidateCache();
			}
		}

		public static void Poll()
		{
			if (!OperatingSystem.IsWindows())
				return;

			EnsureInstalledIfNeeded();
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

		/// <summary>Drops queued toggle edges without consuming them (capture begin / focus loss).</summary>
		public static void ClearPendingEdges()
			=> PendingEdges.Clear();

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
					return false;
				}

				_hookedHwnd = target;
				_originalWndProc = previous;
				_wndProcInstalled = true;
				TryRegisterRawInput(target);
				return true;
			}
			catch (Exception)
			{
				RestoreWndProc();
				return false;
			}
		}


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
			if (!OperatingSystem.IsWindows()) return;
			int cbSize = Marshal.SizeOf<RAWINPUTDEVICE>();
			RemoveKeyboardRawInput(cbSize);

			List<int> errors = new();
			bool registered = WinInputReleaseCandidate.WinRawKeyboardRegistration.Register(true, hwnd, (flags, target) =>
			{
				string mode = target == IntPtr.Zero ? "GLOBAL" : "FOREGROUND";

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
					return true;
				}

				errors.Add(err);
				return false;
			});
			if (registered) return;

			_rawInputRegistered = false;
			_rawInputMode = "FAILED:" + string.Join("/", errors);
		}

		/// <summary>Re-apply raw-input flags after Win-suppression config changes.</summary>
		internal static void RefreshRawInputForWinSuppressSetting()
		{
			if (!OperatingSystem.IsWindows())
				return;

			if (!_wndProcInstalled)
				return;

			IntPtr hwnd = _hookedHwnd;
			if (hwnd == IntPtr.Zero)
				hwnd = GameWindowHandle.TryGet(preferForeground: true);

			TryRegisterRawInput(hwnd);
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
				TryRegisterRawInputDevice(inputOnly, cbSize, out err);
			}
			else
			{
				_rawInputRegistered = true;
				_rawInputMode = "INPUT(temp-os-hotkeys)";
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
				if (msg == 0x001Cu && wParam == IntPtr.Zero)
				{
					WindowsKeySuppressor.RequestFocusLossInputReset();
					WindowsKeySuppressor.InvalidateWinMaskOwnership();
				}
				bool maskDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
				bool maskUp = msg == WM_KEYUP || msg == WM_SYSKEYUP;
				if ((maskDown || maskUp) && WindowsKeySuppressor.IsOwnMaskKeyMessage(
					(uint)(wParam.ToInt64() & 0xFF), maskDown, maskUp))
					goto ForwardOriginal;

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
						HandleKeyDown(msg, wParam, lParam);
						break;
					case WM_KEYUP:
					case WM_SYSKEYUP:
						HandleKeyUp(msg, wParam, lParam);
						break;
					case WM_MOUSEWHEEL:
						HandleMouseWheel(wParam);
						break;
					case WM_MOUSEHWHEEL:
						HandleMouseHWheel(wParam);
						break;
				}
			}
			catch
			{
			}

		ForwardOriginal:
			return CallWindowProc(_originalWndProc, hWnd, msg, wParam, lParam);
		}

		private static bool TryBlockHotkeyMessage(uint msg, out IntPtr result)
		{
			result = IntPtr.Zero;
			return false;
		}

		private static bool TryBlockShellStartMenuMessage(uint msg, IntPtr wParam, out IntPtr result)
		{
			result = IntPtr.Zero;

			if (msg != WM_SYSCOMMAND)
				return false;

			int cmd = wParam.ToInt32() & 0xFFF0;
			bool block = WindowsKeySuppressor.ShouldBlockShellStartMenuCommand(cmd);

			if (!block)
				return false;

			return true;
		}

		private static bool TryBlockStartMenuShortcut(uint msg, IntPtr wParam, out IntPtr result)
		{
			result = IntPtr.Zero;

			bool isDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
			if (!isDown)
				return false;

			uint vk = (uint)(wParam.ToInt64() & 0xFF);
			if (WindowsKeySuppressor.TryApplyCtrlEscCaptureOrBlock(vk, isDown, out bool swallowCtrlEsc))
			{

				if (swallowCtrlEsc)
				{
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
			if (WinInputReleaseCandidate.WinMaskNativeBatchAdapter.IsNativeEcho(
				vk, isDown, isUp, raw.keyboard.ExtraInformation)) return false;
			if (!WindowsKeySuppressor.TryApplyWindowsKeyBlock(vk, isDown, isUp, out bool blocked))
				return false;


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

		private static void HandleKeyDown(uint msg, IntPtr wParam, IntPtr lParam)
		{
			Win32KeystrokeMessage.Parsed k = Win32KeystrokeMessage.Parse(wParam, lParam);
			global::AdvancedKeybinds.Features.DoubleTap.DoubleTapNativeInput.NotifyWindow(
				k.Vk, k.Scan, k.Extended, k.WasKeyDown);
			string messageName = msg == WM_SYSKEYDOWN ? "WM_SYSKEYDOWN" : "WM_KEYDOWN";
			string source = messageName;

			_lastKeydownScan = k.Scan;
			_lastKeydownScanMs = Environment.TickCount64;

			if (WindowsKeySuppressor.IsSlotCapturePassthrough)
			{

			}

			ApplyMicrosoftKeystroke(k, isDown: true, source);
		}

		private static void HandleKeyUp(uint msg, IntPtr wParam, IntPtr lParam)
		{
			Win32KeystrokeMessage.Parsed k = Win32KeystrokeMessage.Parse(wParam, lParam);
			string messageName = msg == WM_SYSKEYUP ? "WM_SYSKEYUP" : "WM_KEYUP";
			string source = messageName;

			ApplyMicrosoftKeystroke(k, isDown: false, source);
		}

		/// <summary>Single acquisition entry: Microsoft keystroke messages only.</summary>
		private static void ApplyMicrosoftKeystroke(Win32KeystrokeMessage.Parsed k, bool isDown, string source)
		{
			if (WindowsKeySuppressor.IsHankakuZenkakuPhysicalKey(k.Vk, k.Scan))
			{
				WindowsKeySuppressor.ApplyHankakuZenkakuPhysical(k.Vk, k.Scan, isDown, source);
				return;
			}

			if (WindowsKeySuppressor.IsKanaPhysicalKey(k.Vk, k.Scan))
			{
				WindowsKeySuppressor.ApplyKanaPhysical(k.Vk, k.Scan, isDown, source);
				return;
			}

			WindowsKeySuppressor.ApplyJisIntlScan(k.Scan, isDown, source);
			WindowsKeySuppressor.ApplyPhysicalKeyEvent(k.Vk, k.Scan, k.Extended, isDown, source);
			if (isDown)
				QueueScanEdge(k.Scan, (ushort)k.Vk, source);
		}

		private static void HandleMouseWheel(IntPtr wParam)
		{
			short delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
			MouseInputHelper.OnWin32VerticalWheelDelta(delta);
		}

		private static void HandleMouseHWheel(IntPtr wParam)
		{
			short delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
			MouseInputHelper.OnWin32WheelDelta(delta);
		}

		private static void QueueScanEdge(ushort makeCode, ushort vKey, string source)
		{
			// Pause/Break share make-code 0x45 with NumLock — never emit NumLock for them or bare 0x45.
			if (WindowsKeySuppressor.IsPauseOrBreakVk(vKey))
			{
				return;
			}

			// Caps/Scroll may use scan; NumLock only when VK is NumLock (never scan 0x45 alone).
			string token;
			if (makeCode == SC_CAPITAL || vKey == VK_CAPITAL)
				token = "CapsLock";
			else if (vKey == VK_NUMLOCK)
				token = "NumLock";
			else if (makeCode == SC_SCROLL)
				token = BindingRestrictions.ScrollLockToken;
			else
				token = null;

			if (token == null && makeCode == SC_NUMLOCK)
			{
				return;
			}

			// JP kana is NLS (often no sticky GetAsyncKeyState) — make/break + edge latch.
			if (token == null
				&& (makeCode == SC_HIRAGANA || vKey == VK_OEM_COPY || vKey == VK_KANA))
			{
				WindowsKeySuppressor.ApplyKanaPhysical(
					vKey,
					makeCode,
					isDown: true,
					source + " make=0x" + makeCode.ToString("X2") + " vk=0x" + vKey.ToString("X2"));
				return;
			}

			if (token == null)
			{
				return;
			}

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

		}

	}
}
