using System.Collections.Generic;
using System.Reflection;
using Terraria.ModLoader;

namespace AdvancedKeybinds.Binding
{
	/// <summary>
	/// Resolves ModKeybind → actionId used by BindingStorage / KeybindLoader.
	/// tML 1.4.4.9: Mod/Name are auto-properties (not fields named Mod/Name).
	/// Official id is ModKeybind.FullName == Mod.Name + "/" + Name (modKeybinds dictionary key).
	/// </summary>
	internal static class ModKeybindReflection
	{
		private static readonly PropertyInfo FullNameProperty = typeof(ModKeybind).GetProperty(
			"FullName",
			BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
		private static readonly PropertyInfo ModProperty = typeof(ModKeybind).GetProperty(
			"Mod",
			BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
		private static readonly PropertyInfo NameProperty = typeof(ModKeybind).GetProperty(
			"Name",
			BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
		// Legacy / fallback if a tML build exposed plain fields (current build does not).
		private static readonly FieldInfo ModField = typeof(ModKeybind).GetField("Mod", BindingFlags.Instance | BindingFlags.NonPublic)
			?? typeof(ModKeybind).GetField("<Mod>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
		private static readonly FieldInfo NameField = typeof(ModKeybind).GetField("Name", BindingFlags.Instance | BindingFlags.NonPublic)
			?? typeof(ModKeybind).GetField("<Name>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
		// Auto-property: must use PropertyInfo, not FieldInfo("DefaultBinding").
		private static readonly PropertyInfo DefaultBindingProperty = typeof(ModKeybind).GetProperty(
			"DefaultBinding",
			BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
		private static readonly FieldInfo ModKeybindsField = typeof(KeybindLoader).GetField(
			"modKeybinds",
			BindingFlags.Static | BindingFlags.NonPublic);

		/// <summary>
		/// Same string as <c>KeybindLoader.modKeybinds</c> keys and BindingStorage action ids
		/// (e.g. <c>FargowiltasSouls/ActiveSkill1</c>).
		/// </summary>
		public static string GetActionId(ModKeybind keybind)
		{
			if (keybind == null)
				return string.Empty;

			// Prefer FullName — computed as Mod.Name + "/" + Name; used by KeybindLoader.RegisterKeybind.
			if (FullNameProperty?.GetValue(keybind) is string fullName && !string.IsNullOrEmpty(fullName))
				return fullName;

			Mod mod = TryGetMod(keybind);
			string name = TryGetName(keybind);
			if (mod == null || string.IsNullOrEmpty(name))
				return string.Empty;

			return $"{mod.Name}/{name}";
		}

		/// <summary>Same Mod/Name used by <see cref="GetActionId"/> (diagnostics).</summary>
		public static bool TryDescribe(ModKeybind keybind, out string modName, out string bindName)
		{
			modName = null;
			bindName = null;
			if (keybind == null)
				return false;

			Mod mod = TryGetMod(keybind);
			bindName = TryGetName(keybind);
			modName = mod?.Name;
			return !string.IsNullOrEmpty(modName) && !string.IsNullOrEmpty(bindName);
		}

		private static Mod TryGetMod(ModKeybind keybind)
		{
			if (ModProperty?.GetValue(keybind) is Mod viaProp)
				return viaProp;
			return ModField?.GetValue(keybind) as Mod;
		}

		private static string TryGetName(ModKeybind keybind)
		{
			if (NameProperty?.GetValue(keybind) is string viaProp && !string.IsNullOrEmpty(viaProp))
				return viaProp;
			return NameField?.GetValue(keybind) as string;
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
