using System;

namespace AdvancedKeybinds.Features.ListView
{
	/// <summary>
	/// Wheel priority inside AK ALL KEY (when frontmost and cursor in body):
	/// scrollable → list scroll; not scrollable → Key Search (Wheel Up/Down probe).
	/// </summary>
	internal static class BindingListViewWheelPolicy
	{
		/// <summary>
		/// True when list content overflows (scrollbar present).
		/// </summary>
		public static bool ListCanScroll(float maxScrollUi)
			=> maxScrollUi > 0.5f;

		/// <summary>
		/// Suppress BindingListKeyProbe wheel edges when chrome owns UI, or when scroll owns the wheel.
		/// When there is no scrollbar, wheel must reach Key Search — do not suppress for that case.
		/// </summary>
		public static bool ShouldSuppressWheelProbe(bool overUiChrome, bool listCanScroll)
			=> overUiChrome || listCanScroll;

		/// <summary>
		/// Apply list scroll delta only while accepting body input and scrollbar is present.
		/// </summary>
		public static bool ShouldScrollList(bool acceptingInput, bool listCanScroll)
			=> acceptingInput && listCanScroll;

	}
}
