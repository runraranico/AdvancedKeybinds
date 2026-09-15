using System;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// ComboPanel input reuse + hover policy.
	/// A: after capture, skip idle UI while a navigation/confirm token stays down.
	/// B: physical hover lock ignores Main.mouse UI snap until the OS cursor moves.
	/// </summary>
	public static class ComboPanelCaptureUiReuse
	{
		public const int PhysicalMoveThresholdPx = 1;

		/// <summary>
		/// Only keys Terraria UI reuses for navigate/confirm. IME/toggle keys (e.g. Kana) must
		/// not arm release-wait suppress — KeyToken.IsDown can stick true and freeze all clicks.
		/// GamePad digital tokens (<c>Pad:A</c>, …) also arm: vanilla/SI often maps them to UI confirm
		/// (e.g. MouseLeft) and would re-open the slot under the cursor while still held.
		/// </summary>
		private static readonly string[] UiReuseSuppressNamedTokens = {
			"Space",
			"W", "A", "S", "D",
			"Up", "Down", "Left", "Right",
			"Enter"
		};

		public static bool ShouldArmUiReuseSuppress(string token)
		{
			if (string.IsNullOrEmpty(token))
				return false;

			// Independent of Keyboard named list — Pad:* must suppress until RELEASE.
			if (GamePadToken.IsPadToken(token))
				return GamePadToken.TryCanonicalize(token, out _);

			for (int i = 0; i < UiReuseSuppressNamedTokens.Length; i++)
			{
				string named = UiReuseSuppressNamedTokens[i];
				if (string.Equals(token, named, StringComparison.OrdinalIgnoreCase))
					return true;

				// SC:/VK: captures of the same physical key (new registration path).
				if (PhysicalKeyIdentity.AreSamePhysical(token, named))
					return true;
			}

			return false;
		}

		public static bool ShouldSkipIdleUiInput(bool suppressActive, bool registeredTokenStillDown)
			=> suppressActive && registeredTokenStillDown;

		public static bool ShouldClearSuppress(bool suppressActive, bool registeredTokenStillDown)
			=> suppressActive && !registeredTokenStillDown;

		/// <summary>
		/// Hover freeze must not end just because the registered key was released — Main.mouse
		/// often remains at the keyboard UI-snap point after WASD / arrows.
		/// </summary>
		public static bool ShouldClearHoverLockOnKeyRelease()
			=> false;

		/// <summary>
		/// After Mouse1/Mouse2 bind, keep the purple-wait hover lock. Clearing it immediately
		/// exposes WASD/arrow-snapped Main.mouse and breaks the next click without OS move.
		/// </summary>
		public static bool ShouldClearHoverLockAfterMouseBind()
			=> false;

		public static bool HasPhysicalMouseMoved(
			int physicalClientX,
			int physicalClientY,
			int anchorClientX,
			int anchorClientY,
			int thresholdPx)
		{
			int dx = physicalClientX - anchorClientX;
			if (dx < 0)
				dx = -dx;
			int dy = physicalClientY - anchorClientY;
			if (dy < 0)
				dy = -dy;
			return dx > thresholdPx || dy > thresholdPx;
		}

		/// <summary>
		/// Prefer physical/locked hover only while the capture lock is active, or when Main.mouse
		/// is poisoned. Do not replace Main.mouse on every 1px divergence (breaks slot hit-tests).
		/// </summary>
		public static bool PreferPhysicalMouseOverMain(
			bool physicalHoverLockActive,
			int mainMouseX,
			int mainMouseY,
			int poisonThreshold)
		{
			if (physicalHoverLockActive)
				return true;

			return mainMouseX <= poisonThreshold || mainMouseY <= poisonThreshold;
		}

		public static bool ShouldRememberMainMouse(bool physicalHoverLockActive, bool uiReuseSuppressActive)
			=> !physicalHoverLockActive && !uiReuseSuppressActive;

		/// <summary>
		/// While the physical hover lock is active, keep the locked panel UI point until the OS
		/// cursor moves. On move, <paramref name="lockUpdatedFromPhysicalMove"/> is true — caller
		/// must clear the lock and re-resolve via Main.mouse (do not adopt Mouse.GetState/UIScale
		/// as the new lock; that space mismatches slot hit-tests).
		/// </summary>
		public static bool TryResolveHover(
			bool physicalHoverLockActive,
			int lockedUiX,
			int lockedUiY,
			int physicalClientX,
			int physicalClientY,
			int anchorClientX,
			int anchorClientY,
			int physicalUiX,
			int physicalUiY,
			int moveThresholdPx,
			out int hoverUiX,
			out int hoverUiY,
			out bool lockUpdatedFromPhysicalMove)
		{
			lockUpdatedFromPhysicalMove = false;
			_ = physicalUiX;
			_ = physicalUiY;

			if (!physicalHoverLockActive)
			{
				hoverUiX = lockedUiX;
				hoverUiY = lockedUiY;
				return false;
			}

			if (HasPhysicalMouseMoved(
				physicalClientX,
				physicalClientY,
				anchorClientX,
				anchorClientY,
				moveThresholdPx))
			{
				hoverUiX = lockedUiX;
				hoverUiY = lockedUiY;
				lockUpdatedFromPhysicalMove = true;
				return true;
			}

			hoverUiX = lockedUiX;
			hoverUiY = lockedUiY;
			return true;
		}

		/// <summary>
		/// Open-click Mouse1 contract:
		/// arm on mouse Open only; clear after HandleClicks when phys+Main.mouseLeft both UP;
		/// block mouse StartSlotCapture while active; Pad path never blocked.
		/// Product keeps open-click block separate from purple Mouse1 swallow so FinishKeyCapture
		/// cannot re-enable the opening click as StartSlotCapture.
		/// </summary>
		public static bool ShouldArmOpenClickMouse1Swallow(bool openedByGamePad)
			=> !openedByGamePad;

		public static bool ShouldClearOpenClickMouse1Swallow(
			bool swallowActive,
			bool physicalMouse1Down,
			bool mainMouseLeftDown)
			=> swallowActive && !physicalMouse1Down && !mainMouseLeftDown;

		/// <summary>
		/// Gate for StartSlotCaptureFromUi. mouseUiPath=false (Pad) is never blocked.
		/// </summary>
		public static bool ShouldBlockMouseStartSlotCapture(
			bool mouseUiPath,
			bool openClickSwallowActive)
			=> mouseUiPath && openClickSwallowActive;

		/// <summary>
		/// Purple-wait mouse priority: dismiss buttons → bind on capturing slot → outside cancel.
		/// Open-click Mouse1 must be fully released before a new Mouse1 can bind.
		/// </summary>
		public static bool ShouldBindCapturedMouse(
			bool swallowOpenClickMouse1,
			bool isMouse1Token,
			bool cursorInsideCapturingSlot,
			bool cursorOverDismissButton)
		{
			if (cursorOverDismissButton)
				return false;
			if (isMouse1Token && swallowOpenClickMouse1)
				return false;
			return cursorInsideCapturingSlot;
		}

		public static bool ShouldDismissCaptureOnMouse(
			bool swallowOpenClickMouse1,
			bool isMouse1Token,
			bool cursorInsideCapturingSlot,
			bool cursorOverDismissButton)
		{
			if (cursorOverDismissButton)
				return true;
			if (isMouse1Token && swallowOpenClickMouse1)
				return false;
			if (cursorInsideCapturingSlot)
				return false;
			return true;
		}
	}
}
