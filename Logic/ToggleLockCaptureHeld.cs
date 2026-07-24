using System;
using System.Collections.Generic;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Capture held-state policy for Caps/Num/Scroll Lock.
	/// Physical Down/Up must never be inferred from OS toggle / LED ON.
	/// </summary>
	public static class ToggleLockCaptureHeld
	{
		public const string CapsLockToken = "CapsLock";
		public const string NumLockToken = "NumLock";
		public static string ScrollLockToken => StableKeyToken.ScrollLockToken;

		/// <summary>
		/// Correct rule: only physical Down counts as held.
		/// <paramref name="toggleOrLedOn"/> is accepted only so callers can document LED state;
		/// it must not affect the result.
		/// </summary>
		public static bool CountsAsHeld(bool physicalDown, bool toggleOrLedOn)
		{
			_ = toggleOrLedOn;
			return physicalDown;
		}

		/// <summary>
		/// Pre-fix defect class (FNA <c>IsKeyDown(NumLock/CapsLock)</c> follows toggle/LED):
		/// treating LED ON as held. Used only by regression tests that prove FAIL before the fix.
		/// </summary>
		public static bool LegacyMiscountsToggleAsHeld(bool physicalDown, bool toggleOrLedOn)
			=> physicalDown || toggleOrLedOn;

		public static bool IsToggleLockToken(string token)
		{
			if (string.IsNullOrEmpty(token))
				return false;

			return string.Equals(token, CapsLockToken, StringComparison.OrdinalIgnoreCase)
				|| string.Equals(token, NumLockToken, StringComparison.OrdinalIgnoreCase)
				|| string.Equals(token, ScrollLockToken, StringComparison.OrdinalIgnoreCase);
		}

		/// <summary>
		/// Builds a capture held set from a base (non-toggle) snapshot plus per-lock physical/LED bits.
		/// LED bits never add tokens under the correct policy.
		/// </summary>
		public static HashSet<string> BuildHeldSnapshot(
			IEnumerable<string> baseHeld,
			bool capsPhysicalDown,
			bool capsToggleOrLedOn,
			bool numPhysicalDown,
			bool numToggleOrLedOn,
			bool scrollPhysicalDown,
			bool scrollToggleOrLedOn,
			bool useLegacyMisread = false)
		{
			var held = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			if (baseHeld != null)
			{
				foreach (string raw in baseHeld)
				{
					string c = StableKeyToken.CanonicalizeOrNull(raw);
					if (c != null && !IsToggleLockToken(c))
						held.Add(c);
				}
			}

			Func<bool, bool, bool> rule = useLegacyMisread
				? LegacyMiscountsToggleAsHeld
				: CountsAsHeld;

			if (rule(capsPhysicalDown, capsToggleOrLedOn))
				held.Add(CapsLockToken);
			if (rule(numPhysicalDown, numToggleOrLedOn))
				held.Add(NumLockToken);
			if (rule(scrollPhysicalDown, scrollToggleOrLedOn))
				held.Add(ScrollLockToken);

			return held;
		}
	}
}
