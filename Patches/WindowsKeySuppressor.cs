using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using AdvancedKeybinds.Binding;
using AdvancedKeybinds.Core;
using AdvancedKeybinds.Diagnostics;
using AdvancedKeybinds.Logic;
using Terraria;
using Terraria.ModLoader;

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
		private const int LLKHF_INJECTED = 0x10;

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
		private const uint VK_CANCEL = 0x03; // Ctrl+Pause / Break
		private const uint VK_PAUSE = 0x13;
		private const uint VK_CAPITAL = 0x14;
		private const uint VK_KANA = 0x15;
		private const uint VK_NUMLOCK = 0x90;
		private const uint VK_SCROLL = 0x91;
		/// <summary>kbd106 「カタカナ／ひらがな」 — also <c>VK_DBE_HIRAGANA</c> (NLS toggle; not a sticky down).</summary>
		private const uint VK_OEM_COPY = 0xF2;
		/// <summary>kbd106 「半角／全角」 KEYDOWN often reports <c>VK_OEM_AUTO</c> (break may use <c>VK_OEM_ENLW</c>).</summary>
		private const uint VK_OEM_AUTO = 0xF3;
		/// <summary>kbd106 「半角／全角」 KEYUP companion of <see cref="VK_OEM_AUTO"/>.</summary>
		private const uint VK_OEM_ENLW = 0xF4;
		/// <summary>Alternate VK for 「半角／全角・漢字」 on some IME configurations.</summary>
		private const uint VK_KANJI = 0x19;
		private const uint SC_HIRAGANA = 0x70;
		/// <summary>kbd106 「半角／全角」 make-code (same slot as US OemTilde / grave).</summary>
		private const uint SC_HANKAKU_ZENKAKU = 0x29;
		/// <summary>kbd106 「ろ」 left of R-Shift → <c>VK_OEM_102</c> / <see cref="Microsoft.Xna.Framework.Input.Keys.OemBackslash"/>.</summary>
		private const uint SC_INTL_RO = 0x73;
		/// <summary>kbd106 「¥」 left of Backspace → <c>VK_OEM_5</c> / <see cref="Microsoft.Xna.Framework.Input.Keys.OemPipe"/>.</summary>
		private const uint SC_INTL_YEN = 0x7D;
		/// <summary>Number-row key left of ^/¥ on JIS (legend <c>-</c>). Same make-code as US OemMinus.</summary>
		private const uint SC_JIS_OEM_MINUS = 0x0C;
		/// <summary>Number-row key left of ¥ on JIS (legend <c>^</c>). Same make-code as US OemPlus.</summary>
		private const uint SC_JIS_OEM_EQUAL = 0x0D;
		/// <summary>
		/// ISO 102 key left of L-Shift (also ABNT2 extra). Same VK_OEM_102 as JIS ろ, but different
		/// make-code — registration uses <c>SC:0x56:Normal</c> so it does not collide with ろ.
		/// </summary>
		private const uint SC_ISO_102 = 0x56;
		/// <summary>kbd106 「変換」 → <c>VK_CONVERT</c>.</summary>
		private const uint SC_CONVERT = 0x79;
		/// <summary>kbd106 「無変換」 → <c>VK_NONCONVERT</c>.</summary>
		private const uint SC_NONCONVERT = 0x7B;
		private const uint SC_CAPITAL = 0x3A;
		private const uint SC_NUMLOCK = 0x45;
		private const uint SC_SCROLL = 0x46;
		/// <summary>
		/// After 「半角／全角」, Imm conversion → CapsLock false edges and FNA OemTilde (grave scancode)
		/// masquerades can linger briefly. Keep longer than a single Imm notify pulse.
		/// </summary>
		private const int HankakuZenkakuCapsSuppressMs = 400;

		private const uint INPUT_KEYBOARD = 1;
		private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
		private const uint KEYEVENTF_KEYUP = 0x0002;

		// Marker stored in dwExtraInfo on the events we inject, so the hook can recognise and ignore them.
		private static readonly IntPtr InjectedMarker = new IntPtr(0x11AC0DE5);

		/// <summary>Readonly compare — does not expose or duplicate the marker value.</summary>
		internal static bool IsOwnInjectedMarker(IntPtr extraInfo)
			=> extraInfo == InjectedMarker;

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

		/// <summary>
		/// Physical Down for slot-capture held snapshots. Prefer hook make/break when installed
		/// (swallow can clear GetAsyncKeyState early). Never uses FNA/LED toggle bits.
		/// </summary>
		public static bool IsToggleLockPhysicallyDownForCapture(string token)
		{
			if (string.Equals(token, "CapsLock", StringComparison.OrdinalIgnoreCase))
			{
				if (IsCapsLockPhysicallyDown)
					return true;
				return !IsInstalled && IsPhysicalKeyDown(VK_CAPITAL);
			}

			if (string.Equals(token, "NumLock", StringComparison.OrdinalIgnoreCase))
			{
				if (IsNumLockPhysicallyDown)
					return true;
				return !IsInstalled && IsPhysicalKeyDown(VK_NUMLOCK);
			}

			if (string.Equals(token, BindingRestrictions.ScrollLockToken, StringComparison.OrdinalIgnoreCase))
			{
				if (IsScrollLockPhysicallyDown)
					return true;
				return !IsInstalled && IsPhysicalKeyDown(VK_SCROLL);
			}

			return false;
		}

		/// <summary>OS toggle / LED latch (GetKeyState low bit). Never treat as physical Down.</summary>
		public static bool IsToggleLockLedOn(string token)
		{
			if (!OperatingSystem.IsWindows())
				return false;

			uint vk = token switch {
				_ when string.Equals(token, "CapsLock", StringComparison.OrdinalIgnoreCase) => VK_CAPITAL,
				_ when string.Equals(token, "NumLock", StringComparison.OrdinalIgnoreCase) => VK_NUMLOCK,
				_ when string.Equals(token, BindingRestrictions.ScrollLockToken, StringComparison.OrdinalIgnoreCase) => VK_SCROLL,
				_ => 0u
			};
			if (vk == 0)
				return false;
			return (GetKeyState((int)vk) & 0x0001) != 0;
		}

		/// <summary>
		/// Pause/Break via Microsoft keystroke make/break (VK_PAUSE / VK_CANCEL).
		/// Never inferred from scan 0x45 alone (shared with NumLock).
		/// </summary>
		public static bool IsPausePhysicallyDown
			=> OperatingSystem.IsWindows()
				&& (IsFallbackVkHeld(VK_PAUSE) || IsFallbackVkHeld(VK_CANCEL));

		/// <summary>
		/// Win32 virtual-key high bit. Used when FNA/SDL <c>KeyboardState</c> drops layout-dependent OEM keys
		/// (JIS <c>@</c>/<c>^</c>/<c>¥</c> → SDLK_AT/CARET/¥ not in FNA keycode map).
		/// </summary>
		public static bool IsVirtualKeyPhysicallyDown(uint vkCode)
			=> OperatingSystem.IsWindows() && IsPhysicalKeyDown(vkCode);

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

		// JP kana key is NLS (scan 0x70 / VK_OEM_COPY): GetAsyncKeyState rarely stays down — edge latch.
		private static volatile string _pendingKanaCaptureToken;
		private static long _pendingKanaCaptureTick;
		private static long _lastKanaEdgeMs;
		private static readonly HashSet<string> _gameplayKanaEdges = new(StringComparer.OrdinalIgnoreCase);
		/// <summary>
		/// Duplicate-source guard. 0 = same as normal rising-edge keys (physical-down / wasDown already
		/// suppress autorepeat). Was 100ms and made Kana feel sluggish vs ordinary keys.
		/// </summary>
		private const int KanaEdgeCoalesceMs = 0;
		/// <summary>WndProc KEYDOWN for VK-aligned Keys that FNA KeyboardState misses (e.g. Oem8 / VK_OEM_8).</summary>
		private static volatile string _pendingWin32KeysCaptureToken;
		private static long _pendingWin32KeysCaptureTick;
		private const int Win32KeysCaptureLatchMaxAgeMs = 500;

		private readonly struct GenericScanId : IEquatable<GenericScanId>
		{
			public readonly ushort Scan;
			public readonly bool Extended;

			public GenericScanId(ushort scan, bool extended)
			{
				Scan = (ushort)(scan & 0xFF);
				Extended = extended;
			}

			public bool Equals(GenericScanId other)
				=> Scan == other.Scan && Extended == other.Extended;

			public override bool Equals(object obj)
				=> obj is GenericScanId other && Equals(other);

			public override int GetHashCode()
				=> HashCode.Combine(Scan, Extended);
		}

		private static readonly Dictionary<GenericScanId, bool> _genericScanHeld = new();
		private static readonly Dictionary<GenericScanId, long> _genericScanDownSinceMs = new();
		/// <summary>
		/// Measured scan recorded per VK on KEYDOWN (non-special). Used so orphan Keys/VK
		/// supplements do not reintroduce layout-table aliases (e.g. OemOpenBrackets→0x1A vs JIS SC 0x1B).
		/// </summary>
		private static readonly Dictionary<uint, GenericScanId> _vkMeasuredScan = new();
		/// <summary>Last measured WM_KEY* event per VK (same-event alias suppression; not layout tables).</summary>
		private static readonly Dictionary<uint, MeasuredPhysicalKeyEvent> _vkLastMeasuredEvent = new();
		private static long _physicalKeyEventSequence;
		/// <summary>EventSequence → one canonical registration candidate (world-portable).</summary>
		private static readonly EventSequenceUnifier _eventSequenceUnifier = new();
		/// <summary>
		/// VKs whose capture identity is <c>VK:</c> (FNA Keys missing). Kept in sync with make/break
		/// so gameplay <see cref="SupplementFallbackVkTokens"/> matches registration tokens.
		/// </summary>
		private static readonly HashSet<uint> _fallbackVkHeld = new();
		private static volatile string _pendingPhysicalCaptureToken;
		private static long _pendingPhysicalCaptureTick;
		private static readonly HashSet<string> _gameplayPhysicalEdges = new(StringComparer.OrdinalIgnoreCase);
		private const int PhysicalCaptureLatchMaxAgeMs = 500;
		/// <summary>Make/break held bit for 「カタカナ／ひらがな」(GetAsyncKeyState is unreliable for NLS).</summary>
		private static bool _kanaPhysicallyDown;
		private static long _kanaPhysicalDownSinceMs;
		private const int KanaPhysicalDownMaxMs = 1500;
		/// <summary>Latched Imm conversion toggle for capture ghost recovery (consumed by SlotCapture).</summary>
		private static bool _imeConversionToggleForRecovery;
		/// <summary>Capture latch TTL — long enough for click-release then NLS press at low FPS.</summary>
		private const int NlsCaptureLatchMaxAgeMs = 2000;

		/// <summary>
		/// Last 「半角／全角」 KEYDOWN seen on any hook path. Used so IME→Caps mapping does not fire
		/// when the OS/IME toggles conversion for hankaku/zenkaku (scan often never reaches WM_KEYDOWN).
		/// </summary>
		private static long _lastHankakuZenkakuEdgeMs;

		/// <summary>True while 「半角／全角」 is physically held (make…break tracked on hook paths).</summary>
		private static bool _hankakuZenkakuPhysicallyDown;
		private static long _hankakuPhysicalDownSinceMs;
		/// <summary>
		/// NLS KEYUP is often missing after IME swallows break; clear stuck "held" so Caps/Oem filters
		/// do not stay armed forever.
		/// </summary>
		private const int HankakuPhysicalDownMaxMs = 1500;

		// NLS hankaku/zenkaku: GetAsyncKeyState / Snapshot rarely see a sticky down — edge latch like kana.
		private static volatile string _pendingHankakuCaptureToken;
		private static long _pendingHankakuCaptureTick;
		private static long _lastHankakuBindEdgeMs;
		private static readonly HashSet<string> _gameplayHankakuEdges = new(StringComparer.OrdinalIgnoreCase);
		/// <summary>Same policy as <see cref="KanaEdgeCoalesceMs"/> (was 100ms).</summary>
		private const int HankakuBindEdgeCoalesceMs = 0;

		// JIS/ISO scans: under US layout Windows often never raises the JP OEM/IME VKs, so VK polling
		// alone misses ¥ / ろ (and 変換／無変換). Track held state + KEYDOWN latch from make/break.
		private static bool _jisIntlYenDown;
		private static bool _jisIntlRoDown;
		private static bool _jisIntlIso102Down;
		private static bool _jisOemMinusDown;
		private static bool _jisOemEqualDown;
		private static bool _jisConvertDown;
		private static bool _jisNonConvertDown;
		private static volatile string _pendingJisIntlCaptureToken;
		private static long _pendingJisIntlCaptureTick;
		private static long _lastJisIntlScanEdgeMs;
		private static readonly HashSet<string> _gameplayJisIntlEdges = new(StringComparer.OrdinalIgnoreCase);
		/// <summary>Same policy as <see cref="KanaEdgeCoalesceMs"/> (was 100ms). Rising edge already gated by wasDown.</summary>
		private const int JisIntlScanEdgeCoalesceMs = 0;

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

		[DllImport("user32.dll")]
		private static extern short GetKeyState(int nVirtKey);

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
#if AK_DIAGNOSTICS
				StartMenuDiag.LogInstall(
					"hook wrong thread hookTid=0x" + _hookThreadId.ToString("X")
					+ " currentTid=0x" + threadId.ToString("X") + " reinstalling");
#endif
				UninstallHookCore();
			}

			if (_hookId == IntPtr.Zero && _attempted && _hookThreadId != 0 && _hookThreadId != threadId)
			{
#if AK_DIAGNOSTICS
				StartMenuDiag.LogInstall("hook prior attempt on worker thread; retrying on main");
#endif
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
					EnsureThreadKeyboardHook(threadId);
#if AK_DIAGNOSTICS
					string threadNote = _threadHookId != IntPtr.Zero ? " threadHook=ok" : " threadHook=fail:" + Marshal.GetLastWin32Error();
					StartMenuDiag.LogInstall("keyboard hook installed tid=0x" + threadId.ToString("X") + threadNote);
#endif
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
			if (!OperatingSystem.IsWindows())
				return;

			if (_threadHookId != IntPtr.Zero)
				return;

			_threadProc = ThreadKeyboardHookCallback;
			_threadHookId = SetWindowsHookEx(WH_KEYBOARD, _threadProc, GetModuleHandle(null), threadId);
		}

		private static void UninstallHookCore()
		{
			// Nothing was ever hooked off Windows; never touch UnhookWindowsHookEx there.
			if (!OperatingSystem.IsWindows())
			{
				_threadHookId = IntPtr.Zero;
				_hookId = IntPtr.Zero;
				_proc = null;
				_threadProc = null;
				_attempted = false;
				_hookThreadId = 0;
				return;
			}

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
			_pendingKanaCaptureToken = null;
			_pendingKanaCaptureTick = 0;
			_lastKanaEdgeMs = 0;
			_gameplayKanaEdges.Clear();
			_kanaPhysicallyDown = false;
			_kanaPhysicalDownSinceMs = 0;
			_imeConversionToggleForRecovery = false;
			_lastHankakuZenkakuEdgeMs = 0;
			_hankakuZenkakuPhysicallyDown = false;
			_hankakuPhysicalDownSinceMs = 0;
			ClearHankakuBindEdges();
			ClearJisIntlScanState();
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

		private static bool IsPhysicalKeyDown(uint vkCode)
		{
			// user32 does not exist off Windows (Linux / other non-Windows runtimes).
			if (!OperatingSystem.IsWindows())
				return false;

			return (GetAsyncKeyState((int)vkCode) & 0x8000) != 0;
		}

		/// <summary>
		/// Win32 GetAsyncKeyState for Escape. FNA Keyboard.GetState can briefly report UP while
		/// the key is still held; SharedUi latch must not clear on that flicker.
		/// </summary>
		public static bool IsEscapePhysicallyDownAsync()
			=> IsPhysicalKeyDown(VK_ESCAPE);

		public static void SetModalWindowsKeySuppress(bool enabled)
		{
			_modalWindowsKeySuppress = enabled;
			if (!enabled)
				SyncWindowsKeyStateFromOs();
		}

		/// <summary>Re-sync hook-tracked Win state from the OS (avoids stale LeftWindows in slot capture).</summary>
		public static void SyncWindowsKeyStateFromOs()
		{
			if (!OperatingSystem.IsWindows())
				return;

			IsLeftWindowsDown = IsPhysicalKeyDown(VK_LWIN);
			IsRightWindowsDown = IsPhysicalKeyDown(VK_RWIN);
		}

		private static bool ShouldSuppressShellInput()
		{
			if (!IsWinKeySuppressionEnabled())
				return false;

			// Non-active: never swallow Win — OS and other apps keep full Win access.
			if (!IsGameForegroundCore())
				return false;

			// Active: suppress Win for gameplay, combo panel, and slot capture.
			return true;
		}

		/// <summary>
		/// Win / Ctrl+Esc shell blocking is Windows-only. A saved SuppressWindowsKey=true on
		/// Linux/macOS must not enable suppression (value is preserved for when the user returns to Windows).
		/// </summary>
		internal static bool IsWinKeySuppressionEnabled()
		{
			AdvancedKeybindsConfig config = ModContent.GetInstance<AdvancedKeybindsConfig>();
			bool saved = config != null && config.SuppressWindowsKey;
			return WindowsKeySuppressConfigPolicy.ShouldExecuteWindowsKeySuppression(
				OperatingSystem.IsWindows(),
				saved);
		}

		/// <summary>Called when the user turns Win suppression off in Mod Config.</summary>
		internal static void OnSuppressionDisabled()
		{
			ReleaseWinSuppressionState();
			_modalWindowsKeySuppress = false;
			GameWindowInputHook.RefreshRawInputForWinSuppressSetting();
		}

		private static bool IsAnyWinPhysicallyDown()
			=> OperatingSystem.IsWindows()
				&& (IsPhysicalKeyDown(VK_LWIN) || IsPhysicalKeyDown(VK_RWIN));

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
			=> IsWinKeySuppressionEnabled() && IsGameForegroundCore();

		private static bool IsWinHeldForChord()
			=> AnyWindowsDown || IsAnyWinPhysicallyDown() || _winPressActiveWhileSuppressing;

		internal static bool IsShiftDownForDiag()
			=> OperatingSystem.IsWindows() && (GetAsyncKeyState((int)VK_SHIFT) & 0x8000) != 0;

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
#if AK_DIAGNOSTICS
				WinChordDiag.Log(path, vk, "native", "reason=inactive");
#endif
				return false;
			}

			if (!IsWinHeldForChord())
			{
#if AK_DIAGNOSTICS
				WinChordDiag.Log(path, vk, "skip", "reason=no-win-held");
#endif
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
#if AK_DIAGNOSTICS
				WinChordDiag.Log(path, VK_S, "snip-cooldown", "ms=" + (now - _lastSnipRequestTick));
#endif
				return false;
			}

			_lastSnipRequestTick = now;
			_snipRequested = true;
			swallowed = true;
#if AK_DIAGNOSTICS
			WinChordDiag.Log(path, VK_S, "snip-request", "queued");
#endif
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
#if AK_DIAGNOSTICS
			WinChordDiag.Log(path, vk, "move-request", "win=0x" + chordWinVk.ToString("X"));
#endif
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
#if AK_DIAGNOSTICS
			WinChordDiag.Log(path, vk, "vd-request", "win=0x" + chordWinVk.ToString("X"));
#endif
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
			=> OperatingSystem.IsWindows() && (GetAsyncKeyState((int)VK_CONTROL) & 0x8000) != 0;

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
			=> OperatingSystem.IsWindows() ? GetForegroundWindow().ToInt64() : 0L;

		/// <summary>Why suppression is off (for StartMenuDiag).</summary>
		internal static string DescribeWhyNotBlocking()
		{
			if (!IsWinKeySuppressionEnabled())
				return "config-disabled";

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
			return "enabled=" + IsWinKeySuppressionEnabled()
				+ " fg=" + IsGameForeground()
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

#if AK_DIAGNOSTICS
				StartMenuDiag.LogWndProc((uint)(isUp ? WM_KEYUP : WM_KEYDOWN), vk, blocked: false, "win reason=" + DescribeWhyNotBlocking());
#endif
				return true;
			}

			blocked = true;
			ApplyWindowsKeyState(vk, isDown, isUp);

			if (IsSlotCapturePassthrough && isDown)
				QueueWinKeyCapture(vk);
			else if (isDown && IsGameForeground())
				QueueGameplayWinEdge(vk);

#if AK_DIAGNOSTICS
			StartMenuDiag.LogWndProc((uint)(isUp ? WM_KEYUP : WM_KEYDOWN), vk, blocked: true, "win");
#endif

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
			if (!OperatingSystem.IsWindows())
				return;

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
#if AK_DIAGNOSTICS
					WinChordDiag.Log("poll", 0, "released", "reason=inactive");
#endif
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

		/// <summary>
		/// kbd106 「カタカナ／ひらがな」 (scan 0x70 / <c>VK_OEM_COPY</c> / <c>VK_KANA</c>).
		/// NLS toggle — held-state polling via GetAsyncKeyState is unreliable; use KEYDOWN edges.
		/// CapsLock (scan 0x3A) on JP can emit <c>VK_OEM_COPY</c> (0xF2) — never treat that as kana.
		/// </summary>
		internal static bool IsKanaPhysicalKey(uint vkCode, uint scanCode)
		{
			uint make = scanCode & 0xFF;
			// Caps position: OEM_COPY pulse must not queue a Kana capture edge.
			if (make == SC_CAPITAL)
				return false;
			return make == SC_HIRAGANA
				|| vkCode == VK_OEM_COPY
				|| vkCode == VK_KANA;
		}

		/// <summary>
		/// Tracks kana make+break for capture held / ghost recovery. KEYDOWN also queues the bind latch.
		/// </summary>
		public static void ApplyKanaPhysical(uint vkCode, uint scanCode, bool isDown, string source)
		{
			if (!Win32KeyboardAcquisitionPolicy.AllowAcquisitionFrom(source))
				return;

			if (!IsKanaPhysicalKey(vkCode, scanCode))
				return;

			if (!isDown)
			{
				if (_kanaPhysicallyDown)
				{
					_kanaPhysicallyDown = false;
					_kanaPhysicalDownSinceMs = 0;
					if (AkDiag.Enabled) AkDiag.Log("Kana KEYUP src=" + source);
				}

				return;
			}

			bool rising = !_kanaPhysicallyDown;
			_kanaPhysicallyDown = true;
			if (rising)
				_kanaPhysicalDownSinceMs = Environment.TickCount64;
			if (rising && IsGameForegroundCore())
				QueueKanaEdge(source);
		}

		/// <summary>
		/// Physical held bit from make/break (NLS). Prefer this over GetAsyncKeyState for capture.
		/// Clears itself when KEYUP was missed past <see cref="KanaPhysicalDownMaxMs"/>.
		/// </summary>
		public static bool IsKanaPhysicallyDown
		{
			get
			{
				if (!_kanaPhysicallyDown)
					return false;

				if (_kanaPhysicalDownSinceMs != 0
					&& Environment.TickCount64 - _kanaPhysicalDownSinceMs > KanaPhysicalDownMaxMs)
				{
					_kanaPhysicallyDown = false;
					_kanaPhysicalDownSinceMs = 0;
					if (AkDiag.Enabled) AkDiag.Log("Kana physical-down timeout cleared (missed KEYUP?)");
					return false;
				}

				return true;
			}
		}

		/// <summary>
		/// Imm conversion-mode changed (typically after 「全角/半角」). Consumed once by SlotCapture recovery.
		/// </summary>
		public static void NoteImeConversionToggleForCaptureRecovery(string source)
		{
			_imeConversionToggleForRecovery = true;
			if (AkDiag.Enabled) AkDiag.Log("IME conversion toggle → capture recovery latch src=" + source);
		}

		public static bool TryConsumeImeConversionToggleForCaptureRecovery()
		{
			if (!_imeConversionToggleForRecovery)
				return false;

			_imeConversionToggleForRecovery = false;
			return true;
		}

		/// <summary>AKDIAG: NLS / JIS latch + ghost held snapshot for SlotCapture.</summary>
		public static string FormatNlsCaptureDiag()
		{
			return "kanaPhys=" + IsKanaPhysicallyDown
				+ " kanaLatch=" + (_pendingKanaCaptureToken != null)
				+ " hankakuPhys=" + IsHankakuZenkakuPhysicallyDown
				+ " hankakuLatch=" + (_pendingHankakuCaptureToken != null)
				+ " hankakuRecent=" + WasRecentHankakuZenkaku()
				+ " jisLatch=" + (_pendingJisIntlCaptureToken != null)
				+ " jisYen=" + _jisIntlYenDown
				+ " jisRo=" + _jisIntlRoDown
				+ " jisConv=" + _jisConvertDown
				+ " jisNonConv=" + _jisNonConvertDown
				+ " imeConvRecov=" + _imeConversionToggleForRecovery;
		}

		/// <summary>
		/// kbd106 「半角／全角」 (<c>VK_OEM_AUTO</c> / <c>VK_OEM_ENLW</c>, or <c>VK_KANJI</c> on JP only,
		/// or scan 0x29 on JP physical / JP layout). Always pass through to OS/IME (never swallow).
		/// Bind via <see cref="BindingRestrictions.HankakuZenkakuToken"/> edge latch — not as OemTilde.
		/// <c>VK_KANJI</c> (0x19) is Hanja on Korean layouts — must NOT be treated as hankaku there.
		/// Scan 0x29 alone is NOT enough: on US hardware that slot is OemTilde / grave and stays OemTilde.
		/// </summary>
		internal static bool IsHankakuZenkakuPhysicalKey(uint vkCode, uint scanCode)
		{
			if (vkCode == VK_OEM_AUTO || vkCode == VK_OEM_ENLW)
				return true;

			// VK_KANJI is 「漢字」/半角全角 only on Japanese IME boards; Korean uses the same VK for Hanja.
			if (vkCode == VK_KANJI
				&& (KeyboardLayoutHelper.IsJapanesePhysicalKeyboard()
					|| KeyboardLayoutHelper.IsJapaneseKeyboardLayout()))
				return true;

			if ((scanCode & 0xFF) != SC_HANKAKU_ZENKAKU)
				return false;

			// Same PC make-code as US grave/tilde — only treat as hankaku on JIS HW or JP layout.
			return KeyboardLayoutHelper.IsJapanesePhysicalKeyboard()
				|| KeyboardLayoutHelper.IsJapaneseKeyboardLayout();
		}

		/// <summary>
		/// Tracks hankaku/zenkaku make+break. KEYDOWN arms IME→Caps suppress, OemTilde masquerade
		/// filtering, and the bindable <see cref="BindingRestrictions.HankakuZenkakuToken"/> edge.
		/// </summary>
		public static void ApplyHankakuZenkakuPhysical(uint vkCode, uint scanCode, bool isDown, string source)
		{
			if (!Win32KeyboardAcquisitionPolicy.AllowAcquisitionFrom(source))
				return;

			if (!IsHankakuZenkakuPhysicalKey(vkCode, scanCode))
				return;

			if (!isDown)
			{
				_hankakuZenkakuPhysicallyDown = false;
				_hankakuPhysicalDownSinceMs = 0;
				if (AkDiag.Enabled) AkDiag.Log("Hankaku/Zenkaku KEYUP src=" + source);
				return;
			}

			// Rising edge only — same as Kana / JisIntl. Autorepeat KEYDOWN must not re-queue gameplay edges.
			bool rising = !_hankakuZenkakuPhysicallyDown;
			_hankakuZenkakuPhysicallyDown = true;
			if (rising)
				_hankakuPhysicalDownSinceMs = Environment.TickCount64;
			if (rising && IsGameForegroundCore())
				NoteHankakuZenkakuEdge(source);
		}

		/// <summary>
		/// Records a hankaku/zenkaku KEYDOWN for Caps suppress + bind latch.
		/// Key always reaches OS/IME (hooks CallNext); we only latch our token.
		/// </summary>
		public static void NoteHankakuZenkakuEdge(string source)
		{
			_lastHankakuZenkakuEdgeMs = Environment.TickCount64;
			QueueHankakuZenkakuEdge(source);
			if (AkDiag.Enabled)
			{
				AkDiag.Log(
				"Hankaku/Zenkaku KEYDOWN edge src=" + source
				+ " token=" + BindingRestrictions.HankakuZenkakuToken
				+ " capture=" + IsSlotCapturePassthrough
				+ " (IME pass-through; bind via edge)");
			}
		}

		/// <summary>True shortly after 「半角／全角」 — suppress JP IME→CapsLock false edges only.</summary>
		public static bool WasRecentHankakuZenkaku(int withinMs = HankakuZenkakuCapsSuppressMs)
		{
			if (_lastHankakuZenkakuEdgeMs == 0)
				return false;
			return Environment.TickCount64 - _lastHankakuZenkakuEdgeMs < withinMs;
		}

		/// <summary>
		/// Imm / conversion-mode CapsLock fakes after 「半角／全角」.
		/// Does NOT block real VK_CAPITAL / hook Caps (英数) — those must always bind and fire.
		/// </summary>
		public static bool ShouldSuppressImeFalseCapsLock()
			=> WasRecentHankakuZenkaku();

		/// <summary>Physical held bit from make/break (NLS; GetAsyncKeyState often useless).</summary>
		public static bool IsHankakuZenkakuPhysicallyDown
		{
			get
			{
				if (!_hankakuZenkakuPhysicallyDown)
					return false;

				if (_hankakuPhysicalDownSinceMs != 0
					&& Environment.TickCount64 - _hankakuPhysicalDownSinceMs > HankakuPhysicalDownMaxMs)
				{
					_hankakuZenkakuPhysicallyDown = false;
					_hankakuPhysicalDownSinceMs = 0;
					if (AkDiag.Enabled) AkDiag.Log("Hankaku/Zenkaku physical-down timeout cleared (missed KEYUP?)");
					return false;
				}

				return true;
			}
		}

		/// <summary>
		/// FNA maps JIS 「半角／全角」 (scan 0x29 / SDL grave) to <c>Keys.OemTilde</c>, which shows as
		/// <c>@</c> under JP layout. Real JP <c>@</c> still uses Win32 <c>VK_OEM_3</c> — only the
		/// scancode masquerade is suppressed so capture binds <see cref="BindingRestrictions.HankakuZenkakuToken"/> instead.
		/// </summary>
		public static bool ShouldIgnoreOemTildeScancodeMasquerade()
			=> IsHankakuZenkakuPhysicallyDown || WasRecentHankakuZenkaku();

		/// <summary>Queues 「半角／全角」 KEYDOWN for capture + one-frame gameplay (same pattern as kana).</summary>
		public static void QueueHankakuZenkakuEdge(string source)
		{
			long now = Environment.TickCount64;
			if (now - _lastHankakuBindEdgeMs < HankakuBindEdgeCoalesceMs)
			{
				if (AkDiag.Enabled)
				{
					AkDiag.Log(
					"Hankaku/Zenkaku KEYDOWN coalesce-skip src=" + source
					+ " capture=" + IsSlotCapturePassthrough);
				}
				return;
			}

			_lastHankakuBindEdgeMs = now;
			_pendingHankakuCaptureToken = BindingRestrictions.HankakuZenkakuToken;
			_pendingHankakuCaptureTick = now;
			_gameplayHankakuEdges.Add(BindingRestrictions.HankakuZenkakuToken);
			if (AkDiag.Enabled)
			{
				AkDiag.Log(
				"Hankaku/Zenkaku KEYDOWN queued token="
				+ BindingRestrictions.HankakuZenkakuToken
				+ " src=" + source
				+ " capture=" + IsSlotCapturePassthrough
				+ " latchAgeMaxMs=" + NlsCaptureLatchMaxAgeMs);
			}
		}

		public static bool TryConsumeHankakuZenkakuCapture(out string token)
		{
			token = _pendingHankakuCaptureToken;
			if (token == null)
				return false;

			if (Environment.TickCount64 - _pendingHankakuCaptureTick > NlsCaptureLatchMaxAgeMs)
			{
				if (AkDiag.Enabled)
				{
					AkDiag.Log(
					"Hankaku/Zenkaku latch expired ageMs>" + NlsCaptureLatchMaxAgeMs
					+ " capture=" + IsSlotCapturePassthrough
					+ " (rejected if WaitingForRelease drained late)");
				}
				_pendingHankakuCaptureToken = null;
				_pendingHankakuCaptureTick = 0;
				token = null;
				return false;
			}

			_pendingHankakuCaptureToken = null;
			_pendingHankakuCaptureTick = 0;
			_gameplayHankakuEdges.Remove(token);
			if (AkDiag.Enabled)
			{
				AkDiag.Log(
				"Hankaku/Zenkaku latch consumed→SlotCapture edges token=" + token
				+ " capture=" + IsSlotCapturePassthrough
				+ " (ACCEPT/REJECT decided by session phase)");
			}
			return true;
		}

		/// <summary>One-frame gameplay edges for 「半角／全角」.</summary>
		public static IReadOnlyCollection<string> DrainGameplayHankakuZenkakuEdges()
		{
			if (_gameplayHankakuEdges.Count == 0)
				return Array.Empty<string>();

			string[] edges = new string[_gameplayHankakuEdges.Count];
			_gameplayHankakuEdges.CopyTo(edges);
			_gameplayHankakuEdges.Clear();
			return edges;
		}

		private static void ClearHankakuBindEdges()
		{
			_pendingHankakuCaptureToken = null;
			_pendingHankakuCaptureTick = 0;
			_lastHankakuBindEdgeMs = 0;
			_gameplayHankakuEdges.Clear();
		}

		/// <summary>Queues a kana press edge (hook / WndProc / Raw). Coalesces duplicate sources.</summary>
		public static void QueueKanaEdge(string source)
		{
			long now = Environment.TickCount64;
			if (now - _lastKanaEdgeMs < KanaEdgeCoalesceMs)
			{
				if (AkDiag.Enabled)
				{
					AkDiag.Log(
					"Kana KEYDOWN coalesce-skip src=" + source
					+ " capture=" + IsSlotCapturePassthrough);
				}
				return;
			}

			_lastKanaEdgeMs = now;
			_pendingKanaCaptureToken = "Kana";
			_pendingKanaCaptureTick = now;
			_gameplayKanaEdges.Add("Kana");
			if (AkDiag.Enabled)
			{
				AkDiag.Log(
				"Kana KEYDOWN queued src=" + source
				+ " capture=" + IsSlotCapturePassthrough
				+ " latchAgeMaxMs=" + NlsCaptureLatchMaxAgeMs);
			}
		}

		public static bool TryConsumeKanaCapture(out string token)
		{
			token = _pendingKanaCaptureToken;
			if (token == null)
				return false;

			if (Environment.TickCount64 - _pendingKanaCaptureTick > NlsCaptureLatchMaxAgeMs)
			{
				if (AkDiag.Enabled)
				{
					AkDiag.Log(
					"Kana latch expired ageMs>" + NlsCaptureLatchMaxAgeMs
					+ " capture=" + IsSlotCapturePassthrough);
				}
				_pendingKanaCaptureToken = null;
				_pendingKanaCaptureTick = 0;
				token = null;
				return false;
			}

			_pendingKanaCaptureToken = null;
			_pendingKanaCaptureTick = 0;
			_gameplayKanaEdges.Remove(token);
			if (AkDiag.Enabled)
			{
				AkDiag.Log(
				"Kana latch consumed→SlotCapture edges token=" + token
				+ " capture=" + IsSlotCapturePassthrough
				+ " (ACCEPT/REJECT decided by session phase)");
			}
			return true;
		}

		/// <summary>Latch physical capture token (VK / SC fallback ids).</summary>
		public static void QueuePhysicalCaptureEdge(
			string token,
			uint vk,
			ushort scan,
			bool extended,
			string source)
		{
			if (string.IsNullOrEmpty(token))
				return;

			if (!IsSlotCapturePassthrough && !KeyboardDiagMode.IsActive)
				return;

			long now = Environment.TickCount64;
			_pendingPhysicalCaptureToken = token;
			_pendingPhysicalCaptureTick = now;
			_pendingWin32KeysCaptureToken = token;
			_pendingWin32KeysCaptureTick = now;
			_gameplayPhysicalEdges.Add(token);

			KeyboardDiagMode.NotifyPhysicalKeyEvent(
				vk,
				scan,
				extended,
				captureToken: token,
				captureSuccess: true,
				captureFailReason: null,
				source: source);

			if (AkDiag.Enabled)
			{
				AkDiag.Log(
				"PhysicalCapture edge token=" + token
				+ " vk=0x" + vk.ToString("X2")
				+ " scan=0x" + (scan & 0xFF).ToString("X2")
				+ " extended=" + extended
				+ " src=" + source
				+ " " + KeyboardLayoutHelper.FormatPhysicalKeyCaptureDiag(
				vk,
				adoptedToken: token,
				eventScan: scan,
				eventExtended: extended));
			}
		}

		public static bool TryConsumePhysicalCapture(out string token)
		{
			token = _pendingPhysicalCaptureToken ?? _pendingWin32KeysCaptureToken;
			if (token == null)
				return false;

			long tick = _pendingPhysicalCaptureToken != null
				? _pendingPhysicalCaptureTick
				: _pendingWin32KeysCaptureTick;
			int maxAge = PhysicalCaptureLatchMaxAgeMs;

			if (Environment.TickCount64 - tick > maxAge)
			{
				if (AkDiag.Enabled)
				{
					AkDiag.Log(
					"PhysicalCapture latch expired ageMs>" + maxAge
					+ " token=" + token
					+ " capture=" + IsSlotCapturePassthrough);
				}
				_pendingPhysicalCaptureToken = null;
				_pendingPhysicalCaptureTick = 0;
				_pendingWin32KeysCaptureToken = null;
				_pendingWin32KeysCaptureTick = 0;
				token = null;
				return false;
			}

			_pendingPhysicalCaptureToken = null;
			_pendingPhysicalCaptureTick = 0;
			_pendingWin32KeysCaptureToken = null;
			_pendingWin32KeysCaptureTick = 0;
			_gameplayPhysicalEdges.Remove(token);
			if (AkDiag.Enabled)
			{
				AkDiag.Log(
				"PhysicalCapture latch consumed→SlotCapture edges token=" + token
				+ " capture=" + IsSlotCapturePassthrough);
			}
			return true;
		}

		/// <summary>One-frame gameplay edges for VK/SC fallback tokens.</summary>
		public static IReadOnlyCollection<string> DrainGameplayPhysicalEdges()
		{
			if (_gameplayPhysicalEdges.Count == 0)
				return Array.Empty<string>();

			string[] edges = new string[_gameplayPhysicalEdges.Count];
			_gameplayPhysicalEdges.CopyTo(edges);
			_gameplayPhysicalEdges.Clear();
			return edges;
		}

		public static bool IsGenericScanTokenDown(string token)
		{
			if (!PhysicalKeyToken.TryParseScan(token, out ushort scan, out bool extended))
				return false;

			return _genericScanHeld.TryGetValue(new GenericScanId(scan, extended), out bool down) && down;
		}

		public static void SupplementGenericScanTokens(HashSet<string> pressed)
		{
			if (pressed == null)
				return;

			foreach (KeyValuePair<GenericScanId, bool> pair in _genericScanHeld)
			{
				if (!pair.Value)
					continue;

				pressed.Add(PhysicalKeyToken.FormatScan(pair.Key.Scan, pair.Key.Extended));
			}
		}

		/// <summary>
		/// Adds <c>VK:</c> tokens for keys held via Win32 KEYDOWN make/break.
		/// Trust hook make/break — do NOT drop on <c>GetAsyncKeyState</c> false
		/// (NLS / OEM keys often fail AsyncKeyState while still physically held).
		/// Ghosts are cleared on KEYUP / focus-loss / <see cref="ClearUnconfirmedScanHeldFlags"/>.
		/// </summary>
		/// <summary>
		/// Adds special-key Keys/VK held tokens, and orphan VK holds that have no measured SC sibling
		/// already present in <paramref name="pressed"/>.
		/// </summary>
		public static void SupplementSpecialOrOrphanFallbackTokens(HashSet<string> pressed)
		{
			if (pressed == null || _fallbackVkHeld.Count == 0)
				return;

			foreach (uint heldVk in _fallbackVkHeld)
			{
				if (!PhysicalKeyToken.IsValidVirtualKey(heldVk))
					continue;

				string vkToken = PhysicalKeyToken.FormatVk(heldVk);
				string keysName = null;
				if (Enum.IsDefined(typeof(Microsoft.Xna.Framework.Input.Keys), (int)heldVk))
				{
					var key = (Microsoft.Xna.Framework.Input.Keys)heldVk;
					if (key != Microsoft.Xna.Framework.Input.Keys.None)
						keysName = key.ToString();
				}

				if (MeasuredPhysicalKeyNormalizer.IsSpecialHandledVirtualKey(heldVk))
				{
					if (!string.IsNullOrEmpty(keysName))
						pressed.Add(keysName);
					else
						pressed.Add(vkToken);
					continue;
				}

				ushort measuredScan = 0;
				bool measuredExt = false;
				bool sameEvent = false;
				long lastSeq = 0;
				if (_vkMeasuredScan.TryGetValue(heldVk, out GenericScanId measured))
				{
					measuredScan = measured.Scan;
					measuredExt = measured.Extended;
				}

				if (_vkLastMeasuredEvent.TryGetValue(heldVk, out MeasuredPhysicalKeyEvent lastEv)
					&& lastEv.HasMeasuredScan
					&& lastEv.IsDown)
				{
					sameEvent = true;
					lastSeq = lastEv.EventSequence;
					if (measuredScan == 0)
					{
						measuredScan = lastEv.ScanCode;
						measuredExt = lastEv.Extended;
					}
				}

				// Priority: EventSequence → measured SC → AreSamePhysical (last resort).
				if (MeasuredVkCoverage.ShouldSuppressOrphanKeysOrVk(
					heldVk, measuredScan, measuredExt, keysName, pressed,
					sameEvent, eventSequence: lastSeq, existingEventSequence: lastSeq))
					continue;

				// True orphan (no measured SC): Keys name or VK: fallback only.
				pressed.Add(!string.IsNullOrEmpty(keysName) ? keysName : vkToken);
			}
		}

		public static void SupplementFallbackVkTokens(HashSet<string> pressed)
		{
			if (pressed == null || _fallbackVkHeld.Count == 0)
				return;

			foreach (uint heldVk in _fallbackVkHeld)
			{
				if (!PhysicalKeyToken.IsValidVirtualKey(heldVk))
					continue;

				pressed.Add(PhysicalKeyToken.FormatVk(heldVk));
			}
		}

		/// <summary>True while a KEYDOWN for this VK was seen and KEYUP has not yet cleared it.</summary>
		public static bool IsFallbackVkHeld(uint vk)
			=> PhysicalKeyToken.IsValidVirtualKey(vk) && _fallbackVkHeld.Contains(vk);

		/// <summary>
		/// Soft reconcile for scan-held ghosts when KEYUP was missed.
		/// Drops scan flags that have been held without a matching VK make for too long.
		/// </summary>
		public static void ReconcileGenericScanHeldGhosts()
		{
			if (_genericScanHeld.Count == 0)
				return;

			long now = Environment.TickCount64;
			List<GenericScanId> stale = null;
			foreach (KeyValuePair<GenericScanId, bool> pair in _genericScanHeld)
			{
				if (!pair.Value)
					continue;

				if (!_genericScanDownSinceMs.TryGetValue(pair.Key, out long sinceMs))
				{
					_genericScanDownSinceMs[pair.Key] = now;
					continue;
				}

				// 3s without KEYUP: treat as missed break (capture clears separately).
				if (now - sinceMs < 3000)
					continue;

				if (KeepExpiredGenericScanIfPhysicallyDown(pair.Key))
					continue;

				stale ??= new List<GenericScanId>();
				stale.Add(pair.Key);
			}

			if (stale == null)
				return;

			for (int i = 0; i < stale.Count; i++)
			{
				_genericScanHeld.Remove(stale[i]);
				_genericScanDownSinceMs.Remove(stale[i]);
			}
		}

		private static bool KeepExpiredGenericScanIfPhysicallyDown(GenericScanId id)
		{
			try
			{
				if (!OperatingSystem.IsWindows())
					return false;

				if (!IsGameForegroundCore())
					return false;

				if (Main.gameMenu)
					return false;

				if (Main.LocalPlayer == null)
					return false;

				if (Main.inFancyUI)
					return false;

				if (Main.ingameOptionsWindow)
					return false;

				if (Main.gamePaused)
					return false;

				if (BindingInputEngine.IsRecording)
					return false;

				if (IsSlotCapturePassthrough)
					return false;

				if (KeyboardDiagMode.IsActive)
					return false;

				foreach (KeyValuePair<uint, GenericScanId> measured in _vkMeasuredScan)
				{
					if (!measured.Value.Equals(id))
						continue;

					if (IsPhysicalKeyDown(measured.Key))
						return true;
				}

				return false;
			}
			catch
			{
				return false;
			}
		}

		/// <summary>
		/// Universal physical key tracker: scan held state + VK fallback held + capture/diag latch.
		/// During normal gameplay, rising edges for VK/SC identities also feed one-frame gameplay edges
		/// so JustPressed / Hold-release share the same tokens as registration.
		/// </summary>
		public static void ApplyPhysicalKeyEvent(
			uint vk,
			ushort scan,
			bool extended,
			bool isDown,
			string source)
		{
			if (!Win32KeyboardAcquisitionPolicy.AllowAcquisitionFrom(source))
				return;

			scan = (ushort)(scan & 0xFF);
			var id = new GenericScanId(scan, extended);

			if (!isDown)
			{
				_genericScanHeld.Remove(id);
				_genericScanDownSinceMs.Remove(id);
				if (PhysicalKeyToken.IsValidVirtualKey(vk))
				{
					_fallbackVkHeld.Remove(vk);
					_vkMeasuredScan.Remove(vk);
					_vkLastMeasuredEvent.Remove(vk);
				}

				long upSeq = ++_physicalKeyEventSequence;
				EventSequenceUnifyResult upResult = _eventSequenceUnifier.ObserveUp(
					scan, extended, vk, upSeq);
				if (AkDiag.Enabled)
				{
					AkDiag.Log(
						"EventSequence KeyUp seq=" + upResult.EventSequence
						+ " released=" + (upResult.CanonicalToken ?? "-")
						+ " sc=0x" + (scan & 0xFF).ToString("X2")
						+ " ext=" + (extended ? 1 : 0)
						+ " vk=0x" + vk.ToString("X2"));
				}

				if (JisOemInGameDiagnostic.IsSessionActive)
				{
					JisOemInGameDiagnostic.ObserveKeyUp(
						vk, scan, extended, source, FormatHeldDiagSnapshot(),
						eventSequence: upResult.EventSequence);
				}

				return;
			}

			bool scanRising = !_genericScanHeld.TryGetValue(id, out bool wasScanDown) || !wasScanDown;
			_genericScanHeld[id] = true;
			_genericScanDownSinceMs[id] = Environment.TickCount64;

			if (!IsGameForegroundCore() && !KeyboardDiagMode.IsActive)
				return;

			if (IsKanaPhysicalKey(vk, scan)
				|| IsHankakuZenkakuPhysicalKey(vk, scan)
				|| TryMapJisIntlScanToToken(scan, out _)
				|| IsPauseOrBreakVk(vk)
				|| (scan == SC_NUMLOCK && vk != VK_NUMLOCK)
				|| vk == VK_CAPITAL
				|| vk == VK_NUMLOCK
				|| vk == VK_SCROLL
				|| scan == SC_CAPITAL
				|| scan == SC_SCROLL)
			{
				// Pause/Break never use GetAsyncKeyState — keep make/break VK held for Snapshot.
				if (IsPauseOrBreakVk(vk) && PhysicalKeyToken.IsValidVirtualKey(vk))
					_fallbackVkHeld.Add(vk);
				return;
			}

			PhysicalKeyResolver.ResolveResult resolved = PhysicalKeyResolver.ResolveForCapture(
				vk,
				scan,
				extended,
				Main.keyState);

			if (!resolved.Success)
			{
				KeyboardDiagMode.NotifyPhysicalKeyEvent(
					vk,
					scan,
					extended,
					captureToken: null,
					captureSuccess: false,
					captureFailReason: resolved.FailReason,
					source: source);
				if (AkDiag.Enabled)
				{
					AkDiag.Log(
						"PhysicalCapture REJECT reason=" + resolved.FailReason
						+ " src=" + source
						+ " " + KeyboardLayoutHelper.FormatPhysicalKeyCaptureDiag(
							vk,
							adoptedToken: "-",
							eventScan: scan,
							eventExtended: extended));
				}

				if (JisOemInGameDiagnostic.IsSessionActive)
				{
					JisOemInGameDiagnostic.Observe(
						vk, scan, extended, isDown: true, source, resolved,
						beforeCandidates: null,
						afterCandidates: null,
						captureResolverInput: null,
						chooseMeasuredToken: null,
						finalOrReject: "resolver-reject");
				}

				return;
			}

			// Always track VK make/break so old Keys/VK bindings can match via PhysicalKeyIdentity.
			long seq = ++_physicalKeyEventSequence;
			MeasuredPhysicalKeyEvent measuredEv = MeasuredPhysicalKeyEvent.FromKeyDown(
				vk, scan, extended, source, resolved.Token, seq);

			bool isKeyRepeat = !scanRising;
			if (AkDiag.Enabled)
				_eventSequenceUnifier.DiagSink ??= static line => AkDiag.Log("EventSequence " + line);

			EventSequenceUnifyResult unify = _eventSequenceUnifier.ObserveDown(measuredEv, isKeyRepeat);

			if (PhysicalKeyToken.IsValidVirtualKey(vk))
			{
				_fallbackVkHeld.Add(vk);
				if (measuredEv.HasMeasuredScan)
				{
					_vkMeasuredScan[vk] = id;
					_vkLastMeasuredEvent[vk] = measuredEv;
				}
				else
				{
					_vkMeasuredScan.Remove(vk);
					_vkLastMeasuredEvent.Remove(vk);
				}
			}

			MeasuredPhysicalKeyEdgePolicy.Decision edge =
				MeasuredPhysicalKeyEdgePolicy.DecideDown(
					vk,
					scan,
					extended,
					resolved.Token,
					scanRising);

			// Registration / edges: EventSequence-unified canonical (SC when measured).
			string canonicalToken = !string.IsNullOrEmpty(unify.CanonicalToken)
				? unify.CanonicalToken
				: (!string.IsNullOrEmpty(measuredEv.CanonicalToken)
					? measuredEv.CanonicalToken
					: edge.CanonicalToken);

			bool jisDiag = JisOemInGameDiagnostic.IsSessionActive;
			string[] beforePreview = null;
			string[] afterPreview = null;
			if (jisDiag || AkDiag.Enabled)
			{
				beforePreview = BuildPreNormalizeCandidatePreview(vk, scan, extended, resolved.Token);
				List<string> reg = SameEventAliasPolicy.BuildRegistrationNewcomers(measuredEv);
				afterPreview = reg.Count == 0 ? Array.Empty<string>() : reg.ToArray();
			}

			if (AkDiag.Enabled)
			{
				AkDiag.Log(
					"EventSequence Down seq=" + seq
					+ " vk=0x" + vk.ToString("X2")
					+ " sc=0x" + (scan & 0xFF).ToString("X2")
					+ " ext=" + (extended ? 1 : 0)
					+ " keys=" + (resolved.Token ?? "-")
					+ " canonical=" + (canonicalToken ?? "-")
					+ " repeat=" + isKeyRepeat
					+ " added=" + unify.AddedAsNewcomer
					+ " suppressedAlias=" + (unify.ExcludedAlias ?? "-")
					+ " replacedSc=" + unify.ReplacedFallbackWithScan
					+ " ignoreRepeat=" + unify.IgnoredAsRepeat);
			}

			bool gameplay = !IsSlotCapturePassthrough && !KeyboardDiagMode.IsActive;

			if (gameplay)
			{
				foreach (string edgeToken in edge.CollectGameplayEdgeTokens())
					_gameplayPhysicalEdges.Add(edgeToken);

				KeyboardDiagMode.NotifyPhysicalKeyEvent(
					vk,
					scan,
					extended,
					captureToken: canonicalToken,
					captureSuccess: true,
					captureFailReason: null,
					source: source);
				if (jisDiag)
				{
					JisOemInGameDiagnostic.Observe(
						vk, scan, extended, isDown: true, source, resolved,
						beforeCandidates: beforePreview,
						afterCandidates: afterPreview,
						captureResolverInput: null,
						chooseMeasuredToken: canonicalToken,
						finalOrReject: "gameplay-edges:[" + string.Join(",", edge.CollectGameplayEdgeTokens()) + "]",
						eventSequence: seq,
						unifyNote: FormatUnifyNote(unify));
				}

				return;
			}

			// Autorepeat / EventSequence unifier: do not re-latch capture for same physical hold.
			bool latch = edge.LatchCapture && unify.AddedAsNewcomer && !unify.IgnoredAsRepeat;
			if (latch)
				QueuePhysicalCaptureEdge(canonicalToken, vk, scan, extended, source);

			if (jisDiag)
			{
				JisOemInGameDiagnostic.Observe(
					vk, scan, extended, isDown: true, source, resolved,
					beforeCandidates: beforePreview,
					afterCandidates: afterPreview,
					captureResolverInput: afterPreview,
					chooseMeasuredToken: canonicalToken,
					finalOrReject: latch
						? "capture-edge:" + canonicalToken
						: (unify.IgnoredAsRepeat
							? "capture-repeat-ignored:" + canonicalToken
							: "capture-same-seq:" + canonicalToken),
					eventSequence: seq,
					unifyNote: FormatUnifyNote(unify));
			}
		}

		private static string FormatUnifyNote(EventSequenceUnifyResult unify)
		{
			return "seq=" + unify.EventSequence
				+ ";added=" + unify.AddedAsNewcomer
				+ ";suppressed=" + (unify.ExcludedAlias ?? "-")
				+ ";replacedSc=" + unify.ReplacedFallbackWithScan
				+ ";repeat=" + unify.IgnoredAsRepeat;
		}

		private static string FormatHeldDiagSnapshot()
		{
			var parts = new List<string>();
			foreach (var pair in _genericScanHeld)
			{
				if (pair.Value)
					parts.Add(PhysicalKeyToken.FormatScan(pair.Key.Scan, pair.Key.Extended));
			}

			return "[" + string.Join(",", parts) + "]";
		}

		private static string[] BuildPreNormalizeCandidatePreview(uint vk, ushort scan, bool extended, string resolverToken)
		{
			var list = new List<string>(3);
			if (!string.IsNullOrEmpty(resolverToken))
				list.Add(resolverToken);
			if (PhysicalKeyToken.IsValidVirtualKey(vk))
				list.Add(PhysicalKeyToken.FormatVk(vk));
			if (scan != 0)
				list.Add(PhysicalKeyToken.FormatScan(scan, extended));
			return list.ToArray();
		}

		/// <summary>Latch FNA-missing Keys token from WndProc/WM_INPUT KEYDOWN (legacy alias).</summary>
		public static void QueueWin32KeysCaptureEdge(
			string token,
			uint vk,
			ushort scan,
			bool extended,
			string source)
			=> QueuePhysicalCaptureEdge(token, vk, scan, extended, source);

		public static bool TryConsumeWin32KeysCapture(out string token)
			=> TryConsumePhysicalCapture(out token);

		/// <summary>One-frame gameplay edges for Kana (same NLS key as capture).</summary>
		public static IReadOnlyCollection<string> DrainGameplayKanaEdges()
		{
			if (_gameplayKanaEdges.Count == 0)
				return Array.Empty<string>();

			string[] edges = new string[_gameplayKanaEdges.Count];
			_gameplayKanaEdges.CopyTo(edges);
			_gameplayKanaEdges.Clear();
			return edges;
		}

		/// <summary>
		/// Maps JIS/ISO make-codes to existing OEM/IME tokens.
		/// <list type="bullet">
		/// <item><c>0x7D</c> ¥| (left of BS) → OemPipe — JIS-unique (never on US ANSI)</item>
		/// <item><c>0x73</c> ろ (left of R-Shift) → OemBackslash — JIS-unique</item>
		/// <item><c>0x56</c> ISO 102 / ABNT2 extra (left of L-Shift) → <c>SC:0x56:Normal</c> (distinct from ろ)</item>
		/// <item><c>0x79</c>/<c>0x7B</c> 変換／無変換 → ImeConvert / ImeNoConvert — JIS-unique</item>
		/// <item><c>0x0C</c>/<c>0x0D</c> JIS <c>-</c>/<c>^</c> — only when physical JIS: DE/etc remaps VKs so FNA may report brackets; pin OemMinus/OemQuotes</item>
		/// </list>
		/// Pure US ANSI never emits 0x7D/0x73/0x79/0x7B; 0x0C/0x0D need the physical-JIS gate.
		/// </summary>
		internal static bool TryMapJisIntlScanToToken(uint scanCode, out string token)
		{
			uint scan = scanCode & 0xFF;
			token = scan switch {
				SC_INTL_YEN => "OemPipe",
				SC_INTL_RO => "OemBackslash",
				// Same VK_OEM_102 as ろ, but different scan — keep distinct for ISO/ABNT2 boards.
				SC_ISO_102 => PhysicalKeyToken.FormatScan((ushort)SC_ISO_102, extended: false),
				SC_CONVERT => "ImeConvert",
				SC_NONCONVERT => "ImeNoConvert",
				_ => null
			};
			if (token != null)
				return true;

			// JP keycaps - / ^ next to ¥: under German layout FNA often yields ß / brackets instead.
			if (KeyboardLayoutHelper.IsJapanesePhysicalKeyboard())
			{
				token = scan switch {
					SC_JIS_OEM_MINUS => "OemMinus",
					SC_JIS_OEM_EQUAL => "OemQuotes",
					_ => null
				};
				return token != null;
			}

			return false;
		}

		/// <summary>
		/// Updates held state for JIS/ISO intl scans from hook / WndProc / Raw make+break.
		/// KEYUP always clears; KEYDOWN requires game process foreground (same as kana).
		/// Rising edges latch capture + one-frame gameplay tokens (US layout may never raise VKs).
		/// </summary>
		public static void ApplyJisIntlScan(uint scanCode, bool isDown, string source)
		{
			if (!Win32KeyboardAcquisitionPolicy.AllowAcquisitionFrom(source))
				return;

			if (!TryMapJisIntlScanToToken(scanCode, out string token))
				return;

			uint scan = scanCode & 0xFF;
			if (!isDown)
			{
				SetJisIntlScanDown(scan, false);
				return;
			}

			if (!IsGameForegroundCore())
				return;

			bool wasDown = IsJisIntlScanDown(scan);
			SetJisIntlScanDown(scan, true);
			if (wasDown)
				return;

			long now = Environment.TickCount64;
			if (now - _lastJisIntlScanEdgeMs < JisIntlScanEdgeCoalesceMs)
				return;

			_lastJisIntlScanEdgeMs = now;
			_pendingJisIntlCaptureToken = token;
			_pendingJisIntlCaptureTick = now;
			_gameplayJisIntlEdges.Add(token);
			if (AkDiag.Enabled)
			{
				AkDiag.Log(
				"JisIntlScan KEYDOWN edge token=" + token
				+ " scan=0x" + scan.ToString("X2")
				+ " src=" + source);
			}
		}

		public static bool TryConsumeJisIntlCapture(out string token)
		{
			token = _pendingJisIntlCaptureToken;
			if (token == null)
				return false;

			if (Environment.TickCount64 - _pendingJisIntlCaptureTick > NlsCaptureLatchMaxAgeMs)
			{
				if (AkDiag.Enabled) AkDiag.Log("JisIntl capture latch expired token=" + token + " ageMs>" + NlsCaptureLatchMaxAgeMs);
				_pendingJisIntlCaptureToken = null;
				_pendingJisIntlCaptureTick = 0;
				token = null;
				return false;
			}

			_pendingJisIntlCaptureToken = null;
			_pendingJisIntlCaptureTick = 0;
			_gameplayJisIntlEdges.Remove(token);
			if (AkDiag.Enabled) AkDiag.Log("JisIntl capture latch accepted token=" + token);
			return true;
		}

		/// <summary>One-frame gameplay edges for JIS/ISO OEM/IME scans (same keys as capture).</summary>
		public static IReadOnlyCollection<string> DrainGameplayJisIntlEdges()
		{
			if (_gameplayJisIntlEdges.Count == 0)
				return Array.Empty<string>();

			string[] edges = new string[_gameplayJisIntlEdges.Count];
			_gameplayJisIntlEdges.CopyTo(edges);
			_gameplayJisIntlEdges.Clear();
			return edges;
		}

		public static bool IsJisIntlScanTokenDown(string token)
		{
			if (string.Equals(token, "OemPipe", StringComparison.OrdinalIgnoreCase))
				return _jisIntlYenDown;
			if (string.Equals(token, "OemBackslash", StringComparison.OrdinalIgnoreCase))
			{
				// New captures: ろ only. Legacy OemBackslash bindings also fire for ISO102.
				return _jisIntlRoDown || _jisIntlIso102Down;
			}
			if (PhysicalKeyToken.TryParseScan(token, out ushort scan, out bool extended)
				&& scan == SC_ISO_102
				&& !extended)
				return _jisIntlIso102Down;
			if (string.Equals(token, "OemMinus", StringComparison.OrdinalIgnoreCase))
				return _jisOemMinusDown;
			if (string.Equals(token, "OemQuotes", StringComparison.OrdinalIgnoreCase))
				return _jisOemEqualDown;
			if (string.Equals(token, "ImeConvert", StringComparison.OrdinalIgnoreCase))
				return _jisConvertDown;
			if (string.Equals(token, "ImeNoConvert", StringComparison.OrdinalIgnoreCase))
				return _jisNonConvertDown;
			return false;
		}

		/// <summary>Adds scan-held JIS/ISO OEM/IME tokens missing from VK polling (US layout + physical JIS).</summary>
		public static void SupplementJisIntlScanTokens(HashSet<string> pressed)
		{
			if (pressed == null)
				return;
			if (_jisIntlYenDown)
				pressed.Add("OemPipe");
			if (_jisIntlRoDown)
				pressed.Add("OemBackslash");
			if (_jisIntlIso102Down)
			{
				pressed.Add(PhysicalKeyToken.FormatScan((ushort)SC_ISO_102, extended: false));
				// Legacy: keep OemBackslash active so old ISO102 bindings still fire.
				pressed.Add("OemBackslash");
			}
			if (_jisOemMinusDown)
			{
				pressed.Add("OemMinus");
				RemoveConflictingOemForJisNumberRow(pressed, keep: "OemMinus");
			}
			if (_jisOemEqualDown)
			{
				pressed.Add("OemQuotes");
				RemoveConflictingOemForJisNumberRow(pressed, keep: "OemQuotes");
			}
			if (_jisConvertDown)
				pressed.Add("ImeConvert");
			if (_jisNonConvertDown)
				pressed.Add("ImeNoConvert");
		}

		/// <summary>
		/// Under DE/etc, FNA may report brackets/plus for the same physical JIS -/^ key as the scan pin.
		/// Drop other OEM names so capture does not store OemCloseBrackets instead of OemQuotes.
		/// Never strip sibling JIS scan pins (-/^/¥/ろ) so multi-key chords still work.
		/// </summary>
		private static void RemoveConflictingOemForJisNumberRow(HashSet<string> pressed, string keep)
		{
			List<string> remove = null;
			foreach (string t in pressed)
			{
				if (string.Equals(t, keep, StringComparison.OrdinalIgnoreCase))
					continue;
				if (!StableKeyToken.IsOemToken(t))
					continue;
				if (IsJisIntlScanTokenDown(t))
					continue;
				remove ??= new List<string>();
				remove.Add(t);
			}

			if (remove == null)
				return;
			foreach (string t in remove)
				pressed.Remove(t);
		}

		private static bool IsJisIntlScanDown(uint scan)
			=> (scan & 0xFF) switch {
				SC_INTL_YEN => _jisIntlYenDown,
				SC_INTL_RO => _jisIntlRoDown,
				SC_ISO_102 => _jisIntlIso102Down,
				SC_JIS_OEM_MINUS => _jisOemMinusDown,
				SC_JIS_OEM_EQUAL => _jisOemEqualDown,
				SC_CONVERT => _jisConvertDown,
				SC_NONCONVERT => _jisNonConvertDown,
				_ => false
			};

		private static void SetJisIntlScanDown(uint scan, bool down)
		{
			switch (scan & 0xFF)
			{
				case SC_INTL_YEN:
					_jisIntlYenDown = down;
					break;
				case SC_INTL_RO:
					_jisIntlRoDown = down;
					break;
				case SC_ISO_102:
					_jisIntlIso102Down = down;
					break;
				case SC_JIS_OEM_MINUS:
					_jisOemMinusDown = down;
					break;
				case SC_JIS_OEM_EQUAL:
					_jisOemEqualDown = down;
					break;
				case SC_CONVERT:
					_jisConvertDown = down;
					break;
				case SC_NONCONVERT:
					_jisNonConvertDown = down;
					break;
			}
		}

		private static void ClearJisIntlPendingEdges()
		{
			_pendingJisIntlCaptureToken = null;
			_pendingJisIntlCaptureTick = 0;
			_gameplayJisIntlEdges.Clear();
			_lastJisIntlScanEdgeMs = 0;
		}

		private static void ClearJisIntlScanState()
		{
			_jisIntlYenDown = false;
			_jisIntlRoDown = false;
			_jisIntlIso102Down = false;
			_jisOemMinusDown = false;
			_jisOemEqualDown = false;
			_jisConvertDown = false;
			_jisNonConvertDown = false;
			ClearJisIntlPendingEdges();
		}

		/// <summary>
		/// True when the full capture held set includes scan/latch-only ghosts absent from confirmed held.
		/// Contradiction evidence for RecoveringFromInvalidState (not a timed clear).
		/// </summary>
		public static bool HasCaptureHeldApiMismatch(
			IReadOnlyCollection<string> held,
			IReadOnlyCollection<string> confirmedHeld)
		{
			if (held == null || held.Count == 0 || confirmedHeld == null)
				return false;

			foreach (string token in held)
			{
				if (string.IsNullOrEmpty(token))
					continue;
				if (ContainsIgnoreCase(confirmedHeld, token))
					continue;
				if (IsUnconfirmedCaptureGhostToken(token))
					return true;
			}

			return false;
		}

		/// <summary>
		/// Clears MOD-owned capture ghost held flags for tokens recovered by the state machine.
		/// Does not touch Terraria Main.blockInput / other-mod state.
		/// </summary>
		public static void ClearCaptureGhostHeldTokens(IReadOnlyCollection<string> tokens)
		{
			if (tokens == null || tokens.Count == 0)
				return;

			foreach (string token in tokens)
			{
				if (string.IsNullOrEmpty(token))
					continue;

				if (string.Equals(token, "OemPipe", StringComparison.OrdinalIgnoreCase))
					_jisIntlYenDown = false;
				else if (string.Equals(token, "OemBackslash", StringComparison.OrdinalIgnoreCase))
				{
					_jisIntlRoDown = false;
					_jisIntlIso102Down = false;
				}
				else if (string.Equals(token, "OemMinus", StringComparison.OrdinalIgnoreCase))
					_jisOemMinusDown = false;
				else if (string.Equals(token, "OemQuotes", StringComparison.OrdinalIgnoreCase))
					_jisOemEqualDown = false;
				else if (string.Equals(token, "ImeConvert", StringComparison.OrdinalIgnoreCase))
					_jisConvertDown = false;
				else if (string.Equals(token, "ImeNoConvert", StringComparison.OrdinalIgnoreCase))
					_jisNonConvertDown = false;
				else if (string.Equals(token, BindingRestrictions.HankakuZenkakuToken, StringComparison.OrdinalIgnoreCase))
				{
					_hankakuZenkakuPhysicallyDown = false;
					_hankakuPhysicalDownSinceMs = 0;
				}
				else if (string.Equals(token, "Kana", StringComparison.OrdinalIgnoreCase)
					|| string.Equals(token, "OemCopy", StringComparison.OrdinalIgnoreCase))
				{
					_kanaPhysicallyDown = false;
					_kanaPhysicalDownSinceMs = 0;
				}
				else if (PhysicalKeyToken.TryParseVk(token, out uint ghostVk))
					_fallbackVkHeld.Remove(ghostVk);
				else if (PhysicalKeyToken.TryParseScan(token, out ushort ghostScan, out bool ghostExtended))
					_genericScanHeld.Remove(new GenericScanId(ghostScan, ghostExtended));
			}

			ClearJisIntlPendingEdges();
			ClearHankakuBindEdges();
			_pendingKanaCaptureToken = null;
			_pendingKanaCaptureTick = 0;
			if (AkDiag.Enabled)
			{
				AkDiag.Log(
				"SlotCapture ghost-held CLEAR tokens=[" + string.Join(",", tokens) + "]"
				+ " capture=" + IsSlotCapturePassthrough
				+ " (contradiction recovery)");
			}
		}

		/// <summary>Clears all unconfirmed scan / hankaku / kana physical flags (exception residue / hard recover).</summary>
		public static void ClearUnconfirmedScanHeldFlags(string reason)
		{
			_jisIntlYenDown = false;
			_jisIntlRoDown = false;
			_jisIntlIso102Down = false;
			_jisOemMinusDown = false;
			_jisOemEqualDown = false;
			_jisConvertDown = false;
			_jisNonConvertDown = false;
			_hankakuZenkakuPhysicallyDown = false;
			_hankakuPhysicalDownSinceMs = 0;
			_kanaPhysicallyDown = false;
			_kanaPhysicalDownSinceMs = 0;
			_genericScanHeld.Clear();
			_genericScanDownSinceMs.Clear();
			_fallbackVkHeld.Clear();
			_vkMeasuredScan.Clear();
			_vkLastMeasuredEvent.Clear();
			_eventSequenceUnifier.Clear();
			_eventSequenceUnifier.DiagSink = null;
			_gameplayPhysicalEdges.Clear();
			ClearJisIntlPendingEdges();
			ClearHankakuBindEdges();
			_pendingKanaCaptureToken = null;
			_pendingKanaCaptureTick = 0;
			_gameplayKanaEdges.Clear();
			if (AkDiag.Enabled) AkDiag.Log("Unconfirmed scan/hankaku/kana held cleared reason=" + reason);
		}

		private static bool IsUnconfirmedCaptureGhostToken(string token)
		{
			if (string.Equals(token, "ImeConvert", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(token, "ImeNoConvert", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(token, "OemPipe", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(token, "OemBackslash", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(token, BindingRestrictions.HankakuZenkakuToken, StringComparison.OrdinalIgnoreCase)
				|| string.Equals(token, "Kana", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(token, "Kanji", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(token, "OemCopy", StringComparison.OrdinalIgnoreCase))
				return true;

			return IsJisIntlScanTokenDown(token);
		}

		private static bool ContainsIgnoreCase(IReadOnlyCollection<string> set, string value)
		{
			foreach (string item in set)
			{
				if (string.Equals(item, value, StringComparison.OrdinalIgnoreCase))
					return true;
			}

			return false;
		}

		private static void QueueWinKeyCapture(uint vkCode)
		{
			_pendingWinCaptureToken = vkCode == VK_LWIN ? "LeftWindows" : "RightWindows";
			_pendingWinCaptureTick = Environment.TickCount64;
			if (AkDiag.Enabled) AkDiag.Log("WinKey KEYDOWN queued for capture: " + _pendingWinCaptureToken);
		}

		public static void SetSlotCapturePassthrough(bool enabled)
		{
			IsSlotCapturePassthrough = enabled;
			if (enabled)
			{
				// Acceptance uses RegisterCaptureSession WaitingForRelease (all start-held up),
				// not a timed warmup — keep warmup unset so fresh toggle edges are not delayed.
				_captureWarmupUntilMs = 0;
				// Drop any pre-open edges; do not leave a fresh pending NumLock from Pause bleed.
				_pendingToggleLockCaptureToken = null;
				_pendingToggleLockCaptureTick = 0;
				_pendingKanaCaptureToken = null;
				_pendingKanaCaptureTick = 0;
				_gameplayKanaEdges.Clear();
				// Keep scan-held flags (may still be physically down); only drop pending edges.
				ClearHankakuBindEdges();
				ClearJisIntlPendingEdges();
				GameWindowInputHook.ClearPendingEdges();
				SyncWindowsKeyStateFromOs();
				SyncSlotCapturePhysicalState();
				JpCapsLockDetector.Reset();
				if (AkDiag.Enabled) AkDiag.Log("Slot capture physical baseline capsDown=" + _prevCapsHighBit + " hook=" + IsInstalled);
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
			if (!OperatingSystem.IsWindows())
				return;

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
						if (AkDiag.Enabled) AkDiag.Log("Toggle-lock releasing CapsLock physical state.");
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
			if (AkDiag.Enabled) AkDiag.Log("Toggle-lock gameplay edge: " + token);
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
			if (AkDiag.Enabled) AkDiag.Log("WinKey gameplay edge: " + token);
		}

		internal static bool TryApplyCtrlEscCaptureOrBlock(uint vk, bool isDown, out bool blocked)
			=> TryHandleCtrlEscChord(vk, isDown, out blocked);

		private static void QueueEscChordCapture()
		{
			_pendingEscChordCaptureToken = "Escape";
			_pendingEscChordCaptureTick = Environment.TickCount64;
			if (AkDiag.Enabled) AkDiag.Log("Ctrl+Esc KEYDOWN queued for slot capture.");
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

		/// <summary>
		/// Clears every pending capture latch and alternate-source edge so opening a combo slot
		/// cannot bind a stale Pause/Break→fake-NumLock or leftover Caps/Scroll edge.
		/// Also re-baselines physical prev so the open-frame high-bit is not treated as a press.
		/// </summary>
		public static void DiscardAllPendingCaptureEdges()
		{
			_pendingToggleLockCaptureToken = null;
			_pendingToggleLockCaptureTick = 0;
			_pendingWinCaptureToken = null;
			_pendingWinCaptureTick = 0;
			_pendingEscChordCaptureToken = null;
			_pendingEscChordCaptureTick = 0;
			_pendingKanaCaptureToken = null;
			_pendingKanaCaptureTick = 0;
			_gameplayKanaEdges.Clear();
			_imeConversionToggleForRecovery = false;
			ClearHankakuBindEdges();
			ClearJisIntlPendingEdges();
			GameWindowInputHook.ClearPendingEdges();
			JpCapsLockDetector.Reset();
			SyncSlotCapturePhysicalState();
			if (AkDiag.Enabled) AkDiag.Log("Discarded all pending capture edges (toggle/win/esc/kana/hankaku/jisIntl/window/JP)");
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
			// Real Caps (VK_CAPITAL / LL-hook queue) must never be discarded for hankaku suppress.
			// Only IME→Caps fakes from alternate drains are filtered (see TryDrainAlternateToggleEdges).
			if (TryConsumeToggleLockPress(out token))
			{
				if (AkDiag.Enabled)
				{
					AkDiag.Log(
					"Toggle-lock queued edge for capture: " + token
					+ (string.Equals(token, "CapsLock", StringComparison.OrdinalIgnoreCase)
					? " src=realHookOrPhysical"
					: ""));
				}
				return true;
			}

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
				if (AkDiag.Enabled) AkDiag.Log("Toggle-lock physical edge for capture: " + token + " src=VK_CAPITAL");
				return true;
			}

			if (TryPhysicalKeyEdge(VK_NUMLOCK, "NumLock", ref _prevNumHighBit, out token))
			{
				if (AkDiag.Enabled) AkDiag.Log("Toggle-lock physical edge for capture: " + token);
				return true;
			}

			if (TryPhysicalKeyEdge(VK_SCROLL, BindingRestrictions.ScrollLockToken, ref _prevScrollHighBit, out token))
			{
				if (AkDiag.Enabled) AkDiag.Log("Toggle-lock physical edge for capture: " + token);
				return true;
			}

			if (TryBoolEdge(IsCapsLockPhysicallyDown, ref _prevCapsPhysicallyDown, "CapsLock", out token))
			{
				if (AkDiag.Enabled) AkDiag.Log("Toggle-lock hook edge for capture: " + token + " src=hookPhysicalDown");
				return true;
			}

			if (TryBoolEdge(IsNumLockPhysicallyDown, ref _prevNumPhysicallyDown, "NumLock", out token))
			{
				if (AkDiag.Enabled) AkDiag.Log("Toggle-lock hook edge for capture: " + token);
				return true;
			}

			if (TryBoolEdge(IsScrollLockPhysicallyDown, ref _prevScrollPhysicallyDown, BindingRestrictions.ScrollLockToken, out token))
			{
				if (AkDiag.Enabled) AkDiag.Log("Toggle-lock hook edge for capture: " + token);
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
				// Imm conversion after 「半角／全角」 can enqueue fake CapsLock — not real VK_CAPITAL.
				if (string.Equals(edge, "CapsLock", StringComparison.OrdinalIgnoreCase)
					&& ShouldSuppressImeFalseCapsLock())
				{
					if (AkDiag.Enabled) AkDiag.Log("IME→Caps fake discarded (hankaku window) src=windowEdge");
					continue;
				}

				token = edge;
				if (AkDiag.Enabled)
				{
					AkDiag.Log(
					"Toggle-lock window edge: " + token
					+ (string.Equals(token, "CapsLock", StringComparison.OrdinalIgnoreCase)
					? " src=window(possibleRealScanCaps)"
					: ""));
				}
				return true;
			}

			JpCapsLockDetector.Reset();

			// Discarded: GetKeyboardState / IME Caps synthesis is not a Microsoft keystroke path.
			if (!Win32KeyboardAcquisitionPolicy.KeystrokeMessagesOnly)
			{
				JpCapsLockDetector.Poll(allowImeEdges);

				foreach (string edge in JpCapsLockDetector.DrainEdges())
				{
					if (string.Equals(edge, "CapsLock", StringComparison.OrdinalIgnoreCase)
						&& ShouldSuppressImeFalseCapsLock())
					{
						if (AkDiag.Enabled) AkDiag.Log("IME→Caps fake discarded (hankaku window) src=JpCaps");
						continue;
					}

					token = edge;
					if (AkDiag.Enabled)
					{
						AkDiag.Log(
						"Toggle-lock JP edge: " + token
						+ (string.Equals(token, "CapsLock", StringComparison.OrdinalIgnoreCase)
						? " src=JpCaps(imeOrScan)"
						: ""));
					}
					return true;
				}
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
				if (AkDiag.Enabled) AkDiag.Log("Toggle-lock KEYDOWN queued for capture: " + _pendingToggleLockCaptureToken);
			}
		}

		/// <summary>VK_PAUSE / VK_CANCEL share make-code 0x45 with NumLock — never treat them as NumLock.</summary>
		internal static bool IsPauseOrBreakVk(uint vkCode)
			=> vkCode == VK_PAUSE || vkCode == VK_CANCEL;

		private static bool TryResolveToggleLockEvent(KBDLLHOOKSTRUCT data, out uint vkCode)
		{
			uint scan = data.scanCode & 0xFF;

			if (data.vkCode == VK_CAPITAL || scan == SC_CAPITAL)
			{
				vkCode = VK_CAPITAL;
				return true;
			}

			// Pause/Break share make-code 0x45 with NumLock. Never treat them (or bare 0x45) as NumLock.
			if (IsPauseOrBreakVk(data.vkCode))
			{
				if (AkDiag.Enabled) AkDiag.Log($"Pause/Break ignored as toggle-lock vk=0x{data.vkCode:X2} scan=0x{scan:X2}");
				vkCode = 0;
				return false;
			}

			// NumLock only when VK is NumLock (0x90). Scan 0x45 alone never decides NumLock.
			if (data.vkCode == VK_NUMLOCK)
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
				uint threadScan = (uint)((lp >> 16) & 0xFF);

				// 「半角／全角」 must always reach OS/IME — never swallow on the thread hook.
				// Acquisition is Microsoft keystroke messages only.
				if (IsHankakuZenkakuPhysicalKey(vk, threadScan))
					return CallNextHookEx(_threadHookId, nCode, wParam, lParam);

				HandleShellBlockedEscapeKeyUp(vk, isUp);

				if (IsGameForegroundCore())
					_lastGameForegroundMs = Environment.TickCount64;

				if (!Win32KeyboardAcquisitionPolicy.KeystrokeMessagesOnly)
				{
					if (IsKanaPhysicalKey(vk, threadScan))
					{
						ApplyKanaPhysical(
							vk,
							threadScan,
							isDown,
							"thread vk=0x" + vk.ToString("X2") + " scan=0x" + threadScan.ToString("X2"));
					}

					ApplyJisIntlScan(threadScan, isDown, "thread vk=0x" + vk.ToString("X2"));
				}
#if AK_DIAGNOSTICS
				if (isDown && vk == VK_ESCAPE && IsControlDown())
				{
					bool suppress = ShouldBlockCtrlEscForShell()
						|| (IsSlotCapturePassthrough && IsGameForegroundCore());
					StartMenuDiag.LogThread(vk, isDown, suppress, suppress ? "ctrl+esc" : "ctrl+esc reason=" + DescribeWhyNotBlocking());
				}
#endif

				if (TryHandleCtrlEscChord(vk, isDown, out bool blockCtrlEsc) && blockCtrlEsc)
				{
#if AK_DIAGNOSTICS
					StartMenuDiag.LogThread(vk, isDown, blocked: true, IsSlotCapturePassthrough ? "ctrl+esc-capture" : "ctrl+esc-chord");
#endif
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
#if AK_DIAGNOSTICS
						StartMenuDiag.LogThread(vk, isDown, blocked: true, "win");
#endif
						return (IntPtr)1;
					}

#if AK_DIAGNOSTICS
					StartMenuDiag.LogThread(vk, isDown, blocked: false, "win reason=" + DescribeWhyNotBlocking());
#endif
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
				bool isDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
				bool isUp = msg == WM_KEYUP || msg == WM_SYSKEYUP;
				bool hookInjected = (data.flags & LLKHF_INJECTED) != 0
					|| data.dwExtraInfo == InjectedMarker;
				bool gameForegroundForDiagnostic = IsGameForegroundCore();
				if (gameForegroundForDiagnostic
					&& (isDown || isUp)
					&& AdvancedKeybindsDiagnosticEvents.HasKeyMessageSubscribers)
				{
					DiagnosticEventPublisher.PublishHook(
						data.vkCode,
						(ushort)(data.scanCode & 0xFF),
						(data.flags & LLKHF_EXTENDED) != 0,
						isDown,
						isRepeat: false,
						hookInjected);
				}

				// Let our own injected events straight through, untouched.
				if (data.dwExtraInfo == InjectedMarker)
					return CallNextHookEx(_hookId, nCode, wParam, lParam);

				if (isDown || isUp)
				{
					global::AdvancedKeybinds.Features.DoubleTap.DoubleTapNativeInput.NotifyLowLevel(
						data.vkCode,
						(ushort)(data.scanCode & 0xFF),
						(data.flags & LLKHF_EXTENDED) != 0,
						isDown,
						isUp,
						data.time,
						ownMarker: false,
						foreground: gameForegroundForDiagnostic);
				}

				if (gameForegroundForDiagnostic)
					_lastGameForegroundMs = Environment.TickCount64;

				// 「半角／全角」 must always reach OS/IME — never swallow in the LL hook.
				// Acquisition is Microsoft keystroke messages only — do not Apply* from the hook.
				if (IsHankakuZenkakuPhysicalKey(data.vkCode, data.scanCode))
					return CallNextHookEx(_hookId, nCode, wParam, lParam);

				HandleShellBlockedEscapeKeyUp(data.vkCode, isUp);

				if (IsSlotCapturePassthrough && isDown)
				{
					uint scan = data.scanCode & 0xFF;
					string hint = IsPauseOrBreakVk(data.vkCode)
						? " Pause/Break(not NumLock)"
						: data.vkCode == VK_NUMLOCK
							? " NumLock"
							: scan == SC_NUMLOCK
								? " scan45(not NumLock)"
								: IsKanaPhysicalKey(data.vkCode, scan)
									? " Kana"
									: TryMapJisIntlScanToToken(scan, out string jisToken)
										? " " + jisToken
										: "";
					if (AkDiag.Enabled) AkDiag.Log($"Slot capture hook keydown vk=0x{data.vkCode:X2} scan=0x{scan:X2}{hint}");
				}

				if (!Win32KeyboardAcquisitionPolicy.KeystrokeMessagesOnly)
				{
					if (IsKanaPhysicalKey(data.vkCode, data.scanCode))
					{
						ApplyKanaPhysical(
							data.vkCode,
							data.scanCode,
							isDown,
							"hook vk=0x" + data.vkCode.ToString("X2")
								+ " scan=0x" + (data.scanCode & 0xFF).ToString("X2"));
					}

					ApplyJisIntlScan(data.scanCode, isDown, "hook vk=0x" + data.vkCode.ToString("X2"));

					bool hookExtended = (data.flags & LLKHF_EXTENDED) != 0;
					ApplyPhysicalKeyEvent(
						data.vkCode,
						(ushort)(data.scanCode & 0xFF),
						hookExtended,
						isDown,
						"hook vk=0x" + data.vkCode.ToString("X2"));
				}
#if AK_DIAGNOSTICS
				if (isDown && data.vkCode == VK_ESCAPE && IsControlDown())
				{
					bool suppress = ShouldBlockCtrlEscForShell()
						|| (IsSlotCapturePassthrough && IsGameForegroundCore());
					string reason = suppress ? "ctrl+esc" : "ctrl+esc reason=" + DescribeWhyNotBlocking();
					StartMenuDiag.LogHook(VK_ESCAPE, true, suppress, reason);
				}
#endif

				if (TryHandleCtrlEscChord(data.vkCode, isDown, out bool blockCtrlEsc) && blockCtrlEsc)
				{
#if AK_DIAGNOSTICS
					StartMenuDiag.LogHook(data.vkCode, isDown, blocked: true, IsSlotCapturePassthrough ? "ctrl+esc-capture" : "ctrl+esc-chord");
#endif
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
#if AK_DIAGNOSTICS
						StartMenuDiag.LogHook(winVk, isDown, blocked: true, "win");
#endif
						return (IntPtr)1;
					}

#if AK_DIAGNOSTICS
					StartMenuDiag.LogHook(winVk, isDown, blocked: false, "win reason=" + DescribeWhyNotBlocking());
#endif
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
			if (!OperatingSystem.IsWindows())
				return;

			FlushScreenshotRequests();
			FlushPendingArrowChords();
		}

		private static void RequestArrowChord(uint arrowVk, uint winVk, bool monitorMove, bool virtualDesktop)
		{
			_pendingArrowChordVk = arrowVk;
			_pendingArrowChordWinVk = winVk;
			_pendingArrowChordWithShift = monitorMove;
			_pendingArrowChordVirtualDesktop = virtualDesktop;
#if AK_DIAGNOSTICS
			WinChordDiag.Log(
				"request",
				arrowVk,
				"arrow-queued",
				"win=0x" + winVk.ToString("X")
					+ " monitor=" + monitorMove
					+ " vd=" + virtualDesktop);
#endif
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
#if AK_DIAGNOSTICS
				WinChordDiag.Log(
					"flush",
					arrowVk,
					moved ? "monitor-move-ok" : "monitor-move-fail",
					"dir=" + (toRight ? "right" : "left"));
#endif
				return;
			}

			if (virtualDesktop)
			{
				uint sent = 0;
				// RIDEV_NOHOTKEYS blocks OS Win+Ctrl+Arrow (virtual desktop). Drop it briefly for inject.
				GameWindowInputHook.RunWithOsWinHotkeysEnabled(() => {
					sent = EmitWinCtrlArrowChord(arrowVk, winVk);
				});
#if AK_DIAGNOSTICS
				WinChordDiag.Log(
					"flush",
					arrowVk,
					sent > 0 ? "vd-inject-ok" : "vd-inject-fail",
					"win=0x" + winVk.ToString("X") + " sent=" + sent);
#endif
			}
		}

		/// <summary>
		/// Moves the game window to the next/previous monitor (Win+Shift+←/→ equivalent).
		/// When Terraria is in borderless / fullscreen-like mode, also retargets resolution to that
		/// monitor so mixed-DPI / mixed-resolution setups fill the destination display.
		/// </summary>
		private static bool TryMoveGameWindowToAdjacentMonitor(bool toRight)
		{
			if (!OperatingSystem.IsWindows())
				return false;

			IntPtr hwnd = GameWindowHandle.TryGet(preferForeground: true);
			if (hwnd == IntPtr.Zero)
				hwnd = GetForegroundWindow();

			if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
			{
				if (AkDiag.Enabled) AkDiag.Log("MonitorMove aborted: no game HWND.");
				return false;
			}

			List<MonitorEntry> monitors = EnumerateMonitors();
			if (monitors.Count < 2)
			{
				if (AkDiag.Enabled) AkDiag.Log("MonitorMove aborted: monitorCount=" + monitors.Count);
				return false;
			}

			IntPtr currentHandle = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
			int currentIndex = monitors.FindIndex(m => m.Handle == currentHandle);
			if (currentIndex < 0)
			{
				if (AkDiag.Enabled) AkDiag.Log("MonitorMove aborted: current monitor not in enum list.");
				return false;
			}

			MonitorEntry current = monitors[currentIndex];
			MonitorEntry? target = FindAdjacentMonitor(monitors, current, toRight);
			if (target == null || target.Value.Handle == current.Handle)
			{
				if (AkDiag.Enabled) AkDiag.Log("MonitorMove aborted: no adjacent monitor dir=" + (toRight ? "right" : "left"));
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
			if (!OperatingSystem.IsWindows())
				return false;

			int destW = dest.Monitor.Width;
			int destH = dest.Monitor.Height;
			if (destW < 640 || destH < 480)
			{
				if (AkDiag.Enabled) AkDiag.Log("MonitorMove borderless-fit aborted: dest too small " + destW + "x" + destH);
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
				if (AkDiag.Enabled) AkDiag.Log("MonitorMove SetResolution failed: " + ex.Message);
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

			if (AkDiag.Enabled)
			{
				AkDiag.Log(
				"MonitorMove borderless-fit " + (ok ? "ok" : "SetWindowPos-fail")
				+ " to=(" + dest.Monitor.Left + "," + dest.Monitor.Top + ")"
				+ " size=" + destW + "x" + destH
				+ " monitors=" + EnumerateMonitors().Count);
			}

			if (ok)
				HoldTierHudSystem.NotifyClientSurfaceChanged("MonitorMove-borderless-fit");

			return ok;
		}

		private static bool MoveWindowKeepSize(IntPtr hwnd, MonitorEntry current, MonitorEntry dest)
		{
			if (!OperatingSystem.IsWindows())
				return false;

			if (!GetWindowRect(hwnd, out RECT windowRect))
			{
				if (AkDiag.Enabled) AkDiag.Log("MonitorMove aborted: GetWindowRect failed.");
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

			if (AkDiag.Enabled)
			{
				AkDiag.Log(
				"MonitorMove keep-size " + (ok ? "ok" : "SetWindowPos-fail")
				+ " from=(" + current.Monitor.Left + "," + current.Monitor.Top + ")"
				+ " to=(" + dest.Monitor.Left + "," + dest.Monitor.Top + ")"
				+ " monitors=" + EnumerateMonitors().Count);
			}

			if (ok)
				HoldTierHudSystem.NotifyClientSurfaceChanged("MonitorMove-keep-size");

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
				if (AkDiag.Enabled) AkDiag.Log("MonitorMove borderless style skipped: " + ex.Message);
			}
		}

		private static long GetWindowLongValue(IntPtr hwnd, int index)
		{
			if (!OperatingSystem.IsWindows())
				return 0L;

			if (IntPtr.Size == 8)
				return GetWindowLongPtr64(hwnd, index).ToInt64();
			return GetWindowLong32(hwnd, index);
		}

		private static void SetWindowLongValue(IntPtr hwnd, int index, long value)
		{
			if (!OperatingSystem.IsWindows())
				return;

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
			if (!OperatingSystem.IsWindows())
				return list;

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
#if AK_DIAGNOSTICS
				WinChordDiag.Log("flush", VK_S, "snip-launched", "ok");
#endif
				if (AkDiag.Enabled) AkDiag.Log("Launched ms-screenclip: for Win+Shift+S.");
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
			if (!OperatingSystem.IsWindows())
				return 0;

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
					if (AkDiag.Enabled) AkDiag.Log($"Win+Ctrl+Arrow SendInput sent {sent}/{batch.Length} events (error {Marshal.GetLastWin32Error()}).");
				else
					if (AkDiag.Enabled) AkDiag.Log($"Win+Ctrl+Arrow injected {sent} events arrow=0x{arrowVk:X2} ctrl=0x{ctrlVk:X2}.");

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
			if (!OperatingSystem.IsWindows())
				return false;

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
