namespace AdvancedKeybinds.Features.ListView
{
	/// <summary>
	/// Source-specific inventory hover <b>sound request</b> gate (not a global SoundEngine mute).
	/// When AK ALL KEY claims front ownership, matched PlaySound requests are skipped;
	/// otherwise the original request runs with the same arguments and return value.
	/// </summary>
	internal static class BindingListViewHoverSoundRequestSuppressGate
	{
		/// <summary>Pure decision used by wrappers and offline tests.</summary>
		public static bool ShouldSuppressRequest(bool akFrontOwnership)
			=> akFrontOwnership;
	}
}
