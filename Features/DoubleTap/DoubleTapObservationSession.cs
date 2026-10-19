using System;

namespace AdvancedKeybinds.Features.DoubleTap
{
	/// <summary>
	/// DT observe session: SourcePolicy + EventBuffer + 5-bit LL physical seed.
	/// Pure C#. No OS / game API / I/O / timer / log. Not Engine-wired.
	/// </summary>
	public sealed class DoubleTapObservationSession
	{
		const int MaskAll = 0x1F;
		const int BitLWin = 1 << 0;
		const int BitRWin = 1 << 1;
		const int BitCaps = 1 << 2;
		const int BitNum = 1 << 3;
		const int BitScroll = 1 << 4;

		readonly DoubleTapObservedEventBuffer _buffer;
		readonly object _gate = new();

		bool _active;
		long _generation;
		long _nextSequence;
		double _startMs;
		int _seedHeldMask;
		int _knownMask;
		int _heldMask;
		object? _focusBoundaryToken;

		public DoubleTapObservationSession(int capacity)
		{
			_buffer = new DoubleTapObservedEventBuffer(capacity);
		}

		public bool IsActive
		{
			get { lock (_gate) return _active; }
		}

		/// <summary>Current generation while active; 0 when inactive.</summary>
		public long Generation
		{
			get { lock (_gate) return _active ? _generation : 0L; }
		}

		/// <summary>Seed snapshot from the last successful Begin or a focus-boundary snapshot.</summary>
		public int SeedHeldMask
		{
			get { lock (_gate) return _seedHeldMask; }
		}

		public object? FocusBoundaryToken
		{
			get { return System.Threading.Volatile.Read(ref _focusBoundaryToken); }
		}

		/// <summary>
		/// Starts a generation. Invalid startMs (nonfinite or negative) ends and returns 0.
		/// Re-Begin while active discards the queue, advances generation, keeps known/held.
		/// </summary>
		public long Begin(int unknownHeldMask, double startMs)
		{
			lock (_gate)
			{
				if (!IsFiniteNonNegative(startMs))
				{
					EndUnlocked();
					return 0L;
				}

				long g = _buffer.BeginGeneration();
				if (g == 0L)
				{
					_active = false;
					_generation = 0L;
					_nextSequence = 0L;
					return 0L;
				}

				_active = true;
				_generation = g;
				_startMs = startMs;
				_nextSequence = 1L;
				_seedHeldMask = BuildSeed(unknownHeldMask);
				return g;
			}
		}

		public void End()
		{
			lock (_gate)
				EndUnlocked();
		}

		/// <summary>After losing the LL source, old physical knowledge is no longer reliable.</summary>
		public void EndAndForgetPhysicalState()
		{
			lock (_gate)
			{
				EndUnlocked();
				_knownMask = 0;
				_heldMask = 0;
				_seedHeldMask = 0;
			}
		}

		public bool ObserveWindow(
			uint vk,
			ushort scan,
			bool extended,
			bool wasKeyDown,
			bool ownInjected,
			bool sentMessage,
			ulong update,
			double eventMs,
			bool blocked)
		{
			lock (_gate)
			{
				bool accepted = DoubleTapObserveSourcePolicy.TryWindowKey(
					vk, scan, extended, wasKeyDown, ownInjected, sentMessage, out int keyId);

				if (blocked)
				{
					EndUnlocked();
					return false;
				}

				if (!accepted)
					return false;

				if (!IsFiniteNonNegative(eventMs))
				{
					EndUnlocked();
					return false;
				}

				if (!_active)
					return false;

				// Stale relative to generation start: reject only (keep queue / active).
				if (eventMs <= _startMs)
					return false;

				return TryEnqueueUnlocked(keyId, down: true, update, eventMs);
			}
		}

		public bool ObserveLowLevel(
			uint vk,
			ushort scan,
			bool extended,
			bool down,
			bool ownInjected,
			ulong update,
			double eventMs,
			bool blocked)
		{
			lock (_gate)
			{
				if (ownInjected)
				{
					// Bits never change for own injection.
					if (blocked)
						EndUnlocked();
					return false;
				}

				bool accepted = DoubleTapObserveSourcePolicy.TryLowLevelKey(
					vk, scan, extended, ownInjected: false, out int keyId);

				if (!accepted)
				{
					if (blocked)
						EndUnlocked();
					return false;
				}

				// Valid LL source, non-own: update known/held even if inactive, blocked, or bad time.
				ApplyPhysicalUnlocked(keyId, down);

				bool badTime = !IsFiniteNonNegative(eventMs);
				bool stale = _active && eventMs <= _startMs;
				if (badTime || stale)
				{
					EndUnlocked();
					return false;
				}

				if (blocked)
				{
					EndUnlocked();
					return false;
				}

				if (!_active)
					return false;

				return TryEnqueueUnlocked(keyId, down, update, eventMs);
			}
		}

		public int Drain(DoubleTapObservedEvent[] dest, int offset)
		{
			lock (_gate)
				return _buffer.Drain(dest, offset);
		}

		public int DrainForFocusBoundary(DoubleTapObservedEvent[] dest, int offset, out long generation, out int seedHeldMask, out object? focusBoundaryToken)
		{
			lock (_gate)
			{
				generation = _active ? _generation : 0L;
				seedHeldMask = _seedHeldMask;
				focusBoundaryToken = _focusBoundaryToken;
				return _buffer.Drain(dest, offset);
			}
		}

		internal bool TryEnterFocusBoundary(long generation, object? focusBoundaryToken)
		{
			System.Threading.Monitor.Enter(_gate);
			if (_active && _generation > 0L && generation == _generation && object.ReferenceEquals(_focusBoundaryToken, focusBoundaryToken))
				return true;

			System.Threading.Monitor.Exit(_gate);
			return false;
		}

		internal bool TryEnterFocusBoundary(object? focusBoundaryToken)
		{
			System.Threading.Monitor.Enter(_gate);
			if (object.ReferenceEquals(_focusBoundaryToken, focusBoundaryToken))
				return true;

			System.Threading.Monitor.Exit(_gate);
			return false;
		}

		internal void ExitFocusBoundary()
		{
			System.Threading.Monitor.Exit(_gate);
		}

		public void MarkFocusBoundary()
		{
			lock (_gate)
			{
				if (_active && _generation > 0L && _nextSequence > 0L)
				{
					_buffer.DiscardThrough(_generation, _nextSequence - 1L);
					_seedHeldMask = BuildSeed(_seedHeldMask);
				}

				System.Threading.Volatile.Write(ref _focusBoundaryToken, new object());
			}
		}

		void EndUnlocked()
		{
			_buffer.EndGeneration();
			_active = false;
			_generation = 0L;
			_nextSequence = 0L;
			// _knownMask / _heldMask / _seedHeldMask retained.
		}

		bool TryEnqueueUnlocked(int keyId, bool down, ulong update, double eventMs)
		{
			if (_nextSequence <= 0L || _nextSequence == long.MaxValue)
			{
				EndUnlocked();
				return false;
			}

			var ev = new DoubleTapObservedEvent
			{
				Generation = _generation,
				Sequence = _nextSequence,
				Update = update,
				TimestampMs = eventMs,
				Down = down,
				KeyId = keyId
			};

			if (!_buffer.TryWrite(ev))
			{
				// Overflow / buffer invalidate: session inactive; physical bits kept.
				_active = false;
				_generation = 0L;
				_nextSequence = 0L;
				return false;
			}

			_nextSequence++;
			return true;
		}

		void ApplyPhysicalUnlocked(int keyId, bool down)
		{
			int bit = BitFromKeyId(keyId);
			if (bit == 0)
				return;

			_knownMask |= bit;
			if (down)
				_heldMask |= bit;
			else
				_heldMask &= ~bit;
		}

		int BuildSeed(int unknownHeldMask)
		{
			int unknown = unknownHeldMask & MaskAll;
			int seed = 0;
			for (int i = 0; i < 5; i++)
			{
				int bit = 1 << i;
				if ((_knownMask & bit) != 0)
					seed |= _heldMask & bit;
				else
					seed |= unknown & bit;
			}

			return seed;
		}

		static int BitFromKeyId(int keyId)
		{
			uint vk = (uint)(keyId & 0xFF);
			if (vk == 0x5Bu) return BitLWin;
			if (vk == 0x5Cu) return BitRWin;
			if (vk == 0x14u) return BitCaps;
			if (vk == 0x90u) return BitNum;
			if (vk == 0x91u) return BitScroll;
			return 0;
		}

		static bool IsFiniteNonNegative(double value)
			=> !double.IsNaN(value)
				&& !double.IsInfinity(value)
				&& value >= 0.0;
	}
}
