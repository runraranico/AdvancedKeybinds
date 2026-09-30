using System;

namespace AdvancedKeybinds.Features.DoubleTap
{
	/// <summary>
	/// 1キーDT独立判定。時刻と入力は引数のみ。ゲームAPI／I/O／タイマー起動なし。
	/// </summary>
	public readonly struct DoubleTapInput
	{
		public readonly ulong Update;
		public readonly double NowMs;
		public readonly int WindowMs;
		public readonly bool Down;
		public readonly bool Pulse;
		public readonly bool Blocked;

		public DoubleTapInput(
			ulong update,
			double nowMs,
			int windowMs,
			bool down,
			bool pulse = false,
			bool blocked = false)
		{
			Update = update;
			NowMs = nowMs;
			WindowMs = windowMs;
			Down = down;
			Pulse = pulse;
			Blocked = blocked;
		}
	}

	public sealed class DoubleTapJudge
	{
		/// <summary>True after any received Step that recorded a seen Update (valid or invalid).</summary>
		private bool _initialized;
		/// <summary>True only when _lastNowMs / _lastWindowMs come from a finite valid-window commit.</summary>
		private bool _hasValidBaseline;
		private ulong _lastUpdate;
		private double _lastNowMs;
		private int _lastWindowMs;
		private bool _previousDown;
		private bool _releaseWait;
		private bool _hasFirst;
		private double _firstMs;
		private int _remainingUpdates;

		public bool Step(DoubleTapInput input)
		{
			// Blocked has priority over the same-update no-mutate gate.
			if (input.Blocked)
			{
				DiscardPair();
				_releaseWait = true;
				if (IsFinite(input.NowMs) && IsValidWindow(input.WindowMs))
					CommitTracking(input);
				else
					CommitTrackingAfterInvalid(input);
				return false;
			}

			// Non-blocked duplicate Update must not mutate (including NaN / bad window).
			if (_initialized && input.Update == _lastUpdate)
				return false;

			if (!IsFinite(input.NowMs) || !IsValidWindow(input.WindowMs))
			{
				DiscardPair();
				if (input.Down)
					_releaseWait = true;
				CommitTrackingAfterInvalid(input);
				return false;
			}

			if (_initialized)
			{
				bool updateRewound = input.Update < _lastUpdate;
				bool clockRewound = _hasValidBaseline && input.NowMs < _lastNowMs;
				if (updateRewound || clockRewound)
				{
					DiscardPair();
					if (input.Down)
						_releaseWait = true;
					CommitTracking(input);
					return false;
				}
			}

			if (input.Pulse && input.Down)
			{
				DiscardPair();
				if (input.Down)
					_releaseWait = true;
				CommitTracking(input);
				return false;
			}

			// Window change vs last *valid* window only — never vs zero default after invalid-only.
			bool windowChanged = _hasValidBaseline && input.WindowMs != _lastWindowMs;
			if (windowChanged)
			{
				DiscardPair();
				if (input.Down)
					_releaseWait = true;
			}

			if (_hasFirst && input.WindowMs == DoubleTapMsPolicy.MinMs && _initialized)
			{
				ulong delta = input.Update - _lastUpdate;
				if (delta >= 15UL)
					_remainingUpdates = 0;
				else
				{
					int d = (int)delta;
					if (_remainingUpdates > d)
						_remainingUpdates -= d;
					else
						_remainingUpdates = 0;
				}
			}

			if (!input.Down)
				_releaseWait = false;

			bool fired = false;
			bool isPress = false;
			if (input.Pulse)
				isPress = true;
			else if (input.Down && !_previousDown && !_releaseWait)
				isPress = true;

			if (isPress)
			{
				if (_hasFirst && IsWithinWindow(input))
				{
					fired = true;
					_hasFirst = false;
					_remainingUpdates = 0;
					if (!input.Pulse)
						_releaseWait = true;
				}
				else
				{
					_hasFirst = true;
					_firstMs = input.NowMs;
					_remainingUpdates = 15;
				}
			}

			CommitTracking(input);
			return fired;
		}

		public void Reset()
		{
			_initialized = false;
			_hasValidBaseline = false;
			_lastUpdate = 0;
			_lastNowMs = 0;
			_lastWindowMs = 0;
			_previousDown = false;
			_hasFirst = false;
			_firstMs = 0;
			_remainingUpdates = 0;
			_releaseWait = true;
		}

		private bool IsWithinWindow(DoubleTapInput input)
		{
			if (input.WindowMs == DoubleTapMsPolicy.MinMs)
				return _remainingUpdates > 0;

			return (input.NowMs - _firstMs) <= input.WindowMs;
		}

		private void DiscardPair()
		{
			_hasFirst = false;
			_remainingUpdates = 0;
			_firstMs = 0;
		}

		/// <summary>
		/// Valid finite clock + window: records seen Update and establishes clock/window baseline.
		/// </summary>
		private void CommitTracking(DoubleTapInput input)
		{
			_lastUpdate = input.Update;
			_lastNowMs = input.NowMs;
			_lastWindowMs = input.WindowMs;
			_previousDown = input.Down;
			_hasValidBaseline = true;
			_initialized = true;
		}

		/// <summary>
		/// Invalid NowMs / WindowMs: always record seen Update for the duplicate gate and Down tracking.
		/// Never adopt nonfinite / out-of-range values as clock or window baseline.
		/// </summary>
		private void CommitTrackingAfterInvalid(DoubleTapInput input)
		{
			_previousDown = input.Down;
			_lastUpdate = input.Update;
			_initialized = true;
			// intentionally leave _lastNowMs / _lastWindowMs / _hasValidBaseline unchanged
		}

		private static bool IsFinite(double value)
		{
			return !double.IsNaN(value) && !double.IsInfinity(value);
		}

		private static bool IsValidWindow(int windowMs)
		{
			return windowMs >= DoubleTapMsPolicy.MinMs && windowMs <= DoubleTapMsPolicy.MaxMs;
		}
	}
}
