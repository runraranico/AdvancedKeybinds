namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Inventory / MenuInquiry Escape+AdditionalOr: prevent one physical Escape hold from
	/// producing two Inventory JustPressed edges (vanilla KeyStatus + AK mirror + rearm desync).
	/// Pure logic — no Terraria references.
	/// </summary>
	public static class SharedUiBackEscapeEdgePolicy
	{
		/// <summary>
		/// Hardware Escape for Inventory latch. Prefer <paramref name="keyboardGetStateEscapeDown"/>
		/// (Keyboard.GetState). <paramref name="mainKeyStateEscapeDown"/> alone is unsafe —
		/// ComboPanel.ConsumeEscape strips Escape from Main.keyState while the key is still held,
		/// which falsely dropped the hold latch and allowed a second ToggleInv edge.
		/// </summary>
		public static bool IsHardwareEscapeDown(
			bool keyboardGetStateEscapeDown,
			bool mainKeyStateEscapeDown)
			=> keyboardGetStateEscapeDown || mainKeyStateEscapeDown;

		/// <summary>
		/// Keep Escape AND "held" while physical Escape is still down even if the pressed
		/// snapshot briefly misses the token (fallback VK / scan flicker).
		/// </summary>
		public static bool StabilizeEscapeAndHeld(
			bool andDownFromPressed,
			bool andIsEscapeEquivalent,
			bool physicalEscapeDown)
		{
			if (andDownFromPressed)
				return true;
			return andIsEscapeEquivalent && physicalEscapeDown;
		}

		/// <summary>
		/// Mirror-fire Escape AND rising only when this Escape hold has not already armed Inventory.
		/// When vanilla KeyStatus already raised Inventory JustPressed this frame, do not mirror —
		/// ApplyResults OR would keep JP true while Player may already have consumed the vanilla edge
		/// (AdditionalOr Escape+Tab double ToggleInv feel).
		/// </summary>
		public static bool ShouldMirrorFireAndRising(
			bool andRising,
			bool escapeHoldLatchActive,
			bool vanillaInventoryAlreadyJustPressed = false)
			=> andRising
				&& !escapeHoldLatchActive
				&& !vanillaInventoryAlreadyJustPressed;

		/// <summary>
		/// When vanilla KeyStatus owns Escape+Tab OR, AK may mirror Escape AND once per hold
		/// (caller must still apply <see cref="ShouldMirrorFireAndRising"/> + SharedUi latch).
		/// LIVE 2026-09-16: disabling this forced REARM_WORLD, which multi-fired on Escape flicker.
		/// Prefer Escape AND mirror + latch; do not inject world REARM.
		/// Tab AdditionalOr rising stays mirrored by the caller separately.
		/// </summary>
		public static bool ShouldMirrorFireEscapeAndWhenVanillaOwnsKeyStatus()
			=> true;

		/// <summary>
		/// Game + KeyboardUI both evaluate Inventory AdditionalOr. SharedUi latch / prev / OR-prev
		/// must key by trigger id (<c>Inventory</c>), never by storage id (<c>KU:Inventory</c>),
		/// or one Escape press can rise twice (G then U).
		/// </summary>
		public static string SharedUiEdgeStateId(string storageOrTriggerId)
		{
			if (string.IsNullOrEmpty(storageOrTriggerId))
				return storageOrTriggerId;
			return ControlsGameUiSeparationPolicy.ToTriggerActionId(storageOrTriggerId);
		}

		/// <summary>
		/// Latch stays after any AND-path Inventory edge while Escape is down, and during
		/// Escape key-up settle (LIVE flicker: brief UP must not clear latch → second mirror).
		/// </summary>
		public static bool NextEscapeHoldLatch(
			bool previousLatch,
			bool physicalEscapeDown,
			bool andPathInventoryEdgeThisFrame,
			bool keyUpSettleActive = false)
		{
			if (andPathInventoryEdgeThisFrame && physicalEscapeDown)
				return true;
			if (previousLatch && physicalEscapeDown)
				return true;
			if (previousLatch && keyUpSettleActive)
				return true;
			if (!physicalEscapeDown && !keyUpSettleActive)
				return false;
			return previousLatch;
		}

		/// <summary>
		/// World / settings rearm must not inject a second Inventory JP during a latched Escape hold.
		/// </summary>
		public static bool ShouldBlockRearmForEscapeHold(bool escapeHoldLatchActive)
			=> escapeHoldLatchActive;
	}
}
