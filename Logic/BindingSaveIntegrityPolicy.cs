using System;
using System.Collections.Generic;
using System.IO;

namespace AdvancedKeybinds.Core
{
		/// <summary>
		/// Integrity rules for bindings.json Load/Save.
		/// Enforced by BindingStorage.SaveToFile after InvalidJson load failure.
		/// </summary>
		public static class BindingSaveIntegrityPolicy
		{
		public static bool ShouldAllowSaveAfterLoadFailure(
			bool loadSucceeded,
			bool memoryCombosEmpty,
			bool diskFileExistsWithContent)
		{
			if (loadSucceeded)
				return true;
			// Failed load left memory empty while a non-empty disk file remains:
			// writing memory would wipe user bindings (Jump/Space etc.).
			if (memoryCombosEmpty && diskFileExistsWithContent)
				return false;
			return true;
		}

		public static bool ShouldCommitComboPanelClose(bool saveRequested, bool dirty)
			=> saveRequested && dirty;

		public static bool JumpKeyboardMustNotShareStorageKeyWithGamePad(
			string keyboardStorageKey,
			string gamePadStorageKey)
		{
			if (string.IsNullOrEmpty(keyboardStorageKey) || string.IsNullOrEmpty(gamePadStorageKey))
				return false;
			return !string.Equals(keyboardStorageKey, gamePadStorageKey, StringComparison.OrdinalIgnoreCase);
		}

		/// <summary>
		/// Simulates InvalidJson load + a Save attempt that respects <see cref="ShouldAllowSaveAfterLoadFailure"/>.
		/// Returns whether Jump survived on disk.
		/// </summary>
		public static bool SimulateInvalidJsonThenEmptySavePreservesJumpOnDisk(
			string isolatedDir,
			Dictionary<string, ComboBindingData> initialWithJump)
		{
			if (string.IsNullOrEmpty(isolatedDir))
				throw new ArgumentException("isolatedDir required", nameof(isolatedDir));
			if (initialWithJump == null || !initialWithJump.ContainsKey("Jump"))
				throw new ArgumentException("initial must contain Jump", nameof(initialWithJump));

			Directory.CreateDirectory(isolatedDir);
			string path = Path.Combine(isolatedDir, "bindings.json");
			File.WriteAllText(path, BindingJsonCodec.Save(initialWithJump));

			BindingJsonCodec.LoadResult failed = BindingJsonCodec.Load("{ not valid json");
			if (failed.Kind != BindingJsonCodec.LoadKind.InvalidJson)
				throw new InvalidOperationException("expected InvalidJson, got " + failed.Kind);

			Dictionary<string, ComboBindingData> memory = failed.Bindings
				?? new Dictionary<string, ComboBindingData>();

			bool allow = ShouldAllowSaveAfterLoadFailure(
				loadSucceeded: false,
				memoryCombosEmpty: memory.Count == 0,
				diskFileExistsWithContent: true);
			if (allow)
				File.WriteAllText(path, BindingJsonCodec.Save(memory));

			BindingJsonCodec.LoadResult again = BindingJsonCodec.Load(File.ReadAllText(path));
			return again.Kind == BindingJsonCodec.LoadKind.Ok
				&& again.Bindings != null
				&& again.Bindings.ContainsKey("Jump")
				&& again.Bindings["Jump"] != null
				&& !again.Bindings["Jump"].IsEmpty;
		}

		public static bool SimulateCommitSkippedLeavesDiskUnchanged(
			string isolatedDir,
			Dictionary<string, ComboBindingData> diskBefore,
			bool saveRequested,
			bool dirty,
			Dictionary<string, ComboBindingData> memoryWorkingJump)
		{
			Directory.CreateDirectory(isolatedDir);
			string path = Path.Combine(isolatedDir, "bindings.json");
			string beforeJson = BindingJsonCodec.Save(diskBefore);
			File.WriteAllText(path, beforeJson);

			if (ShouldCommitComboPanelClose(saveRequested, dirty))
				File.WriteAllText(path, BindingJsonCodec.Save(memoryWorkingJump));

			string afterJson = File.ReadAllText(path);
			return string.Equals(beforeJson, afterJson, StringComparison.Ordinal);
		}
	}
}
