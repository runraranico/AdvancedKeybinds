using System.Collections.Generic;
using System.Reflection;
using Terraria.ModLoader;

namespace AdvancedKeybinds.Binding
{
	internal static class ModKeybindReflection
	{
		private static readonly FieldInfo ModField = typeof(ModKeybind).GetField("Mod", BindingFlags.Instance | BindingFlags.NonPublic);
		private static readonly FieldInfo NameField = typeof(ModKeybind).GetField("Name", BindingFlags.Instance | BindingFlags.NonPublic);
		// Auto-property: must use PropertyInfo, not FieldInfo("DefaultBinding").
		private static readonly PropertyInfo DefaultBindingProperty = typeof(ModKeybind).GetProperty(
			"DefaultBinding",
			BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
		private static readonly FieldInfo ModKeybindsField = typeof(KeybindLoader).GetField(
			"modKeybinds",
			BindingFlags.Static | BindingFlags.NonPublic);

		public static string GetActionId(ModKeybind keybind)
		{
			if (keybind == null)
				return string.Empty;

			Mod mod = ModField?.GetValue(keybind) as Mod;
			string name = NameField?.GetValue(keybind) as string;
			if (mod == null || string.IsNullOrEmpty(name))
				return string.Empty;

			return $"{mod.Name}/{name}";
		}

		/// <summary>
		/// Registered default for a mod keybind (e.g. DualWielding Use Offhand → Mouse2),
		/// matching the "Reset to Default (...)" label in Mod Controls.
		/// </summary>
		public static bool TryGetRegisteredDefaultBinding(string actionId, out string defaultBinding)
		{
			defaultBinding = null;
			if (string.IsNullOrEmpty(actionId) || DefaultBindingProperty == null || ModKeybindsField == null)
				return false;

			if (ModKeybindsField.GetValue(null) is not IDictionary<string, ModKeybind> map)
				return false;

			if (!map.TryGetValue(actionId, out ModKeybind keybind) || keybind == null)
				return false;

			defaultBinding = DefaultBindingProperty.GetValue(keybind) as string;
			return !string.IsNullOrWhiteSpace(defaultBinding);
		}
	}
}
