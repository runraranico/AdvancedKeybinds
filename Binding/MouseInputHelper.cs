using System;
using System.Collections.Generic;
using AdvancedKeybinds.Core;
using AdvancedKeybinds.Patches;
using Microsoft.Xna.Framework.Input;
using Terraria;

namespace AdvancedKeybinds.Binding
{
	internal static class MouseInputHelper
	{
		private static bool _prevX1;
		private static bool _prevX2;
		private static int _prevScrollWheel;
		private static int _scrollAdvanceGameFrame = -1;
		private static readonly List<string> _frameWheelEdges = new(2);
		private static readonly List<string> _pendingWin32WheelEdges = new(4);

		// One Mouse.GetState() per game frame for button polls (wheel sampling may refresh).
		private static int _mouseStateGameFrame = -1;
		private static MouseState _cachedMouseState;
		private static long _mouseGetStateCount;

		/// <summary>Diagnostic: Mouse.GetState calls since process start (includes wheel sync).</summary>
		public static long MouseGetStateCount => _mouseGetStateCount;

		/// <summary>
		/// Force a fresh <see cref="Mouse.GetState"/> for this game frame.
		/// Needed after early-frame caching (PlayerInput) so late systems (hidden menu shortcuts)
		/// still see mid-frame button edges while IngameOptions / FancyUI are open.
		/// </summary>
		public static void RefreshFrameMouseState()
		{
			GetMouseStateFresh();
		}

		private static MouseState GetMouseStateCached()
		{
			int frame = (int)Main.GameUpdateCount;
			if (frame != _mouseStateGameFrame)
			{
				_mouseStateGameFrame = frame;
				_cachedMouseState = Mouse.GetState();
				_mouseGetStateCount++;
			}

			return _cachedMouseState;
		}

		/// <summary>Force refresh (focus sync / wheel baseline).</summary>
		private static MouseState GetMouseStateFresh()
		{
			_mouseStateGameFrame = (int)Main.GameUpdateCount;
			_cachedMouseState = Mouse.GetState();
			_mouseGetStateCount++;
			return _cachedMouseState;
		}

		/// <summary>
		/// Win32 <c>WM_MOUSEWHEEL</c> / <c>GET_WHEEL_DELTA_WPARAM</c>: positive = away from user (奥回し).
		/// </summary>
		internal static string WheelDeltaToToken(int wheelDelta)
			=> wheelDelta > 0 ? StableKeyToken.MouseWheelDownToken : StableKeyToken.MouseWheelUpToken;

		/// <summary>Called from the game window WndProc (before Terraria UI consumes the message).</summary>
		public static void OnWin32WheelDelta(short wheelDelta)
		{
			if (wheelDelta == 0)
				return;

			string token = WheelDeltaToToken(wheelDelta);
			_pendingWin32WheelEdges.Add(token);
			if (AkDiag.Enabled)
			{
				AkDiag.Log(
					"Wheel Win32 edge " + token
					+ " delta=" + wheelDelta
					+ " capturePassthrough=" + WindowsKeySuppressor.IsSlotCapturePassthrough
					+ " wndHook=" + GameWindowInputHook.IsInstalledForDiag
					+ " " + FormatWheelDiag());
			}
		}

		private static void MergePendingWin32WheelEdges()
		{
			for (int i = 0; i < _pendingWin32WheelEdges.Count; i++)
			{
				string edge = _pendingWin32WheelEdges[i];
				bool exists = false;
				for (int j = 0; j < _frameWheelEdges.Count; j++)
				{
					if (string.Equals(_frameWheelEdges[j], edge, StringComparison.OrdinalIgnoreCase))
					{
						exists = true;
						break;
					}
				}

				if (!exists)
					_frameWheelEdges.Add(edge);
			}

			_pendingWin32WheelEdges.Clear();
		}

		private static void TrySampleFnaWheelDelta()
		{
			if (!Main.hasFocus)
			{
				_prevScrollWheel = GetMouseStateFresh().ScrollWheelValue;
				return;
			}

			MouseState state = GetMouseStateFresh();
			int now = state.ScrollWheelValue;
			int delta = now - _prevScrollWheel;
			_prevScrollWheel = now;
			if (delta == 0)
				return;

			string token = WheelDeltaToToken(delta);
			bool exists = false;
			for (int j = 0; j < _frameWheelEdges.Count; j++)
			{
				if (string.Equals(_frameWheelEdges[j], token, StringComparison.OrdinalIgnoreCase))
				{
					exists = true;
					break;
				}
			}

			if (!exists)
				_frameWheelEdges.Add(token);

			if (AkDiag.Enabled)
				AkDiag.Log("Wheel FNA edge " + token + " delta=" + delta + " " + FormatWheelDiag());
		}

		/// <summary>AKDIAG one-liner for wheel latch / frame merge state.</summary>
		public static string FormatWheelDiag()
			=> "wheelFrame=[" + string.Join(",", _frameWheelEdges) + "]"
				+ " wheelPending=" + _pendingWin32WheelEdges.Count
				+ " advFrame=" + _scrollAdvanceGameFrame
				+ " gameFrame=" + Main.GameUpdateCount
				+ " fnaScroll=" + _prevScrollWheel;

		/// <summary>
		/// Physical mouse buttons from the OS/device. Prefer these over Main.mouseLeft/Right/Middle,
		/// which follow remapped actions (e.g. 対話・動作) rather than the hardware button.
		/// Mouse4/5 use XButton1/XButton2 the same way — do not rely on Main.mouseX1/X2 fields
		/// (those names are not reliable Terraria API; reflection always failed → never detected).
		/// </summary>
		public static bool IsPhysicalLeftDown()
			=> GetMouseStateCached().LeftButton == ButtonState.Pressed;

		public static bool IsPhysicalRightDown()
			=> GetMouseStateCached().RightButton == ButtonState.Pressed;

		public static bool IsPhysicalMiddleDown()
			=> GetMouseStateCached().MiddleButton == ButtonState.Pressed;

		public static bool IsMouseX1Down()
		{
			bool down = GetMouseStateCached().XButton1 == ButtonState.Pressed;
			if (AkDiag.Enabled && down != _prevX1)
			{
				_prevX1 = down;
				AkDiag.Log("Mouse4 XButton1 " + (down ? "down" : "up"));
			}
			return down;
		}

		public static bool IsMouseX2Down()
		{
			bool down = GetMouseStateCached().XButton2 == ButtonState.Pressed;
			if (AkDiag.Enabled && down != _prevX2)
			{
				_prevX2 = down;
				AkDiag.Log("Mouse5 XButton2 " + (down ? "down" : "up"));
			}
			return down;
		}

		/// <summary>
		/// Merges Win32 wheel edges and FNA fallback once per game frame.
		/// Edges persist for the whole frame so multiple SlotCapture polls can see the same notch.
		/// </summary>
		public static void AdvanceScrollWheel()
		{
			int frame = (int)Main.GameUpdateCount;
			if (frame != _scrollAdvanceGameFrame)
			{
				_scrollAdvanceGameFrame = frame;
				_frameWheelEdges.Clear();
				TrySampleFnaWheelDelta();
			}

			MergePendingWin32WheelEdges();
		}

		public static void AppendFrameWheelEdges(List<string> edges)
		{
			AdvanceScrollWheel();
			for (int i = 0; i < _frameWheelEdges.Count; i++)
			{
				string edge = _frameWheelEdges[i];
				bool exists = false;
				for (int j = 0; j < edges.Count; j++)
				{
					if (string.Equals(edges[j], edge, StringComparison.OrdinalIgnoreCase))
					{
						exists = true;
						break;
					}
				}

				if (!exists)
					edges.Add(edge);
			}
		}

		public static void AppendFrameWheelEdges(HashSet<string> edges)
		{
			AdvanceScrollWheel();
			for (int i = 0; i < _frameWheelEdges.Count; i++)
				edges.Add(_frameWheelEdges[i]);
		}

		/// <summary>Resync after focus loss so refocus does not emit a spurious wheel edge.</summary>
		public static void SyncScrollWheelBaseline()
		{
			int clearedPending = _pendingWin32WheelEdges.Count;
			_prevScrollWheel = GetMouseStateFresh().ScrollWheelValue;
			_frameWheelEdges.Clear();
			_pendingWin32WheelEdges.Clear();
			_scrollAdvanceGameFrame = (int)Main.GameUpdateCount;
			if (AkDiag.Enabled && (clearedPending > 0 || WindowsKeySuppressor.IsSlotCapturePassthrough))
			{
				if (AkDiag.Enabled)
				{
					AkDiag.Log(
					"Wheel baseline sync"
					+ " clearedPending=" + clearedPending
					+ " capturePassthrough=" + WindowsKeySuppressor.IsSlotCapturePassthrough
					+ " " + FormatWheelDiag());
				}
			}
		}

		/// <summary>
		/// True when the OS cursor is over the game client area while the game is focused.
		/// Prefer Win32 cursor+client-rect: FNA <see cref="Mouse.GetState"/> often clamps to the
		/// last in-window point when clicking another window (deactivate / SS), which wrongly
		/// looked "inside" and cancelled purple slot capture.
		/// Do not use <see cref="Main.mouseLeft"/> here — the combo panel often leaves it false
		/// even for in-window clicks, which would block LeftClick tips and Mouse binds.
		/// </summary>
		public static bool IsCursorInsideGameClient()
		{
			if (!Main.hasFocus)
				return false;

			// Deactivate click: foreground already left us while hasFocus is still true for a frame.
			if (OperatingSystem.IsWindows() && !GameWindowHandle.IsGameProcessForeground())
				return false;

			// Win32 cursor is not FNA-clamped to the last in-window point on other-window clicks.
			if (OperatingSystem.IsWindows()
				&& GameWindowHandle.TryGet(preferForeground: false) != IntPtr.Zero)
				return GameWindowHandle.IsOsCursorInsideClient();

			MouseState state = GetMouseStateCached();
			return state.X >= 0
				&& state.Y >= 0
				&& state.X < Main.screenWidth
				&& state.Y < Main.screenHeight;
		}
	}
}
