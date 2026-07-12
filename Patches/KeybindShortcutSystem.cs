using System;
using System.Reflection;
using AdvancedKeybinds.Binding;
using Microsoft.Xna.Framework.Input;
using Terraria;
using Terraria.ModLoader;
using Terraria.UI;

namespace AdvancedKeybinds.Patches
{
	/// <summary>
	/// Opens in-game menus from gameplay via:
	/// - User-configured keybinds for settings top, controls menu, and mod config list
	/// - Hidden shortcuts that always remain active (even when that keybind is set):
	///   Ctrl + Shift + Alt + left-click -> settings top;
	///   Ctrl + Shift + Alt + right-click -> controls menu;
	///   Ctrl + Shift + Alt + middle-click -> mod config list.
	/// </summary>
	public class KeybindShortcutSystem : ModSystem
	{
		private static MethodInfo _openKeybinds;
		private static FieldInfo _modConfigListField;
		private static bool _resolved;
		private static bool _restoreInventoryAfterShortcutUiClose;
		private static bool _inventoryStateBeforeShortcutUi;
		private static bool _wasLeftMouseDown;
		private static bool _wasRightMouseDown;
		private static bool _wasMiddleMouseDown;

		public override void PostUpdateInput()
		{
			// Install (once, on the main thread that pumps window messages) the low-level hook that
			// swallows the Windows key while tModLoader is the foreground window.
			WindowsKeySuppressor.EnsureInstalled();
			// Win chord flush runs in PlayerInputPatches (UpdateInput) so it is not duplicated here.

			if (Main.gameMenu || Main.drawingPlayerChat || Main.editSign || Main.editChest)
			{
				_wasLeftMouseDown = MouseInputHelper.IsPhysicalLeftDown();
				_wasRightMouseDown = MouseInputHelper.IsPhysicalRightDown();
				_wasMiddleMouseDown = MouseInputHelper.IsPhysicalMiddleDown();
				return;
			}

			RestoreInventoryStateAfterShortcutUiClose();

			// User-configured keybinds (keyboard, mouse, combos, holds - all supported via our engine).
			if (AdvancedKeybinds.OpenSettingsMenuHotkey?.JustPressed == true)
			{
				OpenSettingsMenu();
				return;
			}

			if (AdvancedKeybinds.OpenKeybindMenuHotkey?.JustPressed == true)
			{
				OpenKeybindMenu();
				return;
			}

			if (AdvancedKeybinds.OpenModConfigHotkey?.JustPressed == true)
			{
				OpenModConfigList();
				return;
			}

			// Hidden shortcuts: always available alongside any user binding for OpenKeybindMenu.
			HandleHiddenShortcuts();
		}

		private static void HandleHiddenShortcuts()
		{
			// Use physical mouse buttons so remapping 対話・動作 / アイテムの使用 / Middle Mouse
			// does not break Ctrl+Shift+Alt+click shortcuts.
			bool leftDown = MouseInputHelper.IsPhysicalLeftDown();
			bool rightDown = MouseInputHelper.IsPhysicalRightDown();
			bool middleDown = MouseInputHelper.IsPhysicalMiddleDown();
			bool leftJustPressed = leftDown && !_wasLeftMouseDown;
			bool rightJustPressed = rightDown && !_wasRightMouseDown;
			bool middleJustPressed = middleDown && !_wasMiddleMouseDown;
			_wasLeftMouseDown = leftDown;
			_wasRightMouseDown = rightDown;
			_wasMiddleMouseDown = middleDown;

			if (!IsHiddenChordHeld())
				return;

			// Physical mouse is global; ignore clicks that deactivated the game window.
			if (!Main.hasFocus)
				return;

			if (leftJustPressed)
			{
				OpenSettingsMenu();
				Main.mouseLeftRelease = false;
				return;
			}

			if (rightJustPressed)
			{
				OpenKeybindMenu();
				Main.mouseRightRelease = false;
				return;
			}

			if (middleJustPressed)
				OpenModConfigList();
		}

		private static bool IsHiddenChordHeld()
		{
			KeyboardState keyboard = Main.keyState;
			bool ctrlHeld = keyboard.IsKeyDown(Keys.LeftControl) || keyboard.IsKeyDown(Keys.RightControl);
			bool shiftHeld = keyboard.IsKeyDown(Keys.LeftShift) || keyboard.IsKeyDown(Keys.RightShift);
			bool altHeld = keyboard.IsKeyDown(Keys.LeftAlt) || keyboard.IsKeyDown(Keys.RightAlt);
			return ctrlHeld && shiftHeld && altHeld;
		}

		private static void OpenKeybindMenu()
		{
			RememberInventoryStateBeforeShortcutUi();
			CloseSettingsUiForTransition();

			if (!_resolved)
				ResolveMenuOpeners();

			if (_openKeybinds != null)
			{
				_openKeybinds.Invoke(null, null);
				return;
			}

			AdvancedKeybinds.Instance?.Logger.Warn("Advanced Keybinds could not locate IngameFancyUI.OpenKeybinds to open the key bindings menu.");
		}

		private static void OpenSettingsMenu()
		{
			RememberInventoryStateBeforeShortcutUi();
			CloseSettingsUiForTransition();
			IngameOptions.Open();
		}

		private static void OpenModConfigList()
		{
			RememberInventoryStateBeforeShortcutUi();
			CloseSettingsUiForTransition();

			if (!_resolved)
				ResolveMenuOpeners();

			if (_modConfigListField?.GetValue(null) is UIState modConfigList)
			{
				IngameFancyUI.OpenUIState(modConfigList);
				return;
			}

			AdvancedKeybinds.Instance?.Logger.Warn("Advanced Keybinds could not locate Interface.modConfigList to open the mod configuration menu.");
		}

		private static void RememberInventoryStateBeforeShortcutUi()
		{
			_inventoryStateBeforeShortcutUi = Main.playerInventory;
			_restoreInventoryAfterShortcutUiClose = true;
		}

		/// <summary>
		/// Vanilla IngameOptions.Close / IngameFancyUI.Close force Main.playerInventory = true.
		/// Call this immediately after those Close() methods so the inventory never draws for a frame.
		/// Does not clear the pending flag (CloseSettingsUiForTransition may Close mid-open).
		/// </summary>
		internal static void SuppressVanillaInventoryOpenAfterShortcutUiClose()
		{
			if (!_restoreInventoryAfterShortcutUiClose)
				return;

			if (Main.ingameOptionsWindow || Main.inFancyUI)
				return;

			Main.playerInventory = _inventoryStateBeforeShortcutUi;
			if (Main.LocalPlayer != null)
				Main.LocalPlayer.releaseInventory = false;
		}

		private static void RestoreInventoryStateAfterShortcutUiClose()
		{
			if (!_restoreInventoryAfterShortcutUiClose)
				return;

			if (Main.ingameOptionsWindow || Main.inFancyUI)
				return;

			Main.playerInventory = _inventoryStateBeforeShortcutUi;
			if (Main.LocalPlayer != null)
				Main.LocalPlayer.releaseInventory = false;
			_restoreInventoryAfterShortcutUiClose = false;
		}

		private static void CloseSettingsUiForTransition()
		{
			// IngameOptions and IngameFancyUI do not share the same close flow. If we open one on top of
			// the other without explicitly clearing the previous one, Escape can reveal the older screen.
			if (Main.ingameOptionsWindow)
				IngameOptions.Close();

			if (Main.inFancyUI)
				IngameFancyUI.Close();
		}

		private static void ResolveMenuOpeners()
		{
			_resolved = true;

			foreach (string typeName in new[] { "Terraria.UI.IngameFancyUI", "Terraria.GameContent.UI.States.IngameFancyUI" })
			{
				Type type = typeof(Main).Assembly.GetType(typeName);
				_openKeybinds = type?.GetMethod("OpenKeybinds", BindingFlags.Public | BindingFlags.Static);
				if (_openKeybinds != null)
					break;
			}

			Type iface = typeof(Mod).Assembly.GetType("Terraria.ModLoader.UI.Interface");
			_modConfigListField = iface?.GetField("modConfigList", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
		}
	}
}
