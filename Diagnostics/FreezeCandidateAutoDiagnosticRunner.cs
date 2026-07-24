using System;
using System.Globalization;
using System.IO;
using System.Text;
using AdvancedKeybinds.Core;
using Terraria;

namespace AdvancedKeybinds.Diagnostics
{
	/// <summary>Writes freeze-candidate auto-diagnostic reports (no freeze applied).</summary>
	internal static class FreezeCandidateAutoDiagnosticRunner
	{
		public sealed class Outcome
		{
			public bool Ok;
			public string Message;
			public string LayoutInventoryPath;
			public string AutoMappingPath;
			public string SpecialKeysPath;
			public string GlobalSummaryPath;
			public int Layouts;
			public int LayoutsActivated;
			public int Keys;
		}

		public static Outcome Run()
		{
			var outcome = new Outcome();
			try
			{
				FreezeCandidateAutoDiagnosticCore.RunResult run = FreezeCandidateAutoDiagnosticCore.Run();
				string stamp = run.StartedLocal.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
				string saveDir = Path.Combine(Main.SavePath, "AdvancedKeybinds");
				Directory.CreateDirectory(saveDir);

				string docsDir = TryDocsDiagnosticRunsDir();
				outcome.LayoutInventoryPath = WriteBoth(
					saveDir, docsDir,
					"AdvancedKeybinds_FreezeCandidate_LayoutInventory_" + stamp + ".txt",
					run.LayoutInventoryText);
				outcome.AutoMappingPath = WriteBoth(
					saveDir, docsDir,
					"AdvancedKeybinds_FreezeCandidate_AutoMapping_" + stamp + ".txt",
					run.AutoMappingText);
				outcome.SpecialKeysPath = WriteBoth(
					saveDir, docsDir,
					"AdvancedKeybinds_FreezeCandidate_SpecialKeys_" + stamp + ".txt",
					run.SpecialKeysText);
				outcome.GlobalSummaryPath = WriteBoth(
					saveDir, docsDir,
					"AdvancedKeybinds_FreezeCandidate_GlobalSummary_" + stamp + ".txt",
					run.GlobalSummaryText);

				outcome.Ok = true;
				outcome.Layouts = run.Layouts.Count;
				outcome.LayoutsActivated = run.LayoutsActivated;
				outcome.Keys = run.KeysDiagnosed;
				outcome.Message =
					"Freeze-candidate auto-diagnostic complete (NO freeze applied). layouts="
					+ outcome.Layouts + " activated=" + outcome.LayoutsActivated
					+ " keys=" + outcome.Keys
					+ " summary=" + DiagnosticReportSupport.PublicPath(outcome.GlobalSummaryPath);
				return outcome;
			}
			catch (Exception ex)
			{
				outcome.Ok = false;
				outcome.Message = "Freeze-candidate diagnostic failed: " + ex.GetType().Name + " " + ex.Message;
				return outcome;
			}
		}

		private static string WriteBoth(string saveDir, string docsDir, string fileName, string body)
		{
			string primary = Path.Combine(saveDir, fileName);
			File.WriteAllText(primary, body ?? string.Empty, Encoding.UTF8);
			if (!string.IsNullOrEmpty(docsDir))
			{
				try
				{
					File.WriteAllText(Path.Combine(docsDir, fileName), body ?? string.Empty, Encoding.UTF8);
				}
				catch
				{
					// docs copy is best-effort
				}
			}

			return primary;
		}

		private static string TryDocsDiagnosticRunsDir()
		{
			try
			{
				// Prefer known ModSources tree when present (workshop installs skip docs copy).
				string candidate = Path.Combine(
					Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
					"My Games", "Terraria", "tModLoader", "ModSources", "AdvancedKeybinds");
				if (!Directory.Exists(candidate))
					return null;
				string dir = Path.Combine(candidate, "docs", "diagnostic-runs");
				Directory.CreateDirectory(dir);
				return dir;
			}
			catch
			{
				return null;
			}
		}
	}
}
