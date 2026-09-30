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
#if AK_DOUBLETAP_DIAGNOSTICS
			DoubleTapTrace.Native("Wnd:到着", vk, scan, extended, true, wasKeyDown, double.NaN, true);
#endif
			// Passive diag only: before Session.IsActive early-return; never Observe/End/suppress.
			try { TryPassiveDiagWindow(vk, scan, extended, wasKeyDown); }
			catch { /* never escape; never Session.End */ }

			try
			{
				if (!Session.IsActive)
					return;

				if (RefreshJapaneseContext())
				{
#if AK_DOUBLETAP_DIAGNOSTICS
					DoubleTapTrace.State("Wnd:日本語配列状態変更により終了");
#endif
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
#if AK_DOUBLETAP_DIAGNOSTICS
					DoubleTapTrace.Native("Wnd:時刻不正", vk, scan, extended, true, wasKeyDown, eventMs, true);
#endif
					// Diag only: record a bad timestamp without changing End/no-Observe.
					try
					{
						if (DoubleTapDiagFeed.HasSubscribers)
						{
							DoubleTapDiagReason rejectReason = DoubleTapDiagPolicy.ClassifyWindowObserveRejectReason(
								wasKeyDown, own, sent, blocked, validTimestamp: false);
							DoubleTapDiagPolicy.TryPublishSessionObserveOutcome(
								vk, scan, DoubleTapDiagSource.Window, extended,
								timestampMs: -1L, Main.GameUpdateCount,
								Session.IsActive, WindowAvailable, seedHeld: false,
								accepted: false, isDown: true, rejectReason: rejectReason);
						}
					}
					catch { /* never escape; never alter End */ }
					Session.End();
					return;
				}

				bool accepted = Session.ObserveWindow(
					vk,
					scan,
					extended,
					wasKeyDown,
					own,
					sent,
					Main.GameUpdateCount,
					eventMs,
					blocked);
#if AK_DOUBLETAP_DIAGNOSTICS
				DoubleTapTrace.Native(accepted ? "Wnd:受付" : "Wnd:却下 own=" + own + " sent=" + sent,
					vk, scan, extended, true, wasKeyDown, eventMs, true);
#endif
				try
				{
					if (DoubleTapDiagFeed.HasSubscribers)
					{
						long timestampMs = -1;
						if (!double.IsNaN(eventMs) && !double.IsInfinity(eventMs) && eventMs >= 0.0)
							timestampMs = (long)eventMs;
						if (accepted)
						{
							DoubleTapDiagPolicy.TryPublishSessionObserveOutcome(
								vk, scan, DoubleTapDiagSource.Window, extended,
								timestampMs, Main.GameUpdateCount,
								Session.IsActive, WindowAvailable, seedHeld: false,
								accepted: true, isDown: true);
						}
						else
						{
							DoubleTapDiagReason rejectReason = DoubleTapDiagPolicy.ClassifyWindowObserveRejectReason(
								wasKeyDown, own, sent, blocked, validTimestamp: true);
							DoubleTapDiagPolicy.TryPublishSessionObserveOutcome(
								vk, scan, DoubleTapDiagSource.Window, extended,
								timestampMs, Main.GameUpdateCount,
								Session.IsActive, WindowAvailable, seedHeld: false,
								accepted: false, isDown: true, rejectReason: rejectReason);
						}
					}
				}
				catch { /* never escape; never Session.End */ }
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
#if AK_DOUBLETAP_DIAGNOSTICS
			DoubleTapTrace.Native("LL:到着", vk, scan, extended, isDown, false, double.NaN, foreground);
#endif
			try
			{
				if (!isDown && !isUp)
					return;

				bool blocked = !foreground
					|| BindingInputEngine.IsDoubleTapEnvironmentBlocked();

				// Observer LL pulse + raw candidate (passive; before Arrived helper).
				try { TryPassiveDiagLowLevelPulseAndRaw(vk, scan, extended, isDown, ownMarker, foreground, blocked); }
				catch { /* never escape; never Session.End */ }

				// After blocked is known; down-only; own/non-foreground excluded in helper.
				try { TryPassiveDiagLowLevel(vk, scan, extended, isDown, ownMarker, foreground, blocked); }
				catch { /* never escape; never Session.End */ }

				if (!DoubleTapObserveSourcePolicy.TryLowLevelKey(
					vk, scan, extended, ownInjected: false, out _))
				{
					if (blocked)
						Session.End();
					return;
				}

				double eventMs = double.NaN;
				long now = Environment.TickCount64;
				if (!DoubleTapObserveSourcePolicy.TryEventTimestamp(llTime, now, out eventMs))
					eventMs = double.NaN;

				bool accepted = Session.ObserveLowLevel(
					vk,
					scan,
					extended,
					down: isDown,
					ownInjected: ownMarker,
					Main.GameUpdateCount,
					eventMs,
					blocked);
#if AK_DOUBLETAP_DIAGNOSTICS
				DoubleTapTrace.Native(accepted ? "LL:受付" : "LL:却下 own=" + ownMarker,
					vk, scan, extended, isDown, false, eventMs, foreground);
#endif
				try
				{
					if (DoubleTapDiagFeed.HasSubscribers)
					{
						bool sessionActive = Session.IsActive;
						bool seedHeld = false;
						if (sessionActive)
						{
							DoubleTapDiagKey diagKey = DoubleTapDiagPolicy.Classify(vk, scan);
							seedHeld = IsLlSeedHeldForDiagKey(diagKey, Session.SeedHeldMask);
						}

						long timestampMs = -1;
						if (!double.IsNaN(eventMs) && !double.IsInfinity(eventMs) && eventMs >= 0.0)
							timestampMs = (long)eventMs;
						DoubleTapDiagPolicy.TryPublishSessionObserveOutcome(
							vk,
							scan,
							DoubleTapDiagSource.LowLevel,
							extended,
							timestampMs,
							Main.GameUpdateCount,
							sessionActive,
							LowLevelAvailable,
							seedHeld,
							accepted,
							isDown);
					}
				}
				catch { /* never escape; never Session.End */ }
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

		/// <summary>
		/// Diag-only Window Arrived. No subscribers / non-target → no further work.
		/// Does not Observe/End/suppress. Rejected side-channel omitted (Arrived only).
		/// Invalid diag timestamp uses -1 (does not alter product Observe timestamps).
		/// </summary>
		static void TryPassiveDiagWindow(uint vk, ushort scan, bool extended, bool wasKeyDown)
		{
			if (!DoubleTapDiagFeed.HasSubscribers)
				return;

			if (DoubleTapDiagPolicy.Classify(vk, scan) == DoubleTapDiagKey.None)
				return;

			bool envBlocked = BindingInputEngine.IsDoubleTapEnvironmentBlocked();
			bool focused = Main.hasFocus;
			if (envBlocked || !focused)
				return;

			bool own = WindowsKeySuppressor.IsOwnInjectedMarker(GetMessageExtraInfo());
			bool sent = InSendMessageEx(IntPtr.Zero) != 0;
			if (own || sent)
				return;

			long timestampMs = -1;
			long now = Environment.TickCount64;
			uint msgTime = unchecked((uint)GetMessageTime());
			if (DoubleTapObserveSourcePolicy.TryEventTimestamp(msgTime, now, out double eventMs)
				&& !double.IsNaN(eventMs)
				&& !double.IsInfinity(eventMs)
				&& eventMs >= 0.0)
			{
				timestampMs = (long)eventMs;
			}

			// Ingress for first-down and wasKeyDown repeats (gates already passed; no timestamp work on blocked).
			DoubleTapDiagPolicy.TryPublishWindowIngress(
				vk,
				scan,
				extended,
				isDown: true,
				wasKeyDown,
				timestampMs,
				Main.GameUpdateCount,
				sessionActive: Session.IsActive,
				blockedOrText: false,
				foreground: true);

			// Preserve: TargetArrival only on first down (not auto-repeat).
			if (wasKeyDown)
				return;

			DoubleTapDiagPolicy.TryPublishTargetArrival(
				vk,
				scan,
				DoubleTapDiagSource.Window,
				extended,
				timestampMs: timestampMs,
				update: Main.GameUpdateCount,
				sessionActive: Session.IsActive,
				sourceAvailable: WindowAvailable,
				seedHeld: false,
				blockedOrChat: false,
				foreground: true,
				observationAllowed: true);
		}

		/// <summary>
		/// Diag-only Window key-up ingress. Passive only; not wired to WndProc until separate approval.
		/// No subscribers → immediate return. Does not Observe/End/suppress or change WndProc returns.
		/// </summary>
		public static void NotifyWindowKeyUpForDiag(uint vk, ushort scan, bool extended, bool wasKeyDown)
		{
			try
			{
				if (!DoubleTapDiagFeed.HasSubscribers)
					return;

				if (DoubleTapDiagPolicy.Classify(vk, scan) == DoubleTapDiagKey.None)
					return;

				bool envBlocked = BindingInputEngine.IsDoubleTapEnvironmentBlocked();
				bool focused = Main.hasFocus;
				if (envBlocked || !focused)
					return;

				bool own = WindowsKeySuppressor.IsOwnInjectedMarker(GetMessageExtraInfo());
				bool sent = InSendMessageEx(IntPtr.Zero) != 0;
				if (own || sent)
					return;

				long timestampMs = -1;
				long now = Environment.TickCount64;
				uint msgTime = unchecked((uint)GetMessageTime());
				if (DoubleTapObserveSourcePolicy.TryEventTimestamp(msgTime, now, out double eventMs)
					&& !double.IsNaN(eventMs)
					&& !double.IsInfinity(eventMs)
					&& eventMs >= 0.0)
				{
					timestampMs = (long)eventMs;
				}

				DoubleTapDiagPolicy.TryPublishWindowIngress(
					vk,
					scan,
					extended,
					isDown: false,
					wasKeyDown,
					timestampMs,
					Main.GameUpdateCount,
					sessionActive: Session.IsActive,
					blockedOrText: false,
					foreground: true);
			}
			catch
			{
				/* never escape to input / WndProc */
			}
		}

		/// <summary>
		/// Diag-only LL Observer feed: anonymous Pulse for all keys, RawCandidate for DT targets.
		/// No subscribers → immediate return. Does not Observe/End/suppress/Queue.
		/// ownMarker suppresses RawCandidate only (Pulse still allowed when not blocked).
		/// Invalid diag timestamp uses -1 (does not alter product Observe timestamps).
		/// </summary>
		static void TryPassiveDiagLowLevelPulseAndRaw(
			uint vk,
			ushort scan,
			bool extended,
			bool isDown,
			bool ownMarker,
			bool foreground,
			bool blocked)
		{
			if (!DoubleTapDiagFeed.HasSubscribers)
				return;

			if (blocked || !foreground)
				return;

			long timestampMs = -1;
			double eventMs = NowMs;
			if (!double.IsNaN(eventMs) && !double.IsInfinity(eventMs) && eventMs >= 0.0)
				timestampMs = (long)eventMs;

			ulong update = Main.GameUpdateCount;
			bool sessionActive = Session.IsActive;

			DoubleTapDiagPolicy.TryPublishLowLevelPulse(
				timestampMs,
				update,
				sessionActive,
				blockedOrChat: blocked,
				foreground: foreground);

			DoubleTapDiagPolicy.TryPublishLowLevelRawCandidate(
				vk,
				scan,
				extended,
				isDown,
				timestampMs,
				update,
				sessionActive,
				ownMarker,
				blockedOrChat: blocked,
				foreground: foreground);
		}

		/// <summary>
		/// Diag-only LL down Arrived for DT5 targets. Uses caller blocked/foreground.
		/// No subscribers / non-target / blocked → no further work. Does not Observe/End/suppress.
		/// Invalid diag timestamp uses -1 (does not alter product Observe timestamps).
		/// </summary>
		static void TryPassiveDiagLowLevel(
			uint vk,
			ushort scan,
			bool extended,
			bool isDown,
			bool ownMarker,
			bool foreground,
			bool blocked)
		{
			if (!DoubleTapDiagFeed.HasSubscribers)
				return;

			if (DoubleTapDiagPolicy.Classify(vk, scan) == DoubleTapDiagKey.None)
				return;

			if (!isDown || ownMarker || !foreground)
				return;

			if (blocked)
				return;

			long timestampMs = -1;
			double eventMs = NowMs;
			if (!double.IsNaN(eventMs) && !double.IsInfinity(eventMs) && eventMs >= 0.0)
				timestampMs = (long)eventMs;

			DoubleTapDiagPolicy.TryPublishTargetArrival(
				vk,
				scan,
				DoubleTapDiagSource.LowLevel,
				extended,
				timestampMs: timestampMs,
				update: Main.GameUpdateCount,
				sessionActive: Session.IsActive,
				sourceAvailable: LowLevelAvailable,
				seedHeld: false,
				blockedOrChat: false,
				foreground: true,
				observationAllowed: true);
		}

		static int FailSafeSeed()
		{
			Session.EndAndForgetPhysicalState();
			return FailSafeHeldMask;
		}

		/// <summary>
		/// LL seed bits match Session: LWin=1&lt;&lt;0, RWin=1&lt;&lt;1, Caps=1&lt;&lt;2.
		/// NonConvert/Kana are not LL seed targets → false.
		/// </summary>
		static bool IsLlSeedHeldForDiagKey(DoubleTapDiagKey key, int seedHeldMask)
		{
			int bit = 0;
			if (key == DoubleTapDiagKey.LWin)
				bit = 1 << 0;
			else if (key == DoubleTapDiagKey.RWin)
				bit = 1 << 1;
			else if (key == DoubleTapDiagKey.CapsLock)
				bit = 1 << 2;
			else
				return false;

			return (seedHeldMask & bit) != 0;
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
