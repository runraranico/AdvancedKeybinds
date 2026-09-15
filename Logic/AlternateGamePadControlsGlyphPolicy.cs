using System;
using System.Collections.Generic;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Display-only: Controls GamePad rows should feed GenInput Buttons names (for [g:N] glyphs),
	/// not a single "X / R2" text cell. Does not change save tokens or input.
	/// </summary>
	public static class AlternateGamePadControlsGlyphPolicy
	{
		public const string VanillaGenInputSeparator = "/";

		public enum EntryKind
		{
			/// <summary>Buttons.ToString name suitable for GlyphTagHandler.GenerateTag.</summary>
			ButtonsName = 0,
			/// <summary>Short logical fallback text (R2, LS ↑, …).</summary>
			FallbackText = 1
		}

		public readonly struct DisplayEntry
		{
			public readonly string Value;
			public readonly EntryKind Kind;

			public DisplayEntry(string value, EntryKind kind)
			{
				Value = value ?? string.Empty;
				Kind = kind;
			}

			public bool IsEmpty => string.IsNullOrEmpty(Value);
		}

		/// <summary>
		/// True when Controls GamePad (game or menu) row should use two GenInput cells for Alternate OR.
		/// Keyboard rows and mixed KB/Pad must not use this path.
		/// </summary>
		public static bool ShouldUseGlyphPairGenInput(
			bool isGamePadControlsRow,
			bool hasAlternate,
			bool primaryIsPadToken,
			bool alternateIsPadToken)
		{
			if (!isGamePadControlsRow || !hasAlternate)
				return false;
			if (!primaryIsPadToken || !alternateIsPadToken)
				return false;
			return true;
		}

		/// <summary>
		/// Builds two GenInput entries: [0]=alternate (left), [1]=primary (right).
		/// Prefers Buttons names; per-side fallback text if Buttons name unavailable.
		/// </summary>
		public static bool TryBuildGenInputPair(
			string primaryPadToken,
			string alternatePadToken,
			Func<string, string> tryButtonsNameOrNull,
			Func<string, string> formatFallbackDisplay,
			out DisplayEntry left,
			out DisplayEntry right)
		{
			left = default;
			right = default;
			if (string.IsNullOrEmpty(primaryPadToken) || string.IsNullOrEmpty(alternatePadToken))
				return false;
			if (tryButtonsNameOrNull == null || formatFallbackDisplay == null)
				return false;

			left = ResolveEntry(alternatePadToken, tryButtonsNameOrNull, formatFallbackDisplay);
			right = ResolveEntry(primaryPadToken, tryButtonsNameOrNull, formatFallbackDisplay);
			if (left.IsEmpty && right.IsEmpty)
				return false;
			return true;
		}

		/// <summary>List form for GenInput: never joins into one cell.</summary>
		public static List<string> ToGenInputList(in DisplayEntry left, in DisplayEntry right)
		{
			var list = new List<string>(2);
			list.Add(left.IsEmpty ? string.Empty : left.Value);
			list.Add(right.IsEmpty ? string.Empty : right.Value);
			return list;
		}

		/// <summary>
		/// Overlay / ChatManager path: glyph tag when ButtonsName, else raw fallback text.
		/// Separator matches vanilla GenInput (<c>/</c>, no spaces).
		/// </summary>
		public static string JoinForChatOverlay(
			in DisplayEntry left,
			in DisplayEntry right,
			Func<string, string> buttonsNameToGlyphTagOrPassthrough)
		{
			string l = FormatSide(left, buttonsNameToGlyphTagOrPassthrough);
			string r = FormatSide(right, buttonsNameToGlyphTagOrPassthrough);
			if (string.IsNullOrEmpty(l))
				return r ?? string.Empty;
			if (string.IsNullOrEmpty(r))
				return l;
			return l + VanillaGenInputSeparator + r;
		}

		/// <summary>Legacy single-string text (ComboPanel / non-glyph). Uses " / ".</summary>
		public static string JoinAsPlainText(in DisplayEntry left, in DisplayEntry right)
			=> AlternateGamePadBindingPolicy.FormatAlternateOverPrimaryDisplay(
				left.Value,
				right.Value);

		public static bool IsGamePadControlsInputModeName(string inputModeName)
		{
			if (string.IsNullOrEmpty(inputModeName))
				return false;
			return string.Equals(inputModeName, "XBoxGamepad", StringComparison.Ordinal)
				|| string.Equals(inputModeName, "XBoxGamepadUI", StringComparison.Ordinal);
		}

		private static DisplayEntry ResolveEntry(
			string padToken,
			Func<string, string> tryButtonsNameOrNull,
			Func<string, string> formatFallbackDisplay)
		{
			string buttons = tryButtonsNameOrNull(padToken);
			if (!string.IsNullOrEmpty(buttons))
				return new DisplayEntry(buttons, EntryKind.ButtonsName);

			string fallback = formatFallbackDisplay(padToken);
			if (string.IsNullOrEmpty(fallback))
				fallback = padToken ?? string.Empty;
			return new DisplayEntry(fallback, EntryKind.FallbackText);
		}

		private static string FormatSide(
			in DisplayEntry entry,
			Func<string, string> buttonsNameToGlyphTagOrPassthrough)
		{
			if (entry.IsEmpty)
				return string.Empty;
			if (entry.Kind == EntryKind.ButtonsName && buttonsNameToGlyphTagOrPassthrough != null)
			{
				string tagged = buttonsNameToGlyphTagOrPassthrough(entry.Value);
				return string.IsNullOrEmpty(tagged) ? entry.Value : tagged;
			}
			return entry.Value;
		}
	}
}
