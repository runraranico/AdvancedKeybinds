namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// ComboPanel ESC 状態遷移（画像1→画像3）の純粋ポリシー。
	/// 登録完了とパネル閉じは別処理。同一押しの閉じを物理キーアップまで禁止する。
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

		public static bool ShouldConsumeEscapeForParent(
			bool registrationWait,
			bool escapeRegisteredThisTick,
			bool escapeConsumedThisTick,
			bool blockDismissUntilEscapeKeyUp,
			bool withinEscDismissGrace,
			bool panelOpen)
			=> IsEscapeCaptureTick(registrationWait, escapeRegisteredThisTick, escapeConsumedThisTick)
				|| blockDismissUntilEscapeKeyUp
				|| withinEscDismissGrace
				|| !panelOpen;

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
