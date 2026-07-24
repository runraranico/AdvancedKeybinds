using System;
using System.Globalization;
using AdvancedKeybinds.Binding;

namespace AdvancedKeybinds.Diagnostics
{
	/// <summary>
	/// Read-only diagnostic API for AdvancedKeybindsDiagnostics.
	/// Has no side effects on registration, storage, capture sessions, or settings.
	/// </summary>
	public static class AdvancedKeybindsDiagnosticsApi
	{
		public const int ApiSchemaVersion = 1;

		public static bool IsAvailable
			=> AdvancedKeybinds.Instance != null;

		public static DiagnosticRegistrationResult EvaluateKeyForDiagnostics(DiagnosticKeyInput input)
		{
			if (!IsAvailable)
			{
				return new DiagnosticRegistrationResult {
					EvaluationSucceeded = false,
					FailureStage = "ApiAvailability",
					FailureReason = "Advanced Keybinds is not loaded",
					RelevantMethod = "AdvancedKeybindsDiagnosticsApi.IsAvailable"
				};
			}

			DiagnosticRegistrationResult result = DiagnosticKeyEvaluator.Evaluate(input);
			DiagnosticEventPublisher.PublishCaptureEvaluation(input, result);
			return result;
		}

		public static DiagnosticEnvironmentInfo GetEnvironmentInfo()
		{
			if (!OperatingSystem.IsWindows())
			{
				return new DiagnosticEnvironmentInfo {
					PhysicalJis = null,
					PhysicalLayoutStatus = "PhysicalLayoutUnknown",
					PhysicalLayoutEvidence = "non-Windows",
					CapsHoldViable = null,
					LayoutId = null,
					Hkl = null,
					InputLanguage = CultureInfo.CurrentUICulture?.Name
				};
			}

			try
			{
				IntPtr hkl = KeyboardLayoutHelper.GetActiveLayoutHandle();
				uint lang = (uint)hkl.ToInt64() & 0xFFFF;
				return new DiagnosticEnvironmentInfo {
					PhysicalJis = KeyboardLayoutHelper.IsJapanesePhysicalKeyboard(),
					PhysicalLayoutStatus = KeyboardLayoutHelper.IsJapanesePhysicalKeyboard()
						? "PhysicalLayoutEstimated"
						: "PhysicalLayoutUnknown",
					PhysicalLayoutEvidence = KeyboardLayoutHelper.DescribePhysicalLayoutEvidence(),
					CapsHoldViable = KeyboardLayoutHelper.IsCapsLockHoldViable(),
					LayoutId = lang.ToString("X4"),
					Hkl = hkl.ToInt64().ToString("X"),
					InputLanguage = CultureInfo.CurrentUICulture?.Name
				};
			}
			catch
			{
				return new DiagnosticEnvironmentInfo {
					PhysicalJis = null,
					PhysicalLayoutStatus = "PhysicalLayoutUnknown",
					PhysicalLayoutEvidence = "environment-query-exception",
					CapsHoldViable = null,
					InputLanguage = CultureInfo.CurrentUICulture?.Name
				};
			}
		}
	}
}
