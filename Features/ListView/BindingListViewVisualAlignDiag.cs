using System;
using System.IO;
using System.Text;
using Microsoft.Xna.Framework;
using ReLogic.Graphics;
using Terraria.GameContent;
using Terraria.Localization;

namespace AdvancedKeybinds.Features.ListView
{
	/// <summary>
	/// TEMP HeavyDiag: sample visual vs math vertical centers for ListView TextFit.
	/// Output: deck log\Keyboard\BindingList_VisualAlignDiag.tsv
	/// </summary>
	internal static class BindingListViewVisualAlignDiag
	{
		private static bool _ran;

		private static readonly string[] SampleTexts =
		{
			"B+5",
			"CapsLock",
			"Q+0",
			"L-Shift",
			"キー",
			"動作",
			"MOD",
			"Key",
			"Action",
			"键",
			"动作",
			"模组",
			"Клавиша",
			"Действие",
		};

		public static void TryRunOnce(BindingListViewState state)
		{
			if (!BindingListViewLiveDiagGate.HeavyDiagEnabled)
				return;
			if (_ran || state == null || !state.IsOpen)
				return;
			_ran = true;

			try
			{
				Run(state);
			}
			catch
			{
				// never break draw
			}
		}

		private static void Run(BindingListViewState state)
		{
			string dir = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
				"My Games", "Terraria", "tModLoader", "deck log", "Keyboard");
			Directory.CreateDirectory(dir);
			string path = Path.Combine(dir, "BindingList_VisualAlignDiag.tsv");

			DynamicSpriteFont font = FontAssets.MouseText.Value;
			int lineSpacing = font.LineSpacing;

			var sb = new StringBuilder(4096);
			sb.AppendLine(
				"Sample\tCellY\tCellH\tCellCY\tMeasH\tLineSpacing\tFinalScale\t"
				+ "InkTop\tInkBottom\tInkH\tVisualCenterAt1\tMathCenterAt1\tBiasY_at1\t"
				+ "DrawY_math\tDrawY_visual\tCalcVisualCY\tDeltaVisualToCellCY");

			// Representative cell sizes matching Renderer (header strip 18 / rowInner ≈ RowHeight-4).
			AppendSamples(sb, font, lineSpacing, cellH: 18, labelPrefix: "Hdr18");
			AppendSamples(sb, font, lineSpacing, cellH: 24, labelPrefix: "Row24");
			AppendSamples(sb, font, lineSpacing, cellH: 20, labelPrefix: "Empty20");

			sb.AppendLine();
			sb.AppendLine("# BiasY_at1 = MathCenterAt1 - VisualCenterAt1 (= LineSpacing/2 - (InkTop+InkBottom)/2)");
			sb.AppendLine("# Positive Bias → math center below ink → math-centered draw looks top-biased; visual DrawY shifts down by Bias*scale.");
			sb.AppendLine("# LineSpacing=" + lineSpacing);
			sb.AppendLine("# ActiveCulture=" + (Language.ActiveCulture != null ? Language.ActiveCulture.Name : "?"));
			File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
		}

		private static void AppendSamples(StringBuilder sb, DynamicSpriteFont font, int lineSpacing, int cellH, string labelPrefix)
		{
			var cell = new Rectangle(0, 100, 200, cellH);
			float cellCy = cell.Y + cell.Height * 0.5f;

			for (int i = 0; i < SampleTexts.Length; i++)
			{
				string text = SampleTexts[i];
				Vector2 meas = BindingListViewTextFit.MeasureAtStandard(text);
				float availW = BindingListViewTextFit.AvailableWidth(cell.Width, 0, BindingListViewTextAlign.Center);
				float availH = BindingListViewTextFit.AvailableHeight(cell.Height);
				float scale = BindingListViewTextFit.ComputeUniformScale(meas, availW, availH);
				Vector2 drawSize = BindingListViewTextFit.ScaledDrawSize(meas, scale);

				bool hasInk = BindingListViewTextFit.TryGetVisualInkRange(font, text, out float inkTop, out float inkBottom);
				float visualCenterAt1 = hasInk ? (inkTop + inkBottom) * 0.5f : lineSpacing * 0.5f;
				float mathCenterAt1 = lineSpacing * 0.5f;
				float biasAt1 = mathCenterAt1 - visualCenterAt1;

				float drawYMath = cell.Y + (cell.Height - drawSize.Y) * 0.5f;
				float drawYVisual = BindingListViewTextFit.ComputeDrawYVisual(cell, font, text, scale, meas.Y);
				float calcVisualCy = hasInk
					? drawYVisual + visualCenterAt1 * scale
					: drawYVisual + drawSize.Y * 0.5f;

				sb.Append(labelPrefix).Append(':').Append(text.Replace('\t', ' ')).Append('\t')
					.Append(cell.Y).Append('\t').Append(cell.Height).Append('\t').Append(cellCy.ToString("0.##")).Append('\t')
					.Append(meas.Y.ToString("0.##")).Append('\t').Append(lineSpacing).Append('\t').Append(scale.ToString("0.###")).Append('\t')
					.Append(hasInk ? inkTop.ToString("0.##") : "n/a").Append('\t')
					.Append(hasInk ? inkBottom.ToString("0.##") : "n/a").Append('\t')
					.Append(hasInk ? (inkBottom - inkTop).ToString("0.##") : "n/a").Append('\t')
					.Append(visualCenterAt1.ToString("0.##")).Append('\t')
					.Append(mathCenterAt1.ToString("0.##")).Append('\t')
					.Append(biasAt1.ToString("0.##")).Append('\t')
					.Append(drawYMath.ToString("0.##")).Append('\t')
					.Append(drawYVisual.ToString("0.##")).Append('\t')
					.Append(calcVisualCy.ToString("0.##")).Append('\t')
					.Append((calcVisualCy - cellCy).ToString("0.##"))
					.AppendLine();
			}
		}
	}
}
