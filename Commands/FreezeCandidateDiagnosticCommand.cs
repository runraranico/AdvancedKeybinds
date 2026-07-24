using AdvancedKeybinds.Binding;
using AdvancedKeybinds.Diagnostics;
using Microsoft.Xna.Framework;
using Terraria.ModLoader;

namespace AdvancedKeybinds.Commands
{
	/// <summary>
	/// Runs Windows layout auto-diagnostics for freeze-candidate keys.
	/// Does not freeze any keys.
	/// </summary>
	public sealed class FreezeCandidateDiagnosticCommand : ModCommand
	{
		public override CommandType Type => CommandType.Chat;
		public override string Command => "akfreeze";
		public override string Usage => "/akfreeze candidates";
		public override string Description =>
			"Auto-diagnose freeze-candidate keys across Windows layouts (no freeze).";

		public override void Action(CommandCaller caller, string input, string[] args)
		{
			string op = args.Length == 0 ? "candidates" : args[0].Trim().ToLowerInvariant();
			if (op is not ("candidates" or "run" or "diag"))
			{
				caller.Reply("[AdvancedKeybinds] Usage: " + Usage + "  (does NOT freeze keys)", Color.Orange);
				return;
			}

			caller.Reply(
				"[AdvancedKeybinds] Freeze-candidate Windows layout auto-diagnostic starting (no freeze)…",
				Color.LightYellow);

			FreezeCandidateAutoDiagnosticRunner.Outcome outcome = FreezeCandidateAutoDiagnosticRunner.Run();
			caller.Reply(
				"[AdvancedKeybinds] " + outcome.Message,
				outcome.Ok ? Color.LightGreen : Color.OrangeRed);

			if (!outcome.Ok)
				return;

			if (!string.IsNullOrEmpty(outcome.LayoutInventoryPath))
				caller.Reply("[AdvancedKeybinds] Inventory: " + DiagnosticReportSupport.PublicPath(outcome.LayoutInventoryPath), Color.LightGray);
			if (!string.IsNullOrEmpty(outcome.AutoMappingPath))
				caller.Reply("[AdvancedKeybinds] Mapping: " + DiagnosticReportSupport.PublicPath(outcome.AutoMappingPath), Color.LightGray);
			if (!string.IsNullOrEmpty(outcome.SpecialKeysPath))
				caller.Reply("[AdvancedKeybinds] Special: " + DiagnosticReportSupport.PublicPath(outcome.SpecialKeysPath), Color.LightGray);
			if (!string.IsNullOrEmpty(outcome.GlobalSummaryPath))
				caller.Reply("[AdvancedKeybinds] Summary: " + DiagnosticReportSupport.PublicPath(outcome.GlobalSummaryPath), Color.LightGray);

			if (AkDiag.Enabled) AkDiag.Log("FreezeCandidateDiagnostic command finished ok=" + outcome.Ok);
		}
	}
}
