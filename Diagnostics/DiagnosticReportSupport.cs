using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using AdvancedKeybinds.Binding;
using Terraria;

namespace AdvancedKeybinds.Diagnostics
{
	internal enum DiagnosticVerdict
	{
		Pass,
		Info,
		ExpectedDifference,
		Warning,
		Fail,
		Skipped,
		Untested,
		Unavailable
	}

	internal sealed class DiagnosticSummary
	{
		private readonly Dictionary<DiagnosticVerdict, int> _counts = new();

		public int Total { get; private set; }

		public void Add(DiagnosticVerdict verdict)
		{
			Total++;
			_counts[verdict] = Get(verdict) + 1;
		}

		public void Replace(DiagnosticVerdict previous, DiagnosticVerdict next)
		{
			if (previous == next)
				return;
			if (Get(previous) > 0)
				_counts[previous] = Get(previous) - 1;
			_counts[next] = Get(next) + 1;
		}

		public void AddFinding(DiagnosticVerdict verdict)
			=> _counts[verdict] = Get(verdict) + 1;

		public int Get(DiagnosticVerdict verdict)
			=> _counts.TryGetValue(verdict, out int value) ? value : 0;

		public void AppendTo(StringBuilder sb)
		{
			sb.AppendLine("Total=" + Total);
			foreach (DiagnosticVerdict verdict in Enum.GetValues<DiagnosticVerdict>())
				sb.AppendLine(verdict.ToString().ToUpperInvariant() + "=" + Get(verdict));
		}
	}

	/// <summary>Shared privacy-safe header/path helpers for developer diagnostics.</summary>
	internal static class DiagnosticReportSupport
	{
		public const string FeatureVersion = "2";

		public static long Timestamp() => Stopwatch.GetTimestamp();

		public static double ElapsedMs(long start, long end)
			=> (end - start) * 1000.0 / Stopwatch.Frequency;

		public static string CreatePath(string filePrefix)
		{
			string dir = Path.Combine(Main.SavePath, "AdvancedKeybinds");
			Directory.CreateDirectory(dir);
			return Path.Combine(
				dir,
				filePrefix + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".txt");
		}

		public static string PublicPath(string path)
		{
			if (string.IsNullOrEmpty(path))
				return string.Empty;

			string file = Path.GetFileName(path);
			return Path.Combine("<tModLoader SavePath>", "AdvancedKeybinds", file);
		}

		public static void AppendCommonHeader(
			StringBuilder sb,
			string diagnosticMode,
			IReadOnlyList<KeyboardLayoutHelper.InstalledLayoutInfo> layouts = null)
		{
			layouts ??= KeyboardLayoutHelper.GetInstalledLayouts();
			IntPtr active = KeyboardLayoutHelper.GetActiveLayoutHandle();
			string modVersion = AdvancedKeybinds.Instance?.Version?.ToString() ?? "unavailable";
			string tmlVersion = typeof(Terraria.ModLoader.Mod).Assembly.GetName().Version?.ToString() ?? "unavailable";

			sb.AppendLine("DiagnosticFeatureVersion=" + FeatureVersion);
			sb.AppendLine("DiagnosticMode=" + diagnosticMode);
			sb.AppendLine("ModVersion=" + modVersion);
			sb.AppendLine("tModLoaderAssemblyVersion=" + tmlVersion);
			sb.AppendLine("OS=" + RuntimeInformation.OSDescription);
			sb.AppendLine("ProcessArchitecture=" + RuntimeInformation.ProcessArchitecture);
			sb.AppendLine("ProcessBitness=" + (Environment.Is64BitProcess ? "64bit" : "32bit"));
			sb.AppendLine("GeneratedUtc=" + DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
			sb.AppendLine("StopwatchFrequency=" + Stopwatch.Frequency);
			sb.AppendLine("ActiveHKL=0x" + unchecked((ulong)active.ToInt64()).ToString("X16"));
			sb.AppendLine("InstalledLayoutCount=" + layouts.Count);
			for (int i = 0; i < layouts.Count; i++)
			{
				KeyboardLayoutHelper.InstalledLayoutInfo layout = layouts[i];
				sb.AppendLine(
					"InstalledLayout[" + i + "]="
					+ "HKL:0x" + unchecked((ulong)layout.Hkl.ToInt64()).ToString("X16")
					+ ",LANGID:0x" + layout.LangId.ToString("X4")
					+ ",Identifier:" + layout.LayoutIdentifier
					+ ",Culture:" + layout.CultureName
					+ ",Active:" + layout.IsActive
					+ ",Installed:" + layout.IsInstalled);
			}

			sb.AppendLine(KeyboardLayoutHelper.DescribePhysicalLayoutEvidence());
			sb.AppendLine("UserSettingsModified=False");
			sb.AppendLine("Privacy=NoUserName,NoComputerName,NoSteamAccount,NoTypedTextReconstruction");
			sb.AppendLine("Limitations=Logical HKL does not prove physical keycaps/ANSI/ISO/JIS; live timing starts after Windows/hook delivery; USB/Bluetooth internal latency is not measured; Fn may not emit an independent event.");
			sb.AppendLine();
		}

		public static void WriteAllText(string path, string content)
			=> File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

		public static string EscapeSingleGlyph(string value)
		{
			if (string.IsNullOrEmpty(value))
				return "none";

			// Never reconstruct text. Keep only one Unicode scalar-ish UTF-16 unit for a single event.
			string one = value.Substring(0, 1);
			return "\"" + one.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
		}
	}
}
