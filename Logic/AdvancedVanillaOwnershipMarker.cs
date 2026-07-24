using System;
using System.Collections.Generic;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Advanced-owned bindings must not leave vanilla <c>KeyStatus</c> empty.
	/// Empty lists are treated as "unbound" by many ModKeybind consumers (skip JustPressed,
	/// or re-apply RegisterKeybind defaults), even while AK BindingStorage + UI show a binding.
	/// <para>
	/// Markers use a prefix vanilla cannot match as Keys/Mouse, so PlayerInput will not fire them;
	/// AK <c>BindingInputEngine</c> remains the sole activation path. Prefixing also prevents
	/// default-restoration logic from seeing a raw <c>Mouse3</c> (or other fireable token) and
	/// hijacking edge detection away from the real advanced binding.
	/// </para>
	/// <para>
	/// Residual risks (documented): <c>Enum.Parse</c> on marker strings throws (rare mods);
		/// tooltips that print raw GetAssignedKeys may show <c>AKAdv:…</c>; markers persist in the
		/// input profile until AK Unload writes a vanilla-fireable key (or clears) and saves profiles.
	/// </para>
	/// </summary>
	public static class AdvancedVanillaOwnershipMarker
	{
		public const string Prefix = "AKAdv:";
		public const string OwnedFallback = Prefix + "Owned";

		public static bool IsMarker(string token)
		{
			return !string.IsNullOrEmpty(token)
				&& token.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);
		}

		/// <summary>
		/// Build KeyStatus entries for an advanced combo. Never returns an empty list when
		/// <paramref name="bindingKeys"/> has at least one non-empty slot (or always OwnedFallback).
		/// </summary>
		public static List<string> BuildMarkers(IReadOnlyList<string> bindingKeys)
		{
			var markers = new List<string>(bindingKeys == null ? 1 : Math.Max(1, bindingKeys.Count));
			if (bindingKeys != null)
			{
				for (int i = 0; i < bindingKeys.Count; i++)
				{
					string key = bindingKeys[i];
					if (string.IsNullOrEmpty(key))
						continue;

					if (IsMarker(key))
						markers.Add(key);
					else
						markers.Add(Prefix + key);
				}
			}

			if (markers.Count == 0)
				markers.Add(OwnedFallback);

			return markers;
		}

		/// <summary>
		/// True when a KeyStatus list should be treated as "has a binding" by consumers that
		/// ignore whitespace / <c>None</c> (same idea as typical ModKeybind HasBinding checks).
		/// </summary>
		public static bool IsEffectivelyBound(IReadOnlyList<string> keyStatusEntries)
		{
			if (keyStatusEntries == null || keyStatusEntries.Count == 0)
				return false;

			for (int i = 0; i < keyStatusEntries.Count; i++)
			{
				string existing = keyStatusEntries[i];
				if (string.IsNullOrWhiteSpace(existing))
					continue;
				if (existing.Equals("None", StringComparison.OrdinalIgnoreCase))
					continue;
				return true;
			}

			return false;
		}

		/// <summary>
		/// True when the sole non-None entry equals <paramref name="token"/> (e.g. Mouse3-only checks).
		/// Markers never equal raw Mouse/Keys names.
		/// </summary>
		public static bool IsSoleRawToken(IReadOnlyList<string> keyStatusEntries, string token)
		{
			if (string.IsNullOrEmpty(token) || keyStatusEntries == null)
				return false;

			string found = null;
			for (int i = 0; i < keyStatusEntries.Count; i++)
			{
				string existing = keyStatusEntries[i];
				if (string.IsNullOrWhiteSpace(existing))
					continue;
				if (existing.Equals("None", StringComparison.OrdinalIgnoreCase))
					continue;
				if (found != null)
					return false;
				found = existing;
			}

			return found != null
				&& string.Equals(found, token, StringComparison.OrdinalIgnoreCase);
		}
		/// <summary>
		/// Zero-alloc: does <paramref name="keyStatusEntry"/> match the marker that would be written for
		/// <paramref name="bindingToken"/>? Avoids concatenating <c>AKAdv:</c> + token.
		/// </summary>
		public static bool MarkerMatchesBindingToken(string keyStatusEntry, string bindingToken)
		{
			if (string.IsNullOrEmpty(keyStatusEntry) || string.IsNullOrEmpty(bindingToken))
				return false;

			if (IsMarker(bindingToken))
				return string.Equals(keyStatusEntry, bindingToken, StringComparison.OrdinalIgnoreCase);

			if (!IsMarker(keyStatusEntry))
				return false;

			int prefixLen = Prefix.Length;
			if (keyStatusEntry.Length != prefixLen + bindingToken.Length)
				return false;

			return keyStatusEntry.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
				&& string.Compare(
					keyStatusEntry, prefixLen,
					bindingToken, 0, bindingToken.Length,
					StringComparison.OrdinalIgnoreCase) == 0;
		}
	}
}
