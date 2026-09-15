using AdvancedKeybinds.Core;
using Terraria.GameInput;

namespace AdvancedKeybinds.Binding
{
	/// <summary>
	/// Activation state of the AK native GamePad subsystem plus the counters used by the
	/// performance harness. Decisions live in <see cref="GamePadSubsystemPolicy"/>; this type only
	/// supplies the Terraria-side inputs and holds the per-session latch.
	/// </summary>
	internal static class GamePadSubsystemRuntime
	{
		private static GamePadInputMode _mode = GamePadInputMode.Auto;
		private static bool _sessionLatch;
		private static bool _isActive;

		/// <summary><c>GamePad.GetState</c> calls since the last <see cref="ResetCounters"/>.</summary>
		internal static long GetStateCalls;

		/// <summary>Digital snapshots filled from a connected pad since the last <see cref="ResetCounters"/>.</summary>
		internal static long SnapshotBuilds;

		/// <summary>Token-set materialisations since the last <see cref="ResetCounters"/>. Should stay at 0 per frame.</summary>
		internal static long TokenSetBuilds;

		/// <summary>
		/// Configured mode. Written from <c>AdvancedKeybindsConfig</c> on load / change so the input
		/// path never has to go through <c>ModContent.GetInstance</c> per frame.
		/// </summary>
		internal static GamePadInputMode Mode
		{
			get => _mode;
			set
			{
				if (_mode == value)
					return;

				_mode = value;

				// Leaving Disabled must re-evaluate Auto from scratch instead of resuming an old latch.
				if (value == GamePadInputMode.Disabled)
					_sessionLatch = false;
			}
		}

		/// <summary>True when the subsystem may talk to FNA this tick.</summary>
		internal static bool IsActive => _isActive;

		internal static bool SessionLatched => _sessionLatch;

		/// <summary>
		/// Terraria's own gamepad usage. AK never polls FNA to answer this — see the Auto limitation
		/// documented on <see cref="GamePadSubsystemPolicy"/>.
		/// </summary>
		private static bool TerrariaUsingGamePad
			=> PlayerInput.UsingGamepad
				|| PlayerInput.CurrentInputMode == InputMode.XBoxGamepad
				|| PlayerInput.CurrentInputMode == InputMode.XBoxGamepadUI;

		/// <summary>
		/// Recomputes activation for this input tick.
		/// </summary>
		/// <param name="requiresFlush">True on the tick the subsystem stops being active.</param>
		internal static bool Advance(out bool requiresFlush)
		{
			bool usingPad = _mode == GamePadInputMode.Auto && TerrariaUsingGamePad;
			// ComboPanel purple wait is often opened with the mouse. Terraria may stay in KBM mode,
			// so Auto would skip FNA and Pad:/stick edges never appear — keyboard still works.
			bool slotCaptureActive = SlotCapture.IsCapturing;
			if (slotCaptureActive && _mode != GamePadInputMode.Disabled)
				usingPad = true;

			_sessionLatch = GamePadSubsystemPolicy.NextSessionLatch(_mode, usingPad, _sessionLatch);

			bool wasActive = _isActive;
			_isActive = GamePadSubsystemPolicy.IsActiveForTick(
				_mode, usingPad, _sessionLatch, slotCaptureActive);
			requiresFlush = GamePadSubsystemPolicy.RequiresStateFlush(wasActive, _isActive);
			return _isActive;
		}

		internal static void Reset()
		{
			_sessionLatch = false;
			_isActive = false;
			ResetCounters();
		}

		internal static void ResetCounters()
		{
			GetStateCalls = 0;
			SnapshotBuilds = 0;
			TokenSetBuilds = 0;
		}
	}
}
