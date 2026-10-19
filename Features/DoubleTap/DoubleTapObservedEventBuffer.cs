using System;

namespace AdvancedKeybinds.Features.DoubleTap
{
	/// <summary>
	/// Generation-scoped bounded ring for DT observe events.
	/// Pure C#. No OS / game API / I/O. No unlimited loops in TryWrite.
	/// Global TimestampMs/Update monotonicity is not enforced (cross-key reorder is valid).
	/// </summary>
	public sealed class DoubleTapObservedEventBuffer
	{
		public const int MaxCapacity = 4096;

		readonly DoubleTapObservedEvent[] _slots;
		readonly object _gate = new();

		int _head;
		int _count;
		long _generation;
		long _lastSequence;
		bool _accepting;

		public DoubleTapObservedEventBuffer(int capacity)
		{
			if (capacity < 1 || capacity > MaxCapacity)
				throw new ArgumentOutOfRangeException(nameof(capacity));

			_slots = new DoubleTapObservedEvent[capacity];
		}

		public bool IsAccepting
		{
			get
			{
				lock (_gate)
					return _accepting;
			}
		}

		public void Disable()
		{
			lock (_gate)
			{
				DiscardLogical();
				// Generation identity must never be reused after Disable.
				_accepting = false;
			}
		}

		/// <summary>
		/// Discards pending events and starts a new generation. Returns 0 if generation cannot advance.
		/// </summary>
		public long BeginGeneration()
		{
			lock (_gate)
			{
				DiscardLogical();
				if (_generation == long.MaxValue)
				{
					_accepting = false;
					return 0;
				}

				_generation++;
				_accepting = true;
				return _generation;
			}
		}

		public void EndGeneration()
		{
			lock (_gate)
			{
				DiscardLogical();
				_accepting = false;
			}
		}

		public bool TryWrite(DoubleTapObservedEvent ev)
		{
			lock (_gate)
			{
				if (!_accepting)
					return false;

				if (ev.Generation != _generation)
					return false;

				if (!IsValidTimestamp(ev.TimestampMs))
				{
					Invalidate();
					return false;
				}

				if (ev.Sequence <= 0L || (_lastSequence > 0L && ev.Sequence <= _lastSequence))
				{
					Invalidate();
					return false;
				}

				if (_count >= _slots.Length)
				{
					Invalidate();
					return false;
				}

				int index = (_head + _count) % _slots.Length;
				_slots[index] = ev;
				_count++;
				_lastSequence = ev.Sequence;
				return true;
			}
		}

		/// <summary>
		/// Copies up to available events into dest starting at offset. Reuses caller storage.
		/// Bad args return 0 without consuming the queue.
		/// </summary>
		public int Drain(DoubleTapObservedEvent[] dest, int offset)
		{
			lock (_gate)
			{
				if (dest == null || offset < 0 || offset >= dest.Length)
					return 0;

				int space = dest.Length - offset;
				if (space <= 0 || _count <= 0)
					return 0;

				int n = _count < space ? _count : space;
				for (int i = 0; i < n; i++)
				{
					dest[offset + i] = _slots[_head];
					_head = (_head + 1) % _slots.Length;
				}

				_count -= n;
				if (_count == 0)
					_head = 0;

				return n;
			}
		}

		public int DiscardThrough(long generation, long inclusiveSequence)
		{
			lock (_gate)
			{
				if (!_accepting
					|| _generation <= 0L
					|| generation != _generation
					|| inclusiveSequence < 0L
					|| _count <= 0)
					return 0;

				int removed = 0;
				int limit = _count;
				for (int i = 0; i < limit; i++)
				{
					if (_slots[_head].Sequence > inclusiveSequence)
						break;

					_head = (_head + 1) % _slots.Length;
					_count--;
					removed++;
				}

				if (_count == 0)
					_head = 0;

				return removed;
			}
		}

		void Invalidate()
		{
			DiscardLogical();
			_accepting = false;
		}

		void DiscardLogical()
		{
			_head = 0;
			_count = 0;
			_lastSequence = 0;
		}

		static bool IsValidTimestamp(double timestampMs)
			=> !double.IsNaN(timestampMs)
				&& !double.IsInfinity(timestampMs)
				&& timestampMs >= 0.0;
	}
}
