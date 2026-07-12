using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using AdvancedKeybinds.Binding;
using AdvancedKeybinds.Patches;
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

		// After registering a mouse button, ignore slot clicks until that button is released so the same
		// press cannot re-open capture or trigger right-click clear (UpdateInput captures before Draw).
		private static int _blockSlotMouseButtonsUntilUp;

		// Cursor tip for Save-time LeftClick-alone confirmation.
		private static long _rejectLeftClickTipUntilMs;
		private static bool _leftClickConfirmPending;
		private const long RejectLeftClickTipMs = 10000;
		private const float RejectLeftClickTipScale = 1.5f;
		// ~3 cm upward from the default mouse-text spot (96 DPI ≈ 113 px).
		private const float RejectLeftClickTipOffsetY = -113f;
		private static readonly Color RejectLeftClickTipColor = new(0xFF, 0x9B, 0x00);

		// True once the user actually changes something in the panel. Saving/closing without any change
		// must leave the existing binding untouched (never clear or reset it to a default).
		private static bool _dirty;

		// After binding Escape in a slot, block ESC-driven closes for a short real-time window so the
		// same press (or its release / key-repeat edge) cannot also close this panel or the menu behind it.
		private static long _escBindGraceUntilMs;

		// After Escape closes the panel, keep swallowing menu-level ESC briefly so the parent
		// keybind screen does not close on the same press.
		private static long _escDismissGraceUntilMs;

		private const int EscBindGraceMs = 500;
		private const int EscDismissGraceMs = 450;

		/// <summary>Clears overlay UI state after mod unload/reload so stale flags cannot block Escape.</summary>
		public static void ResetStaticState()
		{
			IsOpen = false;
			_actionId = null;
			_actionLabel = null;
			_capturingSlot = 0;
			_dirty = false;
			_escBindGraceUntilMs = 0;
			_escDismissGraceUntilMs = 0;
			_awaitingSlotKey = false;
			_holdTyping = false;
			_draggingHold = false;
			_guardBackgroundUntilMouseUp = false;
			_blockSlotMouseButtonsUntilUp = 0;
			_rejectLeftClickTipUntilMs = 0;
			_leftClickConfirmPending = false;
			_openedSnapshot = null;
			SlotCapture.Cancel();
			BindingInputEngine.IsRecording = false;
			WindowsKeySuppressor.SetModalWindowsKeySuppress(false);
		}

		// True from slot click until a key is captured or capture is cancelled.
		private static bool _awaitingSlotKey;

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

		// ~1.5 cm extra at 96 DPI for a 4-line footer hint band (longer locales wrap without shrinking).
		private const int FooterPanelExtraHeight = 57;
		private const int PanelHeight = 378 + FooterPanelExtraHeight;
		private const int FooterHintTopGap = 12;
		private const int FooterHintBottomGap = 14;
		private const int FooterHintLineSlots = 4;
		private const float FooterHintMaxScale = 1.15f;

		private static Rectangle _headerTitleRect;
		private static Rectangle _headerPreviewRect;
		private static Rectangle _footerHintRect;

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
			_escBindGraceUntilMs = 0;
			_escDismissGraceUntilMs = 0;
			_awaitingSlotKey = false;
			_holdTyping = false;
			_draggingHold = false;
			_holdBuffer = _working.EffectiveHoldMs.ToString();
			_prevKeyState = Main.keyState;
			BindingInputEngine.IsRecording = true;
			WindowsKeySuppressor.SetModalWindowsKeySuppress(true);
			_prevLeft = true; // swallow the opening click's release
			_prevGameLeft = true;
			_blockSlotMouseButtonsUntilUp = 0;
			_rejectLeftClickTipUntilMs = 0;
			_leftClickConfirmPending = false;
			AkDiag.Log($"ComboPanel open action='{actionId}'");
		}

		public static void Close(bool save, bool userInitiated = false)
		{
			if (!IsOpen)
				return;

			if (!userInitiated && IsWithinEscBindGrace())
			{
				AkDiag.Log("ComboPanel close blocked (esc bind grace).");
				return;
			}

			// Commit any in-progress typed hold value before deciding what to persist.
			if (_holdTyping)
			{
				ApplyHoldBuffer();
				_holdTyping = false;
			}

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
				AkDiag.Log("ComboPanel LeftClick-alone save needs confirm; panel stays open.");
				return;
			}

			if (save && wasDirty)
				Commit();

			IsOpen = false;
			_capturingSlot = 0;
			_dirty = false;
			_escBindGraceUntilMs = 0;
			_awaitingSlotKey = false;
			_holdTyping = false;
			_draggingHold = false;
			_openedSnapshot = null;
			SlotCapture.Cancel();
			BindingInputEngine.IsRecording = false;
			WindowsKeySuppressor.SetModalWindowsKeySuppress(false);
			_blockSlotMouseButtonsUntilUp = 0;
			ClearLeftClickConfirmPrompt();

			// If we closed on a mouse click, keep the background frozen until the button is released so
			// the click's release can't reach the "Reset to default" button behind the Save button.
			if (MouseInputHelper.IsPhysicalLeftDown())
				_guardBackgroundUntilMouseUp = true;

			AkDiag.Log($"ComboPanel close save={save} dirty={wasDirty} userInitiated={userInitiated}");
		}

		private static void CancelSlotCaptureKeepPanel()
		{
			_capturingSlot = 0;
			_awaitingSlotKey = false;
			SlotCapture.Cancel();
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
			_holdTyping = false;
			_draggingHold = false;
			_blockSlotMouseButtonsUntilUp = 0;
			_rejectLeftClickTipUntilMs = 0;
			_leftClickConfirmPending = false;

			_working = _openedSnapshot != null ? _openedSnapshot.Clone() : new ComboBinding();
			_holdBuffer = _working.EffectiveHoldMs.ToString();
			_dirty = false;

			SoundEngine.PlaySound(SoundID.MenuOpen);
			AkDiag.Log("ComboPanel restored opened snapshot.");
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
			_escBindGraceUntilMs = 0;
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

			// Keep the purple "press a key" state while Terraria is inactive (e.g. clicking
			// another desktop to screenshot). Do not accept keys or pending Win edges then.
			if (!Main.hasFocus)
			{
				SlotCapture.DiscardPendingEdgesWhileInactive();
				return;
			}

			ProcessSlotCaptureInput();
		}

		public static void HandleEscape()
		{
			if (!IsOpen)
				return;

			// Slot capture runs in the input phase so the binding is committed before Draw/UI back logic.
			if (_awaitingSlotKey || SlotCapture.IsCapturing)
			{
				if (Main.hasFocus)
					ProcessSlotCaptureInput();
				else
					SlotCapture.DiscardPendingEdgesWhileInactive();
				return;
			}

			// After binding Escape, ignore ESC for a short real-time window (same press / release / repeat).
			if (IsWithinEscBindGrace())
			{
				if (Main.keyState.IsKeyDown(Keys.Escape))
					ConsumeEscape();

				return;
			}

			bool escapeJustPressed = Main.keyState.IsKeyDown(Keys.Escape) && !Main.oldKeyState.IsKeyDown(Keys.Escape);
			if (!escapeJustPressed)
				return;

			if (_holdTyping)
			{
				// First Escape while typing a hold value cancels the edit (keeps the panel open).
				_holdTyping = false;
				_holdBuffer = _working.EffectiveHoldMs.ToString();
			}
			else
			{
				AkDiag.Log("ComboPanel Escape dismiss.");
				Close(save: true, userInitiated: true);
				BeginEscDismissGrace();
			}

			ConsumeEscape();
		}

		private static void ProcessSlotCaptureInput()
		{
			bool escapeJustPressed = Main.keyState.IsKeyDown(Keys.Escape) && !Main.oldKeyState.IsKeyDown(Keys.Escape);

			if (escapeJustPressed && _capturingSlot > 0)
			{
				bool ctrlHeld = Main.keyState.IsKeyDown(Keys.LeftControl) || Main.keyState.IsKeyDown(Keys.RightControl);
				if (!ctrlHeld)
				{
					CompleteSlotCapture("Escape");
					ConsumeEscape();
					return;
				}
			}

			SlotCapture.PollResult result = SlotCapture.Poll(out string token);
			if (result == SlotCapture.PollResult.Captured)
			{
				CompleteSlotCapture(token);
				return;
			}

			if (Main.keyState.IsKeyDown(Keys.Escape))
				ConsumeEscape();
		}

		private static void CompleteSlotCapture(string token)
		{
			AkDiag.Log($"ComboPanel CompleteSlotCapture token={token} slot={_capturingSlot}");
			int slot = _capturingSlot;
			bool accepted = TrySetSlot(slot, token);

			// Rejected binds must keep the purple "press a key" wait (LeftClick confirm, duplicates, etc.).
			if (!accepted)
			{
				_capturingSlot = slot;
				_awaitingSlotKey = true;
				SlotCapture.Begin();
				AkDiag.Log("ComboPanel rejected capture; staying on press-a-key.");
				return;
			}

			ClearLeftClickConfirmPrompt();
			_capturingSlot = 0;
			_awaitingSlotKey = false;
			SlotCapture.Cancel();

			// The mouse press that finished capture must not also hit HandleClicks this frame.
			if (KeyToken.IsMouseToken(token))
				_blockSlotMouseButtonsUntilUp |= MouseTokenToButtonMask(token);

			if (string.Equals(token, "LeftWindows", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(token, "RightWindows", StringComparison.OrdinalIgnoreCase))
			{
				WindowsKeySuppressor.ArmWinKeyReleaseGuard();
			}

			if (string.Equals(token, "Escape", StringComparison.OrdinalIgnoreCase))
			{
				ConsumeEscape();
				BeginEscapeBindGrace();
				AkDiag.Log("ComboPanel bound Escape during slot capture.");
			}
		}

		/// <summary>While the modal combo panel is open, strip MenuInquiry before UIManageControls.Update runs.</summary>
		public static void GuardModalInput()
		{
			if (!ShouldSwallowMenuEscape())
				return;

			ClearMenuInquiry(PlayerInput.Triggers.JustPressed);
			ClearMenuInquiry(PlayerInput.Triggers.Current);
		}

		/// <summary>Block parent keybind menu from reacting to Escape while the panel or its dismiss grace is active.</summary>
		public static bool ShouldSwallowMenuEscape()
			=> IsOpen || IsWithinEscBindGrace() || IsWithinEscDismissGrace();

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
		/// While the combo panel is still open, leave Escape in Main.keyState so HandleEscape can close the panel.
		/// After close (dismiss grace) or Escape-bind grace, consume the key so the parent menu does not also close.
		/// </summary>
		public static void PrepareParentMenuForEscape()
		{
			if (!ShouldSwallowMenuEscape())
				return;

			GuardModalInput();

			// Do not eat Escape while the panel is open — that made Esc appear to do nothing.
			if (!IsOpen && Main.keyState.IsKeyDown(Keys.Escape))
				ConsumeEscape();
		}

		private static bool IsWithinEscBindGrace() => Environment.TickCount64 < _escBindGraceUntilMs;

		private static bool IsWithinEscDismissGrace() => Environment.TickCount64 < _escDismissGraceUntilMs;

		/// <summary>After Escape is bound, swallow ESC-driven closes for <see cref="EscBindGraceMs"/>.</summary>
		private static void BeginEscapeBindGrace()
		{
			_escBindGraceUntilMs = Environment.TickCount64 + EscBindGraceMs;
			ConsumeEscape();
		}

		private static void BeginEscDismissGrace()
		{
			_escDismissGraceUntilMs = Environment.TickCount64 + EscDismissGraceMs;
			ConsumeEscape();
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
				AkDiag.Log("ComboPanel commit rejected unsupported binding.");
				return;
			}

			BindingRestrictions.ClearUnsupportedHoldFlags(_working);

			if (_working.RequiresAdvancedStorage())
			{
				BindingStorage.Set(_actionId, _working);
				VanillaRebindHelper.ClearVanillaBinding(_actionId);
			}
			else
			{
				// Keep slot layout (left/middle/right) for reopen; vanilla owns the list label + input.
				BindingStorage.Set(_actionId, _working);
				var keys = _working.Keys;
				if (keys.Count == 1 && !KeyToken.IsMouseToken(keys[0]))
					VanillaRebindHelper.AssignVanillaBinding(_actionId, keys[0]);
				else
					VanillaRebindHelper.ClearVanillaBinding(_actionId);
			}

			if (OpenKeybindMenuDefaults.IsOpenKeybindMenuAction(_actionId))
				OpenKeybindMenuDefaults.MarkCustomized();
		}

		private static bool TrySetSlot(int index, string token)
		{
			if (string.IsNullOrEmpty(token))
				return false;

			for (int i = 1; i <= 3; i++)
			{
				if (i == index)
					continue;

				if (string.Equals(_working.GetSlot(i), token, StringComparison.OrdinalIgnoreCase))
				{
					SoundEngine.PlaySound(SoundID.MenuTick);
					AkDiag.Log($"ComboPanel duplicate rejected token={token}");
					return false;
				}
			}

			string previous = _working.GetSlot(index);
			_working.SetSlot(index, token);
			BindingRestrictions.ClearUnsupportedHoldFlags(_working);

			if (BindingRestrictions.RejectsBinding(_actionId, _working))
			{
				_working.SetSlot(index, previous);
				BindingRestrictions.ClearUnsupportedHoldFlags(_working);
				SoundEngine.PlaySound(SoundID.MenuTick);
				AkDiag.Log("ComboPanel rejected unsupported binding.");
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
			_working.SetSlot(index, null);
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

			Point hover = new((int)Main.MouseScreen.X, (int)Main.MouseScreen.Y);
			_hoverHoldNumber = HoldEditable && _holdNumberRect.Contains(hover);
			_hoverHoldSlider = HoldEditable && HoldSliderHit().Contains(hover);
			_hoverHoldDefaultBtn = HoldEditable && _holdDefaultBtn.Contains(hover);
			_hoverHoldWhileHeld = _holdWhileHeldBox.Contains(hover);
			_hoverHoldOnRelease = _holdOnReleaseBox.Contains(hover);

			// Physical buttons so remapping 対話・動作 / アイテムの使用 does not break panel UI
			// (slot select, right-click clear, hold slider).
			bool leftDown = MouseInputHelper.IsPhysicalLeftDown();
			bool rightDown = MouseInputHelper.IsPhysicalRightDown();
			bool gameLeftDown = Main.mouseLeft;

			// Physical Mouse.GetState() still sees clicks on other windows/monitors. Sync state but
			// do not run click handlers while Terraria is inactive.
			if (!Main.hasFocus)
			{
				_prevLeft = leftDown;
				_prevRight = rightDown;
				_prevGameLeft = gameLeftDown;
				_prevKeyState = Main.keyState;
				_holdTyping = false;
				_draggingHold = false;
				Draw(spriteBatch);
				if (!Main.gameMenu && Main.LocalPlayer != null)
					Main.LocalPlayer.mouseInterface = true;
				Main.HoverItem = new Item();
				Main.hoverItemName = string.Empty;
				DrawRejectLeftClickTipIfActive(spriteBatch);
				return;
			}

			bool leftJustPressed = leftDown && !_prevLeft;
			bool rightJustPressed = rightDown && !_prevRight;
			// Game-processed left click only (does not fire when clicking another window/monitor).
			bool gameLeftJustPressed = gameLeftDown && !_prevGameLeft;

			// Slot capture runs in the input phase and again during Draw so toggle-lock presses are not missed.
			// Back still works during capture so the user can undo without finishing the purple wait.
			if (_awaitingSlotKey || SlotCapture.IsCapturing)
			{
				if (leftJustPressed && _backBtn.Contains(hover))
					RestoreOpenedSnapshot();
				else
					ProcessSlotCaptureInput();

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
			_prevKeyState = Main.keyState;

			Draw(spriteBatch);

			if (!Main.gameMenu && Main.LocalPlayer != null)
				Main.LocalPlayer.mouseInterface = true;

			// Safety net: clear any item tooltip a background UI set this frame (e.g. Recipe Browser's
			// "press LeftControl to translate"). We draw before the tooltip pass, so this suppresses it.
			Main.HoverItem = new Item();
			Main.hoverItemName = string.Empty;

			DrawRejectLeftClickTipIfActive(spriteBatch);
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

		private static void DrawRejectLeftClickTipIfActive(SpriteBatch spriteBatch)
		{
			if (!_leftClickConfirmPending && Environment.TickCount64 >= _rejectLeftClickTipUntilMs)
				return;

			string text = Language.GetTextValue("Mods.AdvancedKeybinds.UI.Combo.ConfirmLeftClickOnly");
			Vector2 pos = new(
				Main.MouseScreen.X,
				Main.MouseScreen.Y + RejectLeftClickTipOffsetY);
			Utils.DrawBorderString(spriteBatch, text, pos, RejectLeftClickTipColor, RejectLeftClickTipScale);
		}

		private static void HandleClicks(bool leftJustPressed, bool rightJustPressed, bool gameLeftJustPressed)
		{
			if (_blockSlotMouseButtonsUntilUp != 0)
			{
				if (IsBlockedMouseButtonHeld(_blockSlotMouseButtonsUntilUp))
					return;

				_blockSlotMouseButtonsUntilUp = 0;
			}

			Point mouse = new((int)Main.MouseScreen.X, (int)Main.MouseScreen.Y);

			if (rightJustPressed)
			{
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
				for (int i = 1; i <= 3; i++)
				{
					if (_slotBox[i].Contains(mouse))
					{
						ClearLeftClickConfirmPrompt();
						_capturingSlot = i;
						_awaitingSlotKey = true;
						SlotCapture.Begin();
						SoundEngine.PlaySound(SoundID.MenuOpen);
						return;
					}
				}

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

				if (_backBtn.Contains(mouse))
				{
					RestoreOpenedSnapshot();
					return;
				}

				if (_closeBtn.Contains(mouse))
				{
					Close(save: true, userInitiated: true);
					return;
				}
			}

			// Click outside the panel = confirm & close.
			// Use game left only: physical left still fires when clicking another window/monitor
			// (often while hasFocus is still true for one frame), which wrongly closed the panel.
			if (gameLeftJustPressed && !_panel.Contains(mouse))
			{
				AkDiag.Log("ComboPanel close from in-game click outside panel.");
				Close(save: true, userInitiated: true);
			}
		}

		private static void ComputeLayout()
		{
			const int w = 600;
			const int h = PanelHeight;
			const int gap = 10;
			int x = (Main.screenWidth - w) / 2;
			int y = (Main.screenHeight - h) / 2;
			_panel = new Rectangle(x, y, w, h);

			int contentLeft = x + ContentMargin;
			int contentRight = x + w - ContentMargin;
			int contentWidth = contentRight - contentLeft;

			int titleTop = y + HeaderTopPad;
			int previewTop = titleTop + HeaderTitleHeight + HeaderLineGap;
			int holdRowTop = previewTop + HeaderPreviewHeight + PreviewToHoldGap;
			int holdCenterY = holdRowTop + HoldControlBoxH / 2;
			int rowY = holdRowTop + HoldControlBoxH + HoldToSlotGap;

			// Back matches the hold ms number box size; sits above it on the title row.
			_backBtn = new Rectangle(contentRight - HoldControlBoxW, titleTop, HoldControlBoxW, HoldControlBoxH);
			int headerTextWidth = Math.Max(40, contentWidth - HoldControlBoxW - gap);
			_headerTitleRect = new Rectangle(contentLeft, titleTop, headerTextWidth, HeaderTitleHeight);
			_headerPreviewRect = new Rectangle(contentLeft, previewTop, headerTextWidth, HeaderPreviewHeight);

			// Hold-duration row spans the content width: [Default box][slider][number box].
			_holdNumberRect = new Rectangle(contentRight - HoldControlBoxW, holdCenterY - HoldControlBoxH / 2, HoldControlBoxW, HoldControlBoxH);
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
			int slotsWidth = contentRight - slotsLeft;
			int slotW = (slotsWidth - gap * 2) / 3;
			for (int i = 1; i <= 3; i++)
				_slotBox[i] = new Rectangle(slotsLeft + (i - 1) * (slotW + gap), rowY, slotW, slotH);

			int btnW = PanelButtonW;
			int btnH = PanelButtonH;
			_clearBtn = new Rectangle(x + ContentMargin, y + h - btnH - 14, btnW, btnH);
			_closeBtn = new Rectangle(x + w - btnW - ContentMargin, y + h - btnH - 14, btnW, btnH);

			int hintAreaTop = rowY + slotH + FooterHintTopGap;
			int hintAreaBottom = _clearBtn.Y - FooterHintBottomGap;
			int hintAreaH = Math.Max(FooterHintLineSlots * 16, hintAreaBottom - hintAreaTop);
			_footerHintRect = new Rectangle(contentLeft, hintAreaTop, contentWidth, hintAreaH);
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
			float proportion = MathHelper.Clamp((Main.MouseScreen.X - _holdInnerX) / _holdInnerWidth, 0f, 1f);
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
			Point mouse = new((int)Main.MouseScreen.X, (int)Main.MouseScreen.Y);
			Texture2D pixel = TextureAssets.MagicPixel.Value;
			Rectangle previousScissor = Main.instance.GraphicsDevice.ScissorRectangle;

			try
			{
				// UIManageControls の描画中はシザー矩形で一覧領域にクリップされていることがあり、
				// オーバーレイ全体が見えず「下のボタンだけ見える」状態になる。
				// ここだけ画面全体へ一時的に広げて、パネルを完全表示する。
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
				for (int i = 1; i <= 3; i++)
				{
					bool capturing = (_awaitingSlotKey || SlotCapture.IsCapturing) && _capturingSlot == i;
					bool hover = _slotBox[i].Contains(mouse);
					Color fill = capturing ? new Color(96, 54, 150) : new Color(46, 48, 64);
					Color border = capturing ? new Color(220, 170, 255) : (hover ? Color.White : new Color(110, 110, 140));
					DrawBox(spriteBatch, _slotBox[i], fill, border);

					string content = capturing
						? Language.GetTextValue("Mods.AdvancedKeybinds.UI.Combo.Press")
						: SlotDisplay(_working.GetSlot(i));
					DrawCentered(spriteBatch, content, _slotBox[i], Color.White, maxScale: 1f);
				}

				// Buttons.
				DrawButton(spriteBatch, _backBtn, Language.GetTextValue("Mods.AdvancedKeybinds.UI.Combo.Undo"), mouse);
				DrawButton(spriteBatch, _clearBtn, Language.GetTextValue("Mods.AdvancedKeybinds.UI.Combo.Clear"), mouse);
				DrawButton(spriteBatch, _closeBtn, Language.GetTextValue("Mods.AdvancedKeybinds.UI.Combo.Save"), mouse);

				DrawFooterHints(spriteBatch, new Color(180, 180, 200));
			}
			finally
			{
				Main.instance.GraphicsDevice.ScissorRectangle = previousScissor;
			}
		}

		private static string BuildPreview()
		{
			var keys = _working.Keys;
			if (keys.Count == 0)
				return Language.GetTextValue("Mods.AdvancedKeybinds.UI.Combo.Unset");

			string joined = KeyToken.FormatSet(keys, compact: true);
			bool isHold = _working.Hold && keys.Count == 1;
			if (!isHold)
				return joined;

			string prefix = _working.HoldOnRelease
				? Language.GetTextValue("Mods.AdvancedKeybinds.UI.Combo.HoldPreviewOnRelease")
				: Language.GetTextValue("Mods.AdvancedKeybinds.UI.Combo.HoldPreviewWhileHeld");
			return prefix + joined;
		}

		private static string SlotDisplay(string token)
		{
			return string.IsNullOrEmpty(token)
				? Language.GetTextValue("Mods.AdvancedKeybinds.UI.Combo.Empty")
				: KeyToken.FormatToken(token);
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
			"Mods.AdvancedKeybinds.UI.Combo.HintLine4",
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
			DrawCentered(spriteBatch, text, rect, Color.White, maxScale: 0.8f);
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
			Vector2 size = FontAssets.MouseText.Value.MeasureString(text) * scale;
			Vector2 pos = new(
				rect.X + (rect.Width - size.X) / 2f,
				rect.Y + (rect.Height - size.Y) / 2f);
			Utils.DrawBorderString(spriteBatch, text, pos, color, scale);
		}
	}
}
