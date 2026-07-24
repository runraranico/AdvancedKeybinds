using System;
using System.Reflection;
using AdvancedKeybinds.Core;
using Terraria;
using Terraria.ModLoader;

namespace AdvancedKeybinds.Patches
{
	/// <summary>
	/// Skips Terraria's F7/F8/F10/F11 special-UI toggles when
	/// <see cref="TerrariaDebugUiHotkeyPolicy.SuppressVanillaSpecialUi"/> is on.
	/// Does not rewrite <see cref="Main.keyState"/> — AK capture / BindingInputEngine still see the keys.
	/// </summary>
	internal static class TerrariaDebugUiHotkeyPatches
	{
		private delegate void orig_Void();

		public static void Apply()
		{
			Hook("DoUpdate_F7_ToggleGraphicsDiagnostics");
			Hook("DoUpdate_F8_ToggleNetDiagnostics");
			Hook("DoUpdate_F10_ToggleFPS");
			Hook("DoUpdate_F11_ToggleUI");
		}

		private static void Hook(string methodName)
		{
			MethodInfo method = typeof(Main).GetMethod(
				methodName,
				BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
				binder: null,
				types: Type.EmptyTypes,
				modifiers: null);

			if (method == null)
			{
				AdvancedKeybinds.Instance?.Logger.Warn(
					"Advanced Keybinds could not find Main." + methodName
					+ "; Terraria debug UI hotkey suppress may be incomplete.");
				return;
			}

			ModDetours.Apply(method, (orig_Void orig) => {
				if (TerrariaDebugUiHotkeyPolicy.ShouldSkipVanillaToggle())
					return;

				orig();
			});
		}
	}
}
