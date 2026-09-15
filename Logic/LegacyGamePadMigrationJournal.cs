using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Newtonsoft.Json;

namespace AdvancedKeybinds.Core
{
	public enum JournalPhase
	{
		Prepared,
		CommitStarted,
		BindingsReplaced,
		ProfilesReplaced,
		Verified,
		Completed,
		RollbackStarted,
		RolledBack,
		CriticalRollbackFailure,
		CriticalRecoveryConflict
	}

	public static class LegacyGamePadMigrationJournal
	{
		public sealed class JournalRecord
		{
			public string DirectoryPath;
			public string TransactionId;
			public JournalPhase Phase;
			public string ProfileName;
			public string BindingsPath;
			public string ProfilesPath;
			public string BindingsBackupSha;
			public string ProfilesBackupSha;
			public string BindingsStagingSha;
			public string ProfilesStagingSha;
			public bool BindingsExisted;
			public bool ProfilesExisted;
			public DateTime CreatedUtc;
			public DateTime UpdatedUtc;
		}

		public sealed class RecoveryResult
		{
			public bool Found;
			public bool Recovered;
			public bool CriticalRecoveryConflict;
			public JournalRecord Journal;
			public Dictionary<string, List<string>> GpSnapshot;
			public Dictionary<string, List<string>> GpUiSnapshot;
		}

		public static string TransactionRoot(string saveRoot)
			=> Path.Combine(saveRoot ?? string.Empty, "AdvancedKeybinds", "migration-txn");

		public static bool TryCreatePrepared(
			string saveRoot,
			string transactionId,
			string profileName,
			string bindingsPath,
			string profilesPath,
			byte[] bindingsBefore,
			byte[] profilesBefore,
			byte[] bindingsStaging,
			byte[] profilesStaging,
			MigrationMutationPlan plan,
			Dictionary<string, List<string>> gpSnapshot,
			Dictionary<string, List<string>> gpUiSnapshot,
			out JournalRecord journal)
		{
			journal = null;
			try
			{
				string id = string.IsNullOrWhiteSpace(transactionId)
					? Guid.NewGuid().ToString("N")
					: transactionId;
				string dir = Path.Combine(TransactionRoot(saveRoot), id);
				if (Directory.Exists(dir))
					return false;
				Directory.CreateDirectory(dir);
				bool bindingsExisted = bindingsBefore != null;
				bool profilesExisted = profilesBefore != null;
				WriteBackup(dir, "bindings.bak", bindingsBefore);
				WriteBackup(dir, "profiles.bak", profilesBefore);
				File.WriteAllBytes(Path.Combine(dir, "bindings.staging"), bindingsStaging ?? Array.Empty<byte>());
				File.WriteAllBytes(Path.Combine(dir, "profiles.staging"), profilesStaging ?? Array.Empty<byte>());
				File.WriteAllText(Path.Combine(dir, "plan.json"), JsonConvert.SerializeObject(plan, Formatting.Indented));
				File.WriteAllText(Path.Combine(dir, "snap-gp.json"), JsonConvert.SerializeObject(gpSnapshot, Formatting.Indented));
				File.WriteAllText(Path.Combine(dir, "snap-gpui.json"), JsonConvert.SerializeObject(gpUiSnapshot, Formatting.Indented));
				journal = new JournalRecord {
					DirectoryPath = dir,
					TransactionId = id,
					Phase = JournalPhase.Prepared,
					ProfileName = profileName,
					BindingsPath = bindingsPath,
					ProfilesPath = profilesPath,
					BindingsBackupSha = Sha(bindingsBefore),
					ProfilesBackupSha = Sha(profilesBefore),
					BindingsStagingSha = Sha(bindingsStaging),
					ProfilesStagingSha = Sha(profilesStaging),
					BindingsExisted = bindingsExisted,
					ProfilesExisted = profilesExisted,
					CreatedUtc = DateTime.UtcNow,
					UpdatedUtc = DateTime.UtcNow
				};
				return WriteJournal(journal);
			}
			catch
			{
				journal = null;
				return false;
			}
		}

		public static bool TrySetPhase(string directory, JournalPhase phase)
		{
			try
			{
				JournalRecord record = Load(directory);
				if (record == null)
					return false;
				record.Phase = phase;
				record.UpdatedUtc = DateTime.UtcNow;
				return WriteJournal(record);
			}
			catch { return false; }
		}

		public static JournalRecord TryLoadIncomplete(string saveRoot)
		{
			try
			{
				string root = TransactionRoot(saveRoot);
				if (!Directory.Exists(root))
					return null;
				JournalRecord newest = null;
				foreach (string dir in Directory.EnumerateDirectories(root))
				{
					JournalRecord current = Load(dir);
					if (current == null || IsComplete(current.Phase))
						continue;
					if (newest == null || current.UpdatedUtc > newest.UpdatedUtc)
						newest = current;
				}
				return newest;
			}
			catch { return null; }
		}

		public static RecoveryResult TryRecover(
			JournalRecord journal,
			string bindingsPath,
			string profilesPath,
			Func<string, string, bool> tryRestore)
		{
			var result = new RecoveryResult { Found = journal != null, Journal = journal };
			if (journal == null)
				return result;
			tryRestore ??= LegacyGamePadMigrationTransaction.TryRestoreFileOverwrite;
			bool bindingsOk = RecoverOne(
				journal, bindingsPath ?? journal.BindingsPath, "bindings",
				journal.BindingsExisted, journal.BindingsBackupSha, journal.BindingsStagingSha, tryRestore);
			bool profilesOk = RecoverOne(
				journal, profilesPath ?? journal.ProfilesPath, "profiles",
				journal.ProfilesExisted, journal.ProfilesBackupSha, journal.ProfilesStagingSha, tryRestore);
			if (!bindingsOk || !profilesOk)
			{
				TrySetPhase(journal.DirectoryPath, JournalPhase.CriticalRecoveryConflict);
				result.CriticalRecoveryConflict = true;
				result.Journal = journal;
				TryLoadSnapshots(journal.DirectoryPath, result);
				return result;
			}
			TryLoadSnapshots(journal.DirectoryPath, result);
			TrySetPhase(journal.DirectoryPath, JournalPhase.RolledBack);
			result.Recovered = true;
			return result;
		}

		private static void TryLoadSnapshots(string dir, RecoveryResult result)
		{
			try
			{
				string gpPath = Path.Combine(dir, "snap-gp.json");
				string gpUiPath = Path.Combine(dir, "snap-gpui.json");
				if (File.Exists(gpPath))
					result.GpSnapshot = JsonConvert.DeserializeObject<Dictionary<string, List<string>>>(
						File.ReadAllText(gpPath));
				if (File.Exists(gpUiPath))
					result.GpUiSnapshot = JsonConvert.DeserializeObject<Dictionary<string, List<string>>>(
						File.ReadAllText(gpUiPath));
			}
			catch
			{
				/* keep null — caller treats missing as conflict when restore required */
			}
		}

		private static bool RecoverOne(
			JournalRecord journal,
			string path,
			string label,
			bool existed,
			string backupSha,
			string stagingSha,
			Func<string, string, bool> restore)
		{
			string currentSha = ShaFile(path);
			if (string.Equals(currentSha, backupSha, StringComparison.Ordinal))
				return true;
			if (!string.Equals(currentSha, stagingSha, StringComparison.Ordinal))
				return false;
			string backup = Path.Combine(journal.DirectoryPath, label + ".bak");
			if (!existed)
			{
				try
				{
					if (File.Exists(path))
						File.Delete(path);
					return !File.Exists(path);
				}
				catch { return false; }
			}
			return File.Exists(backup) && restore(backup, path)
				&& string.Equals(ShaFile(path), backupSha, StringComparison.Ordinal);
		}

		private static void WriteBackup(string dir, string name, byte[] bytes)
		{
			if (bytes == null)
			{
				File.WriteAllText(Path.Combine(dir, name + ".MISSING"), "MISSING");
				return;
			}
			File.WriteAllBytes(Path.Combine(dir, name), bytes);
		}

		private static bool IsComplete(JournalPhase phase)
			=> phase == JournalPhase.Completed || phase == JournalPhase.RolledBack;

		private static JournalRecord Load(string dir)
		{
			string path = Path.Combine(dir, "journal.json");
			if (!File.Exists(path))
				return null;
			JournalRecord record = JsonConvert.DeserializeObject<JournalRecord>(File.ReadAllText(path));
			if (record != null)
				record.DirectoryPath = dir;
			return record;
		}

		private static bool WriteJournal(JournalRecord record)
			=> LegacyGamePadAtomicFileIo.TryWriteAllTextAtomic(
				Path.Combine(record.DirectoryPath, "journal.json"),
				JsonConvert.SerializeObject(record, Formatting.Indented));

		public static string ShaFile(string path)
			=> string.IsNullOrEmpty(path) || !File.Exists(path) ? null : Sha(File.ReadAllBytes(path));

		public static string Sha(byte[] bytes)
		{
			if (bytes == null)
				return null;
			using SHA256 sha = SHA256.Create();
			return Convert.ToHexString(sha.ComputeHash(bytes)).ToLowerInvariant();
		}
	}
}
