using System;
using System.Reflection;
using AdvancedKeybinds.Binding;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Terraria;
using Terraria.Audio;
using Terraria.ID;
using Terraria.Localization;
using Terraria.ModLoader;
using Terraria.Social.Steam;
using Terraria.UI;

namespace AdvancedKeybinds.Patches
{
	/// <summary>
	/// Opens in-game menus from gameplay via:
	/// - User-configured keybinds for settings top, controls menu, and mod config list
	/// - Hidden shortcuts (title screen and in-world; no Controls row):
	///   Ctrl + Shift + Alt + left-click -> settings top;
	///   Ctrl + Shift + Alt + right-click -> controls menu;
	///   Ctrl + Shift + Alt + middle-click -> mod config list;
	///   Ctrl + Shift + Alt + M -> toggle cursor confine;
	///   Ctrl + Shift + Alt + Win + Del -> save and exit (in-world only).
	/// </summary>
	public class KeybindShortcutSystem : ModSystem
	{
		private static MethodInfo _openKeybinds;
		private static FieldInfo _modConfigListField;
		private static bool _resolved;
		private static bool _warnedOpenKeybindsMissing;
		private static bool _warnedModConfigListMissing;
		private static bool _restoreInventoryAfterShortcutUiClose;
		private static bool _inventoryStateBeforeShortcutUi;
		private static bool _wasLeftMouseDown;
		private static bool _wasRightMouseDown;
		private static bool _wasMiddleMouseDown;
		private static bool _wasHiddenConfineChordDown;
		private static bool _wasHiddenSaveQuitDeleteDown;

		private const uint VkDelete = 0x2E;

		public override void PostUpdateInput()
		{
			#if AK_DIAGNOSTICS
			Diagnostics.AkCpuMeasureSystem.Begin(Diagnostics.AkCpuMeasureSystem.IdKeybindShortcut);
			#endif
			try
			{
				// Install (once, on the main thread that pumps window messages) the low-level hook that
				// swallows the Windows key while tModLoader is the foreground window.
				WindowsKeySuppressor.EnsureInstalled();
				// Win chord flush runs in PlayerInputPatches (UpdateInput) so it is not duplicated here.

				CursorConfineHelper.Poll();

				// Cursor confine: hidden Ctrl+Shift+Alt+M only (no Controls keybind row).
				HandleHiddenCursorConfineShortcut();
				// Save and exit: hidden Ctrl+Shift+Alt+Win+Del only (no Controls keybind row).
				HandleHiddenSaveAndQuitShortcut();

				// Mouse chord shortcuts must run here even when the world is paused
				// (IngameOptions / FancyUI / Mod Config). PreUpdatePlayers is skipped while paused,
				// which previously made Ctrl+Shift+Alt+click dead inside those menus.
				// PostUpdateInput still runs before SetControls, so UseTile latch on RMB stays early enough.
				HandleHiddenShortcuts();

				// Configured Open Settings / Controls / Mod Config hotkeys must also work while those
				// menus are already open (jump between them without closing first).
				if (!Main.drawingPlayerChat && !Main.editSign && !Main.editChest)
					HandleConfiguredMenuHotkeys();

				if (Main.gameMenu)
					return;

				RestoreInventoryStateAfterShortcutUiClose();
			}
			finally
			{
				#if AK_DIAGNOSTICS
				Diagnostics.AkCpuMeasureSystem.End(Diagnostics.AkCpuMeasureSystem.IdKeybindShortcut);
				#endif
			}
		}

		/// <summary>
		/// Backup path while the world is simulating: if PostUpdateInput somehow missed an edge,
		/// still arm UseTile suppression before SetControls. No-op when the click was already
		/// consumed in <see cref="PostUpdateInput"/> (justPressed is false).
		/// </summary>
		public override void PreUpdatePlayers()
		{
			if (!Main.gameMenu)
				HandleHiddenShortcuts();
		}

		private static void HandleConfiguredMenuHotkeys()
		{
			// Title screen: still allow jumping between title settings / controls / mod config.
			// In-world: allow while IngameOptions / FancyUI (controls / mod config) are open.
			if (IsMenuHotkeyJustPressed(AdvancedKeybinds.OpenSettingsMenuHotkey))
			{
				OpenSettingsMenu();
				return;
			}

			if (IsMenuHotkeyJustPressed(AdvancedKeybinds.OpenKeybindMenuHotkey))
			{
				OpenKeybindMenu();
				return;
			}

			if (IsMenuHotkeyJustPressed(AdvancedKeybinds.OpenModConfigHotkey))
			{
				OpenModConfigList();
			}
		}

		/// <summary>
		/// Vanilla <see cref="ModKeybind.JustPressed"/> can stay false while Controls / FancyUI
		/// swallows input; BindingInputEngine still records the advanced/plain trigger edge
		/// (also covered by <see cref="ModKeybindPatches"/>, kept here as an explicit fallback).
		/// </summary>
		private static bool IsMenuHotkeyJustPressed(ModKeybind hotkey)
		{
			if (hotkey == null)
				return false;

			if (hotkey.JustPressed)
				return true;

			string actionId = ModKeybindReflection.GetActionId(hotkey);
			return !string.IsNullOrEmpty(actionId) && BindingInputEngine.WasJustPressed(actionId);
		}

		private static void HandleHiddenSaveAndQuitShortcut()
		{
			if (Main.gameMenu || Main.LocalPlayer == null || !Main.LocalPlayer.active)
			{
				_wasHiddenSaveQuitDeleteDown = IsDeleteDownForHiddenSaveQuit();
				return;
			}

			bool deleteDown = IsDeleteDownForHiddenSaveQuit();
			bool deleteJustPressed = deleteDown && !_wasHiddenSaveQuitDeleteDown;
			_wasHiddenSaveQuitDeleteDown = deleteDown;

			if (!deleteJustPressed || !IsHiddenSaveAndQuitChordHeld() || !Main.hasFocus)
				return;

			if (Main.drawingPlayerChat || Main.editSign || Main.editChest)
				return;

			TriggerSaveAndExit();
		}

		/// <summary>Same sequence as the vanilla settings menu "Save and Exit" left-side button.</summary>
		private static void TriggerSaveAndExit()
		{
			if (AkDiag.Enabled) AkDiag.Log("Hidden shortcut: Save and Exit (Ctrl+Shift+Alt+Win+Del).");

			SteamedWraps.StopPlaytimeTracking();
			SystemLoader.PreSaveAndQuit();
			IngameOptions.Close();
			Main.menuMode = 10;
			Main.gameMenu = true;
			WorldGen.SaveAndQuit();
		}

		private static bool IsHiddenSaveAndQuitChordHeld()
			=> IsHiddenChordHeld() && WindowsKeySuppressor.AnyWindowsDown;

		private static bool IsDeleteDownForHiddenSaveQuit()
		{
			if (Main.keyState.IsKeyDown(Keys.Delete))
				return true;

			return OperatingSystem.IsWindows() && WindowsKeySuppressor.IsVirtualKeyPhysicallyDown(VkDelete);
		}

		private static void HandleHiddenCursorConfineShortcut()
		{
			KeyboardState keyboard = Keyboard.GetState();
			bool chord = IsHiddenChordHeld() && keyboard.IsKeyDown(Keys.M);
			bool justPressed = chord && !_wasHiddenConfineChordDown;
			_wasHiddenConfineChordDown = chord;

			if (!justPressed || !Main.hasFocus)
				return;

			// Avoid firing while the user is typing in chat/sign with the chord held from elsewhere.
			if (Main.drawingPlayerChat || Main.editSign || Main.editChest)
				return;

			ToggleCursorConfine();
		}

		internal static void ToggleCursorConfine()
		{
			if (!OperatingSystem.IsWindows())
			{
				Main.NewText(Language.GetTextValue("Mods.AdvancedKeybinds.CursorConfine.WindowsOnly"), Color.Orange);
				return;
			}

			bool enabled = !CursorConfineHelper.SessionEnabled;
			CursorConfineHelper.SetSessionEnabled(enabled);

			if (enabled)
				Main.NewText(Language.GetTextValue("Mods.AdvancedKeybinds.CursorConfine.Enabled"), Color.LightGreen);
			else
				Main.NewText(Language.GetTextValue("Mods.AdvancedKeybinds.CursorConfine.Disabled"), Color.LightGray);
		}

		private static void HandleHiddenShortcuts()
		{
			// PlayerInput / Snapshot may have cached MouseState earlier this frame. Refresh so
			// Ctrl+Shift+Alt+click edges are visible while paused in settings / controls / mod config.
			MouseInputHelper.RefreshFrameMouseState();

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
				Main.mouseLeft = false;
				return;
			}

			if (rightJustPressed)
			{
				OpenKeybindMenu();
				// Do not clear Main.mouseRight here — that races Triggers/SetControls and can still
				// talk to NPCs. Arm world UseTile suppression until RMB is released instead.
				KeybindUiWorldInputGuard.NotifyHiddenShortcutConsumedRightClick();
				return;
			}

			if (middleJustPressed)
			{
				OpenModConfigList();
				Main.mouseMiddleRelease = false;
				Main.mouseMiddle = false;
			}
		}

		private static bool IsHiddenChordHeld()
		{
			// Keyboard.GetState is current; PostUpdateInput / PreUpdatePlayers both use this.
			KeyboardState keyboard = Keyboard.GetState();
			bool ctrlHeld = keyboard.IsKeyDown(Keys.LeftControl) || keyboard.IsKeyDown(Keys.RightControl);
			bool shiftHeld = keyboard.IsKeyDown(Keys.LeftShift) || keyboard.IsKeyDown(Keys.RightShift);
			bool altHeld = keyboard.IsKeyDown(Keys.LeftAlt) || keyboard.IsKeyDown(Keys.RightAlt);
			return ctrlHeld && shiftHeld && altHeld;
		}

		private static void OpenKeybindMenu()
		{
			if (Main.gameMenu)
			{
				OpenTitleKeybindMenu();
				return;
			}

			RememberInventoryStateBeforeShortcutUi();
			CloseSettingsUiForTransition();

			// Same RMB that opens controls must not clear the hovered row to 未設定.
			if (MouseInputHelper.IsPhysicalRightDown())
				KeybindingUiPatches.SuppressRowRightClearUntilPhysicalRightUp();

			if (!_resolved)
				ResolveMenuOpeners();

			if (_openKeybinds != null)
			{
				_openKeybinds.Invoke(null, null);
				return;
			}

			if (!_warnedOpenKeybindsMissing)
			{
				_warnedOpenKeybindsMissing = true;
				AdvancedKeybinds.Instance?.Logger.Warn("Advanced Keybinds could not locate IngameFancyUI.OpenKeybinds to open the key bindings menu.");
			}
		}

		private static void OpenSettingsMenu()
		{
			if (Main.gameMenu)
			{
				OpenTitleSettingsMenu();
				return;
			}

			RememberInventoryStateBeforeShortcutUi();
			CloseSettingsUiForTransition();
			IngameOptions.Open();
		}

		private static void OpenModConfigList()
		{
			if (Main.gameMenu)
			{
				OpenTitleModConfigList();
				return;
			}

			RememberInventoryStateBeforeShortcutUi();
			CloseSettingsUiForTransition();

			if (!_resolved)
				ResolveMenuOpeners();

			if (_modConfigListField?.GetValue(null) is UIState modConfigList)
			{
				IngameFancyUI.OpenUIState(modConfigList);
				return;
			}

			if (!_warnedModConfigListMissing)
			{
				_warnedModConfigListMissing = true;
				AdvancedKeybinds.Instance?.Logger.Warn("Advanced Keybinds could not locate Interface.modConfigList to open the mod configuration menu.");
			}
		}

		/// <summary>
		/// Title screen uses <see cref="Main.MenuUI"/> (menuMode 888), not <see cref="Main.InGameUI"/>.
		/// IngameOptions / IngameFancyUI are not drawn while <see cref="Main.gameMenu"/> is true.
		/// </summary>
		private static void OpenTitleSettingsMenu()
		{
			CloseTitleMenuUiForTransition();
			Main.menuMode = 11;
			SoundEngine.PlaySound(SoundID.MenuOpen);
			if (AkDiag.Enabled) AkDiag.Log("Hidden shortcut: title settings menu (menuMode=11).");
		}

		private static void OpenTitleKeybindMenu()
		{
			CloseTitleMenuUiForTransition();

			if (MouseInputHelper.IsPhysicalRightDown())
				KeybindingUiPatches.SuppressRowRightClearUntilPhysicalRightUp();

			Main.MenuUI.SetState(Main.ManageControlsMenu);
			Main.menuMode = 888;
			SoundEngine.PlaySound(SoundID.MenuOpen);
			if (AkDiag.Enabled) AkDiag.Log("Hidden shortcut: title controls menu (MenuUI.ManageControlsMenu).");
		}

		private static void OpenTitleModConfigList()
		{
			CloseTitleMenuUiForTransition();

			if (!_resolved)
				ResolveMenuOpeners();

			if (_modConfigListField?.GetValue(null) is UIState modConfigList)
			{
				Main.MenuUI.SetState(modConfigList);
				Main.menuMode = 888;
				SoundEngine.PlaySound(SoundID.MenuOpen);
				if (AkDiag.Enabled) AkDiag.Log("Hidden shortcut: title mod config list (MenuUI.modConfigList).");
				return;
			}

			// Fallback: Interface.ModLoaderMenus transitions 10027 -> MenuUI on the next DrawMenu pass.
			Main.menuMode = 10027;
			SoundEngine.PlaySound(SoundID.MenuOpen);
			if (AkDiag.Enabled) AkDiag.Log("Hidden shortcut: title mod config list (menuMode=10027 fallback).");
		}

		private static void CloseTitleMenuUiForTransition()
		{
			if (Main.ingameOptionsWindow)
				IngameOptions.Close();

			if (Main.inFancyUI)
				IngameFancyUI.Close();

			if (Main.menuMode == 888)
				Main.MenuUI.SetState(null);
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
