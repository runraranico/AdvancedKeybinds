using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework.Input;

namespace AdvancedKeybinds.Binding
{
	/// <summary>Validation rules for bindings that cannot work reliably with the current input model.</summary>
	internal static class BindingRestrictions
	{
		public const string ScrollLockToken = "ScrollLock";

		public static bool IsToggleLockKey(Keys key)
			=> key == Keys.CapsLock || key == Keys.NumLock;

		public static bool IsCapsLockToken(string token)
			=> string.Equals(token, "CapsLock", StringComparison.OrdinalIgnoreCase);

		public static bool IsToggleLockToken(string token)
		{
			if (string.IsNullOrEmpty(token))
				return false;

			if (string.Equals(token, ScrollLockToken, StringComparison.OrdinalIgnoreCase))
				return true;

			if (!KeyToken.TryParse(token, out Keys key, out bool isMouse, out _))
				return false;

			return !isMouse && IsToggleLockKey(key);
		}

		public static bool IsWindowsKeyToken(string token)
		{
			return string.Equals(token, "LeftWindows", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(token, "RightWindows", StringComparison.OrdinalIgnoreCase);
		}

		/// <summary>Single-key taps that are swallowed by the keyboard hook and need advanced storage.</summary>
		public static bool IsHookManagedTapToken(string token)
			=> IsToggleLockToken(token) || IsWindowsKeyToken(token);

		public static bool UsesToggleLockHold(ComboBinding binding)
		{
			if (binding == null || !binding.Hold)
				return false;

			List<string> keys = binding.Keys;
			if (keys.Count != 1)
				return false;

			string key = keys[0];
			if (IsCapsLockToken(key))
				return !KeyboardLayoutHelper.IsCapsLockHoldViable();

			return IsToggleLockToken(key);
		}

		public static bool SupportsHold(ComboBinding binding)
		{
			if (binding == null)
				return false;

			List<string> keys = binding.Keys;
			if (keys.Count != 1)
				return false;

			string key = keys[0];
			if (IsCapsLockToken(key))
				return KeyboardLayoutHelper.IsCapsLockHoldViable();

			return !IsToggleLockToken(key);
		}

		/// <summary>ANSI hardware can hold Caps Lock physically; JIS 英数 cannot.</summary>
		public static bool SupportsCapsLockPhysicalHold(string token, ComboBinding binding)
		{
			if (binding == null || !binding.Hold || binding.Keys.Count != 1)
				return false;

			return IsCapsLockToken(token) && KeyboardLayoutHelper.IsCapsLockHoldViable();
		}

		/// <summary>
		/// True when the binding is LeftClick (Mouse1) alone. Conflicts with UI clicking unless
		/// the user confirms in the combo panel, or the action is vanilla Use Item (<c>MouseLeft</c>).
		/// </summary>
		public static bool IsLeftClickOnly(string actionId, ComboBinding binding)
		{
			if (binding == null)
				return false;

			if (string.Equals(actionId, "MouseLeft", StringComparison.OrdinalIgnoreCase))
				return false;

			List<string> keys = binding.Keys;
			return keys.Count == 1
				&& string.Equals(keys[0], "Mouse1", StringComparison.OrdinalIgnoreCase);
		}

		/// <summary>Obsolete name; prefer <see cref="IsLeftClickOnly"/>.</summary>
		public static bool RejectsLeftClickOnly(string actionId, ComboBinding binding)
			=> IsLeftClickOnly(actionId, binding);

		public static bool RejectsBinding(string actionId, ComboBinding binding)
		{
			// LeftClick-alone is allowed after an in-panel confirmation; do not hard-reject here.
			return UsesToggleLockHold(binding);
		}

		public static void ClearUnsupportedHoldFlags(ComboBinding binding)
		{
			if (binding == null || SupportsHold(binding))
				return;

			binding.Hold = false;
			binding.HoldOnRelease = false;
		}
	}
}
