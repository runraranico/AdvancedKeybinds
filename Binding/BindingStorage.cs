using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Terraria;
using Terraria.GameInput;

namespace AdvancedKeybinds.Binding
{
	/// <summary>
	/// Persists advanced (non-vanilla) combo bindings to a JSON file, keyed by action id.
	/// Multi-key / hold / mouse / hook-managed bindings are stored here for input.
	/// Plain single-key taps are also stored so the combo panel can restore left/middle/right slot
	/// placement, while vanilla still owns the controls-list label and gameplay input for those.
	/// </summary>
	public static class BindingStorage
	{
		private static readonly Dictionary<string, ComboBinding> Combos = new();

		private static string FilePath => Path.Combine(Main.SavePath, "AdvancedKeybinds", "bindings.json");

		public static void Initialize()
		{
			LoadFromFile();
			if (SanitizeLoadedBindings())
				SaveToFile();
			SyncPlainSingleKeyLayoutsToVanilla();
			PromoteHookManagedVanillaBindings();
			BindingInputEngine.InvalidateBindings();
		}

		/// <summary>
		/// Drops unsupported Hold flags. LeftClick-alone is user-confirmable and is not removed on load.
		/// Returns true when the in-memory set changed and should be persisted.
		/// </summary>
		private static bool SanitizeLoadedBindings()
		{
			bool changed = false;

			foreach (KeyValuePair<string, ComboBinding> pair in Combos)
			{
				ComboBinding combo = pair.Value;
				if (combo == null)
					continue;

				bool holdBefore = combo.Hold;
				bool holdOnReleaseBefore = combo.HoldOnRelease;
				BindingRestrictions.ClearUnsupportedHoldFlags(combo);
				if (holdBefore != combo.Hold || holdOnReleaseBefore != combo.HoldOnRelease)
				{
					changed = true;
					AkDiag.Log("Sanitized unsupported Hold on load action=" + pair.Key);
				}
			}

			return changed;
		}

		/// <summary>
		/// Plain single-key layouts are kept for combo-panel slot memory, but vanilla must own the key
		/// so the controls list shows the normal centered label.
		/// </summary>
		private static void SyncPlainSingleKeyLayoutsToVanilla()
		{
			bool changed = false;
			foreach (KeyValuePair<string, ComboBinding> pair in Combos)
			{
				ComboBinding combo = pair.Value;
				if (combo == null || !combo.IsPlainSingleTap)
					continue;

				VanillaRebindHelper.AssignVanillaBinding(pair.Key, combo.Keys[0]);
				changed = true;
			}

			if (changed)
				AkDiag.Log("Synced plain single-key layouts to vanilla for list display.");
		}

		public static void Unload()
		{
			Combos.Clear();
		}

		public static ComboBinding Get(string actionId)
		{
			if (string.IsNullOrEmpty(actionId) || !Combos.TryGetValue(actionId, out ComboBinding combo))
				return null;

			return combo;
		}

		public static bool HasAdvancedBindings(string actionId)
		{
			return Get(actionId) != null;
		}

		/// <summary>
		/// Stores the combo for an action. Plain single-key taps are kept so the combo panel can restore
		/// left/middle/right slot placement; input/list display for those still go through vanilla.
		/// </summary>
		public static void Set(string actionId, ComboBinding combo)
		{
			if (string.IsNullOrEmpty(actionId))
				return;

			if (combo == null || combo.IsEmpty)
				Combos.Remove(actionId);
			else
				Combos[actionId] = combo.Clone();

			SaveToFile();
			BindingInputEngine.InvalidateBindings();
		}

		public static void ClearBindings(string actionId)
		{
			if (string.IsNullOrEmpty(actionId))
				return;

			if (Combos.Remove(actionId))
			{
				SaveToFile();
				BindingInputEngine.InvalidateBindings();
			}
		}

		public static IEnumerable<KeyValuePair<string, ComboBinding>> AllBindings => Combos;

		public static void PromoteHookManagedVanillaBindings()
		{
			PlayerInputProfile profile = PlayerInput.CurrentProfile;
			if (profile?.InputModes == null)
				return;

			if (!profile.InputModes.TryGetValue(InputMode.Keyboard, out KeyConfiguration config)
				&& !profile.InputModes.TryGetValue(InputMode.KeyboardUI, out config))
			{
				foreach (KeyValuePair<InputMode, KeyConfiguration> pair in profile.InputModes)
				{
					config = pair.Value;
					break;
				}
			}

			if (config?.KeyStatus == null)
				return;

			bool changed = false;
			foreach (KeyValuePair<string, List<string>> pair in config.KeyStatus)
			{
				if (Combos.ContainsKey(pair.Key))
					continue;

				List<string> keys = pair.Value;
				if (keys == null || keys.Count != 1)
					continue;

				if (!BindingRestrictions.IsHookManagedTapToken(keys[0]))
					continue;

				Combos[pair.Key] = new ComboBinding { Slot3 = keys[0] };
				keys.Clear();
				changed = true;
				AkDiag.Log("Promoted hook-managed vanilla binding action=" + pair.Key + " key=" + Combos[pair.Key].Slot3);
			}

			if (changed)
				SaveToFile();
		}

		private static void LoadFromFile()
		{
			Combos.Clear();

			try
			{
				if (!File.Exists(FilePath))
					return;

				string json = File.ReadAllText(FilePath);

				Dictionary<string, ComboBinding> data;
				try
				{
					data = JsonConvert.DeserializeObject<Dictionary<string, ComboBinding>>(json);
				}
				catch (JsonException)
				{
					// A file from the old (list-based) format can't be read into the new combo format.
					// Discard it so we start clean instead of throwing on every launch.
					AdvancedKeybinds.Instance?.Logger.Info("Advanced Keybinds: resetting incompatible bindings file (old format).");
					TryDeleteFile();
					return;
				}

				if (data == null)
					return;

				foreach (KeyValuePair<string, ComboBinding> pair in data)
				{
					if (pair.Value == null || pair.Value.IsEmpty)
						continue;

					Combos[pair.Key] = pair.Value;
				}
			}
			catch (Exception ex)
			{
				AdvancedKeybinds.Instance?.Logger.Warn("Advanced Keybinds failed to load bindings: " + ex.Message);
			}
		}

		private static void TryDeleteFile()
		{
			try
			{
				if (File.Exists(FilePath))
					File.Delete(FilePath);
			}
			catch
			{
			}
		}

		private static void SaveToFile()
		{
			try
			{
				string directory = Path.GetDirectoryName(FilePath);
				if (!string.IsNullOrEmpty(directory))
					Directory.CreateDirectory(directory);

				string json = JsonConvert.SerializeObject(Combos, Formatting.Indented);
				File.WriteAllText(FilePath, json);
			}
			catch (Exception ex)
			{
				AdvancedKeybinds.Instance?.Logger.Warn("Advanced Keybinds failed to save bindings: " + ex.Message);
			}
		}
	}
}
