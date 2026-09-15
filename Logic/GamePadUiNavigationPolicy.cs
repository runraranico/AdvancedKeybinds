namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Rules for feeding the D-Pad into Terraria's own UILinkPoint navigation.
	/// </summary>
	/// <remarks>
	/// Vanilla <c>TriggersSet.GetNavigatorDirections()</c> builds the navigator vector from the
	/// movement triggers (left thumbstick in the gamepad profiles) and only ORs in
	/// MenuUp/MenuDown/MenuLeft/MenuRight (the D-Pad in the gamepad profiles) when one of a fixed
	/// set of screens is open — title menu, the in-game options window, sign/chest editing, or the
	/// inventory with D-Pad move keys. In-world FancyUI screens (Controls, Achievements, Mod
	/// Config) match none of those, so the D-Pad is discarded there and only the stick navigates.
	///
	/// AK reproduces the same formula rather than adding a second navigation system, so a
	/// simultaneous stick + D-Pad press is a boolean OR and still moves exactly one step.
	/// </remarks>
	public static class GamePadUiNavigationPolicy
	{
		/// <summary>Mirrors the vanilla gate. When true, AK must not interfere at all.</summary>
		public static bool VanillaCountsMenuDirections(
			bool gameMenu,
			bool ingameOptionsWindow,
			bool editChest,
			bool editSign,
			bool playerInventory,
			bool talkingToNpc,
			bool usingDpadMoveKeys)
		{
			if (gameMenu || ingameOptionsWindow || editChest || editSign)
				return true;

			return (playerInventory || talkingToNpc) && usingDpadMoveKeys;
		}

		/// <summary>
		/// Only in-world UI hosts, only while the controller drives the UI, and never while a
		/// key-capture modal owns the input.
		/// </summary>
		public static bool ShouldSupplementMenuDirections(
			bool vanillaCountsMenuDirections,
			bool controllerUiInputMode,
			bool uiHostOpen,
			bool captureModalActive)
		{
			if (vanillaCountsMenuDirections)
				return false;
			if (!controllerUiInputMode)
				return false;
			if (!uiHostOpen)
				return false;
			if (captureModalActive)
				return false;
			return true;
		}

		/// <summary>
		/// Same shape as the vanilla computation. <paramref name="includeMenuDirections"/> false
		/// reproduces the vanilla result exactly.
		/// </summary>
		public static void ResolveNavigatorDirections(
			bool up,
			bool down,
			bool left,
			bool right,
			bool menuUp,
			bool menuDown,
			bool menuLeft,
			bool menuRight,
			bool includeMenuDirections,
			out int x,
			out int y)
		{
			bool resolvedUp = up || (includeMenuDirections && menuUp);
			bool resolvedDown = down || (includeMenuDirections && menuDown);
			bool resolvedLeft = left || (includeMenuDirections && menuLeft);
			bool resolvedRight = right || (includeMenuDirections && menuRight);

			x = (resolvedRight ? 1 : 0) - (resolvedLeft ? 1 : 0);
			y = (resolvedDown ? 1 : 0) - (resolvedUp ? 1 : 0);
		}

		/// <summary>
		/// Feeds left-stick digital directions into the same movement + Menu* flags the D-Pad uses
		/// on FancyUI Controls, so stick and D-Pad share one navigator path.
		/// </summary>
		public static void MergeLeftStickDigitalIntoNavigatorFlags(
			bool stickUp,
			bool stickDown,
			bool stickLeft,
			bool stickRight,
			bool up,
			bool down,
			bool left,
			bool right,
			bool menuUp,
			bool menuDown,
			bool menuLeft,
			bool menuRight,
			out bool outUp,
			out bool outDown,
			out bool outLeft,
			out bool outRight,
			out bool outMenuUp,
			out bool outMenuDown,
			out bool outMenuLeft,
			out bool outMenuRight)
		{
			outUp = up || stickUp;
			outDown = down || stickDown;
			outLeft = left || stickLeft;
			outRight = right || stickRight;
			outMenuUp = menuUp || stickUp;
			outMenuDown = menuDown || stickDown;
			outMenuLeft = menuLeft || stickLeft;
			outMenuRight = menuRight || stickRight;
		}
	}
}
