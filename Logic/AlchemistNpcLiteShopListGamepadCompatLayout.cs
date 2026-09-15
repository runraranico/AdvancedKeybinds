using System;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Pure navigation / tentative-selection policy for AlchemistNPCLite ShopChangeUI* lists.
	/// AK owns only the tentative index until confirm. The adapter temporarily shows that index by making
	/// only its shop-name TextColor Yellow; official Lime / White is restored through the mod's
	/// Shops / ReCheckColor path.
	/// Confirm must go through the row's left checkbox button, which is the only element the mod
	/// registers a click handler on. Index space is 1-based. No Terraria / XNA / NPC identity types.
	/// </summary>
	public static class AlchemistNpcLiteShopListGamepadCompatLayout
	{
		public const int DefaultMinIndex = 1;
		/// <summary>Matches ComboPanelGamePadNavigation.InitialRepeatDelayMs.</summary>
		public const int InitialRepeatDelayMs = 350;
		/// <summary>Matches ComboPanelGamePadNavigation.RepeatIntervalMs.</summary>
		public const int RepeatIntervalMs = 120;

		public enum FrameGate
		{
			Process = 0,
			SkipNotApplied,
			SkipDisabled,
			SkipListClosed,
			SkipNotTalking,
			SkipPanelNotOwnedByTalkNpc,
			SkipNotGamepad,
			SkipComboPanel,
			SkipChatOrModal,
			SkipNoItems
		}

		public enum MoveIntent
		{
			None = 0,
			Up,
			Down
		}

		/// <summary>What the caller must do to the real UI this frame.</summary>
		public enum ListAction
		{
			None = 0,
			Move,
			Confirm,
			Cancel
		}

		public enum HighlightSource
		{
			None = 0,
			MouseHover,
			GamepadTentative
		}

		/// <summary>
		/// The only element AlchemistNPCLite makes clickable per row is the left checkbox
		/// (<c>UIImageButton</c>). The shop-name <c>UIText</c> has no click handler, so AK must never
		/// treat it as a confirm target.
		/// </summary>
		public enum ConfirmClickTarget
		{
			None = 0,
			RowCheckboxButton
		}

		/// <summary>Rising edges plus held levels; timestamps are caller-supplied for pure tests.</summary>
		public readonly struct ListInput
		{
			public readonly bool DpadUpJustPressed;
			public readonly bool DpadDownJustPressed;
			public readonly bool DpadUpHeld;
			public readonly bool DpadDownHeld;
			public readonly bool ConfirmJustPressed;
			public readonly bool ConfirmHeld;
			public readonly bool CancelJustPressed;
			public readonly bool CancelHeld;
			public readonly long NowMs;

			public ListInput(
				bool dpadUpJustPressed,
				bool dpadDownJustPressed,
				bool confirmJustPressed,
				bool confirmHeld,
				bool cancelJustPressed,
				bool dpadUpHeld = false,
				bool dpadDownHeld = false,
				bool cancelHeld = false,
				long nowMs = 0)
			{
				DpadUpJustPressed = dpadUpJustPressed;
				DpadDownJustPressed = dpadDownJustPressed;
				DpadUpHeld = dpadUpHeld;
				DpadDownHeld = dpadDownHeld;
				ConfirmJustPressed = confirmJustPressed;
				ConfirmHeld = confirmHeld;
				CancelJustPressed = cancelJustPressed;
				CancelHeld = cancelHeld;
				NowMs = nowMs;
			}
		}

		public readonly struct StepResult
		{
			public readonly ListAction Action;
			/// <summary>1-based tentative index the action applies to.</summary>
			public readonly int Index;
			/// <summary>True when AK owns the frame's navigation input and must stop it leaking.</summary>
			public readonly bool SuppressNavigation;

			public StepResult(ListAction action, int index, bool suppressNavigation)
			{
				Action = action;
				Index = index;
				SuppressNavigation = suppressNavigation;
			}
		}

		/// <summary>The single row AK focuses this frame, and where that choice came from.</summary>
		public readonly struct HighlightResult
		{
			public readonly int Index;
			public readonly HighlightSource Source;

			public HighlightResult(int index, HighlightSource source)
			{
				Index = index;
				Source = source;
			}
		}

		/// <summary>Which element the confirm press must click, and the row it belongs to.</summary>
		public readonly struct ConfirmClick
		{
			public readonly ConfirmClickTarget Target;
			/// <summary>1-based ordinal, matching the mod's PlayButtonClickedN numbering.</summary>
			public readonly int Ordinal;
			/// <summary>0-based row index into the collected checkbox buttons.</summary>
			public readonly int RowIndex;

			public ConfirmClick(ConfirmClickTarget target, int ordinal, int rowIndex)
			{
				Target = target;
				Ordinal = ordinal;
				RowIndex = rowIndex;
			}
		}

		public sealed class SessionState
		{
			public bool Applied;
			public bool Disabled;
			public bool LoggedDisableOnce;
			public string LastDisableReason;

			/// <summary>Active panel slot index into the discovered panel table, or -1 when none open.</summary>
			public int ActivePanelSlot = -1;

			/// <summary>True while AK is driving an open, owned shop list with the gamepad.</summary>
			public bool Engaged;
			/// <summary>1-based tentative cursor. Never written to the mod until confirm.</summary>
			public int TentativeIndex;
			/// <summary>1-based row whose checkbox the mouse is over, or 0 when none.</summary>
			public int MouseHoverIndex;
			public int ItemCount;
			/// <summary>The confirm press that opened the list must be released before it can confirm a row.</summary>
			public bool AwaitingConfirmRelease;
			/// <summary>UILink point focused when the list opened; -1 when unknown.</summary>
			public int ReturnPointId = -1;
			/// <summary>
			/// After Pad cancel closes the list, parent NPC-chat cancellation stays blocked until that
			/// physical cancel is released. This survives <see cref="Disengage"/>.
			/// </summary>
			public bool CancelReleaseGate;

			/// <summary>
			/// After Pad confirm closes the list, the parent chat UI stays blocked until that physical
			/// confirm is released, otherwise the very same press also clicks the chat shop button and the
			/// buy screen opens instantly. Survives <see cref="Disengage"/>, like the cancel gate.
			/// </summary>
			public bool ConfirmReleaseGate;
			/// <summary>UILink point to focus once the confirm press is physically released; -1 when none.</summary>
			public int PendingFocusPoint = -1;
			/// <summary>talkNPC index the confirm gate belongs to, so a changed conversation drops it.</summary>
			public int ConfirmGateTalkNpc = -1;

			/// <summary>D-Pad hold-repeat latch: 0 none, +1 up, -1 down.</summary>
			public int DpadRepeatLatch;
			public bool DpadRepeatArmed;
			public long DpadNextRepeatMs;

			/// <summary>Rising-edge latch for Architect ReCheckColor-on-open correction.</summary>
			public bool ArchitectPanelWasVisible;
			public bool ArchitectRecheckDoneThisOpen;

			public void ResetForReload()
			{
				Applied = false;
				Disabled = false;
				LoggedDisableOnce = false;
				LastDisableReason = null;
				ActivePanelSlot = -1;
				CancelReleaseGate = false;
				ConfirmReleaseGate = false;
				PendingFocusPoint = -1;
				ConfirmGateTalkNpc = -1;
				MouseHoverIndex = 0;
				ArchitectPanelWasVisible = false;
				ArchitectRecheckDoneThisOpen = false;
				ClearDpadRepeat();
				Disengage();
			}

			public void Disengage()
			{
				Engaged = false;
				TentativeIndex = 0;
				ItemCount = 0;
				AwaitingConfirmRelease = false;
				ReturnPointId = -1;
				ClearDpadRepeat();
			}

			public void ClearDpadRepeat()
			{
				DpadRepeatLatch = 0;
				DpadRepeatArmed = false;
				DpadNextRepeatMs = 0;
			}
		}

		/// <summary>
		/// PlayerInput.UsingGamepad can temporarily be false while a native-cursor mod owns mouse mode, so a
		/// relevant list-control input also proves controller intent. AK must be able to read the pad
		/// itself, otherwise it would take the list over without being able to drive it.
		/// </summary>
		public static bool IsControllerInputMode(
			bool playerInputUsingGamepad,
			bool controllerConnected,
			bool relevantListInputActive)
			=> controllerConnected && (playerInputUsingGamepad || relevantListInputActive);

		/// <summary>Arms parent-cancel suppression until cancel is physically released.</summary>
		public static void ArmCancelReleaseGate(SessionState state)
		{
			if (state != null)
				state.CancelReleaseGate = true;
		}

		/// <summary>
		/// Returns whether parent cancel must be suppressed this frame. The first released frame clears the gate.
		/// </summary>
		public static bool PollCancelReleaseGate(SessionState state, bool cancelHeld)
		{
			if (state == null || !state.CancelReleaseGate)
				return false;
			if (cancelHeld)
				return true;
			state.CancelReleaseGate = false;
			return false;
		}

		/// <summary>
		/// Arms parent-confirm suppression until confirm is physically released, and parks the focus move
		/// that must not happen while the press is still down.
		/// </summary>
		public static void ArmConfirmReleaseGate(SessionState state, int pendingFocusPoint, int talkNpc)
		{
			if (state == null)
				return;
			state.ConfirmReleaseGate = true;
			state.PendingFocusPoint = pendingFocusPoint;
			state.ConfirmGateTalkNpc = talkNpc;
		}

		/// <summary>
		/// Returns whether the parent confirm must be suppressed this frame. Release is decided by the
		/// physical button only; the first released frame clears the gate.
		/// </summary>
		public static bool PollConfirmReleaseGate(SessionState state, bool confirmHeld)
		{
			if (state == null || !state.ConfirmReleaseGate)
				return false;
			if (confirmHeld)
				return true;
			state.ConfirmReleaseGate = false;
			return false;
		}

		/// <summary>
		/// Drops the gate without moving focus. Used when the conversation closed, the NPC changed or the
		/// game left play, because the parked point no longer describes the current screen.
		/// </summary>
		public static void AbandonConfirmReleaseGate(SessionState state)
		{
			if (state == null)
				return;
			state.ConfirmReleaseGate = false;
			state.PendingFocusPoint = -1;
			state.ConfirmGateTalkNpc = -1;
		}

		/// <summary>True when the parked focus move belongs to a screen that no longer exists.</summary>
		public static bool ConfirmGateContextLost(SessionState state, bool inPlay, bool talking, int talkNpc)
		{
			if (state == null || !state.ConfirmReleaseGate)
				return false;
			return !inPlay || !talking || talkNpc != state.ConfirmGateTalkNpc;
		}

		/// <summary>Consumes the parked focus point, returning -1 when there is nothing to move to.</summary>
		public static int TakePendingFocusPoint(SessionState state)
		{
			if (state == null)
				return -1;
			int point = state.PendingFocusPoint;
			state.PendingFocusPoint = -1;
			state.ConfirmGateTalkNpc = -1;
			return point;
		}

		/// <summary>Clamps a raw Shops value into the visible 1-based list range.</summary>
		public static int ClampShopIndex(int shopsValue, int itemCount)
			=> Clamp(shopsValue, DefaultMinIndex, Math.Max(DefaultMinIndex, itemCount));

		/// <summary>
		/// Resolves the single row AK paints as its tentative selection. Checkbox hover wins over the pad
		/// cursor. The mod's confirmed shop is never consulted here.
		/// </summary>
		public static HighlightResult ResolveHighlight(
			int mouseHoverIndex,
			bool gamepadEngaged,
			int tentativeIndex,
			int itemCount)
		{
			if (itemCount <= 0)
				return new HighlightResult(0, HighlightSource.None);

			if (mouseHoverIndex >= DefaultMinIndex && mouseHoverIndex <= itemCount)
				return new HighlightResult(mouseHoverIndex, HighlightSource.MouseHover);

			if (gamepadEngaged
			    && tentativeIndex >= DefaultMinIndex
			    && tentativeIndex <= itemCount)
				return new HighlightResult(tentativeIndex, HighlightSource.GamepadTentative);

			return new HighlightResult(0, HighlightSource.None);
		}

		/// <summary>Exactly one valid row carries the AK tentative highlight.</summary>
		public static bool IsTentativeRow(int rowIndex, int highlightIndex, int itemCount)
			=> rowIndex >= DefaultMinIndex
			   && rowIndex <= itemCount
			   && highlightIndex >= DefaultMinIndex
			   && highlightIndex <= itemCount
			   && rowIndex == highlightIndex;

		/// <summary>
		/// Confirmed shop is independent of the tentative highlight. Moving the highlight never changes it.
		/// </summary>
		public static bool ConfirmedShopUnchangedByHighlightMove(int confirmedBefore, int confirmedAfter)
			=> confirmedBefore == confirmedAfter;

		/// <summary>
		/// Rising edge of Architect ShopChangeUIA visibility → call official ReCheckColor once.
		/// </summary>
		public static bool ShouldCallArchitectRecheckOnOpen(
			SessionState state,
			bool architectPanelVisibleNow)
		{
			if (state == null)
				return false;

			bool was = state.ArchitectPanelWasVisible;
			state.ArchitectPanelWasVisible = architectPanelVisibleNow;

			if (!architectPanelVisibleNow)
			{
				state.ArchitectRecheckDoneThisOpen = false;
				return false;
			}

			if (was)
				return false;
			if (state.ArchitectRecheckDoneThisOpen)
				return false;

			state.ArchitectRecheckDoneThisOpen = true;
			return true;
		}

		/// <summary>
		/// Starts a tentative-selection session on a freshly opened, owned list.
		/// The opening confirm press is latched so it cannot also commit a row.
		/// </summary>
		public static void Engage(SessionState state, int currentShopIndex, int itemCount, int returnPointId)
		{
			if (state == null || itemCount <= 0)
				return;

			state.Engaged = true;
			state.ItemCount = itemCount;
			state.TentativeIndex = ClampShopIndex(currentShopIndex, itemCount);
			state.AwaitingConfirmRelease = true;
			state.ReturnPointId = returnPointId;
			state.ClearDpadRepeat();
		}

		/// <summary>D-Pad only. Simultaneous up+down cancels out. Shoulder buttons are never consulted.</summary>
		public static MoveIntent ResolveMoveIntent(bool dpadUpJustPressed, bool dpadDownJustPressed)
		{
			if (dpadUpJustPressed && dpadDownJustPressed)
				return MoveIntent.None;
			if (dpadUpJustPressed)
				return MoveIntent.Up;
			if (dpadDownJustPressed)
				return MoveIntent.Down;
			return MoveIntent.None;
		}

		/// <summary>
		/// Edge first, then hold-repeat after InitialRepeatDelayMs / RepeatIntervalMs
		/// (same cadence as ComboPanelGamePadNavigation).
		/// </summary>
		public static MoveIntent ResolveMoveIntentWithRepeat(SessionState state, ListInput input)
		{
			MoveIntent edge = ResolveMoveIntent(input.DpadUpJustPressed, input.DpadDownJustPressed);
			if (edge != MoveIntent.None)
			{
				if (state != null)
				{
					state.DpadRepeatLatch = edge == MoveIntent.Up ? 1 : -1;
					state.DpadRepeatArmed = true;
					state.DpadNextRepeatMs = input.NowMs + InitialRepeatDelayMs;
				}
				return edge;
			}

			if (state == null)
				return MoveIntent.None;

			bool up = input.DpadUpHeld && !input.DpadDownHeld;
			bool down = input.DpadDownHeld && !input.DpadUpHeld;
			if (!up && !down)
			{
				state.ClearDpadRepeat();
				return MoveIntent.None;
			}

			int latch = up ? 1 : -1;
			if (state.DpadRepeatLatch != latch)
			{
				state.DpadRepeatLatch = latch;
				state.DpadRepeatArmed = true;
				state.DpadNextRepeatMs = input.NowMs + InitialRepeatDelayMs;
				return MoveIntent.None;
			}

			if (!state.DpadRepeatArmed || input.NowMs < state.DpadNextRepeatMs)
				return MoveIntent.None;

			state.DpadNextRepeatMs = input.NowMs + RepeatIntervalMs;
			return latch > 0 ? MoveIntent.Up : MoveIntent.Down;
		}

		/// <summary>Clamps to [minIndex, maxIndex]. No wrap-around (matches the mod's mouse behaviour).</summary>
		public static int ApplyMove(int currentIndex, MoveIntent intent, int minIndex, int maxIndex)
		{
			if (maxIndex < minIndex)
				return currentIndex;

			currentIndex = Clamp(currentIndex, minIndex, maxIndex);

			return intent switch {
				MoveIntent.Up => currentIndex <= minIndex ? minIndex : currentIndex - 1,
				MoveIntent.Down => currentIndex >= maxIndex ? maxIndex : currentIndex + 1,
				_ => currentIndex
			};
		}

		/// <summary>
		/// Advances the tentative-selection state machine for one frame.
		/// Cancel wins over confirm; confirm is blocked until the opening press is released.
		/// Shoulder / L1 / R1 are never consulted.
		/// </summary>
		public static StepResult Step(SessionState state, ListInput input)
		{
			if (state == null || !state.Engaged || state.ItemCount <= 0)
				return new StepResult(ListAction.None, 0, false);

			if (state.AwaitingConfirmRelease && !input.ConfirmHeld)
				state.AwaitingConfirmRelease = false;

			if (input.CancelJustPressed)
				return new StepResult(ListAction.Cancel, state.TentativeIndex, true);

			if (input.ConfirmJustPressed && !state.AwaitingConfirmRelease)
			{
				state.AwaitingConfirmRelease = true;
				return new StepResult(ListAction.Confirm, state.TentativeIndex, true);
			}

			MoveIntent intent = ResolveMoveIntentWithRepeat(state, input);
			if (intent != MoveIntent.None)
			{
				state.TentativeIndex = ApplyMove(state.TentativeIndex, intent, DefaultMinIndex, state.ItemCount);
				return new StepResult(ListAction.Move, state.TentativeIndex, true);
			}

			// Engaged but idle: still own the up/down axis so it cannot reach chat navigation.
			return new StepResult(ListAction.None, state.TentativeIndex, true);
		}

		/// <summary>
		/// Maps the highlighted row to the row checkbox that must receive the click.
		/// Without a resolved checkbox for every row there is no safe confirm, so AK does nothing.
		/// </summary>
		public static ConfirmClick ResolveConfirmClick(
			HighlightResult highlight,
			int itemCount,
			bool hasCheckboxButtons)
		{
			if (!hasCheckboxButtons)
				return new ConfirmClick(ConfirmClickTarget.None, 0, -1);

			int ordinal = ClickHandlerOrdinal(highlight.Index, DefaultMinIndex, itemCount);
			if (ordinal <= 0)
				return new ConfirmClick(ConfirmClickTarget.None, 0, -1);

			return new ConfirmClick(ConfirmClickTarget.RowCheckboxButton, ordinal, ordinal - DefaultMinIndex);
		}

		/// <summary>
		/// L1/R1 are only taken away from the parent UI while AK is actually driving a visible, owned
		/// shop list with the pad. Outside that state Terraria keeps its normal shoulder behaviour.
		/// </summary>
		public static bool ShouldSuppressShoulderInput(
			bool applied,
			bool disabled,
			bool listVisible,
			bool engaged,
			bool controllerInputMode,
			bool shoulderDown)
			=> applied
			   && !disabled
			   && listVisible
			   && engaged
			   && controllerInputMode
			   && shoulderDown;

		public static FrameGate DecideFrame(
			bool applied,
			bool disabled,
			bool listVisible,
			bool talkingToNpc,
			bool panelOwnedByTalkNpc,
			bool usingGamepad,
			bool comboPanelOpen,
			bool chatOrModalBlocks,
			int itemCount)
		{
			if (!applied)
				return FrameGate.SkipNotApplied;
			if (disabled)
				return FrameGate.SkipDisabled;
			if (!listVisible)
				return FrameGate.SkipListClosed;
			if (!talkingToNpc)
				return FrameGate.SkipNotTalking;
			if (!panelOwnedByTalkNpc)
				return FrameGate.SkipPanelNotOwnedByTalkNpc;
			if (!usingGamepad)
				return FrameGate.SkipNotGamepad;
			if (comboPanelOpen)
				return FrameGate.SkipComboPanel;
			if (chatOrModalBlocks)
				return FrameGate.SkipChatOrModal;
			if (itemCount <= 0)
				return FrameGate.SkipNoItems;
			return FrameGate.Process;
		}

		/// <summary>
		/// Picks the first currently-visible panel among <paramref name="visibleFlags"/>, reading only
		/// <paramref name="count"/> entries so callers can reuse a fixed buffer.
		/// Returns -1 when none are open. Does not use NPC identity.
		/// </summary>
		public static int SelectActivePanelSlot(bool[] visibleFlags, int count)
		{
			if (visibleFlags == null)
				return -1;
			if (count > visibleFlags.Length)
				count = visibleFlags.Length;

			for (int i = 0; i < count; i++)
			{
				if (visibleFlags[i])
					return i;
			}

			return -1;
		}

		public static int SelectActivePanelSlot(bool[] visibleFlags)
			=> SelectActivePanelSlot(visibleFlags, visibleFlags?.Length ?? 0);

		public static bool ShouldLogDisable(SessionState state)
		{
			if (state == null || state.LoggedDisableOnce)
				return false;
			state.LoggedDisableOnce = true;
			return true;
		}

		/// <summary>PlayButtonClickedN is 1-based; returns 0 if out of range.</summary>
		public static int ClickHandlerOrdinal(int shopsIndex, int minIndex, int maxIndex)
		{
			if (shopsIndex < minIndex || shopsIndex > maxIndex)
				return 0;
			return shopsIndex;
		}

		/// <summary>L1/R1 must never drive this list. Pure guard for regression tests.</summary>
		public static bool ShoulderButtonsDriveListNavigation()
			=> false;

		private static int Clamp(int value, int min, int max)
		{
			if (max < min)
				return min;
			if (value < min)
				return min;
			return value > max ? max : value;
		}
	}
}
