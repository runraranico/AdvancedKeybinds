using System;
using System.Collections.Generic;

namespace AdvancedKeybinds.Core
{
	public sealed class ModeMutation
	{
		public Dictionary<string, List<string>> UpsertActions = new(StringComparer.Ordinal);
		public HashSet<string> RemoveActions = new(StringComparer.Ordinal);
		public HashSet<string> UnchangedActions = new(StringComparer.Ordinal);
		public HashSet<string> MigratedActionIds = new(StringComparer.Ordinal);
		public Dictionary<string, List<string>> BeforeAll = new(StringComparer.Ordinal);
		public Dictionary<string, List<string>> AfterExpectedTarget = new(StringComparer.Ordinal);
		public Dictionary<string, List<string>> OutOfScopeBefore = new(StringComparer.Ordinal);
	}

	public sealed class MigrationMutationPlan
	{
		public ModeMutation Gamepad = new();
		public ModeMutation GamepadUi = new();
		public List<LegacyGamePadStorageMigrationPolicy.PlanItem> StoragePlan = new();
		public bool CombosNeedMutation;
		public bool KeyStatusNeedMutation;
		public bool IsNoOp;

		public static MigrationMutationPlan Build(
			Dictionary<string, List<string>> beforeGp,
			Dictionary<string, List<string>> beforeGpUi,
			Dictionary<string, List<string>> afterGp,
			Dictionary<string, List<string>> afterGpUi,
			List<LegacyGamePadStorageMigrationPolicy.PlanItem> storagePlanItems,
			IEnumerable<string> migratedActionIdSet)
		{
			var migrated = new HashSet<string>(
				migratedActionIdSet ?? Array.Empty<string>(),
				StringComparer.Ordinal);
			var result = new MigrationMutationPlan {
				Gamepad = BuildMode(beforeGp, afterGp, migrated),
				GamepadUi = BuildMode(beforeGpUi, afterGpUi, migrated),
				StoragePlan = storagePlanItems == null
					? new List<LegacyGamePadStorageMigrationPolicy.PlanItem>()
					: new List<LegacyGamePadStorageMigrationPolicy.PlanItem>(storagePlanItems)
			};
			result.CombosNeedMutation =
				LegacyGamePadStorageMigrationPolicy.PlanRequiresCombosMutation(result.StoragePlan);
			result.KeyStatusNeedMutation =
				result.Gamepad.UpsertActions.Count != 0 || result.Gamepad.RemoveActions.Count != 0
				|| result.GamepadUi.UpsertActions.Count != 0 || result.GamepadUi.RemoveActions.Count != 0;
			result.IsNoOp = !result.CombosNeedMutation && !result.KeyStatusNeedMutation;
			return result;
		}

		public static void BuildAfterMapsFromMigration(
			Dictionary<string, List<string>> beforeGp,
			Dictionary<string, List<string>> beforeGpUi,
			Dictionary<string, ComboBindingData> combosAfter,
			IReadOnlyList<LegacyGamePadStorageMigrationPolicy.PlanItem> storagePlan,
			bool stripOrphans,
			out Dictionary<string, List<string>> afterGp,
			out Dictionary<string, List<string>> afterGpUi)
		{
			afterGp = LegacyGamePadKeyStatusMaps.Clone(beforeGp);
			afterGpUi = LegacyGamePadKeyStatusMaps.Clone(beforeGpUi);
			if (storagePlan != null)
			{
				for (int i = 0; i < storagePlan.Count; i++)
				ApplyStorageStatus(storagePlan[i], combosAfter, afterGp, afterGpUi);
			}
			if (stripOrphans)
			{
				StripOrphans(afterGp, ControlsGameUiSeparationPolicy.ModeGamePad, combosAfter);
				StripOrphans(afterGpUi, ControlsGameUiSeparationPolicy.ModeGamePadUi, combosAfter);
			}
		}

		private static ModeMutation BuildMode(
			Dictionary<string, List<string>> before,
			Dictionary<string, List<string>> after,
			HashSet<string> migrated)
		{
			var mode = new ModeMutation {
				BeforeAll = LegacyGamePadKeyStatusMaps.Clone(before),
				AfterExpectedTarget = LegacyGamePadKeyStatusMaps.Clone(after),
				MigratedActionIds = new HashSet<string>(migrated, StringComparer.Ordinal)
			};
			foreach (KeyValuePair<string, List<string>> pair in mode.BeforeAll)
			{
				if (!migrated.Contains(pair.Key))
					mode.OutOfScopeBefore[pair.Key] = pair.Value == null ? null : new List<string>(pair.Value);
			}
			foreach (string action in migrated)
			{
				bool hadBefore = mode.BeforeAll.TryGetValue(action, out List<string> beforeTokens);
				bool hasAfter = mode.AfterExpectedTarget.TryGetValue(action, out List<string> afterTokens);
				if (!hasAfter)
				{
					if (hadBefore)
						mode.RemoveActions.Add(action);
					continue;
				}
				var oneBefore = new Dictionary<string, List<string>>(StringComparer.Ordinal);
				var oneAfter = new Dictionary<string, List<string>>(StringComparer.Ordinal);
				if (hadBefore)
					oneBefore[action] = beforeTokens;
				oneAfter[action] = afterTokens;
				if (!hadBefore || !LegacyGamePadKeyStatusMaps.MapsEqual(oneBefore, oneAfter))
					mode.UpsertActions[action] = afterTokens == null ? null : new List<string>(afterTokens);
				else
					mode.UnchangedActions.Add(action);
			}
			return mode;
		}

		private static void ApplyStorageStatus(
			LegacyGamePadStorageMigrationPolicy.PlanItem item,
			Dictionary<string, ComboBindingData> combosAfter,
			Dictionary<string, List<string>> gp,
			Dictionary<string, List<string>> gpUi)
		{
			if (!item.MutatesCombos)
				return;
			string oldTrigger = ControlsGameUiSeparationPolicy.ToTriggerActionId(item.StorageKey);
			if (!string.IsNullOrEmpty(oldTrigger))
				ClearMarkersOnly(gp, oldTrigger);
			if (item.Action != LegacyGamePadStorageMigrationPolicy.PlanAction.Migrate
				|| string.IsNullOrEmpty(item.TargetStorageKey)
				|| combosAfter == null
				|| !combosAfter.TryGetValue(item.TargetStorageKey, out ComboBindingData binding)
				|| binding == null
				|| !binding.RequiresAdvancedStorage)
				return;
			string targetTrigger = ControlsGameUiSeparationPolicy.ToTriggerActionId(item.TargetStorageKey);
			List<string> markers = AdvancedVanillaOwnershipMarker.BuildMarkers(binding.Keys);
			if (ControlsGameUiSeparationPolicy.IsGamePadUiStorageKey(item.TargetStorageKey))
				gpUi[targetTrigger] = markers;
			else
				gp[targetTrigger] = markers;
		}

		private static void ClearMarkersOnly(
			Dictionary<string, List<string>> map,
			string trigger)
		{
			if (!map.TryGetValue(trigger, out List<string> tokens))
				return;
			List<string> kept = LegacyGamePadKeyStatusMaps.StripOrphanMarkersOnly(tokens);
			if (kept.Count == 0)
				map.Remove(trigger);
			else
				map[trigger] = kept;
		}

		private static void StripOrphans(
			Dictionary<string, List<string>> map,
			int mode,
			Dictionary<string, ComboBindingData> combosAfter)
		{
			var keys = new List<string>(map.Keys);
			for (int i = 0; i < keys.Count; i++)
			{
				string trigger = keys[i];
				List<string> tokens = map[trigger];
				bool hasMarker = false;
				if (tokens != null)
				{
					for (int t = 0; t < tokens.Count; t++)
						hasMarker |= AdvancedVanillaOwnershipMarker.IsMarker(tokens[t]);
				}
				if (!hasMarker || !LegacyGamePadStorageMigrationPolicy.IsOrphanGamePadOwnershipTrigger(
					trigger, mode, key => combosAfter != null && combosAfter.ContainsKey(key)))
					continue;
				List<string> kept = LegacyGamePadKeyStatusMaps.StripOrphanMarkersOnly(tokens);
				if (kept.Count == 0)
					map.Remove(trigger);
				else
					map[trigger] = kept;
			}
		}
	}
}
