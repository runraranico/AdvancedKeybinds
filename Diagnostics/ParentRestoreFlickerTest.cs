using Terraria.ModLoader;

namespace AdvancedKeybinds.Diagnostics
{
	/// <summary>
	/// Diagnosis-only test build for ComboPanel parent-restore flicker / pad-nav split.
	/// NOT a product fix — do not ship these gates as the final solution.
	/// </summary>
	internal static class ParentRestoreFlickerTest
	{
		/// <summary>Previous test: skip ALL parent restore. Must stay false — breaks pad UI nav.</summary>
		public static bool AllParentRestoreDisabled => false;

		/// <summary>
		/// This test: run immediate ChangePoint/View restore once; skip DeferredRestoreFrames re-apply.
		/// ShouldHold against GamepadTab/Back still ticks down without re-calling TryRestore.
		/// </summary>
		public static bool DeferredReapplyDisabled => true;

		public static void AnnounceOnLoad(Mod mod)
		{
			try
			{
				mod?.Logger?.Info("[ParentRestoreFlickerTest] IMMEDIATE_ONLY_DEFERRED_DISABLED");
			}
			catch
			{
				/* ignore */
			}
		}
	}
}
