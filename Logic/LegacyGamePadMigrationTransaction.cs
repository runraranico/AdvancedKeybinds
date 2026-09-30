using System;
using System.Collections.Generic;
using System.IO;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Shared two-file transaction coordinator for legacy GP migration.
	/// Used by Orchestrator (tests) and BindingStorage adapters — one rollback contract.
	/// </summary>
	public static class LegacyGamePadMigrationTransaction
	{
		public sealed class Snapshot
		{
			public string BindingsPath;
			public string ProfilesPath;
			public string BindingsBackupPath;
			public string ProfilesBackupPath;
			public string BindingsShaBefore;
			public string ProfilesShaBefore;
			public bool BindingsExistedBefore;
			public bool ProfilesExistedBefore;
			public Dictionary<string, ComboBindingData> CombosBefore =
				new(StringComparer.Ordinal);
			public Dictionary<string, List<string>> KeyStatusGamepadBefore =
				new(StringComparer.Ordinal);
			public Dictionary<string, List<string>> KeyStatusGamepadUiBefore =
				new(StringComparer.Ordinal);
		}

		public sealed class RollbackReport
		{
			public bool BindingsRollbackOk = true;
			public bool ProfilesRollbackOk = true;
			public bool MemoryRestored;
			public bool CriticalFailure;
			public readonly List<string> Log = new();
		}

		public static Snapshot Capture(
			LegacyGamePadMigrationOrchestrator.Session session,
			string bindingsPath,
			string profilesPath)
		{
			var snap = new Snapshot {
				BindingsPath = bindingsPath,
				ProfilesPath = profilesPath,
				BindingsExistedBefore = !string.IsNullOrEmpty(bindingsPath) && File.Exists(bindingsPath),
				ProfilesExistedBefore = !string.IsNullOrEmpty(profilesPath) && File.Exists(profilesPath),
				BindingsShaBefore = LegacyGamePadMigrationOrchestrator.Sha256File(bindingsPath),
				ProfilesShaBefore = LegacyGamePadMigrationOrchestrator.Sha256File(profilesPath),
				CombosBefore = CloneCombos(session?.Combos),
				KeyStatusGamepadBefore = CloneMap(session?.KeyStatusGamepad),
				KeyStatusGamepadUiBefore = CloneMap(session?.KeyStatusGamepadUi)
			};
			return snap;
		}

		public static bool TryBackupRequired(
			Snapshot snap,
			string stamp,
			bool backupBindings,
			bool backupProfiles,
			Func<string, string, bool> tryCopy,
			Action<string, string, string, string> log)
		{
			tryCopy ??= LegacyGamePadAtomicFileIo.TryCopyFileNoOverwrite;
			bool ok = true;
			if (backupBindings && snap.BindingsExistedBefore)
			{
				string dest = snap.BindingsPath + ".bak-" + stamp;
				try
				{
					if (!tryCopy(snap.BindingsPath, dest))
					{
						ok = false;
						log?.Invoke("BACKUP_FAIL", snap.BindingsPath, dest, "copy returned false");
					}
					else
					{
						snap.BindingsBackupPath = dest;
						log?.Invoke("BACKUP", snap.BindingsPath, dest, "legacy-gp-migrate");
					}
				}
				catch (Exception ex)
				{
					ok = false;
					log?.Invoke("BACKUP_FAIL", snap.BindingsPath, null, ex.GetType().Name);
				}
			}

			if (backupProfiles && snap.ProfilesExistedBefore)
			{
				string dest = snap.ProfilesPath + ".bak-" + stamp;
				try
				{
					if (!tryCopy(snap.ProfilesPath, dest))
					{
						ok = false;
						log?.Invoke("BACKUP_FAIL", snap.ProfilesPath, dest, "copy returned false");
					}
					else
					{
						snap.ProfilesBackupPath = dest;
						log?.Invoke("BACKUP", snap.ProfilesPath, dest, "legacy-gp-migrate");
					}
				}
				catch (Exception ex)
				{
					ok = false;
					log?.Invoke("BACKUP_FAIL", snap.ProfilesPath, null, ex.GetType().Name);
				}
			}

			return ok;
		}

		/// <summary>
		/// Restore Combos + KeyStatus memory and both disk files from pre-migration backups.
		/// Any restore failure sets <see cref="RollbackReport.CriticalFailure"/>.
		/// </summary>
		public static RollbackReport RollbackAll(
			Snapshot snap,
			LegacyGamePadMigrationOrchestrator.Session session,
			Func<string, string, bool> tryRestoreFile = null)
		{
			tryRestoreFile ??= TryRestoreFileOverwrite;
			var report = new RollbackReport();
			if (snap == null)
			{
				report.CriticalFailure = true;
				report.Log.Add(Line("ROLLBACK_FAIL", "-", "-", "null snapshot"));
				return report;
			}

			if (session != null)
			{
				session.Combos = CloneCombos(snap.CombosBefore);
				session.KeyStatusGamepad = CloneMap(snap.KeyStatusGamepadBefore);
				session.KeyStatusGamepadUi = CloneMap(snap.KeyStatusGamepadUiBefore);
				report.MemoryRestored = true;
			}

			report.BindingsRollbackOk = RollbackOneFile(
				snap.BindingsPath,
				snap.BindingsBackupPath,
				snap.BindingsExistedBefore,
				snap.BindingsShaBefore,
				tryRestoreFile,
				report.Log,
				"bindings");
			report.ProfilesRollbackOk = RollbackOneFile(
				snap.ProfilesPath,
				snap.ProfilesBackupPath,
				snap.ProfilesExistedBefore,
				snap.ProfilesShaBefore,
				tryRestoreFile,
				report.Log,
				"profiles");

			if (!report.BindingsRollbackOk || !report.ProfilesRollbackOk || !report.MemoryRestored)
				report.CriticalFailure = true;

			return report;
		}

		public static bool FilesMatchSnapshot(Snapshot snap)
		{
			if (snap == null)
				return false;
			string b = LegacyGamePadMigrationOrchestrator.Sha256File(snap.BindingsPath);
			string p = LegacyGamePadMigrationOrchestrator.Sha256File(snap.ProfilesPath);
			return string.Equals(b, snap.BindingsShaBefore, StringComparison.Ordinal)
				&& string.Equals(p, snap.ProfilesShaBefore, StringComparison.Ordinal);
		}

		public static bool TryRestoreFileOverwrite(string backupPath, string destinationPath)
		{
			if (string.IsNullOrEmpty(backupPath) || !File.Exists(backupPath))
				return false;
			if (string.IsNullOrEmpty(destinationPath))
				return false;
			try
			{
				string dir = Path.GetDirectoryName(destinationPath);
				if (!string.IsNullOrEmpty(dir))
					Directory.CreateDirectory(dir);
				string temp = destinationPath + ".rollback-tmp-" + Guid.NewGuid().ToString("N");
				File.Copy(backupPath, temp, overwrite: true);
				if (File.Exists(destinationPath))
				{
					string replaceBak = destinationPath + ".rollback-old-" + Guid.NewGuid().ToString("N");
					File.Replace(temp, destinationPath, replaceBak, ignoreMetadataErrors: true);
					try { File.Delete(replaceBak); } catch { /* ignore */ }
				}
				else
					File.Move(temp, destinationPath);
				return true;
			}
			catch
			{
				return false;
			}
		}

		private static bool RollbackOneFile(
			string path,
			string backupPath,
			bool existedBefore,
			string shaBefore,
			Func<string, string, bool> tryRestoreFile,
			List<string> log,
			string label)
		{
			if (string.IsNullOrEmpty(path))
				return true;

			if (!existedBefore)
			{
				try
				{
					if (File.Exists(path))
						File.Delete(path);
					log.Add(Line("ROLLBACK", path, "-", label + " deleted (did not exist before)"));
					return !File.Exists(path);
				}
				catch (Exception ex)
				{
					log.Add(Line("ROLLBACK_FAIL", path, null, label + " " + ex.GetType().Name));
					return false;
				}
			}

			if (string.IsNullOrEmpty(backupPath) || !File.Exists(backupPath))
			{
				log.Add(Line("ROLLBACK_FAIL", path, backupPath, label + " missing backup"));
				return false;
			}

			try
			{
				if (!tryRestoreFile(backupPath, path))
				{
					log.Add(Line("ROLLBACK_FAIL", path, backupPath, label + " restore returned false"));
					return false;
				}

				string after = LegacyGamePadMigrationOrchestrator.Sha256File(path);
				if (!string.Equals(after, shaBefore, StringComparison.Ordinal))
				{
					log.Add(Line("ROLLBACK_FAIL", path, backupPath, label + " sha mismatch after restore"));
					return false;
				}

				log.Add(Line("ROLLBACK", path, backupPath, label + " restored"));
				return true;
			}
			catch (Exception ex)
			{
				log.Add(Line("ROLLBACK_FAIL", path, backupPath, label + " " + ex.GetType().Name));
				return false;
			}
		}

		private static string Line(string op, string from, string to, string reason)
			=> "[LegacyGpMigrate] " + op
				+ " from=" + (from ?? "-")
				+ " to=" + (to ?? "-")
				+ " reason=" + (reason ?? "-");

		private static Dictionary<string, ComboBindingData> CloneCombos(
			Dictionary<string, ComboBindingData> src)
		{
			var dst = new Dictionary<string, ComboBindingData>(StringComparer.Ordinal);
			if (src == null)
				return dst;
			foreach (KeyValuePair<string, ComboBindingData> pair in src)
			{
				ComboBindingData s = pair.Value;
				if (s == null)
				{
					dst[pair.Key] = null;
					continue;
				}

				dst[pair.Key] = new ComboBindingData {
					Slot1 = s.Slot1,
					Slot2 = s.Slot2,
					Slot3 = s.Slot3,
					Slot1Display = s.Slot1Display,
					Slot2Display = s.Slot2Display,
					Slot3Display = s.Slot3Display,
					AlternateGamePadToken = s.AlternateGamePadToken,
					Hold = s.Hold,
					HoldOnRelease = s.HoldOnRelease,
					HoldMs = s.HoldMs,
					PersistAsEmptyRecord = s.PersistAsEmptyRecord,
					UseTerrariaKeyStatusOr = s.UseTerrariaKeyStatusOr,
					DoubleTap = s.DoubleTap,
					DoubleTapMs = s.DoubleTapMs
				};
			}

			return dst;
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
	}
}
