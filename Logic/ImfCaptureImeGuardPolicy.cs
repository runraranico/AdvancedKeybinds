using System;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Pure policy for AK-only IMF capture compatibility.
	/// When Input Method Fixer left SDL text input ON, letter edges never reach SlotCapture.
	/// Proposed fix: Disable IImeService only while capturing, then restore.
	/// Does not modify IMF. Does not change keyboard registration/fire math.
	/// </summary>
	public static class ImfCaptureImeGuardPolicy
	{
		public const string InputMethodFixerModName = "InputMethodFixer";

		/// <summary>
		/// Suppress only when: user setting allows, IMF is present, and IME was enabled at capture begin.
		/// Avoids touching IME when IMF is absent or IME was already off.
		/// </summary>
		public static bool ShouldSuppressImeDuringCapture(
			bool configEnabled,
			bool inputMethodFixerPresent,
			bool imeEnabledAtCaptureBegin)
			=> configEnabled && inputMethodFixerPresent && imeEnabledAtCaptureBegin;

		public static bool ShouldRestoreImeAfterCapture(bool imeEnabledAtCaptureBegin)
			=> imeEnabledAtCaptureBegin;

		/// <summary>
		/// Mechanical model used by Core.Tests: after AK suppress, SDL text input is inactive
		/// even if IMF had left it active at Load.
		/// </summary>
		public static bool EffectiveSdlTextInputActiveDuringCapture(
			bool imfLeftTextInputActive,
			bool akSuppressImeDuringCapture)
			=> imfLeftTextInputActive && !akSuppressImeDuringCapture;

		/// <summary>
		/// Chat / sign WritingText must never trigger AK IME Disable by itself.
		/// Suppress is capture-session only (<see cref="ShouldSuppressImeDuringCapture"/>).
		/// </summary>
		public static bool ShouldSuppressImeBecauseWritingText(bool writingText)
			=> false;

		/// <summary>
		/// While the player is typing in Terraria chat (WritingText) and SlotCapture is idle,
		/// OS/IMF IME must stay enabled so Chinese (and other) text can be composed and sent.
		/// IMF presence is not required for this gate; capture-active always blocks the guarantee.
		/// </summary>
		public static bool ChatImeShouldRemainUsable(bool writingText, bool slotCaptureActive)
			=> writingText && !slotCaptureActive;
	}
}
