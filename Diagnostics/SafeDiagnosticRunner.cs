using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using AdvancedKeybinds.Binding;
using AdvancedKeybinds.Core;
using Microsoft.Xna.Framework.Input;

namespace AdvancedKeybinds.Diagnostics
{
	internal sealed class DiagnosticRunOutcome
	{
		public bool Ok { get; init; }
		public string ReportPath { get; init; }
		public string Message { get; init; }
		public int Total { get; init; }
		public int Pass { get; init; }
		public int Info { get; init; }
		public int ExpectedDifference { get; init; }
		public int Warning { get; init; }
		public int Fail { get; init; }
		public int Skipped { get; init; }
		public int Untested { get; init; }
		public int Unavailable { get; init; }
	}

	internal static class SafeDiagnosticRunner
	{
		private sealed class LayoutKeyResult
		{
			public KeyboardLayoutHelper.InstalledLayoutInfo Layout;
			public ushort Scan;
			public bool Extended;
			public uint Vk;
			public string MonoGameKey;
			public int ToUnicodeResult;
			public string Glyph;
			public bool DeadKey;
			public string DisplayName;
			public string ResolvedTier;
			public string ResolvedToken;
			public string CaptureToken;
			public string SavedToken;
			public string ReloadedToken;
			public string ConversionReason;
			public string SideAndEnterResolution;
			public DiagnosticVerdict Verdict;
			public string Exception;
		}

		public static DiagnosticRunOutcome RunInternal()
		{
			try
			{
				var layouts = KeyboardLayoutHelper.GetInstalledLayouts();
				string path = DiagnosticReportSupport.CreatePath("AdvancedKeybinds_InternalDiagnostic");
				var results = new List<AllKeyDiagnosticResult>();
				foreach (string source in AllKeyDiagnosticCore.EnumerateDiagnosticTargets())
				{
					AllKeyDiagnosticResult item = AllKeyDiagnosticCore.EvaluateRegistrationAndRoundTrip(source);
					if (item.Verdict != AllKeyDiagnosticVerdict.Skipped)
					{
						try
						{
							string token = string.IsNullOrEmpty(item.ReloadedInternalValue)
								? item.SourceInternalValue
								: item.ReloadedInternalValue;
							AllKeyDiagnosticCore.ApplyDisplayInspection(
								item,
								KeyToken.FormatDisplayForToken(token),
								null);
						}
						catch (Exception ex)
						{
							AllKeyDiagnosticCore.ApplyDisplayInspection(item, string.Empty, ex);
						}
					}

					results.Add(item);
				}

				AllKeyDiagnosticCore.ApplyCrossKeyAnalysis(results);
				AllKeyDiagnosticCore.CountVerdicts(
					results,
					out int total,
					out int pass,
					out int warning,
					out int fail,
					out int skipped);

				var sb = new StringBuilder();
				sb.AppendLine("=== A. Internal Codec Diagnostic ===");
				DiagnosticReportSupport.AppendCommonHeader(sb, "Internal Codec Diagnostic", layouts);
				sb.AppendLine("OutputPath=" + DiagnosticReportSupport.PublicPath(path));
				sb.AppendLine("F11PolicyScope=MOD-wide registration policy (ComboSlotRules.TrySetSlot), not diagnostic-only");
				sb.AppendLine("StorageScope=temporary in-memory BindingJsonCodec only; BindingStorage and bindings.json untouched");
				sb.AppendLine();
				sb.Append(AllKeyDiagnosticCore.BuildReport(results, DateTime.UtcNow, DateTime.UtcNow, DiagnosticReportSupport.PublicPath(path)));
				sb.AppendLine();
				sb.AppendLine("=== NORMALIZED SUMMARY ===");
				sb.AppendLine("Total=" + total);
				sb.AppendLine("PASS=" + pass);
				sb.AppendLine("INFO=0");
				sb.AppendLine("EXPECTED_DIFFERENCE=0");
				sb.AppendLine("WARNING=" + warning);
				sb.AppendLine("FAIL=" + fail);
				sb.AppendLine("SKIPPED=" + skipped);
				sb.AppendLine("UNTESTED=0");
				sb.AppendLine("UNAVAILABLE=0");
				AppendInternalFocusLists(sb, results);
				DiagnosticReportSupport.WriteAllText(path, sb.ToString());

				return Outcome(path, total, pass, 0, 0, warning, fail, skipped, 0, 0);
			}
			catch (Exception ex)
			{
				return new DiagnosticRunOutcome {
					Ok = false,
					Message = ex.GetType().Name + ": " + ex.Message
				};
			}
		}

		public static DiagnosticRunOutcome RunInstalledLayouts()
		{
			try
			{
				IReadOnlyList<KeyboardLayoutHelper.InstalledLayoutInfo> layouts =
					KeyboardLayoutHelper.GetInstalledLayouts();
				string path = DiagnosticReportSupport.CreatePath("AdvancedKeybinds_LayoutDiagnostic");
				var results = new List<LayoutKeyResult>();
				var summary = new DiagnosticSummary();

				if (!OperatingSystem.IsWindows())
				{
					summary.Add(DiagnosticVerdict.Unavailable);
				}
				else if (layouts.Count == 0)
				{
					summary.Add(DiagnosticVerdict.Unavailable);
				}
				else
				{
					foreach (KeyboardLayoutHelper.InstalledLayoutInfo layout in layouts)
					{
						for (ushort scan = 1; scan <= 0x7F; scan++)
						{
							EvaluateLayoutPosition(layout, scan, extended: false, results, summary);
							EvaluateLayoutPosition(layout, scan, extended: true, results, summary);
						}
					}

					ApplyLayoutInternalCollisions(results, summary);
					ApplyLayoutDisplayDuplicates(results, summary);
				}

				var sb = new StringBuilder(results.Count * 260 + 4096);
				sb.AppendLine("=== B. Installed Layout Diagnostic ===");
				DiagnosticReportSupport.AppendCommonHeader(sb, "Installed Layout Diagnostic", layouts);
				sb.AppendLine("OutputPath=" + DiagnosticReportSupport.PublicPath(path));
				sb.AppendLine("LayoutSwitchPerformed=False");
				sb.AppendLine("ToUnicodeApi=ToUnicodeEx with explicit HKL and wFlags=0x4 (do-not-change-state where supported)");
				sb.AppendLine("DeadKeySafety=No composition is requested; no input layout or IME state is changed");
				sb.AppendLine("PhysicalKeyboardConclusion=Not asserted from HKL");
				sb.AppendLine("CollisionPolicy=INFO for expected Windows multicode aliases or scan-sweep aliases not proven to be separate physical keys");
				sb.AppendLine("CollisionPolicyWarning=Only known distinct physical positions that collapse to one Capture/Saved/Reloaded token, or distinct tokens with an indistinguishable DisplayName");
				sb.AppendLine("ExecutionEvidence=UNAVAILABLE in layout-only diagnostic; actual duplicate execution requires Live/InputRoute evidence");
				sb.AppendLine("Fn=UNAVAILABLE unless hardware/driver emits another ordinary key event");
				sb.AppendLine("Win32OemAliases=Oem1:VK_OEM_1/OemSemicolon,Oem2:VK_OEM_2/OemQuestion,Oem3:VK_OEM_3/OemTilde,Oem4:VK_OEM_4/OemOpenBrackets,Oem5:VK_OEM_5/OemPipe,Oem6:VK_OEM_6/OemCloseBrackets,Oem7:VK_OEM_7/OemQuotes,Oem8:VK_OEM_8/Oem8,Oem102:VK_OEM_102/OemBackslash");
				sb.AppendLine();

				for (int i = 0; i < layouts.Count; i++)
				{
					KeyboardLayoutHelper.InstalledLayoutInfo layout = layouts[i];
					int count = 0;
					for (int j = 0; j < results.Count; j++)
					{
						if (results[j].Layout.Hkl != layout.Hkl)
							continue;
						count++;
						AppendLayoutResult(sb, results[j]);
					}

					sb.AppendLine(
						"LayoutResultCount HKL=0x"
						+ unchecked((ulong)layout.Hkl.ToInt64()).ToString("X16")
						+ " Count=" + count);
				}

				AppendLayoutDifferences(sb, results, summary);
				AppendLayoutFocusLists(sb, results);
				sb.AppendLine();
				sb.AppendLine("=== SUMMARY ===");
				sb.AppendLine("SummaryNote=Total counts scanned layout positions; EXPECTED_DIFFERENCE counts additional cross-layout comparison findings");
				summary.AppendTo(sb);
				sb.AppendLine("=== WARNING LIST ===");
				AppendByVerdict(sb, results, DiagnosticVerdict.Warning);
				sb.AppendLine("=== FAIL LIST ===");
				AppendByVerdict(sb, results, DiagnosticVerdict.Fail);
				sb.AppendLine("=== SKIPPED LIST ===");
				AppendByVerdict(sb, results, DiagnosticVerdict.Skipped);
				sb.AppendLine("=== UNAVAILABLE LIST ===");
				if (!OperatingSystem.IsWindows())
					sb.AppendLine("UNAVAILABLE | Windows layout APIs | reason=non-Windows");
				else if (layouts.Count == 0)
					sb.AppendLine("UNAVAILABLE | installed layouts | reason=GetKeyboardLayoutList returned none");
				sb.AppendLine("UNAVAILABLE | Fn | reason=commonly not emitted as an independent OS key event");
				DiagnosticReportSupport.WriteAllText(path, sb.ToString());

				return Outcome(
					path,
					summary.Total,
					summary.Get(DiagnosticVerdict.Pass),
					summary.Get(DiagnosticVerdict.Info),
					summary.Get(DiagnosticVerdict.ExpectedDifference),
					summary.Get(DiagnosticVerdict.Warning),
					summary.Get(DiagnosticVerdict.Fail),
					summary.Get(DiagnosticVerdict.Skipped),
					summary.Get(DiagnosticVerdict.Untested),
					summary.Get(DiagnosticVerdict.Unavailable));
			}
			catch (Exception ex)
			{
				return new DiagnosticRunOutcome {
					Ok = false,
					Message = ex.GetType().Name + ": " + ex.Message
				};
			}
		}

		private static void EvaluateLayoutPosition(
			KeyboardLayoutHelper.InstalledLayoutInfo layout,
			ushort scan,
			bool extended,
			List<LayoutKeyResult> results,
			DiagnosticSummary summary)
		{
			bool hasVk = KeyboardLayoutHelper.TryGetVkFromScanEx(scan, extended, layout.Hkl, out uint vk);

			var item = new LayoutKeyResult {
				Layout = layout,
				Scan = scan,
				Extended = extended,
				Vk = vk,
				Verdict = DiagnosticVerdict.Pass,
				ConversionReason = "ok"
			};

			try
			{
				if (!hasVk)
				{
					item.MonoGameKey = "-";
					item.ResolvedTier = "ScanCode";
					item.ResolvedToken = PhysicalKeyToken.FormatScan(scan, extended);
					item.CaptureToken = item.ResolvedToken;
					item.DisplayName = KeyToken.FormatDisplayForToken(item.ResolvedToken) ?? string.Empty;
					item.ToUnicodeResult = 0;
					item.Glyph = string.Empty;
					item.DeadKey = false;
					item.SideAndEnterResolution = "not-applicable";
					item.Verdict = string.IsNullOrWhiteSpace(item.DisplayName)
						? DiagnosticVerdict.Fail
						: DiagnosticVerdict.Info;
					item.ConversionReason = string.IsNullOrWhiteSpace(item.DisplayName)
						? "scan-fallback-display-empty"
						: "no-virtual-key-for-scan-under-layout; scan-fallback-saveable";
					ApplyLayoutRoundTrip(item);
					results.Add(item);
					summary.Add(item.Verdict);
					return;
				}

				if (Enum.IsDefined(typeof(Keys), (int)vk) && (Keys)vk != Keys.None)
				{
					item.MonoGameKey = ((Keys)vk).ToString();
					item.ResolvedTier = "MonoGameEnumCandidate";
					item.ResolvedToken = item.MonoGameKey;
				}
				else
				{
					item.MonoGameKey = "-";
					item.ResolvedTier = PhysicalKeyToken.IsValidVirtualKey(vk) ? "VirtualKey" : "ScanCode";
					item.ResolvedToken = PhysicalKeyToken.IsValidVirtualKey(vk)
						? PhysicalKeyToken.FormatVk(vk)
						: PhysicalKeyToken.FormatScan(scan, extended);
				}

				// This is a transformation candidate, not a claim that FNA observed a physical press.
				// Prefer the Keys identity when the VK has one, matching normal capture's highest tier.
				item.CaptureToken = item.ResolvedToken;
				item.SideAndEnterResolution = DescribeSideAndEnter(vk, scan, extended);

				item.ToUnicodeResult = KeyboardLayoutHelper.QueryToUnicodeEx(vk, scan, layout.Hkl, out string text);
				item.DeadKey = item.ToUnicodeResult < 0;
				item.Glyph = item.ToUnicodeResult > 0 && !string.IsNullOrEmpty(text)
					? text.Substring(0, 1)
					: string.Empty;
				item.DisplayName = KeyToken.FormatDisplayForToken(item.ResolvedToken) ?? string.Empty;

				if (string.IsNullOrWhiteSpace(item.DisplayName))
				{
					item.Verdict = DiagnosticVerdict.Fail;
					item.ConversionReason = "display-empty";
				}
				else if (string.Equals(item.DisplayName.Trim(), "Unknown", StringComparison.OrdinalIgnoreCase))
				{
					item.Verdict = DiagnosticVerdict.Fail;
					item.ConversionReason = "display-unknown";
				}
				else if (item.ToUnicodeResult <= 0)
				{
					item.Verdict = DiagnosticVerdict.Info;
					item.ConversionReason = item.DeadKey
						? "dead-key-no-standalone-glyph"
						: "no-glyph-allowed-for-control-function-media-or-nonprinting-key";
				}

				ApplyLayoutRoundTrip(item);
			}
			catch (Exception ex)
			{
				item.Verdict = DiagnosticVerdict.Fail;
				item.ConversionReason = "conversion-exception";
				item.Exception = ex.GetType().Name + ": " + ex.Message;
			}

			results.Add(item);
			summary.Add(item.Verdict);
		}

		private static void ApplyLayoutInternalCollisions(List<LayoutKeyResult> results, DiagnosticSummary summary)
		{
			var groups = new Dictionary<string, List<LayoutKeyResult>>(StringComparer.OrdinalIgnoreCase);
			foreach (LayoutKeyResult item in results)
			{
				if (item.Verdict is DiagnosticVerdict.Fail or DiagnosticVerdict.Skipped)
					continue;
				string key = item.Layout.LayoutIdentifier + "|" + item.CaptureToken;
				if (!groups.TryGetValue(key, out List<LayoutKeyResult> group))
					groups[key] = group = new List<LayoutKeyResult>();
				group.Add(item);
			}

			foreach (List<LayoutKeyResult> group in groups.Values)
			{
				if (group.Count < 2)
					continue;

				var positions = new HashSet<string>(StringComparer.Ordinal);
				foreach (LayoutKeyResult item in group)
					positions.Add(item.Scan.ToString("X2") + ":" + item.Extended);
				if (positions.Count < 2)
					continue;

				bool persistedIdentityCollides =
					group.Select(x => x.SavedToken)
						.Where(x => !string.IsNullOrEmpty(x))
						.Distinct(StringComparer.OrdinalIgnoreCase)
						.Count() <= 1
					&& group.Select(x => x.ReloadedToken)
						.Where(x => !string.IsNullOrEmpty(x))
						.Distinct(StringComparer.OrdinalIgnoreCase)
						.Count() <= 1;
				bool knownDistinctPhysicalPositions = AreKnownDistinctPhysicalPositions(group);
				bool expectedWindowsAlias = IsExpectedWindowsAlias(group);
				DiagnosticVerdict collisionVerdict =
					persistedIdentityCollides && knownDistinctPhysicalPositions && !expectedWindowsAlias
						? DiagnosticVerdict.Warning
						: DiagnosticVerdict.Info;
				string collisionReason = collisionVerdict == DiagnosticVerdict.Warning
					? "same-layout-distinct-physical-position-persisted-identity-collision"
					: expectedWindowsAlias
						? "expected-windows-multicode-alias-same-logical-key"
						: persistedIdentityCollides
							? "scan-sweep-alias-not-proven-as-distinct-physical-key"
							: "scan-or-extended-distinguishes-and-persisted-identity-remains-distinct";

				foreach (LayoutKeyResult item in group)
				{
					if (item.Verdict == DiagnosticVerdict.Fail)
						continue;
					DiagnosticVerdict previous = item.Verdict;
					item.Verdict = collisionVerdict;
					item.ConversionReason = collisionReason
						+ " positions=[" + string.Join(",", positions) + "]"
						+ " captureToken=" + (item.CaptureToken ?? "(null)")
						+ " savedToken=" + (item.SavedToken ?? "(null)")
						+ " reloadedToken=" + (item.ReloadedToken ?? "(null)")
						+ " displayName=" + (item.DisplayName ?? "(null)")
						+ " executionEvidence=UNAVAILABLE(layout-only; verify with Live/InputRoute)";
					summary.Replace(previous, collisionVerdict);
				}
			}
		}

		/// <summary>
		/// True only for scan/extended pairs that represent known separate physical positions.
		/// Other values in the exhaustive 0x01-0x7F × Normal/E0 sweep may be API aliases for
		/// synthetic/nonexistent combinations and are not promoted to WARNING without live evidence.
		/// </summary>
		private static bool AreKnownDistinctPhysicalPositions(List<LayoutKeyResult> group)
		{
			var positionSet = new HashSet<string>(
				group.Select(x => x.Scan.ToString("X2") + ":" + x.Extended),
				StringComparer.Ordinal);

			// Main Enter / numpad Enter; left/right Ctrl; slash/numpad Divide; left/right Alt;
			// numpad navigation positions / dedicated E0 navigation positions.
			ushort[] normalExtendedPairs = {
				0x1C, 0x1D, 0x35, 0x38,
				0x47, 0x48, 0x49, 0x4B, 0x4D,
				0x4F, 0x50, 0x51, 0x52, 0x53
			};
			foreach (ushort scan in normalExtendedPairs)
			{
				if (positionSet.Contains(scan.ToString("X2") + ":False")
					&& positionSet.Contains(scan.ToString("X2") + ":True"))
					return true;
			}

			// Left/right Shift and Left/right Windows use different make codes.
			if (positionSet.Any(x => x.StartsWith("2A:", StringComparison.Ordinal))
				&& positionSet.Any(x => x.StartsWith("36:", StringComparison.Ordinal)))
				return true;
			if (positionSet.Any(x => x.StartsWith("5B:", StringComparison.Ordinal))
				&& positionSet.Any(x => x.StartsWith("5C:", StringComparison.Ordinal)))
				return true;

			return false;
		}

		/// <summary>
		/// Windows may expose multiple make-code forms for one logical key. These aliases are INFO
		/// unless a later live diagnostic proves duplicate capture/action execution.
		/// </summary>
		private static bool IsExpectedWindowsAlias(List<LayoutKeyResult> group)
		{
			string token = group[0].CaptureToken ?? string.Empty;
			if (string.Equals(token, "PrintScreen", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(token, "Pause", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(token, "Break", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(token, "VK:0x03", StringComparison.OrdinalIgnoreCase))
				return true;

			return false;
		}

		private static void ApplyLayoutDisplayDuplicates(List<LayoutKeyResult> results, DiagnosticSummary summary)
		{
			var groups = new Dictionary<string, List<LayoutKeyResult>>(StringComparer.Ordinal);
			foreach (LayoutKeyResult item in results)
			{
				if (item.Verdict is DiagnosticVerdict.Fail or DiagnosticVerdict.Skipped
					|| string.IsNullOrWhiteSpace(item.DisplayName))
					continue;
				string key = item.Layout.LayoutIdentifier + "|" + item.DisplayName.Trim();
				if (!groups.TryGetValue(key, out List<LayoutKeyResult> group))
					groups[key] = group = new List<LayoutKeyResult>();
				group.Add(item);
			}

			foreach (List<LayoutKeyResult> group in groups.Values)
			{
				if (group.Select(x => x.CaptureToken).Distinct(StringComparer.OrdinalIgnoreCase).Count() < 2)
					continue;
				string tokens = string.Join(",", group.Select(x => x.CaptureToken).Distinct(StringComparer.OrdinalIgnoreCase));
				foreach (LayoutKeyResult item in group)
				{
					if (item.Verdict != DiagnosticVerdict.Warning)
					{
						DiagnosticVerdict previous = item.Verdict;
						item.Verdict = DiagnosticVerdict.Warning;
						summary.Replace(previous, DiagnosticVerdict.Warning);
					}
					item.ConversionReason += " | display-name-duplicate tokens=[" + tokens + "]";
				}
			}
		}

		private static void AppendLayoutResult(StringBuilder sb, LayoutKeyResult r)
		{
			sb.AppendLine("---");
			sb.AppendLine("Layout=" + r.Layout.CultureName);
			sb.AppendLine("HKL=0x" + unchecked((ulong)r.Layout.Hkl.ToInt64()).ToString("X16"));
			sb.AppendLine("LANGID=0x" + r.Layout.LangId.ToString("X4"));
			sb.AppendLine("LayoutIdentifier=" + r.Layout.LayoutIdentifier);
			sb.AppendLine("Active=" + r.Layout.IsActive);
			sb.AppendLine("Installed=" + r.Layout.IsInstalled);
			sb.AppendLine("ScanCode=0x" + r.Scan.ToString("X2"));
			sb.AppendLine("Extended=" + r.Extended);
			sb.AppendLine("VirtualKey=0x" + r.Vk.ToString("X2"));
			sb.AppendLine("MonoGameKeys=" + r.MonoGameKey);
			sb.AppendLine("ToUnicodeExResult=" + r.ToUnicodeResult);
			sb.AppendLine("Glyph=" + DiagnosticReportSupport.EscapeSingleGlyph(r.Glyph));
			sb.AppendLine("DeadKey=" + r.DeadKey);
			sb.AppendLine("DisplayName=" + r.DisplayName);
			sb.AppendLine("ResolvedTier=" + r.ResolvedTier);
			sb.AppendLine("ResolvedToken=" + r.ResolvedToken);
			sb.AppendLine("CaptureToken=" + r.CaptureToken);
			sb.AppendLine("SavedToken=" + r.SavedToken);
			sb.AppendLine("ReloadedToken=" + r.ReloadedToken);
			sb.AppendLine("SideAndEnterResolution=" + r.SideAndEnterResolution);
			sb.AppendLine("Verdict=" + r.Verdict.ToString().ToUpperInvariant());
			sb.AppendLine("Reason=" + r.ConversionReason);
			if (!string.IsNullOrEmpty(r.Exception))
				sb.AppendLine("Exception=" + r.Exception);
		}

		private static void AppendLayoutDifferences(
			StringBuilder sb,
			List<LayoutKeyResult> results,
			DiagnosticSummary summary)
		{
			sb.AppendLine();
			sb.AppendLine("=== LAYOUT DIFFERENCES (differences are not errors) ===");
			var byPosition = new Dictionary<string, List<LayoutKeyResult>>(StringComparer.Ordinal);
			foreach (LayoutKeyResult item in results)
			{
				string key = item.Scan.ToString("X2") + ":" + item.Extended;
				if (!byPosition.TryGetValue(key, out List<LayoutKeyResult> list))
					byPosition[key] = list = new List<LayoutKeyResult>();
				list.Add(item);
			}

			foreach (KeyValuePair<string, List<LayoutKeyResult>> pair in byPosition)
			{
				if (pair.Value.Count < 2 || !HasLayoutDifference(pair.Value))
					continue;

				summary.AddFinding(DiagnosticVerdict.ExpectedDifference);
				sb.AppendLine("EXPECTED_DIFFERENCE ScanCode=0x" + pair.Key.Replace(":False", " Extended=False").Replace(":True", " Extended=True"));
				foreach (LayoutKeyResult item in pair.Value)
				{
					sb.AppendLine(
						"  " + item.Layout.CultureName
						+ " HKL=0x" + unchecked((ulong)item.Layout.Hkl.ToInt64()).ToString("X16")
						+ " VK=0x" + item.Vk.ToString("X2")
						+ " Keys=" + item.MonoGameKey
						+ " Glyph=" + DiagnosticReportSupport.EscapeSingleGlyph(item.Glyph)
						+ " DeadKey=" + item.DeadKey
						+ " DisplayName=" + item.DisplayName
						+ " ResolvedToken=" + item.ResolvedToken);
				}
			}
		}

		private static bool HasLayoutDifference(List<LayoutKeyResult> items)
		{
			LayoutKeyResult first = items[0];
			for (int i = 1; i < items.Count; i++)
			{
				LayoutKeyResult other = items[i];
				if (first.Vk != other.Vk
					|| !string.Equals(first.MonoGameKey, other.MonoGameKey, StringComparison.Ordinal)
					|| !string.Equals(first.Glyph, other.Glyph, StringComparison.Ordinal)
					|| first.DeadKey != other.DeadKey
					|| !string.Equals(first.DisplayName, other.DisplayName, StringComparison.Ordinal)
					|| !string.Equals(first.ResolvedToken, other.ResolvedToken, StringComparison.Ordinal))
					return true;
			}

			return false;
		}

		private static void ApplyLayoutRoundTrip(LayoutKeyResult item)
		{
			var binding = new ComboBindingData();
			ComboSlotRules.SetSlotResult set = ComboSlotRules.TrySetSlot(binding, 1, item.CaptureToken);
			if (set == ComboSlotRules.SetSlotResult.RejectedF11)
			{
				item.Verdict = DiagnosticVerdict.Skipped;
				item.ConversionReason = "F11-reserved-by-MOD-wide-registration-policy";
				return;
			}
			if (set != ComboSlotRules.SetSlotResult.Accepted)
			{
				item.Verdict = DiagnosticVerdict.Fail;
				item.ConversionReason = "layout-capture-token-register-rejected:" + set;
				return;
			}

			item.SavedToken = binding.Slot1;
			string id = "__ak_layout_diag_" + item.Layout.LayoutIdentifier
				+ "_" + item.Scan.ToString("X2") + "_" + item.Extended;
			var temporary = new Dictionary<string, ComboBindingData>(StringComparer.Ordinal) {
				[id] = binding
			};
			BindingJsonCodec.LoadResult loaded = BindingJsonCodec.Load(BindingJsonCodec.Save(temporary));
			if (loaded.Kind != BindingJsonCodec.LoadKind.Ok
				|| loaded.Bindings == null
				|| !loaded.Bindings.TryGetValue(id, out ComboBindingData reloaded)
				|| reloaded == null)
			{
				item.Verdict = DiagnosticVerdict.Fail;
				item.ConversionReason = "layout-token-reload-failed:" + loaded.Kind;
				return;
			}

			item.ReloadedToken = reloaded.Slot1;
			if (!string.Equals(item.CaptureToken, item.ReloadedToken, StringComparison.Ordinal))
			{
				item.Verdict = DiagnosticVerdict.Fail;
				item.ConversionReason = "layout-token-roundtrip-mismatch before="
					+ item.CaptureToken + " after=" + (item.ReloadedToken ?? "(null)");
			}
		}

		private static void AppendInternalFocusLists(StringBuilder sb, List<AllKeyDiagnosticResult> results)
		{
			sb.AppendLine("=== SKIPPED LIST ===");
			foreach (AllKeyDiagnosticResult r in results)
				if (r.Verdict == AllKeyDiagnosticVerdict.Skipped)
					sb.AppendLine("SKIPPED | " + r.SourceKeyName + " | reason=" + r.Reason);
			sb.AppendLine("=== OEM KEY RESULTS ===");
			foreach (AllKeyDiagnosticResult r in results)
				if (r.SourceKeyName.StartsWith("Oem", StringComparison.OrdinalIgnoreCase))
					sb.AppendLine(r.Verdict.ToString().ToUpperInvariant() + " | " + r.SourceKeyName + " | saved=" + r.SavedValue + " | reloaded=" + r.ReloadedInternalValue + " | display=" + r.DisplayName);
			sb.AppendLine("=== SPECIAL KEY RESULTS ===");
			foreach (AllKeyDiagnosticResult r in results)
				if (IsFocusKey(r.SourceKeyName))
					sb.AppendLine(r.Verdict.ToString().ToUpperInvariant() + " | " + r.SourceKeyName + " | saved=" + r.SavedValue + " | reloaded=" + r.ReloadedInternalValue + " | display=" + r.DisplayName);
		}

		private static void AppendLayoutFocusLists(StringBuilder sb, List<LayoutKeyResult> results)
		{
			sb.AppendLine();
			sb.AppendLine("=== OEM / SPECIAL KEY POSITION RESULTS ===");
			foreach (LayoutKeyResult r in results)
			{
				if ((r.MonoGameKey?.StartsWith("Oem", StringComparison.OrdinalIgnoreCase) ?? false)
					|| IsFocusKey(r.MonoGameKey)
					|| r.Vk is 0x0D or 0x10 or 0x11 or 0x12 or 0x13 or 0x14 or 0x2C or 0x5B or 0x5C
						or 0x5D or 0x6F or 0x90 or 0x91
					|| (r.Vk >= 0xA6 && r.Vk <= 0xB7))
				{
					sb.AppendLine(
						r.Verdict.ToString().ToUpperInvariant()
						+ " | " + r.Layout.CultureName
						+ " | scan=0x" + r.Scan.ToString("X2")
						+ " ext=" + r.Extended
						+ " vk=0x" + r.Vk.ToString("X2")
						+ " keys=" + r.MonoGameKey
						+ " token=" + r.CaptureToken
						+ " display=" + r.DisplayName
						+ " reason=" + r.ConversionReason);
				}
			}
		}

		private static bool IsFocusKey(string key)
			=> key is "Pause" or "PrintScreen" or "NumLock" or "CapsLock" or "ScrollLock"
				or "Scroll" or "LeftWindows" or "RightWindows" or "Apps" or "Enter"
				or "Divide" or "RightControl" or "RightAlt" or "BrowserBack" or "BrowserForward"
				or "VolumeMute" or "VolumeDown" or "VolumeUp" or "MediaNextTrack"
				or "MediaPreviousTrack" or "MediaStop" or "MediaPlayPause"
				or "Kana" or "Kanji" or "ImeConvert" or "ImeNoConvert" or "HankakuZenkaku";

		private static string DescribeSideAndEnter(uint vk, ushort scan, bool extended)
		{
			return vk switch {
				0x10 => "UNKNOWN_SIDE(VK_SHIFT generic; scan=0x" + scan.ToString("X2") + ")",
				0x11 => extended ? "RightControlByExtended" : "LeftControlByNonExtended",
				0x12 => extended ? "RightAltByExtended" : "LeftAltByNonExtended",
				0xA0 => "LeftShift",
				0xA1 => "RightShift",
				0xA2 => "LeftControl",
				0xA3 => "RightControl",
				0xA4 => "LeftAlt",
				0xA5 => "RightAlt",
				0x0D => extended ? "NumpadEnterByExtended" : "MainEnterByNonExtended",
				_ => "not-applicable"
			};
		}

		private static void AppendByVerdict(
			StringBuilder sb,
			List<LayoutKeyResult> results,
			DiagnosticVerdict verdict)
		{
			foreach (LayoutKeyResult r in results)
			{
				if (r.Verdict != verdict)
					continue;
				sb.AppendLine(
					verdict.ToString().ToUpperInvariant()
					+ " | layout=" + r.Layout.CultureName
					+ " scan=0x" + r.Scan.ToString("X2")
					+ " ext=" + r.Extended
					+ " vk=0x" + r.Vk.ToString("X2")
					+ " token=" + r.CaptureToken
					+ " reason=" + r.ConversionReason);
			}
		}

		private static DiagnosticRunOutcome Outcome(
			string path,
			int total,
			int pass,
			int info,
			int expected,
			int warning,
			int fail,
			int skipped,
			int untested,
			int unavailable)
			=> new() {
				Ok = true,
				ReportPath = path,
				Message = "ok",
				Total = total,
				Pass = pass,
				Info = info,
				ExpectedDifference = expected,
				Warning = warning,
				Fail = fail,
				Skipped = skipped,
				Untested = untested,
				Unavailable = unavailable
			};
	}
}
