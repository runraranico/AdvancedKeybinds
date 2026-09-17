using System;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// ComboPanel ESC 状態遷移（画像1→画像3）の純粋ポリシー。
	/// 登録完了とパネル閉じは別処理。同一押しの閉じを物理キーアップまで禁止する。
	/// Keyboard Escape と GamePad Start の取得は分離し、Inventory 共有チャネルへの誤消去を防ぐ。
	/// </summary>
	public static class ComboPanelEscapeRegistrationPolicy
	{
		public static bool IsRegistrationWait(
			bool awaitingSlotKey,
			bool slotCaptureActive,
			int capturingSlot,
			bool awaitingKeyInfoPick)
			=> awaitingKeyInfoPick
				|| awaitingSlotKey
				|| slotCaptureActive
				|| capturingSlot is >= 1 and <= 3;

		/// <summary>同一入力ティック内: 登録待ち解除後も親戻る／Inventory を抑止。</summary>
		public static bool IsEscapeCaptureTick(
			bool registrationWait,
			bool escapeRegisteredThisTick,
			bool escapeConsumedThisTick)
			=> registrationWait || escapeRegisteredThisTick || escapeConsumedThisTick;

		/// <summary>
		/// 物理 Escape がまだ押されている間は dismiss 禁止。
		/// Main.keyState は Consume で空になり得るため、ゲート解除は physicalEscapeDown を使うこと。
		/// </summary>
		public static bool ShouldClearDismissKeyUpGate(bool blockDismissUntilEscapeKeyUp, bool physicalEscapeDown)
			=> blockDismissUntilEscapeKeyUp && !physicalEscapeDown;

		public static bool ShouldDismissPanelOnEscape(
			bool registrationWait,
			bool escapeRegisteredThisTick,
			bool escapeConsumedThisTick,
			bool blockDismissUntilEscapeKeyUp)
			=> !IsEscapeCaptureTick(registrationWait, escapeRegisteredThisTick, escapeConsumedThisTick)
				&& !blockDismissUntilEscapeKeyUp;

		/// <summary>
		/// Keyboard Escape 専用の親メニュー swallow。Pad ゲートは含めない。
		/// パネル閉鎖かつ Controls/options 以外では絶対に false（通常ゲームの ESC を殺さない）。
		/// </summary>
		public static bool ShouldSwallowKeyboardEscapeForParent(
			bool panelOpen,
			bool keyInfoNameEditing,
			bool registrationWait,
			bool escapeRegisteredThisTick,
			bool escapeConsumedThisTick,
			bool blockDismissUntilEscapeKeyUp,
			bool withinEscDismissGrace,
			bool keyboardEscapeDown,
			bool parentInterventionContext = true)
		{
			if (keyInfoNameEditing)
				return true;
			if (panelOpen)
				return true;

			// World / craft / inventory / title: never swallow Escape or clear Inventory for Start.
			if (!parentInterventionContext)
				return false;

			if (!TerrariaCancelInputPolicy.ShouldTreatRegistrationWaitAsEscapeSwallow(
				panelOpen,
				registrationWait,
				keyboardEscapeDown)
				&& !escapeRegisteredThisTick
				&& !escapeConsumedThisTick
				&& !blockDismissUntilEscapeKeyUp
				&& !withinEscDismissGrace)
				return false;

			if (!keyboardEscapeDown)
				return false;

			if (TerrariaCancelInputPolicy.ShouldTreatRegistrationWaitAsEscapeSwallow(
				panelOpen, registrationWait, keyboardEscapeDown))
				return true;
			if (withinEscDismissGrace)
				return true;
			if (blockDismissUntilEscapeKeyUp)
				return true;
			if (escapeRegisteredThisTick || escapeConsumedThisTick)
				return true;
			return false;
		}

		/// <summary>
		/// Strip keyboard Escape from keyState. Inventory/MenuInquiry はキーボード Escape 押下中のみ消す。
		/// Escape が上がっているときに Inventory を消すと GamePad Start（戻る）が同時に死ぬ。
		/// </summary>
		public static bool ShouldClearInventoryTriggersForEscapeConsume(bool keyboardEscapeDown)
			=> keyboardEscapeDown;

		/// <summary>
		/// Strip Escape from Main.keyState for parent while capture-tick / key-up / grace is active.
		/// Must NOT return true merely because the ComboPanel is open and idle — UI Update can call
		/// PrepareParentMenuForEscape before PlayerInput.HandleEscape; consuming early strips the
		/// Escape edge from Main.keyState so idle dismiss never sees escapeJustPressed.
		/// Registration wait / same-tick Escape save / key-up gate / dismiss grace still consume.
		/// Key-up / grace consume only while Escape is still physically down.
		/// </summary>
		public static bool ShouldConsumeEscapeForParent(
			bool registrationWait,
			bool escapeRegisteredThisTick,
			bool escapeConsumedThisTick,
			bool blockDismissUntilEscapeKeyUp,
			bool withinEscDismissGrace,
			bool panelOpen,
			bool physicalEscapeDown = true,
			bool parentInterventionContext = true)
		{
			// panelOpen alone must leave Escape for HandleEscape idle dismiss (historical contract).
			_ = panelOpen;
			_ = parentInterventionContext;

			if (IsEscapeCaptureTick(registrationWait, escapeRegisteredThisTick, escapeConsumedThisTick))
				return true;
			if (withinEscDismissGrace)
				return true;
			if (blockDismissUntilEscapeKeyUp && physicalEscapeDown)
				return true;
			return false;
		}

		/// <summary>
		/// Legacy swallow while ComboPanel owns Escape (open / same-tick / key-up / grace).
		/// Prefer <see cref="ShouldSwallowKeyboardEscapeForParent"/> — does not include Pad gates.
		/// </summary>
		public static bool ShouldBlockParentUiClose(
			bool panelOpen,
			bool escapeRegisteredThisTick,
			bool escapeConsumedThisTick,
			bool blockDismissUntilEscapeKeyUp,
			bool withinEscDismissGrace)
			=> panelOpen
				|| escapeRegisteredThisTick
				|| escapeConsumedThisTick
				|| blockDismissUntilEscapeKeyUp
				|| withinEscDismissGrace;

		/// <summary>
		/// IngameFancyUI.Close のみ。Pad:B 同一押しゲートは FancyUI 用。Inventory 消去には使わない。
		/// </summary>
		public static bool ShouldBlockParentFancyUiClose(
			bool panelOpen,
			bool keyInfoNameEditing,
			bool registrationWait,
			bool escapeRegisteredThisTick,
			bool escapeConsumedThisTick,
			bool blockDismissUntilEscapeKeyUp,
			bool withinEscDismissGrace,
			bool physicalEscapeDown,
			bool padBackCloseGate,
			bool closeInputConsumeGate,
			bool parentInterventionContext = true)
		{
			if (ShouldSwallowKeyboardEscapeForParent(
				panelOpen,
				keyInfoNameEditing,
				registrationWait,
				escapeRegisteredThisTick,
				escapeConsumedThisTick,
				blockDismissUntilEscapeKeyUp,
				withinEscDismissGrace,
				keyboardEscapeDown: physicalEscapeDown,
				parentInterventionContext: parentInterventionContext))
				return true;

			// Pad same-press gates only while still on Controls/options intervention context.
			if (parentInterventionContext && (padBackCloseGate || closeInputConsumeGate))
				return true;

			return false;
		}

		/// <summary>
		/// When ComboPanel is closed and keyboard Escape is up, stale key-up must clear.
		/// </summary>
		public static bool ShouldForceClearStaleDismissKeyUpGate(
			bool panelOpen,
			bool registrationWait,
			bool blockDismissUntilEscapeKeyUp,
			bool keyboardEscapeDown)
			=> !panelOpen
				&& !registrationWait
				&& blockDismissUntilEscapeKeyUp
				&& !keyboardEscapeDown;

		/// <summary>
		/// Display-name edit ended without Escape held: drop the dismiss key-up gate.
		/// Per-frame gate arming while typing must not leave sticky same-press residue
		/// that kills Controls ESC after mouse Save/Cancel back to 操作設定.
		/// </summary>
		public static bool ShouldClearDismissGateAfterNameEditEnds(bool keyboardEscapeDown)
			=> !keyboardEscapeDown;

		/// <summary>
		/// Panel Close (mouse Save/Cancel or ForceClose) must clear grace + protect frames
		/// together with the key-up gate. Clearing only the gate left EscDismissGrace armed
		/// so the next Escape on Controls looked like same-press and Inventory stayed dead.
		/// Idle ESC dismiss re-arms grace via BeginEscDismissGrace after Close.
		/// </summary>
		public static bool ShouldClearEscDismissGraceOnPanelClose()
			=> true;

		/// <summary>
		/// After display-name edit ends, the next Idle ESC panel dismiss must still arm the
		/// key-up gate (same press must not FancyUI.Close), but must NOT arm timed EscDismissGrace.
		/// Timed grace made Controls ESC feel dead around rename → panel ESC → settings ESC.
		/// Normal Idle ESC (no pending name-edit session) keeps timed grace.
		/// </summary>
		public static bool ShouldArmTimedEscDismissGraceAfterIdleClose(bool pendingAfterNameEditSession)
			=> !pendingAfterNameEditSession;

		/// <summary>
		/// While Idle-ESC release-protect is armed, always suppress parent Inventory / FancyUI
		/// close — including on Escape key-up (LIVE: FANCY_CLOSE_ALLOWED with kbEsc=0).
		/// </summary>
		public static bool ShouldSuppressParentDuringEscDismissReleaseProtect(bool protectFramesActive)
			=> protectFramesActive;

		/// <summary>
		/// LIVE 2026-09-16: protect=3 frames expired in ~33ms while SharedUi latch + ApplyAdvanced
		/// still re-injected Inventory JP → FancyUI.Close. Keep protect for this many Escape-up
		/// game updates after Idle ESC panel dismiss (new Escape press clears early).
		/// Inventory / MenuInquiry only — must not clear Jump/Throw gameplay Triggers.
		/// </summary>
		public const int IdleEscParentProtectRequiredEscapeUpUpdates = 12;

		/// <summary>
		/// Pad back / close-consume gates must not run Inventory suppress outside Controls/Combo context.
		/// </summary>
		public static bool ShouldApplyPadParentInventorySuppress(
			bool comboPanelOpen,
			bool controlsOrFancyUiContext)
			=> comboPanelOpen || controlsOrFancyUiContext;

		/// <summary>
		/// 画像1で ESC → 画像3: Escape 登録・capture 終了・panel 維持・parent back なし。
		/// </summary>
		public static bool IsValidImage1ToImage3Outcome(
			bool panelOpen,
			bool captureActive,
			bool escapeRegisteredInSlot,
			bool parentBackTriggered)
			=> panelOpen
				&& !captureActive
				&& escapeRegisteredInSlot
				&& !parentBackTriggered;

		/// <summary>通常時（非登録待ち）ESC → ComboPanel 閉じ許可。</summary>
		public static bool IsValidIdleEscapeClosesPanel(
			bool wasRegistrationWait,
			bool panelOpenAfter,
			bool parentBackAllowed)
			=> !wasRegistrationWait && !panelOpenAfter && parentBackAllowed;
	}
}
