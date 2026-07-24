using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Pure all-key registration / save / reload diagnostic (no Terraria I/O, no BindingStorage).
	/// Display-name generation is supplied by the game layer; this type never mutates user settings.
	/// </summary>
	public enum AllKeyDiagnosticVerdict
	{
		Pass,
		Warning,
		Fail,
		Skipped
	}

	public sealed class AllKeyDiagnosticResult
	{
		public string SourceKeyName { get; set; }
		public string SourceInternalValue { get; set; }
		public string SavedValue { get; set; }
		public string ReloadedInternalValue { get; set; }
		public string DisplayName { get; set; }
		public AllKeyDiagnosticVerdict Verdict { get; set; }
		public string Reason { get; set; }
		public string Exception { get; set; }
	}

	public static class AllKeyDiagnosticCore
	{
		public const string TemporaryActionPrefix = "__ak_diag_tmp_";

		/// <summary>
		/// Registerable keyboard tokens currently known to the mod:
		/// XNA/FNA <c>Keys</c> names (except <c>None</c>) plus mod-only <c>HankakuZenkaku</c> / <c>ScrollLock</c>.
		/// Does not invent Fn / layout-only keys.
		/// </summary>
		public static List<string> EnumerateDiagnosticTargets()
		{
			var list = new List<string>(StableKeyToken.AllKnownKeysNames.Count + 2);
			foreach (string name in StableKeyToken.AllKnownKeysNames)
			{
				if (string.Equals(name, "None", StringComparison.OrdinalIgnoreCase))
					continue;
				list.Add(name);
			}

			if (!ContainsOrdinalIgnoreCase(list, StableKeyToken.HankakuZenkakuToken))
				list.Add(StableKeyToken.HankakuZenkakuToken);
			if (!ContainsOrdinalIgnoreCase(list, StableKeyToken.ScrollLockToken))
				list.Add(StableKeyToken.ScrollLockToken);

			return list;
		}

		/// <summary>
		/// Enumerate → register → JSON save → reload → compare internal values.
		/// Display inspection is applied afterward via <see cref="ApplyDisplayInspection"/>.
		/// </summary>
		public static AllKeyDiagnosticResult EvaluateRegistrationAndRoundTrip(string sourceKeyName)
		{
			var result = new AllKeyDiagnosticResult {
				SourceKeyName = sourceKeyName ?? string.Empty,
				SourceInternalValue = string.Empty,
				SavedValue = string.Empty,
				ReloadedInternalValue = string.Empty,
				DisplayName = string.Empty,
				Verdict = AllKeyDiagnosticVerdict.Fail,
				Reason = string.Empty,
				Exception = null
			};

			try
			{
				if (string.IsNullOrWhiteSpace(sourceKeyName))
				{
					result.Reason = "source-key-empty";
					return result;
				}

				string canonical = StableKeyToken.CanonicalizeOrNull(sourceKeyName);
				if (canonical == null)
				{
					result.Reason = "cannot-enumerate-or-canonicalize";
					return result;
				}

				result.SourceInternalValue = canonical;

				var binding = new ComboBindingData();
				ComboSlotRules.SetSlotResult set = ComboSlotRules.TrySetSlot(binding, 1, canonical);
				if (set == ComboSlotRules.SetSlotResult.RejectedF11)
				{
					result.Verdict = AllKeyDiagnosticVerdict.Skipped;
					result.Reason = "F11-reserved-by-policy";
					result.SavedValue = string.Empty;
					result.ReloadedInternalValue = string.Empty;
					return result;
				}

				if (set != ComboSlotRules.SetSlotResult.Accepted)
				{
					result.Verdict = AllKeyDiagnosticVerdict.Fail;
					result.Reason = "register-rejected:" + set;
					return result;
				}

				string registered = binding.GetSlot(1);
				if (!string.Equals(registered, canonical, StringComparison.Ordinal))
				{
					result.Verdict = AllKeyDiagnosticVerdict.Fail;
					result.Reason = "register-mutated-internal-value"
						+ " expected=" + canonical
						+ " got=" + (registered ?? "(null)");
					result.SavedValue = registered ?? string.Empty;
					return result;
				}

				string actionId = TemporaryActionPrefix + canonical;
				var temporary = new Dictionary<string, ComboBindingData>(StringComparer.Ordinal) {
					[actionId] = binding.Clone()
				};

				string json = BindingJsonCodec.Save(temporary);
				result.SavedValue = registered;

				BindingJsonCodec.LoadResult loaded = BindingJsonCodec.Load(json);
				if (loaded.Kind != BindingJsonCodec.LoadKind.Ok)
				{
					result.Verdict = AllKeyDiagnosticVerdict.Fail;
					result.Reason = "reload-failed:" + loaded.Kind
						+ (string.IsNullOrEmpty(loaded.Message) ? string.Empty : (" " + loaded.Message));
					return result;
				}

				if (loaded.Bindings == null
					|| !loaded.Bindings.TryGetValue(actionId, out ComboBindingData reloaded)
					|| reloaded == null)
				{
					result.Verdict = AllKeyDiagnosticVerdict.Fail;
					result.Reason = "reload-missing-temporary-action";
					return result;
				}

				string reloadedToken = reloaded.GetSlot(1) ?? string.Empty;
				result.ReloadedInternalValue = reloadedToken;

				if (!string.Equals(reloadedToken, canonical, StringComparison.Ordinal))
				{
					result.Verdict = AllKeyDiagnosticVerdict.Fail;
					result.Reason = "reload-internal-value-mismatch"
						+ " before=" + canonical
						+ " after=" + reloadedToken;
					return result;
				}

				if (!StableKeyToken.TokensEqual(canonical, reloadedToken))
				{
					result.Verdict = AllKeyDiagnosticVerdict.Fail;
					result.Reason = "reload-token-equality-failed";
					return result;
				}

				result.Verdict = AllKeyDiagnosticVerdict.Pass;
				result.Reason = "ok";
				return result;
			}
			catch (Exception ex)
			{
				result.Verdict = AllKeyDiagnosticVerdict.Fail;
				result.Reason = "exception-during-round-trip";
				result.Exception = ex.GetType().Name + ": " + ex.Message;
				return result;
			}
		}

		/// <summary>
		/// Apply display-name checks. Empty / Unknown / exception → FAIL (or escalate).
		/// Does not downgrade existing FAIL/SKIPPED except by appending reasons.
		/// </summary>
		public static void ApplyDisplayInspection(
			AllKeyDiagnosticResult result,
			string displayName,
			Exception displayException)
		{
			if (result == null || result.Verdict == AllKeyDiagnosticVerdict.Skipped)
				return;

			if (displayException != null)
			{
				Escalate(result, AllKeyDiagnosticVerdict.Fail, "display-exception");
				result.Exception = string.IsNullOrEmpty(result.Exception)
					? displayException.GetType().Name + ": " + displayException.Message
					: result.Exception + " | " + displayException.GetType().Name + ": " + displayException.Message;
				result.DisplayName = displayName ?? string.Empty;
				return;
			}

			result.DisplayName = displayName ?? string.Empty;

			if (string.IsNullOrWhiteSpace(result.DisplayName))
			{
				Escalate(result, AllKeyDiagnosticVerdict.Fail, "display-empty");
				return;
			}

			if (IsUnknownDisplay(result.DisplayName))
			{
				Escalate(result, AllKeyDiagnosticVerdict.Fail, "display-unknown");
			}
		}

		/// <summary>
		/// Cross-key analysis: internal-value collisions are FAIL; display-name duplicates are WARNING.
		/// Left/Right pairs that collapse to one internal id are FAIL.
		/// </summary>
		public static void ApplyCrossKeyAnalysis(IList<AllKeyDiagnosticResult> results)
		{
			if (results == null || results.Count == 0)
				return;

			var byInternal = new Dictionary<string, List<AllKeyDiagnosticResult>>(StringComparer.OrdinalIgnoreCase);
			var byDisplay = new Dictionary<string, List<AllKeyDiagnosticResult>>(StringComparer.Ordinal);

			foreach (AllKeyDiagnosticResult r in results)
			{
				if (r == null || r.Verdict == AllKeyDiagnosticVerdict.Skipped)
					continue;

				string internalId = !string.IsNullOrEmpty(r.ReloadedInternalValue)
					? r.ReloadedInternalValue
					: r.SourceInternalValue;
				if (!string.IsNullOrEmpty(internalId))
				{
					if (!byInternal.TryGetValue(internalId, out List<AllKeyDiagnosticResult> list))
					{
						list = new List<AllKeyDiagnosticResult>(2);
						byInternal[internalId] = list;
					}

					list.Add(r);
				}

				if (!string.IsNullOrWhiteSpace(r.DisplayName)
					&& r.Verdict != AllKeyDiagnosticVerdict.Fail)
				{
					string displayKey = r.DisplayName.Trim();
					if (!byDisplay.TryGetValue(displayKey, out List<AllKeyDiagnosticResult> dlist))
					{
						dlist = new List<AllKeyDiagnosticResult>(2);
						byDisplay[displayKey] = dlist;
					}

					dlist.Add(r);
				}
			}

			foreach (KeyValuePair<string, List<AllKeyDiagnosticResult>> pair in byInternal)
			{
				if (pair.Value.Count < 2)
					continue;

				var sources = new List<string>(pair.Value.Count);
				foreach (AllKeyDiagnosticResult r in pair.Value)
					sources.Add(r.SourceKeyName);

				string reason = "internal-value-collision id=" + pair.Key
					+ " sources=[" + string.Join(",", sources) + "]";
				foreach (AllKeyDiagnosticResult r in pair.Value)
					Escalate(r, AllKeyDiagnosticVerdict.Fail, reason);
			}

			AssertLeftRightDistinct(results, "LeftShift", "RightShift");
			AssertLeftRightDistinct(results, "LeftControl", "RightControl");
			AssertLeftRightDistinct(results, "LeftAlt", "RightAlt");
			AssertLeftRightDistinct(results, "LeftWindows", "RightWindows");

			foreach (KeyValuePair<string, List<AllKeyDiagnosticResult>> pair in byDisplay)
			{
				if (pair.Value.Count < 2)
					continue;

				// Only warn results that are not already FAIL.
				var sources = new List<string>(pair.Value.Count);
				foreach (AllKeyDiagnosticResult r in pair.Value)
					sources.Add(r.SourceKeyName);

				string reason = "display-name-duplicate display=\"" + pair.Key
					+ "\" sources=[" + string.Join(",", sources) + "]";
				foreach (AllKeyDiagnosticResult r in pair.Value)
				{
					if (r.Verdict == AllKeyDiagnosticVerdict.Fail
						|| r.Verdict == AllKeyDiagnosticVerdict.Skipped)
						continue;
					Escalate(r, AllKeyDiagnosticVerdict.Warning, reason);
				}
			}
		}

		public static string BuildReport(
			IReadOnlyList<AllKeyDiagnosticResult> results,
			DateTime startedUtc,
			DateTime endedUtc,
			string outputPath)
		{
			int pass = 0, warn = 0, fail = 0, skipped = 0;
			foreach (AllKeyDiagnosticResult r in results)
			{
				switch (r.Verdict)
				{
					case AllKeyDiagnosticVerdict.Pass: pass++; break;
					case AllKeyDiagnosticVerdict.Warning: warn++; break;
					case AllKeyDiagnosticVerdict.Fail: fail++; break;
					case AllKeyDiagnosticVerdict.Skipped: skipped++; break;
				}
			}

			var sb = new StringBuilder(results.Count * 160);
			sb.AppendLine("=== AdvancedKeybinds All-Key Diagnostic BEGIN ===");
			sb.AppendLine("StartedUtc=" + startedUtc.ToString("o", CultureInfo.InvariantCulture));
			sb.AppendLine("EndedUtc=" + endedUtc.ToString("o", CultureInfo.InvariantCulture));
			sb.AppendLine("OutputPath=" + (outputPath ?? string.Empty));
			sb.AppendLine("Note=temporary in-memory BindingsJsonCodec only; BindingStorage / user bindings.json untouched");
			sb.AppendLine();

			foreach (AllKeyDiagnosticResult r in results)
			{
				sb.AppendLine("---");
				sb.AppendLine("SourceKey=" + (r.SourceKeyName ?? string.Empty));
				sb.AppendLine("SourceInternal=" + (r.SourceInternalValue ?? string.Empty));
				sb.AppendLine("SavedValue=" + (r.SavedValue ?? string.Empty));
				sb.AppendLine("ReloadedInternal=" + (r.ReloadedInternalValue ?? string.Empty));
				sb.AppendLine("DisplayName=" + (r.DisplayName ?? string.Empty));
				sb.AppendLine("Verdict=" + r.Verdict.ToString().ToUpperInvariant());
				sb.AppendLine("Reason=" + (r.Reason ?? string.Empty));
				if (!string.IsNullOrEmpty(r.Exception))
					sb.AppendLine("Exception=" + r.Exception);
			}

			sb.AppendLine();
			sb.AppendLine("=== SUMMARY ===");
			sb.AppendLine("Total: " + results.Count);
			sb.AppendLine("PASS: " + pass);
			sb.AppendLine("WARNING: " + warn);
			sb.AppendLine("FAIL: " + fail);
			sb.AppendLine("SKIPPED: " + skipped);
			sb.AppendLine();
			sb.AppendLine("=== FAIL / WARNING LIST ===");
			foreach (AllKeyDiagnosticResult r in results)
			{
				if (r.Verdict != AllKeyDiagnosticVerdict.Fail && r.Verdict != AllKeyDiagnosticVerdict.Warning)
					continue;
				sb.AppendLine(
					r.Verdict.ToString().ToUpperInvariant()
					+ " | " + (r.SourceKeyName ?? string.Empty)
					+ " | internal=" + (r.ReloadedInternalValue ?? r.SourceInternalValue ?? string.Empty)
					+ " | display=" + (r.DisplayName ?? string.Empty)
					+ " | reason=" + (r.Reason ?? string.Empty)
					+ (string.IsNullOrEmpty(r.Exception) ? string.Empty : (" | exception=" + r.Exception)));
			}

			sb.AppendLine("=== AdvancedKeybinds All-Key Diagnostic END ===");
			return sb.ToString();
		}

		public static void CountVerdicts(
			IReadOnlyList<AllKeyDiagnosticResult> results,
			out int total,
			out int pass,
			out int warning,
			out int fail,
			out int skipped)
		{
			total = results?.Count ?? 0;
			pass = warning = fail = skipped = 0;
			if (results == null)
				return;

			foreach (AllKeyDiagnosticResult r in results)
			{
				switch (r.Verdict)
				{
					case AllKeyDiagnosticVerdict.Pass: pass++; break;
					case AllKeyDiagnosticVerdict.Warning: warning++; break;
					case AllKeyDiagnosticVerdict.Fail: fail++; break;
					case AllKeyDiagnosticVerdict.Skipped: skipped++; break;
				}
			}
		}

		private static void AssertLeftRightDistinct(
			IList<AllKeyDiagnosticResult> results,
			string leftName,
			string rightName)
		{
			AllKeyDiagnosticResult left = FindBySource(results, leftName);
			AllKeyDiagnosticResult right = FindBySource(results, rightName);
			if (left == null || right == null)
				return;
			if (left.Verdict == AllKeyDiagnosticVerdict.Skipped || right.Verdict == AllKeyDiagnosticVerdict.Skipped)
				return;

			string leftId = !string.IsNullOrEmpty(left.ReloadedInternalValue)
				? left.ReloadedInternalValue
				: left.SourceInternalValue;
			string rightId = !string.IsNullOrEmpty(right.ReloadedInternalValue)
				? right.ReloadedInternalValue
				: right.SourceInternalValue;

			if (string.IsNullOrEmpty(leftId) || string.IsNullOrEmpty(rightId))
				return;

			if (string.Equals(leftId, rightId, StringComparison.OrdinalIgnoreCase))
			{
				string reason = "left-right-collapsed " + leftName + "/" + rightName + " -> " + leftId;
				Escalate(left, AllKeyDiagnosticVerdict.Fail, reason);
				Escalate(right, AllKeyDiagnosticVerdict.Fail, reason);
			}
		}

		private static AllKeyDiagnosticResult FindBySource(IList<AllKeyDiagnosticResult> results, string name)
		{
			foreach (AllKeyDiagnosticResult r in results)
			{
				if (r != null && string.Equals(r.SourceKeyName, name, StringComparison.OrdinalIgnoreCase))
					return r;
			}

			return null;
		}

		private static void Escalate(AllKeyDiagnosticResult result, AllKeyDiagnosticVerdict next, string reason)
		{
			if (result.Verdict == AllKeyDiagnosticVerdict.Skipped)
				return;

			if (next == AllKeyDiagnosticVerdict.Fail)
				result.Verdict = AllKeyDiagnosticVerdict.Fail;
			else if (next == AllKeyDiagnosticVerdict.Warning && result.Verdict == AllKeyDiagnosticVerdict.Pass)
				result.Verdict = AllKeyDiagnosticVerdict.Warning;

			if (string.IsNullOrEmpty(result.Reason) || result.Reason == "ok")
				result.Reason = reason;
			else if (result.Reason.IndexOf(reason, StringComparison.Ordinal) < 0)
				result.Reason = result.Reason + " | " + reason;
		}

		private static bool IsUnknownDisplay(string display)
			=> string.Equals(display.Trim(), "Unknown", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(display.Trim(), "(unknown)", StringComparison.OrdinalIgnoreCase);

		private static bool ContainsOrdinalIgnoreCase(List<string> list, string value)
		{
			foreach (string item in list)
			{
				if (string.Equals(item, value, StringComparison.OrdinalIgnoreCase))
					return true;
			}

			return false;
		}
	}
}
