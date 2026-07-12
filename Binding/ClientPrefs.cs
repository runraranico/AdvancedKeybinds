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

		public static void Load()
		{
			_data = new PrefsData();
			_loaded = true;

			try
			{
				if (!File.Exists(FilePath))
					return;

				PrefsData loaded = JsonConvert.DeserializeObject<PrefsData>(File.ReadAllText(FilePath));
				if (loaded != null)
					_data = loaded;
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
