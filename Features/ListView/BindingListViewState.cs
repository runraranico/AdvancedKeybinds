using Microsoft.Xna.Framework;

namespace AdvancedKeybinds.Features.ListView
{
	internal enum BindingListContentMode : byte
	{
		Empty = 0,
		All = 1,
		Filtered = 2
	}

	/// <summary>Which device family the list shows for this open session.</summary>
	internal enum BindingListDeviceFilter : byte
	{
		KeyboardMouse = 0,
		GamePad = 1
	}

	/// <summary>UI-only state for the binding list overlay. Not persisted to bindings.json.</summary>
	internal sealed class BindingListViewState
	{
		public const float MinWidthUi = 320f;
		/// <summary>Floor for minimized title-bar chrome only (title + −/□ + ×).</summary>
		public const float MinMinimizedWidthUi = 100f;
		public const float MinBodyHeightUi = 140f;
		/// <summary>
		/// Initial maximized body height (UI units) on every Open. Not content-fitted; not persisted.
		/// Total chrome ≈ TitleBarHeightUi + this. Screen clamp via <see cref="BindingListViewLayout.ClampToScreen"/>.
		/// </summary>
		public const float DefaultBodyHeightUi = 360f;

		public bool IsOpen { get; private set; }
		public bool IsMaximized { get; private set; } = true;
		public BindingListContentMode ContentMode { get; private set; } = BindingListContentMode.All;
		public BindingListDeviceFilter DeviceFilter { get; private set; } = BindingListDeviceFilter.KeyboardMouse;
		public string FilterProbeToken { get; private set; }

		/// <summary>When true (default), rows are MOD-priority sorted; MOD header is lit.</summary>
		public bool ModSortEnabled { get; private set; } = true;

		public Vector2 PositionUi { get; set; } = new Vector2(80f, 120f);

		public float WidthUi { get; set; } = 560f;
		public float BodyHeightUi { get; set; } = DefaultBodyHeightUi;
		public float TitleBarHeightUi { get; set; } = 28f;

		public float ScrollOffsetUi { get; set; }
		public float ContentHeightUi { get; set; }

		/// <summary>Absolute column widths inside the rows viewport (UI units). 0 = use fraction fallback.</summary>
		public int ColKeyWidthUi { get; set; }
		public int ColActionWidthUi { get; set; }
		public int ColModWidthUi { get; set; }

		/// <summary>While true, skip content-based width auto-fit (active edge resize).</summary>
		public bool SuppressContentWidthFit { get; set; }

		/// <summary>User finished a width drag — keep until open / show-all / probe filter.</summary>
		public bool WidthLockedByUser { get; set; }

		/// <summary>Maximized width restored on Maximize. Not persisted. 0 = unset.</summary>
		public float WidthBeforeMinimizeUi { get; set; }

		public void OpenMaximizedAll(BindingListDeviceFilter deviceFilter)
		{
			IsOpen = true;
			IsMaximized = true;
			DeviceFilter = deviceFilter;
			ContentMode = BindingListContentMode.All;
			ModSortEnabled = true;
			FilterProbeToken = null;
			ScrollOffsetUi = 0f;
			WidthLockedByUser = false;
			SuppressContentWidthFit = false;
			WidthBeforeMinimizeUi = 0f;
			// Every Open: fixed initial body height (UI units). Not content-driven; not restored from prefs.
			BodyHeightUi = DefaultBodyHeightUi;
		}

		public void Close()
		{
			IsOpen = false;
			FilterProbeToken = null;
			ContentMode = BindingListContentMode.All;
			ScrollOffsetUi = 0f;
			WidthBeforeMinimizeUi = 0f;
		}

		/// <summary>
		/// End of play session / title screen: force closed; next open starts with MOD sort ON.
		/// </summary>
		public void ResetForNewSession()
		{
			Close();
			ModSortEnabled = true;
			IsMaximized = true;
			WidthLockedByUser = false;
			SuppressContentWidthFit = false;
			BodyHeightUi = DefaultBodyHeightUi;
			WidthBeforeMinimizeUi = 0f;
		}

		public void ToggleOpenFromHotkey(BindingListDeviceFilter deviceFilterWhenOpening)
		{
			if (IsOpen)
				Close();
			else
				OpenMaximizedAll(deviceFilterWhenOpening);
		}

		public void Minimize()
		{
			if (!IsOpen)
				return;
			if (IsMaximized)
				WidthBeforeMinimizeUi = WidthUi;
			IsMaximized = false;
			ScrollOffsetUi = 0f;
			SuppressContentWidthFit = true;
			WidthLockedByUser = true;
			float compact = BindingListViewLayout.ComputeMinimizedChromeWidthUi(this);
			WidthUi = compact < MinMinimizedWidthUi ? MinMinimizedWidthUi : compact;
		}

		public void Maximize()
		{
			if (!IsOpen)
				return;
			IsMaximized = true;
			SuppressContentWidthFit = false;
			WidthLockedByUser = false;
			if (WidthBeforeMinimizeUi >= MinWidthUi)
				WidthUi = WidthBeforeMinimizeUi;
			WidthBeforeMinimizeUi = 0f;
		}

		/// <summary>Clear key filter; keep <see cref="ModSortEnabled"/> (title AK ALL KEY).</summary>
		public void ShowAll()
		{
			if (!IsOpen || !IsMaximized)
				return;
			ContentMode = BindingListContentMode.All;
			FilterProbeToken = null;
			ScrollOffsetUi = 0f;
			WidthLockedByUser = false;
		}

		/// <summary>Toggle MOD sort light. On = MOD priority; Off = key-letter groups.</summary>
		public void ToggleModSort()
		{
			if (!IsOpen || !IsMaximized)
				return;
			ModSortEnabled = !ModSortEnabled;
			ScrollOffsetUi = 0f;
			WidthLockedByUser = false;
		}

		public void ApplyProbeToggle(string probeToken)
		{
			if (!IsOpen || !IsMaximized || string.IsNullOrEmpty(probeToken))
				return;

			if (ContentMode == BindingListContentMode.Filtered
				&& string.Equals(FilterProbeToken, probeToken, System.StringComparison.OrdinalIgnoreCase))
			{
				ContentMode = BindingListContentMode.Empty;
				FilterProbeToken = null;
				ScrollOffsetUi = 0f;
				WidthLockedByUser = false;
				return;
			}

			ContentMode = BindingListContentMode.Filtered;
			FilterProbeToken = probeToken;
			ScrollOffsetUi = 0f;
			WidthLockedByUser = false;
		}

		public float CurrentHeightUi
			=> IsMaximized ? TitleBarHeightUi + BodyHeightUi : TitleBarHeightUi;

		public float MaxScrollUi
		{
			get
			{
				// Match RowsViewportRect: top inset 34 + bottom pad 6.
				float view = BodyHeightUi - 40f;
				if (view < 1f)
					view = 1f;
				float max = ContentHeightUi - view;
				return max > 0f ? max : 0f;
			}
		}

		public void ClampScroll()
		{
			float max = MaxScrollUi;
			if (ScrollOffsetUi < 0f)
				ScrollOffsetUi = 0f;
			else if (ScrollOffsetUi > max)
				ScrollOffsetUi = max;
		}

		public void AddScrollDelta(float deltaUi)
		{
			ScrollOffsetUi += deltaUi;
			ClampScroll();
		}

		public void SetWidthUi(float width)
		{
			WidthUi = width < MinWidthUi ? MinWidthUi : width;
		}

		public void SetBodyHeightUi(float height)
		{
			BodyHeightUi = height < MinBodyHeightUi ? MinBodyHeightUi : height;
			ClampScroll();
		}
	}
}
