using Terraria.ModLoader;

namespace AdvancedKeybinds.Binding
{
	/// <summary>
	/// Keeps mod-menu shortcut rows showing 未設定 on first launch while still exposing
	/// registered defaults (L / K / O) via the reset button.
	/// </summary>
	internal static class OpenKeybindMenuDefaults
	{
		public static void ApplyStartupState()
		{
			if (ClientPrefs.OpenKeybindMenuCustomized)
				return;

			TryStripRegisteredDefault(AdvancedKeybinds.OpenSettingsMenuHotkey, "L");
			TryStripRegisteredDefault(AdvancedKeybinds.OpenKeybindMenuHotkey, "K");
			TryStripRegisteredDefault(AdvancedKeybinds.OpenModConfigHotkey, "O");
		}

		private static void TryStripRegisteredDefault(ModKeybind hotkey, string expectedKey)
		{
			if (hotkey == null)
				return;

			string actionId = ModKeybindReflection.GetActionId(hotkey);
			if (string.IsNullOrEmpty(actionId) || BindingStorage.HasAdvancedBindings(actionId))
				return;

			try
			{
				var keys = hotkey.GetAssignedKeys();
				if (keys == null || keys.Count != 1)
					return;

				if (!string.Equals(keys[0], expectedKey, System.StringComparison.OrdinalIgnoreCase))
					return;

				// tModLoader may apply the registered default on load; strip it so the row stays unset.
				VanillaRebindHelper.ClearVanillaBinding(actionId);
			}
			catch
			{
			}
		}

		public static bool IsOpenKeybindMenuAction(string bind)
		{
			if (string.IsNullOrEmpty(bind))
				return false;

			if (IsActionId(bind, AdvancedKeybinds.OpenSettingsMenuHotkey)
				|| IsActionId(bind, AdvancedKeybinds.OpenKeybindMenuHotkey)
				|| IsActionId(bind, AdvancedKeybinds.OpenModConfigHotkey))
			{
				return true;
			}

			return bind.Contains("OpenSettingsMenu", System.StringComparison.OrdinalIgnoreCase)
				|| bind.Contains("OpenKeybindMenu", System.StringComparison.OrdinalIgnoreCase)
				|| bind.Contains("OpenModConfigMenu", System.StringComparison.OrdinalIgnoreCase)
				|| bind.Contains("Open Controls Menu", System.StringComparison.OrdinalIgnoreCase)
				|| bind.Contains("Open Settings", System.StringComparison.OrdinalIgnoreCase)
				|| bind.Contains("Open Controls", System.StringComparison.OrdinalIgnoreCase)
				|| bind.Contains("Open Mod Configuration", System.StringComparison.OrdinalIgnoreCase)
				|| bind.Contains("Control Settings", System.StringComparison.OrdinalIgnoreCase)
				|| bind.Contains("General Settings", System.StringComparison.OrdinalIgnoreCase)
				|| bind.Contains("Mod Settings", System.StringComparison.OrdinalIgnoreCase)
				|| bind.Contains("設定を開く", System.StringComparison.OrdinalIgnoreCase)
				|| bind.Contains("コントロールを開く", System.StringComparison.OrdinalIgnoreCase)
				|| bind.Contains("MOD設定を開く", System.StringComparison.OrdinalIgnoreCase)
				|| bind.Contains("キーコンフィグ画面を開く", System.StringComparison.OrdinalIgnoreCase)
				|| bind.Contains("一般設定", System.StringComparison.OrdinalIgnoreCase)
				|| bind.Contains("コントロール設定", System.StringComparison.OrdinalIgnoreCase)
				|| bind.Contains("MODの設定", System.StringComparison.OrdinalIgnoreCase);
		}

		/// <summary>Registered ModKeybind defaults (L / K / O) for middle-click row reset.</summary>
		public static bool TryGetRegisteredDefaultKey(string actionId, out string key)
		{
			key = null;
			if (string.IsNullOrEmpty(actionId))
				return false;

			if (IsActionId(actionId, AdvancedKeybinds.OpenSettingsMenuHotkey))
			{
				key = "L";
				return true;
			}

			if (IsActionId(actionId, AdvancedKeybinds.OpenKeybindMenuHotkey))
			{
				key = "K";
				return true;
			}

			if (IsActionId(actionId, AdvancedKeybinds.OpenModConfigHotkey))
			{
				key = "O";
				return true;
			}

			return false;
		}

		private static bool IsActionId(string bind, ModKeybind hotkey)
		{
			string actionId = ModKeybindReflection.GetActionId(hotkey);
			return !string.IsNullOrEmpty(actionId)
				&& string.Equals(bind, actionId, System.StringComparison.OrdinalIgnoreCase);
		}

		public static void MarkCustomized()
		{
			ClientPrefs.OpenKeybindMenuCustomized = true;
		}
	}
}
