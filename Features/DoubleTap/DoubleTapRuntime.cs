using System;

namespace AdvancedKeybinds.Features.DoubleTap
{
	/// <summary>
	/// Physical-input wrapper around <see cref="DoubleTapJudge"/>.
	/// Computes presses from Pulse / down-edge; Judge receives pulse-only samples (Down always false).
	/// </summary>
	/// <summary>
	/// Physical-input wrapper. Snapshot/edge Step remains for mouse/pad/wheel/non-Windows.
	/// Windows keyboard DT uses <see cref="ObserveKeyboard"/> (partial Keyboard).
	/// </summary>
	public sealed partial class DoubleTapRuntime
	{
		readonly DoubleTapJudge _judge = new();

		bool _hasSeenUpdate;
		ulong _lastUpdate;
		bool _hasValidBaseline;
		double _lastNowMs;
		int _lastWindowMs;

		bool _physicalPreviousDown;
		bool _releaseWait;

		public DoubleTapRuntime()
		{
			Reset();
		}

		public void Reset()
		{
			_judge.Reset();
			_hasSeenUpdate = false;
			_lastUpdate = 0;
			_hasValidBaseline = false;
			_lastNowMs = 0;
			_lastWindowMs = 0;
			_physicalPreviousDown = false;
			_releaseWait = true;
			ResetKeyboard();
		}

		public bool Step(DoubleTapInput input)
		{
			if (input.Blocked)
				return ProcessBlocked(input);

			if (_hasSeenUpdate && input.Update == _lastUpdate)
				return false;

			if (!IsFinite(input.NowMs) || !IsValidWindow(input.WindowMs))
				return ProcessInvalid(input);

			if (_hasSeenUpdate)
			{
				bool updateRewound = input.Update < _lastUpdate;
				bool clockRewound = _hasValidBaseline && input.NowMs < _lastNowMs;
				if (updateRewound || clockRewound)
					return ProcessRewind(input);
			}

			bool rising = input.Down && !_physicalPreviousDown;
			bool pressed = input.Pulse || (rising && !_releaseWait);

			bool fired = _judge.Step(ToJudgePulse(input, pressed));
			CommitAfterValidStep(input);
			return fired;
		}

		bool ProcessBlocked(DoubleTapInput input)
		{
			_judge.Step(ToJudgeBlocked(input));
			_releaseWait = true;
			CommitAfterBlockedOrInvalid(input);
			return false;
		}

		bool ProcessInvalid(DoubleTapInput input)
		{
			_judge.Step(ToJudgeBlocked(input));
			_releaseWait = input.Down;
			CommitAfterBlockedOrInvalid(input);
			return false;
		}

		bool ProcessRewind(DoubleTapInput input)
		{
			_judge.Step(ToJudgeBlocked(input));
			_releaseWait = input.Down;
			CommitAfterValidStep(input);
			return false;
		}

		static DoubleTapInput ToJudgePulse(DoubleTapInput input, bool pressed)
		{
			return new DoubleTapInput(
				input.Update,
				input.NowMs,
				input.WindowMs,
				down: false,
				pulse: pressed,
				blocked: false);
		}

		static DoubleTapInput ToJudgeBlocked(DoubleTapInput input)
		{
			return new DoubleTapInput(
				input.Update,
				input.NowMs,
				input.WindowMs,
				down: false,
				pulse: false,
				blocked: true);
		}

		void CommitAfterValidStep(DoubleTapInput input)
		{
			// An edge can arrive after the held snapshot. Its next held sample is
			// still the same press; only a later quiet release clears this guard.
			if (input.Pulse)
				_releaseWait = true;
			else if (!input.Down)
				_releaseWait = false;

			_physicalPreviousDown = input.Down;
			_lastUpdate = input.Update;
			_hasSeenUpdate = true;
			_lastNowMs = input.NowMs;
			_lastWindowMs = input.WindowMs;
			_hasValidBaseline = true;
		}

		void CommitAfterBlockedOrInvalid(DoubleTapInput input)
		{
			_physicalPreviousDown = input.Down;
			_lastUpdate = input.Update;
			_hasSeenUpdate = true;
		}

		static bool IsFinite(double value)
		{
			return !double.IsNaN(value) && !double.IsInfinity(value);
		}

		static bool IsValidWindow(int windowMs)
		{
			return windowMs >= DoubleTapMsPolicy.MinMs && windowMs <= DoubleTapMsPolicy.MaxMs;
		}
	}
}
