using System;
using System.Reflection;
using AdvancedKeybinds.Binding;
using AdvancedKeybinds.Core;
using AdvancedKeybinds.UI;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.GameInput;

namespace AdvancedKeybinds.Patches
{
	/// <summary>
	/// Lets the D-Pad drive Terraria's own UILinkPoint navigation on in-world FancyUI screens.
	/// </summary>
	/// <remarks>
	/// Vanilla only ORs MenuUp/MenuDown/MenuLeft/MenuRight into the navigator vector on the title
	/// menu, the in-game options window, sign/chest editing and the inventory. The Controls,
	/// Achievements and Mod Config screens match none of those, which is why the left thumbstick
	/// moves the selection there but the D-Pad does nothing.
	///
	/// This adds no navigation of its own: it recomputes the same vector with the D-Pad included,
	/// so cooldown, repeat rate, Travel*/page handling and the cursor follow all stay vanilla.
	/// </remarks>
	internal static class GamePadUiNavigationPatches
	{
		private delegate Vector2 GetNavigatorDirectionsDelegate(TriggersSet self);

		private static bool _applied;

		internal static void Apply()
		{
			if (_applied)
				return;

			MethodInfo target = typeof(TriggersSet).GetMethod(
				nameof(TriggersSet.GetNavigatorDirections),
				BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

			if (target == null)
			{
				AdvancedKeybinds.Instance?.Logger?.Warn(
					"AK: TriggersSet.GetNavigatorDirections not found — D-Pad UI navigation stays vanilla.");
				return;
			}

			ModDetours.Apply(
				target,
				(Func<GetNavigatorDirectionsDelegate, TriggersSet, Vector2>)OnGetNavigatorDirections);
			_applied = true;
		}

		internal static void ClearRuntimeState()
		{
			// Detours themselves are released by ModDetours.DisposeAll on unload.
			_applied = false;
		}

		private static Vector2 OnGetNavigatorDirections(
			GetNavigatorDirectionsDelegate orig,
			TriggersSet self)
		{
			Vector2 vanilla = orig(self);

			try
			{
				if (self == null)
					return vanilla;

				bool controlsOpen = ControlsUiHost.IsManageControlsOpen() && !ComboPanel.IsOpen && !SlotCapture.IsCapturing;
				bool stickUp = false;
				bool stickDown = false;
				bool stickLeft = false;
				bool stickRight = false;
				bool dpadUp = false;
				bool dpadDown = false;
				bool dpadLeft = false;
				bool dpadRight = false;
				if (controlsOpen && PlayerInput.CurrentInputMode == InputMode.XBoxGamepadUI)
				{
					stickUp = FnaGamePadProvider.IsDown(GamePadToken.LeftStickUp);
					stickDown = FnaGamePadProvider.IsDown(GamePadToken.LeftStickDown);
					stickLeft = FnaGamePadProvider.IsDown(GamePadToken.LeftStickLeft);
					stickRight = FnaGamePadProvider.IsDown(GamePadToken.LeftStickRight);
					// Same digital source as Ensure/Reassert — keeps first D-Pad on the Menu* path
					// even when vanilla Processkey did not fill Triggers this frame.
					dpadUp = FnaGamePadProvider.IsDown(GamePadToken.DPadUp);
					dpadDown = FnaGamePadProvider.IsDown(GamePadToken.DPadDown);
					dpadLeft = FnaGamePadProvider.IsDown(GamePadToken.DPadLeft);
					dpadRight = FnaGamePadProvider.IsDown(GamePadToken.DPadRight);
				}

				bool anyStick = stickUp || stickDown || stickLeft || stickRight;
				bool anyDpad = dpadUp || dpadDown || dpadLeft || dpadRight;
				if (!ShouldSupplement()
					&& !(controlsOpen && (anyStick || anyDpad) && PlayerInput.CurrentInputMode == InputMode.XBoxGamepadUI))
					return vanilla;

				GamePadUiNavigationPolicy.MergeLeftStickDigitalIntoNavigatorFlags(
					stickUp || dpadUp, stickDown || dpadDown, stickLeft || dpadLeft, stickRight || dpadRight,
					self.Up, self.Down, self.Left, self.Right,
					self.MenuUp, self.MenuDown, self.MenuLeft, self.MenuRight,
					out bool up, out bool down, out bool left, out bool right,
					out bool menuUp, out bool menuDown, out bool menuLeft, out bool menuRight);

				GamePadUiNavigationPolicy.ResolveNavigatorDirections(
					up, down, left, right,
					menuUp, menuDown, menuLeft, menuRight,
					includeMenuDirections: true,
					out int x,
					out int y);

				return new Vector2(x, y);
			}
			catch (Exception)
			{
				return vanilla;
			}
		}

		/// <summary>
		/// Cheap field reads only — no reflection, no allocation, no gamepad polling. Keyboard and
		/// mouse users fail the input-mode check before anything else runs.
		/// </summary>
		private static bool ShouldSupplement()
		{
			if (PlayerInput.CurrentInputMode != InputMode.XBoxGamepadUI)
				return false;

			bool uiHostOpen = Main.inFancyUI
				|| (Main.InGameUI != null && Main.InGameUI.IsVisible);
			if (!uiHostOpen)
				return false;

			// UsingDpadMovekeys() walks the profile lists and builds strings, so it is only reached
			// when the inventory or an NPC chat is actually open — never on the FancyUI path.
			bool talkingToNpc = Main.LocalPlayer != null && Main.LocalPlayer.talkNPC != -1;
			bool usingDpadMoveKeys = (Main.playerInventory || talkingToNpc)
				&& PlayerInput.CurrentProfile != null
				&& PlayerInput.CurrentProfile.UsingDpadMovekeys();

			bool vanillaAlreadyCounts = GamePadUiNavigationPolicy.VanillaCountsMenuDirections(
				Main.gameMenu,
				Main.ingameOptionsWindow,
				Main.editChest,
				Main.editSign,
				Main.playerInventory,
				talkingToNpc,
				usingDpadMoveKeys);

			bool captureModalActive = ComboPanel.IsOpen || SlotCapture.IsCapturing;

			return GamePadUiNavigationPolicy.ShouldSupplementMenuDirections(
				vanillaAlreadyCounts,
				controllerUiInputMode: true,
				uiHostOpen: true,
				captureModalActive: captureModalActive);
		}
	}
}
