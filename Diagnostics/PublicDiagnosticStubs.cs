// In-tree public no-op stubs for tModLoader in-game Build+Reload (globs **/*.cs under this mod only).
// Canonical copy also kept at: ../AdvancedKeybinds.Dev/public-stubs/
// When EnableAkDiagnostics=true (dotnet), these are Compile-Removed and real diag bodies are linked.
// Public-build only (EnableAkDiagnostics!=true). Exact API surface for production call sites.
// No file I/O, no log strings, no retained diagnostic state.
using System;
using System.Collections.Generic;
using AdvancedKeybinds.Core;
using AdvancedKeybinds.Diagnostics;
using AdvancedKeybinds.Binding;
using Terraria.GameInput;

namespace AdvancedKeybinds.Binding
{
	internal static class JisOemInGameDiagnostic
	{
		public static bool IsSessionActive => false;
		public static void EndCaptureSessionAndFlush(string reason) { }
		public static void NoteCaptureAccept(string token, string collapseDiag, string reason) { }
		public static void NoteCaptureReject(string collapseDiag, string reason) { }
	}

	internal static class CaptureResponsivenessDiag
	{
		public static void Begin(IEnumerable<string> heldAtBegin, RegisterCaptureSession.Phase phase) { }
		public static void Cancel(string reason) { }
		public static void Poll(
			RegisterCaptureSession.Phase phaseBefore,
			RegisterCaptureSession.Phase phaseAfter,
			IEnumerable<string> held,
			IEnumerable<string> edges,
			bool accepted,
			string acceptedToken,
			string decisionReason,
			IEnumerable<string> blocked)
		{ }
		public static void NoteInternalUpdated() { }
		public static void NoteUiUpdated() { }
	}

	internal static class DisplayResolveDiag
	{
		public static void LogCapture(string token) { }
		public static void LogBindingSnapshot(string context, string actionId, ComboBinding binding) { }
		public static void LogSlotSet(string actionId, int slotIndex, string token) { }
		public static void LogSlotClear(string actionId, int slotIndex, string previousToken) { }
	}

}

namespace AdvancedKeybinds.Diagnostics
{
	// First-group (Trace / TwoKeyLiveFire / Ps5 / DefaultUnbound): no product stubs.
	// Bodies live in AdvancedKeybinds.Dev/diagnostics/first-group/; product call sites use #if AK_DIAGNOSTICS.

	public static class LiveInputDiagnosticSession
	{
		public static bool IsActive => false;
		public static void Shutdown() { }
		public static void ObserveCaptureDecision(bool accepted, string token, string reason) { }
	}

	public static class AdvancedKeybindsDiagnosticEvents
	{
		public static void ClearAllSubscribers() { }
	}

	/// <summary>Public no-op: real probe body is Compile-Removed outside AK_DIAGNOSTICS.</summary>
	internal static class GamePadControlTabProbe
	{
		public static void NoteComboPanelOpenedFromRow(string actionId, InputMode rowMode) { }
		public static void NoteComboPanelCapture(string actionId, string padToken, bool accepted) { }
	}

	/// <summary>Public no-op: body in AdvancedKeybinds.Dev/diagnostics/product-tree.</summary>




	public static class InventoryEscLiveVerifySystem
	{
		public static bool ForceEnabled { get; set; }
		public static void NoteFancyUiClose(
			bool allowed,
			bool padConsumeBlocked,
			bool samePress,
			bool keyboardEscapeDown)
		{ }
		public static void NoteInventoryJpPhase(string phase) { }
		public static void NoteToggleInvCall(Terraria.Player self, bool beforeInv, bool afterInv) { }
		public static void TickFromGameLoop(string source) { }
	}
}

namespace AdvancedKeybinds.Core
{
}

namespace AdvancedKeybinds.Patches
{

}
