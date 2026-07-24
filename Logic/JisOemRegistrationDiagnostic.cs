using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Diagnostic-only: reconstruct SlotCapture newcomer candidates from one WM_KEY* event
	/// the same way post-MS-keystroke Snapshot tends to (Keys + VK: + SC:), then run
	/// CaptureTokenResolver. Does not change capture/gameplay.
	/// </summary>
	public static class JisOemRegistrationDiagnostic
	{
		public sealed class PressRecord
		{
			public string LabelHint;
			public bool ReceivedKeyDown;
			public bool ReceivedKeyUp;
			public uint Vk;
			public ushort Scan;
			public bool Extended;
			public string ResolverTier;
			public string ResolverToken;
			public string ResolverFailReason;
			public string KeysName;
			public string VkToken;
			public string ScToken;
			public List<string> SlotCaptureCandidates = new();
			public List<string> CaptureResolverInput = new();
			public string CaptureResolveKind;
			public string CaptureResolveToken;
			public string CaptureResolveReason;
			public string RejectReason;
			public List<string> OemCollapseFamiliesPresent = new();
			public string CollisionNotes;
		}

		public static PressRecord AnalyzeKeystroke(
			Win32KeystrokeMessage.Parsed downOrUp,
			string labelHint,
			bool isKeyDown)
		{
			var rec = new PressRecord {
				LabelHint = labelHint ?? string.Empty,
				ReceivedKeyDown = isKeyDown,
				ReceivedKeyUp = !isKeyDown,
				Vk = downOrUp.Vk,
				Scan = downOrUp.Scan,
				Extended = downOrUp.Extended
			};

			// Mirror PhysicalKeyResolver Keys-first priority (no FNA).
			string keysName = null;
			if (downOrUp.Vk != 0 && IsDefinedKeysVk(downOrUp.Vk, out keysName))
			{
				rec.KeysName = keysName;
				rec.ResolverTier = "Keys";
				rec.ResolverToken = keysName;
			}
			else if (PhysicalKeyToken.IsValidVirtualKey(downOrUp.Vk))
			{
				rec.VkToken = PhysicalKeyToken.FormatVk(downOrUp.Vk);
				rec.ResolverTier = "VK";
				rec.ResolverToken = rec.VkToken;
			}
			else if (downOrUp.Scan != 0)
			{
				rec.ScToken = PhysicalKeyToken.FormatScan(downOrUp.Scan, downOrUp.Extended);
				rec.ResolverTier = "SC";
				rec.ResolverToken = rec.ScToken;
			}
			else
			{
				rec.ResolverTier = "fail";
				rec.ResolverFailReason = "no-internal-key";
				rec.RejectReason = "PhysicalKeyResolver failed";
				return rec;
			}

			rec.VkToken = PhysicalKeyToken.IsValidVirtualKey(downOrUp.Vk)
				? PhysicalKeyToken.FormatVk(downOrUp.Vk)
				: "-";
			rec.ScToken = downOrUp.Scan != 0
				? PhysicalKeyToken.FormatScan(downOrUp.Scan, downOrUp.Extended)
				: "-";
			if (rec.KeysName == null && IsDefinedKeysVk(downOrUp.Vk, out keysName))
				rec.KeysName = keysName;
			if (rec.KeysName == null)
				rec.KeysName = "-";

			// Snapshot-equivalent newcomers for ONE physical press under current MS-keystroke path:
			// Fallback VK held + promote to Keys name + generic SC held.
			var candidates = new List<string>();
			if (rec.KeysName != "-" && !string.IsNullOrEmpty(rec.KeysName))
				AddUnique(candidates, rec.KeysName);
			if (rec.VkToken != "-")
				AddUnique(candidates, rec.VkToken);
			if (rec.ScToken != "-")
				AddUnique(candidates, rec.ScToken);

			rec.SlotCaptureCandidates = new List<string>(candidates);
			rec.CaptureResolverInput = new List<string>(candidates);

			CaptureTokenResolver.ResolveResult resolved = CaptureTokenResolver.ResolveNewcomers(candidates);
			rec.CaptureResolveKind = resolved.Kind.ToString();
			rec.CaptureResolveToken = resolved.Token ?? "-";
			rec.CaptureResolveReason = resolved.Reason ?? "-";

			if (resolved.Kind == CaptureTokenResolver.ResolveKind.Resolved)
				rec.RejectReason = "(not rejected) accepted=" + resolved.Token + " via=" + resolved.Reason;
			else if (resolved.Kind == CaptureTokenResolver.ResolveKind.Ambiguous)
				rec.RejectReason = "CaptureTokenResolver Ambiguous: " + resolved.Reason;
			else
				rec.RejectReason = "CaptureTokenResolver " + resolved.Kind + ": " + resolved.Reason;

			rec.OemCollapseFamiliesPresent = ListOemCollapseFamilies();
			rec.CollisionNotes = DescribeCollision(candidates, downOrUp);
			return rec;
		}

		public static string BuildReport(IReadOnlyList<PressRecord> records, string headerNote)
		{
			var sb = new StringBuilder();
			sb.AppendLine("AdvancedKeybinds_JisOemRegistrationDiagnostic");
			sb.AppendLine("Generated=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
			sb.AppendLine("Purpose=Investigate 、 。 「 registration failure (NO FIX applied)");
			sb.AppendLine("Note=" + (headerNote ?? string.Empty));
			sb.AppendLine();
			sb.AppendLine("=== CaptureTokenResolver OEM collapse families (code inventory; not a fix) ===");
			foreach (string f in ListOemCollapseFamilies())
				sb.AppendLine("  Family | " + f);
			sb.AppendLine("  MISSING | SC:0x33:Normal + OemComma + VK:0xBC  (、 / US comma)");
			sb.AppendLine("  MISSING | SC:0x34:Normal + OemPeriod + VK:0xBE  (。 / US period)");
			sb.AppendLine("  NOTE    | OemCloseBrackets is collapsed with SC:0x2B (US \\), NOT SC:0x1B (US ])");
			sb.AppendLine("  EXISTS  | SC:0x1A:Normal + OemOpenBrackets/OemTilde + VK:0xDB/0xC0  (JIS @ / US [)");
			sb.AppendLine();

			if (records == null || records.Count == 0)
			{
				sb.AppendLine("=== Measured presses ===");
				sb.AppendLine("(none — no WM_KEY* recorded yet)");
				return sb.ToString();
			}

			int i = 0;
			foreach (PressRecord r in records)
			{
				i++;
				sb.AppendLine("===== Press #" + i + " hint=" + r.LabelHint + " =====");
				sb.AppendLine("WM_KEYDOWN_received=" + r.ReceivedKeyDown);
				sb.AppendLine("WM_KEYUP_received=" + r.ReceivedKeyUp);
				sb.AppendLine("wParam_VK=0x" + r.Vk.ToString("X2", CultureInfo.InvariantCulture));
				sb.AppendLine("lParam_ScanCode=0x" + r.Scan.ToString("X2", CultureInfo.InvariantCulture));
				sb.AppendLine("Extended=" + (r.Extended ? "1" : "0"));
				sb.AppendLine("PhysicalKeyResolver_Tier=" + r.ResolverTier);
				sb.AppendLine("PhysicalKeyResolver_Token=" + r.ResolverToken);
				sb.AppendLine("PhysicalKeyResolver_FailReason=" + (r.ResolverFailReason ?? "-"));
				sb.AppendLine("Generated_KeysName=" + r.KeysName);
				sb.AppendLine("Generated_VK_Token=" + r.VkToken);
				sb.AppendLine("Generated_SC_Token=" + r.ScToken);
				sb.AppendLine("SlotCapture_Candidates=[" + string.Join(", ", r.SlotCaptureCandidates) + "]");
				sb.AppendLine("CaptureTokenResolver_Input=[" + string.Join(", ", r.CaptureResolverInput) + "]");
				sb.AppendLine("CaptureTokenResolver_Kind=" + r.CaptureResolveKind);
				sb.AppendLine("CaptureTokenResolver_Token=" + r.CaptureResolveToken);
				sb.AppendLine("CaptureTokenResolver_Reason=" + r.CaptureResolveReason);
				sb.AppendLine("FinalRejectReason=" + r.RejectReason);
				sb.AppendLine("OemCollapseFamilyExistsForThisPress=" + DescribeFamilyMatch(r));
				sb.AppendLine("CollisionNotes=" + r.CollisionNotes);
				sb.AppendLine();
			}

			sb.AppendLine("=== Hypothesis (from measured + code; no fix applied) ===");
			sb.AppendLine("After MS-keystroke-only acquisition, one physical OEM press produces Keys+VK:+SC:");
			sb.AppendLine("newcomers in the same frame. CaptureTokenResolver collapses only listed OEM families.");
			sb.AppendLine("OemComma / OemPeriod families are not listed → Ambiguous → registration rejected.");
			sb.AppendLine("「 depends on measured SC/VK: if SC:0x1B+OemCloseBrackets, collapse targets wrong SC:0x2B.");
			return sb.ToString();
		}

		public static List<string> ListOemCollapseFamilies()
		{
			return new List<string> {
				"SC:0x35:Normal + OemQuestion + VK:0xBF",
				"SC:0x2B:Normal + OemCloseBrackets + VK:0xDD  (LIKELY WRONG SCAN for ]/「)」",
				"SC:0x1A:Normal + OemOpenBrackets|OemTilde + VK:0xDB|VK:0xC0",
				"SC:0x29:Normal + OemTilde + VK:0xC0",
				"SC:0x73:Normal + OemBackslash + VK:0xE2",
				"SC:0x27:Normal + OemPlus + VK:0xBB  (via TryPreferJisSemicolonColon)",
				"SC:0x28:Normal + OemSemicolon + VK:0xBA  (via TryPreferJisSemicolonColon)",
			};
		}

		private static string DescribeFamilyMatch(PressRecord r)
		{
			bool commaFamily = string.Equals(r.KeysName, "OemComma", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(r.ScToken, "SC:0x33:Normal", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(r.VkToken, "VK:0xBC", StringComparison.OrdinalIgnoreCase);
			bool periodFamily = string.Equals(r.KeysName, "OemPeriod", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(r.ScToken, "SC:0x34:Normal", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(r.VkToken, "VK:0xBE", StringComparison.OrdinalIgnoreCase);
			bool openBr = string.Equals(r.KeysName, "OemOpenBrackets", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(r.ScToken, "SC:0x1A:Normal", StringComparison.OrdinalIgnoreCase);
			bool closeBr = string.Equals(r.KeysName, "OemCloseBrackets", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(r.ScToken, "SC:0x1B:Normal", StringComparison.OrdinalIgnoreCase);

			if (commaFamily)
				return "NO — OemComma/SC:0x33 family not in CaptureTokenResolver collapse list";
			if (periodFamily)
				return "NO — OemPeriod/SC:0x34 family not in CaptureTokenResolver collapse list";
			if (openBr)
				return "YES — SC:0x1A OemOpenBrackets/OemTilde family exists";
			if (closeBr)
				return "PARTIAL/MISMATCH — OemCloseBrackets collapsed with SC:0x2B not SC:0x1B";
			return "see OEM family inventory";
		}

		private static string DescribeCollision(List<string> candidates, Win32KeystrokeMessage.Parsed k)
		{
			if (candidates.Count <= 1)
				return "single-candidate (no multi-token collision)";

			return "same-press multi-token collision among [" + string.Join(", ", candidates) + "]"
				+ " vk=0x" + k.Vk.ToString("X2", CultureInfo.InvariantCulture)
				+ " sc=0x" + k.Scan.ToString("X2", CultureInfo.InvariantCulture)
				+ " E0=" + (k.Extended ? "1" : "0")
				+ "; StableKeyToken.IsOemToken treats VK:/SC: as OEM so prefer-non-oem cannot break the tie";
		}

		private static bool IsDefinedKeysVk(uint vk, out string name)
		{
			name = null;
			// Mirror XNA Keys enum integers without referencing XNA in Core.
			if (!PhysicalKeyIdentity.TryGetVkForKeysName("OemComma", out uint oemComma))
				oemComma = 0xBC;
			_ = oemComma;

			foreach (string candidate in new[] {
				"OemComma", "OemPeriod", "OemOpenBrackets", "OemCloseBrackets", "OemTilde",
				"OemQuestion", "OemPlus", "OemSemicolon", "OemMinus", "OemQuotes", "OemPipe",
				"OemBackslash", "Oem8"
			})
			{
				if (PhysicalKeyIdentity.TryGetVkForKeysName(candidate, out uint mapped) && mapped == vk)
				{
					name = candidate;
					return true;
				}
			}

			// Broader: walk known Keys names via PhysicalKeyIdentity map.
			foreach (string kn in StableKeyToken.AllKnownKeysNames)
			{
				if (PhysicalKeyIdentity.TryGetVkForKeysName(kn, out uint mapped) && mapped == vk)
				{
					name = kn;
					return true;
				}
			}

			return false;
		}

		private static void AddUnique(List<string> list, string value)
		{
			if (string.IsNullOrEmpty(value))
				return;
			foreach (string existing in list)
			{
				if (string.Equals(existing, value, StringComparison.OrdinalIgnoreCase))
					return;
			}

			list.Add(value);
		}
	}
}
