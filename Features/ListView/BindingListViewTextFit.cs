using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ReLogic.Graphics;
using Terraria.GameContent;
using Terraria.UI.Chat;

namespace AdvancedKeybinds.Features.ListView
{
	internal enum BindingListViewTextAlign
	{
		Left,
		Center,
		Right
	}

	/// <summary>
	/// Shared Text Fit + Alignment for ListView draws.
	/// Order: measure → available → scale → final size → position → draw.
	/// No language-name or Localization-Key coordinate hacks. No UIScale multiply/divide.
	/// Measurement uses ChatManager.GetStringSize to match ChatManager draw metrics.
	/// Vertical placement uses DynamicSpriteFont glyph Padding/Glyph (visual ink), not LineSpacing alone.
	/// </summary>
	internal static class BindingListViewTextFit
	{
		public const float StandardScale = 1f;
		public const float HorizontalSafeMargin = 2f;
		public const float VerticalSafeMargin = 2f;

		/// <summary>
		/// Min uniform scale: row content band ≈24, after margins ≈20; keep drawn height ≥ ~11px → 0.55.
		/// </summary>
		public const float MinUniformScale = 0.55f;

		/// <summary>
		/// Title-bar − / × (and □) may exceed StandardScale so thin glyphs fill the fixed 22×22 chrome.
		/// Still capped by availW/availH via Visual Ink; never expands the button Rectangle.
		/// </summary>
		public const float ChromeGlyphMaxScale = 1.85f;

		public static Vector2 MeasureAtStandard(string text)
		{
			BindingListViewOpenPerfDiag.NoteMeasureCall();
			if (string.IsNullOrEmpty(text))
				return Vector2.Zero;
			return ChatManager.GetStringSize(FontAssets.MouseText.Value, text, new Vector2(StandardScale));
		}

		public static Vector2 MeasureAtScale(string text, float scale)
		{
			BindingListViewOpenPerfDiag.NoteMeasureCall();
			if (string.IsNullOrEmpty(text))
				return Vector2.Zero;
			float s = scale > 0.01f ? scale : MinUniformScale;
			return ChatManager.GetStringSize(FontAssets.MouseText.Value, text, new Vector2(s));
		}

		public static float AvailableWidth(int boundsWidth, int padX, BindingListViewTextAlign align)
		{
			float w;
			if (align == BindingListViewTextAlign.Center)
			{
				float inset = Math.Max(padX, HorizontalSafeMargin);
				w = boundsWidth - inset * 2f;
			}
			else
			{
				w = boundsWidth - padX - HorizontalSafeMargin;
			}

			return w > 1f ? w : 1f;
		}

		public static float AvailableHeight(int boundsHeight)
		{
			float pad = boundsHeight >= 22 ? VerticalSafeMargin * 2f : 1f;
			float h = boundsHeight - pad;
			return h > 1f ? h : 1f;
		}

		public static float ComputeUniformScale(
			Vector2 measuredAtStandard,
			float availableWidth,
			float availableHeight,
			float heightMetricOverride = -1f,
			float maxScale = -1f)
		{
			float cap = maxScale > 0.01f ? maxScale : StandardScale;
			float scale = cap;
			if (measuredAtStandard.X > 0.01f)
				scale = Math.Min(scale, availableWidth / measuredAtStandard.X);

			// Height: optional visual-ink height (body/header/chrome). Default = GetStringSize.Y (LineSpacing).
			// LineSpacing (≈29) vs row availH (≈20) was capping ALL short body text at ~0.69.
			float heightMetric = heightMetricOverride > 0.01f
				? heightMetricOverride
				: measuredAtStandard.Y;
			if (heightMetric > 0.01f)
				scale = Math.Min(scale, availableHeight / heightMetric);

			if (scale > cap)
				scale = cap;
			if (scale < MinUniformScale)
				scale = MinUniformScale;
			return scale;
		}

		/// <summary>Visual ink height at scale 1, or 0 if unavailable.</summary>
		public static float GetVisualInkHeight(DynamicSpriteFont font, string text)
		{
			if (TryGetVisualInkRange(font, text, out float inkTop, out float inkBottom))
			{
				float h = inkBottom - inkTop;
				if (h > 0.01f)
					return h;
			}

			return 0f;
		}

		public static bool FitsAtScale(
			Vector2 measuredAtStandard,
			float scale,
			float availableWidth,
			float availableHeight,
			float heightMetricOverride = -1f)
		{
			float heightMetric = heightMetricOverride > 0.01f
				? heightMetricOverride
				: measuredAtStandard.Y;
			return measuredAtStandard.X * scale <= availableWidth + 0.05f
				&& heightMetric * scale <= availableHeight + 0.05f;
		}

		/// <summary>
		/// Final laid-out size after FinalScale. Uses measuredAtStandard * scale for BOTH axes.
		/// ChatManager.GetStringSize(text, scaleVec) has been observed to scale X but leave Y ≈ unscaled
		/// (LIVE: TextW scaled, TextH=29 with FinalScale=0.586) — do not use that for position.
		/// Note: measured Y is DynamicSpriteFont LineSpacing (not visual ink height).
		/// </summary>
		public static Vector2 ScaledDrawSize(Vector2 measuredAtStandard, float scale)
		{
			float s = scale > 0.01f ? scale : MinUniformScale;
			return new Vector2(measuredAtStandard.X * s, measuredAtStandard.Y * s);
		}

		/// <summary>
		/// Visual ink range relative to ChatManager/DynamicSpriteFont draw origin (top of line box), at scale 1.
		/// InternalDraw places each glyph at cursor + Padding; glyph height is Glyph.Height.
		/// MeasureString.Y is LineSpacing and includes asymmetric empty padding — not this range.
		/// Whitespace is skipped: it advances layout but must not expand vertical ink bounds
		/// (MouseText space often has Padding.Y≈40 with Glyph.Height=1).
		/// </summary>
		public static bool TryGetVisualInkRange(DynamicSpriteFont font, string text, out float inkTop, out float inkBottom)
		{
			inkTop = float.MaxValue;
			inkBottom = float.MinValue;
			if (font == null || string.IsNullOrEmpty(text))
				return false;

			var map = font.SpriteCharacters;
			var fallback = font.DefaultCharacterData;
			bool any = false;
			for (int i = 0; i < text.Length; i++)
			{
				char c = text[i];
				// Whitespace advances layout but has no visible ink (MouseText space: Padding.Y≈40, Glyph.H=1).
				// Including it inflates VisualCenter and pushes DrawY up — exclude from ink bounds only.
				if (c == '\n' || c == '\r' || char.IsWhiteSpace(c))
					continue;

				DynamicSpriteFont.SpriteCharacterData data;
				if (map == null || !map.TryGetValue(c, out data))
					data = fallback;

				float top = data.Padding.Y;
				float bottom = data.Padding.Y + data.Glyph.Height;
				if (top < inkTop)
					inkTop = top;
				if (bottom > inkBottom)
					inkBottom = bottom;
				any = true;
			}

			return any && inkBottom > inkTop + 0.01f;
		}

		/// <summary>
		/// Top-left draw X for ChatManager (origin = Zero). Horizontal only — uses measured width * scale.
		/// </summary>
		public static float ComputeDrawX(
			Rectangle cell,
			float drawWidth,
			BindingListViewTextAlign align,
			int padX)
		{
			float x;
			switch (align)
			{
				case BindingListViewTextAlign.Center:
					x = cell.X + (cell.Width - drawWidth) * 0.5f;
					break;
				case BindingListViewTextAlign.Right:
					x = cell.Right - padX - drawWidth;
					break;
				default:
					x = cell.X + padX;
					break;
			}

			if (drawWidth <= cell.Width)
			{
				if (x < cell.X)
					x = cell.X;
				if (x + drawWidth > cell.Right)
					x = cell.Right - drawWidth;
			}
			else
			{
				x = align == BindingListViewTextAlign.Center
					? cell.X + (cell.Width - drawWidth) * 0.5f
					: cell.X;
			}

			return x;
		}

		/// <summary>
		/// Top-left draw Y so visual glyph ink centers in the cell after FinalScale.
		/// Falls back to LineSpacing-box math center when glyph metrics are unavailable.
		/// </summary>
		public static float ComputeDrawYVisual(
			Rectangle cell,
			DynamicSpriteFont font,
			string text,
			float scale,
			float measuredLineHeightAtStandard)
		{
			float s = scale > 0.01f ? scale : MinUniformScale;
			float cellCenterY = cell.Y + cell.Height * 0.5f;

			if (TryGetVisualInkRange(font, text, out float inkTop, out float inkBottom))
			{
				float visualCenterAt1 = (inkTop + inkBottom) * 0.5f;
				float drawY = cellCenterY - visualCenterAt1 * s;

				float inkTopScreen = drawY + inkTop * s;
				float inkBottomScreen = drawY + inkBottom * s;
				float inkH = inkBottomScreen - inkTopScreen;
				if (inkH <= cell.Height + 0.05f)
				{
					if (inkTopScreen < cell.Y)
						drawY += cell.Y - inkTopScreen;
					if (inkBottomScreen > cell.Bottom)
						drawY -= inkBottomScreen - cell.Bottom;
				}

				return drawY;
			}

			// Fallback: previous math center using LineSpacing-sized box.
			float lineH = measuredLineHeightAtStandard > 0.01f
				? measuredLineHeightAtStandard * s
				: font.LineSpacing * s;
			float y = cell.Y + (cell.Height - lineH) * 0.5f;
			if (lineH <= cell.Height)
			{
				if (y < cell.Y)
					y = cell.Y;
				if (y + lineH > cell.Bottom)
					y = cell.Bottom - lineH;
			}

			return y;
		}

		public static string FitEllipsisForScale(string text, float availableWidth, float scale)
		{
			if (string.IsNullOrEmpty(text) || availableWidth < 8f)
				return string.Empty;

			float s = scale > 0.01f ? scale : MinUniformScale;
			if (MeasureAtScale(text, s).X <= availableWidth)
				return text;

			const string ellipsis = "…";
			if (MeasureAtScale(ellipsis, s).X >= availableWidth)
				return ellipsis;

			int lo = 0;
			int hi = text.Length;
			string best = ellipsis;
			while (lo <= hi)
			{
				int mid = (lo + hi) / 2;
				string candidate = text.Substring(0, mid) + ellipsis;
				if (MeasureAtScale(candidate, s).X <= availableWidth)
				{
					best = candidate;
					lo = mid + 1;
				}
				else
					hi = mid - 1;
			}

			return best;
		}

		/// <summary>
		/// Single path: measure → fit scale → X from measured width → Y from visual ink → ChatManager draw.
		/// <paramref name="useVisualInkHeightForScale"/>: body/header/chrome use ink height for the
		/// height scale cap so short strings are not forced to LineSpacing/availH (~0.69). Title keeps false.
		/// <paramref name="heightMetricOverride"/>: shared header ink (Key/Action/MOD same hierarchy).
		/// <paramref name="maxScale"/>: chrome glyphs may exceed <see cref="StandardScale"/>; body stays 1.
		/// </summary>
		public static void DrawFittedText(
			SpriteBatch sb,
			string text,
			Rectangle cell,
			Color color,
			BindingListViewTextAlign align,
			int padX = 0,
			bool useVisualInkHeightForScale = false,
			float heightMetricOverride = -1f,
			float maxScale = -1f)
		{
			if (sb == null || cell.Width <= 4 || cell.Height <= 2)
				return;

			BindingListViewOpenPerfDiag.NoteDrawFittedCall();

			string raw = text ?? string.Empty;
			if (raw.Length == 0)
				return;

			DynamicSpriteFont font = FontAssets.MouseText.Value;
			float availW = AvailableWidth(cell.Width, padX, align);
			// availH:
			// - shared header override: nearly full strip (H≈18) so Key/Action/MOD can approach body size
			// - body/chrome ink paths: 1px top/bottom
			// - title / default: AvailableHeight (safer pad)
			float availH;
			if (heightMetricOverride > 0.01f)
				availH = Math.Max(1f, cell.Height - 1f);
			else if (useVisualInkHeightForScale)
				availH = Math.Max(1f, cell.Height - 2f);
			else
				availH = AvailableHeight(cell.Height);
			Vector2 measuredStd = MeasureAtStandard(raw);
			float heightMetric = heightMetricOverride > 0.01f
				? heightMetricOverride
				: ResolveHeightMetricForScale(font, raw, measuredStd.Y, useVisualInkHeightForScale);
			float scale = ComputeUniformScale(measuredStd, availW, availH, heightMetric, maxScale);

			string drawText = raw;
			if (!FitsAtScale(measuredStd, scale, availW, availH, heightMetric))
			{
				drawText = FitEllipsisForScale(raw, availW, scale);
				measuredStd = MeasureAtStandard(drawText);
				heightMetric = heightMetricOverride > 0.01f
					? heightMetricOverride
					: ResolveHeightMetricForScale(font, drawText, measuredStd.Y, useVisualInkHeightForScale);
				scale = ComputeUniformScale(measuredStd, availW, availH, heightMetric, maxScale);
			}

			Vector2 drawSize = ScaledDrawSize(measuredStd, scale);
			float x = ComputeDrawX(cell, drawSize.X, align, padX);
			float y = ComputeDrawYVisual(cell, font, drawText, scale, measuredStd.Y);
			Vector2 pos = new Vector2(x, y);

			ChatManager.DrawColorCodedStringWithShadow(
				sb,
				font,
				drawText,
				pos,
				color,
				0f,
				Vector2.Zero,
				new Vector2(scale));
		}

		/// <summary>
		/// Height used only for uniform scale vs availableHeight.
		/// Body: visual ink height. Header/title: LineSpacing box (measuredY).
		/// </summary>
		private static float ResolveHeightMetricForScale(
			DynamicSpriteFont font,
			string text,
			float measuredLineSpacingY,
			bool useVisualInkHeight)
		{
			if (!useVisualInkHeight)
				return measuredLineSpacingY;
			if (TryGetVisualInkRange(font, text, out float inkTop, out float inkBottom))
			{
				float inkH = inkBottom - inkTop;
				if (inkH > 0.01f)
					return inkH;
			}

			return measuredLineSpacingY;
		}
	}
}
