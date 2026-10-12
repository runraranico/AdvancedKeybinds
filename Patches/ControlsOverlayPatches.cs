using System;
using System.Reflection;
using AdvancedKeybinds.Binding;
using AdvancedKeybinds.Core;
using AdvancedKeybinds.Diagnostics;
using AdvancedKeybinds.UI;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.GameInput;
using Terraria.ModLoader;
using Terraria.UI;

namespace AdvancedKeybinds.Patches
{
	/// <summary>
	/// Draws the combo overlay panel on top of the controls menu (works both in the main-menu settings
	/// and the in-game controls screen, since both use UIManageControls). While the panel is open it also
	/// freezes the underlying controls menu so background buttons ("Reset to default") can't be clicked
	/// through the panel, and blocks IngameFancyUI.Close so Escape only closes the panel, not the menu.
	/// </summary>
	internal static class ControlsOverlayPatches
	{
		private delegate void orig_Draw(UIState self, SpriteBatch spriteBatch);
		private delegate void orig_UIUpdate(UserInterface self, GameTime gameTime);
		private delegate void orig_FancyClose();
		private delegate void orig_ElementUpdate(UIElement self, GameTime gameTime);

		public static void Apply()
		{
			HookDraw();
			HookBackgroundUpdate();
			HookFancyClose();
			HookOptionsClose();
			HookManageControlsUpdate();
		}

		private static void HookDraw()
		{
			MethodInfo draw = typeof(UIState).GetMethod("Draw", BindingFlags.Instance | BindingFlags.Public,
				null, new[] { typeof(SpriteBatch) }, null);

			if (draw == null)
			{
				AdvancedKeybinds.Instance?.Logger.Warn("Advanced Keybinds could not hook UIState.Draw; the combo panel will not render.");
				return;
			}

			ModDetours.Apply(draw, (orig_Draw orig, UIState self, SpriteBatch spriteBatch) => {
				orig(self, spriteBatch);

				if (self != null && self.GetType().Name.Contains("UIManageControls"))
				{
					KeybindUiWorldInputGuard.NotifyManageControlsUiPresent();
					KeybindingUiPatches.DrawControlsHeaderTip(self, spriteBatch);
					ComboPanel.DrawAndHandle(spriteBatch);
				}
			});
		}

		/// <summary>
		/// While the combo panel is open, run every background UserInterface update with the cursor moved
		/// off-screen. This stops the controls menu rows/buttons (e.g. "Reset to default" behind the Save
		/// button) AND any other mod UI (Recipe Browser, etc.) from hovering or receiving the click the
		/// panel is handling. Mouse-button state still updates normally, so nothing desyncs. The condition
		/// is intentionally broad (any UserInterface) because the in-game controls menu's CurrentState is
		/// not the UIManageControls type directly, and the stray tooltips come from other UIs entirely.
		/// </summary>
		private static void HookBackgroundUpdate()
		{
			MethodInfo update = typeof(UserInterface).GetMethod("Update", BindingFlags.Instance | BindingFlags.Public,
				null, new[] { typeof(GameTime) }, null);

			if (update == null)
			{
				AdvancedKeybinds.Instance?.Logger.Warn("Advanced Keybinds could not hook UserInterface.Update; clicks may pass through the combo panel.");
				return;
			}

			ModDetours.Apply(update, (orig_UIUpdate orig, UserInterface self, GameTime gameTime) => {
#if AK_DIAGNOSTICS
				ComboPanelIdleEscPathDiag.Note("UserInterface.Update.enter");
#endif
				// Only while ComboPanel owns Escape — never clear Inventory on every UI tick game-wide.
				if (ComboPanel.IsOpen || ComboPanel.ShouldSwallowMenuEscape())
					ComboPanel.PrepareParentMenuForEscape();

				if (ComboPanel.ShouldBlockBackgroundInput())
				{
					ComboPanel.PollSlotCaptureIfActive();
					int savedX = Main.mouseX;
					int savedY = Main.mouseY;
					Main.mouseX = -1000;
					Main.mouseY = -1000;
					try
					{
						orig(self, gameTime);
					}
					finally
					{
						Main.mouseX = savedX;
						Main.mouseY = savedY;
					}

					if (ComboPanel.IsOpen || ComboPanel.ShouldSwallowMenuEscape())
						ComboPanel.PrepareParentMenuForEscape();
#if AK_DIAGNOSTICS
					ComboPanelIdleEscPathDiag.Note("UserInterface.Update.exit", "blockedBg=1");
#endif
					return;
				}

				orig(self, gameTime);
				if (ComboPanel.IsOpen || ComboPanel.ShouldSwallowMenuEscape())
					ComboPanel.PrepareParentMenuForEscape();
#if AK_DIAGNOSTICS
				ComboPanelIdleEscPathDiag.Note("UserInterface.Update.exit", "blockedBg=0");
#endif
			});
		}

		/// <summary>
		/// UIManageControls can read MenuInquiry during its own Update; strip it around that pass too.
		/// </summary>
		private static void HookManageControlsUpdate()
		{
			Type manageType = typeof(UIState).Assembly.GetType("Terraria.GameContent.UI.States.UIManageControls");
			MethodInfo update = manageType?.GetMethod("Update", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
				null, new[] { typeof(GameTime) }, null);

			if (update == null)
			{
				AdvancedKeybinds.Instance?.Logger.Warn("Advanced Keybinds could not hook UIManageControls.Update; Escape may close the whole keybind menu.");
				return;
			}

			ModDetours.Apply(update, (orig_ElementUpdate orig, UIElement self, GameTime gameTime) => {
				if (self == null || !self.GetType().Name.Contains("UIManageControls"))
				{
					orig(self, gameTime);
					return;
				}

#if AK_DIAGNOSTICS
				ComboPanelIdleEscPathDiag.Note("UIManageControls.Update.enter");
#endif
				KeybindUiWorldInputGuard.NotifyManageControlsUiPresent();
				KeybindingUiPatches.EnsureControlsUndoButton(self);
				ControlsPadBHoldCancelFocus.BeforeManageControlsUpdate();
				ComboPanelCloseInputConsumeGate.PollAndSuppress();
				// Only while ComboPanel owns Escape / same-press swallow — idle Controls must leave
				// Inventory for Escape/Tab → ToggleInv → IngameFancyUI.Close.
				if (ComboPanel.IsOpen || ComboPanel.ShouldSwallowMenuEscape())
				{
					ComboPanel.PrepareParentMenuForEscape();
					ComboPanel.PollPadBackCloseGateAndSuppressParent();
					ComboPanel.GuardModalInput();
				}
				else
					ComboPanel.PollPadBackCloseGateAndSuppressParent();
				orig(self, gameTime);
				if (ComboPanel.IsOpen || ComboPanel.ShouldSwallowMenuEscape())
				{
					ComboPanel.PrepareParentMenuForEscape();
					ComboPanel.PollPadBackCloseGateAndSuppressParent();
					ComboPanel.GuardModalInput();
				}
				else
					ComboPanel.PollPadBackCloseGateAndSuppressParent();
				ComboPanelCloseInputConsumeGate.PollAndSuppress();
				ControlsPadBHoldCancelFocus.BeforeManageControlsUpdate();
#if AK_DIAGNOSTICS
				ComboPanelIdleEscPathDiag.Note("UIManageControls.Update.exit");
#endif
			});
		}

		/// <summary>
		/// Blocks IngameFancyUI.Close only while the combo keybind panel is open. Do not block other
		/// FancyUI screens (achievements, mod development, etc.) or ESC feels broken after reload.
		/// </summary>
		private static void HookFancyClose()
		{
			Type fancyType = typeof(UIState).Assembly.GetType("Terraria.UI.IngameFancyUI");
			MethodInfo close = fancyType?.GetMethod("Close", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);

			if (close == null)
			{
				AdvancedKeybinds.Instance?.Logger.Warn("Advanced Keybinds could not hook IngameFancyUI.Close; Escape may close the whole keybind menu.");
				return;
			}

			ModDetours.Apply(close, (orig_FancyClose orig) => {
				// Keyboard Escape leave: pad close-consume must not block (Esc-only menu back).
				bool keyboardEscapeDown = false;
				try
				{
					keyboardEscapeDown = Microsoft.Xna.Framework.Input.Keyboard.GetState()
						.IsKeyDown(Microsoft.Xna.Framework.Input.Keys.Escape);
				}
				catch
				{
					keyboardEscapeDown = false;
				}

				bool padConsumeBlocks = !keyboardEscapeDown
					&& ComboPanelCloseInputConsumeGate.ShouldBlockParentBack();

				if (!ComboPanel.ShouldBlockFancyUiClose() && !padConsumeBlocks)
				{
#if AK_DIAGNOSTICS
					ComboPanelEscPathDiag.NoteStack("IngameFancyUI.Close.ALLOWED");
					ComboPanelIdleEscPathDiag.Note("FancyUI.Close.ALLOWED");
#endif
					InventoryEscLiveVerifySystem.NoteFancyUiClose(
						allowed: true,
						padConsumeBlocked: false,
						samePress: ComboPanel.IsEscapeSamePressActivePublic(),
						keyboardEscapeDown: keyboardEscapeDown);
					// Pad:B held after panel close without gate ⇒ parent Controls would close (leak).
					if (!ComboPanel.IsOpen
						&& FnaGamePadProvider.IsDown(GamePadToken.B))
						GamePadProbeSystem.LogControlsCloseLeak("FancyUI.Close.PadBHeld");
					bool inventoryOpenBeforeClose = Main.playerInventory;
					orig();
					// Vanilla Close sets playerInventory=true; undo so Esc back never flashes inventory.
					KeybindShortcutSystem.SuppressVanillaInventoryOpenAfterSettingsUiClose(inventoryOpenBeforeClose);
					ComboPanel.ArmSettingsEscapeLeaveInventoryGate();
					return;
				}

				if (padConsumeBlocks)
				{
					ComboPanelCloseInputConsumeGate.LogDuplicateBackBlocked(
						"CloseComboPanel",
						"CloseParentControls");
				}

#if AK_DIAGNOSTICS
				ComboPanelEscPathDiag.NoteStack("IngameFancyUI.Close.BLOCKED");
				ComboPanelIdleEscPathDiag.Note("FancyUI.Close.BLOCKED");
#endif
				InventoryEscLiveVerifySystem.NoteFancyUiClose(
					allowed: false,
					padConsumeBlocked: padConsumeBlocks,
					samePress: ComboPanel.IsEscapeSamePressActivePublic(),
					keyboardEscapeDown: keyboardEscapeDown);
				if (AkDiag.Enabled) AkDiag.Log("Blocked IngameFancyUI.Close while combo panel is open.");
			});
		}

		/// <summary>
		/// IngameOptions.Close always sets Main.playerInventory = true. Escape-down leave and AK
		/// programmatic Settings closes still call SuppressVanillaInventoryOpenAfterSettingsUiClose
		/// so Esc/transition never flash inventory. Ordinary mouse close (Escape up, no AK close
		/// context) leaves the native Inventory/release result untouched.
		/// </summary>
		private static void HookOptionsClose()
		{
			MethodInfo close = typeof(IngameOptions).GetMethod("Close", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);

			if (close == null)
			{
				AdvancedKeybinds.Instance?.Logger.Warn("Advanced Keybinds could not hook IngameOptions.Close; Esc from settings may flash inventory.");
				return;
			}

			ModDetours.Apply(close, (orig_FancyClose orig) => {
				bool keyboardEscapeDown = false;
				try
				{
					keyboardEscapeDown = Microsoft.Xna.Framework.Input.Keyboard.GetState()
						.IsKeyDown(Microsoft.Xna.Framework.Input.Keys.Escape);
				}
				catch
				{
					keyboardEscapeDown = false;
				}

				bool akCloseContext = KeybindShortcutSystem.IsSettingsCloseContextActive;
				bool inventoryOpenBeforeClose = Main.playerInventory;
				orig();
				if (keyboardEscapeDown || akCloseContext)
					KeybindShortcutSystem.SuppressVanillaInventoryOpenAfterSettingsUiClose(inventoryOpenBeforeClose);
				ComboPanel.ArmSettingsEscapeLeaveInventoryGate();
			});
		}
	}
}
