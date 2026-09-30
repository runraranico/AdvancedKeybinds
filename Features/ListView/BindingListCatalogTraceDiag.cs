using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AdvancedKeybinds.Binding;
using Terraria.GameInput;
using Terraria.ModLoader;

namespace AdvancedKeybinds.Features.ListView
{
	/// <summary>
	/// TEMP: compare RecipeBrowser Toggle vs QueryHoveredItem through Catalog stages.
	/// Investigation complete (user confirmed Query Hovered Item display). Default OFF — no file I/O.
	/// Output when Enabled: deck log\Keyboard\BindingList_CatalogTrace.tsv + BindingList_CatalogTrace_LAST.txt
	/// </summary>
	internal static class BindingListCatalogTraceDiag
	{
		/// <summary>Default OFF after user-confirmed fix. Re-enable only for Catalog stage investigation.</summary>
		private const bool Enabled = false;

		private const string ToggleId = "RecipeBrowser/ToggleRecipeBrowser";
		private const string QueryId = "RecipeBrowser/QueryHoveredItem";

		public static void TraceRebuild(
			IReadOnlyList<BindingListEntry> builtAll,
			IReadOnlyList<BindingListEntry> deviceRows,
			IReadOnlyList<BindingListEntry> visibleRows,
			BindingListDeviceFilter device,
			BindingListContentMode contentMode,
			string filterProbe,
			bool modSort)
		{
			if (!Enabled)
				return;

			try
			{
				string dir = Path.Combine(
					Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
					"My Games", "Terraria", "tModLoader", "deck log", "Keyboard");
				Directory.CreateDirectory(dir);
				string pathTsv = Path.Combine(dir, "BindingList_CatalogTrace.tsv");
				string pathLast = Path.Combine(dir, "BindingList_CatalogTrace_LAST.txt");

				var sb = new StringBuilder(8192);
				sb.AppendLine("Utc\tActionId\tStage\tDetail");
				string utc = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
				sb.Append(utc).Append("\t*\tREBUILD\tdevice=").Append(device)
					.Append("\tmode=").Append(contentMode)
					.Append("\tprobe=").Append(filterProbe ?? "")
					.Append("\tmodSort=").Append(modSort)
					.Append("\tcombosRev=").Append(BindingStorage.CombosMutationRevision)
					.Append("\tbuilt=").Append(builtAll?.Count ?? -1)
					.Append("\tdeviceRows=").Append(deviceRows?.Count ?? -1)
					.Append("\tvisible=").Append(visibleRows?.Count ?? -1)
					.AppendLine();

				DumpCombosRecipeBrowser(sb, utc);
				TraceOne(sb, utc, ToggleId, "A_ok_reference", builtAll, deviceRows, visibleRows);
				TraceOne(sb, utc, QueryId, "B_missing_report", builtAll, deviceRows, visibleRows);

				string text = sb.ToString();
				File.AppendAllText(pathTsv, text, Encoding.UTF8);
				File.WriteAllText(pathLast, text, Encoding.UTF8);
			}
			catch
			{
				// never break open
			}
		}

		private static void DumpCombosRecipeBrowser(StringBuilder sb, string utc)
		{
			void Row(string stage, string detail)
				=> sb.Append(utc).Append("\t*\t").Append(stage).Append('\t')
					.Append(detail?.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ') ?? "")
					.AppendLine();

			int n = 0;
			foreach (KeyValuePair<string, ComboBinding> pair in BindingStorage.AllBindings)
			{
				if (pair.Key == null
					|| pair.Key.IndexOf("RecipeBrowser", StringComparison.OrdinalIgnoreCase) < 0)
					continue;
				n++;
				ComboBinding c = pair.Value;
				string skip = BindingListCatalog.DescribeCreateSkipReason(pair.Key, c);
				Row("COMBOS_RB", "id=" + pair.Key
					+ " slots=" + (c?.Slot1 ?? "-") + "/" + (c?.Slot2 ?? "-") + "/" + (c?.Slot3 ?? "-")
					+ " empty=" + (c == null || c.IsEmpty)
					+ " skip=" + (skip ?? "WOULD_CREATE"));
			}
			Row("COMBOS_RB_COUNT", "n=" + n);
		}

		private static void TraceOne(
			StringBuilder sb,
			string utc,
			string actionId,
			string tag,
			IReadOnlyList<BindingListEntry> builtAll,
			IReadOnlyList<BindingListEntry> deviceRows,
			IReadOnlyList<BindingListEntry> visibleRows)
		{
			void Row(string stage, string detail)
				=> sb.Append(utc).Append('\t').Append(actionId).Append('\t').Append(stage).Append('\t')
					.Append(detail?.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ') ?? "")
					.AppendLine();

			Row("TAG", tag);

			bool inCombosDict = false;
			ComboBinding live = null;
			foreach (KeyValuePair<string, ComboBinding> pair in BindingStorage.AllBindings)
			{
				if (!string.Equals(pair.Key, actionId, StringComparison.Ordinal))
					continue;
				inCombosDict = true;
				live = pair.Value;
				break;
			}

			ComboBinding viaGet = BindingStorage.Get(actionId);
			Row("FOUND_COMBOS", inCombosDict ? "YES" : "NO");
			Row("FOUND_GET", viaGet != null ? "YES" : "NO");

			bool modLoaded = false;
			try
			{
				modLoaded = ModLoader.HasMod("RecipeBrowser");
			}
			catch
			{
				// ignore
			}
			Row("MOD_LOADED", modLoaded ? "YES" : "NO");

			string keyStatus = ReadKeyStatus(actionId);
			Row("KEYSTATUS", string.IsNullOrEmpty(keyStatus) ? "(missing/empty)" : keyStatus);

			ComboBinding combo = live ?? viaGet;
			if (combo == null)
			{
				Row("CLASS", "A_or_G_not_in_Combos");
				Row("ASSIGNED", "NO");
				return;
			}

			Row("ASSIGNED", "slots="
				+ (combo.Slot1 ?? "-") + "/"
				+ (combo.Slot2 ?? "-") + "/"
				+ (combo.Slot3 ?? "-")
				+ " hold=" + combo.Hold
				+ " empty=" + combo.IsEmpty);

			string skip = BindingListCatalog.DescribeCreateSkipReason(actionId, combo);
			Row("CREATE_SKIP", skip ?? "none_would_create");

			bool menuDup = BindingListCatalog.IsMenuStorageDuplicate(actionId);
			bool hiddenMove = BindingListCatalog.IsHiddenVanillaMovementAction(actionId);
			bool isMod = BindingListCatalog.IsModKeybindAction(actionId);
			bool enabledMod = BindingListCatalog.IsFromEnabledMod(actionId);
			Row("PARSED_FILTERS", "menuDup=" + menuDup
				+ " hiddenMove=" + hiddenMove
				+ " isMod=" + isMod
				+ " enabledMod=" + enabledMod
				+ " matchCount=" + CountFilled(combo));

			bool wouldCreate = skip == null;
			Row("ENTRY_CREATED_EXPECTED", wouldCreate ? "YES" : "NO");

			bool inBuilt = ContainsAction(builtAll, actionId);
			Row("IN_BUILDALL", inBuilt ? "YES" : "NO");
			bool inDevice = ContainsAction(deviceRows, actionId);
			Row("IN_DEVICE_FILTER", inDevice ? "YES" : "NO");
			bool inVisible = ContainsAction(visibleRows, actionId);
			Row("LIST_ADDED", inVisible ? "YES" : "NO");

			if (inVisible)
				Row("CLASS", "F_or_ok_in_visible_list");
			else if (inDevice && !inVisible)
				Row("CLASS", "E_filtered_or_content_mode");
			else if (inBuilt && !inDevice)
				Row("CLASS", "E_device_filter");
			else if (wouldCreate && !inBuilt)
				Row("CLASS", "D_entry_expected_but_not_in_buildall");
			else if (!wouldCreate)
				Row("CLASS", "C_or_filter_blocked_create");
			else if (!inCombosDict)
				Row("CLASS", "A_or_G_not_in_Combos");
			else
				Row("CLASS", "H_other");

			if (inBuilt)
			{
				BindingListEntry e = Find(builtAll, actionId);
				if (e != null)
					Row("ENTRY_DISPLAY", "key=" + (e.KeyComboDisplay ?? "")
						+ " action=" + (e.ActionDisplay ?? "")
						+ " mod=" + (e.ModDisplay ?? ""));
			}
		}

		private static int CountFilled(ComboBinding combo)
		{
			if (combo == null)
				return 0;
			int n = 0;
			if (!string.IsNullOrEmpty(combo.Slot1)) n++;
			if (!string.IsNullOrEmpty(combo.Slot2)) n++;
			if (!string.IsNullOrEmpty(combo.Slot3)) n++;
			if (!string.IsNullOrEmpty(combo.AdditionalOrToken)) n++;
			return n;
		}

		private static bool ContainsAction(IReadOnlyList<BindingListEntry> list, string actionId)
			=> Find(list, actionId) != null;

		private static BindingListEntry Find(IReadOnlyList<BindingListEntry> list, string actionId)
		{
			if (list == null || string.IsNullOrEmpty(actionId))
				return null;
			for (int i = 0; i < list.Count; i++)
			{
				if (list[i] != null
					&& string.Equals(list[i].ActionId, actionId, StringComparison.Ordinal))
					return list[i];
			}
			return null;
		}

		private static string ReadKeyStatus(string actionId)
		{
			try
			{
				IReadOnlyList<string> keys = VanillaRebindHelper.GetAssignedKeysForInputMode(
					actionId, InputMode.Keyboard);
				if (keys == null || keys.Count == 0)
					return "";
				var sb = new StringBuilder();
				for (int i = 0; i < keys.Count; i++)
				{
					if (i > 0)
						sb.Append(',');
					sb.Append(keys[i]);
				}
				return sb.ToString();
			}
			catch
			{
				return "(error)";
			}
		}
	}
}
