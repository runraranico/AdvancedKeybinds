using System.Collections.Generic;
using System.Reflection;
using AdvancedKeybinds.Binding;
using AdvancedKeybinds.Core;
using AdvancedKeybinds.Diagnostics;
using AdvancedKeybinds.UI;
using Terraria;
using Terraria.GameInput;
using Terraria.ModLoader;
using Terraria.UI.Gamepad;

namespace AdvancedKeybinds.Patches
{
	internal static class PlayerInputPatches
	{
		private delegate void orig_Void();
		private delegate void OrigToggleInv(Player self);
		private static bool _toggleInvOneShotHookInstalled;

		public static void Apply()
		{
			TryHookToggleInvWorldOneShot();

			MethodInfo updateInput = typeof(PlayerInput).GetMethod("UpdateInput", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
				?? typeof(PlayerInput).GetMethod("Update", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

			if (updateInput != null)
			{
				ModDetours.Apply(updateInput, (orig_Void orig) => {
					try
					{
					GameWindowInputHook.EnsureInstalledIfNeeded();
					KeybindingUiPatches.SyncPhysicalRowMouseEdges();

					// Non-Windows FNA keyboard cache: advance even when Main.GameUpdateCount stays 0 (menus).
					FnaKeyboardProvider.AdvanceInputTickCache();

					// Independent GamePad subsystem tick (same UpdateInput timing; no keyboard internals).
					FnaGamePadProvider.AdvanceInputTick();
					GamePadProbeSystem.AdvanceProbeFromInputUpdate();

					// Face-South (Pad:A) PRESS while a binding row is focused → direct ComboPanel open.
					PendingGamePadRowConfirm.PollArmFromInputTick();
					// Pad:A on SectionHeader → save StableId before list rebuild remaps PointIds.
					ControlsGamepadAutoScrollPatches.PollSaveFocusOnSectionConfirm();


					int dpadBefore = UILinkPointNavigator.CurrentPoint;
					ControlsUiHost.EnsureInWorldGamepadUiModeForControls();

					ComboPanel.BeginInputTickEscapeCaptureState();

					// HandleEscape must see Escape before any parent-menu swallow.
					if (ComboPanel.IsOpen)
					{
						ComboPanel.PollSlotCaptureIfActive();
						ComboPanel.HandleEscape();
					}


					if (ComboPanel.IsOpen)
					{
						ComboPanel.PrepareParentMenuForEscape();
						ComboPanel.SuppressGamePadParentUiAfterVanillaInputRebuild();
					}

					if (ComboPanel.ShouldSwallowMenuEscape())
						ComboPanel.PrepareParentMenuForEscape();


					ComboPanelCloseInputConsumeGate.PollAndSuppress();

					WindowsKeySuppressor.EnsureInstalled();
					WindowsKeySuppressor.PollWinKeyReleaseGuard();
					WindowsKeySuppressor.FlushDeferredWinActions();

					orig();
					// After MouseInput may demote cursor: keep selection display while D-Pad/stick active.
					ControlsUiHost.ReassertGamepadUiCursorAfterVanillaInput();
					// Fixed Pad ↓ / Pad → → vanilla MouseLeft / Inventory for all GamePad UI (title…in-game).
					FixedGamepadUiConfirmApplicator.ApplyAfterVanillaInput();

					// Temporary NativeGamepadCursorModeProbe: remains stubbed (not restored).

					ComboPanel.SuppressEscapeAfterVanillaInputRebuild();
					// Settings menus: Tab AdditionalOr must not ToggleInv-close (Escape only).
					// World / inventory gameplay: leave Tab Inventory alone.
					ComboPanel.SuppressTabInventoryOnSettingsMenusIfNeeded();
					// After settings Escape leave: keep Inventory dead until Escape key-up.
					ComboPanel.PollSettingsEscapeLeaveInventoryGate();
					ComboPanel.PollWorldEscapeInventoryOneShotGate();
					ComboPanel.SuppressGamePadParentUiAfterVanillaInputRebuild();
					ComboPanel.PollPadBackCloseGateAndSuppressParent();
					ComboPanelCloseInputConsumeGate.PollAndSuppress();

				ControlsPadBHoldCancelFocus.AfterPlayerInputTick();
				ControlsGamepadAutoScrollPatches.PollSectionConfirmRelease();
				ControlsGamepadAutoScrollPatches.PollComboPanelParentRestore();
				ControlsGamepadAutoScrollPatches.PollManualRightStickScroll();
				ComboPanelCloseInputConsumeGate.PollAndSuppress();

				if (FnaGamePadProvider.WasJustPressed(GamePadToken.DPadDown))
					ControlsUiHost.LogDpadEdgeIfNeeded(GamePadToken.DPadDown, dpadBefore);
				if (FnaGamePadProvider.WasJustPressed(GamePadToken.DPadUp))
					ControlsUiHost.LogDpadEdgeIfNeeded(GamePadToken.DPadUp, dpadBefore);

				if (ComboPanel.IsOpen)
				{
					ComboPanel.PollSlotCaptureIfActive();
					ComboPanel.GuardModalInput();
				}
				else if (ComboPanel.ShouldSwallowMenuEscape() || ComboPanel.IsPadBackCloseGateActive())
				{
					ComboPanel.PrepareParentMenuForEscape();
					ComboPanel.PollPadBackCloseGateAndSuppressParent();
				}

				ApplyAdvancedBindingsToTriggers();
					// Advanced OR may re-arm Inventory from Tab after vanilla suppress — clear again on menus.
					ComboPanel.SuppressTabInventoryOnSettingsMenusIfNeeded();
					// Same Escape press after settings leave must not re-open inventory.
					ComboPanel.PollSettingsEscapeLeaveInventoryGate();
					ComboPanel.PollWorldEscapeInventoryOneShotGate();
					// Settings / title Escape leave safety net (KeyStatus owns Escape but AK cleared edge).
					// Product path — not diagnostics (title BackButtonInUse needs Inventory JP).
					InventoryEscapeRearm.ApplyAfterAkSuppress();
					// B1: releaseInventory stuck false after ComboPanel dismiss → Controls ESC dead.
					ComboPanel.TryRestoreReleaseInventoryForSettingsEscapeLeave();
					ComboPanel.PollSettingsEscapeLeaveInventoryGate();
					ComboPanel.PollWorldEscapeInventoryOneShotGate();
					ComboPanel.TryRestoreReleaseInventoryForSettingsEscapeLeave();
					// LIVE: ApplyAdvanced may re-inject Inventory JP after Idle-ESC suppress — re-clear.
					ComboPanel.PollIdleEscParentReleaseProtectAfterAdvanced();
					// After Advanced apply: typed Jump/Throw/QuickMount etc. from display-name edit.
					ComboPanel.PollPostTextEditGameplaySettle();
					}
					finally
					{
					}
				});
			}
			else
			{
				AdvancedKeybinds.Instance?.Logger.Warn("Advanced Keybinds could not find PlayerInput update method; advanced bindings may be limited.");
			}
		}

		/// <summary>
		/// Product: single Player.ToggleInv detour — same-frame coalesce + world Escape one-shot + LIVE note.
		/// </summary>
		private static void TryHookToggleInvWorldOneShot()
		{
			if (_toggleInvOneShotHookInstalled)
				return;
			try
			{
				MethodInfo mi = typeof(Player).GetMethod(
					"ToggleInv",
					BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
				if (mi == null)
				{
					AdvancedKeybinds.Instance?.Logger.Warn(
						"Advanced Keybinds could not hook Player.ToggleInv; world Escape coalesce unavailable.");
					return;
				}

				ModDetours.Apply(mi, (OrigToggleInv orig, Player self) => {
					int gameUpdate = 0;
					try { gameUpdate = unchecked((int)Main.GameUpdateCount); }
					catch { gameUpdate = 0; }

					if (!ToggleInvSameFrameCoalescePolicy.ShouldInvokeOrigForContext(
						gameUpdate,
						FnaKeyboardProvider.InputTickEpoch,
						!Main.gameMenu && (Main.gamePaused || Main.inFancyUI || Main.ingameOptionsWindow),
						ref _toggleInvLastGameUpdate,
						ref _toggleInvInvokedThisUpdate,
						ref _toggleInvLastInputTickEpoch,
						ref _toggleInvInvokedThisInputTick,
						ref _toggleInvMenuInvokedThisInputTick))
					{
						return;
					}

					bool before = false;
					try { before = Main.playerInventory; }
					catch { before = false; }

					orig(self);

					bool after = false;
					try { after = Main.playerInventory; }
					catch { after = false; }

					ComboPanel.TryArmWorldEscapeInventoryOneShotAfterToggleInv();
					InventoryEscLiveVerifySystem.NoteToggleInvCall(self, before, after);
				});
				_toggleInvOneShotHookInstalled = true;
			}
			catch (System.Exception ex)
			{
				AdvancedKeybinds.Instance?.Logger.Warn(
					"Advanced Keybinds ToggleInv coalesce hook failed: " + ex.GetType().Name);
			}
		}

		private static int _toggleInvLastGameUpdate = int.MinValue;
		private static bool _toggleInvInvokedThisUpdate;
		private static int _toggleInvLastInputTickEpoch = int.MinValue;
		private static bool _toggleInvInvokedThisInputTick;
		private static bool _toggleInvMenuInvokedThisInputTick;

		private static void ApplyAdvancedBindingsToTriggers()
		{
			BindingInputEngine.Update();

			TriggersSet justPressed = PlayerInput.Triggers.JustPressed;
			TriggersSet current = PlayerInput.Triggers.Current;
			TriggersSet justReleased = PlayerInput.Triggers.JustReleased;


			BindingInputEngine.ApplyResultsToTriggers(justPressed, current);

			// AK-owned actions: discard vanilla/default edges for that action id only (not global keys).
			BindingInputEngine.ApplyOwnershipToTriggers(justPressed, current, justReleased);

			SuppressConflictingVanillaTriggers(justPressed);
			BindingInputEngine.PublishDecisionSnapshotsAfterApply(justPressed);
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

				// A paused world has fresh input ticks but a frozen combo snapshot.
				// Do not erase a new Inventory edge using last world's consumed keys.
				if (pair.Key == "Inventory" && (BindingInputEngine.ShouldPreservePausedInventoryTriggers()
					|| PlayerInput.CurrentInputMode == InputMode.XBoxGamepad
					|| PlayerInput.CurrentInputMode == InputMode.XBoxGamepadUI))
					continue;

				// TwoKey OR ActionIds: Terraria KeyStatus owns fire — do not suppress.
				if (GamePadTwoKeyTerrariaOrPolicy.IsAllowlistedActionId(pair.Key))
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
