using System;
using System.Collections.Generic;
using System.IO;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Terraria-free single entry point for scheduling and executing legacy GamePad migration.
	/// </summary>
	public static class LegacyGamePadMigrationDriver
	{
		public sealed class State
		{
			public bool CombosMigrationCompletedOnce;
			public bool OrphanStripPending;
			public bool OrphanStripCompletedOnce;
		}

		public sealed class HostPaths
		{
			public string BindingsPath;
			public string ProfilesPath;
			public string SaveRoot;
		}

		public static LegacyGamePadMigrationCommitPipeline.Outcome ExecutePipeline(
			LegacyGamePadMigrationOrchestrator.Session session,
			HostPaths paths,
			string stamp,
			State state,
			Action<MigrationMutationPlan> applyMemory = null,
			Action<Dictionary<string, List<string>>, Dictionary<string, List<string>>> restoreMemory = null)
		{
			state ??= new State();
			paths ??= new HostPaths();
			string saveRoot = paths.SaveRoot;
			if (string.IsNullOrEmpty(saveRoot))
				saveRoot = Path.GetDirectoryName(paths.ProfilesPath) ?? string.Empty;
			LegacyGamePadMigrationCommitPipeline.Outcome recovery =
				LegacyGamePadMigrationCommitPipeline.TryRecoverIncompleteThenStop(
					saveRoot, paths.BindingsPath, paths.ProfilesPath,
					restoreMemory: restoreMemory);
			if (recovery.RecoveryStopped)
				return recovery;
			if (session == null)
				return new LegacyGamePadMigrationCommitPipeline.Outcome { Aborted = true };

			var keys = new List<string>(session.Combos.Keys);
			List<LegacyGamePadStorageMigrationPolicy.PlanItem> storagePlan =
				LegacyGamePadMigrationOrchestrator.BuildDtSafeStoragePlan(
					keys,
					key => session.Combos.ContainsKey(key),
					key => session.Combos.TryGetValue(key, out ComboBindingData data) ? data : null);
			Dictionary<string, ComboBindingData> combosAfter =
				LegacyGamePadMigrationCommitPipeline.SimulateStoragePlan(session.Combos, storagePlan);

			Dictionary<string, List<string>> liveGp = LegacyGamePadKeyStatusMaps.Clone(session.KeyStatusGamepad);
			Dictionary<string, List<string>> liveGpUi = LegacyGamePadKeyStatusMaps.Clone(session.KeyStatusGamepadUi);

			var request = new LegacyGamePadMigrationCommitPipeline.Request {
				BindingsPath = paths.BindingsPath,
				ProfilesPath = paths.ProfilesPath,
				SaveRoot = saveRoot,
				Stamp = stamp,
				SelectedProfileName = session.ProfilesRoot == null
					? "Custom"
					: LegacyGamePadProfilesDocument.SelectedProfileName(session.ProfilesRoot),
				CombosBefore = session.Combos,
				CombosAfter = combosAfter,
				GpBefore = session.KeyStatusGamepad,
				GpUiBefore = session.KeyStatusGamepadUi,
				ProfileReady = session.ProfileReady,
				State = state,
				ApplyMemoryMutation = plan => {
					LegacyGamePadKeyStatusMaps.ApplyMutation(
						liveGp, plan.Gamepad.UpsertActions, plan.Gamepad.RemoveActions);
					LegacyGamePadKeyStatusMaps.ApplyMutation(
						liveGpUi, plan.GamepadUi.UpsertActions, plan.GamepadUi.RemoveActions);
					applyMemory?.Invoke(plan);
				},
				RestoreMemoryFull = (gp, gpUi) => {
					LegacyGamePadKeyStatusMaps.RestoreFull(liveGp, gp);
					LegacyGamePadKeyStatusMaps.RestoreFull(liveGpUi, gpUi);
					restoreMemory?.Invoke(gp, gpUi);
				}
			};
			LegacyGamePadMigrationCommitPipeline.Outcome outcome =
				LegacyGamePadMigrationCommitPipeline.Run(request);
			if (outcome.Completed && outcome.Plan != null && !outcome.Plan.IsNoOp)
			{
				session.Combos = outcome.CombosResult;
				session.KeyStatusGamepad = outcome.GpResult;
				session.KeyStatusGamepadUi = outcome.GpUiResult;
			}
			else if (outcome.Aborted || outcome.CriticalRollbackFailure)
			{
				session.KeyStatusGamepad = LegacyGamePadKeyStatusMaps.Clone(liveGp);
				session.KeyStatusGamepadUi = LegacyGamePadKeyStatusMaps.Clone(liveGpUi);
			}
			state.CombosMigrationCompletedOnce |= outcome.Completed
				&& outcome.Plan != null && outcome.Plan.CombosNeedMutation;
			state.OrphanStripPending = outcome.OrphanPending || outcome.Aborted;
			if (outcome.Completed && session.ProfileReady && !outcome.OrphanPending)
			{
				state.OrphanStripPending = false;
				state.OrphanStripCompletedOnce = true;
			}
			return outcome;
		}

		public static LegacyGamePadMigrationOrchestrator.Result Execute(
			LegacyGamePadMigrationOrchestrator.Session session,
			HostPaths paths,
			string stamp,
			State state,
			LegacyGamePadMigrationOrchestrator.BackupFailMode failMode =
				LegacyGamePadMigrationOrchestrator.BackupFailMode.AbortRequired,
			Func<string, string, bool> tryCopy = null,
			Func<string, Dictionary<string, ComboBindingData>, bool> tryWriteBindings = null,
			Func<string, LegacyGamePadMigrationOrchestrator.Session, bool> tryWriteProfiles = null,
			Func<string, LegacyGamePadMigrationOrchestrator.Session, bool> tryFinalizeAfterProfiles = null,
			Func<string, string, bool> tryRestoreFile = null)
		{
			bool compatibilityInjection = tryCopy != null || tryWriteBindings != null
				|| tryWriteProfiles != null || tryFinalizeAfterProfiles != null || tryRestoreFile != null
				|| failMode != LegacyGamePadMigrationOrchestrator.BackupFailMode.AbortRequired;
			if (!compatibilityInjection && session != null)
			{
				LegacyGamePadMigrationCommitPipeline.Outcome pipeline =
					ExecutePipeline(session, paths, stamp, state);
				return ToLegacyResult(pipeline);
			}
			state ??= new State();
			paths ??= new HostPaths();

			if (session == null)
			{
				return LegacyGamePadMigrationOrchestrator.Run(
					null,
					paths.BindingsPath,
					paths.ProfilesPath,
					stamp,
					LegacyGamePadMigrationOrchestrator.RunPhase.Full,
					failMode,
					tryCopy,
					tryWriteBindings,
					tryWriteProfiles,
					tryFinalizeAfterProfiles,
					tryRestoreFile);
			}

			var keys = new List<string>(session.Combos.Count);
			foreach (KeyValuePair<string, ComboBindingData> pair in session.Combos)
				keys.Add(pair.Key);

			List<LegacyGamePadStorageMigrationPolicy.PlanItem> plan =
				LegacyGamePadMigrationOrchestrator.BuildDtSafeStoragePlan(
					keys,
					key => session.Combos.ContainsKey(key),
					key => session.Combos.TryGetValue(key, out ComboBindingData data) ? data : null);
			bool combosNeedMutation =
				LegacyGamePadStorageMigrationPolicy.PlanRequiresCombosMutation(plan);

			int detectedOrphans = CountOrphans(session);
			int orphanCount = session.ProfileReady ? detectedOrphans : 0;
			bool orphanMarkersPresent = orphanCount > 0;
			bool orphanExpected = session.ProfileReady
				? orphanMarkersPresent
				: detectedOrphans > 0 || combosNeedMutation || state.OrphanStripPending;

			LegacyGamePadMigrationRetryPolicy.Phase next =
				LegacyGamePadMigrationRetryPolicy.NextPhase(
					combosNeedMutation,
					orphanExpected,
					session.ProfileReady,
					state.CombosMigrationCompletedOnce,
					state.OrphanStripPending,
					state.OrphanStripCompletedOnce);

			session.OrphanPendingRequested =
				session.OrphanPendingRequested || state.OrphanStripPending || orphanExpected;

			if (next == LegacyGamePadMigrationRetryPolicy.Phase.None)
			{
				if (!session.ProfileReady && orphanExpected)
					state.OrphanStripPending = true;
				var skip = BuildSkipOnlyResult(session, plan);
				skip.OrphanPending = state.OrphanStripPending;
				skip.OrphanDeferred = !session.ProfileReady && state.OrphanStripPending;
				return skip;
			}

			LegacyGamePadMigrationOrchestrator.RunPhase runPhase =
				next == LegacyGamePadMigrationRetryPolicy.Phase.OrphanStripOnly
					? LegacyGamePadMigrationOrchestrator.RunPhase.OrphanStripOnly
					: LegacyGamePadMigrationOrchestrator.RunPhase.Full;

			LegacyGamePadMigrationOrchestrator.Result result =
				LegacyGamePadMigrationOrchestrator.Run(
					session,
					paths.BindingsPath,
					paths.ProfilesPath,
					stamp,
					runPhase,
					failMode,
					tryCopy,
					tryWriteBindings,
					tryWriteProfiles,
					tryFinalizeAfterProfiles,
					tryRestoreFile);

			if (runPhase == LegacyGamePadMigrationOrchestrator.RunPhase.Full
				&& combosNeedMutation
				&& !result.Aborted
				&& result.BindingsSaveSucceeded)
			{
				state.CombosMigrationCompletedOnce = true;
			}

			bool combosPhaseRan =
				runPhase == LegacyGamePadMigrationOrchestrator.RunPhase.Full
				&& combosNeedMutation;
			if (result.OrphanPending
				|| LegacyGamePadMigrationRetryPolicy.ShouldMarkOrphanPending(
					session.ProfileReady,
					orphanExpected,
					combosPhaseRan))
			{
				state.OrphanStripPending = true;
			}

			if (result.OrphanStripCompleted)
			{
				state.OrphanStripPending = false;
				state.OrphanStripCompletedOnce = true;
			}

			if (result.Aborted && result.OrphanPending)
				state.OrphanStripPending = true;

			return result;
		}

		private static LegacyGamePadMigrationOrchestrator.Result ToLegacyResult(
			LegacyGamePadMigrationCommitPipeline.Outcome outcome)
		{
			var result = new LegacyGamePadMigrationOrchestrator.Result {
				Aborted = outcome?.Aborted ?? true,
				CriticalRollbackFailure = outcome?.CriticalRollbackFailure ?? false,
				OrphanPending = outcome?.OrphanPending ?? false,
				OrphanDeferred = outcome?.OrphanPending ?? false,
				CombosMutated = outcome?.Completed == true && outcome.Plan?.CombosNeedMutation == true,
				KeyStatusMutated = outcome?.Completed == true && outcome.Plan?.KeyStatusNeedMutation == true,
				BindingsSaveAttempted = outcome?.Plan?.CombosNeedMutation == true,
				BindingsSaveSucceeded = outcome?.Completed == true && outcome.Plan?.CombosNeedMutation == true,
				ProfilesSaveAttempted = outcome?.Plan?.KeyStatusNeedMutation == true,
				ProfilesSaveSucceeded = outcome?.Completed == true && outcome.Plan?.KeyStatusNeedMutation == true,
				OrphanStripCompleted = outcome?.Completed == true && outcome.Plan?.KeyStatusNeedMutation == true,
				Log = outcome?.Log == null ? new List<string>() : new List<string>(outcome.Log)
			};
			if (outcome?.Plan?.StoragePlan != null)
			{
				for (int i = 0; i < outcome.Plan.StoragePlan.Count; i++)
				{
					if (outcome.Plan.StoragePlan[i].Action == LegacyGamePadStorageMigrationPolicy.PlanAction.Migrate)
						result.MigrateOps++;
					else if (outcome.Plan.StoragePlan[i].Action == LegacyGamePadStorageMigrationPolicy.PlanAction.DeleteDuplicate)
						result.DeleteOps++;
				}
			}
			result.StripOps = (outcome?.Plan?.Gamepad.RemoveActions.Count ?? 0)
				+ (outcome?.Plan?.GamepadUi.RemoveActions.Count ?? 0);
			return result;
		}

		public static int CountOrphans(LegacyGamePadMigrationOrchestrator.Session session)
			=> LegacyGamePadMigrationOrchestrator.CountOrphansInSession(session);

		private static LegacyGamePadMigrationOrchestrator.Result BuildSkipOnlyResult(
			LegacyGamePadMigrationOrchestrator.Session session,
			List<LegacyGamePadStorageMigrationPolicy.PlanItem> plan)
		{
			var result = new LegacyGamePadMigrationOrchestrator.Result();
			for (int i = 0; i < plan.Count; i++)
			{
				LegacyGamePadStorageMigrationPolicy.PlanItem item = plan[i];
				if (item.Action != LegacyGamePadStorageMigrationPolicy.PlanAction.SkipUnclassified)
					continue;
				string line = "[LegacyGpMigrate] SKIP"
					+ " from=" + (item.StorageKey ?? "-")
					+ " to=" + (item.TargetStorageKey ?? "-")
					+ " reason=" + (item.Reason ?? "-");
				session.Log.Add(line);
			}

			result.Log = new List<string>(session.Log);
			return result;
		}
	}
}
