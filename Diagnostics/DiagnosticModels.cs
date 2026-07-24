using System.Collections.Generic;

namespace AdvancedKeybinds.Diagnostics
{
	public sealed class DiagnosticKeyInput
	{
		public string DeviceType { get; set; } = "Keyboard";
		public string EventType { get; set; } = "KeyDown";
		public bool IsRepeat { get; set; }
		public int RepeatCount { get; set; }

		public string MonoGameKey { get; set; }
		public uint VirtualKey { get; set; }
		public ushort ScanCode { get; set; }
		public bool Extended { get; set; }
		public bool E0 { get; set; }
		public bool E1 { get; set; }

		public IReadOnlyList<string> SourceEvents { get; set; }
		public DiagnosticModifierState Modifiers { get; set; }
		public DiagnosticImeState ImeState { get; set; }
	}

	public sealed class DiagnosticModifierState
	{
		public bool? LeftShift { get; set; }
		public bool? RightShift { get; set; }
		public bool? LeftCtrl { get; set; }
		public bool? RightCtrl { get; set; }
		public bool? LeftAlt { get; set; }
		public bool? RightAlt { get; set; }
		public bool? LeftWin { get; set; }
		public bool? RightWin { get; set; }
		public bool? CapsLock { get; set; }
		public bool? NumLock { get; set; }
		public bool? ScrollLock { get; set; }
		public bool? PossibleAltGr { get; set; }
		public string AltGrEvidence { get; set; }
	}

	public sealed class DiagnosticImeState
	{
		public string ImeOpen { get; set; } = "Unknown";
		public string ConversionMode { get; set; } = "Unknown";
		public string SentenceMode { get; set; } = "Unknown";
		public string Composing { get; set; } = "Unknown";
		public string CandidateWindowActive { get; set; } = "Unknown";
		public string ImeContextAvailable { get; set; } = "Unknown";
	}

	public sealed class DiagnosticRegistrationResult
	{
		public bool EvaluationSucceeded { get; set; }

		public string ResolvedTier { get; set; } = "Unknown";
		public string MonoGameKey { get; set; }
		public uint VirtualKey { get; set; }
		public ushort ScanCode { get; set; }
		public bool Extended { get; set; }
		public string PhysicalKeyId { get; set; }
		/// <summary>Keycap / user-facing label for normal UI (not a save token).</summary>
		public string DisplayLabel { get; set; }
		/// <summary>MonoGame Keys, VK, scan, extended, PhysicalKeyId — for detail panels only.</summary>
		public string DeveloperDetail { get; set; }

		public bool IsSupported { get; set; }
		public bool CanRegister { get; set; }
		public string RestrictionResult { get; set; }
		public string DuplicateResult { get; set; }
		public string CaptureCompatibility { get; set; }
		public string FailureStage { get; set; }
		public string FailureReason { get; set; }
		public string RelevantMethod { get; set; }
		public string RelevantRule { get; set; }
	}

	public sealed class DiagnosticEnvironmentInfo
	{
		public bool? PhysicalJis { get; set; }
		public string PhysicalLayoutStatus { get; set; }
		public string PhysicalLayoutEvidence { get; set; }
		public bool? CapsHoldViable { get; set; }
		public string LayoutId { get; set; }
		public string Hkl { get; set; }
		public string InputLanguage { get; set; }
	}
}
