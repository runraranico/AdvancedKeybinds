using System;
using AdvancedKeybinds.Core;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Display-only: map Pad tokens to Terraria/FNA <c>Buttons.ToString()</c> names for
	/// <c>GlyphTagHandler</c> on Controls GamePad rows (XBoxGamepad / XBoxGamepadUI).
	/// Does not change ownership, KeyStatus, save tokens, or ComboPanel labels.
	/// Stick directions are intentionally excluded from <see cref="ControlsEditTargetPolicy.TryPadTokenToVanillaButtonsName"/>.
	/// </summary>
	public static class ControlsGamePadGlyphDisplayPolicy
	{
		public enum DisplayPath : byte
		{
			/// <summary>User custom display alias — keep text, do not force glyph.</summary>
			UserAlias = 0,
			/// <summary>Buttons name for GenInput / GlyphTagHandler.GenerateTag.</summary>
			GlyphButtonsName = 1,
			/// <summary>GamePadToken.FormatForDisplay (or raw) when no safe glyph mapping.</summary>
			FallbackText = 2,
			/// <summary>Not a Controls GamePad row — caller must not apply this policy.</summary>
			OutOfScope = 3
		}

		/// <summary>
		/// Target screens only: Terraria InputMode names for Game Controls / Menu Controls under the controller tab.
		/// Does not use localized UI strings.
		/// </summary>
		public static bool IsTargetControlsGamePadMode(string inputModeName)
			=> AlternateGamePadControlsGlyphPolicy.IsGamePadControlsInputModeName(inputModeName);

		public static bool IsTargetControlsGamePadModeOrdinal(int terrariaInputModeOrdinal)
		{
			// Live tModLoader: Keyboard=0, KeyboardUI=1, Mouse=2, XBoxGamepad=3, XBoxGamepadUI=4
			return terrariaInputModeOrdinal == 3 || terrariaInputModeOrdinal == 4;
		}

		/// <summary>
		/// Resolve a Pad:* token for Controls GamePad glyph display.
		/// Priority: userAlias → family plain label (PS) → Glyph Buttons name → fallback text.
		/// Family defaults to <see cref="GamePadControllerFamily.Unknown"/> (Xbox atlas).
		/// Product Controls rows should call the overload with <c>GamePadControllerFamilySnapshot.Current</c>.
		/// </summary>
		public static DisplayPath ResolveDisplay(
			string inputModeName,
			string padToken,
			string userAliasOrNull,
			out string displayValue)
			=> ResolveDisplay(
				inputModeName,
				padToken,
				userAliasOrNull,
				GamePadControllerFamily.Unknown,
				out displayValue);

		/// <summary>
		/// Same as <see cref="ResolveDisplay(string,string,string,out string)"/> with an explicit controller family.
		/// </summary>
		public static DisplayPath ResolveDisplay(
			string inputModeName,
			string padToken,
			string userAliasOrNull,
			GamePadControllerFamily family,
			out string displayValue)
		{
			displayValue = null;
			if (!IsTargetControlsGamePadMode(inputModeName))
			{
				displayValue = string.Empty;
				return DisplayPath.OutOfScope;
			}

			if (!string.IsNullOrWhiteSpace(userAliasOrNull))
			{
				displayValue = userAliasOrNull.Trim();
				return DisplayPath.UserAlias;
			}

			if (ControlsGamePadFamilyGlyphPolicy.TryResolveFamilyPlainLabel(
				    family, padToken, out string familyLabel))
			{
				displayValue = familyLabel;
				return DisplayPath.FallbackText;
			}

			if (TryResolveGlyphButtonsName(padToken, out string buttonsName))
			{
				displayValue = buttonsName;
				return DisplayPath.GlyphButtonsName;
			}

			displayValue = GamePadToken.FormatForDisplay(padToken);
			if (string.IsNullOrEmpty(displayValue))
				displayValue = padToken ?? string.Empty;
			return DisplayPath.FallbackText;
		}

		/// <summary>
		/// Buttons name only when the family uses the Xbox glyph atlas; otherwise false
		/// so GenInput / Alternate paths fall through to family plain labels.
		/// </summary>
		public static bool TryResolveGlyphButtonsNameForFamily(
			GamePadControllerFamily family,
			string padToken,
			out string buttonsName)
		{
			buttonsName = null;
			if (!GamePadControllerFamilyPolicy.UsesXboxGlyphAtlas(family)
				&& ControlsGamePadFamilyGlyphPolicy.TryResolveFamilyPlainLabel(family, padToken, out _))
				return false;
			return TryResolveGlyphButtonsName(padToken, out buttonsName);
		}

		/// <summary>Family-aware Controls fallback (PS labels, else <see cref="GamePadToken.FormatForDisplay"/>).</summary>
		public static string FormatControlsFallbackForFamily(
			GamePadControllerFamily family,
			string padToken)
		{
			if (ControlsGamePadFamilyGlyphPolicy.TryResolveFamilyPlainLabel(
				    family, padToken, out string familyLabel))
				return familyLabel;
			string text = GamePadToken.FormatForDisplay(padToken);
			return string.IsNullOrEmpty(text) ? (padToken ?? string.Empty) : text;
		}

		/// <summary>
		/// Buttons.ToString names accepted by Terraria GenInput / GlyphTagHandler.
		/// Stick directions map to LeftThumbstick* / RightThumbstick*; other pads use ownership-safe strip.
		/// Returns false when no safe glyph name exists (caller keeps text fallback).
		/// </summary>
		public static bool TryResolveGlyphButtonsName(string padToken, out string buttonsName)
		{
			buttonsName = null;
			if (!GamePadToken.TryCanonicalize(padToken, out string canonical))
				return false;

			switch (canonical)
			{
				case GamePadToken.LeftStickUp:
					buttonsName = "LeftThumbstickUp";
					return true;
				case GamePadToken.LeftStickDown:
					buttonsName = "LeftThumbstickDown";
					return true;
				case GamePadToken.LeftStickLeft:
					buttonsName = "LeftThumbstickLeft";
					return true;
				case GamePadToken.LeftStickRight:
					buttonsName = "LeftThumbstickRight";
					return true;
				case GamePadToken.RightStickUp:
					buttonsName = "RightThumbstickUp";
					return true;
				case GamePadToken.RightStickDown:
					buttonsName = "RightThumbstickDown";
					return true;
				case GamePadToken.RightStickLeft:
					buttonsName = "RightThumbstickLeft";
					return true;
				case GamePadToken.RightStickRight:
					buttonsName = "RightThumbstickRight";
					return true;
			}

			// Face / shoulder / trigger / DPad / L3 / R3 — same Buttons names as ownership helper.
			return ControlsEditTargetPolicy.TryPadTokenToVanillaButtonsName(canonical, out buttonsName);
		}

		/// <summary>
		/// True when left vs right stick direction tokens must not be confused.
		/// </summary>
		public static bool IsLeftStickDirectionGlyph(string buttonsName)
			=> !string.IsNullOrEmpty(buttonsName)
			   && buttonsName.StartsWith("LeftThumbstick", StringComparison.Ordinal);

		public static bool IsRightStickDirectionGlyph(string buttonsName)
			=> !string.IsNullOrEmpty(buttonsName)
			   && buttonsName.StartsWith("RightThumbstick", StringComparison.Ordinal);

		public static bool IsDPadGlyph(string buttonsName)
			=> !string.IsNullOrEmpty(buttonsName)
			   && buttonsName.StartsWith("DPad", StringComparison.Ordinal);
	}
}
