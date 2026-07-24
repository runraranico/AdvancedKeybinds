using System;
using System.Collections.Generic;
using AdvancedKeybinds.Core;
using Microsoft.Xna.Framework.Input;

namespace AdvancedKeybinds.Binding
{
	/// <summary>Validation rules for bindings that cannot work reliably with the current input model.</summary>
	internal static class BindingRestrictions
	{
		public const string ScrollLockToken = StableKeyToken.ScrollLockToken;

		/// <summary>kbd106 「半角／全角」 — NLS edge token (not an XNA <see cref="Keys"/> name).</summary>
		public const string HankakuZenkakuToken = StableKeyToken.HankakuZenkakuToken;

		public static bool IsHankakuZenkakuToken(string token)
			=> string.Equals(token, HankakuZenkakuToken, StringComparison.OrdinalIgnoreCase);

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

		/// <summary>F11 is reserved (Terraria hideUI hotkey); cannot be bound in a combo slot.</summary>
		public static bool IsF11Token(string token)
		{
			if (string.IsNullOrEmpty(token))
				return false;

			if (string.Equals(token, "F11", StringComparison.OrdinalIgnoreCase))
				return true;

			return KeyToken.TryParse(token, out Keys key, out bool isMouse, out _)
				&& !isMouse
				&& key == Keys.F11;
		}

		/// <summary>
		/// Single-key taps that must drive Advanced Keybinds storage (hook-swallowed or NLS edge tokens
		/// that vanilla <c>Keys</c> cannot represent).
		/// </summary>
		public static bool IsHookManagedTapToken(string token)
			=> PlainSingleTapPolicy.IsHookManagedTapToken(token);

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

			if (StableKeyToken.IsMouseWheelToken(key))
				return false;

			if (IsToggleLockToken(key))
				return false;

			// NLS / IME keys: KEYUP is often missing or delayed — hold tracking is unreliable.
			if (IsUnreliableHoldToken(key))
				return false;

			return true;
		}

		/// <summary>
		/// Keys that register as taps but must not use HOLD (NLS KEYUP miss / IME instability).
		/// Covers JP 半角／かな and KO Hangul/Hanja (same internal tokens <c>Kana</c> / <c>Kanji</c>,
		/// and equivalent <c>VK:0x15</c> / <c>VK:0x19</c> fallback ids).
		/// </summary>
		public static bool IsUnreliableHoldToken(string token)
		{
			if (string.IsNullOrEmpty(token))
				return false;

			if (IsHankakuZenkakuToken(token))
				return true;

			if (PhysicalKeyToken.TryParseVk(token, out uint vk)
				&& (vk == 0x15 /* VK_KANA / Hangul */ || vk == 0x19 /* VK_KANJI / Hanja */))
				return true;

			if (!KeyToken.TryParse(token, out Keys key, out bool isMouse, out _))
				return false;

			return !isMouse && (key == Keys.Kana || key == Keys.Kanji);
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
