using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AdvancedKeybinds.Core;
using Terraria;
using Terraria.ModLoader;

namespace AdvancedKeybinds.Binding
{
	/// <summary>
	/// Layout-neutral keyboard probe: logs MonoGame / VK / scan / glyph / capture tier per press.
	/// </summary>
	public static class KeyboardDiagMode
	{
		private static readonly List<string> _recentLines = new(24);
		private static readonly object _fileLock = new();
		private static string _lastLine = string.Empty;

#if AK_DIAGNOSTICS
		public static bool IsActive
			=> AdvancedKeybindsConfig.Instance?.KeyboardDiagnosticMode ?? false;

		public static bool LogToFile
			=> AdvancedKeybindsConfig.Instance?.KeyboardDiagnosticLogToFile ?? false;
#else
		public static bool IsActive => false;
		public static bool LogToFile => false;
#endif

		public static IReadOnlyList<string> RecentLines => _recentLines;

		public static string LastLine => _lastLine;

		public static void Clear()
		{
			_recentLines.Clear();
			_lastLine = string.Empty;
		}

		public static void NotifyPhysicalKeyEvent(
			uint vk,
			ushort scan,
			bool extended,
			string captureToken,
			bool captureSuccess,
			string captureFailReason,
			string source)
		{
			if (!IsActive)
				return;

			PhysicalKeyResolver.ResolveResult resolved = PhysicalKeyResolver.ResolveForCapture(
				vk,
				scan,
				extended,
				Main.keyState);

			string mono = resolved.MonoGameKey?.ToString() ?? "-";
			bool fnaDown = resolved.FnaReportsKey;
			int written = KeyboardLayoutHelper.QueryToUnicodeEx(
				vk,
				scan,
				KeyboardLayoutHelper.GetActiveLayoutHandle(),
				out string unicodeRaw);
			bool deadKey = written < 0;
			bool hasGlyph = KeyboardLayoutHelper.TryGetUnshiftedVkGlyph(vk, out string glyph);

			string line = "KeyDiag"
				+ " mono=" + mono
				+ " fnaDown=" + fnaDown
				+ " vk=0x" + vk.ToString("X2")
				+ " scan=0x" + (scan & 0xFF).ToString("X2")
				+ " extended=" + extended
				+ " glyph=" + (hasGlyph ? "\"" + glyph + "\"" : "none")
				+ " toUnicodeWritten=" + written
				+ " deadKey=" + deadKey
				+ " resolvedTier=" + (resolved.Success ? resolved.Tier : "-")
				+ " resolvedToken=" + (resolved.Success ? resolved.Token : "-")
				+ " captureToken=" + (captureToken ?? "-")
				+ " captureOk=" + captureSuccess
				+ " fail=" + (captureFailReason ?? "-")
				+ " src=" + source
				+ " layout=" + KeyboardLayoutHelper.FormatCaptureLayoutDiag();

			_lastLine = line;
			_recentLines.Add(line);
			if (_recentLines.Count > 20)
				_recentLines.RemoveAt(0);

			if (AkDiag.Enabled) AkDiag.Log(line);

			if (LogToFile)
				AppendLogFile(line);
		}

		public static void FlushLogHeader()
		{
			if (!IsActive || !LogToFile)
				return;

			AppendLogFile("=== KeyboardDiag session " + DateTime.Now.ToString("o") + " ===");
		}

		private static void AppendLogFile(string line)
		{
			try
			{
				string dir = Path.Combine(Main.SavePath, "AdvancedKeybinds");
				Directory.CreateDirectory(dir);
				string path = Path.Combine(dir, "keyboard-diag.log");
				lock (_fileLock)
				{
					File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
				}
			}
			catch (Exception ex)
			{
				AkDiag.WarnRare("KeyboardDiag log write failed: " + ex.Message);
			}
		}
	}
}
