using System;
using System.IO;
using Newtonsoft.Json;
using Terraria;

namespace AdvancedKeybinds.Binding
{
	/// <summary>Small client-side flags that are not part of bindings.json.</summary>
	internal static class ClientPrefs
	{
		private sealed class PrefsData
		{
			public bool OpenKeybindMenuCustomized;
			public bool HoldTierHudHotkeyCustomized;
			public bool EnableCursorConfineOnLoad;
			/// <summary>Last committed Hold HUD Custom position (screen-normalized). Survives ModConfig UI saves that wipe HiddenConfigElement fields.</summary>
			public bool HoldHudHasCustomPlacement;
			public float HoldHudCustomNormX;
			public float HoldHudCustomNormY;
			/// <summary>
			/// True after a drag commit until the user explicitly picks Feet/Mouse (hotkey or config).
			/// Distinguishes ModConfig UI wiping Anchor back to Feet from an intentional Feet choice.
			/// </summary>
			public bool HoldHudPreferCustom;
			/// <summary>Show / AllowDrag remembered per Feet/Mouse when the cycle hotkey leaves that mode for Custom.</summary>
			public bool HoldHudHasFeetModeToggles;
			public bool HoldHudFeetEnabled;
			public bool HoldHudFeetAllowDrag;
			public bool HoldHudHasMouseModeToggles;
			public bool HoldHudMouseEnabled;
			public bool HoldHudMouseAllowDrag;
		}

		private static PrefsData _data = new();
		private static bool _loaded;

		private static string FilePath => Path.Combine(Main.SavePath, "AdvancedKeybinds", "prefs.json");

		public static bool OpenKeybindMenuCustomized
		{
			get
			{
				EnsureLoaded();
				return _data.OpenKeybindMenuCustomized;
			}
			set
			{
				EnsureLoaded();
				if (_data.OpenKeybindMenuCustomized == value)
					return;

				_data.OpenKeybindMenuCustomized = value;
				Save();
			}
		}

		public static bool HoldTierHudHotkeyCustomized
		{
			get
			{
				EnsureLoaded();
				return _data.HoldTierHudHotkeyCustomized;
			}
			set
			{
				EnsureLoaded();
				if (_data.HoldTierHudHotkeyCustomized == value)
					return;

				_data.HoldTierHudHotkeyCustomized = value;
				Save();
			}
		}

		public static bool EnableCursorConfineOnLoad
		{
			get
			{
				EnsureLoaded();
				return _data.EnableCursorConfineOnLoad;
			}
			set
			{
				EnsureLoaded();
				if (_data.EnableCursorConfineOnLoad == value)
					return;

				_data.EnableCursorConfineOnLoad = value;
				Save();
			}
		}

		/// <summary>Remember Custom HUD placement so ModConfig UI save cannot wipe HiddenConfig floats.</summary>
		/// <param name="preferCustom">
		/// null = leave PreferCustom unchanged (normalize/migrate must not resurrect Custom after Feet/Mouse).
		/// true/false = set PreferCustom explicitly (drag commit / EnterCustom / NotifyPreset).
		/// </param>
		public static void SetHoldHudCustomPlacement(float normX, float normY, bool? preferCustom = true)
		{
			EnsureLoaded();
			_data.HoldHudHasCustomPlacement = true;
			_data.HoldHudCustomNormX = normX;
			_data.HoldHudCustomNormY = normY;
			if (preferCustom.HasValue)
				_data.HoldHudPreferCustom = preferCustom.Value;
			Save();
		}

		public static bool TryGetHoldHudCustomPlacement(out float normX, out float normY)
		{
			EnsureLoaded();
			normX = _data.HoldHudCustomNormX;
			normY = _data.HoldHudCustomNormY;
			return _data.HoldHudHasCustomPlacement;
		}

		public static bool HoldHudPreferCustom
		{
			get
			{
				EnsureLoaded();
				return _data.HoldHudPreferCustom;
			}
		}

		public static void SetHoldHudPreferCustom(bool prefer)
		{
			EnsureLoaded();
			if (_data.HoldHudPreferCustom == prefer)
				return;
			_data.HoldHudPreferCustom = prefer;
			Save();
		}

		/// <summary>
		/// Snapshot Show / AllowDrag for Feet or Mouse before the cycle hotkey leaves that mode.
		/// </summary>
		public static void SaveHoldHudModeToggles(HoldTierHudAnchor mode, bool enabled, bool allowDrag)
		{
			if (mode != HoldTierHudAnchor.Feet && mode != HoldTierHudAnchor.Mouse)
				return;

			EnsureLoaded();
			if (mode == HoldTierHudAnchor.Feet)
			{
				if (_data.HoldHudHasFeetModeToggles
					&& _data.HoldHudFeetEnabled == enabled
					&& _data.HoldHudFeetAllowDrag == allowDrag)
					return;
				_data.HoldHudHasFeetModeToggles = true;
				_data.HoldHudFeetEnabled = enabled;
				_data.HoldHudFeetAllowDrag = allowDrag;
			}
			else
			{
				if (_data.HoldHudHasMouseModeToggles
					&& _data.HoldHudMouseEnabled == enabled
					&& _data.HoldHudMouseAllowDrag == allowDrag)
					return;
				_data.HoldHudHasMouseModeToggles = true;
				_data.HoldHudMouseEnabled = enabled;
				_data.HoldHudMouseAllowDrag = allowDrag;
			}

			Save();
		}

		/// <summary>
		/// Restore Show / AllowDrag for Feet or Mouse when the cycle hotkey leaves Custom.
		/// Missing snapshots default to off (matches first-load defaults).
		/// </summary>
		public static void GetHoldHudModeToggles(HoldTierHudAnchor mode, out bool enabled, out bool allowDrag)
		{
			EnsureLoaded();
			if (mode == HoldTierHudAnchor.Feet && _data.HoldHudHasFeetModeToggles)
			{
				enabled = _data.HoldHudFeetEnabled;
				allowDrag = _data.HoldHudFeetAllowDrag;
				return;
			}

			if (mode == HoldTierHudAnchor.Mouse && _data.HoldHudHasMouseModeToggles)
			{
				enabled = _data.HoldHudMouseEnabled;
				allowDrag = _data.HoldHudMouseAllowDrag;
				return;
			}

			enabled = false;
			allowDrag = false;
		}

		public static void Load()
		{
			_data = new PrefsData();
			_loaded = true;

			try
			{
				if (!File.Exists(FilePath))
					return;

				string text = File.ReadAllText(FilePath);
				PrefsData loaded = JsonConvert.DeserializeObject<PrefsData>(text);
				if (loaded != null)
				{
					_data = loaded;
					// Older prefs lacked PreferCustom: keep using the dragged spot until Feet/Mouse is chosen.
					if (_data.HoldHudHasCustomPlacement
						&& text.IndexOf("HoldHudPreferCustom", StringComparison.Ordinal) < 0)
					{
						_data.HoldHudPreferCustom = true;
					}

					// Pre-existing installs: do not strip a user-bound U on first run of the U-default feature.
					if (text.IndexOf("HoldTierHudHotkeyCustomized", StringComparison.Ordinal) < 0)
						_data.HoldTierHudHotkeyCustomized = true;
				}
			}
			catch (Exception ex)
			{
				AdvancedKeybinds.Instance?.Logger.Warn("Advanced Keybinds failed to load prefs: " + ex.Message);
			}
		}

		public static void Unload()
		{
			_data = new PrefsData();
			_loaded = false;
		}

		private static void EnsureLoaded()
		{
			if (!_loaded)
				Load();
		}

		private static void Save()
		{
			try
			{
				string directory = Path.GetDirectoryName(FilePath);
				if (!string.IsNullOrEmpty(directory))
					Directory.CreateDirectory(directory);

				File.WriteAllText(FilePath, JsonConvert.SerializeObject(_data, Formatting.Indented));
			}
			catch (Exception ex)
			{
				AdvancedKeybinds.Instance?.Logger.Warn("Advanced Keybinds failed to save prefs: " + ex.Message);
			}
		}
	}
}
