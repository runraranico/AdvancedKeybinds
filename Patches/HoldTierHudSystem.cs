using System;
using System.Collections.Generic;
using AdvancedKeybinds.Binding;
using AdvancedKeybinds.Diagnostics;
using AdvancedKeybinds.Logic;
using AdvancedKeybinds.UI;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Terraria;
using Terraria.GameContent;
using Terraria.GameInput;
using Terraria.Localization;
using Terraria.ModLoader;
using Terraria.UI;
using Terraria.UI.Chat;

namespace AdvancedKeybinds.Patches
{
	/// <summary>
	/// Draws the on-release Hold tier gauge while charging (horizontal bar), and cycles HUD anchor via keybind.
	/// Drag is Custom-only (viewport/game-pixel middle-drag). Feet/Mouse never drag and never promote to Custom.
	/// </summary>
	public class HoldTierHudSystem : ModSystem
	{
		private const int HideAfterLastHoldExtraMs = 500;
		private const float BarHeight = 10f;
		/// <summary>AllowDrag preview bar — slightly thicker so the grab target is obvious.</summary>
		private const float PreviewBarHeight = 14f;
		/// <summary>
		/// Gap below player feet for Feet mode (game px authored; ÷UIScale for UI draw).
		/// Custom head-side init uses the same magnitude above the head for visual symmetry.
		/// </summary>
		private const float FeetLowerPx = 38f;
		/// <summary>Extra gap under feet / above head (authored game px), shared by Feet and Custom init.</summary>
		private const float FeetCustomSymmetryPadPx = 4f;
		/// <summary>
		/// Gap between player hitbox top and Custom bar bottom when seeding / resetting Custom
		/// (game pixels). Matches Feet's downward offset for head↔feet symmetry.
		/// </summary>
		private const float CustomHeadGapPixel = FeetLowerPx + FeetCustomSymmetryPadPx;
		/// <summary>
		/// Mouse-follow offset from the cursor tip (authored px → UI via PxToHud).
		/// Do not reuse <see cref="FeetLowerPx"/> here: that +38 pushed the bar past the edge
		/// early so keep-visible looked short of the true bottom-right (Custom can reach).
		/// </summary>
		private const float MouseFollowOffsetXPx = 18f;
		private const float MouseFollowOffsetYPx = 22f;
		/// <summary>
		/// Legacy full-fit inset (Feet only). Mouse / Custom use keep-visible instead.
		/// </summary>
		private const float ScreenMargin = 0f;
		/// <summary>

		/// Custom free-placement: keep at least this many UI px of the draw bar on-screen

		/// so the bar cannot vanish and become un-grabbable. Not a layout-fit margin.

		/// </summary>

		private const float MinVisiblePx = 8f;

		/// <summary>Normalized Custom may slightly exceed 0–1 when the bar hangs off an edge.</summary>
		private const float NormStoreMin = -0.5f;
		private const float NormStoreMax = 1.5f;
		/// <summary>
		/// Config defaults / Restore Defaults sentinel. Only the exact pair (-1,-1) means unset;
		/// ordinary negative drag coords must clamp to the margin, not warp to default.
		/// </summary>
		private const float CustomPositionUnset = -1f;
		/// <summary>
		/// Idle charge-bar hit inflate (AllowDrag off). Target pad around the draw bar;
		/// at screen edges outer pad is clipped and redistributed inward (see LayoutFromOrigin).
		/// </summary>
		private const int HitPadX = 24;
		private const int HitPadY = 28;
		/// <summary>AllowDrag grab inflate — large enough that bar-center clicks cannot miss.</summary>
		private const int DragHitPadX = 40;
		private const int DragHitPadY = 53;
		/// <summary>AllowDrag minimum hit height (14 bar + padY*2 → at least 120). Legacy self-hit only.</summary>
		private const int DragMinHitHeight = 120;
		/// <summary>UI handle vertical pad around the draw bar (UI units after PxToHud).</summary>
		private const int UiDragHitPadYPx = 28;
		/// <summary>
		/// Drag X magnet: snap bar top-center (origin.X) to UI width÷2 when within this many
		/// authored px (via <see cref="PxToHud"/>), floored to a % of UI width so high UIScale
		/// does not shrink the magnet to a few units. Y stays free.
		/// Wide on purpose — previous ±12 / ±36 felt invisible in play.
		/// </summary>
		private const int CenterXSnapRadiusPx = 80;
		/// <summary>Minimum center-X magnet half-width as a fraction of UI width.</summary>
		private const float CenterXSnapMinScreenFraction = 0.06f;
		/// <summary>
		/// Last follow/commit origin written by NotifyUiDragFollow (magnet applied).
		/// Draw must use this while dragging — GetBarOrigin can still reflect pre-snap mouse Left.
		/// </summary>
		private static Vector2 _dragFollowOrigin;
		private static bool _hasDragFollowOrigin;

		/// <summary>Custom-only: middle-drag in viewport pixel space (not UIElement / UIScale).</summary>
		private static bool _customPixelDragActive;
		private static Vector2 _customDragOriginPixel;
		private static Vector2 _customGrabOffsetPixel;
		private static bool _prevCustomMiddleDown;
		private static int _trackedCustomViewportW;
		private static int _trackedCustomViewportH;

		private static readonly List<ReleaseHoldHudGroup> ActiveGroups = new();
		private static string _flashActionId;
		private static double _flashUntilMs;
		private static int _previousSelectedIndex = -1;
		private static string _previousKeyToken;

		public static bool IsDragging { get; private set; }
		public static Rectangle LastBarRect { get; private set; }

		private static Vector2 _lastBarOrigin;
		private static Vector2 _preDragCustomPos;
		private static HoldTierHudAnchor _preDragAnchor;
		private static bool _prevEscapeDown;
		private static bool _prevAllowDrag;
		/// <summary>Tracks whether the last PollDrag saw Enabled=true (edge-log when turning off).</summary>
		private static bool _prevAllowDragLoggedEnabled;
		/// <summary>Last-frame center-X snap state (AKDIAG on engage/release only).</summary>
		private static bool _centerXSnapped;
		/// <summary>P+Middle hold accumulator for Custom→player-head reset (same tick model as Hold bindings).</summary>
		private const double FeetResetHoldMs = 2000.0;
		private static double _feetResetHeldMs;
		private static bool _feetResetFired;
		private static bool _feetResetChordWasHeld;
		/// <summary>True when the cursor is over the grab hit rect (AllowDrag). Used for preview bar hover tint.</summary>
		private static bool _mouseOverBar;

		private UserInterface _dragUi;
		private HoldTierHudDragUiState _dragUiState;
		private static HoldTierHudDragHandle _dragHandle;
		private GameTime _lastUiGameTime;
		private static uint _lastClampLogUpdate;
		private static uint _lastUnstableClampLogUpdate;
		private static uint _lastMismatchClampLogUpdate;
		private static uint _lastTransientRejectLogUpdate;
		private static bool _loggedPollAlive;
		private static bool _prevHasFocus;
		private static bool _hasPrevHasFocus;
		/// <summary>Exact layout from the last PostDraw — used for LastBarRect / draw tracking.</summary>
		private static HudBarLayout _lastDrawnLayout;
		private static bool _hasLastDrawnLayout;
		/// <summary>Last stable HUD screen size (drag follow / transient guards). Custom is stored normalized.</summary>
		private static int _trackedScreenW;
		private static int _trackedScreenH;
		/// <summary>
		/// Pending size while we wait for a few matching frames. Prevents treating transient
		/// borderless/monitor-move sizes (e.g. backbuffer 3840 while HUD is 1600) as settled.
		/// </summary>
		private static int _candidateScreenW;
		private static int _candidateScreenH;
		private static int _candidateStableFrames;
		private static string _candidateReason;
		private static uint _lastCandidateLogUpdate;
		/// <summary>Skip clamp/remap writes while the client surface is in a transient junk size.</summary>
		private const int MinStableScreenW = 640;
		private const int MinStableScreenH = 480;
		/// <summary>
		/// Consecutive matching samples before accepting a new tracked size. Monitor-move Device
		/// Reset can hold an inflated Main.screen* for several frames — 3 was too eager.
		/// </summary>
		private const int ScreenChangeConfirmFrames = 12;

		public override void Load()
		{
			if (Main.dedServ)
				return;

			HoldHudCoords.InvalidateGamePixelSurface("ModLoad");
			_dragUi = new UserInterface();
			_dragUiState = new HoldTierHudDragUiState();
			_dragUiState.Activate();
			_dragHandle = _dragUiState.Handle;
		}

		public override void Unload()
		{
			HoldHudCoords.InvalidateGamePixelSurface("ModUnload");
			_dragUi = null;
			_dragUiState = null;
			_dragHandle = null;
			_lastUiGameTime = null;
			IsDragging = false;
			_centerXSnapped = false;
			_hasDragFollowOrigin = false;
			_customPixelDragActive = false;
			_prevCustomMiddleDown = false;
		}

		public override void UpdateUI(GameTime gameTime)
		{
			#if AK_DIAGNOSTICS
			AkCpuMeasureSystem.TickPhase("HoldUpdateUI");
			#endif
			#if AK_DIAGNOSTICS
			AkCpuMeasureSystem.Begin(AkCpuMeasureSystem.IdHoldHudUpdateUI);
			#endif
			try
			{
			_lastUiGameTime = gameTime;
			HoldHudCoords.LogPhaseSnapshot("UpdateUI");
			SyncDragUiVisibility();
			if (_dragUi?.CurrentState == null)
				return;

			// Custom uses HoldTierHudSystem pixel drag — do not drive UIElement layout/hover.
			if (!IsUsingCustomPixelSpace(AdvancedKeybindsConfig.Instance))
			{
				if (!IsDragging)
					SyncHandleToResolvedOrigin();

				_dragUi.Update(gameTime);
				HoldHudCoords.LogPhaseSnapshot("UIElement-Update");

				if (_dragHandle != null)
					_mouseOverBar = IsDragging || _dragHandle.IsHovering;
			}
			}
			finally
			{
				#if AK_DIAGNOSTICS
				AkCpuMeasureSystem.End(AkCpuMeasureSystem.IdHoldHudUpdateUI);
				#endif
			}
		}

		public override void ModifyInterfaceLayers(List<GameInterfaceLayer> layers)
		{
			int mouseTextIndex = layers.FindIndex(layer => layer.Name.Equals("Vanilla: Mouse Text"));
			if (mouseTextIndex == -1)
				return;

			layers.Insert(mouseTextIndex, new LegacyGameInterfaceLayer(
				"AdvancedKeybinds: HoldTierHudDrag",
				delegate
				{
					if (_lastUiGameTime != null && _dragUi?.CurrentState != null)
					{
						HoldHudCoords.LogPhaseSnapshot("UIElement-Draw");
						_dragUi.Draw(Main.spriteBatch, _lastUiGameTime);
					}
					return true;
				},
				InterfaceScaleType.UI));

			// Immediately before Vanilla Mouse Text so the tip is not cleared by later UI.
			mouseTextIndex = layers.FindIndex(layer => layer.Name.Equals("Vanilla: Mouse Text"));
			if (mouseTextIndex != -1)
			{
				layers.Insert(mouseTextIndex, new LegacyGameInterfaceLayer(
					"AdvancedKeybinds: HoldTierHudCustomHoverTip",
					delegate
					{
						TryShowCustomGaugeHoverTip(AdvancedKeybindsConfig.Instance);
						return true;
					},
					InterfaceScaleType.UI));
			}
		}

		private readonly struct HudScreenInfo
		{
			public readonly int MainW;
			public readonly int MainH;
			public readonly int ViewportW;
			public readonly int ViewportH;
			public readonly int BackBufferW;
			public readonly int BackBufferH;
			public readonly int Width;
			public readonly int Height;

			public HudScreenInfo(int mainW, int mainH, int vpW, int vpH, int bbW, int bbH, int width, int height)
			{
				MainW = mainW;
				MainH = mainH;
				ViewportW = vpW;
				ViewportH = vpH;
				BackBufferW = bbW;
				BackBufferH = bbH;
				Width = width;
				Height = height;
			}

			public float MinX => ScreenMargin;
			public float MaxX => Width - ScreenMargin;
			public float MinY => ScreenMargin;
			public float MaxY => Height - ScreenMargin;

			public string Format()
				=> "hud=" + Width + "x" + Height
					+ " main=" + MainW + "x" + MainH
					+ " vp=" + ViewportW + "x" + ViewportH
					+ " bb=" + BackBufferW + "x" + BackBufferH
					+ " clamp=[" + MinX.ToString("0") + ".." + MaxX.ToString("0")
					+ "," + MinY.ToString("0") + ".." + MaxY.ToString("0") + "]";
		}

		/// <summary>Absolute gate: Hold gauge feature (draw / hit / drag / placement ops).</summary>
		private static bool IsHudFeatureEnabled(AdvancedKeybindsConfig config)
			=> config != null && config.HoldTierHudEnabled;

		/// <summary>Absolute gate: placement preview, drag, P+Middle reset — Custom only.</summary>
		private static bool IsHudDragAllowed(AdvancedKeybindsConfig config)
			=> IsHudFeatureEnabled(config)
				&& config.HoldTierHudAllowDrag
				&& config.HoldTierHudAnchor == HoldTierHudAnchor.Custom;

		public override void PostUpdateInput()
		{
			#if AK_DIAGNOSTICS
			AkCpuMeasureSystem.Begin(AkCpuMeasureSystem.IdHoldHudPostUpdateInput);
			#endif
			try
			{
				if (Main.gameMenu || Main.drawingPlayerChat || Main.editSign || Main.editChest)
				{
					CancelDragSilent();
					_mouseOverBar = false;
					_prevEscapeDown = false;
					return;
				}

				AdvancedKeybindsConfig config = AdvancedKeybindsConfig.Instance;
				if (config == null)
					return;

				// Capture game-pixel surface before any UI zoom pollutes Main.screenWidth.
				HoldHudCoords.RefreshGamePixelSurface("PostUpdateInput");
				HoldHudCoords.LogPhaseSnapshot("PostUpdateInput", config);

				// Track stable screen + migrate legacy absolute Custom → normalized 0–1.
				// Denormalize at draw time so relative placement survives monitor moves.
				if (!IsDragging && !_customPixelDragActive)
					SyncCustomPositionToScreenSize(config, "PostUpdateInput");

				if (AdvancedKeybinds.CycleHoldTierHudAnchorHotkey?.JustPressed == true)
					config.CycleAnchor();

				// Same timing as KeybindShortcutSystem (physical middle works reliably here).
				PollDrag();
			}
			finally
			{
				#if AK_DIAGNOSTICS
				AkCpuMeasureSystem.End(AkCpuMeasureSystem.IdHoldHudPostUpdateInput);
				#endif
			}
		}

		/// <summary>
		/// Monitor-move hook. Intentionally does NOT rewrite Custom immediately:
		/// SetResolution leaves Main.screen* at monitor-native size for several frames.
		/// Stable Sync (migrate / track size) runs later from PostUpdateInput when not dragging.
		/// Normalized Custom denormalizes against the live screen at draw time.
		/// </summary>
		public static void NotifyClientSurfaceChanged(string reason)
		{
			HoldHudCoords.InvalidateGamePixelSurface(reason);
			HoldHudCoords.RefreshGamePixelSurface("NotifyClientSurfaceChanged:" + reason);
			if (AkDiag.Enabled) AkDiag.Log("HoldHud Notify surface-invalidate reason=" + reason);
		}

		/// <summary>
		/// Esc / P+Middle (2s) Custom-head reset / AllowDrag toggle.
		/// Custom: viewport-pixel drag in this system. Feet/Mouse: UIElement handle (UI space).
		/// </summary>
		public static void PollDrag()
		{
			AdvancedKeybindsConfig config = AdvancedKeybindsConfig.Instance;
			if (!IsHudFeatureEnabled(config) || Main.LocalPlayer == null || !Main.LocalPlayer.active)
			{
				if (config != null && _prevAllowDragLoggedEnabled)
				{
					_prevAllowDragLoggedEnabled = false;
					if (AkDiag.Enabled)
					{
						AkDiag.Log(
						"HoldHud early-exit Enabled=false"
						+ " anchor=" + config.HoldTierHudAnchor
						+ " allowDrag=" + config.HoldTierHudAllowDrag
						+ " preferCustom=" + ClientPrefs.HoldHudPreferCustom);
					}
				}

				CancelDragSilent();
				ClearFeetResetHold();
				_mouseOverBar = false;
				_prevEscapeDown = false;
				_prevCustomMiddleDown = false;
				_hasLastDrawnLayout = false;
				return;
			}

			_prevAllowDragLoggedEnabled = true;

			if (!_loggedPollAlive)
			{
				_loggedPollAlive = true;
				if (AkDiag.Enabled) AkDiag.Log("HoldHud drag PollDrag alive (PostUpdateInput)");
			}

			bool allowDrag = IsHudDragAllowed(config);
			if (allowDrag != _prevAllowDrag)
			{
				GetCustomPixelSurfaceSize(out int vpW, out int vpH);
				if (AkDiag.Enabled)
				{
					AkDiag.Log(
					"HoldHud AllowDrag=" + allowDrag
					+ " enabled=" + config.HoldTierHudEnabled
					+ " allowDragSetting=" + config.HoldTierHudAllowDrag
					+ " anchor=" + config.HoldTierHudAnchor
					+ " custom=(" + config.HoldTierHudCustomPositionX.ToString("0.#")
					+ "," + config.HoldTierHudCustomPositionY.ToString("0.#") + ")"
					+ " barW=" + config.HoldTierHudBarWidth
					+ " UIScale=" + Main.UIScale.ToString("0.###")
					+ " viewport=" + vpW + "x" + vpH);
				}
				_prevAllowDrag = allowDrag;
			}

			bool middleDown = MouseInputHelper.IsPhysicalMiddleDown();

			if (IsUsingCustomPixelSpace(config) || _customPixelDragActive)
			{
				PollCustomPixelInteraction(config, allowDrag, middleDown);
				return;
			}

			// Feet / Mouse: follow only — never drag, never promote to Custom, never P+Middle reset.
			Vector2 mouse = GetHudMouse();
			LogFocusChangeIfNeeded(mouse);

			if (IsDragging && !_customPixelDragActive)
			{
				#if AK_DIAGNOSTICS
				HoldHudProbe.Log(
					"ANOMALY stale-ui-drag-on-feet-or-mouse anchor=" + config.HoldTierHudAnchor);
				#endif
				RestorePreDragAndEnd(save: false, reason: "stale-ui-drag-cleared");
			}

			ClearFeetResetHold();
			_mouseOverBar = false;
			_prevEscapeDown = false;

			bool middleEdge = middleDown && !_prevCustomMiddleDown;
			_prevCustomMiddleDown = middleDown;
			if (middleEdge && allowDrag)
			{
				#if AK_DIAGNOSTICS
				HoldHudProbe.LogDragCheck(
					"anchor-not-custom",
					false,
					"anchor=" + config.HoldTierHudAnchor
					+ " allowDrag=true enabled=" + config.HoldTierHudEnabled
					+ " preferCustom=" + ClientPrefs.HoldHudPreferCustom);
				#endif
			}

			NormalizeCustomPositionIfNeeded(config);
		}

		/// <summary>Custom viewport-pixel hit / drag / P+Middle reset (no UIElement).</summary>
		private static void PollCustomPixelInteraction(
			AdvancedKeybindsConfig config,
			bool allowDrag,
			bool middleDown)
		{
			LogFocusChangeIfNeeded(GetCustomPixelMouse());

			if (_customPixelDragActive)
			{
				ClearFeetResetHold();
				_mouseOverBar = true;
				if (!allowDrag)
				{
					if (AkDiag.Enabled) AkDiag.Log("HoldHud custom-pixel drag cancelled (AllowDrag off)");
					CancelDragSilent();
					_mouseOverBar = false;
					_prevCustomMiddleDown = middleDown;
					return;
				}

				bool escapeDown = Main.keyState.IsKeyDown(Keys.Escape);
				if (escapeDown && !_prevEscapeDown)
				{
					RestorePreDragAndEnd(save: true, reason: "Esc-cancel-custom-pixel");
					SuppressDragInputs();
					_prevEscapeDown = escapeDown;
					_prevCustomMiddleDown = middleDown;
					return;
				}
				_prevEscapeDown = escapeDown;

				if (!middleDown)
				{
					CommitCustomPixelDrag(config);
					_prevCustomMiddleDown = false;
					return;
				}

				Vector2 mouse = GetCustomPixelMouse();
				Vector2 desired = mouse - _customGrabOffsetPixel;
				GetCustomPixelSurfaceSize(out int vw, out _);
				desired = SnapCustomOriginXToViewportCenter(desired, vw, out bool snapped);
				LogCenterXSnapChange(snapped, mouse - _customGrabOffsetPixel, desired);
				_customDragOriginPixel = desired;
				SuppressDragInputs();
				Main.LocalPlayer.mouseInterface = true;
				_prevCustomMiddleDown = middleDown;
				return;
			}

			_prevEscapeDown = false;

			// Hover hit-test runs even when AllowDrag is off (so the Custom tip can show).
			// Drag begin / P+Middle reset still require AllowDrag.
			if (TryResolveCustomOriginPixel(config, out Vector2 origin))
			{
				float barW = ResolveCustomBarWidthPixel(config);
				float barH = ResolveCustomBarHeightPixel(allowDrag);
				Rectangle drawRect = GetCustomPixelDrawRect(origin, barW, barH);
				GetCustomPixelHitPads(out int padX, out int padY);
				Rectangle hitRect = InflateHitRectPixel(drawRect, padX, padY);
				Vector2 mouse = GetCustomPixelMouse();
				Vector2 mouseUi = GetHudMouse();
				bool over = hitRect.Contains((int)mouse.X, (int)mouse.Y);
				_mouseOverBar = over;

				#if AK_DIAGNOSTICS
				HoldHudProbe.LogHitTest(
					drawRect,
					hitRect,
					mouse,
					mouseUi,
					config,
					drawBranch: "custom-Identity",
					hitBranch: "custom-pixel-hit");
				#endif

				if (!allowDrag)
				{
					ClearFeetResetHold();
					_prevCustomMiddleDown = middleDown;
					NormalizeCustomPositionIfNeeded(config);
					return;
				}

				PollFeetResetHold(config, middleDown);

				if (_feetResetChordWasHeld)
				{
					_mouseOverBar = false;
					_prevCustomMiddleDown = middleDown;
					NormalizeCustomPositionIfNeeded(config);
					return;
				}

				bool edge = middleDown && !_prevCustomMiddleDown;
				if (edge)
				{
					HoldHudCoords.LogCustomLayout(
						"input-edge",
						drawRect,
						hitRect,
						mouse,
						new Vector2(config.HoldTierHudCustomPositionX, config.HoldTierHudCustomPositionY),
						origin);
					string detail =
						"anchor=" + config.HoldTierHudAnchor
						+ " enabled=" + config.HoldTierHudEnabled
						+ " allowDrag=" + allowDrag
						+ " preferCustom=" + ClientPrefs.HoldHudPreferCustom
						+ " isDragging=" + IsDragging
						+ " customPixelDrag=" + _customPixelDragActive
						+ " prevMiddle=" + _prevCustomMiddleDown
						+ " middle=" + middleDown
						+ " over=" + over
						+ " mouse=" + FormatVec(mouse)
						+ " origin=" + FormatVec(origin)
						+ " draw=" + FormatRect(drawRect)
						+ " hit=" + FormatRect(hitRect)
						+ " norm=(" + config.HoldTierHudCustomPositionX.ToString("0.###") + ","
						+ config.HoldTierHudCustomPositionY.ToString("0.###") + ")"
						+ " surfaceStable=" + HoldHudCoords.GamePixelSurfaceW + "x" + HoldHudCoords.GamePixelSurfaceH
						+ " screen=" + Main.screenWidth + "x" + Main.screenHeight
						+ " uiZoomPolluted=" + HoldHudCoords.IsUiZoomScreenPolluted();

					if (config.HoldTierHudAnchor != HoldTierHudAnchor.Custom && !_customPixelDragActive)
						{
							#if AK_DIAGNOSTICS
							HoldHudProbe.LogDragCheck("anchor-not-custom", false, detail);
							#endif
						}
					else if (IsDragging || _customPixelDragActive)
						{
							#if AK_DIAGNOSTICS
							HoldHudProbe.LogDragCheck("stale-drag-state", false, detail);
							#endif
						}
					else if (!over)
						{
							#if AK_DIAGNOSTICS
							HoldHudProbe.LogDragCheck("mouse-not-over-hitbox", false, detail);
							#endif
						}
					else if (IsFeetResetKeyHeld())
						{
							#if AK_DIAGNOSTICS
							HoldHudProbe.LogDragCheck("feet-reset-chord", false, detail);
							#endif
						}
					else
					{
						#if AK_DIAGNOSTICS
						HoldHudProbe.LogDragCheck("true", true, detail);
						#endif
						BeginCustomPixelDrag(config, origin, mouse);
						#if AK_DIAGNOSTICS
						HoldHudProbe.Log(
							"DRAG-BEGIN origin=" + FormatVec(origin)
							+ " mouse=" + FormatVec(mouse)
							+ " grabOff=" + FormatVec(_customGrabOffsetPixel)
							+ " " + detail);
						#endif
					}
				}
			}
			else
			{
				_mouseOverBar = false;
				ClearFeetResetHold();
				if (middleDown && !_prevCustomMiddleDown)
				{
					#if AK_DIAGNOSTICS
					HoldHudProbe.LogDragCheck(
						"origin-unresolved",
						false,
						"anchor=" + config.HoldTierHudAnchor
						+ " norm=(" + config.HoldTierHudCustomPositionX.ToString("0.###") + ","
						+ config.HoldTierHudCustomPositionY.ToString("0.###") + ")");
					#endif
				}
			}

			_prevCustomMiddleDown = middleDown;
			NormalizeCustomPositionIfNeeded(config);
		}

		private static void BeginCustomPixelDrag(
			AdvancedKeybindsConfig config,
			Vector2 originPixel,
			Vector2 mousePixel)
		{
			if (config == null || config.HoldTierHudAnchor != HoldTierHudAnchor.Custom)
			{
				#if AK_DIAGNOSTICS
				HoldHudProbe.LogDragCheck(
					"anchor-not-custom",
					false,
					"anchor=" + (config?.HoldTierHudAnchor.ToString() ?? "null")
					+ " path=BeginCustomPixelDrag");
				#endif
				return;
			}

			if (!IsHudDragAllowed(config))
			{
				#if AK_DIAGNOSTICS
				HoldHudProbe.LogDragCheck("allowDrag-false", false, "path=BeginCustomPixelDrag");
				#endif
				if (AkDiag.Enabled)
				{
					AkDiag.Log(
					"HoldHud custom-pixel drag REJECTED"
					+ " enabled=" + (config?.HoldTierHudEnabled ?? false)
					+ " allowDrag=" + (config?.HoldTierHudAllowDrag ?? false));
				}
				return;
			}

			_preDragAnchor = config.HoldTierHudAnchor;
			_preDragCustomPos = new Vector2(
				config.HoldTierHudCustomPositionX,
				config.HoldTierHudCustomPositionY);

			IsDragging = true;
			_customPixelDragActive = true;
			_centerXSnapped = false;
			_customDragOriginPixel = originPixel;
			_customGrabOffsetPixel = mousePixel - originPixel;
			_hasDragFollowOrigin = false;

			GetCustomPixelSurfaceSize(out int vw, out int vh);
			float barW = ResolveCustomBarWidthPixel(config);
			float barH = ResolveCustomBarHeightPixel(true);
			Rectangle drawRect = GetCustomPixelDrawRect(originPixel, barW, barH);
			GetCustomPixelHitPads(out int padX, out int padY);
			Rectangle hitRect = InflateHitRectPixel(drawRect, padX, padY);
			if (AkDiag.Enabled)
			{
				AkDiag.Log(
				"HoldHud custom-pixel drag START"
				+ " origin=(" + originPixel.X.ToString("0.#") + "," + originPixel.Y.ToString("0.#") + ")"
				+ " mouse=(" + mousePixel.X.ToString("0.#") + "," + mousePixel.Y.ToString("0.#") + ")"
				+ " draw=" + FormatRect(drawRect)
				+ " hit=" + FormatRect(hitRect)
				+ " viewport=" + vw + "x" + vh
				+ " screen=" + Main.screenWidth + "x" + Main.screenHeight
				+ " UIScale=" + Main.UIScale.ToString("0.###")
				+ " matrix=Identity");
			}
			#if AK_DIAGNOSTICS
			HoldHudProbe.LogSurface("drag-start");
			#endif
			#if AK_DIAGNOSTICS
			HoldHudProbe.Log(
				"DRAG start origin=" + FormatVec(originPixel)
				+ " mouse=" + FormatVec(mousePixel)
				+ " grabOff=" + FormatVec(_customGrabOffsetPixel)
				+ " fromAnchor=" + _preDragAnchor);
			#endif
		}

		private static void CommitCustomPixelDrag(AdvancedKeybindsConfig config)
		{
			if (!_customPixelDragActive)
				return;

			#if AK_DIAGNOSTICS
			HoldHudProbe.LogSurface("drag-commit-before");
			#endif
			GetCustomPixelSurfaceSize(out int vw, out int vh);
			Vector2 pixelBeforeSnap = _customDragOriginPixel;
			Vector2 pixel = SnapCustomOriginXToViewportCenter(_customDragOriginPixel, vw, out bool snapped);
			_customDragOriginPixel = pixel;

			config.HoldTierHudAnchor = HoldTierHudAnchor.Custom;
			IsDragging = false;
			_customPixelDragActive = false;
			_centerXSnapped = false;
			_hasDragFollowOrigin = false;

			if (IsStableHudScreen(vw, vh))
				StoreNormalizedCustomPosition(config, pixel, vw, vh, "custom-pixel-commit");

			Vector2 roundTrip = DenormalizeCustomPosition(
				config.HoldTierHudCustomPositionX,
				config.HoldTierHudCustomPositionY,
				vw,
				vh);
			#if AK_DIAGNOSTICS
			HoldHudProbe.LogCoordPipeline(
				"custom-pixel-commit",
				pixelBeforeSnap,
				config.HoldTierHudCustomPositionX,
				config.HoldTierHudCustomPositionY,
				roundTrip,
				vw,
				vh);
			#endif
			#if AK_DIAGNOSTICS
			HoldHudProbe.Log(
				"COMMIT snapX=" + snapped
				+ " beforeSnap=(" + pixelBeforeSnap.X.ToString("0.#") + "," + pixelBeforeSnap.Y.ToString("0.#") + ")"
				+ " afterSnap=(" + pixel.X.ToString("0.#") + "," + pixel.Y.ToString("0.#") + ")"
				+ " mouse=" + FormatVec(GetCustomPixelMouse())
				+ " preferCustom=" + ClientPrefs.HoldHudPreferCustom);
			#endif

			_suppressConfigClamp = true;
			try
			{
				config.SaveHoldHudChangesSilent();
			}
			finally
			{
				_suppressConfigClamp = false;
			}

			AlignTrackedCustomViewport("PostCustomPixelDrag");
			#if AK_DIAGNOSTICS
			HoldHudProbe.LogState("drag-commit-after-save", config);
			#endif
			#if AK_DIAGNOSTICS
			HoldHudProbe.Log(
				"DRAG-END reason=custom-pixel-commit"
				+ " anchor=" + config.HoldTierHudAnchor
				+ " allowDrag=" + config.HoldTierHudAllowDrag
				+ " enabled=" + config.HoldTierHudEnabled
				+ " preferCustom=" + ClientPrefs.HoldHudPreferCustom
				+ " isDragging=" + IsDragging
				+ " customPixelDrag=" + _customPixelDragActive
				+ " norm=(" + config.HoldTierHudCustomPositionX.ToString("0.###") + ","
				+ config.HoldTierHudCustomPositionY.ToString("0.###") + ")"
				+ " originPixel=(" + pixel.X.ToString("0.#") + "," + pixel.Y.ToString("0.#") + ")"
				+ " surface=" + vw + "x" + vh
				+ " screenNow=" + Main.screenWidth + "x" + Main.screenHeight
				+ " uiZoomPolluted=" + HoldHudCoords.IsUiZoomScreenPolluted());
			#endif
			if (AkDiag.Enabled)
			{
				AkDiag.Log(
				"HoldHud custom-pixel drag END"
				+ " norm=(" + config.HoldTierHudCustomPositionX.ToString("0.###") + ","
				+ config.HoldTierHudCustomPositionY.ToString("0.###") + ")"
				+ " originPixel=(" + pixel.X.ToString("0.#") + "," + pixel.Y.ToString("0.#") + ")"
				+ " viewport=" + vw + "x" + vh
				+ " screen=" + Main.screenWidth + "x" + Main.screenHeight
				+ " UIScale=" + Main.UIScale.ToString("0.###")
				+ " matrix=Identity"
				+ " anchor=" + config.HoldTierHudAnchor
				+ " preferCustom=" + ClientPrefs.HoldHudPreferCustom);
			}
		}

		private static string FormatVec(Vector2 v)
			=> "(" + v.X.ToString("0.#") + "," + v.Y.ToString("0.#") + ")";

		/// <summary>
		/// True while P is held — middle-drag must not start (chord is reserved for Custom head reset).
		/// </summary>
		public static bool IsFeetResetKeyHeld()
			=> Main.keyState.IsKeyDown(Keys.P);

		private static void ClearFeetResetHold()
		{
			if (_feetResetChordWasHeld)
				if (AkDiag.Enabled) AkDiag.Log("HoldHud feet-reset chord cleared");
			_feetResetHeldMs = 0;
			_feetResetFired = false;
			_feetResetChordWasHeld = false;
		}

		private static void PollFeetResetHold(AdvancedKeybindsConfig config, bool middleDown)
		{
			bool chordHeld = IsFeetResetKeyHeld() && middleDown;
			if (!chordHeld)
			{
				ClearFeetResetHold();
				return;
			}

			if (!_feetResetChordWasHeld)
			{
				_feetResetChordWasHeld = true;
				_feetResetHeldMs = 0;
				_feetResetFired = false;
				if (AkDiag.Enabled) AkDiag.Log("HoldHud feet-reset chord armed (P+Middle)");
			}

			SuppressDragInputs();
			if (Main.LocalPlayer != null)
				Main.LocalPlayer.mouseInterface = true;

			if (_feetResetFired)
				return;

			_feetResetHeldMs += Main.GameUpdateCount > 0 ? 1000.0 / 60.0 : 16.0;
			if (_feetResetHeldMs < FeetResetHoldMs)
				return;

			_feetResetFired = true;
			ResetCustomToPlayerHead(config, reason: "P+Middle-2s");
			SuppressDragInputs();
			_mouseOverBar = false;
		}

		private void SyncDragUiVisibility()
		{
			if (_dragUi == null || _dragUiState == null)
				return;

			// Formal spec: drag is Custom-only (pixel path). Never mount the Feet/Mouse UIElement
			// handle — that path used to commit → Anchor=Custom (promotion abolished 2026-07-22).
			bool want = false;

			if (want)
			{
				if (_dragUi.CurrentState == null)
					_dragUi.SetState(_dragUiState);
			}
			else if (_dragUi.CurrentState != null)
			{
				if (IsDragging && !_customPixelDragActive)
					CancelDragSilent();
				_dragUi.SetState(null);
			}
		}

		/// <summary>True when Custom HUD uses viewport pixel space (draw / hit / drag).</summary>
		internal static bool IsUsingCustomPixelSpace(AdvancedKeybindsConfig config)
			=> _customPixelDragActive
				|| (config != null && config.HoldTierHudAnchor == HoldTierHudAnchor.Custom);

		/// <summary>UIElement drag handle must stay inactive while Custom owns pixel hit-testing.</summary>
		internal static bool IsCustomPixelDragUiDisabled()
			=> IsUsingCustomPixelSpace(AdvancedKeybindsConfig.Instance);

		private static void SyncHandleToResolvedOrigin()
		{
			if (_dragHandle == null)
				return;

			AdvancedKeybindsConfig config = AdvancedKeybindsConfig.Instance;
			if (config == null)
				return;

			GetHandleLayoutMetrics(config, out Vector2 origin, out float barW, out float barH, out float padY);
			_dragHandle.SyncLayout(origin, barW, barH, padY);
		}

		private static void GetHandleLayoutMetrics(
			AdvancedKeybindsConfig config,
			out Vector2 origin,
			out float barW,
			out float barH,
			out float padY)
		{
			origin = ResolveOrigin(config);
			barW = ResolveBarWidthHud(config);
			barH = PreviewBarHeightHud;
			padY = PxToHud(UiDragHitPadYPx);
		}

		/// <summary>UI handle begin — abolished. Drag is Custom pixel-path only.</summary>
		public static bool TryBeginUiDrag(Vector2 barOrigin)
		{
			AdvancedKeybindsConfig config = AdvancedKeybindsConfig.Instance;
			#if AK_DIAGNOSTICS
			HoldHudProbe.LogDragCheck(
				"anchor-not-custom",
				false,
				"anchor=" + (config?.HoldTierHudAnchor.ToString() ?? "null")
				+ " path=TryBeginUiDrag-abolished"
				+ " originUi=" + FormatVec(barOrigin));
			#endif
			if (config != null && config.HoldTierHudAnchor != HoldTierHudAnchor.Custom)
			{
				#if AK_DIAGNOSTICS
				HoldHudProbe.Log(
					"ANOMALY feet-or-mouse-ui-drag-attempted anchor=" + config.HoldTierHudAnchor);
				#endif
			}
			return false;
		}

		/// <summary>UI handle follow — abolished (Custom uses pixel drag).</summary>
		public static void NotifyUiDragFollow(Vector2 desiredOrigin)
		{
			#if AK_DIAGNOSTICS
			HoldHudProbe.Log(
				"ANOMALY NotifyUiDragFollow-called-after-abolish desiredUi=" + FormatVec(desiredOrigin)
				+ " anchor=" + (AdvancedKeybindsConfig.Instance?.HoldTierHudAnchor.ToString() ?? "null"));
			#endif
		}

		/// <summary>
		/// When true, <see cref="ApplyClampedCustomPosition"/> is a no-op so SaveChanges→OnChanged
		/// cannot reclamps a just-committed drag (that was snapping back to the pre-drag spot).
		/// </summary>
		private static bool _suppressConfigClamp;

		/// <summary>Guards re-entrant <see cref="RecoverCustomPlacementIfWiped"/> during silent re-save.</summary>
		private static bool _recoveringPlacement;

		/// <summary>UI handle commit — abolished. Must not promote Feet/Mouse → Custom.</summary>
		public static void NotifyUiDragCommit()
		{
			AdvancedKeybindsConfig config = AdvancedKeybindsConfig.Instance;
			#if AK_DIAGNOSTICS
			HoldHudProbe.Log(
				"ANOMALY NotifyUiDragCommit-called-after-abolish"
				+ " anchor=" + (config?.HoldTierHudAnchor.ToString() ?? "null")
				+ " isDragging=" + IsDragging
				+ " preferCustom=" + ClientPrefs.HoldHudPreferCustom);
			#endif
			if (IsDragging && !_customPixelDragActive)
				RestorePreDragAndEnd(save: false, reason: "UiCommit-abolished");
		}

		public static void NotifyUiDragCancel()
		{
			if (!IsDragging)
				return;
			RestorePreDragAndEnd(save: true, reason: "UiCancel");
		}

		public static void SuppressDragInputsPublic() => SuppressDragInputs();

		private static string FormatRect(Rectangle r)
			=> "[" + r.X + "," + r.Y + " " + r.Width + "x" + r.Height + "]";

		/// <summary>
		/// Mouse in UI space — same as <see cref="Main.mouseX"/> after SetZoom_UI and the
		/// <see cref="Main.UIScaleMatrix"/> draw path. Focused: use Main.mouse directly.
		/// Unfocused: Main.mouse freezes, so map raw client pixels ÷ UIScale into UI space.
		/// (Approach A: never mix Identity/backbuffer hit rects with Main.mouse.)
		/// </summary>
		private static Vector2 GetHudMouse() => HoldHudCoords.GetMouseUi();

		/// <summary>UIScale used for HUD layout (never 0).</summary>
		private static float HudUiScale => HoldHudCoords.UiScale;

		/// <summary>Authored backbuffer-pixel constants → UI (Main.mouse) units.</summary>
		private static float PxToHud(float pixels) => HoldHudCoords.GamePxToUiScalar(pixels);

		private static int PxToHudI(int pixels)
			=> Math.Max(0, (int)Math.Round(HoldHudCoords.GamePxToUiScalar(pixels)));

		private static void LogFocusChangeIfNeeded(Vector2 hudMouse)
		{
			bool focus = Main.hasFocus;
			if (_hasPrevHasFocus && focus == _prevHasFocus)
				return;

			_hasPrevHasFocus = true;
			_prevHasFocus = focus;

			HudScreenInfo screen = GetHudScreenInfo();
			MouseState raw = Mouse.GetState();
			string hit = _hasLastDrawnLayout
				? FormatRect(_lastDrawnLayout.HitRect)
				: "n/a";
			if (AkDiag.Enabled)
			{
				AkDiag.Log(
				"HoldHud focus=" + focus
				+ " hudMouse=(" + hudMouse.X.ToString("0.#") + "," + hudMouse.Y.ToString("0.#") + ")"
				+ " mainMouse=(" + Main.mouseX + "," + Main.mouseY + ")"
				+ " piMouse=(" + PlayerInput.MouseX + "," + PlayerInput.MouseY + ")"
				+ " rawMouse=(" + raw.X + "," + raw.Y + ")"
				+ " hitRect=" + hit
				+ " UIScale=" + Main.UIScale.ToString("0.###")
				+ " " + screen.Format());
			}
		}

		private static float BarHeightForHit(bool allowDragPreview)
			=> PxToHud(allowDragPreview ? PreviewBarHeight : BarHeight);

		private static float PreviewBarHeightHud => PxToHud(PreviewBarHeight);

		private static float ScreenMarginHud => PxToHud(ScreenMargin);

		private static float ResolveBarWidthHud(AdvancedKeybindsConfig config = null)
		{
			config ??= AdvancedKeybindsConfig.Instance;
			float px = config != null ? MathHelper.Clamp(config.HoldTierHudBarWidth, 150, 500) : 200f;
			return PxToHud(px);
		}

		private static void GetHitPads(bool allowDrag, out int padX, out int padY)
		{
			if (!allowDrag)
			{
				padX = PxToHudI(HitPadX);
				padY = PxToHudI(HitPadY);
				return;
			}

			padX = PxToHudI(DragHitPadX);
			// PreviewBarHeight (14) + 2*padY >= DragMinHitHeight (120) → padY >= 53 (pixel space).
			int padYPx = Math.Max(DragHitPadY, (DragMinHitHeight - (int)PreviewBarHeight + 1) / 2);
			padY = PxToHudI(padYPx);
		}

		// ---------------------------------------------------------------------------
		// Layout invariants (single source of truth)
		//
		// 1. Custom / AllowDrag: free placement like other mod status panels. Origin may
		//    hang the bar off any edge; only MinVisiblePx of DrawRect must stay on-screen
		//    (or emergency center reset if clearly multi-screen off).
		// 2. HitRect always covers DrawRect. Ideal pad is padX/padY on each side; any
		//    outer overflow is clipped and redistributed to the inward side so the
		//    grab target stays large at edges (bar at edge, hit inward).
		// 3. Feet: soft layout-fit (ScreenMargin=0). Mouse / Custom: no draw-time
		//    placement clamp (free edges like QoL panels; Custom load may keep-visible).
		//
		// Coordinate space (Approach A): Main.UIScaleMatrix + Main.mouseX/Y UI units.
		// Bar pixel constants are converted via PxToHud. Normalized Custom is against UI size
		// (slightly outside 0–1 allowed when the bar hangs off-screen).
		// ---------------------------------------------------------------------------

		/// <summary>Draw + hit derived from one origin. Hit covers draw; pads bias inward at edges.</summary>
		private readonly struct HudBarLayout
		{
			public readonly Vector2 Origin;
			public readonly Rectangle DrawRect;
			public readonly Rectangle HitRect;

			public HudBarLayout(Vector2 origin, Rectangle drawRect, Rectangle hitRect)
			{
				Origin = origin;
				DrawRect = drawRect;
				HitRect = hitRect;
			}
		}

		/// <summary>
		/// origin = bar top-center. Hit covers the bar; pads that would leave the screen
		/// are clipped on the outside and added inward so edge re-grab stays reliable.
		/// </summary>
		private static HudBarLayout LayoutFromOrigin(
			Vector2 origin,
			float barWidth,
			float barHeight,
			int padX,
			int padY)
		{
			float halfW = Math.Max(0.5f, barWidth * 0.5f);
			int drawX = (int)Math.Floor(origin.X - halfW);
			int drawY = (int)Math.Floor(origin.Y);
			int drawW = Math.Max(1, (int)Math.Ceiling(barWidth));
			int drawH = Math.Max(1, (int)Math.Ceiling(barHeight));
			var drawRect = new Rectangle(drawX, drawY, drawW, drawH);
			GetHudScreenSize(out int sw, out int sh);
			Rectangle hitRect = MakeEdgeAwareHitRect(drawRect, padX, padY, sw, sh);
			return new HudBarLayout(origin, drawRect, hitRect);
		}

		private static HudBarLayout LayoutFromOrigin(Vector2 origin, float barWidth, float barHeight)
		{
			AdvancedKeybindsConfig config = AdvancedKeybindsConfig.Instance;
			bool allowDrag = config != null && config.HoldTierHudAllowDrag;
			GetHitPads(allowDrag, out int padX, out int padY);
			return LayoutFromOrigin(origin, barWidth, barHeight, padX, padY);
		}

		/// <summary>
		/// Build a hit rect that covers <paramref name="drawRect"/> with target pads, but
		/// never extends past the HUD screen. Clipped outer pad is redistributed inward.
		/// </summary>
		private static Rectangle MakeEdgeAwareHitRect(
			Rectangle drawRect,
			int padX,
			int padY,
			int sw,
			int sh)
		{
			padX = Math.Max(0, padX);
			padY = Math.Max(0, padY);
			if (sw <= 0 || sh <= 0)
			{
				return new Rectangle(
					drawRect.X - padX,
					drawRect.Y - padY,
					drawRect.Width + padX * 2,
					drawRect.Height + padY * 2);
			}

			int roomLeft = Math.Max(0, drawRect.X);
			int roomRight = Math.Max(0, sw - (drawRect.X + drawRect.Width));
			int roomTop = Math.Max(0, drawRect.Y);
			int roomBottom = Math.Max(0, sh - (drawRect.Y + drawRect.Height));

			int left = Math.Min(padX, roomLeft);
			int right = Math.Min(padX, roomRight);
			int top = Math.Min(padY, roomTop);
			int bottom = Math.Min(padY, roomBottom);

			int needX = padX * 2 - (left + right);
			if (needX > 0)
			{
				int addLeft = Math.Min(needX, roomLeft - left);
				left += addLeft;
				needX -= addLeft;
				if (needX > 0)
					right += Math.Min(needX, roomRight - right);
			}

			int needY = padY * 2 - (top + bottom);
			if (needY > 0)
			{
				int addTop = Math.Min(needY, roomTop - top);
				top += addTop;
				needY -= addTop;
				if (needY > 0)
					bottom += Math.Min(needY, roomBottom - bottom);
			}

			return new Rectangle(
				drawRect.X - left,
				drawRect.Y - top,
				drawRect.Width + left + right,
				drawRect.Height + top + bottom);
		}

		/// <summary>
		/// Feet/Mouse: keep the full draw bar on-screen (ScreenMargin is 0).
		/// Prefer <see cref="ClampOriginKeepPartiallyVisible"/> for Custom / drag.
		/// </summary>
		private static Vector2 ClampOriginSoLayoutFits(
			Vector2 origin,
			float barWidth,
			float barHeight,
			int sw,
			int sh)
		{
			if (sw <= 0 || sh <= 0)
				return new Vector2(400f, 300f);

			float halfW = Math.Max(0.5f, barWidth * 0.5f);
			float barH = Math.Max(1f, barHeight);
			GetDrawOriginClampBox(sw, sh, halfW, barH, out float minX, out float maxX, out float minY, out float maxY);
			return ClampOriginToBox(origin, minX, maxX, minY, maxY);
		}

		/// <summary>
		/// Custom free placement: allow hanging off edges; only prevent the draw bar from
		/// vanishing entirely. Absurd multi-screen coords emergency-reset to center.
		/// </summary>
		private static Vector2 ClampOriginKeepPartiallyVisible(
			Vector2 origin,
			float barWidth,
			float barHeight,
			int sw,
			int sh)
		{
			if (sw <= 0 || sh <= 0)
				return new Vector2(400f, 300f);

			if (float.IsNaN(origin.X) || float.IsNaN(origin.Y)
				|| float.IsInfinity(origin.X) || float.IsInfinity(origin.Y)
				|| IsCustomPositionUnset(origin)
				|| IsClearlyOffscreen(origin, sw, sh))
			{
				return DefaultCustomOriginForSize(sw, sh);
			}

			float halfW = Math.Max(0.5f, barWidth * 0.5f);
			float barH = Math.Max(1f, barHeight);
			float keep = PxToHud(MinVisiblePx);
			GetKeepVisibleOriginBox(sw, sh, halfW, barH, keep, out float minX, out float maxX, out float minY, out float maxY);
			return ClampOriginToBox(origin, minX, maxX, minY, maxY);
		}

		/// <summary>Placement box for bar top-center: DrawRect flush within ScreenMargin.</summary>
		private static void GetDrawOriginClampBox(
			int sw,
			int sh,
			float halfW,
			float barH,
			out float minX,
			out float maxX,
			out float minY,
			out float maxY)
		{
			float margin = ScreenMarginHud;
			minX = margin + halfW;
			maxX = sw - margin - halfW;
			minY = margin;
			maxY = sh - margin - barH;
		}

		/// <summary>

		/// Origin box so at least <paramref name="keep"/> px of DrawRect intersects the HUD.

		/// Top-center origin: left=X-halfW, top=Y, right=X+halfW, bottom=Y+barH.

		/// </summary>

		private static void GetKeepVisibleOriginBox(

			int sw,

			int sh,

			float halfW,

			float barH,

			float keep,

			out float minX,

			out float maxX,

			out float minY,

			out float maxY)

		{

			keep = Math.Max(1f, keep);

			minX = keep - halfW;

			maxX = sw - keep + halfW;

			minY = keep - barH;

			maxY = sh - keep;

		}


		/// <summary>
		/// AllowDrag recovery: snap Custom origin to the current player head (viewport pixels)
		/// and keep Anchor=Custom. Does not follow the player afterward.
		/// </summary>
		private static void ResetCustomToPlayerHead(AdvancedKeybindsConfig config, string reason)
		{
			if (config == null)
				return;

			if (!IsHudDragAllowed(config))
			{
				if (AkDiag.Enabled)
				{
					AkDiag.Log(
					"HoldHud reset custom-head REJECTED reason=" + reason
					+ " enabled=" + config.HoldTierHudEnabled
					+ " allowDrag=" + config.HoldTierHudAllowDrag
					+ " anchor=" + config.HoldTierHudAnchor);
				}
				return;
			}

			if (IsDragging || _customPixelDragActive)
			{
				IsDragging = false;
				_customPixelDragActive = false;
				_centerXSnapped = false;
				_hasDragFollowOrigin = false;
				_dragHandle?.CancelUiDrag();
			}

			config.HoldTierHudAnchor = HoldTierHudAnchor.Custom;
			ClientPrefs.SetHoldHudPreferCustom(true);

			if (!TryGetPlayerHeadCustomOriginPixel(
					config,
					out Vector2 headPixel,
					out Vector2 headWorld,
					out Vector2 headAfterMatrix,
					out Vector2 headViewport,
					out Vector2 beforeClamp,
					out string failReason))
			{
				if (AkDiag.Enabled)
				{
					AkDiag.Log(
					"HoldHud reset custom-head ABORTED reason=" + reason
					+ " fail=" + failReason
					+ " keptNorm=(" + config.HoldTierHudCustomPositionX.ToString("0.###") + ","
					+ config.HoldTierHudCustomPositionY.ToString("0.###") + ")"
					+ " UIScale=" + Main.UIScale.ToString("0.###"));
				}
				return;
			}

			GetCustomPixelSurfaceSize(out int vw, out int vh);
			float barW = ResolveCustomBarWidthPixel(config);
			float barH = ResolveCustomLayoutBarHeightPixel();
			Vector2 fitted = headPixel; // already clamped in TryGetPlayerHead
			StoreNormalizedCustomPosition(config, fitted, vw, vh, "ResetCustomHead");

			AlignTrackedCustomViewport("ResetCustomHead");
			_hasLastDrawnLayout = false;
			config.SaveHoldHudChangesSilent();

			float nx = config.HoldTierHudCustomPositionX;
			float ny = config.HoldTierHudCustomPositionY;
			Vector2 roundTrip = DenormalizeCustomPosition(nx, ny, vw, vh);
			#if AK_DIAGNOSTICS
			HoldHudProbe.LogCustomInit(
				reason: "explicit-reset-to-head",
				saved: true,
				playerTopWorld: headWorld,
				playerCenterWorld: Main.LocalPlayer?.Center,
				screenPosition: Main.screenPosition,
				worldToScreenGamePx: headAfterMatrix,
				barSize: new Vector2(barW, barH),
				headGap: CustomHeadGapPixel,
				beforeClampGamePx: beforeClamp,
				afterClampGamePx: fitted,
				surfaceW: vw,
				surfaceH: vh,
				surfaceSource: HoldHudCoords.GamePixelSurfaceSource,
				storedNorm: new Vector2(nx, ny),
				roundTripPx: roundTrip,
				roundTripErr: roundTrip - fitted);
			#endif

			Rectangle drawRect = GetCustomPixelDrawRect(fitted, barW, barH);
			GetCustomPixelHitPads(out int padX, out int padY);
			Rectangle hitRect = InflateHitRectPixel(drawRect, padX, padY);
			if (AkDiag.Enabled)
			{
				AkDiag.Log(
				"HoldHud reset custom-head reason=" + reason
				+ " anchor=" + config.HoldTierHudAnchor
				+ " preferCustom=" + ClientPrefs.HoldHudPreferCustom
				+ " headWorld=(" + headWorld.X.ToString("0.#") + "," + headWorld.Y.ToString("0.#") + ")"
				+ " afterMatrix=(" + headAfterMatrix.X.ToString("0.#") + "," + headAfterMatrix.Y.ToString("0.#") + ")"
				+ " afterViewport=(" + headViewport.X.ToString("0.#") + "," + headViewport.Y.ToString("0.#") + ")"
				+ " beforeClamp=(" + beforeClamp.X.ToString("0.#") + "," + beforeClamp.Y.ToString("0.#") + ")"
				+ " originPixel=(" + fitted.X.ToString("0.#") + "," + fitted.Y.ToString("0.#") + ")"
				+ " draw=" + FormatRect(drawRect)
				+ " hit=" + FormatRect(hitRect)
				+ " norm=(" + nx.ToString("0.###") + "," + ny.ToString("0.###") + ")"
				+ " viewport=" + vw + "x" + vh
				+ " screen=" + Main.screenWidth + "x" + Main.screenHeight
				+ " UIScale=" + Main.UIScale.ToString("0.###")
				+ " matrix=Identity");
			}
		}

		/// <summary>
		/// Config UI Anchor click / CycleAnchor selected Feet or Mouse — drop any in-progress
		/// drag so a pending Custom write cannot overwrite the user's explicit anchor choice.
		/// Clears PreferCustom so Recover will not force Anchor back to Custom.
		/// AllowDrag toggle alone must not call this.
		/// </summary>
		public static void NotifyPresetAnchorChosen(AdvancedKeybindsConfig config)
		{
			if (config == null)
				return;

			ClientPrefs.SetHoldHudPreferCustom(false);
			#if AK_DIAGNOSTICS
			HoldHudProbe.LogState("NotifyPresetAnchorChosen", config);
			#endif

			if (IsDragging)
			{
				// Discard drag edits; keep the preset anchor the config just applied.
				HoldTierHudAnchor keep = config.HoldTierHudAnchor;
				Vector2 keepCustom = new(
					config.HoldTierHudCustomPositionX,
					config.HoldTierHudCustomPositionY);
				RestorePreDragAndEnd(save: false, reason: "PresetAnchor");
				config.HoldTierHudAnchor = keep;
				config.HoldTierHudCustomPositionX = keepCustom.X;
				config.HoldTierHudCustomPositionY = keepCustom.Y;
			}

			_hasLastDrawnLayout = false;
		}

		/// <summary>
		/// Cycle hotkey / config UI entered Custom: PreferCustom on, keep prior spot, or seed from
		/// player head in game pixels when Custom coords are unset (-1,-1).
		/// </summary>
		public static void EnterCustomFromHotkey(AdvancedKeybindsConfig config)
		{
			if (config == null)
				return;

			config.HoldTierHudAnchor = HoldTierHudAnchor.Custom;
			ClientPrefs.SetHoldHudPreferCustom(true);

			if (IsDragging || _customPixelDragActive)
				CancelInFlightDragKeepPreferCustom(config);

			bool unset = IsCustomPositionUnset(
				new Vector2(config.HoldTierHudCustomPositionX, config.HoldTierHudCustomPositionY));
			if (unset && RecoverCustomPlacementIfWiped(config))
				unset = IsCustomPositionUnset(
					new Vector2(config.HoldTierHudCustomPositionX, config.HoldTierHudCustomPositionY));

			if (unset)
			{
				if (TrySeedCustomHeadIfUnset(config, "HotkeyEnterCustom", out _, logExistingValid: false))
				{
					// seeded + CUSTOM-INIT logged inside TrySeed
				}
				else if (AkDiag.Enabled)
				{
					AkDiag.Log(
					"HoldHud hotkey enter Custom keep unset (-1,-1)"
					+ " UIScale=" + Main.UIScale.ToString("0.###"));
				}
			}
			else
			{
				#if AK_DIAGNOSTICS
				HoldHudProbe.LogCustomInit(
					reason: "existing-valid-placement",
					saved: false,
					storedNorm: new Vector2(
						config.HoldTierHudCustomPositionX,
						config.HoldTierHudCustomPositionY));
				#endif
			}

			_hasLastDrawnLayout = false;
			if (AkDiag.Enabled)
			{
				AkDiag.Log(
				"HoldHud hotkey enter Custom preferCustom=" + ClientPrefs.HoldHudPreferCustom
				+ " enabled=" + config.HoldTierHudEnabled
				+ " allowDrag=" + config.HoldTierHudAllowDrag
				+ " custom=(" + config.HoldTierHudCustomPositionX.ToString("0.###") + ","
				+ config.HoldTierHudCustomPositionY.ToString("0.###") + ")"
				+ " seededUnset=" + unset
				+ " previewOk=" + IsHudDragAllowed(config)
				+ " dragOk=" + IsHudDragAllowed(config));
			}
		}

		/// <summary>Cancel in-flight drag without clearing PreferCustom (ModConfig may have wiped coords).</summary>
		public static void CancelInFlightDragKeepPreferCustom(AdvancedKeybindsConfig config)
		{
			if (config == null || (!IsDragging && !_customPixelDragActive))
				return;

			HoldTierHudAnchor keep = config.HoldTierHudAnchor;
			Vector2 keepCustom = new(
				config.HoldTierHudCustomPositionX,
				config.HoldTierHudCustomPositionY);
			RestorePreDragAndEnd(save: false, reason: "ConfigSave");
			config.HoldTierHudAnchor = keep;
			config.HoldTierHudCustomPositionX = keepCustom.X;
			config.HoldTierHudCustomPositionY = keepCustom.Y;
			_hasLastDrawnLayout = false;
		}

		private static void SuppressDragInputs()
		{
			Main.mouseLeft = false;
			Main.mouseLeftRelease = false;
			Main.mouseMiddle = false;
			Main.mouseMiddleRelease = false;

			TriggersSet current = PlayerInput.Triggers.Current;
			TriggersSet justPressed = PlayerInput.Triggers.JustPressed;
			TriggersSet justReleased = PlayerInput.Triggers.JustReleased;

			current.MouseLeft = false;
			justPressed.MouseLeft = false;
			justReleased.MouseLeft = false;
			current.MouseMiddle = false;
			justPressed.MouseMiddle = false;
			justReleased.MouseMiddle = false;

			for (int i = 0; i < 10; i++)
			{
				string key = "Hotbar" + (i + 1);
				if (current.KeyStatus.ContainsKey(key))
					current.KeyStatus[key] = false;
				if (justPressed.KeyStatus.ContainsKey(key))
					justPressed.KeyStatus[key] = false;
			}
		}

		/// <summary>
		/// Chat / sign / chest / AllowDrag-off: same as Esc — restore pre-drag pos+anchor and persist.
		/// </summary>
		private static void CancelDragSilent()
		{
			if (!IsDragging && !_customPixelDragActive)
				return;

			RestorePreDragAndEnd(save: true, reason: "CancelDragSilent");
			SuppressDragInputs();
		}

		private static void RestorePreDragAndEnd(bool save, string reason)
		{
			if (!IsDragging && !_customPixelDragActive)
				return;

			AdvancedKeybindsConfig config = AdvancedKeybindsConfig.Instance;
			if (config != null)
			{
				config.HoldTierHudCustomPositionX = _preDragCustomPos.X;
				config.HoldTierHudCustomPositionY = _preDragCustomPos.Y;
				config.HoldTierHudAnchor = _preDragAnchor;
				if (save)
					config.SaveHoldHudChangesSilent();
			}

			IsDragging = false;
			_customPixelDragActive = false;
			_centerXSnapped = false;
			_hasDragFollowOrigin = false;
			_dragHandle?.CancelUiDrag();
			if (AkDiag.Enabled)
			{
				AkDiag.Log(
				"HoldHud drag " + reason
				+ " restoreAnchor=" + _preDragAnchor
				+ " pos=(" + _preDragCustomPos.X.ToString("0.#")
				+ "," + _preDragCustomPos.Y.ToString("0.#") + ")");
			}

			if (config != null && config.HoldTierHudAnchor == HoldTierHudAnchor.Custom)
				AlignTrackedCustomViewport("PostDrag-" + reason);
			else if (config != null)
				AlignTrackedScreenToCurrent("PostDrag-" + reason);
		}

		/// <summary>
		/// Middle-drag follow (Feet/Mouse UI path only): may temporarily write absolute UI px
		/// into config fields until commit normalizes via viewport bridge.
		/// Custom pixel drag must not call this — it keeps coords in <see cref="_customDragOriginPixel"/>.
		/// </summary>
		private static void ApplyDragFollowPosition(AdvancedKeybindsConfig config, Vector2 desiredCenter)
		{
			if (_customPixelDragActive)
				return;

			if (float.IsNaN(desiredCenter.X) || float.IsNaN(desiredCenter.Y)
				|| float.IsInfinity(desiredCenter.X) || float.IsInfinity(desiredCenter.Y))
				return;

			config.HoldTierHudCustomPositionX = desiredCenter.X;
			config.HoldTierHudCustomPositionY = desiredCenter.Y;
		}

		/// <summary>
		/// When bar top-center X is within the magnet radius of UI width÷2, lock X to that
		/// midpoint (origin is top-center, so visual bar center == origin.X). Y is never modified.
		/// Center comes from the drag UI parent width when available so it shares the handle's
		/// coordinate space (avoids GetHudScreenSize vs Main.mouseX mismatches).
		/// </summary>
		private static Vector2 SnapOriginXToScreenCenter(Vector2 origin, out bool snapped)
		{
			snapped = false;
			if (float.IsNaN(origin.X) || float.IsNaN(origin.Y)
				|| float.IsInfinity(origin.X) || float.IsInfinity(origin.Y))
				return origin;

			float centerX = GetUiCenterX();
			float radius = GetCenterXSnapRadiusHud();
			if (radius < 1f || Math.Abs(origin.X - centerX) > radius)
				return origin;

			snapped = true;
			origin.X = centerX;
			return origin;
		}

		/// <summary>
		/// UI-space screen center X for the magnet. Prefer the drag UI parent width (same
		/// space as Left/Top / GetBarOrigin); fall back to GetHudScreenSize.
		/// </summary>
		private static float GetUiCenterX()
		{
			if (_dragHandle?.Parent != null)
			{
				float w = _dragHandle.Parent.GetDimensions().Width;
				if (w > 1f)
					return w * 0.5f;
			}

			GetHudScreenSize(out int sw, out _);
			return Math.Max(1, sw) * 0.5f;
		}

		private static float GetUiWidthForSnap()
		{
			if (_dragHandle?.Parent != null)
			{
				float w = _dragHandle.Parent.GetDimensions().Width;
				if (w > 1f)
					return w;
			}

			GetHudScreenSize(out int sw, out _);
			return Math.Max(1, sw);
		}

		/// <summary>
		/// Magnet half-width in UI units: max(authored px÷UIScale, fraction of UI width).
		/// </summary>
		private static float GetCenterXSnapRadiusHud()
			=> Math.Max(PxToHud(CenterXSnapRadiusPx), GetUiWidthForSnap() * CenterXSnapMinScreenFraction);

		private static void LogCenterXSnapChange(bool snapped, Vector2 desired, Vector2 origin)
		{
			if (snapped == _centerXSnapped)
				return;

			_centerXSnapped = snapped;
			if (AkDiag.Enabled)
			{
				AkDiag.Log(
				"HoldHud centerXSnap=" + (snapped ? "on" : "off")
				+ " desiredX=" + desired.X.ToString("0.#")
				+ " origin=(" + origin.X.ToString("0.#") + "," + origin.Y.ToString("0.#") + ")"
				+ " centerX=" + GetUiCenterX().ToString("0.#")
				+ " radiusHud=" + GetCenterXSnapRadiusHud().ToString("0.#"));
			}
		}

		/// <summary>
		/// After a drag commit, Custom is stored as normalized 0–1 against the current screen.
		/// Point the size tracker at that box so transient inflated sizes are not treated as settled.
		/// </summary>
		private static void AlignTrackedScreenToCurrent(string reason)
		{
			HudScreenInfo screen = GetHudScreenInfo();
			if (!IsStableHudScreen(screen.Width, screen.Height) || IsTransientInflatedHudSize(screen))
				return;

			if (_trackedScreenW == screen.Width && _trackedScreenH == screen.Height)
			{
				ClearScreenChangeCandidate();
				return;
			}

			if (AkDiag.Enabled)
			{
				AkDiag.Log(
				"HoldHud tracked align reason=" + reason
				+ " " + _trackedScreenW + "x" + _trackedScreenH
				+ " -> " + screen.Width + "x" + screen.Height
				+ " UIScale=" + Main.UIScale.ToString("0.###")
				+ " " + screen.Format());
			}

			_trackedScreenW = screen.Width;
			_trackedScreenH = screen.Height;
			ClearScreenChangeCandidate();
		}

		/// <summary>
		/// After a drag commit: store as screen-normalized fractions (no edge pull-back).
		/// </summary>
		private static void NormalizeCustomPositionForCommit(AdvancedKeybindsConfig config)
		{
			GetCustomPixelSurfaceSize(out int vw, out int vh);
			if (!IsStableHudScreen(vw, vh))
				return;

			Vector2 raw = new(config.HoldTierHudCustomPositionX, config.HoldTierHudCustomPositionY);
			if (IsCustomPositionUnset(raw) || IsStoredNormalized(raw.X, raw.Y))
				return;
			if (IsClearlyOffscreen(raw, vw, vh))
			{
				if (AkDiag.Enabled)
				{
					AkDiag.Log(
					"HoldHud commit-normalize ABORTED offscreen"
					+ " raw=(" + raw.X.ToString("0.#") + "," + raw.Y.ToString("0.#") + ")"
					+ " viewport=" + vw + "x" + vh);
				}
				return;
			}

			StoreNormalizedCustomPosition(config, raw, vw, vh, "commit");
		}

		/// <summary>
		/// Quietly migrate legacy absolute Custom px → normalized 0–1 when the client is stable.
		/// Does not Save — commit / OnChanged / Sync own persistence when needed.
		/// </summary>
			private static void NormalizeCustomPositionIfNeeded(AdvancedKeybindsConfig config)
		{
			if (IsDragging || _customPixelDragActive)
				return;
			// Only touch stored Custom when Custom is active (or AllowDrag may commit to it).
			if (config.HoldTierHudAnchor != HoldTierHudAnchor.Custom && !config.HoldTierHudAllowDrag)
				return;
			if (config.HoldTierHudAnchor != HoldTierHudAnchor.Custom
				&& config.HoldTierHudCustomPositionX < 0f
				&& config.HoldTierHudCustomPositionY < 0f)
				return;

			MigrateLegacyCustomPositionIfNeeded(config, persist: false);
		}

		public static void ApplyClampedCustomPosition(AdvancedKeybindsConfig config, Vector2 desiredStoredOrPx)
		{
			if (_suppressConfigClamp)
				return;

			// Drag path uses ApplyDragFollowPosition; keep this as a hard bypass if called anyway.
			if (IsDragging)
			{
				ApplyDragFollowPosition(config, desiredStoredOrPx);
				return;
			}

			float oldX = config.HoldTierHudCustomPositionX;
			float oldY = config.HoldTierHudCustomPositionY;

			// Custom storage is viewport-normalized (not UIScale / GetHudScreenSize).
			GetCustomPixelSurfaceSize(out int clampW, out int clampH);
			HudScreenInfo screen = GetHudScreenInfo();

			// Transient resize frames (monitor move / borderless) can report tiny sizes; writing
			// those clamps permanently corrupts Custom coords.
			if (!IsStableHudScreen(clampW, clampH))
			{
				uint nowSkip = Main.GameUpdateCount;
				if (nowSkip - _lastUnstableClampLogUpdate >= 60)
				{
					_lastUnstableClampLogUpdate = nowSkip;
					if (AkDiag.Enabled)
					{
						AkDiag.Log(
						"HoldHud clamp skipped unstable viewport desired=("
						+ desiredStoredOrPx.X.ToString("0.###") + "," + desiredStoredOrPx.Y.ToString("0.###")
						+ ") stored=(" + oldX.ToString("0.###") + "," + oldY.ToString("0.###")
						+ ") viewport=" + clampW + "x" + clampH
						+ " UIScale=" + Main.UIScale.ToString("0.###"));
					}
				}
				return;
			}

			// Size pending settle: do not rewrite normalized fractions against a transient box.
			if (IsStableHudScreen(_trackedCustomViewportW, _trackedCustomViewportH)
				&& (clampW != _trackedCustomViewportW || clampH != _trackedCustomViewportH))
			{
				uint nowMis = Main.GameUpdateCount;
				if (nowMis - _lastMismatchClampLogUpdate >= 60)
				{
					_lastMismatchClampLogUpdate = nowMis;
					if (AkDiag.Enabled)
					{
						AkDiag.Log(
						"HoldHud clamp deferred viewport-mismatch tracked="
						+ _trackedCustomViewportW + "x" + _trackedCustomViewportH
						+ " desired=(" + desiredStoredOrPx.X.ToString("0.###") + ","
						+ desiredStoredOrPx.Y.ToString("0.###") + ") UIScale="
						+ Main.UIScale.ToString("0.###") + " viewport=" + clampW + "x" + clampH);
					}
				}
				return;
			}

			if (IsCustomPositionUnset(desiredStoredOrPx))
				return;

			// Already normalized (e.g. just-committed drag): do not keep-visible reclamps —
			// that was pulling the bar back after release.
			if (IsStoredNormalized(desiredStoredOrPx.X, desiredStoredOrPx.Y))
				return;

			if (!IsStoredLegacyAbsolute(desiredStoredOrPx.X, desiredStoredOrPx.Y))
				return;

			Vector2 pixel = desiredStoredOrPx;
			if (IsClearlyOffscreen(pixel, clampW, clampH))
			{
				if (AkDiag.Enabled)
				{
					AkDiag.Log(
					"HoldHud clamp-migrate ABORTED offscreen"
					+ " pixel=(" + pixel.X.ToString("0.#") + "," + pixel.Y.ToString("0.#") + ")"
					+ " viewport=" + clampW + "x" + clampH
					+ " UIScale=" + Main.UIScale.ToString("0.###"));
				}
				return;
			}

			StoreNormalizedCustomPosition(config, pixel, clampW, clampH, "clamp-migrate");

			uint now = Main.GameUpdateCount;
			if (now - _lastClampLogUpdate >= 30)
			{
				_lastClampLogUpdate = now;
				if (AkDiag.Enabled)
				{
					AkDiag.Log(
					"HoldHud clamp-migrate"
					+ " pixel=(" + pixel.X.ToString("0.#") + "," + pixel.Y.ToString("0.#")
					+ ") stored=(" + oldX.ToString("0.###") + "," + oldY.ToString("0.###")
					+ ") -> norm=("
					+ config.HoldTierHudCustomPositionX.ToString("0.###") + ","
					+ config.HoldTierHudCustomPositionY.ToString("0.###")
					+ ") viewport=" + clampW + "x" + clampH
					+ " UIScale=" + Main.UIScale.ToString("0.###")
					+ " " + screen.Format());
				}
			}
		}

		private static bool IsStableHudScreen(int w, int h)
			=> w >= MinStableScreenW && h >= MinStableScreenH;

		/// <summary>
		/// Tracks stable HUD screen size and migrates legacy absolute Custom → normalized 0–1.
		/// Normalized storage does not need absolute px remapping on resize — denormalize at draw.
		/// </summary>
		private static void SyncCustomPositionToScreenSize(AdvancedKeybindsConfig config, string reason)
		{
			if (IsDragging || _customPixelDragActive)
				return;

			if (Main.screenBorderlessPendingResizes > 0)
			{
				ClearScreenChangeCandidate();
				uint nowPend = Main.GameUpdateCount;
				if (nowPend - _lastTransientRejectLogUpdate >= 60)
				{
					_lastTransientRejectLogUpdate = nowPend;
					HudScreenInfo pendingScreen = GetHudScreenInfo();
					if (AkDiag.Enabled)
					{
						AkDiag.Log(
						"HoldHud sync deferred borderless-pending=" + Main.screenBorderlessPendingResizes
						+ " reason=" + reason
						+ " tracked=" + _trackedScreenW + "x" + _trackedScreenH
						+ " UIScale=" + Main.UIScale.ToString("0.###")
						+ " " + pendingScreen.Format());
					}
				}
				return;
			}

			HudScreenInfo screen = GetHudScreenInfo();
			int newW;
			int newH;
			if (config.HoldTierHudAnchor == HoldTierHudAnchor.Custom)
				GetCustomPixelSurfaceSize(out newW, out newH);
			else
			{
				newW = screen.Width;
				newH = screen.Height;
			}

			if (!IsStableHudScreen(newW, newH))
			{
				ClearScreenChangeCandidate();
				return;
			}

			if (IsTransientInflatedHudSize(screen)
				&& config.HoldTierHudAnchor != HoldTierHudAnchor.Custom)
			{
				ClearScreenChangeCandidate();
				uint nowRej = Main.GameUpdateCount;
				if (nowRej - _lastTransientRejectLogUpdate >= 60)
				{
					_lastTransientRejectLogUpdate = nowRej;
					if (AkDiag.Enabled)
					{
						AkDiag.Log(
						"HoldHud sync rejected transient-inflated reason=" + reason
						+ " tracked=" + _trackedScreenW + "x" + _trackedScreenH
						+ " UIScale=" + Main.UIScale.ToString("0.###")
						+ " " + screen.Format());
					}
				}
				return;
			}

			if (_trackedScreenW <= 0 || _trackedScreenH <= 0)
			{
				_trackedScreenW = newW;
				_trackedScreenH = newH;
				ClearScreenChangeCandidate();
				if (config.HoldTierHudAnchor == HoldTierHudAnchor.Custom)
					MigrateLegacyCustomPositionIfNeeded(config, persist: true);
				return;
			}

			if (_trackedScreenW == newW && _trackedScreenH == newH)
			{
				ClearScreenChangeCandidate();
				if (config.HoldTierHudAnchor == HoldTierHudAnchor.Custom)
					MigrateLegacyCustomPositionIfNeeded(config, persist: true);
				return;
			}

			// Debounce: PostUpdateInput vs PostDrawInterface (and Notify) can briefly disagree
			// on Main.screenWidth during borderless monitor moves.
			if (newW != _candidateScreenW || newH != _candidateScreenH)
			{
				_candidateScreenW = newW;
				_candidateScreenH = newH;
				_candidateStableFrames = 1;
				_candidateReason = reason;
				uint nowCand = Main.GameUpdateCount;
				if (nowCand - _lastCandidateLogUpdate >= 30)
				{
					_lastCandidateLogUpdate = nowCand;
					if (AkDiag.Enabled)
					{
						AkDiag.Log(
						"HoldHud screen candidate reason=" + reason
						+ " " + _trackedScreenW + "x" + _trackedScreenH
						+ " -> " + newW + "x" + newH
						+ " (need " + ScreenChangeConfirmFrames + " frames) "
						+ "UIScale=" + Main.UIScale.ToString("0.###") + " "
						+ screen.Format());
					}
				}
				return;
			}

			_candidateStableFrames++;
			if (!string.IsNullOrEmpty(reason)
				&& reason.StartsWith("Notify:", StringComparison.Ordinal))
				_candidateReason = reason;

			if (_candidateStableFrames < ScreenChangeConfirmFrames)
				return;

			string commitReason = string.IsNullOrEmpty(_candidateReason) ? reason : _candidateReason;
			ClearScreenChangeCandidate();

			int oldW = _trackedScreenW;
			int oldH = _trackedScreenH;
			_trackedScreenW = newW;
			_trackedScreenH = newH;
			if (config.HoldTierHudAnchor == HoldTierHudAnchor.Custom)
			{
				_trackedCustomViewportW = newW;
				_trackedCustomViewportH = newH;
			}

			float rawX = config.HoldTierHudCustomPositionX;
			float rawY = config.HoldTierHudCustomPositionY;

			if (AkDiag.Enabled)
			{
				AkDiag.Log(
				"HoldHud screen change reason=" + commitReason
				+ " " + oldW + "x" + oldH + " -> " + newW + "x" + newH
				+ " stored=(" + rawX.ToString("0.###") + "," + rawY.ToString("0.###")
				+ ") anchor=" + config.HoldTierHudAnchor
				+ " UIScale=" + Main.UIScale.ToString("0.###")
				+ " " + screen.Format());
			}

			// Normalized storage: no absolute remap. Migrate leftover legacy absolute if any.
			if (config.HoldTierHudAnchor == HoldTierHudAnchor.Custom)
				MigrateLegacyCustomPositionIfNeeded(config, persist: true);
		}

		/// <summary>
		/// True when Main.screen* is a transient monitor-native / Device-Reset size that must
		/// not be committed for Custom remap. Two signals (either is enough):
		/// 1) Main==BackBuffer while UIScale implies a clearly smaller logical client.
		/// 2) Main==BackBuffer and larger than an already-tracked stable size (UIScale may be
		///    ~1.0 right after SetResolution — that used to bypass signal 1 and corrupt coords).
		/// </summary>
		private static bool IsTransientInflatedHudSize(HudScreenInfo screen)
		{
			if (screen.BackBufferW < MinStableScreenW || screen.BackBufferH < MinStableScreenH)
				return false;
			if (screen.MainW != screen.BackBufferW || screen.MainH != screen.BackBufferH)
				return false;

			float uiScale = Main.UIScale;
			if (uiScale > 1.01f)
			{
				int logicalW = (int)Math.Round(screen.BackBufferW / uiScale);
				int logicalH = (int)Math.Round(screen.BackBufferH / uiScale);
				if (IsStableHudScreen(logicalW, logicalH)
					&& logicalW + 16 < screen.MainW
					&& logicalH + 16 < screen.MainH)
					return true;
			}

			// SetResolution often reports UIScale≈1 while Main still equals the new monitor bb.
			if (IsStableHudScreen(_trackedScreenW, _trackedScreenH)
				&& (screen.MainW > _trackedScreenW + 16 || screen.MainH > _trackedScreenH + 16))
				return true;

			return false;
		}

		/// <summary>
		/// After reload / init, legacy absolute Custom px may still be in a previous buffer-sized
		/// space while Main.screen* is the logical client. Rescale when raw fits the backbuffer
		/// but exceeds the current clamp box. Normalized 0–1 storage is left untouched.
		/// </summary>
		private static Vector2 TryRecoverCoordsFromMismatchedSpace(Vector2 raw, HudScreenInfo screen)
		{
			if (IsCustomPositionUnset(raw) || IsStoredNormalized(raw.X, raw.Y))
				return raw;

			int mainW = screen.Width;
			int mainH = screen.Height;
			float maxX = mainW - ScreenMargin;
			float maxY = mainH - ScreenMargin;
			bool exceedsMain = raw.X > maxX + 1f || raw.Y > maxY + 1f || raw.X < -1f || raw.Y < -1f;
			if (!exceedsMain)
				return raw;

			int bbW = screen.BackBufferW;
			int bbH = screen.BackBufferH;
			if (bbW > mainW + 16 && bbH > mainH + 16
				&& raw.X >= -bbW && raw.Y >= -bbH
				&& raw.X <= bbW * 1.05f && raw.Y <= bbH * 1.05f)
			{
				return ClampBarCenter(
					new Vector2(raw.X * mainW / (float)bbW, raw.Y * mainH / (float)bbH),
					mainW,
					mainH);
			}

			// Oversized vs current main with no usable bb hint: keep relative placement via clamp
			// only if within 2× (IsClearlyOffscreen handles worse cases).
			if (!IsClearlyOffscreen(raw, mainW, mainH))
				return ClampBarCenter(raw, mainW, mainH);

			return raw;
		}

		private static void ClearScreenChangeCandidate()
		{
			_candidateScreenW = 0;
			_candidateScreenH = 0;
			_candidateStableFrames = 0;
			_candidateReason = null;
		}

		/// <summary>
		/// True when Custom px is absurd relative to the client (several screens away),
		/// not merely a few pixels past the margin.
		/// </summary>
		private static bool IsClearlyOffscreen(Vector2 center, int sw, int sh)
		{
			if (IsCustomPositionUnset(center))
				return false;
			if (float.IsNaN(center.X) || float.IsNaN(center.Y)
				|| float.IsInfinity(center.X) || float.IsInfinity(center.Y))
				return true;

			float maxX = sw * 2f;
			float maxY = sh * 2f;
			return center.X < -sw || center.Y < -sh || center.X > maxX || center.Y > maxY;
		}

		private static Vector2 DefaultCustomOriginForSize(int sw, int sh)
			=> new(sw * 0.5f, sh * 0.7f);

		/// <summary>
		/// Drawable HUD size in the same pixel space as <see cref="Main.mouseX"/> /
		/// <see cref="GetFeetHudOrigin"/> / Identity draw. Clamp bounds use
		/// <see cref="Main.screenWidth"/> / <see cref="Main.screenHeight"/> only —
		/// BackBuffer/Viewport can be larger than the visible game view (borderless /
		/// multi-monitor) and must not inflate the clamp box.
		/// Viewport and BackBuffer sizes are still captured for diagnostic logs.
		/// </summary>
		private static HudScreenInfo GetHudScreenInfo()
		{
			int mainW = Math.Max(1, Main.screenWidth);
			int mainH = Math.Max(1, Main.screenHeight);
			int vpW = 0;
			int vpH = 0;
			int bbW = 0;
			int bbH = 0;

			try
			{
				var gd = Main.instance?.GraphicsDevice;
				if (gd != null)
				{
					var vp = gd.Viewport;
					vpW = vp.Width;
					vpH = vp.Height;

					var pp = gd.PresentationParameters;
					bbW = pp.BackBufferWidth;
					bbH = pp.BackBufferHeight;
				}
			}
			catch
			{
				// Keep Main.screen* fallback.
			}

			return new HudScreenInfo(mainW, mainH, vpW, vpH, bbW, bbH, mainW, mainH);
		}

		/// <summary>
		/// Drawable HUD size in UI (Main.mouse / UIScaleMatrix) space.
		/// When SetZoom_UI has already shrunk <see cref="Main.screenWidth"/> to UI units,
		/// do NOT divide by UIScale again (that was collapsing the clamp box and skewing
		/// Feet/Mouse layout during interface frames — probe uiZoomPolluted=True).
		/// </summary>
		private static void GetHudScreenSize(out int width, out int height)
		{
			HudScreenInfo info = GetHudScreenInfo();
			if (HoldHudCoords.IsUiZoomScreenPolluted())
			{
				width = Math.Max(1, info.Width);
				height = Math.Max(1, info.Height);
				return;
			}

			float ui = HudUiScale;
			width = Math.Max(1, (int)Math.Round(info.Width / ui));
			height = Math.Max(1, (int)Math.Round(info.Height / ui));
		}

		/// <summary>
		/// Draw transform for bar positions in UI space (same as <see cref="Main.mouseX"/> /
		/// hit tests / drag follow). Must stay <see cref="Main.UIScaleMatrix"/> — Identity
		/// left the visible bar in backbuffer pixels while focused hits used ÷UIScale mouse,
		/// so the center bar only grabbed near the bottom-right.
		/// </summary>
		private static Matrix GetHudDrawMatrix()
			=> Main.UIScaleMatrix;

		private static bool IsCustomPositionUnset(Vector2 center)
			=> center.X == CustomPositionUnset && center.Y == CustomPositionUnset;

		/// <summary>One axis unset, NaN/Inf, or outside normalized store band — not a usable placement.</summary>
		private static bool IsCustomPositionCorrupt(float x, float y)
		{
			if (float.IsNaN(x) || float.IsNaN(y) || float.IsInfinity(x) || float.IsInfinity(y))
				return true;
			if (IsCustomPositionUnset(new Vector2(x, y)))
				return false;
			// Partial unset (-1, y) or (x, -1)
			if (x == CustomPositionUnset || y == CustomPositionUnset)
				return true;
			return !IsStoredNormalized(x, y) && !IsStoredLegacyAbsolute(x, y);
		}

		private static bool IsCustomPositionNeedsRepair(float x, float y)
			=> IsCustomPositionUnset(new Vector2(x, y)) || IsCustomPositionCorrupt(x, y);

		/// <summary>Stored Custom is screen-normalized (both axes near 0–1; slight overshoot OK). Sentinel (-1,-1) is unset.</summary>
		private static bool IsStoredNormalized(float x, float y)
		{
			if (IsCustomPositionUnset(new Vector2(x, y)))
				return false;
			return x >= NormStoreMin && x <= NormStoreMax
				&& y >= NormStoreMin && y <= NormStoreMax;
		}

		/// <summary>Legacy absolute px: not unset and not in the normalized fraction band.</summary>
		private static bool IsStoredLegacyAbsolute(float x, float y)
		{
			if (IsCustomPositionUnset(new Vector2(x, y)))
				return false;
			return !IsStoredNormalized(x, y);
		}

		private static Vector2 DenormalizeCustomPosition(float nx, float ny, int sw, int sh)
			=> HoldHudCoords.DenormalizeToGamePx(nx, ny, sw, sh);

		/// <summary>
		/// Persist Custom as game-pixel-normalized fractions. Slightly outside 0–1 is allowed when
		/// the bar hangs off an edge (keep-visible). Hard-clamped only to NormStore* sanity.
		/// Always round-trip validates inputGamePx → norm → restoredGamePx.
		/// </summary>
		private static void StoreNormalizedCustomPosition(
			AdvancedKeybindsConfig config,
			Vector2 inputGamePx,
			int gamePixelSurfaceW,
			int gamePixelSurfaceH,
			string reason)
		{
			Vector2 customNorm = HoldHudCoords.NormalizeGamePx(inputGamePx, gamePixelSurfaceW, gamePixelSurfaceH);
			float nx = MathHelper.Clamp(customNorm.X, NormStoreMin, NormStoreMax);
			float ny = MathHelper.Clamp(customNorm.Y, NormStoreMin, NormStoreMax);
			float oldX = config.HoldTierHudCustomPositionX;
			float oldY = config.HoldTierHudCustomPositionY;
			config.HoldTierHudCustomPositionX = nx;
			config.HoldTierHudCustomPositionY = ny;
			// PreferCustom ownership:
			// - Intentional Custom placement (drag commit / hotkey enter / head reset / unset init)
			//   must set PreferCustom=true.
			// - Passive/normalize/clamp must NOT flip PreferCustom back to true after Feet/Mouse
			//   (that was forcing Recover → Custom and made the cycle hotkey look "stuck on Custom").
			bool intentionalCustom =
				reason.StartsWith("custom-pixel-commit", StringComparison.Ordinal)
				|| reason.StartsWith("commit-from-ui", StringComparison.Ordinal)
				|| reason.StartsWith("HotkeyEnterCustom", StringComparison.Ordinal)
				|| reason.StartsWith("ResetCustomHead", StringComparison.Ordinal)
				|| reason.StartsWith("CustomUnsetInit", StringComparison.Ordinal)
				|| reason.StartsWith("CustomRepairInit", StringComparison.Ordinal);
			if (intentionalCustom)
				ClientPrefs.SetHoldHudCustomPlacement(nx, ny, preferCustom: true);
			else
				ClientPrefs.SetHoldHudCustomPlacement(nx, ny, preferCustom: null);

			#if AK_DIAGNOSTICS
			HoldHudProbe.Log(
				"STORE norm reason=" + reason
				+ " inputGamePx=(" + inputGamePx.X.ToString("0.#") + "," + inputGamePx.Y.ToString("0.#") + ")"
				+ " -> customNorm=(" + nx.ToString("0.###") + "," + ny.ToString("0.###") + ")"
				+ " gamePixelSurface=" + gamePixelSurfaceW + "x" + gamePixelSurfaceH
				+ " source=" + HoldHudCoords.GamePixelSurfaceSource
				+ " intentionalPrefer=" + intentionalCustom
				+ " preferNow=" + ClientPrefs.HoldHudPreferCustom
				+ " anchor=" + config.HoldTierHudAnchor);
			#endif

			HoldHudCoords.ValidateRoundTrip(
				reason, inputGamePx, nx, ny, gamePixelSurfaceW, gamePixelSurfaceH);

			if (Math.Abs(oldX - nx) > 0.0005f || Math.Abs(oldY - ny) > 0.0005f
				|| !IsStoredNormalized(oldX, oldY))
			{
				if (AkDiag.Enabled)
				{
					AkDiag.Log(
					"HoldHud normalize reason=" + reason
					+ " inputGamePx=(" + inputGamePx.X.ToString("0.#") + "," + inputGamePx.Y.ToString("0.#")
					+ ") -> customNorm=(" + nx.ToString("0.###") + "," + ny.ToString("0.###")
					+ ") surface=" + gamePixelSurfaceW + "x" + gamePixelSurfaceH
					+ " prev=(" + oldX.ToString("0.###") + "," + oldY.ToString("0.###") + ")");
				}
			}
		}

		/// <summary>
		/// ModConfig UI uses <see cref="HiddenConfigElement"/> for Custom X/Y; tML
		/// <c>DefaultValueHandling.IgnoreAndPopulate</c> can reset those floats to -1 and/or
		/// Anchor to Feet on Accept. <see cref="ClientPrefs"/> is the source of truth for the
		/// last drag-committed spot. PreferCustom distinguishes intentional Feet/Mouse.
		/// When PreferCustom, prefs apply even if coords were only partially wiped.
		/// Returns true when config fields were changed (caller may re-save).
		/// </summary>
		public static bool RecoverCustomPlacementIfWiped(AdvancedKeybindsConfig config)
		{
			if (config == null || IsDragging || _recoveringPlacement)
				return false;

			if (!ClientPrefs.TryGetHoldHudCustomPlacement(out float nx, out float ny)
				|| !IsStoredNormalized(nx, ny))
				return false;

			float cx = config.HoldTierHudCustomPositionX;
			float cy = config.HoldTierHudCustomPositionY;
			bool unset = IsCustomPositionUnset(new Vector2(cx, cy));
			bool corrupt = IsCustomPositionCorrupt(cx, cy);
			bool prefer = ClientPrefs.HoldHudPreferCustom;
			bool changed = false;

			if (prefer || corrupt)
			{
				// Prefs win for PreferCustom, and for any corrupt/partial/NaN config values.
				if (Math.Abs(cx - nx) > 0.0005f || Math.Abs(cy - ny) > 0.0005f)
				{
					config.HoldTierHudCustomPositionX = nx;
					config.HoldTierHudCustomPositionY = ny;
					changed = true;
				}

				if (prefer && config.HoldTierHudAnchor != HoldTierHudAnchor.Custom)
				{
					#if AK_DIAGNOSTICS
					HoldHudProbe.Log(
						"RECOVER force-Custom was=" + config.HoldTierHudAnchor
						+ " prefer=true corrupt=" + corrupt);
					#endif
					config.HoldTierHudAnchor = HoldTierHudAnchor.Custom;
					changed = true;
				}
			}
			else if (unset)
			{
				// Keep leftover coords for a later drag, but do not force Custom over Feet/Mouse.
				config.HoldTierHudCustomPositionX = nx;
				config.HoldTierHudCustomPositionY = ny;
				changed = true;
			}

			if (changed)
			{
				if (AkDiag.Enabled)
				{
					AkDiag.Log(
					"HoldHud recover from prefs norm=("
					+ nx.ToString("0.###") + "," + ny.ToString("0.###")
					+ ") anchor=" + config.HoldTierHudAnchor
					+ " prefer=" + prefer
					+ " wasUnset=" + unset
					+ " wasCorrupt=" + corrupt);
				}
			}

			return changed;
		}

		private static bool _hasLoggedHoldHudConfigState;
		private static HoldTierHudAnchor _loggedHoldHudAnchor;
		private static float _loggedHoldHudX;
		private static float _loggedHoldHudY;
		private static bool _loggedHoldHudPrefer;
		private static bool _loggedHoldHudAllowDrag;

		/// <summary>
		/// State-change only (Accept / OnLoaded / OnChanged). Logs Anchor/X/Y/PreferCustom/AllowDrag.
		/// </summary>
		public static void LogHoldHudConfigStateIfChanged(AdvancedKeybindsConfig config, string phase)
		{
			if (config == null)
				return;

			bool prefer = ClientPrefs.HoldHudPreferCustom;
			bool allowDrag = config.HoldTierHudAllowDrag;
			if (_hasLoggedHoldHudConfigState
				&& _loggedHoldHudAnchor == config.HoldTierHudAnchor
				&& Math.Abs(_loggedHoldHudX - config.HoldTierHudCustomPositionX) < 0.0005f
				&& Math.Abs(_loggedHoldHudY - config.HoldTierHudCustomPositionY) < 0.0005f
				&& _loggedHoldHudPrefer == prefer
				&& _loggedHoldHudAllowDrag == allowDrag)
			{
				return;
			}

			_hasLoggedHoldHudConfigState = true;
			_loggedHoldHudAnchor = config.HoldTierHudAnchor;
			_loggedHoldHudX = config.HoldTierHudCustomPositionX;
			_loggedHoldHudY = config.HoldTierHudCustomPositionY;
			_loggedHoldHudPrefer = prefer;
			_loggedHoldHudAllowDrag = allowDrag;

			HoldHudCoords.LogPhaseSnapshot(phase, config);

			if (AkDiag.Enabled)
			{
				AkDiag.Log(
				"HoldHud config " + phase
				+ " anchor=" + config.HoldTierHudAnchor
				+ " xy=(" + config.HoldTierHudCustomPositionX.ToString("0.###")
				+ "," + config.HoldTierHudCustomPositionY.ToString("0.###") + ")"
				+ " prefer=" + prefer
				+ " allowDrag=" + allowDrag);
			}
		}

		/// <summary>
		/// After ModConfig Accept wrote wiped values to disk, re-persist recovered Custom fields.
		/// </summary>
		public static void PersistRecoveredPlacement(AdvancedKeybindsConfig config)
		{
			if (config == null || _recoveringPlacement)
				return;

			_recoveringPlacement = true;
			_suppressConfigClamp = true;
			try
			{
				config.SaveHoldHudChangesSilent();
			}
			finally
			{
				_suppressConfigClamp = false;
				_recoveringPlacement = false;
			}
		}

		/// <summary>
		/// Convert legacy absolute Custom px to normalized 0–1 using Viewport size (not UIScale).
		/// Clearly-offscreen → leave unchanged and log (no fake center). Returns true if changed.
		/// </summary>
		private static bool MigrateLegacyCustomPositionIfNeeded(AdvancedKeybindsConfig config, bool persist)
		{
			if (config == null || IsDragging || _customPixelDragActive)
				return false;

			float x = config.HoldTierHudCustomPositionX;
			float y = config.HoldTierHudCustomPositionY;
			if (!IsStoredLegacyAbsolute(x, y))
				return false;

			GetCustomPixelSurfaceSize(out int vw, out int vh);
			if (!IsStableHudScreen(vw, vh))
				return false;

			Vector2 raw = new(x, y);
			if (IsClearlyOffscreen(raw, vw, vh))
			{
				if (AkDiag.Enabled)
				{
					AkDiag.Log(
					"HoldHud migrate-absolute ABORTED clearly-offscreen"
					+ " abs=(" + x.ToString("0.#") + "," + y.ToString("0.#") + ")"
					+ " viewport=" + vw + "x" + vh
					+ " UIScale=" + Main.UIScale.ToString("0.###"));
				}
				return false;
			}

			float nx = MathHelper.Clamp(raw.X / vw, NormStoreMin, NormStoreMax);
			float ny = MathHelper.Clamp(raw.Y / vh, NormStoreMin, NormStoreMax);
			config.HoldTierHudCustomPositionX = nx;
			config.HoldTierHudCustomPositionY = ny;
			if (persist)
				config.SaveHoldHudChangesSilent();

			if (AkDiag.Enabled)
			{
				AkDiag.Log(
				"HoldHud migrate-absolute"
				+ " abs=(" + x.ToString("0.#") + "," + y.ToString("0.#")
				+ ") -> norm=(" + nx.ToString("0.###") + "," + ny.ToString("0.###")
				+ ") viewport=" + vw + "x" + vh
				+ " persist=" + persist
				+ " UIScale=" + Main.UIScale.ToString("0.###"));
			}
			return true;
		}

		private static Vector2 ClampOriginToBox(
			Vector2 origin,
			float minX,
			float maxX,
			float minY,
			float maxY)
		{
			if (float.IsNaN(origin.X) || float.IsNaN(origin.Y)
				|| float.IsInfinity(origin.X) || float.IsInfinity(origin.Y)
				|| IsCustomPositionUnset(origin))
			{
				origin = new Vector2((minX + maxX) * 0.5f, minY + (maxY - minY) * 0.7f);
			}

			if (minX > maxX)
				origin.X = (minX + maxX) * 0.5f;
			else
				origin.X = MathHelper.Clamp(origin.X, minX, maxX);

			if (minY > maxY)
				origin.Y = minY + (maxY - minY) * 0.7f;
			else
				origin.Y = MathHelper.Clamp(origin.Y, minY, maxY);

			return origin;
		}

		private static float ResolveClampBarWidth()
			=> ResolveBarWidthHud();

		/// <summary>
		/// Shared clamp for Custom drag commit / load / ResolveOrigin(Custom).
		/// Keep-visible only — not full-bar layout-fit.
		/// </summary>
		private static Vector2 ClampBarCenter(Vector2 center, int sw, int sh)
			=> ClampOriginKeepPartiallyVisible(center, ResolveClampBarWidth(), PreviewBarHeightHud, sw, sh);

		// ---------------------------------------------------------------------------
		// Draw — Main.mouseX UI units + Main.UIScaleMatrix (hit == visual).
		// ---------------------------------------------------------------------------

		public override void PostDrawInterface(SpriteBatch spriteBatch)
		{
			#if AK_DIAGNOSTICS
			AkCpuMeasureSystem.Begin(AkCpuMeasureSystem.IdHoldHudPostDraw);
			#endif
			try
			{
			if (Main.gameMenu || Main.LocalPlayer == null || !Main.LocalPlayer.active)
				return;

			AdvancedKeybindsConfig config = AdvancedKeybindsConfig.Instance;
			if (config == null || !IsHudFeatureEnabled(config))
				return;

			BindingInputEngine.CollectActiveReleaseHoldHudGroups(ActiveGroups);

			bool needDraw = false;
			foreach (ReleaseHoldHudGroup group in ActiveGroups)
			{
				if (config.ShouldShowHoldTierHud(group.Tiers.Count) && WouldDrawGroup(group))
				{
					needDraw = true;
					break;
				}
			}

			if (!needDraw && !IsHudDragAllowed(config))
			{
				_previousSelectedIndex = -1;
				_previousKeyToken = null;
				return;
			}

			bool customPixel = IsUsingCustomPixelSpace(config);
			HoldHudCoords.LogPhaseSnapshot("PostDrawInterface", config);
			#if AK_DIAGNOSTICS
			HoldHudProbe.LogDrawThrottle(
				"MODE-PIPELINE anchor=" + config.HoldTierHudAnchor
				+ " customPixelBranch=" + customPixel
				+ " matrix=" + (customPixel ? "Identity" : "UIScaleMatrix")
				+ " preferCustom=" + ClientPrefs.HoldHudPreferCustom
				+ " allowDrag=" + config.HoldTierHudAllowDrag
				+ " enabled=" + config.HoldTierHudEnabled
				+ " uiZoomPolluted=" + HoldHudCoords.IsUiZoomScreenPolluted()
				+ " screen=" + Main.screenWidth + "x" + Main.screenHeight
				+ " stableSurface=" + HoldHudCoords.GamePixelSurfaceW + "x" + HoldHudCoords.GamePixelSurfaceH
				+ " source=" + HoldHudCoords.GamePixelSurfaceSource
				+ " customNorm=(" + config.HoldTierHudCustomPositionX.ToString("0.###") + ","
				+ config.HoldTierHudCustomPositionY.ToString("0.###") + ")");
			#endif
			Matrix hudMatrix = customPixel ? Matrix.Identity : GetHudDrawMatrix();
			spriteBatch.End();
			spriteBatch.Begin(
				SpriteSortMode.Deferred,
				BlendState.AlphaBlend,
				SamplerState.PointClamp,
				DepthStencilState.None,
				RasterizerState.CullNone,
				null,
				hudMatrix);

			try
			{
				ReleaseHoldHudGroup? primary = null;
				double primaryHeldMs = -1.0;
				foreach (ReleaseHoldHudGroup group in ActiveGroups)
				{
					if (!config.ShouldShowHoldTierHud(group.Tiers.Count) || !WouldDrawGroup(group))
						continue;

					if (group.HeldMs > primaryHeldMs)
					{
						primaryHeldMs = group.HeldMs;
						primary = group;
					}
				}

				bool drewAny = false;
				if (primary.HasValue)
				{
					ReleaseHoldHudGroup group = primary.Value;
					UpdateFlash(group);
					if (customPixel)
					{
						if (!TryResolveCustomOriginPixel(config, out Vector2 origin))
						{
							// Unset / unresolved Custom — do not invent a draw position.
						}
						else
						{
							float w = ResolveCustomBarWidthPixel(config);
							float barH = ResolveCustomBarHeightPixel(IsHudDragAllowed(config));
							UpdateCustomPixelDragTracking(origin, w, barH);
							GetCustomPixelSurfaceSize(out int drawSw, out int drawSh);
							#if AK_DIAGNOSTICS
							HoldHudProbe.LogDrawThrottle(
								"DRAW customPx=" + FormatVec(origin)
								+ " norm=(" + config.HoldTierHudCustomPositionX.ToString("0.###") + ","
								+ config.HoldTierHudCustomPositionY.ToString("0.###") + ")"
								+ " surface=" + drawSw + "x" + drawSh
								+ " dragging=" + _customPixelDragActive
								+ " mouse=" + FormatVec(GetCustomPixelMouse()));
							#endif
							DrawGroup(spriteBatch, group, origin);
							drewAny = true;
						}
					}
					else
					{
						Vector2 origin = ResolveOrigin(config);
						float w = ResolveBarWidthHud(config);
						float barH = BarHeightForHit(IsHudDragAllowed(config));
						UpdateDragTracking(origin, w, barH);
						if (config.HoldTierHudAnchor == HoldTierHudAnchor.Feet && _hasLastDrawnLayout)
						{
							Rectangle dr = _lastDrawnLayout.DrawRect;
							#if AK_DIAGNOSTICS
							HoldHudProbe.LogDrawThrottle(
								"DRAW feetUi=" + FormatVec(origin)
								+ " drawRect=" + FormatRect(dr)
								+ " matrix=UIScaleMatrix"
								+ " customReferenced=false");
							#endif
						}
						DrawGroup(spriteBatch, group, origin);
						drewAny = true;
					}
				}

				if (!drewAny && IsHudDragAllowed(config))
				{
					if (customPixel)
					{
						if (TryResolveCustomOriginPixel(config, out Vector2 origin))
							DrawPreviewCustomPixel(spriteBatch, config, origin);
					}
					else
						DrawPreview(spriteBatch, config);
				}
				else if (!drewAny)
				{
					_previousSelectedIndex = -1;
					_previousKeyToken = null;
				}

				if (IsHudDragAllowed(config) && !IsDragging && !customPixel && _dragHandle != null)
					_mouseOverBar = _dragHandle.IsHovering;
			}
			finally
			{
				spriteBatch.End();
				spriteBatch.Begin(
					SpriteSortMode.Deferred,
					BlendState.AlphaBlend,
					SamplerState.LinearClamp,
					DepthStencilState.None,
					RasterizerState.CullCounterClockwise,
					null,
					Main.UIScaleMatrix);
			}
			}
			finally
			{
				#if AK_DIAGNOSTICS
				AkCpuMeasureSystem.End(AkCpuMeasureSystem.IdHoldHudPostDraw);
				#endif
			}
		}

		/// <summary>
		/// Custom anchor only: show drag instruction as MouseText while hovering the gauge.
		/// Called from the interface layer immediately before Vanilla: Mouse Text.
		/// Not shown for Feet/Mouse, while dragging, or when the cursor is off the bar.
		/// </summary>
		private static void TryShowCustomGaugeHoverTip(AdvancedKeybindsConfig config)
		{
			if (config == null
				|| !IsHudFeatureEnabled(config)
				|| config.HoldTierHudAnchor != HoldTierHudAnchor.Custom
				|| !_mouseOverBar
				|| IsDragging
				|| _customPixelDragActive)
			{
				return;
			}

			string tip = Language.GetTextValue("Mods.AdvancedKeybinds.Configs.HoldTierHudAnchor.Custom.Tooltip");
			if (string.IsNullOrWhiteSpace(tip)
				|| tip.StartsWith("Mods.AdvancedKeybinds.", StringComparison.Ordinal))
			{
				tip = Language.GetTextValue("Mods.AdvancedKeybinds.Config.HoldTierHud.CustomGaugeHoverTip");
			}

			if (string.IsNullOrWhiteSpace(tip)
				|| tip.StartsWith("Mods.AdvancedKeybinds.", StringComparison.Ordinal))
			{
				return;
			}

			Main.HoverItem = new Item();
			Main.hoverItemName = string.Empty;
			Main.instance.MouseText(tip);
		}

		private static bool WouldDrawGroup(ReleaseHoldHudGroup group)
		{
			int lastReleaseMs = Math.Max(1, group.Tiers[group.Tiers.Count - 1].HoldMs);
			int lastAnyHoldMs = Math.Max(lastReleaseMs, GetMaxHoldMsOnKey(group.KeyToken));
			int hideAtMs = lastAnyHoldMs + HideAfterLastHoldExtraMs;
			return group.HeldMs < hideAtMs;
		}

		private static void DrawPreview(SpriteBatch spriteBatch, AdvancedKeybindsConfig config)
		{
			Vector2 origin = ResolveOrigin(config);
			float width = ResolveBarWidthHud(config);
			float barH = PreviewBarHeightHud;
			HudBarLayout layout = UpdateDragTracking(origin, width, barH);

			bool hovered = IsDragging || _mouseOverBar;

			DrawBarBackground(spriteBatch, layout.DrawRect);
			Color tint = config.HoldTierHudColor * (IsDragging ? 0.55f : hovered ? 0.42f : 0.28f);
			spriteBatch.Draw(TextureAssets.MagicPixel.Value, layout.DrawRect, tint);
		}

		private static void DrawPreviewCustomPixel(
			SpriteBatch spriteBatch,
			AdvancedKeybindsConfig config,
			Vector2 origin)
		{
			float width = ResolveCustomBarWidthPixel(config);
			float barH = ResolveCustomBarHeightPixel(true);
			HudBarLayout layout = UpdateCustomPixelDragTracking(origin, width, barH);
			bool hovered = IsDragging || _mouseOverBar;
			DrawBarBackground(spriteBatch, layout.DrawRect);
			Color tint = config.HoldTierHudColor * (IsDragging ? 0.55f : hovered ? 0.42f : 0.28f);
			spriteBatch.Draw(TextureAssets.MagicPixel.Value, layout.DrawRect, tint);
		}

		private static HudBarLayout UpdateDragTracking(Vector2 origin, float width, float barHeight)
		{
			AdvancedKeybindsConfig config = AdvancedKeybindsConfig.Instance;
			bool allowDrag = IsHudDragAllowed(config);
			GetHitPads(allowDrag, out int padX, out int padY);
			HudBarLayout layout = LayoutFromOrigin(origin, width, barHeight, padX, padY);
			_lastBarOrigin = origin;
			LastBarRect = layout.HitRect;
			_lastDrawnLayout = layout;
			_hasLastDrawnLayout = true;
			return layout;
		}

		private static HudBarLayout UpdateCustomPixelDragTracking(Vector2 origin, float width, float barHeight)
		{
			GetCustomPixelHitPads(out int padX, out int padY);
			Rectangle drawRect = GetCustomPixelDrawRect(origin, width, barHeight);
			Rectangle hitRect = InflateHitRectPixel(drawRect, padX, padY);
			var layout = new HudBarLayout(origin, drawRect, hitRect);
			_lastBarOrigin = origin;
			LastBarRect = hitRect;
			_lastDrawnLayout = layout;
			_hasLastDrawnLayout = true;
			return layout;
		}

		private static void UpdateFlash(ReleaseHoldHudGroup group)
		{
			if (!string.Equals(group.KeyToken, _previousKeyToken, StringComparison.OrdinalIgnoreCase))
			{
				_previousKeyToken = group.KeyToken;
				_previousSelectedIndex = group.SelectedIndex;
			}
			else if (group.SelectedIndex != _previousSelectedIndex && group.SelectedIndex >= 0)
			{
				_previousSelectedIndex = group.SelectedIndex;
			}

			if (group.SelectedIndex >= 0 && group.SelectedIndex < group.Tiers.Count)
			{
				string actionId = group.Tiers[group.SelectedIndex].ActionId;
				if (BindingInputEngine.WasJustPressed(actionId))
				{
					_flashActionId = actionId;
					_flashUntilMs = Main.gameTimeCache.TotalGameTime.TotalMilliseconds + 220;
				}
			}
		}

		private static Vector2 ResolveOrigin(AdvancedKeybindsConfig config)
		{
			Vector2 origin;
			HoldTierHudAnchor anchor = config.HoldTierHudAnchor;

			// Absolute UI units only while middle-dragging (Feet/Mouse UI handle).
			// Custom uses TryResolveCustomOriginPixel / _customDragOriginPixel (viewport px).
			if (IsDragging && !_customPixelDragActive && _hasDragFollowOrigin)
			{
				origin = _dragFollowOrigin;
			}
			else if (IsDragging && !_customPixelDragActive && _dragHandle != null)
			{
				origin = _dragHandle.GetBarOrigin();
			}
			else if (IsDragging && !_customPixelDragActive)
			{
				origin = new Vector2(config.HoldTierHudCustomPositionX, config.HoldTierHudCustomPositionY);
			}
			else if (anchor == HoldTierHudAnchor.Custom || _customPixelDragActive)
			{
				if (!TryResolveCustomOriginPixel(config, out origin))
					origin = new Vector2(CustomPositionUnset, CustomPositionUnset);
			}
			else if (anchor == HoldTierHudAnchor.Mouse)
			{
				// Cursor-relative; FeetLowerPx is Feet-only (see MouseFollowOffset*Px).
				Vector2 mouseOffsetUi = new(
					PxToHud(MouseFollowOffsetXPx),
					PxToHud(MouseFollowOffsetYPx));
				origin = GetHudMouse() + mouseOffsetUi;
				if (!IsDragging && !_customPixelDragActive)
					HoldHudCoords.CheckMouseAnomaly(origin, mouseOffsetUi);
			}
			else
			{
				// Feet (default) and any unknown/legacy value.
				origin = GetFeetHudOrigin();
			}

			// Custom / Mouse / drag: no placement clamp (free like QoL status panels —
			// keep-visible on Mouse was still short of Custom because GetHudScreenSize
			// can be tighter than live mouse range; Custom draw is unclamped).
			// Feet only: soft full-bar fit (margin 0) — do not loosen.
			if (!IsDragging
				&& !_customPixelDragActive
				&& anchor != HoldTierHudAnchor.Custom
				&& anchor != HoldTierHudAnchor.Mouse)
			{
				GetHudScreenSize(out int sw, out int sh);
				origin = ClampOriginSoLayoutFits(origin, ResolveClampBarWidth(), PreviewBarHeightHud, sw, sh);
			}

			return origin;
		}

		/// <summary>
		/// Player feet in UI space for <see cref="Main.UIScaleMatrix"/> draw.
		/// World → screen uses the same path as <c>Utils.ToScreenPosition</c>:
		/// <c>(world - screenPosition) * ZoomMatrix</c> (backbuffer/game pixels), then ÷ UIScale.
		/// Do NOT use <see cref="SpriteViewMatrix.TransformationMatrix"/> here — it re-applies
		/// view translation on already screen-relative coords and pushed Feet to the wrong side
		/// of the screen (FEET-COORD zoom vs transform delta).
		/// Feet-only — Custom must not use this path.
		/// </summary>
		private static Vector2 GetFeetHudOrigin()
		{
			Player player = Main.LocalPlayer;
			Vector2 playerFeetWorld = HoldHudCoords.PlayerFeetWorld(player);
			Vector2 worldMinusScreen = playerFeetWorld - Main.screenPosition;
			Vector2 playerFeetGamePx = HoldHudCoords.WorldToGamePx(playerFeetWorld);
			Vector2 playerFeetUi = HoldHudCoords.GamePxToUi(playerFeetGamePx);
			float feetOffsetUi = PxToHud(FeetCustomSymmetryPadPx + FeetLowerPx);
			Vector2 finalOriginUi = playerFeetUi + new Vector2(0f, feetOffsetUi);
			LogFeetCoordDiagnostics(
				player, playerFeetWorld, worldMinusScreen, playerFeetGamePx, playerFeetUi, finalOriginUi);
			HoldHudCoords.CheckFeetAnomaly(finalOriginUi, feetOffsetUi);
			return finalOriginUi;
		}

		private static void LogFeetCoordDiagnostics(
			Player player,
			Vector2 feetWorld,
			Vector2 worldMinusScreen,
			Vector2 zoomScreenPx,
			Vector2 postUiScale,
			Vector2 finalDraw)
		{
			Vector2 transformPx = HoldHudCoords.WorldMinusScreenThroughTransformationMatrix(worldMinusScreen);
			Vector2 transformAsUi = HoldHudCoords.GamePxToUi(transformPx);
			float dx = transformAsUi.X - postUiScale.X;
			float dy = transformAsUi.Y - postUiScale.Y;
			bool customReferenced = false;
			AdvancedKeybindsConfig config = AdvancedKeybindsConfig.Instance;
			string anomaly = "";
			if (config != null && config.HoldTierHudAnchor == HoldTierHudAnchor.Feet
				&& (Math.Abs(dx) > 8f || Math.Abs(dy) > 8f))
			{
				anomaly = " ANOMALY transformVsZoomUiDelta=(" + dx.ToString("0.#") + "," + dy.ToString("0.#") + ")";
			}

			if (config != null && config.HoldTierHudAnchor == HoldTierHudAnchor.Feet
				&& IsUsingCustomPixelSpace(config))
			{
				customReferenced = true;
				anomaly += " ANOMALY customBranchWhileFeet";
			}

			int vpW = 0, vpH = 0, bbW = 0, bbH = 0;
			try
			{
				var gd = Main.instance?.GraphicsDevice;
				if (gd != null)
				{
					vpW = gd.Viewport.Width;
					vpH = gd.Viewport.Height;
					bbW = gd.PresentationParameters.BackBufferWidth;
					bbH = gd.PresentationParameters.BackBufferHeight;
				}
			}
			catch { /* ignore */ }

			GetHudScreenSize(out int uiW, out int uiH);
			bool onScreen = finalDraw.X >= -50 && finalDraw.Y >= -50
				&& finalDraw.X <= uiW + 50 && finalDraw.Y <= uiH + 50;

			#if AK_DIAGNOSTICS
			HoldHudProbe.LogFeetCoordThrottle(
				"FEET-COORD anchor=" + (config?.HoldTierHudAnchor.ToString() ?? "null")
				+ " playerPos=(" + player.position.X.ToString("0.#") + "," + player.position.Y.ToString("0.#") + ")"
				+ " playerCenter=(" + player.Center.X.ToString("0.#") + "," + player.Center.Y.ToString("0.#") + ")"
				+ " playerBottomWorld=(" + feetWorld.X.ToString("0.#") + "," + feetWorld.Y.ToString("0.#") + ")"
				+ " screenPosition=(" + Main.screenPosition.X.ToString("0.#") + "," + Main.screenPosition.Y.ToString("0.#") + ")"
				+ " screen=" + Main.screenWidth + "x" + Main.screenHeight
				+ " UIScale=" + Main.UIScale.ToString("0.###")
				+ " gdViewport=" + vpW + "x" + vpH
				+ " backbuffer=" + bbW + "x" + bbH
				+ " worldMinusScreen=(" + worldMinusScreen.X.ToString("0.#") + "," + worldMinusScreen.Y.ToString("0.#") + ")"
				+ " zoomScreenPx=(" + zoomScreenPx.X.ToString("0.#") + "," + zoomScreenPx.Y.ToString("0.#") + ")"
				+ " transformPx=(" + transformPx.X.ToString("0.#") + "," + transformPx.Y.ToString("0.#") + ")"
				+ " preScale=zoomScreenPx postScale=(" + postUiScale.X.ToString("0.#") + "," + postUiScale.Y.ToString("0.#") + ")"
				+ " finalDraw=(" + finalDraw.X.ToString("0.#") + "," + finalDraw.Y.ToString("0.#") + ")"
				+ " matrix=UIScaleMatrix"
				+ " customNorm=(" + (config?.HoldTierHudCustomPositionX.ToString("0.###") ?? "?") + ","
				+ (config?.HoldTierHudCustomPositionY.ToString("0.###") ?? "?") + ")"
				+ " customReferenced=" + customReferenced
				+ " onScreen=" + onScreen
				+ " uiZoomPolluted=" + HoldHudCoords.IsUiZoomScreenPolluted()
				+ anomaly);
			#endif
		}

		// ---------------------------------------------------------------------------
		// Custom game-pixel space (Identity draw) — surface/mouse via HoldHudCoords
		// ---------------------------------------------------------------------------

		/// <summary>
		/// Custom free-placement surface in game pixels (Identity). Never uses SetZoom_UI-shrunk
		/// <see cref="Main.screenWidth"/> alone — see <see cref="HoldHudCoords.RefreshGamePixelSurface"/>.
		/// </summary>
		private static void GetCustomPixelSurfaceSize(out int width, out int height)
			=> HoldHudCoords.GetGamePixelSurface(out width, out height);

		internal static void GetCustomPixelSurfaceSizePublic(out int width, out int height)
			=> GetCustomPixelSurfaceSize(out width, out height);

		internal static bool IsCustomPixelDragActivePublic => _customPixelDragActive;

		internal static bool IsUiZoomScreenPollutedPublic() => HoldHudCoords.IsUiZoomScreenPolluted();

		/// <summary>Custom mouse in gamePixelSurface space (client→surface ratio when sizes differ).</summary>
		private static Vector2 GetCustomPixelMouse() => HoldHudCoords.GetMouseCustomGamePx();

		private static float ResolveCustomBarWidthPixel(AdvancedKeybindsConfig config)
		{
			float w = config?.HoldTierHudBarWidth ?? 220f;
			return MathHelper.Clamp(w, 150f, 500f);
		}

		private static float ResolveCustomBarHeightPixel(bool allowDragPreview)
			=> allowDragPreview ? PreviewBarHeight : BarHeight;

		/// <summary>
		/// Stable height for Custom origin seed / clamp / head gap (top-aligned).
		/// Always PreviewBarHeight so AllowDrag toggle does not move the stored top edge.
		/// </summary>
		private static float ResolveCustomLayoutBarHeightPixel() => PreviewBarHeight;

		private static void GetCustomPixelHitPads(out int padX, out int padY)
		{
			// Drag pads only when Custom drag is actually allowed; otherwise tight idle pads.
			if (IsHudDragAllowed(AdvancedKeybindsConfig.Instance))
			{
				padX = DragHitPadX;
				int padYPx = Math.Max(DragHitPadY, (DragMinHitHeight - (int)PreviewBarHeight + 1) / 2);
				padY = padYPx;
			}
			else
			{
				padX = HitPadX;
				padY = HitPadY;
			}
		}

		private static Rectangle GetCustomPixelDrawRect(Vector2 originGamePx, float widthPixel, float heightPixel)
			=> HoldHudCoords.DrawRectFromOriginGamePx(originGamePx, widthPixel, heightPixel);

		private static Rectangle InflateHitRectPixel(Rectangle drawRect, int padX, int padY)
		{
			GetCustomPixelSurfaceSize(out int sw, out int sh);
			return HoldHudCoords.HitRectFromDrawRectGamePx(drawRect, padX, padY, sw, sh);
		}

		private static Vector2 ClampCustomOriginFullyVisible(
			Vector2 origin,
			float barWidthPixel,
			float barHeightPixel,
			int vw,
			int vh)
		{
			float halfW = Math.Max(0.5f, barWidthPixel * 0.5f);
			float barH = Math.Max(1f, barHeightPixel);
			float minX = halfW;
			float maxX = vw - halfW;
			float minY = 0f;
			float maxY = vh - barH;
			if (minX > maxX)
				origin.X = (minX + maxX) * 0.5f;
			else
				origin.X = MathHelper.Clamp(origin.X, minX, maxX);
			if (minY > maxY)
				origin.Y = Math.Max(0f, minY);
			else
				origin.Y = MathHelper.Clamp(origin.Y, minY, maxY);
			return origin;
		}

		private static Vector2 SnapCustomOriginXToViewportCenter(Vector2 origin, int viewportWidth, out bool snapped)
		{
			snapped = false;
			if (float.IsNaN(origin.X) || float.IsInfinity(origin.X))
				return origin;

			float centerX = viewportWidth * 0.5f;
			float radius = Math.Max(
				CenterXSnapRadiusPx,
				viewportWidth * CenterXSnapMinScreenFraction);
			if (Math.Abs(origin.X - centerX) > radius)
				return origin;

			snapped = true;
			origin.X = centerX;
			return origin;
		}

		/// <summary>
		/// Bridge Feet/Mouse UI-space origin into Custom viewport pixels at commit only.
		/// Not used for ongoing Custom math.
		/// </summary>
		private static Vector2 UiSpaceOriginToCustomPixel(Vector2 originUi)
		{
			GetHudScreenSize(out int uiSurfaceW, out int uiSurfaceH);
			GetCustomPixelSurfaceSize(out int gamePixelSurfaceW, out int gamePixelSurfaceH);
			return HoldHudCoords.UiOriginToGamePx(
				originUi, uiSurfaceW, uiSurfaceH, gamePixelSurfaceW, gamePixelSurfaceH);
		}

		private static void AlignTrackedCustomViewport(string reason)
		{
			GetCustomPixelSurfaceSize(out int vw, out int vh);
			if (!IsStableHudScreen(vw, vh))
				return;

			if (_trackedCustomViewportW == vw && _trackedCustomViewportH == vh)
				return;

			if (AkDiag.Enabled)
			{
				AkDiag.Log(
				"HoldHud custom viewport change reason=" + reason
				+ " " + _trackedCustomViewportW + "x" + _trackedCustomViewportH
				+ " -> " + vw + "x" + vh
				+ " screen=" + Main.screenWidth + "x" + Main.screenHeight
				+ " UIScale=" + Main.UIScale.ToString("0.###"));
			}
			_trackedCustomViewportW = vw;
			_trackedCustomViewportH = vh;
			_trackedScreenW = vw;
			_trackedScreenH = vh;
		}

		/// <summary>
		/// Player hitbox top-center → Custom bar top-center in game pixels.
		/// Gap above head matches Feet's downward offset (<see cref="CustomHeadGapPixel"/>).
		/// No HudUiScale / PxToHud. Fails closed (no center/feet fallback).
		/// </summary>
		private static bool TryGetPlayerHeadCustomOriginPixel(
			AdvancedKeybindsConfig config,
			out Vector2 originPixel,
			out Vector2 headWorld,
			out Vector2 headAfterMatrix,
			out Vector2 headViewport,
			out Vector2 beforeClamp,
			out string failReason)
		{
			originPixel = default;
			headWorld = default;
			headAfterMatrix = default;
			headViewport = default;
			beforeClamp = default;
			failReason = null;

			Player player = Main.LocalPlayer;
			if (player == null || !player.active)
			{
				failReason = "player-inactive";
				return false;
			}

			GetCustomPixelSurfaceSize(out int vw, out int vh);
			if (!IsStableHudScreen(vw, vh))
			{
				failReason = "viewport-unstable";
				return false;
			}

			headWorld = HoldHudCoords.PlayerTopWorld(player);
			// Same space as Custom Identity draw / gamePixelSurface (ZoomMatrix game pixels).
			headAfterMatrix = HoldHudCoords.WorldToGamePx(headWorld);
			headViewport = headAfterMatrix;

			float barW = ResolveCustomBarWidthPixel(config);
			float barH = ResolveCustomLayoutBarHeightPixel();
			// origin = bar top-center: X aligned to player, Y = head - barH - gap (bar above head).
			beforeClamp = new Vector2(
				headViewport.X,
				headViewport.Y - barH - CustomHeadGapPixel);
			originPixel = ClampCustomOriginFullyVisible(beforeClamp, barW, barH, vw, vh);
			return true;
		}

		/// <summary>
		/// When Custom coords are unset (-1,-1), seed head-side once and persist.
		/// Never called for Feet/Mouse (logs ANOMALY if misrouted). Never overwrites valid placement.
		/// </summary>
		private static bool TrySeedCustomHeadIfUnset(
			AdvancedKeybindsConfig config,
			string storeReason,
			out Vector2 originPixel,
			bool logExistingValid)
		{
			originPixel = default;
			if (config == null)
				return false;

			if (config.HoldTierHudAnchor != HoldTierHudAnchor.Custom)
			{
				#if AK_DIAGNOSTICS
				HoldHudProbe.Log(
					"ANOMALY CUSTOM-INIT-from-non-custom anchor=" + config.HoldTierHudAnchor
					+ " reason=" + storeReason
					+ " preferCustom=" + ClientPrefs.HoldHudPreferCustom);
				#endif
				return false;
			}

			Vector2 stored = new(config.HoldTierHudCustomPositionX, config.HoldTierHudCustomPositionY);
			if (!IsCustomPositionUnset(stored))
			{
				if (logExistingValid)
				{
					#if AK_DIAGNOSTICS
					HoldHudProbe.LogCustomInit(
						reason: "existing-valid-placement",
						saved: false,
						storedNorm: stored);
					#endif
				}
				return false;
			}

			if (!TryGetPlayerHeadCustomOriginPixel(
					config,
					out originPixel,
					out Vector2 headWorld,
					out Vector2 afterMatrix,
					out _,
					out Vector2 beforeClamp,
					out string failReason))
			{
				if (AkDiag.Enabled)
				{
					AkDiag.Log(
					"HoldHud custom init deferred fail=" + failReason
					+ " keptUnset=true UIScale=" + Main.UIScale.ToString("0.###")
					+ " reason=" + storeReason);
				}
				return false;
			}

			GetCustomPixelSurfaceSize(out int vw, out int vh);
			float barW = ResolveCustomBarWidthPixel(config);
			float barH = ResolveCustomLayoutBarHeightPixel();
			Player player = Main.LocalPlayer;
			Vector2 playerCenter = player != null ? player.Center : headWorld;

			StoreNormalizedCustomPosition(config, originPixel, vw, vh, storeReason);
			AlignTrackedCustomViewport(storeReason);

			float nx = config.HoldTierHudCustomPositionX;
			float ny = config.HoldTierHudCustomPositionY;
			Vector2 roundTrip = DenormalizeCustomPosition(nx, ny, vw, vh);
			Vector2 roundTripErr = roundTrip - originPixel;

			#if AK_DIAGNOSTICS
			HoldHudProbe.LogCustomInit(
				reason: "unset",
				saved: true,
				playerTopWorld: headWorld,
				playerCenterWorld: playerCenter,
				screenPosition: Main.screenPosition,
				worldToScreenGamePx: afterMatrix,
				barSize: new Vector2(barW, barH),
				headGap: CustomHeadGapPixel,
				beforeClampGamePx: beforeClamp,
				afterClampGamePx: originPixel,
				surfaceW: vw,
				surfaceH: vh,
				surfaceSource: HoldHudCoords.GamePixelSurfaceSource,
				storedNorm: new Vector2(nx, ny),
				roundTripPx: roundTrip,
				roundTripErr: roundTripErr);
			#endif

			if (AkDiag.Enabled)
			{
				AkDiag.Log(
				"HoldHud custom init head reason=" + storeReason
				+ " headWorld=(" + headWorld.X.ToString("0.#") + "," + headWorld.Y.ToString("0.#") + ")"
				+ " afterMatrix=(" + afterMatrix.X.ToString("0.#") + "," + afterMatrix.Y.ToString("0.#") + ")"
				+ " beforeClamp=(" + beforeClamp.X.ToString("0.#") + "," + beforeClamp.Y.ToString("0.#") + ")"
				+ " originPixel=(" + originPixel.X.ToString("0.#") + "," + originPixel.Y.ToString("0.#") + ")"
				+ " norm=(" + nx.ToString("0.###") + "," + ny.ToString("0.###") + ")"
				+ " surface=" + vw + "x" + vh
				+ " source=" + HoldHudCoords.GamePixelSurfaceSource
				+ " UIScale=" + Main.UIScale.ToString("0.###"));
			}

			return true;
		}

		/// <summary>
		/// Resolve Custom draw/hit origin in viewport pixels. Does not invent center/feet fallbacks.
		/// Unset (-1,-1) seeds head when possible; otherwise returns false and leaves unset.
		/// </summary>
		private static bool TryResolveCustomOriginPixel(AdvancedKeybindsConfig config, out Vector2 originPixel)
		{
			originPixel = default;
			if (config == null)
				return false;

			if (_customPixelDragActive)
			{
				originPixel = _customDragOriginPixel;
				return true;
			}

			GetCustomPixelSurfaceSize(out int vw, out int vh);
			float x = config.HoldTierHudCustomPositionX;
			float y = config.HoldTierHudCustomPositionY;
			Vector2 stored = new(x, y);

			if (IsCustomPositionNeedsRepair(x, y))
			{
				if (RecoverCustomPlacementIfWiped(config))
				{
					x = config.HoldTierHudCustomPositionX;
					y = config.HoldTierHudCustomPositionY;
					stored = new Vector2(x, y);
				}

				if (IsCustomPositionNeedsRepair(x, y)
					&& config.HoldTierHudAnchor == HoldTierHudAnchor.Custom)
				{
					// Prefs missing/invalid: clear corrupt/partial and seed head (never stay undrawable).
					config.HoldTierHudCustomPositionX = CustomPositionUnset;
					config.HoldTierHudCustomPositionY = CustomPositionUnset;
					if (TrySeedCustomHeadIfUnset(config, "CustomRepairInit", out originPixel, logExistingValid: false))
					{
						config.SaveHoldHudChangesSilent();
						return true;
					}

					return false;
				}
			}

			MigrateLegacyCustomPositionIfNeeded(config, persist: true);
			x = config.HoldTierHudCustomPositionX;
			y = config.HoldTierHudCustomPositionY;

			if (IsStoredNormalized(x, y))
			{
				originPixel = DenormalizeCustomPosition(x, y, vw, vh);
				if (_trackedCustomViewportW > 0
					&& (_trackedCustomViewportW != vw || _trackedCustomViewportH != vh))
				{
					if (AkDiag.Enabled)
					{
						AkDiag.Log(
						"HoldHud custom denormalize"
						+ " norm=(" + x.ToString("0.###") + "," + y.ToString("0.###") + ")"
						+ " -> px=(" + originPixel.X.ToString("0.#") + "," + originPixel.Y.ToString("0.#") + ")"
						+ " viewport=" + vw + "x" + vh
						+ " tracked=" + _trackedCustomViewportW + "x" + _trackedCustomViewportH
						+ " UIScale=" + Main.UIScale.ToString("0.###"));
					}
					_trackedCustomViewportW = vw;
					_trackedCustomViewportH = vh;
				}
				return true;
			}

			if (IsStoredLegacyAbsolute(x, y) && IsStableHudScreen(vw, vh))
			{
				Vector2 raw = new(x, y);
				// Treat legacy absolute as viewport pixels when plausible; else abort (no fake center).
				if (IsClearlyOffscreen(raw, vw, vh))
				{
					if (AkDiag.Enabled)
					{
						AkDiag.Log(
						"HoldHud custom legacy absolute clearly-offscreen — leave for migrate"
						+ " raw=(" + x.ToString("0.#") + "," + y.ToString("0.#") + ")"
						+ " viewport=" + vw + "x" + vh);
					}
					return false;
				}

				originPixel = raw;
				return true;
			}

			return false;
		}

		/// <summary>Legacy name — delegates to <see cref="TryResolveCustomOriginPixel"/>.</summary>
		private static Vector2 ResolveCustomPixelOrigin(AdvancedKeybindsConfig config)
		{
			if (TryResolveCustomOriginPixel(config, out Vector2 origin))
				return origin;
			return new Vector2(CustomPositionUnset, CustomPositionUnset);
		}

		private static void DrawGroup(SpriteBatch spriteBatch, ReleaseHoldHudGroup group, Vector2 origin)
		{
			AdvancedKeybindsConfig config = AdvancedKeybindsConfig.Instance;
			int lastReleaseMs = Math.Max(1, group.Tiers[group.Tiers.Count - 1].HoldMs);

			float fill = MathHelper.Clamp((float)(group.HeldMs / lastReleaseMs), 0f, 1f);
			bool flashing = !string.IsNullOrEmpty(_flashActionId)
				&& Main.gameTimeCache.TotalGameTime.TotalMilliseconds <= _flashUntilMs;

			bool hideMsText = group.HeldMs >= lastReleaseMs;
			string timeText = hideMsText ? null : ((int)group.HeldMs).ToString() + "ms";
			string selectedLabel = group.SelectedIndex >= 0
				? FormatActionLabel(group.Tiers[group.SelectedIndex].ActionId)
				: "-";

			Color baseColor = config?.HoldTierHudColor ?? new Color(255, 220, 90);
			Color accent = group.SelectedIndex >= 0
				? baseColor
				: Color.Lerp(baseColor, Color.White, 0.55f);
			if (flashing)
				accent = Color.White;

			float width = IsUsingCustomPixelSpace(config)
				? ResolveCustomBarWidthPixel(config)
				: ResolveBarWidthHud(config);
			float fillAlpha = group.SelectedIndex >= 0 ? 0.85f : 0.45f;
			DrawHorizontal(spriteBatch, group, origin, fill, lastReleaseMs, width, accent, fillAlpha, timeText, selectedLabel);
		}

		private static int GetMaxHoldMsOnKey(string keyToken)
		{
			int maxMs = 1;
			foreach (KeyValuePair<string, ComboBinding> pair in BindingStorage.AllBindings)
			{
				ComboBinding combo = pair.Value;
				if (!combo.Hold)
					continue;

				List<string> keys = combo.Keys;
				if (keys.Count != 1 || !string.Equals(keys[0], keyToken, StringComparison.OrdinalIgnoreCase))
					continue;

				maxMs = Math.Max(maxMs, combo.EffectiveHoldMs);
			}

			return maxMs;
		}

		private static void DrawHorizontal(
			SpriteBatch spriteBatch,
			ReleaseHoldHudGroup group,
			Vector2 origin,
			float fill,
			int lastMs,
			float width,
			Color accent,
			float fillAlpha,
			string timeText,
			string selectedLabel)
		{
			AdvancedKeybindsConfig config = AdvancedKeybindsConfig.Instance;
			bool allowDrag = IsHudDragAllowed(config);
			bool customPixel = IsUsingCustomPixelSpace(config);
			float drawBarH = customPixel
				? ResolveCustomBarHeightPixel(allowDrag)
				: BarHeightForHit(allowDrag);
			HudBarLayout layout;
			if (customPixel)
			{
				GetCustomPixelHitPads(out int padX, out int padY);
				Rectangle draw = GetCustomPixelDrawRect(origin, width, drawBarH);
				Rectangle hit = InflateHitRectPixel(draw, padX, padY);
				layout = new HudBarLayout(origin, draw, hit);
			}
			else
			{
				GetHitPads(allowDrag, out int padX, out int padY);
				layout = LayoutFromOrigin(origin, width, drawBarH, padX, padY);
			}
			Rectangle drawRect = layout.DrawRect;

			DrawBarBackground(spriteBatch, drawRect);
			int fillW = Math.Max(0, (int)Math.Round(drawRect.Width * fill));
			if (fillW > 0)
			{
				spriteBatch.Draw(
					TextureAssets.MagicPixel.Value,
					new Rectangle(drawRect.X, drawRect.Y, fillW, drawRect.Height),
					accent * fillAlpha);
			}

			for (int i = 0; i < group.Tiers.Count; i++)
			{
				float x = drawRect.X + drawRect.Width * (group.Tiers[i].HoldMs / (float)lastMs);
				bool selected = i == group.SelectedIndex || i - 1 == group.SelectedIndex;
				float tickH = selected ? drawRect.Height + 10f : drawRect.Height + 4f;
				float tickY = drawRect.Y - (selected ? 5f : 2f);
				float tickW = selected ? 3f : 1f;
				Color tickColor = selected ? accent : Color.White * 0.7f;
				spriteBatch.Draw(
					TextureAssets.MagicPixel.Value,
					new Rectangle((int)(x - tickW * 0.5f), (int)tickY, (int)tickW, (int)tickH),
					tickColor);
			}

			DrawLabels(spriteBatch, origin + new Vector2(0f, drawRect.Height + 4f), timeText, selectedLabel, accent);
		}

		private static void DrawBarBackground(SpriteBatch spriteBatch, Rectangle drawRect)
		{
			spriteBatch.Draw(
				TextureAssets.MagicPixel.Value,
				new Rectangle(drawRect.X - 1, drawRect.Y - 1, drawRect.Width + 2, drawRect.Height + 2),
				Color.Black * 0.55f);
			spriteBatch.Draw(
				TextureAssets.MagicPixel.Value,
				drawRect,
				Color.Black * 0.35f);
		}

		private static void DrawLabels(SpriteBatch spriteBatch, Vector2 center, string timeText, string selectedLabel, Color accent)
		{
			float y = center.Y;
			if (!string.IsNullOrEmpty(timeText))
			{
				Vector2 timeSize = ChatManager.GetStringSize(FontAssets.MouseText.Value, timeText, Vector2.One);
				ChatManager.DrawColorCodedStringWithShadow(
					spriteBatch,
					FontAssets.MouseText.Value,
					timeText,
					new Vector2(center.X - timeSize.X * 0.5f, y),
					Color.White,
					0f,
					Vector2.Zero,
					Vector2.One);
				y += 16f;
			}

			string label = selectedLabel;
			if (label.Length > 18)
				label = label.Substring(0, 17) + "…";

			Vector2 labelSize = ChatManager.GetStringSize(FontAssets.MouseText.Value, label, new Vector2(0.85f));
			ChatManager.DrawColorCodedStringWithShadow(
				spriteBatch,
				FontAssets.MouseText.Value,
				label,
				new Vector2(center.X - labelSize.X * 0.5f, y),
				accent,
				0f,
				Vector2.Zero,
				new Vector2(0.85f));
		}

		private static string FormatActionLabel(string actionId)
		{
			if (string.IsNullOrEmpty(actionId))
				return "?";

			int slash = actionId.LastIndexOf('/');
			if (slash >= 0 && slash < actionId.Length - 1)
				return actionId[(slash + 1)..];

			return actionId;
		}
	}
}
