using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AdvancedKeybinds.Binding;
using AdvancedKeybinds.Core;
using Terraria;
using Terraria.GameInput;

namespace AdvancedKeybinds.Diagnostics
{
	/// <summary>
	/// TEMP: Jump (and MouseLeft contrast) JustPressed before/after ApplyAdvanced stages.
	/// Output: deck log\GamePad\AK_JumpApplyStage_Diag_LIVE.txt
	/// Delete after wipe vs vanilla-miss is confirmed and fixed.
	/// </summary>
	internal static class JumpApplyStageDiag
	{
		internal const bool Enabled = true;

		private const string Folder =
			@"C:\Users\runra\Documents\My Games\Terraria\tModLoader\deck log\GamePad";
		private const string LiveFileName = "AK_JumpApplyStage_Diag_LIVE.txt";
		private const int MaxLines = 4000;

		private static readonly object Gate = new();
		private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
		private static readonly StringBuilder Line = new(384);

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

		internal static bool ReadTrigger(TriggersSet set, string actionId)
		{
			if (set?.KeyStatus == null || string.IsNullOrEmpty(actionId))
				return false;
			return set.KeyStatus.TryGetValue(actionId, out bool v) && v;
		}

		/// <summary>
		/// Log when Pad:A edge, Jump changes across stages, or MouseLeft changes (contrast).
		/// </summary>
		internal static void Observe(
			bool jumpBefore,
			bool jumpAfterResults,
			bool jumpAfterOwnership,
			bool jumpAfterSuppress,
			bool mlBefore,
			bool mlAfterSuppress,
			bool padAJp)
		{
			if (!Enabled || _broken)
				return;

			bool jumpChanged = jumpBefore != jumpAfterResults
				|| jumpAfterResults != jumpAfterOwnership
				|| jumpAfterOwnership != jumpAfterSuppress;
			bool mlChanged = mlBefore != mlAfterSuppress;
			if (!padAJp && !jumpChanged && !mlChanged
				&& !jumpBefore && !jumpAfterSuppress && !mlBefore && !mlAfterSuppress)
				return;

			string key = (padAJp ? 1 : 0)
				+ "|" + (jumpBefore ? 1 : 0)
				+ "|" + (jumpAfterResults ? 1 : 0)
				+ "|" + (jumpAfterOwnership ? 1 : 0)
				+ "|" + (jumpAfterSuppress ? 1 : 0)
				+ "|" + (mlBefore ? 1 : 0)
				+ "|" + (mlAfterSuppress ? 1 : 0);
			if (string.Equals(key, _lastKey, StringComparison.Ordinal))
				return;
			_lastKey = key;

			string verdict;
			if (jumpBefore && !jumpAfterSuppress)
			{
				if (jumpBefore && !jumpAfterResults)
					verdict = "wipe_at_ApplyResults";
				else if (jumpAfterResults && !jumpAfterOwnership)
					verdict = "wipe_at_ApplyOwnership";
				else if (jumpAfterOwnership && !jumpAfterSuppress)
					verdict = "wipe_at_Suppress";
				else
					verdict = "wipe_multi_stage";
			}
			else if (!jumpBefore && !jumpAfterSuppress && padAJp)
				verdict = "vanilla_miss_Jump0";
			else if (jumpBefore && jumpAfterSuppress)
				verdict = "Jump_ok";
			else
				verdict = "other";

			try
			{
				lock (Gate)
				{
					EnsureFilesUnlocked();
					if (_written >= MaxLines)
						return;

					string ks = FormatJumpKeyStatus();

					Line.Clear();
					Line.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
					Line.Append(" epoch=").Append(FnaGamePadProvider.InputTickEpoch);
					Line.Append(" menu=").Append(Main.gameMenu ? 1 : 0);
					Line.Append(" mode=").Append(PlayerInput.CurrentInputMode.ToString());
					Line.Append(" Ajp=").Append(padAJp ? 1 : 0);
					Line.Append(" Jump=").Append(jumpBefore ? 1 : 0)
						.Append("->R").Append(jumpAfterResults ? 1 : 0)
						.Append("->O").Append(jumpAfterOwnership ? 1 : 0)
						.Append("->S").Append(jumpAfterSuppress ? 1 : 0);
					Line.Append(" ML=").Append(mlBefore ? 1 : 0)
						.Append("->").Append(mlAfterSuppress ? 1 : 0);
					Line.Append(" verdict=").Append(verdict);
					Line.Append(" keyStatusJump=").Append(ks);
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

		private static string FormatJumpKeyStatus()
		{
			try
			{
				IReadOnlyList<string> game =
					VanillaRebindHelper.GetAssignedKeysForInputMode("Jump", InputMode.XBoxGamepad);
				if (game == null || game.Count == 0)
					return "(empty)";
				var sb = new StringBuilder(64);
				for (int i = 0; i < game.Count; i++)
				{
					if (i > 0)
						sb.Append('+');
					sb.Append(game[i] ?? "?");
				}
				return sb.ToString();
			}
			catch
			{
				return "?";
			}
		}

		private static void EnsureFilesUnlocked()
		{
			if (_filesInitialized)
				return;
			Directory.CreateDirectory(Folder);
			string livePath = Path.Combine(Folder, LiveFileName);
			string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
			_historyPath = Path.Combine(Folder, "AK_JumpApplyStage_Diag_" + stamp + ".txt");
			string header =
				"# AK JumpApplyStage TEMP. Jump JP: before -> afterResults(R) -> afterOwnership(O) -> afterSuppress(S).\r\n"
				+ "# verdict: wipe_at_* | vanilla_miss_Jump0 | Jump_ok | other. Delete after fix.\r\n"
				+ "# started=" + DateTime.Now.ToString("o") + "\r\n";
			File.WriteAllText(livePath, header, Utf8NoBom);
			File.WriteAllText(_historyPath, header, Utf8NoBom);
			_filesInitialized = true;
			_written = 0;
		}
	}
}
