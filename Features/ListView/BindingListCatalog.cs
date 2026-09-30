using System;
using System.Collections.Generic;
using AdvancedKeybinds.Binding;
using AdvancedKeybinds.Core;
using AdvancedKeybinds.Features.DoubleTap;
using Terraria.Localization;
using Terraria.ModLoader;

namespace AdvancedKeybinds.Features.ListView
{
	/// <summary>
	/// Builds list rows by reading <see cref="BindingStorage"/> only. Never writes bindings.
	/// </summary>
	internal static class BindingListCatalog
	{
		public static List<BindingListEntry> BuildAll()
		{
			var list = new List<BindingListEntry>(64);
			foreach (KeyValuePair<string, ComboBinding> pair in BindingStorage.AllBindings)
			{
				if (!TryCreateEntry(pair.Key, pair.Value, out BindingListEntry entry))
					continue;
				list.Add(entry);
			}

			list.Sort(CompareEntries);
			return list;
		}

		/// <summary>
		/// Hide Controls "メニュー" duplicates (KU: / GP:U:) — same action already appears as game-side row.
		/// </summary>
		public static bool IsMenuStorageDuplicate(string actionId)
		{
			if (string.IsNullOrEmpty(actionId))
				return false;
			return ControlsGameUiSeparationPolicy.IsKeyboardUiStorageKey(actionId)
				|| ControlsGameUiSeparationPolicy.IsGamePadUiStorageKey(actionId);
		}

		public static bool IsPadToken(string token)
			=> !string.IsNullOrEmpty(token)
				&& token.StartsWith("Pad:", StringComparison.OrdinalIgnoreCase);

		public static bool EntryHasPadToken(BindingListEntry entry)
		{
			if (entry?.MatchTokens == null)
				return false;
			for (int i = 0; i < entry.MatchTokens.Count; i++)
			{
				if (IsPadToken(entry.MatchTokens[i]))
					return true;
			}

			return false;
		}

		public static bool EntryHasKeyboardOrMouseToken(BindingListEntry entry)
		{
			if (entry?.MatchTokens == null)
				return false;
			for (int i = 0; i < entry.MatchTokens.Count; i++)
			{
				if (!IsPadToken(entry.MatchTokens[i]))
					return true;
			}

			return false;
		}

		public static List<BindingListEntry> FilterByDevice(
			IReadOnlyList<BindingListEntry> all,
			BindingListDeviceFilter device)
		{
			var filtered = new List<BindingListEntry>();
			if (all == null)
				return filtered;

			for (int i = 0; i < all.Count; i++)
			{
				BindingListEntry e = all[i];
				if (device == BindingListDeviceFilter.GamePad)
				{
					if (EntryHasPadToken(e))
						filtered.Add(e);
				}
				else if (EntryHasKeyboardOrMouseToken(e))
					filtered.Add(e);
			}

			return filtered;
		}

		public static List<BindingListEntry> FilterByProbeToken(IReadOnlyList<BindingListEntry> all, string probeToken)
		{
			var filtered = new List<BindingListEntry>();
			if (all == null || string.IsNullOrEmpty(probeToken))
				return filtered;

			for (int i = 0; i < all.Count; i++)
			{
				BindingListEntry e = all[i];
				if (EntryMatchesProbe(e, probeToken))
					filtered.Add(e);
			}

			return filtered;
		}

		public static bool EntryMatchesProbe(BindingListEntry entry, string probeToken)
		{
			if (entry?.MatchTokens == null || string.IsNullOrEmpty(probeToken))
				return false;

			for (int i = 0; i < entry.MatchTokens.Count; i++)
			{
				if (PhysicalKeyIdentity.AreSamePhysical(entry.MatchTokens[i], probeToken))
					return true;
			}

			return false;
		}

		/// <summary>
		/// TEMP LIVE: why <see cref="TryCreateEntry"/> would reject (null = would create).
		/// </summary>
		public static string DescribeCreateSkipReason(string actionId, ComboBinding combo)
		{
			if (string.IsNullOrEmpty(actionId) || combo == null)
				return "null_action_or_combo";
			if (combo.IsEmpty)
				return "combo_empty";
			if (IsMenuStorageDuplicate(actionId))
				return "menu_storage_duplicate";
			if (IsHiddenVanillaMovementAction(actionId))
				return "hidden_vanilla_movement";
			if (!IsModKeybindAction(actionId))
				return "not_mod_keybind";
			if (!IsFromEnabledMod(actionId))
				return "mod_not_enabled";
			int filled = 0;
			for (int slot = 1; slot <= 3; slot++)
			{
				if (!string.IsNullOrEmpty(combo.GetSlot(slot)))
					filled++;
			}
			if (!string.IsNullOrEmpty(combo.AdditionalOrToken))
				filled++;
			if (filled == 0)
				return "no_match_tokens";
			return null;
		}

		private static bool TryCreateEntry(string actionId, ComboBinding combo, out BindingListEntry entry)
		{
			entry = null;
			if (string.IsNullOrEmpty(actionId) || combo == null || combo.IsEmpty)
				return false;

			// Drop menu-side duplicates (KU: / GP:U:) — keeps the list readable.
			if (IsMenuStorageDuplicate(actionId))
				return false;

			// Vanilla movement — clutter in ALL KEY (Modコントロール対象外).
			if (IsHiddenVanillaMovementAction(actionId))
				return false;

			// Modコントロール相当: ModName/Action のみ（Terraria 本体行は出さない）。
			// Combos 非空＝AK が保持している変更分のみ（BuildAll のソースが AllBindings）。
			if (!IsModKeybindAction(actionId))
				return false;

			// Orphan bindings from disabled / uninstalled mods stay in bindings.json — hide them here.
			if (!IsFromEnabledMod(actionId))
				return false;

			var match = new List<string>(4);
			string primaryToken = null;
			string primaryDisplay = null;

			for (int slot = 1; slot <= 3; slot++)
			{
				string tok = combo.GetSlot(slot);
				if (string.IsNullOrEmpty(tok))
					continue;
				match.Add(tok);
				if (primaryToken == null)
				{
					primaryToken = tok;
					primaryDisplay = combo.FormatSlotLabel(slot);
				}
			}

			if (!string.IsNullOrEmpty(combo.AdditionalOrToken))
			{
				match.Add(combo.AdditionalOrToken);
				if (primaryToken == null)
				{
					primaryToken = combo.AdditionalOrToken;
					primaryDisplay = KeyToken.FormatDisplayForToken(combo.AdditionalOrToken);
				}
			}

			if (match.Count == 0)
				return false;

			// ListView-only Hold labels: press = "Hold ↓ ", release = "Hold ↑ " (half-width spaces).
			// Does not change Controls / ComboPanel localization (Hold / Hold↑).
			string comboDisplay = FormatListViewKeyComboDisplay(combo);
			if (string.IsNullOrEmpty(comboDisplay))
				comboDisplay = combo.FormatJoinedLabel(compact: false);
			string comboSortDisplay = comboDisplay;
			comboDisplay = DoubleTapControlsDisplay.ApplyPrefix(comboDisplay, combo);

			SplitActionId(actionId, out string modDisplay, out string actionDisplay);

			entry = new BindingListEntry {
				ActionId = actionId,
				KeyComboDisplay = comboDisplay,
				KeyComboSortDisplay = comboSortDisplay,
				ActionDisplay = actionDisplay,
				ModDisplay = modDisplay,
				MatchTokens = match,
				PrimaryToken = primaryToken,
				PrimaryDisplay = primaryDisplay ?? KeyToken.FormatDisplayForToken(primaryToken),
				GroupSortKey = BindingListSort.BuildGroupSortKey(primaryToken, primaryDisplay)
			};
			return true;
		}

		/// <summary>Modコントロール行と同じく ModName/KeyName 形式のみ。</summary>
		public static bool IsModKeybindAction(string actionId)
		{
			if (string.IsNullOrEmpty(actionId))
				return false;
			string bare = ControlsGameUiSeparationPolicy.ToTriggerActionId(actionId);
			int slash = bare.IndexOf('/');
			return slash > 0 && slash < bare.Length - 1;
		}

		/// <summary>Vanilla movement / jump — hide from ALL KEY list.</summary>
		public static bool IsHiddenVanillaMovementAction(string actionId)
		{
			if (string.IsNullOrEmpty(actionId))
				return false;
			string bare = ControlsGameUiSeparationPolicy.ToTriggerActionId(actionId);
			return string.Equals(bare, "Left", StringComparison.Ordinal)
				|| string.Equals(bare, "Right", StringComparison.Ordinal)
				|| string.Equals(bare, "Up", StringComparison.Ordinal)
				|| string.Equals(bare, "Down", StringComparison.Ordinal)
				|| string.Equals(bare, "Jump", StringComparison.Ordinal);
		}

		/// <summary>
		/// Same join path as <see cref="BindingDisplayResolver.FormatComboDisplayLabel"/>,
		/// but Hold prefixes are ListView-specific arrows with half-width spaces.
		/// </summary>
		private static string FormatListViewKeyComboDisplay(ComboBinding combo)
		{
			if (combo == null || combo.IsEmpty)
				return string.Empty;

			string joined;
			if (combo.HasAlternateGamePad && combo.FilledSlotCount == 1)
			{
				string primaryDisplay = KeyToken.FormatDisplayForToken(combo.Keys[0]);
				string alternateDisplay = KeyToken.FormatDisplayForToken(combo.AlternateGamePadToken);
				joined = AlternateGamePadBindingPolicy.FormatAlternateOverPrimaryDisplay(
					alternateDisplay,
					primaryDisplay);
			}
			else
				joined = combo.FormatJoinedLabel(compact: true);

			string withHold = BindingDisplayText.ApplyHoldPrefix(
				joined,
				combo.Hold,
				combo.HoldOnRelease,
				combo.FilledSlotCount,
				"Hold ↓ ",
				"Hold ↑ ");

			if (!combo.HasAdditionalOr)
				return withHold;

			string orDisplay = KeyToken.FormatDisplayForToken(combo.AdditionalOrToken);
			return KeyboardAdditionalOrPolicy.FormatAndWithAdditionalOrDisplay(withHold, orDisplay);
		}

		/// <summary>
		/// Vanilla (no ModName/) always shown. Mod actions only when that mod is currently loaded/enabled.
		/// </summary>
		public static bool IsFromEnabledMod(string actionId)
		{
			if (string.IsNullOrEmpty(actionId))
				return false;

			string bare = ControlsGameUiSeparationPolicy.ToTriggerActionId(actionId);
			int slash = bare.IndexOf('/');
			if (slash <= 0 || slash >= bare.Length - 1)
				return true; // Terraria / non-mod storage id

			string modName = bare.Substring(0, slash);
			if (string.IsNullOrEmpty(modName))
				return false;

			try
			{
				return ModLoader.HasMod(modName);
			}
			catch
			{
				return false;
			}
		}

		private static void SplitActionId(string actionId, out string modDisplay, out string actionDisplay)
		{
			// KU: / GP:G: / GP:U: / GP: are AK storage prefixes (game vs menu Controls), not mod names.
			string bare = ControlsGameUiSeparationPolicy.ToTriggerActionId(actionId);
			string contextSuffix = null;
			if (ControlsGameUiSeparationPolicy.IsGamePadGameStorageKey(actionId)
				|| ControlsGameUiSeparationPolicy.IsGamePadLegacySharedStorageKey(actionId))
				contextSuffix = SafeLocal("Mods.AdvancedKeybinds.UI.BindingList.SuffixGamePad", " (Pad)");

			int slash = bare.IndexOf('/');
			if (slash <= 0 || slash >= bare.Length - 1)
			{
				modDisplay = "Terraria";
				actionDisplay = (TryLocalizeVanillaAction(bare) ?? bare) + (contextSuffix ?? string.Empty);
				return;
			}

			string modName = bare.Substring(0, slash);
			string rest = bare.Substring(slash + 1);
			modDisplay = modName;
			actionDisplay = (TryLocalizeModAction(modName, rest) ?? rest) + (contextSuffix ?? string.Empty);
		}

		private static string SafeLocal(string key, string fallback)
		{
			try
			{
				string v = Language.GetTextValue(key);
				if (string.IsNullOrEmpty(v) || v == key || v.StartsWith("Mods.AdvancedKeybinds.UI.BindingList", StringComparison.Ordinal))
					return fallback;
				return v;
			}
			catch
			{
				return fallback;
			}
		}

		private static string TryLocalizeVanillaAction(string actionId)
		{
			try
			{
				// Best-effort; missing keys fall back to raw actionId.
				LocalizedText text = Language.GetText("Mods.AdvancedKeybinds.UI.BindingList.Vanilla." + actionId);
				if (text != null)
				{
					string v = text.Value;
					if (!string.IsNullOrEmpty(v) && !v.StartsWith("Mods.AdvancedKeybinds", StringComparison.Ordinal))
						return v;
				}
			}
			catch
			{
				// ignore
			}

			return null;
		}

		private static string TryLocalizeModAction(string modName, string keyName)
		{
			try
			{
				if (!ModLoader.TryGetMod(modName, out Mod mod) || mod == null)
					return null;

				// ModKeybind display names live under Mods.<Mod>.Keybinds.<Name>.DisplayName when present.
				string path = "Mods." + modName + ".Keybinds." + keyName + ".DisplayName";
				LocalizedText text = Language.GetText(path);
				if (text == null)
					return null;
				string v = text.Value;
				if (string.IsNullOrEmpty(v) || v.StartsWith("Mods.", StringComparison.Ordinal))
					return null;
				return v;
			}
			catch
			{
				return null;
			}
		}

		private static int CompareEntries(BindingListEntry a, BindingListEntry b)
		{
			int g = string.Compare(a.GroupSortKey, b.GroupSortKey, StringComparison.OrdinalIgnoreCase);
			if (g != 0)
				return g;
			int k = string.Compare(a.KeyComboSortDisplay, b.KeyComboSortDisplay, StringComparison.OrdinalIgnoreCase);
			if (k != 0)
				return k;
			return string.Compare(a.ActionId, b.ActionId, StringComparison.OrdinalIgnoreCase);
		}

		/// <summary>Plan B: MOD first, then key, then action. No letter headings.</summary>
		public static List<BindingListEntry> SortByMod(IReadOnlyList<BindingListEntry> rows)
		{
			var list = new List<BindingListEntry>();
			if (rows == null)
				return list;
			for (int i = 0; i < rows.Count; i++)
				list.Add(rows[i]);
			list.Sort(CompareEntriesByMod);
			return list;
		}

		private static int CompareEntriesByMod(BindingListEntry a, BindingListEntry b)
		{
			int m = string.Compare(a.ModDisplay, b.ModDisplay, StringComparison.OrdinalIgnoreCase);
			if (m != 0)
				return m;
			int k = string.Compare(a.KeyComboSortDisplay, b.KeyComboSortDisplay, StringComparison.OrdinalIgnoreCase);
			if (k != 0)
				return k;
			int act = string.Compare(a.ActionDisplay, b.ActionDisplay, StringComparison.OrdinalIgnoreCase);
			if (act != 0)
				return act;
			return string.Compare(a.ActionId, b.ActionId, StringComparison.OrdinalIgnoreCase);
		}
	}
}
