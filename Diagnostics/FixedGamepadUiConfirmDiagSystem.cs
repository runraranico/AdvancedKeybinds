using System;
using System.IO;
using System.Text;
using AdvancedKeybinds.Binding;
using Terraria.ModLoader;

namespace AdvancedKeybinds.Diagnostics
{
	/// <summary>
	/// TEMP: logs fixed Pad UI confirm/cancel decisions on input/decision edges only.
	/// Remove after in-game title confirm verification.
	/// </summary>
	internal static class FixedGamepadUiConfirmDiagSystem
	{
		internal const bool Enabled = true;

		private const string Folder =
			@"C:\Users\runra\Documents\My Games\Terraria\tModLoader\deck log\GamePad";
		private const string LiveFileName = "AK_FixedUiPadConfirm_Diag_LIVE.txt";
		private const int MaxLines = 2000;

		private static readonly object Gate = new();
		private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
		private static readonly StringBuilder Line = new(512);

		private static bool _filesInitialized;
		private static bool _broken;
		private static int _written;
		private static string _historyPath;
		private static string _lastKey;

		internal static void Reset()
		{
			lock (Gate)
			{
				_filesInitialized = false;
				_broken = false;
				_written = 0;
				_historyPath = null;
				_lastKey = null;
			}
		}

		internal static void ObserveDecision(
			bool gameMenu,
			string modeName,
			int usingPad,
			int usingPadUi,
			int padAJp,
			int padBJp,
			int fixedConfirm,
			int fixedCancel,
			string denyConfirm,
			string denyCancel,
			int mlBefore,
			int mlAfter,
			int mrBefore,
			int mrAfter,
			int invBefore,
			int invAfter,
			int akOwns,
			int closeGate,
			int menuOrUi,
			int padSession)
		{
			if (!Enabled || _broken)
				return;

			string key = modeName
				+ "|" + usingPad + "|" + usingPadUi
				+ "|" + padAJp + "|" + padBJp
				+ "|" + fixedConfirm + "|" + fixedCancel
				+ "|" + (denyConfirm ?? "-") + "|" + (denyCancel ?? "-")
				+ "|" + mlBefore + "|" + mlAfter
				+ "|" + akOwns + "|" + menuOrUi + "|" + padSession;
			if (string.Equals(key, _lastKey, StringComparison.Ordinal))
				return;
			_lastKey = key;

			try
			{
				lock (Gate)
				{
					EnsureFilesUnlocked();
					if (_written >= MaxLines)
						return;

					Line.Clear();
					Line.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
					Line.Append(" epoch=").Append(FnaGamePadProvider.InputTickEpoch);
					Line.Append(" gameMenu=").Append(gameMenu ? 1 : 0);
					Line.Append(" mode=").Append(modeName ?? "?");
					Line.Append(" usingPad=").Append(usingPad);
					Line.Append(" usingPadUI=").Append(usingPadUi);
					Line.Append(" padSession=").Append(padSession);
					Line.Append(" menuOrUi=").Append(menuOrUi);
					Line.Append(" Ajp=").Append(padAJp);
					Line.Append(" Bjp=").Append(padBJp);
					Line.Append(" injectConfirm=").Append(fixedConfirm);
					Line.Append(" injectCancel=").Append(fixedCancel);
					Line.Append(" denyC=").Append(denyConfirm ?? "-");
					Line.Append(" denyX=").Append(denyCancel ?? "-");
					Line.Append(" ml=").Append(mlBefore).Append("->").Append(mlAfter);
					Line.Append(" mr=").Append(mrBefore).Append("->").Append(mrAfter);
					Line.Append(" inv=").Append(invBefore).Append("->").Append(invAfter);
					Line.Append(" akOwns=").Append(akOwns);
					Line.Append(" closeGate=").Append(closeGate);
					Line.Append("\r\n");

					string text = Line.ToString();
					File.AppendAllText(Path.Combine(Folder, LiveFileName), text, Utf8NoBom);
					if (!string.IsNullOrEmpty(_historyPath))
						File.AppendAllText(_historyPath, text, Utf8NoBom);
					_written++;
				}
			}
			catch
			{
				_broken = true;
			}
		}

		private static void EnsureFilesUnlocked()
		{
			if (_filesInitialized)
				return;
			Directory.CreateDirectory(Folder);
			_historyPath = Path.Combine(
				Folder,
				"AK_FixedUiPadConfirm_Diag_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt");
			string header =
				"# AK FixedUiPadConfirm TEMP diag. Edge/decision only. UTF-8.\r\n"
				+ "# Title often: mode=XBoxGamepad usingPadUI=0 — inject must still run.\r\n"
				+ "# started=" + DateTime.Now.ToString("o") + "\r\n";
			File.WriteAllText(Path.Combine(Folder, LiveFileName), header, Utf8NoBom);
			File.WriteAllText(_historyPath, header, Utf8NoBom);
			_filesInitialized = true;
			_written = 0;
		}
	}
}
