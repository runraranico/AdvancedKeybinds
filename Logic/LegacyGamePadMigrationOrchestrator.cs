using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// File-backed legacy GP:* migration session (Terraria-free).
	/// Order: classify → required backups → mutate Combos/KeyStatus → save bindings → strip orphans → save profiles.
	/// </summary>
	public static class LegacyGamePadMigrationOrchestrator
	{
		public const string MarkerPrefix = AdvancedVanillaOwnershipMarker.Prefix;
		public const string GamepadSection = "Gamepad";
		public const string GamepadUiSection = "Gamepad UI";

		public enum BackupFailMode
		{
			/// <summary>Mirrors BindingStorage before abort fix: BACKUP_FAIL logged, migration continues.</summary>
			ContinueLikeCurrentBindingStorage = 0,
			/// <summary>Required contract: abort with no Combos/KeyStatus/file mutation.</summary>
			AbortRequired = 1
		}

		public enum RunPhase
		{
			Full = 0,
			OrphanStripOnly = 1
		}

		public sealed class Session
		{
			public Dictionary<string, ComboBindingData> Combos =
				new(StringComparer.Ordinal);

			/// <summary>trigger → KeyStatus tokens for Gamepad (ModeGamePad).</summary>
			public Dictionary<string, List<string>> KeyStatusGamepad =
				new(StringComparer.Ordinal);

			/// <summary>trigger → KeyStatus tokens for Gamepad UI.</summary>
			public Dictionary<string, List<string>> KeyStatusGamepadUi =
				new(StringComparer.Ordinal);

			/// <summary>Full input profiles.json document, preserved across Gamepad map edits.</summary>
			public JObject ProfilesRoot;
			public bool ProfileReady = true;
			public bool OrphanPendingRequested;
			public readonly List<string> Log = new();
		}

		public sealed class Result
		{
			public bool Aborted;
			public bool CombosMutated;
			public bool KeyStatusMutated;
			public bool BindingsSaveAttempted;
			public bool BindingsSaveSucceeded;
			public bool ProfilesSaveAttempted;
			public bool ProfilesSaveSucceeded;
			public int MigrateOps;
			public int DeleteOps;
			public int StripOps;
			public bool OrphanDeferred;
			public bool OrphanPending;
			public bool OrphanStripCompleted;
			/// <summary>True when a required disk/memory rollback failed — never treat as safe success.</summary>
			public bool CriticalRollbackFailure;
			public List<string> Log = new();
		}

		public static string Sha256File(string path)
		{
			if (string.IsNullOrEmpty(path) || !File.Exists(path))
				return null;
			using FileStream fs = File.OpenRead(path);
			using SHA256 sha = SHA256.Create();
			byte[] hash = sha.ComputeHash(fs);
			var sb = new StringBuilder(hash.Length * 2);
			for (int i = 0; i < hash.Length; i++)
				sb.Append(hash[i].ToString("x2"));
			return sb.ToString();
		}

		public static string Sha256NormalizedCombos(Dictionary<string, ComboBindingData> combos)
		{
			string json = BindingJsonCodec.Save(combos ?? new Dictionary<string, ComboBindingData>());
			using SHA256 sha = SHA256.Create();
			byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(json));
			var sb = new StringBuilder(hash.Length * 2);
			for (int i = 0; i < hash.Length; i++)
				sb.Append(hash[i].ToString("x2"));
			return sb.ToString();
		}

		public static Session LoadSessionFromFiles(
			string bindingsPath,
			string profilesPath,
			bool profileReady)
		{
			var session = new Session { ProfileReady = profileReady };
			if (!string.IsNullOrEmpty(bindingsPath) && File.Exists(bindingsPath))
			{
				string json = File.ReadAllText(bindingsPath);
				BindingJsonCodec.LoadResult loaded = BindingJsonCodec.Load(json);
				session.Combos = loaded.Bindings ?? new Dictionary<string, ComboBindingData>(StringComparer.Ordinal);
			}

			if (!string.IsNullOrEmpty(profilesPath) && File.Exists(profilesPath))
			{
				session.ProfilesRoot = LegacyGamePadProfilesDocument.LoadOrEmpty(profilesPath);
				LegacyGamePadProfilesDocument.ReadGamepadMaps(
					session.ProfilesRoot,
					session.KeyStatusGamepad,
					session.KeyStatusGamepadUi);
			}

			return session;
		}

		public static void WriteBindingsFile(string path, Dictionary<string, ComboBindingData> combos)
		{
			Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
			File.WriteAllText(path, BindingJsonCodec.Save(combos), Encoding.UTF8);
		}

		public static void WriteMinimalProfilesFile(
			string path,
			Dictionary<string, List<string>> gamepad,
			Dictionary<string, List<string>> gamepadUi)
		{
			Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
			var root = new JObject {
				["Selected Profile"] = "Custom",
				["Custom"] = new JObject {
					[GamepadSection] = ToSectionObject(gamepad),
					[GamepadUiSection] = ToSectionObject(gamepadUi)
				}
			};
			File.WriteAllText(path, root.ToString(Formatting.Indented), Encoding.UTF8);
		}

		/// <summary>
		/// Fixed contract runner (abort on required backup/save failure).
		/// </summary>
		public static Result Run(
			Session session,
			string bindingsPath,
			string profilesPath,
			string stamp,
			RunPhase phase = RunPhase.Full,
			BackupFailMode failMode = BackupFailMode.AbortRequired,
			Func<string, string, bool> tryCopyFile = null,
			Func<string, Dictionary<string, ComboBindingData>, bool> tryWriteBindings = null,
			Func<string, Session, bool> tryWriteProfiles = null,
			Func<string, Session, bool> tryFinalizeAfterProfiles = null,
			Func<string, string, bool> tryRestoreFile = null)
		{
			tryCopyFile ??= LegacyGamePadAtomicFileIo.TryCopyFileNoOverwrite;
			tryWriteBindings ??= DefaultWriteBindings;
			tryWriteProfiles ??= DefaultWriteProfiles;
			tryFinalizeAfterProfiles ??= ((_, __) => true);
			tryRestoreFile ??= LegacyGamePadMigrationTransaction.TryRestoreFileOverwrite;

			var result = new Result();
			if (session == null)
			{
				result.Aborted = true;
				Log(result, session, "MIGRATION_ABORTED", "-", "-", "null session");
				return result;
			}

			var keys = new List<string>(session.Combos.Count);
			foreach (KeyValuePair<string, ComboBindingData> pair in session.Combos)
				keys.Add(pair.Key);

			List<LegacyGamePadStorageMigrationPolicy.PlanItem> plan =
				BuildDtSafeStoragePlan(
					keys,
					k => session.Combos.ContainsKey(k),
					k => session.Combos.TryGetValue(k, out ComboBindingData data) ? data : null);

			bool combosMutate = phase == RunPhase.Full
				&& LegacyGamePadStorageMigrationPolicy.PlanRequiresCombosMutation(plan);
			int orphanCount = CountOrphansInSession(session);
			bool orphanWork = orphanCount > 0;

			if (!session.ProfileReady)
			{
				bool orphanExpected = session.OrphanPendingRequested || orphanWork || combosMutate;
				if (orphanExpected)
				{
					result.OrphanDeferred = true;
					result.OrphanPending = true;
				}
				orphanWork = false;
			}

			if (!combosMutate && !orphanWork)
			{
				LogSkips(result, session, plan);
				result.Log = new List<string>(session.Log);
				return result;
			}

			LegacyGamePadMigrationTransaction.Snapshot txn =
				LegacyGamePadMigrationTransaction.Capture(session, bindingsPath, profilesPath);

			bool backupOk = LegacyGamePadMigrationTransaction.TryBackupRequired(
				txn,
				stamp,
				backupBindings: phase == RunPhase.Full && combosMutate,
				backupProfiles: orphanWork || (phase == RunPhase.Full && combosMutate && File.Exists(profilesPath)),
				tryCopyFile,
				(op, from, to, reason) => Log(result, session, op, from, to, reason));

			if (!backupOk)
			{
				if (failMode == BackupFailMode.AbortRequired)
				{
					result.Aborted = true;
					Log(result, session, "MIGRATION_ABORTED", "-", "-", "required backup failed");
					result.Log = new List<string>(session.Log);
					return result;
				}
			}

			Dictionary<string, ComboBindingData> working = CloneCombos(session.Combos);
			if (phase == RunPhase.Full)
				ApplyPlan(working, plan, result, session);

			bool bindingsWritten = false;
			if (result.MigrateOps > 0 || result.DeleteOps > 0)
			{
				result.CombosMutated = true;
				result.BindingsSaveAttempted = true;
				bool saved;
				try { saved = tryWriteBindings(bindingsPath, working); }
				catch (Exception ex)
				{
					saved = false;
					Log(result, session, "SAVE_FAIL", bindingsPath, null, "bindings-throw:" + ex.GetType().Name);
				}

				result.BindingsSaveSucceeded = saved;
				if (!saved)
				{
					if (failMode == BackupFailMode.AbortRequired)
					{
						Log(result, session, "SAVE_FAIL", bindingsPath, null, "bindings");
						AbortWithFullRollback(
							result,
							session,
							txn,
							"bindings save failed",
							orphanPending: true,
							tryRestoreFile);
						return result;
					}

					session.Combos = working;
					Log(result, session, "SAVE_FAIL", bindingsPath, null, "bindings-continue");
				}
				else
				{
					session.Combos = working;
					bindingsWritten = true;
				}
			}

			LogSkips(result, session, plan);

			if (session.ProfileReady)
			{
				int stripped = StripOrphans(session, result);
				bool keyStatusChanged = !MapsEqual(session.KeyStatusGamepad, txn.KeyStatusGamepadBefore)
					|| !MapsEqual(session.KeyStatusGamepadUi, txn.KeyStatusGamepadUiBefore);
				if (stripped > 0 || keyStatusChanged)
				{
					result.KeyStatusMutated = true;
					result.ProfilesSaveAttempted = true;
					bool ps;
					try { ps = tryWriteProfiles(profilesPath, session); }
					catch (Exception ex)
					{
						ps = false;
						Log(result, session, "SAVE_FAIL", profilesPath, null, "profiles-throw:" + ex.GetType().Name);
					}

					result.ProfilesSaveSucceeded = ps;
					if (!ps)
					{
						if (failMode == BackupFailMode.AbortRequired)
						{
							Log(result, session, "SAVE_FAIL", profilesPath, null, "profiles");
							AbortWithFullRollback(
								result,
								session,
								txn,
								"profiles save failed",
								orphanPending: true,
								tryRestoreFile);
							return result;
						}

						session.KeyStatusGamepad = CloneMap(txn.KeyStatusGamepadBefore);
						session.KeyStatusGamepadUi = CloneMap(txn.KeyStatusGamepadUiBefore);
						result.KeyStatusMutated = false;
						result.OrphanPending = true;
						Log(result, session, "SAVE_FAIL", profilesPath, null, "profiles");
					}
					else
					{
						bool finalized;
						try { finalized = tryFinalizeAfterProfiles(bindingsPath, session); }
						catch (Exception ex)
						{
							finalized = false;
							Log(result, session, "SAVE_FAIL", bindingsPath, null, "finalize-throw:" + ex.GetType().Name);
						}

						if (!finalized)
						{
							if (failMode == BackupFailMode.AbortRequired)
							{
								Log(result, session, "SAVE_FAIL", bindingsPath, null, "finalize");
								AbortWithFullRollback(
									result,
									session,
									txn,
									"bindings finalize failed after profiles",
									orphanPending: true,
									tryRestoreFile);
								return result;
							}
						}
						else
						{
							result.OrphanPending = CountOrphansInSession(session) > 0;
							result.OrphanStripCompleted = !result.OrphanPending;
						}
					}
				}
				else if (phase == RunPhase.OrphanStripOnly || session.OrphanPendingRequested)
				{
					result.OrphanPending = false;
					result.OrphanStripCompleted = true;
				}
			}

			_ = bindingsWritten;
			result.Log = new List<string>(session.Log);
			return result;
		}

		private static void AbortWithFullRollback(
			Result result,
			Session session,
			LegacyGamePadMigrationTransaction.Snapshot txn,
			string reason,
			bool orphanPending,
			Func<string, string, bool> tryRestoreFile = null)
		{
			LegacyGamePadMigrationTransaction.RollbackReport report =
				LegacyGamePadMigrationTransaction.RollbackAll(txn, session, tryRestoreFile);
			for (int i = 0; i < report.Log.Count; i++)
			{
				session.Log.Add(report.Log[i]);
				result.Log.Add(report.Log[i]);
			}

			result.CriticalRollbackFailure = report.CriticalFailure;
			result.CombosMutated = false;
			result.KeyStatusMutated = false;
			result.BindingsSaveSucceeded = false;
			result.ProfilesSaveSucceeded = false;
			result.Aborted = true;
			result.OrphanPending = orphanPending;
			result.OrphanStripCompleted = false;
			Log(result, session, "MIGRATION_ABORTED", "-", "-", reason);
			if (report.CriticalFailure)
				Log(result, session, "ROLLBACK_FAIL", "-", "-", "critical — unsafe to treat as success");
			result.Log = new List<string>(session.Log);
		}

		/// <summary>Compatibility overload for callers that passed fail mode as the fifth argument.</summary>
		public static Result Run(
			Session session,
			string bindingsPath,
			string profilesPath,
			string stamp,
			BackupFailMode failMode,
			Func<string, string, bool> tryCopyFile = null,
			Func<string, Dictionary<string, ComboBindingData>, bool> tryWriteBindings = null,
			Func<string, Session, bool> tryWriteProfiles = null)
			=> Run(
				session,
				bindingsPath,
				profilesPath,
				stamp,
				RunPhase.Full,
				failMode,
				tryCopyFile,
				tryWriteBindings,
				tryWriteProfiles);

		/// <summary>Alias documenting current BindingStorage backup handling.</summary>
		public static Result RunCurrentBindingStorageMirror(
			Session session,
			string bindingsPath,
			string profilesPath,
			string stamp,
			Func<string, string, bool> tryCopyFile = null,
			Func<string, Dictionary<string, ComboBindingData>, bool> tryWriteBindings = null,
			Func<string, Session, bool> tryWriteProfiles = null)
			=> Run(
				session,
				bindingsPath,
				profilesPath,
				stamp,
				RunPhase.Full,
				BackupFailMode.ContinueLikeCurrentBindingStorage,
				tryCopyFile,
				tryWriteBindings,
				tryWriteProfiles);

		public static Result RunFixed(
			Session session,
			string bindingsPath,
			string profilesPath,
			string stamp,
			Func<string, string, bool> tryCopyFile = null,
			Func<string, Dictionary<string, ComboBindingData>, bool> tryWriteBindings = null,
			Func<string, Session, bool> tryWriteProfiles = null)
			=> Run(
				session,
				bindingsPath,
				profilesPath,
				stamp,
				RunPhase.Full,
				BackupFailMode.AbortRequired,
				tryCopyFile,
				tryWriteBindings,
				tryWriteProfiles);

		public static ComboBindingData Slot(string s1, string s2 = null, string s3 = null)
			=> new() { Slot1 = s1, Slot2 = s2, Slot3 = s3 };

		private static bool DefaultWriteBindings(string path, Dictionary<string, ComboBindingData> combos)
			=> LegacyGamePadAtomicFileIo.TryWriteAllTextAtomic(path, BindingJsonCodec.Save(combos));

		private static bool DefaultWriteProfiles(string path, Session session)
		{
			if (session.ProfilesRoot != null)
			{
				var root = (JObject)session.ProfilesRoot.DeepClone();
				LegacyGamePadProfilesDocument.WriteGamepadMaps(
					root,
					session.KeyStatusGamepad,
					session.KeyStatusGamepadUi);
				if (!LegacyGamePadProfilesDocument.TryWriteAtomic(path, root))
					return false;
				session.ProfilesRoot = root;
				return true;
			}

			var minimal = new JObject {
				["Selected Profile"] = "Custom",
				["Custom"] = new JObject {
					[GamepadSection] = ToSectionObject(session.KeyStatusGamepad),
					[GamepadUiSection] = ToSectionObject(session.KeyStatusGamepadUi)
				}
			};
			if (!LegacyGamePadProfilesDocument.TryWriteAtomic(path, minimal))
				return false;
			session.ProfilesRoot = minimal;
			return true;
		}

		private static void AssertUnchangedDisk(
			string bindingsPath,
			string profilesPath,
			string bindingsShaBefore,
			string profilesShaBefore)
		{
			// Soft check helpers for callers; orchestrator itself does not throw.
			_ = bindingsPath;
			_ = profilesPath;
			_ = bindingsShaBefore;
			_ = profilesShaBefore;
		}

		private static bool TryRequiredBackups(
			Session session,
			Result result,
			string bindingsPath,
			string profilesPath,
			string stamp,
			Func<string, string, bool> tryCopyFile,
			bool backupBindings,
			bool backupProfiles)
		{
			bool ok = true;
			if (backupBindings && !string.IsNullOrEmpty(bindingsPath) && File.Exists(bindingsPath))
			{
				string dest = bindingsPath + ".bak-" + stamp;
				try
				{
					if (!tryCopyFile(bindingsPath, dest))
					{
						ok = false;
						Log(result, session, "BACKUP_FAIL", bindingsPath, dest, "copy returned false");
					}
					else
						Log(result, session, "BACKUP", bindingsPath, dest, "legacy-gp-migrate");
				}
				catch (Exception ex)
				{
					ok = false;
					Log(result, session, "BACKUP_FAIL", bindingsPath, null, ex.GetType().Name);
				}
			}

			if (backupProfiles && !string.IsNullOrEmpty(profilesPath) && File.Exists(profilesPath))
			{
				string dest = profilesPath + ".bak-" + stamp;
				try
				{
					if (!tryCopyFile(profilesPath, dest))
					{
						ok = false;
						Log(result, session, "BACKUP_FAIL", profilesPath, dest, "copy returned false");
					}
					else
						Log(result, session, "BACKUP", profilesPath, dest, "legacy-gp-migrate");
				}
				catch (Exception ex)
				{
					ok = false;
					Log(result, session, "BACKUP_FAIL", profilesPath, null, ex.GetType().Name);
				}
			}

			return ok;
		}

		private static void ApplyPlan(
			Dictionary<string, ComboBindingData> working,
			List<LegacyGamePadStorageMigrationPolicy.PlanItem> plan,
			Result result,
			Session session)
		{
			for (int i = 0; i < plan.Count; i++)
			{
				LegacyGamePadStorageMigrationPolicy.PlanItem item = plan[i];
				if (item.Action == LegacyGamePadStorageMigrationPolicy.PlanAction.DeleteDuplicate)
				{
					if (!working.Remove(item.StorageKey))
						continue;
					ClearMarkersForStorageKey(session, item.StorageKey);
					result.DeleteOps++;
					Log(result, session, "DELETE", item.StorageKey, null, item.Reason);
					continue;
				}

				if (item.Action != LegacyGamePadStorageMigrationPolicy.PlanAction.Migrate)
					continue;
				if (string.IsNullOrEmpty(item.TargetStorageKey))
					continue;
				if (!working.TryGetValue(item.StorageKey, out ComboBindingData legacy) || legacy == null)
					continue;

				if (working.ContainsKey(item.TargetStorageKey))
				{
					working.Remove(item.StorageKey);
					ClearMarkersForStorageKey(session, item.StorageKey);
					result.DeleteOps++;
					Log(result, session, "DELETE", item.StorageKey, item.TargetStorageKey, item.Reason + " (target existed)");
					continue;
				}

				working.Remove(item.StorageKey);
				working[item.TargetStorageKey] = CloneBinding(legacy);
				ClearMarkersForStorageKey(session, item.StorageKey);
				EnsureOwnedMarkers(session, item.TargetStorageKey, working[item.TargetStorageKey]);
				result.MigrateOps++;
				Log(result, session, "MIGRATE", item.StorageKey, item.TargetStorageKey, item.Reason);
			}
		}

		private static void ClearMarkersForStorageKey(Session session, string storageKey)
		{
			string trigger = ControlsGameUiSeparationPolicy.ToTriggerActionId(storageKey);
			if (string.IsNullOrEmpty(trigger))
				return;
			session.KeyStatusGamepad.Remove(trigger);
			// Legacy vanilla did not own UI; leave UI unless stripping orphans later.
		}

		private static void EnsureOwnedMarkers(Session session, string storageKey, ComboBindingData binding)
		{
			string trigger = ControlsGameUiSeparationPolicy.ToTriggerActionId(storageKey);
			if (string.IsNullOrEmpty(trigger) || binding == null)
				return;
			List<string> markers = AdvancedVanillaOwnershipMarker.BuildMarkers(binding.Keys);
			if (ControlsGameUiSeparationPolicy.IsGamePadUiStorageKey(storageKey))
				session.KeyStatusGamepadUi[trigger] = markers;
			else
				session.KeyStatusGamepad[trigger] = markers;
		}

		public static int CountOrphansInSession(Session session)
		{
			if (session == null)
				return 0;
			int n = 0;
			foreach (KeyValuePair<string, List<string>> pair in session.KeyStatusGamepad)
			{
				if (!HasAdvancedMarker(pair.Value))
					continue;
				if (LegacyGamePadStorageMigrationPolicy.IsOrphanGamePadOwnershipTrigger(
					    pair.Key, ControlsGameUiSeparationPolicy.ModeGamePad, k => session.Combos.ContainsKey(k)))
					n++;
			}

			foreach (KeyValuePair<string, List<string>> pair in session.KeyStatusGamepadUi)
			{
				if (!HasAdvancedMarker(pair.Value))
					continue;
				if (LegacyGamePadStorageMigrationPolicy.IsOrphanGamePadOwnershipTrigger(
					    pair.Key, ControlsGameUiSeparationPolicy.ModeGamePadUi, k => session.Combos.ContainsKey(k)))
					n++;
			}

			return n;
		}

		private static int StripOrphans(Session session, Result result)
		{
			int stripped = 0;
			stripped += StripMode(
				session,
				result,
				session.KeyStatusGamepad,
				ControlsGameUiSeparationPolicy.ModeGamePad);
			stripped += StripMode(
				session,
				result,
				session.KeyStatusGamepadUi,
				ControlsGameUiSeparationPolicy.ModeGamePadUi);
			result.StripOps = stripped;
			return stripped;
		}

		private static int StripMode(
			Session session,
			Result result,
			Dictionary<string, List<string>> map,
			int modeOrdinal)
		{
			var triggers = new List<string>(map.Keys);
			int stripped = 0;
			for (int i = 0; i < triggers.Count; i++)
			{
				string trigger = triggers[i];
				if (!map.TryGetValue(trigger, out List<string> tokens) || !HasAdvancedMarker(tokens))
					continue;
				if (!LegacyGamePadStorageMigrationPolicy.IsOrphanGamePadOwnershipTrigger(
					    trigger, modeOrdinal, k => session.Combos.ContainsKey(k)))
					continue;

				List<string> cleaned = new();
				for (int t = 0; t < tokens.Count; t++)
				{
					if (!AdvancedVanillaOwnershipMarker.IsMarker(tokens[t]))
						cleaned.Add(tokens[t]);
				}

				if (cleaned.Count == 0)
					map.Remove(trigger);
				else
					map[trigger] = cleaned;

				stripped++;
				Log(result, session, "STRIP_AKADV", trigger, modeOrdinal.ToString(), "orphan GamePad ownership markers");
			}

			return stripped;
		}

		private static bool HasAdvancedMarker(List<string> tokens)
		{
			if (tokens == null)
				return false;
			for (int i = 0; i < tokens.Count; i++)
			{
				if (AdvancedVanillaOwnershipMarker.IsMarker(tokens[i]))
					return true;
			}

			return false;
		}

		private static void LogSkips(
			Result result,
			Session session,
			List<LegacyGamePadStorageMigrationPolicy.PlanItem> plan)
		{
			for (int i = 0; i < plan.Count; i++)
			{
				if (plan[i].Action == LegacyGamePadStorageMigrationPolicy.PlanAction.SkipUnclassified)
					Log(result, session, "SKIP", plan[i].StorageKey, plan[i].TargetStorageKey, plan[i].Reason);
			}
		}

		private static void Log(Result result, Session session, string op, string from, string to, string reason)
		{
			string line = "[LegacyGpMigrate] " + op
				+ " from=" + (from ?? "-")
				+ " to=" + (to ?? "-")
				+ " reason=" + (reason ?? "-");
			session?.Log.Add(line);
			result?.Log.Add(line);
		}

		private static Dictionary<string, ComboBindingData> CloneCombos(Dictionary<string, ComboBindingData> src)
		{
			var dst = new Dictionary<string, ComboBindingData>(StringComparer.Ordinal);
			if (src == null)
				return dst;
			foreach (KeyValuePair<string, ComboBindingData> pair in src)
				dst[pair.Key] = CloneBinding(pair.Value);
			return dst;
		}

		private static ComboBindingData CloneBinding(ComboBindingData src)
		{
			if (src == null)
				return null;
			return new ComboBindingData {
				Slot1 = src.Slot1,
				Slot2 = src.Slot2,
				Slot3 = src.Slot3,
				Slot1Display = src.Slot1Display,
				Slot2Display = src.Slot2Display,
				Slot3Display = src.Slot3Display,
				AlternateGamePadToken = src.AlternateGamePadToken,
				Hold = src.Hold,
				HoldOnRelease = src.HoldOnRelease,
				HoldMs = src.HoldMs,
				PersistAsEmptyRecord = src.PersistAsEmptyRecord,
				UseTerrariaKeyStatusOr = src.UseTerrariaKeyStatusOr,
				DoubleTap = src.DoubleTap,
				DoubleTapMs = src.DoubleTapMs
			};
		}

		internal static bool IsDoubleTapData(ComboBindingData data)
			=> data != null && data.DoubleTap;

		public static List<LegacyGamePadStorageMigrationPolicy.PlanItem> BuildDtSafeStoragePlan(
			IEnumerable<string> storageKeys,
			Func<string, bool> hasBinding,
			Func<string, ComboBindingData> getData)
		{
			List<LegacyGamePadStorageMigrationPolicy.PlanItem> plan =
				LegacyGamePadStorageMigrationPolicy.BuildPlan(storageKeys, hasBinding);
			if (plan == null || plan.Count == 0 || getData == null)
				return plan;

			var filtered = new List<LegacyGamePadStorageMigrationPolicy.PlanItem>(plan.Count);
			for (int i = 0; i < plan.Count; i++)
			{
				LegacyGamePadStorageMigrationPolicy.PlanItem item = plan[i];
				ComboBindingData source = getData(item.StorageKey);
				if (IsDoubleTapData(source)
					&& (item.Action == LegacyGamePadStorageMigrationPolicy.PlanAction.DeleteDuplicate
						|| item.Action == LegacyGamePadStorageMigrationPolicy.PlanAction.Migrate))
				{
					filtered.Add(new LegacyGamePadStorageMigrationPolicy.PlanItem(
						item.StorageKey,
						item.Kind,
						LegacyGamePadStorageMigrationPolicy.PlanAction.Keep,
						item.TargetStorageKey,
						item.Reason + " (DT source keep)"));
					continue;
				}

				if (item.Action == LegacyGamePadStorageMigrationPolicy.PlanAction.Migrate
					&& !string.IsNullOrEmpty(item.TargetStorageKey)
					&& IsDoubleTapData(getData(item.TargetStorageKey)))
				{
					filtered.Add(new LegacyGamePadStorageMigrationPolicy.PlanItem(
						item.StorageKey,
						item.Kind,
						LegacyGamePadStorageMigrationPolicy.PlanAction.Keep,
						item.TargetStorageKey,
						item.Reason + " (DT target keep)"));
					continue;
				}

				if (item.Action == LegacyGamePadStorageMigrationPolicy.PlanAction.DeleteDuplicate
					&& IsLegacyVanillaDuplicateOfDtModern(item.StorageKey, getData))
				{
					filtered.Add(new LegacyGamePadStorageMigrationPolicy.PlanItem(
						item.StorageKey,
						item.Kind,
						LegacyGamePadStorageMigrationPolicy.PlanAction.Keep,
						item.TargetStorageKey,
						item.Reason + " (DT modern sibling keep legacy)"));
					continue;
				}

				filtered.Add(item);
			}

			return filtered;
		}

		private static bool IsLegacyVanillaDuplicateOfDtModern(
			string legacyStorageKey,
			Func<string, ComboBindingData> getData)
		{
			if (!ControlsGameUiSeparationPolicy.IsGamePadLegacySharedStorageKey(legacyStorageKey))
				return false;
			string trigger = ControlsGameUiSeparationPolicy.ToTriggerActionId(legacyStorageKey);
			string g = ControlsGameUiSeparationPolicy.GamePadGamePrefix + trigger;
			string u = ControlsGameUiSeparationPolicy.GamePadUiPrefix + trigger;
			return IsDoubleTapData(getData(g)) || IsDoubleTapData(getData(u));
		}

		private static (Dictionary<string, List<string>> g, Dictionary<string, List<string>> u) SnapshotKeyStatus(Session s)
			=> (CloneMap(s.KeyStatusGamepad), CloneMap(s.KeyStatusGamepadUi));

		private static void RestoreKeyStatus(
			Session s,
			(Dictionary<string, List<string>> g, Dictionary<string, List<string>> u) snap)
		{
			s.KeyStatusGamepad = snap.g;
			s.KeyStatusGamepadUi = snap.u;
		}

		private static Dictionary<string, List<string>> CloneMap(Dictionary<string, List<string>> src)
		{
			var dst = new Dictionary<string, List<string>>(StringComparer.Ordinal);
			if (src == null)
				return dst;
			foreach (KeyValuePair<string, List<string>> pair in src)
				dst[pair.Key] = pair.Value == null ? null : new List<string>(pair.Value);
			return dst;
		}

		private static bool MapsEqual(
			Dictionary<string, List<string>> left,
			Dictionary<string, List<string>> right)
		{
			if (ReferenceEquals(left, right))
				return true;
			if (left == null || right == null || left.Count != right.Count)
				return false;
			foreach (KeyValuePair<string, List<string>> pair in left)
			{
				if (!right.TryGetValue(pair.Key, out List<string> other))
					return false;
				List<string> value = pair.Value;
				if (ReferenceEquals(value, other))
					continue;
				if (value == null || other == null || value.Count != other.Count)
					return false;
				for (int i = 0; i < value.Count; i++)
				{
					if (!string.Equals(value[i], other[i], StringComparison.Ordinal))
						return false;
				}
			}

			return true;
		}

		private static JObject ToSectionObject(Dictionary<string, List<string>> map)
		{
			var o = new JObject();
			if (map == null)
				return o;
			foreach (KeyValuePair<string, List<string>> pair in map)
			{
				var arr = new JArray();
				if (pair.Value != null)
				{
					for (int i = 0; i < pair.Value.Count; i++)
						arr.Add(pair.Value[i]);
				}

				o[pair.Key] = arr;
			}

			return o;
		}

		private static void ReadProfilesKeyStatus(string path, Session session)
		{
			JObject root = JObject.Parse(File.ReadAllText(path));
			string selected = root.Value<string>("Selected Profile") ?? "Custom";
			JObject profile = root[selected] as JObject ?? root["Custom"] as JObject;
			if (profile == null)
				return;
			ReadSection(profile[GamepadSection] as JObject, session.KeyStatusGamepad);
			ReadSection(profile[GamepadUiSection] as JObject, session.KeyStatusGamepadUi);
		}

		private static void ReadSection(JObject section, Dictionary<string, List<string>> map)
		{
			if (section == null)
				return;
			foreach (KeyValuePair<string, JToken> prop in section)
			{
				var list = new List<string>();
				if (prop.Value is JArray arr)
				{
					for (int i = 0; i < arr.Count; i++)
						list.Add(arr[i]?.ToString());
				}

				map[prop.Key] = list;
			}
		}
	}
}
