using System;
using System.Collections.Generic;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Shared held/just-pressed matching used by BindingInputEngine.
	/// Order: exact string match → PhysicalKeyIdentity.AreSamePhysical.
	/// Same-frame pair cache reduces GC/CPU without changing match results.
	/// </summary>
	public static class BindingPhysicalHeldMatch
	{
		private static readonly Dictionary<string, bool> PairCache = new(StringComparer.OrdinalIgnoreCase);
		private static int _cacheFrame = -1;

		/// <summary>Clear pair cache at the start of each BindingInputEngine.Update frame.</summary>
		public static void BeginFrame(int frameId)
		{
			if (frameId == _cacheFrame)
				return;
			_cacheFrame = frameId;
			PairCache.Clear();
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

			if (PairCache.TryGetValue(key, out bool cached))
				return cached;

			bool same = PhysicalKeyIdentity.AreSamePhysical(a, b);
			PairCache[key] = same;
			return same;
		}
	}
}
