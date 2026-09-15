// STUB: NativeGamepadSmartCursorDrawProbe disabled — not a ModSystem (no PostDrawInterface).
// Full source: _developer/archive/NativeGamepadCursorDiagnostics_2026-07-29/NativeGamepadSmartCursorDrawProbeSystem.cs.txt
namespace AdvancedKeybinds.Diagnostics
{
	/// <summary>No-op. Cursor draw probe hooks are not registered.</summary>
	internal static class NativeGamepadSmartCursorDrawProbeSystem
	{
		public static void ApplyHooks() { }

		public static bool HookAppliedForTests => false;

		public static void ForceUnloadForTests() { }
	}
}
