using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.Xna.Framework.Input;

namespace AdvancedKeybinds.Binding
{
	/// <summary>
	/// UI-only: maps <see cref="Keys"/> to the active Windows layout legend when safe and unique.
	/// Colliding glyphs fall back to internal names (caller shows <see cref="Keys.ToString()"/>).
	/// </summary>
	internal static class KeyLayoutDisplayResolver
	{
		private static readonly Keys[] LayoutGlyphCandidates = BuildLayoutGlyphCandidates();

		private static IntPtr _cachedLayout = IntPtr.Zero;
		private static Dictionary<Keys, string> _uniqueLayoutGlyphs;
		private static HashSet<Keys> _glyphCollisionKeys;

		private static Keys[] BuildLayoutGlyphCandidates()
		{
			var keys = new List<Keys>(48);
			for (Keys k = Keys.A; k <= Keys.Z; k++)
				keys.Add(k);
			for (Keys k = Keys.D0; k <= Keys.D9; k++)
				keys.Add(k);

			keys.Add(Keys.OemSemicolon);
			keys.Add(Keys.OemPlus);
			keys.Add(Keys.OemComma);
			keys.Add(Keys.OemMinus);
			keys.Add(Keys.OemPeriod);
			keys.Add(Keys.OemQuestion);
			keys.Add(Keys.OemTilde);
			keys.Add(Keys.OemOpenBrackets);
			keys.Add(Keys.OemPipe);
			keys.Add(Keys.OemCloseBrackets);
			keys.Add(Keys.OemQuotes);
			keys.Add(Keys.OemBackslash);
			keys.Add(Keys.Oem8);
			return keys.ToArray();
		}

		/// <summary>
		/// True when a stable, unique layout legend exists for <paramref name="key"/>.
		/// </summary>
		public static bool TryGetLayoutLegend(Keys key, out string display)
		{
			display = null;
			if (!OperatingSystem.IsWindows())
				return false;

			EnsureCache();
			return _uniqueLayoutGlyphs.TryGetValue(key, out display);
		}

		private static void EnsureCache()
		{
			IntPtr layout = KeyboardLayoutHelper.GetActiveLayoutHandle();
			if (layout == _cachedLayout && _uniqueLayoutGlyphs != null)
				return;

			RebuildCache(layout);
		}

		private static void RebuildCache(IntPtr layout)
		{
			_cachedLayout = layout;
			var glyphByKey = new Dictionary<Keys, string>();
			var keysByGlyph = new Dictionary<string, List<Keys>>(StringComparer.Ordinal);

			for (int i = 0; i < LayoutGlyphCandidates.Length; i++)
			{
				Keys key = LayoutGlyphCandidates[i];
				if (!KeyboardLayoutHelper.TryGetUnshiftedVkGlyph((uint)key, layout, out string rawGlyph))
					continue;
				if (!IsStableLayoutGlyph(rawGlyph))
					continue;

				string display = FormatDisplayGlyph(rawGlyph);
				string dedupeKey = NormalizeGlyphForDedup(display);

				if (!keysByGlyph.TryGetValue(dedupeKey, out List<Keys> owners))
				{
					owners = new List<Keys>(1);
					keysByGlyph[dedupeKey] = owners;
				}

				owners.Add(key);
				glyphByKey[key] = display;
			}

			var collisions = new HashSet<Keys>();
			foreach (KeyValuePair<string, List<Keys>> entry in keysByGlyph)
			{
				if (entry.Value.Count <= 1)
					continue;

				for (int i = 0; i < entry.Value.Count; i++)
					collisions.Add(entry.Value[i]);
			}

			var unique = new Dictionary<Keys, string>();
			foreach (KeyValuePair<Keys, string> entry in glyphByKey)
			{
				if (!collisions.Contains(entry.Key))
					unique[entry.Key] = entry.Value;
			}

			_uniqueLayoutGlyphs = unique;
			_glyphCollisionKeys = collisions;
		}

		internal static string FormatDisplayGlyph(string glyph)
		{
			if (glyph.Length == 1 && char.IsLetter(glyph[0]))
				return char.ToUpperInvariant(glyph[0]).ToString();
			return glyph;
		}

		private static string NormalizeGlyphForDedup(string display)
		{
			if (display.Length == 1 && char.IsLetter(display[0]))
				return char.ToUpperInvariant(display[0]).ToString();
			return display;
		}

		/// <summary>
		/// Single printable base character from ToUnicodeEx (not composed text, not a combining mark).
		/// </summary>
		internal static bool IsStableLayoutGlyph(string glyph)
		{
			if (string.IsNullOrEmpty(glyph) || glyph.Length != 1)
				return false;

			char ch = glyph[0];
			if (char.IsControl(ch) || ch == ' ')
				return false;

			UnicodeCategory cat = char.GetUnicodeCategory(ch);
			return cat != UnicodeCategory.NonSpacingMark
				&& cat != UnicodeCategory.EnclosingMark;
		}
	}
}
