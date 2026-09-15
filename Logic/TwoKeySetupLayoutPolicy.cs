using System;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Fit four centered explanation lines into a panel with one shared scale.
	/// Measurement widths/heights are injected (no Terraria font dependency).
	/// </summary>
	public static class TwoKeySetupLayoutPolicy
	{
		public const float MinScale = 0.35f;
		public const float MaxScale = 1.75f;
		public const float DefaultPaddingFraction = 0.08f;

		public const string JapaneseLine1 = "2 キー 設定";

		public readonly struct LineMetrics
		{
			public readonly float WidthAtUnitScale;
			public readonly float HeightAtUnitScale;

			public LineMetrics(float widthAtUnitScale, float heightAtUnitScale)
			{
				WidthAtUnitScale = widthAtUnitScale;
				HeightAtUnitScale = heightAtUnitScale;
			}
		}

		public readonly struct LayoutResult
		{
			public readonly float Scale;
			public readonly float TotalHeight;
			public readonly float MaxLineWidth;
			public readonly bool Fits;
			public readonly bool Focusable;

			public LayoutResult(
				float scale,
				float totalHeight,
				float maxLineWidth,
				bool fits)
			{
				Scale = scale;
				TotalHeight = totalHeight;
				MaxLineWidth = maxLineWidth;
				Fits = fits;
				Focusable = false;
			}
		}

		public static LayoutResult Compute(
			float panelWidth,
			float panelHeight,
			LineMetrics line1,
			LineMetrics line2,
			LineMetrics line3,
			LineMetrics line4,
			float paddingFraction = DefaultPaddingFraction,
			float lineGapAtUnitScale = 2f)
		{
			if (panelWidth <= 0f || panelHeight <= 0f
				|| float.IsNaN(panelWidth) || float.IsNaN(panelHeight)
				|| float.IsInfinity(panelWidth) || float.IsInfinity(panelHeight))
			{
				return new LayoutResult(MinScale, 0f, 0f, fits: false);
			}

			float padX = panelWidth * Clamp01(paddingFraction);
			float padY = panelHeight * Clamp01(paddingFraction);
			float innerW = Math.Max(1f, panelWidth - padX * 2f);
			float innerH = Math.Max(1f, panelHeight - padY * 2f);

			float maxW = Max4(
				Safe(line1.WidthAtUnitScale),
				Safe(line2.WidthAtUnitScale),
				Safe(line3.WidthAtUnitScale),
				Safe(line4.WidthAtUnitScale));
			float h1 = Safe(line1.HeightAtUnitScale);
			float h2 = Safe(line2.HeightAtUnitScale);
			float h3 = Safe(line3.HeightAtUnitScale);
			float h4 = Safe(line4.HeightAtUnitScale);
			int nonEmpty = 0;
			if (h1 > 0f)
				nonEmpty++;
			if (h2 > 0f)
				nonEmpty++;
			if (h3 > 0f)
				nonEmpty++;
			if (h4 > 0f)
				nonEmpty++;
			float gapCount = nonEmpty > 1 ? nonEmpty - 1 : 0;
			float sumH = h1 + h2 + h3 + h4 + Safe(lineGapAtUnitScale) * gapCount;

			if (maxW <= 0f)
				maxW = 1f;
			if (sumH <= 0f)
				sumH = 1f;

			float scaleW = innerW / maxW;
			float scaleH = innerH / sumH;
			float scale = Math.Min(scaleW, scaleH);
			if (float.IsNaN(scale) || float.IsInfinity(scale) || scale <= 0f)
				scale = MinScale;
			scale = Math.Clamp(scale, MinScale, MaxScale);

			float totalH = sumH * scale;
			float maxLine = maxW * scale;
			bool fits = maxLine <= innerW + 0.01f && totalH <= innerH + 0.01f;
			return new LayoutResult(scale, totalH, maxLine, fits);
		}

		public static bool IsJapaneseLine1Valid(string line1)
			=> string.Equals(line1, JapaneseLine1, StringComparison.Ordinal);

		public static bool IsExplanationFocusable => false;

		private static float Safe(float v)
		{
			if (float.IsNaN(v) || float.IsInfinity(v) || v < 0f)
				return 0f;
			return v;
		}

		private static float Clamp01(float v)
		{
			if (float.IsNaN(v) || float.IsInfinity(v) || v < 0f)
				return 0f;
			if (v > 0.4f)
				return 0.4f;
			return v;
		}

		private static float Max4(float a, float b, float c, float d)
			=> Math.Max(Math.Max(a, b), Math.Max(c, d));
	}
}
