namespace AdvancedKeybinds.Features.ListView
{
	/// <summary>
	/// ListView UI prefs stub. Body height is no longer persisted (Open always uses
	/// <see cref="BindingListViewState.DefaultBodyHeightUi"/>; session resize only).
	/// Legacy listview-ui.json is ignored so an old tall height cannot re-apply.
	/// </summary>
	internal static class BindingListViewUiPrefs
	{
		/// <summary>Always 0 — height restore disabled.</summary>
		public static float SavedBodyHeightUi => 0f;

		/// <summary>No-op. Height must not be saved across Close / restart.</summary>
		public static void SaveExpandedBodyHeightIfGrew(float bodyHeightUi)
		{
			_ = bodyHeightUi;
		}
	}
}
