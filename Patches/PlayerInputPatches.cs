using System.Collections.Generic;
using System.Reflection;
using AdvancedKeybinds.Binding;
using AdvancedKeybinds.Diagnostics;
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
#if AK_DIAGNOSTICS
					AkCpuMeasureSystem.TickPhase("PlayerInput");
					AkCpuMeasureSystem.Begin(AkCpuMeasureSystem.IdPlayerInputDetour);
#endif
					try
					{
#if AK_DIAGNOSTICS
					if (LiveInputDiagnosticSession.IsActive)
						LiveInputDiagnosticSession.Poll();
#endif
					GameWindowInputHook.EnsureInstalledIfNeeded();
					KeybindingUiPatches.SyncPhysicalRowMouseEdges();

					ComboPanel.BeginInputTickEscapeCaptureState();

					// HandleEscape must see Escape before any parent-menu swallow.
					if (ComboPanel.IsOpen)
					{
						ComboPanel.PollSlotCaptureIfActive();
						ComboPanel.HandleEscape();
					}

					if (ComboPanel.ShouldSwallowMenuEscape())
						ComboPanel.PrepareParentMenuForEscape();

#if AK_DIAGNOSTICS
					AkCpuMeasureSystem.Begin(AkCpuMeasureSystem.IdWinKeySuppressorBlock);
#endif
					WindowsKeySuppressor.EnsureInstalled();
					WindowsKeySuppressor.PollWinKeyReleaseGuard();
					WindowsKeySuppressor.FlushDeferredWinActions();
#if AK_DIAGNOSTICS
					AkCpuMeasureSystem.End(AkCpuMeasureSystem.IdWinKeySuppressorBlock);
#endif

#if AK_DIAGNOSTICS
					ComboPanelEscPathDiag.Note("PlayerInput.beforeOrig");
#endif
				orig();
#if AK_DIAGNOSTICS
					ComboPanelEscPathDiag.Note("PlayerInput.afterOrig");
#endif
					ComboPanel.SuppressEscapeAfterVanillaInputRebuild();

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
#if AK_DIAGNOSTICS
				try
				{
					AkCpuMeasureSystem.Begin(AkCpuMeasureSystem.IdZoomTick);
					ZoomKeyLiveVerifySystem.TickFromGameLoop("PlayerInput");
					AkCpuMeasureSystem.End(AkCpuMeasureSystem.IdZoomTick);
				}
				catch (System.Exception)
				{
					// Never let investigation verifier break the input detour.
				}
#endif
					}
					finally
					{
#if AK_DIAGNOSTICS
						AkCpuMeasureSystem.End(AkCpuMeasureSystem.IdPlayerInputDetour);
#endif
					}
				});
			}
			else
			{
				AdvancedKeybinds.Instance?.Logger.Warn("Advanced Keybinds could not find PlayerInput update method; advanced bindings may be limited.");
			}
		}

		private static void ApplyAdvancedBindingsToTriggers()
		{
#if AK_DIAGNOSTICS
			AkCpuMeasureSystem.Begin(AkCpuMeasureSystem.IdBindingInputEngine);
#endif
			BindingInputEngine.Update();
#if AK_DIAGNOSTICS
			AkCpuMeasureSystem.End(AkCpuMeasureSystem.IdBindingInputEngine);
#endif

			TriggersSet justPressed = PlayerInput.Triggers.JustPressed;
			TriggersSet current = PlayerInput.Triggers.Current;
			TriggersSet justReleased = PlayerInput.Triggers.JustReleased;

			BindingInputEngine.ApplyResultsToTriggers(justPressed, current);
			// AK-owned actions: discard vanilla/default edges for that action id only (not global keys).
			BindingInputEngine.ApplyOwnershipToTriggers(justPressed, current, justReleased);
			SuppressConflictingVanillaTriggers(justPressed);
		}

		private static readonly List<string> _suppressScratch = new(8);

		/// <summary>
		/// Stops a plain vanilla single-key trigger from also firing when its key is currently being
		/// consumed by a fully-held combo (e.g. "I" alone vs "Ctrl+Alt+I").
		/// </summary>
		private static void SuppressConflictingVanillaTriggers(TriggersSet justPressed)
		{
			_suppressScratch.Clear();

			foreach (KeyValuePair<string, bool> pair in justPressed.KeyStatus)
			{
				if (!pair.Value)
					continue;

				if (BindingInputEngine.WasJustPressed(pair.Key))
					continue; // this trigger was fired by us, keep it

				if (BindingInputEngine.ShouldSuppressVanillaTrigger(pair.Key, GetAssignedKeys(pair.Key)))
					_suppressScratch.Add(pair.Key);
			}

			for (int i = 0; i < _suppressScratch.Count; i++)
				justPressed.KeyStatus[_suppressScratch[i]] = false;
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
