using System.IO;
using AdvancedKeybinds.Binding;
using AdvancedKeybinds.Core;
using AdvancedKeybinds.Diagnostics;
using AdvancedKeybinds.Patches;
using AdvancedKeybinds.UI;
using Microsoft.Xna.Framework.Input;
using Terraria;
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

		/// <summary>Cycles Hold-tier HUD anchor (feet / mouse / custom). Default U via reset; starts unset.</summary>
		public static ModKeybind CycleHoldTierHudAnchorHotkey { get; private set; }

		/// <summary>
		/// Opens the loosely-coupled binding list overlay (<c>Features/ListView</c>).
		/// Detach: remove this hotkey registration and delete that folder.
		/// </summary>
		public static ModKeybind OpenBindingListHotkey { get; private set; }

		public override void Load()
		{
			Instance = this;

			AkPlatform.ResetForReload();

			ComboPanel.ResetStaticState();
			ModDetours.DisposeAll();
			FargoActiveSkillDisplayCompat.ResetAppliedFlag();
			MachineTranslateDisplayCompat.ResetAppliedFlag();
			DialogueTweakGamepadCompat.ResetAppliedFlag();
			NativeCursorGamepadCompat.ResetAppliedFlag();
			AlchemistNpcLiteShopListGamepadCompat.ResetAppliedFlag();
			GamePadUiNavigationPatches.ClearRuntimeState();

			// Registered defaults: L / K / O / U (reset button). Rows start unset until the user binds or resets.
			// Cursor confine is config + hidden shortcut only (no Controls row):
			// Ctrl+Shift+Alt+M -> toggle cursor confine.
			// Ctrl+Shift+Alt+Win+Del -> save and exit.
			// Other hidden shortcuts: Ctrl+Shift+Alt+left-click -> settings top;
			// Ctrl+Shift+Alt+right-click -> controls; Ctrl+Shift+Alt+middle-click -> mod config.
			OpenSettingsMenuHotkey = KeybindLoader.RegisterKeybind(this, "OpenSettingsMenu", Keys.L);
			OpenKeybindMenuHotkey = KeybindLoader.RegisterKeybind(this, "OpenKeybindMenu", Keys.K);
			OpenModConfigHotkey = KeybindLoader.RegisterKeybind(this, "OpenModConfigMenu", Keys.O);
			CycleHoldTierHudAnchorHotkey = KeybindLoader.RegisterKeybind(this, "CycleHoldTierHudAnchor", Keys.U);
			// Binding list overlay (Features/ListView). Default F9; user can rebind / clear in Controls.
			OpenBindingListHotkey = KeybindLoader.RegisterKeybind(this, "OpenBindingList", Keys.F9);

			ClientPrefs.Load();
			// Pref drives session on/off for this load (ON→on, OFF→off). Manual toggles after load are free.
			CursorConfineHelper.ApplyOnLoadPreference(ClientPrefs.EnableCursorConfineOnLoad);
			WheelDirectionMigration.RunBeforeStorageLoad();
			AdditionalOrPersistencePatches.Apply();
			BindingStorage.Initialize();
			KeyDisplayAliasStorage.Initialize();
			OpenKeybindMenuDefaults.ApplyStartupState();
			ModKeybindPatches.Apply();
			PlayerInputPatches.Apply();
			// Temporary cursor / GamePad probes are archived stubs — do not register draw hooks.
			KeybindingUiPatches.Apply();
			ControlsOverlayPatches.Apply();
			ControlsGamepadAutoScrollPatches.Apply();
			GamePadUiNavigationPatches.Apply();
			KeybindUiWorldInputGuard.Apply();
			TerrariaDebugUiHotkeyPatches.Apply();
			FargoActiveSkillDisplayCompat.Apply();
			MachineTranslateDisplayCompat.Apply();
			DialogueTweakGamepadCompat.Apply();
			NativeCursorGamepadCompat.Apply();
			AlchemistNpcLiteShopListGamepadCompat.Apply();

			// Diagnosis-only: immediate parent restore once; deferred re-apply off (NOT a product fix).
			ParentRestoreFlickerTest.AnnounceOnLoad(this);

		}

		public override void PostSetupContent()
		{
			MigrateGamePadInputModeOnce();
		}

		/// <summary>
		/// Installs that predate the GamePad mode setting keep working untouched: anyone who already
		/// bound a <c>Pad:</c> token is moved to Enabled once, everyone else stays on Auto and pays
		/// no gamepad cost. Never re-runs, so an explicit Disabled is never silently undone.
		/// </summary>
		private static void MigrateGamePadInputModeOnce()
		{
			if (ClientPrefs.GamePadModeMigrated)
				return;

			AdvancedKeybindsConfig config = AdvancedKeybindsConfig.Instance;
			if (config == null)
				return;

			bool hasPadTokens = false;
			foreach (System.Collections.Generic.KeyValuePair<string, ComboBinding> pair in BindingStorage.AllBindings)
			{
				if (GamePadSubsystemPolicy.ContainsPadToken(pair.Value?.Keys))
				{
					hasPadTokens = true;
					break;
				}
			}

			ClientPrefs.GamePadModeMigrated = true;

			if (!GamePadSubsystemPolicy.MigrationChangesSavedMode(
					alreadyMigrated: false,
					config.GamePadInputMode,
					hasPadTokens))
			{
				GamePadSubsystemRuntime.Mode = config.GamePadInputMode;
				return;
			}

			config.GamePadInputMode = GamePadSubsystemPolicy.ResolveMigratedMode(
				alreadyMigrated: false,
				config.GamePadInputMode,
				hasPadTokens);
			GamePadSubsystemRuntime.Mode = config.GamePadInputMode;
			config.SaveClientSettingsSilent();
		}

		public override void Unload()
		{
			ComboPanel.ResetStaticState();
			ModDetours.DisposeAll();
			FargoActiveSkillDisplayCompat.ResetAppliedFlag();
			MachineTranslateDisplayCompat.ResetAppliedFlag();
			DialogueTweakGamepadCompat.ResetAppliedFlag();
			NativeCursorGamepadCompat.ResetAppliedFlag();
			AlchemistNpcLiteShopListGamepadCompat.ResetAppliedFlag();
			GamePadUiNavigationPatches.ClearRuntimeState();
			KeybindingUiPatches.ClearCaches();
			ControlsGamepadAutoScrollPatches.ClearRuntimeState();
			ControlsPadBHoldCancelFocus.ClearRuntimeState();
			ControlsSectionHeaderSupport.ClearRuntimeState();
			ControlsUiHost.ClearRuntimeState();
			ComboPanelCloseInputConsumeGate.ClearRuntimeState();
			PendingGamePadRowConfirm.ClearRuntimeState();
			CursorConfineHelper.ResetSession();
			GameWindowInputHook.Uninstall();
			WindowsKeySuppressor.Uninstall();
			FnaGamePadProvider.Reset();
			BindingInputEngine.Unload();
			try
			{
				BindingStorage.Unload();
			}
			finally
			{
				AdditionalOrPersistencePatches.Dispose();
			}
			KeyDisplayAliasStorage.Unload();
			ClientPrefs.Unload();
			OpenSettingsMenuHotkey = null;
			OpenKeybindMenuHotkey = null;
			OpenModConfigHotkey = null;
			CycleHoldTierHudAnchorHotkey = null;
			OpenBindingListHotkey = null;
			AkPlatform.ResetForReload();
			Instance = null;
		}
	}
}
