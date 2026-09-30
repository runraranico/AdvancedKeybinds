using Microsoft.Xna.Framework;
using Terraria;
using Terraria.GameContent;
using Terraria.Localization;

namespace AdvancedKeybinds.Features.ListView
{
	/// <summary>
	/// Keeps the list window fully on the current UI surface (never fully off-screen).
	/// Uses Main.screenWidth/Height only as the interface surface during UI draw phases.
	/// </summary>
	internal static class BindingListViewLayout
	{
		public static void ClampToScreen(BindingListViewState state)
		{
			if (state == null)
				return;

			float surfaceW = Main.screenWidth;
			float surfaceH = Main.screenHeight;
			if (surfaceW < 32f || surfaceH < 32f)
				return;

			float w = state.WidthUi;
			float h = state.CurrentHeightUi;
			if (state.IsMaximized)
			{
				if (w < BindingListViewState.MinWidthUi)
					w = BindingListViewState.MinWidthUi;
			}
			else if (w < BindingListViewState.MinMinimizedWidthUi)
			{
				w = BindingListViewState.MinMinimizedWidthUi;
			}
			if (state.IsMaximized && state.BodyHeightUi < BindingListViewState.MinBodyHeightUi)
				state.SetBodyHeightUi(BindingListViewState.MinBodyHeightUi);

			// Cap size to surface so clamp always keeps chrome reachable.
			if (w > surfaceW)
				w = surfaceW;
			float maxBody = surfaceH - state.TitleBarHeightUi;
			if (state.IsMaximized && state.BodyHeightUi > maxBody && maxBody >= BindingListViewState.MinBodyHeightUi)
				state.SetBodyHeightUi(maxBody);

			h = state.CurrentHeightUi;
			if (h < state.TitleBarHeightUi)
				h = state.TitleBarHeightUi;

			float maxX = System.Math.Max(0f, surfaceW - w);
			float maxY = System.Math.Max(0f, surfaceH - h);

			Vector2 p = state.PositionUi;
			if (float.IsNaN(p.X) || float.IsInfinity(p.X))
				p.X = 0f;
			if (float.IsNaN(p.Y) || float.IsInfinity(p.Y))
				p.Y = 0f;

			p.X = MathHelper.Clamp(p.X, 0f, maxX);
			p.Y = MathHelper.Clamp(p.Y, 0f, maxY);
			state.PositionUi = p;
			state.WidthUi = w;
		}

		public static Rectangle TitleBarRect(BindingListViewState state)
		{
			return new Rectangle(
				(int)state.PositionUi.X,
				(int)state.PositionUi.Y,
				(int)state.WidthUi,
				(int)state.TitleBarHeightUi);
		}

		public static Rectangle BodyRect(BindingListViewState state)
		{
			return new Rectangle(
				(int)state.PositionUi.X,
				(int)(state.PositionUi.Y + state.TitleBarHeightUi),
				(int)state.WidthUi,
				(int)state.BodyHeightUi);
		}

		public static Rectangle CloseButtonRect(BindingListViewState state)
		{
			const int size = 22;
			int pad = 3;
			Rectangle title = TitleBarRect(state);
			return new Rectangle(title.Right - pad - size, title.Y + (title.Height - size) / 2, size, size);
		}

		public static Rectangle MinMaxButtonRect(BindingListViewState state)
		{
			const int size = 22;
			int pad = 3;
			Rectangle close = CloseButtonRect(state);
			return new Rectangle(close.X - pad - size, close.Y, size, size);
		}

		/// <summary>
		/// Compact title-bar width for minimized chrome: title text + paddings + −/□ + ×.
		/// Measured from current title string (no language hardcode). UI units only.
		/// </summary>
		public static float ComputeMinimizedChromeWidthUi(BindingListViewState state)
		{
			const int buttonSize = 22;
			const int buttonPad = 3;
			const int leftPad = 8;
			const int titleToButtonsGap = 6;
			string title = Language.GetTextValue("Mods.AdvancedKeybinds.UI.BindingList.Title");
			if (string.IsNullOrEmpty(title) || title.StartsWith("Mods.", System.StringComparison.Ordinal))
				title = "AK ALL KEY";
			float titleW = 0f;
			try
			{
				titleW = FontAssets.MouseText.Value.MeasureString(title).X;
			}
			catch
			{
				titleW = 90f;
			}

			float w = leftPad + titleW + titleToButtonsGap + buttonSize + buttonPad + buttonSize + buttonPad;
			if (w < BindingListViewState.MinMinimizedWidthUi)
				w = BindingListViewState.MinMinimizedWidthUi;
			return w;
		}

		/// <summary>Clickable title label area (left of min/max buttons).</summary>
		public static Rectangle TitleLabelRect(BindingListViewState state)
		{
			Rectangle title = TitleBarRect(state);
			Rectangle minMax = MinMaxButtonRect(state);
			int right = minMax.X - 6;
			int width = System.Math.Max(40, right - title.X - 8);
			return new Rectangle(title.X + 8, title.Y, width, title.Height);
		}

		/// <summary>Title bar minus − / × — drag handle (maximized or minimized).</summary>
		public static Rectangle TitleDragRect(BindingListViewState state)
		{
			Rectangle title = TitleBarRect(state);
			Rectangle minMax = MinMaxButtonRect(state);
			int width = System.Math.Max(8, minMax.X - 4 - title.X);
			return new Rectangle(title.X, title.Y, width, title.Height);
		}

		public const int ScrollbarWidth = 10;
		/// <summary>Bottom (height) resize hit thickness (UI units), centered on the frame edge.</summary>
		public const int ResizeEdgeUi = 15;
		/// <summary>
		/// Former bottom-right corner size. Not interactive for resize; used only to keep the
		/// bottom height-resize band from covering that corner (corner must not become bottom-resize).
		/// </summary>
		public const int ResizeCornerUi = 15;

		/// <summary>
		/// Body strip for キー / 動作 / MOD headers (above the rows viewport).
		/// Same top inset as <see cref="RowsViewportRect"/> — UI chrome, not list probe area.
		/// </summary>
		public static Rectangle ColumnHeaderStripRect(BindingListViewState state)
		{
			Rectangle body = BodyRect(state);
			const int rowsTopInset = 34;
			int h = System.Math.Min(rowsTopInset, System.Math.Max(0, body.Height));
			return new Rectangle(body.X, body.Y, body.Width, h);
		}

		public static Rectangle RowsViewportRect(BindingListViewState state)
		{
			Rectangle body = BodyRect(state);
			// Below header labels + MOD button + separator (separator sits under MOD frame).
			const int rowsTopInset = 34;
			const int rowsBottomPad = 6;
			return new Rectangle(
				body.X + 8,
				body.Y + rowsTopInset,
				System.Math.Max(40, body.Width - 16 - ScrollbarWidth - 4),
				System.Math.Max(20, body.Height - rowsTopInset - rowsBottomPad));
		}

		public static Rectangle ScrollTrackRect(BindingListViewState state)
		{
			Rectangle body = BodyRect(state);
			Rectangle view = RowsViewportRect(state);
			return new Rectangle(
				body.Right - 6 - ScrollbarWidth,
				view.Y,
				ScrollbarWidth,
				view.Height);
		}

		/// <summary>Full chrome (title + body if maximized) for hover / wheel focus.</summary>
		public static Rectangle WindowRect(BindingListViewState state)
		{
			return new Rectangle(
				(int)state.PositionUi.X,
				(int)state.PositionUi.Y,
				(int)state.WidthUi,
				(int)state.CurrentHeightUi);
		}

		/// <summary>
		/// Former right-edge resize band. Manual width resize is disabled — always empty.
		/// Kept as a named API so callers do not invent an alternate right-edge hit.
		/// </summary>
		public static Rectangle ResizeRightRect(BindingListViewState state)
		{
			return Rectangle.Empty;
		}

		/// <summary>
		/// Height-resize hit band on the bottom frame edge.
		/// Excludes the bottom-right corner square so that corner is not treated as bottom resize.
		/// </summary>
		public static Rectangle ResizeBottomRect(BindingListViewState state)
		{
			if (state == null || !state.IsMaximized)
				return Rectangle.Empty;
			Rectangle win = WindowRect(state);
			int inward = ResizeEdgeUi / 2;
			// Corner left = win.Right - (ResizeCornerUi/2); bottom stops before that X.
			int cornerInward = ResizeCornerUi / 2;
			int bottomW = win.Width - cornerInward;
			if (bottomW < 1)
				return Rectangle.Empty;
			return new Rectangle(win.X, win.Bottom - inward, bottomW, ResizeEdgeUi);
		}

		/// <summary>
		/// Former bottom-right corner resize square. Manual corner resize is disabled — always empty.
		/// Bottom height-resize already excludes this region via <see cref="ResizeBottomRect"/>.
		/// </summary>
		public static Rectangle ResizeCornerRect(BindingListViewState state)
		{
			return Rectangle.Empty;
		}

		/// <summary>Legacy full-column rect — prefer <see cref="BindingListViewRenderer.ModHeaderButtonRect"/> for hits.</summary>
		public static Rectangle ModHeaderColumnRect(BindingListViewState state)
		{
			if (state == null || !state.IsMaximized)
				return Rectangle.Empty;

			Rectangle body = BodyRect(state);
			var view = new Rectangle(
				body.X + 8,
				body.Y + 6,
				System.Math.Max(40, body.Width - 16 - ScrollbarWidth - 4),
				18);

			int keyW = state.ColKeyWidthUi;
			int actW = state.ColActionWidthUi;
			int modW = state.ColModWidthUi;
			int need = keyW + actW + modW;
			if (keyW <= 0 || actW <= 0 || modW <= 0 || need <= 0)
			{
				keyW = (int)(view.Width * 0.34f);
				actW = (int)(view.Width * 0.38f);
				modW = System.Math.Max(24, view.Width - keyW - actW);
			}
			else if (need > view.Width)
			{
				float s = (float)view.Width / need;
				keyW = System.Math.Max(24, (int)(keyW * s));
				actW = System.Math.Max(24, (int)(actW * s));
				modW = System.Math.Max(24, view.Width - keyW - actW);
			}
			else
				modW = System.Math.Max(modW, view.Width - keyW - actW);

			return new Rectangle(view.X + keyW + actW, view.Y, System.Math.Max(20, modW), view.Height);
		}
	}
}
