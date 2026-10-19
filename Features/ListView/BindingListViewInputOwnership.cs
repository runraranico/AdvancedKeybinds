using System;

namespace AdvancedKeybinds.Features.ListView
{
	/// <summary>
	/// Single policy for AK ALL KEY front/back mouse+wheel ownership.
	/// Separates "UI layered above AK" from "Inventory open under AK" so those two cases
	/// cannot be collapsed into one boolean (that collapse caused the same bug 3 times).
	/// </summary>
	internal static class BindingListViewInputOwnership
	{
		/// <summary>
		/// True when another menu is layered ABOVE AK ALL KEY (Mod Config / InGameUI / options / map / chat).
		/// Does NOT include <c>Main.playerInventory</c> alone — Inventory sits behind the list overlay.
		/// </summary>
		public static bool IsForegroundMenuCovering(
			bool drawingPlayerChat,
			bool editSign,
			bool editChest,
			bool ingameOptionsWindow,
			bool mapFullscreen,
			bool inGameUiHasState)
		{
			if (drawingPlayerChat || editSign || editChest)
				return true;
			if (ingameOptionsWindow)
				return true;
			if (mapFullscreen)
				return true;
			if (inGameUiHasState)
				return true;
			return false;
		}

		/// <summary>
		/// Claim mouseInterface / wheel Lock+Consume when the pointer is over list chrome
		/// OR a title-origin left gesture is still owned, AND no true foreground menu covers the list.
		/// Idle outside (no title-origin gesture) never claims — Baseline 21.
		/// </summary>
		public static bool ShouldClaimFrontWindowInput(
			bool overListChrome,
			bool foregroundMenuCoversList,
			bool titleOriginLeftGestureHeld)
			=> (overListChrome || titleOriginLeftGestureHeld) && !foregroundMenuCoversList;

	}
}
