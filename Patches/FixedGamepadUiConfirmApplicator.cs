using AdvancedKeybinds.Binding;
using AdvancedKeybinds.Core;
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
			}
			else if (cancelJp)
			{
			}

		}

		private static void SetTrigger(TriggersSet set, string name, bool value)
		{
			if (set?.KeyStatus == null || string.IsNullOrEmpty(name))
				return;
			set.KeyStatus[name] = value;
		}
	}
}
