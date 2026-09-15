using System;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// GamePad HOLD slider: focus moves freely; value edits only after confirm (A).
	/// Pure state machine — no Terraria types.
	/// </summary>
	public static class HoldSliderConfirmEditPolicy
	{
		public const int StepMs = 10;
		public const int InitialRepeatDelayMs = 280;
		public const int RepeatIntervalMs = 60;

		public enum Phase
		{
			Idle = 0,
			Focused,
			Editing
		}

		public enum Action
		{
			None = 0,
			EnterEdit,
			Commit,
			CancelRestore,
			AdjustValue,
			MoveFocusHorizontal,
			ForceExit
		}

		public sealed class State
		{
			public Phase Phase;
			public int BaselineMs;
			public int CurrentMs;
			public int MinMs = 100;
			public int MaxMs = 1000;
			public long RepeatArmedAtMs = long.MinValue;
			public int LastAdjustDir;
			public int LastAdjustEpoch = int.MinValue;
			public bool ConsumedConfirmThisFrame;
			public bool ConsumedCancelThisFrame;
			/// <summary>
			/// After EnterEdit, ignore ConfirmPressed for Commit until ConfirmHeld goes false once.
			/// Prevents same physical A press (WasJustPressed true across multi Observe in one tick) from Enter+Commit.
			/// </summary>
			public bool BlockCommitUntilConfirmRelease;
			/// <summary>
			/// After Commit, ignore ConfirmPressed for EnterEdit until ConfirmHeld goes false once.
			/// Prevents same physical A press from Commit+re-Enter in one tick (stays yellow).
			/// </summary>
			public bool BlockEnterUntilConfirmRelease;

			public void Reset()
			{
				Phase = Phase.Idle;
				BaselineMs = 0;
				CurrentMs = 0;
				RepeatArmedAtMs = long.MinValue;
				LastAdjustDir = 0;
				LastAdjustEpoch = int.MinValue;
				ConsumedConfirmThisFrame = false;
				ConsumedCancelThisFrame = false;
				BlockCommitUntilConfirmRelease = false;
				BlockEnterUntilConfirmRelease = false;
			}
		}

		public readonly struct FrameInput
		{
			public readonly bool SliderFocused;
			public readonly bool HoldEditable;
			public readonly bool ConfirmPressed;
			public readonly bool ConfirmHeld;
			public readonly bool CancelPressed;
			public readonly int HorizontalDir;
			public readonly int VerticalDir;
			public readonly bool HorizontalAnalogAboveDeadzone;
			public readonly int InputEpoch;
			public readonly long NowMs;
			public readonly bool ForceExit;

			public FrameInput(
				bool sliderFocused,
				bool holdEditable,
				bool confirmPressed = false,
				bool cancelPressed = false,
				int horizontalDir = 0,
				int verticalDir = 0,
				bool horizontalAnalogAboveDeadzone = false,
				int inputEpoch = 0,
				long nowMs = 0,
				bool forceExit = false,
				bool confirmHeld = false)
			{
				SliderFocused = sliderFocused;
				HoldEditable = holdEditable;
				ConfirmPressed = confirmPressed;
				ConfirmHeld = confirmHeld;
				CancelPressed = cancelPressed;
				HorizontalDir = horizontalDir;
				VerticalDir = verticalDir;
				HorizontalAnalogAboveDeadzone = horizontalAnalogAboveDeadzone;
				InputEpoch = inputEpoch;
				NowMs = nowMs;
				ForceExit = forceExit;
			}
		}

		public readonly struct Result
		{
			public readonly Action Action;
			public readonly int NewMs;
			public readonly bool ShowFavoriteYellow;
			public readonly bool BlockFocusMove;
			public readonly bool BlockPanelCancel;

			public Result(
				Action action,
				int newMs,
				bool showFavoriteYellow,
				bool blockFocusMove,
				bool blockPanelCancel)
			{
				Action = action;
				NewMs = newMs;
				ShowFavoriteYellow = showFavoriteYellow;
				BlockFocusMove = blockFocusMove;
				BlockPanelCancel = blockPanelCancel;
			}
		}

		public static Result Observe(State state, in FrameInput input)
		{
			if (state == null)
				return default;

			state.ConsumedConfirmThisFrame = false;
			state.ConsumedCancelThisFrame = false;

			if (input.ForceExit || !input.HoldEditable)
			{
				bool wasEditing = state.Phase == Phase.Editing;
				state.Phase = Phase.Idle;
				state.RepeatArmedAtMs = long.MinValue;
				state.BlockCommitUntilConfirmRelease = false;
				state.BlockEnterUntilConfirmRelease = false;
				return new Result(
					wasEditing ? Action.ForceExit : Action.None,
					state.CurrentMs,
					showFavoriteYellow: false,
					blockFocusMove: false,
					blockPanelCancel: false);
			}

			if (!input.SliderFocused)
			{
				if (state.Phase == Phase.Editing)
				{
					state.Phase = Phase.Idle;
					state.RepeatArmedAtMs = long.MinValue;
					state.BlockCommitUntilConfirmRelease = false;
					state.BlockEnterUntilConfirmRelease = false;
					return new Result(Action.ForceExit, state.CurrentMs, false, false, false);
				}

				state.Phase = Phase.Idle;
				state.BlockCommitUntilConfirmRelease = false;
				state.BlockEnterUntilConfirmRelease = false;
				return new Result(Action.None, state.CurrentMs, false, false, false);
			}

			if (state.Phase == Phase.Idle)
				state.Phase = Phase.Focused;

			if (state.Phase == Phase.Focused)
			{
				if (state.BlockEnterUntilConfirmRelease)
				{
					if (!input.ConfirmHeld)
						state.BlockEnterUntilConfirmRelease = false;
				}

				if (!state.BlockEnterUntilConfirmRelease && input.ConfirmPressed)
				{
					state.Phase = Phase.Editing;
					state.BaselineMs = state.CurrentMs;
					state.RepeatArmedAtMs = long.MinValue;
					state.BlockCommitUntilConfirmRelease = true;
					state.BlockEnterUntilConfirmRelease = false;
					state.ConsumedConfirmThisFrame = true;
					return new Result(
						Action.EnterEdit,
						state.CurrentMs,
						showFavoriteYellow: true,
						blockFocusMove: true,
						blockPanelCancel: false);
				}

				// Left/right while focused: allow adjacent UI move (do not change value).
				if (input.HorizontalDir != 0 || input.HorizontalAnalogAboveDeadzone)
				{
					return new Result(
						Action.MoveFocusHorizontal,
						state.CurrentMs,
						showFavoriteYellow: false,
						blockFocusMove: false,
						blockPanelCancel: false);
				}

				return new Result(Action.None, state.CurrentMs, false, false, false);
			}

			// Editing — clear Enter-press gate on release before accepting Commit.
			if (state.BlockCommitUntilConfirmRelease)
			{
				if (!input.ConfirmHeld)
					state.BlockCommitUntilConfirmRelease = false;
			}

			if (!state.BlockCommitUntilConfirmRelease && input.ConfirmPressed)
			{
				state.Phase = Phase.Focused;
				state.RepeatArmedAtMs = long.MinValue;
				state.BlockCommitUntilConfirmRelease = false;
				state.BlockEnterUntilConfirmRelease = true;
				state.ConsumedConfirmThisFrame = true;
				return new Result(
					Action.Commit,
					state.CurrentMs,
					showFavoriteYellow: false,
					blockFocusMove: true,
					blockPanelCancel: false);
			}

			if (input.CancelPressed)
			{
				state.CurrentMs = state.BaselineMs;
				state.Phase = Phase.Focused;
				state.RepeatArmedAtMs = long.MinValue;
				state.BlockCommitUntilConfirmRelease = false;
				state.BlockEnterUntilConfirmRelease = false;
				state.ConsumedCancelThisFrame = true;
				return new Result(
					Action.CancelRestore,
					state.CurrentMs,
					showFavoriteYellow: false,
					blockFocusMove: true,
					blockPanelCancel: true);
			}

			// Vertical must not change value.
			int dir = input.HorizontalDir;
			if (dir == 0 && input.HorizontalAnalogAboveDeadzone)
				dir = 0; // host must already resolve deadzone into HorizontalDir

			if (dir == 0)
			{
				return new Result(
					Action.None,
					state.CurrentMs,
					showFavoriteYellow: true,
					blockFocusMove: true,
					blockPanelCancel: true);
			}

			if (!TryAllowAdjust(state, dir, input.InputEpoch, input.NowMs))
			{
				return new Result(
					Action.None,
					state.CurrentMs,
					showFavoriteYellow: true,
					blockFocusMove: true,
					blockPanelCancel: true);
			}

			int next = Clamp(state.CurrentMs + dir * StepMs, state.MinMs, state.MaxMs);
			state.CurrentMs = next;
			return new Result(
				Action.AdjustValue,
				next,
				showFavoriteYellow: true,
				blockFocusMove: true,
				blockPanelCancel: true);
		}

		public static bool TryAllowAdjust(State state, int dir, int epoch, long nowMs)
		{
			if (state == null || dir == 0)
				return false;

			// One adjust per input epoch.
			if (state.LastAdjustEpoch == epoch)
				return false;

			if (state.LastAdjustDir != dir)
			{
				state.LastAdjustDir = dir;
				state.RepeatArmedAtMs = nowMs + InitialRepeatDelayMs;
				state.LastAdjustEpoch = epoch;
				return true;
			}

			if (state.RepeatArmedAtMs == long.MinValue)
			{
				state.RepeatArmedAtMs = nowMs + InitialRepeatDelayMs;
				state.LastAdjustEpoch = epoch;
				return true;
			}

			if (nowMs < state.RepeatArmedAtMs)
				return false;

			state.RepeatArmedAtMs = nowMs + RepeatIntervalMs;
			state.LastAdjustEpoch = epoch;
			return true;
		}

		public static int Clamp(int value, int min, int max)
		{
			if (value < min)
				return min;
			if (value > max)
				return max;
			return value;
		}

		public static bool ShowFavoriteYellow(Phase phase)
			=> phase == Phase.Editing;
	}
}
