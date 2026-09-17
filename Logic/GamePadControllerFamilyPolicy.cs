using System;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Connected controller family for Controls glyph / label display only.
	/// Does not affect save tokens (<c>Pad:A</c> …) or input firing.
	/// </summary>
	public enum GamePadControllerFamily : byte
	{
		/// <summary>Unknown / disconnected / unmapped — keep Xbox GlyphTagHandler path.</summary>
		Unknown = 0,
		Xbox = 1,
		PlayStation = 2,
		Nintendo = 3
	}

	/// <summary>
	/// Pure GUID / capability → family mapping (no FNA types).
	/// Uses FNA <c>GetGUIDEXT</c> strings and optional touch-pad hint.
	/// OS-agnostic (Windows / Linux / Steam Deck share the same rules).
	/// </summary>
	public static class GamePadControllerFamilyPolicy
	{
		/// <summary>FNA remaps Valve SI Xbox virtual pads to this GUID.</summary>
		public const string GuidXInput = "xinput";

		/// <summary>FNA remaps Valve SI DualShock 4 virtual to this GUID.</summary>
		public const string GuidPs4Valve = "4c05c405";

		/// <summary>FNA remaps Valve SI DualSense virtual to this GUID.</summary>
		public const string GuidPs5Valve = "4c05e60c";

		/// <summary>
		/// Resolve family from <c>GamePad.GetGUIDEXT</c> and optional Capabilities EXT.
		/// Empty / null GUID → <see cref="GamePadControllerFamily.Unknown"/> (Xbox glyph fallback at display).
		/// </summary>
		public static GamePadControllerFamily Resolve(string guidOrNull, bool hasTouchPadExt = false)
		{
			if (string.IsNullOrWhiteSpace(guidOrNull))
			{
				// DualSense often exposes touchpad; use only when GUID missing.
				return hasTouchPadExt
					? GamePadControllerFamily.PlayStation
					: GamePadControllerFamily.Unknown;
			}

			string g = guidOrNull.Trim();
			if (g.Equals(GuidXInput, StringComparison.OrdinalIgnoreCase))
				return GamePadControllerFamily.Xbox;

			if (g.Equals(GuidPs4Valve, StringComparison.OrdinalIgnoreCase)
				|| g.Equals(GuidPs5Valve, StringComparison.OrdinalIgnoreCase))
				return GamePadControllerFamily.PlayStation;

			// SDL-style 8 hex: vv vv pp pp (little-endian vendor/product packed by FNA).
			if (LooksLikePackedGuid(g))
			{
				// Sony (0x054C) → bytes "4c05...."
				if (g.StartsWith("4c05", StringComparison.OrdinalIgnoreCase))
					return GamePadControllerFamily.PlayStation;

				// Nintendo (0x057E) → "7e05...."
				if (g.StartsWith("7e05", StringComparison.OrdinalIgnoreCase))
					return GamePadControllerFamily.Nintendo;

				// Microsoft (0x045E) → "5e04...."
				if (g.StartsWith("5e04", StringComparison.OrdinalIgnoreCase))
					return GamePadControllerFamily.Xbox;
			}

			if (hasTouchPadExt)
				return GamePadControllerFamily.PlayStation;

			return GamePadControllerFamily.Unknown;
		}

		/// <summary>
		/// Display path: Unknown behaves like Xbox (safe GlyphTagHandler fallback).
		/// Nintendo Phase 1 also keeps Xbox glyphs (no Switch atlas on PC).
		/// </summary>
		public static bool UsesXboxGlyphAtlas(GamePadControllerFamily family)
			=> family != GamePadControllerFamily.PlayStation;

		private static bool LooksLikePackedGuid(string g)
		{
			if (g.Length != 8)
				return false;
			for (int i = 0; i < 8; i++)
			{
				char c = g[i];
				bool hex = (c >= '0' && c <= '9')
					|| (c >= 'a' && c <= 'f')
					|| (c >= 'A' && c <= 'F');
				if (!hex)
					return false;
			}
			return true;
		}
	}
}
