using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Terraria-free mediator for one GamePad / GamePad UI user-edit transaction
	/// (normal commit and Undo). Capture → mutate memory → bindings save → profiles save once;
	/// any failure restores files and memory from the pre-edit snapshot.
	/// </summary>
	public static class GamePadUserEditTransaction
	{
		public sealed class FileBackup
		{
			public bool Existed;
			public byte[] Bytes;
		}

		public sealed class PreEditSnapshot
		{
			public FileBackup Bindings = new();
			public FileBackup Profiles = new();
			public Dictionary<string, ComboBindingData> Combos =
				new(StringComparer.Ordinal);
			/// <summary>Full KeyStatus for <c>InputMode.XBoxGamepad</c>.</summary>
			public Dictionary<string, List<string>> KeyStatusGamepad =
				new(StringComparer.Ordinal);
			/// <summary>Full KeyStatus for <c>InputMode.XBoxGamepadUI</c>.</summary>
			public Dictionary<string, List<string>> KeyStatusGamepadUi =
				new(StringComparer.Ordinal);
			public bool OpenKeybindMenuCustomized;
			public bool HoldTierHudHotkeyCustomized;
		}

		public enum FailStage
		{
			None = 0,
			Capture = 1,
			ApplyMemory = 2,
			BindingsSave = 3,
			ProfilesSave = 4,
			Rollback = 5
		}

		public sealed class Outcome
		{
			public bool Success;
			public FailStage Stage = FailStage.None;
			public string Message;
			public bool RollbackFilesOk = true;
			public bool RollbackMemoryApplied;
			public int ProfilesSaveCalls;
			public bool BindingsBytesMatchAfterRollback = true;
			public bool ProfilesBytesMatchAfterRollback = true;
		}

		/// <summary>Injectable IO — production wires real files; tests inject failures.</summary>
		public sealed class Io
		{
			public Func<string, bool> FileExists;
			public Func<string, byte[]> ReadAllBytes;
			public Func<string, bool> TryDeleteFile;
			/// <summary>
			/// Atomic restore of existing-file bytes (temp write → Replace/Move). Must not
			/// truncate the destination with a direct overwrite.
			/// </summary>
			public Func<string, byte[], bool> TryRestoreBytesAtomic;
			public Func<string, string, bool> TryWriteBindingsAtomic;
			public Func<bool> TrySaveProfiles;
		}

		public static Io CreateDefaultIo()
		{
			return new Io {
				FileExists = System.IO.File.Exists,
				ReadAllBytes = System.IO.File.ReadAllBytes,
				TryDeleteFile = path => {
					try
					{
						if (System.IO.File.Exists(path))
							System.IO.File.Delete(path);
						return true;
					}
					catch
					{
						return false;
					}
				},
				TryRestoreBytesAtomic = LegacyGamePadAtomicFileIo.TryWriteAllBytesAtomic,
				TryWriteBindingsAtomic = LegacyGamePadAtomicFileIo.TryWriteAllTextAtomic,
				TrySaveProfiles = () => false
			};
		}

		/// <summary>
		/// Existing file must be readable. Missing file → Existed=false, Bytes=null (OK).
		/// Read failure → false and backup left unset (never pretend missing).
		/// </summary>
		public static bool TryCaptureFileBackup(string path, Io io, out FileBackup backup, out string error)
		{
			backup = null;
			error = null;
			if (io == null)
			{
				error = "io-null";
				return false;
			}

			bool exists;
			try
			{
				exists = !string.IsNullOrEmpty(path) && io.FileExists(path);
			}
			catch (Exception ex)
			{
				error = "exists-check:" + ex.Message;
				return false;
			}

			if (!exists)
			{
				backup = new FileBackup { Existed = false, Bytes = null };
				return true;
			}

			try
			{
				byte[] bytes = io.ReadAllBytes(path);
				backup = new FileBackup { Existed = true, Bytes = bytes };
				return true;
			}
			catch (Exception ex)
			{
				error = "read:" + ex.Message;
				backup = null;
				return false;
			}
		}

		public static bool TryRestoreFile(string path, FileBackup backup, Io io)
		{
			if (backup == null || io == null || string.IsNullOrEmpty(path))
				return false;

			if (!backup.Existed)
			{
				try
				{
					if (io.FileExists(path))
						return io.TryDeleteFile(path);
					return true;
				}
				catch
				{
					return false;
				}
			}

			if (backup.Bytes == null)
				return false;
			return io.TryRestoreBytesAtomic != null
				&& io.TryRestoreBytesAtomic(path, backup.Bytes);
		}

		public static bool FileMatchesBackup(string path, FileBackup backup, Io io)
		{
			if (backup == null || io == null)
				return false;
			try
			{
				bool exists = io.FileExists(path);
				if (!backup.Existed)
					return !exists;
				if (!exists || backup.Bytes == null)
					return false;
				byte[] now = io.ReadAllBytes(path);
				return BytesEqual(now, backup.Bytes);
			}
			catch
			{
				return false;
			}
		}

		public static bool BytesEqual(byte[] a, byte[] b)
		{
			if (ReferenceEquals(a, b))
				return true;
			if (a == null || b == null || a.Length != b.Length)
				return false;
			for (int i = 0; i < a.Length; i++)
			{
				if (a[i] != b[i])
					return false;
			}
			return true;
		}

		public static string Sha256Hex(byte[] bytes)
		{
			if (bytes == null)
				return string.Empty;
			byte[] hash = SHA256.HashData(bytes);
			return Convert.ToHexString(hash);
		}

		public static Dictionary<string, ComboBindingData> CloneCombos(
			Dictionary<string, ComboBindingData> source)
		{
			var result = new Dictionary<string, ComboBindingData>(StringComparer.Ordinal);
			if (source == null)
				return result;
			foreach (KeyValuePair<string, ComboBindingData> pair in source)
				result[pair.Key] = pair.Value?.Clone();
			return result;
		}

		public static bool CombosEqual(
			Dictionary<string, ComboBindingData> left,
			Dictionary<string, ComboBindingData> right)
		{
			if (ReferenceEquals(left, right))
				return true;
			if (left == null || right == null || left.Count != right.Count)
				return false;
			foreach (KeyValuePair<string, ComboBindingData> pair in left)
			{
				if (!right.TryGetValue(pair.Key, out ComboBindingData other))
					return false;
				if (!ComboBindingData.ContentEquals(pair.Value, other))
					return false;
			}
			return true;
		}

		/// <summary>
		/// After memory is already mutated: write bindings then profiles once.
		/// On failure restore both files from <paramref name="before"/> then invoke
		/// <paramref name="restoreMemory"/>.
		/// </summary>
		public static Outcome PersistAfterMemoryApplied(
			PreEditSnapshot before,
			string bindingsPath,
			string profilesPath,
			Func<string> buildBindingsJson,
			Action restoreMemory,
			Io io)
		{
			var outcome = new Outcome();
			if (before == null || io == null || buildBindingsJson == null)
			{
				outcome.Stage = FailStage.Capture;
				outcome.Message = "null-args";
				return outcome;
			}

			string json;
			try
			{
				json = buildBindingsJson() ?? string.Empty;
			}
			catch (Exception ex)
			{
				outcome.Stage = FailStage.BindingsSave;
				outcome.Message = "build-json:" + ex.Message;
				RestoreAll(before, bindingsPath, profilesPath, restoreMemory, io, outcome);
				return outcome;
			}

			bool bindingsOk;
			try
			{
				bindingsOk = io.TryWriteBindingsAtomic != null
					&& io.TryWriteBindingsAtomic(bindingsPath, json);
			}
			catch (Exception ex)
			{
				bindingsOk = false;
				outcome.Message = "bindings-write:" + ex.Message;
			}

			if (!bindingsOk)
			{
				outcome.Stage = FailStage.BindingsSave;
				RestoreAll(before, bindingsPath, profilesPath, restoreMemory, io, outcome);
				return outcome;
			}

			outcome.ProfilesSaveCalls = 1;
			bool profilesOk;
			try
			{
				profilesOk = io.TrySaveProfiles != null && io.TrySaveProfiles();
			}
			catch (Exception ex)
			{
				profilesOk = false;
				outcome.Message = "profiles-save:" + ex.Message;
			}

			if (!profilesOk)
			{
				outcome.Stage = FailStage.ProfilesSave;
				RestoreAll(before, bindingsPath, profilesPath, restoreMemory, io, outcome);
				return outcome;
			}

			outcome.Success = true;
			outcome.Stage = FailStage.None;
			return outcome;
		}

		/// <summary>
		/// Full transaction: apply memory mutation then <see cref="PersistAfterMemoryApplied"/>.
		/// </summary>
		public static Outcome Run(
			PreEditSnapshot before,
			string bindingsPath,
			string profilesPath,
			Action applyMemory,
			Func<string> buildBindingsJson,
			Action restoreMemory,
			Io io)
		{
			var outcome = new Outcome();
			if (before == null || applyMemory == null)
			{
				outcome.Stage = FailStage.Capture;
				outcome.Message = "null-args";
				return outcome;
			}

			try
			{
				applyMemory();
			}
			catch (Exception ex)
			{
				outcome.Stage = FailStage.ApplyMemory;
				outcome.Message = ex.Message;
				try
				{
					restoreMemory?.Invoke();
					outcome.RollbackMemoryApplied = true;
				}
				catch
				{
					outcome.RollbackMemoryApplied = false;
					outcome.Stage = FailStage.Rollback;
				}

				return outcome;
			}

			return PersistAfterMemoryApplied(
				before, bindingsPath, profilesPath, buildBindingsJson, restoreMemory, io);
		}

		private static void RestoreAll(
			PreEditSnapshot before,
			string bindingsPath,
			string profilesPath,
			Action restoreMemory,
			Io io,
			Outcome outcome)
		{
			bool bindingsRestored = TryRestoreFile(bindingsPath, before.Bindings, io);
			bool profilesRestored = TryRestoreFile(profilesPath, before.Profiles, io);
			outcome.RollbackFilesOk = bindingsRestored && profilesRestored;
			outcome.BindingsBytesMatchAfterRollback =
				FileMatchesBackup(bindingsPath, before.Bindings, io);
			outcome.ProfilesBytesMatchAfterRollback =
				FileMatchesBackup(profilesPath, before.Profiles, io);

			try
			{
				restoreMemory?.Invoke();
				outcome.RollbackMemoryApplied = true;
			}
			catch
			{
				outcome.RollbackMemoryApplied = false;
				outcome.Stage = FailStage.Rollback;
			}

			if (!outcome.RollbackFilesOk || !outcome.RollbackMemoryApplied)
				outcome.Stage = FailStage.Rollback;
		}
	}
}
