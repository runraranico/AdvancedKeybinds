using AdvancedKeybinds.Binding;
using AdvancedKeybinds.Patches;
using AdvancedKeybinds.UI;
using Microsoft.Xna.Framework.Input;
using Terraria.ModLoader;

namespace AdvancedKeybinds
{
	public class AdvancedKeybinds : Mod
	{
		public static AdvancedKeybinds Instance { get; private set; }

		/// <summary>Opens the vanilla settings menu top. Configurable in the Controls screen.</summary>
		public static ModKeybind OpenSettingsMenuHotkey { get; private set; }

		/// <summary>Opens the vanilla controls/keybind menu. Configurable in the Controls screen.</summary>
		public static ModKeybind OpenKeybindMenuHotkey { get; private set; }

		/// <summary>Opens the Mod Config list. Configurable in the Controls screen.</summary>
		public static ModKeybind OpenModConfigHotkey { get; private set; }

		public override void Load()
		{
			Instance = this;

			ComboPanel.ResetStaticState();
			ModDetours.DisposeAll();

			// Registered defaults: L / K / O (reset button). Rows start 未設定 until the user binds or resets.
			// Hidden shortcuts (always active): Ctrl+Shift+Alt+left-click -> settings top;
			// Ctrl+Shift+Alt+right-click -> controls; Ctrl+Shift+Alt+middle-click -> mod config.
			OpenSettingsMenuHotkey = KeybindLoader.RegisterKeybind(this, "OpenSettingsMenu", Keys.L);
			OpenKeybindMenuHotkey = KeybindLoader.RegisterKeybind(this, "OpenKeybindMenu", Keys.K);
			OpenModConfigHotkey = KeybindLoader.RegisterKeybind(this, "OpenModConfigMenu", Keys.O);

			ClientPrefs.Load();
			BindingStorage.Initialize();
			OpenKeybindMenuDefaults.ApplyStartupState();
			ModKeybindPatches.Apply();
			PlayerInputPatches.Apply();
			KeybindingUiPatches.Apply();
			ControlsOverlayPatches.Apply();
		}

		public override void Unload()
		{
			ComboPanel.ResetStaticState();
			ModDetours.DisposeAll();
			GameWindowInputHook.Uninstall();
			WindowsKeySuppressor.Uninstall();
			BindingInputEngine.Unload();
			BindingStorage.Unload();
			ClientPrefs.Unload();
			OpenSettingsMenuHotkey = null;
			OpenKeybindMenuHotkey = null;
			OpenModConfigHotkey = null;
			Instance = null;
		}
	}
}
