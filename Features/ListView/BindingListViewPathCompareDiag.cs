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
	/// TEMP investigation: compare Title / KeyCap / Action / Header / Close glyph draw inputs.
	/// One-shot (not CultureSweep). Does not change Baseline draw.
	/// Output: deck log\Keyboard\BindingList_PathCompareDiag.tsv
	/// Gate: <see cref="BindingListViewLiveDiagGate.PathCompareEnabled"/> (default OFF).
	/// </summary>
	internal static class BindingListViewPathCompareDiag
	{
		private static bool _ran;

		public static void TryRunOnce(BindingListViewState state)
		{
			// Intentionally NOT HeavyDiag — one small dump for path A/B compare.
			if (!BindingListViewLiveDiagGate.PathCompareEnabled)
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
			string path = Path.Combine(dir, "BindingList_PathCompareDiag.tsv");

			DynamicSpriteFont font = FontAssets.MouseText.Value;
			var sb = new StringBuilder(8192);
			sb.AppendLine(
				"Tag\tRole\tText\tDrawMethod\tAlign\tPadX\t"
				+ "CellX\tCellY\tCellW\tCellH\tCellCY\t"
				+ "FontLineSpacing\tMeasW\tMeasH\tAvailW\tAvailH\tFinalScale\tScaledW\tScaledH_LineBox\t"
				+ "InkTop\tInkBottom\tInkH\tVisualCenterAt1\tMathCenterAt1\tBiasY_at1\t"
				+ "DrawX\tDrawY_math\tDrawY_visual\tOriginX\tOriginY\t"
				+ "CalcMathCY\tCalcVisualCY\tDeltaMathToCell\tDeltaVisualToCell\t"
				+ "ClampWouldPushUp\tNotes");

			Rectangle titleBar = BindingListViewLayout.TitleBarRect(state);
			Rectangle titleLabel = BindingListViewLayout.TitleLabelRect(state);
			Rectangle closeBtn = BindingListViewLayout.CloseButtonRect(state);
			Rectangle body = BindingListViewLayout.BodyRect(state);
			var headerStrip = new Rectangle(
				body.X + 8,
				body.Y + 6,
				Math.Max(40, body.Width - 16 - BindingListViewLayout.ScrollbarWidth - 4),
				18);

			string titleText = Language.GetTextValue("Mods.AdvancedKeybinds.UI.BindingList.Title");
			if (string.IsNullOrEmpty(titleText) || titleText.StartsWith("Mods.AdvancedKeybinds", StringComparison.Ordinal))
				titleText = "AK ALL KEY";

			Append(sb, "A_Title", "up_biased_reference", titleText, titleLabel, BindingListViewTextAlign.Left, 0,
				"DrawTitleLabel→DrawTextClipped→DrawFittedText",
				"TitleLabelRect H=TitleBarHeightUi; same ChatManager path");

			Append(sb, "A_TitleBar", "context_only", titleText, titleBar, BindingListViewTextAlign.Left, 0,
				"(not drawn into TitleBarRect directly)",
				"TitleBarRect vs TitleLabelRect — label uses full title height");

			Append(sb, "B_CloseX", "same_path_smaller_cell", "x", closeBtn, BindingListViewTextAlign.Center, 0,
				"DrawCloseButton→DrawCenteredGlyph→DrawFittedText",
				"Same TextFit; cell=22 centered in title bar");

			string keyHdr = Language.GetTextValue("Mods.AdvancedKeybinds.UI.BindingList.ColKey");
			Append(sb, "Hdr_Key", "header_h18", string.IsNullOrEmpty(keyHdr) ? "Key" : keyHdr,
				new Rectangle(headerStrip.X, headerStrip.Y, Math.Max(40, state.ColKeyWidthUi - 4), headerStrip.Height),
				BindingListViewTextAlign.Left, 6,
				"DrawColumnHeaders→DrawTextClipped→DrawFittedText",
				"Header strip height fixed 18");

			Rectangle modBtn = BindingListViewRenderer.ModHeaderButtonRect(state);
			if (!modBtn.IsEmpty)
			{
				string modLabel = Language.GetTextValue("Mods.AdvancedKeybinds.UI.BindingList.ColMod");
				if (string.IsNullOrEmpty(modLabel) || modLabel.StartsWith("Mods.AdvancedKeybinds", StringComparison.Ordinal))
					modLabel = "MOD";
				Append(sb, "Hdr_MOD", "header_mod_chrome", modLabel, modBtn, BindingListViewTextAlign.Center, 0,
					"DrawColumnHeaders→DrawTextClipped→DrawFittedText",
					"Small chrome rect; Center align");
			}

			// Synthetic key / action cells matching DrawRowsClipped geometry.
			int rowInnerH = 28 - 4; // RowHeight - 4
			var keyCell = new Rectangle(headerStrip.X, headerStrip.Y + 40, Math.Max(20, state.ColKeyWidthUi - 6), rowInnerH);
			var actionCell = new Rectangle(keyCell.Right + 6, keyCell.Y, Math.Max(20, state.ColActionWidthUi - 4), rowInnerH);

			Append(sb, "Key_B5", "body_ink_height_scale", "B+5", keyCell, BindingListViewTextAlign.Left, 6,
				"DrawKeyCap→DrawFittedText(useVisualInkHeightForScale)",
				"Body Key; expect FinalScale≈1 when width/ink fit (was ~0.69 LineSpacing-capped)",
				useInkHeightScale: true);

			Append(sb, "Key_Caps", "body_ink_height_scale", "CapsLock", keyCell, BindingListViewTextAlign.Left, 6,
				"DrawKeyCap→DrawFittedText(useVisualInkHeightForScale)",
				"Same body path as B+5",
				useInkHeightScale: true);

			Append(sb, "Act_Sample", "body_ink_height_scale", "Cycle Hold HUD", actionCell, BindingListViewTextAlign.Left, 2,
				"DrawBodyTextClipped→DrawFittedText(useVisualInkHeightForScale)",
				"Short-ish Action; should share StandardScale with Key when width allows",
				useInkHeightScale: true);

			Append(sb, "Mod_Sample", "body_ink_height_scale", "AdvancedKeybinds", actionCell, BindingListViewTextAlign.Left, 2,
				"DrawBodyTextClipped→DrawFittedText(useVisualInkHeightForScale)",
				"MOD body same scale path",
				useInkHeightScale: true);

			Append(sb, "Act_LongRu", "body_width_shrink", "Переключить окно избранных рецептов", actionCell, BindingListViewTextAlign.Left, 2,
				"DrawBodyTextClipped→DrawFittedText(useVisualInkHeightForScale)",
				"Long RU Action — expect FinalScale < 1 from width only",
				useInkHeightScale: true);

			Append(sb, "Close_x", "header_like_lineSpacing_scale", "x", closeBtn, BindingListViewTextAlign.Center, 0,
				"DrawCloseButton→DrawCenteredGlyph→DrawFittedText",
				"Non-body; LineSpacing height metric (hierarchy)");

			// Glyph metrics for letters in title / keys.
			sb.AppendLine();
			sb.AppendLine("# GlyphMetrics (MouseText) Padding.Y / Glyph.H for sample chars");
			sb.AppendLine("Char\tPaddingX\tPaddingY\tPaddingW\tPaddingH\tGlyphW\tGlyphH\tInkTop\tInkBottom\tInkMid");
			DumpChar(sb, font, 'A');
			DumpChar(sb, font, 'K');
			DumpChar(sb, font, ' ');
			DumpChar(sb, font, 'L');
			DumpChar(sb, font, 'B');
			DumpChar(sb, font, '5');
			DumpChar(sb, font, 'x');
			DumpChar(sb, font, 'p');
			DumpChar(sb, font, 'g');
			DumpChar(sb, font, '中');
			DumpChar(sb, font, 'К');

			sb.AppendLine();
			sb.AppendLine("# SPACE_GLYPH_ANOMALY");
			DumpChar(sb, font, ' ');
			sb.AppendLine("# Space Padding.Y is typically huge (~40) with Glyph.H=1.");
			sb.AppendLine("# TryGetVisualInkRange now excludes whitespace from ink Top/Bottom (vertical only).");

			sb.AppendLine();
			sb.AppendLine("# THREE_WAY_VERDICT");
			WriteThreeWay(sb, font, titleText, titleLabel, "B+5", keyCell, "AdvancedKeybinds", actionCell);

			sb.AppendLine();
			sb.AppendLine("# CONCLUSION_HINTS");
			sb.AppendLine("# Path search: ListView content uses ONLY DrawFittedText→ChatManager (Origin Zero). No SpriteBatch.DrawString for rows/title.");
			sb.AppendLine("# MeasureString appears only for MOD header WIDTH chrome (ModHeaderButtonRect), not vertical draw.");
			sb.AppendLine("# HitDiag/ResizeCursor use ChatManager directly for overlays — not AK ALL KEY content.");
			sb.AppendLine("# ActiveCulture=" + (Language.ActiveCulture != null ? Language.ActiveCulture.Name : "?"));
			sb.AppendLine("# LineSpacing=" + font.LineSpacing);

			File.WriteAllText(path, sb.ToString(), Encoding.UTF8);

			// Companion LAST summary for this investigation.
			string lastPath = Path.Combine(dir, "BindingList_VertPathCompare_LAST.txt");
			File.WriteAllText(lastPath, BuildLastSummary(font, titleText, titleLabel, "B+5", keyCell, "AdvancedKeybinds", actionCell), Encoding.UTF8);
		}

		private static void WriteThreeWay(
			StringBuilder sb,
			DynamicSpriteFont font,
			string titleText,
			Rectangle titleCell,
			string keyText,
			Rectangle keyCell,
			string centeredText,
			Rectangle centeredCell)
		{
			sb.AppendLine("Role\tText\tHasSpace\tInkAll_T/B/Mid\tInkNoWs_T/B/Mid\tLetterMidShiftPx\tVerdictHint");
			AppendThree(sb, font, "UP_Title", titleText, titleCell);
			AppendThree(sb, font, "UP_KeyCap", keyText, keyCell);
			AppendThree(sb, font, "CENTERISH_ModCol", centeredText, centeredCell);
		}

		private static void AppendThree(StringBuilder sb, DynamicSpriteFont font, string role, string text, Rectangle cell)
		{
			bool hasAll = BindingListViewTextFit.TryGetVisualInkRange(font, text, out float tAll, out float bAll);
			bool hasNo = TryGetVisualInkRangeExcludingWhitespace(font, text, out float tNo, out float bNo);
			float midAll = hasAll ? (tAll + bAll) * 0.5f : -1f;
			float midNo = hasNo ? (tNo + bNo) * 0.5f : -1f;
			float availH = BindingListViewTextFit.AvailableHeight(cell.Height);
			Vector2 meas = BindingListViewTextFit.MeasureAtStandard(text);
			float scale = BindingListViewTextFit.ComputeUniformScale(
				meas,
				BindingListViewTextFit.AvailableWidth(cell.Width, 0, BindingListViewTextAlign.Left),
				availH);
			float letterShift = (hasAll && hasNo) ? (midAll - midNo) * scale : 0f;
			bool hasSpace = text != null && text.IndexOf(' ') >= 0;
			string hint = hasSpace && letterShift > 1f
				? "SPACE_inflates_VisualCenter→DrawY_too_high_for_letters"
				: (!hasSpace ? "no_space_metric_center_OK_if_Delta0_still_top=glyph_pad_or_frame_optics" : "check");
			sb.Append(role).Append('\t').Append(text.Replace('\t', ' ')).Append('\t').Append(hasSpace).Append('\t');
			if (hasAll)
				sb.Append(tAll.ToString("0.##")).Append('/').Append(bAll.ToString("0.##")).Append('/').Append(midAll.ToString("0.##"));
			else
				sb.Append("n/a");
			sb.Append('\t');
			if (hasNo)
				sb.Append(tNo.ToString("0.##")).Append('/').Append(bNo.ToString("0.##")).Append('/').Append(midNo.ToString("0.##"));
			else
				sb.Append("n/a");
			sb.Append('\t').Append(letterShift.ToString("0.##")).Append('\t').Append(hint).AppendLine();
		}

		private static string BuildLastSummary(
			DynamicSpriteFont font,
			string titleText,
			Rectangle titleCell,
			string keyText,
			Rectangle keyCell,
			string centeredText,
			Rectangle centeredCell)
		{
			var sb = new StringBuilder(4096);
			sb.AppendLine("=== Vert path compare (investigation only; no draw fix) ===");
			sb.AppendLine("Font=MouseText LineSpacing=" + font.LineSpacing);
			sb.AppendLine("Content path: DrawTextClipped/DrawCenteredGlyph → DrawFittedText → ChatManager.DrawColorCodedStringWithShadow Origin=(0,0)");
			sb.AppendLine();
			AppendDetailBlock(sb, font, "1_TITLE_UP", titleText, titleCell, BindingListViewTextAlign.Left, 0);
			AppendDetailBlock(sb, font, "2_KEYCAP_UP", keyText, keyCell, BindingListViewTextAlign.Left, 6);
			AppendDetailBlock(sb, font, "3_CENTERISH", centeredText, centeredCell, BindingListViewTextAlign.Left, 2);
			sb.AppendLine();
			sb.AppendLine("CLASSIFICATION:");
			sb.AppendLine("A DrawMethod different? NO — all DrawFittedText/ChatManager");
			sb.AppendLine("B DrawFittedText pass/skip? NO — all pass");
			sb.AppendLine("C VisualCenter pass/skip? NO — all ComputeDrawYVisual");
			sb.AppendLine("D FinalScale Height formula different? NO — ScaledDrawSize=meas*scale; scale differs by cell H only");
			sb.AppendLine("E Rectangle Center different? Different cells, each centers to own CellCY");
			sb.AppendLine("F Origin different? NO — all Vector2.Zero");
			sb.AppendLine("G FIXED: TryGetVisualInkRange excludes whitespace from ink Top/Bottom (space no longer inflates VisualCenter).");
			sb.AppendLine("G REMAINING (separate): short no-space keycaps (B+5) may still look slightly high — metric Delta≈0; no Y offset in this fix.");
			sb.AppendLine();
			sb.AppendLine("VERIFY_TITLE_WHITESPACE_FIX:");
			AppendDetailBlock(sb, font, "VERIFY_Title", titleText, titleCell, BindingListViewTextAlign.Left, 0);
			return sb.ToString();
		}

		private static void AppendDetailBlock(
			StringBuilder sb,
			DynamicSpriteFont font,
			string tag,
			string text,
			Rectangle cell,
			BindingListViewTextAlign align,
			int padX)
		{
			float cellCy = cell.Y + cell.Height * 0.5f;
			Vector2 meas = BindingListViewTextFit.MeasureAtStandard(text);
			float availW = BindingListViewTextFit.AvailableWidth(cell.Width, padX, align);
			float availH = BindingListViewTextFit.AvailableHeight(cell.Height);
			float scale = BindingListViewTextFit.ComputeUniformScale(meas, availW, availH);
			Vector2 drawSize = BindingListViewTextFit.ScaledDrawSize(meas, scale);
			bool hasAll = BindingListViewTextFit.TryGetVisualInkRange(font, text, out float tAll, out float bAll);
			bool hasNo = TryGetVisualInkRangeExcludingWhitespace(font, text, out float tNo, out float bNo);
			float midAll = hasAll ? (tAll + bAll) * 0.5f : font.LineSpacing * 0.5f;
			float midNo = hasNo ? (tNo + bNo) * 0.5f : midAll;
			float drawY = BindingListViewTextFit.ComputeDrawYVisual(cell, font, text, scale, meas.Y);
			float visualCyUsed = drawY + midAll * scale;
			float letterCyIfNoWs = drawY + midNo * scale;
			sb.AppendLine("--- " + tag + " text=[" + text + "] ---");
			sb.AppendLine("TargetRect=" + cell.X + "," + cell.Y + " " + cell.Width + "x" + cell.Height
				+ " CenterY=" + cellCy.ToString("0.##"));
			sb.AppendLine("Font=MouseText Method=DrawFittedText→ChatManager.DrawColorCodedStringWithShadow");
			sb.AppendLine("MeasuredH(LineSpacing box)=" + meas.Y.ToString("0.##")
				+ " LineSpacing=" + font.LineSpacing
				+ " FinalScale=" + scale.ToString("0.###")
				+ " ScaledH=" + drawSize.Y.ToString("0.##"));
			sb.AppendLine("GlyphVisual(all chars) Top/Bottom/Center@1="
				+ (hasAll ? tAll.ToString("0.##") + "/" + bAll.ToString("0.##") + "/" + midAll.ToString("0.##") : "n/a"));
			sb.AppendLine("GlyphVisual(no whitespace) Top/Bottom/Center@1="
				+ (hasNo ? tNo.ToString("0.##") + "/" + bNo.ToString("0.##") + "/" + midNo.ToString("0.##") : "n/a"));
			sb.AppendLine("OriginY=0 DrawY=" + drawY.ToString("0.##")
				+ " FinalVisualCenterY(using_all)=" + visualCyUsed.ToString("0.##")
				+ " LetterInkCenterY(at_same_DrawY)=" + letterCyIfNoWs.ToString("0.##")
				+ " LetterDeltaToCellCY=" + (letterCyIfNoWs - cellCy).ToString("0.##")
				+ " MatchCellCY=" + (Math.Abs(visualCyUsed - cellCy) < 0.05f && Math.Abs(letterCyIfNoWs - cellCy) < 0.05f));
			sb.AppendLine();
		}

		/// <summary>Diag-only: same as TextFit ink range but skips whitespace (does not change draw).</summary>
		private static bool TryGetVisualInkRangeExcludingWhitespace(
			DynamicSpriteFont font,
			string text,
			out float inkTop,
			out float inkBottom)
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

		private static void Append(
			StringBuilder sb,
			string tag,
			string role,
			string text,
			Rectangle cell,
			BindingListViewTextAlign align,
			int padX,
			string drawMethod,
			string notes,
			bool useInkHeightScale = false)
		{
			DynamicSpriteFont font = FontAssets.MouseText.Value;
			float cellCy = cell.Y + cell.Height * 0.5f;
			Vector2 meas = BindingListViewTextFit.MeasureAtStandard(text ?? string.Empty);
			float availW = BindingListViewTextFit.AvailableWidth(cell.Width, padX, align);
			float availH = useInkHeightScale
				? Math.Max(1f, cell.Height - 2f)
				: BindingListViewTextFit.AvailableHeight(cell.Height);
			bool hasInk = BindingListViewTextFit.TryGetVisualInkRange(font, text, out float inkTop, out float inkBottom);
			float heightMetric = meas.Y;
			if (useInkHeightScale && hasInk)
			{
				float inkH = inkBottom - inkTop;
				if (inkH > 0.01f)
					heightMetric = inkH;
			}

			float scale = BindingListViewTextFit.ComputeUniformScale(meas, availW, availH, heightMetric);
			Vector2 drawSize = BindingListViewTextFit.ScaledDrawSize(meas, scale);

			float visualCenterAt1 = hasInk ? (inkTop + inkBottom) * 0.5f : font.LineSpacing * 0.5f;
			float mathCenterAt1 = font.LineSpacing * 0.5f;
			float biasAt1 = mathCenterAt1 - visualCenterAt1;

			float drawX = BindingListViewTextFit.ComputeDrawX(cell, drawSize.X, align, padX);
			float drawYMath = cell.Y + (cell.Height - drawSize.Y) * 0.5f;
			float drawYVisual = BindingListViewTextFit.ComputeDrawYVisual(cell, font, text, scale, meas.Y);

			float calcMathCy = drawYMath + drawSize.Y * 0.5f;
			float calcVisualCy = drawYVisual + visualCenterAt1 * scale;

			bool clampPushUp = false;
			if (hasInk)
			{
				float unclamped = cellCy - visualCenterAt1 * scale;
				float unclampedBottom = unclamped + inkBottom * scale;
				clampPushUp = unclampedBottom > cell.Bottom + 0.05f && (inkBottom - inkTop) * scale <= cell.Height + 0.05f;
			}

			sb.Append(tag).Append('\t').Append(role).Append('\t')
				.Append((text ?? string.Empty).Replace('\t', ' ')).Append('\t')
				.Append(drawMethod.Replace('\t', ' ')).Append('\t')
				.Append(align).Append('\t').Append(padX).Append('\t')
				.Append(cell.X).Append('\t').Append(cell.Y).Append('\t').Append(cell.Width).Append('\t').Append(cell.Height).Append('\t')
				.Append(cellCy.ToString("0.##")).Append('\t')
				.Append(font.LineSpacing).Append('\t')
				.Append(meas.X.ToString("0.##")).Append('\t').Append(meas.Y.ToString("0.##")).Append('\t')
				.Append(availW.ToString("0.##")).Append('\t').Append(availH.ToString("0.##")).Append('\t')
				.Append(scale.ToString("0.###")).Append('\t')
				.Append(drawSize.X.ToString("0.##")).Append('\t').Append(drawSize.Y.ToString("0.##")).Append('\t')
				.Append(hasInk ? inkTop.ToString("0.##") : "n/a").Append('\t')
				.Append(hasInk ? inkBottom.ToString("0.##") : "n/a").Append('\t')
				.Append(hasInk ? (inkBottom - inkTop).ToString("0.##") : "n/a").Append('\t')
				.Append(visualCenterAt1.ToString("0.##")).Append('\t')
				.Append(mathCenterAt1.ToString("0.##")).Append('\t')
				.Append(biasAt1.ToString("0.##")).Append('\t')
				.Append(drawX.ToString("0.##")).Append('\t')
				.Append(drawYMath.ToString("0.##")).Append('\t')
				.Append(drawYVisual.ToString("0.##")).Append('\t')
				.Append("0").Append('\t').Append("0").Append('\t')
				.Append(calcMathCy.ToString("0.##")).Append('\t')
				.Append(calcVisualCy.ToString("0.##")).Append('\t')
				.Append((calcMathCy - cellCy).ToString("0.##")).Append('\t')
				.Append((calcVisualCy - cellCy).ToString("0.##")).Append('\t')
				.Append(clampPushUp).Append('\t')
				.Append("inkHMetric=").Append(heightMetric.ToString("0.##")).Append(' ')
				.Append(notes.Replace('\t', ' '))
				.AppendLine();
		}

		private static void DumpChar(StringBuilder sb, DynamicSpriteFont font, char c)
		{
			DynamicSpriteFont.SpriteCharacterData data;
			if (font.SpriteCharacters != null && font.SpriteCharacters.TryGetValue(c, out data))
			{
				// use data
			}
			else
				data = font.DefaultCharacterData;

			float inkTop = data.Padding.Y;
			float inkBottom = data.Padding.Y + data.Glyph.Height;
			sb.Append(c).Append('\t')
				.Append(data.Padding.X).Append('\t').Append(data.Padding.Y).Append('\t')
				.Append(data.Padding.Width).Append('\t').Append(data.Padding.Height).Append('\t')
				.Append(data.Glyph.Width).Append('\t').Append(data.Glyph.Height).Append('\t')
				.Append(inkTop.ToString("0.##")).Append('\t').Append(inkBottom.ToString("0.##")).Append('\t')
				.Append(((inkTop + inkBottom) * 0.5f).ToString("0.##"))
				.AppendLine();
		}
	}
}
