using System;
using System.Collections.Generic;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// One registration newcomer tagged with the WM_KEY* EventSequence that produced it.
	/// </summary>
	public readonly struct SequencedNewcomer
	{
		public readonly long EventSequence;
		public readonly string Token;
		public readonly bool HasMeasuredScan;
		public readonly bool IsKeyRepeat;

		public SequencedNewcomer(long eventSequence, string token, bool hasMeasuredScan, bool isKeyRepeat = false)
		{
			EventSequence = eventSequence;
			Token = token;
			HasMeasuredScan = hasMeasuredScan;
			IsKeyRepeat = isKeyRepeat;
		}
	}

	/// <summary>
	/// Result of observing one measured physical key event through <see cref="EventSequenceUnifier"/>.
	/// </summary>
	public readonly struct EventSequenceUnifyResult
	{
		public readonly long EventSequence;
		public readonly string CanonicalToken;
		public readonly bool AddedAsNewcomer;
		public readonly bool SuppressedAlias;
		public readonly bool ReplacedFallbackWithScan;
		public readonly bool IgnoredAsRepeat;
		public readonly bool ClearedOnKeyUp;
		public readonly string ExcludedAlias;

		public EventSequenceUnifyResult(
			long eventSequence,
			string canonicalToken,
			bool addedAsNewcomer,
			bool suppressedAlias,
			bool replacedFallbackWithScan,
			bool ignoredAsRepeat,
			bool clearedOnKeyUp,
			string excludedAlias)
		{
			EventSequence = eventSequence;
			CanonicalToken = canonicalToken;
			AddedAsNewcomer = addedAsNewcomer;
			SuppressedAlias = suppressedAlias;
			ReplacedFallbackWithScan = replacedFallbackWithScan;
			IgnoredAsRepeat = ignoredAsRepeat;
			ClearedOnKeyUp = clearedOnKeyUp;
			ExcludedAlias = excludedAlias;
		}
	}

	/// <summary>
	/// World-portable EventSequence unifier: same WM_KEY* EventSequence → one registration candidate.
	/// Does not use PhysicalKeyIdentity.AreSamePhysical as the primary same-press test.
	/// Bounded slots — no unlimited history.
	/// </summary>
	public sealed class EventSequenceUnifier
	{
		public const string PolicyVersion = "event-sequence-unify-v1";
		public const int MaxActiveSlots = 64;

		private struct Slot
		{
			public long Sequence;
			public long MakeSequence;
			public string Canonical;
			public bool HasMeasuredScan;
			public ushort Scan;
			public bool Extended;
			public uint Vk;
			public bool Held;
			public string FallbackAlias;
		}

		private readonly Slot[] _slots = new Slot[MaxActiveSlots];
		private readonly Dictionary<long, int> _bySequence = new();
		private readonly Dictionary<int, int> _byPhysical = new();
		private int _count;

		/// <summary>Optional diag sink; never allocate when null (public / diag OFF).</summary>
		public Action<string> DiagSink { get; set; }

		public int ActiveSlotCount => _count;

		public void Clear()
		{
			_bySequence.Clear();
			_byPhysical.Clear();
			for (int i = 0; i < _count; i++)
				_slots[i] = default;
			_count = 0;
		}

		/// <summary>
		/// KeyDown observation. Same EventSequence merges to one canonical (SC wins).
		/// Autorepeat (isKeyRepeat) does not add a second newcomer.
		/// </summary>
		public EventSequenceUnifyResult ObserveDown(in MeasuredPhysicalKeyEvent ev, bool isKeyRepeat)
		{
			if (ev.EventSequence == 0 || string.IsNullOrEmpty(ev.CanonicalToken))
			{
				return new EventSequenceUnifyResult(
					ev.EventSequence, ev.CanonicalToken,
					addedAsNewcomer: false, suppressedAlias: false,
					replacedFallbackWithScan: false, ignoredAsRepeat: false,
					clearedOnKeyUp: false, excludedAlias: null);
			}

			int physicalKey = PhysicalKeyId(ev);
			string excluded = null;

			// Autorepeat / still-held: same physical press — never a second registration newcomer.
			int heldIdx = -1;
			bool physicallyHeld = physicalKey != 0
				&& _byPhysical.TryGetValue(physicalKey, out heldIdx)
				&& heldIdx >= 0
				&& heldIdx < _count
				&& _slots[heldIdx].Held;

			if (isKeyRepeat || physicallyHeld)
			{
				int idx = physicallyHeld ? heldIdx : -1;
				if (idx < 0 && _bySequence.TryGetValue(ev.EventSequence, out int sIdx))
					idx = sIdx;

				if (idx >= 0)
				{
					bool upgraded = TryUpgradeSlotToMeasuredScan(ref _slots[idx], in ev, out excluded);
					_bySequence[ev.EventSequence] = idx;
					Diag("repeat-ignore seq=" + ev.EventSequence
						+ " phys=" + FormatPhys(ev)
						+ " canonical=" + _slots[idx].Canonical
						+ (upgraded ? " upgraded-sc" : string.Empty));
					return new EventSequenceUnifyResult(
						ev.EventSequence,
						_slots[idx].Canonical,
						addedAsNewcomer: false,
						suppressedAlias: !string.IsNullOrEmpty(excluded),
						replacedFallbackWithScan: upgraded,
						ignoredAsRepeat: true,
						clearedOnKeyUp: false,
						excludedAlias: excluded);
				}
			}

			// Same EventSequence already recorded → merge aliases into one canonical.
			if (_bySequence.TryGetValue(ev.EventSequence, out int existing))
			{
				bool upgraded = TryUpgradeSlotToMeasuredScan(ref _slots[existing], in ev, out excluded);
				if (!upgraded && !string.Equals(_slots[existing].Canonical, ev.CanonicalToken, StringComparison.OrdinalIgnoreCase))
				{
					// Alias for same sequence while SC already owns — suppress.
					if (_slots[existing].HasMeasuredScan && !ev.HasMeasuredScan)
					{
						excluded = ev.CanonicalToken;
						Diag("same-seq-suppress-alias seq=" + ev.EventSequence
							+ " keep=" + _slots[existing].Canonical
							+ " drop=" + excluded);
						return new EventSequenceUnifyResult(
							ev.EventSequence,
							_slots[existing].Canonical,
							addedAsNewcomer: false,
							suppressedAlias: true,
							replacedFallbackWithScan: false,
							ignoredAsRepeat: false,
							clearedOnKeyUp: false,
							excludedAlias: excluded);
					}
				}

				TouchPhysicalIndex(existing, in ev);
				Diag("same-seq-merge seq=" + ev.EventSequence
					+ " canonical=" + _slots[existing].Canonical
					+ (upgraded ? " replaced-fallback→sc" : string.Empty));
				return new EventSequenceUnifyResult(
					ev.EventSequence,
					_slots[existing].Canonical,
					addedAsNewcomer: false,
					suppressedAlias: !string.IsNullOrEmpty(excluded),
					replacedFallbackWithScan: upgraded,
					ignoredAsRepeat: false,
					clearedOnKeyUp: false,
					excludedAlias: excluded);
			}

			// New EventSequence → new slot (distinct physical press / chord peer).
			if (!TryAlloc(out int neu))
			{
				Diag("slot-overflow drop seq=" + ev.EventSequence);
				return new EventSequenceUnifyResult(
					ev.EventSequence, ev.CanonicalToken,
					addedAsNewcomer: false, suppressedAlias: false,
					replacedFallbackWithScan: false, ignoredAsRepeat: false,
					clearedOnKeyUp: false, excludedAlias: null);
			}

			_slots[neu] = new Slot {
				Sequence = ev.EventSequence,
				MakeSequence = ev.EventSequence,
				Canonical = ev.CanonicalToken,
				HasMeasuredScan = ev.HasMeasuredScan,
				Scan = ev.ScanCode,
				Extended = ev.Extended,
				Vk = ev.Vk,
				Held = ev.IsDown,
				FallbackAlias = ev.HasMeasuredScan ? ev.ResolverFallbackToken : null
			};
			_bySequence[ev.EventSequence] = neu;
			TouchPhysicalIndex(neu, in ev);

			if (ev.HasMeasuredScan && !string.IsNullOrEmpty(ev.ResolverFallbackToken)
				&& !string.Equals(ev.ResolverFallbackToken, ev.CanonicalToken, StringComparison.OrdinalIgnoreCase))
			{
				excluded = ev.ResolverFallbackToken;
			}

			Diag("new-seq seq=" + ev.EventSequence
				+ " canonical=" + ev.CanonicalToken
				+ " hasSc=" + ev.HasMeasuredScan
				+ (excluded != null ? " excluded-alias=" + excluded : string.Empty));

			return new EventSequenceUnifyResult(
				ev.EventSequence,
				ev.CanonicalToken,
				addedAsNewcomer: true,
				suppressedAlias: excluded != null,
				replacedFallbackWithScan: false,
				ignoredAsRepeat: false,
				clearedOnKeyUp: false,
				excludedAlias: excluded);
		}

		/// <summary>
		/// KeyUp: release by measured SC+Extended (preferred), not by layout VK name alone.
		/// </summary>
		public EventSequenceUnifyResult ObserveUp(ushort scan, bool extended, uint vk, long eventSequence = 0)
		{
			scan = (ushort)(scan & 0xFF);
			int idx = -1;
			if (scan != 0)
			{
				int phys = PackScan(scan, extended);
				if (_byPhysical.TryGetValue(phys, out int p))
					idx = p;
			}

			if (idx < 0 && eventSequence != 0 && _bySequence.TryGetValue(eventSequence, out int s))
				idx = s;

			if (idx < 0 && vk != 0)
			{
				int physVk = PackVk(vk);
				if (_byPhysical.TryGetValue(physVk, out int v))
					idx = v;
			}

			if (idx < 0)
			{
				return new EventSequenceUnifyResult(
					eventSequence, null,
					addedAsNewcomer: false, suppressedAlias: false,
					replacedFallbackWithScan: false, ignoredAsRepeat: false,
					clearedOnKeyUp: false, excludedAlias: null);
			}

			string token = _slots[idx].Canonical;
			long seq = _slots[idx].Sequence;
			FreeSlot(idx);
			Diag("keyup seq=" + seq + " released=" + token);
			return new EventSequenceUnifyResult(
				seq, token,
				addedAsNewcomer: false, suppressedAlias: false,
				replacedFallbackWithScan: false, ignoredAsRepeat: false,
				clearedOnKeyUp: true, excludedAlias: null);
		}

		/// <summary>Canonical tokens for currently held unified presses (registration / diag).</summary>
		public void CopyHeldCanonicals(List<string> destination)
		{
			if (destination == null)
				return;
			for (int i = 0; i < _count; i++)
			{
				if (!_slots[i].Held || string.IsNullOrEmpty(_slots[i].Canonical))
					continue;
				if (!ContainsIgnoreCase(destination, _slots[i].Canonical))
					destination.Add(_slots[i].Canonical);
			}
		}

		/// <summary>
		/// Collapse sequenced newcomers: same EventSequence → one token (SC wins).
		/// Different EventSequences stay separate. Does not merge by AreSamePhysical.
		/// </summary>
		public static List<string> CollapseSequencedNewcomers(IReadOnlyList<SequencedNewcomer> newcomers)
		{
			var result = new List<string>();
			if (newcomers == null || newcomers.Count == 0)
				return result;

			// sequence → best token (measured SC preferred)
			var bySeq = new Dictionary<long, SequencedNewcomer>();
			var unsequenced = new List<string>();

			for (int i = 0; i < newcomers.Count; i++)
			{
				SequencedNewcomer n = newcomers[i];
				if (string.IsNullOrEmpty(n.Token))
					continue;
				if (n.IsKeyRepeat)
					continue;
				if (n.EventSequence == 0)
				{
					if (!ContainsIgnoreCase(unsequenced, n.Token))
						unsequenced.Add(n.Token);
					continue;
				}

				if (!bySeq.TryGetValue(n.EventSequence, out SequencedNewcomer existing))
				{
					bySeq[n.EventSequence] = n;
					continue;
				}

				// Same sequence: measured SC replaces Keys/VK fallback.
				if (n.HasMeasuredScan && !existing.HasMeasuredScan)
					bySeq[n.EventSequence] = n;
				else if (n.HasMeasuredScan && existing.HasMeasuredScan)
				{
					// Prefer exact SC token form if both claim measured.
					if (PhysicalKeyToken.TryParseScan(n.Token, out _, out _)
						&& !PhysicalKeyToken.TryParseScan(existing.Token, out _, out _))
						bySeq[n.EventSequence] = n;
				}
				// else keep existing (alias suppressed)
			}

			foreach (KeyValuePair<long, SequencedNewcomer> pair in bySeq)
			{
				if (!ContainsIgnoreCase(result, pair.Value.Token))
					result.Add(pair.Value.Token);
			}

			if (unsequenced.Count > 0)
			{
				List<string> collapsed = MeasuredPhysicalKeyNormalizer.CollapseNewcomers(unsequenced);
				for (int i = 0; i < collapsed.Count; i++)
				{
					if (!ContainsIgnoreCase(result, collapsed[i]))
						result.Add(collapsed[i]);
				}
			}

			return result;
		}

		/// <summary>
		/// Simulate late SC arrival for the same EventSequence (VK/Keys first, then SC).
		/// </summary>
		public static List<string> MergeLateMeasuredScan(
			long eventSequence,
			string fallbackToken,
			string measuredScanToken)
		{
			var list = new List<SequencedNewcomer>(2) {
				new SequencedNewcomer(eventSequence, fallbackToken, hasMeasuredScan: false),
				new SequencedNewcomer(eventSequence, measuredScanToken, hasMeasuredScan: true)
			};
			return CollapseSequencedNewcomers(list);
		}

		private bool TryUpgradeSlotToMeasuredScan(ref Slot slot, in MeasuredPhysicalKeyEvent ev, out string excluded)
		{
			excluded = null;
			if (!ev.HasMeasuredScan)
				return false;
			if (slot.HasMeasuredScan
				&& string.Equals(slot.Canonical, ev.CanonicalToken, StringComparison.OrdinalIgnoreCase))
			{
				slot.Held = true;
				slot.Sequence = ev.EventSequence;
				return false;
			}

			if (!slot.HasMeasuredScan && !string.IsNullOrEmpty(slot.Canonical)
				&& !string.Equals(slot.Canonical, ev.CanonicalToken, StringComparison.OrdinalIgnoreCase))
				excluded = slot.Canonical;
			else if (slot.HasMeasuredScan
				&& !string.Equals(slot.Canonical, ev.CanonicalToken, StringComparison.OrdinalIgnoreCase))
				excluded = slot.Canonical;

			slot.Canonical = ev.CanonicalToken;
			slot.HasMeasuredScan = true;
			slot.Scan = ev.ScanCode;
			slot.Extended = ev.Extended;
			slot.Vk = ev.Vk != 0 ? ev.Vk : slot.Vk;
			slot.Held = true;
			slot.Sequence = ev.EventSequence;
			return true;
		}

		private void TouchPhysicalIndex(int idx, in MeasuredPhysicalKeyEvent ev)
		{
			int phys = PhysicalKeyId(ev);
			if (phys == 0)
				return;
			_byPhysical[phys] = idx;
			_slots[idx].Held = true;
		}

		private static int PhysicalKeyId(in MeasuredPhysicalKeyEvent ev)
		{
			if (ev.HasMeasuredScan
				|| (ev.ScanCode != 0 && !MeasuredPhysicalKeyNormalizer.IsSpecialHandledVirtualKey(ev.Vk)))
				return PackScan(ev.ScanCode, ev.Extended);
			if (PhysicalKeyToken.IsValidVirtualKey(ev.Vk))
				return PackVk(ev.Vk);
			return 0;
		}

		private static int PackScan(ushort scan, bool extended)
			=> (1 << 24) | ((scan & 0xFF) << 1) | (extended ? 1 : 0);

		private static int PackVk(uint vk)
			=> (2 << 24) | (int)(vk & 0xFF);

		private static string FormatPhys(in MeasuredPhysicalKeyEvent ev)
			=> ev.HasMeasuredScan || ev.ScanCode != 0
				? PhysicalKeyToken.FormatScan(ev.ScanCode, ev.Extended)
				: PhysicalKeyToken.FormatVk(ev.Vk);

		private bool TryAlloc(out int idx)
		{
			if (_count < MaxActiveSlots)
			{
				idx = _count++;
				return true;
			}

			// Evict first non-held slot (swap-remove then append).
			for (int i = 0; i < _count; i++)
			{
				if (_slots[i].Held)
					continue;
				FreeSlot(i);
				break;
			}

			if (_count < MaxActiveSlots)
			{
				idx = _count++;
				return true;
			}

			idx = -1;
			return false;
		}

		private void FreeSlot(int idx)
		{
			if (idx < 0 || idx >= _count)
				return;

			long seq = _slots[idx].Sequence;
			ushort scan = _slots[idx].Scan;
			bool ext = _slots[idx].Extended;
			uint vk = _slots[idx].Vk;
			_bySequence.Remove(seq);
			if (scan != 0)
				_byPhysical.Remove(PackScan(scan, ext));
			if (vk != 0)
				_byPhysical.Remove(PackVk(vk));

			int last = _count - 1;
			if (idx != last)
			{
				_slots[idx] = _slots[last];
				_bySequence[_slots[idx].Sequence] = idx;
				if (_slots[idx].Scan != 0)
					_byPhysical[PackScan(_slots[idx].Scan, _slots[idx].Extended)] = idx;
				else if (_slots[idx].Vk != 0)
					_byPhysical[PackVk(_slots[idx].Vk)] = idx;
			}

			_slots[last] = default;
			_count--;
		}

		private void Diag(string line)
		{
			DiagSink?.Invoke(line);
		}

		private static bool ContainsIgnoreCase(List<string> list, string value)
		{
			for (int i = 0; i < list.Count; i++)
			{
				if (string.Equals(list[i], value, StringComparison.OrdinalIgnoreCase))
					return true;
			}

			return false;
		}
	}
}
