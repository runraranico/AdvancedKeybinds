using System;
using System.IO;
using System.Text;
using Terraria.UI;

namespace AdvancedKeybinds.Features.ListView
{
	/// <summary>
	/// TEMP LIVE — counts <see cref="UIElement.MouseOver"/> suppressed under claimFront.
	/// File I/O only when <see cref="BindingListViewLiveDiagGate.FileOutputEnabled"/>.
	/// Not a Baseline. Does not mute SoundEngine.
	/// </summary>
	internal static class BindingListViewHoverSoundDiag
	{
		private static int _suppressedThisSecond;
		private static int _secondUtc = -1;
		private static string _lastTypeName = string.Empty;
		private static int _lastLoggedSecond = -1;

		private static string LogPath
		{
			get
			{
				string root = Path.Combine(
					Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
					"My Games", "Terraria", "tModLoader", "deck log", "Keyboard");
				Directory.CreateDirectory(root);
				return Path.Combine(root, "ListView_HoverSound_LIVE.txt");
			}
		}

		public static void NoteSuppressedMouseOver(UIElement self)
		{
			// Gate before time/type/allocations — not only before Append.
			if (!BindingListViewLiveDiagGate.FileOutputEnabled)
				return;

			int sec = (int)(DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond);
			if (sec != _secondUtc)
			{
				_secondUtc = sec;
				_suppressedThisSecond = 0;
			}

			_suppressedThisSecond++;
			_lastTypeName = self != null ? self.GetType().FullName ?? self.GetType().Name : "(null)";

			// One summary line per second while suppressing (detect continuous re-enter).
			if (sec == _lastLoggedSecond)
				return;
			_lastLoggedSecond = sec;

			var sb = new StringBuilder(160);
			sb.Append(DateTime.UtcNow.ToString("HH:mm:ss.fff"));
			sb.Append("\tsuppressedMouseOverPerSec=").Append(_suppressedThisSecond);
			sb.Append("\tlastType=").Append(_lastTypeName);
			sb.AppendLine();
			try
			{
				File.AppendAllText(LogPath, sb.ToString());
			}
			catch
			{
				// ignore I/O failures during LIVE
			}
		}
	}
}
