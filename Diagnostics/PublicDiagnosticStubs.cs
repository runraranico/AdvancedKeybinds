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
	public static class KeyboardDiagMode
	{
		public static bool IsActive => false;
		public static bool LogToFile => false;
		public static IReadOnlyList<string> RecentLines => Array.Empty<string>();
		public static string LastLine => string.Empty;
		public static void Clear() { }
		public static void FlushLogHeader() { }
		public static void NotifyPhysicalKeyEvent(
			uint vk, ushort scan, bool extended,
			string captureToken, bool captureSuccess, string captureFailReason, string source)
		{ }
	}

	internal static class JisOemInGameDiagnostic
	{
		public static string LastReportPath => null;
		public static bool IsSessionActive => false;
		public static void BeginCaptureSession() { }
		public static void EndCaptureSessionAndFlush(string reason) { }
		public static void Observe(
			uint vk, ushort scan, bool extended, bool isDown, string source,
			string resolverTier, string resolverToken, string keysName, string vkToken, string scToken,
			IReadOnlyList<string> beforeCandidates, IReadOnlyList<string> afterCandidates,
			IReadOnlyList<string> captureResolverInput, string chooseMeasuredToken, string finalOrReject,
			long eventSequence = 0, string unifyNote = null)
		{ }
		public static void Observe(
			uint vk, ushort scan, bool extended, bool isDown, string source,
			PhysicalKeyResolver.ResolveResult resolved,
			IReadOnlyList<string> beforeCandidates, IReadOnlyList<string> afterCandidates,
			IReadOnlyList<string> captureResolverInput, string chooseMeasuredToken, string finalOrReject,
			long eventSequence = 0, string unifyNote = null)
		{ }
		public static void ObserveKeyUp(uint vk, ushort scan, bool extended, string source, string heldAfterUp, long eventSequence = 0) { }
		public static void NoteCaptureAccept(string token, string collapseDiag, string reason) { }
		public static void NoteCaptureReject(string collapseDiag, string reason) { }
		public static void NoteActivation(string savedToken, IEnumerable<string> pressedTokens, bool wouldFire) { }
	}

	internal static class CaptureResponsivenessDiag
	{
		public static void Begin(IEnumerable<string> heldAtBegin, RegisterCaptureSession.Phase phase) { }
		public static void Cancel(string reason) { }
		public static void NoteRawKeyDown(uint vk, ushort scan, bool extended, string source, bool isRepeat) { }
		public static void NoteRawKeyUp(uint vk, ushort scan, bool extended, string source) { }
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
		public static string FormatShown(string value) => value ?? string.Empty;
		public static void LogAliasChange(string storageKey, string previousDisplay, string newDisplay) { }
		public static void LogAliasesLoaded(int count, IReadOnlyDictionary<string, string> aliases) { }
		public static void LogCapture(string token) { }
		public static void LogBindingSnapshot(string context, string actionId, ComboBinding binding) { }
		public static void LogSlotSet(string actionId, int slotIndex, string token) { }
		public static void LogSlotClear(string actionId, int slotIndex, string previousToken) { }
	}

	internal static class BindingMutationDiag
	{
		public static void LogVanillaMutation(string actionId, string before, string after, string reason) { }
		public static void LogAdvancedMutation(string actionId, string before, string after, string reason) { }
		public static void ObserveControlsRow(string actionId) { }
		public static void ResetObservationBaseline() { }
		public static void LogWatchedBaseline(string phase) { }
		internal static string FormatComboSnapshot(ComboBinding combo)
			=> combo == null || combo.IsEmpty ? "(none)" : string.Empty;
	}
}

namespace AdvancedKeybinds.Diagnostics
{
	// First-group (Trace / TwoKeyLiveFire / Ps5 / DefaultUnbound): no product stubs.
	// Bodies live in AdvancedKeybinds.Dev/diagnostics/first-group/; product call sites use #if AK_DIAGNOSTICS.

	public static class LiveInputDiagnosticSession
	{
		public static bool IsActive => false;
		public static string LastRoutePath => null;
		public static string LastTimingPath => null;
		public static void Poll() { }
		public static void Shutdown() { }
		public static string Start() => string.Empty;
		public static string Stop() => string.Empty;
		public static string WriteCurrentRouteSnapshot() => string.Empty;
		public static string WriteCurrentTimingSnapshot() => string.Empty;
		public static void ObserveCaptureDecision(bool accepted, string token, string reason) { }
		public static void ObserveActionExecution(string actionId) { }
		public static void ObserveHold(
			string actionId, string phase, double engineHeldMs, int thresholdMs, bool holdOnRelease)
		{ }
	}

	public static class AdvancedKeybindsDiagnosticEvents
	{
		public static bool HasKeyMessageSubscribers => false;
		public static bool HasRawInputSubscribers => false;
		public static bool HasImeStateSubscribers => false;
		public static bool HasCaptureEvaluationSubscribers => false;
		public static void ClearAllSubscribers() { }
	}

	internal static class DiagnosticEventPublisher
	{
		public static void PublishKeyMessage(
			string messageName, uint virtualKey, ushort scanCode, bool extended, bool isKeyDown, long lParam)
		{ }
		public static void PublishHook(
			uint virtualKey, ushort scanCode, bool extended, bool isKeyDown, bool isRepeat, bool injected)
		{ }
		public static void PublishRawInput(
			uint virtualKey, ushort makeCode, ushort rawFlags, bool isKeyUp, IntPtr deviceHandle)
		{ }
		public static void PublishImeState(DiagnosticImeState state, string source) { }
		public static void PublishCaptureEvaluation(DiagnosticKeyInput input, DiagnosticRegistrationResult result) { }
	}

	/// <summary>Public no-op: real probe body is Compile-Removed outside AK_DIAGNOSTICS.</summary>
	internal static class GamePadControlTabProbe
	{
		public static bool IsControllerTabVisible => false;
		public static InputMode ExpectedListInputMode => InputMode.Keyboard;
		public static void Apply() { }
		public static void ObserveAfterVanillaInputRebuild() { }
		public static void NoteComboPanelOpenedFromRow(string actionId, InputMode rowMode) { }
		public static void NoteComboPanelCapture(string actionId, string padToken, bool accepted) { }
	}

	/// <summary>Public no-op: body in AdvancedKeybinds.Dev/diagnostics/product-tree.</summary>
	internal static class ComboPanelRegistrationWaitDiag
	{
		internal static void Unload() { }
		internal static void ObserveRegistrationWait(
			bool registrationWaitNow,
			string caller,
			string reason,
			string actionId,
			int slot,
			int capturingSlot,
			bool awaitingSlotKey,
			bool slotCaptureIsCapturing,
			string capturePhase,
			string acceptedToken,
			string blockedAtBegin)
		{ }
		internal static void ForceLeave(string caller, string reason, string actionId) { }
		internal static void NoteCaptureCompleted(
			string token,
			int slot,
			bool accepted,
			bool finishKeyCaptureCalled,
			bool slotCaptureCancelled,
			int capturingSlotAfter,
			bool awaitingSlotKeyAfter,
			bool slotCaptureIsCapturingAfter,
			string capturePhaseAfter,
			string focusAfter,
			bool padNavVisibleAfter,
			bool confirmButtonStillHeld)
		{ }
		internal static void NoteHoldToggleRequest(
			string source,
			string target,
			string requestedValue,
			bool accepted,
			string deniedReason,
			bool registrationWait,
			int capturingSlot,
			bool awaitingSlotKey,
			bool slotCaptureIsCapturing,
			string capturePhase,
			string focus,
			bool padNavVisible,
			bool captureGateRaw,
			bool confirmButtonHeld)
		{ }
	}

	internal static class HoldHudProbe
	{
		public static bool ForceEnabled;
		public static bool Enabled => false;
		public static void Log(string message) { }
		public static void LogSurface(string reason) { }
		public static void LogState(string reason, AdvancedKeybindsConfig config) { }
		public static void LogCoordPipeline(
			string stage,
			Microsoft.Xna.Framework.Vector2 inputPixel,
			float normX,
			float normY,
			Microsoft.Xna.Framework.Vector2 drawPixel,
			int sw,
			int sh)
		{ }
		public static void LogDrawThrottle(string line) { }
		public static void LogFeetCoordThrottle(string line) { }
		public static void LogDragCheck(string reason, bool result, string details = null) { }
		public static void LogCustomInit(
			string reason,
			bool saved,
			Microsoft.Xna.Framework.Vector2? playerTopWorld = null,
			Microsoft.Xna.Framework.Vector2? playerCenterWorld = null,
			Microsoft.Xna.Framework.Vector2? screenPosition = null,
			Microsoft.Xna.Framework.Vector2? worldToScreenGamePx = null,
			Microsoft.Xna.Framework.Vector2? barSize = null,
			float? headGap = null,
			Microsoft.Xna.Framework.Vector2? beforeClampGamePx = null,
			Microsoft.Xna.Framework.Vector2? afterClampGamePx = null,
			int? surfaceW = null,
			int? surfaceH = null,
			string surfaceSource = null,
			Microsoft.Xna.Framework.Vector2? storedNorm = null,
			Microsoft.Xna.Framework.Vector2? roundTripPx = null,
			Microsoft.Xna.Framework.Vector2? roundTripErr = null)
		{ }
		public static void LogHitTest(
			Microsoft.Xna.Framework.Rectangle drawRect,
			Microsoft.Xna.Framework.Rectangle hitRect,
			Microsoft.Xna.Framework.Vector2 mousePx,
			Microsoft.Xna.Framework.Vector2 mouseUi,
			AdvancedKeybindsConfig config,
			string drawBranch,
			string hitBranch)
		{ }
	}

	public static class AkCpuMeasureSystem
	{
		public const int IdPlayerInputDetour = 0;
		public const int IdBindingInputEngine = 1;
		public const int IdWinKeySuppressorBlock = 2;
		public const int IdKeybindShortcut = 3;
		public const int IdHoldHudPostUpdateInput = 4;
		public const int IdHoldHudUpdateUI = 5;
		public const int IdHoldHudPostDraw = 6;
		public const int IdCaptureSurfaces = 7;
		public const int IdRefreshGamePixelSurface = 8;
		public const int IdZoomTick = 9;
		public const int IdHoldHudProbeLog = 10;
		public static bool ForceEnabled;
		public static void TickPhase(string source) { }
		public static void Begin(int id) { }
		public static void End(int id) { }
	}

	public static class ZoomKeyLiveVerifySystem
	{
		public static bool ForceEnabled;
		public static void TickFromGameLoop(string source) { }
	}

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
		public static void TryInstallToggleInvHook() { }
		public static void TickFromGameLoop(string source) { }
		/// <summary>No-op. Product rearm is <see cref="AdvancedKeybinds.Core.InventoryEscapeRearm"/>.</summary>
		public static void TryRearmInventoryEscapeAfterAkSuppress() { }
	}
}

namespace AdvancedKeybinds.Core
{
	public static class PhysicalKeyDupDiag
	{
		public static Action<string> Emit;
		public static bool ResolveTraceEnabled;
		public static string SessionLogPath => null;
		public static void Configure(Action<string> emit, string sessionLogPath = null, bool enableResolveTrace = false) { }
		public static void Reset() { }
		public static void Write(string message) { }
		public static void LogDupCheck(
			string caller, int targetSlot, int existingSlot, string existingToken, string incomingToken,
			bool tokensEqual, bool samePhysical, bool physicalIdEqual, string result, string reason)
		{ }
		public static void LogResolveTrace(
			string inputToken, string canonical, string branch, bool success, string physicalId)
		{ }
	}
}

namespace AdvancedKeybinds.Patches
{
	internal static class WinChordDiag
	{
		public static void Log(string path, uint vk, string action, string detail) { }
	}

	internal static class StartMenuDiag
	{
		public static void LogHook(uint vk, bool isDown, bool blocked, string note) { }
		public static void LogLl(uint vk, bool isDown, bool blocked, string note) { }
		public static void LogThread(uint vk, bool isDown, bool blocked, string note) { }
		public static void LogRaw(uint vk, bool isDown, bool blocked, string note) { }
		public static void LogWndProc(uint msg, uint vk, bool blocked, string note) { }
		public static void LogSyscmd(int cmd, bool blocked, string note) { }
		public static void LogForwarded(uint msg, IntPtr wParam) { }
		public static void LogInstall(string note) { }
	}
}
