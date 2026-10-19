using System;
using System.Runtime.InteropServices;
using AdvancedKeybinds.Binding;
using AdvancedKeybinds.Patches;
using Terraria;

namespace AdvancedKeybinds.Features.DoubleTap
{
	/// <summary>
	/// Product OS boundary for DT keyboard observe. Session is pure 128-ring.
	/// Does not change normal input Apply/Queue/return/hook install.
	/// No timer / file / log / Task.
	/// </summary>
	internal static class DoubleTapNativeInput
	{
		public const int DefaultCapacity = 128;
		const int MaskAll = 0x1F;
		const int FailSafeHeldMask = 0x1F;

		const uint VkLWin = 0x5B;
		const uint VkRWin = 0x5C;
		const uint VkCapital = 0x14;
		const uint VkNumLock = 0x90;
		const uint VkScroll = 0x91;

		static readonly DoubleTapObservationSession SessionInstance =
			new DoubleTapObservationSession(DefaultCapacity);

		static bool _jpPhysical;
		static bool _jpLayout;

		public static DoubleTapObservationSession Session => SessionInstance;

		public static bool IsWindows
		{
			get
			{
				try { return OperatingSystem.IsWindows(); }
				catch { return false; }
			}
		}

		public static bool WindowAvailable
		{
			get
			{
				try { return GameWindowInputHook.IsInstalledForDiag; }
				catch { return false; }
			}
		}

		public static bool LowLevelAvailable
		{
			get
			{
				try { return WindowsKeySuppressor.IsInstalled; }
				catch { return false; }
			}
		}

		public static bool ThreadWindowsAvailable
		{
			get
			{
				try
				{
					return WindowsKeySuppressor.IsThreadKeyboardHookInstalled;
				}
				catch
				{
					return false;
				}
			}
		}

		public static void NotifyThreadWindowsKey(uint vk, ushort scan, bool extended, bool isDown, bool isUp, bool wasKeyDown, bool foreground)
		{
			try
			{
				if (vk != 91 && vk != 92)
					return;
				if (isDown == isUp)
					return;
				if (isDown && wasKeyDown)
					return;
				if (WindowsKeySuppressor.IsOwnInjectedMarker(GetMessageExtraInfo()))
					return;
				bool blocked = !foreground || BindingInputEngine.IsDoubleTapEnvironmentBlocked();
				long now = Environment.TickCount64;
				uint msg = unchecked((uint)GetMessageTime());
				double eventMs;
				if (!DoubleTapObserveSourcePolicy.TryEventTimestamp(msg, now, out eventMs))
					eventMs = double.NaN;
				Session.ObserveLowLevel(vk, scan, extended, isDown, false, Main.GameUpdateCount, eventMs, blocked);
			}
			catch
			{
				try
				{
					Session.End();
				}
				catch
				{
				}
			}
		}

		public static bool JapanesePhysical => _jpPhysical;
		public static bool JapaneseLayout => _jpLayout;

		public static double NowMs
		{
			get
			{
				try { return Environment.TickCount64; }
				catch { return double.NaN; }
			}
		}

		/// <summary>
		/// Begin-only. Refreshes JP cache. GetAsync high-bit for 5 keys; any failure → mask 0x1F (held / no-fire bias).
		/// </summary>
		public static int ReadInitialHeldMask()
		{
			try
			{
				RefreshJapaneseContext();
				if (!OperatingSystem.IsWindows())
					return FailSafeSeed();

				int mask = 0;
				if (!TryAsyncHighBit(VkLWin, out bool lWin)) return FailSafeSeed();
				if (!TryAsyncHighBit(VkRWin, out bool rWin)) return FailSafeSeed();
				if (!TryAsyncHighBit(VkCapital, out bool caps)) return FailSafeSeed();
				if (!TryAsyncHighBit(VkNumLock, out bool num)) return FailSafeSeed();
				if (!TryAsyncHighBit(VkScroll, out bool scroll)) return FailSafeSeed();

				if (lWin) mask |= 1 << 0;
				if (rWin) mask |= 1 << 1;
				if (caps) mask |= 1 << 2;
				if (num) mask |= 1 << 3;
				if (scroll) mask |= 1 << 4;
				return mask & MaskAll;
			}
			catch
			{
				return FailSafeSeed();
			}
		}

		/// <summary>WndProc path. Game thread only. Inactive → early return.</summary>
		public static void NotifyWindow(uint vk, ushort scan, bool extended, bool wasKeyDown)
		{
			try
			{
				if (!Session.IsActive)
					return;

				if (RefreshJapaneseContext())
				{
					Session.End();
					return;
				}

				bool own = WindowsKeySuppressor.IsOwnInjectedMarker(GetMessageExtraInfo());
				bool sent = InSendMessageEx(IntPtr.Zero) != 0;
				bool blocked = BindingInputEngine.IsDoubleTapEnvironmentBlocked();

				double eventMs = double.NaN;
				long now = Environment.TickCount64;
				uint msgTime = unchecked((uint)GetMessageTime());
				if (!DoubleTapObserveSourcePolicy.TryEventTimestamp(msgTime, now, out eventMs))
				{
					Session.End();
					return;
				}

				Session.ObserveWindow(
					vk,
					scan,
					extended,
					wasKeyDown,
					own,
					sent,
					Main.GameUpdateCount,
					eventMs,
					blocked);
			}
			catch
			{
				try { Session.End(); }
				catch { /* never escape to WndProc */ }
			}
		}

		/// <summary>
		/// LL hook path. No HKL / GetAsync. Non-LL5 early return; if blocked → End.
		/// LL5 always Observe (held bits even when inactive). foreground false → blocked.
		/// </summary>
		public static void NotifyLowLevel(
			uint vk,
			ushort scan,
			bool extended,
			bool isDown,
			bool isUp,
			uint llTime,
			bool ownMarker,
			bool foreground)
		{
			try
			{
				if (!isDown && !isUp)
					return;

				bool blocked = !foreground
					|| BindingInputEngine.IsDoubleTapEnvironmentBlocked();

				if (!DoubleTapObserveSourcePolicy.TryLowLevelKey(
					vk, scan, extended, ownInjected: false, out _))
				{
					if (blocked)
						Session.End();
					return;
				}

				if (foreground && !blocked && isDown && (vk == 91 || vk == 92) && ThreadWindowsAvailable)
					return;

				double eventMs = double.NaN;
				long now = Environment.TickCount64;
				if (!DoubleTapObserveSourcePolicy.TryEventTimestamp(llTime, now, out eventMs))
					eventMs = double.NaN;

				Session.ObserveLowLevel(
					vk,
					scan,
					extended,
					down: isDown,
					ownInjected: ownMarker,
					Main.GameUpdateCount,
					eventMs,
					blocked);
			}
			catch
			{
				try { Session.End(); }
				catch { /* never escape to LL callback */ }
			}
		}

		/// <summary>Returns true when cached JP physical/layout changed.</summary>
		static bool RefreshJapaneseContext()
		{
			bool phys = false;
			bool layout = false;
			if (OperatingSystem.IsWindows())
			{
				phys = KeyboardLayoutHelper.IsJapanesePhysicalKeyboard();
				layout = KeyboardLayoutHelper.IsJapaneseKeyboardLayout();
			}

			bool changed = phys != _jpPhysical || layout != _jpLayout;
			_jpPhysical = phys;
			_jpLayout = layout;
			return changed;
		}


		static int FailSafeSeed()
		{
			Session.EndAndForgetPhysicalState();
			return FailSafeHeldMask;
		}

		static bool TryAsyncHighBit(uint vk, out bool down)
		{
			down = false;
			try
			{
				down = (GetAsyncKeyState((int)vk) & 0x8000) != 0;
				return true;
			}
			catch
			{
				return false;
			}
		}

		[DllImport("user32.dll")]
		static extern short GetAsyncKeyState(int vKey);

		[DllImport("user32.dll")]
		static extern int GetMessageTime();

		[DllImport("user32.dll")]
		static extern IntPtr GetMessageExtraInfo();

		[DllImport("user32.dll")]
		static extern uint InSendMessageEx(IntPtr lpReserved);
	}
}
