using System.Collections.Generic;
using System.Reflection;
using AdvancedKeybinds.Binding;
using AdvancedKeybinds.UI;
using Terraria;
using Terraria.GameInput;
using Terraria.ModLoader;

namespace AdvancedKeybinds.Patches
{
	internal static class PlayerInputPatches
	{
		private delegate void orig_Void();

		public static void Apply()
		{
			MethodInfo updateInput = typeof(PlayerInput).GetMethod("UpdateInput", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
				?? typeof(PlayerInput).GetMethod("Update", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

			if (updateInput != null)
			{
				ModDetours.Apply(updateInput, (orig_Void orig) => {
					GameWindowInputHook.EnsureInstalledIfNeeded();
					KeybindingUiPatches.SyncPhysicalRowMouseEdges();

					// HandleEscape must see Escape before any parent-menu swallow.
					if (ComboPanel.IsOpen)
					{
						ComboPanel.PollSlotCaptureIfActive();
						ComboPanel.HandleEscape();
					}

					if (ComboPanel.ShouldSwallowMenuEscape())
						ComboPanel.PrepareParentMenuForEscape();

					WindowsKeySuppressor.EnsureInstalled();
					WindowsKeySuppressor.PollWinKeyReleaseGuard();
					WindowsKeySuppressor.FlushDeferredWinActions();

					orig();

					if (ComboPanel.IsOpen)
					{
						ComboPanel.PollSlotCaptureIfActive();
						ComboPanel.GuardModalInput();
					}
					else if (ComboPanel.ShouldSwallowMenuEscape())
					{
						ComboPanel.PrepareParentMenuForEscape();
					}

					ApplyAdvancedBindingsToTriggers();
				});
			}
			else
			{
				AdvancedKeybinds.Instance?.Logger.Warn("Advanced Keybinds could not find PlayerInput update method; advanced bindings may be limited.");
			}
		}

		private static void ApplyAdvancedBindingsToTriggers()
		{
			BindingInputEngine.Update();

			TriggersSet justPressed = PlayerInput.Triggers.JustPressed;
			TriggersSet current = PlayerInput.Triggers.Current;

			foreach (KeyValuePair<string, ComboBinding> pair in BindingStorage.AllBindings)
			{
				string actionId = pair.Key;

				if (BindingInputEngine.WasJustPressed(actionId))
					justPressed.KeyStatus[actionId] = true;

				if (BindingInputEngine.IsCurrent(actionId))
					current.KeyStatus[actionId] = true;
			}

			SuppressConflictingVanillaTriggers(justPressed);
		}

		/// <summary>
		/// Stops a plain vanilla single-key trigger from also firing when its key is currently being
		/// consumed by a fully-held combo (e.g. "I" alone vs "Ctrl+Alt+I").
		/// </summary>
		private static void SuppressConflictingVanillaTriggers(TriggersSet justPressed)
		{
			List<string> toSuppress = null;

			foreach (KeyValuePair<string, bool> pair in justPressed.KeyStatus)
			{
				if (!pair.Value)
					continue;

				if (BindingInputEngine.WasJustPressed(pair.Key))
					continue; // this trigger was fired by us, keep it

				if (BindingInputEngine.ShouldSuppressVanillaTrigger(pair.Key, GetAssignedKeys(pair.Key)))
					(toSuppress ??= new List<string>()).Add(pair.Key);
			}

			if (toSuppress == null)
				return;

			foreach (string actionId in toSuppress)
				justPressed.KeyStatus[actionId] = false;
		}

		private static List<string> GetAssignedKeys(string actionId)
		{
			PlayerInputProfile profile = PlayerInput.CurrentProfile;
			if (profile?.InputModes == null)
				return null;

			if (!profile.InputModes.TryGetValue(InputMode.Keyboard, out KeyConfiguration config))
				return null;

			return config.KeyStatus.TryGetValue(actionId, out List<string> keys) ? keys : null;
		}
	}
}
