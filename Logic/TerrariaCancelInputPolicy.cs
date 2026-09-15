using System;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Terraria native back / close / cancel vs AK modal ownership.
	/// Hard rule: while ComboPanel is closed and Controls/ModConfig intervention context is false,
	/// AK must not swallow Keyboard Escape, clear Inventory (Start), or suppress Pad:B (Pad →).
	/// </summary>
	public static class TerrariaCancelInputPolicy
	{
		public const string KeyboardEscape = "KeyboardEscape";
		public const string GamePadStart = "GamePadStart";
		public const string PadCancel = "Pad:B";
		public const string TriggerInventory = "Inventory";
		public const string TriggerMouseRight = "MouseRight";
		public const string TriggerMenuInquiry = "MenuInquiry";
		public const string TriggerMenuBack = "MenuBack";

		/// <summary>
		/// True only while AK may legally interfere with parent back/cancel triggers.
		/// Bare Main.inFancyUI is intentionally excluded (sticky FancyUI must not kill world ESC).
		/// </summary>
		public static bool IsAkParentCancelInterventionContext(
			bool comboPanelOpen,
			bool manageControlsOpen,
			bool ingameOptions)
			=> comboPanelOpen || manageControlsOpen || ingameOptions;

		/// <summary>
		/// Pure world / craft / NPC / chest / title (non-AK-modal): AK must not consume cancel.
		/// </summary>
		public static bool MustPassThroughNativeCancel(bool comboPanelOpen, bool keyInfoNameEditing)
			=> !comboPanelOpen && !keyInfoNameEditing;

		/// <summary>
		/// AK may consume a cancel/back edge only while ComboPanel (or rename edit) owns the UI.
		/// </summary>
		public static bool AkMayConsumeCancelInput(bool comboPanelOpen, bool keyInfoNameEditing)
			=> comboPanelOpen || keyInfoNameEditing;

		/// <summary>
		/// SlotCapture alone with panel closed must NOT count as modal ownership.
		/// </summary>
		public static bool AkOwnsModalForFixedPadCancel(bool comboPanelOpen, bool slotCaptureIsCapturing)
		{
			_ = slotCaptureIsCapturing;
			return comboPanelOpen;
		}

		/// <summary>
		/// Registration-wait swallow only while ComboPanel is actually open.
		/// Closed panel + leftover wait flags must never arm Escape swallow in world.
		/// </summary>
		public static bool ShouldTreatRegistrationWaitAsEscapeSwallow(
			bool panelOpen,
			bool registrationWaitFlags,
			bool keyboardEscapeDown)
		{
			_ = keyboardEscapeDown;
			return panelOpen && registrationWaitFlags;
		}

		/// <summary>
		/// Outside ComboPanel + Controls/options: always clear leftover gates / capture / grace.
		/// </summary>
		public static bool ShouldHardClearCancelResidueOutsideIntervention(
			bool panelOpen,
			bool interventionContext)
			=> !panelOpen && !interventionContext;

		/// <summary>
		/// Force-clear capture / pad-back residue when panel closed outside Controls/options.
		/// Outside intervention context always true so BeginInputTick hard-clears every tick.
		/// </summary>
		public static bool ShouldForceClearModalResidueWhenPanelClosed(
			bool panelOpen,
			bool slotCaptureIsCapturing,
			bool awaitingSlotKey,
			bool capturingSlotActive,
			bool padBackGate,
			bool closeConsumeBackGate,
			bool controlsOrFancyUiContext = false)
		{
			if (panelOpen)
				return false;

			if (ShouldHardClearCancelResidueOutsideIntervention(panelOpen, controlsOrFancyUiContext))
				return true;

			return slotCaptureIsCapturing || awaitingSlotKey || capturingSlotActive;
		}

		/// <summary>
		/// Pad:B Inventory/MouseRight suppress only on ComboPanel / ManageControls / options —
		/// never on bare FancyUI, Guide craft, world inventory, or title.
		/// </summary>
		public static bool ShouldSuppressPadCancelTriggersForParent(
			bool comboPanelOpen,
			bool manageControlsOpen,
			bool inFancyUi,
			bool ingameOptions)
		{
			_ = inFancyUi;
			return IsAkParentCancelInterventionContext(comboPanelOpen, manageControlsOpen, ingameOptions);
		}

		/// <summary>
		/// When AK modal is closed, native ESC / Start / Pad → must pass (gates ignored in world).
		/// </summary>
		public static bool ShouldAllowTerrariaNativeCancel(
			bool comboPanelOpen,
			bool keyInfoNameEditing,
			bool keyboardEscapeDownForSamePressGate,
			bool escapeKeyUpGate,
			bool escapeGrace)
		{
			if (MustPassThroughNativeCancel(comboPanelOpen, keyInfoNameEditing))
				return true;

			if (AkMayConsumeCancelInput(comboPanelOpen, keyInfoNameEditing))
				return false;

			_ = keyboardEscapeDownForSamePressGate;
			_ = escapeKeyUpGate;
			_ = escapeGrace;
			return true;
		}

		/// <summary>
		/// After ComboPanel closes, cancel suppress must not survive into the next idle frame in world.
		/// </summary>
		public static bool CancelSuppressMustBeClearAfterPanelClosedNextFrame(
			bool panelOpen,
			bool padBackGate,
			bool closeConsumeGate,
			bool escapeKeyUpGate,
			bool escapeGrace,
			bool interventionContext)
		{
			if (panelOpen || interventionContext)
				return true; // not asserting world-clear here
			return !padBackGate && !closeConsumeGate && !escapeKeyUpGate && !escapeGrace;
		}

		public static bool GuideCraftCancelRequiresInventoryOrMouseRight()
			=> true;

		public static bool PadCancelDisplayIsPadRightArrow()
			=> string.Equals(FixedGamepadUiConfirmPolicy.CancelDisplay, "Pad \u2192", StringComparison.Ordinal);
	}
}
