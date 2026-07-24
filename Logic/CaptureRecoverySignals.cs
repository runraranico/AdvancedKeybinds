using System;
using System.Collections.Generic;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Injectable evidence for capture recovery (ghost held / KeyUp miss / layout / API mismatch).
	/// <see cref="ConfirmedHeld"/> null means “no recovery evidence this frame” (legacy Poll path).
	/// Empty <see cref="ConfirmedHeld"/> means ordinary APIs report no held keys/mouse.
	/// </summary>
	public sealed class CaptureRecoverySignals
	{
		public static CaptureRecoverySignals None { get; } = new();

		/// <summary>
		/// Held keys confirmed by ordinary APIs (FNA / Win32 VK / mouse / physical toggles).
		/// Must not include latch-only or scan-flag ghosts that lack corroboration.
		/// Null skips contradiction recovery; empty (non-null) participates.
		/// </summary>
		public IReadOnlyCollection<string> ConfirmedHeld { get; init; }

		public bool LayoutChanged { get; init; }
		public bool ApiMismatch { get; init; }
		public bool ImeComposingEnded { get; init; }
		/// <summary>
		/// Imm conversion-mode toggle (e.g. after 「全角/半角」) while capture is waiting —
		/// contradiction evidence for NLS KeyUp-miss ghosts (not a timed clear).
		/// </summary>
		public bool ImeConversionToggled { get; init; }
		public bool ExceptionResidue { get; init; }

		/// <summary>Human-readable reason tags for diagnostics / tests.</summary>
		public string DiagHint { get; init; }

		public bool HasRecoveryEvidence
			=> ConfirmedHeld != null
				|| LayoutChanged
				|| ApiMismatch
				|| ImeComposingEnded
				|| ImeConversionToggled
				|| ExceptionResidue;
	}
}
