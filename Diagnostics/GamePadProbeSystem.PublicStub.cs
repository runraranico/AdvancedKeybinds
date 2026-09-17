// In-tree public no-op stubs for tModLoader in-game Build+Reload (globs **/*.cs under this mod only).
// Canonical copy also kept at: ../AdvancedKeybinds.Dev/public-stubs/
// When EnableAkDiagnostics=true (dotnet), these are Compile-Removed and real diag bodies are linked.
// Auto-generated public stub: no I/O, no diagnostic log kind strings.
using System;
using AdvancedKeybinds.Core;
using AdvancedKeybinds.Binding;
using Microsoft.Xna.Framework;
using Terraria.ModLoader;

namespace AdvancedKeybinds.Diagnostics
{
	public sealed class GamePadProbeSystem : ModSystem
	{
		public override void OnModLoad() { }
		public override void Unload() { }
		public static void AdvanceProbeFromInputUpdate() { }
		public static void LogCapture(int slotNumber, string gamePadToken, bool accepted, bool purpleCleared, string reason = null) { }
		public static void LogUiSuppressBegin(string gamePadToken) { }
		public static void LogUiSuppressEnd(string gamePadToken) { }
		public static void LogPanelCloseAttempt(string gamePadToken, string source, bool blocked) { }
		public static void LogParentUiTrigger(string gamePadToken, string triggerName, bool cleared) { }
		public static void LogCaptureEdge(string gamePadToken, int slotNumber, string phaseBefore, string phaseAfter, bool captureCompleted, bool purpleWaitCleared, bool keyboardOrMouseAlsoPresent, string note) { }
		public static void LogPadTabOpen(Terraria.GameInput.InputMode mode) { }
		public static void LogPadBindWaitBegin(string action, Terraria.GameInput.InputMode mode, string source) { }
		public static void LogPadBindWaitEnd(string action, Terraria.GameInput.InputMode mode) { }
		public static void LogPadBindAttempt(string action, string padToken, string vanillaValue, Terraria.GameInput.InputMode mode) { }
		public static void LogPadBindResult(string action, bool? accepted, Terraria.GameInput.InputMode storedMode, string note) { }
		public static void LogPanelPadNavBegin(ComboPanelPadFocus focus, string openToken) { }
		public static void LogPanelPadOpen(ComboPanelPadFocus focus, string openToken) { }
		public static void LogPanelOpenSource(string source, bool displayNameEnabled) { }
		public static void LogPanelMouseSessionOpen(string openSource, bool mouseInputEnabled, bool displayNameEnabled) { }
		public static void LogPanelMouseLeftPress(string openSource, int mouseX, int mouseY, bool insidePanel, string hoveredElement, string consumedBy) { }
		public static void LogPanelMouseSaveClick(string openSource, bool insideSaveButton, bool saveInvoked, bool closeInvoked, bool returnToControls, bool inputConsumed) { }
		public static void LogPanelOutsideClick(string openSource, bool insidePanel, bool saveInvoked, bool closeInvoked, bool returnToControls, string restoreAction, bool inputConsumed) { }
		public static void LogPanelMouseBlocked(string openSource, string target, string reason) { }
		public static void LogPanelDisplayNameDisabled(string reason, bool noticeVisible, bool navigationRegistered, bool mouseClickEnabled) { }
		public static void LogPanelDisplayNameEnabled(string source, bool noticeVisible, bool navigationRegistered) { }
		public static void LogPanelDisplayNameBlocked(string source, string input) { }
		public static void LogPanelOpenSourceClear(string previousSource, string reason) { }
		public static void LogPanelPadOpenGateEnd(string token) { }
		public static void LogPanelPadFocus(ComboPanelPadFocus from, ComboPanelPadFocus to, string input) { }
		public static void LogPanelPadConfirm(ComboPanelPadFocus focus) { }
		public static void LogPanelPadCaptureBegin(int slot) { }
		public static void LogPanelPadCaptureEnd(int slot, string token, bool accepted, string note = null) { }
		public static void LogPanelPadCaptureConsumeBegin(string token, int slot, bool accepted) { }
		public static void LogPanelPadNavSkipped(string token, string reason) { }
		public static void LogPanelPadCaptureReleaseGateEnd(string token) { }
		public static void LogPanelPadNumberEditBegin(int value) { }
		public static void LogPanelPadNumberChange(int from, int to, string input) { }
		public static void LogPanelPadNumberEditEnd(int value, bool committed) { }
		public static void LogPanelPadClearConfirmBegin() { }
		public static void LogPanelPadClearConfirmBegin(ComboPanelPadFocus focus) { }
		public static void LogPanelPadClearConfirmEnd(bool confirmed) { }
		public static void LogPanelPadClearConfirmEnd(bool confirmed, ComboPanelPadFocus focus) { }
		public static void LogPanelClearConfirmArm(string input, bool bindingChanged, int epoch) { }
		public static void LogPanelClearConfirmReleaseReady(int epoch) { }
		public static void LogPanelClearConfirmCommit(string input, bool bindingChanged, int epoch) { }
		public static void LogPanelClearConfirmCancel(string input) { }
		public static void LogPanelClearDoubleUseBlocked(string token) { }
		public static void LogPanelClearChangedBeforeCommit(string reason) { }
		public static void LogPanelPadUnexpectedCaptureBegin(ComboPanelPadFocus focus) { }
		public static void LogPanelPadSave() { }
		public static void LogPanelSaveCloseBegin(string action, string host, string focus, int epoch) { }
		public static void LogPanelSaveCloseEnd(bool isOpen, bool navigationActive, bool actionCleared, bool parentControlsOpen, int parentPoint) { }
		public static void LogPanelOpenAttempt(string action, string source, bool padAJustPressed, bool padADown, bool closeConsumeGate, int epoch) { }
		public static void LogPanelOpenBlocked(string action, string reason) { }
		public static void LogPanelRowConfirmPendingBegin(string host, int point, string action, int epoch) { }
		public static void LogPanelRowConfirmPendingConsume(string host, int point, string action, int epochDelta) { }
		public static void LogPanelRowConfirmPendingReject(int expectedPoint, int actualPoint, string expectedAction, string actualAction, string reason) { }
		public static void LogPanelRowConfirmPendingExpired(string action, int ageEpochs) { }
		public static void LogPanelRowConfirmPendingNotArmed(string reason) { }
		public static void LogPanelRowConfirmPoll(int currentPoint, string rowKind, string action, bool armAllowed, string reason) { }
		public static void LogKeybindRowClickHookEnter(string elementType, bool mousePhysical, bool padAJustPressed, int currentPoint, bool manualScrollActive) { }
		public static void LogKeybindRowActionResolve(bool success, string action, string reason) { }
		public static void LogKeybindRowOpenResolveEnter(string action) { }
		public static void LogKeybindRowOpenEarlyReturn(string reason) { }
		public static void LogPanelUnexpectedReopen(string action, string previousCloseReason, int epochDelta) { }
		public static void LogPanelSaveContext(string host, string action, int parentPointBefore, int parentPointAfter, bool reopened) { }
		public static void LogPanelPadCancel(string source) { }
		public static void LogPanelCancelDetail(bool pressed, bool captureActive, string comboBefore, string comboAfter, bool closeBlockedByCaptureGate) { }
		public static void LogCaptureBeginDiag(int captureId, string combo) { }
		public static void LogCaptureTokenDiag(string device, string token, string source) { }
		public static void LogPanelCloseComboDiag(string reason, string comboBeforeCommit) { }
		public static void LogCommitBeginDiag(string combo) { }
		public static void LogSaveBindingDiag(string action, string combo) { }
		public static void LogComboChangedDiag(string before, string after, string reason) { }
		public static void LogControlNavMove(string from, string to, string direction) { }
		public static void LogControlRowCatalog(int totalSnap, int navigable, int collapsedChildrenSkipped) { }
		public static void LogControlRowMapSummary(int totalSnap, int elementMapCount, int mapped, int unmapped, int hasDimensions, int navigable) { }
		public static void LogControlRowMapSample(string fields) { }
		public static void LogControlRowFailsafe(string reason, int totalSnap, string action) { }
		public static void LogControlNavCenterScroll(string target, float oldView, float newView, bool clamped, string clamp) { }
		public static void LogControlAccordionChildSkipped(string parent, string child, string reason) { }
		public static void LogControlSectionHeaderSample(string stableId, string elementType, string snapName, bool isNavigable, bool isHorizontalOnly, bool? expanded) { }
		public static void LogControlSectionHeaderSkipped(string stableId, string reason) { }
		public static void LogControlPadBHoldBegin(int thresholdMs, string host = null) { }
		public static void LogControlPadBHoldTrigger(long elapsedMs, string target, int pointId = int.MinValue) { }
		public static void LogControlPadBHoldEnd(bool triggered, long? elapsedMs) { }
		public static void LogControlPadBHoldUnavailable(string reason) { }
		public static void LogPanelLeftStickNav(float rawY, float normalizedY, string logicalDirection, string currentFocus, string targetFocus) { }
		public static void LogPanelLeftStickDirectionMismatch(float rawY, string expected, string actual) { }
		public static void LogPanelRowActionIdentity(int point, string focusedAction, string pendingAction, string clickAction, bool matched) { }
		public static void LogPanelRowActionIdentityMismatch(int point, string focusedAction, string clickAction, bool allowed) { }
		public static void LogIngameTriggerNavInput(string token, bool mainGameMenu, bool playerInventory, bool comboPanelOpen, bool controlsOpen, string inputMode, int currentPoint, bool navigatorInUse) { }
		public static void LogIngameTriggerNavState(string token, bool menuLeft, bool menuRight, bool hotbarMinus, bool hotbarPlus, bool mouseLeft, bool mouseRight, bool allowGamepadInstructions, bool gamepadAllowScrolling) { }
		public static void LogIngameTriggerNavSuppress(string token, bool beforeMenuLeft, bool afterMenuLeft, bool beforeMenuRight, bool afterMenuRight, string suppressOwner, bool suppressApplied) { }
		public static void LogIngameTriggerNavResult(string token, int beforePoint, int afterPoint) { }
		public static void LogIngameTriggerNavMode(string before, string after, string changedBy) { }
		public static void LogForeignUiNavInput(string uiType, string token, int currentPointBefore, string currentInputMode, bool navigatorInUse) { }
		public static void LogForeignUiNavResult(string uiType, int before, int after, int up, int down, int left, int right, float targetX, float targetY) { }
		public static void LogForeignUiAkInterference(string uiType, string owner, string trigger, bool cleared) { }
		public static void LogGlobalUiNavInput(string uiType, string token, int beforePoint, string inputMode, bool navigatorInUse, bool akControlsUiActive, bool akSuppressActive) { }
		public static void LogGlobalUiNavLink(int point, int up, int down, int left, int right, bool targetExists, bool targetVisible, string targetUiType, string pointIdName = null, float posX = 0f, float posY = 0f) { }
		public static void LogGlobalUiNavResult(int beforePoint, int afterPoint, bool moved, bool officialLinkUsed, bool akInterference) { }
		public static void LogGlobalUiNavAkSuppress(string uiType, string owner, string trigger, bool cleared) { }
		public static void LogControlUiContext(string host, string currentStateType, bool manageControlsFound, string currentInputMode, int currentPoint, int pointCount, int highestPoint) { }
		public static void LogControlUiDpad(string host, string token, bool menuFlag, int beforePoint, int afterPoint) { }
		public static void LogControlSectionLink(string stableId, string elementType, int snapId, int listIndex, int pointId, bool navigable, int up, int down, float posX, float posY) { }
		public static void LogControlSectionLinkFailed(string stableId, string reason) { }
		public static void LogControlGridLink(string leftAction, string rightAction, int logicalRow, int leftPoint, int rightPoint) { }
		public static void LogControlGridVertical(string from, string to, string column, string direction) { }
		public static void LogControlGridHorizontal(string from, string to, string direction) { }
		public static void LogControlGridColumnLost(string from, string actualTo, string expectedColumn) { }
		public static void LogControlManualScrollBegin(int preferredColumn) { }
		public static void LogControlManualScrollEnd(int focusPoint) { }
		public static void LogPanelInputConsumeBegin(string token, string reason, int epoch) { }
		public static void LogPanelInputConsumeBlock(string token, string target) { }
		public static void LogPanelInputConsumeEnd(string token, string reason) { }
		public static void LogControlBackDuplicateBlocked(string token, string firstAction, string blockedAction) { }
		public static void LogControlNavSelectedNotVisible(string target) { }
		public static void LogControlNavScroll(float oldView, float newView, string target) { }
		public static void LogControlNavSkipScrollItem(string from, string to, string direction) { }
		public static void LogControlAccordionToggleBegin(string current, string stableId, bool? expandedBefore) { }
		public static void LogControlAccordionToggleEnd(string stableId, bool? expandedAfter, string restored) { }
		public static void LogControlNavRestore(string oldPoint, string newPoint, string reason) { }
		public static void LogControlNavUnexpectedFixedUi(string from, string to, string reason) { }
		public static void LogPanelPadBackBegin(string mode, string parentScreen) { }
		public static void LogPanelClose() { }
		public static void LogReturnParentControls() { }
		public static void LogPanelParentRestore(string actionId, string bindingStableId, string parentHeaderStableId, string matchKind, int restorePoint, float viewPosition) { }
		public static void LogPanelPadBackEnd(string token) { }
		public static void LogNavRebuildBegin(string currentStableId) { }
		public static void LogNavRebuildEnd(string restoredStableId) { }
		public static void LogRestoreResult(bool success, bool fallback) { }
		public static void LogNavRestoreFailed(string currentStableId, string reason) { }
		public static void LogScrollDuplicate(string target, int epoch) { }
		public static void LogControlsClose(string source) { }
		public static void LogControlsCloseLeak(string source) { }
		public static void LogPanelBackgroundInputBlockBegin(string host) { }
		public static void LogPanelBackgroundInputBlockEnd(string reason) { }
		public static void LogPanelBackgroundInputBlock(string token, string target, string trigger, bool cleared) { }
		public static void LogPanelInputOwnership(string token, string owner, bool parentBlocked, bool gameplayBlocked) { }
		public static void LogPanelOutsideClickBack(bool physicalMouse, bool panelClosed, bool parentActionBlocked) { }
		public static void LogPanelEditHistoryBegin(string action, string initialSlots, string holdMode, int holdMs) { }
		public static void LogPanelEditHistoryPush(string reason, int depth) { }
		public static void LogPanelEditHistoryEnd(string reason, int discardedDepth, string action = null) { }
		public static void LogPanelUndoApply(string reason, int depthBefore, int depthAfter, bool panelStillOpen) { }
		public static void LogPanelUndoUnavailable(string reason) { }
		public static void LogControlTopNavElement(int index, string stableId, string elementType, string snapName, int snapId, int pointId, Vector2 position, bool visible, bool navigable, int left, int right) { }
		public static void LogControlTopNavInput(string token, int beforePoint, string beforeStableId, string expectedNeighbor) { }
		public static void LogControlTopNavResult(int beforePoint, int afterPoint, string afterStableId, bool moved, bool akUndoSkipped) { }
		public static void LogControlTopNavSkip(string skippedStableId, string reason) { }
		public static void LogControlTopNavFocus(string stableId, int pointId, bool highlighted) { }
		public static void LogControlTopNavConfirm(string stableId, int pointId, string token, bool invoked, bool executedOnce) { }
		public static void LogAkSectionConfirmLifetimeBegin(int epoch, int pointId, string stableId, bool padAPressed, bool? expandedBefore) { }
		public static void LogAkSectionConfirmExactRestore(int epoch, string stableId, int pointId, bool padACurrent, bool sessionActive) { }
		public static void LogAkSectionConfirmRelease(int epoch, string stableId, int currentPoint, string currentStableId, bool sessionActive) { }
		public static void LogAkSectionPostRelease3002(int epoch, int msSincePress, int msSinceRelease, int fromPoint, string fromStableId, int toPoint, string callerType, string callerMethod, string stack, bool padAPressed, bool padACurrent, bool padAReleased, bool menuConfirmPressed, bool menuConfirmCurrent, bool menuConfirmReleased, bool duringSetup, bool duringOnActivate, bool duringUiUpdate, bool duringDraw, bool restoreSessionActive, bool manualScrollActive, float rightStickY, bool manualScrollSyncRan, int manualScrollSelectedPoint, string manualScrollSelectedStableId, bool applied) { }
		public static void LogAkSectionConfirmLifetimeEnd(string originalStableId, int finalPoint, string finalStableId, bool movedToGamepadTab, string cause, bool fixedOk) { }
		public static void LogAkManualScrollFocusSync(int epoch, int currentPointBefore, string currentStableIdBefore, float rightStickY, int selectedPoint, string selectedStableId, int currentPointAfter, bool triggeredDuringSectionConfirm) { }
		public static void LogAkModBindingRowClassify(int point, string elementType, string action, string parentType, string parentStableId, string kind, bool hasBindingFlag, bool hasSectionChildFlag, bool navigable) { }
		public static void LogAkModBindingConfirm(int point, string action, string kind, bool bindingAllowed, string reason) { }
		public static void LogAkSectionFocusSave(int epoch, int pointId, string stableId, string elementType, bool? expandedBefore) { }
		public static void LogAkControlsRebuildDetected(int epoch, string savedStableId, int currentPoint, string currentStableId, bool gamepadTabTransient) { }
		public static void LogAkControlsCatalogReady(int epoch, string savedStableId, int resolvedPoint, string resolvedStableId, bool exactStableIdMatch) { }
		public static void LogAkSectionFocusRestore(int epoch, int fromPoint, string fromStableId, int toPoint, string toStableId, bool exactStableIdMatch, bool beforeDraw) { }
		public static void LogAkSectionFocusVerify(int epoch, string requestedStableId, int finalPoint, string finalStableId, bool success, bool fallbackUsed) { }
		public static void LogAkSectionFocusMismatch(int epoch, string requestedStableId, string resolvedStableId, int pointId, string reason) { }
		public static void LogControlTopNavChange(int epoch, int sequence, string caller, int from, int to, string reason) { }
		public static void LogControlTopNavResultDetail(int epoch, int beforePoint, int expectedPoint, int afterPoint, int moveCount, bool undoSkipped, bool duplicateInput, bool pointOverwritten) { }
		public static void LogControlResetElement(string action, string elementType, bool visible, bool navigable, int pointId, Vector2 position, int matchedBindingPoint, string matchMethod) { }
		public static void LogControlResetLink(string action, int bindingPoint, int resetPoint, int bindingRight, int resetLeft, int resetUp, int resetDown) { }
		public static void LogControlResetNav(string direction, string fromAction, string fromColumn, string toAction, string toColumn, bool moved) { }
		public static void LogControlResetConfirm(string action, string token, bool invoked, bool executedOnce, bool focusRestored) { }
		public static void LogControlResetLeftInput(int epoch, string token, int beforePoint, string beforeAction, int linkedLeftPoint, string linkedLeftAction) { }
		public static void LogControlResetLeftChange(int epoch, int sequence, string caller, int fromPoint, string fromColumn, int toPoint, string toColumn, string reason) { }
		public static void LogControlResetLeftResult(int epoch, int finalPoint, string finalAction, string finalColumn, int moveCount, bool duplicateInput, bool restoredToReset, bool rebuildOccurred) { }
		public static void LogControlResetFocusVisual(int currentPoint, string currentColumn, bool bindingHighlighted, bool resetHighlighted, string mouseHoverElement) { }
		public static void LogControlResetVerticalNav(string token, string fromAction, string toAction, int fromPoint, int toPoint) { }
		public static void LogControlResetCenterScroll(string targetAction, int targetPoint, float targetTop, float targetCenter, float viewportTop, float viewportBottom, float oldScroll, float newScroll, bool clamped, bool sharedWithBindingColumn) { }
		public static void LogControlResetScrollSkipped(string targetAction, string reason) { }
	}
}
