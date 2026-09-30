namespace AdvancedKeybinds.Features.ListView
{
	/// <summary>
	/// Source-specific hover <b>request</b> gate (not final shared draw suppress).
	/// When AK ALL KEY genuinely claims front ownership, matched producer requests are skipped;
	/// otherwise the original request runs with the same arguments.
	/// </summary>
	internal static class BindingListViewHoverRequestSuppressGate
	{
		/// <summary>Pure decision used by wrappers and offline tests.</summary>
		public static bool ShouldSuppressRequest(bool akFrontOwnership)
			=> akFrontOwnership;
	}
}
