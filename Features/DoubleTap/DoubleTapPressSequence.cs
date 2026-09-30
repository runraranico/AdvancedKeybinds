using System;

namespace AdvancedKeybinds.Features.DoubleTap
{
	/// <summary>
	/// 真正押下だけの DT ペア判定。引数のみ。ゲーム API / OS / I/O / timer / ログなし。
	/// repeat・初期 held・blocked は上位ソースの仕事。
	/// </summary>
	public sealed class DoubleTapPressSequence
	{
		long _lastSequence;
		bool _hasValidBaseline;
		ulong _lastUpdate;
		double _lastNowMs;
		int _lastWindowMs;

		bool _hasFirst;
		ulong _firstUpdate;
		double _firstMs;

		/// <summary>
		/// pair と sequence / baseline を破棄する。
		/// 呼び出し側は、上位の押下ソースが generation を flush したあとで呼ぶこと。
		/// </summary>
		public void Reset()
		{
			_lastSequence = 0;
			_hasValidBaseline = false;
			_lastUpdate = 0;
			_lastNowMs = 0;
			_lastWindowMs = 0;
			DiscardPair();
		}

		/// <summary>
		/// 真正押下 1 回。初回 false、窓内第 2 打 true。3 打目は新 first、4 打目で再び true。
		/// </summary>
		public bool Press(long sequence, ulong update, double nowMs, int windowMs)
		{
			// Same / older / non-positive: no state change (even if clock/window are invalid).
			if (sequence <= 0L || sequence <= _lastSequence)
				return false;

			bool finite = IsFinite(nowMs);
			bool validWindow = IsValidWindow(windowMs);

			if (!finite || !validWindow)
			{
				DiscardPair();
				_lastSequence = sequence;
				// Do not adopt nonfinite clock or out-of-range window as baseline.
				return false;
			}

			if (_hasValidBaseline)
			{
				bool updateRewound = update < _lastUpdate;
				bool clockRewound = nowMs < _lastNowMs;
				if (updateRewound || clockRewound)
				{
					DiscardPair();
					_lastSequence = sequence;
					CommitBaseline(update, nowMs, windowMs);
					// Re-base only; do not arm as a new first.
					return false;
				}

				if (windowMs != _lastWindowMs)
				{
					DiscardPair();
					_lastSequence = sequence;
					ArmFirst(update, nowMs, windowMs);
					return false;
				}
			}

			_lastSequence = sequence;

			if (_hasFirst && IsWithinWindow(update, nowMs, windowMs))
			{
				DiscardPair();
				CommitBaseline(update, nowMs, windowMs);
				return true;
			}

			ArmFirst(update, nowMs, windowMs);
			return false;
		}

		void ArmFirst(ulong update, double nowMs, int windowMs)
		{
			_hasFirst = true;
			_firstUpdate = update;
			_firstMs = nowMs;
			CommitBaseline(update, nowMs, windowMs);
		}

		void CommitBaseline(ulong update, double nowMs, int windowMs)
		{
			_lastUpdate = update;
			_lastNowMs = nowMs;
			_lastWindowMs = windowMs;
			_hasValidBaseline = true;
		}

		void DiscardPair()
		{
			_hasFirst = false;
			_firstUpdate = 0;
			_firstMs = 0;
		}

		bool IsWithinWindow(ulong update, double nowMs, int windowMs)
		{
			if (windowMs == DoubleTapMsPolicy.MinMs)
				return (update - _firstUpdate) < 15UL;

			return (nowMs - _firstMs) <= windowMs;
		}

		static bool IsFinite(double value)
			=> !double.IsNaN(value) && !double.IsInfinity(value);

		static bool IsValidWindow(int windowMs)
			=> windowMs >= DoubleTapMsPolicy.MinMs && windowMs <= DoubleTapMsPolicy.MaxMs;
	}
}
