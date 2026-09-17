namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Character-control Escape Inventory: one ToggleInv per physical Escape press.
	/// Alternate to SharedUi latch — LIVE showed latch stayed 0 because REARM_WORLD injects
	/// Inventory JP after BindingInputEngine, so hold-flicker caused REARM → ToggleInv spam.
	/// Does not force Main.playerInventory (open/close result of the first ToggleInv must stick).
	///
	/// LIVE 2026-09-16 AddOr Escape+Tab: Escape brief-up then re-down cleared the gate
	/// (treated as "new press") → REARM_WORLD × N / stroke. Flicker re-down must KEEP armed.
	/// </summary>
	public static class WorldEscapeInventoryOneShotPolicy
	{
		/// <summary>
		/// Consecutive Escape-up game updates required before the gate / SharedUi latch may clear.
		/// LIVE 2026-09-16: ~107ms Escape-up mid-hold cleared a 6-frame settle; use ~10 (~167ms@60fps).
		/// </summary>
		public const int DefaultRequiredEscapeUpUpdatesBeforeClear = 10;

		/// <summary>
		/// World Escape one-shot arming disabled. LIVE: settle after arm blocked rapid ESC re-taps;
		/// world REARM is already off — latch + GetAsyncKeyState physical Escape is the path.
		/// </summary>
		public static bool ShouldArmAfterToggleInv(
			bool comboPanelOpen,
			bool keyboardEscapeDown)
		{
			_ = comboPanelOpen;
			_ = keyboardEscapeDown;
			return false;
		}

		/// <summary>
		/// While armed, strip Inventory JP/Current (not playerInventory) so a second ToggleInv
		/// cannot fire until Escape key-up settle completes.
		/// </summary>
		public static bool ShouldSuppressInventoryTriggers(
			bool gateArmed,
			bool keyboardEscapeDown,
			int keyUpSettleRemaining = 0)
			=> gateArmed && (keyboardEscapeDown || keyUpSettleRemaining > 0);

		public static bool ShouldClearGate(
			bool gateArmed,
			bool keyboardEscapeDown,
			int keyUpSettleRemaining = 0)
			=> gateArmed && !keyboardEscapeDown && keyUpSettleRemaining <= 0;

		/// <summary>
		/// Escape returned while key-up settle was counting: hardware flicker / KeyState bounce,
		/// not a new physical press. Keep the one-shot armed (do not clear).
		/// </summary>
		public static bool ShouldClearGateOnEscapeRedownDuringSettle(
			bool gateArmed,
			bool keyboardEscapeDown,
			int escapeUpGameUpdates)
		{
			_ = gateArmed;
			_ = keyboardEscapeDown;
			_ = escapeUpGameUpdates;
			return false;
		}

		/// <summary>
		/// Same settle mapping as settings-leave, but with a longer default so brief Escape-up
		/// flickers cannot clear the gate mid-hold.
		/// </summary>
		public static int ResolveKeyUpSettleRemaining(
			bool gateArmed,
			bool keyboardEscapeDown,
			int escapeUpGameUpdates,
			int requiredEscapeUpUpdatesBeforeClear = DefaultRequiredEscapeUpUpdatesBeforeClear)
		{
			if (!gateArmed)
				return 0;
			if (keyboardEscapeDown)
				return 1;
			int required = requiredEscapeUpUpdatesBeforeClear > 0
				? requiredEscapeUpUpdatesBeforeClear
				: DefaultRequiredEscapeUpUpdatesBeforeClear;
			if (escapeUpGameUpdates <= required)
				return 1;
			return 0;
		}

		public static bool ShouldBlockWorldRearm(bool gateArmed)
			=> gateArmed;

		/// <summary>
		/// Pure gate step for ComboPanel poll + stress tests (no Terraria refs).
		/// </summary>
		public struct GateSimState
		{
			public bool Armed;
			public int EscapeUpUpdates;
		}

		/// <summary>
		/// Advance one game update. <paramref name="toggleInvThisFrame"/> arms when Escape is down
		/// and the panel is closed (caller already filtered ComboPanel).
		/// </summary>
		public static GateSimState StepGate(
			GateSimState state,
			bool keyboardEscapeDown,
			bool toggleInvThisFrame,
			int requiredEscapeUpUpdatesBeforeClear = DefaultRequiredEscapeUpUpdatesBeforeClear)
		{
			if (toggleInvThisFrame
				&& ShouldArmAfterToggleInv(comboPanelOpen: false, keyboardEscapeDown))
			{
				state.Armed = true;
				state.EscapeUpUpdates = 0;
			}

			if (!state.Armed)
				return state;

			if (keyboardEscapeDown)
			{
				// Flicker re-down: keep armed. Never treat as new press.
				if (ShouldClearGateOnEscapeRedownDuringSettle(
					state.Armed, keyboardEscapeDown, state.EscapeUpUpdates))
				{
					state.Armed = false;
					state.EscapeUpUpdates = 0;
					return state;
				}

				state.EscapeUpUpdates = 0;
			}
			else
			{
				state.EscapeUpUpdates++;
			}

			int settleRemaining = ResolveKeyUpSettleRemaining(
				state.Armed,
				keyboardEscapeDown,
				state.EscapeUpUpdates,
				requiredEscapeUpUpdatesBeforeClear);

			if (ShouldClearGate(state.Armed, keyboardEscapeDown, settleRemaining))
			{
				state.Armed = false;
				state.EscapeUpUpdates = 0;
			}

			return state;
		}

		/// <summary>
		/// Simulate Escape AND mirror + SharedUi latch (world REARM off, no long settle).
		/// Continuous Escape down = 1 toggle. Full key-up then down = new toggle.
		/// When <paramref name="physicalEscapeDownPerUpdate"/> is null, each frame's GetState
		/// value is also treated as physical (clean stream). Pass a separate physical stream to
		/// model LIVE FNA GetState flicker while GetAsyncKeyState stays down.
		/// </summary>
		public static int CountToggleInvForEscapeDownStream(
			bool[] escapeDownPerUpdate,
			int requiredEscapeUpUpdatesBeforeClear = DefaultRequiredEscapeUpUpdatesBeforeClear,
			bool[] physicalEscapeDownPerUpdate = null)
		{
			_ = requiredEscapeUpUpdatesBeforeClear;
			if (escapeDownPerUpdate == null || escapeDownPerUpdate.Length == 0)
				return 0;

			bool latch = false;
			bool prevHeld = false;
			int toggles = 0;

			for (int i = 0; i < escapeDownPerUpdate.Length; i++)
			{
				bool getStateDown = escapeDownPerUpdate[i];
				bool physicalDown = physicalEscapeDownPerUpdate != null
					&& i < physicalEscapeDownPerUpdate.Length
					? physicalEscapeDownPerUpdate[i]
					: getStateDown;

				bool andHeld = SharedUiBackEscapeEdgePolicy.StabilizeEscapeAndHeld(
					andDownFromPressed: getStateDown,
					andIsEscapeEquivalent: true,
					physicalEscapeDown: physicalDown);
				bool rising = andHeld && !prevHeld;

				if (InventoryEscLiveVerifyPolicy.ShouldRearmInventoryForWorldEscapeEdge(
					settingsMenusOpen: false,
					comboPanelOpen: false,
					escapeRisingEdge: rising,
					inventoryJustPressedAlive: false,
					keyStatusContainsEscape: true))
				{
					throw new System.InvalidOperationException("World REARM must remain disabled.");
				}

				bool mirror = SharedUiBackEscapeEdgePolicy.ShouldMirrorFireEscapeAndWhenVanillaOwnsKeyStatus()
					&& SharedUiBackEscapeEdgePolicy.ShouldMirrorFireAndRising(
						andRising: rising,
						escapeHoldLatchActive: latch,
						vanillaInventoryAlreadyJustPressed: false);

				if (mirror)
					toggles++;

				latch = SharedUiBackEscapeEdgePolicy.NextEscapeHoldLatch(
					previousLatch: latch,
					physicalEscapeDown: physicalDown,
					andPathInventoryEdgeThisFrame: mirror,
					keyUpSettleActive: false);

				prevHeld = andHeld;
			}

			return toggles;
		}
	}
}
