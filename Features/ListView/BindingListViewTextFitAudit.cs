using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.Xna.Framework;
using Terraria.Localization;

namespace AdvancedKeybinds.Features.ListView
{
	/// <summary>
	/// One-shot mechanical fit audit for BindingList localization strings across cultures.
	/// Writes TSV under deck log / Keyboard. Does not change input or layout Baseline.
	/// </summary>
	internal static class BindingListViewTextFitAudit
	{
		private static bool _ran;

		private static readonly string[] BindingListKeys =
		{
			"Mods.AdvancedKeybinds.UI.BindingList.Title",
			"Mods.AdvancedKeybinds.UI.BindingList.ColKey",
			"Mods.AdvancedKeybinds.UI.BindingList.ColAction",
			"Mods.AdvancedKeybinds.UI.BindingList.ColMod",
			"Mods.AdvancedKeybinds.UI.BindingList.EmptyHint",
			"Mods.AdvancedKeybinds.UI.BindingList.NoMatch",
		};

		// Typical cell sizes matching Renderer (rowInnerH=24, empty band=20, title≈label width).
		private const int RowInnerH = 24;
		private const int EmptyBandH = 20;
		private const int HeaderStripH = 22;
		private const int TitleBarH = 28;
		private const int TypicalKeyCellW = 120;
		private const int TypicalActionCellW = 220;
		private const int TypicalModCellW = 160;
		private const int TypicalEmptyW = 420;
		private const int TypicalTitleW = 200;
		private const int TypicalHeaderKeyW = 80;
		private const int TypicalHeaderActionW = 80;
		private const int TypicalHeaderModW = 48;

		public static void TryRunOnce()
		{
			if (!BindingListViewLiveDiagGate.HeavyDiagEnabled)
				return;
			if (_ran)
				return;
			_ran = true;

			BindingListViewOpenPerfDiag.OnCultureAuditStart();
			try
			{
				Run();
			}
			catch
			{
				// Audit must never break draw.
			}
			finally
			{
				BindingListViewOpenPerfDiag.OnCultureAuditEnd();
			}
		}

		private static void Run()
		{
			string dir = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
				"My Games", "Terraria", "tModLoader", "deck log", "Keyboard");
			Directory.CreateDirectory(dir);
			string path = Path.Combine(dir, "BindingList_TextFitAudit.tsv");

			var sb = new StringBuilder();
			sb.AppendLine("Language\tLocKey\tRole\tText\tMeasW\tMeasH\tAvailW\tAvailH\tStdScale\tFinalScale\tFits\tEllipsisNeeded");

			GameCulture previous = Language.ActiveCulture;
			float worstScale = BindingListViewTextFit.StandardScale;
			string worstScaleLine = string.Empty;
			float worstWidthRatio = 0f;
			string worstWidthLine = string.Empty;
			float worstHeightRatio = 0f;
			string worstHeightLine = string.Empty;
			int noFitAtMin = 0;

			try
			{
				foreach (GameCulture culture in GameCulture.KnownCultures)
				{
					if (culture == null)
						continue;
					LanguageManager.Instance.SetLanguage(culture);
					string lang = culture.Name;

					foreach (string key in BindingListKeys)
					{
						string text = Language.GetTextValue(key);
						if (string.IsNullOrEmpty(text) || text == key)
							continue;

						ResolveRole(key, out string role, out int cellW, out int cellH, out int padX);
						AppendSample(
							sb, lang, key, role, text, cellW, cellH, padX,
							ref worstScale, ref worstScaleLine,
							ref worstWidthRatio, ref worstWidthLine,
							ref worstHeightRatio, ref worstHeightLine,
							ref noFitAtMin);
					}
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
					// ignore restore failure
				}
			}

			sb.AppendLine();
			sb.AppendLine("# SUMMARY");
			sb.AppendLine("WorstFinalScale\t" + worstScale.ToString("0.###") + "\t" + worstScaleLine);
			sb.AppendLine("WorstWidthPressure\t" + worstWidthRatio.ToString("0.###") + "\t" + worstWidthLine);
			sb.AppendLine("WorstHeightPressure\t" + worstHeightRatio.ToString("0.###") + "\t" + worstHeightLine);
			sb.AppendLine("NoFitAtMinCount\t" + noFitAtMin);
			sb.AppendLine("StandardScale\t" + BindingListViewTextFit.StandardScale);
			sb.AppendLine("MinUniformScale\t" + BindingListViewTextFit.MinUniformScale);
			sb.AppendLine("HorizontalSafeMargin\t" + BindingListViewTextFit.HorizontalSafeMargin);
			sb.AppendLine("VerticalSafeMargin\t" + BindingListViewTextFit.VerticalSafeMargin);

			File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
		}

		private static void ResolveRole(string key, out string role, out int cellW, out int cellH, out int padX)
		{
			if (key.EndsWith(".Title", StringComparison.Ordinal))
			{
				role = "Title";
				cellW = TypicalTitleW;
				cellH = TitleBarH;
				padX = 0;
			}
			else if (key.EndsWith(".ColKey", StringComparison.Ordinal))
			{
				role = "HeaderKey";
				cellW = TypicalHeaderKeyW;
				cellH = HeaderStripH;
				padX = 2;
			}
			else if (key.EndsWith(".ColAction", StringComparison.Ordinal))
			{
				role = "HeaderAction";
				cellW = TypicalHeaderActionW;
				cellH = HeaderStripH;
				padX = 2;
			}
			else if (key.EndsWith(".ColMod", StringComparison.Ordinal))
			{
				role = "HeaderMod";
				cellW = TypicalHeaderModW;
				cellH = HeaderStripH;
				padX = 4;
			}
			else if (key.EndsWith(".EmptyHint", StringComparison.Ordinal)
				|| key.EndsWith(".NoMatch", StringComparison.Ordinal))
			{
				role = "EmptyOrNoMatch";
				cellW = TypicalEmptyW;
				cellH = EmptyBandH;
				padX = 0;
			}
			else
			{
				role = "Other";
				cellW = TypicalActionCellW;
				cellH = RowInnerH;
				padX = 2;
			}
		}

		private static void AppendSample(
			StringBuilder sb,
			string lang,
			string key,
			string role,
			string text,
			int cellW,
			int cellH,
			int padX,
			ref float worstScale,
			ref string worstScaleLine,
			ref float worstWidthRatio,
			ref string worstWidthLine,
			ref float worstHeightRatio,
			ref string worstHeightLine,
			ref int noFitAtMin)
		{
			float availW = BindingListViewTextFit.AvailableWidth(cellW, padX, BindingListViewTextAlign.Left);
			float availH = BindingListViewTextFit.AvailableHeight(cellH);
			Vector2 measured = BindingListViewTextFit.MeasureAtStandard(text);
			float scale = BindingListViewTextFit.ComputeUniformScale(measured, availW, availH);
			bool fits = BindingListViewTextFit.FitsAtScale(measured, scale, availW, availH);
			bool ellipsis = !fits;
			if (ellipsis)
				noFitAtMin++;

			string safeText = (text ?? string.Empty).Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
			string line = string.Join("\t",
				lang,
				key,
				role,
				safeText,
				measured.X.ToString("0.##"),
				measured.Y.ToString("0.##"),
				availW.ToString("0.##"),
				availH.ToString("0.##"),
				BindingListViewTextFit.StandardScale.ToString("0.##"),
				scale.ToString("0.###"),
				fits ? "1" : "0",
				ellipsis ? "1" : "0");
			sb.AppendLine(line);

			if (scale < worstScale)
			{
				worstScale = scale;
				worstScaleLine = lang + " | " + key + " | " + safeText + " | scale=" + scale.ToString("0.###");
			}

			if (measured.X > 0.01f)
			{
				float wr = measured.X / availW;
				if (wr > worstWidthRatio)
				{
					worstWidthRatio = wr;
					worstWidthLine = lang + " | " + key + " | " + safeText
						+ " | measW=" + measured.X.ToString("0.##")
						+ " availW=" + availW.ToString("0.##");
				}
			}

			if (measured.Y > 0.01f)
			{
				float hr = measured.Y / availH;
				if (hr > worstHeightRatio)
				{
					worstHeightRatio = hr;
					worstHeightLine = lang + " | " + key + " | " + safeText
						+ " | measH=" + measured.Y.ToString("0.##")
						+ " availH=" + availH.ToString("0.##");
				}
			}
		}
	}
}
