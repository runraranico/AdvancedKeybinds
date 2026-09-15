using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AdvancedKeybinds.Core
{
	public static class LegacyGamePadMigrationCommitPipeline
	{
		public enum FailPoint
		{
			None,
			BeforeBindingsStaging,
			AfterBindingsStaging,
			BeforeProfilesStaging,
			AfterProfilesStaging,
			AfterJournalPrepared,
			AfterCommitStarted,
			AfterBindingsReplaced,
			AfterProfilesReplaced,
			DuringVerify,
			DuringRollbackBindings,
			DuringRollbackProfiles,
			DuringRestoreMemory
		}

		public sealed class Request
		{
			public string BindingsPath, ProfilesPath, SaveRoot, Stamp, SelectedProfileName;
			public Dictionary<string, ComboBindingData> CombosBefore;
			public Dictionary<string, ComboBindingData> CombosAfter;
			public Dictionary<string, List<string>> GpBefore, GpUiBefore;
			public bool ProfileReady;
			public LegacyGamePadMigrationDriver.State State;
			public Func<string, byte[], bool> TryWriteBytesAtomic;
			public Func<string, string, bool> TryReplaceFile;
			public Func<string, string, bool> TryRestoreFile;
			/// <summary>Apply targeted KeyStatus mutation to live host (PlayerInput or test store).</summary>
			public Action<MigrationMutationPlan> ApplyMemoryMutation;
			/// <summary>Fully restore Gamepad / Gamepad UI KeyStatus to the pre-migration snapshot.</summary>
			public Action<Dictionary<string, List<string>>, Dictionary<string, List<string>>> RestoreMemoryFull;
			public Action FailAfter;
			public FailPoint InjectFail;
		}

		public sealed class Outcome
		{
			public bool Aborted, CriticalRollbackFailure, CriticalRecoveryConflict, Completed;
			public bool OrphanPending;
			public MigrationMutationPlan Plan;
			public List<string> Log = new();
			public Dictionary<string, ComboBindingData> CombosResult;
			public Dictionary<string, List<string>> GpResult, GpUiResult;
			public bool RecoveryStopped;
			public bool MemoryApplied;
		}

		public static Outcome Run(Request req)
		{
			var outcome = NewOutcome(req);
			if (req == null)
			{
				outcome.Aborted = true;
				return outcome;
			}

			// 1–2: snapshot inputs are req.*Before; build after maps + MutationMutationPlan once.
			List<LegacyGamePadStorageMigrationPolicy.PlanItem> storagePlan =
				BuildStoragePlan(req.CombosBefore);
			var migrated = new HashSet<string>(StringComparer.Ordinal);
			for (int i = 0; i < storagePlan.Count; i++)
			{
				if (!storagePlan[i].MutatesCombos)
					continue;
				AddTrigger(migrated, storagePlan[i].StorageKey);
				AddTrigger(migrated, storagePlan[i].TargetStorageKey);
			}
			MigrationMutationPlan.BuildAfterMapsFromMigration(
				req.GpBefore, req.GpUiBefore, req.CombosAfter, storagePlan,
				req.ProfileReady, out Dictionary<string, List<string>> gpAfter,
				out Dictionary<string, List<string>> gpUiAfter);
			AddChangedKeys(migrated, req.GpBefore, gpAfter);
			AddChangedKeys(migrated, req.GpUiBefore, gpUiAfter);
			outcome.Plan = MigrationMutationPlan.Build(
				req.GpBefore, req.GpUiBefore, gpAfter, gpUiAfter, storagePlan, migrated);
			outcome.OrphanPending = !req.ProfileReady
				&& (outcome.Plan.CombosNeedMutation || HasAnyMarker(req.GpBefore) || HasAnyMarker(req.GpUiBefore));
			if (outcome.Plan.IsNoOp)
			{
				outcome.Completed = true;
				outcome.CombosResult = CloneCombos(req.CombosBefore);
				outcome.GpResult = LegacyGamePadKeyStatusMaps.Clone(req.GpBefore);
				outcome.GpUiResult = LegacyGamePadKeyStatusMaps.Clone(req.GpUiBefore);
				return outcome;
			}

			// 3–4: apply targeted diff onto working maps; verify targets + out-of-scope.
			Dictionary<string, List<string>> gpWorking = LegacyGamePadKeyStatusMaps.Clone(req.GpBefore);
			Dictionary<string, List<string>> gpUiWorking = LegacyGamePadKeyStatusMaps.Clone(req.GpUiBefore);
			LegacyGamePadKeyStatusMaps.ApplyMutation(
				gpWorking, outcome.Plan.Gamepad.UpsertActions, outcome.Plan.Gamepad.RemoveActions);
			LegacyGamePadKeyStatusMaps.ApplyMutation(
				gpUiWorking, outcome.Plan.GamepadUi.UpsertActions, outcome.Plan.GamepadUi.RemoveActions);
			if (!LegacyGamePadKeyStatusMaps.VerifyTargetsAndOutOfScope(
					gpWorking, outcome.Plan.Gamepad, gpAfter)
				|| !LegacyGamePadKeyStatusMaps.VerifyTargetsAndOutOfScope(
					gpUiWorking, outcome.Plan.GamepadUi, gpUiAfter))
			{
				return AbortBeforeCommit(outcome, "memory mutation verify failed");
			}

			bool memoryApplied = false;
			try
			{
				req.ApplyMemoryMutation?.Invoke(outcome.Plan);
				memoryApplied = true;
				outcome.MemoryApplied = true;
			}
			catch (Exception ex)
			{
				outcome.Log.Add("[LegacyGpMigrate] MEMORY_APPLY_FAIL reason=" + ex.Message);
				return AbortBeforeCommit(outcome, "memory apply failed");
			}

			byte[] bindingsBefore = ReadOptional(req.BindingsPath);
			byte[] profilesBefore = ReadOptional(req.ProfilesPath);
			byte[] bindingsStaging = Encoding.UTF8.GetBytes(
				BindingJsonCodec.Save(req.CombosAfter ?? new Dictionary<string, ComboBindingData>()));
			JObject profilesRoot = LoadProfiles(profilesBefore);
			JObject profilesBeforeDoc = LoadProfiles(profilesBefore);
			if (req.ProfileReady && outcome.Plan.KeyStatusNeedMutation)
				LegacyGamePadProfilesDocument.PatchSelectedProfileGamepad(
					profilesRoot, outcome.Plan.Gamepad, outcome.Plan.GamepadUi);
			byte[] profilesStaging = Encoding.UTF8.GetBytes(profilesRoot.ToString(Formatting.Indented));
			req.TryWriteBytesAtomic ??= TryWriteBytesAtomicDefault;
			req.TryReplaceFile ??= TryReplaceFileDefault;
			req.TryRestoreFile ??= LegacyGamePadMigrationTransaction.TryRestoreFileOverwrite;

			string txnId = (string.IsNullOrWhiteSpace(req.Stamp) ? "txn" : req.Stamp)
				+ "-" + Guid.NewGuid().ToString("N");
			// Staging beside real files (same parent / volume) before any live replace.
			string bindingsStagePath = SiblingStagingPath(req.BindingsPath, "bindings");
			string profilesStagePath = SiblingStagingPath(req.ProfilesPath, "profiles");
			bool commitStarted = false;
			try
			{
				Inject(req, FailPoint.BeforeBindingsStaging);
				if (!req.TryWriteBytesAtomic(bindingsStagePath, bindingsStaging))
					return AbortWithMemoryRestore(req, outcome, memoryApplied, "bindings staging failed");
				if (!TryVerifyBindingsStaging(bindingsStagePath, bindingsStaging))
					return AbortWithMemoryRestore(req, outcome, memoryApplied, "bindings staging verify failed");
				Inject(req, FailPoint.AfterBindingsStaging);

				Inject(req, FailPoint.BeforeProfilesStaging);
				if (!req.TryWriteBytesAtomic(profilesStagePath, profilesStaging))
					return AbortWithMemoryRestore(req, outcome, memoryApplied, "profiles staging failed");
				if (!TryVerifyProfilesStaging(
						profilesStagePath, profilesStaging, profilesBeforeDoc, outcome.Plan, req.ProfileReady))
					return AbortWithMemoryRestore(req, outcome, memoryApplied, "profiles staging verify failed");
				Inject(req, FailPoint.AfterProfilesStaging);

				// 8: durable journal + byte backups (must succeed before live replace).
				if (!LegacyGamePadMigrationJournal.TryCreatePrepared(
					req.SaveRoot, txnId, req.SelectedProfileName,
					req.BindingsPath, req.ProfilesPath,
					bindingsBefore, profilesBefore, bindingsStaging, profilesStaging,
					outcome.Plan, req.GpBefore, req.GpUiBefore,
					out LegacyGamePadMigrationJournal.JournalRecord journal))
					return AbortWithMemoryRestore(req, outcome, memoryApplied, "journal prepare failed");
				Inject(req, FailPoint.AfterJournalPrepared);
				if (!LegacyGamePadMigrationJournal.TrySetPhase(journal.DirectoryPath, JournalPhase.CommitStarted))
					return AbortWithMemoryRestore(req, outcome, memoryApplied, "journal commit-start failed");
				commitStarted = true;
				Inject(req, FailPoint.AfterCommitStarted);

				if (outcome.Plan.CombosNeedMutation
					&& !req.TryReplaceFile(Path.Combine(journal.DirectoryPath, "bindings.staging"), req.BindingsPath))
					throw new IOException("bindings replace failed");
				LegacyGamePadMigrationJournal.TrySetPhase(journal.DirectoryPath, JournalPhase.BindingsReplaced);
				Inject(req, FailPoint.AfterBindingsReplaced);

				if (req.ProfileReady && outcome.Plan.KeyStatusNeedMutation
					&& !req.TryReplaceFile(Path.Combine(journal.DirectoryPath, "profiles.staging"), req.ProfilesPath))
					throw new IOException("profiles replace failed");
				LegacyGamePadMigrationJournal.TrySetPhase(journal.DirectoryPath, JournalPhase.ProfilesReplaced);
				Inject(req, FailPoint.AfterProfilesReplaced);
				Inject(req, FailPoint.DuringVerify);

				if (outcome.Plan.CombosNeedMutation
					&& !string.Equals(
						LegacyGamePadMigrationJournal.ShaFile(req.BindingsPath),
						LegacyGamePadMigrationJournal.Sha(bindingsStaging),
						StringComparison.Ordinal))
					throw new IOException("bindings verify failed");
				if (req.ProfileReady && outcome.Plan.KeyStatusNeedMutation)
				{
					if (!string.Equals(
						LegacyGamePadMigrationJournal.ShaFile(req.ProfilesPath),
						LegacyGamePadMigrationJournal.Sha(profilesStaging),
						StringComparison.Ordinal))
						throw new IOException("profiles verify failed");
					JObject disk = JObject.Parse(File.ReadAllText(req.ProfilesPath));
					if (!LegacyGamePadProfilesDocument.SemanticEqualsUnrelated(profilesBeforeDoc, disk))
						throw new IOException("unrelated profiles changed");
					if (!LegacyGamePadKeyStatusMaps.MapsEqual(gpWorking, gpAfter)
						|| !LegacyGamePadKeyStatusMaps.MapsEqual(gpUiWorking, gpUiAfter))
						throw new IOException("memory vs expected after mismatch");
				}
				LegacyGamePadMigrationJournal.TrySetPhase(journal.DirectoryPath, JournalPhase.Verified);
				// Markers / Completed only after disk+memory verification.
				LegacyGamePadMigrationJournal.TrySetPhase(journal.DirectoryPath, JournalPhase.Completed);
				outcome.Completed = true;
				outcome.CombosResult = CloneCombos(req.CombosAfter);
				outcome.GpResult = gpAfter;
				outcome.GpUiResult = gpUiAfter;
				TryDeleteQuiet(bindingsStagePath);
				TryDeleteQuiet(profilesStagePath);
				return outcome;
			}
			catch (Exception ex)
			{
				outcome.Log.Add("[LegacyGpMigrate] COMMIT_FAIL reason=" + ex.Message);
				if (commitStarted)
					Rollback(req, outcome, txnId, bindingsBefore, profilesBefore, memoryApplied);
				else
				{
					outcome.Aborted = true;
					if (memoryApplied)
						TryRestoreMemory(req, outcome);
				}
				TryDeleteQuiet(bindingsStagePath);
				TryDeleteQuiet(profilesStagePath);
				return outcome;
			}
		}

		public static Outcome TryRecoverIncompleteThenStop(
			string saveRoot,
			string bindingsPath,
			string profilesPath,
			Func<string, string, bool> tryRestore = null,
			Action<Dictionary<string, List<string>>, Dictionary<string, List<string>>> restoreMemory = null)
		{
			var outcome = new Outcome();
			LegacyGamePadMigrationJournal.JournalRecord journal =
				LegacyGamePadMigrationJournal.TryLoadIncomplete(saveRoot);
			if (journal == null)
				return outcome;
			LegacyGamePadMigrationJournal.RecoveryResult recovered =
				LegacyGamePadMigrationJournal.TryRecover(
					journal, bindingsPath, profilesPath, tryRestore);
			outcome.RecoveryStopped = true;
			outcome.CriticalRecoveryConflict = recovered.CriticalRecoveryConflict;
			outcome.Aborted = recovered.CriticalRecoveryConflict;
			outcome.Completed = recovered.Recovered;
			outcome.GpResult = recovered.GpSnapshot;
			outcome.GpUiResult = recovered.GpUiSnapshot;
			if (recovered.Recovered && restoreMemory != null
				&& recovered.GpSnapshot != null && recovered.GpUiSnapshot != null)
			{
				try
				{
					restoreMemory(recovered.GpSnapshot, recovered.GpUiSnapshot);
					outcome.Log.Add("[LegacyGpMigrate] RECOVERY memory restored from journal snapshots");
				}
				catch (Exception ex)
				{
					outcome.CriticalRecoveryConflict = true;
					outcome.Aborted = true;
					outcome.Completed = false;
					outcome.Log.Add("[LegacyGpMigrate] RECOVERY_MEMORY_FAIL reason=" + ex.Message);
				}
			}
			return outcome;
		}

		private static void Rollback(
			Request req,
			Outcome outcome,
			string txnId,
			byte[] bindingsBefore,
			byte[] profilesBefore,
			bool memoryApplied)
		{
			outcome.Aborted = true;
			string dir = Path.Combine(LegacyGamePadMigrationJournal.TransactionRoot(req.SaveRoot), txnId);
			LegacyGamePadMigrationJournal.TrySetPhase(dir, JournalPhase.RollbackStarted);
			outcome.Log.Add("[LegacyGpMigrate] ROLLBACK started");
			bool bindingsOk = RestoreOne(req, dir, "bindings.bak", req.BindingsPath, bindingsBefore != null, FailPoint.DuringRollbackBindings);
			bool profilesOk = RestoreOne(req, dir, "profiles.bak", req.ProfilesPath, profilesBefore != null, FailPoint.DuringRollbackProfiles);
			bool memoryOk = true;
			if (memoryApplied)
			{
				try
				{
					Inject(req, FailPoint.DuringRestoreMemory);
					TryRestoreMemory(req, outcome);
				}
				catch
				{
					memoryOk = false;
				}
			}
			else
			{
				outcome.CombosResult = CloneCombos(req.CombosBefore);
				outcome.GpResult = LegacyGamePadKeyStatusMaps.Clone(req.GpBefore);
				outcome.GpUiResult = LegacyGamePadKeyStatusMaps.Clone(req.GpUiBefore);
			}

			bool shaOk = true;
			if (bindingsBefore != null)
				shaOk &= string.Equals(
					LegacyGamePadMigrationJournal.ShaFile(req.BindingsPath),
					LegacyGamePadMigrationJournal.Sha(bindingsBefore),
					StringComparison.Ordinal);
			else
				shaOk &= !File.Exists(req.BindingsPath);
			if (profilesBefore != null)
				shaOk &= string.Equals(
					LegacyGamePadMigrationJournal.ShaFile(req.ProfilesPath),
					LegacyGamePadMigrationJournal.Sha(profilesBefore),
					StringComparison.Ordinal);
			else
				shaOk &= !File.Exists(req.ProfilesPath);

			bool mapsOk = LegacyGamePadKeyStatusMaps.MapsEqual(outcome.GpResult, req.GpBefore)
				&& LegacyGamePadKeyStatusMaps.MapsEqual(outcome.GpUiResult, req.GpUiBefore);

			if (!bindingsOk || !profilesOk || !memoryOk || !shaOk || !mapsOk)
			{
				outcome.CriticalRollbackFailure = true;
				LegacyGamePadMigrationJournal.TrySetPhase(dir, JournalPhase.CriticalRollbackFailure);
				outcome.Log.Add("[LegacyGpMigrate] ROLLBACK critical failure");
			}
			else
			{
				LegacyGamePadMigrationJournal.TrySetPhase(dir, JournalPhase.RolledBack);
				outcome.Log.Add("[LegacyGpMigrate] ROLLBACK completed pending");
			}
		}

		private static void TryRestoreMemory(Request req, Outcome outcome)
		{
			outcome.CombosResult = CloneCombos(req.CombosBefore);
			outcome.GpResult = LegacyGamePadKeyStatusMaps.Clone(req.GpBefore);
			outcome.GpUiResult = LegacyGamePadKeyStatusMaps.Clone(req.GpUiBefore);
			req.RestoreMemoryFull?.Invoke(outcome.GpResult, outcome.GpUiResult);
		}

		private static Outcome AbortWithMemoryRestore(
			Request req, Outcome outcome, bool memoryApplied, string reason)
		{
			AbortBeforeCommit(outcome, reason);
			if (memoryApplied)
			{
				try { TryRestoreMemory(req, outcome); }
				catch
				{
					outcome.CriticalRollbackFailure = true;
					outcome.Log.Add("[LegacyGpMigrate] ROLLBACK critical failure memory");
				}
			}
			return outcome;
		}

		private static bool RestoreOne(
			Request req, string dir, string backupName, string destination,
			bool existed, FailPoint failPoint)
		{
			try
			{
				Inject(req, failPoint);
				if (!existed)
				{
					if (File.Exists(destination))
						File.Delete(destination);
					return !File.Exists(destination);
				}
				return req.TryRestoreFile(Path.Combine(dir, backupName), destination);
			}
			catch { return false; }
		}

		private static Outcome AbortBeforeCommit(Outcome outcome, string reason)
		{
			outcome.Aborted = true;
			outcome.Log.Add("[LegacyGpMigrate] MIGRATION_ABORTED reason=" + reason);
			return outcome;
		}

		private static Outcome NewOutcome(Request req)
			=> new() {
				CombosResult = CloneCombos(req?.CombosBefore),
				GpResult = LegacyGamePadKeyStatusMaps.Clone(req?.GpBefore),
				GpUiResult = LegacyGamePadKeyStatusMaps.Clone(req?.GpUiBefore)
			};

		private static List<LegacyGamePadStorageMigrationPolicy.PlanItem> BuildStoragePlan(
			Dictionary<string, ComboBindingData> combos)
			=> LegacyGamePadStorageMigrationPolicy.BuildPlan(
				combos == null ? Array.Empty<string>() : (IEnumerable<string>)combos.Keys,
				key => combos != null && combos.ContainsKey(key));

		public static Dictionary<string, ComboBindingData> SimulateStoragePlan(
			Dictionary<string, ComboBindingData> before,
			IReadOnlyList<LegacyGamePadStorageMigrationPolicy.PlanItem> plan)
		{
			Dictionary<string, ComboBindingData> after = CloneCombos(before);
			if (plan == null)
				return after;
			for (int i = 0; i < plan.Count; i++)
			{
				LegacyGamePadStorageMigrationPolicy.PlanItem item = plan[i];
				if (item.Action == LegacyGamePadStorageMigrationPolicy.PlanAction.DeleteDuplicate)
					after.Remove(item.StorageKey);
				else if (item.Action == LegacyGamePadStorageMigrationPolicy.PlanAction.Migrate
					&& after.TryGetValue(item.StorageKey, out ComboBindingData value))
				{
					after.Remove(item.StorageKey);
					if (!after.ContainsKey(item.TargetStorageKey))
						after[item.TargetStorageKey] = value?.Clone();
				}
			}
			return after;
		}

		private static Dictionary<string, ComboBindingData> CloneCombos(Dictionary<string, ComboBindingData> source)
		{
			var result = new Dictionary<string, ComboBindingData>(StringComparer.Ordinal);
			if (source != null)
			{
				foreach (KeyValuePair<string, ComboBindingData> pair in source)
					result[pair.Key] = pair.Value?.Clone();
			}
			return result;
		}

		private static JObject LoadProfiles(byte[] bytes)
		{
			if (bytes == null || bytes.Length == 0)
				return new JObject { ["Selected Profile"] = "Custom", ["Custom"] = new JObject() };
			string json = Encoding.UTF8.GetString(bytes);
			if (json.Length > 0 && json[0] == '\uFEFF')
				json = json.Substring(1);
			return JObject.Parse(json);
		}

		private static byte[] ReadOptional(string path)
			=> string.IsNullOrEmpty(path) || !File.Exists(path) ? null : File.ReadAllBytes(path);

		private static string SiblingStagingPath(string realPath, string label)
		{
			string dir = Path.GetDirectoryName(realPath) ?? ".";
			string name = Path.GetFileName(realPath) ?? label;
			return Path.Combine(dir, name + ".ak-mig-stage");
		}

		private static bool TryVerifyBindingsStaging(string path, byte[] expected)
		{
			try
			{
				if (!File.Exists(path))
					return false;
				byte[] disk = File.ReadAllBytes(path);
				if (!string.Equals(
					LegacyGamePadMigrationJournal.Sha(disk),
					LegacyGamePadMigrationJournal.Sha(expected),
					StringComparison.Ordinal))
					return false;
				string json = Encoding.UTF8.GetString(disk);
				BindingJsonCodec.LoadResult loaded = BindingJsonCodec.Load(json);
				return loaded.Kind != BindingJsonCodec.LoadKind.InvalidJson
					&& loaded.Kind != BindingJsonCodec.LoadKind.NullDocument;
			}
			catch { return false; }
		}

		private static bool TryVerifyProfilesStaging(
			string path,
			byte[] expected,
			JObject beforeDoc,
			MigrationMutationPlan plan,
			bool profileReady)
		{
			try
			{
				if (!File.Exists(path))
					return false;
				byte[] disk = File.ReadAllBytes(path);
				if (!string.Equals(
					LegacyGamePadMigrationJournal.Sha(disk),
					LegacyGamePadMigrationJournal.Sha(expected),
					StringComparison.Ordinal))
					return false;
				JObject staged = JObject.Parse(Encoding.UTF8.GetString(disk));
				if (!LegacyGamePadProfilesDocument.SemanticEqualsUnrelated(beforeDoc, staged))
					return false;
				if (profileReady && plan != null && plan.KeyStatusNeedMutation)
				{
					// Target upserts must appear; removes must be absent under selected profile.
					string name = LegacyGamePadProfilesDocument.SelectedProfileName(staged);
					if (staged[name] is not JObject profile)
						return false;
					if (!SectionMatchesMutation(
							profile[LegacyGamePadProfilesDocument.GamepadSection] as JObject, plan.Gamepad))
						return false;
					if (!SectionMatchesMutation(
							profile[LegacyGamePadProfilesDocument.GamepadUiSection] as JObject, plan.GamepadUi))
						return false;
				}
				return true;
			}
			catch { return false; }
		}

		private static bool SectionMatchesMutation(JObject section, ModeMutation mutation)
		{
			if (mutation == null)
				return true;
			section ??= new JObject();
			foreach (string remove in mutation.RemoveActions)
			{
				if (section[remove] != null)
					return false;
			}
			foreach (KeyValuePair<string, List<string>> pair in mutation.UpsertActions)
			{
				if (section[pair.Key] is not JArray arr)
					return false;
				var list = new List<string>();
				for (int i = 0; i < arr.Count; i++)
					list.Add(arr[i]?.ToString());
				if (!LegacyGamePadKeyStatusMaps.MapsEqual(
					new Dictionary<string, List<string>>(StringComparer.Ordinal) { [pair.Key] = list },
					new Dictionary<string, List<string>>(StringComparer.Ordinal) { [pair.Key] = pair.Value }))
					return false;
			}
			return true;
		}

		private static bool TryWriteBytesAtomicDefault(string path, byte[] bytes)
		{
			try
			{
				Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
				string temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
				File.WriteAllBytes(temp, bytes ?? Array.Empty<byte>());
				if (File.Exists(path))
				{
					string backup = path + ".old-" + Guid.NewGuid().ToString("N");
					File.Replace(temp, path, backup, true);
					try { File.Delete(backup); } catch { }
				}
				else
					File.Move(temp, path);
				return true;
			}
			catch { return false; }
		}

		private static bool TryReplaceFileDefault(string staging, string final)
		{
			try
			{
				Directory.CreateDirectory(Path.GetDirectoryName(final) ?? ".");
				string temp = final + ".migration-replace-" + Guid.NewGuid().ToString("N");
				File.Copy(staging, temp, true);
				if (File.Exists(final))
				{
					string backup = final + ".migration-old-" + Guid.NewGuid().ToString("N");
					File.Replace(temp, final, backup, true);
					try { File.Delete(backup); } catch { }
				}
				else
					File.Move(temp, final);
				return true;
			}
			catch { return false; }
		}

		private static void TryDeleteQuiet(string path)
		{
			try
			{
				if (!string.IsNullOrEmpty(path) && File.Exists(path))
					File.Delete(path);
			}
			catch { /* ignore */ }
		}

		private static void Inject(Request req, FailPoint point)
		{
			if (req.InjectFail != point)
				return;
			req.FailAfter?.Invoke();
			throw new IOException("injected " + point);
		}

		private static void AddTrigger(HashSet<string> set, string storageKey)
		{
			string trigger = ControlsGameUiSeparationPolicy.ToTriggerActionId(storageKey);
			if (!string.IsNullOrEmpty(trigger))
				set.Add(trigger);
		}

		private static void AddChangedKeys(
			HashSet<string> set,
			Dictionary<string, List<string>> before,
			Dictionary<string, List<string>> after)
		{
			var keys = new HashSet<string>(StringComparer.Ordinal);
			if (before != null) keys.UnionWith(before.Keys);
			if (after != null) keys.UnionWith(after.Keys);
			foreach (string key in keys)
			{
				var b = new Dictionary<string, List<string>>(StringComparer.Ordinal);
				var a = new Dictionary<string, List<string>>(StringComparer.Ordinal);
				if (before != null && before.TryGetValue(key, out List<string> bv)) b[key] = bv;
				if (after != null && after.TryGetValue(key, out List<string> av)) a[key] = av;
				if (!LegacyGamePadKeyStatusMaps.MapsEqual(b, a))
					set.Add(key);
			}
		}

		private static bool HasAnyMarker(Dictionary<string, List<string>> map)
		{
			if (map == null) return false;
			foreach (List<string> tokens in map.Values)
			{
				if (tokens == null) continue;
				for (int i = 0; i < tokens.Count; i++)
					if (AdvancedVanillaOwnershipMarker.IsMarker(tokens[i])) return true;
			}
			return false;
		}
	}
}
