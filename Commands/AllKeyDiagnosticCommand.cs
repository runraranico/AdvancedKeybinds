using AdvancedKeybinds.Binding;
using AdvancedKeybinds.Diagnostics;
using Microsoft.Xna.Framework;
using Terraria.ModLoader;

namespace AdvancedKeybinds.Commands
{
	/// <summary>
	/// Developer chat command: /akdiagallkeys
	/// Runs only when <see cref="AkDiagSwitch.On"/> is true. Never auto-runs during normal play.
	/// </summary>
	public sealed class AllKeyDiagnosticCommand : ModCommand
	{
		public override CommandType Type => CommandType.Chat;

		public override string Command => "akdiagallkeys";

		public override string Usage => "/akdiagallkeys";

		public override string Description =>
			"AKDIAG: run all-key register/save/reload/display diagnostic (temporary memory only).";

		public override void Action(CommandCaller caller, string input, string[] args)
		{
			if (!AkDiagSwitch.On)
			{
				caller.Reply(
					"[AdvancedKeybinds] /akdiagallkeys requires AKDIAG ON (AkDiagSwitch).",
					Color.OrangeRed);
				return;
			}

			caller.Reply("[AdvancedKeybinds] All-key diagnostic starting…", Color.LightYellow);

			AllKeyDiagnosticRunner.RunOutcome outcome = AllKeyDiagnosticRunner.Run();
			if (!outcome.Ok)
			{
				caller.Reply("[AdvancedKeybinds] All-key diagnostic failed: " + outcome.Message, Color.OrangeRed);
				return;
			}

			caller.Reply(
				"[AdvancedKeybinds] All-key diagnostic done. total=" + outcome.Total
				+ " PASS=" + outcome.Pass
				+ " WARNING=" + outcome.Warning
				+ " FAIL=" + outcome.Fail
				+ " SKIPPED=" + outcome.Skipped,
				Color.LightGreen);
			caller.Reply(
				"[AdvancedKeybinds] Report: " + DiagnosticReportSupport.PublicPath(outcome.ReportPath),
				Color.LightGray);
		}
	}
}
