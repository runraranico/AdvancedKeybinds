using System;
using System.IO;
using AdvancedKeybinds.Core;
using Terraria;

namespace AdvancedKeybinds.Binding
{
	/// <summary>
	/// One-time, pre-load migration for the vertical wheel naming correction.
	/// No in-memory storage rewrite is needed because both stores load after this completes.
	/// </summary>
	internal static class WheelDirectionMigration
	{
		private const string CompletedValue = "windows-vertical-wheel-v1";

		private static string DirectoryPath => Path.Combine(Main.SavePath, "AdvancedKeybinds");
		private static string BindingsPath => Path.Combine(DirectoryPath, "bindings.json");
		private static string AliasesPath => Path.Combine(DirectoryPath, "key-display-aliases.json");
		private static string CompletedFlagPath => Path.Combine(DirectoryPath, "wheel-direction-windows-v1.done");

		public static void RunBeforeStorageLoad()
		{
			if (File.Exists(CompletedFlagPath))
				return;

			string originalBindings = null;
			string originalAliases = null;
			bool bindingsWritten = false;
			bool aliasesWritten = false;

			try
			{
				bool hasBindings = File.Exists(BindingsPath);
				bool hasAliases = File.Exists(AliasesPath);
				originalBindings = hasBindings ? File.ReadAllText(BindingsPath) : null;
				originalAliases = hasAliases ? File.ReadAllText(AliasesPath) : null;

				string migratedBindings = WheelDirectionPolicy.MigrateBindingsJson(
					originalBindings,
					migrationAlreadyCompleted: false,
					out int bindingSwapCount);
				string migratedAliases = WheelDirectionPolicy.MigrateAliasesJson(
					originalAliases,
					migrationAlreadyCompleted: false,
					out int aliasSwapCount);

				if (bindingSwapCount > 0)
				{
					WriteAtomically(BindingsPath, migratedBindings);
					bindingsWritten = true;
				}

				if (aliasSwapCount > 0)
				{
					WriteAtomically(AliasesPath, migratedAliases);
					aliasesWritten = true;
				}

				Directory.CreateDirectory(DirectoryPath);
				WriteAtomically(CompletedFlagPath, CompletedValue);
			}
			catch (Exception ex)
			{
				// Do not leave a partially migrated pair with no completion flag.
				TryRollback(AliasesPath, originalAliases, aliasesWritten);
				TryRollback(BindingsPath, originalBindings, bindingsWritten);
				TryDelete(CompletedFlagPath);
				AdvancedKeybinds.Instance?.Logger.Warn(
					"Advanced Keybinds vertical wheel migration failed; original settings were retained: "
					+ ex.Message);
			}
		}

		private static void WriteAtomically(string path, string text)
		{
			string directory = Path.GetDirectoryName(path);
			if (!string.IsNullOrEmpty(directory))
				Directory.CreateDirectory(directory);

			string temporaryPath = path + ".tmp";
			File.WriteAllText(temporaryPath, text ?? string.Empty);
			File.Move(temporaryPath, path, overwrite: true);
		}

		private static void TryRollback(string path, string originalText, bool wasWritten)
		{
			if (!wasWritten)
				return;

			try
			{
				WriteAtomically(path, originalText);
			}
			catch
			{
				// Preserve the first failure; retry is still blocked unless the completion flag exists.
			}
		}

		private static void TryDelete(string path)
		{
			try
			{
				if (File.Exists(path))
					File.Delete(path);
			}
			catch
			{
			}
		}
	}
}
