using AdvancedKeybinds.Binding;
using AdvancedKeybinds.UI;
using Microsoft.Xna.Framework.Input;
using Terraria;
using Terraria.GameInput;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Product safety net: re-arm Inventory JustPressed after AK suppress paths.
	/// Title / player-select BackButtonInUse and settings FancyUI.Close both need Inventory JP
	/// when KeyStatus (or AdditionalOr) still owns Escape. No file I/O / no diagnostics.
	/// </summary>
	public static class InventoryEscapeRearm
	{
		private static bool _prevEscapeDown;

		public static void ApplyAfterAkSuppress()
		{
			try
			{
				bool settings = ComboPanel.IsSettingsMenuBackContextPublic();
				bool panelOpen = ComboPanel.IsOpen;
				bool escapeDown = false;
				try { escapeDown = Keyboard.GetState().IsKeyDown(Keys.Escape); }
				catch { escapeDown = false; }

				bool rising = escapeDown && !_prevEscapeDown;
				_prevEscapeDown = escapeDown;

				bool samePress = ComboPanel.IsEscapeSamePressActivePublic();
				bool invAlive = ReadInventoryJustPressed();
				bool keyHasEscape = KeyStatusHasEscape();
				bool escapeLatch = BindingInputEngine.IsSharedUiEscapeHoldLatchActive("Inventory");
				bool sharedUiBackEscapeTab = InventorySharedUiBackEscapeTabConfigured();

				if (InventoryEscLiveVerifyPolicy.ShouldRearmInventoryForSettingsEscapeLeave(
					settings, panelOpen, samePress, rising, escapeDown, invAlive, keyHasEscape,
					escapeLatch, sharedUiBackEscapeTab))
				{
					SetInventoryJustPressed(true);
					if (Main.LocalPlayer != null)
						Main.LocalPlayer.releaseInventory = true;
					return;
				}

				// Title / player-select / world-select: BackButtonInUse needs Inventory JP.
				if (InventoryEscLiveVerifyPolicy.ShouldRearmInventoryForTitleMenuEscapeBack(
					Main.gameMenu,
					settings,
					panelOpen,
					samePress,
					rising,
					escapeDown,
					invAlive,
					keyHasEscape,
					escapeLatch,
					sharedUiBackEscapeTab))
				{
					SetInventoryJustPressed(true);
					return;
				}

				if (InventoryEscLiveVerifyPolicy.ShouldRearmInventoryForWorldEscapeEdge(
					settings,
					panelOpen,
					rising,
					invAlive,
					keyHasEscape,
					ComboPanel.IsSettingsEscapeLeaveInventoryGateArmedPublic(),
					escapeLatch,
					ComboPanel.IsWorldEscapeInventoryOneShotArmedPublic()))
				{
					SetInventoryJustPressed(true);
					if (Main.LocalPlayer != null)
						Main.LocalPlayer.releaseInventory = true;
				}
			}
			catch
			{
				/* never break input */
			}
		}

		private static bool KeyStatusHasEscape()
		{
			var kb = VanillaRebindHelper.GetAssignedKeysForInputMode("Inventory", InputMode.Keyboard);
			var ui = VanillaRebindHelper.GetAssignedKeysForInputMode("Inventory", InputMode.KeyboardUI);
			return InventoryEscLiveVerifyPolicy.KeyStatusListContainsEscape(kb)
				|| InventoryEscLiveVerifyPolicy.KeyStatusListContainsEscape(ui);
		}

		private static bool InventorySharedUiBackEscapeTabConfigured()
		{
			try
			{
				ComboBinding g = BindingStorage.Get("Inventory");
				if (g != null && !g.IsEmpty
					&& KeyboardAdditionalOrPolicy.ShouldPreserveVanillaAndKeyWithAdditionalOr(
						"Inventory", g.Hold, g.Keys, g.AdditionalOrToken))
					return true;

				ComboBinding u = BindingStorage.Get("KU:Inventory");
				if (u != null && !u.IsEmpty
					&& KeyboardAdditionalOrPolicy.ShouldPreserveVanillaAndKeyWithAdditionalOr(
						"KU:Inventory", u.Hold, u.Keys, u.AdditionalOrToken))
					return true;
			}
			catch
			{
				/* ignore */
			}

			return false;
		}

		private static bool ReadInventoryJustPressed()
		{
			try
			{
				return PlayerInput.Triggers?.JustPressed?.KeyStatus != null
					&& PlayerInput.Triggers.JustPressed.KeyStatus.TryGetValue("Inventory", out bool v)
					&& v;
			}
			catch
			{
				return false;
			}
		}

		private static void SetInventoryJustPressed(bool value)
		{
			try
			{
				if (PlayerInput.Triggers?.JustPressed?.KeyStatus == null)
					return;
				PlayerInput.Triggers.JustPressed.KeyStatus["Inventory"] = value;
				if (PlayerInput.Triggers.Current?.KeyStatus != null)
					PlayerInput.Triggers.Current.KeyStatus["Inventory"] = value;
			}
			catch
			{
				/* ignore */
			}
		}
	}
}
