using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ReLogic.Graphics;
using Terraria;
using Terraria.GameContent;
using Terraria.Localization;
using Terraria.UI.Chat;

namespace AdvancedKeybinds.Features.ListView
{
	internal static class BindingListViewRenderer
	{
		private static readonly Color TitleBarColor = new(28, 36, 52, 230);
		private static readonly Color BodyColor = new(20, 28, 40, 210);
		private static readonly Color BorderColor = new(140, 190, 220, 255);
		private static readonly Color TitleTextColor = new(255, 210, 90, 255);
		private static readonly Color HeaderColor = new(160, 180, 200, 255);
		private static readonly Color RowTextColor = new(220, 230, 240, 255);
		private static readonly Color KeyCapBack = new(40, 48, 62, 255);
		private static readonly Color KeyCapBorder = new(120, 140, 160, 255);
		private static readonly Color AccentKey = new(255, 230, 90, 255);
		private static readonly Color ButtonFace = new(50, 60, 78, 255);
		private static readonly Color ScrollTrack = new(30, 38, 50, 220);
		private static readonly Color ScrollThumb = new(120, 150, 180, 230);

		private const float RowHeight = 28f;
		private const float GroupGap = 10f;
		private const float GroupHeadingHeight = 22f;
		private const float ColKeyFrac = 0.34f;
		private const float ColActionFrac = 0.38f;
		private const int KeyTextPadX = 6;
		/// <summary>Horizontal chrome outside rows viewport: left/right pad 8+8, scrollbar gap 4, scrollbar width.</summary>
		private const int WindowChromeXUi = 16 + 4 + BindingListViewLayout.ScrollbarWidth;
		// Remainder → MOD column inside rows viewport when fractions are used.

		public static void Draw(SpriteBatch spriteBatch, BindingListViewState state, IReadOnlyList<BindingListEntry> rows)
		{
			if (spriteBatch == null || state == null || !state.IsOpen)
				return;


			BindingListViewLayout.ClampToScreen(state);
			Rectangle title = BindingListViewLayout.TitleBarRect(state);
			DrawRect(spriteBatch, title, TitleBarColor);
			DrawBorder(spriteBatch, title, BorderColor);

			DrawTitleLabel(spriteBatch, state);
			DrawMinMaxButton(spriteBatch, state);
			DrawCloseButton(spriteBatch, state);

			if (!state.IsMaximized)
			{
				return;
			}

			Rectangle body = BindingListViewLayout.BodyRect(state);
			DrawRect(spriteBatch, body, BodyColor);
			DrawBorder(spriteBatch, body, BorderColor);

			DrawColumnHeaders(spriteBatch, state, body);

			bool modSort = state.ModSortEnabled;
			float contentH = MeasureContentHeight(rows, modSort);
			state.ContentHeightUi = contentH;
			state.ClampScroll();

			Rectangle rowsView = BindingListViewLayout.RowsViewportRect(state);
			DrawRowsClipped(spriteBatch, state, rowsView, rows, modSort);
			DrawScrollbar(spriteBatch, state, rowsView);
		}

		/// <summary>
		/// Fix window width to the longest Key/Action/MOD among all registered rows for this device
		/// (not the filtered visible subset). Stable across probe filters. Height is unchanged.
		/// </summary>
		public static void ApplyFixedWidthFromRegistered(
			BindingListViewState state,
			IReadOnlyList<BindingListEntry> allRegisteredForDevice)
		{
			if (state == null)
				return;

			MeasureColumnContentWidths(allRegisteredForDevice, out int keyCol, out int actCol, out int modCol);
			state.ColKeyWidthUi = keyCol;
			state.ColActionWidthUi = actCol;
			state.ColModWidthUi = modCol;

			// Minimized chrome uses its own compact width — do not apply list content width.
			if (!state.IsMaximized || state.SuppressContentWidthFit || state.WidthLockedByUser)
				return;

			int viewNeed = keyCol + actCol + modCol;
			float preferW = viewNeed + WindowChromeXUi;
			if (preferW < BindingListViewState.MinWidthUi)
				preferW = BindingListViewState.MinWidthUi;

			float surfaceW = Main.screenWidth;
			if (surfaceW >= 32f && preferW > surfaceW)
				preferW = surfaceW;

			state.SetWidthUi(preferW);
		}

		private static void MeasureColumnContentWidths(
			IReadOnlyList<BindingListEntry> rows,
			out int keyCol,
			out int actCol,
			out int modCol)
		{
			// Restore pre-TextFit layout sizing (user-confirmed AK ALL KEY layout).
			// TextFit must NOT alter these rectangles — it only draws into them.
			float keyMax = MeasureTextWidth(SafeLocal("Mods.AdvancedKeybinds.UI.BindingList.ColKey", "キー"));
			float actMax = MeasureTextWidth(SafeLocal("Mods.AdvancedKeybinds.UI.BindingList.ColAction", "動作"));
			float modMax = MeasureTextWidth(SafeLocal("Mods.AdvancedKeybinds.UI.BindingList.ColMod", "MOD"));

			if (rows != null)
			{
				for (int i = 0; i < rows.Count; i++)
				{
					BindingListEntry e = rows[i];
					if (e == null)
						continue;
					keyMax = Math.Max(keyMax, MeasureTextWidth(e.KeyComboDisplay));
					actMax = Math.Max(actMax, MeasureTextWidth(e.ActionDisplay));
					modMax = Math.Max(modMax, MeasureTextWidth(e.ModDisplay));
				}
			}

			// Match DrawRowsClipped padding: keyRect = keyW-6 + KeyTextPadX; action = actW-4 + pad 2; mod = modW + pad 2.
			keyCol = Math.Max(48, (int)Math.Ceiling(keyMax + KeyTextPadX * 2 + 6));
			actCol = Math.Max(48, (int)Math.Ceiling(actMax + 4 + 4));
			modCol = Math.Max(40, (int)Math.Ceiling(modMax + 4));
		}

		private static float MeasureTextWidth(string text)
		{
			if (string.IsNullOrEmpty(text))
				return 0f;
			return FontAssets.MouseText.Value.MeasureString(text).X;
		}

		private static void ResolveColumnWidths(BindingListViewState state, float viewWidth, out int keyW, out int actW, out int modW)
		{
			keyW = state.ColKeyWidthUi;
			actW = state.ColActionWidthUi;
			modW = state.ColModWidthUi;
			int need = keyW + actW + modW;
			if (keyW <= 0 || actW <= 0 || modW <= 0 || need <= 0)
			{
				keyW = (int)(viewWidth * ColKeyFrac);
				actW = (int)(viewWidth * ColActionFrac);
				modW = Math.Max(24, (int)viewWidth - keyW - actW);
				return;
			}

			if (need > viewWidth && need > 0)
			{
				float s = viewWidth / need;
				keyW = Math.Max(24, (int)(keyW * s));
				actW = Math.Max(24, (int)(actW * s));
				modW = Math.Max(24, (int)viewWidth - keyW - actW);
			}
			else
			{
				// Absorb leftover into MOD so the viewport is fully used.
				modW = Math.Max(modW, (int)viewWidth - keyW - actW);
			}
		}

		public static float MeasureContentHeight(IReadOnlyList<BindingListEntry> rows, bool modSort = false)
		{
			if (rows == null || rows.Count == 0)
				return 24f;

			if (modSort)
			{
				string lastMod = null;
				float y = 0f;
				for (int i = 0; i < rows.Count; i++)
				{
					string mod = rows[i].ModDisplay ?? string.Empty;
					if (lastMod != null
						&& !string.Equals(mod, lastMod, StringComparison.OrdinalIgnoreCase))
						y += RowHeight; // one blank row between MOD groups
					lastMod = mod;
					y += RowHeight;
				}

				return y + 4f;
			}

			string lastHeading = null;
			float yh = 0f;
			for (int i = 0; i < rows.Count; i++)
			{
				string heading = BindingListSort.GroupHeading(rows[i].PrimaryToken, rows[i].PrimaryDisplay);
				if (!string.Equals(heading, lastHeading, StringComparison.Ordinal))
				{
					if (lastHeading != null)
						yh += GroupGap;
					lastHeading = heading;
					yh += GroupHeadingHeight;
				}

				yh += RowHeight;
			}

			return yh + 4f;
		}

		private static void DrawTitleLabel(SpriteBatch sb, BindingListViewState state)
		{
			Rectangle r = BindingListViewLayout.TitleLabelRect(state);
			const string fallback = "AK ALL KEY";
			string text = SafeLocal("Mods.AdvancedKeybinds.UI.BindingList.Title", fallback);
			DrawTextClipped(sb, text, r, TitleTextColor, padX: 0);
		}

		private static void DrawCloseButton(SpriteBatch sb, BindingListViewState state)
		{
			Rectangle r = BindingListViewLayout.CloseButtonRect(state);
			DrawRect(sb, r, ButtonFace);
			DrawBorder(sb, r, BorderColor);
			DrawCenteredGlyph(sb, r, "x", RowTextColor);
		}

		private static void DrawMinMaxButton(SpriteBatch sb, BindingListViewState state)
		{
			Rectangle r = BindingListViewLayout.MinMaxButtonRect(state);
			DrawRect(sb, r, ButtonFace);
			DrawBorder(sb, r, BorderColor);
			string glyph = state.IsMaximized ? "-" : "□";
			DrawCenteredGlyph(sb, r, glyph, RowTextColor);
		}

		private static void DrawColumnHeaders(SpriteBatch sb, BindingListViewState state, Rectangle body)
		{
			Rectangle view = new(
				body.X + 8,
				body.Y + 6,
				Math.Max(40, body.Width - 16 - BindingListViewLayout.ScrollbarWidth - 4),
				18);
			ResolveColumnWidths(state, view.Width, out int keyW, out int actW, out _);

			string keyLabel = SafeLocal("Mods.AdvancedKeybinds.UI.BindingList.ColKey", "キー");
			string actLabel = SafeLocal("Mods.AdvancedKeybinds.UI.BindingList.ColAction", "動作");
			string modLabel = SafeLocal("Mods.AdvancedKeybinds.UI.BindingList.ColMod", "MOD");

			var keyRect = new Rectangle(view.X, view.Y, keyW - 4, view.Height);
			var actRect = new Rectangle(view.X + keyW, view.Y, actW - 4, view.Height);

			// Shared Visual Ink height so Key / Action / MOD share one hierarchy (no per-label height skew).
			DynamicSpriteFont font = FontAssets.MouseText.Value;
			float sharedInkH = 0f;
			float kh = BindingListViewTextFit.GetVisualInkHeight(font, keyLabel);
			float ah = BindingListViewTextFit.GetVisualInkHeight(font, actLabel);
			float mh = BindingListViewTextFit.GetVisualInkHeight(font, modLabel);
			if (kh > sharedInkH) sharedInkH = kh;
			if (ah > sharedInkH) sharedInkH = ah;
			if (mh > sharedInkH) sharedInkH = mh;
			float heightMetric = sharedInkH > 0.01f ? sharedInkH : -1f;

			DrawHeaderText(sb, keyLabel, keyRect, HeaderColor, BindingListViewTextAlign.Left, KeyTextPadX, heightMetric);
			DrawHeaderText(sb, actLabel, actRect, HeaderColor, BindingListViewTextAlign.Left, 2, heightMetric);

			Rectangle modBtn = ModHeaderButtonRect(state);
			if (!modBtn.IsEmpty)
			{
				DrawRect(sb, modBtn, ButtonFace);
				DrawBorder(sb, modBtn, BorderColor);
				Color modHeaderColor = state.ModSortEnabled ? TitleTextColor : HeaderColor;
				// Fixed chrome rect unchanged — TextFit scales/centers inside only.
				DrawHeaderText(sb, modLabel, modBtn, modHeaderColor, BindingListViewTextAlign.Center, 0, heightMetric);
			}

			float lineY = body.Y + 28f;
			if (!modBtn.IsEmpty)
				lineY = Math.Max(lineY, modBtn.Bottom + 3f);
			DrawRect(sb, new Rectangle(body.X + 8, (int)lineY, body.Width - 16, 1), BorderColor * 0.6f);
		}

		/// <summary>
		/// Column header labels only. Ink-height scale (shared metric) + Visual Ink Y. Body path unchanged.
		/// </summary>
		private static void DrawHeaderText(
			SpriteBatch sb,
			string text,
			Rectangle bounds,
			Color color,
			BindingListViewTextAlign align,
			int padX,
			float sharedInkHeightMetric)
		{
			BindingListViewTextFit.DrawFittedText(
				sb, text, bounds, color, align, padX,
				useVisualInkHeightForScale: true,
				heightMetricOverride: sharedInkHeightMetric);
		}

		private const int ModHeaderPadX = 6;

		/// <summary>
		/// Pre-TextFit MOD header chrome (small). Sized like the user-confirmed layout — NOT the full mod column.
		/// TextFit draws into this rect only; it must not widen this rect.
		/// </summary>
		public static Rectangle ModHeaderButtonRect(BindingListViewState state)
		{
			if (state == null || !state.IsMaximized)
				return Rectangle.Empty;

			Rectangle body = BindingListViewLayout.BodyRect(state);
			var view = new Rectangle(
				body.X + 8,
				body.Y + 6,
				Math.Max(40, body.Width - 16 - BindingListViewLayout.ScrollbarWidth - 4),
				18);
			ResolveColumnWidths(state, view.Width, out int keyW, out int actW, out _);

			string modLabel = SafeLocal("Mods.AdvancedKeybinds.UI.BindingList.ColMod", "MOD");
			Vector2 textSize = FontAssets.MouseText.Value.MeasureString(modLabel ?? string.Empty);
			int btnW = Math.Max(22, (int)Math.Ceiling(textSize.X) + ModHeaderPadX * 2);
			// Keep height inside the header strip so layout Y does not shift per glyph metrics.
			int btnH = view.Height;
			int x = view.X + keyW + actW;
			int y = view.Y;
			int maxRight = view.X + view.Width;
			if (x + btnW > maxRight)
				btnW = Math.Max(16, maxRight - x);
			return new Rectangle(x, y, btnW, btnH);
		}

		private static void DrawRowsClipped(
			SpriteBatch sb,
			BindingListViewState state,
			Rectangle view,
			IReadOnlyList<BindingListEntry> rows,
			bool modSort)
		{
			if (state.ContentMode == BindingListContentMode.Empty
				|| rows == null
				|| rows.Count == 0)
			{
				string msg = state.ContentMode == BindingListContentMode.Filtered
					? SafeLocal("Mods.AdvancedKeybinds.UI.BindingList.NoMatch", "(no bindings for this key)")
					: SafeLocal("Mods.AdvancedKeybinds.UI.BindingList.EmptyHint", "Click AK ALL KEY, or press a key inside the window");
				// Same body ink-height TextFit as Key/GroupHeading — not LineSpacing-capped DrawTextClipped.
				var hintRect = new Rectangle(view.X, view.Y + 4, view.Width, 20);
				BindingListViewTextFit.DrawFittedText(
					sb, msg, hintRect, HeaderColor, BindingListViewTextAlign.Left, padX: 0,
					useVisualInkHeightForScale: true);
				return;
			}

			GraphicsDevice device = Main.instance.GraphicsDevice;
			Rectangle prevScissor = device.ScissorRectangle;
			RasterizerState prevRs = sb.GraphicsDevice.RasterizerState;

			try
			{
				sb.End();
				Rectangle scissor = TransformUiRectToScissor(view);
				scissor = Rectangle.Intersect(scissor, device.Viewport.Bounds);
				device.ScissorRectangle = scissor;
				var rs = new RasterizerState { ScissorTestEnable = true };
				sb.Begin(
					SpriteSortMode.Deferred,
					BlendState.AlphaBlend,
					SamplerState.LinearClamp,
					DepthStencilState.None,
					rs,
					null,
					Main.UIScaleMatrix);

				string lastHeading = null;
				string lastMod = null;
				float y = view.Y - state.ScrollOffsetUi;
				ResolveColumnWidths(state, view.Width, out int keyW, out int actW, out int modW);
				int rowInnerH = (int)RowHeight - 4;

				for (int i = 0; i < rows.Count; i++)
				{
					BindingListEntry e = rows[i];

					if (modSort)
					{
						string mod = e.ModDisplay ?? string.Empty;
						if (lastMod != null
							&& !string.Equals(mod, lastMod, StringComparison.OrdinalIgnoreCase))
							y += RowHeight; // one blank row between MOD groups
						lastMod = mod;
					}
					else
					{
						string heading = BindingListSort.GroupHeading(e.PrimaryToken, e.PrimaryDisplay);
						if (!string.Equals(heading, lastHeading, StringComparison.Ordinal))
						{
							if (lastHeading != null)
								y += GroupGap;
							lastHeading = heading;
							if (y + GroupHeadingHeight >= view.Y - 2 && y <= view.Bottom)
								DrawGroupHeading(sb, heading, view.X, (int)y, keyW - 6, (int)GroupHeadingHeight);
							y += GroupHeadingHeight;
						}
					}

					if (y + RowHeight < view.Y)
					{
						y += RowHeight;
						continue;
					}

					if (y > view.Bottom)
						break;

					var keyRect = new Rectangle(view.X, (int)y, Math.Max(20, keyW - 6), rowInnerH);
					var actionRect = new Rectangle(view.X + keyW, (int)y, Math.Max(20, actW - 4), rowInnerH);
					var modRect = new Rectangle(view.X + keyW + actW, (int)y, Math.Max(20, modW), rowInnerH);

					DrawKeyCap(sb, keyRect, e.KeyComboDisplay ?? string.Empty);
					DrawBodyTextClipped(sb, e.ActionDisplay ?? string.Empty, actionRect, RowTextColor, padX: 2);
					DrawBodyTextClipped(sb, e.ModDisplay ?? string.Empty, modRect, RowTextColor, padX: 2);
					y += RowHeight;
				}
			}
			finally
			{
				try
				{
					sb.End();
				}
				catch
				{
					// ignore
				}

				device.ScissorRectangle = prevScissor;
				sb.Begin(
					SpriteSortMode.Deferred,
					BlendState.AlphaBlend,
					SamplerState.LinearClamp,
					DepthStencilState.None,
					prevRs ?? RasterizerState.CullCounterClockwise,
					null,
					Main.UIScaleMatrix);
			}
		}

		private static void DrawKeyCap(SpriteBatch sb, Rectangle keyRect, string text)
		{
			DrawRect(sb, keyRect, KeyCapBack);
			DrawBorder(sb, keyRect, KeyCapBorder);
			// Body Key: same StandardScale as Action/MOD; ink-height scale so short keys are not LineSpacing-capped.
			BindingListViewTextFit.DrawFittedText(
				sb, text, keyRect, AccentKey, BindingListViewTextAlign.Left, KeyTextPadX,
				useVisualInkHeightForScale: true);
		}

		/// <summary>
		/// List body Action/MOD text. Uses visual-ink height for scale (not header/title path).
		/// </summary>
		private static void DrawBodyTextClipped(
			SpriteBatch sb,
			string text,
			Rectangle bounds,
			Color color,
			int padX = 0)
		{
			BindingListViewTextFit.DrawFittedText(
				sb, text, bounds, color, BindingListViewTextAlign.Left, padX,
				useVisualInkHeightForScale: true);
		}

		/// <summary>
		/// Letter/F-key section label. Same left inset as key text inside caps so "A" lines up with "A" / "A+S+D".
		/// Uses body ink-height TextFit (same rule as Key caps) — must not stay on LineSpacing-capped DrawTextClipped.
		/// </summary>
		private static void DrawGroupHeading(SpriteBatch sb, string heading, int x, int y, int keyColW, int height)
		{
			int w = Math.Max(20, keyColW);
			var textBounds = new Rectangle(x, y, w, height);
			BindingListViewTextFit.DrawFittedText(
				sb, heading ?? string.Empty, textBounds, AccentKey, BindingListViewTextAlign.Left, KeyTextPadX,
				useVisualInkHeightForScale: true);
		}

		private static Rectangle TransformUiRectToScissor(Rectangle uiRect)
		{
			// InterfaceScaleType.UI: UI units → backbuffer via UIScale.
			float scale = Main.UIScale;
			if (scale <= 0.01f)
				scale = 1f;
			return new Rectangle(
				(int)Math.Floor(uiRect.X * scale),
				(int)Math.Floor(uiRect.Y * scale),
				(int)Math.Ceiling(uiRect.Width * scale),
				(int)Math.Ceiling(uiRect.Height * scale));
		}

		private static void DrawScrollbar(SpriteBatch sb, BindingListViewState state, Rectangle view)
		{
			float maxScroll = state.MaxScrollUi;
			if (maxScroll <= 0.5f)
				return;

			Rectangle track = BindingListViewLayout.ScrollTrackRect(state);
			DrawRect(sb, track, ScrollTrack);
			DrawBorder(sb, track, BorderColor * 0.7f);

			float viewH = view.Height;
			float contentH = Math.Max(viewH + maxScroll, viewH + 1f);
			float thumbH = MathHelper.Clamp(viewH * (viewH / contentH), 24f, track.Height);
			float t = maxScroll <= 0f ? 0f : state.ScrollOffsetUi / maxScroll;
			float thumbY = track.Y + (track.Height - thumbH) * t;
			var thumb = new Rectangle(track.X + 1, (int)thumbY, track.Width - 2, (int)thumbH);
			DrawRect(sb, thumb, ScrollThumb);
		}

		/// <summary>Maps a Y in the scroll track to scroll offset (for thumb drag).</summary>
		public static float ScrollOffsetFromTrackY(BindingListViewState state, float mouseY)
		{
			Rectangle track = BindingListViewLayout.ScrollTrackRect(state);
			float maxScroll = state.MaxScrollUi;
			if (maxScroll <= 0f || track.Height <= 1)
				return 0f;

			float viewH = BindingListViewLayout.RowsViewportRect(state).Height;
			float contentH = Math.Max(viewH + maxScroll, viewH + 1f);
			float thumbH = MathHelper.Clamp(viewH * (viewH / contentH), 24f, track.Height);
			float travel = track.Height - thumbH;
			if (travel <= 1f)
				return 0f;

			float rel = MathHelper.Clamp(mouseY - track.Y - thumbH * 0.5f, 0f, travel);
			return maxScroll * (rel / travel);
		}

		private static void DrawCenteredGlyph(SpriteBatch sb, Rectangle r, string glyph, Color color)
		{
			// Title-bar chrome only: allow > StandardScale so − / × fill the fixed 22×22 rect; Visual Ink centers.
			BindingListViewTextFit.DrawFittedText(
				sb, glyph, r, color, BindingListViewTextAlign.Center, padX: 0,
				useVisualInkHeightForScale: true,
				maxScale: BindingListViewTextFit.ChromeGlyphMaxScale);
		}

		private static void DrawTextClipped(
			SpriteBatch sb,
			string text,
			Rectangle bounds,
			Color color,
			int padX = 0)
		{
			BindingListViewTextFit.DrawFittedText(sb, text, bounds, color, BindingListViewTextAlign.Left, padX);
		}

		private static void DrawRect(SpriteBatch sb, Rectangle r, Color c)
		{
			sb.Draw(TextureAssets.MagicPixel.Value, r, c);
		}

		private static void DrawBorder(SpriteBatch sb, Rectangle r, Color c)
		{
			sb.Draw(TextureAssets.MagicPixel.Value, new Rectangle(r.X, r.Y, r.Width, 1), c);
			sb.Draw(TextureAssets.MagicPixel.Value, new Rectangle(r.X, r.Bottom - 1, r.Width, 1), c);
			sb.Draw(TextureAssets.MagicPixel.Value, new Rectangle(r.X, r.Y, 1, r.Height), c);
			sb.Draw(TextureAssets.MagicPixel.Value, new Rectangle(r.Right - 1, r.Y, 1, r.Height), c);
		}

		private static string SafeLocal(string key, string fallback)
		{
			try
			{
				string v = Language.GetTextValue(key);
				if (string.IsNullOrEmpty(v) || v == key || v.StartsWith("Mods.AdvancedKeybinds.UI.BindingList", StringComparison.Ordinal))
					return fallback;
				return v;
			}
			catch
			{
				return fallback;
			}
		}
	}
}
