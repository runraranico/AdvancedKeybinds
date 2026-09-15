using System;
using System.Collections.Generic;
using System.Reflection;
using AdvancedKeybinds.Binding;
using AdvancedKeybinds.Logic;
using Terraria.GameInput;
using Terraria.ModLoader;

namespace AdvancedKeybinds.Patches
{
	internal static class ModKeybindPatches
	{
		private delegate bool orig_BoolGetter(ModKeybind self);

		public static void Apply()
		{
			// New ModKeybinds are absent from TriggersSet.KeyStatus until tModLoader rebuilds the trigger
			// sets after mod load, and vanilla JustPressed/Current index that dictionary directly. Skip
			// orig while the action id is unreadable: throwing there would be logged by tModLoader's
			// first-chance handler even though the keybind is simply unbound / not yet registered.
			// The catch stays as a guard for foreign hooks further down the chain; it must not be the
			// mechanism that keeps normal frames quiet.
			HookBoolGetter("get_JustPressed", (orig, self) => {
				string actionId = ModKeybindReflection.GetActionId(self);
				try
				{
					bool vanilla = ModKeybindTriggerReadPolicy.IsActionIdReadable(
							PlayerInput.Triggers?.JustPressed?.KeyStatus, actionId)
						&& orig(self);
					return vanilla || BindingInputEngine.WasJustPressed(actionId);
				}
				catch (KeyNotFoundException)
				{
					if (AkDiag.Enabled) AkDiag.Log("ModKeybind.JustPressed KeyNotFound for " + actionId);
					return BindingInputEngine.WasJustPressed(actionId);
				}
			});
			HookBoolGetter("get_Current", (orig, self) => {
				string actionId = ModKeybindReflection.GetActionId(self);
				try
				{
					bool vanilla = ModKeybindTriggerReadPolicy.IsActionIdReadable(
							PlayerInput.Triggers?.Current?.KeyStatus, actionId)
						&& orig(self);
					return vanilla || BindingInputEngine.IsCurrent(actionId);
				}
				catch (KeyNotFoundException)
				{
					if (AkDiag.Enabled) AkDiag.Log("ModKeybind.Current KeyNotFound for " + actionId);
					return BindingInputEngine.IsCurrent(actionId);
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
