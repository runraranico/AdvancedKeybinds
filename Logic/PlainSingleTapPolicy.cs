using System;
using System.Collections.Generic;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// A案: Vanilla が <c>Keys.ToString()</c> で照合できる単押しだけ plain。
	/// 測定／フォールバック／hook 管理トークンは Advanced Engine → Triggers。
	/// 判定は接頭辞・固定名の比較のみ（毎フレームの LINQ / 文字列生成なし）。
	/// </summary>
	public static class PlainSingleTapPolicy
	{
		public const string PhysicalPrefix = "Physical:";
		public const string MeasuredPrefix = "Measured:";

		/// <summary>
		/// Tokens that must never be written into vanilla KeyConfiguration as the sole binding string.
		/// </summary>
		public static bool IsNonVanillaConfigurationToken(string token)
		{
			if (string.IsNullOrWhiteSpace(token))
				return false;

			token = token.Trim();

			if (PhysicalKeyToken.IsFallbackToken(token))
				return true;

			// Gamepad logical tokens — never written as sole vanilla KeyConfiguration strings.
			if (GamePadToken.IsPadToken(token))
				return true;

			// Identity / diagnostic prefixes (not Keys.ToString names).
			if (token.StartsWith(PhysicalKeyIdentity.Prefix, StringComparison.OrdinalIgnoreCase)
				|| token.StartsWith(PhysicalPrefix, StringComparison.OrdinalIgnoreCase)
				|| token.StartsWith(MeasuredPrefix, StringComparison.OrdinalIgnoreCase))
				return true;

			// Optional Keys alias form — vanilla matches bare enum names only.
			if (PhysicalKeyToken.IsKeyAliasPrefix(token))
				return true;

			return false;
		}

		/// <summary>
		/// Single-key taps that Advanced must own (hook-swallowed / NLS / locks /
		/// IME Keys names that Terraria/tML KeyboardState cannot fire).
		/// Hangul/Hanja share the same saved Keys names as <c>Kana</c>/<c>Kanji</c> (VK 0x15/0x19).
		/// </summary>
		public static bool IsHookManagedTapToken(string token)
		{
			if (string.IsNullOrEmpty(token))
				return false;

			if (StableKeyToken.IsHankakuZenkakuToken(token)
				|| string.Equals(token, StableKeyToken.ScrollLockToken, StringComparison.OrdinalIgnoreCase)
				|| string.Equals(token, "CapsLock", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(token, "NumLock", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(token, "LeftWindows", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(token, "RightWindows", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(token, "ImeNoConvert", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(token, "ImeConvert", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(token, "Kana", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(token, "Kanji", StringComparison.OrdinalIgnoreCase))
				return true;

			return false;
		}

		/// <summary>
		/// True → vanilla owns the binding. False → BindingInputEngine owns it.
		/// </summary>
		public static bool IsPlainSingleTap(bool hold, IReadOnlyList<string> keys)
		{
			if (hold || keys == null || keys.Count != 1)
				return false;

			return IsPlainSingleTapSole(hold: false, keys[0]);
		}

		/// <summary>
		/// Identifies Escape-equivalent capture tokens for diagnostics / policy helpers.
		/// Does NOT force plain ownership — Escape-plain caused normal ESC death after B+R.
		/// Do NOT treat SC:0x1B (JIS OemOpenBrackets) as Escape.
		/// </summary>
		public static bool IsEscapeEquivalentToken(string key)
		{
			if (string.IsNullOrEmpty(key))
				return false;

			if (string.Equals(key, "Escape", StringComparison.OrdinalIgnoreCase))
				return true;

			if (string.Equals(key, "VK:0x1B", StringComparison.OrdinalIgnoreCase))
				return true;

			if (key.Length >= 10
				&& key.StartsWith("SC:0x01:", StringComparison.OrdinalIgnoreCase))
				return true;

			return false;
		}

		/// <summary>Zero-alloc sole-key form (same rules as <see cref="IsPlainSingleTap"/> for one key).</summary>
		public static bool IsPlainSingleTapSole(bool hold, string key)
		{
			if (hold || string.IsNullOrEmpty(key))
				return false;

			if (StableKeyToken.IsMouseToken(key))
				return false;

			if (IsHookManagedTapToken(key))
				return false;

			if (IsNonVanillaConfigurationToken(key))
				return false;

			return true;
		}

		public static bool RequiresAdvancedStorage(bool hold, IReadOnlyList<string> keys, bool isEmpty)
		{
			if (isEmpty)
				return false;
			return !IsPlainSingleTap(hold, keys);
		}
	}
}
