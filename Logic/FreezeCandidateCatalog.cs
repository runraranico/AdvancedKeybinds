using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Freeze-candidate key catalog for Windows layout auto-diagnostics only.
	/// Does not freeze keys or alter runtime binding.
	/// </summary>
	public static class FreezeCandidateCatalog
	{
		public enum KeyFamily
		{
			Function,
			Letter,
			NumPad,
			Cursor,
			NavCluster,
			Toggle,
			Windows,
			Apps,
			PrintScreen,
			Pause,
			JisLang
		}

		public sealed class Entry
		{
			public string Id { get; init; }
			public string DisplayLabel { get; init; }
			public uint Vk { get; init; }
			public KeyFamily Family { get; init; }
			/// <summary>Expected E0 for Set-1 make codes when known (null = unknown / layout-dependent).</summary>
			public bool? ExpectedE0 { get; init; }
			/// <summary>True when Pause-style E1 sequencing may apply.</summary>
			public bool MayNeedE1 { get; init; }
			public bool IsJisOnlyCandidate { get; init; }
			public bool IsSpecialSequenceCandidate { get; init; }
		}

		private static readonly Entry[] EntriesInternal = {
			// F1–F12
			E("F1", "F1", 0x70, KeyFamily.Function),
			E("F2", "F2", 0x71, KeyFamily.Function),
			E("F3", "F3", 0x72, KeyFamily.Function),
			E("F4", "F4", 0x73, KeyFamily.Function),
			E("F5", "F5", 0x74, KeyFamily.Function),
			E("F6", "F6", 0x75, KeyFamily.Function),
			E("F7", "F7", 0x76, KeyFamily.Function),
			E("F8", "F8", 0x77, KeyFamily.Function),
			E("F9", "F9", 0x78, KeyFamily.Function),
			E("F10", "F10", 0x79, KeyFamily.Function),
			E("F11", "F11", 0x7A, KeyFamily.Function),
			E("F12", "F12", 0x7B, KeyFamily.Function),
			// A–Z
			E("A", "A", 0x41, KeyFamily.Letter),
			E("B", "B", 0x42, KeyFamily.Letter),
			E("C", "C", 0x43, KeyFamily.Letter),
			E("D", "D", 0x44, KeyFamily.Letter),
			E("E", "E", 0x45, KeyFamily.Letter),
			E("F", "F", 0x46, KeyFamily.Letter),
			E("G", "G", 0x47, KeyFamily.Letter),
			E("H", "H", 0x48, KeyFamily.Letter),
			E("I", "I", 0x49, KeyFamily.Letter),
			E("J", "J", 0x4A, KeyFamily.Letter),
			E("K", "K", 0x4B, KeyFamily.Letter),
			E("L", "L", 0x4C, KeyFamily.Letter),
			E("M", "M", 0x4D, KeyFamily.Letter),
			E("N", "N", 0x4E, KeyFamily.Letter),
			E("O", "O", 0x4F, KeyFamily.Letter),
			E("P", "P", 0x50, KeyFamily.Letter),
			E("Q", "Q", 0x51, KeyFamily.Letter),
			E("R", "R", 0x52, KeyFamily.Letter),
			E("S", "S", 0x53, KeyFamily.Letter),
			E("T", "T", 0x54, KeyFamily.Letter),
			E("U", "U", 0x55, KeyFamily.Letter),
			E("V", "V", 0x56, KeyFamily.Letter),
			E("W", "W", 0x57, KeyFamily.Letter),
			E("X", "X", 0x58, KeyFamily.Letter),
			E("Y", "Y", 0x59, KeyFamily.Letter),
			E("Z", "Z", 0x5A, KeyFamily.Letter),
			// Numpad
			E("NumPad0", "NumPad0", 0x60, KeyFamily.NumPad, expectedE0: false),
			E("NumPad1", "NumPad1", 0x61, KeyFamily.NumPad, expectedE0: false),
			E("NumPad2", "NumPad2", 0x62, KeyFamily.NumPad, expectedE0: false),
			E("NumPad3", "NumPad3", 0x63, KeyFamily.NumPad, expectedE0: false),
			E("NumPad4", "NumPad4", 0x64, KeyFamily.NumPad, expectedE0: false),
			E("NumPad5", "NumPad5", 0x65, KeyFamily.NumPad, expectedE0: false),
			E("NumPad6", "NumPad6", 0x66, KeyFamily.NumPad, expectedE0: false),
			E("NumPad7", "NumPad7", 0x67, KeyFamily.NumPad, expectedE0: false),
			E("NumPad8", "NumPad8", 0x68, KeyFamily.NumPad, expectedE0: false),
			E("NumPad9", "NumPad9", 0x69, KeyFamily.NumPad, expectedE0: false),
			E("NumPadMultiply", "NumPad*", 0x6A, KeyFamily.NumPad, expectedE0: false),
			E("NumPadAdd", "NumPad+", 0x6B, KeyFamily.NumPad, expectedE0: false),
			E("NumPadSeparator", "NumPadSep", 0x6C, KeyFamily.NumPad, expectedE0: false),
			E("NumPadSubtract", "NumPad-", 0x6D, KeyFamily.NumPad, expectedE0: false),
			E("NumPadDecimal", "NumPad.", 0x6E, KeyFamily.NumPad, expectedE0: false),
			E("NumPadDivide", "NumPad/", 0x6F, KeyFamily.NumPad, expectedE0: true),
			// Cursor
			E("Up", "↑", 0x26, KeyFamily.Cursor, expectedE0: true),
			E("Down", "↓", 0x28, KeyFamily.Cursor, expectedE0: true),
			E("Left", "←", 0x25, KeyFamily.Cursor, expectedE0: true),
			E("Right", "→", 0x27, KeyFamily.Cursor, expectedE0: true),
			// Special / nav
			E("PrintScreen", "PrintScreen", 0x2C, KeyFamily.PrintScreen, expectedE0: true, special: true),
			E("ScrollLock", "ScrollLock", 0x91, KeyFamily.Toggle, expectedE0: false),
			E("Pause", "Pause", 0x13, KeyFamily.Pause, mayNeedE1: true, special: true),
			E("Insert", "Insert", 0x2D, KeyFamily.NavCluster, expectedE0: true),
			E("Home", "Home", 0x24, KeyFamily.NavCluster, expectedE0: true),
			E("PageUp", "PageUp", 0x21, KeyFamily.NavCluster, expectedE0: true),
			E("Delete", "Delete", 0x2E, KeyFamily.NavCluster, expectedE0: true),
			E("End", "End", 0x23, KeyFamily.NavCluster, expectedE0: true),
			E("PageDown", "PageDown", 0x22, KeyFamily.NavCluster, expectedE0: true),
			E("CapsLock", "CapsLock", 0x14, KeyFamily.Toggle, expectedE0: false),
			E("LeftWindows", "L-Win", 0x5B, KeyFamily.Windows, expectedE0: true),
			E("RightWindows", "R-Win", 0x5C, KeyFamily.Windows, expectedE0: true),
			E("Kana", "かな", 0x15, KeyFamily.JisLang, jisOnly: true),
			E("Convert", "変換", 0x1C, KeyFamily.JisLang, jisOnly: true),
			E("NonConvert", "無変換", 0x1D, KeyFamily.JisLang, jisOnly: true),
			E("Apps", "Apps/Menu", 0x5D, KeyFamily.Apps, expectedE0: true),
		};

		public static IReadOnlyList<Entry> All { get; } =
			new ReadOnlyCollection<Entry>(EntriesInternal);

		public static int Count => EntriesInternal.Length;

		private static Entry E(
			string id,
			string label,
			uint vk,
			KeyFamily family,
			bool? expectedE0 = null,
			bool mayNeedE1 = false,
			bool jisOnly = false,
			bool special = false)
			=> new() {
				Id = id,
				DisplayLabel = label,
				Vk = vk,
				Family = family,
				ExpectedE0 = expectedE0,
				MayNeedE1 = mayNeedE1,
				IsJisOnlyCandidate = jisOnly,
				IsSpecialSequenceCandidate = special
			};
	}
}
