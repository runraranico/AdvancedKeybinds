using System;
using System.Collections.Generic;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Shared held/just-pressed matching used by BindingInputEngine.
	/// Order: exact string match → PhysicalKeyIdentity.AreSamePhysical.
	/// Same-frame pair cache reduces GC/CPU without changing match results.
	/// Alias forms of one physical key (Keys / VK / SC, or Spain layout LANGID family
	/// detection paths that still resolve to the same PHYS id) count as one held key.
	/// Pair cache is thread-local so Core.Tests parallel sessions cannot corrupt it.
	/// </summary>
	public static class BindingPhysicalHeldMatch
	{
		[ThreadStatic]
		private static Dictionary<string, bool> _pairCache;

		[ThreadStatic]
		private static int _cacheFrame;

		private static Dictionary<string, bool> PairCache
			=> _pairCache ??= new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

		/// <summary>Clear pair cache at the start of each BindingInputEngine.Update frame.</summary>
		public static void BeginFrame(int frameId)
		{
			// ThreadStatic int defaults to 0; use sentinel so first BeginFrame(0) still clears.
			if (_pairCache != null && frameId == _cacheFrame)
				return;
			_cacheFrame = frameId;
			PairCache.Clear();
		}

		/// <summary>
		/// Number of distinct physical keys in <paramref name="pressed"/> (aliases collapse to 1).
		/// Used so AND condition counting never treats the same key twice.
		/// </summary>
		public static int CountDistinctPhysical(IEnumerable<string> pressed)
		{
			if (pressed == null)
				return 0;

			var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			int unresolved = 0;
			foreach (string token in pressed)
			{
				if (string.IsNullOrEmpty(token))
					continue;
				if (PhysicalKeyIdentity.TryResolve(token, out string id) && !string.IsNullOrEmpty(id))
					ids.Add(id);
				else
					unresolved++;
			}

			return ids.Count + unresolved;
		}

		/// <summary>
		/// Removes redundant aliases of the same physical key from <paramref name="pressed"/>,
		/// keeping the first-seen token per physical id. Does not invent tokens.
		/// </summary>
		public static void CollapsePhysicalAliasesInPlace(HashSet<string> pressed)
		{
			if (pressed == null || pressed.Count < 2)
				return;

			var keep = new List<string>(pressed.Count);
			var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (string token in pressed)
			{
				if (string.IsNullOrEmpty(token))
					continue;
				if (PhysicalKeyIdentity.TryResolve(token, out string id) && !string.IsNullOrEmpty(id))
				{
					if (!seenIds.Add(id))
						continue;
				}

				keep.Add(token);
			}

			if (keep.Count == pressed.Count)
				return;

			pressed.Clear();
			for (int i = 0; i < keep.Count; i++)
				pressed.Add(keep[i]);
		}

		public static bool IsHeld(string token, IEnumerable<string> pressed)
		{
			if (string.IsNullOrEmpty(token) || pressed == null)
				return false;

			if (pressed is HashSet<string> set)
				return IsHeld(token, set);

			foreach (string held in pressed)
			{
				if (string.Equals(token, held, StringComparison.OrdinalIgnoreCase))
					return true;
			}

			foreach (string held in pressed)
			{
				if (AreSamePhysicalCached(token, held))
					return true;
			}

			return false;
		}

		/// <summary>HashSet fast path: exact Contains then physical identity scan.</summary>
		public static bool IsHeld(string token, HashSet<string> pressed)
		{
			if (string.IsNullOrEmpty(token) || pressed == null)
				return false;

			if (pressed.Contains(token))
				return true;

			foreach (string held in pressed)
			{
				if (AreSamePhysicalCached(token, held))
					return true;
			}

			return false;
		}

		public static bool IsJustPressed(string token, IEnumerable<string> pressedNow, IEnumerable<string> pressedPrevious)
		{
			bool now = IsHeld(token, pressedNow);
			bool was = IsHeld(token, pressedPrevious);
			return now && !was;
		}

		private static bool AreSamePhysicalCached(string a, string b)
		{
			if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
				return false;

			// Stable cache key independent of argument order.
			string key = string.Compare(a, b, StringComparison.OrdinalIgnoreCase) <= 0
				? a + "\n" + b
				: b + "\n" + a;

			Dictionary<string, bool> cache = PairCache;
			if (cache.TryGetValue(key, out bool cached))
				return cached;

			bool same = PhysicalKeyIdentity.AreSamePhysical(a, b);
			cache[key] = same;
			return same;
		}
	}
}
