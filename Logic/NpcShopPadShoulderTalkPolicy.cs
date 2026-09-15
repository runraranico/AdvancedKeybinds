namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Pure policy: while an NPC buy-shop is open, Pad L1 (LeftShoulder) must not start a talk
	/// with a different nearby NPC via <c>Main.HoverOverNPCs</c> (XBoxGamepadUI maps L1→MouseRight).
	/// Does not clear Main.mouseRight and does not close the current shop / talkNPC.
	/// </summary>
	public static class NpcShopPadShoulderTalkPolicy
	{
		/// <summary>
		/// True when HoverOverNPCs must be skipped so L1 cannot call SetTalkNPC on another NPC
		/// (vanilla also clears npcShop when switching). Physical mouse RMB is never blocked here.
		/// </summary>
		public static bool ShouldSkipHoverOverNpcs(
			bool npcShopOpen,
			bool talkNpcActive,
			bool leftShoulderDown,
			bool physicalMouseRightDown)
		{
			_ = physicalMouseRightDown; // documented distinction: physical RMB alone never sets leftShoulderDown
			if (!npcShopOpen)
				return false;
			if (!talkNpcActive)
				return false;
			// FNA LeftShoulder only — physical mouse RMB does not set this bit.
			return leftShoulderDown;
		}

		/// <summary>Main.npcShop &gt; 0 means the buy screen (item grid), not the shop-change list.</summary>
		public static bool IsNpcBuyShopOpen(int npcShopIndex)
			=> npcShopIndex > 0;

		public static bool IsTalkNpcActive(int talkNpcIndex)
			=> talkNpcIndex >= 0;

		/// <summary>
		/// Old gap: shop-list shoulder suppress does not cover the buy screen, so L1 still reaches
		/// HoverOverNPCs as MouseRight and can change talkNPC.
		/// </summary>
		public static bool OldBuyShopLeftUnprotectedByShopListSuppress(
			bool shopChangeListVisible,
			bool npcBuyShopOpen)
			=> !shopChangeListVisible && npcBuyShopOpen;
	}
}
