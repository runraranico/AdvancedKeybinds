namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Per-frame gate: AkFireDecision and TriggerAfterApply each at most once per frame.
	/// </summary>
	internal sealed class AkDecisionFrameGate
	{
		private const byte PhaseIdle = 0;
		private const byte PhaseFire = 1;
		private const byte PhaseFinal = 2;

		private ulong _frame;
		private byte _phase;

		public bool TryBeginFire(ulong frame)
		{
			if (_phase != PhaseIdle && _frame == frame)
				return false;

			_frame = frame;
			_phase = PhaseFire;
			return true;
		}

		public bool TryBeginFinal(ulong frame)
		{
			if (_phase != PhaseFire || _frame != frame)
				return false;

			_phase = PhaseFinal;
			return true;
		}

		public void Reset()
		{
			_phase = PhaseIdle;
		}
	}
}
