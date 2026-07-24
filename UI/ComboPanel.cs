using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using AdvancedKeybinds.Binding;
using AdvancedKeybinds.Core;
using AdvancedKeybinds.Diagnostics;
using AdvancedKeybinds.Patches;
using ReLogic.Localization.IME;
using ReLogic.OS;
using Terraria;
using Terraria.Audio;
using Terraria.GameContent;
using Terraria.GameInput;
using Terraria.ID;
using Terraria.Localization;

namespace AdvancedKeybinds.UI
{
	/// <summary>
	/// Overlay panel opened by clicking a control row. Presents three key slots (left / middle / right)
	/// plus a Hold toggle. Each slot is set by clicking it and pressing a key or mouse button. The
	/// combination fires the action when every filled slot is held together.
	/// </summary>
	internal static class ComboPanel
	{
		public static bool IsOpen { get; private set; }

		private static string _actionId;
		private static string _actionLabel;
		private static Color _accent = new(120, 110, 180);
		private static ComboBinding _working;
		/// <summary>Snapshot of bindings when the panel opened; Back restores this.</summary>
		private static ComboBinding _openedSnapshot;
		private static int _capturingSlot;
		private static bool _prevLeft;
		private static bool _prevRight;
		private static bool _prevGameLeft;
		private static bool _prevGameRight;

		// After registering a mouse button, ignore slot clicks until that button is released so the same
		// press cannot re-open capture or trigger right-click clear (UpdateInput captures before Draw).
		private static int _blockSlotMouseButtonsUntilUp;

		// While the left-click that opened purple wait is still held, never treat it as outside-cancel
		// or Cancel/Clear dismiss. Background UI updates also shove Main.mouseX/Y to -1000; that must
		// not look like "click outside the capturing slot".
		private static bool _swallowOpenClickMouse1UntilUp;

		/// <summary>Tracks focus so deactivate / regain edges cannot cancel purple wait.</summary>
		private static bool _prevHasFocus = true;

		/// <summary>
		/// Sentinel used by <see cref="Patches.ControlsOverlayPatches"/> while background UIs update.
		/// Hit-tests that use Main.mouseX/Y / MouseScreen are meaningless under this poison.
		/// </summary>
		private const int BackgroundUiCursorPoison = -1000;

		/// <summary>
		/// Last unpoisoned Main.mouse UI point. Background UserInterface updates shove mouse to
		/// (-1000,-1000); after keyboard slot capture without mouse move, Main.mouse can stay
		/// poisoned so hover/click miss until the next mouse-move message refreshes coords.
		/// </summary>
		private static int _lastGoodMouseUiX;
		private static int _lastGoodMouseUiY;
		private static bool _hasLastGoodMouseUi;

		/// <summary>
		/// After a slot key is accepted, Terraria may still treat that same key (especially Space)
		/// as UI confirm in following frames. While the registered token stays held, ComboPanel
		/// skips idle UI clicks. Hover protection is separate (physical mouse lock).
		/// </summary>
		private static string _uiReuseSuppressToken;
		private static bool _uiReuseSuppressActive;

		/// <summary>
		/// Physical OS cursor UI point captured when a slot enters purple wait. Keyboard UI nav
		/// rewrites Main.mouse without moving Mouse.GetState — keep this until the OS cursor moves.
		/// </summary>
		private static bool _physicalHoverLockActive;
		private static int _physicalHoverUiX;
		private static int _physicalHoverUiY;
		private static int _physicalHoverAnchorClientX;
		private static int _physicalHoverAnchorClientY;

		/// <summary>
		/// Focus / foreground / client for capture mouse gestures. Main.mouse poison must not
		/// block in-slot Mouse1/Mouse2 binds — hit-tests use <see cref="GetPanelMouseUi"/>.
		/// </summary>
		private static bool IsCaptureFocusContextSafe()
			=> Main.hasFocus
				&& GameWindowHandle.IsGameProcessForeground()
				&& MouseInputHelper.IsCursorInsideGameClient();

		// Cursor tip for Save-time LeftClick-alone confirmation.
		private static long _rejectLeftClickTipUntilMs;
		private static bool _leftClickConfirmPending;
		private const long RejectLeftClickTipMs = 10000;
		private const float RejectLeftClickTipScale = 1.5f;
		// ~3 cm upward from the default mouse-text spot (96 DPI → 113 px).
		private const float RejectLeftClickTipOffsetY = -113f;
		private static readonly Color RejectLeftClickTipColor = new(0xFF, 0x9B, 0x00);
		/// <summary>First click on reset-displays arms confirm; second click on the same button commits.</summary>
		private static bool _resetDisplaysConfirmPending;
		private static readonly Color ResetDisplaysWarnFill = new(140, 36, 36);
		private static readonly Color ResetDisplaysWarnBorder = new(255, 72, 72);
		/// <summary>Last single-key display clear (right-click on key-info); Undo restores this.</summary>
		private static string _displayAliasUndoToken;
		private static string _displayAliasUndoDisplay;
		private static long _displayRestoredTipUntilMs;
		private static string _displayRestoredTipText;
		private const long DisplayRestoredTipMs = 3000;
		private static readonly Color DisplayRestoredTipColor = new(0xFF, 0x9B, 0x9B);

		// True once the user actually changes something in the panel. Saving/closing without any change
		// must leave the existing binding untouched (never clear or reset it to a default).
		private static bool _dirty;

		// After binding Escape in a slot, block ESC-driven dismiss until Escape is released once
		// (same physical press must not also close the panel). Not a wall-clock timer — GameUpdateCount
		// can stall in paused menus and would leave Escape "dead" forever.
		private static bool _escapeDismissBlockedUntilKeyUp;

		// After Escape closes the panel, keep swallowing menu-level ESC briefly so the parent
		// keybind screen does not close on the same press.
		private static long _escDismissGraceUntilMs;

		/// <summary>
		/// Same-input-tick flags: registration wait clears immediately after Escape is saved, but parent /
		/// Inventory / HandleEscape-dismiss must still see this press as consumed until the next input tick.
		/// Cleared by <see cref="BeginInputTickEscapeCaptureState"/> via a local tick serial (not GameUpdateCount).
		/// </summary>
		private static bool _escapeRegisteredThisTick;
		private static bool _escapeConsumedThisTick;
		private static uint _escapeCaptureInputTick;

		/// <summary>Increments every PlayerInput Escape prep — independent of Main.GameUpdateCount.</summary>
		private static uint _escapeInputTickSerial;

		private const int EscDismissGraceMs = 450;

		// Diag mirrors (path tracer).
		internal static bool DiagRegistrationWaitActive => IsRegistrationWaitActive();
		internal static int DiagCapturingSlot => _capturingSlot;
		internal static bool DiagEscapeRegisteredThisFrame => _escapeRegisteredThisTick;
		internal static bool DiagEscapeConsumedThisFrame => _escapeConsumedThisTick;

		/// <summary>Clears overlay UI state after mod unload/reload so stale flags cannot block Escape.</summary>
		public static void ResetStaticState()
		{
			IsOpen = false;
			_actionId = null;
			_actionLabel = null;
			_capturingSlot = 0;
			_dirty = false;
			_escapeDismissBlockedUntilKeyUp = false;
			_escDismissGraceUntilMs = 0;
			_escapeRegisteredThisTick = false;
			_escapeConsumedThisTick = false;
			_escapeCaptureInputTick = 0;
			_awaitingSlotKey = false;
			_awaitingKeyInfoPick = false;
			_keyInfoRenameSession = false;
			_holdTyping = false;
			_draggingHold = false;
			CancelKeyInfoNameEdit(commit: false);
			_keyInfoToken = null;
			_keyInfoNameSwallowNextClick = false;
			PlayerInput.WritingText = false;
			_guardBackgroundUntilMouseUp = false;
			_blockSlotMouseButtonsUntilUp = 0;
			_swallowOpenClickMouse1UntilUp = false;
			_hasLastGoodMouseUi = false;
			ClearUiReuseSuppress();
			ClearPhysicalHoverLock();
			_prevHasFocus = true;
			_rejectLeftClickTipUntilMs = 0;
			_leftClickConfirmPending = false;
			_resetDisplaysConfirmPending = false;
			ClearPendingDisplayAliasUndo();
			ClearDisplayRestoredTip();
			_openedSnapshot = null;
			SlotCapture.Cancel();
			BindingInputEngine.IsRecording = false;
			WindowsKeySuppressor.SetModalWindowsKeySuppress(false);
		}

		// True from slot click until a key is captured or capture is cancelled.
		private static bool _awaitingSlotKey;
		/// <summary>True while waiting for a key to rename (entry via 表示名変更, not a combo slot).</summary>
		private static bool _awaitingKeyInfoPick;
		/// <summary>True after entering via 表示名変更 until the key-info side is fully reset.</summary>
		private static bool _keyInfoRenameSession;

		// Set when the panel closes while the left mouse button is still held (e.g. clicking "Save").
		// We keep guarding the background until that button is released, so the click's *release* can't
		// fall through onto the "Reset to default" button that sits right behind our Save button.
		private static bool _guardBackgroundUntilMouseUp;

		// Layout rectangles, recomputed each frame before input handling.
		private static Rectangle _panel;
		private static Rectangle _holdColumn;
		private static Rectangle _holdWhileHeldBox;
		private static Rectangle _holdOnReleaseBox;
		private static readonly Rectangle[] _slotBox = new Rectangle[4]; // 1..3 used
		private static Rectangle _clearBtn;
		private static Rectangle _closeBtn;
		private static Rectangle _backBtn;

		private const int PanelButtonW = 120;
		private const int PanelButtonH = 30;

		// Hold-duration controls. Always shown; editable only while Hold is on. Slider snaps to 10 ms
		// steps over [MinHoldMs, SliderMaxHoldMs]; the number box accepts [MinTypedHoldMs, MaxHoldMs] when typed.
		private static Rectangle _holdSliderFrame;
		private static Rectangle _holdNumberRect;
		private static Rectangle _holdDefaultBtn;
		private static float _holdInnerX;
		private static float _holdInnerY;
		private static float _holdInnerWidth;
		private static bool _holdTyping;
		private static bool _draggingHold;
		private static string _holdBuffer = string.Empty;
		private static bool _hoverHoldNumber;
		private static bool _hoverHoldSlider;
		private static bool _hoverHoldDefaultBtn;
		private static bool _hoverHoldWhileHeld;
		private static bool _hoverHoldOnRelease;
		private static KeyboardState _prevKeyState;

		/// <summary>Display-name Backspace: immediate first delete, then a short hold delay and steady repeat.</summary>
		private const long SlotRenameBackspaceInitialDelayMs = 450;
		private const long SlotRenameBackspaceRepeatMs = 100;
		/// <summary>Max chars while typing a display name (key-info name field).</summary>
		private const int MaxSlotRenameInputLength = 48;
		private const int MaxSlotDisplayLength = 24;
		private static readonly Color SlotRenameCompositionColor = new(255, 240, 20);

		// Right-side key info + display-name field (no keyboard figure / no live press tracking).
		// Left block keeps original geometry; panel grows right for splice + key-info with breathing room.
		private const int LeftBlockWidth = 600;
		private const int KeyInfoColW = 170;
		private const int KeyInfoNameFieldH = 28;
		private const int KeyInfoPad = 8;
		/// <summary>Gap between left block (Cancel/Save edge) and key-info box — holds the wavy splice.</summary>
		private const int KeyInfoSpliceGap = 16;
		/// <summary>Extra inset so the key-info box sits slightly right of the splice (not glued to the wave).</summary>
		private const int KeyInfoLeftInset = 22;
		/// <summary>Padding from key-info box to the panel's right outer edge.</summary>
		private const int KeyInfoRightPad = 14;
		private const int SlotRowGap = 10; // Hold / Slot1..3 only
		private const int PanelWidthTarget =
			LeftBlockWidth + KeyInfoSpliceGap + KeyInfoLeftInset + KeyInfoColW + KeyInfoRightPad; // ~816
		private static readonly Color SlotFill = new(46, 48, 64);
		private static readonly Color SlotBorder = new(110, 110, 140);
		private static readonly Color SlotHoverFill = new(70, 74, 100);
		private static readonly Color KeyInfoHintColor = new(150, 150, 165);
		private static readonly Color KeyInfoLabelColor = new(190, 190, 210);
		private static readonly Color KeyInfoValueColor = new(230, 230, 240);
		/// <summary>
		/// Cancel / Clear / Save / key-info name field: largest text that still fits the panel
		/// (shrinks for long multi-language strings).
		/// </summary>
		private const float PanelFillTextMaxScale = 2.5f;
		private const float PanelFillTextMinScale = 0.35f;
		private const float PanelFillTextPad = 4f;
		/// <summary>Key slots: 2× previous label size (was 0.8); long names still shrink to fit.</summary>
		private const float SlotLabelMaxScale = 1.6f;
		private const float SlotLabelMinScale = 0.40f;
		private static Rectangle _keyInfoColumn;
		private static Rectangle _keyInfoBody;
		private static Rectangle _keyInfoNameBox;
		/// <summary>Bottom-right: reset all key display aliases (same size as name field).</summary>
		private static Rectangle _resetDisplaysBtn;
		/// <summary>Token shown in the right column (edit target), not live Raw Input.</summary>
		private static string _keyInfoToken;
		private static bool _keyInfoNameEditing;
		private static string _keyInfoNameBuffer = string.Empty;
		/// <summary>Display alias at edit start (Escape restores this).</summary>
		private static string _keyInfoNameBaseline;
		private static bool _keyInfoNameOwnedBlockInput;
		private static bool _keyInfoNameBackspaceHeld;
		private static long _keyInfoNameNextBackspaceMs;
		/// <summary>After outside-click commit, ignore the same click for other panel actions.</summary>
		private static bool _keyInfoNameSwallowNextClick;
		/// <summary>Deferred MouseText for key-info (set during Draw, shown after hover clear).</summary>
		private static string _keyInfoHoverTip;

		private static string HoldDefaultButtonLabel =>
			Language.GetTextValue("Mods.AdvancedKeybinds.UI.Combo.HoldDefaultLabel") + " : 400ms";

		private const int ContentMargin = 18;
		private const int HeaderTopPad = 18;
		private const int HeaderTitleHeight = 22;
		private const int HeaderLineGap = 6;
		private const int HeaderPreviewHeight = 20;
		private const int PreviewToHoldGap = 10;
		private const int HoldToSlotGap = 12;
		private const int HoldControlBoxW = 112;
		private const int HoldControlBoxH = 28;
		private const int HoldControlGap = 10;

		// ~1.5 cm extra at 96 DPI for a 3-line footer hint band (longer locales wrap without shrinking).
		private const int FooterPanelExtraHeight = 73;
		private const int PanelHeight = 378 + FooterPanelExtraHeight;
		private const int FooterHintTopGap = 12;
		private const int FooterHintBottomGap = 14;
		private const int FooterHintLineSlots = 3;
		private const float FooterHintMaxScale = 1.15f;

		private static Rectangle _headerTitleRect;
		private static Rectangle _headerPreviewRect;
		private static Rectangle _footerHintRect;
		private static Rectangle _keyInfoGuideRect;

		public static void Open(string actionId, string label, Color accent)
		{
			if (string.IsNullOrEmpty(actionId))
				return;

			_actionId = actionId;
			_actionLabel = string.IsNullOrEmpty(label) ? actionId : label;
			_accent = accent.R + accent.G + accent.B > 0 ? accent : new Color(120, 110, 180);
			_capturingSlot = 0;
			SlotCapture.Cancel();
			_awaitingSlotKey = false;
			_awaitingKeyInfoPick = false;

			ComboBinding stored = BindingStorage.Get(actionId);
			if (stored != null)
			{
				_working = stored.Clone();
			}
			else
			{
				_working = new ComboBinding();
				string vanilla = VanillaRebindHelper.GetAssignedKey(actionId);
				if (!string.IsNullOrEmpty(vanilla))
					_working.Slot3 = vanilla;
			}

			_openedSnapshot = _working.Clone();

			IsOpen = true;
			_dirty = false;
			_escapeDismissBlockedUntilKeyUp = false;
			_escDismissGraceUntilMs = 0;
			_escapeRegisteredThisTick = false;
			_escapeConsumedThisTick = false;
			_awaitingSlotKey = false;
			_awaitingKeyInfoPick = false;
			_keyInfoRenameSession = false;
			_holdTyping = false;
			_draggingHold = false;
			_holdBuffer = _working.EffectiveHoldMs.ToString();
			CancelKeyInfoNameEdit(commit: false);
			_keyInfoToken = null;
			_keyInfoNameSwallowNextClick = false;
			_prevKeyState = Main.keyState;
			BindingInputEngine.IsRecording = true;
			WindowsKeySuppressor.SetModalWindowsKeySuppress(true);
			_prevLeft = true; // swallow the opening click's release
			_prevGameLeft = true;
			_prevGameRight = Main.mouseRight;
			_blockSlotMouseButtonsUntilUp = 0;
			_swallowOpenClickMouse1UntilUp = false;
			_hasLastGoodMouseUi = false;
			ClearUiReuseSuppress();
			ClearPhysicalHoverLock();
			_prevHasFocus = Main.hasFocus;
			_rejectLeftClickTipUntilMs = 0;
			_leftClickConfirmPending = false;
			_resetDisplaysConfirmPending = false;
			ClearPendingDisplayAliasUndo();
			ClearDisplayRestoredTip();
			RememberPanelMouseUiIfValid();
			if (AkDiag.Enabled)
			{
				AkDiag.Log("ComboPanel open action='" + actionId + "' keyInfoSide=v0.3.3 layout=slotRow+keyInfo");
				DisplayResolveDiag.LogBindingSnapshot("panel-open", actionId, _working);
			}
		}

		public static void Close(bool save, bool userInitiated = false)
		{
			if (!IsOpen)
				return;

#if AK_DIAGNOSTICS
			ComboPanelEscPathDiag.NoteStack(
				"ComboPanel.Close",
				"save=" + save + " userInitiated=" + userInitiated);
#endif

			// Hard stop: the Escape-registration tick must never become Image2.
			// (Do not use _escapeConsumedThisTick here — key-up-gate suppress sets it every tick.)
			if (_escapeRegisteredThisTick)
			{
#if AK_DIAGNOSTICS
				ComboPanelEscPathDiag.Note("Close.blockedDuringEscapeRegisteredTick");
#endif
				if (AkDiag.Enabled) AkDiag.Log("ComboPanel close blocked (escape registered this tick).");
				return;
			}

			if (!userInitiated && (IsEscapeDismissBlockedUntilKeyUp() || IsEscapeCaptureTickActive()))
			{
				if (AkDiag.Enabled) AkDiag.Log("ComboPanel close blocked (esc capture/key-up gate).");
				return;
			}

			// Commit any in-progress typed hold value before deciding what to persist.
			if (_holdTyping)
			{
				ApplyHoldBuffer();
				_holdTyping = false;
			}

			// Name-field edit: discard on panel close (avoid unintended mid-edit save).
			CancelKeyInfoNameEdit(commit: false);

			// Only write anything if the user actually changed a slot / Hold / duration / cleared. Otherwise
			// leave the existing binding exactly as it was (pressing Save on an untouched panel = go back).
			bool wasDirty = _dirty;

			// LeftClick-alone: first Save / outside-click / Escape-save shows a confirm tip and stays open;
			// the same action again commits.
			if (save
				&& wasDirty
				&& BindingRestrictions.IsLeftClickOnly(_actionId, _working)
				&& !_leftClickConfirmPending)
			{
				CancelSlotCaptureKeepPanel();
				ArmLeftClickConfirmPrompt();
				SoundEngine.PlaySound(SoundID.MenuTick);
				if (AkDiag.Enabled) AkDiag.Log("ComboPanel LeftClick-alone save needs confirm; panel stays open.");
				return;
			}

			if (save && wasDirty)
				Commit();

			IsOpen = false;
			_capturingSlot = 0;
			_dirty = false;
			_escapeDismissBlockedUntilKeyUp = false;
			_awaitingSlotKey = false;
			_awaitingKeyInfoPick = false;
			_keyInfoRenameSession = false;
			_holdTyping = false;
			_draggingHold = false;
			_keyInfoToken = null;
			_keyInfoNameSwallowNextClick = false;
			_openedSnapshot = null;
			SlotCapture.Cancel();
			BindingInputEngine.IsRecording = false;
			WindowsKeySuppressor.SetModalWindowsKeySuppress(false);
			_blockSlotMouseButtonsUntilUp = 0;
			_swallowOpenClickMouse1UntilUp = false;
			_hasLastGoodMouseUi = false;
			ClearUiReuseSuppress();
			ClearPhysicalHoverLock();
			ClearLeftClickConfirmPrompt();
			ClearResetDisplaysConfirm();
			ClearPendingDisplayAliasUndo();
			ClearDisplayRestoredTip();

			// If we closed on a mouse click, keep the background frozen until the button is released so
			// the click's release can't reach the "Reset to default" button behind the Save button.
			if (MouseInputHelper.IsPhysicalLeftDown())
				_guardBackgroundUntilMouseUp = true;

			if (AkDiag.Enabled) AkDiag.Log($"ComboPanel close save={save} dirty={wasDirty} userInitiated={userInitiated}");
		}

		private static void CancelSlotCaptureKeepPanel()
			=> CancelKeyCapture();

		/// <summary>
		/// A. FinishKeyCapture — 登録待ちだけ終了。ComboPanel は開いたまま（画像3）。
		/// Close / 親画面へ戻る処理は呼ばない。
		/// </summary>
		private static void FinishKeyCapture()
		{
			_capturingSlot = 0;
			_awaitingSlotKey = false;
			_awaitingKeyInfoPick = false;
			_swallowOpenClickMouse1UntilUp = false;
			SlotCapture.Cancel();
			// Do not RememberPanelMouseUiIfValid here: PlayerInput/UI may already have moved
			// Main.mouse via WASD/arrow navigation on the same registration key.
			SyncMouseEdgeTrackersFromHardware();
#if AK_DIAGNOSTICS
			ComboPanelEscPathDiag.Note("FinishKeyCapture panelOpen=" + IsOpen);
#endif
		}

		/// <summary>
		/// C. CancelKeyCapture — 登録確定せず登録待ちだけ終了。ComboPanel は維持。
		/// </summary>
		private static void CancelKeyCapture()
		{
			_capturingSlot = 0;
			_awaitingSlotKey = false;
			_awaitingKeyInfoPick = false;
			_swallowOpenClickMouse1UntilUp = false;
			SlotCapture.Cancel();
			ClearUiReuseSuppress();
			SyncMouseEdgeTrackersFromHardware();
#if AK_DIAGNOSTICS
			ComboPanelEscPathDiag.Note("CancelKeyCapture");
#endif
		}

		/// <summary>
		/// B. CloseComboPanel — ComboPanel を閉じ、親キーバインド画面へ戻る（通常時 ESC）。
		/// ESC キー登録パスからは絶対に呼ばない。
		/// </summary>
		private static void CloseComboPanel(bool save, bool userInitiated)
		{
#if AK_DIAGNOSTICS
			ComboPanelEscPathDiag.NoteStack("CloseComboPanel", "save=" + save);
#endif
			Close(save, userInitiated);
		}

		/// <summary>Reset the right-side key-info rename UI to empty (does not clear combo slots).</summary>
		private static void ResetKeyInfoRenameSide()
		{
			CancelKeyInfoNameEdit(commit: false);
			CancelSlotCaptureKeepPanel();
			_keyInfoToken = null;
			_keyInfoRenameSession = false;
			_keyInfoNameSwallowNextClick = false;
		}

		/// <summary>
		/// During a 表示名変更 session: Cancel = one step back, Clear = full reset of the right side.
		/// Returns true when the click was consumed.
		/// </summary>
		private static bool TryHandleKeyInfoSessionUndoOrReset(Point mouse, bool leftJustPressed)
		{
			if (!leftJustPressed || !_keyInfoRenameSession)
				return false;

			if (_backBtn.Contains(mouse))
			{
				if (_keyInfoNameEditing)
				{
					// Name edit → back to "press a key"
					CancelKeyInfoNameEdit(commit: false);
					_keyInfoToken = null;
					StartKeyInfoPick();
					SoundEngine.PlaySound(SoundID.MenuTick);
					return true;
				}

				if (_awaitingKeyInfoPick)
				{
					// Press-key wait → idle
					ResetKeyInfoRenameSide();
					SoundEngine.PlaySound(SoundID.MenuTick);
					return true;
				}

				if (!string.IsNullOrEmpty(_keyInfoToken))
				{
					// Key selected (not editing) → idle
					ResetKeyInfoRenameSide();
					SoundEngine.PlaySound(SoundID.MenuTick);
					return true;
				}

				return false;
			}

			if (_clearBtn.Contains(mouse))
			{
				// Full reset of the rename side only (combo slots untouched).
				ResetKeyInfoRenameSide();
				SoundEngine.PlaySound(SoundID.MenuClose);
				return true;
			}

			return false;
		}

		/// <summary>
		/// Restores slots / Hold / duration to the state from when this panel was opened.
		/// Does not close the panel or write to storage (Save still required to persist).
		/// </summary>
		private static void RestoreOpenedSnapshot()
		{
			SlotCapture.Cancel();
			_capturingSlot = 0;
			_awaitingSlotKey = false;
			_awaitingKeyInfoPick = false;
			_keyInfoRenameSession = false;
			_holdTyping = false;
			_draggingHold = false;
			CancelKeyInfoNameEdit(commit: false);
			ResetKeyInfoRenameSide();
			_blockSlotMouseButtonsUntilUp = 0;
			_swallowOpenClickMouse1UntilUp = false;
			_rejectLeftClickTipUntilMs = 0;
			_leftClickConfirmPending = false;
			_resetDisplaysConfirmPending = false;
			ClearDisplayRestoredTip();

			_working = _openedSnapshot != null ? _openedSnapshot.Clone() : new ComboBinding();
			_holdBuffer = _working.EffectiveHoldMs.ToString();
			_dirty = false;

			// Restore a display name cleared by right-click on the key-info panel (if any).
			RestorePendingDisplayAliasUndo();

			SoundEngine.PlaySound(SoundID.MenuOpen);
			if (AkDiag.Enabled) AkDiag.Log("ComboPanel restored opened snapshot.");
		}

		/// <summary>
		/// True while background UI input must be neutralized: the panel is open, OR it just closed on a
		/// mouse click and that button is still held (so the release doesn't fall through to a button
		/// behind the panel).
		/// </summary>
		public static bool ShouldBlockBackgroundInput()
		{
			if (IsOpen)
				return true;

			if (_guardBackgroundUntilMouseUp)
			{
				if (MouseInputHelper.IsPhysicalLeftDown())
					return true;

				_guardBackgroundUntilMouseUp = false;
			}

			return false;
		}

		public static void ForceClose()
		{
			_escapeDismissBlockedUntilKeyUp = false;
			_escapeRegisteredThisTick = false;
			_escapeConsumedThisTick = false;
			_escDismissGraceUntilMs = 0;
			Close(save: false, userInitiated: true);
		}

		/// <summary>
		/// Handles modal input for the combo panel. Must run right after PlayerInput.UpdateInput and
		/// before any UI reads Escape / MenuInquiry. Slot capture is polled here (not in Draw) so Escape
		/// registration and ESC swallowing happen in the same input phase.
		/// </summary>
		/// <summary>Polls slot capture early in the input tick (toggle-lock keys need per-frame OS polling).</summary>
		public static void PollSlotCaptureIfActive()
		{
			if (!IsOpen || (!_awaitingSlotKey && !SlotCapture.IsCapturing))
				return;

			// Mid-frame UI Update can run after the per-frame Mouse.GetState cache was filled
			// with "button up". Refresh so purple-wait Mouse1/Mouse2 edges are not missed.
			MouseInputHelper.RefreshFrameMouseState();

			bool leftDown = MouseInputHelper.IsPhysicalLeftDown();
			bool rightDown = MouseInputHelper.IsPhysicalRightDown();
			bool gameLeftDown = Main.mouseLeft;
			bool gameRightDown = Main.mouseRight;

			// Keep the purple "press a key" state while Terraria is inactive (e.g. clicking
			// another desktop to screenshot). Do not accept keys or pending edges then.
			// Foreground flips before Main.hasFocus 窶・treat that the same as unfocused.
			bool gameActive = Main.hasFocus && GameWindowHandle.IsGameProcessForeground();
			if (!gameActive)
			{
				SlotCapture.DiscardPendingEdgesWhileInactive();
				// Sync edges while inactive so focus-regain does not see a fake justPressed dismiss.
				NoteCapturePollMouseEdges(leftDown, rightDown, gameLeftDown, gameRightDown);
				_prevHasFocus = false;
				return;
			}

			// Focus just returned: swallow residual mouse edges (the click that restored focus /
			// leftover Main.mouseLeft from deactivate). Capture stays purple; SlotCapture.Poll
			// re-baselines held keys via AbsorbPressedIntoBaselineAfterFocusRegain.
			if (!_prevHasFocus)
			{
				SlotCapture.DiscardPendingEdgesWhileInactive();
				NoteCapturePollMouseEdges(leftDown, rightDown, gameLeftDown, gameRightDown);
				_prevHasFocus = true;
				ReleaseOpenClickMouse1SwallowIfNeeded();
				ComputeLayout();
				// Do not Poll mouse binds this frame 窶・residual deactivate/restore click must not
				// "capture Mouse1 竊・outside-slot dismiss" and clear purple wait.
				return;
			}

			_prevHasFocus = true;
			ReleaseOpenClickMouse1SwallowIfNeeded();

			// Input phase runs before Draw: UI dismiss gestures must win over mouse capture.
			ComputeLayout();
			RememberPanelMouseUiIfValid();
			bool leftJustPressed = leftDown && !_prevLeft;
			bool gameLeftJustPressed = gameLeftDown && !_prevGameLeft;
			bool gameRightJustPressed = gameRightDown && !_prevGameRight;

			if (TryDismissCaptureWithPanelButton(leftJustPressed))
			{
				// Consume edges so nested UserInterface.Update (mouseX poisoned to -1000) cannot
				// re-fire the same click as "outside slot" dismiss in the same frame.
				NoteCapturePollMouseEdges(leftDown, rightDown, gameLeftDown, gameRightDown);
				return;
			}

			// Display-name target wait: Left/Right click complete as Mouse1/Mouse2 BEFORE SlotCapture.Poll.
			if (TryCompleteKeyInfoPickWithPanelMouseButtons(
				leftJustPressed,
				rightDown && !_prevRight,
				gameLeftJustPressed,
				gameRightJustPressed))
			{
				NoteCapturePollMouseEdges(leftDown, rightDown, gameLeftDown, gameRightDown);
				return;
			}

			// Combo slots: same Mouse1/Mouse2 direct bind (do not rely on SlotCapture.Poll for buttons).
			if (TryCompleteSlotCaptureWithPanelMouseButtons(
				leftJustPressed,
				rightDown && !_prevRight,
				gameLeftJustPressed,
				gameRightJustPressed))
			{
				NoteCapturePollMouseEdges(leftDown, rightDown, gameLeftDown, gameRightDown);
				return;
			}

			// Outside-slot dismiss must use game-processed clicks only. Physical Mouse.GetState
			// fires for other-window deactivate (coords often clamped "inside" client) and was
			// ending purple wait — break SS-while-waiting.
			if (TryDismissCaptureWithOutsideClick(gameLeftJustPressed, gameRightJustPressed))
			{
				NoteCapturePollMouseEdges(leftDown, rightDown, gameLeftDown, gameRightDown);
				return;
			}

			ProcessSlotCaptureInput();
			NoteCapturePollMouseEdges(leftDown, rightDown, gameLeftDown, gameRightDown);
		}

		/// <summary>
		/// Advance ComboPanel mouse edge trackers from capture polls. Capture runs many times per
		/// frame (PlayerInput + each background UserInterface); without this, the same justPressed
		/// is re-evaluated against poisoned (-1000) cursor coords and cancels purple wait.
		/// </summary>
		private static void NoteCapturePollMouseEdges(bool leftDown, bool rightDown, bool gameLeftDown, bool gameRightDown)
		{
			_prevLeft = leftDown;
			_prevRight = rightDown;
			_prevGameLeft = gameLeftDown;
			_prevGameRight = gameRightDown;
		}

		private static void ReleaseOpenClickMouse1SwallowIfNeeded()
		{
			if (!_swallowOpenClickMouse1UntilUp)
				return;
			if (!MouseInputHelper.IsPhysicalLeftDown())
				_swallowOpenClickMouse1UntilUp = false;
		}

		/// <summary>
		/// True while background-UI blocking has shoved Main.mouseX/Y off-screen. Slot/button
		/// hit-tests that use MouseScreen must not run then (looks like "outside capturing slot").
		/// </summary>
		private static bool IsMainMouseCursorPoisonedForHitTest()
			=> Main.mouseX <= BackgroundUiCursorPoison || Main.mouseY <= BackgroundUiCursorPoison;

		private static void ClearUiReuseSuppress()
		{
			_uiReuseSuppressActive = false;
			_uiReuseSuppressToken = null;
		}

		private static void ClearPhysicalHoverLock()
		{
			_physicalHoverLockActive = false;
		}

		/// <summary>
		/// Snapshot the panel hit-test UI point when entering purple wait (same space as slot boxes).
		/// OS client coords are only the move-detection anchor — keyboard UI nav must not move hover.
		/// </summary>
		private static void ArmPhysicalHoverLockAt(int panelMouseUiX, int panelMouseUiY)
		{
			MouseState state = Mouse.GetState();
			_physicalHoverAnchorClientX = state.X;
			_physicalHoverAnchorClientY = state.Y;
			_physicalHoverUiX = panelMouseUiX;
			_physicalHoverUiY = panelMouseUiY;
			_physicalHoverLockActive = true;
			_lastGoodMouseUiX = panelMouseUiX;
			_lastGoodMouseUiY = panelMouseUiY;
			_hasLastGoodMouseUi = true;
		}

		/// <summary>
		/// A only: skip idle UI for Space / WASD / arrows / Enter until that token releases.
		/// Does not clear the physical hover lock. Non-nav keys (e.g. Kana) must not arm this.
		/// </summary>
		private static void ArmUiReuseSuppressAfterCapture(string token)
		{
			if (!_physicalHoverLockActive)
			{
				if (_hasLastGoodMouseUi)
					ArmPhysicalHoverLockAt(_lastGoodMouseUiX, _lastGoodMouseUiY);
				else
				{
					GetPhysicalMouseUi(out int physX, out int physY, out _, out _);
					ArmPhysicalHoverLockAt(physX, physY);
				}
			}

			if (!ComboPanelCaptureUiReuse.ShouldArmUiReuseSuppress(token))
			{
				ClearUiReuseSuppress();
				return;
			}

			_uiReuseSuppressToken = token;
			_uiReuseSuppressActive = true;
			PollUiReuseSuppressRelease();
		}

		private static void PollUiReuseSuppressRelease()
		{
			if (!_uiReuseSuppressActive)
				return;

			string token = _uiReuseSuppressToken;
			bool stillDown = !string.IsNullOrEmpty(token) && KeyToken.IsDown(token);
			if (ComboPanelCaptureUiReuse.ShouldClearSuppress(_uiReuseSuppressActive, stillDown))
				ClearUiReuseSuppress();
		}

		private static bool IsUiReuseSuppressBlockingIdle()
		{
			PollUiReuseSuppressRelease();
			if (!_uiReuseSuppressActive)
				return false;

			string token = _uiReuseSuppressToken;
			bool stillDown = !string.IsNullOrEmpty(token) && KeyToken.IsDown(token);
			return ComboPanelCaptureUiReuse.ShouldSkipIdleUiInput(_uiReuseSuppressActive, stillDown);
		}

		private static void GetPhysicalMouseUi(out int physicalUiX, out int physicalUiY, out int clientX, out int clientY)
		{
			MouseState state = Mouse.GetState();
			clientX = state.X;
			clientY = state.Y;
			float scale = Main.UIScale;
			if (scale < 0.01f)
				scale = 1f;
			physicalUiX = (int)(state.X / scale);
			physicalUiY = (int)(state.Y / scale);
		}

		private static void RememberPanelMouseUiIfValid()
		{
			if (!ComboPanelCaptureUiReuse.ShouldRememberMainMouse(
				_physicalHoverLockActive,
				_uiReuseSuppressActive))
				return;

			if (IsMainMouseCursorPoisonedForHitTest())
				return;

			_lastGoodMouseUiX = Main.mouseX;
			_lastGoodMouseUiY = Main.mouseY;
			_hasLastGoodMouseUi = true;
		}

		/// <summary>
		/// UI-space cursor for ComboPanel hit-tests / hover / click (same basis).
		/// While the capture hover lock is active, keep the slot-click panel point until the OS
		/// cursor moves (ignore Main.mouse keyboard UI snap). During purple slot wait, never drop
		/// the lock on tiny OS jitter — that made Mouse1/Mouse2 look "outside" the purple slot.
		/// While the panel is open and unlocked, always use <see cref="GetPhysicalMouseUi"/> so
		/// vanilla WASD UI-nav cannot move slot hover via Main.mouse.
		/// </summary>
		private static Point GetPanelMouseUi()
		{
			PollUiReuseSuppressRelease();

			GetPhysicalMouseUi(out int physUiX, out int physUiY, out int clientX, out int clientY);

			if (_physicalHoverLockActive)
			{
				bool holdLockDuringPurpleWait = _awaitingSlotKey || SlotCapture.IsCapturing;

				ComboPanelCaptureUiReuse.TryResolveHover(
					_physicalHoverLockActive,
					_physicalHoverUiX,
					_physicalHoverUiY,
					clientX,
					clientY,
					_physicalHoverAnchorClientX,
					_physicalHoverAnchorClientY,
					physUiX,
					physUiY,
					ComboPanelCaptureUiReuse.PhysicalMoveThresholdPx,
					out int lockedX,
					out int lockedY,
					out bool clearLockForMove);

				if (clearLockForMove && !holdLockDuringPurpleWait)
					ClearPhysicalHoverLock();
				else
				{
					_lastGoodMouseUiX = lockedX;
					_lastGoodMouseUiY = lockedY;
					_hasLastGoodMouseUi = true;
					return new Point(lockedX, lockedY);
				}
			}

			// Panel open (idle / after lock clear): physical mouse only — not Main.mouse.
			// Hover borders and click hit-tests share this path (Do not split bases).
			if (IsOpen)
			{
				_lastGoodMouseUiX = physUiX;
				_lastGoodMouseUiY = physUiY;
				_hasLastGoodMouseUi = true;
				return new Point(physUiX, physUiY);
			}

			if (ComboPanelCaptureUiReuse.PreferPhysicalMouseOverMain(
				physicalHoverLockActive: false,
				Main.mouseX,
				Main.mouseY,
				BackgroundUiCursorPoison))
			{
				_lastGoodMouseUiX = physUiX;
				_lastGoodMouseUiY = physUiY;
				_hasLastGoodMouseUi = true;
				return new Point(physUiX, physUiY);
			}

			RememberPanelMouseUiIfValid();

			ComboPanelMouseUiResolve.TryResolve(
				Main.mouseX,
				Main.mouseY,
				BackgroundUiCursorPoison,
				_hasLastGoodMouseUi,
				_lastGoodMouseUiX,
				_lastGoodMouseUiY,
				physUiX,
				physUiY,
				out int x,
				out int y,
				out _,
				out _);
			return new Point(x, y);
		}

		private static void SyncMouseEdgeTrackersFromHardware()
		{
			_prevLeft = MouseInputHelper.IsPhysicalLeftDown();
			_prevRight = MouseInputHelper.IsPhysicalRightDown();
			_prevGameLeft = Main.mouseLeft;
			_prevGameRight = Main.mouseRight;
		}

		/// <summary>
		/// Cancel / Clear during purple wait: exit capture only (do not bind that click as Mouse1,
		/// and do not restore/clear the working binding).
		/// </summary>
		private static bool TryDismissCaptureWithPanelButton(bool leftJustPressed)
		{
			if (!leftJustPressed)
				return false;

			if (!_awaitingSlotKey && !SlotCapture.IsCapturing)
				return false;

			// Open-click Mouse1 must not cancel/clear while still held.
			if (_swallowOpenClickMouse1UntilUp)
				return false;

			if (!IsCaptureFocusContextSafe())
				return false;

			Point mouse = GetPanelMouseUi();
			if (_keyInfoRenameSession && TryHandleKeyInfoSessionUndoOrReset(mouse, leftJustPressed: true))
				return true;

			if (!_backBtn.Contains(mouse) && !_clearBtn.Contains(mouse))
				return false;

			DismissSlotCaptureOnly(MouseTokenToButtonMask("Mouse1"), "Cancel/Clear");
			return true;
		}

		/// <summary>
		/// Left/right-click outside the purple capturing slot during wait: exit capture only
		/// (do not bind Mouse1/Mouse2). Click on the purple slot itself still binds that button.
		/// Outside left-click here is capture cancel only — not LeftClick-alone Save confirm.
		/// Callers must pass game-processed mouse edges (<see cref="Main.mouseLeft"/> /
		/// <see cref="Main.mouseRight"/>), not physical OS buttons — those fire on other-window
		/// deactivate and must not clear the purple wait (screenshot use case).
		/// Display-name key pick AND combo-slot purple wait: Mouse1/Mouse2 are selectable keys
		/// (same as pressing Q) — do not treat panel mouse as dismiss; dedicated complete paths
		/// register them. Cancel via Clear/Cancel buttons (or Esc where applicable).
		/// </summary>
		private static bool TryDismissCaptureWithOutsideClick(bool gameLeftJustPressed, bool gameRightJustPressed)
		{
			if (!gameLeftJustPressed && !gameRightJustPressed)
				return false;

			if (!_awaitingSlotKey && !SlotCapture.IsCapturing)
				return false;

			// Key-info and combo-slot purple wait: LMB/RMB register as Mouse1/Mouse2, not cancel.
			// Outside-slot cancel via mouse fought the bind path (hit-tests vs hover lock / poison).
			if (_awaitingKeyInfoPick || (_awaitingSlotKey && _capturingSlot >= 1 && _capturingSlot <= 3))
				return false;

			// Deactivate / other-window clicks: never cancel purple wait (SS use case).
			if (!IsCaptureFocusContextSafe())
				return false;

			// Registration-start Mouse1 must neither register nor cancel until released.
			if (gameLeftJustPressed && _swallowOpenClickMouse1UntilUp)
				return false;

			if (IsCursorInsideCapturingSlot())
				return false;

			if (gameLeftJustPressed)
			{
				DismissSlotCaptureOnly(MouseTokenToButtonMask("Mouse1"), "outside capturing slot left-click");
				return true;
			}

			DismissSlotCaptureOnly(MouseTokenToButtonMask("Mouse2"), "outside capturing slot right-click");
			return true;
		}

		private static void DismissSlotCaptureOnly(int blockMouseMask, string reason)
		{
			bool clearKeyInfo = _awaitingKeyInfoPick
				|| _keyInfoRenameSession
				|| !string.IsNullOrEmpty(_keyInfoToken);

			CancelSlotCaptureKeepPanel();
			if (clearKeyInfo)
			{
				_keyInfoToken = null;
				_keyInfoRenameSession = false;
			}

			if (blockMouseMask != 0)
				_blockSlotMouseButtonsUntilUp |= blockMouseMask;

			SoundEngine.PlaySound(SoundID.MenuTick);
			if (AkDiag.Enabled) AkDiag.Log("ComboPanel capture dismissed via " + reason + " (no mouse bind).");
		}

		/// <summary>
		/// After SlotCapture briefly accepted a deactivate mouse edge, restore purple wait
		/// without writing a binding (slots stay as they were 窶・usually "(縺ｪ縺・").
		/// </summary>
		private static void RearmSlotCaptureAfterSpuriousMouse(string token)
		{
			_awaitingSlotKey = true;
			// Keep key-info pick mode if that is what was interrupted.
			_swallowOpenClickMouse1UntilUp = MouseInputHelper.IsPhysicalLeftDown()
				|| string.Equals(token, "Mouse1", StringComparison.OrdinalIgnoreCase);
			SlotCapture.Begin(forceBlockMouse1: _swallowOpenClickMouse1UntilUp
				|| MouseInputHelper.IsPhysicalLeftDown());
			int mask = MouseTokenToButtonMask(token);
			if (mask != 0)
				_blockSlotMouseButtonsUntilUp |= mask;
		}

		/// <summary>Call once at the start of each PlayerInput tick before Escape handling.</summary>
		public static void BeginInputTickEscapeCaptureState()
		{
			_escapeInputTickSerial++;
			_escapeRegisteredThisTick = false;
			_escapeConsumedThisTick = false;
			_escapeCaptureInputTick = _escapeInputTickSerial;

			// Gate must use PHYSICAL Escape — Main.keyState is stripped by ConsumeEscape and would
			// falsely look like "released", clearing the gate and letting the same press Close the panel.
			if (ComboPanelEscapeRegistrationPolicy.ShouldClearDismissKeyUpGate(
				_escapeDismissBlockedUntilKeyUp,
				IsPhysicalEscapeDown()))
			{
				_escapeDismissBlockedUntilKeyUp = false;
#if AK_DIAGNOSTICS
				ComboPanelEscPathDiag.Note("DismissKeyUpGate.cleared");
#endif
			}

#if AK_DIAGNOSTICS
			ComboPanelEscPathDiag.Note("BeginInputTick");
#endif
		}

		/// <summary>Marks this input tick as Escape-registration-only (survives clearing purple wait).</summary>
		private static void MarkEscapeRegisteredAndConsumedThisTick(string reason)
		{
			_escapeRegisteredThisTick = true;
			_escapeConsumedThisTick = true;
			_escapeCaptureInputTick = _escapeInputTickSerial;
			_escapeDismissBlockedUntilKeyUp = true;
#if AK_DIAGNOSTICS
			ComboPanelEscPathDiag.Note("MarkEscapeCaptureTick", reason);
#endif
		}

		private static bool IsEscapeCaptureTickActive()
			=> ComboPanelEscapeRegistrationPolicy.IsEscapeCaptureTick(
				IsRegistrationWaitActive(),
				_escapeRegisteredThisTick,
				_escapeConsumedThisTick);

		private static bool IsEscapeDismissBlockedUntilKeyUp()
			=> _escapeDismissBlockedUntilKeyUp;

		/// <summary>Purple "press a key…" / key-info pick — Escape must register, never navigate back.</summary>
		private static bool IsRegistrationWaitActive()
			=> ComboPanelEscapeRegistrationPolicy.IsRegistrationWait(
				_awaitingSlotKey,
				SlotCapture.IsCapturing,
				_capturingSlot,
				_awaitingKeyInfoPick);

		public static void HandleEscape()
		{
			if (!IsOpen)
				return;

#if AK_DIAGNOSTICS
			ComboPanelEscPathDiag.Note("HandleEscape.enter");
#endif

			bool escapeJustPressed =
				Main.keyState.IsKeyDown(Keys.Escape) && !Main.oldKeyState.IsKeyDown(Keys.Escape);

			// Display-name text entry: ESC cancels typing only — keep ComboPanel open (Image4).
			// Arm key-up gate: ConsumeEscape strips Escape from keyState, so the next tick would
			// otherwise see a fresh Escape edge and CloseComboPanel (NG → Keybindings).
			if (_keyInfoNameEditing)
			{
				if (escapeJustPressed)
				{
					CancelKeyInfoNameEdit(commit: false);
					SoundEngine.PlaySound(SoundID.MenuClose);
				}

				_escapeDismissBlockedUntilKeyUp = true;
				SuppressEscapeDerivedTriggers("HandleEscape.keyInfoNameEdit");
				return;
			}

			// Image1: registration wait / same-tick after Escape save → register only, never CloseComboPanel.
			if (IsEscapeCaptureTickActive())
			{
				if (IsRegistrationWaitActive() && Main.hasFocus)
				{
					if (!TryRegisterEscapeDuringRegistrationWait())
						ProcessSlotCaptureInput();
				}
				else if (IsRegistrationWaitActive())
					SlotCapture.DiscardPendingEdgesWhileInactive();

				SuppressEscapeDerivedTriggers("HandleEscape.captureTick");
#if AK_DIAGNOSTICS
				ComboPanelEscPathDiag.Note("HandleEscape.captureTickHold open=" + IsOpen);
#endif
				return;
			}

			// Same physical Escape press that registered: Image3 stay until key-up (physical).
			if (IsEscapeDismissBlockedUntilKeyUp())
			{
				if (IsPhysicalEscapeDown())
					SuppressEscapeDerivedTriggers("HandleEscape.keyUpGate");
#if AK_DIAGNOSTICS
				ComboPanelEscPathDiag.Note("HandleEscape.keyUpGate open=" + IsOpen);
#endif
				return;
			}

			if (!escapeJustPressed)
				return;

			if (!ComboPanelEscapeRegistrationPolicy.ShouldDismissPanelOnEscape(
				IsRegistrationWaitActive(),
				_escapeRegisteredThisTick,
				_escapeConsumedThisTick,
				_escapeDismissBlockedUntilKeyUp))
			{
#if AK_DIAGNOSTICS
				ComboPanelEscPathDiag.Note("HandleEscape.dismissBlockedByPolicy");
#endif
				return;
			}

			if (_resetDisplaysConfirmPending)
			{
				ClearResetDisplaysConfirm();
				SoundEngine.PlaySound(SoundID.MenuClose);
				if (AkDiag.Enabled) AkDiag.Log("ComboPanel reset-displays confirm cancelled (Escape).");
			}
			else if (_holdTyping)
			{
				_holdTyping = false;
				_holdBuffer = _working.EffectiveHoldMs.ToString();
			}
			else
			{
				// Idle ComboPanel ESC → Image2 (parent). Never used from Escape key registration.
#if AK_DIAGNOSTICS
				ComboPanelEscPathDiag.NoteStack("HandleEscape.CloseComboPanel");
#endif
				if (AkDiag.Enabled) AkDiag.Log("ComboPanel Escape dismiss (idle).");
				CloseComboPanel(save: true, userInitiated: true);
				BeginEscDismissGrace();
			}

			ConsumeEscape();
		}

		/// <summary>Hardware Escape, not Main.keyState (which ConsumeEscape rewrites).</summary>
		private static bool IsPhysicalEscapeDown()
		{
			try
			{
				if (Keyboard.GetState().IsKeyDown(Keys.Escape))
					return true;
			}
			catch
			{
				// fall through
			}

			return WindowsKeySuppressor.IsShellBlockedEscapeDown;
		}

		private static bool IsEscapeDownForRegistration()
			=> IsPhysicalEscapeDown() || Main.keyState.IsKeyDown(Keys.Escape);

		/// <summary>
		/// Registers Escape during purple wait / key-info pick. Returns true when the Escape edge was handled.
		/// </summary>
		private static bool TryRegisterEscapeDuringRegistrationWait()
		{
			if (!IsEscapeDownForRegistration() || Main.oldKeyState.IsKeyDown(Keys.Escape))
				return false;

			// Ctrl+Esc is handled as shell-blocked Escape elsewhere; still treat as Escape registration
			// when the suppressor reports the shell-blocked Escape chord.
			bool ctrlHeld = Main.keyState.IsKeyDown(Keys.LeftControl) || Main.keyState.IsKeyDown(Keys.RightControl);
			if (ctrlHeld && !WindowsKeySuppressor.IsShellBlockedEscapeDown)
				return false;

			// Mark BEFORE clearing registration wait so same-tick parent paths see capture-tick.
			MarkEscapeRegisteredAndConsumedThisTick("TryRegisterEscape");

			if (_awaitingKeyInfoPick)
			{
#if AK_DIAGNOSTICS
				ComboPanelEscPathDiag.Note("EscapeSaved.keyInfo");
#endif
				CompleteKeyInfoPick("Escape");
			}
			else if (_capturingSlot >= 1 && _capturingSlot <= 3)
			{
#if AK_DIAGNOSTICS
				ComboPanelEscPathDiag.Note("EscapeSaved.slot=" + _capturingSlot);
#endif
				CompleteSlotCapture("Escape");
			}
			else
			{
				_escapeRegisteredThisTick = false;
				_escapeDismissBlockedUntilKeyUp = false;
				return false;
			}

			SuppressEscapeDerivedTriggers("TryRegisterEscape.done");
			return true;
		}

		private static void ProcessSlotCaptureInput()
		{
			MouseInputHelper.AdvanceScrollWheel();

			if (TryRegisterEscapeDuringRegistrationWait())
				return;

			SlotCapture.PollResult result = SlotCapture.Poll(out string token);
			if (result == SlotCapture.PollResult.Captured)
			{
				// Physical Mouse1 still fires when clicking another window while hasFocus lags.
				// Poll may accept Mouse1; treating that as "outside slot dismiss" cleared purple.
				// Deactivate / outside-client: keep purple wait (re-arm), do not bind, do not dismiss.
				// Main.mouse poison alone must NOT block in-slot Mouse1/Mouse2 (panel hit-tests use
				// GetPanelMouseUi).
				if (KeyToken.IsMouseButtonToken(token) && !IsCaptureFocusContextSafe())
				{
					if (AkDiag.Enabled)
					{
						AkDiag.Log(
						"ComboPanel keep purple: ignore mouse token=" + token
						+ " (inactive/outside client — not L-Click bind, not dismiss)");
					}
					RearmSlotCaptureAfterSpuriousMouse(token);
					return;
				}

				// Safety net: in-window dismiss UI gestures must never become a mouse bind.
				if (KeyToken.IsMouseButtonToken(token) && ShouldIgnoreCapturedMouseAsDismissGesture(token))
				{
					DismissSlotCaptureOnly(MouseTokenToButtonMask(token), "mouse over dismiss target");
					return;
				}

				if (_awaitingKeyInfoPick)
					CompleteKeyInfoPick(token);
				else
					CompleteSlotCapture(token);
				return;
			}

			if (Main.keyState.IsKeyDown(Keys.Escape))
				ConsumeEscape();
		}

		private static bool ShouldIgnoreCapturedMouseAsDismissGesture(string token)
		{
			bool isMouse1 = string.Equals(token, "Mouse1", StringComparison.OrdinalIgnoreCase);
			bool isMouse2 = string.Equals(token, "Mouse2", StringComparison.OrdinalIgnoreCase);
			if (!isMouse1 && !isMouse2)
				return IsCursorOverCaptureDismissButton();

			Point mouse = GetPanelMouseUi();
			bool overDismiss = _backBtn.Contains(mouse) || _clearBtn.Contains(mouse);

			// Key-info and combo-slot purple wait: Mouse1/Mouse2 are keys to bind, not cancel.
			if (_awaitingKeyInfoPick || (_awaitingSlotKey && _capturingSlot >= 1 && _capturingSlot <= 3))
				return overDismiss;

			return ComboPanelCaptureUiReuse.ShouldDismissCaptureOnMouse(
				_swallowOpenClickMouse1UntilUp,
				isMouse1Token: isMouse1,
				cursorInsideCapturingSlot: IsCursorInsideCapturingSlot(),
				cursorOverDismissButton: overDismiss);
		}

		private static bool IsCursorOverCaptureDismissButton()
		{
			Point mouse = GetPanelMouseUi();
			return _backBtn.Contains(mouse) || _clearBtn.Contains(mouse);
		}

		private static bool IsCursorInsideCapturingSlot()
		{
			Point mouse = GetPanelMouseUi();

			// Display-name pick: the whole key-info cell is the "purple" target.
			if (_awaitingKeyInfoPick)
				return _keyInfoColumn.Contains(mouse) || _keyInfoNameBox.Contains(mouse);

			if (_capturingSlot < 1 || _capturingSlot > 3)
				return false;

			return _slotBox[_capturingSlot].Contains(mouse);
		}

		private static void StartKeyInfoPick()
		{
			if (_awaitingSlotKey || SlotCapture.IsCapturing)
				return;

			CancelKeyInfoNameEdit(commit: false);
			ClearLeftClickConfirmPrompt();
			_capturingSlot = 0;
			_keyInfoToken = null;
			_keyInfoRenameSession = true;
			_awaitingKeyInfoPick = true;
			_awaitingSlotKey = true;
			_swallowOpenClickMouse1UntilUp = true;
			Point pickMouse = GetPanelMouseUi();
			ArmPhysicalHoverLockAt(pickMouse.X, pickMouse.Y);
			if (AkDiag.Enabled) AkDiag.Log("ComboPanel keyinfo-pick START -> SlotCapture.Begin");
			SlotCapture.Begin(forceBlockMouse1: true);
			SoundEngine.PlaySound(SoundID.MenuOpen);
		}

		/// <summary>
		/// During display-name "which key?" wait, Left/Right click select Mouse1/Mouse2
		/// (storage tokens; display labels remain LeftClick/RightClick via alias path).
		/// Accepts physical or game mouse edges. Mouse buttons are keys (same as pressing Q).
		/// Open-click Mouse1 swallow still applies. Cancel/Clear buttons are excluded.
		/// </summary>
		private static bool TryCompleteKeyInfoPickWithPanelMouseButtons(
			bool leftJustPressed,
			bool rightJustPressed,
			bool gameLeftJustPressed,
			bool gameRightJustPressed)
		{
			if (!_awaitingKeyInfoPick || !_awaitingSlotKey)
				return false;

			if (!IsCaptureFocusContextSafe())
				return false;

			bool leftEdge = leftJustPressed || gameLeftJustPressed;
			bool rightEdge = rightJustPressed || gameRightJustPressed;
			if (!leftEdge && !rightEdge)
				return false;

			Point mouse = GetPanelMouseUi();
			if (_backBtn.Contains(mouse) || _clearBtn.Contains(mouse))
				return false;

			if (leftEdge)
			{
				if (_swallowOpenClickMouse1UntilUp)
					return false;

				CompleteKeyInfoPick("Mouse1");
				return true;
			}

			CompleteKeyInfoPick("Mouse2");
			return true;
		}

		/// <summary>
		/// Combo-slot purple wait: bind Mouse1/Mouse2 the same way as display-name key pick —
		/// no slot hit-test (that path kept losing to hover-lock / Main.mouse mismatch and
		/// outside-dismiss). Open-click Mouse1 stays swallowed until physical release.
		/// Cancel via 戻る／解除 only (same as key-info mouse pick).
		/// </summary>
		private static bool TryCompleteSlotCaptureWithPanelMouseButtons(
			bool leftJustPressed,
			bool rightJustPressed,
			bool gameLeftJustPressed,
			bool gameRightJustPressed)
		{
			if (!_awaitingSlotKey || _awaitingKeyInfoPick)
				return false;

			if (_capturingSlot < 1 || _capturingSlot > 3)
				return false;

			// Do not require IsCaptureFocusContextSafe here — key-info uses it, but slot mouse bind
			// was failing when that gate disagreed with an obvious in-panel click. Focus is still
			// required indirectly (Poll only runs while the panel is open and game is interactive).

			bool leftEdge = leftJustPressed || gameLeftJustPressed;
			bool rightEdge = rightJustPressed || gameRightJustPressed;
			if (!leftEdge && !rightEdge)
				return false;

			Point mouse = GetPanelMouseUi();
			if (_backBtn.Contains(mouse) || _clearBtn.Contains(mouse))
				return false;

			if (leftEdge)
			{
				if (_swallowOpenClickMouse1UntilUp)
					return false;

				CompleteSlotCapture("Mouse1");
				return true;
			}

			CompleteSlotCapture("Mouse2");
			return true;
		}

		private static void CompleteKeyInfoPick(string token)
		{
			token = KeyToken.NormalizeCapturedToken(token);
			if (AkDiag.Enabled) AkDiag.Log("ComboPanel CompleteKeyInfoPick token=" + token);

			bool isEscape = string.Equals(token, "Escape", StringComparison.OrdinalIgnoreCase);
			if (isEscape)
				MarkEscapeRegisteredAndConsumedThisTick("CompleteKeyInfoPick.beforeFinish");

			FinishKeyCapture();
			ArmUiReuseSuppressAfterCapture(token);
			SetKeyInfoToken(token);

			if (KeyToken.IsMouseButtonToken(token))
				_blockSlotMouseButtonsUntilUp |= MouseTokenToButtonMask(token);

			if (string.Equals(token, "LeftWindows", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(token, "RightWindows", StringComparison.OrdinalIgnoreCase))
			{
				WindowsKeySuppressor.ArmWinKeyReleaseGuard();
			}

			if (isEscape)
				SuppressEscapeDerivedTriggers("CompleteKeyInfoPick.Escape");

			BeginKeyInfoNameEdit();
		}

		private static void CompleteSlotCapture(string token)
		{
			token = KeyToken.NormalizeCapturedToken(token);
			bool wheel = StableKeyToken.IsMouseWheelToken(token);
			if (AkDiag.Enabled)
			{
				AkDiag.Log(
				"ComboPanel CompleteSlotCapture token=" + token
				+ " slot=" + _capturingSlot
				+ (wheel ? " wheel=complete display=" + KeyToken.FormatToken(token) : string.Empty)
				+ (wheel ? " " + MouseInputHelper.FormatWheelDiag() : string.Empty));
			}
			int slot = _capturingSlot;
			bool accepted = TrySetSlot(slot, token);

			// Rejected binds must keep the purple "press a key" wait (LeftClick confirm, duplicates, etc.).
			if (!accepted)
			{
				_capturingSlot = slot;
				_awaitingSlotKey = true;
				if (MouseInputHelper.IsPhysicalLeftDown())
					_swallowOpenClickMouse1UntilUp = true;
				SlotCapture.Begin(forceBlockMouse1: MouseInputHelper.IsPhysicalLeftDown());
				if (AkDiag.Enabled)
				{
					AkDiag.Log(
					"ComboPanel rejected capture; staying on press-a-key."
					+ (wheel ? " wheel=rejected token=" + token : string.Empty));
				}
				return;
			}

			ClearLeftClickConfirmPrompt();
#if AK_DIAGNOSTICS
			ComboPanelEscPathDiag.Note("RegistrationWaitCleared.slotCapture token=" + token);
#endif

			bool isEscape = string.Equals(token, "Escape", StringComparison.OrdinalIgnoreCase);
			if (isEscape)
			{
				// Mark BEFORE FinishKeyCapture so same-tick parent/Inventory paths stay suppressed
				// after wait flags clear (Image1 → Image3, never Image2).
				MarkEscapeRegisteredAndConsumedThisTick("CompleteSlotCapture.beforeFinish");
			}

			SlotCapture.NoteLatencyInternalUpdated();
			SlotCapture.NoteLatencyUiUpdated();
			int completedSlot = _capturingSlot;
			FinishKeyCapture();
			ArmUiReuseSuppressAfterCapture(token);
			// Do NOT ClearPhysicalHoverLock for Mouse1/Mouse2 here.
			// After WASD/arrows, Main.mouse often remains at the UI-nav snap point. Dropping the
			// purple-wait hover lock made GetPanelMouseUi fall back to that snapped Main.mouse, so
			// the next hover/click missed the slot until the OS cursor moved. Keep the lock (panel
			// point from opening this wait) until a real OS move clears it in GetPanelMouseUi.
			if (completedSlot == 0)
				SetKeyInfoToken(token);

			// The mouse press that finished capture must not also hit HandleClicks this frame.
			if (KeyToken.IsMouseButtonToken(token))
				_blockSlotMouseButtonsUntilUp |= MouseTokenToButtonMask(token);

			if (string.Equals(token, "LeftWindows", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(token, "RightWindows", StringComparison.OrdinalIgnoreCase))
			{
				WindowsKeySuppressor.ArmWinKeyReleaseGuard();
			}

			if (isEscape)
			{
				SuppressEscapeDerivedTriggers("CompleteSlotCapture.Escape");
				if (AkDiag.Enabled) AkDiag.Log("ComboPanel bound Escape during slot capture (FinishKeyCapture only).");
			}
		}

		/// <summary>While the modal combo panel is open, strip MenuInquiry/Inventory before UIManageControls.Update runs.</summary>
		public static void GuardModalInput()
		{
			if (!ShouldSwallowMenuEscape())
				return;

			if (IsEscapeCaptureTickActive() || IsEscapeDismissBlockedUntilKeyUp())
				SuppressEscapeDerivedTriggers("GuardModalInput.captureOrKeyUpGate");
			else
			{
				ClearMenuInquiry(PlayerInput.Triggers.JustPressed);
				ClearMenuInquiry(PlayerInput.Triggers.Current);
			}
		}

		/// <summary>Block parent keybind menu from reacting to Escape while the panel or capture/key-up gate is active.</summary>
		public static bool ShouldSwallowMenuEscape()
			=> _keyInfoNameEditing
				|| ComboPanelEscapeRegistrationPolicy.ShouldBlockParentUiClose(
					IsOpen,
					_escapeRegisteredThisTick,
					_escapeConsumedThisTick,
					_escapeDismissBlockedUntilKeyUp,
					IsWithinEscDismissGrace());

		public static bool ShouldBlockFancyUiClose()
			=> ShouldSwallowMenuEscape();

		private static void ClearMenuInquiry(TriggersSet set)
		{
			if (set?.KeyStatus == null)
				return;

			if (set.KeyStatus.ContainsKey("MenuInquiry"))
				set.KeyStatus["MenuInquiry"] = false;
		}

		/// <summary>
		/// Strip menu-level Escape for the parent keybind screen.
		/// During purple registration wait / Escape capture-tick / key-up gate: always consume Escape.
		/// While the panel is open and idle (after Escape released): leave Escape so <see cref="HandleEscape"/> can dismiss.
		/// After close (dismiss grace): consume so the parent menu does not also close.
		/// </summary>
		public static void PrepareParentMenuForEscape()
		{
			if (!ShouldSwallowMenuEscape())
				return;

#if AK_DIAGNOSTICS
			ComboPanelEscPathDiag.Note("PrepareParent.before");
#endif

			GuardModalInput();

			bool registrationWait = IsRegistrationWaitActive() || _keyInfoNameEditing;
			if (IsRegistrationWaitActive() && IsOpen)
			{
				// UI Update can run before/after PlayerInput; register Escape here if still waiting.
				TryRegisterEscapeDuringRegistrationWait();
			}

			if (_keyInfoNameEditing
				|| ComboPanelEscapeRegistrationPolicy.ShouldConsumeEscapeForParent(
				IsRegistrationWaitActive(),
				_escapeRegisteredThisTick,
				_escapeConsumedThisTick,
				_escapeDismissBlockedUntilKeyUp,
				IsWithinEscDismissGrace(),
				IsOpen))
			{
				SuppressEscapeDerivedTriggers("PrepareParent.consume");
			}

#if AK_DIAGNOSTICS
			ComboPanelEscPathDiag.Note("PrepareParent.after");
#endif
		}

		/// <summary>
		/// PlayerInput.orig() re-reads the physical keyboard and can re-arm Inventory from Escape.
		/// Re-suppress after vanilla rebuild when this tick registered Escape or same-press gate is active.
		/// </summary>
		public static void SuppressEscapeAfterVanillaInputRebuild()
		{
			// Keep suppressing while physical Escape is still held after registration (Image3 stay).
			if (!IsEscapeCaptureTickActive()
				&& !IsEscapeDismissBlockedUntilKeyUp()
				&& !IsWithinEscDismissGrace())
				return;

#if AK_DIAGNOSTICS
			ComboPanelEscPathDiag.Note("AfterVanilla.beforeSuppress");
#endif
			SuppressEscapeDerivedTriggers("AfterVanillaInputRebuild");
			if (Main.LocalPlayer != null)
				Main.LocalPlayer.releaseInventory = false;
#if AK_DIAGNOSTICS
			ComboPanelEscPathDiag.Note("AfterVanilla.afterSuppress");
#endif
		}

		private static bool IsWithinEscDismissGrace() => Environment.TickCount64 < _escDismissGraceUntilMs;

		private static void BeginEscDismissGrace()
		{
			_escDismissGraceUntilMs = Environment.TickCount64 + EscDismissGraceMs;
			SuppressEscapeDerivedTriggers("BeginEscDismissGrace");
		}

		private static void SuppressEscapeDerivedTriggers(string reason)
		{
			_escapeConsumedThisTick = true;
			_escapeCaptureInputTick = _escapeInputTickSerial;
			ConsumeEscape();
#if AK_DIAGNOSTICS
			ComboPanelEscPathDiag.Note("SuppressEscapeDerivedTriggers", reason);
#endif
		}

		private static void ConsumeEscape()
		{
			Keys[] pressed = Main.keyState.GetPressedKeys();
			List<Keys> remaining = new(pressed.Length);
			foreach (Keys k in pressed)
			{
				if (k != Keys.Escape)
					remaining.Add(k);
			}
			Main.keyState = new KeyboardState(remaining.ToArray());

			// Escape is also surfaced through gameplay triggers (Inventory / MenuInquiry); clear those too
			// so nothing behind the modal panel reacts to this Escape press.
			ClearTrigger(PlayerInput.Triggers.JustPressed);
			ClearTrigger(PlayerInput.Triggers.Current);
		}

		private static void ClearTrigger(TriggersSet set)
		{
			if (set?.KeyStatus == null)
				return;

			foreach (string trigger in new[] { "Inventory", "MenuInquiry" })
			{
				if (set.KeyStatus.ContainsKey(trigger))
					set.KeyStatus[trigger] = false;
			}
		}

		private static void Commit()
		{
			if (_working == null)
				return;

			if (BindingRestrictions.RejectsBinding(_actionId, _working))
			{
				if (AkDiag.Enabled) AkDiag.Log("ComboPanel commit rejected unsupported binding.");
				return;
			}

			BindingRestrictions.ClearUnsupportedHoldFlags(_working);
			ControlsBindingUndo.Snapshot undo = ControlsBindingUndo.Capture(_actionId);

			// BindingStorage.Set owns KeyStatus sync (advanced markers / plain / empty).
			string reason = _working.RequiresAdvancedStorage()
				? "combo-panel-commit-advanced"
				: "combo-panel-commit-plain-or-empty";
			BindingStorage.Set(_actionId, _working, reason);

			if (OpenKeybindMenuDefaults.IsOpenKeybindMenuAction(_actionId))
				OpenKeybindMenuDefaults.MarkCustomized();
			if (OpenKeybindMenuDefaults.IsHoldTierHudHotkeyAction(_actionId))
				OpenKeybindMenuDefaults.MarkHoldTierHudHotkeyCustomized();

			ControlsBindingUndo.RememberIfChanged(undo, "combo-panel-commit");
			SlotCapture.NoteLatencySaveCompleted();
			if (AkDiag.Enabled)
			{
				DisplayResolveDiag.LogBindingSnapshot("commit", _actionId, _working);
				if (AkDiag.Enabled) AkDiag.Log("ComboPanel commit latency " + SlotCapture.LatencyClock.FormatSummary());
			}
		}

		private static bool TrySetSlot(int index, string token)
		{
			if (string.IsNullOrEmpty(token))
				return false;

			// Never persist UI display labels (LeftClick / L-Ctrl / MB4 / 窶ｦ).
			if (StableKeyToken.IsDisplayAlias(token))
			{
				SoundEngine.PlaySound(SoundID.MenuTick);
				if (AkDiag.Enabled) AkDiag.Log($"ComboPanel display-alias rejected token={token}");
				return false;
			}

			string canonical = StableKeyToken.CanonicalizeOrNull(token);
			if (canonical != null)
				token = canonical;
			else if (!KeyToken.TryParse(token, out _, out _, out _)
				&& !BindingRestrictions.IsHankakuZenkakuToken(token)
				&& !BindingRestrictions.IsToggleLockToken(token))
			{
				SoundEngine.PlaySound(SoundID.MenuTick);
				if (AkDiag.Enabled) AkDiag.Log($"ComboPanel unknown token rejected token={token}");
				return false;
			}

			if (BindingRestrictions.IsF11Token(token))
			{
				SoundEngine.PlaySound(SoundID.MenuTick);
				if (AkDiag.Enabled) AkDiag.Log($"ComboPanel F11 rejected token={token}");
				return false;
			}

#if AK_DIAGNOSTICS
			bool comparedAny = false;
#endif
			for (int i = 1; i <= 3; i++)
			{
				if (i == index)
					continue;

				string existing = _working.GetSlot(i);
				if (string.IsNullOrEmpty(existing))
					continue;

#if AK_DIAGNOSTICS
				comparedAny = true;
#endif
				// Physical identity (SC+E0+E1). Display names are never consulted.
				bool tokensEqual = StableKeyToken.TokensEqual(existing, token);
				bool samePhysical = PhysicalKeyIdentity.AreSamePhysical(existing, token);

				if (tokensEqual || samePhysical)
				{
#if AK_DIAGNOSTICS
					bool resolveA = PhysicalKeyIdentity.TryResolve(existing, out string idA);
					bool resolveB = PhysicalKeyIdentity.TryResolve(token, out string idB);
					bool physicalIdEqual = resolveA && resolveB
						&& string.Equals(idA, idB, StringComparison.OrdinalIgnoreCase);
					PhysicalKeyDupDiag.LogDupCheck(
						caller: "ComboPanel.TrySetSlot",
						targetSlot: index,
						existingSlot: i,
						existingToken: existing,
						incomingToken: token,
						tokensEqual: tokensEqual,
						samePhysical: samePhysical,
						physicalIdEqual: physicalIdEqual,
						result: "RejectedDuplicate",
						reason: tokensEqual ? "tokens-equal" : "same-physical-id");
#endif
					SoundEngine.PlaySound(SoundID.MenuTick);
					if (AkDiag.Enabled) AkDiag.Log($"ComboPanel duplicate rejected token={token} conflicts-with-slot={i} existing={existing}");
					return false;
				}

#if AK_DIAGNOSTICS
				{
					bool resolveA = PhysicalKeyIdentity.TryResolve(existing, out string idA);
					bool resolveB = PhysicalKeyIdentity.TryResolve(token, out string idB);
					bool physicalIdEqual = resolveA && resolveB
						&& string.Equals(idA, idB, StringComparison.OrdinalIgnoreCase);
					PhysicalKeyDupDiag.LogDupCheck(
						caller: "ComboPanel.TrySetSlot",
						targetSlot: index,
						existingSlot: i,
						existingToken: existing,
						incomingToken: token,
						tokensEqual: tokensEqual,
						samePhysical: samePhysical,
						physicalIdEqual: physicalIdEqual,
						result: "Accepted",
						reason: !resolveA || !resolveB
							? "pair-ok-but-resolve-incomplete"
							: "different-physical-id");
				}
#endif
			}

#if AK_DIAGNOSTICS
			if (!comparedAny)
			{
				PhysicalKeyDupDiag.LogDupCheck(
					caller: "ComboPanel.TrySetSlot",
					targetSlot: index,
					existingSlot: 0,
					existingToken: string.Empty,
					incomingToken: token,
					tokensEqual: false,
					samePhysical: false,
					physicalIdEqual: false,
					result: "Accepted",
					reason: "no-other-filled-slots");
			}
#endif

			string previous = _working.GetSlot(index);
			string previousDisplay = _working.GetSlotDisplay(index);
			_working.SetSlot(index, token);
			_working.SetSlotDisplay(index, null);
			BindingRestrictions.ClearUnsupportedHoldFlags(_working);

			if (BindingRestrictions.RejectsBinding(_actionId, _working))
			{
				_working.SetSlot(index, previous);
				_working.SetSlotDisplay(index, previousDisplay);
				BindingRestrictions.ClearUnsupportedHoldFlags(_working);
				SoundEngine.PlaySound(SoundID.MenuTick);
				if (AkDiag.Enabled) AkDiag.Log("ComboPanel rejected unsupported binding.");
				return false;
			}

			if (_working.FilledSlotCount != 1)
			{
				_working.Hold = false;
				_working.HoldOnRelease = false;
			}
			else
			{
				BindingRestrictions.ClearUnsupportedHoldFlags(_working);
			}

			// Any edit invalidates a pending Save-time LeftClick confirm.
			ClearLeftClickConfirmPrompt();
			_dirty = true;
			if (AkDiag.Enabled)
				DisplayResolveDiag.LogSlotSet(_actionId, index, token);
			SoundEngine.PlaySound(SoundID.MenuTick);
			return true;
		}

		private static int MouseTokenToButtonMask(string token) => token switch {
			"Mouse1" => 1,
			"Mouse2" => 2,
			"Mouse3" => 4,
			"Mouse4" => 8,
			"Mouse5" => 16,
			_ => 0
		};

		private static bool IsBlockedMouseButtonHeld(int mask) =>
			(mask & 1) != 0 && MouseInputHelper.IsPhysicalLeftDown()
			|| (mask & 2) != 0 && MouseInputHelper.IsPhysicalRightDown()
			|| (mask & 4) != 0 && MouseInputHelper.IsPhysicalMiddleDown()
			|| (mask & 8) != 0 && MouseInputHelper.IsMouseX1Down()
			|| (mask & 16) != 0 && MouseInputHelper.IsMouseX2Down();

		private static void ClearSlot(int index)
		{
			string previous = _working.GetSlot(index);
			if (AkDiag.Enabled)
				DisplayResolveDiag.LogSlotClear(_actionId, index, previous);
			_working.SetSlot(index, null);
			_working.SetSlotDisplay(index, null);
			BindingRestrictions.ClearUnsupportedHoldFlags(_working);
			if (_working.FilledSlotCount != 1)
			{
				_working.Hold = false;
				_working.HoldOnRelease = false;
			}

			ClearLeftClickConfirmPrompt();
			_dirty = true;
			SoundEngine.PlaySound(SoundID.MenuTick);
		}

		public static void DrawAndHandle(SpriteBatch spriteBatch)
		{
			if (!IsOpen)
				return;

			ComputeLayout();
			RememberPanelMouseUiIfValid();

			Point hover = GetPanelMouseUi();
			_hoverHoldNumber = HoldEditable && _holdNumberRect.Contains(hover);
			_hoverHoldSlider = HoldEditable && HoldSliderHit().Contains(hover);
			_hoverHoldDefaultBtn = HoldEditable && _holdDefaultBtn.Contains(hover);
			_hoverHoldWhileHeld = _holdWhileHeldBox.Contains(hover);
			_hoverHoldOnRelease = _holdOnReleaseBox.Contains(hover);

			// Physical buttons so remapping 蟇ｾ隧ｱ繝ｻ蜍穂ｽ・/ 繧｢繧､繝・Β縺ｮ菴ｿ逕ｨ does not break panel UI
			// (slot select, right-click clear, hold slider).
			bool leftDown = MouseInputHelper.IsPhysicalLeftDown();
			bool rightDown = MouseInputHelper.IsPhysicalRightDown();
			bool gameLeftDown = Main.mouseLeft;
			bool gameRightDown = Main.mouseRight;

			// Physical Mouse.GetState() still sees clicks on other windows/monitors. Sync state but
			// do not run click handlers while Terraria is inactive 窶・purple wait stays.
			bool gameActive = Main.hasFocus && GameWindowHandle.IsGameProcessForeground();
			if (!gameActive)
			{
				SlotCapture.DiscardPendingEdgesWhileInactive();
				_prevLeft = leftDown;
				_prevRight = rightDown;
				_prevGameLeft = gameLeftDown;
				_prevGameRight = gameRightDown;
				_prevHasFocus = false;
				_prevKeyState = Main.keyState;
				_holdTyping = false;
				_draggingHold = false;
				// Keep display-name edit while Terraria is inactive (alt-tab / outside click).
				Draw(spriteBatch);
				if (!Main.gameMenu && Main.LocalPlayer != null)
					Main.LocalPlayer.mouseInterface = true;
				Main.HoverItem = new Item();
				Main.hoverItemName = string.Empty;
				ApplyDeferredKeyInfoHoverTip();
				DrawPanelTips(spriteBatch);
				return;
			}

			// Focus regain: swallow residual mouse edges; keep purple; do not Poll this frame
			// (same residual Mouse1 must not become outside-slot dismiss).
			if (!_prevHasFocus && (_awaitingSlotKey || SlotCapture.IsCapturing))
			{
				SlotCapture.DiscardPendingEdgesWhileInactive();
				_prevLeft = leftDown;
				_prevRight = rightDown;
				_prevGameLeft = gameLeftDown;
				_prevGameRight = gameRightDown;
				_prevHasFocus = true;
				_prevKeyState = Main.keyState;
				_holdTyping = false;
				_draggingHold = false;
				ReleaseOpenClickMouse1SwallowIfNeeded();
				Draw(spriteBatch);
				if (!Main.gameMenu && Main.LocalPlayer != null)
					Main.LocalPlayer.mouseInterface = true;
				Main.HoverItem = new Item();
				Main.hoverItemName = string.Empty;
				ApplyDeferredKeyInfoHoverTip();
				DrawPanelTips(spriteBatch);
				return;
			}

			_prevHasFocus = true;
			bool leftJustPressed = leftDown && !_prevLeft;
			bool rightJustPressed = rightDown && !_prevRight;
			// Game-processed clicks only (do not fire when clicking another window/monitor).
			bool gameLeftJustPressed = gameLeftDown && !_prevGameLeft;
			bool gameRightJustPressed = gameRightDown && !_prevGameRight;

			ReleaseOpenClickMouse1SwallowIfNeeded();

			// Reset-displays confirm: modal — only confirm / cancel paths accept input.
			if (_resetDisplaysConfirmPending)
			{
				HandleResetDisplaysConfirmInput(
					leftJustPressed,
					gameLeftJustPressed,
					rightJustPressed,
					gameRightJustPressed);
				_holdTyping = false;
				_draggingHold = false;
				_prevLeft = leftDown;
				_prevRight = rightDown;
				_prevGameLeft = gameLeftDown;
				_prevGameRight = gameRightDown;
				_prevKeyState = Main.keyState;
				Draw(spriteBatch);
				if (!Main.gameMenu && Main.LocalPlayer != null)
					Main.LocalPlayer.mouseInterface = true;
				Main.HoverItem = new Item();
				Main.hoverItemName = string.Empty;
				ApplyDeferredKeyInfoHoverTip();
				DrawPanelTips(spriteBatch);
				return;
			}

			// Name-field edit: exclusive input (no slot capture / hold / combo registration).
			if (_keyInfoNameEditing)
			{
				Point mouseEdit = GetPanelMouseUi();
				if (TryHandleKeyInfoSessionUndoOrReset(mouseEdit, leftJustPressed || gameLeftJustPressed))
				{
					_holdTyping = false;
					_draggingHold = false;
					_prevLeft = leftDown;
					_prevRight = rightDown;
					_prevGameLeft = gameLeftDown;
					_prevGameRight = gameRightDown;
					_prevKeyState = Main.keyState;
					Draw(spriteBatch);
					if (!Main.gameMenu && Main.LocalPlayer != null)
						Main.LocalPlayer.mouseInterface = true;
					Main.HoverItem = new Item();
					Main.hoverItemName = string.Empty;
					ApplyDeferredKeyInfoHoverTip();
					DrawPanelTips(spriteBatch);
					return;
				}

				HandleKeyInfoNameInput();
				HandleKeyInfoNameClicks(leftJustPressed, gameLeftJustPressed, rightJustPressed, gameRightJustPressed);
				_holdTyping = false;
				_draggingHold = false;
				_prevLeft = leftDown;
				_prevRight = rightDown;
				_prevGameLeft = gameLeftDown;
				_prevGameRight = gameRightDown;
				_prevKeyState = Main.keyState;
				Draw(spriteBatch);
				if (!Main.gameMenu && Main.LocalPlayer != null)
					Main.LocalPlayer.mouseInterface = true;
				Main.HoverItem = new Item();
				Main.hoverItemName = string.Empty;
				ApplyDeferredKeyInfoHoverTip();
				DrawPanelTips(spriteBatch);
				return;
			}

			// Cancel / Clear / outside left|right-click during capture only exit the purple wait.
			if (_awaitingSlotKey || SlotCapture.IsCapturing)
			{
				// Key-info / slot Mouse1|Mouse2 binds are handled in PollSlotCaptureIfActive
				// (before _prevLeft is advanced). Do not re-handle here.
				if (TryCancelKeyInfoPickWithOutsideRightClick(rightJustPressed, gameRightJustPressed))
				{
					_holdTyping = false;
					_draggingHold = false;
				}
				else if (!TryDismissCaptureWithPanelButton(leftJustPressed)
					&& !TryCompleteSlotCaptureWithPanelMouseButtons(
						leftJustPressed,
						rightJustPressed,
						gameLeftJustPressed,
						gameRightJustPressed)
					&& !TryDismissCaptureWithOutsideClick(gameLeftJustPressed, gameRightJustPressed))
					ProcessSlotCaptureInput();

				_holdTyping = false;
				_draggingHold = false;
			}
			else if (IsUiReuseSuppressBlockingIdle())
			{
				// Registered key still held: skip confirm/nav reuse (Space → re-capture, WASD snap).
				// Keep edge trackers current so the next physical click after release is clean.
				_holdTyping = false;
				_draggingHold = false;
			}
			else
			{
				HandleClicks(leftJustPressed, rightJustPressed, gameLeftJustPressed);
				HandleHoldInput();
			}

			_prevLeft = leftDown;
			_prevRight = rightDown;
			_prevGameLeft = gameLeftDown;
			_prevGameRight = gameRightDown;
			_prevKeyState = Main.keyState;

			Draw(spriteBatch);

			if (!Main.gameMenu && Main.LocalPlayer != null)
				Main.LocalPlayer.mouseInterface = true;

			// Safety net: clear any item tooltip a background UI set this frame (e.g. Recipe Browser's
			// "press LeftControl to translate"). We draw before the tooltip pass, so this suppresses it.
			Main.HoverItem = new Item();
			Main.hoverItemName = string.Empty;

			// Hold ms number box: range tip on hover (same MouseText path as keybind row tooltips).
			// hackedMouse* = GetPanelMouseUi so WASD UI-nav (Main.mouse) cannot move the tip.
			Point tipAt = GetPanelMouseUi();
			if (_holdNumberRect.Contains(hover))
			{
				string tip = Language.GetTextValue("Mods.AdvancedKeybinds.UI.Combo.HoldMsInputTooltip");
				if (!string.IsNullOrEmpty(tip))
					Main.instance.MouseText(tip, 0, 0, tipAt.X, tipAt.Y);
			}
			else if (!string.IsNullOrEmpty(_keyInfoHoverTip))
			{
				Main.instance.MouseText(_keyInfoHoverTip, 0, 0, tipAt.X, tipAt.Y);
			}

			_keyInfoHoverTip = null;
			DrawPanelTips(spriteBatch);
		}

		private static void ApplyDeferredKeyInfoHoverTip()
		{
			if (!string.IsNullOrEmpty(_keyInfoHoverTip))
			{
				Point tipAt = GetPanelMouseUi();
				Main.instance.MouseText(_keyInfoHoverTip, 0, 0, tipAt.X, tipAt.Y);
			}
			_keyInfoHoverTip = null;
		}

		private static void ArmLeftClickConfirmPrompt()
		{
			_leftClickConfirmPending = true;
			_rejectLeftClickTipUntilMs = Environment.TickCount64 + RejectLeftClickTipMs;
		}

		private static void ClearLeftClickConfirmPrompt()
		{
			_leftClickConfirmPending = false;
			_rejectLeftClickTipUntilMs = 0;
		}

		private static void ArmResetDisplaysConfirm()
		{
			ClearLeftClickConfirmPrompt();
			_resetDisplaysConfirmPending = true;
			SoundEngine.PlaySound(SoundID.MenuTick);
			if (AkDiag.Enabled) AkDiag.Log("ComboPanel reset-displays confirm armed.");
		}

		private static void ClearResetDisplaysConfirm()
		{
			_resetDisplaysConfirmPending = false;
		}

		/// <summary>
		/// While reset-displays confirm is pending: second click on the button commits;
		/// Cancel / any other panel click / outside click / right-click cancels (does not close the panel).
		/// ESC is handled in <see cref="HandleEscape"/>.
		/// </summary>
		private static void HandleResetDisplaysConfirmInput(
			bool leftJustPressed,
			bool gameLeftJustPressed,
			bool rightJustPressed,
			bool gameRightJustPressed)
		{
			bool rightCancel = rightJustPressed || gameRightJustPressed;
			if (!leftJustPressed && !gameLeftJustPressed && !rightCancel)
				return;

			Point mouse = GetPanelMouseUi();

			if (leftJustPressed && _resetDisplaysBtn.Contains(mouse))
			{
				ClearResetDisplaysConfirm();
				ResetAllRegisteredDisplays();
				return;
			}

			// Cancel: right-click, Undo, any other combo-panel hit, or outside left-click (modal — do not close).
			if (rightCancel || leftJustPressed)
			{
				ClearResetDisplaysConfirm();
				SoundEngine.PlaySound(SoundID.MenuClose);
				if (AkDiag.Enabled)
				{
					string reason = rightCancel
						? "ComboPanel reset-displays confirm cancelled (right-click)."
						: _backBtn.Contains(mouse) ? "ComboPanel reset-displays confirm cancelled (Undo)."
						: _panel.Contains(mouse) ? "ComboPanel reset-displays confirm cancelled (panel click)."
						: "ComboPanel reset-displays confirm cancelled (outside click).";
					AkDiag.Log(reason);
				}
			}
		}

		private static void DrawPanelTips(SpriteBatch spriteBatch)
		{
			if (_resetDisplaysConfirmPending)
			{
				DrawResetDisplaysConfirmTipIfActive(spriteBatch);
				return;
			}

			DrawRejectLeftClickTipIfActive(spriteBatch);
			DrawDisplayRestoredTipIfActive(spriteBatch);
		}

		private static void DrawRejectLeftClickTipIfActive(SpriteBatch spriteBatch)
		{
			if (!_leftClickConfirmPending && Environment.TickCount64 >= _rejectLeftClickTipUntilMs)
				return;

			string text = Language.GetTextValue("Mods.AdvancedKeybinds.UI.Combo.ConfirmLeftClickOnly");
			DrawMouseConfirmTip(spriteBatch, text);
		}

		private static void DrawResetDisplaysConfirmTipIfActive(SpriteBatch spriteBatch)
		{
			string text = LocOr(
				"Mods.AdvancedKeybinds.UI.Combo.KeyInfoResetDisplaysConfirm",
				"変更したキー表示名を、すべて初期値に戻しますか？");
			DrawMouseConfirmTip(spriteBatch, text);
		}

		private static void DrawMouseConfirmTip(SpriteBatch spriteBatch, string text)
			=> DrawMouseConfirmTip(spriteBatch, text, RejectLeftClickTipColor);

		private static void DrawMouseConfirmTip(SpriteBatch spriteBatch, string text, Color color)
		{
			if (string.IsNullOrEmpty(text))
				return;

			Point tipMouse = GetPanelMouseUi();
			Vector2 pos = new(
				tipMouse.X,
				tipMouse.Y + RejectLeftClickTipOffsetY);
			Utils.DrawBorderString(spriteBatch, text, pos, color, RejectLeftClickTipScale);
		}

		private static void DrawDisplayRestoredTipIfActive(SpriteBatch spriteBatch)
		{
			if (Environment.TickCount64 >= _displayRestoredTipUntilMs || string.IsNullOrEmpty(_displayRestoredTipText))
				return;

			DrawMouseConfirmTip(spriteBatch, _displayRestoredTipText, DisplayRestoredTipColor);
		}

		private static void ClearDisplayRestoredTip()
		{
			_displayRestoredTipUntilMs = 0;
			_displayRestoredTipText = null;
		}

		private static void ClearPendingDisplayAliasUndo()
		{
			_displayAliasUndoToken = null;
			_displayAliasUndoDisplay = null;
		}

		private static void RestorePendingDisplayAliasUndo()
		{
			if (string.IsNullOrEmpty(_displayAliasUndoToken) || string.IsNullOrEmpty(_displayAliasUndoDisplay))
			{
				ClearPendingDisplayAliasUndo();
				return;
			}

			string token = _displayAliasUndoToken;
			string display = _displayAliasUndoDisplay;
			ClearPendingDisplayAliasUndo();

			// Persist restore without flipping _dirty (Undo already reset combo state).
			KeyDisplayAliasStorage.SetDisplay(token, display);
			SyncWorkingSlotDisplaysForToken(token, display);
			if (AkDiag.Enabled) AkDiag.Log("ComboPanel display-alias undo restored token=" + token + " display=" + display);
		}

		private static void ArmDisplayRestoredTip(string previousDisplay, string originalKey)
		{
			string fmt = LocOr(
				"Mods.AdvancedKeybinds.UI.Combo.KeyInfoDisplayRestored",
				"表示名{0}を{1}に戻しました");
			_displayRestoredTipText = string.Format(fmt, previousDisplay ?? string.Empty, originalKey ?? string.Empty);
			_displayRestoredTipUntilMs = Environment.TickCount64 + DisplayRestoredTipMs;
		}

		/// <summary>
		/// Right-click on the key-info panel: clear that key's custom display name only.
		/// </summary>
		private static bool TryClearKeyInfoDisplayAliasByRightClick(Point mouse)
		{
			if (string.IsNullOrEmpty(_keyInfoToken) || _awaitingKeyInfoPick)
				return false;

			if (!_keyInfoColumn.Contains(mouse))
				return false;

			string previous = KeyDisplayAliasStorage.GetDisplay(_keyInfoToken);
			if (string.IsNullOrEmpty(previous))
				return true; // swallow right-click on the panel even when nothing to clear

			string original = KeyToken.FormatToken(_keyInfoToken);
			if (string.IsNullOrEmpty(original))
				original = _keyInfoToken;

			_displayAliasUndoToken = _keyInfoToken;
			_displayAliasUndoDisplay = previous;

			CancelKeyInfoNameEdit(commit: false);
			ApplyDisplayAlias(_keyInfoToken, null);
			ArmDisplayRestoredTip(previous, original);
			SoundEngine.PlaySound(SoundID.MenuClose);
			if (AkDiag.Enabled) AkDiag.Log("ComboPanel key-info right-click cleared display token=" + _keyInfoToken);
			return true;
		}

		private static void HandleClicks(bool leftJustPressed, bool rightJustPressed, bool gameLeftJustPressed)
		{
			if (_blockSlotMouseButtonsUntilUp != 0)
			{
				if (IsBlockedMouseButtonHeld(_blockSlotMouseButtonsUntilUp))
					return;

				_blockSlotMouseButtonsUntilUp = 0;
			}

			Point mouse = GetPanelMouseUi();

			// Swallow is only for the in-edit path (HandleKeyInfoNameClicks). Applying it here
			// while idle discarded the first name-box / shell click after leaving text entry.

			if (TryHandleKeyInfoSessionUndoOrReset(mouse, leftJustPressed))
				return;

			if (rightJustPressed)
			{
				if (TryClearKeyInfoDisplayAliasByRightClick(mouse))
					return;

				for (int i = 1; i <= 3; i++)
				{
					if (!_slotBox[i].Contains(mouse))
						continue;

					if (string.IsNullOrEmpty(_working.GetSlot(i)))
						continue;

					ClearSlot(i);
					return;
				}

				return;
			}

			if (!leftJustPressed && !gameLeftJustPressed)
				return;

			// Hold-duration controls only respond while Hold is on; otherwise they're greyed out.
			if (leftJustPressed && HoldEditable)
			{
				// A click anywhere but the number box confirms and ends an in-progress hold-value edit.
				if (_holdTyping && !_holdNumberRect.Contains(mouse))
				{
					ApplyHoldBuffer();
					_holdTyping = false;
				}

				if (_holdNumberRect.Contains(mouse))
				{
					_holdTyping = true;
					_holdBuffer = string.Empty;
					SoundEngine.PlaySound(SoundID.MenuTick);
					return;
				}

				if (_holdDefaultBtn.Contains(mouse))
				{
					_holdTyping = false;
					SetHoldMs(ComboBinding.DefaultHoldMs);
					SoundEngine.PlaySound(SoundID.MenuTick);
					return;
				}

				if (HoldSliderHit().Contains(mouse))
				{
					_draggingHold = true;
					_holdTyping = false;
					ApplyHoldSliderFromMouse();
					return;
				}
			}

			if (leftJustPressed)
			{
				if (_keyInfoNameBox.Contains(mouse))
				{
					// Key already selected → text input mode (image 2).
					// Empty / no token → pick a key first (press-a-key wait).
					if (!string.IsNullOrEmpty(_keyInfoToken) && !_awaitingKeyInfoPick)
					{
						if (!_keyInfoNameEditing)
							BeginKeyInfoNameEdit();
						return;
					}

					StartKeyInfoPick();
					return;
				}

				// Key-info shell (not the name button): start display-name change flow (idle or selected).
				if (IsKeyInfoShellHit(mouse)
					&& !_keyInfoNameEditing
					&& !_awaitingKeyInfoPick
					&& !_awaitingSlotKey
					&& !SlotCapture.IsCapturing)
				{
					StartKeyInfoPick();
					return;
				}
			}

			// Physical or game left edge: after keyboard capture without mouse move, Main.mouse
			// may still be poisoned while the OS click arrives — accept either edge on slots.
			if (leftJustPressed || gameLeftJustPressed)
			{
				for (int i = 1; i <= 3; i++)
				{
					if (!_slotBox[i].Contains(mouse))
						continue;

					CancelKeyInfoNameEdit(commit: false);
					ClearLeftClickConfirmPrompt();
					ResetKeyInfoRenameSide();

					_awaitingKeyInfoPick = false;
					_keyInfoRenameSession = false;
					_capturingSlot = i;
					_awaitingSlotKey = true;
					// Open-click Mouse1: block accept + outside-cancel until physical release.
					_swallowOpenClickMouse1UntilUp = true;
					ArmPhysicalHoverLockAt(mouse.X, mouse.Y);
					if (AkDiag.Enabled)
					{
						AkDiag.Log(
						"ComboPanel slot-capture START slot=" + i
						+ " diag=" + (AkDiag.Enabled ? "ON" : "OFF")
						+ " swallowOpenMouse1=true"
						+ " -> SlotCapture.Begin (must log SlotCapture begin next)");
					}
					SlotCapture.Begin(forceBlockMouse1: true);
					SoundEngine.PlaySound(SoundID.MenuOpen);
					return;
				}
			}

			if (leftJustPressed)
			{
				if (_holdWhileHeldBox.Contains(mouse))
				{
					if (SupportsHold)
					{
						if (_working.Hold && !_working.HoldOnRelease)
							_working.Hold = false;
						else
						{
							_working.Hold = true;
							_working.HoldOnRelease = false;
						}

						_dirty = true;
						SoundEngine.PlaySound(SoundID.MenuTick);
					}
					else
					{
						SoundEngine.PlaySound(SoundID.MenuTick);
					}

					return;
				}

				if (_holdOnReleaseBox.Contains(mouse))
				{
					if (SupportsHold)
					{
						if (_working.Hold && _working.HoldOnRelease)
							_working.Hold = false;
						else
						{
							_working.Hold = true;
							_working.HoldOnRelease = true;
						}

						_dirty = true;
						SoundEngine.PlaySound(SoundID.MenuTick);
					}
					else
					{
						SoundEngine.PlaySound(SoundID.MenuTick);
					}

					return;
				}

				if (_clearBtn.Contains(mouse))
				{
					ClearLeftClickConfirmPrompt();
					_working = new ComboBinding();
					_dirty = true;
					SoundEngine.PlaySound(SoundID.MenuClose);
					return;
				}

				if (_resetDisplaysBtn.Contains(mouse))
				{
					ArmResetDisplaysConfirm();
					return;
				}

				if (_backBtn.Contains(mouse))
				{
					CancelKeyInfoNameEdit(commit: false);
					RestoreOpenedSnapshot();
					return;
				}

				if (_closeBtn.Contains(mouse))
				{
					Close(save: true, userInitiated: true);
					return;
				}
			}

			// Outside the panel: always return to the keybind list (even if key-info shows a token).
			// Use game left only: physical left still fires when clicking another window/monitor
			// (often while hasFocus is still true for one frame), which wrongly closed the panel.
			if (gameLeftJustPressed && !_panel.Contains(mouse))
			{
				if (AkDiag.Enabled) AkDiag.Log("ComboPanel close from in-game click outside panel.");
				Close(save: true, userInitiated: true);
			}
		}

		/// <summary>True when the cursor is on the key-info column but not the name / press field.</summary>
		private static bool IsKeyInfoShellHit(Point mouse)
			=> _keyInfoColumn.Width > 0
				&& _keyInfoColumn.Contains(mouse)
				&& !_keyInfoNameBox.Contains(mouse);

		private static void SetKeyInfoToken(string token)
		{
			if (string.IsNullOrWhiteSpace(token))
			{
				_keyInfoToken = null;
				return;
			}

			_keyInfoToken = token.Trim();
		}

		private static void BeginKeyInfoNameEdit()
		{
			if (string.IsNullOrEmpty(_keyInfoToken))
				return;

			CancelSlotCaptureKeepPanel();
			_holdTyping = false;
			_draggingHold = false;

			_keyInfoNameEditing = true;
			_keyInfoNameBaseline = KeyDisplayAliasStorage.GetDisplay(_keyInfoToken);
			_keyInfoNameBuffer = _keyInfoNameBaseline ?? string.Empty;
			if (_keyInfoNameBuffer.Length > MaxSlotDisplayLength)
				_keyInfoNameBuffer = _keyInfoNameBuffer[..MaxSlotDisplayLength];

			// LeftClick on the key-info shell (above the purple name field) starts edit via
			// CompleteKeyInfoPick, then the delayed Main.mouseLeft edge arrives with the cursor
			// still outside _keyInfoNameBox. Without swallowing that edge, HandleKeyInfoNameClicks
			// treats it as "click outside name field → commit" and drops straight to image-2
			// (token set, not editing). Purple-field clicks are inside the name box so they
			// already survive; shell clicks need this swallow.
			if (KeyToken.IsMouseButtonToken(_keyInfoToken)
				|| MouseInputHelper.IsPhysicalLeftDown()
				|| Main.mouseLeft)
			{
				_keyInfoNameSwallowNextClick = true;
			}

			_keyInfoNameBackspaceHeld = false;
			_keyInfoNameNextBackspaceMs = 0;
			Main.clrInput();
			PlayerInput.WritingText = true;
			if (!Main.blockInput)
			{
				Main.blockInput = true;
				_keyInfoNameOwnedBlockInput = true;
			}

			Main.instance.HandleIME();
			SoundEngine.PlaySound(SoundID.MenuTick);
			if (AkDiag.Enabled) AkDiag.Log("ComboPanel keyinfo-name START token=" + _keyInfoToken);
		}

		private static void HandleKeyInfoNameInput()
		{
			if (!_keyInfoNameEditing)
				return;

			PlayerInput.WritingText = true;
			Main.instance.HandleIME();

			if (Main.keyState.IsKeyDown(Keys.Escape) && !_prevKeyState.IsKeyDown(Keys.Escape))
			{
				CancelKeyInfoNameEdit(commit: false);
				// Same as HandleEscape: do not let the held Escape dismiss the panel next tick.
				_escapeDismissBlockedUntilKeyUp = true;
				SuppressEscapeDerivedTriggers("KeyInfoNameInput.Escape");
				SoundEngine.PlaySound(SoundID.MenuClose);
				return;
			}

			string compositionBefore = GetImeCompositionString();
			string before = _keyInfoNameBuffer;
			string next = Main.GetInputText(before);
			string compositionAfter = GetImeCompositionString();
			next = ApplyKeyInfoNameBackspaceRepeat(
				before,
				next,
				!string.IsNullOrEmpty(compositionBefore) || !string.IsNullOrEmpty(compositionAfter));
			if (next != null && next.Length > MaxSlotRenameInputLength)
				next = next[..MaxSlotRenameInputLength];
			_keyInfoNameBuffer = next ?? string.Empty;

			// IME commit Enter must not end name edit. Only Enter with empty composition both sides.
			if (Main.inputTextEnter
				&& string.IsNullOrEmpty(compositionBefore)
				&& string.IsNullOrEmpty(compositionAfter))
			{
				CancelKeyInfoNameEdit(commit: true);
				SoundEngine.PlaySound(SoundID.MenuTick);
			}
		}

		private static void HandleKeyInfoNameClicks(
			bool leftJustPressed,
			bool gameLeftJustPressed,
			bool rightJustPressed,
			bool gameRightJustPressed)
		{
			if (!_keyInfoNameEditing)
				return;

			// Swallow the same LeftClick that opened name edit (shell hit / delayed game edge).
			if (_keyInfoNameSwallowNextClick)
			{
				if (leftJustPressed || gameLeftJustPressed || rightJustPressed)
				{
					_keyInfoNameSwallowNextClick = false;
					return;
				}
			}

			Point mouse = GetPanelMouseUi();

			if (TryCancelKeyInfoNameEditWithOutsideRightClick(rightJustPressed, gameRightJustPressed, mouse))
				return;

			if (!leftJustPressed && !gameLeftJustPressed)
				return;

			if (_backBtn.Contains(mouse) && leftJustPressed)
			{
				CancelKeyInfoNameEdit(commit: false);
				RestoreOpenedSnapshot();
				return;
			}

			// Outside the combo panel: commit the display name and return to the keybind list
			// (skip the idle "キーを選ぶ" intermediate state).
			if (!_panel.Contains(mouse))
			{
				CancelKeyInfoNameEdit(commit: true);
				Close(save: true, userInitiated: true);
				return;
			}

			// Click outside the name field but still on the panel: commit and stay open.
			if (!_keyInfoNameBox.Contains(mouse))
			{
				CancelKeyInfoNameEdit(commit: true);
				SoundEngine.PlaySound(SoundID.MenuTick);
			}
		}

		/// <summary>
		/// Right-click outside the key-info column cancels display-name text entry (no commit).
		/// </summary>
		private static bool TryCancelKeyInfoNameEditWithOutsideRightClick(
			bool rightJustPressed,
			bool gameRightJustPressed,
			Point mouse)
		{
			if (!_keyInfoNameEditing || (!rightJustPressed && !gameRightJustPressed))
				return false;

			if (_keyInfoColumn.Contains(mouse))
				return false;

			CancelKeyInfoNameEdit(commit: false);
			SoundEngine.PlaySound(SoundID.MenuClose);
			if (AkDiag.Enabled) AkDiag.Log("ComboPanel keyinfo-name cancelled (outside right-click).");
			return true;
		}

		/// <summary>
		/// Right-click outside the key-info column cancels the "press a key" pick for display rename.
		/// </summary>
		private static bool TryCancelKeyInfoPickWithOutsideRightClick(bool rightJustPressed, bool gameRightJustPressed)
		{
			if (!_awaitingKeyInfoPick || (!rightJustPressed && !gameRightJustPressed))
				return false;

			Point mouse = GetPanelMouseUi();
			if (_keyInfoColumn.Contains(mouse))
				return false;

			ResetKeyInfoRenameSide();
			SoundEngine.PlaySound(SoundID.MenuClose);
			if (AkDiag.Enabled) AkDiag.Log("ComboPanel keyinfo-pick cancelled (outside right-click).");
			return true;
		}

		private static string ApplyKeyInfoNameBackspaceRepeat(string before, string after, bool imeCompositionActive)
		{
			before ??= string.Empty;
			after ??= string.Empty;

			bool backspaceDown = Main.keyState.IsKeyDown(Keys.Back);
			if (imeCompositionActive || !backspaceDown)
			{
				_keyInfoNameBackspaceHeld = false;
				_keyInfoNameNextBackspaceMs = 0;
				return after;
			}

			long now = Environment.TickCount64;
			bool shouldDelete;
			if (!_keyInfoNameBackspaceHeld)
			{
				_keyInfoNameBackspaceHeld = true;
				_keyInfoNameNextBackspaceMs = now + SlotRenameBackspaceInitialDelayMs;
				shouldDelete = true;
			}
			else if (now >= _keyInfoNameNextBackspaceMs)
			{
				shouldDelete = true;
				do
				{
					_keyInfoNameNextBackspaceMs += SlotRenameBackspaceRepeatMs;
				}
				while (_keyInfoNameNextBackspaceMs <= now);
			}
			else
			{
				shouldDelete = false;
			}

			bool nativeDeleted = after.Length < before.Length
				&& before.StartsWith(after, StringComparison.Ordinal);
			if (!shouldDelete)
				return nativeDeleted ? before : after;

			if (nativeDeleted)
				return after;

			return after.Length > 0 ? after[..^1] : after;
		}

		private static void CancelKeyInfoNameEdit(bool commit)
		{
			if (!_keyInfoNameEditing)
			{
				EndKeyInfoNameInputSession();
				// Idle leftover swallow (e.g. Escape after mouse-key pick without a click edge)
				// must not eat the next name-box / shell left-click.
				_keyInfoNameSwallowNextClick = false;
				return;
			}

			if (commit)
				TryCommitKeyInfoNameBuffer();

			_keyInfoNameEditing = false;
			_keyInfoNameBuffer = string.Empty;
			_keyInfoNameBaseline = null;
			_keyInfoNameBackspaceHeld = false;
			_keyInfoNameNextBackspaceMs = 0;
			_keyInfoNameSwallowNextClick = false;
			EndKeyInfoNameInputSession();
		}

		private static void TryCommitKeyInfoNameBuffer()
		{
			if (string.IsNullOrEmpty(_keyInfoToken))
				return;

			string trimmed = string.IsNullOrWhiteSpace(_keyInfoNameBuffer) ? null : _keyInfoNameBuffer.Trim();
			if (trimmed != null && trimmed.Length > MaxSlotDisplayLength)
				trimmed = trimmed[..MaxSlotDisplayLength];

			ApplyDisplayAlias(_keyInfoToken, trimmed);
		}

		private static void EndKeyInfoNameInputSession()
		{
			PlayerInput.WritingText = false;
			if (_keyInfoNameOwnedBlockInput)
			{
				Main.blockInput = false;
				_keyInfoNameOwnedBlockInput = false;
			}

			try
			{
				Main.instance?.HandleIME();
			}
			catch
			{
				// Ignore during unload.
			}

			Main.clrInput();
		}

		private static void ComputeLayout()
		{
			// Left block = original 600px combo UI. Extra width = splice + inset + key-info + right pad.
			int w = Math.Min(PanelWidthTarget, Math.Max(LeftBlockWidth + 80, Main.screenWidth - 40));
			if (w < LeftBlockWidth + KeyInfoSpliceGap + KeyInfoColW)
				w = Math.Min(PanelWidthTarget, Main.screenWidth - 40);

			const int h = PanelHeight;
			const int gap = SlotRowGap;
			int x = (Main.screenWidth - w) / 2;
			int y = (Main.screenHeight - h) / 2;
			_panel = new Rectangle(x, y, w, h);

			// --- Left block (same geometry as pre-keyinfo 600px panel) ---
			int contentLeft = x + ContentMargin;
			int leftContentRight = x + LeftBlockWidth - ContentMargin;
			int leftContentWidth = leftContentRight - contentLeft;

			int titleTop = y + HeaderTopPad;
			int previewTop = titleTop + HeaderTitleHeight + HeaderLineGap;
			int holdRowTop = previewTop + HeaderPreviewHeight + PreviewToHoldGap;
			int holdCenterY = holdRowTop + HoldControlBoxH / 2;
			int rowY = holdRowTop + HoldControlBoxH + HoldToSlotGap;

			_backBtn = new Rectangle(leftContentRight - HoldControlBoxW, titleTop, HoldControlBoxW, HoldControlBoxH);
			int headerTextWidth = Math.Max(40, leftContentWidth - HoldControlBoxW - gap);
			_headerTitleRect = new Rectangle(contentLeft, titleTop, headerTextWidth, HeaderTitleHeight);
			_headerPreviewRect = new Rectangle(contentLeft, previewTop, headerTextWidth, HeaderPreviewHeight);

			_holdNumberRect = new Rectangle(leftContentRight - HoldControlBoxW, holdCenterY - HoldControlBoxH / 2, HoldControlBoxW, HoldControlBoxH);
			_holdDefaultBtn = new Rectangle(contentLeft, holdCenterY - HoldControlBoxH / 2, HoldControlBoxW, HoldControlBoxH);

			Texture2D colorBar = TextureAssets.ColorBar.Value;
			int barH = colorBar?.Height ?? 20;
			int sliderLeft = _holdDefaultBtn.Right + HoldControlGap;
			int sliderRight = _holdNumberRect.X - HoldControlGap;
			int sliderW = Math.Max(48, sliderRight - sliderLeft);
			_holdSliderFrame = new Rectangle(sliderLeft, holdCenterY - barH / 2, sliderW, barH);
			_holdInnerX = _holdSliderFrame.X + 5f;
			_holdInnerY = _holdSliderFrame.Y + 4f;
			_holdInnerWidth = _holdSliderFrame.Width - 10f;

			const int slotH = 138;
			int holdColW = 168;
			_holdColumn = new Rectangle(contentLeft, rowY, holdColW, slotH);
			int halfH = slotH / 2;
			_holdWhileHeldBox = new Rectangle(contentLeft, rowY, holdColW, halfH);
			_holdOnReleaseBox = new Rectangle(contentLeft, rowY + halfH, holdColW, slotH - halfH);

			int slotsLeft = _holdColumn.Right + gap;
			int slotsWidth = leftContentRight - slotsLeft;
			int slotW = Math.Max(40, (slotsWidth - gap * 2) / 3);
			for (int i = 1; i <= 3; i++)
				_slotBox[i] = new Rectangle(slotsLeft + (i - 1) * (slotW + gap), rowY, slotW, slotH);

			int btnW = PanelButtonW;
			int btnH = PanelButtonH;
			_clearBtn = new Rectangle(x + ContentMargin, y + h - btnH - 14, btnW, btnH);
			// Save / Cancel stay on the left block's right edge (before the wavy splice).
			_closeBtn = new Rectangle(leftContentRight - btnW, y + h - btnH - 14, btnW, btnH);

			int hintAreaTop = rowY + slotH + FooterHintTopGap;
			int hintAreaBottom = _clearBtn.Y - FooterHintBottomGap;
			int hintAreaH = Math.Max(FooterHintLineSlots * 16, hintAreaBottom - hintAreaTop);
			_footerHintRect = new Rectangle(contentLeft, hintAreaTop, leftContentWidth, hintAreaH);

			// --- Key-info: same row as slots, shifted right of the splice for visual balance ---
			int keyInfoW = KeyInfoColW;
			int keyInfoX = leftContentRight + KeyInfoSpliceGap + KeyInfoLeftInset;
			int maxRight = x + w - KeyInfoRightPad;
			if (keyInfoX + keyInfoW > maxRight)
				keyInfoW = Math.Max(120, maxRight - keyInfoX);

			_keyInfoColumn = new Rectangle(keyInfoX, rowY, keyInfoW, slotH);
			_keyInfoNameBox = new Rectangle(
				_keyInfoColumn.X + KeyInfoPad,
				_keyInfoColumn.Bottom - KeyInfoPad - KeyInfoNameFieldH,
				Math.Max(40, keyInfoW - KeyInfoPad * 2),
				KeyInfoNameFieldH);
			_keyInfoBody = new Rectangle(
				_keyInfoColumn.X + KeyInfoPad,
				_keyInfoColumn.Y + KeyInfoPad,
				Math.Max(40, keyInfoW - KeyInfoPad * 2),
				Math.Max(20, _keyInfoNameBox.Y - KeyInfoPad - (_keyInfoColumn.Y + KeyInfoPad)));
			_keyInfoGuideRect = new Rectangle(
				_keyInfoColumn.X,
				hintAreaTop,
				_keyInfoColumn.Width,
				Math.Max(20, hintAreaBottom - hintAreaTop));

			// Right of Cancel (same band), above the key-info panel — 2-line control.
			const int resetH = 44;
			int resetW = _keyInfoNameBox.Width;
			int resetX = _keyInfoColumn.X + (_keyInfoColumn.Width - resetW) / 2;
			int resetY = _backBtn.Y;
			int maxResetBottom = _keyInfoColumn.Y - 6;
			if (resetY + resetH > maxResetBottom)
				resetY = Math.Max(_panel.Y + HeaderTopPad, maxResetBottom - resetH);
			_resetDisplaysBtn = new Rectangle(resetX, resetY, resetW, resetH);
		}

		/// <summary>Hold-duration editing is only allowed when this is an active single-key Hold binding.</summary>
		private static bool SupportsHold => BindingRestrictions.SupportsHold(_working);

		private static bool HoldEditable => _working != null && _working.Hold && SupportsHold;

		private static Rectangle HoldSliderHit()
		{
			Rectangle r = _holdSliderFrame;
			r.Inflate(-5, 2);
			return r;
		}

		private static void HandleHoldInput()
		{
			// While Hold is off the controls are greyed out and cannot be edited.
			if (!HoldEditable)
			{
				_holdTyping = false;
				_draggingHold = false;
				return;
			}

			if (_draggingHold && MouseInputHelper.IsPhysicalLeftDown())
				ApplyHoldSliderFromMouse();

			if (!MouseInputHelper.IsPhysicalLeftDown())
				_draggingHold = false;

			if (_holdTyping)
				HandleHoldTyping();
		}

		private static void ApplyHoldSliderFromMouse()
		{
			Point mouse = GetPanelMouseUi();
			float proportion = MathHelper.Clamp((mouse.X - _holdInnerX) / _holdInnerWidth, 0f, 1f);
			int span = ComboBinding.SliderMaxHoldMs - ComboBinding.MinHoldMs;
			int raw = ComboBinding.MinHoldMs + (int)(proportion * span);
			int value = System.Math.Clamp(RoundToTen(raw), ComboBinding.MinHoldMs, ComboBinding.SliderMaxHoldMs);
			SetHoldMs(value);
		}

		private static void HandleHoldTyping()
		{
			foreach (Keys key in System.Enum.GetValues<Keys>())
			{
				if (!Main.keyState.IsKeyDown(key) || _prevKeyState.IsKeyDown(key))
					continue;

				if (key == Keys.Back)
				{
					if (_holdBuffer.Length > 0)
						_holdBuffer = _holdBuffer[..^1];
					continue;
				}

				if (key == Keys.Enter)
				{
					ApplyHoldBuffer();
					_holdTyping = false;
					SoundEngine.PlaySound(SoundID.MenuTick);
					continue;
				}

				string digit = KeyToDigit(key);
				if (digit != null && _holdBuffer.Length < 5)
					_holdBuffer += digit;
			}
		}
		private static void ApplyDisplayAlias(string token, string display)
		{
			if (string.IsNullOrEmpty(token))
				return;

			display = string.IsNullOrWhiteSpace(display) ? null : display.Trim();
			if (display != null && display.Length > MaxSlotDisplayLength)
				display = display[..MaxSlotDisplayLength];

			string previous = KeyToken.FormatDisplayForToken(token);
			KeyDisplayAliasStorage.SetDisplay(token, display);
			SyncWorkingSlotDisplaysForToken(token, display);

			if (!string.Equals(previous, KeyToken.FormatDisplayForToken(token), StringComparison.Ordinal))
				_dirty = true;
		}

		private static void SyncWorkingSlotDisplaysForToken(string token, string display)
		{
			if (_working == null || string.IsNullOrEmpty(token))
				return;

			string key = KeyDisplayAliasStorage.ResolveStorageKey(token);
			for (int i = 1; i <= 3; i++)
			{
				string slot = _working.GetSlot(i);
				if (string.IsNullOrEmpty(slot))
					continue;

				string slotKey = KeyDisplayAliasStorage.ResolveStorageKey(slot);
				if (string.Equals(slotKey, key, StringComparison.OrdinalIgnoreCase))
					_working.SetSlotDisplay(i, display);
			}
		}
		private static void ClearAllDisplayAliases()
		{
			int aliases = KeyDisplayAliasStorage.ClearAll();
			int slots = BindingStorage.ClearAllSlotDisplays();
			if (_working != null)
			{
				for (int i = 1; i <= 3; i++)
					_working.SetSlotDisplay(i, null);
			}

			if (aliases > 0 || slots > 0)
				_dirty = true;

			if (AkDiag.Enabled) AkDiag.Log("ComboPanel display-alias ClearAll aliases=" + aliases + " slotDisplays=" + slots);
		}

		/// <summary>
		/// 「登録表示を初期に戻す」: wipe global display aliases + slot overrides.
		/// Combo key tokens themselves are kept; only custom display strings are cleared.
		/// </summary>
		private static void ResetAllRegisteredDisplays()
		{
			ClearLeftClickConfirmPrompt();
			ClearResetDisplaysConfirm();
			CancelKeyInfoNameEdit(commit: false);
			if (_awaitingKeyInfoPick)
				CancelSlotCaptureKeepPanel();

			ClearAllDisplayAliases();
			_keyInfoToken = null;
			_keyInfoRenameSession = false;
			SoundEngine.PlaySound(SoundID.MenuClose);
			if (AkDiag.Enabled) AkDiag.Log("ComboPanel ResetAllRegisteredDisplays");
		}

		private static void DrawResetDisplaysButton(SpriteBatch spriteBatch, Point mouse)
		{
			if (_resetDisplaysBtn.Width <= 0)
				return;

			string line1 = LocOr("Mods.AdvancedKeybinds.UI.Combo.KeyInfoResetDisplaysLine1", "全ての登録表示を");
			string line2 = LocOr("Mods.AdvancedKeybinds.UI.Combo.KeyInfoResetDisplaysLine2", "初期値に戻す");
			bool warn = _resetDisplaysConfirmPending || _resetDisplaysBtn.Contains(mouse);
			DrawBox(
				spriteBatch,
				_resetDisplaysBtn,
				warn ? ResetDisplaysWarnFill : SlotFill,
				warn ? ResetDisplaysWarnBorder : SlotBorder);

			int half = _resetDisplaysBtn.Height / 2;
			var top = new Rectangle(_resetDisplaysBtn.X, _resetDisplaysBtn.Y, _resetDisplaysBtn.Width, half);
			var bottom = new Rectangle(_resetDisplaysBtn.X, _resetDisplaysBtn.Y + half, _resetDisplaysBtn.Width, _resetDisplaysBtn.Height - half);
			// Largest font that still fits each half of the box (shared max so both lines look consistent).
			const float pad = 3f;
			float innerW = Math.Max(1f, _resetDisplaysBtn.Width - pad * 2f);
			float halfH = Math.Max(1f, half - pad * 2f);
			float scale = Math.Min(
				FitTextScaleToBox(line1, innerW, halfH, maxScale: 2.5f, minScale: 0.35f),
				FitTextScaleToBox(line2, innerW, halfH, maxScale: 2.5f, minScale: 0.35f));
			DrawCenteredFixedScale(spriteBatch, line1, top, Color.White, scale);
			DrawCenteredFixedScale(spriteBatch, line2, bottom, Color.White, scale);
		}
		private static string GetImeCompositionString()
		{
			try
			{
				return Platform.Get<IImeService>()?.CompositionString ?? string.Empty;
			}
			catch
			{
				return string.Empty;
			}
		}

		private static void ApplyHoldBuffer()
		{
			if (!int.TryParse(_holdBuffer, out int parsed))
				parsed = _working.EffectiveHoldMs;

			SetHoldMs(System.Math.Clamp(parsed, ComboBinding.MinTypedHoldMs, ComboBinding.MaxHoldMs));
			_holdBuffer = _working.EffectiveHoldMs.ToString();
		}

		private static void SetHoldMs(int value)
		{
			value = System.Math.Clamp(value, ComboBinding.MinTypedHoldMs, ComboBinding.MaxHoldMs);
			if (_working.HoldMs != value)
			{
				_working.HoldMs = value;
				_dirty = true;
			}

			_holdBuffer = value.ToString();
		}

		private static int RoundToTen(int value) => (int)(System.Math.Round(value / 10.0) * 10.0);

		private static string KeyToDigit(Keys key) => key switch {
			Keys.D0 or Keys.NumPad0 => "0",
			Keys.D1 or Keys.NumPad1 => "1",
			Keys.D2 or Keys.NumPad2 => "2",
			Keys.D3 or Keys.NumPad3 => "3",
			Keys.D4 or Keys.NumPad4 => "4",
			Keys.D5 or Keys.NumPad5 => "5",
			Keys.D6 or Keys.NumPad6 => "6",
			Keys.D7 or Keys.NumPad7 => "7",
			Keys.D8 or Keys.NumPad8 => "8",
			Keys.D9 or Keys.NumPad9 => "9",
			_ => null
		};

		private static void Draw(SpriteBatch spriteBatch)
		{
			Point mouse = GetPanelMouseUi();
			Texture2D pixel = TextureAssets.MagicPixel.Value;
			Rectangle previousScissor = Main.instance.GraphicsDevice.ScissorRectangle;

			try
			{
				// UIManageControls 縺ｮ謠冗判荳ｭ縺ｯ繧ｷ繧ｶ繝ｼ遏ｩ蠖｢縺ｧ荳隕ｧ鬆伜沺縺ｫ繧ｯ繝ｪ繝・・縺輔ｌ縺ｦ縺・ｋ縺薙→縺後≠繧翫・
				// 繧ｪ繝ｼ繝舌・繝ｬ繧､蜈ｨ菴薙′隕九∴縺壹御ｸ九・繝懊ち繝ｳ縺縺題ｦ九∴繧九咲憾諷九↓縺ｪ繧九・
				// 縺薙％縺縺醍判髱｢蜈ｨ菴薙∈荳譎ら噪縺ｫ蠎・￡縺ｦ縲√ヱ繝阪Ν繧貞ｮ悟・陦ｨ遉ｺ縺吶ｋ縲・
				Main.instance.GraphicsDevice.ScissorRectangle = new Rectangle(0, 0, Main.screenWidth, Main.screenHeight);

				// Dim the whole screen behind the panel.
				spriteBatch.Draw(pixel, new Rectangle(0, 0, Main.screenWidth, Main.screenHeight), Color.Black * 0.55f);

				// Tint the panel with the row's category color: a bright border and a subtly tinted fill.
				Color panelBorder = Color.Lerp(_accent, Color.White, 0.25f);
				Color panelFill = Color.Lerp(new Color(30, 32, 58), _accent, 0.18f) * 0.98f;
				DrawBox(spriteBatch, _panel, panelFill, panelBorder);

				DrawFittedText(spriteBatch, _actionLabel, _headerTitleRect, Color.White, maxScale: 0.9f);

				string preview = BuildPreview();
				DrawFittedText(spriteBatch, preview, _headerPreviewRect, new Color(210, 200, 255), maxScale: 0.8f);

				// Hold-duration slider + number box (always visible).
				DrawHoldControls(spriteBatch, mouse);

				// Hold mode selectors (top = while held, bottom = on release).
				DrawHoldModeBoxes(spriteBatch, mouse);

				// Slots. All three share identical dimensions; no per-slot label is drawn.
				string slotDisplayHoverTip = null;
				for (int i = 1; i <= 3; i++)
				{
					bool capturing = (_awaitingSlotKey || SlotCapture.IsCapturing) && _capturingSlot == i;
					bool hover = _slotBox[i].Contains(mouse);
					Color fill = capturing ? new Color(96, 54, 150) : new Color(46, 48, 64);
					Color border = capturing
						? new Color(220, 170, 255)
						: (hover ? Color.White : new Color(110, 110, 140));
					DrawBox(spriteBatch, _slotBox[i], fill, border);

					string content = capturing
						? Language.GetTextValue("Mods.AdvancedKeybinds.UI.Combo.Press")
						: SlotDisplay(i);

					if (!capturing && TryGetSlotRenamedDisplay(i, out string usingKey, out string displayName))
					{
						// Show the using key only; custom display name is on hover.
						// Slot text keeps the original (non-fill) size; long labels still shrink to fit.
						DrawCentered(spriteBatch, usingKey, _slotBox[i], Color.White, maxScale: SlotLabelMaxScale, minScale: SlotLabelMinScale);
						if (hover && string.IsNullOrEmpty(slotDisplayHoverTip))
						{
							string label = LocOr("Mods.AdvancedKeybinds.UI.Combo.KeyInfoDisplay", "表示名");
							string template = LocOr(
								"Mods.AdvancedKeybinds.UI.Combo.SlotDisplayNameHover",
								"{0} : {1}");
							slotDisplayHoverTip = string.Format(template, label, displayName);
						}
					}
					else
						DrawCentered(spriteBatch, content, _slotBox[i], Color.White, maxScale: SlotLabelMaxScale, minScale: SlotLabelMinScale);
				}

				// Buttons.
				DrawButton(spriteBatch, _backBtn, Language.GetTextValue("Mods.AdvancedKeybinds.UI.Combo.Undo"), mouse);
				DrawButton(spriteBatch, _clearBtn, Language.GetTextValue("Mods.AdvancedKeybinds.UI.Combo.Clear"), mouse);
				DrawButton(spriteBatch, _closeBtn, Language.GetTextValue("Mods.AdvancedKeybinds.UI.Combo.Save"), mouse);
				DrawResetDisplaysButton(spriteBatch, mouse);

				try
				{
					DrawKeyInfoColumn(spriteBatch, mouse);
				}
				catch (Exception ex)
				{
					if (AkDiag.Enabled) AkDiag.Log("ComboPanel DrawKeyInfoColumn FAIL: " + ex.GetType().Name + " " + ex.Message);
					// Emergency marker so a silent failure is visible.
					DrawBox(spriteBatch, new Rectangle(_panel.Right - 190, _panel.Y + 80, 170, 138),
						new Color(180, 40, 40), Color.Yellow);
				}

				// Slot display-name tip after key-info (key-info hover wins if both set).
				if (string.IsNullOrEmpty(_keyInfoHoverTip) && !string.IsNullOrEmpty(slotDisplayHoverTip))
					_keyInfoHoverTip = slotDisplayHoverTip;

				DrawFooterHints(spriteBatch, new Color(180, 180, 200));
			}
			finally
			{
				Main.instance.GraphicsDevice.ScissorRectangle = previousScissor;
			}
		}

		private static void DrawKeyInfoColumn(SpriteBatch spriteBatch, Point mouse)
		{
			_keyInfoHoverTip = null;
			bool slotCaptureActive = _awaitingSlotKey || SlotCapture.IsCapturing;
			bool hasToken = !string.IsNullOrEmpty(_keyInfoToken) && !slotCaptureActive;
			bool nameHover = (hasToken || _awaitingKeyInfoPick) && _keyInfoNameBox.Contains(mouse);
			bool shellHover = IsKeyInfoShellHit(mouse) && !_keyInfoNameEditing;

			// Outer border only turns red while waiting; white when hovering the info shell (not name button).
			Color colFill = SlotFill;
			Color colBorder = _awaitingKeyInfoPick
				? new Color(230, 90, 90)
				: (shellHover ? Color.White : SlotBorder);
			DrawBox(spriteBatch, _keyInfoColumn, colFill, colBorder);
			DrawKeyInfoSpliceWave(spriteBatch);

			var body = _keyInfoBody;
			string curLabel = LocOr("Mods.AdvancedKeybinds.UI.Combo.KeyInfoCurrent", "元のキー");
			string dispLabel = LocOr("Mods.AdvancedKeybinds.UI.Combo.KeyInfoDisplay", "表示名");
			string scanLabel = LocOr("Mods.AdvancedKeybinds.UI.Combo.KeyInfoScan", "ScanCode");
			string defLabel = LocOr("Mods.AdvancedKeybinds.UI.Combo.KeyInfoDefault", "初期名");
			string none = LocOr("Mods.AdvancedKeybinds.UI.Combo.KeyInfoNone", "なし");
			string present = LocOr("Mods.AdvancedKeybinds.UI.Combo.KeyInfoPresent", "あり");

			{
				string[] lines;
				if (_awaitingKeyInfoPick)
				{
					string awaitHeader = LocOr("Mods.AdvancedKeybinds.UI.Combo.KeyInfoAwaitHeader", "変更したいキー");
					lines = new[] {
						awaitHeader,
						"—",
						dispLabel + ": —",
						scanLabel + ": —",
						"E0: —  E1: —"
					};
				}
				else if (!hasToken)
				{
					// Hybrid A+B: short guide on row 1, same 5-row skeleton as selected (values = —).
					string guide = LocOr("Mods.AdvancedKeybinds.UI.Combo.KeyInfoSelectGuide", "キーを選ぶ");
					lines = new[] {
						guide,
						"—",
						dispLabel + ": —",
						scanLabel + ": —",
						"E0: —  E1: —"
					};
				}
				else
				{
					ComboPanelKeyInfo.Detail detail = ComboPanelKeyInfo.Describe(_keyInfoToken);
					string e0 = detail.E0 == true ? present : (detail.E0 == false ? none : "—");
					string e1 = detail.E1 == true ? present : (detail.E1 == false ? none : "—");
					string keyName = string.IsNullOrEmpty(detail.CurrentKey) ? "—" : detail.CurrentKey;
					lines = new[] {
						curLabel,
						keyName,
						dispLabel + ": " + detail.DisplayName,
						scanLabel + ": " + detail.ScanCode,
						"E0: " + e0 + "  E1: " + e1
					};

					if (nameHover && !_keyInfoNameEditing && !string.IsNullOrEmpty(detail.DefaultName))
						_keyInfoHoverTip = defLabel + ": " + detail.DefaultName;
				}

				int lineH = Math.Max(12, body.Height / lines.Length);
				for (int i = 0; i < lines.Length; i++)
				{
					Color c;
					if (_awaitingKeyInfoPick || !hasToken)
						c = KeyInfoHintColor;
					else if (i == 1)
						c = Color.White;
					else if (i == 0)
						c = KeyInfoValueColor;
					else
						c = KeyInfoLabelColor;

					DrawKeyInfoTopLeft(spriteBatch, lines[i], new Rectangle(body.X, body.Y + i * lineH, body.Width, lineH), c, 0.68f);
				}
			}

			// Name field: button/slot family colors; brighter border only while editing / pick-wait.
			Color nameFill;
			Color nameBorder;
			if (_awaitingKeyInfoPick)
			{
				nameFill = new Color(96, 54, 150);
				nameBorder = new Color(220, 170, 255);
			}
			else if (!hasToken)
			{
				nameFill = new Color(40, 42, 56);
				nameBorder = nameHover ? Color.White : new Color(90, 90, 115);
			}
			else if (_keyInfoNameEditing)
			{
				nameFill = new Color(96, 54, 150);
				nameBorder = new Color(220, 170, 255);
			}
			else if (nameHover)
			{
				nameFill = SlotHoverFill;
				nameBorder = Color.White;
			}
			else
			{
				nameFill = SlotFill;
				nameBorder = SlotBorder;
			}

			DrawBox(spriteBatch, _keyInfoNameBox, nameFill, nameBorder);

			if (_keyInfoNameEditing)
			{
				string composition = GetImeCompositionString();
				DrawSlotRenameText(spriteBatch, _keyInfoNameBox, _keyInfoNameBuffer, composition, maxScale: 0.85f);
				// Do not DrawWindowsIMEPanel here: the candidate list covers the key-info UI.
				// Composition text still shows inline (yellow) via DrawSlotRenameText.
			}
			else if (_awaitingKeyInfoPick)
			{
				string press = LocOr("Mods.AdvancedKeybinds.UI.Combo.KeyInfoPressHint", "キーを押してください");
				DrawCentered(spriteBatch, press, _keyInfoNameBox, Color.White, maxScale: PanelFillTextMaxScale, minScale: PanelFillTextMinScale, pad: PanelFillTextPad);
			}
			else if (hasToken && !string.IsNullOrEmpty(KeyDisplayAliasStorage.GetDisplay(_keyInfoToken)))
			{
				string shown = KeyDisplayAliasStorage.GetDisplay(_keyInfoToken);
				DrawCentered(spriteBatch, shown, _keyInfoNameBox, Color.White, maxScale: PanelFillTextMaxScale, minScale: PanelFillTextMinScale, pad: PanelFillTextPad);
			}
			else
			{
				string ph = LocOr("Mods.AdvancedKeybinds.UI.Combo.KeyInfoNamePlaceholder", "キーの表示名変更");
				DrawCentered(
					spriteBatch,
					ph,
					_keyInfoNameBox,
					hasToken ? KeyInfoHintColor : new Color(120, 120, 135),
					maxScale: PanelFillTextMaxScale,
					minScale: PanelFillTextMinScale,
					pad: PanelFillTextPad);
			}

			if (_keyInfoNameEditing)
				DrawKeyInfoNameEditGuides(spriteBatch, _keyInfoGuideRect);
		}

		private static string LocOr(string key, string fallback)
		{
			string v = Language.GetTextValue(key);
			return string.IsNullOrEmpty(v) || v.StartsWith("Mods.", StringComparison.Ordinal) ? fallback : v;
		}

		/// <summary>
		/// While typing a custom display name: three centered guide lines in the right-side lower
		/// empty area, aligned vertically with the left-panel footer hints.
		/// </summary>
		private static void DrawKeyInfoNameEditGuides(SpriteBatch spriteBatch, Rectangle body)
		{
			string line1 = LocOr("Mods.AdvancedKeybinds.UI.Combo.KeyInfoNameGuideLine1", "変更後表示する");
			string line2 = LocOr("Mods.AdvancedKeybinds.UI.Combo.KeyInfoNameGuideLine2", "文字を入力して");
			string line3 = LocOr("Mods.AdvancedKeybinds.UI.Combo.KeyInfoNameGuideLine3", "Enterで決定");
			string[] lines = { line1, line2, line3 };
			float slotH = body.Height / (float)FooterHintLineSlots;
			for (int i = 0; i < lines.Length; i++)
			{
				var row = new Rectangle(
					body.X,
					(int)(body.Y + i * slotH),
					body.Width,
					Math.Max(1, (int)Math.Ceiling(slotH)));
				DrawCentered(
					spriteBatch,
					lines[i],
					row,
					i == 0 ? Color.White : KeyInfoHintColor,
					maxScale: FooterHintMaxScale,
					minScale: 0.45f,
					pad: 4f);
			}
		}

		/// <summary>Left-aligned, vertically centered in a single line box (not full-body centered).</summary>
		private static void DrawKeyInfoTopLeft(SpriteBatch spriteBatch, string text, Rectangle bounds, Color color, float maxScale)
		{
			if (string.IsNullOrEmpty(text) || bounds.Width <= 0 || bounds.Height <= 0)
				return;

			float scale = FitTextScaleToBox(text, bounds.Width, bounds.Height, maxScale, 0.40f);
			Vector2 size = FontAssets.MouseText.Value.MeasureString(text) * scale;
			Vector2 pos = new(bounds.X, bounds.Y + (bounds.Height - size.Y) / 2f);
			Utils.DrawBorderString(spriteBatch, text, pos, color, scale);
		}

		private static Rectangle InflateRect(Rectangle r, int dx, int dy)
			=> new(r.X - dx, r.Y - dy, Math.Max(1, r.Width + dx * 2), Math.Max(1, r.Height + dy * 2));

		private static string BuildPreview()
		{
			if (_working.FilledSlotCount == 0)
				return Language.GetTextValue("Mods.AdvancedKeybinds.UI.Combo.Unset");

			return BindingDisplayResolver.FormatComboDisplayLabel(_working);
		}

		private static string SlotDisplay(int index)
		{
			string token = _working.GetSlot(index);
			if (string.IsNullOrEmpty(token))
				return Language.GetTextValue("Mods.AdvancedKeybinds.UI.Combo.Empty");

			string label = _working.FormatSlotLabel(index);
			return string.IsNullOrEmpty(label)
				? Language.GetTextValue("Mods.AdvancedKeybinds.UI.Combo.Empty")
				: label;
		}

		/// <summary>
		/// When the slot shows a custom display name (global alias or per-slot override),
		/// returns the using-key label and the custom display (using key is drawn; display is hover-only).
		/// </summary>
		private static bool TryGetSlotRenamedDisplay(int index, out string usingKey, out string displayName)
		{
			usingKey = null;
			displayName = null;
			string token = _working.GetSlot(index);
			if (string.IsNullOrEmpty(token))
				return false;

			usingKey = KeyToken.FormatToken(token);
			if (string.IsNullOrEmpty(usingKey))
				return false;

			displayName = KeyToken.FormatDisplayForToken(token, _working.GetSlotDisplay(index));
			if (string.IsNullOrEmpty(displayName)
				|| string.Equals(displayName, usingKey, StringComparison.Ordinal))
				return false;

			return true;
		}

		private static void DrawHoldModeBoxes(SpriteBatch spriteBatch, Point mouse)
		{
			bool holdEnabled = SupportsHold;
			bool whileHeldActive = holdEnabled && _working.Hold && !_working.HoldOnRelease;
			bool onReleaseActive = holdEnabled && _working.Hold && _working.HoldOnRelease;

			Color whileHeldFill = !holdEnabled
				? new Color(40, 40, 46)
				: (whileHeldActive ? new Color(60, 120, 70) : new Color(48, 50, 66));
			Color onReleaseFill = !holdEnabled
				? new Color(40, 40, 46)
				: (onReleaseActive ? new Color(54, 96, 130) : new Color(48, 50, 66));

			Color whileHeldBorder = !holdEnabled
				? new Color(72, 72, 84)
				: (_hoverHoldWhileHeld ? Color.White : new Color(110, 110, 140));
			Color onReleaseBorder = !holdEnabled
				? new Color(72, 72, 84)
				: (_hoverHoldOnRelease ? Color.White : new Color(110, 110, 140));

			DrawBox(spriteBatch, _holdWhileHeldBox, whileHeldFill, whileHeldBorder);
			DrawBox(spriteBatch, _holdOnReleaseBox, onReleaseFill, onReleaseBorder);

			// Divider between the two halves.
			Texture2D pixel = TextureAssets.MagicPixel.Value;
			int dividerY = _holdWhileHeldBox.Bottom - 1;
			spriteBatch.Draw(pixel, new Rectangle(_holdColumn.X, dividerY, _holdColumn.Width, 2), new Color(110, 110, 140));

			Color textColor = holdEnabled ? Color.White : new Color(120, 120, 120);
			DrawHoldModeHalf(spriteBatch, _holdWhileHeldBox,
				Language.GetTextValue("Mods.AdvancedKeybinds.UI.Combo.HoldWhileHeldTitle"),
				Language.GetTextValue("Mods.AdvancedKeybinds.UI.Combo.HoldWhileHeldHint"),
				textColor);
			DrawHoldModeHalf(spriteBatch, _holdOnReleaseBox,
				Language.GetTextValue("Mods.AdvancedKeybinds.UI.Combo.HoldOnReleaseTitle"),
				Language.GetTextValue("Mods.AdvancedKeybinds.UI.Combo.HoldOnReleaseHint"),
				textColor);
		}

		private static void DrawHoldModeHalf(SpriteBatch spriteBatch, Rectangle rect, string title, string hint, Color color)
		{
			const float maxScale = 1f;
			const float minScale = 0.45f;
			const float padW = 8f;
			const float padH = 6f;
			const float hintGap = 4f;
			var font = FontAssets.MouseText.Value;

			float innerW = rect.Width - padW * 2f;
			float innerH = rect.Height - padH * 2f;
			float titleMaxH = innerH * 0.42f;
			float titleScale = FitTextScaleToBox(title, innerW, titleMaxH, maxScale, minScale);
			Vector2 titleSize = font.MeasureString(title) * titleScale;

			float hintMaxH = Math.Max(8f, innerH - titleSize.Y - hintGap);
			float hintScale = FitTextScaleToBox(hint, innerW, hintMaxH, maxScale, minScale);
			Vector2 hintSize = font.MeasureString(hint) * hintScale;

			float blockH = titleSize.Y + hintGap + hintSize.Y;
			float startY = rect.Y + (rect.Height - blockH) / 2f;

			float titleX = rect.X + (rect.Width - titleSize.X) / 2f;
			float hintX = rect.X + (rect.Width - hintSize.X) / 2f;
			Utils.DrawBorderString(spriteBatch, title, new Vector2(titleX, startY), color, titleScale);
			Utils.DrawBorderString(spriteBatch, hint, new Vector2(hintX, startY + titleSize.Y + hintGap), color * 0.92f, hintScale);
		}

		private static readonly string[] FooterHintKeys = {
			"Mods.AdvancedKeybinds.UI.Combo.HintLine1",
			"Mods.AdvancedKeybinds.UI.Combo.HintLine2",
			"Mods.AdvancedKeybinds.UI.Combo.HintLine3",
		};

		/// <summary>Footer hints: one localized line per row; auto-shrinks to stay inside each slot.</summary>
		private static void DrawFooterHints(SpriteBatch spriteBatch, Color hintColor)
		{
			if (_footerHintRect.Width <= 0 || _footerHintRect.Height <= 0)
				return;

			float slotH = _footerHintRect.Height / (float)FooterHintLineSlots;
			for (int i = 0; i < FooterHintLineSlots; i++)
			{
				string text = Language.GetTextValue(FooterHintKeys[i]);
				var slotRect = new Rectangle(
					_footerHintRect.X,
					(int)(_footerHintRect.Y + i * slotH),
					_footerHintRect.Width,
					Math.Max(1, (int)Math.Ceiling(slotH)));
				DrawFittedText(spriteBatch, text, slotRect, hintColor, FooterHintMaxScale);
			}
		}

		/// <summary>Pick the largest scale so text fits inside a box (for localized hint lines).</summary>
		private static float FitTextScaleToBox(string text, float maxWidth, float maxHeight, float maxScale, float minScale)
		{
			if (string.IsNullOrEmpty(text) || maxWidth <= 0f || maxHeight <= 0f)
				return minScale;

			Vector2 raw = FontAssets.MouseText.Value.MeasureString(text);
			if (raw.X <= 0f || raw.Y <= 0f)
				return minScale;

			float scale = Math.Min(maxWidth / raw.X, maxHeight / raw.Y);
			return MathHelper.Clamp(scale, minScale, maxScale);
		}

		private static void DrawHoldControls(SpriteBatch spriteBatch, Point mouse)
		{
			bool editable = HoldEditable;

			// Reset-to-default button (same width as the number box; label is always English).
			Color defaultBtnFill = !editable
				? new Color(38, 38, 46)
				: (_hoverHoldDefaultBtn ? new Color(70, 74, 100) : new Color(50, 52, 70));
			Color defaultBtnBorder = !editable
				? new Color(72, 72, 84)
				: (_hoverHoldDefaultBtn ? Color.White : new Color(110, 110, 140));
			DrawBox(spriteBatch, _holdDefaultBtn, defaultBtnFill, defaultBtnBorder);
			DrawCentered(spriteBatch, HoldDefaultButtonLabel, _holdDefaultBtn,
				editable ? Color.White : new Color(120, 120, 130), maxScale: 0.8f);

			// Number box (typed value; accepts [MinTypedHoldMs, MaxHoldMs]). Greyed out when Hold is off.
			Color numFill = !editable
				? new Color(38, 38, 46)
				: (_holdTyping ? new Color(96, 54, 150) : (_hoverHoldNumber ? new Color(70, 74, 100) : new Color(46, 48, 64)));
			Color numBorder = !editable
				? new Color(72, 72, 84)
				: (_holdTyping ? new Color(220, 170, 255) : (_hoverHoldNumber ? Color.White : new Color(110, 110, 140)));
			DrawBox(spriteBatch, _holdNumberRect, numFill, numBorder);
			string numStr = _holdTyping ? (_holdBuffer.Length == 0 ? "_____" : _holdBuffer) : _working.EffectiveHoldMs.ToString();
			DrawHoldNumberText(spriteBatch, numStr,
				editable ? Color.White : new Color(120, 120, 130),
				editable ? new Color(200, 200, 210) : new Color(110, 110, 120));

			// Slider drawn like the vanilla config slider (ColorBar frame + gradient + handle).
			Texture2D colorBar = TextureAssets.ColorBar.Value;
			Texture2D blip = TextureAssets.ColorBlip.Value;
			Texture2D highlight = TextureAssets.ColorHighlight.Value;
			Texture2D knob = TextureAssets.ColorSlider.Value;

			float dim = editable ? 1f : 0.4f;
			spriteBatch.Draw(colorBar, _holdSliderFrame, Color.White * dim);

			int steps = Math.Max(1, (int)_holdInnerWidth);
			for (int i = 0; i < steps; i++)
			{
				float percent = i / _holdInnerWidth;
				spriteBatch.Draw(blip, new Vector2(_holdInnerX + i, _holdInnerY), null, Color.Lerp(Color.Black, Color.White, percent) * dim, 0f, Vector2.Zero, 1f, SpriteEffects.None, 0f);
			}

			if (editable && (_hoverHoldSlider || _draggingHold))
				spriteBatch.Draw(highlight, _holdSliderFrame, Main.OurFavoriteColor);

			float sliderSpan = ComboBinding.SliderMaxHoldMs - ComboBinding.MinHoldMs;
			int sliderValue = System.Math.Clamp(_working.EffectiveHoldMs, ComboBinding.MinHoldMs, ComboBinding.SliderMaxHoldMs);
			float proportion = sliderSpan > 0
				? MathHelper.Clamp((sliderValue - (float)ComboBinding.MinHoldMs) / sliderSpan, 0f, 1f)
				: 0f;
			spriteBatch.Draw(knob, new Vector2(_holdInnerX + _holdInnerWidth * proportion, _holdInnerY + 4f), null, Color.White * dim, 0f, knob.Size() * 0.5f, 1f, SpriteEffects.None, 0f);
		}

		private static void DrawHoldNumberText(SpriteBatch spriteBatch, string numStr, Color valueColor, Color unitColor)
		{
			var font = FontAssets.MouseText.Value;
			const float maxScale = 0.8f;
			const float minScale = 0.45f;
			const float pad = 8f;
			const float unitGap = 4f;

			float unitRawW = font.MeasureString("ms").X;
			float numMaxW = _holdNumberRect.Width - pad * 2f - unitRawW - unitGap;
			float scale = FitTextScaleToBox(numStr, numMaxW, _holdNumberRect.Height - 4f, maxScale, minScale);

			Vector2 numSize = font.MeasureString(numStr) * scale;
			Vector2 msSize = font.MeasureString("ms") * scale;

			float numY = _holdNumberRect.Y + (_holdNumberRect.Height - numSize.Y) / 2f;
			float msY = _holdNumberRect.Y + (_holdNumberRect.Height - msSize.Y) / 2f;

			Utils.DrawBorderString(spriteBatch, numStr, new Vector2(_holdNumberRect.X + pad, numY), valueColor, scale);
			Utils.DrawBorderString(spriteBatch, "ms", new Vector2(_holdNumberRect.Right - pad - msSize.X, msY), unitColor, scale);
		}

		private static void DrawButton(SpriteBatch spriteBatch, Rectangle rect, string text, Point mouse)
		{
			bool hover = rect.Contains(mouse);
			DrawBox(spriteBatch, rect, hover ? new Color(70, 74, 100) : new Color(50, 52, 70), hover ? Color.White : new Color(110, 110, 140));
			// Cancel / Clear / Save: largest size that still fits the button (multi-language shrink).
			DrawCentered(
				spriteBatch,
				text,
				rect,
				Color.White,
				maxScale: PanelFillTextMaxScale,
				minScale: PanelFillTextMinScale,
				pad: PanelFillTextPad);
		}

		/// <summary>
		/// Vertical wavy splice from the panel's top border to bottom border,
		/// in the gap between the left block and the key-info extension.
		/// </summary>
		private static void DrawKeyInfoSpliceWave(SpriteBatch spriteBatch)
		{
			if (_panel.Width <= 0 || _panel.Height <= 0)
				return;

			const int borderT = 2;
			int yTop = _panel.Y + borderT;
			int yBottom = _panel.Bottom - borderT;
			if (yBottom <= yTop + 4)
				return;

			// Sit in the gap just right of Cancel/Save's shared right edge.
			int edgeX = _backBtn.Width > 0 ? _backBtn.Right : (_panel.X + LeftBlockWidth);
			if (_keyInfoColumn.Width > 0 && _keyInfoColumn.X > edgeX)
				edgeX = edgeX + (_keyInfoColumn.X - edgeX) / 2;

			Texture2D pixel = TextureAssets.MagicPixel.Value;
			Color wave = SlotBorder;
			const float amplitude = 2.5f;
			const float period = 8.0f;
			double twoPiOverPeriod = Math.PI * 2.0 / period;
			const int thickness = 2;

			for (int y = yTop; y < yBottom; y++)
			{
				int waveX = (int)Math.Round(amplitude * Math.Sin((y - yTop) * twoPiOverPeriod));
				spriteBatch.Draw(pixel, new Rectangle(edgeX + waveX - thickness / 2, y, thickness, 1), wave);
			}
		}

		private static void DrawBox(SpriteBatch spriteBatch, Rectangle rect, Color fill, Color border)
		{
			Texture2D pixel = TextureAssets.MagicPixel.Value;
			spriteBatch.Draw(pixel, rect, fill);

			const int t = 2;
			spriteBatch.Draw(pixel, new Rectangle(rect.X, rect.Y, rect.Width, t), border);
			spriteBatch.Draw(pixel, new Rectangle(rect.X, rect.Bottom - t, rect.Width, t), border);
			spriteBatch.Draw(pixel, new Rectangle(rect.X, rect.Y, t, rect.Height), border);
			spriteBatch.Draw(pixel, new Rectangle(rect.Right - t, rect.Y, t, rect.Height), border);
		}

		private static void DrawFittedText(SpriteBatch spriteBatch, string text, Rectangle bounds, Color color, float maxScale, float minScale = 0.45f)
		{
			if (string.IsNullOrEmpty(text) || bounds.Width <= 0 || bounds.Height <= 0)
				return;

			float scale = FitTextScaleToBox(text, bounds.Width, bounds.Height, maxScale, minScale);
			Vector2 size = FontAssets.MouseText.Value.MeasureString(text) * scale;
			Vector2 pos = new(
				bounds.X,
				bounds.Y + (bounds.Height - size.Y) / 2f);
			Utils.DrawBorderString(spriteBatch, text, pos, color, scale);
		}

		private static void DrawCentered(SpriteBatch spriteBatch, string text, Rectangle rect, Color color, float maxScale, float minScale = 0.45f, float pad = 6f)
		{
			if (string.IsNullOrEmpty(text))
				return;

			float innerW = Math.Max(1f, rect.Width - pad * 2f);
			float innerH = Math.Max(1f, rect.Height - pad * 2f);
			float scale = FitTextScaleToBox(text, innerW, innerH, maxScale, minScale);
			DrawCenteredFixedScale(spriteBatch, text, rect, color, scale);
		}

		private static void DrawCenteredFixedScale(SpriteBatch spriteBatch, string text, Rectangle rect, Color color, float scale)
		{
			if (string.IsNullOrEmpty(text))
				return;

			Vector2 size = FontAssets.MouseText.Value.MeasureString(text) * scale;
			Vector2 pos = new(
				rect.X + (rect.Width - size.X) / 2f,
				rect.Y + (rect.Height - size.Y) / 2f);
			Utils.DrawBorderString(spriteBatch, text, pos, color, scale);
		}

		/// <summary>
		/// Shows committed rename text plus IME composition (yellow), matching vanilla chat.
		/// </summary>
		private static void DrawSlotRenameText(SpriteBatch spriteBatch, Rectangle rect, string committed, string composition, float maxScale = 1f, float minScale = 0.45f, float pad = 6f)
		{
			string baseText = committed ?? string.Empty;
			string comp = composition ?? string.Empty;
			string measure = baseText + comp;
			if (measure.Length == 0)
				measure = "_";

			float innerW = Math.Max(1f, rect.Width - pad * 2f);
			float innerH = Math.Max(1f, rect.Height - pad * 2f);
			float scale = FitTextScaleToBox(measure, innerW, innerH, maxScale, minScale);
			var font = FontAssets.MouseText.Value;
			Vector2 totalSize = font.MeasureString(measure) * scale;
			Vector2 pos = new(
				rect.X + (rect.Width - totalSize.X) / 2f,
				rect.Y + (rect.Height - totalSize.Y) / 2f);

			if (baseText.Length == 0 && comp.Length == 0)
			{
				Utils.DrawBorderString(spriteBatch, "_", pos, Color.White, scale);
				return;
			}

			if (baseText.Length > 0)
			{
				Utils.DrawBorderString(spriteBatch, baseText, pos, Color.White, scale);
				pos.X += font.MeasureString(baseText).X * scale;
			}

			if (comp.Length > 0)
				Utils.DrawBorderString(spriteBatch, comp, pos, SlotRenameCompositionColor, scale);
		}
	}
}
