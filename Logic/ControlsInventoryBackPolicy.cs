using System;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// 設定系 UI からキャラクター操作へ戻る契約（完成条件）。
	/// 対象画面:
	/// 1) 操作設定 (UIManageControls / IngameFancyUI)
	/// 2) Modの設定 (UIModConfig 等 / IngameFancyUI)
	/// 3) 設定メニュー (Main.ingameOptionsWindow / IngameOptions.Close)
	/// 経路: Inventory JustPressed → Player.ToggleInv →
	///   ingameOptionsWindow なら IngameOptions.Close、
	///   inFancyUI なら IngameFancyUI.Close。
	/// 3メニューの閉じるキーは Escape のみ。
	/// AdditionalOr=Tab はキャラクター操作画面でインベントリ開閉用（メニュー戻しには使わない）。
	/// </summary>
	public static class ControlsInventoryBackPolicy
	{
		/// <summary>
		/// FancyUI.Close（操作設定・Mod設定）および Inventory 経由の IngameOptions.Close を許可するか。
		/// ComboPanel / 表示名編集中、および Escape 同一押し残渣（gate / grace / release-protect）は拒否。
		/// LIVE: 表示名後 Idle ESC → PANEL_CLOSE のあと、ESC 離し瞬間に samePress が残ったまま
		/// keyboardEscapeDown=false だと旧条件では許可され操作設定が一瞬で閉じた。
		/// 設定メニュー戻しは Escape 押下時のみ想定（Tab 単独は Inventory を消して ToggleInv させない）。
		/// </summary>
		public static bool ShouldAllowFancyUiCloseFromControlsBack(
			bool comboPanelOpen,
			bool keyInfoNameEditing,
			bool escapeSamePressActive,
			bool keyboardEscapeDown,
			bool padBackCloseGate,
			bool closeInputConsumeGate)
		{
			if (comboPanelOpen || keyInfoNameEditing)
				return false;
			// Keyboard Escape back must not be blocked by Pad:B / close-consume same-press.
			// Those gates exist to stop Pad reuse of the same hold — not Esc menu leave.
			if (!keyboardEscapeDown && (padBackCloseGate || closeInputConsumeGate))
				return false;
			// Block for the whole same-press residue window (incl. Escape key-up + release-protect),
			// not only while Escape is physically down.
			if (escapeSamePressActive)
				return false;
			return true;
		}

		/// <summary>
		/// 3画面共通の戻り許可（FancyUI.Close / Inventory→IngameOptions.Close の前提）。
		/// </summary>
		public static bool ShouldAllowSettingsMenuBackClose(
			bool comboPanelOpen,
			bool keyInfoNameEditing,
			bool escapeSamePressActive,
			bool keyboardEscapeDown,
			bool padBackCloseGate,
			bool closeInputConsumeGate)
			=> ShouldAllowFancyUiCloseFromControlsBack(
				comboPanelOpen,
				keyInfoNameEditing,
				escapeSamePressActive,
				keyboardEscapeDown,
				padBackCloseGate,
				closeInputConsumeGate);

		/// <summary>
		/// 設定3メニューが「本当に」開いているか（ワールド ESC 死防止）。
		/// ManageControls / ModConfig は inFancyUI（またはタイトル MenuUI）中だけ true。
		/// FancyUI.Close 後に CurrentState が残っても、inFancyUI=false なら介入しない。
		/// </summary>
		public static bool IsSettingsMenuBackContext(
			bool ingameOptionsWindow,
			bool inFancyUI,
			bool gameMenu,
			bool manageControlsPresent,
			bool modConfigPresent)
		{
			if (ingameOptionsWindow)
				return true;
			if (gameMenu && manageControlsPresent)
				return true;
			if (!inFancyUI)
				return false;
			return manageControlsPresent || modConfigPresent;
		}

		/// <summary>
		/// After ComboPanel Idle ESC dismiss, Suppress / release-protect can leave
		/// <c>releaseInventory=false</c>. While still on settings menus with same-press clear,
		/// restore so the next Escape Inventory edge can ToggleInv → FancyUI.Close.
		/// Do not restore during same-press, panel open, or post-leave Inventory gate.
		/// </summary>
		public static bool ShouldRestoreReleaseInventoryForSettingsEscapeLeave(
			bool settingsMenusOpen,
			bool comboPanelOpen,
			bool escapeSamePressActive,
			bool settingsLeaveInventoryGateArmed,
			bool releaseInventoryAlreadyTrue)
		{
			if (!settingsMenusOpen || comboPanelOpen)
				return false;
			if (escapeSamePressActive)
				return false;
			if (settingsLeaveInventoryGateArmed)
				return false;
			if (releaseInventoryAlreadyTrue)
				return false;
			return true;
		}

		/// <summary>
		/// 設定3メニュー上で Tab AdditionalOr 由来の Inventory を消すか。
		/// Escape が上がっていて Tab が押されているときだけ true（メニューは Escape でのみ閉じる）。
		/// キャラ操作画面（settingsMenusOpen=false）では絶対に false — Tab でインベントリ開閉を維持。
		/// </summary>
		public static bool ShouldSuppressTabInventoryOnSettingsMenus(
			bool settingsMenusOpen,
			bool keyboardEscapeDown,
			bool keyboardTabDown)
		{
			if (!settingsMenusOpen)
				return false;
			if (keyboardEscapeDown)
				return false;
			return keyboardTabDown;
		}

		/// <summary>
		/// Inventory / MenuInquiry を Escape 由来として消してよいか。
		/// Escape 押下中の同一押しゲートだけ true。Tab 押下（Escape 上）では絶対に消さない
		/// （ただし設定メニュー上の Tab 単独は <see cref="ShouldSuppressTabInventoryOnSettingsMenus"/>）。
		/// </summary>
		public static bool ShouldClearInventoryForEscapeSamePress(
			bool escapeSamePressActive,
			bool keyboardEscapeDown)
			=> escapeSamePressActive && keyboardEscapeDown;

		/// <summary>
		/// PrepareParent / ShouldSwallow 相当。ComboPanel 閉鎖中は同一押し Escape だけ swallow。
		/// </summary>
		public static bool ShouldSwallowEscapeOnControls(
			bool comboPanelOpen,
			bool keyInfoNameEditing,
			bool escapeSamePressActive,
			bool keyboardEscapeDown)
		{
			if (keyInfoNameEditing || comboPanelOpen)
				return true;
			return ShouldClearInventoryForEscapeSamePress(escapeSamePressActive, keyboardEscapeDown);
		}

		/// <summary>設定メニュー3画面共通の Escape swallow 判定。</summary>
		public static bool ShouldSwallowEscapeOnSettingsMenus(
			bool comboPanelOpen,
			bool keyInfoNameEditing,
			bool escapeSamePressActive,
			bool keyboardEscapeDown)
			=> ShouldSwallowEscapeOnControls(
				comboPanelOpen,
				keyInfoNameEditing,
				escapeSamePressActive,
				keyboardEscapeDown);

		/// <summary>
		/// ToggleInv が選ぶ閉じ先の分類（自動テスト用）。製品は Terraria 本体が分岐する。
		/// </summary>
		public static string ResolveToggleInvCloseTarget(
			bool ingameOptionsWindow,
			bool inFancyUI)
		{
			if (ingameOptionsWindow)
				return "IngameOptions.Close";
			if (inFancyUI)
				return "IngameFancyUI.Close";
			return "None";
		}

		public static bool IsEscapeSamePressActive(
			bool escapeRegisteredThisTick,
			bool escapeConsumedThisTick,
			bool blockDismissUntilEscapeKeyUp,
			bool withinEscDismissGrace,
			bool protectParentReleaseFramesActive)
			=> escapeRegisteredThisTick
				|| escapeConsumedThisTick
				|| blockDismissUntilEscapeKeyUp
				|| withinEscDismissGrace
				|| protectParentReleaseFramesActive;

		/// <summary>
		/// After shortcut-opened settings Close, may we re-apply the pre-open inventory flag?
		/// ESC / 戻る leave uses forceGameplayReturn (character control) — never restore open inventory
		/// (that re-open after Suppress caused a one-frame inventory flash).
		/// </summary>
		public static bool ShouldRestoreInventoryAfterShortcutSettingsClose(
			bool restoreArmed,
			bool settingsMenusStillOpen,
			bool forceGameplayReturnLeaveContract = true)
		{
			if (!restoreArmed || settingsMenusStillOpen)
				return false;
			if (forceGameplayReturnLeaveContract)
				return false;
			return true;
		}

		/// <summary>
		/// Opening settings/controls/modconfig via shortcut while inventory is open: close inventory
		/// immediately so ESC leave cannot reveal then close it (flicker).
		/// </summary>
		public static bool ShouldCloseInventoryWhenOpeningShortcutSettings(bool playerInventoryOpen)
			=> playerInventoryOpen;

		/// <summary>
		/// Inventory value after settings Close under AK back contract.
		/// Escape leave from the 3 settings menus must land on character control:
		/// never leave Main.playerInventory true (vanilla Close forces true → flash).
		/// Prefer shortcut-remembered closed state; otherwise keep pre-close only when
		/// <paramref name="forceGameplayReturn"/> is false.
		/// </summary>
		public static bool ResolveInventoryAfterSettingsClose(
			bool menusFullyClosed,
			bool shortcutRestoreArmed,
			bool shortcutInventoryBeforeOpen,
			bool inventoryOpenBeforeClose,
			bool forceGameplayReturn = true)
		{
			if (!menusFullyClosed)
				return inventoryOpenBeforeClose;
			// ESC / settings leave: character control — no inventory via-flash.
			if (forceGameplayReturn)
				return false;
			if (shortcutRestoreArmed)
				return shortcutInventoryBeforeOpen;
			return inventoryOpenBeforeClose;
		}

		/// <summary>
		/// After settings Close on Escape, keep Inventory suppressed while Escape is down
		/// and while key-up settle remains so Escape release cannot open inventory
		/// (LIVE: TOGGLEINV #2 on ESC_UP after settings leave).
		/// </summary>
		public static bool ShouldSuppressInventoryUntilEscapeKeyUpAfterSettingsLeave(
			bool gateArmed,
			bool keyboardEscapeDown,
			int keyUpSettleFramesRemaining = 0)
			=> gateArmed && (keyboardEscapeDown || keyUpSettleFramesRemaining > 0);

		/// <summary>
		/// Clear the post-settings-leave gate only after Escape is up and key-up settle is done.
		/// Clearing on the first Escape-up tick allowed a second ToggleInv to open inventory.
		/// </summary>
		public static bool ShouldClearSettingsLeaveInventoryGate(
			bool gateArmed,
			bool keyboardEscapeDown,
			int keyUpSettleFramesRemaining = 0)
			=> gateArmed && !keyboardEscapeDown && keyUpSettleFramesRemaining <= 0;

		/// <summary>
		/// Map Escape-up update count → settle remaining.
		/// While Escape is down, settle stays armed (1) for the upcoming key-up update.
		/// While Escape is up, suppress for the first <paramref name="requiredEscapeUpUpdatesBeforeClear"/>
		/// game updates (same GameUpdateCount may Poll multiple times — count updates, not Polls).
		/// </summary>
		public static int ResolveSettingsLeaveKeyUpSettleRemaining(
			bool gateArmed,
			bool keyboardEscapeDown,
			int escapeUpGameUpdates,
			int requiredEscapeUpUpdatesBeforeClear = 1)
		{
			if (!gateArmed)
				return 0;
			if (keyboardEscapeDown)
				return 1;
			int required = requiredEscapeUpUpdatesBeforeClear > 0 ? requiredEscapeUpUpdatesBeforeClear : 1;
			if (escapeUpGameUpdates <= required)
				return 1;
			return 0;
		}

		/// <summary>
		/// Idle ComboPanel ESC release-protect: same Escape-up settle shape as settings leave,
		/// with a longer required Escape-up window (LIVE: 3-frame protect was too short).
		/// </summary>
		public static int ResolveIdleEscParentProtectSettleRemaining(
			bool protectArmed,
			bool keyboardEscapeDown,
			int escapeUpGameUpdates,
			int requiredEscapeUpUpdatesBeforeClear)
			=> ResolveSettingsLeaveKeyUpSettleRemaining(
				protectArmed,
				keyboardEscapeDown,
				escapeUpGameUpdates,
				requiredEscapeUpUpdatesBeforeClear);

		/// <summary>
		/// New Escape press after Idle-ESC protect already counted Escape-up updates:
		/// clear protect so intentional Controls leave can ToggleInv / FancyUI.Close.
		/// </summary>
		public static bool ShouldClearIdleEscParentProtectOnNewEscapePress(
			bool protectArmed,
			bool keyboardEscapeDown,
			int escapeUpGameUpdatesAlready)
			=> protectArmed && keyboardEscapeDown && escapeUpGameUpdatesAlready > 0;
	}
}
