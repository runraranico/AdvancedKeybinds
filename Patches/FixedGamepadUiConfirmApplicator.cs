using AdvancedKeybinds.Binding;
using AdvancedKeybinds.Core;
using AdvancedKeybinds.Diagnostics;
using AdvancedKeybinds.UI;
using Terraria;
using Terraria.GameInput;

namespace AdvancedKeybinds.Patches
{
	/// <summary>
	/// After vanilla UpdateInput: physical Pad:A/B → fixed UI confirm/cancel for menus.
	/// Does not require UsingGamepadUI (title often uses XBoxGamepad with usingPadUI=0).
	/// Injects only on JustPressed edge for the current frame; ComboPanel ownership skips.
	/// </summary>
	internal static class FixedGamepadUiConfirmApplicator
	{
		internal static void ClearRuntimeState()
		{
#if AK_DIAGNOSTICS
			FixedGamepadUiConfirmDiagSystem.Reset();
#endif
		}

		internal static void ApplyAfterVanillaInput()
		{
			bool subsystem = FnaGamePadProvider.IsSubsystemActive;
			bool usingPad = PlayerInput.UsingGamepad || PlayerInput.UsingGamepadUI;
			bool usingPadUi = PlayerInput.UsingGamepadUI;
			bool modePad = PlayerInput.CurrentInputMode == InputMode.XBoxGamepad;
			bool modePadUi = PlayerInput.CurrentInputMode == InputMode.XBoxGamepadUI;
			bool kbMouseOnly = PlayerInput.CurrentInputMode == InputMode.Keyboard
				|| PlayerInput.CurrentInputMode == InputMode.KeyboardUI
				|| PlayerInput.CurrentInputMode == InputMode.Mouse;

			bool padSession = FixedGamepadUiConfirmPolicy.IsPhysicalPadUiSession(
				subsystem, usingPad, usingPadUi, modePad, modePadUi, kbMouseOnly);

			bool talking = Main.LocalPlayer != null && Main.LocalPlayer.talkNPC >= 0;
			bool inGameUiVisible = Main.InGameUI != null && Main.InGameUI.IsVisible;
			bool menuOrUi = FixedGamepadUiConfirmPolicy.IsMenuOrUiConfirmContext(
				Main.gameMenu,
				Main.ingameOptionsWindow,
				Main.inFancyUI,
				inGameUiVisible,
				Main.playerInventory,
				talking,
				Main.editSign || Main.editChest,
				Main.mapFullscreen);
			bool menuOrUiCancel = FixedGamepadUiConfirmPolicy.IsMenuOrUiCancelContext(
				Main.gameMenu,
				Main.ingameOptionsWindow,
				Main.inFancyUI,
				Main.playerInventory,
				talking,
				Main.editSign || Main.editChest,
				Main.mapFullscreen);

			bool akOwns = TerrariaCancelInputPolicy.AkOwnsModalForFixedPadCancel(
				ComboPanel.IsOpen,
				SlotCapture.IsCapturing);
			bool closeGate = ComboPanelCloseInputConsumeGate.ShouldBlockParentKeybindRowOpen();

			bool confirmJp = subsystem
				&& FnaGamePadProvider.WasJustPressed(FixedGamepadUiConfirmPolicy.ConfirmToken)
				&& FixedGamepadUiConfirmPolicy.ShouldAcceptConfirmEdge(true, false);
			bool cancelJp = subsystem
				&& FnaGamePadProvider.WasJustPressed(FixedGamepadUiConfirmPolicy.CancelToken)
				&& FixedGamepadUiConfirmPolicy.ShouldAcceptCancelEdge(true, false);

			FixedGamepadUiConfirmPolicy.ResolveExclusiveEdges(
				confirmJp, cancelJp, out bool applyConfirmEdge, out bool applyCancelEdge);

			bool injectConfirm = FixedGamepadUiConfirmPolicy.ShouldInjectVanillaUiConfirm(
				padSession, menuOrUi, subsystem, akOwns, closeGate,
				applyConfirmEdge, confirmJp && cancelJp);

			bool injectCancel = FixedGamepadUiConfirmPolicy.ShouldInjectVanillaUiCancel(
				padSession, menuOrUiCancel, subsystem, akOwns,
				applyCancelEdge, confirmJp && cancelJp);

#if AK_DIAGNOSTICS
			bool mlBefore = TriggerOn(PlayerInput.Triggers?.JustPressed, "MouseLeft");
			bool mrBefore = TriggerOn(PlayerInput.Triggers?.JustPressed, "MouseRight");
			bool invBefore = TriggerOn(PlayerInput.Triggers?.JustPressed, "Inventory");
#endif

			if (injectConfirm)
			{
				SetTrigger(PlayerInput.Triggers?.JustPressed, FixedGamepadUiConfirmPolicy.VanillaUiConfirmTrigger, true);
				SetTrigger(PlayerInput.Triggers?.Current, FixedGamepadUiConfirmPolicy.VanillaUiConfirmTrigger, true);
				Main.mouseLeft = true;
				Main.mouseLeftRelease = true;

				if (FixedGamepadUiConfirmPolicy.ShouldClearJumpForConfirmDualFire(menuOrUi, true))
				{
					SetTrigger(PlayerInput.Triggers?.JustPressed, "Jump", false);
					SetTrigger(PlayerInput.Triggers?.Current, "Jump", false);
				}
			}

			if (injectCancel)
			{
				SetTrigger(PlayerInput.Triggers?.JustPressed, FixedGamepadUiConfirmPolicy.VanillaUiCancelTriggerPrimary, true);
				SetTrigger(PlayerInput.Triggers?.Current, FixedGamepadUiConfirmPolicy.VanillaUiCancelTriggerPrimary, true);
				SetTrigger(PlayerInput.Triggers?.JustPressed, FixedGamepadUiConfirmPolicy.VanillaUiCancelTriggerAlternate, true);
				SetTrigger(PlayerInput.Triggers?.Current, FixedGamepadUiConfirmPolicy.VanillaUiCancelTriggerAlternate, true);
				Main.mouseRight = true;
				Main.mouseRightRelease = true;
#if AK_DIAGNOSTICS
				KeybindEscapeDiagSystem.NoteCancelConsume(
					"FixedGamepadUiConfirmApplicator.injectCancel",
					"None",
					consumed: false);
#endif
			}
			else if (cancelJp)
			{
#if AK_DIAGNOSTICS
				string deny = FixedGamepadUiConfirmPolicy.ClassifyInjectDenyReason(
					padSession, menuOrUi, subsystem, akOwns, closeInputConsumeGateBlocking: false,
					applyConfirmEdge, applyCancelEdge, wantConfirm: false);
				KeybindEscapeDiagSystem.NoteCancelConsume(
					"FixedGamepadUiConfirmApplicator",
					deny ?? (akOwns ? "AkOwnsModal" : "Denied"),
					consumed: akOwns);
#endif
			}

#if AK_DIAGNOSTICS
			if (confirmJp || cancelJp || injectConfirm || injectCancel)
			{
				string denyConfirm = injectConfirm
					? null
					: FixedGamepadUiConfirmPolicy.ClassifyInjectDenyReason(
						padSession, menuOrUi, subsystem, akOwns, closeGate,
						applyConfirmEdge, applyCancelEdge, wantConfirm: true);
				string denyCancel = injectCancel
					? null
					: FixedGamepadUiConfirmPolicy.ClassifyInjectDenyReason(
						padSession, menuOrUiCancel, subsystem, akOwns, closeInputConsumeGateBlocking: false,
						applyConfirmEdge, applyCancelEdge, wantConfirm: false);

				FixedGamepadUiConfirmDiagSystem.ObserveDecision(
					gameMenu: Main.gameMenu,
					modeName: PlayerInput.CurrentInputMode.ToString(),
					usingPad: usingPad ? 1 : 0,
					usingPadUi: usingPadUi ? 1 : 0,
					padAJp: confirmJp ? 1 : 0,
					padBJp: cancelJp ? 1 : 0,
					fixedConfirm: injectConfirm ? 1 : 0,
					fixedCancel: injectCancel ? 1 : 0,
					denyConfirm: denyConfirm,
					denyCancel: denyCancel,
					mlBefore: mlBefore ? 1 : 0,
					mlAfter: TriggerOn(PlayerInput.Triggers?.JustPressed, "MouseLeft") ? 1 : 0,
					mrBefore: mrBefore ? 1 : 0,
					mrAfter: TriggerOn(PlayerInput.Triggers?.JustPressed, "MouseRight") ? 1 : 0,
					invBefore: invBefore ? 1 : 0,
					invAfter: TriggerOn(PlayerInput.Triggers?.JustPressed, "Inventory") ? 1 : 0,
					akOwns: akOwns ? 1 : 0,
					closeGate: closeGate ? 1 : 0,
					menuOrUi: menuOrUi ? 1 : 0,
					padSession: padSession ? 1 : 0);
			}
#endif
		}

		private static bool TriggerOn(TriggersSet set, string name)
		{
			if (set?.KeyStatus == null || string.IsNullOrEmpty(name))
				return false;
			return set.KeyStatus.TryGetValue(name, out bool on) && on;
		}

		private static void SetTrigger(TriggersSet set, string name, bool value)
		{
			if (set?.KeyStatus == null || string.IsNullOrEmpty(name))
				return;
			set.KeyStatus[name] = value;
		}
	}
}
