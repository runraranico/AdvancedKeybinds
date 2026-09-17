using System;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// 設定系3メニュー（操作設定 / Modの設定 / 設定メニュー）から
	/// Escape のみでキャラ操作へ戻る経路の純シミュレータ。
	/// Tab AdditionalOr はメニュー上では Inventory を消して閉じない（キャラ操作の開閉用）。
	/// ポリシー真偽だけでは完成としない — Inventory エッジ生存・Close 許可・メニュー閉鎖・
	/// インベントリ非オープンまで一連で検証する。
	/// </summary>
	public static class SettingsMenuBackPipeline
	{
		public enum MenuKind
		{
			/// <summary>操作設定 — Main.inFancyUI + UIManageControls</summary>
			ControlsFancyUI,
			/// <summary>Modの設定 — Main.inFancyUI + UIModConfig</summary>
			ModConfigFancyUI,
			/// <summary>設定メニュー — Main.ingameOptionsWindow</summary>
			IngameOptions
		}

		public enum PressedKey
		{
			Escape,
			Tab
		}

		public sealed class AkGateState
		{
			public bool ComboPanelOpen;
			public bool KeyInfoNameEditing;
			public bool EscapeRegisteredThisTick;
			public bool EscapeConsumedThisTick;
			public bool BlockDismissUntilEscapeKeyUp;
			public bool WithinEscDismissGrace;
			public bool ProtectParentReleaseFramesActive;
			public bool PadBackCloseGate;
			public bool CloseInputConsumeGate;
		}

		public sealed class Result
		{
			public bool InventoryEdgeBeforeSwallow;
			public bool InventoryEdgeAfterSwallow;
			public bool ReleaseInventoryBeforeToggle;
			public bool FancyUiCloseAllowed;
			public string CloseTarget;
			public bool CloseInvoked;
			public bool CloseBlocked;
			public bool MenuClosed;
			public bool PlayerInventoryOpened;
			public bool ReturnedToGameplay;
			public bool SettingsLeaveInventoryGateArmed;
			public string FailReason;
		}

		/// <summary>
		/// 1 フレーム分: メニュー open → キー押下で Inventory エッジ → AK swallow → ToggleInv 分岐 → Close。
		/// vanilla Close は playerInventory=true にしやすいが、shortcut 復元契約どおり閉じ後は false を維持。
		/// Tab 単独は設定メニュー上で Inventory を消し、閉じない。
		/// </summary>
		public static Result SimulateIdleBackPress(
			MenuKind menu,
			PressedKey key,
			AkGateState gates = null,
			bool inventoryJustPressedFromKeyStatus = true,
			bool releaseInventory = true,
			bool playerInventoryBefore = false)
		{
			gates ??= new AkGateState();
			bool keyboardEscapeDown = key == PressedKey.Escape;
			bool keyboardTabDown = key == PressedKey.Tab;

			bool samePress = ControlsInventoryBackPolicy.IsEscapeSamePressActive(
				gates.EscapeRegisteredThisTick,
				gates.EscapeConsumedThisTick,
				gates.BlockDismissUntilEscapeKeyUp,
				gates.WithinEscDismissGrace,
				gates.ProtectParentReleaseFramesActive);

			var r = new Result {
				InventoryEdgeBeforeSwallow = inventoryJustPressedFromKeyStatus,
				ReleaseInventoryBeforeToggle = releaseInventory,
				CloseTarget = ResolveCloseTarget(menu)
			};

			bool clearInventory = ControlsInventoryBackPolicy.ShouldClearInventoryForEscapeSamePress(
				samePress, keyboardEscapeDown);
			// ComboPanel open / name edit: always clear parent Inventory (panel owns Escape).
			if (gates.ComboPanelOpen || gates.KeyInfoNameEditing)
				clearInventory = true;
			// Contract: settings menus close on Escape only — suppress Tab AdditionalOr Inventory.
			if (ControlsInventoryBackPolicy.ShouldSuppressTabInventoryOnSettingsMenus(
				settingsMenusOpen: true,
				keyboardEscapeDown,
				keyboardTabDown))
				clearInventory = true;

			r.InventoryEdgeAfterSwallow = r.InventoryEdgeBeforeSwallow && !clearInventory;

			r.FancyUiCloseAllowed = ControlsInventoryBackPolicy.ShouldAllowSettingsMenuBackClose(
				gates.ComboPanelOpen,
				gates.KeyInfoNameEditing,
				samePress,
				keyboardEscapeDown,
				gates.PadBackCloseGate,
				gates.CloseInputConsumeGate);

			bool ingameOptions = menu == MenuKind.IngameOptions;
			bool inFancyUi = menu == MenuKind.ControlsFancyUI || menu == MenuKind.ModConfigFancyUI;

			// Mirror Player.ToggleInv edge: needs controlInv rising via releaseInventory + Inventory.
			if (!r.InventoryEdgeAfterSwallow || !releaseInventory)
			{
				r.CloseInvoked = false;
				r.MenuClosed = false;
				r.PlayerInventoryOpened = false;
				r.ReturnedToGameplay = false;
				r.FailReason = !r.InventoryEdgeAfterSwallow
					? (keyboardTabDown && !keyboardEscapeDown
						? "TabInventorySuppressedOnSettingsMenu"
						: "InventoryEdgeClearedBySwallow")
					: "ReleaseInventoryFalse";
				return r;
			}

			// ToggleInv branch order (vanilla): options → fancyUI → … → open inventory
			if (ingameOptions)
			{
				r.CloseInvoked = true;
				r.CloseBlocked = false;
				r.MenuClosed = true;
				// Vanilla Close forces playerInventory=true; Escape leave forces gameplay (no via-inv).
				r.PlayerInventoryOpened = ControlsInventoryBackPolicy.ResolveInventoryAfterSettingsClose(
					menusFullyClosed: true,
					shortcutRestoreArmed: true,
					shortcutInventoryBeforeOpen: playerInventoryBefore,
					inventoryOpenBeforeClose: playerInventoryBefore,
					forceGameplayReturn: keyboardEscapeDown);
				r.SettingsLeaveInventoryGateArmed = keyboardEscapeDown;
				r.ReturnedToGameplay = !r.PlayerInventoryOpened;
				r.FailReason = r.PlayerInventoryOpened ? "InventoryOpenedAfterClose" : null;
				return r;
			}

			if (inFancyUi)
			{
				r.CloseInvoked = true;
				if (!r.FancyUiCloseAllowed)
				{
					r.CloseBlocked = true;
					r.MenuClosed = false;
					r.PlayerInventoryOpened = false;
					r.ReturnedToGameplay = false;
					r.FailReason = "FancyUiCloseBlocked";
					return r;
				}

				r.CloseBlocked = false;
				r.MenuClosed = true;
				r.PlayerInventoryOpened = ControlsInventoryBackPolicy.ResolveInventoryAfterSettingsClose(
					menusFullyClosed: true,
					shortcutRestoreArmed: true,
					shortcutInventoryBeforeOpen: playerInventoryBefore,
					inventoryOpenBeforeClose: playerInventoryBefore,
					forceGameplayReturn: keyboardEscapeDown);
				r.SettingsLeaveInventoryGateArmed = keyboardEscapeDown;
				r.ReturnedToGameplay = !r.PlayerInventoryOpened;
				r.FailReason = r.PlayerInventoryOpened ? "InventoryOpenedAfterClose" : null;
				return r;
			}

			// No settings menu — would open inventory (must not happen in our completion cases).
			r.CloseInvoked = false;
			r.MenuClosed = false;
			r.PlayerInventoryOpened = true;
			r.ReturnedToGameplay = false;
			r.FailReason = "OpenedInventoryInsteadOfClosingMenu";
			_ = playerInventoryBefore;
			return r;
		}

		public static string ResolveCloseTarget(MenuKind menu)
			=> menu switch {
				MenuKind.IngameOptions => "IngameOptions.Close",
				MenuKind.ControlsFancyUI => "IngameFancyUI.Close",
				MenuKind.ModConfigFancyUI => "IngameFancyUI.Close",
				_ => "None"
			};

		public static bool IsCompletionSuccess(Result r)
			=> r != null
				&& r.InventoryEdgeAfterSwallow
				&& r.MenuClosed
				&& r.ReturnedToGameplay
				&& !r.PlayerInventoryOpened
				&& !r.CloseBlocked
				&& string.IsNullOrEmpty(r.FailReason);

		/// <summary>
		/// After Escape closed a settings menu: same physical Escape still down must not
		/// open inventory (no via-inventory flash / second ToggleInv).
		/// <paramref name="keyUpSettleFramesRemaining"/> models the Escape-up settle window.
		/// </summary>
		public static Result SimulateHeldEscapeAfterSettingsLeave(
			bool settingsLeaveInventoryGateArmed,
			bool keyboardEscapeDown,
			bool inventoryJustPressedFromKeyStatus = true,
			int keyUpSettleFramesRemaining = 0)
		{
			var r = new Result {
				InventoryEdgeBeforeSwallow = inventoryJustPressedFromKeyStatus,
				SettingsLeaveInventoryGateArmed = settingsLeaveInventoryGateArmed,
				CloseTarget = "None"
			};

			int settle = keyUpSettleFramesRemaining;
			if (settingsLeaveInventoryGateArmed && keyboardEscapeDown)
			{
				settle = ControlsInventoryBackPolicy.ResolveSettingsLeaveKeyUpSettleRemaining(
					settingsLeaveInventoryGateArmed,
					keyboardEscapeDown,
					escapeUpGameUpdates: 0,
					requiredEscapeUpUpdatesBeforeClear: 1);
			}

			bool suppress = ControlsInventoryBackPolicy.ShouldSuppressInventoryUntilEscapeKeyUpAfterSettingsLeave(
				settingsLeaveInventoryGateArmed,
				keyboardEscapeDown,
				settle);
			r.InventoryEdgeAfterSwallow = r.InventoryEdgeBeforeSwallow && !suppress;
			r.PlayerInventoryOpened = r.InventoryEdgeAfterSwallow;
			r.ReturnedToGameplay = !r.PlayerInventoryOpened;
			r.MenuClosed = true;
			r.FailReason = r.PlayerInventoryOpened
				? "SameEscapePressOpenedInventoryAfterSettingsLeave"
				: null;

			if (ControlsInventoryBackPolicy.ShouldClearSettingsLeaveInventoryGate(
				settingsLeaveInventoryGateArmed, keyboardEscapeDown, settle))
				r.SettingsLeaveInventoryGateArmed = false;
			else
				r.SettingsLeaveInventoryGateArmed = settingsLeaveInventoryGateArmed;
			return r;
		}
	}
}
