using System;
using System.Collections.Generic;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Pure AND gate for multi-slot combos. All non-empty required tokens must be down.
	/// </summary>
	public static class ComboAndGatePolicy
	{
		/// <summary>
		/// True only when every entry in <paramref name="keys"/> is non-empty and <paramref name="isDown"/> returns true.
		/// Empty key list → false (nothing to arm).
		/// </summary>
		public static bool AllRequiredDown(IReadOnlyList<string> keys, Func<string, bool> isDown)
		{
			if (keys == null || keys.Count == 0 || isDown == null)
				return false;

			for (int i = 0; i < keys.Count; i++)
			{
				string key = keys[i];
				if (string.IsNullOrEmpty(key) || !isDown(key))
					return false;
			}

			return true;
		}
	}
}
