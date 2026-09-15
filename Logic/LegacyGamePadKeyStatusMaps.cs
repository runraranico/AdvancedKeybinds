using System;
using System.Collections.Generic;

namespace AdvancedKeybinds.Core
{
	public static class LegacyGamePadKeyStatusMaps
	{
		public static Dictionary<string, List<string>> Clone(Dictionary<string, List<string>> source)
		{
			var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
			if (source == null)
				return result;
			foreach (KeyValuePair<string, List<string>> pair in source)
				result[pair.Key] = pair.Value == null ? null : new List<string>(pair.Value);
			return result;
		}

		public static bool MapsEqual(
			Dictionary<string, List<string>> left,
			Dictionary<string, List<string>> right)
		{
			if (ReferenceEquals(left, right))
				return true;
			if (left == null || right == null || left.Count != right.Count)
				return false;
			foreach (KeyValuePair<string, List<string>> pair in left)
			{
				if (!right.TryGetValue(pair.Key, out List<string> other)
					|| !SequenceEqual(pair.Value, other))
					return false;
			}
			return true;
		}

		public static void ApplyMutation(
			Dictionary<string, List<string>> planMode,
			Dictionary<string, List<string>> upsert,
			HashSet<string> remove)
		{
			if (planMode == null)
				return;
			if (remove != null)
			{
				foreach (string key in remove)
					planMode.Remove(key);
			}
			if (upsert != null)
			{
				foreach (KeyValuePair<string, List<string>> pair in upsert)
					planMode[pair.Key] = pair.Value == null ? null : new List<string>(pair.Value);
			}
		}

		public static void RestoreFull(
			Dictionary<string, List<string>> target,
			Dictionary<string, List<string>> snapshot)
		{
			if (target == null)
				return;
			target.Clear();
			if (snapshot == null)
				return;
			foreach (KeyValuePair<string, List<string>> pair in snapshot)
				target[pair.Key] = pair.Value == null ? null : new List<string>(pair.Value);
		}

		/// <summary>
		/// After targeted ApplyMutation: migrated targets match expected after-map;
		/// out-of-scope actions keep pre-migration key sets and token sequences.
		/// </summary>
		public static bool VerifyTargetsAndOutOfScope(
			Dictionary<string, List<string>> live,
			ModeMutation mutation,
			Dictionary<string, List<string>> expectedAfterFull)
		{
			if (mutation == null)
				return MapsEqual(live, expectedAfterFull);
			live ??= new Dictionary<string, List<string>>(StringComparer.Ordinal);
			foreach (string remove in mutation.RemoveActions)
			{
				if (live.ContainsKey(remove))
					return false;
			}
			foreach (KeyValuePair<string, List<string>> pair in mutation.UpsertActions)
			{
				if (!live.TryGetValue(pair.Key, out List<string> tokens)
					|| !MapsEqual(
						new Dictionary<string, List<string>>(StringComparer.Ordinal) { [pair.Key] = tokens },
						new Dictionary<string, List<string>>(StringComparer.Ordinal) { [pair.Key] = pair.Value }))
					return false;
			}
			foreach (KeyValuePair<string, List<string>> pair in mutation.OutOfScopeBefore)
			{
				if (!live.TryGetValue(pair.Key, out List<string> tokens)
					|| !MapsEqual(
						new Dictionary<string, List<string>>(StringComparer.Ordinal) { [pair.Key] = tokens },
						new Dictionary<string, List<string>>(StringComparer.Ordinal) { [pair.Key] = pair.Value }))
					return false;
			}
			// Keys present only in expected after that are migrated must exist on live.
			if (expectedAfterFull != null)
			{
				foreach (string action in mutation.MigratedActionIds)
				{
					bool expect = expectedAfterFull.ContainsKey(action);
					bool have = live.ContainsKey(action);
					if (expect != have)
						return false;
					if (expect
						&& !MapsEqual(
							new Dictionary<string, List<string>>(StringComparer.Ordinal) { [action] = live[action] },
							new Dictionary<string, List<string>>(StringComparer.Ordinal) { [action] = expectedAfterFull[action] }))
						return false;
				}
			}
			return true;
		}

		public static List<string> StripOrphanMarkersOnly(IReadOnlyList<string> tokens)
		{
			var kept = new List<string>();
			if (tokens == null)
				return kept;
			for (int i = 0; i < tokens.Count; i++)
			{
				if (!AdvancedVanillaOwnershipMarker.IsMarker(tokens[i]))
					kept.Add(tokens[i]);
			}
			return kept;
		}

		private static bool SequenceEqual(IReadOnlyList<string> left, IReadOnlyList<string> right)
		{
			if (ReferenceEquals(left, right))
				return true;
			if (left == null || right == null || left.Count != right.Count)
				return false;
			for (int i = 0; i < left.Count; i++)
			{
				if (!string.Equals(left[i], right[i], StringComparison.Ordinal))
					return false;
			}
			return true;
		}
	}
}
