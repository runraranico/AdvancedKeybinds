using System;
using System.Collections.Generic;
using System.Reflection;
using AdvancedKeybinds.Binding;
using Terraria.ModLoader;

namespace AdvancedKeybinds.Patches
{
	internal static class ModKeybindPatches
	{
		private delegate bool orig_BoolGetter(ModKeybind self);

		public static void Apply()
		{
			// New ModKeybinds can throw KeyNotFoundException from vanilla JustPressed/Current until the
			// input profile gains that action id (common right after adding a keybind / on the title screen).
			HookBoolGetter("get_JustPressed", (orig, self) => {
				try
				{
					return orig(self) || BindingInputEngine.WasJustPressed(ModKeybindReflection.GetActionId(self));
				}
				catch (KeyNotFoundException)
				{
					if (AkDiag.Enabled) AkDiag.Log("ModKeybind.JustPressed KeyNotFound for " + ModKeybindReflection.GetActionId(self));
					return BindingInputEngine.WasJustPressed(ModKeybindReflection.GetActionId(self));
				}
			});
			HookBoolGetter("get_Current", (orig, self) => {
				try
				{
					return orig(self) || BindingInputEngine.IsCurrent(ModKeybindReflection.GetActionId(self));
				}
				catch (KeyNotFoundException)
				{
					if (AkDiag.Enabled) AkDiag.Log("ModKeybind.Current KeyNotFound for " + ModKeybindReflection.GetActionId(self));
					return BindingInputEngine.IsCurrent(ModKeybindReflection.GetActionId(self));
				}
			});
		}

		private static void HookBoolGetter(string getterName, Func<orig_BoolGetter, ModKeybind, bool> hook)
		{
			PropertyInfo property = typeof(ModKeybind).GetProperty(getterName[4..], BindingFlags.Instance | BindingFlags.Public);
			if (property?.GetGetMethod() is not MethodInfo getter)
				return;

			ModDetours.Apply(getter, hook);
		}
	}
}
