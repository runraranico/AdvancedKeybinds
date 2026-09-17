using System;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Windows layout LANGID equivalence for input identity (not culture display).
	/// Spanish Spain Traditional (0x040A) and International sort (0x0C0A) share one keyboard
	/// layout family; treat them as the same layout for capture recovery / identity.
	/// Does not merge other Spanish locales (e.g. Mexico 0x080A).
	/// </summary>
	public static class LayoutLangEquivalence
	{
		/// <summary>Spanish (Spain, Traditional sort).</summary>
		public const ushort SpanishSpainTraditional = 0x040A;

		/// <summary>Spanish (Spain, International sort) — same Spain keyboard family as Traditional.</summary>
		public const ushort SpanishSpainInternationalSort = 0x0C0A;

		/// <summary>
		/// Canonical LANGID for equivalence buckets. Spain International sort collapses to Traditional.
		/// </summary>
		public static ushort NormalizeLangId(ushort langId)
		{
			if (langId == SpanishSpainInternationalSort)
				return SpanishSpainTraditional;
			return langId;
		}

		public static bool AreEquivalentLangIds(ushort a, ushort b)
			=> NormalizeLangId(a) == NormalizeLangId(b);

		/// <summary>LANGID low-word of an HKL handle (0 when handle is zero).</summary>
		public static ushort LangIdFromHkl(IntPtr hkl)
		{
			if (hkl == IntPtr.Zero)
				return 0;
			return unchecked((ushort)(hkl.ToInt64() & 0xFFFF));
		}

		/// <summary>
		/// True when handles are identical, or their LANGIDs normalize to the same bucket
		/// (Spain Traditional ↔ International sort). Different primary locales stay distinct.
		/// </summary>
		public static bool AreEquivalentHkl(IntPtr a, IntPtr b)
		{
			if (a == IntPtr.Zero || b == IntPtr.Zero)
				return false;
			if (a == b)
				return true;
			return AreEquivalentLangIds(LangIdFromHkl(a), LangIdFromHkl(b));
		}
	}
}
