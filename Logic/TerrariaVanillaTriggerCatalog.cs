using System;
using System.Collections.Generic;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Known Terraria / tModLoader PlayerInput trigger ActionIds (vanilla Controls rows).
	/// Distinguishes legacy <c>GP:{vanilla}</c> from <c>GP:ModName/…</c> without '/' alone.
	/// Orphan migrate targets are derived from AssembleBindPanels row sets + legacy sync ModeGamePad.
	/// </summary>
	public static class TerrariaVanillaTriggerCatalog
	{
		/// <summary>
		/// Union of KnownTriggers + AssembleBindPanels standard rows
		/// (see OfficialDefaultKeyRestoreLogicTests gold / tModLoader.dll ProductVersion 2026.05.3.0).
		/// </summary>
		private static readonly HashSet<string> Vanilla = new(StringComparer.Ordinal) {
			"MouseLeft", "MouseRight", "MouseMiddle", "MouseXButton1", "MouseXButton2",
			"Up", "Down", "Left", "Right", "Jump", "Throw", "Inventory",
			"Grapple", "SmartSelect", "SmartCursor", "QuickMount", "QuickHeal", "QuickMana", "QuickBuff",
			"MapZoomIn", "MapZoomOut", "MapAlphaUp", "MapAlphaDown", "MapFull", "MapStyle",
			"Hotbar1", "Hotbar2", "Hotbar3", "Hotbar4", "Hotbar5", "Hotbar6", "Hotbar7", "Hotbar8",
			"Hotbar9", "Hotbar10", "HotbarMinus", "HotbarPlus",
			"DpadRadial1", "DpadRadial2", "DpadRadial3", "DpadRadial4", "RadialHotbar", "RadialQuickbar",
			"DpadSnap1", "DpadSnap2", "DpadSnap3", "DpadSnap4",
			"MenuUp", "MenuDown", "MenuLeft", "MenuRight", "LockOn",
			"ViewZoomIn", "ViewZoomOut", "Loadout1", "Loadout2", "Loadout3",
			"ToggleCameraMode", "ToggleCreativeMenu"
		};

		/// <summary>
		/// UIManageControls AssembleBindPanels — GamePad ゲーム操作 standard rows (not Menu*).
		/// Inventory / SmartCursor / SmartSelect are members → orphan legacy → GP:G:.
		/// </summary>
		private static readonly HashSet<string> PadGameAssembleRows = new(StringComparer.Ordinal) {
			"MouseLeft", "MouseRight", "MouseMiddle", "MouseXButton1", "MouseXButton2",
			"Up", "Down", "Left", "Right", "Jump",
			"Grapple", "SmartSelect", "SmartCursor", "QuickMount", "QuickHeal", "QuickMana", "QuickBuff",
			"LockOn", "Throw", "Inventory",
			"Loadout1", "Loadout2", "Loadout3", "ToggleCameraMode"
		};

		/// <summary>Menu navigation triggers — orphan legacy → GP:U:.</summary>
		private static readonly HashSet<string> PreferGamePadUi = new(StringComparer.Ordinal) {
			"MenuUp", "MenuDown", "MenuLeft", "MenuRight"
		};

		public static bool IsKnownVanillaTrigger(string actionId)
			=> !string.IsNullOrEmpty(actionId) && Vanilla.Contains(actionId);

		public static bool IsPadGameAssembleRow(string actionId)
			=> !string.IsNullOrEmpty(actionId) && PadGameAssembleRows.Contains(actionId);

		public static bool IsPreferGamePadUiTrigger(string actionId)
			=> !string.IsNullOrEmpty(actionId) && PreferGamePadUi.Contains(actionId);

		/// <summary>
		/// Orphan legacy migrate target, or null when not known vanilla (skip — no delete / no guess).
		/// GP:G: default: legacy <see cref="ControlsGameUiSeparationPolicy.ResolveSyncKind"/> uses ModeGamePad;
		/// PadGameAssembleRows includes Inventory/SmartCursor/SmartSelect. Menu* → GP:U:.
		/// </summary>
		public static string ResolveOrphanMigrateStorageKey(string vanillaActionId)
		{
			if (!IsKnownVanillaTrigger(vanillaActionId))
				return null;

			if (IsPreferGamePadUiTrigger(vanillaActionId))
				return ControlsGameUiSeparationPolicy.GamePadUiPrefix + vanillaActionId;

			return ControlsGameUiSeparationPolicy.GamePadGamePrefix + vanillaActionId;
		}
	}
}
