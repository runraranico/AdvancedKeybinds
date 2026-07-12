using System;
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
			HookBoolGetter("get_JustPressed", (orig, self) => orig(self) || BindingInputEngine.WasJustPressed(ModKeybindReflection.GetActionId(self)));
			HookBoolGetter("get_Current", (orig, self) => orig(self) || BindingInputEngine.IsCurrent(ModKeybindReflection.GetActionId(self)));
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
