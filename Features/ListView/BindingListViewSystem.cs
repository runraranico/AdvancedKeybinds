using System;
using System.Collections.Generic;
using AdvancedKeybinds;
using AdvancedKeybinds.Binding;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.GameInput;
using Terraria.ModLoader;
using Terraria.UI;

namespace AdvancedKeybinds.Features.ListView
{
	/// <summary>
	/// Standalone overlay host for the binding list. Does not patch input engines or Combo UI.
	/// Detach: delete Features/ListView and remove OpenBindingListHotkey registration.
	/// </summary>
	public sealed class BindingListViewSystem : ModSystem
	{
		private static readonly BindingListViewState State = new();
		private static List<BindingListEntry> _cachedAll;
		private static List<BindingListEntry> _deviceRows = new();
		private static List<BindingListEntry> _visibleRows = new();
		private static bool _cacheDirty = true;
		private static int _seenCombosMutationRevision = -1;

		private static bool _wasMouseLeft;
		private static bool _dragging;
		private static bool _pendingTitleAllClick;
		/// <summary>Title-bar press pending (label ShowAll and/or drag threshold). Excludes −/× buttons.</summary>
		private static bool _pendingTitleBarPress;
		/// <summary>
		/// Independent of pending/drag: left-edge press originated on <see cref="BindingListViewLayout.TitleBarRect"/>
		/// (label, empty, −, ×). Owns rear-click suppress until left release (incl. release frame),
		/// surviving Close/Minimize clearing of pending/drag. Not armed by held entry from outside.
		/// </summary>
		private static bool _titleOriginLeftOwned;
		private static Vector2 _pressOriginUi;
		private static Vector2 _dragGrabOffsetUi;
		private static bool _scrollingThumb;
		private static bool _resizingBottom;
		private static bool _pointerOverWindow;
		private static int _prevRawScrollWheel;
		private static bool _rawScrollSynced;
		private static int _wheelHandledGameFrame = -1;
		private static long _lastTitleClickTick;
		private static Vector2 _lastTitleClickPosUi;
		private static bool _hasTitleClickCandidate;
		/// <summary>
		/// Front-window mouse capture active this frame (claimFront).
		/// Latches button state for AK chrome; swallows Main/PlayerInput buttons for back UI.
		/// Does NOT park mouse X/Y — coordinate park broke TitleBar Drag Baseline.
		/// </summary>
		private static bool _frontMouseCaptureActive;
		private static bool _latchedMouseLeft;
		private static bool _latchedMouseRight;
		private static bool _latchedMouseMiddle;
		/// <summary>
		/// claimFront computed in PostUpdateInput (correct mouse phase via SetZoom_UI).
		/// InterfaceScaleType.UI layers must NOT re-run SetZoom_UI HitTest — that double-transforms
		/// Main.mouse and yields claim=false (LIVE: mouse≈-1789), so Hover clear never ran.
		/// </summary>
		private static bool _claimFrontCachedThisFrame;
		private static Point _mouseUiCachedThisFrame;
		/// <summary>
		/// Legacy click-focus flag (chrome click). Wheel claim uses <see cref="BindingListViewInputOwnership"/> /
		/// <c>claimFront</c>, not this alone.
		/// </summary>
		private static bool _ownsWheelFocus;
		/// <summary>
		/// True front menu (InGameUI / options / …) temporarily cleared <see cref="_ownsWheelFocus"/> —
		/// restore when that menu closes. Inventory alone does not suspend.
		/// </summary>
		private static bool _wheelFocusSuspendedByOtherUi;
		/// <summary>Hotbar slot snapshot while list is open (last frame without list wheel consume).</summary>
		private static int _frozenHotbarSlot = -1;
		/// <summary>List consumed wheel this frame — restore hotbar after Player.Update if it still moved.</summary>
		private static bool _suppressHotbarWheelThisFrame;
		private const float DragSlopUi = 4f;
		/// <summary>Max ms between title-bar clicks to count as DoubleClick (minimize/restore).</summary>
		private const int TitleDoubleClickMs = 450;
		/// <summary>Max UI-space distance between the two clicks of a title-bar DoubleClick.</summary>
		private const float TitleDoubleClickSlopUi = 8f;
		/// <summary>UI pixels per mouse-wheel notch (Terraria delta is typically ±120).</summary>
		private const float ScrollPixelsPerNotch = 48f;

		public override void Load()
		{
			// Build+Reload / mod load: never restore an open list or MOD-off from prior session memory.
			State.ResetForNewSession();
			BindingListKeyProbe.Reset();
			_ownsWheelFocus = false;
			_wheelFocusSuspendedByOtherUi = false;
			_frozenHotbarSlot = -1;
			_suppressHotbarWheelThisFrame = false;
			BindingListViewResizeCursor.Clear();
			BindingListViewInputOwnership.RunRegressionSelfCheck();
			BindingListViewWheelPolicy.RunRegressionSelfCheck();
			BindingListViewItemSlotGuard.Apply();
			BindingListViewUiHoverGuard.Apply();
			BindingListViewHoverRequestSuppress.Apply();
			BindingListViewHoverSoundRequestSuppress.Apply();
			BindingListViewHitDiag.BeginSession("ModSystem.Load");
		}

		public override void PostSetupContent()
		{
			BindingListViewHoverRequestSuppress.TryApplyBankButtons();
		}

		public override void Unload()
		{
			BindingListViewItemSlotGuard.UnapplyNote();
			BindingListViewUiHoverGuard.UnapplyNote();
			BindingListViewHoverRequestSuppress.UnapplyNote();
			BindingListViewHoverSoundRequestSuppress.UnapplyNote();
			State.ResetForNewSession();
			BindingListKeyProbe.Reset();
			_cachedAll = null;
			_deviceRows = new List<BindingListEntry>();
			_visibleRows = new List<BindingListEntry>();
			_cacheDirty = true;
			_dragging = false;
			_pendingTitleAllClick = false;
			_pendingTitleBarPress = false;
			_titleOriginLeftOwned = false;
			_hasTitleClickCandidate = false;
			_frontMouseCaptureActive = false;
			_claimFrontCachedThisFrame = false;
			_resizingBottom = false;
			_pointerOverWindow = false;
			_rawScrollSynced = false;
			_wasMouseLeft = false;
			_ownsWheelFocus = false;
			_wheelFocusSuspendedByOtherUi = false;
			_frozenHotbarSlot = -1;
			_suppressHotbarWheelThisFrame = false;
			BindingListViewResizeCursor.Clear();
		}

		/// <summary>
		/// After Player.Update may have applied wheel to hotbar despite Lock/Consume — undo that slot change only
		/// when the list claimed the wheel this frame (cursor was in the input region).
		/// </summary>
		public override void PostUpdatePlayers()
		{
			if (_suppressHotbarWheelThisFrame)
			{
				RestoreFrozenHotbarSlot();
				_suppressHotbarWheelThisFrame = false;
			}
			else
			{
				// Only keep a freeze snapshot while cursor is in the input region.
				// If list is merely open (cursor outside), never freeze hotbar across frames.
				_frozenHotbarSlot = -1;
			}
		}

		public override void UpdateUI(GameTime gameTime)
		{
			// Re-assert button swallow after UIElement trees update and before Draw click handlers
			// that read Main.mouseLeft. No coordinate park.
			// Narrow Close tail: even when list is closed, title-origin left ownership still reasserts.
			if (!State.IsOpen && !ShouldBlockInventoryItemSlotLeftClick())
				return;
			if (ShouldBlockInventoryItemSlotLeftClick())
				EnsureFrontWindowMouseCapture();
		}

		public override void PostUpdateInput()
		{
			// Title / character select: never keep the list open across sessions.
			if (Main.gameMenu)
			{
				if (State.IsOpen || !State.ModSortEnabled)
				{
					State.ResetForNewSession();
					BindingListKeyProbe.Reset();
					BindingListViewResizeCursor.Clear();
					_cachedAll = null;
					_deviceRows = new List<BindingListEntry>();
					_visibleRows = new List<BindingListEntry>();
					_cacheDirty = true;
					_resizingBottom = false;
					_ownsWheelFocus = false;
					_wheelFocusSuspendedByOtherUi = false;
					_frozenHotbarSlot = -1;
					_suppressHotbarWheelThisFrame = false;
				}

				CancelTitleOriginLeftGesture(clearPendingDrag: true);
				_claimFrontCachedThisFrame = false;
				_frontMouseCaptureActive = false;
				_wasMouseLeft = false;
				return;
			}

			// Chat / sign / chest are foreground covers — still refresh claim so UI-layer guards
			// do not keep a stale claim=0/1 from a prior frame (old early-return skipped refresh).
			bool suspendContentForEditUi = Main.drawingPlayerChat || Main.editSign || Main.editChest;

			ModKeybind hotkey = AdvancedKeybinds.OpenBindingListHotkey;
			if (!suspendContentForEditUi && hotkey != null && hotkey.JustPressed)
			{
				BindingListDeviceFilter device = DetectOpenDeviceFilter();
				bool wasOpen = State.IsOpen;
				State.ToggleOpenFromHotkey(device);
				if (State.IsOpen)
				{
					if (!wasOpen)
					{
						BindingListViewOpenPerfDiag.OnHotkeyOpenDetected();
						BindingListViewOpenPerfDiag.OnOpenStateTrue();
					}

					// Always rebuild catalog from current Combos on open (add/change/clear).
					_cachedAll = null;
					_cacheDirty = true;
					_seenCombosMutationRevision = -1;
					_ownsWheelFocus = true;
					_wheelFocusSuspendedByOtherUi = false;
					BindingListKeyProbe.Reset();
					CaptureFrozenHotbarSlot();
					BindingListViewHitDiag.BeginSession("ListViewOpened");
					BindingListViewHoverLeakDiag.ResetLayerDumpFlag();
				}
				else
				{
					BindingListKeyProbe.Reset();
					BindingListViewResizeCursor.Clear();
					_ownsWheelFocus = false;
					_wheelFocusSuspendedByOtherUi = false;
					_cachedAll = null;
					_deviceRows = new List<BindingListEntry>();
					_visibleRows = new List<BindingListEntry>();
				}
			}

			// New frame: vanilla refreshed Main.mouse*. Capture from a prior frame must not stick.
			_frontMouseCaptureActive = false;
			_claimFrontCachedThisFrame = false;

			if (!State.IsOpen)
			{
				_pointerOverWindow = false;
				_rawScrollSynced = false;
				_ownsWheelFocus = false;
				_wheelFocusSuspendedByOtherUi = false;
				TickTitleOriginLeftGestureWhileClosed(suspendContentForEditUi);
				// READ-ONLY audit after per-frame capture clear (Close residual check).
				BindingListViewInputRegressionAudit.Tick(State);
				return;
			}

			// True front menus (InGameUI / options / …) suspend claim; Inventory alone does not.
			UpdateWheelFocusAgainstOtherUi();

			// Front-window occupancy uses full chrome (title + body + resize), UI-space mouse.
			// SetZoom_UI here (PostUpdateInput) — NOT again inside InterfaceScaleType.UI layers.
			Point mouseUi = GetMouseUiForHitTest();
			_mouseUiCachedThisFrame = mouseUi;
			bool overListChrome = IsPointerOverListInteractive(mouseUi);
			bool foregroundCovers = ForegroundMenuCoversListView() || suspendContentForEditUi;
			bool leftHeldAtPostUpdateStart = Main.mouseLeft;

			// Arm / cancel title-origin left ownership BEFORE occupy / back-UI (Inventory).
			// Left edge only — held entry from outside does not arm.
			TickTitleOriginLeftGestureWhileOpen(mouseUi, foregroundCovers);

			bool claimFront = BindingListViewInputOwnership.ShouldClaimFrontWindowInput(
				overListChrome,
				foregroundCovers,
				_titleOriginLeftOwned);
			_claimFrontCachedThisFrame = claimFront;
			bool acceptingInputEarly = IsListViewAcceptingInput(mouseUi) && claimFront;

			BindingListViewClickPathDiag.CapturePostUpdateClick(
				State,
				mouseUi,
				overListChrome,
				foregroundCovers,
				claimFront,
				acceptingInputEarly);

			if (suspendContentForEditUi)
			{
				BindingListViewHoverLeakDiag.Capture("PostUpdate_EditUiSuspend", State, claimFront, _frontMouseCaptureActive);
				FinishTitleOriginLeftReleaseAfterClaim(leftHeldAtPostUpdateStart);
				return;
			}

			if (!State.IsMaximized)
			{
				BindingListKeyProbe.Reset();
				_rawScrollSynced = false;
				_suppressHotbarWheelThisFrame = false;
				_frozenHotbarSlot = -1;
				// Minimized title still blocks clicks/wheel through to Inventory under it —
				// but yields when a true front menu covers AK ALL KEY.
				if (claimFront)
					OccupyFrontWindowMouse(blockWheel: true, occupyReason: "PostUpdate_Minimized");
				BindingListViewHoverLeakDiag.Capture("PostUpdate_Minimized_AfterOccupy", State, claimFront, _frontMouseCaptureActive);
				FinishTitleOriginLeftReleaseAfterClaim(leftHeldAtPostUpdateStart);
				return;
			}

			// Rebuild when BindingStorage Combos change (Controls/ComboPanel edit while list stays open).
			int combosRev = BindingStorage.CombosMutationRevision;
			if (combosRev != _seenCombosMutationRevision)
			{
				_seenCombosMutationRevision = combosRev;
				_cacheDirty = true;
			}

			if (_cacheDirty || _cachedAll == null)
			{
				BindingListViewOpenPerfDiag.OnCatalogBuildStart();
				_cachedAll = BindingListCatalog.BuildAll();
				BindingListViewOpenPerfDiag.OnCatalogBuildEnd();
				_cacheDirty = false;
				BindingListViewOpenPerfDiag.OnRebuildVisibleStart();
				RebuildVisible();
				BindingListViewOpenPerfDiag.OnRebuildVisibleEnd();
				BindingListCatalogTraceDiag.TraceRebuild(
					_cachedAll,
					_deviceRows,
					_visibleRows,
					State.DeviceFilter,
					State.ContentMode,
					State.FilterProbeToken,
					State.ModSortEnabled);
			}

			bool acceptingInput = acceptingInputEarly;

			// TEMP: HitDiag-only wheel snapshots BEFORE ProcessWheel consumes them.
			int wheelBefore = 0;
			int wheelUiBefore = 0;
			if (BindingListViewLiveDiagGate.FileOutputEnabled)
			{
				wheelBefore = PlayerInput.ScrollWheelDelta;
				wheelUiBefore = PlayerInput.ScrollWheelDeltaForUI;
			}

			// Claim only when AK is the frontmost interactive layer over its chrome.
			// Inventory under AK → claim. Mod Config / InGameUI over AK → yield.
			if (claimFront)
				OccupyFrontWindowMouse(blockWheel: false, occupyReason: "PostUpdate_Maximized");
			BindingListViewHoverLeakDiag.Capture("PostUpdate_Maximized_AfterOccupy", State, claimFront, _frontMouseCaptureActive);

			ProcessWheelForListView(acceptingInput, claimFront);

			// TEMP investigation: raw vs UI mouse + accepting (verify same-space fix).
			if (BindingListViewLiveDiagGate.FileOutputEnabled)
			{
				BindingListViewHitDiag.CapturePostUpdateInput(
					State,
					new Point(Main.mouseX, Main.mouseY),
					mouseUi,
					acceptingInput,
					_suppressHotbarWheelThisFrame,
					_frozenHotbarSlot,
					_ownsWheelFocus,
					_wheelFocusSuspendedByOtherUi,
					_pointerOverWindow,
					wheelBefore,
					wheelUiBefore);
			}

			// Key / mouse / wheel-as-search probe: apply only while accepting input.
			bool overUiChrome = IsPointerOverTitleBar(mouseUi)
				|| IsPointerOverColumnHeader(mouseUi)
				|| IsPointerOverResizeChrome(mouseUi)
				|| _resizingBottom;
			EnsureContentHeightForScroll();
			bool listCanScroll = BindingListViewWheelPolicy.ListCanScroll(State.MaxScrollUi);
			// Scrollbar track / thumb-drag: suppress mouse-button key search only.
			bool suppressMouseProbe = overUiChrome
				|| _scrollingThumb
				|| (State.IsMaximized && listCanScroll && BindingListViewLayout.ScrollTrackRect(State).Contains(mouseUi));
			if (acceptingInput)
			{
				// Scrollbar → list scroll owns wheel. No scrollbar → Wheel Up/Down Key Search.
				bool suppressWheelProbe = BindingListViewWheelPolicy.ShouldSuppressWheelProbe(overUiChrome, listCanScroll);
				string probe = BindingListKeyProbe.TryGetRisingProbeToken(
					suppressWheel: suppressWheelProbe,
					suppressMouseButtons: suppressMouseProbe,
					suppressKeyboard: overUiChrome);
				if (!string.IsNullOrEmpty(probe))
				{
					State.ApplyProbeToggle(probe);
					RebuildVisible();
				}
			}
			else
			{
				// Outside body (or outside window): drain rising edges; do not Apply.
				BindingListKeyProbe.TryGetRisingProbeToken(
					suppressWheel: true,
					suppressMouseButtons: true,
					suppressKeyboard: true);
			}

			// READ-ONLY audit after claim/swallow for this frame (Open path).
			BindingListViewInputRegressionAudit.Tick(State);
			FinishTitleOriginLeftReleaseAfterClaim(leftHeldAtPostUpdateStart);
		}

		public override void ModifyInterfaceLayers(List<GameInterfaceLayer> layers)
		{
			// READ-ONLY Mouse Item LIVE: create session file/header even with zero user input.
			BindingListViewMouseItemDiag.EnsureSessionStarted();

			// BEFORE Vanilla: Inventory — ItemSlot LeftClick runs in that layer.
			// PostUpdateInput / Mouse Text mouseInterface is too late once DrawInterface resets it.
			int inventoryIndex = layers.FindIndex(l => l.Name.Equals("Vanilla: Inventory"));
			if (inventoryIndex >= 0)
			{
				layers.Insert(inventoryIndex, new LegacyGameInterfaceLayer(
					"AdvancedKeybinds: BindingListViewMouseOccupy",
					delegate
					{
						TryOccupyMouseBeforeInventory();
						return true;
					},
					InterfaceScaleType.UI));

				// Re-find: insert shifted Inventory. TEMP LIVE: observe mouseItem after ItemSlot.
				int invAfterOccupy = layers.FindIndex(l => l.Name.Equals("Vanilla: Inventory"));
				if (invAfterOccupy >= 0)
				{
					layers.Insert(invAfterOccupy + 1, new LegacyGameInterfaceLayer(
						"AdvancedKeybinds: BindingListViewClickPathAfterInventory",
						delegate
						{
							// Inventory ItemSlot.Draw sets HoverItem even when LeftClick is guarded.
							// Clear under the same claimFront gate so tooltips do not draw through AK.
							ClearBackUiHoverIfClaimingFront();
							if (BindingListViewLiveDiagGate.HoverLeakDiagEnabled)
							{
								BindingListViewHoverLeakDiag.Capture(
									"LAYER_AfterInventory",
									State,
									ShouldBlockInventoryItemSlotLeftClick(),
									_frontMouseCaptureActive);
							}
							BindingListViewClickPathDiag.CapturePostInventoryLayer(State);
							// READ-ONLY Mouse Item hold/draw LIVE (no state mutation).
							BindingListViewMouseItemDiag.NoteAfterInventory(State);
							return true;
						},
						InterfaceScaleType.UI));
				}
			}

			int index = layers.FindIndex(l => l.Name.Equals("Vanilla: Mouse Text"));
			if (index < 0)
				index = layers.Count;

			layers.Insert(index, new LegacyGameInterfaceLayer(
				"AdvancedKeybinds: BindingListView",
				delegate
				{
					if (!State.IsOpen)
					{
						BindingListViewResizeCursor.Clear();
						return true;
					}

					if (BindingListViewLiveDiagGate.HoverLeakDiagEnabled)
					{
						BindingListViewHoverLeakDiag.Capture(
							"LAYER_AK_Draw_ENTER",
							State,
							ShouldBlockInventoryItemSlotLeftClick(),
							_frontMouseCaptureActive);
					}
					ClearBackUiHoverIfClaimingFront();
					UpdateMouseUi();
					BindingListViewLayout.ClampToScreen(State);
					BindingListViewRenderer.Draw(Main.spriteBatch, State, State.IsMaximized ? _visibleRows : null);
					BindingListViewHitDiag.DrawOverlay(Main.spriteBatch);
					if (BindingListViewLiveDiagGate.HoverLeakDiagEnabled)
					{
						BindingListViewHoverLeakDiag.Capture(
							"LAYER_AK_Draw_AFTER_DRAW",
							State,
							ShouldBlockInventoryItemSlotLeftClick(),
							_frontMouseCaptureActive);
					}
					return true;
				},
				InterfaceScaleType.UI));

			// After vanilla software cursor — draw resize hint (no OS cursor; avoids FPS flicker).
			// NOTE: Do NOT Restore/RePark Main.mouseX/Y here — that regressed TitleBar Drag.
			int resizeCursorIndex = layers.FindIndex(l => l.Name.Equals("Vanilla: Cursor"));
			if (resizeCursorIndex < 0)
				resizeCursorIndex = layers.Count - 1;
			layers.Insert(resizeCursorIndex + 1, new LegacyGameInterfaceLayer(
				"AdvancedKeybinds: BindingListViewResizeCursor",
				delegate
				{
					if (!State.IsOpen)
					{
						BindingListViewResizeCursor.Clear();
						return true;
					}

					BindingListViewResizeCursor.Present(Main.spriteBatch);
					return true;
				},
				InterfaceScaleType.UI));

			// TEMP A/B TEST (2026-09-20): disable ONLY the Vanilla Mouse Item / NPC Head wrap.
			// Purpose: determine if AK layer replacement causes cursor Mouse Item not to draw
			// despite Main.mouseItem holding the item (CASE A hold confirmed in LIVE).
			// Vanilla layer must remain untouched in the interface list.
			// Do NOT promote this disable to Baseline. Re-enable or replace with a real fix after RESULT A/B.
			// Other layers (MouseText wrap, occupy, AfterInventory diag, AK draw) stay as-is.
			const bool TemporarilyDisableMouseItemLayerWrapForAbTest = true;
			if (!TemporarilyDisableMouseItemLayerWrapForAbTest)
			{
				// READ-ONLY wrap: log mouseItem immediately before/after Vanilla Mouse Item draw.
				// Does not alter draw semantics — same pattern as Mouse Text wrap (observe only).
				int mouseItemIndex = layers.FindIndex(l => l.Name.Equals("Vanilla: Mouse Item / NPC Head"));
				if (mouseItemIndex >= 0)
				{
					GameInterfaceLayer vanillaMouseItem = layers[mouseItemIndex];
					layers[mouseItemIndex] = new LegacyGameInterfaceLayer(
						"Vanilla: Mouse Item / NPC Head",
						delegate
						{
							bool active = vanillaMouseItem.Active;
							BindingListViewMouseItemDiag.CapturePreMouseItemLayer(State, active);
							BindingListViewMouseItemDiag.CaptureWrapBeforeDraw(State, active);
							bool drew = vanillaMouseItem.Draw();
							BindingListViewMouseItemDiag.CapturePostMouseItemLayer(State, active, drew);
							return drew;
						},
						InterfaceScaleType.UI);
				}
			}

			BindingListViewHoverLeakDiag.DumpInterfaceLayerOrderOnce(layers);
		}

		/// <summary>
		/// tML PostDrawInterface timing may be after Mouse Text draw; MouseText wrap clears first.
		/// Kept as a cheap secondary clear for shared hover state. No mouse coordinate park.
		/// </summary>
		public override void PostDrawInterface(SpriteBatch spriteBatch)
		{
			bool claim = ShouldBlockInventoryItemSlotLeftClick();
			if (BindingListViewLiveDiagGate.HoverLeakDiagEnabled)
				BindingListViewHoverLeakDiag.Capture("PostDrawInterface_BEFORE_CLEAR", State, claim, _frontMouseCaptureActive);
			if (claim)
				ClearBackUiHoverIfClaimingFront();
			if (BindingListViewLiveDiagGate.HoverLeakDiagEnabled)
				BindingListViewHoverLeakDiag.Capture("PostDrawInterface_AFTER_CLEAR", State, claim, _frontMouseCaptureActive);
		}

		/// <summary>
		/// Runs in an interface layer immediately before <c>Vanilla: Inventory</c>.
		/// TEMP LIVE: ENTER/SKIP/EXIT traces only — no behavior change beyond existing occupy.
		/// </summary>
		private static void TryOccupyMouseBeforeInventory()
		{
			bool listOpen = State.IsOpen;
			// Inventory layer is InterfaceScaleType.UI — Main.mouse* already UI. Do not SetZoom_UI.
			// Prefer PostUpdate claim cache — do NOT overwrite it here (UI-phase mouse can disagree
			// with PostUpdate SetZoom_UI mouse and falsely clear claimFront → Hover clear skipped).
			Point mouseUi = listOpen ? GetMouseUiAlreadyInInterfaceLayer() : Point.Zero;
			// Title-origin Close tail: ShouldBlock can be true while list is already closed.
			bool claimFront = ShouldBlockInventoryItemSlotLeftClick();
			if (listOpen)
				_mouseUiCachedThisFrame = mouseUi;

			bool clickPath = BindingListViewLiveDiagGate.ClickPathDiagEnabled
				&& BindingListViewLiveDiagGate.FileOutputEnabled;
			Rectangle win = Rectangle.Empty;
			Rectangle body = Rectangle.Empty;
			bool overListChrome = false;
			bool foregroundCovers = false;
			bool mIfaceBefore = false;
			bool blockBefore = false;
			if (clickPath)
			{
				win = listOpen ? BindingListViewLayout.WindowRect(State) : Rectangle.Empty;
				body = listOpen ? BindingListViewLayout.BodyRect(State) : Rectangle.Empty;
				overListChrome = listOpen && IsPointerOverListInteractive(mouseUi);
				foregroundCovers = listOpen && ForegroundMenuCoversListView();
				mIfaceBefore = ReadPlayerMouseInterface();
				blockBefore = Main.blockMouse;
				BindingListViewClickPathDiag.TraceTryOccupy(
					"ENTER",
					State,
					mouseUi,
					win,
					body,
					listOpen,
					overListChrome,
					foregroundCovers,
					claimFront,
					calledOccupy: false,
					mIfaceBefore,
					blockBefore,
					mIfaceBefore,
					blockBefore);
			}

			if (!listOpen && !claimFront)
			{
				if (clickPath)
				{
					BindingListViewClickPathDiag.TraceTryOccupy(
						"SKIP_LIST_CLOSED",
						State,
						mouseUi,
						win,
						body,
						listOpen,
						overListChrome,
						foregroundCovers,
						claimFront,
						calledOccupy: false,
						mIfaceBefore,
						blockBefore,
						mIfaceBefore,
						blockBefore);
				}
				return;
			}

			if (!claimFront)
			{
				if (clickPath)
				{
					BindingListViewClickPathDiag.TraceTryOccupy(
						"SKIP_NO_CLAIM",
						State,
						mouseUi,
						win,
						body,
						listOpen,
						overListChrome,
						foregroundCovers,
						claimFront,
						calledOccupy: false,
						mIfaceBefore,
						blockBefore,
						mIfaceBefore,
						blockBefore);
				}
				return;
			}

			OccupyFrontWindowMouse(blockWheel: false, occupyReason: listOpen ? "PreInventory" : "PreInventory_TitleOriginClosedTail");

			if (clickPath)
			{
				BindingListViewClickPathDiag.TraceTryOccupy(
					"EXIT_AFTER_OCCUPY",
					State,
					mouseUi,
					win,
					body,
					listOpen,
					overListChrome,
					foregroundCovers,
					claimFront,
					calledOccupy: true,
					mIfaceBefore,
					blockBefore,
					ReadPlayerMouseInterface(),
					Main.blockMouse);
			}
		}

		private static bool ReadPlayerMouseInterface()
		{
			if (Main.myPlayer < 0 || Main.myPlayer >= Main.player.Length)
				return false;
			Player player = Main.LocalPlayer;
			return player != null && player.active && player.mouseInterface;
		}

		private static BindingListDeviceFilter DetectOpenDeviceFilter()
		{
			try
			{
				if (PlayerInput.UsingGamepad
					|| PlayerInput.CurrentInputMode == InputMode.XBoxGamepad
					|| PlayerInput.CurrentInputMode == InputMode.XBoxGamepadUI)
					return BindingListDeviceFilter.GamePad;
			}
			catch
			{
				// ignore
			}

			return BindingListDeviceFilter.KeyboardMouse;
		}

		/// <summary>
		/// Single source of truth for ListView hit tests:
		/// Mouse = UI space (via <see cref="GetMouseUiForHitTest"/> before any call);
		/// Bounds = UI space (<c>PositionUi</c> layout rects).
		/// Callers must never pass raw/phase-mixed <see cref="Main.mouseX"/> here.
		/// Hit APIs must not apply /UIScale or *UIScale OR aliases.
		/// </summary>
		private static Point GetMouseUiForHitTest()
		{
			// Always use the live Main.mouse* path (SetZoom_UI). Never substitute a parked/latched
			// coordinate — that froze TitleBar Drag / HitTest on stale or -9999 positions.
			int prevMouseX = Main.mouseX;
			int prevMouseY = Main.mouseY;
			int prevLastMouseX = Main.lastMouseX;
			int prevLastMouseY = Main.lastMouseY;
			int prevScreenW = Main.screenWidth;
			int prevScreenH = Main.screenHeight;
			try
			{
				PlayerInput.SetZoom_UI();
				return new Point(Main.mouseX, Main.mouseY);
			}
			finally
			{
				Main.mouseX = prevMouseX;
				Main.mouseY = prevMouseY;
				Main.lastMouseX = prevLastMouseX;
				Main.lastMouseY = prevLastMouseY;
				Main.screenWidth = prevScreenW;
				Main.screenHeight = prevScreenH;
			}
		}

		/// <summary>
		/// Shared gate: ListView accepts key / mouse / wheel content updates only while the cursor
		/// is in the operation target region (maximized body; title bar excluded).
		/// Open alone does not accept input. <paramref name="mouseUi"/> must be UI space.
		/// </summary>
		private static bool IsListViewAcceptingInput(Point mouseUi)
			=> IsPointerOverListInputRegion(mouseUi);

		/// <summary>
		/// Wheel / front-window occupancy (BASELINE front/back + scroll-vs-search):
		/// When <paramref name="claimFront"/> — cursor over chrome AND AK is frontmost —
		/// block Craft/Hotbar (Lock/Consume/Hotbar freeze). List scroll only if scrollbar present.
		/// No scrollbar: do not scroll; Wheel Up/Down Key Search uses MouseInputHelper edges
		/// (probe runs after this; PlayerInput consume alone must not suppress that probe).
		/// When a true front menu covers AK, claimFront is false → no Lock/Consume.
		/// </summary>
		private static void ProcessWheelForListView(bool acceptingInput, bool claimFront)
		{
			if (!claimFront)
			{
				_rawScrollSynced = false;
				_suppressHotbarWheelThisFrame = false;
				_frozenHotbarSlot = -1;
				return;
			}

			EnsureContentHeightForScroll();

			// Front window owns the wheel versus Craft / Hotbar (even when Key Search uses the notch).
			CaptureFrozenHotbarSlot();
			SetLocalMouseInterface();
			PlayerInput.LockVanillaMouseScroll("AdvancedKeybinds: BindingListView");

			bool wheelMoved = WheelMovedThisFrame();
			if (wheelMoved)
				MarkListWheelConsumedForHotbar();

			int frame = (int)Main.GameUpdateCount;
			if (_wheelHandledGameFrame == frame)
			{
				ConsumeWheelDeltas();
				return;
			}

			_wheelHandledGameFrame = frame;

			bool listCanScroll = BindingListViewWheelPolicy.ListCanScroll(State.MaxScrollUi);
			bool canScrollList = BindingListViewWheelPolicy.ShouldScrollList(acceptingInput, listCanScroll);

			if (canScrollList)
			{
				int use = ReadWheelDeltaForScroll();
				if (use != 0)
				{
					if (!wheelMoved)
						MarkListWheelConsumedForHotbar();
					float notches = use / 120f;
					if (Math.Abs(notches) < 0.01f)
						notches = use > 0 ? 1f : -1f;
					State.AddScrollDelta(-notches * ScrollPixelsPerNotch);
				}
				else
					SyncRawScrollBaseline();
			}
			else
			{
				// No scrollbar (or outside body): no list scroll. Still consumed above vs Hotbar;
				// Key Search reads MouseInputHelper frame edges, not PlayerInput deltas.
				SyncRawScrollBaseline();
			}

			ConsumeWheelDeltas();
		}

		/// <summary>
		/// Front-window mouse ownership for the current claimFront region (not Inventory-specific).
		/// 1) Standard: mouseInterface + blockMouse.
		/// 2) Swallow mouse *buttons* only (not coordinates) so back UI cannot Click; AK uses latch for buttons.
		/// Hover labels: SuppressBackHover + PostDrawInterface clear (claimFront). No coordinate park.
		/// </summary>
		private static void OccupyFrontWindowMouse(bool blockWheel, string occupyReason = null)
		{
			bool clickPath = occupyReason != null
				&& BindingListViewLiveDiagGate.ClickPathDiagEnabled
				&& BindingListViewLiveDiagGate.FileOutputEnabled;
			bool mIfaceBefore = false;
			bool blockBefore = false;
			if (clickPath)
			{
				mIfaceBefore = ReadPlayerMouseInterface();
				blockBefore = Main.blockMouse;
				BindingListViewClickPathDiag.TraceOccupyFront("ENTER", occupyReason, blockWheel, mIfaceBefore, blockBefore, mIfaceBefore, blockBefore);
			}

			EnsureFrontWindowMouseCapture();
			if (!blockWheel)
			{
				if (clickPath)
				{
					BindingListViewClickPathDiag.TraceOccupyFront(
						"EXIT_CLAIM_MOUSE_ONLY",
						occupyReason,
						blockWheel,
						mIfaceBefore,
						blockBefore,
						ReadPlayerMouseInterface(),
						Main.blockMouse);
				}

				return;
			}

			CaptureFrozenHotbarSlot();
			PlayerInput.LockVanillaMouseScroll("AdvancedKeybinds: BindingListView");
			if (WheelMovedThisFrame())
				MarkListWheelConsumedForHotbar();
			ConsumeWheelDeltas();

			if (clickPath)
			{
				BindingListViewClickPathDiag.TraceOccupyFront(
					"EXIT_CLAIM_MOUSE_AND_WHEEL",
					occupyReason,
					blockWheel,
					mIfaceBefore,
					blockBefore,
					ReadPlayerMouseInterface(),
					Main.blockMouse);
			}
		}

		/// <summary>
		/// Latch real button state for AK; swallow buttons on Main/PlayerInput for back UI.
		/// Leaves Main.mouseX/Y untouched so HitTest/Drag/DoubleClick keep live coordinates.
		/// </summary>
		private static void EnsureFrontWindowMouseCapture()
		{
			if (!_frontMouseCaptureActive)
			{
				_latchedMouseLeft = Main.mouseLeft;
				_latchedMouseRight = Main.mouseRight;
				_latchedMouseMiddle = Main.mouseMiddle;
				_frontMouseCaptureActive = true;
			}

			SetLocalMouseInterface();
			SwallowMouseButtonsOnMain();
		}

		private static void SetLocalMouseInterface()
		{
			if (Main.myPlayer < 0 || Main.myPlayer >= Main.player.Length)
				return;
			Player player = Main.LocalPlayer;
			if (player == null || !player.active)
				return;
			player.mouseInterface = true;
			Main.blockMouse = true;
		}

		/// <summary>
		/// Swallow click buttons for back UI only (Baseline 23). Does not modify mouse coordinates.
		/// Coordinate park (-9999) was removed — it broke TitleBar Drag Baseline.
		/// Must clear Triggers.JustPressed / JustReleased (not only Current): some back UIs
		/// read JustPressed.MouseLeft in UpdateUI after PostUpdateInput.
		/// </summary>
		private static void SwallowMouseButtonsOnMain()
		{
			Main.mouseLeft = false;
			Main.mouseRight = false;
			Main.mouseMiddle = false;
			Main.mouseLeftRelease = false;
			Main.mouseRightRelease = false;
			Main.mouseMiddleRelease = false;

			try
			{
				PlayerInput.Triggers.Current.MouseLeft = false;
				PlayerInput.Triggers.Current.MouseRight = false;
				PlayerInput.Triggers.Current.MouseMiddle = false;
				PlayerInput.Triggers.JustPressed.MouseLeft = false;
				PlayerInput.Triggers.JustPressed.MouseRight = false;
				PlayerInput.Triggers.JustPressed.MouseMiddle = false;
				PlayerInput.Triggers.JustReleased.MouseLeft = false;
				PlayerInput.Triggers.JustReleased.MouseRight = false;
				PlayerInput.Triggers.JustReleased.MouseMiddle = false;
				if (PlayerInput.MouseKeys != null && PlayerInput.MouseKeys.Count > 0)
					PlayerInput.MouseKeys.Clear();
			}
			catch
			{
				// ignore PlayerInput shape differences
			}
		}

		private static void EnsureContentHeightForScroll()
		{
			if (_visibleRows == null)
				return;
			bool modSort = State.ModSortEnabled;
			float measured = BindingListViewRenderer.MeasureContentHeight(_visibleRows, modSort);
			if (measured > State.ContentHeightUi)
				State.ContentHeightUi = measured;
			else if (State.ContentHeightUi < 1f)
				State.ContentHeightUi = measured;
		}

		private static int ReadWheelDeltaForScroll()
		{
			int wheelUi = PlayerInput.ScrollWheelDeltaForUI;
			int wheel = PlayerInput.ScrollWheelDelta;
			int use = wheelUi != 0 ? wheelUi : wheel;

			int raw = 0;
			try
			{
				raw = Microsoft.Xna.Framework.Input.Mouse.GetState().ScrollWheelValue;
			}
			catch
			{
				raw = _prevRawScrollWheel;
			}

			if (!_rawScrollSynced)
			{
				_prevRawScrollWheel = raw;
				_rawScrollSynced = true;
			}
			else
			{
				int rawDelta = raw - _prevRawScrollWheel;
				_prevRawScrollWheel = raw;
				if (use == 0 && rawDelta != 0)
					use = rawDelta;
			}

			return use;
		}

		/// <summary>
		/// Maximized body (headers + rows). Title bar excluded.
		/// <paramref name="mouseUi"/> and layout rects are both UI space — no scale OR aliases.
		/// </summary>
		private static bool IsPointerOverListInputRegion(Point mouseUi)
		{
			if (!State.IsOpen || !State.IsMaximized)
				return false;
			if (!IsPointerOverWindow(mouseUi))
				return false;

			Rectangle body = BindingListViewLayout.BodyRect(State);
			return !body.IsEmpty && body.Contains(mouseUi);
		}

		/// <summary>
		/// True front menus (InGameUI / options / map / chat) suspend list wheel claim.
		/// Inventory alone does not — AK ALL KEY stays front over Inventory.
		/// </summary>
		private static void UpdateWheelFocusAgainstOtherUi()
		{
			if (ForegroundMenuCoversListView())
			{
				if (_ownsWheelFocus)
				{
					_wheelFocusSuspendedByOtherUi = true;
					_ownsWheelFocus = false;
				}

				return;
			}

			if (_wheelFocusSuspendedByOtherUi)
			{
				_ownsWheelFocus = true;
				_wheelFocusSuspendedByOtherUi = false;
				CaptureFrozenHotbarSlot();
			}
		}

		/// <summary>
		/// Another UI is layered ABOVE AK ALL KEY. Inventory open under AK is NOT covering.
		/// </summary>
		private static bool ForegroundMenuCoversListView()
		{
			bool inGameUiHasState = false;
			try
			{
				inGameUiHasState = Main.InGameUI != null && Main.InGameUI.CurrentState != null;
			}
			catch
			{
				// ignore
			}

			return BindingListViewInputOwnership.IsForegroundMenuCovering(
				Main.drawingPlayerChat,
				Main.editSign,
				Main.editChest,
				Main.ingameOptionsWindow,
				Main.mapFullscreen,
				inGameUiHasState);
		}

		/// <summary>TEMP READ-ONLY for input regression audit. Not a product API.</summary>
		internal static bool FrontMouseCaptureActiveForAudit => _frontMouseCaptureActive;

		/// <summary>
		/// PreInventory / ItemSlot / UpdateUI gate: per-frame claim cache only (incl. title-origin
		/// closed-tail for the release frame). Next PostUpdate clears the cache. No multi-frame
		/// owned flag bypass that would miss a release-frame cache or persist after Close.
		/// </summary>
		internal static bool ShouldBlockInventoryItemSlotLeftClick()
		{
			if (ForegroundMenuCoversListView())
				return false;
			return _claimFrontCachedThisFrame;
		}

		/// <summary>
		/// Drop title-origin left ownership. When <paramref name="clearPendingDrag"/>, also clear
		/// pending/drag so a foreground cover cannot resume a stale title drag after it closes.
		/// </summary>
		private static void CancelTitleOriginLeftGesture(bool clearPendingDrag)
		{
			_titleOriginLeftOwned = false;
			if (!clearPendingDrag)
				return;

			_pendingTitleBarPress = false;
			_pendingTitleAllClick = false;
			_dragging = false;
			_hasTitleClickCandidate = false;
		}

		/// <summary>
		/// After claim cache was set for this frame: if left was already up at PostUpdate start,
		/// drop multi-frame owned so the next frame cannot keep claiming without UpdateMouseUi.
		/// Cache stays true until the next PostUpdate clears it (release-frame PreInventory/UpdateUI).
		/// </summary>
		private static void FinishTitleOriginLeftReleaseAfterClaim(bool leftHeldAtPostUpdateStart)
		{
			if (_titleOriginLeftOwned && !leftHeldAtPostUpdateStart)
				_titleOriginLeftOwned = false;
		}

		/// <summary>
		/// PostUpdateInput (list open), before occupy: arm on left edge over TitleBarRect;
		/// cancel on foreground. Release drops multi-frame owned after claim via
		/// <see cref="FinishTitleOriginLeftReleaseAfterClaim"/> (and UpdateMouseUi leftUp).
		/// </summary>
		private static void TickTitleOriginLeftGestureWhileOpen(Point mouseUi, bool foregroundCovers)
		{
			if (foregroundCovers)
			{
				CancelTitleOriginLeftGesture(clearPendingDrag: true);
				return;
			}

			bool leftEdge = Main.mouseLeft && !_wasMouseLeft;
			if (leftEdge && IsPointerOverTitleBar(mouseUi))
				_titleOriginLeftOwned = true;
		}

		/// <summary>
		/// Close / session tail while list is closed: set per-frame claim cache + occupy while
		/// multi-frame owned; on release drop owned but leave cache true for this frame only.
		/// </summary>
		private static void TickTitleOriginLeftGestureWhileClosed(bool suspendContentForEditUi)
		{
			bool foregroundCovers = ForegroundMenuCoversListView() || suspendContentForEditUi;
			if (foregroundCovers)
			{
				CancelTitleOriginLeftGesture(clearPendingDrag: true);
				_wasMouseLeft = Main.mouseLeft;
				return;
			}

			if (!_titleOriginLeftOwned)
			{
				_wasMouseLeft = Main.mouseLeft;
				return;
			}

			bool leftHeldAtStart = Main.mouseLeft;
			_claimFrontCachedThisFrame = BindingListViewInputOwnership.ShouldClaimFrontWindowInput(
				overListChrome: false,
				foregroundMenuCoversList: false,
				titleOriginLeftGestureHeld: true);
			OccupyFrontWindowMouse(blockWheel: true, occupyReason: "PostUpdate_TitleOriginClosedTail");

			bool leftLogical = _frontMouseCaptureActive ? _latchedMouseLeft : leftHeldAtStart;
			_wasMouseLeft = leftLogical;
			FinishTitleOriginLeftReleaseAfterClaim(leftHeldAtStart);
		}

		/// <summary>
		/// Mouse in UI space during an InterfaceScaleType.UI layer — Main.mouseX/Y are already UI.
		/// Do not call SetZoom_UI again (double transform). Used by UpdateMouseUi / drag / chrome.
		/// </summary>
		private static Point GetMouseUiAlreadyInInterfaceLayer()
			=> new Point(Main.mouseX, Main.mouseY);

		/// <summary>
		/// Reject clearly broken UI-space mouse (double SetZoom / bad phase). Not a park — fallback only.
		/// </summary>
		private static bool IsSaneUiMouse(Point mouseUi)
		{
			if (mouseUi.X < -64 || mouseUi.Y < -64)
				return false;
			int maxX = Main.screenWidth + 64;
			int maxY = Main.screenHeight + 64;
			if (maxX < 128)
				maxX = 8192;
			if (maxY < 128)
				maxY = 8192;
			return mouseUi.X <= maxX && mouseUi.Y <= maxY;
		}

		/// <summary>
		/// Same path as the − / □ title button: Minimize ↔ Maximize + compact/restore width.
		/// </summary>
		private static void ToggleMinimizedChrome()
		{
			if (!State.IsOpen)
				return;

			if (State.IsMaximized)
			{
				State.Minimize();
				BindingListKeyProbe.Reset();
			}
			else
			{
				State.Maximize();
				_cacheDirty = true;
			}

			BindingListViewLayout.ClampToScreen(State);
			_pendingTitleBarPress = false;
			_pendingTitleAllClick = false;
			_dragging = false;
			_hasTitleClickCandidate = false;
		}

		private static bool TryConsumeTitleBarDoubleClick(Vector2 mouseUi)
		{
			long now = Environment.TickCount64;
			if (_hasTitleClickCandidate
				&& now - _lastTitleClickTick <= TitleDoubleClickMs
				&& Vector2.DistanceSquared(mouseUi, _lastTitleClickPosUi)
					<= TitleDoubleClickSlopUi * TitleDoubleClickSlopUi)
			{
				_hasTitleClickCandidate = false;
				return true;
			}

			_hasTitleClickCandidate = true;
			_lastTitleClickTick = now;
			_lastTitleClickPosUi = mouseUi;
			return false;
		}

		/// <summary>
		/// When AK claims front over its actual WindowRect, clear shared hover/MouseText state so
		/// Vanilla: Mouse Text and plain hover labels (e.g. "Sort Inventory") do not draw from back UI.
		/// Same claimFront gate as Click capture — not a paint-over. Does not enumerate back mods.
		/// </summary>
		private static void ClearBackUiHoverIfClaimingFront()
		{
			if (!ShouldBlockInventoryItemSlotLeftClick())
				return;

			Main.HoverItem = new Item();
			Main.hoverItemName = string.Empty;
			Main.mouseText = false;
		}

		private static void ConsumeWheelDeltas()
		{
			PlayerInput.ScrollWheelDelta = 0;
			PlayerInput.ScrollWheelDeltaForUI = 0;
		}

		private static bool WheelMovedThisFrame()
		{
			if (PlayerInput.ScrollWheelDelta != 0 || PlayerInput.ScrollWheelDeltaForUI != 0)
				return true;

			if (!_rawScrollSynced)
				return false;

			try
			{
				int raw = Microsoft.Xna.Framework.Input.Mouse.GetState().ScrollWheelValue;
				return raw != _prevRawScrollWheel;
			}
			catch
			{
				return false;
			}
		}

		private static void CaptureFrozenHotbarSlot()
		{
			if (Main.myPlayer < 0 || Main.myPlayer >= Main.player.Length)
				return;
			Player player = Main.LocalPlayer;
			if (player == null || !player.active)
				return;
			_frozenHotbarSlot = player.selectedItem;
		}

		private static void MarkListWheelConsumedForHotbar()
		{
			if (_frozenHotbarSlot < 0)
				CaptureFrozenHotbarSlot();
			_suppressHotbarWheelThisFrame = true;
			RestoreFrozenHotbarSlot();
		}

		private static void RestoreFrozenHotbarSlot()
		{
			if (_frozenHotbarSlot < 0)
				return;
			if (Main.myPlayer < 0 || Main.myPlayer >= Main.player.Length)
				return;
			Player player = Main.LocalPlayer;
			if (player == null || !player.active)
				return;
			if (player.selectedItem != _frozenHotbarSlot)
				player.selectedItem = _frozenHotbarSlot;
		}

		private static void SyncRawScrollBaseline()
		{
			try
			{
				_prevRawScrollWheel = Microsoft.Xna.Framework.Input.Mouse.GetState().ScrollWheelValue;
				_rawScrollSynced = true;
			}
			catch
			{
				_rawScrollSynced = false;
			}
		}

		/// <summary>UI-space mouse vs UI-space window rect. No scale aliases.</summary>
		private static bool IsPointerOverWindow(Point mouseUi)
		{
			Rectangle win = BindingListViewLayout.WindowRect(State);
			return !win.IsEmpty && win.Contains(mouseUi);
		}

		/// <summary>Window body/title or resize bands (may extend slightly outside the frame).</summary>
		private static bool IsPointerOverListInteractive(Point mouseUi)
		{
			if (IsPointerOverWindow(mouseUi))
				return true;

			if (IsPointerOverResizeChrome(mouseUi))
				return true;

			return false;
		}

		/// <summary>Bottom height-resize band only (right / corner manual resize disabled).</summary>
		private static bool IsPointerOverResizeChrome(Point mouseUi)
		{
			Rectangle bottom = BindingListViewLayout.ResizeBottomRect(State);
			return PointHitsUiRect(mouseUi, bottom);
		}

		/// <summary>キー / 動作 / MOD header strip — clicks and keys are UI, not list probe.</summary>
		private static bool IsPointerOverColumnHeader(Point mouseUi)
		{
			if (!State.IsMaximized)
				return false;
			return PointHitsUiRect(mouseUi, BindingListViewLayout.ColumnHeaderStripRect(State));
		}

		/// <summary>Title bar (AK ALL KEY / drag / − / ×) — Mouse1 is UI, not a filter probe.</summary>
		private static bool IsPointerOverTitleBar(Point mouseUi)
		{
			return PointHitsUiRect(mouseUi, BindingListViewLayout.TitleBarRect(State));
		}

		/// <summary>UI-space point vs UI-space rect. Same space only — no /scale or *scale OR.</summary>
		private static bool PointHitsUiRect(Point mouseUi, Rectangle r)
		{
			return !r.IsEmpty && r.Contains(mouseUi);
		}

		private static void RebuildVisible()
		{
			if (_cachedAll == null)
			{
				_deviceRows = new List<BindingListEntry>();
				_visibleRows = new List<BindingListEntry>();
				return;
			}

			_deviceRows = BindingListCatalog.FilterByDevice(_cachedAll, State.DeviceFilter);
			// Width fixed to longest among all registered for this device (stable across key filters).
			BindingListViewRenderer.ApplyFixedWidthFromRegistered(State, _deviceRows);

			switch (State.ContentMode)
			{
				case BindingListContentMode.All:
					_visibleRows = State.ModSortEnabled
						? BindingListCatalog.SortByMod(_deviceRows)
						: _deviceRows;
					break;
				case BindingListContentMode.Filtered:
					{
						var filtered = BindingListCatalog.FilterByProbeToken(_deviceRows, State.FilterProbeToken);
						_visibleRows = State.ModSortEnabled
							? BindingListCatalog.SortByMod(filtered)
							: filtered;
						break;
					}
				default:
					_visibleRows = new List<BindingListEntry>();
					break;
			}
		}

		private static void UpdateMouseUi()
		{
			// When claimFront captured earlier, Main.mouseLeft is swallowed — use latch for AK chrome.
			bool left = _frontMouseCaptureActive ? _latchedMouseLeft : Main.mouseLeft;
			bool leftEdge = left && !_wasMouseLeft;
			bool leftUp = !left && _wasMouseLeft;
			// Drop multi-frame title-origin ownership on release BEFORE early returns (_dragging etc.).
			// Release-frame claim already lives in PostUpdate _claimFrontCachedThisFrame until next frame.
			if (leftUp)
				_titleOriginLeftOwned = false;

			// Prefer live UI-layer mouse; if a scale pass left Main.mouse* nonsensical, use PostUpdate cache
			// (never -9999 park). Keeps TitleBar Drag on real motion when the layer mouse is valid.
			Point mouseUi = GetMouseUiAlreadyInInterfaceLayer();
			if (!IsSaneUiMouse(mouseUi))
				mouseUi = _mouseUiCachedThisFrame;
			Vector2 mouse = new(mouseUi.X, mouseUi.Y);
			Point mp = mouseUi;

			_pointerOverWindow = IsPointerOverWindow(mp);
			bool overListChrome = IsPointerOverListInteractive(mp);
			bool foregroundCovers = ForegroundMenuCoversListView();
			// Prefer PostUpdate cache for claim; still allow live over-chrome for wheel focus edges.
			bool claimFront = _claimFrontCachedThisFrame
				|| BindingListViewInputOwnership.ShouldClaimFrontWindowInput(
					overListChrome,
					foregroundCovers,
					_titleOriginLeftOwned);
			bool acceptingInput = IsListViewAcceptingInput(mp) && claimFront;

			// TEMP investigation: HitDiag-only wheel / raw Point snapshots BEFORE ProcessWheel.
			int wheelBeforeUi = 0;
			int wheelUiBeforeUi = 0;
			Point rawMainPoint = Point.Zero;
			if (BindingListViewLiveDiagGate.FileOutputEnabled)
			{
				wheelBeforeUi = PlayerInput.ScrollWheelDelta;
				wheelUiBeforeUi = PlayerInput.ScrollWheelDeltaForUI;
				rawMainPoint = new Point(Main.mouseX, Main.mouseY);
			}

			// UI layer: re-assert front ownership (mouseInterface + swallow) for the whole hover.
			if (claimFront)
				EnsureFrontWindowMouseCapture();

			ProcessWheelForListView(acceptingInput, claimFront);

			if (BindingListViewLiveDiagGate.FileOutputEnabled)
			{
				BindingListViewHitDiag.CaptureUiLayer(
					State,
					rawMainPoint,
					mouseUi,
					acceptingInput,
					_suppressHotbarWheelThisFrame,
					_frozenHotbarSlot,
					_ownsWheelFocus,
					_wheelFocusSuspendedByOtherUi,
					_pointerOverWindow,
					wheelBeforeUi,
					wheelUiBeforeUi);
			}

			if (leftEdge)
			{
				if (ForegroundMenuCoversListView())
				{
					// Front menu (Mod Config etc.): do not re-claim focus under it.
					_ownsWheelFocus = false;
					if (!overListChrome)
						_wheelFocusSuspendedByOtherUi = false;
				}
				else if (overListChrome)
				{
					_ownsWheelFocus = true;
					_wheelFocusSuspendedByOtherUi = false;
					CaptureFrozenHotbarSlot();
				}
				else
				{
					_ownsWheelFocus = false;
					_wheelFocusSuspendedByOtherUi = false;
				}
			}

			// Bottom height-resize cursor only (right / corner disabled).
			BindingListViewResizeCursor.UpdateKind(State, mp, _resizingBottom);

			if (_resizingBottom)
			{
				// Keep content auto-width from jumping while the user drags height.
				State.SuppressContentWidthFit = true;
				if (left)
				{
					if (State.IsMaximized)
						State.SetBodyHeightUi(mouse.Y - State.PositionUi.Y - State.TitleBarHeightUi);
					BindingListViewLayout.ClampToScreen(State);
				}
				else
				{
					// Session-only height: do not persist body height across Close / restart.
					_resizingBottom = false;
					State.SuppressContentWidthFit = false;
				}

				_wasMouseLeft = left;
				return;
			}

			State.SuppressContentWidthFit = false;

			if (_scrollingThumb)
			{
				if (left && State.IsMaximized)
				{
					State.ScrollOffsetUi = BindingListViewRenderer.ScrollOffsetFromTrackY(State, mouse.Y);
					State.ClampScroll();
				}
				else
					_scrollingThumb = false;

				_wasMouseLeft = left;
				return;
			}

			if (_dragging)
			{
				if (left)
				{
					State.PositionUi = mouse - _dragGrabOffsetUi;
					BindingListViewLayout.ClampToScreen(State);
				}
				else
				{
					_dragging = false;
					BindingListViewLayout.ClampToScreen(State);
				}

				_wasMouseLeft = left;
				return;
			}

			if (_pendingTitleBarPress && left)
			{
				if (Vector2.DistanceSquared(mouse, _pressOriginUi) >= DragSlopUi * DragSlopUi)
				{
					// Drag started — not a DoubleClick / ShowAll click.
					_pendingTitleBarPress = false;
					_pendingTitleAllClick = false;
					_hasTitleClickCandidate = false;
					_dragging = true;
					_dragGrabOffsetUi = _pressOriginUi - State.PositionUi;
					State.PositionUi = mouse - _dragGrabOffsetUi;
					BindingListViewLayout.ClampToScreen(State);
				}

				_wasMouseLeft = left;
				return;
			}

			if (leftEdge)
			{
				Rectangle close = BindingListViewLayout.CloseButtonRect(State);
				Rectangle minMax = BindingListViewLayout.MinMaxButtonRect(State);
				Rectangle titleLabel = BindingListViewLayout.TitleLabelRect(State);
				Rectangle titleDrag = BindingListViewLayout.TitleDragRect(State);
				Rectangle track = BindingListViewLayout.ScrollTrackRect(State);
				Rectangle bottom = BindingListViewLayout.ResizeBottomRect(State);
				Rectangle modHeader = BindingListViewRenderer.ModHeaderButtonRect(State);

				if (close.Contains(mp))
				{
					State.Close();
					BindingListViewResizeCursor.Clear();
					BindingListKeyProbe.Reset();
					_ownsWheelFocus = false;
					_wheelFocusSuspendedByOtherUi = false;
					_cachedAll = null;
					_deviceRows = new List<BindingListEntry>();
					_visibleRows = new List<BindingListEntry>();
					_hasTitleClickCandidate = false;
				}
				else if (minMax.Contains(mp))
				{
					ToggleMinimizedChrome();
				}
				else if (State.IsMaximized && bottom.Contains(mp))
				{
					// Height only. Right-edge and bottom-right corner resize are disabled;
					// bottom rect already excludes the corner so corner drag does not height-resize.
					_resizingBottom = true;
					_hasTitleClickCandidate = false;
				}
				else if (State.IsMaximized
					&& !modHeader.IsEmpty
					&& modHeader.Contains(mp)
					&& !IsPointerOverResizeChrome(mp))
				{
					State.ToggleModSort();
					RebuildVisible();
					_hasTitleClickCandidate = false;
				}
				else if (State.IsMaximized && State.MaxScrollUi > 0.5f && track.Contains(mp))
				{
					_scrollingThumb = true;
					State.ScrollOffsetUi = BindingListViewRenderer.ScrollOffsetFromTrackY(State, mouse.Y);
					State.ClampScroll();
					_hasTitleClickCandidate = false;
				}
				else if (titleDrag.Contains(mp))
				{
					// Title bar (label + empty) excluding −/×. DoubleClick → same Toggle as − button.
					if (TryConsumeTitleBarDoubleClick(mouse))
					{
						ToggleMinimizedChrome();
					}
					else
					{
						_pendingTitleBarPress = true;
						_pressOriginUi = mouse;
						// Maximized label area: release without drag still ShowAll (existing).
						_pendingTitleAllClick = State.IsMaximized && titleLabel.Contains(mp);
					}
				}
			}

			if (leftUp)
			{
				if (_pendingTitleBarPress)
				{
					_pendingTitleBarPress = false;
					if (_pendingTitleAllClick)
					{
						_pendingTitleAllClick = false;
						State.ShowAll();
						if (_cachedAll == null)
							_cacheDirty = true;
						else
							RebuildVisible();
					}
				}

				_dragging = false;
				_scrollingThumb = false;
				_resizingBottom = false;
			}

			_wasMouseLeft = left;
		}
	}
}
