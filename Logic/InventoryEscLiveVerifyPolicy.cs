using System;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// ゲーム内ライブ検証 / 設定メニュー ESC 戻り用の純判定。
	/// Core.Tests と InventoryEscLiveVerifySystem / PlayerInput 配線で共有する。
	/// </summary>
	public static class InventoryEscLiveVerifyPolicy
	{
		public enum Scene
		{
			WorldGameplay,
			ControlsFancyUI,
			ModConfigFancyUI,
			IngameOptions,
			Other
		}

		/// <summary>
		/// AK が Inventory を消したあと、設定3メニューで Escape 戻りを再武装してよいか。
		/// ComboPanel 同一押しゲート中は再武装しない。Escape 立ち上がりエッジのみ。
		/// </summary>
		public static bool ShouldRearmInventoryForSettingsEscapeLeave(
			bool settingsMenusOpen,
			bool comboPanelOpen,
			bool escapeSamePressActive,
			bool escapeRisingEdge,
			bool keyboardEscapeDown,
			bool inventoryJustPressedAlive,
			bool keyStatusContainsEscape,
			bool escapeHoldLatchActive = false,
			bool inventorySharedUiBackEscapeConfigured = false)
		{
			if (!settingsMenusOpen || comboPanelOpen)
				return false;
			bool escapeOwnedForRearm = keyStatusContainsEscape || inventorySharedUiBackEscapeConfigured;
			if (!escapeRisingEdge || !keyboardEscapeDown || !escapeOwnedForRearm)
				return false;
			if (escapeSamePressActive)
				return false;
			if (inventoryJustPressedAlive)
				return false;
			if (SharedUiBackEscapeEdgePolicy.ShouldBlockRearmForEscapeHold(escapeHoldLatchActive))
				return false;
			return true;
		}

		/// <summary>
		/// オープニング（プレイヤー選択等）: Escape → Inventory JP → BackButtonInUse。
		/// AdditionalOr 時にバニラ Inventory エッジが欠ける LIVE 対策。ワールド REARM は禁止のまま。
		/// </summary>
		public static bool ShouldRearmInventoryForTitleMenuEscapeBack(
			bool gameMenu,
			bool settingsMenusOpen,
			bool comboPanelOpen,
			bool escapeSamePressActive,
			bool escapeRisingEdge,
			bool keyboardEscapeDown,
			bool inventoryJustPressedAlive,
			bool keyStatusContainsEscape,
			bool escapeHoldLatchActive = false,
			bool inventorySharedUiBackEscapeConfigured = false)
		{
			if (!gameMenu || settingsMenusOpen || comboPanelOpen)
				return false;
			bool escapeOwnedForRearm = keyStatusContainsEscape || inventorySharedUiBackEscapeConfigured;
			if (!escapeRisingEdge || !keyboardEscapeDown || !escapeOwnedForRearm)
				return false;
			if (escapeSamePressActive)
				return false;
			if (inventoryJustPressedAlive)
				return false;
			if (SharedUiBackEscapeEdgePolicy.ShouldBlockRearmForEscapeHold(escapeHoldLatchActive))
				return false;
			return true;
		}

		/// <summary>
		/// キャラ操作画面の Inventory REARM。常に禁止。
		/// LIVE 2026-09-16: REARM_WORLD が Escape フリッカで ToggleInv を複数回起こした。
		/// Escape+AdditionalOr は BindingInputEngine の Escape AND ミラー + SharedUi latch が正経路。
		/// タイトル戻りは <see cref="ShouldRearmInventoryForTitleMenuEscapeBack"/>。
		/// 設定メニュー戻りは <see cref="ShouldRearmInventoryForSettingsEscapeLeave"/>。
		/// </summary>
		public static bool ShouldRearmInventoryForWorldEscapeEdge(
			bool settingsMenusOpen,
			bool comboPanelOpen,
			bool escapeRisingEdge,
			bool inventoryJustPressedAlive,
			bool keyStatusContainsEscape,
			bool settingsLeaveInventoryGateArmed = false,
			bool escapeHoldLatchActive = false,
			bool worldEscapeInventoryOneShotArmed = false)
		{
			_ = settingsMenusOpen;
			_ = comboPanelOpen;
			_ = escapeRisingEdge;
			_ = inventoryJustPressedAlive;
			_ = keyStatusContainsEscape;
			_ = settingsLeaveInventoryGateArmed;
			_ = escapeHoldLatchActive;
			_ = worldEscapeInventoryOneShotArmed;
			return false;
		}

		public static bool KeyStatusListContainsEscape(System.Collections.Generic.IReadOnlyList<string> assigned)
		{
			if (assigned == null)
				return false;
			for (int i = 0; i < assigned.Count; i++)
			{
				string t = assigned[i];
				if (string.IsNullOrEmpty(t))
					continue;
				if (AdvancedVanillaOwnershipMarker.IsMarker(t)
					&& t.Length > AdvancedVanillaOwnershipMarker.Prefix.Length)
					t = t.Substring(AdvancedVanillaOwnershipMarker.Prefix.Length);
				if (string.Equals(t, "Escape", StringComparison.OrdinalIgnoreCase))
					return true;
				if (PlainSingleTapPolicy.IsEscapeEquivalentToken(t))
					return true;
			}
			return false;
		}

		public static string JudgeSettingsEscapeLeave(
			bool inventoryJustPressedAlive,
			bool fancyUiCloseAllowed,
			bool menuStillOpenAfter)
		{
			if (!inventoryJustPressedAlive)
				return "FAIL_InventoryEdgeMissing";
			if (!fancyUiCloseAllowed)
				return "FAIL_FancyUiCloseBlocked";
			if (menuStillOpenAfter)
				return "FAIL_MenuStillOpen";
			return "PASS";
		}

		public static string JudgeWorldEscapeInventory(
			bool inventoryJustPressedAlive,
			bool playerInventoryOpenedOrToggled)
		{
			if (!inventoryJustPressedAlive)
				return "FAIL_InventoryEdgeMissing";
			if (!playerInventoryOpenedOrToggled)
				return "FAIL_InventoryDidNotToggle";
			return "PASS";
		}

		public static Scene ClassifyScene(
			bool ingameOptionsWindow,
			bool inFancyUI,
			bool manageControlsPresent,
			bool modConfigPresent,
			bool gameMenu)
		{
			if (ingameOptionsWindow)
				return Scene.IngameOptions;
			if (gameMenu && manageControlsPresent)
				return Scene.ControlsFancyUI;
			if (inFancyUI && manageControlsPresent)
				return Scene.ControlsFancyUI;
			if (inFancyUI && modConfigPresent)
				return Scene.ModConfigFancyUI;
			if (gameMenu)
				return Scene.Other;
			return Scene.WorldGameplay;
		}
	}
}
