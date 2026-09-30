using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.UI;

namespace AdvancedKeybinds.Features.ListView
{
	/// <summary>
	/// TEMP LIVE — determine why back-UI hover labels (e.g. Fargo "Stat Sheet") still draw through AK.
	/// Log: deck log\Keyboard\ListView_HoverLeak_LIVE.txt
	/// Gate: <see cref="BindingListViewLiveDiagGate.HoverLeakDiagEnabled"/> (default OFF).
	/// </summary>
	internal static class BindingListViewHoverLeakDiag
	{
		private static bool _dumpedLayers;
		private static int _linesThisSecond;
		private static int _secondUtc = -1;
		private const int MaxLinesPerSecond = 40;

		private static string LogPath
		{
			get
			{
				string root = Path.Combine(
					Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
					"My Games", "Terraria", "tModLoader", "deck log", "Keyboard");
				Directory.CreateDirectory(root);
				return Path.Combine(root, "ListView_HoverLeak_LIVE.txt");
			}
		}

		public static void DumpInterfaceLayerOrderOnce(List<GameInterfaceLayer> layers)
		{
			if (!BindingListViewLiveDiagGate.HoverLeakDiagEnabled || _dumpedLayers || layers == null)
				return;
			_dumpedLayers = true;

			var sb = new StringBuilder(2048);
			sb.AppendLine("===== INTERFACE LAYER ORDER (once per session) =====");
			sb.AppendLine("timeUtc=" + DateTime.UtcNow.ToString("o"));
			for (int i = 0; i < layers.Count; i++)
			{
				GameInterfaceLayer layer = layers[i];
				string name = layer != null ? layer.Name : "(null)";
				bool mark = name.IndexOf("BindingListView", StringComparison.OrdinalIgnoreCase) >= 0
					|| name.IndexOf("Mouse Text", StringComparison.OrdinalIgnoreCase) >= 0
					|| name.IndexOf("Cursor", StringComparison.OrdinalIgnoreCase) >= 0
					|| name.IndexOf("Fargo", StringComparison.OrdinalIgnoreCase) >= 0
					|| name.IndexOf("Stat", StringComparison.OrdinalIgnoreCase) >= 0
					|| name.IndexOf("Improve", StringComparison.OrdinalIgnoreCase) >= 0;
				sb.Append(i).Append('\t').Append(mark ? "*" : " ").Append('\t').AppendLine(name);
			}

			sb.AppendLine("===== END LAYER ORDER =====");
			Append(sb.ToString());
		}

		public static void Capture(
			string phase,
			BindingListViewState state,
			bool claimFront,
			bool captureActive)
		{
			if (!BindingListViewLiveDiagGate.HoverLeakDiagEnabled)
				return;

			string hoverName = Main.hoverItemName ?? string.Empty;
			bool mouseText = Main.mouseText;
			int hoverType = Main.HoverItem != null ? Main.HoverItem.type : -1;
			bool interesting = claimFront
				|| hoverName.Length > 0
				|| mouseText
				|| hoverType > 0
				|| phase.StartsWith("LAYER_", StringComparison.Ordinal)
				|| phase.Contains("PostDraw", StringComparison.Ordinal)
				|| phase.Contains("PostUpdate", StringComparison.Ordinal);

			if (!interesting)
				return;

			Throttle();
			var sb = new StringBuilder(360);
			sb.Append(DateTime.UtcNow.ToString("HH:mm:ss.fff"));
			sb.Append('\t').Append(Main.GameUpdateCount);
			sb.Append('\t').Append(phase);
			sb.Append("\tclaim=").Append(claimFront ? 1 : 0);
			sb.Append("\tcapture=").Append(captureActive ? 1 : 0);
			sb.Append("\topen=").Append(state != null && state.IsOpen ? 1 : 0);
			sb.Append("\tmax=").Append(state != null && state.IsMaximized ? 1 : 0);
			sb.Append("\tmouse=").Append(Main.mouseX).Append(',').Append(Main.mouseY);
			sb.Append("\thoverName=\"").Append(hoverName.Replace('\t', ' ').Replace('"', '\'')).Append('"');
			sb.Append("\tmouseText=").Append(mouseText ? 1 : 0);
			sb.Append("\thoverItemType=").Append(hoverType);
			sb.Append("\tmLeft=").Append(Main.mouseLeft ? 1 : 0);
			sb.AppendLine();
			Append(sb.ToString());
		}

		public static void ResetLayerDumpFlag()
		{
			_dumpedLayers = false;
		}

		private static void Throttle()
		{
			int sec = (int)(DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond);
			if (sec != _secondUtc)
			{
				_secondUtc = sec;
				_linesThisSecond = 0;
			}

			_linesThisSecond++;
		}

		private static void Append(string text)
		{
			if (_linesThisSecond > MaxLinesPerSecond && !text.StartsWith("=====", StringComparison.Ordinal))
				return;
			try
			{
				File.AppendAllText(LogPath, text);
			}
			catch
			{
				// ignore I/O errors during LIVE
			}
		}
	}
}
