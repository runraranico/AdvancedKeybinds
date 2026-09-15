using System;
using System.IO;
using System.Text;
using Terraria;

namespace AdvancedKeybinds.Patches
{
	/// <summary>
	/// TEMPORARY diagnostic writer for the AlchemistNPCLite shop-list controller work.
	/// Event-driven only (no per-frame writes, consecutive duplicates are dropped).
	/// Delete this file and every <c>AlchemistNpcLiteShopListTempDiag</c> call once the in-game
	/// verification round is finished. It must not ship in a release build.
	/// </summary>
	internal static class AlchemistNpcLiteShopListTempDiag
	{
		internal const bool Enabled = true;

		private const string Folder =
			@"C:\Users\runra\Documents\My Games\Terraria\tModLoader\deck log\GamePad";
		private const string FileName = "AK_AlchemistShopList_Diag.txt";
		private const int MaxLines = 4000;

		private static readonly object Gate = new();
		private static readonly StringBuilder Line = new();
		private static string _lastPayload;
		private static int _written;
		private static bool _broken;

		internal static void Event(string tag, string detail = null)
		{
			if (!Enabled || _broken || tag == null)
				return;

			lock (Gate)
			{
				if (_written >= MaxLines)
					return;

				string payload = detail == null ? tag : tag + " " + detail;
				if (string.Equals(payload, _lastPayload, StringComparison.Ordinal))
					return;
				_lastPayload = payload;

				Line.Clear();
				Line.Append(DateTime.Now.ToString("HH:mm:ss.fff"));
				Line.Append(" upd=");
				Line.Append(Main.GameUpdateCount);
				Line.Append(' ');
				Line.Append(payload);

				try
				{
					Directory.CreateDirectory(Folder);
					File.AppendAllText(Path.Combine(Folder, FileName), Line.ToString() + Environment.NewLine);
					_written++;
				}
				catch
				{
					_broken = true;
				}
			}
		}

		/// <summary>Allows the next identical payload through (used when a new list session starts).</summary>
		internal static void ResetDedupe()
		{
			lock (Gate)
			{
				_lastPayload = null;
			}
		}
	}
}
