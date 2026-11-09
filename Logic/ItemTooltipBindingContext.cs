using System;
using System.Collections.Generic;

namespace AdvancedKeybinds.Core
{
	/// <summary>Ephemeral display provenance; never substitutes or writes assigned input keys.</summary>
	internal sealed class ItemTooltipBindingContext : IDisposable
	{
		[ThreadStatic] private static ItemTooltipBindingContext _current;
		private readonly ItemTooltipBindingContext _parent;
		private readonly List<Assignment> _assignments = new();
		private readonly HashSet<string> _unresolvedMarkers = new(StringComparer.Ordinal);
		private bool _disposed;
		private string _tooltipText;
		private Func<IReadOnlyList<string>, string, string> _resolveUnobserved;
		internal static ItemTooltipBindingContext Current => _current;

		private ItemTooltipBindingContext()
		{
			_parent = _current;
			_current = this;
		}

		internal static ItemTooltipBindingContext Begin() => new();
		internal void SetUnobservedResolver(Func<IReadOnlyList<string>, string, string> resolver)
			=> _resolveUnobserved = resolver;

		internal void MarkUnresolved(IReadOnlyList<string> keys)
		{
			if (keys == null) return;
			for (int i = 0; i < keys.Count; i++)
				if (AdvancedVanillaOwnershipMarker.IsMarker(keys[i]))
					_unresolvedMarkers.Add(keys[i]);
		}

		internal void Record(string actionId, IReadOnlyList<string> assignedKeys, string display)
		{
			if (string.IsNullOrEmpty(actionId) || string.IsNullOrEmpty(display)
				|| assignedKeys == null || assignedKeys.Count == 0)
				return;

			// Mixed native OR lists are not a proven AND sequence.
			var snapshot = new string[assignedKeys.Count];
			for (int i = 0; i < snapshot.Length; i++)
			{
				if (!AdvancedVanillaOwnershipMarker.IsMarker(assignedKeys[i]))
					return;
				snapshot[i] = assignedKeys[i];
			}
			foreach (Assignment old in _assignments)
				if (old.ActionId == actionId && old.Display == display && Matches(old.Keys, snapshot))
					return;
			_assignments.Add(new Assignment(actionId, snapshot, display));
		}

		internal string Rewrite(string text, Func<string, string> resolveKey)
			=> BindingDisplayText.ReplaceOwnershipMarkersInDisplayText(text, resolveKey,
				markers => ResolveRun(markers, _tooltipText ?? text));

		internal void PrepareRewrite(IReadOnlyList<string> text) => _tooltipText = string.Join("\n", text);

		internal string ResolveRun(IReadOnlyList<string> markers)
			=> ResolveRun(markers, null);

		internal string RewriteGeneratedInput(IReadOnlyList<string> markers, string display)
		{
			if (markers.Count == 1 && _resolveUnobserved != null)
			{
				bool observed = false;
				foreach (Assignment assignment in _assignments)
					observed |= string.Equals(markers[0], assignment.Keys[0], StringComparison.Ordinal);
				if (!observed)
					return markers[0]; // Defer until the complete tooltip can exclude other slots.
			}
			return ResolveRun(markers) ?? display;
		}

		private string ResolveRun(IReadOnlyList<string> markers, string text)
		{
			foreach (string marker in markers)
				if (_unresolvedMarkers.Contains(marker))
					return null;
			string result = null;
			bool observedMatch = false;
			bool alreadyShown = false;
			foreach (Assignment assignment in _assignments)
			{
				bool matches = markers.Count == 1
					? string.Equals(markers[0], assignment.Keys[0], StringComparison.Ordinal)
					: Matches(markers, assignment.Keys);
				if (!matches)
					continue;
				observedMatch = true;
				if (result != null && !string.Equals(result, assignment.Display, StringComparison.Ordinal))
					return null; // Never guess between different actions sharing their first key.
				result = assignment.Display;
				if (markers.Count == 1 && text != null && HasOtherSlotsInText(assignment, text))
					alreadyShown = true;
			}
			// Presentation cannot eliminate a competing action and manufacture certainty.
			if (observedMatch)
				return alreadyShown ? null : result;
			return _resolveUnobserved?.Invoke(markers, text);
		}

		private static bool HasOtherSlotsInText(Assignment assignment, string text)
		{
			for (int i = 1; i < assignment.Keys.Length; i++)
				if (BindingDisplayText.ContainsOwnershipMarker(text, assignment.Keys[i]))
					return true;
			return false;
		}

		private static bool Matches(IReadOnlyList<string> left, IReadOnlyList<string> right)
		{
			if (left.Count != right.Count)
				return false;
			for (int i = 0; i < left.Count; i++)
				if (!string.Equals(left[i], right[i], StringComparison.Ordinal))
					return false;
			return true;
		}

		public void Dispose()
		{
			if (_disposed)
				return;
			_current = _parent;
			_assignments.Clear();
			_unresolvedMarkers.Clear();
			_resolveUnobserved = null;
			_disposed = true;
		}

		private sealed class Assignment
		{
			internal readonly string ActionId;
			internal readonly string[] Keys;
			internal readonly string Display;
			internal Assignment(string actionId, string[] keys, string display)
			{
				ActionId = actionId;
				Keys = keys;
				Display = display;
			}
		}
	}
}
