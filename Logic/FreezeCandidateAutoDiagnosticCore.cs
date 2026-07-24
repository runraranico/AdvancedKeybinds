using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Orchestrates freeze-candidate Windows layout auto-diagnostics and builds report texts.
	/// Does not freeze keys.
	/// </summary>
	public static class FreezeCandidateAutoDiagnosticCore
	{
		public enum FreezeProposal
		{
			FreezeReadyGlobal,
			FreezeReadyByKeyboardFamily,
			FreezeReadyJISOnly,
			FreezeReadyWithSpecialHandler,
			NeedsManualReview,
			DoNotFreezeYet
		}

		public sealed class KeySummary
		{
			public string KeyId;
			public int LayoutsTried;
			public int ForwardOk;
			public int ForwardFail;
			public string StableScan;
			public string StableE0;
			public string StableE1;
			public string LayoutDiff;
			public bool NeedsSpecialHandler;
			public string ApiValidation;
			public string HostHardwareNote;
			public string OtherHardwareStatus;
			public FreezeProposal Proposal;
			public string ProposalReason;
			public string DominantVerdict;
		}

		public sealed class RunResult
		{
			public DateTime StartedLocal;
			public DateTime EndedLocal;
			public List<FreezeCandidateWin32.LayoutInfo> Layouts = new();
			public List<FreezeCandidateWin32.MappingResult> Mappings = new();
			public FreezeCandidateWin32.HostKeyboardInfo Host;
			public List<KeySummary> Summaries = new();
			public string LayoutInventoryText;
			public string AutoMappingText;
			public string SpecialKeysText;
			public string GlobalSummaryText;
			public int LayoutsActivated;
			public int KeysDiagnosed;
			public List<string> KeysNeedingHardware = new();
			public List<string> SpecialHandlerKeys = new();
			public List<string> Warnings = new();
		}

		public static RunResult Run()
		{
			var run = new RunResult {
				StartedLocal = DateTime.Now,
				Host = OperatingSystem.IsWindows()
					? FreezeCandidateWin32.CollectHostKeyboardInfo()
					: new FreezeCandidateWin32.HostKeyboardInfo { Note = "Non-Windows host; Win32 probe skipped." }
			};

			if (!OperatingSystem.IsWindows())
			{
				run.Warnings.Add("OS is not Windows — layout activation and MapVirtualKeyEx skipped.");
				run.EndedLocal = DateTime.Now;
				BuildEmptyReports(run);
				return run;
			}

			run.Layouts = FreezeCandidateWin32.BuildLayoutInventory();
			var mappings = new List<FreezeCandidateWin32.MappingResult>();

			FreezeCandidateWin32.ForEachActivatableLayout(run.Layouts, layout => {
				run.LayoutsActivated++;
				foreach (FreezeCandidateCatalog.Entry key in FreezeCandidateCatalog.All)
					mappings.Add(FreezeCandidateWin32.MapKey(key, layout));
			});

			run.Mappings = mappings;
			run.KeysDiagnosed = FreezeCandidateCatalog.Count;
			run.Summaries = BuildSummaries(run);
			foreach (KeySummary s in run.Summaries)
			{
				if (s.DominantVerdict == "PhysicalHardwareRequired"
					|| s.Proposal == FreezeProposal.DoNotFreezeYet
					|| s.Proposal == FreezeProposal.NeedsManualReview)
				{
					if (s.NeedsSpecialHandler || s.DominantVerdict == "SpecialSequence"
						|| s.DominantVerdict == "PhysicalHardwareRequired")
						run.KeysNeedingHardware.Add(s.KeyId);
				}

				if (s.NeedsSpecialHandler)
					run.SpecialHandlerKeys.Add(s.KeyId);
			}

			run.LayoutInventoryText = BuildLayoutInventoryReport(run);
			run.AutoMappingText = BuildAutoMappingReport(run);
			run.SpecialKeysText = BuildSpecialKeysReport(run);
			run.GlobalSummaryText = BuildGlobalSummaryReport(run);
			run.EndedLocal = DateTime.Now;
			return run;
		}

		private static void BuildEmptyReports(RunResult run)
		{
			run.LayoutInventoryText = "Non-Windows: no layout inventory.\n";
			run.AutoMappingText = "Non-Windows: no mappings.\n";
			run.SpecialKeysText = "Non-Windows: no special-key probe.\n";
			run.GlobalSummaryText = "Non-Windows: no summary.\n";
		}

		private static List<KeySummary> BuildSummaries(RunResult run)
		{
			var list = new List<KeySummary>();
			foreach (FreezeCandidateCatalog.Entry key in FreezeCandidateCatalog.All)
			{
				var rows = run.Mappings.Where(m => m.KeyId == key.Id).ToList();
				var s = new KeySummary {
					KeyId = key.Id,
					LayoutsTried = rows.Count,
					ForwardOk = rows.Count(m => m.ForwardOk),
					ForwardFail = rows.Count(m => !m.ForwardOk),
					HostHardwareNote = "Windows API auto-validation only on this run; Raw Input keypress: NOT COLLECTED",
					OtherHardwareStatus = "他国実機未確認",
					ApiValidation = "Windows API自動検証済み / Windowsレイアウト切替検証済み"
				};

				var okScans = rows.Where(m => m.ForwardOk)
					.Select(m => "0x" + m.Scan.ToString("X2", CultureInfo.InvariantCulture)
						+ "|E0=" + (m.E0 ? "1" : "0")
						+ "|E1hint=" + (m.E1Hint ? "1" : "0"))
					.Distinct(StringComparer.OrdinalIgnoreCase)
					.ToList();

				s.StableScan = okScans.Count == 1
					? okScans[0].Split('|')[0]
					: (okScans.Count == 0 ? "-" : "VARIES(" + okScans.Count + ")");
				s.StableE0 = SummarizeBool(rows.Where(m => m.ForwardOk).Select(m => m.E0));
				s.StableE1 = SummarizeBool(rows.Where(m => m.ForwardOk).Select(m => m.E1Hint));
				s.LayoutDiff = okScans.Count <= 1 ? "none-or-single" : string.Join(" ; ", okScans);

				var verdicts = rows.GroupBy(m => m.AutoVerdict)
					.OrderByDescending(g => g.Count())
					.ToList();
				s.DominantVerdict = verdicts.Count > 0 ? verdicts[0].Key : "UnsupportedOnLayout";

				s.NeedsSpecialHandler = key.IsSpecialSequenceCandidate
					|| string.Equals(s.DominantVerdict, "SpecialSequence", StringComparison.Ordinal);
				AssignProposal(key, s, rows);
				list.Add(s);
			}

			return list;
		}

		private static void AssignProposal(
			FreezeCandidateCatalog.Entry key,
			KeySummary s,
			List<FreezeCandidateWin32.MappingResult> rows)
		{
			if (key.IsJisOnlyCandidate)
			{
				s.Proposal = FreezeProposal.FreezeReadyJISOnly;
				s.ProposalReason = "JIS-only candidate (かな/変換/無変換). Non-JP HKL results must not imply physical key presence.";
				return;
			}

			if (key.IsSpecialSequenceCandidate || s.NeedsSpecialHandler)
			{
				s.Proposal = FreezeProposal.FreezeReadyWithSpecialHandler;
				s.ProposalReason = "PrintScreen/Pause-class special handling required; not a simple make-code freeze.";
				return;
			}

			int ambiguous = rows.Count(m => m.AutoVerdict == "MappingAmbiguous");
			int hardware = rows.Count(m => m.AutoVerdict == "PhysicalHardwareRequired");
			int unsupported = rows.Count(m => m.AutoVerdict == "UnsupportedOnLayout");
			int stable = rows.Count(m => m.AutoVerdict == "AutoStable");
			int layoutDiff = rows.Count(m => m.AutoVerdict == "AutoStableWithLayoutDifference");

			if (hardware > 0 && ambiguous == 0)
			{
				s.Proposal = FreezeProposal.NeedsManualReview;
				s.ProposalReason =
					"PhysicalHardwareRequired: MapVirtualKeyEx did not preserve expected E0/E1; Raw Input keypress needed (他国実機未確認).";
				return;
			}

			if (stable > 0 && ambiguous == 0 && hardware == 0 && unsupported == 0
				&& s.StableScan.StartsWith("0x", StringComparison.Ordinal))
			{
				s.Proposal = FreezeProposal.FreezeReadyGlobal;
				s.ProposalReason =
					"Forward+reverse OK on all activated layouts with stable scan/E0 (API-level only; 他国実機未確認).";
				return;
			}

			if ((stable > 0 || layoutDiff > 0) && ambiguous == 0 && hardware == 0
				&& s.StableScan.StartsWith("0x", StringComparison.Ordinal))
			{
				s.Proposal = FreezeProposal.FreezeReadyByKeyboardFamily;
				s.ProposalReason =
					"Stable where supported; some layouts unsupported. Freeze by keyboard-family if at all.";
				return;
			}

			if (ambiguous > 0)
			{
				s.Proposal = FreezeProposal.NeedsManualReview;
				s.ProposalReason = "MappingAmbiguous on one or more layouts (reverse mismatch).";
				return;
			}

			s.Proposal = FreezeProposal.DoNotFreezeYet;
			s.ProposalReason =
				"Insufficient stable API evidence across activated layouts (layout scan variance or unsupported).";
		}

		private static string SummarizeBool(IEnumerable<bool> values)
		{
			var list = values.ToList();
			if (list.Count == 0)
				return "-";
			bool anyT = list.Any(v => v);
			bool anyF = list.Any(v => !v);
			if (anyT && anyF)
				return "VARIES";
			return anyT ? "1" : "0";
		}

		private static string BuildLayoutInventoryReport(RunResult run)
		{
			var sb = new StringBuilder();
			sb.AppendLine("AdvancedKeybinds_FreezeCandidate_LayoutInventory");
			sb.AppendLine("Generated=" + run.StartedLocal.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
			sb.AppendLine("Scope=Windows API layout inventory + temporary LoadKeyboardLayout attempts");
			sb.AppendLine("LanguagePackInstall=NOT performed");
			sb.AppendLine("RegistryPermanentChange=NOT performed");
			sb.AppendLine();
			sb.AppendLine("=== Host keyboard (attached device; NOT changed by HKL switch) ===");
			if (run.Host != null)
			{
				sb.AppendLine("KeyboardType=" + run.Host.KeyboardType + " (" + run.Host.TypeLabel + ")");
				sb.AppendLine("KeyboardSubtype=" + run.Host.KeyboardSubtype);
				sb.AppendLine("KeyboardMode=" + run.Host.KeyboardMode);
				sb.AppendLine("Note=" + run.Host.Note);
				sb.AppendLine("ActiveHKL(at-start)=" + FreezeCandidateWin32.GetActiveHkl().ToString("X"));
				sb.AppendLine("ActiveKLID(at-start)=" + FreezeCandidateWin32.GetActiveKlid());
				foreach (string line in run.Host.DeviceLines)
					sb.AppendLine("Device | " + line);
			}

			sb.AppendLine();
			sb.AppendLine("=== Layout inventory ===");
			sb.AppendLine("Count=" + run.Layouts.Count);
			sb.AppendLine("ActivatedSuccessfully=" + run.Layouts.Count(l => l.SwitchSucceeded));
			foreach (FreezeCandidateWin32.LayoutInfo L in run.Layouts)
			{
				sb.AppendLine(
					"HKL=0x" + L.Hkl.ToInt64().ToString("X", CultureInfo.InvariantCulture)
					+ " KLID=" + (L.Klid ?? "-")
					+ " Lang=0x" + L.LangId.ToString("X4", CultureInfo.InvariantCulture)
					+ " Culture=" + (L.CultureName ?? "-")
					+ " Installed=" + L.IsInstalled
					+ " SwitchOK=" + L.SwitchSucceeded
					+ " FamilyGuess=" + (L.FamilyGuess ?? "-")
					+ " Err=" + (L.SwitchError ?? "-"));
			}

			sb.AppendLine();
			sb.AppendLine("EvidenceTags=Windows API自動検証済み; Windowsレイアウト切替検証済み(成功分); 現在の実機Raw Input確認済み=デバイス列挙のみ; 他国実機未確認");
			return sb.ToString();
		}

		private static string BuildAutoMappingReport(RunResult run)
		{
			var sb = new StringBuilder();
			sb.AppendLine("AdvancedKeybinds_FreezeCandidate_AutoMapping");
			sb.AppendLine("Generated=" + run.StartedLocal.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
			sb.AppendLine("Rows=" + run.Mappings.Count);
			sb.AppendLine("Columns=Key|HKL|Language|VK|VK_TO_VSC|VK_TO_VSC_EX|ScanCode|E0|E1hint|VSC_TO_VK|VSC_TO_VK_EX|ReverseMatch|Verdict|Notes|CharHint");
			sb.AppendLine();
			foreach (FreezeCandidateWin32.MappingResult m in run.Mappings)
			{
				sb.Append(m.KeyId).Append('|')
					.Append("0x").Append(m.Hkl.ToInt64().ToString("X", CultureInfo.InvariantCulture)).Append('|')
					.Append(m.CultureName).Append('|')
					.Append("0x").Append(m.Vk.ToString("X2", CultureInfo.InvariantCulture)).Append('|')
					.Append("0x").Append(m.VkToVsc.ToString("X", CultureInfo.InvariantCulture)).Append('|')
					.Append("0x").Append(m.VkToVscEx.ToString("X", CultureInfo.InvariantCulture)).Append('|')
					.Append(m.ForwardOk ? "0x" + m.Scan.ToString("X2", CultureInfo.InvariantCulture) : "0").Append('|')
					.Append(m.ForwardOk ? (m.E0 ? "1" : "0") : "-").Append('|')
					.Append(m.ForwardOk ? (m.E1Hint ? "1" : "0") : "-").Append('|')
					.Append("0x").Append(m.VscToVk.ToString("X", CultureInfo.InvariantCulture)).Append('|')
					.Append("0x").Append(m.VscToVkEx.ToString("X", CultureInfo.InvariantCulture)).Append('|')
					.Append(m.ReverseMatches ? "Y" : "N").Append('|')
					.Append(m.AutoVerdict).Append('|')
					.Append(Escape(m.Notes)).Append('|')
					.Append(Escape(m.CharHint))
					.AppendLine();
			}

			return sb.ToString();
		}

		private static string BuildSpecialKeysReport(RunResult run)
		{
			string[] specialIds = {
				"PrintScreen", "Pause", "LeftWindows", "RightWindows", "Apps", "Kana", "Convert", "NonConvert"
			};
			var sb = new StringBuilder();
			sb.AppendLine("AdvancedKeybinds_FreezeCandidate_SpecialKeys");
			sb.AppendLine("Generated=" + run.StartedLocal.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
			sb.AppendLine();
			foreach (string id in specialIds)
			{
				sb.AppendLine("===== " + id + " =====");
				var entry = FreezeCandidateCatalog.All.FirstOrDefault(e => e.Id == id);
				if (entry != null)
				{
					sb.AppendLine("VK=0x" + entry.Vk.ToString("X2", CultureInfo.InvariantCulture));
					sb.AppendLine("Family=" + entry.Family);
					sb.AppendLine("JisOnlyCandidate=" + entry.IsJisOnlyCandidate);
					sb.AppendLine("SpecialSequenceCandidate=" + entry.IsSpecialSequenceCandidate);
				}

				foreach (FreezeCandidateWin32.MappingResult m in run.Mappings.Where(x => x.KeyId == id))
				{
					sb.AppendLine(
						"  HKL=" + m.Klid + "/" + m.CultureName
						+ " fwd=" + m.ForwardOk
						+ " sc=" + (m.ForwardOk ? "0x" + m.Scan.ToString("X2", CultureInfo.InvariantCulture) : "0")
						+ " E0=" + m.E0
						+ " E1hint=" + m.E1Hint
						+ " revOK=" + m.ReverseMatches
						+ " verdict=" + m.AutoVerdict
						+ " notes=" + m.Notes);
				}

				sb.AppendLine();
			}

			sb.AppendLine("InterpretationNotes=");
			sb.AppendLine("- PrintScreen: often E0 make; may involve multi-event sequences on real hardware.");
			sb.AppendLine("- Pause: may require E1 prefix; MapVirtualKeyEx frequently incomplete → SpecialSequence.");
			sb.AppendLine("- L/R Windows / Apps: E0 extended; must not be merged.");
			sb.AppendLine("- かな/変換/無変換: JISOnlyCandidate; non-JP HKL values ≠ physical key existence abroad.");
			sb.AppendLine("- Evidence: Windows API自動検証済み; 他国実機未確認; Raw Input keypress未取得.");
			return sb.ToString();
		}

		private static string BuildGlobalSummaryReport(RunResult run)
		{
			var sb = new StringBuilder();
			sb.AppendLine("AdvancedKeybinds_FreezeCandidate_GlobalSummary");
			sb.AppendLine("Generated=" + run.StartedLocal.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
			sb.AppendLine("CatalogKeys=" + FreezeCandidateCatalog.Count);
			sb.AppendLine("LayoutsInInventory=" + run.Layouts.Count);
			sb.AppendLine("LayoutsActivated=" + run.LayoutsActivated);
			sb.AppendLine("MappingRows=" + run.Mappings.Count);
			sb.AppendLine();
			sb.AppendLine("IMPORTANT: FreezeProposal is diagnostic advice only. NO freeze applied.");
			sb.AppendLine("Do NOT read this as 'verified on all world physical keyboards'.");
			sb.AppendLine("EvidenceTags=Windows API自動検証済み; Windowsレイアウト切替検証済み; 現在の実機Raw Input確認済み=デバイス列挙のみ(キー押下未取得); 他国実機未確認");
			sb.AppendLine();
			foreach (KeySummary s in run.Summaries)
			{
				sb.AppendLine("--- " + s.KeyId + " ---");
				sb.AppendLine("LayoutsTried=" + s.LayoutsTried);
				sb.AppendLine("ForwardOk=" + s.ForwardOk);
				sb.AppendLine("ForwardFail=" + s.ForwardFail);
				sb.AppendLine("StableScanCode=" + s.StableScan);
				sb.AppendLine("E0=" + s.StableE0);
				sb.AppendLine("E1hint=" + s.StableE1);
				sb.AppendLine("LayoutDiff=" + s.LayoutDiff);
				sb.AppendLine("DominantVerdict=" + s.DominantVerdict);
				sb.AppendLine("NeedsSpecialHandler=" + s.NeedsSpecialHandler);
				sb.AppendLine("WindowsApiValidation=" + s.ApiValidation);
				sb.AppendLine("HostHardware=" + s.HostHardwareNote);
				sb.AppendLine("OtherHardware=" + s.OtherHardwareStatus);
				sb.AppendLine("FreezeProposal=" + s.Proposal);
				sb.AppendLine("ProposalReason=" + s.ProposalReason);
				sb.AppendLine();
			}

			return sb.ToString();
		}

		private static string Escape(string value)
		{
			if (string.IsNullOrEmpty(value))
				return string.Empty;
			return value.Replace('\r', ' ').Replace('\n', ' ').Replace('|', '/');
		}
	}
}
