using System;
using System.Collections.Generic;
using System.Globalization;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Support layouts for world-keyboard regression.
	/// KLID list mirrors the product preload set historically kept on FreezeCandidateWin32
	/// (diag-only in public builds). Keep this list in sync when adding layouts.
	/// </summary>
	public static class WorldKeyboardSupportCatalog
	{
		public sealed class LayoutEntry
		{
			public string Klid { get; init; }
			public ushort DeclaredLangId { get; init; }
			public string Tag { get; init; }
			public bool IsJapanese { get; init; }
			public bool IsKorean { get; init; }
		}

		/// <summary>Same order/contents as FreezeCandidateWin32.WellKnownKlids (32 layouts).</summary>
		public static readonly string[] WellKnownKlids = {
			"00000409", // US
			"00000809", // UK
			"00000407", // German
			"0000040C", // French
			"00000410", // Italian
			"0000040A", // Spanish
			"00000816", // Portuguese
			"00000416", // Brazilian
			"00000411", // Japanese
			"00000412", // Korean
			"00000804", // Chinese Simplified
			"00000404", // Chinese Traditional
			"00000419", // Russian
			"0000041D", // Swedish
			"00000414", // Norwegian
			"00000406", // Danish
			"0000040B", // Finnish
			"0000041F", // Turkish Q
			"00000413", // Dutch
			"00000415", // Polish
			"00000405", // Czech
			"0000040E", // Hungarian
			"0000041B", // Slovak
			"00000418", // Romanian
			"00000422", // Ukrainian
			"00000423", // Belarusian
			"0000042A", // Vietnamese
			"0000041E", // Thai
			"00000421", // Indonesian
			"00000439", // Hindi
			"00000401", // Arabic
			"0000040D", // Hebrew
		};

		private static readonly LayoutEntry[] EntriesInternal = Build();

		public static IReadOnlyList<LayoutEntry> All => EntriesInternal;

		public static int Count => EntriesInternal.Length;

		/// <summary>
		/// Known Windows LANGID pairs that share one keyboard family (same physical keys).
		/// Currently: Spanish Spain Traditional ↔ International sort.
		/// </summary>
		public static IReadOnlyList<(ushort A, ushort B, string Reason)> KnownEquivalentLangIdPairs { get; } =
			new List<(ushort, ushort, string)> {
				(LayoutLangEquivalence.SpanishSpainTraditional,
					LayoutLangEquivalence.SpanishSpainInternationalSort,
					"es-ES Traditional/International sort")
			};

		public static ushort ParseLangIdFromKlid(string klid)
		{
			if (string.IsNullOrWhiteSpace(klid) || klid.Length < 8)
				return 0;
			if (!ushort.TryParse(klid.AsSpan(klid.Length - 4), NumberStyles.HexNumber,
				CultureInfo.InvariantCulture, out ushort lang))
				return 0;
			return lang;
		}

		private static LayoutEntry[] Build()
		{
			var list = new List<LayoutEntry>(WellKnownKlids.Length);
			foreach (string klid in WellKnownKlids)
			{
				ushort lang = ParseLangIdFromKlid(klid);
				list.Add(new LayoutEntry {
					Klid = klid,
					DeclaredLangId = lang,
					Tag = "klid-" + klid,
					IsJapanese = lang == 0x0411,
					IsKorean = lang == 0x0412
				});
			}

			return list.ToArray();
		}
	}
}
