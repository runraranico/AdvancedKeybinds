using AdvancedKeybinds.Binding;

using AdvancedKeybinds.Patches;

using Microsoft.Xna.Framework;

using Microsoft.Xna.Framework.Graphics;

using Terraria;

using Terraria.UI;



namespace AdvancedKeybinds.UI

{

	/// <summary>

	/// Full-screen UIState hosting the Hold gauge drag handle (AllowDrag).

	/// Feet/Mouse only — Custom uses viewport-pixel hit/drag in HoldTierHudSystem.

	/// </summary>

	public sealed class HoldTierHudDragUiState : UIState

	{

		public HoldTierHudDragHandle Handle { get; private set; }



		public override void OnInitialize()

		{

			Handle = new HoldTierHudDragHandle();

			Append(Handle);

		}

	}



	/// <summary>

	/// Transparent grab target sized to the gauge bar (Feet/Mouse UI space).

	/// Custom Anchor disables this path — see HoldTierHudSystem pixel drag.

	/// </summary>

	public sealed class HoldTierHudDragHandle : UIElement

	{

		private Vector2 _grabOffset;

		private float _originPadY;

		private bool _prevMiddleDown;



		public bool IsUiDragging { get; private set; }



		public bool IsHovering => IsMouseHovering || (Parent != null && ContainsPoint(Main.MouseScreen));



		public Vector2 GetBarOrigin()

		{

			CalculatedStyle dims = GetDimensions();

			return new Vector2(dims.X + dims.Width * 0.5f, dims.Y + _originPadY);

		}



		/// <summary>

		/// Place the hit box around <paramref name="barOrigin"/> (bar top-center).

		/// Skipped while dragging so mouse follow owns Left/Top.

		/// </summary>

		public void SyncLayout(Vector2 barOrigin, float barWidth, float barHeight, float padY)

		{

			if (IsUiDragging)

				return;



			ApplyLayout(barOrigin, barWidth, barHeight, padY);

		}



		/// <summary>Force Left/Top from a keep-visible bar origin (vanishing guard during drag).</summary>

		public void SnapToOrigin(Vector2 barOrigin, float barWidth, float barHeight, float padY)

			=> ApplyLayout(barOrigin, barWidth, barHeight, padY);



		private void ApplyLayout(Vector2 barOrigin, float barWidth, float barHeight, float padY)

		{

			_originPadY = MathHelper.Max(0f, padY);

			float width = MathHelper.Max(1f, barWidth);

			float height = MathHelper.Max(1f, barHeight + _originPadY * 2f);



			Width.Set(width, 0f);

			Height.Set(height, 0f);

			Left.Set(barOrigin.X - width * 0.5f, 0f);

			Top.Set(barOrigin.Y - _originPadY, 0f);

			Recalculate();

		}



		public void CancelUiDrag()

		{

			IsUiDragging = false;

			_grabOffset = Vector2.Zero;

		}



		public override void MiddleMouseDown(UIMouseEvent evt)

		{

			base.MiddleMouseDown(evt);

			if (HoldTierHudSystem.IsCustomPixelDragUiDisabled())

				return;

			if (evt.Target == this)

				TryBeginDrag(evt.MousePosition);

		}



		public override void MiddleMouseUp(UIMouseEvent evt)

		{

			base.MiddleMouseUp(evt);

			if (IsUiDragging)

				FinishDrag(commit: true);

		}



		public override void Update(GameTime gameTime)

		{

			base.Update(gameTime);



			if (HoldTierHudSystem.IsCustomPixelDragUiDisabled())

			{

				if (IsUiDragging)

					CancelUiDrag();

				_prevMiddleDown = false;

				return;

			}



			if (Main.LocalPlayer == null || !Main.LocalPlayer.active)

			{

				_prevMiddleDown = false;

				return;

			}



			bool hovering = ContainsPoint(Main.MouseScreen);

			if (hovering || IsUiDragging)

				Main.LocalPlayer.mouseInterface = true;



			bool middleDown = MouseInputHelper.IsPhysicalMiddleDown() || Main.mouseMiddle;



			// Backup start: UI MiddleMouseDown may miss when Main.mouseMiddle is remapped;

			// physical middle + UI ContainsPoint is the reliable path.

			if (!IsUiDragging && hovering && middleDown && !_prevMiddleDown)

				TryBeginDrag(Main.MouseScreen);



			if (IsUiDragging)

			{

				if (!middleDown)

				{

					FinishDrag(commit: true);

				}

				else

				{

					// Mouse-proposed origin from stable grab offset (do not retarget while

					// magnet holds, or exit detection would latch forever at screen center).

					float left = Main.mouseX - _grabOffset.X;

					float top = Main.mouseY - _grabOffset.Y;

					Left.Set(left, 0f);

					Top.Set(top, 0f);

					Recalculate();

					// Desired bar top-center from the mouse proposal — Notify applies magnet

					// and pins Left/Top (including when free, so draw never lags the magnet).

					Vector2 desiredOrigin = new(left + Width.Pixels * 0.5f, top + _originPadY);

					HoldTierHudSystem.NotifyUiDragFollow(desiredOrigin);

					HoldTierHudSystem.SuppressDragInputsPublic();

				}

			}



			_prevMiddleDown = middleDown;

		}



		protected override void DrawSelf(SpriteBatch spriteBatch)

		{

			// Intentionally empty — gauge is drawn by HoldTierHudSystem.PostDrawInterface.

			// Hit target stays invisible so preview / charge bar visuals are unchanged.

		}



		private void TryBeginDrag(Vector2 mouse)

		{

			if (IsUiDragging)

				return;

			if (HoldTierHudSystem.IsCustomPixelDragUiDisabled())

				return;

			// P+Middle (2s hold) is reserved for reset-to-player-head (PollDrag), not grab.

			if (HoldTierHudSystem.IsFeetResetKeyHeld())

				return;

			if (!HoldTierHudSystem.TryBeginUiDrag(GetBarOrigin()))

				return;



			IsUiDragging = true;

			_grabOffset = mouse - new Vector2(Left.Pixels, Top.Pixels);

			HoldTierHudSystem.SuppressDragInputsPublic();

			if (Main.LocalPlayer != null)

				Main.LocalPlayer.mouseInterface = true;

		}



		private void FinishDrag(bool commit)

		{

			if (!IsUiDragging)

				return;



			IsUiDragging = false;

			_grabOffset = Vector2.Zero;

			if (commit)

				HoldTierHudSystem.NotifyUiDragCommit();

			else

				HoldTierHudSystem.NotifyUiDragCancel();

		}

	}

}

