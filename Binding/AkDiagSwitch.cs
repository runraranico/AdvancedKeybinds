// Managed by tools/Set-AkDiagEnabled.ps1 — toggle the On value via that script (not by hand).
// In-game Roslyn builds ignore csproj DefineConstants; this .cs flag is what actually gates digs.
namespace AdvancedKeybinds.Binding
{
	internal static class AkDiagSwitch
	{
		/// <summary>
		/// Compile-time constant so <c>if (AkDiag.Enabled) { … }</c> call sites are eliminated
		/// when OFF (no method call, no string.Join / interpolation / List alloc).
		/// Toggle with tools/Set-AkDiagEnabled.ps1 then Build + Reload.
		/// </summary>
		public const bool On = false;
	}
}
