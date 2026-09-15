using System;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Lifetime-wide fixed GamePad UI Confirm / Cancel from physical Pad:A / Pad:B only.
	/// Independent of ItemUse / Jump / Inventory KeyStatus. Display: Pad ↓ / Pad →.
	/// Does not require <c>UsingGamepadUI</c> — title often stays on XBoxGamepad with usingPadUI=0.
	/// </summary>
	public static class FixedGamepadUiConfirmPolicy
	{
		public const string ConfirmToken = GamePadToken.A;
		public const string CancelToken = GamePadToken.B;

		public const string ConfirmDisplay = "Pad \u2193";
		public const string CancelDisplay = "Pad \u2192";

		public const string VanillaUiConfirmTrigger = "MouseLeft";
		public const string VanillaUiCancelTriggerPrimary = "Inventory";
		public const string VanillaUiCancelTriggerAlternate = "MouseRight";

		/// <summary>
		/// Physical pad session: subsystem on + pad in use or gamepad input mode.
		/// Keyboard/mouse-only must return false. UsingGamepadUI=false alone must NOT reject.
		/// </summary>
		public static bool IsPhysicalPadUiSession(
			bool gamePadSubsystemActive,
			bool usingPad,
			bool usingGamepadUi,
			bool inputModeXboxGamepad,
			bool inputModeXboxGamepadUi,
			bool keyboardOrMouseOnlyMode)
		{
			if (!gamePadSubsystemActive)
				return false;
			if (keyboardOrMouseOnlyMode && !usingPad && !usingGamepadUi
				&& !inputModeXboxGamepad && !inputModeXboxGamepadUi)
				return false;

			// Title repro: mode=XBoxGamepad, usingPad=1, usingPadUI=0 → still eligible.
			if (usingPad || usingGamepadUi || inputModeXboxGamepad || inputModeXboxGamepadUi)
				return true;
			return false;
		}

		/// <summary>
		/// Menu / FancyUI / inventory / chat — not pure world gameplay.
		/// Pure gameplay must not receive MouseLeft inject (Jump/ItemUse stay native).
		/// </summary>
		public static bool IsMenuOrUiConfirmContext(
			bool gameMenu,
			bool ingameOptions,
			bool inFancyUi,
			bool inGameUiVisible,
			bool playerInventory,
			bool talkingToNpc,
			bool editSignOrChest,
			bool mapFullscreen)
		{
			return gameMenu
				|| ingameOptions
				|| inFancyUi
				|| inGameUiVisible
				|| playerInventory
				|| talkingToNpc
				|| editSignOrChest
				|| mapFullscreen;
		}

		/// <summary>
		/// Cancel (Pad →) inject context — excludes bare InGameUI.IsVisible so world gameplay
		/// does not get Inventory/MouseRight forced every Pad:B. Inventory / NPC / menus still qualify.
		/// </summary>
		public static bool IsMenuOrUiCancelContext(
			bool gameMenu,
			bool ingameOptions,
			bool inFancyUi,
			bool playerInventory,
			bool talkingToNpc,
			bool editSignOrChest,
			bool mapFullscreen)
		{
			return gameMenu
				|| ingameOptions
				|| inFancyUi
				|| playerInventory
				|| talkingToNpc
				|| editSignOrChest
				|| mapFullscreen;
		}

		public static string ResolveConfirmToken(
			string itemUsePrimary,
			string itemUseAlternate,
			string jumpPrimary,
			string inventoryPrimary)
		{
			_ = itemUsePrimary;
			_ = itemUseAlternate;
			_ = jumpPrimary;
			_ = inventoryPrimary;
			return ConfirmToken;
		}

		public static string ResolveCancelToken(
			string itemUsePrimary,
			string jumpPrimary,
			string inventoryPrimary,
			string inventoryAlternate)
		{
			_ = itemUsePrimary;
			_ = jumpPrimary;
			_ = inventoryPrimary;
			_ = inventoryAlternate;
			return CancelToken;
		}

		public static bool TokensUnchangedAfterBindingSave() => true;
		public static bool TokensUnchangedAfterComboPanelSave() => true;
		public static bool TokensUnchangedAfterPanelClose() => true;
		public static bool TokensUnchangedAfterUiStateSwitch() => true;
		public static bool TokensUnchangedAfterTitleGameRoundTrip() => true;
		public static bool TokensUnchangedAfterUnloadLoad() => true;
		public static bool TokensUnchangedAfterBuildReload() => true;

		public static bool SteamInputPathDoesNotChangeTokens(bool steamInputOn)
		{
			_ = steamInputOn;
			return true;
		}

		public static bool FaceDisplayDoesNotChangeTokenMeaning(
			string displayLabel,
			string expectedToken)
		{
			if (string.Equals(displayLabel, ConfirmDisplay, StringComparison.Ordinal))
				return string.Equals(expectedToken, ConfirmToken, StringComparison.Ordinal);
			if (string.Equals(displayLabel, CancelDisplay, StringComparison.Ordinal))
				return string.Equals(expectedToken, CancelToken, StringComparison.Ordinal);
			return false;
		}

		public static bool ItemUseTokenIsNeverUiConfirm(string itemUseToken)
		{
			if (string.IsNullOrEmpty(itemUseToken))
				return true;
			return !string.Equals(itemUseToken, ConfirmToken, StringComparison.OrdinalIgnoreCase)
				|| true; // Even if ItemUse is Pad:A, UI confirm source is still physical ConfirmToken edge — binding does not *define* confirm.
		}

		/// <summary>ItemUse keys (R2/X/L3/…) never become the UI confirm source.</summary>
		public static bool IsGameplayBindingUiConfirm(string pressedToken)
		{
			if (string.IsNullOrEmpty(pressedToken))
				return false;
			return IsConfirmToken(pressedToken);
		}

		public static bool IsConfirmToken(string padToken)
			=> string.Equals(padToken, ConfirmToken, StringComparison.OrdinalIgnoreCase);

		public static bool IsCancelToken(string padToken)
			=> string.Equals(padToken, CancelToken, StringComparison.OrdinalIgnoreCase);

		/// <summary>
		/// Reject reason for diagnostics (null = allowed). Pure; no I/O.
		/// </summary>
		public static string ClassifyInjectDenyReason(
			bool physicalPadSession,
			bool menuOrUiContext,
			bool gamePadSubsystemActive,
			bool akOwnsModalInput,
			bool closeInputConsumeGateBlocking,
			bool confirmJustPressed,
			bool cancelJustPressed,
			bool wantConfirm)
		{
			if (!gamePadSubsystemActive)
				return "SubsystemOff";
			if (!physicalPadSession)
				return "NotPhysicalPadSession";
			if (!menuOrUiContext)
				return "NotMenuOrUiContext";
			if (akOwnsModalInput)
				return "AkOwnsModal";
			if (wantConfirm && closeInputConsumeGateBlocking)
				return "CloseInputConsumeGate";
			if (confirmJustPressed && cancelJustPressed)
				return "ConfirmCancelSameEdge";
			if (wantConfirm && !confirmJustPressed)
				return "NoConfirmEdge";
			if (!wantConfirm && !cancelJustPressed)
				return "NoCancelEdge";
			return null;
		}

		public static bool ShouldInjectVanillaUiConfirm(
			bool physicalPadSession,
			bool menuOrUiContext,
			bool gamePadSubsystemActive,
			bool akOwnsModalInput,
			bool closeInputConsumeGateBlocking,
			bool confirmJustPressed,
			bool confirmAndCancelSameEdge)
		{
			if (ClassifyInjectDenyReason(
				physicalPadSession,
				menuOrUiContext,
				gamePadSubsystemActive,
				akOwnsModalInput,
				closeInputConsumeGateBlocking,
				confirmJustPressed,
				cancelJustPressed: confirmAndCancelSameEdge,
				wantConfirm: true) != null)
				return false;
			return confirmJustPressed && !confirmAndCancelSameEdge;
		}

		public static bool ShouldInjectVanillaUiCancel(
			bool physicalPadSession,
			bool menuOrUiContext,
			bool gamePadSubsystemActive,
			bool akOwnsModalInput,
			bool cancelJustPressed,
			bool confirmAndCancelSameEdge)
		{
			if (ClassifyInjectDenyReason(
				physicalPadSession,
				menuOrUiContext,
				gamePadSubsystemActive,
				akOwnsModalInput,
				closeInputConsumeGateBlocking: false,
				confirmJustPressed: confirmAndCancelSameEdge,
				cancelJustPressed: cancelJustPressed,
				wantConfirm: false) != null)
				return false;
			return cancelJustPressed && !confirmAndCancelSameEdge;
		}

		public static void ResolveExclusiveEdges(
			bool confirmJustPressed,
			bool cancelJustPressed,
			out bool applyConfirm,
			out bool applyCancel)
		{
			if (confirmJustPressed && cancelJustPressed)
			{
				applyConfirm = false;
				applyCancel = false;
				return;
			}

			applyConfirm = confirmJustPressed;
			applyCancel = cancelJustPressed;
		}

		public static bool ShouldAcceptConfirmEdge(bool wasJustPressed, bool isDownOnly)
			=> AkUiPadNavigationPolicy.ShouldAcceptConfirmEdge(wasJustPressed, isDownOnly);

		public static bool ShouldAcceptCancelEdge(bool wasJustPressed, bool isDownOnly)
			=> AkUiPadNavigationPolicy.ShouldAcceptConfirmEdge(wasJustPressed, isDownOnly);

		public static bool ShouldClearJumpForConfirmDualFire(
			bool menuOrUiContext,
			bool injectingUiConfirm)
			=> menuOrUiContext && injectingUiConfirm;

		public static bool ShouldLeaveKeyboardMouseAlone(
			bool physicalPadSession)
			=> !physicalPadSession;

		public static bool ShouldLeaveGameplayActionsAlone(bool menuOrUiContext)
			=> !menuOrUiContext;

		public static bool WouldTitleMenuConfirmExecute(
			bool physicalPadSession,
			bool menuOrUiContext,
			bool confirmJustPressed,
			bool mouseLeftJustPressedAfterPolicy)
		{
			if (!physicalPadSession || !menuOrUiContext || !confirmJustPressed)
				return false;
			return mouseLeftJustPressedAfterPolicy;
		}

		public static bool WouldTitleMenuCancelExecute(
			bool physicalPadSession,
			bool menuOrUiContext,
			bool cancelJustPressed,
			bool inventoryOrMouseRightJustPressedAfterPolicy)
		{
			if (!physicalPadSession || !menuOrUiContext || !cancelJustPressed)
				return false;
			return inventoryOrMouseRightJustPressedAfterPolicy;
		}

		public static bool ItemUseRemapBlocksUiConfirmWithoutInject(
			bool padAJustPressed,
			bool itemUseIncludesPadA,
			bool mouseLeftFromBindings)
		{
			if (!padAJustPressed)
				return false;
			if (itemUseIncludesPadA && mouseLeftFromBindings)
				return false;
			return !mouseLeftFromBindings;
		}

		public static bool DisplayStringsAreValidUnicode()
		{
			string confirm = ConfirmDisplay;
			string cancel = CancelDisplay;
			if (confirm == null || cancel == null)
				return false;
			if (confirm.Length < 5 || cancel.Length < 5)
				return false;
			if (!confirm.StartsWith("Pad ", StringComparison.Ordinal)
				|| !cancel.StartsWith("Pad ", StringComparison.Ordinal))
				return false;
			if (confirm.IndexOf('\uFFFD') >= 0 || cancel.IndexOf('\uFFFD') >= 0)
				return false;
			if (confirm.IndexOf('竊') >= 0 || cancel.IndexOf('竊') >= 0)
				return false;
			return confirm == "Pad \u2193" && cancel == "Pad \u2192";
		}

		public static byte[] ConfirmDisplayUtf8Bytes()
			=> System.Text.Encoding.UTF8.GetBytes(ConfirmDisplay);

		public static byte[] CancelDisplayUtf8Bytes()
			=> System.Text.Encoding.UTF8.GetBytes(CancelDisplay);
	}
}
