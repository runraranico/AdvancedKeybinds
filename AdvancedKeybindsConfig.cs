using System;
using System.ComponentModel;
using AdvancedKeybinds.Binding;
using AdvancedKeybinds.Config.UI;
using AdvancedKeybinds.Core;
using AdvancedKeybinds.Diagnostics;
using AdvancedKeybinds.Patches;
using Microsoft.Xna.Framework;
using Terraria;
using Newtonsoft.Json;
using Terraria.ModLoader;
using Terraria.ModLoader.Config;

namespace AdvancedKeybinds
{
	/// <summary>
	/// Client settings: Win-key suppression, cursor confine, and on-release Hold tier HUD.
	/// Cursor session on/off is not saved; auto on/off at mod load is in <see cref="ClientPrefs"/>.
	/// Cursor-confine toggles stay in code but are omitted from ModConfig UI ([JsonIgnore] without
	/// [ShowDespiteJsonIgnore]); Custom coords use <see cref="HiddenConfigElement"/>.
	/// tML UI lists fields before properties 窶・keep only hidden coords as fields, and visible
	/// options as properties in Windows 竊・Hold gauge order.
	/// </summary>
	public class AdvancedKeybindsConfig : ModConfig
	{
		public override ConfigScope Mode => ConfigScope.ClientSide;

		// Saved with ModConfig / Restore Defaults, but hidden from UI (set by dragging the gauge).
		// Listed first because tML enumerates fields before properties; HiddenConfigElement is height 0.
		// Normalized 0窶・ screen fractions (origin / Main.screen*). Legacy absolute px (>1) are migrated.
		// [DefaultValue(-1)] is required so tML IgnoreAndPopulate treats unset as (-1,-1); ClientPrefs
		// recovers drag commits when ModConfig Accept wipes these HiddenConfigElement fields.
		[DefaultValue(-1f)]
		[CustomModConfigItem(typeof(HiddenConfigElement))]
		public float HoldTierHudCustomPositionX = -1f;

		// Normalized 0窶・; legacy absolute migrated. Sentinel (-1,-1) = unset.
		[DefaultValue(-1f)]
		[CustomModConfigItem(typeof(HiddenConfigElement))]
		public float HoldTierHudCustomPositionY = -1f;

		[Header("$Mods.AdvancedKeybinds.Config.WindowsKey.Header")]
		[LabelKey("$Mods.AdvancedKeybinds.Config.WindowsKey.Suppress.Label")]
		[TooltipKey("$Mods.AdvancedKeybinds.Config.WindowsKey.Suppress.Tooltip")]
		[DefaultValue(false)]
		public bool SuppressWindowsKey { get; set; } = false;

		[LabelKey("$Mods.AdvancedKeybinds.Config.WindowsKey.SuppressDebugUi.Label")]
		[TooltipKey("$Mods.AdvancedKeybinds.Config.WindowsKey.SuppressDebugUi.Tooltip")]
		[DefaultValue(false)]
		public bool SuppressTerrariaDebugUiHotkeys { get; set; } = false;

		// Cursor-confine section: hidden from ModConfig UI for now (logic + prefs + Ctrl+Shift+Alt+M remain).
		// To show again: restore [Header] below and add [ShowDespiteJsonIgnore] on both properties.
		// [Header("$Mods.AdvancedKeybinds.Config.Guide.NotesHeader")]
		[LabelKey("$Mods.AdvancedKeybinds.Config.Guide.CursorConfineToggle.Label")]
		[TooltipKey("$Mods.AdvancedKeybinds.Config.Guide.CursorConfineToggle.Tooltip")]
		[JsonIgnore]
		public bool CursorConfineToggle
		{
			get => CursorConfineHelper.SessionEnabled;
			set
			{
				if (CursorConfineHelper.SessionEnabled == value)
					return;

				CursorConfineHelper.SetSessionEnabled(value);
			}
		}

		[LabelKey("$Mods.AdvancedKeybinds.Config.Guide.CursorConfineOnLoad.Label")]
		[TooltipKey("$Mods.AdvancedKeybinds.Config.Guide.CursorConfineOnLoad.Tooltip")]
		[JsonIgnore]
		public bool EnableCursorConfineOnLoad
		{
			get => ClientPrefs.EnableCursorConfineOnLoad;
			set => ClientPrefs.EnableCursorConfineOnLoad = value;
		}

		[Header("$Mods.AdvancedKeybinds.Config.HoldTierHud.Header")]
		[LabelKey("$Mods.AdvancedKeybinds.Config.HoldTierHud.Enabled.Label")]
		[TooltipKey("$Mods.AdvancedKeybinds.Config.HoldTierHud.Enabled.Tooltip")]
		[DefaultValue(false)]
		public bool HoldTierHudEnabled { get; set; } = false;

		[LabelKey("$Mods.AdvancedKeybinds.Config.HoldTierHud.AllowDrag.Label")]
		[TooltipKey("$Mods.AdvancedKeybinds.Config.HoldTierHud.AllowDrag.Tooltip")]
		[DefaultValue(false)]
		public bool HoldTierHudAllowDrag { get; set; } = false;

#if AK_DIAGNOSTICS
		[Header("$Mods.AdvancedKeybinds.Config.KeyboardDiag.Header")]
		[LabelKey("$Mods.AdvancedKeybinds.Config.KeyboardDiag.Mode.Label")]
		[TooltipKey("$Mods.AdvancedKeybinds.Config.KeyboardDiag.Mode.Tooltip")]
		[DefaultValue(false)]
		public bool KeyboardDiagnosticMode { get; set; }

		[LabelKey("$Mods.AdvancedKeybinds.Config.KeyboardDiag.LogToFile.Label")]
		[TooltipKey("$Mods.AdvancedKeybinds.Config.KeyboardDiag.LogToFile.Tooltip")]
		[DefaultValue(true)]
		public bool KeyboardDiagnosticLogToFile { get; set; } = true;
#endif

		[LabelKey("$Mods.AdvancedKeybinds.Config.HoldTierHud.Anchor.Label")]
		[TooltipKey("$Mods.AdvancedKeybinds.Config.HoldTierHud.Anchor.Tooltip")]
		[DefaultValue(HoldTierHudAnchor.Feet)]
		[CustomModConfigItem(typeof(HoldTierHudAnchorElement))]
		public HoldTierHudAnchor HoldTierHudAnchor { get; set; } = HoldTierHudAnchor.Feet;

		[LabelKey("$Mods.AdvancedKeybinds.Config.HoldTierHud.BarWidth.Label")]
		[TooltipKey("$Mods.AdvancedKeybinds.Config.HoldTierHud.BarWidth.Tooltip")]
		[DefaultValue(200)]
		[Range(150, 500)]
		[Increment(10)]
		[Slider]
		public int HoldTierHudBarWidth { get; set; } = 200;

		[LabelKey("$Mods.AdvancedKeybinds.Config.HoldTierHud.Color.Label")]
		[TooltipKey("$Mods.AdvancedKeybinds.Config.HoldTierHud.Color.Tooltip")]
		[DefaultValue(typeof(Color), "255, 220, 90, 255")]
		[CustomModConfigItem(typeof(LocalizedColorElement))]
		public Color HoldTierHudColor { get; set; } = new(255, 220, 90);

		public override void OnLoaded()
		{
			HoldTierHudSystem.LogHoldHudConfigStateIfChanged(this, "OnLoaded-enter");
			// Cold load / reload: ModConfig JSON may omit wiped Hidden floats; prefs keep the drag spot.
			if (HoldTierHudSystem.RecoverCustomPlacementIfWiped(this))
				HoldTierHudSystem.PersistRecoveredPlacement(this);
			SyncTerrariaDebugUiHotkeyPolicy();
			HoldTierHudSystem.LogHoldHudConfigStateIfChanged(this, "OnLoaded-exit");
		}

		public override void OnChanged()
		{
			if (HoldTierHudBarWidth < 150)
				HoldTierHudBarWidth = 150;
			else if (HoldTierHudBarWidth > 500)
				HoldTierHudBarWidth = 500;
			HoldTierHudBarWidth = (HoldTierHudBarWidth / 10) * 10;

			// Old Head (1) falls back to Feet. Custom (3) is allowed.
			if (HoldTierHudAnchor != HoldTierHudAnchor.Feet
				&& HoldTierHudAnchor != HoldTierHudAnchor.Mouse
				&& HoldTierHudAnchor != HoldTierHudAnchor.Custom)
			{
				HoldTierHudAnchor = HoldTierHudAnchor.Feet;
			}

			HoldTierHudSystem.LogHoldHudConfigStateIfChanged(this, "OnChanged-enter");

			// Accept / live apply only (AnchorElement must not write ClientPrefs on click).
			// Custom → PreferCustom + seed if unset. Feet/Mouse → clear PreferCustom.
			if (HoldTierHudAnchor == HoldTierHudAnchor.Custom)
			{
				HoldTierHudSystem.EnterCustomFromHotkey(this);
			}
			else if (HoldTierHudAnchor == HoldTierHudAnchor.Feet
				|| HoldTierHudAnchor == HoldTierHudAnchor.Mouse)
			{
				HoldTierHudSystem.NotifyPresetAnchorChosen(this);
			}

			// Prefs are authoritative for wiped / partial / invalid Custom coords.
			if (HoldTierHudSystem.RecoverCustomPlacementIfWiped(this))
				HoldTierHudSystem.PersistRecoveredPlacement(this);

			// Migrate/clamp stored Custom only when Custom is active.
			if (!HoldTierHudSystem.IsDragging
				&& HoldTierHudAnchor == HoldTierHudAnchor.Custom)
			{
				HoldTierHudSystem.ApplyClampedCustomPosition(
					this,
					new Vector2(HoldTierHudCustomPositionX, HoldTierHudCustomPositionY));
			}

			HoldTierHudSystem.LogHoldHudConfigStateIfChanged(this, "OnChanged-exit");

			if (!SuppressWindowsKey)
				WindowsKeySuppressor.OnSuppressionDisabled();
			else
				GameWindowInputHook.RefreshRawInputForWinSuppressSetting();

			SyncTerrariaDebugUiHotkeyPolicy();

#if AK_DIAGNOSTICS
			if (KeyboardDiagnosticMode)
				KeyboardDiagMode.FlushLogHeader();
			else
				KeyboardDiagMode.Clear();
#endif
		}

		private void SyncTerrariaDebugUiHotkeyPolicy()
		{
			TerrariaDebugUiHotkeyPolicy.SuppressVanillaSpecialUi = SuppressTerrariaDebugUiHotkeys;
			if (!SuppressTerrariaDebugUiHotkeys)
				return;

			// If UI was already hidden / FPS overlay left on, clear so the player is not stuck.
			try
			{
				Main.hideUI = false;
				Main.showFrameRate = false;
			}
			catch
			{
				// ignore during early load
			}
		}

		public static AdvancedKeybindsConfig Instance => ModContent.GetInstance<AdvancedKeybindsConfig>();

		public bool ShouldShowHoldTierHud(int tierCount)
			=> HoldTierHudEnabled && tierCount >= 2;

		public void CycleAnchor()
		{
			// Hotkey: Feet 竊・Mouse 竊・Custom 竊・Feet. Config UI can still pick any anchor.
			// Invariant: never mutate HoldTierHudEnabled / HoldTierHudAllowDrag from this hotkey.
			bool enabledBefore = HoldTierHudEnabled;
			bool allowDragBefore = HoldTierHudAllowDrag;
			HoldTierHudAnchor from = HoldTierHudAnchor;
			HoldTierHudAnchor next = from switch
			{
				HoldTierHudAnchor.Feet => HoldTierHudAnchor.Mouse,
				HoldTierHudAnchor.Mouse => HoldTierHudAnchor.Custom,
				_ => HoldTierHudAnchor.Feet
			};

			#if AK_DIAGNOSTICS
			HoldHudProbe.Log(
				"CYCLE begin from=" + from
				+ " next=" + next
				+ " preferCustom=" + ClientPrefs.HoldHudPreferCustom
				+ " enabled=" + enabledBefore
				+ " allowDrag=" + allowDragBefore);
			#endif
			if (AkDiag.Enabled)
			{
				AkDiag.Log(
				"HoldHud CycleAnchor begin"
				+ " from=" + from
				+ " next=" + next
				+ " enabled=" + enabledBefore
				+ " allowDrag=" + allowDragBefore
				+ " preferCustom=" + ClientPrefs.HoldHudPreferCustom);
			}

			if (next == HoldTierHudAnchor.Custom)
			{
				HoldTierHudSystem.EnterCustomFromHotkey(this);
			}
			else
			{
				HoldTierHudAnchor = next;
				HoldTierHudSystem.NotifyPresetAnchorChosen(this);
			}

			#if AK_DIAGNOSTICS
			HoldHudProbe.LogState("CYCLE after-branch", this);
			#endif

			// Re-assert after EnterCustom / Notify / any OnChanged side effects from nested saves.
			HoldTierHudEnabled = enabledBefore;
			HoldTierHudAllowDrag = allowDragBefore;

			SaveHoldHudChangesSilent();

			// SaveChanges → OnChanged must not resurrect forced toggles either.
			HoldTierHudEnabled = enabledBefore;
			HoldTierHudAllowDrag = allowDragBefore;

			#if AK_DIAGNOSTICS
			HoldHudProbe.LogState("CYCLE after-save", this);
			#endif
			if (HoldTierHudAnchor != next)
			{
				#if AK_DIAGNOSTICS
				HoldHudProbe.Log(
					"CYCLE ANOMALY expectedAnchor=" + next
					+ " actual=" + HoldTierHudAnchor
					+ " preferCustom=" + ClientPrefs.HoldHudPreferCustom
					+ " (Recover/OnChanged overwrote shortcut target)");
				#endif
			}

			if (AkDiag.Enabled)
			{
				AkDiag.Log(
				"HoldHud CycleAnchor end"
				+ " anchor=" + HoldTierHudAnchor
				+ " enabled=" + HoldTierHudEnabled
				+ " allowDrag=" + HoldTierHudAllowDrag
				+ " preferCustom=" + ClientPrefs.HoldHudPreferCustom
				+ " previewOk=" + (HoldTierHudEnabled && HoldTierHudAllowDrag)
				+ " dragOk=" + (HoldTierHudEnabled && HoldTierHudAllowDrag)
				+ " togglesUnchanged="
				+ (HoldTierHudEnabled == enabledBefore && HoldTierHudAllowDrag == allowDragBefore));
			}
		}

		/// <summary>
		/// Persist client Hold-HUD fields without UI sound. Call only on discrete events
		/// (drag commit, Esc/silent cancel restore, CycleAnchor) 窶・not every frame while dragging.
		/// </summary>
		internal void SaveHoldHudChangesSilent()
		{
			try
			{
				SaveChanges(silent: true);
			}
			catch (Exception ex)
			{
				AdvancedKeybinds.Instance?.Logger.Warn(
					"Advanced Keybinds failed to save Hold HUD config: " + ex.Message);
			}
		}
	}
}
