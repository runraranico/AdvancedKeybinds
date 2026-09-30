using System;
using System.IO;
using System.Text;
using Microsoft.Xna.Framework;
using Terraria.Localization;

namespace AdvancedKeybinds.Features.ListView
{
	/// <summary>
	/// TEMP: header rect vs text-center comparison across cultures. Diagnosis only.
	/// Output: deck log\Keyboard\BindingList_HeaderPosDiag.tsv
	/// </summary>
	internal static class BindingListViewLayoutRectDiag
	{
		private static bool _ran;

		public static void TryRunOnce(BindingListViewState state)
		{
			if (!BindingListViewLiveDiagGate.HeavyDiagEnabled)
				return;
			if (_ran || state == null || !state.IsOpen || !state.IsMaximized)
				return;
			_ran = true;

			BindingListViewOpenPerfDiag.OnLayoutRectDiagStart();
			try
			{
				Run(state);
			}
			catch
			{
				// never break draw
			}
			finally
			{
				BindingListViewOpenPerfDiag.OnLayoutRectDiagEnd();
			}
		}

		private static void Run(BindingListViewState state)
		{
			string dir = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
				"My Games", "Terraria", "tModLoader", "deck log", "Keyboard");
			Directory.CreateDirectory(dir);
			string path = Path.Combine(dir, "BindingList_HeaderPosDiag.tsv");

			var sb = new StringBuilder();
			sb.AppendLine(
				"Lang\tWinX\tWinY\tWinW\tWinH\tViewX\tViewY\tViewW\tViewH\t"
				+ "KeyW\tActW\tModW\t"
				+ "KeyHdrX\tKeyHdrY\tKeyHdrW\tKeyHdrH\tKeyHdrCX\tKeyHdrCY\t"
				+ "ActHdrX\tActHdrY\tActHdrW\tActHdrH\tActHdrCX\tActHdrCY\t"
				+ "ModHdrX\tModHdrY\tModHdrW\tModHdrH\tModHdrCX\tModHdrCY\t"
				+ "ModLabel\tMeasW\tMeasH\tStdScale\tFinalScale\tOriginX\tOriginY\t"
				+ "DrawX\tDrawY\tTextW\tTextH\tTextCX\tTextCY\t"
				+ "DeltaCX\tDeltaCY\tClassHint");

			GameCulture previous = Language.ActiveCulture;
			try
			{
				foreach (GameCulture culture in GameCulture.KnownCultures)
				{
					if (culture == null)
						continue;
					LanguageManager.Instance.SetLanguage(culture);
					AppendCulture(sb, state, culture.Name ?? "?");
				}
			}
			finally
			{
				try
				{
					if (previous != null)
						LanguageManager.Instance.SetLanguage(previous);
				}
				catch
				{
					// ignore
				}
			}

			sb.AppendLine();
			sb.AppendLine("# CLASSIFICATION_HINTS");
			sb.AppendLine("# Compare ModHdrX/W across ja-JP,en-US,ru-RU,zh-Hans:");
			sb.AppendLine("# A = ModHdr rect moves with language (X/W differ)");
			sb.AppendLine("# B = ModHdr rect same, TextCX/TextCY differ (Delta nonzero differs)");
			sb.AppendLine("# C = DeltaCX/CY ~0 but visual glyph still looks off (metrics/shadow)");
			sb.AppendLine("# D = Scale/Origin/Position inconsistency (Origin always 0 here)");
			File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
		}

		private static void AppendCulture(StringBuilder sb, BindingListViewState state, string lang)
		{
			Rectangle win = BindingListViewLayout.WindowRect(state);
			Rectangle body = BindingListViewLayout.BodyRect(state);
			var view = new Rectangle(
				body.X + 8,
				body.Y + 6,
				Math.Max(40, body.Width - 16 - BindingListViewLayout.ScrollbarWidth - 4),
				18);

			ResolveWidths(state, view.Width, out int keyW, out int actW, out int modW);

			var keyHdr = new Rectangle(view.X, view.Y, Math.Max(1, keyW - 4), view.Height);
			var actHdr = new Rectangle(view.X + keyW, view.Y, Math.Max(1, actW - 4), view.Height);
			Rectangle modHdr = BindingListViewRenderer.ModHeaderButtonRect(state);

			string modLabel = Language.GetTextValue("Mods.AdvancedKeybinds.UI.BindingList.ColMod");
			if (string.IsNullOrEmpty(modLabel) || modLabel.StartsWith("Mods.AdvancedKeybinds", StringComparison.Ordinal))
				modLabel = "MOD";

			const float std = BindingListViewTextFit.StandardScale;
			var font = Terraria.GameContent.FontAssets.MouseText.Value;
			Vector2 meas = BindingListViewTextFit.MeasureAtStandard(modLabel);
			float availW = BindingListViewTextFit.AvailableWidth(modHdr.Width, 0, BindingListViewTextAlign.Center);
			float availH = BindingListViewTextFit.AvailableHeight(modHdr.Height);
			float scale = BindingListViewTextFit.ComputeUniformScale(meas, availW, availH);
			Vector2 drawSize = BindingListViewTextFit.ScaledDrawSize(meas, scale);
			float drawX = BindingListViewTextFit.ComputeDrawX(modHdr, drawSize.X, BindingListViewTextAlign.Center, 0);
			float drawY = BindingListViewTextFit.ComputeDrawYVisual(modHdr, font, modLabel, scale, meas.Y);
			Vector2 drawPos = new Vector2(drawX, drawY);
			Vector2 origin = Vector2.Zero; // ChatManager path uses origin Zero

			float modCx = modHdr.X + modHdr.Width * 0.5f;
			float modCy = modHdr.Y + modHdr.Height * 0.5f;
			float textCx = drawPos.X + drawSize.X * 0.5f;
			// Visual ink center (not LineSpacing box center).
			bool hasInk = BindingListViewTextFit.TryGetVisualInkRange(font, modLabel, out float inkTop, out float inkBottom);
			float textCy = hasInk
				? drawPos.Y + (inkTop + inkBottom) * 0.5f * scale
				: drawPos.Y + drawSize.Y * 0.5f;
			float dCx = textCx - modCx;
			float dCy = textCy - modCy;

			string hint = Math.Abs(dCx) < 0.51f && Math.Abs(dCy) < 0.51f
				? "VisualInkCenterOK"
				: "VisualInkCenter_offset_from_HdrCenter";

			sb.Append(lang).Append('\t')
				.Append(win.X).Append('\t').Append(win.Y).Append('\t').Append(win.Width).Append('\t').Append(win.Height).Append('\t')
				.Append(view.X).Append('\t').Append(view.Y).Append('\t').Append(view.Width).Append('\t').Append(view.Height).Append('\t')
				.Append(keyW).Append('\t').Append(actW).Append('\t').Append(modW).Append('\t')
				.AppendRect(keyHdr)
				.AppendRect(actHdr)
				.AppendRect(modHdr)
				.Append(modLabel.Replace('\t', ' ')).Append('\t')
				.Append(meas.X.ToString("0.##")).Append('\t').Append(meas.Y.ToString("0.##")).Append('\t')
				.Append(std.ToString("0.##")).Append('\t').Append(scale.ToString("0.###")).Append('\t')
				.Append(origin.X.ToString("0.##")).Append('\t').Append(origin.Y.ToString("0.##")).Append('\t')
				.Append(drawPos.X.ToString("0.##")).Append('\t').Append(drawPos.Y.ToString("0.##")).Append('\t')
				.Append(drawSize.X.ToString("0.##")).Append('\t').Append(drawSize.Y.ToString("0.##")).Append('\t')
				.Append(textCx.ToString("0.##")).Append('\t').Append(textCy.ToString("0.##")).Append('\t')
				.Append(dCx.ToString("0.##")).Append('\t').Append(dCy.ToString("0.##")).Append('\t')
				.Append(hint).AppendLine();
		}

		private static StringBuilder AppendRect(this StringBuilder sb, Rectangle r)
		{
			float cx = r.X + r.Width * 0.5f;
			float cy = r.Y + r.Height * 0.5f;
			sb.Append(r.X).Append('\t').Append(r.Y).Append('\t').Append(r.Width).Append('\t').Append(r.Height).Append('\t')
				.Append(cx.ToString("0.##")).Append('\t').Append(cy.ToString("0.##")).Append('\t');
			return sb;
		}

		private static void ResolveWidths(BindingListViewState state, int viewWidth, out int keyW, out int actW, out int modW)
		{
			keyW = state.ColKeyWidthUi;
			actW = state.ColActionWidthUi;
			modW = state.ColModWidthUi;
			int need = keyW + actW + modW;
			if (keyW <= 0 || actW <= 0 || modW <= 0 || need <= 0)
			{
				keyW = (int)(viewWidth * 0.34f);
				actW = (int)(viewWidth * 0.38f);
				modW = Math.Max(24, viewWidth - keyW - actW);
				return;
			}

			if (need > viewWidth)
			{
				float s = viewWidth / (float)need;
				keyW = Math.Max(24, (int)(keyW * s));
				actW = Math.Max(24, (int)(actW * s));
				modW = Math.Max(24, viewWidth - keyW - actW);
			}
			else
			{
				modW = Math.Max(modW, viewWidth - keyW - actW);
			}
		}
	}
}
