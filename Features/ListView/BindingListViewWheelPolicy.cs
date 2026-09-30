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

		/// <summary>
		/// Regression: no-scrollbar must allow probe; scrollbar must suppress probe for scroll.
		/// </summary>
		internal static void RunRegressionSelfCheck()
		{
			if (!ListCanScroll(32f) || ListCanScroll(0f) || ListCanScroll(0.4f))
				throw new InvalidOperationException("ListView REGRESSION: ListCanScroll threshold broken.");

			// Scrollbar present → probe suppressed (scroll wins).
			if (!ShouldSuppressWheelProbe(overUiChrome: false, listCanScroll: true))
				throw new InvalidOperationException("ListView REGRESSION: scrollable must suppress wheel probe.");

			// No scrollbar → probe allowed (Key Search).
			if (ShouldSuppressWheelProbe(overUiChrome: false, listCanScroll: false))
				throw new InvalidOperationException("ListView REGRESSION: no-scrollbar must allow Wheel Key Search.");

			// Title/chrome → always suppress.
			if (!ShouldSuppressWheelProbe(overUiChrome: true, listCanScroll: false))
				throw new InvalidOperationException("ListView REGRESSION: chrome must suppress wheel probe.");

			if (!ShouldScrollList(acceptingInput: true, listCanScroll: true))
				throw new InvalidOperationException("ListView REGRESSION: scrollable body must scroll.");

			if (ShouldScrollList(acceptingInput: true, listCanScroll: false))
				throw new InvalidOperationException("ListView REGRESSION: no-scrollbar must not list-scroll.");

			if (ShouldScrollList(acceptingInput: false, listCanScroll: true))
				throw new InvalidOperationException("ListView REGRESSION: outside body must not list-scroll.");
		}
	}
}
