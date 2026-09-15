using System;
using System.Collections.Generic;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Pure plan for migrating legacy shared <c>GP:{vanilla}</c> to <c>GP:G:</c> / <c>GP:U:</c>.
	/// Does not touch disk, KeyStatus, or MOD <c>GP:Mod/…</c> records.
	/// </summary>
	public static class LegacyGamePadStorageMigrationPolicy
	{
		public enum RecordKind
		{
			KeepModernGame = 0,
			KeepModernUi = 1,
			KeepMod = 2,
			LegacyVanilla = 3,
			Unclassified = 4
		}

		public enum PlanAction
		{
			None = 0,
			Keep = 1,
			DeleteDuplicate = 2,
			Migrate = 3,
			SkipUnclassified = 4
		}

		public readonly struct PlanItem
		{
			public readonly string StorageKey;
			public readonly RecordKind Kind;
			public readonly PlanAction Action;
			public readonly string TargetStorageKey;
			public readonly string Reason;

			public PlanItem(
				string storageKey,
				RecordKind kind,
				PlanAction action,
				string targetStorageKey,
				string reason)
			{
				StorageKey = storageKey;
				Kind = kind;
				Action = action;
				TargetStorageKey = targetStorageKey;
				Reason = reason ?? string.Empty;
			}

			public bool MutatesCombos
				=> Action == PlanAction.DeleteDuplicate || Action == PlanAction.Migrate;
		}

		public static RecordKind Classify(string storageKey)
		{
			if (string.IsNullOrEmpty(storageKey))
				return RecordKind.Unclassified;

			if (ControlsGameUiSeparationPolicy.IsGamePadGameStorageKey(storageKey))
				return RecordKind.KeepModernGame;
			if (ControlsGameUiSeparationPolicy.IsGamePadUiStorageKey(storageKey))
				return RecordKind.KeepModernUi;

			if (!ControlsGameUiSeparationPolicy.IsGamePadLegacySharedStorageKey(storageKey))
				return RecordKind.Unclassified;

			string trigger = ControlsGameUiSeparationPolicy.ToTriggerActionId(storageKey);
			if (TerrariaVanillaTriggerCatalog.IsKnownVanillaTrigger(trigger))
				return RecordKind.LegacyVanilla;

			// Not in vanilla catalog → treat as MOD / foreign keybind storage (keep).
			// Includes GP:ModName/InternalName and any non-catalog GP: token we must not guess-delete.
			if (!string.IsNullOrEmpty(trigger))
				return RecordKind.KeepMod;

			return RecordKind.Unclassified;
		}

		/// <summary>
		/// Build migration plan. Order of application is caller responsibility:
		/// classify → migrate/delete → persist Combos → then orphan AKAdv cleanup.
		/// </summary>
		public static List<PlanItem> BuildPlan(
			IEnumerable<string> storageKeys,
			Func<string, bool> hasBinding)
		{
			var plan = new List<PlanItem>();
			if (storageKeys == null || hasBinding == null)
				return plan;

			foreach (string key in storageKeys)
			{
				if (string.IsNullOrEmpty(key))
					continue;

				RecordKind kind = Classify(key);
				switch (kind)
				{
					case RecordKind.KeepModernGame:
						plan.Add(new PlanItem(key, kind, PlanAction.Keep, null, "GP:G: keep"));
						break;
					case RecordKind.KeepModernUi:
						plan.Add(new PlanItem(key, kind, PlanAction.Keep, null, "GP:U: keep"));
						break;
					case RecordKind.KeepMod:
						plan.Add(new PlanItem(key, kind, PlanAction.Keep, null, "MOD/non-catalog GP: keep"));
						break;
					case RecordKind.LegacyVanilla:
						plan.Add(PlanLegacyVanilla(key, hasBinding));
						break;
					default:
						plan.Add(new PlanItem(
							key,
							RecordKind.Unclassified,
							PlanAction.SkipUnclassified,
							null,
							"unclassified — no delete / no guess migrate"));
						break;
				}
			}

			return plan;
		}

		public static bool PlanRequiresCombosMutation(IReadOnlyList<PlanItem> plan)
		{
			if (plan == null)
				return false;
			for (int i = 0; i < plan.Count; i++)
			{
				if (plan[i].MutatesCombos)
					return true;
			}

			return false;
		}

		/// <summary>
		/// True when KeyStatus markers for <paramref name="trigger"/> on a GamePad mode have no owning Combos
		/// that sync to that mode (orphan AKAdv candidates).
		/// </summary>
		public static bool IsOrphanGamePadOwnershipTrigger(
			string trigger,
			int inputModeOrdinal,
			Func<string, bool> hasBinding)
		{
			if (string.IsNullOrEmpty(trigger) || hasBinding == null)
				return false;
			if (!TerrariaVanillaTriggerCatalog.IsKnownVanillaTrigger(trigger)
				&& trigger.IndexOf('/') < 0)
			{
				// Still allow orphan strip for any trigger string that has markers;
				// ownership is defined solely by Combos presence for this mode.
			}

			string g = ControlsGameUiSeparationPolicy.GamePadGamePrefix + trigger;
			string u = ControlsGameUiSeparationPolicy.GamePadUiPrefix + trigger;
			string legacy = ControlsGameUiSeparationPolicy.GamePadLegacyPrefix + trigger;

			if (inputModeOrdinal == ControlsGameUiSeparationPolicy.ModeGamePad)
			{
				if (hasBinding(g) || hasBinding(legacy))
					return false;
				// Mod legacy GP:Mod/x syncs both modes — hasBinding(legacy) already covered when key is GP:Mod/…
				return true;
			}

			if (inputModeOrdinal == ControlsGameUiSeparationPolicy.ModeGamePadUi)
			{
				if (hasBinding(u))
					return false;
				// Legacy mod GP:Mod/x owned both; vanilla legacy did not sync UI.
				if (hasBinding(legacy)
					&& !TerrariaVanillaTriggerCatalog.IsKnownVanillaTrigger(trigger))
					return false;
				return true;
			}

			return false;
		}

		private static PlanItem PlanLegacyVanilla(string storageKey, Func<string, bool> hasBinding)
		{
			string trigger = ControlsGameUiSeparationPolicy.ToTriggerActionId(storageKey);
			string g = ControlsGameUiSeparationPolicy.GamePadGamePrefix + trigger;
			string u = ControlsGameUiSeparationPolicy.GamePadUiPrefix + trigger;
			bool hasG = hasBinding(g);
			bool hasU = hasBinding(u);

			if (hasG || hasU)
			{
				return new PlanItem(
					storageKey,
					RecordKind.LegacyVanilla,
					PlanAction.DeleteDuplicate,
					null,
					"duplicate of " + (hasG ? g : u) + " — delete legacy");
			}

			string target = TerrariaVanillaTriggerCatalog.ResolveOrphanMigrateStorageKey(trigger);
			if (string.IsNullOrEmpty(target))
			{
				return new PlanItem(
					storageKey,
					RecordKind.LegacyVanilla,
					PlanAction.SkipUnclassified,
					null,
					"orphan vanilla but migrate target unresolved — skip");
			}

			return new PlanItem(
				storageKey,
				RecordKind.LegacyVanilla,
				PlanAction.Migrate,
				target,
				"orphan legacy → " + target
					+ (TerrariaVanillaTriggerCatalog.IsPadGameAssembleRow(trigger)
						? " (PadGameAssembleRows+legacy ModeGamePad)"
						: TerrariaVanillaTriggerCatalog.IsPreferGamePadUiTrigger(trigger)
							? " (Menu* PreferGamePadUi)"
							: " (known vanilla + legacy ModeGamePad sync)"));
		}
	}
}
