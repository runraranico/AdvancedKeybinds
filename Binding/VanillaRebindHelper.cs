using System;
using System.Collections.Generic;
using System.Reflection;
using Terraria.GameInput;

namespace AdvancedKeybinds.Binding
{
	/// <summary>
	/// Reads and writes vanilla's own key configuration for the right slot's plain single-key taps.
	/// The new combo panel never starts a vanilla listen, so no listen-state juggling is needed here.
	/// </summary>
	internal static class VanillaRebindHelper
	{
		private static bool _onBindingChangeResolved;
		private static FieldInfo _onBindingChangeField;

		/// <summary>
		/// Resolves the KeyConfiguration that stores gameplay key bindings (the Keyboard profile).
		/// </summary>
		private static bool TryGetBindingConfiguration(out KeyConfiguration config)
		{
			config = null;
			PlayerInputProfile profile = PlayerInput.CurrentProfile;
			if (profile?.InputModes == null)
				return false;

			if (profile.InputModes.TryGetValue(InputMode.Keyboard, out config))
				return true;

			if (profile.InputModes.TryGetValue(InputMode.KeyboardUI, out config))
				return true;

			foreach (KeyValuePair<InputMode, KeyConfiguration> pair in profile.InputModes)
			{
				config = pair.Value;
				return true;
			}

			return false;
		}

		/// <summary>Returns the first vanilla-assigned key for an action, or null if unset.</summary>
		public static string GetAssignedKey(string trigger)
		{
			if (string.IsNullOrEmpty(trigger) || !TryGetBindingConfiguration(out KeyConfiguration config))
				return null;

			if (config.KeyStatus.TryGetValue(trigger, out List<string> keys) && keys.Count > 0)
				return keys[0];

			return null;
		}

		public static void ClearVanillaBinding(string trigger)
		{
			if (string.IsNullOrEmpty(trigger) || !TryGetBindingConfiguration(out KeyConfiguration config))
				return;

			if (config.KeyStatus.TryGetValue(trigger, out List<string> keys))
			{
				keys.Clear();
				NotifyBindingChanged();
			}
		}

		public static void AssignVanillaBinding(string trigger, string key)
		{
			if (string.IsNullOrEmpty(trigger) || string.IsNullOrEmpty(key) || !TryGetBindingConfiguration(out KeyConfiguration config))
				return;

			if (!config.KeyStatus.TryGetValue(trigger, out List<string> keys))
			{
				keys = new List<string>();
				config.KeyStatus[trigger] = keys;
			}

			keys.Clear();
			keys.Add(key);
			NotifyBindingChanged();
		}

		/// <summary>
		/// Restores <paramref name="trigger"/> to the keys stored in <see cref="PlayerInput.OriginalProfiles"/>
		/// (same source as the per-row / profile defaults). Clears the list when the original had none.
		/// </summary>
		public static void ResetVanillaBindingToDefault(string trigger)
		{
			if (string.IsNullOrEmpty(trigger) || !TryGetBindingConfiguration(out KeyConfiguration config))
				return;

			if (!config.KeyStatus.TryGetValue(trigger, out List<string> keys))
				return;

			keys.Clear();
			List<string> defaults = TryGetOriginalKeys(trigger);
			if (defaults != null)
			{
				foreach (string key in defaults)
				{
					if (!string.IsNullOrEmpty(key))
						keys.Add(key);
				}
			}

			NotifyBindingChanged();
		}

		private static List<string> TryGetOriginalKeys(string trigger)
		{
			Dictionary<string, PlayerInputProfile> originals = PlayerInput.OriginalProfiles;
			if (originals == null || originals.Count == 0)
				return null;

			PlayerInputProfile current = PlayerInput.CurrentProfile;
			if (current != null
				&& !string.IsNullOrEmpty(current.Name)
				&& originals.TryGetValue(current.Name, out PlayerInputProfile named)
				&& TryReadKeys(named, trigger, out List<string> fromNamed))
			{
				return fromNamed;
			}

			foreach (KeyValuePair<string, PlayerInputProfile> pair in originals)
			{
				if (TryReadKeys(pair.Value, trigger, out List<string> found))
					return found;
			}

			return null;
		}

		private static bool TryReadKeys(PlayerInputProfile profile, string trigger, out List<string> keys)
		{
			keys = null;
			if (profile?.InputModes == null)
				return false;

			if (!profile.InputModes.TryGetValue(InputMode.Keyboard, out KeyConfiguration config)
				&& !profile.InputModes.TryGetValue(InputMode.KeyboardUI, out config))
			{
				foreach (KeyValuePair<InputMode, KeyConfiguration> pair in profile.InputModes)
				{
					config = pair.Value;
					break;
				}
			}

			if (config?.KeyStatus == null || !config.KeyStatus.TryGetValue(trigger, out List<string> source) || source == null)
				return false;

			keys = new List<string>(source);
			return true;
		}

		private static void NotifyBindingChanged()
		{
			if (!_onBindingChangeResolved)
			{
				_onBindingChangeResolved = true;
				_onBindingChangeField = typeof(PlayerInput).GetField("OnBindingChange", BindingFlags.Static | BindingFlags.NonPublic);
			}

			if (_onBindingChangeField?.GetValue(null) is System.Action callback)
				callback();
		}
	}
}
