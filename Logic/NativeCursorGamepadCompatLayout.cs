using System;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Pure layout for Native Cursor GamePad cursor restore (Cursor 13 / 15 / 17).
	/// Mirrors Terraria.Main.DrawCursor gamepad branch after Swap — no Terraria / XNA types.
	/// Pointer position must be supplied from PlayerInput.smartSelectPointer.GetPointerPosition()
	/// (never approximate size / DistUniform / zoom in AK).
	/// </summary>
	public static class NativeCursorGamepadCompatLayout
	{
		public const int CursorSmart = 13;
		public const int CursorDefault = 15;
		public const int CursorSmartAlt = 17;

		/// <summary>When Cursor 17 is also drawn, Cursor 13 multiplies alpha by this.</summary>
		public const float SeparatedSmartAlphaScale = 0.3f;

		public struct DrawItem
		{
			public int CursorIndex;
			public float PosX;
			public float PosY;
			public float Rotation;
			/// <summary>Final color multiplier on White (0..1+), includes GamepadCursorAlpha and separation scale.</summary>
			public float ColorMul;
			/// <summary>True → source = Frame(2,1,0) of Cursor 13.</summary>
			public bool UseSmartFrame;
		}

		/// <summary>Vanilla: (π/2) * GlobalTimeWrappedHourly.</summary>
		public static float Cursor17Rotation(float globalTimeWrappedHourly)
			=> (float)(Math.PI / 2.0) * globalTimeWrappedHourly;

		/// <summary>
		/// Builds 0..2 draw items matching Main.DrawCursor ShowGamepadCursor branch.
		/// <paramref name="pointerX"/>/<paramref name="pointerY"/> must be the live
		/// SmartSelectGamepadPointer.GetPointerPosition() result (after UpdateCenter(ScreenSize/2)).
		/// </summary>
		public static int BuildDrawPlan(
			bool showGamepadCursor,
			bool smartCursorIsUsed,
			bool smartDrawArg,
			bool uiLinkBlocksSmartCrosshair,
			bool suppressDeadOrInvisible,
			float mouseX,
			float mouseY,
			float pointerX,
			float pointerY,
			bool pointerValid,
			float gamepadCursorAlpha,
			float globalTimeWrappedHourly,
			float bonusX,
			float bonusY,
			DrawItem[] dest)
		{
			if (dest == null || dest.Length < 2)
				return 0;
			if (!showGamepadCursor || suppressDeadOrInvisible)
				return 0;

			// Vanilla locals: t = mouse, t2 = pointer (before Swap).
			float tX = mouseX;
			float tY = mouseY;
			float t2X = pointerX;
			float t2Y = pointerY;
			bool flag2 = smartCursorIsUsed;

			if (flag2)
			{
				if (!pointerValid)
					return 0;

				float dx = t2X - tX;
				float dy = t2Y - tY;
				if (dx * dx + dy * dy < 1f)
				{
					flag2 = false;
				}
				else
				{
					// Utils.Swap(ref t, ref t2): t = pointer, t2 = mouse
					float sx = tX;
					float sy = tY;
					tX = t2X;
					tY = t2Y;
					t2X = sx;
					t2Y = sy;
				}
			}

			int count = 0;
			float num = 1f;

			if (flag2)
			{
				// Cursor 17 at t2 + bonus (= original mouse after Swap)
				num = SeparatedSmartAlphaScale;
				dest[count++] = new DrawItem
				{
					CursorIndex = CursorSmartAlt,
					PosX = t2X + bonusX,
					PosY = t2Y + bonusY,
					Rotation = Cursor17Rotation(globalTimeWrappedHourly),
					ColorMul = gamepadCursorAlpha,
					UseSmartFrame = false
				};
			}

			if (smartDrawArg && !uiLinkBlocksSmartCrosshair)
			{
				// Cursor 13 at t + bonus (= pointer after Swap, or mouse if not separated)
				dest[count++] = new DrawItem
				{
					CursorIndex = CursorSmart,
					PosX = tX + bonusX,
					PosY = tY + bonusY,
					Rotation = 0f,
					ColorMul = gamepadCursorAlpha * num,
					UseSmartFrame = true
				};
			}
			else
			{
				// Cursor 15 at (mouseX, mouseY) + bonus
				dest[count++] = new DrawItem
				{
					CursorIndex = CursorDefault,
					PosX = mouseX + bonusX,
					PosY = mouseY + bonusY,
					Rotation = 0f,
					ColorMul = 1f,
					UseSmartFrame = false
				};
			}

			return count;
		}
	}

	/// <summary>
	/// Apply / frame-gate policy for NativeCursorGamepadCompat (testable, no Terraria).
	/// </summary>
	public static class NativeCursorGamepadCompatPolicy
	{
		public enum InitOutcome
		{
			SkipModAbsent = 0,
			SkipAlreadyApplied,
			SkipDisabled,
			SkipOsDisabled,
			SkipFutureGamepadSupport,
			ApplyOk
		}

		public enum FrameGate
		{
			Draw = 0,
			SkipNotApplied,
			SkipDisabled,
			SkipNcInactive,
			SkipFutureSupport,
			SkipNotShowGamepadCursor,
			SkipGameMenu,
			SkipCursorOverride,
			SkipDeadInvisible,
			SkipVanillaAssumedDrawing,
			SkipPointerUnavailable
		}

		public sealed class SessionState
		{
			public bool Applied;
			public bool Disabled;
			public bool LoggedDisableOnce;
			/// <summary>Cached at Apply: NC present and suppresses vanilla DrawCursor.</summary>
			public bool NativeCursorSuppressesVanillaDraw;
			public bool NativeCursorDeclaresGamepadSupport;
			public string LastDisableReason;

			public void ResetForReload()
			{
				Applied = false;
				Disabled = false;
				LoggedDisableOnce = false;
				NativeCursorSuppressesVanillaDraw = false;
				NativeCursorDeclaresGamepadSupport = false;
				LastDisableReason = null;
			}
		}

		public static InitOutcome DecideInit(
			bool alreadyApplied,
			bool disabled,
			bool modPresent,
			bool ncRuntimeWouldSuppressVanilla,
			bool ncDeclaresGamepadSupport)
		{
			if (alreadyApplied)
				return InitOutcome.SkipAlreadyApplied;
			if (disabled)
				return InitOutcome.SkipDisabled;
			if (!modPresent)
				return InitOutcome.SkipModAbsent;
			if (ncDeclaresGamepadSupport)
				return InitOutcome.SkipFutureGamepadSupport;
			if (!ncRuntimeWouldSuppressVanilla)
				return InitOutcome.SkipOsDisabled;
			return InitOutcome.ApplyOk;
		}

		public static FrameGate DecideFrame(
			bool applied,
			bool disabled,
			bool ncSuppressesVanillaCached,
			bool ncDeclaresGamepadSupportCached,
			bool showGamepadCursor,
			bool gameMenu,
			bool cursorOverrideActive,
			bool suppressDeadOrInvisible,
			bool vanillaDrawCursorBodyRuns,
			bool pointerAvailable)
		{
			if (!applied)
				return FrameGate.SkipNotApplied;
			if (disabled)
				return FrameGate.SkipDisabled;
			if (ncDeclaresGamepadSupportCached)
				return FrameGate.SkipFutureSupport;
			if (!ncSuppressesVanillaCached)
				return FrameGate.SkipNcInactive;
			if (vanillaDrawCursorBodyRuns)
				return FrameGate.SkipVanillaAssumedDrawing;
			if (gameMenu)
				return FrameGate.SkipGameMenu;
			if (!showGamepadCursor)
				return FrameGate.SkipNotShowGamepadCursor;
			if (cursorOverrideActive)
				return FrameGate.SkipCursorOverride;
			if (suppressDeadOrInvisible)
				return FrameGate.SkipDeadInvisible;
			if (!pointerAvailable)
				return FrameGate.SkipPointerUnavailable;
			return FrameGate.Draw;
		}

		public static bool ShouldLogDisable(SessionState state)
		{
			if (state == null || state.LoggedDisableOnce)
				return false;
			state.LoggedDisableOnce = true;
			return true;
		}
	}
}
