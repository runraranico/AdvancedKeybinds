using System;

namespace AdvancedKeybinds.Features.DoubleTap
{
	/// <summary>
	/// 単一 key / 単一 source 用。down repeat を押下にせず、up→down だけを PressSequence へ渡す。
	/// 引数のみ。ゲーム API / OS / I/O / timer / ログなし。
	/// </summary>
	public sealed class DoubleTapObservedKey
	{
		readonly DoubleTapPressSequence _press = new();

		long _lastSequence;
		bool _held;
		bool _releaseWait;
		bool _hasValidBaseline;
		ulong _lastUpdate;
		double _lastNowMs;
		int _lastWindowMs;

		/// <summary>安全側: 初期 held。解放まで押下にしない。</summary>
		public DoubleTapObservedKey()
		{
			Reset(initiallyHeld: true);
		}

		/// <summary>
		/// pair / sequence / baseline を破棄する。
		/// 呼び出し側は、上位の押下ソースが generation を flush したあとで呼ぶこと。
		/// </summary>
		public void Reset(bool initiallyHeld)
		{
			_press.Reset();
			_lastSequence = 0;
			_held = initiallyHeld;
			_releaseWait = false;
			_hasValidBaseline = false;
			_lastUpdate = 0;
			_lastNowMs = 0;
			_lastWindowMs = 0;
		}

		/// <summary>
		/// 観測 1 件。真正 up→down のみ PressSequence へ。blocked は重複 sequence でも最優先。
		/// </summary>
		public bool Observe(
			long sequence,
			ulong update,
			double nowMs,
			int windowMs,
			bool down,
			bool blocked)
		{
			if (blocked)
			{
				ApplyBlocked(sequence, update, nowMs, windowMs, down);
				return false;
			}

			// Non-blocked same/older/non-positive: no state change.
			if (sequence <= 0L || sequence <= _lastSequence)
				return false;

			bool finite = IsFinite(nowMs);
			bool validWindow = IsValidWindow(windowMs);

			if (!finite || !validWindow)
			{
				_lastSequence = sequence;
				_press.Reset();
				_releaseWait = true;
				_held = down;
				// Do not adopt nonfinite / out-of-range window as baseline.
				return false;
			}

			if (_hasValidBaseline)
			{
				if (update < _lastUpdate || nowMs < _lastNowMs)
				{
					_lastSequence = sequence;
					_press.Reset();
					_releaseWait = true;
					CommitBaseline(update, nowMs, windowMs);
					_held = down;
					return false;
				}

				if (windowMs != _lastWindowMs)
				{
					_press.Reset();
					if (_held)
						_releaseWait = true;
					// Fall through to normal up/down so an up clears release-wait
					// and a released→down with window change can arm as new first.
				}
			}

			_lastSequence = sequence;
			CommitBaseline(update, nowMs, windowMs);

			bool wasHeld = _held;
			_held = down;

			// Release-wait clears only on non-blocked up (held true→false).
			if (wasHeld && !down)
				_releaseWait = false;

			if (!wasHeld && down && !_releaseWait)
				return _press.Press(sequence, update, nowMs, windowMs);

			return false;
		}

		void ApplyBlocked(long sequence, ulong update, double nowMs, int windowMs, bool down)
		{
			_press.Reset();
			_releaseWait = true;
			_held = down;
			if (sequence > 0L && sequence > _lastSequence)
				_lastSequence = sequence;
			if (IsFinite(nowMs) && IsValidWindow(windowMs))
				CommitBaseline(update, nowMs, windowMs);
		}

		void CommitBaseline(ulong update, double nowMs, int windowMs)
		{
			_lastUpdate = update;
			_lastNowMs = nowMs;
			_lastWindowMs = windowMs;
			_hasValidBaseline = true;
		}

		static bool IsFinite(double value)
			=> !double.IsNaN(value) && !double.IsInfinity(value);

		static bool IsValidWindow(int windowMs)
			=> windowMs >= DoubleTapMsPolicy.MinMs && windowMs <= DoubleTapMsPolicy.MaxMs;
	}
}
