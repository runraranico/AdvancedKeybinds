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

		public override void Load()
		{
			Instance = this;
			AkDiag.ResetAnnounceState();

			ComboPanel.ResetStaticState();
			ModDetours.DisposeAll();
			FargoActiveSkillDisplayCompat.ResetAppliedFlag();

			// Registered defaults: L / K / O / U (reset button). Rows start 未設定 until the user binds or resets.
			// Cursor confine is config + hidden shortcut only (no Controls row):
			// Ctrl+Shift+Alt+M -> toggle cursor confine.
			// Ctrl+Shift+Alt+Win+Del -> save and exit.
			// Other hidden shortcuts: Ctrl+Shift+Alt+left-click -> settings top;
			// Ctrl+Shift+Alt+right-click -> controls; Ctrl+Shift+Alt+middle-click -> mod config.
			OpenSettingsMenuHotkey = KeybindLoader.RegisterKeybind(this, "OpenSettingsMenu", Keys.L);
			OpenKeybindMenuHotkey = KeybindLoader.RegisterKeybind(this, "OpenKeybindMenu", Keys.K);
			OpenModConfigHotkey = KeybindLoader.RegisterKeybind(this, "OpenModConfigMenu", Keys.O);
			CycleHoldTierHudAnchorHotkey = KeybindLoader.RegisterKeybind(this, "CycleHoldTierHudAnchor", Keys.U);

			ClientPrefs.Load();
			// Pref drives session on/off for this load (ON→on, OFF→off). Manual toggles after load are free.
			CursorConfineHelper.ApplyOnLoadPreference(ClientPrefs.EnableCursorConfineOnLoad);
			BindingStorage.Initialize();
			KeyDisplayAliasStorage.Initialize();
			OpenKeybindMenuDefaults.ApplyStartupState();
			ModKeybindPatches.Apply();
			PlayerInputPatches.Apply();
			KeybindingUiPatches.Apply();
			ControlsOverlayPatches.Apply();
			KeybindUiWorldInputGuard.Apply();
			TerrariaDebugUiHotkeyPatches.Apply();
			FargoActiveSkillDisplayCompat.Apply();

			// Dup-check file / resolve-trace only in AK_DIAGNOSTICS builds (never in public DLL).
#if AK_DIAGNOSTICS
			if (AkDiag.Enabled)
			{
				string dupLog = Path.Combine(Main.SavePath, "AdvancedKeybinds", "dup-check-session.log");
				PhysicalKeyDupDiag.Configure(
					msg => {
						try
						{
							Instance?.Logger?.Info(msg);
						}
						catch
						{
							// ignore
						}
					},
					dupLog,
					enableResolveTrace: false);
			}
			else
			{
				PhysicalKeyDupDiag.Reset();
			}

			if (AkDiag.Enabled)
				AkDiag.AnnounceLoad();
			else
				AkDiag.ResetAnnounceState();
#else
			AkDiag.ResetAnnounceState();
#endif
		}

		public override void Unload()
		{
			LiveInputDiagnosticSession.Shutdown();
#if AK_DIAGNOSTICS
			PhysicalKeyDupDiag.Reset();
#endif
			ComboPanel.ResetStaticState();
			ModDetours.DisposeAll();
			FargoActiveSkillDisplayCompat.ResetAppliedFlag();
			KeybindingUiPatches.ClearCaches();
			CursorConfineHelper.ResetSession();
			GameWindowInputHook.Uninstall();
			WindowsKeySuppressor.Uninstall();
			BindingInputEngine.Unload();
			BindingStorage.Unload();
			KeyDisplayAliasStorage.Unload();
			ClientPrefs.Unload();
			OpenSettingsMenuHotkey = null;
			OpenKeybindMenuHotkey = null;
			OpenModConfigHotkey = null;
			CycleHoldTierHudAnchorHotkey = null;
			AkDiag.ResetAnnounceState();
			AdvancedKeybindsDiagnosticEvents.ClearAllSubscribers();
			Instance = null;
		}
	}
}
