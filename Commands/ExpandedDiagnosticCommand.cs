using System;
using AdvancedKeybinds.Binding;
using AdvancedKeybinds.Diagnostics;
using Microsoft.Xna.Framework;
using Terraria.ModLoader;

namespace AdvancedKeybinds.Commands
{
	/// <summary>Developer-only entry point for the five separated diagnostics.</summary>
	public sealed class ExpandedDiagnosticCommand : ModCommand
	{
		public override CommandType Type => CommandType.Chat;
		public override string Command => "akdiag";
		public override string Usage =>
			"/akdiag internal|layout|live-start|live-stop|route|timing|safe|status|jis-oem";
		public override string Description =>
			"AKDIAG separated internal/layout/live/route/timing/jis-oem diagnostics.";

		public override void Action(CommandCaller caller, string input, string[] args)
		{
			string operation = args.Length == 0 ? "status" : args[0].Trim().ToLowerInvariant();

			// jis-oem is a lightweight capture arm — independent of AkDiag dig spam.
			if (operation == "jis-oem")
			{
				JisOemInGameDiagnostic.BeginCaptureSession();
				caller.Reply(
					"[AdvancedKeybinds] JIS OEM diagnostic armed (independent of AKDIAG). Open a combo slot and press keys — TXT under AdvancedKeybinds save folder.",
					Color.LightYellow);
				return;
			}

			if (!AkDiagSwitch.On)
			{
				caller.Reply(
					"[AdvancedKeybinds] /akdiag requires AKDIAG ON (tools/Set-AkDiagEnabled.ps1). Exception: /akdiag jis-oem works while OFF.",
					Color.OrangeRed);
				return;
			}

			switch (operation)
			{
				case "internal":
					ReplyOutcome(caller, "Internal Diagnostic", SafeDiagnosticRunner.RunInternal());
					break;

				case "layout":
					ReplyOutcome(caller, "Installed Layout Diagnostic", SafeDiagnosticRunner.RunInstalledLayouts());
					break;

				case "safe":
					caller.Reply("[AdvancedKeybinds] Running safe diagnostics (no physical-key test)…", Color.LightYellow);
					ReplyOutcome(caller, "Internal Diagnostic", SafeDiagnosticRunner.RunInternal());
					ReplyOutcome(caller, "Installed Layout Diagnostic", SafeDiagnosticRunner.RunInstalledLayouts());
					caller.Reply("[AdvancedKeybinds] Live/Route/Timing were not started.", Color.LightGray);
					break;

				case "live-start":
					string start = LiveInputDiagnosticSession.Start();
					caller.Reply(
						start == "started"
							? "[AdvancedKeybinds] LIVE INPUT DIAGNOSTIC STARTED. Press physical keys, then use /akdiag live-stop. Emergency stop: the same command or unload the mod."
							: "[AdvancedKeybinds] Live Input Diagnostic is already active.",
						start == "started" ? Color.LightGreen : Color.Orange);
					break;

				case "live-stop":
					ReplyOutcome(caller, "Live Input Diagnostic", LiveInputDiagnosticSession.Stop());
					if (!string.IsNullOrEmpty(LiveInputDiagnosticSession.LastRoutePath))
						caller.Reply("[AdvancedKeybinds] Route: " + DiagnosticReportSupport.PublicPath(LiveInputDiagnosticSession.LastRoutePath), Color.LightGray);
					if (!string.IsNullOrEmpty(LiveInputDiagnosticSession.LastTimingPath))
						caller.Reply("[AdvancedKeybinds] Timing: " + DiagnosticReportSupport.PublicPath(LiveInputDiagnosticSession.LastTimingPath), Color.LightGray);
					break;

				case "route":
					ReplyOutcome(caller, "Input Route Diagnostic", LiveInputDiagnosticSession.WriteCurrentRouteSnapshot());
					break;

				case "timing":
					ReplyOutcome(caller, "Timing Diagnostic", LiveInputDiagnosticSession.WriteCurrentTimingSnapshot());
					break;

				case "status":
					caller.Reply(
						"[AdvancedKeybinds] Live Input Diagnostic: "
						+ (LiveInputDiagnosticSession.IsActive ? "ACTIVE" : "inactive")
						+ ". Commands: internal, layout, live-start, live-stop, route, timing, safe.",
						LiveInputDiagnosticSession.IsActive ? Color.LightGreen : Color.LightGray);
					break;

				default:
					caller.Reply("[AdvancedKeybinds] Usage: " + Usage, Color.Orange);
					break;
			}
		}

		private static void ReplyOutcome(CommandCaller caller, string name, DiagnosticRunOutcome outcome)
		{
			if (outcome == null || !outcome.Ok)
			{
				caller.Reply(
					"[AdvancedKeybinds] " + name + " did not run: " + (outcome?.Message ?? "unknown"),
					Color.OrangeRed);
				return;
			}

			caller.Reply(
				"[AdvancedKeybinds] " + name
				+ " Total=" + outcome.Total
				+ " PASS=" + outcome.Pass
				+ " INFO=" + outcome.Info
				+ " EXPECTED_DIFFERENCE=" + outcome.ExpectedDifference
				+ " WARNING=" + outcome.Warning
				+ " FAIL=" + outcome.Fail
				+ " SKIPPED=" + outcome.Skipped
				+ " UNTESTED=" + outcome.Untested
				+ " UNAVAILABLE=" + outcome.Unavailable,
				outcome.Fail > 0 ? Color.OrangeRed : outcome.Warning > 0 ? Color.Orange : Color.LightGreen);
			caller.Reply(
				"[AdvancedKeybinds] Report: " + DiagnosticReportSupport.PublicPath(outcome.ReportPath),
				Color.LightGray);
		}
	}
}
