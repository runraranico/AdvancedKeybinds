using System;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Display-only maker labels when Terraria <c>GlyphTagHandler</c> has no PS/Nintendo atlas
	/// (PC registers Xbox <c>Buttons</c> names only; unknown strings pass through as text).
	/// Does not change <see cref="GamePadToken.FormatForDisplay"/> (ComboPanel arrows stay).
	/// </summary>
	public static class ControlsGamePadFamilyGlyphPolicy
	{
		/// <summary>
		/// When true, Controls should skip Buttons→glyph and show <paramref name="label"/> as plain text.
		/// Stick directions stay on Xbox glyph names (shared atlas).
		/// </summary>
		public static bool TryResolveFamilyPlainLabel(
			GamePadControllerFamily family,
			string padToken,
			out string label)
		{
			label = null;
			if (GamePadControllerFamilyPolicy.UsesXboxGlyphAtlas(family))
				return false;
			if (!GamePadToken.TryCanonicalize(padToken, out string canonical))
				return false;

			// Stick dirs: keep GlyphTagHandler Xbox stick icons (no PS-specific stick glyphs on PC).
			if (GamePadToken.IsStickDirectionToken(canonical))
				return false;

			if (family == GamePadControllerFamily.PlayStation)
				return TryPlayStationLabel(canonical, out label);

			return false;
		}

		private static bool TryPlayStationLabel(string canonical, out string label)
		{
			label = canonical switch {
				GamePadToken.A => "\u00D7", // × Cross (South)
				GamePadToken.B => "\u25CB", // ○ Circle (East)
				GamePadToken.X => "\u25A1", // □ Square (West)
				GamePadToken.Y => "\u25B3", // △ Triangle (North)
				GamePadToken.LeftShoulder => "L1",
				GamePadToken.RightShoulder => "R1",
				GamePadToken.LeftTrigger => "L2",
				GamePadToken.RightTrigger => "R2",
				GamePadToken.LeftStick => "L3",
				GamePadToken.RightStick => "R3",
				GamePadToken.Back => "Create",
				GamePadToken.Start => "Options",
				GamePadToken.Guide => "PS",
				GamePadToken.DPadUp => "D-Up",
				GamePadToken.DPadDown => "D-Down",
				GamePadToken.DPadLeft => "D-Left",
				GamePadToken.DPadRight => "D-Right",
				_ => null
			};
			return !string.IsNullOrEmpty(label);
		}
	}
}
