using System.Collections.Generic;
using AdvancedKeybinds.Core;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace AdvancedKeybinds.Binding
{
	/// <summary>
	/// Independent GamePad subsystem: <see cref="GamePad.GetState"/> → canonical <c>Pad:</c> tokens.
	/// Advanced once per <c>PlayerInput.UpdateInput</c> via <see cref="AdvanceInputTick"/>.
	/// Does not depend on <see cref="FnaKeyboardProvider"/> internals. No Win32 / Steam Input / RawInput.
	/// Stick-direction tokens use Runtime thresholds in Held; Capture uses <see cref="GamePadStickCaptureArming"/>.
	///
	/// Activation is gated by <see cref="GamePadSubsystemRuntime"/>: while inactive nothing here
	/// touches FNA and every query answers from a cleared state, so keyboard/mouse-only sessions
	/// pay no gamepad cost. While active the tick runs against two pooled snapshots and a bit mask,
	/// so a normal frame allocates nothing.
	/// </summary>
	internal static class FnaGamePadProvider
	{
		private static readonly PlayerIndex[] Indices = {
			PlayerIndex.One, PlayerIndex.Two, PlayerIndex.Three, PlayerIndex.Four
		};

		private static int _inputTickEpoch;

		// Fixed A/B pair — advanced by swapping references, never by allocating.
		private static readonly GamePadDigitalSnapshot _snapshotA = new();
		private static readonly GamePadDigitalSnapshot _snapshotB = new();
		private static GamePadDigitalSnapshot _previous = _snapshotA;
		private static GamePadDigitalSnapshot _current = _snapshotB;

		private static ulong _justPressed;
		private static ulong _justReleased;
		private static bool _hasAdvanced;

		/// <summary>Own tick counter — independent of keyboard epoch / <c>Main.GameUpdateCount</c>.</summary>
		internal static int InputTickEpoch => _inputTickEpoch;

		internal static PlayerIndex? ActivePlayerIndex => _current.ActivePlayerIndex;

		internal static bool IsConnected => _current.IsConnected;

		internal static GamePadDigitalSnapshot Current => _current;

		internal static GamePadDigitalSnapshot Previous => _previous;

		/// <summary>True when this subsystem is polling FNA. False means every query answers false.</summary>
		internal static bool IsSubsystemActive => GamePadSubsystemRuntime.IsActive;

		/// <summary>
		/// Call once at the start of each <c>PlayerInput.UpdateInput</c> detour (alongside keyboard advance).
		/// </summary>
		internal static void AdvanceInputTick()
		{
			unchecked
			{
				_inputTickEpoch++;
			}

			_hasAdvanced = true;

			bool active = GamePadSubsystemRuntime.Advance(out bool requiresFlush);

			// Swap roles instead of allocating: last tick's current becomes this tick's previous.
			GamePadDigitalSnapshot target = _previous;
			_previous = _current;
			_current = target;

			if (!active)
			{
				// Deactivating must not leave a button latched as held forever.
				if (requiresFlush)
					_previous.Clear();

				target.Clear();
				_justPressed = GamePadDigitalBits.None;
				_justReleased = GamePadDigitalBits.None;
				return;
			}

			ulong previousDigital = _previous.Digital;
			CaptureInto(target, previousDigital);

			_justPressed = GamePadDigitalBits.RisingEdges(previousDigital, target.Digital);
			_justReleased = GamePadDigitalBits.FallingEdges(previousDigital, target.Digital);
		}

		internal static void Reset()
		{
			_inputTickEpoch = 0;
			_snapshotA.Clear();
			_snapshotB.Clear();
			_previous = _snapshotA;
			_current = _snapshotB;
			_justPressed = GamePadDigitalBits.None;
			_justReleased = GamePadDigitalBits.None;
			_hasAdvanced = false;
			GamePadSubsystemRuntime.Reset();
			GamePadStickCaptureArming.EndSession();
		}

		public static bool IsDown(string token)
		{
			if (!GamePadDigitalBits.TryGetBit(token, out ulong bit))
				return false;

			EnsureAdvancedAtLeastOnce();
			return GamePadDigitalBits.IsDown(_current.Digital, bit);
		}

		public static bool WasJustPressed(string token)
		{
			if (!GamePadDigitalBits.TryGetBit(token, out ulong bit))
				return false;

			EnsureAdvancedAtLeastOnce();
			return GamePadDigitalBits.IsDown(_justPressed, bit);
		}

		public static bool WasJustReleased(string token)
		{
			if (!GamePadDigitalBits.TryGetBit(token, out ulong bit))
				return false;

			EnsureAdvancedAtLeastOnce();
			return GamePadDigitalBits.IsDown(_justReleased, bit);
		}

		public static void SupplementHeld(HashSet<string> pressed)
		{
			if (pressed == null)
				return;

			EnsureAdvancedAtLeastOnce();
			if (_current.Digital == GamePadDigitalBits.None)
				return;

			GamePadSubsystemRuntime.TokenSetBuilds++;
			GamePadDigitalBits.AppendTokens(_current.Digital, pressed);
		}

		/// <summary>
		/// Rising edges for the current input tick (capture latch). Safe to call multiple times per tick.
		/// </summary>
		public static void AppendFrameEdges(List<string> edges)
		{
			if (edges == null)
				return;

			EnsureAdvancedAtLeastOnce();

			// Runtime stick-direction edges must not auto-register in ComboPanel (Capture uses CaptureArming).
			GamePadDigitalBits.AppendTokensDistinct(_justPressed, edges, skipStickDirections: true);
		}

		/// <summary>
		/// True when any digital Pad:* rose this input tick.
		/// Used so ComboPanel mouse short-circuit does not steal L1→MouseRight (etc.) before SlotCapture.
		/// </summary>
		internal static bool HasAnyDigitalJustPressed()
		{
			EnsureAdvancedAtLeastOnce();
			return _justPressed != GamePadDigitalBits.None;
		}

		/// <summary>Temporary diagnostics — change-only callers.</summary>
		internal static void CopyJustPressed(List<string> dest)
		{
			if (dest == null)
				return;
			EnsureAdvancedAtLeastOnce();
			GamePadDigitalBits.AppendTokens(_justPressed, dest);
		}

		/// <summary>Temporary diagnostics — change-only callers.</summary>
		internal static void CopyJustReleased(List<string> dest)
		{
			if (dest == null)
				return;
			EnsureAdvancedAtLeastOnce();
			GamePadDigitalBits.AppendTokens(_justReleased, dest);
		}

		private static void EnsureAdvancedAtLeastOnce()
		{
			if (_hasAdvanced)
				return;
			AdvanceInputTick();
		}

		private static void CaptureInto(GamePadDigitalSnapshot target, ulong previousDigital)
		{
			for (int i = 0; i < Indices.Length; i++)
			{
				PlayerIndex index = Indices[i];
				GamePadState state;
				try
				{
					GamePadSubsystemRuntime.GetStateCalls++;
					state = GamePad.GetState(index);
				}
				catch
				{
					continue;
				}

				if (!state.IsConnected)
					continue;

				GamePadRawFrame raw = ReadRawFrame(state);
				ulong digital = GamePadDigitalFrameResolver.Resolve(raw, previousDigital);
				target.SetConnected(index, raw, digital);
				GamePadSubsystemRuntime.SnapshotBuilds++;
				return;
			}

			target.Clear();
		}

		private const Buttons Paddle1Ext = (Buttons)0x00010000;
		private const Buttons Paddle2Ext = (Buttons)0x00020000;
		private const Buttons Paddle3Ext = (Buttons)0x00040000;
		private const Buttons Paddle4Ext = (Buttons)0x00080000;

		/// <summary>
		/// FNA state → plain button bits. Triggers and stick directions are intentionally left out:
		/// both need the previous tick and are resolved by <see cref="GamePadDigitalFrameResolver"/>.
		/// </summary>
		private static GamePadRawFrame ReadRawFrame(GamePadState state)
		{
			ulong buttons = GamePadDigitalBits.None;

			if (state.IsButtonDown(Buttons.A)) buttons |= GamePadDigitalBits.A;
			if (state.IsButtonDown(Buttons.B)) buttons |= GamePadDigitalBits.B;
			if (state.IsButtonDown(Buttons.X)) buttons |= GamePadDigitalBits.X;
			if (state.IsButtonDown(Buttons.Y)) buttons |= GamePadDigitalBits.Y;
			if (state.IsButtonDown(Buttons.Back)) buttons |= GamePadDigitalBits.Back;
			if (state.IsButtonDown(Buttons.Start)) buttons |= GamePadDigitalBits.Start;
			if (state.IsButtonDown(Buttons.BigButton)) buttons |= GamePadDigitalBits.Guide;
			if (state.IsButtonDown(Buttons.LeftShoulder)) buttons |= GamePadDigitalBits.LeftShoulder;
			if (state.IsButtonDown(Buttons.RightShoulder)) buttons |= GamePadDigitalBits.RightShoulder;
			if (state.IsButtonDown(Buttons.LeftStick)) buttons |= GamePadDigitalBits.LeftStick;
			if (state.IsButtonDown(Buttons.RightStick)) buttons |= GamePadDigitalBits.RightStick;
			if (state.IsButtonDown(Buttons.DPadUp)) buttons |= GamePadDigitalBits.DPadUp;
			if (state.IsButtonDown(Buttons.DPadDown)) buttons |= GamePadDigitalBits.DPadDown;
			if (state.IsButtonDown(Buttons.DPadLeft)) buttons |= GamePadDigitalBits.DPadLeft;
			if (state.IsButtonDown(Buttons.DPadRight)) buttons |= GamePadDigitalBits.DPadRight;

			if (state.IsButtonDown(Paddle1Ext)) buttons |= GamePadDigitalBits.RightPaddle1;
			if (state.IsButtonDown(Paddle2Ext)) buttons |= GamePadDigitalBits.LeftPaddle1;
			if (state.IsButtonDown(Paddle3Ext)) buttons |= GamePadDigitalBits.RightPaddle2;
			if (state.IsButtonDown(Paddle4Ext)) buttons |= GamePadDigitalBits.LeftPaddle2;

			return new GamePadRawFrame {
				Connected = true,
				ButtonBits = buttons,
				LeftTriggerAnalog = state.Triggers.Left,
				RightTriggerAnalog = state.Triggers.Right,
				LeftStickX = state.ThumbSticks.Left.X,
				LeftStickY = state.ThumbSticks.Left.Y,
				RightStickX = state.ThumbSticks.Right.X,
				RightStickY = state.ThumbSticks.Right.Y
			};
		}
	}
}
