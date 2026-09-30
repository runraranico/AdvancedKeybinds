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

		/// <summary>
		/// Regression table for the 3× recurrence (Inventory-under vs InGameUI-over).
		/// Fail-fast on Load if someone re-merges the two cases into one predicate again.
		/// </summary>
		internal static void RunRegressionSelfCheck()
		{
			// Inventory alone is intentionally absent from IsForegroundMenuCovering parameters.
			if (IsForegroundMenuCovering(false, false, false, false, false, inGameUiHasState: false))
				throw new InvalidOperationException("ListView REGRESSION: empty flags must not cover.");

			if (!IsForegroundMenuCovering(false, false, false, false, false, inGameUiHasState: true))
				throw new InvalidOperationException("ListView REGRESSION: InGameUI (Mod Config) must cover.");

			if (!IsForegroundMenuCovering(false, false, false, ingameOptionsWindow: true, false, false))
				throw new InvalidOperationException("ListView REGRESSION: options must cover.");

			if (!IsForegroundMenuCovering(drawingPlayerChat: true, false, false, false, false, false))
				throw new InvalidOperationException("ListView REGRESSION: chat must cover.");

			// Over AK chrome + Mod Config in front → must NOT claim (TEST 2 / 3rd recurrence).
			if (ShouldClaimFrontWindowInput(overListChrome: true, foregroundMenuCoversList: true, titleOriginLeftGestureHeld: false))
				throw new InvalidOperationException("ListView REGRESSION: must not steal wheel when covered by front UI.");

			// Over AK chrome + only Inventory behind → MUST claim (TEST 5 Baseline).
			if (!ShouldClaimFrontWindowInput(overListChrome: true, foregroundMenuCoversList: false, titleOriginLeftGestureHeld: false))
				throw new InvalidOperationException("ListView REGRESSION: must occupy when AK is front over Inventory.");

			// Outside chrome, no title-origin gesture → never claim.
			if (ShouldClaimFrontWindowInput(overListChrome: false, foregroundMenuCoversList: false, titleOriginLeftGestureHeld: false))
				throw new InvalidOperationException("ListView REGRESSION: must not claim outside chrome.");

			if (ShouldClaimFrontWindowInput(overListChrome: false, foregroundMenuCoversList: true, titleOriginLeftGestureHeld: false))
				throw new InvalidOperationException("ListView REGRESSION: must not claim outside chrome when covered.");

			// Title-origin left gesture held outside chrome → still claim (drag/pending/Close/−/× tail).
			if (!ShouldClaimFrontWindowInput(overListChrome: false, foregroundMenuCoversList: false, titleOriginLeftGestureHeld: true))
				throw new InvalidOperationException("ListView REGRESSION: title-origin left gesture must claim outside chrome.");

			// Foreground still wins over title-origin gesture.
			if (ShouldClaimFrontWindowInput(overListChrome: false, foregroundMenuCoversList: true, titleOriginLeftGestureHeld: true))
				throw new InvalidOperationException("ListView REGRESSION: foreground cover must yield even with title-origin gesture.");
		}
	}
}
