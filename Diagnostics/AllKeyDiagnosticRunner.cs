using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AdvancedKeybinds.Binding;
using AdvancedKeybinds.Core;
using Terraria;

namespace AdvancedKeybinds.Diagnostics
{
	/// <summary>
	/// Game-layer runner for the all-key diagnostic: enumerates targets, uses real UI display
	/// formatting, writes a dedicated UTF-8 report under Main.SavePath. Never touches BindingStorage.
	/// </summary>
	internal static class AllKeyDiagnosticRunner
	{
		public sealed class RunOutcome
		{
			public bool Ok { get; init; }
			public string Message { get; init; }
			public string ReportPath { get; init; }
			public int Total { get; init; }
			public int Pass { get; init; }
			public int Warning { get; init; }
			public int Fail { get; init; }
			public int Skipped { get; init; }
		}

		public static RunOutcome Run()
		{
			DateTime startedUtc = DateTime.UtcNow;
			if (AkDiag.Enabled) AkDiag.Log("AllKeyDiagnostic BEGIN target-enumeration + in-memory round-trip (BindingStorage untouched)");

			List<string> targets;
			try
			{
				targets = AllKeyDiagnosticCore.EnumerateDiagnosticTargets();
			}
			catch (Exception ex)
			{
				string msg = "AllKeyDiagnostic enumeration failed: " + ex.Message;
				if (AkDiag.Enabled) AkDiag.Log(msg);
				return new RunOutcome {
					Ok = false,
					Message = msg,
					ReportPath = string.Empty
				};
			}

			var results = new List<AllKeyDiagnosticResult>(targets.Count);
			foreach (string source in targets)
			{
				AllKeyDiagnosticResult item = AllKeyDiagnosticCore.EvaluateRegistrationAndRoundTrip(source);

				string display = string.Empty;
				Exception displayEx = null;
				if (item.Verdict != AllKeyDiagnosticVerdict.Skipped)
				{
					string tokenForDisplay = !string.IsNullOrEmpty(item.ReloadedInternalValue)
						? item.ReloadedInternalValue
						: (!string.IsNullOrEmpty(item.SourceInternalValue)
							? item.SourceInternalValue
							: source);
					try
					{
						display = KeyToken.FormatDisplayForToken(tokenForDisplay) ?? string.Empty;
					}
					catch (Exception ex)
					{
						displayEx = ex;
						display = string.Empty;
					}
				}

				AllKeyDiagnosticCore.ApplyDisplayInspection(item, display, displayEx);
				results.Add(item);
			}

			AllKeyDiagnosticCore.ApplyCrossKeyAnalysis(results);

			string reportPath;
			try
			{
				reportPath = WriteReport(results, startedUtc);
			}
			catch (Exception ex)
			{
				AllKeyDiagnosticCore.CountVerdicts(results, out int t, out int p, out int w, out int f, out int s);
				string msg = "AllKeyDiagnostic report write failed: " + ex.Message;
				if (AkDiag.Enabled)
				{
					AkDiag.Log(
					"AllKeyDiagnostic END write-failed total=" + t
					+ " PASS=" + p + " WARNING=" + w + " FAIL=" + f + " SKIPPED=" + s
					+ " error=" + ex.Message);
				}
				return new RunOutcome {
					Ok = false,
					Message = msg,
					ReportPath = string.Empty,
					Total = t,
					Pass = p,
					Warning = w,
					Fail = f,
					Skipped = s
				};
			}

			DateTime endedUtc = DateTime.UtcNow;
			AllKeyDiagnosticCore.CountVerdicts(results, out int total, out int pass, out int warning, out int fail, out int skipped);
			if (AkDiag.Enabled)
			{
				AkDiag.Log(
				"AllKeyDiagnostic END path=" + DiagnosticReportSupport.PublicPath(reportPath)
				+ " total=" + total
				+ " PASS=" + pass
				+ " WARNING=" + warning
				+ " FAIL=" + fail
				+ " SKIPPED=" + skipped);
			}

			return new RunOutcome {
				Ok = true,
				Message = "wrote " + reportPath,
				ReportPath = reportPath,
				Total = total,
				Pass = pass,
				Warning = warning,
				Fail = fail,
				Skipped = skipped
			};
		}

		private static string WriteReport(IReadOnlyList<AllKeyDiagnosticResult> results, DateTime startedUtc)
		{
			DateTime endedUtc = DateTime.UtcNow;
			string dir = Path.Combine(Main.SavePath, "AdvancedKeybinds");
			Directory.CreateDirectory(dir);
			string fileName = "AdvancedKeybinds_KeyDiagnostic_"
				+ DateTime.Now.ToString("yyyyMMdd_HHmmss")
				+ ".txt";
			string path = Path.Combine(dir, fileName);
			string body = AllKeyDiagnosticCore.BuildReport(
				results,
				startedUtc,
				endedUtc,
				DiagnosticReportSupport.PublicPath(path));
			File.WriteAllText(path, body, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
			return path;
		}
	}
}
