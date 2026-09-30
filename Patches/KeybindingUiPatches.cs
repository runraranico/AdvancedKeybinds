using System;
using AdvancedKeybinds.Features.DoubleTap;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using AdvancedKeybinds.Binding;
using AdvancedKeybinds.Core;
using AdvancedKeybinds.Diagnostics;
using AdvancedKeybinds.UI;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.Audio;
using Terraria.GameContent;
using Terraria.GameContent.UI.Chat;
using Terraria.GameContent.UI.Elements;
using Terraria.GameInput;
using Terraria.ID;
using Terraria.Localization;
using Terraria.ModLoader;
using Terraria.UI;
using Terraria.UI.Chat;
using Terraria.UI.Gamepad;
using ReLogic.Graphics;

namespace AdvancedKeybinds.Patches
{
	/// <summary>
		/// Redirects a control row left-click to open the combo panel (instead of starting a vanilla listen),
		/// physical right-click to clear the binding to unset, physical middle-click to restore the default
		/// binding, and draws advanced combo bindings in the row's key slot.
	/// </summary>
	internal static class KeybindingUiPatches
	{
		private delegate void orig_DrawSelf(UIElement self, SpriteBatch spriteBatch);
		private delegate string orig_GenInput(UIKeybindingListItem self, List<string> list);
		private delegate void orig_OnClick(object self, UIMouseEvent evt, UIElement listeningElement);
		private delegate UIElement orig_CreatePanel(object self, string bind, InputMode mode, Color color);

		/// <summary>While true, vanilla key labels are not drawn (our overlay replaces them).</summary>
		[ThreadStatic]
		private static bool _suppressVanillaKeyLabelDraw;

		private static readonly Dictionary<Type, FieldInfo> _bindFieldCache = new();
		private static readonly HashSet<Type> _bindReflectionFailedTypes = new();
		private static readonly ConditionalWeakTable<object, string> _rowActionIds = new();
		private static readonly ConditionalWeakTable<UIElement, UIElement> _controlsUndoButtons = new();
		private static readonly HashSet<Type> _dumpedTypes = new();
		private static readonly HashSet<Type> _dumpedColorTypes = new();
		/// <summary>Cached GetFriendlyName getters — never GetMethod per DrawSelf.</summary>
		private static readonly Dictionary<Type, MethodInfo> _friendlyNameGetterCache = new();
		/// <summary>Cached <c>_inputmode</c> FieldInfo per row type — never GetField per DrawSelf.</summary>
		private static readonly Dictionary<Type, FieldInfo> _inputModeFieldCache = new();
		private static readonly HashSet<Type> _inputModeFieldMissingTypes = new();

		/// <summary>Clears type caches on Unload so reloads do not keep stale Assembly MethodInfo.</summary>
		public static void ClearCaches()
		{
			_bindFieldCache.Clear();
			_bindReflectionFailedTypes.Clear();
			_dumpedTypes.Clear();
			_dumpedColorTypes.Clear();
			_friendlyNameGetterCache.Clear();
			_inputModeFieldCache.Clear();
			_inputModeFieldMissingTypes.Clear();
			_listeningTriggerField = null;
			_listeningTriggerProperty = null;
			_currentlyRebindingField = null;
			_currentlyRebindingProperty = null;
			_rebindFieldsResolved = false;
		}

		// Fallback accent used when we can't read the row's own color.
		private static readonly Color DefaultAccent = new(120, 110, 180);

		// The private string field that vanilla/tModLoader uses to store the row's action id.
		private static readonly string[] _bindFieldNames = { "_keybind", "keybind", "_bind", "bind" };

		// PlayerInput's rebind session fields, used only as a last-resort resolver for the click path.
		private static FieldInfo _listeningTriggerField;
		private static PropertyInfo _listeningTriggerProperty;
		private static FieldInfo _currentlyRebindingField;
		private static PropertyInfo _currentlyRebindingProperty;
		private static bool _rebindFieldsResolved;

		private const float ControlsUndoButtonWidth = 100f;
		private const float ControlsUndoButtonGap = 10f;
		private static readonly Color AdvancedKeyTextColor = new(210, 190, 255);

		/// <summary>
		/// Matches <c>UIKeybindingListItem.DrawSelf</c> key-label layout (vanilla).
		/// </summary>
		private const float VanillaKeyLabelScale = 0.8f;
		private const float VanillaKeyLabelRightPad = 10f;
		private const float VanillaKeyLabelTopPad = 2f;
		private const float VanillaKeyLabelNameOffsetY = 6f;
		private const float VanillaGlyphsScaleWhileDrawing = 0.85f;
		/// <summary>Matches vanilla friendly-name inset (<c>val3.X += 8f</c> in DrawSelf).</summary>
		private const float VanillaFriendlyNameLeftPad = 8f;
		/// <summary>Minimum gap between action name and key label before shrink/abbreviate.</summary>
		private const float KeyLabelOverlapGap = 8f;
		private const float KeyLabelMinScale = 0.55f;
		private const float KeyLabelScaleStep = 0.05f;
		private const float KeyLabelMinAvailableWidth = 24f;

		// Physical mouse edges for row clear / reset: sampled once per tick, consumed by one row.
		private static bool _physicalRightClearJustPressed;
		private static bool _prevPhysicalRightForRowClear;
		private static bool _physicalMiddleResetJustPressed;
		private static bool _prevPhysicalMiddleForRowReset;
		private static bool _prevPhysicalLeftForUnsetConfirmCancel;
		/// <summary>Physical LMB press edge this input tick (for ComboPanel open source).</summary>
		private static bool _physicalLeftOpenJustPressed;
		/// <summary>
		/// Latched from physical LMB press until consumed by Open or expiry.
		/// Terraria often fires row OnClick after LMB RELEASE, so IsPhysicalLeftDown is already false.
		/// </summary>
		private static bool _physicalLeftOpenArmed;
		private static int _physicalLeftOpenArmedEpoch = int.MinValue;
		private const int PhysicalLeftOpenArmMaxAgeEpochs = 8;
		// Ctrl+Shift+Alt+RMB (etc.) opens controls while RMB is still down; ignore row-unset until up.
		private static bool _suppressRowRightClearUntilRightUp;
		/// <summary>Action id armed for two-step right-click unset; null when idle.</summary>
		private static string _pendingUnsetConfirmActionId;
		/// <summary>
		/// Latched while the confirm tip should follow the mouse. Set on arm / while on the armed
		/// row; cleared with pending. Survives Draw-before-Update ordering (unlike a same-frame flag).
		/// </summary>
		private static bool _pendingUnsetConfirmTipLatch;

		public static void Apply()
		{
			Type listItemType = typeof(UIKeybindingListItem);
			Type toggleItemType = typeof(UIKeybindingToggleListItem);

			HookRowConstructor(listItemType);
			HookRowConstructor(toggleItemType);
			HookGenInputSuppression();
			HookListItem(listItemType);
			HookListItem(toggleItemType);
			HookCreatePanel();
		}

		/// <summary>
		/// Adds a one-level Undo button immediately left of vanilla's profile ("Custom") panel.
		/// The button uses the same UIKeybindingSimpleListItem type and tint as that panel.
		/// ja-JP localization uses 「取消」 for Undo — Pad:B hold focuses this button.
		/// </summary>
		internal static UIElement TryGetControlsUndoButton(UIElement root)
		{
			if (root == null)
				return null;
			return _controlsUndoButtons.TryGetValue(root, out UIElement button) ? button : null;
		}

		/// <summary>
		/// Shared Controls-screen Undo (mouse button, top-bar Pad:A, Pad:B long-press).
		/// Does not close UIManageControls.
		/// </summary>
		internal static bool TryExecuteControlsUndo(string reason)
		{
			_ = reason;
			if (ComboPanel.IsOpen)
				return false;

			ClearPendingUnsetConfirm();
			return ControlsBindingUndo.TryUndo();
		}

		internal static void EnsureControlsUndoButton(UIElement root)
		{
			if (root == null || _controlsUndoButtons.TryGetValue(root, out _))
				return;

			FieldInfo profileField = root.GetType().GetField(
				"_buttonProfile",
				BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
			if (profileField?.GetValue(root) is not UIElement profileButton || profileButton.Parent == null)
				return;

			var undoButton = new UIKeybindingSimpleListItem(
				() => Language.GetTextValue("Mods.AdvancedKeybinds.UI.Combo.Undo"),
				new Color(73, 94, 171, 255) * 0.9f);

			undoButton.HAlign = profileButton.HAlign;
			undoButton.VAlign = profileButton.VAlign;
			undoButton.Width.Set(ControlsUndoButtonWidth, 0f);
			undoButton.Height.Set(profileButton.Height.Pixels, profileButton.Height.Percent);
			undoButton.Top.Set(profileButton.Top.Pixels, profileButton.Top.Percent);
			undoButton.Left.Set(profileButton.Left.Pixels, profileButton.Left.Percent);

			float profileWidth = profileButton.Width.Pixels;
			if (profileWidth <= 0f)
				profileWidth = profileButton.GetDimensions().Width;
			undoButton.MarginRight = profileButton.MarginRight + profileWidth + ControlsUndoButtonGap;

			undoButton.OnLeftClick += (_, _) => {
				if (TryExecuteControlsUndo("MouseClick"))
					SoundEngine.PlaySound(SoundID.MenuOpen);
			};

			profileButton.Parent.Append(undoButton);
			_controlsUndoButtons.Add(root, undoButton);
			if (AkDiag.Enabled) AkDiag.Log("Installed controls Undo button beside profile panel.");
		}

		private static void HookGenInputSuppression()
		{
			MethodInfo genInput = typeof(UIKeybindingListItem).GetMethod("GenInput", BindingFlags.Instance | BindingFlags.NonPublic);
			if (genInput == null)
				return;

			// Empty string would make vanilla show Lang.menu[195] ("未設定").
			// Zero-width space (\u200b) can render as a visible "*" glyph in Terraria's font,
			// so use a normal space when our overlay replaces the label. For plain single-key
			// rows, prefer the combo-panel display string (e.g. VK:0xBF → /) via GenInput itself.
			ModDetours.Apply(genInput, (orig_GenInput orig, UIKeybindingListItem self, List<string> list) => {
				if (_suppressVanillaKeyLabelDraw)
					return " ";
				if (TryResolveCanonicalRowStorageTokens(self, list, out List<string> canonical))
					list = canonical;
				return orig(self, PreferComboPanelDisplayLabels(self, list));
			});
		}

		/// <summary>
		/// Maps raw saved tokens to the same display strings used in combo-panel slots.
		/// Does not mutate the live KeyStatus list.
		/// Alternate GamePad OR on GamePad Controls rows: two Buttons-name cells for vanilla GenInput glyphs.
		/// Stick directions on GamePad Controls rows use FNA Buttons names for GlyphTagHandler (display-only).
		/// </summary>

		private static bool TryResolveCanonicalRowStorageTokens(
			UIElement self,
			List<string> keyStatusListFromDraw,
			out List<string> canonical)
		{
			canonical = null;
			if (self == null)
				return false;

			string actionId = ResolveActionId(self, self.GetType(), forClick: false);
			if (string.IsNullOrEmpty(actionId))
				return false;

			if (!TryGetRowInputMode(self, out InputMode rowMode))
				rowMode = InputMode.Keyboard;

			string storageKey = ControlsGameUiSeparationPolicy.ResolveStorageKey((int)rowMode, actionId);
			bool hasAk = BindingStorage.HasRecord(storageKey);
			IReadOnlyList<string> akKeys = null;
			if (hasAk)
			{
				ComboBinding combo = BindingStorage.Get(storageKey);
				if (combo != null && !combo.IsEmpty && !combo.UseTerrariaKeyStatusOr)
					akKeys = combo.Keys;
				else if (combo != null && combo.UseTerrariaKeyStatusOr)
					hasAk = false;
			}

			IReadOnlyList<string> keyStatus = keyStatusListFromDraw;
			if (keyStatus == null || keyStatus.Count == 0)
				keyStatus = VanillaRebindHelper.GetAssignedKeysForInputMode(actionId, rowMode);

			canonical = ControlsBindingViewResolvePolicy.ResolveCanonicalStorageTokens(
				hasAk && akKeys != null && akKeys.Count > 0,
				akKeys,
				keyStatus,
				(int)rowMode,
				actionId);

			// Display-only: never WriteKeyStatus from GenInput/Draw (breaks capture / purple wait).
			return canonical != null;
		}
		private static List<string> PreferComboPanelDisplayLabels(UIElement self, List<string> list)
		{
			if (TryBuildAlternateOverPrimaryGenInputList(self, out List<string> glyphPair))
				return glyphPair;

			if (list == null || list.Count == 0)
				return list;

			bool isGamePadRow = TryGetRowInputMode(self, out InputMode rowMode)
				&& ControlsGamePadGlyphDisplayPolicy.IsTargetControlsGamePadMode(rowMode.ToString());
			string modeName = isGamePadRow ? rowMode.ToString() : null;
			GamePadControllerFamily padFamily = isGamePadRow
				? GamePadControllerFamilySnapshot.Current
				: GamePadControllerFamily.Unknown;

			List<string> mapped = null;
			for (int i = 0; i < list.Count; i++)
			{
				string token = list[i];
				if (string.IsNullOrEmpty(token))
					continue;

				// Do not rewrite vanilla Buttons names on GamePad rows — GenInput needs them for [g:N].
				if (isGamePadRow
					&& !GamePadToken.IsPadToken(token)
					&& !PhysicalKeyToken.IsFallbackToken(token)
					&& !token.StartsWith("AKAdv:", StringComparison.Ordinal))
				{
					continue;
				}

				if (isGamePadRow && GamePadToken.IsPadToken(BindingDisplayText.UnwrapOwnershipMarkerForDisplay(token)))
				{
					string alias = TryGetUserDisplayAliasOnly(token);
					ControlsGamePadGlyphDisplayPolicy.DisplayPath path =
						ControlsGamePadGlyphDisplayPolicy.ResolveDisplay(
							modeName,
							BindingDisplayText.UnwrapOwnershipMarkerForDisplay(token),
							alias,
							padFamily,
							out string resolved);
					// GlyphButtonsName → Xbox [g:N]; FallbackText → PS plain labels (×○□△ …).
					if (path == ControlsGamePadGlyphDisplayPolicy.DisplayPath.GlyphButtonsName
						|| path == ControlsGamePadGlyphDisplayPolicy.DisplayPath.UserAlias
						|| path == ControlsGamePadGlyphDisplayPolicy.DisplayPath.FallbackText)
					{
						if (!string.Equals(resolved, token, StringComparison.Ordinal))
						{
							mapped ??= new List<string>(list);
							mapped[i] = resolved;
						}
						continue;
					}
				}

				string display = KeyToken.FormatDisplayForToken(token);
				if (string.IsNullOrEmpty(display) || string.Equals(display, token, StringComparison.Ordinal))
					continue;

				mapped ??= new List<string>(list);
				mapped[i] = display;
			}

			return mapped ?? list;
		}

		/// <summary>Display-alias only (does not run FormatToken). Name-change panel storage untouched.</summary>
		private static string TryGetUserDisplayAliasOnly(string token)
		{
			string displayToken = BindingDisplayText.UnwrapOwnershipMarkerForDisplay(token);
			if (string.IsNullOrEmpty(displayToken))
				return null;
			string alias = KeyDisplayAliasStorage.GetDisplay(displayToken);
			return string.IsNullOrEmpty(alias) ? null : alias;
		}

		/// <summary>
		/// GamePad Controls (game/menu): feed GenInput two Buttons names so GlyphTagHandler draws icons.
		/// Keyboard rows: false (unchanged). Does not touch save tokens.
		/// </summary>
		private static bool TryBuildAlternateOverPrimaryGenInputList(UIElement self, out List<string> genInputList)
		{
			genInputList = null;
			if (self == null)
				return false;

			string actionId = ResolveActionId(self, self.GetType(), forClick: false);
			if (string.IsNullOrEmpty(actionId))
				return false;

			if (!TryGetRowInputMode(self, out InputMode rowMode))
				rowMode = InputMode.Keyboard;

			bool isGamePadRow = AlternateGamePadControlsGlyphPolicy.IsGamePadControlsInputModeName(rowMode.ToString());
			string storageKey = ControlsEditTargetPolicy.ToStorageKey(
				ControlsEditTargetPolicy.FromTerrariaInputModeOrdinal((int)rowMode),
				actionId);
			storageKey = ControlsGameUiSeparationPolicy.ResolveStorageKey((int)rowMode, actionId);
			ComboBinding combo = BindingStorage.Get(storageKey);
			if (combo == null)
				return false;

			string primary;
			string alternate;
			if (combo.UseTerrariaKeyStatusOr
				|| GamePadTwoKeyTerrariaOrPolicy.IsTargetStorageKey(storageKey))
			{
				if (string.IsNullOrEmpty(combo.Slot1) || string.IsNullOrEmpty(combo.Slot2))
					return false;
				primary = combo.Slot2;
				alternate = combo.Slot1;
			}
			else if (combo.HasAlternateGamePad && combo.FilledSlotCount == 1)
			{
				primary = combo.Keys[0];
				alternate = combo.AlternateGamePadToken;
			}
			else
				return false;

			if (!AlternateGamePadControlsGlyphPolicy.ShouldUseGlyphPairGenInput(
				    isGamePadRow,
				    hasAlternate: true,
				    GamePadToken.IsPadToken(primary),
				    GamePadToken.IsPadToken(alternate)))
				return false;

			GamePadControllerFamily padFamily = GamePadControllerFamilySnapshot.Current;
			if (!AlternateGamePadControlsGlyphPolicy.TryBuildGenInputPair(
				    primary,
				    alternate,
				    pad => ControlsGamePadGlyphDisplayPolicy.TryResolveGlyphButtonsNameForFamily(
					    padFamily, pad, out string b)
					    ? b
					    : null,
				    pad => ControlsGamePadGlyphDisplayPolicy.FormatControlsFallbackForFamily(padFamily, pad),
				    out AlternateGamePadControlsGlyphPolicy.DisplayEntry left,
				    out AlternateGamePadControlsGlyphPolicy.DisplayEntry right))
				return false;

			genInputList = AlternateGamePadControlsGlyphPolicy.ToGenInputList(left, right);
			return genInputList != null && genInputList.Count == 2;
		}

		private delegate void orig_KeybindCtor(object self, string bind, InputMode mode, Color color);

		private static void HookRowConstructor(Type itemType)
		{
			if (itemType == null)
				return;

			ConstructorInfo ctor = itemType.GetConstructor(
				BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
				null,
				new[] { typeof(string), typeof(InputMode), typeof(Color) },
				null);

			if (ctor == null)
				return;

			ModDetours.Apply(ctor, (orig_KeybindCtor orig, object self, string bind, InputMode mode, Color color) => {
				orig(self, bind, mode, color);
				RememberRowActionId(self, bind);
				if (self is UIElement row)
				{
					HookRowRightClickClear(row, bind, mode);
					HookRowMiddleClickReset(row, bind, mode);
				}
			});
		}

		/// <summary>
		/// Two-step unset on physical right mouse button (hardware Mouse2):
		/// first press arms a mouse-following confirm tip; second press on the same row clears
		/// the binding. Leaving the armed row cancels the confirm (tip disappears). Edge is
		/// sampled once per input tick and consumed on use. Ignored while the left button is
		/// held (scroll/drag). Does not use UI OnRightClick.
		/// Hooked on both the keybind list item and CreatePanel (full Mod Controls row including
		/// Reset) so hover over the Reset half still works — same coverage as middle-click.
		/// </summary>
		private static void HookRowRightClickClear(UIElement row, string bind, InputMode mode)
		{
			row.OnUpdate += _ => {
				TryProcessPhysicalRightClickUnset(row, bind, mode, row.IsMouseHovering);
			};
		}

		/// <summary>
		/// Shared unset edge for list-item and CreatePanel. Returns after consume so only one
		/// handler per press acts. Prefer live <see cref="TryGetRowInputMode"/> when available.
		/// </summary>
		private static void TryProcessPhysicalRightClickUnset(
			UIElement hoverSource,
			string bind,
			InputMode constructedMode,
			bool isHovering)
		{
			if (ComboPanel.IsOpen || string.IsNullOrEmpty(bind))
				return;

			InputMode mode = constructedMode;
			if (hoverSource != null && TryGetRowInputMode(hoverSource, out InputMode live))
				mode = live;

			// Cancel when the cursor leaves the armed row. Inner list-item must not cancel while the
			// parent CreatePanel (Reset half) is still hovered — same full-row coverage as middle-click.
			if (IsPendingUnsetConfirm(bind))
			{
				if (!isHovering)
				{
					if (IsAncestorMouseHovering(hoverSource))
						return;
					ClearPendingUnsetConfirm();
					return;
				}

				_pendingUnsetConfirmTipLatch = true;
			}

			if (!isHovering)
				return;

			// Left-drag / scrollbar drag must never clear rows.
			if (MouseInputHelper.IsPhysicalLeftDown())
				return;

			if (!TryConsumePhysicalRightJustPressedForRowClear())
				return;

			if (IsPendingUnsetConfirm(bind))
			{
				ClearPendingUnsetConfirm();
				ClearRowToUnset(bind, mode);
				return;
			}

			if (!RowHasAssignableBindingForUnset(bind, mode))
				return;

			ArmPendingUnsetConfirm(bind);
		}

		/// <summary>True when a parent UIElement still reports mouse hover (CreatePanel over Reset).</summary>
		private static bool IsAncestorMouseHovering(UIElement element)
		{
			for (UIElement p = element?.Parent; p != null; p = p.Parent)
			{
				if (p.IsMouseHovering)
					return true;
			}

			return false;
		}

		/// <summary>
		/// True when AK storage and/or vanilla KeyStatus (row mode + Keyboard sibling) has a binding.
		/// </summary>
		private static bool RowHasAssignableBindingForUnset(string bind, InputMode mode)
		{
			string storageKey = ControlsGameUiSeparationPolicy.ResolveStorageKey((int)mode, bind);
			bool hadAdvanced = BindingStorage.HasRecord(storageKey)
				|| BindingStorage.HasAdvancedBindings(storageKey);
			bool hadVanilla = !string.IsNullOrEmpty(
				VanillaRebindHelper.GetAssignedKeyForInputMode(bind, mode));
			if (!hadVanilla
				&& ControlsRowUnsetArmPolicy.IncludeKeyboardSiblingMode((int)mode))
			{
				InputMode sibling = mode == InputMode.Keyboard
					? InputMode.KeyboardUI
					: InputMode.Keyboard;
				hadVanilla = !string.IsNullOrEmpty(
					VanillaRebindHelper.GetAssignedKeyForInputMode(bind, sibling));
			}

			if (!hadVanilla && !ControlsEditTargetPolicy.IsGamePadModeOrdinal((int)mode))
				hadVanilla = !string.IsNullOrEmpty(VanillaRebindHelper.GetAssignedKey(bind));

			return ControlsRowUnsetArmPolicy.ShouldArmConfirm(hadAdvanced, hadVanilla);
		}

		/// <summary>
		/// Restore a single row to its profile default on physical middle mouse button (hardware wheel click).
		/// Uses the device button, not the Middle Mouse action — remapping that action must not break this.
		/// </summary>
		private static void HookRowMiddleClickReset(UIElement row, string bind, InputMode mode)
		{
			row.OnUpdate += _ => {
				if (ComboPanel.IsOpen || !row.IsMouseHovering)
					return;

				if (MouseInputHelper.IsPhysicalLeftDown() || MouseInputHelper.IsPhysicalRightDown())
					return;

				if (!TryConsumePhysicalMiddleJustPressedForRowReset())
					return;

				ClearPendingUnsetConfirm();
				// Prefer live _inputmode so GAME (Keyboard) never restores KeyboardUI (S/Down) defaults.
				InputMode effective = mode;
				if (TryGetRowInputMode(row, out InputMode live))
					effective = live;
				ResetRowToDefault(bind, effective);
			};
		}

		/// <summary>
		/// Call once per input tick (before UI row updates) so press edges are stable.
		/// </summary>
		internal static void SyncPhysicalRowMouseEdges()
		{
			bool rightDown = MouseInputHelper.IsPhysicalRightDown();
			if (_suppressRowRightClearUntilRightUp)
			{
				// Opening chord used MouseRight: eat its held edge; re-arm only after a full up.
				_physicalRightClearJustPressed = false;
				if (!rightDown)
				{
					_suppressRowRightClearUntilRightUp = false;
					_prevPhysicalRightForRowClear = false;
				}
				else
				{
					_prevPhysicalRightForRowClear = true;
				}
			}
			else
			{
				_physicalRightClearJustPressed = rightDown && !_prevPhysicalRightForRowClear;
				_prevPhysicalRightForRowClear = rightDown;
			}

			bool middleDown = MouseInputHelper.IsPhysicalMiddleDown();
			_physicalMiddleResetJustPressed = middleDown && !_prevPhysicalMiddleForRowReset;
			_prevPhysicalMiddleForRowReset = middleDown;

			// Left-click cancels a pending unset confirm (does not change bindings).
			// Also arms ComboPanel open: Terraria often fires row OnClick after LMB RELEASE,
			// so edge/down alone is insufficient. Keep the latch while LMB is held (refresh epoch),
			// then allow a short post-release window for delayed OnClick.
			bool leftDown = MouseInputHelper.IsPhysicalLeftDown();
			_physicalLeftOpenJustPressed = leftDown && !_prevPhysicalLeftForUnsetConfirmCancel;
			if (_physicalLeftOpenJustPressed)
				ClearPendingUnsetConfirm();

			if (leftDown)
			{
				_physicalLeftOpenArmed = true;
				_physicalLeftOpenArmedEpoch = FnaGamePadProvider.InputTickEpoch;
			}
			else if (_physicalLeftOpenArmed
				&& _physicalLeftOpenArmedEpoch != int.MinValue
				&& FnaGamePadProvider.InputTickEpoch - _physicalLeftOpenArmedEpoch > PhysicalLeftOpenArmMaxAgeEpochs)
			{
				_physicalLeftOpenArmed = false;
				_physicalLeftOpenArmedEpoch = int.MinValue;
			}

			_prevPhysicalLeftForUnsetConfirmCancel = leftDown;
		}

		private static void ConsumePhysicalLeftOpenArm()
		{
			_physicalLeftOpenArmed = false;
			_physicalLeftOpenArmedEpoch = int.MinValue;
			_physicalLeftOpenJustPressed = false;
		}

		/// <summary>
		/// Decide whether a keybind-row OnClick may open ComboPanel, and with which input source.
		/// Order: Close gate → Save epoch gate → suppress if Pad:A already direct-opened →
		/// physical mouse → UI confirm (Pad:A) edge → matching Pending (Pad:A gesture only) →
		/// refuse IsDown-only / unmatched clicks. Never treats Item Use as UI confirm.
		/// </summary>
		internal static bool TryResolveComboPanelOpenIntent(
			string clickActionId,
			out bool openedByGamePad,
			out string source,
			out string blockReason,
			out string gamePadOpenToken)
		{
			openedByGamePad = false;
			source = "Other";
			blockReason = null;
			gamePadOpenToken = null;

			string confirmTok = AkUiPadNavigationPolicy.ConfirmToken;
			bool confirmEdge = FnaGamePadProvider.WasJustPressed(confirmTok)
				&& AkUiPadNavigationPolicy.ShouldAcceptConfirmEdge(wasJustPressed: true, isDownOnly: false);
			bool confirmDown = FnaGamePadProvider.IsDown(confirmTok);
			bool physicalLeftDown = MouseInputHelper.IsPhysicalLeftDown();
			bool physicalLeftEdge = _physicalLeftOpenJustPressed;
			bool physicalLeftArmed = _physicalLeftOpenArmed;

			if (ComboPanelCloseInputConsumeGate.ShouldBlockParentKeybindRowOpen())
			{
				source = confirmDown || confirmEdge ? (confirmEdge ? "PadConfirmEdge" : "PadConfirmHeld") : "Other";
				blockReason = "CloseInputConsumeGate";
				return false;
			}

			if (ComboPanel.ShouldBlockOpenAfterSaveClose())
			{
				source = confirmEdge ? "PadConfirmEdge" : (confirmDown ? "PadConfirmHeld" : (physicalLeftDown || physicalLeftArmed ? "PhysicalMouse" : "Other"));
				blockReason = "SaveCloseEpochGate";
				return false;
			}

			// Pad:A already opened this hold via PollArm direct path — ignore delayed ItemUse OnClick.
			if (PendingGamePadRowConfirm.ShouldSuppressOnClickOpen)
			{
				source = confirmEdge || confirmDown ? "PadConfirmEdge" : "Other";
				blockReason = "AlreadyDirectOpened";
				return false;
			}

			// Physical mouse: edge, currently down, or armed latch (OnClick often after RELEASE).
			if ((physicalLeftEdge || physicalLeftDown || physicalLeftArmed)
				&& !confirmEdge
				&& !confirmDown)
			{
				source = "PhysicalMouse";
				openedByGamePad = false;
				ConsumePhysicalLeftOpenArm();
				return true;
			}

			if (confirmEdge)
			{
				source = "PadConfirmEdge";
				openedByGamePad = true;
				gamePadOpenToken = confirmTok;
				return true;
			}

			if (PendingGamePadRowConfirm.TryConsumeMatching(
				clickActionId,
				out string pendingSource,
				out string pendingReject,
				out string pendingToken))
			{
				source = pendingSource;
				openedByGamePad = true;
				gamePadOpenToken = pendingToken ?? confirmTok;
				return true;
			}

			if (!string.IsNullOrEmpty(pendingReject))
			{
				source = "PadConfirmPending";
				blockReason = pendingReject;
				return false;
			}

			if (confirmDown)
			{
				source = "PadConfirmHeld";
				blockReason = "PadConfirmHeldNotEdge";
				return false;
			}

			source = "Other";
			blockReason = "NoTrustedInputSource";
			return false;
		}

		/// <summary>
		/// Opens ComboPanel from Pad:A PollArm without synthesizing MouseLeft / OnClick.
		/// </summary>
		internal static bool TryOpenComboPanelFromPadConfirm(
			UIElement row,
			string actionId,
			string openToken,
			out string rejectReason)
		{
			rejectReason = null;
			if (row == null || string.IsNullOrEmpty(actionId))
			{
				rejectReason = "NoRowOrAction";
				return false;
			}

			if (ComboPanel.IsOpen)
			{
				rejectReason = "ComboPanelAlreadyOpen";
				return false;
			}

			if (ComboPanelCloseInputConsumeGate.ShouldBlockParentKeybindRowOpen())
			{
				rejectReason = "CloseInputConsumeGate";
				return false;
			}

			if (ComboPanel.ShouldBlockOpenAfterSaveClose())
			{
				rejectReason = "SaveCloseEpochGate";
				return false;
			}

			if (!TryGetRowInputMode(row, out InputMode rowMode))
				rowMode = InputMode.Keyboard;

			string token = string.IsNullOrEmpty(openToken)
				? AkUiPadNavigationPolicy.ConfirmToken
				: openToken;
			GamePadControlTabProbe.NoteComboPanelOpenedFromRow(actionId, rowMode);
			ControlsSectionHeaderSupport.TryResolveRowSectionSemantic(row, out string sectionSemantic);
			ComboPanel.Open(
				actionId,
				GetRowLabel(row, actionId),
				GetRowAccent(row),
				openedByGamePad: true,
				gamePadOpenToken: token,
				openSource: ComboPanelOpenSource.GamePad,
				editInputMode: rowMode,
				sectionSemantic: sectionSemantic);
			return true;
		}

		/// <summary>
		/// Maps trusted open-intent labels to a session-fixed <see cref="ComboPanelOpenSource"/>.
		/// Does not poll live InputMode — only the source string from this open attempt.
		/// </summary>
		private static ComboPanelOpenSource MapComboPanelOpenSource(string openSource, bool openedByGamePad)
		{
			if (openedByGamePad
				|| string.Equals(openSource, "PadConfirmEdge", StringComparison.Ordinal)
				|| string.Equals(openSource, "PadConfirmPending", StringComparison.Ordinal)
				|| string.Equals(openSource, "DirectPadAOpen", StringComparison.Ordinal)
				|| string.Equals(openSource, "PadAEdge", StringComparison.Ordinal)
				|| string.Equals(openSource, "PadAPending", StringComparison.Ordinal))
				return ComboPanelOpenSource.GamePad;

			if (string.Equals(openSource, "PhysicalMouse", StringComparison.Ordinal))
				return ComboPanelOpenSource.Mouse;

			if (string.Equals(openSource, "Keyboard", StringComparison.Ordinal))
				return ComboPanelOpenSource.Keyboard;

			// Non-GamePad trusted opens today are physical mouse only.
			return ComboPanelOpenSource.Mouse;
		}

		/// <summary>Resolve action id for a list row element (PendingGamePadRowConfirm arm).</summary>
		internal static string TryResolveActionIdFromRowElement(UIElement row)
		{
			if (row == null)
				return null;
			return ResolveActionId(row, row.GetType(), forClick: false);
		}

		/// <summary>True when the row's InputMode is XBoxGamepad / XBoxGamepadUI (controller Controls pages).</summary>
		internal static bool IsGamePadControlsListItem(UIElement row)
		{
			if (row == null || !TryGetRowInputMode(row, out InputMode mode))
				return false;
			return AlternateGamePadControlsGlyphPolicy.IsGamePadControlsInputModeName(mode.ToString());
		}

		private static bool IsPendingUnsetConfirm(string actionId)
			=> !string.IsNullOrEmpty(_pendingUnsetConfirmActionId)
				&& string.Equals(_pendingUnsetConfirmActionId, actionId, StringComparison.Ordinal);

		private static void ArmPendingUnsetConfirm(string actionId)
		{
			if (string.IsNullOrEmpty(actionId))
				return;

			_pendingUnsetConfirmActionId = actionId;
			_pendingUnsetConfirmTipLatch = true;
			SoundEngine.PlaySound(SoundID.MenuTick);
			if (AkDiag.Enabled) AkDiag.Log($"Row right-click unset confirm armed for '{actionId}'.");
		}

		private static void ClearPendingUnsetConfirm()
		{
			_pendingUnsetConfirmActionId = null;
			_pendingUnsetConfirmTipLatch = false;
		}

		/// <summary>
		/// After opening the controls UI with MouseRight still held (hidden shortcut / hotkey),
		/// do not treat that press as row-right-click unset until the button is released once.
		/// </summary>
		internal static void SuppressRowRightClearUntilPhysicalRightUp()
		{
			_suppressRowRightClearUntilRightUp = true;
			_physicalRightClearJustPressed = false;
			_prevPhysicalRightForRowClear = MouseInputHelper.IsPhysicalRightDown();
			if (AkDiag.Enabled) AkDiag.Log("Row right-clear suppressed (open-right-click) until RMB up.");
		}

		/// <summary>Obsolete name kept for call-site clarity during transition; prefer <see cref="SyncPhysicalRowMouseEdges"/>.</summary>
		internal static void SyncPhysicalRightClearEdge() => SyncPhysicalRowMouseEdges();

		private static bool TryConsumePhysicalRightJustPressedForRowClear()
		{
			if (_suppressRowRightClearUntilRightUp || !_physicalRightClearJustPressed)
				return false;

			_physicalRightClearJustPressed = false;
			return true;
		}

		private static bool TryConsumePhysicalMiddleJustPressedForRowReset()
		{
			if (!_physicalMiddleResetJustPressed)
				return false;

			_physicalMiddleResetJustPressed = false;
			return true;
		}

		private static void ClearRowToUnset(string actionId, InputMode mode)
		{
			if (string.IsNullOrEmpty(actionId))
				return;

			ControlsEditTargetPolicy.DeviceKind kind =
				ControlsEditTargetPolicy.FromTerrariaInputModeOrdinal((int)mode);
			string storageKey = ControlsGameUiSeparationPolicy.ResolveStorageKey((int)mode, actionId);
			bool gamePad = kind == ControlsEditTargetPolicy.DeviceKind.GamePad;
			bool twoKeyTerrariaOr = GamePadTwoKeyTerrariaOrPolicy.IsTarget((int)mode, actionId);
			bool modKeybind = ControlsGameUiSeparationPolicy.IsModKeybindActionId(actionId);

			if (!RowHasAssignableBindingForUnset(actionId, mode))
				return;

			ControlsBindingUndo.Snapshot undo = ControlsBindingUndo.Capture(storageKey);

			if (OpenKeybindMenuDefaults.IsOpenKeybindMenuAction(actionId))
				OpenKeybindMenuDefaults.MarkCustomized();
			if (OpenKeybindMenuDefaults.IsHoldTierHudHotkeyAction(actionId))
				OpenKeybindMenuDefaults.MarkHoldTierHudHotkeyCustomized();

			BindingStorage.ClearBindings(storageKey, "row-right-click-unset");
			if (twoKeyTerrariaOr)
			{
				// ClearBindings already persisted empty + synced this InputMode;
				// Game→UI mirror also clears Menu Combos/KeyStatus when editing Game.
			}
			else if (modKeybind && gamePad)
			{
				VanillaRebindHelper.ClearVanillaBindingForInputModes(
					actionId,
					"row-right-click-unset-mod-gamepad",
					InputMode.XBoxGamepad,
					InputMode.XBoxGamepadUI);
			}
			else if (modKeybind)
			{
				VanillaRebindHelper.ClearVanillaBinding(actionId, "row-right-click-unset-mod");
			}
			else
			{
				// Vanilla GAME/UI: clear the edited InputMode.
				VanillaRebindHelper.ClearVanillaBindingForInputModes(
					actionId,
					"row-right-click-unset-mode",
					mode);
				// Game→UI one-way: also clear Menu KeyStatus when editing Game Controls.
				if (ControlsGameUiSeparationPolicy.TryGetGameToUiMirrorInputModeOrdinal(
					    storageKey, out int uiModeOrdinal))
				{
					VanillaRebindHelper.ClearVanillaBindingForInputModes(
						actionId,
						"row-right-click-unset-game-to-ui",
						(InputMode)uiModeOrdinal);
					BindingStorage.TryPersistPlayerInputProfilesAfterGamePadEdit(
						"row-right-click-unset-game-to-ui");
				}
			}
			ControlsBindingUndo.RememberIfChanged(undo, "row-right-click-unset");
			SoundEngine.PlaySound(SoundID.MenuClose);
			if (AkDiag.Enabled) AkDiag.Log($"Row right-click cleared binding to unset for '{storageKey}'.");
		}

		internal static bool TryExecuteRowResetToDefault(string actionId)
		{
			if (string.IsNullOrEmpty(actionId) || ComboPanel.IsOpen)
				return false;
			ResetRowToDefault(actionId, InputMode.Keyboard);
			return true;
		}

		private static void ResetRowToDefault(string actionId, InputMode mode)
		{
			if (string.IsNullOrEmpty(actionId))
				return;

			ControlsEditTargetPolicy.DeviceKind kind =
				ControlsEditTargetPolicy.FromTerrariaInputModeOrdinal((int)mode);
			string storageKey = ControlsGameUiSeparationPolicy.ResolveStorageKey((int)mode, actionId);
			bool gamePad = kind == ControlsEditTargetPolicy.DeviceKind.GamePad;
			bool twoKeyTerrariaOr = GamePadTwoKeyTerrariaOrPolicy.IsTarget((int)mode, actionId);
			bool modKeybind = ControlsGameUiSeparationPolicy.IsModKeybindActionId(actionId);
			ControlsRowResetDefaultAction resetAction = ControlsRowResetDefaultPolicy.Resolve(
				modKeybind,
				twoKeyTerrariaOr);

			ControlsBindingUndo.Snapshot undo = ControlsBindingUndo.Capture(storageKey);

			OpenKeybindMenuDefaults.MarkCustomizedIfTracked(actionId);

			if (resetAction == ControlsRowResetDefaultAction.ClearTwoKeyTerrariaOrRecord)
			{
				// Exact (Gamepad, GAME, Up|Down|Left|Right): Reset → Slot1=LS direction, Slot2 unset,
				// persist AK record. Other TwoKey rows: explicit empty Persist (product unset).
				if (GamePadTwoKeyTerrariaOrPolicy.TryGetGameMovementLeftStickDefault(
					    (int)mode, actionId, out string lsDefault))
				{
					var restored = new ComboBinding {
						Slot1 = lsDefault,
						Slot2 = null,
						UseTerrariaKeyStatusOr = true,
						PersistAsEmptyRecord = false
					};
					BindingStorage.TryCommitGamePadTwoKeyTerrariaOr(
						storageKey, restored, "row-reset-default-two-key-game-move-ls");
					ControlsBindingUndo.RememberIfChanged(undo, "row-reset-default-two-key-game-move-ls");
				}
				else
				{
					BindingStorage.ClearBindings(storageKey, "row-reset-default-two-key");
					ControlsBindingUndo.RememberIfChanged(undo, "row-reset-default-two-key");
				}
				SoundEngine.PlaySound(SoundID.MenuOpen);
				if (AkDiag.Enabled) AkDiag.Log($"Row reset two-key Terraria-OR for '{storageKey}'.");
				return;
			}

			// ClearBindings syncs KeyStatus to empty for the storage mode. If the profile is not
			// writable, skip Clear+Restore so we do not leave Jump/etc permanently unset.
			if (resetAction != ControlsRowResetDefaultAction.RestoreModDefault
				&& !VanillaRebindHelper.IsProfileReady())
			{
				if (AkDiag.Enabled) AkDiag.Log($"Row reset aborted: profile not ready for '{storageKey}'.");
				return;
			}

			BindingStorage.ClearBindings(storageKey, "row-reset-default");

			if (resetAction == ControlsRowResetDefaultAction.RestoreModDefault)
			{
				if (gamePad)
				{
					VanillaRebindHelper.ClearVanillaBindingForInputModes(
						actionId,
						"row-reset-default-mod-gamepad",
						InputMode.XBoxGamepad,
						InputMode.XBoxGamepadUI);
				}
				else if (OpenKeybindMenuDefaults.TryGetRegisteredDefaultKey(actionId, out string registeredDefault))
					VanillaRebindHelper.AssignVanillaBinding(actionId, registeredDefault, "row-reset-default-open-menu");
				else if (ModKeybindReflection.TryGetRegisteredDefaultBinding(actionId, out string modDefault))
					VanillaRebindHelper.AssignVanillaBinding(actionId, modDefault, "row-reset-default-modkeybind");
				else
					VanillaRebindHelper.ResetVanillaBindingToDefault(actionId, "row-reset-default-original-profiles");
			}
			else
			{
				// Vanilla Keyboard / KeyboardUI / GamePad / GamePadUI:
				// exact OriginalProfiles[edited mode][action] (OfficialDefaultKeyRestoreLogic).
				// Game→UI one-way: after Game restore, copy that KeyStatus list to Menu
				// (do not restore Menu from its own OriginalProfiles — those often differ / empty).
				VanillaRebindHelper.ResetVanillaBindingToDefaultForInputModes(
					actionId,
					"row-reset-default-original-mode",
					mode);
				BindingStorage.DiscardEmptyPersistAfterVanillaDefaultRestore(storageKey);

				if (ControlsGameUiSeparationPolicy.TryGetGameToUiMirrorInputModeOrdinal(
					    storageKey, out int uiModeOrdinal)
					&& ControlsGameUiSeparationPolicy.TryGetGameToUiMirrorStorageKey(
					    storageKey, out string uiStorageKey))
				{
					VanillaRebindHelper.CopyAssignedKeysBetweenInputModes(
						actionId,
						mode,
						(InputMode)uiModeOrdinal,
						"row-reset-default-game-to-ui-copy");
					BindingStorage.DiscardEmptyPersistAfterVanillaDefaultRestore(uiStorageKey);
					BindingStorage.SaveBindingsFileAfterMirrorDefaultRestore();
					BindingStorage.TryPersistPlayerInputProfilesAfterGamePadEdit(
						"row-reset-default-game-to-ui");
				}
				else
				{
					BindingStorage.SaveBindingsFileAfterMirrorDefaultRestore();
					if (gamePad)
						BindingStorage.TryPersistPlayerInputProfilesAfterGamePadEdit(
							"row-reset-default");
				}
			}

			ControlsBindingUndo.RememberIfChanged(undo, "row-reset-default");
			SoundEngine.PlaySound(SoundID.MenuOpen);
			if (AkDiag.Enabled) AkDiag.Log($"Row reset storage='{storageKey}' mode={mode} action={resetAction}.");
		}

		/// <summary>
		/// The controls menu's per-row "Reset to default" button only resets vanilla's own KeyConfiguration.
		/// Advanced combo/hold/mouse bindings live separately in bindings.json (BindingStorage) and would
		/// otherwise survive a reset, keeping the row's advanced label. We hook the row-building method so
		/// that clicking Reset also clears our stored binding for that action.
		/// </summary>
		private static void HookCreatePanel()
		{
			Type manageType = typeof(UIState).Assembly.GetType("Terraria.GameContent.UI.States.UIManageControls");
			MethodInfo createPanel = manageType?.GetMethod("CreatePanel",
				BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
				null, new[] { typeof(string), typeof(InputMode), typeof(Color) }, null);

			if (createPanel == null)
			{
				AdvancedKeybinds.Instance?.Logger.Warn("Advanced Keybinds could not hook UIManageControls.CreatePanel; \"Reset to default\" will not clear advanced bindings.");
				return;
			}

			ModDetours.Apply(createPanel, (orig_CreatePanel orig, object self, string bind, InputMode mode, Color color) => {
				UIElement panel = orig(self, bind, mode, color);
				if (panel != null && !string.IsNullOrEmpty(bind))
				{
					RegisterRowActionId(panel, bind);
					HookResetButton(panel, bind, mode);
				}

				return panel;
			});
		}

		/// <summary>
		/// Adds a left-click handler to the whole row panel. Clicks bubble up from children, so this fires
		/// for both the key row and the Reset button. We ignore clicks whose target is inside the keybinding
		/// row itself (those open the combo panel); anything else in the row is the Reset button, so we clear
		/// the advanced binding for that action to complete the reset.
		/// </summary>
		private static void HookResetButton(UIElement panel, string bind, InputMode mode)
		{
			panel.OnLeftClick += (evt, listeningElement) => {
				if (ComboPanel.IsOpen)
					return;
				if (IsWithinKeybindRow(evt.Target))
					return;

				// Full row reset (same as middle-click), not only clearing advanced storage.
				ResetRowToDefault(bind, mode);
			};

			// Middle-click / right-click unset: whole CreatePanel row (key slot + Reset label).
			panel.OnUpdate += _ => {
				if (ComboPanel.IsOpen || !panel.IsMouseHovering)
				{
					// Still run unset cancel path when leaving the full panel.
					TryProcessPhysicalRightClickUnset(panel, bind, mode, panel.IsMouseHovering);
					return;
				}

				TryProcessPhysicalRightClickUnset(panel, bind, mode, isHovering: true);

				if (MouseInputHelper.IsPhysicalLeftDown() || MouseInputHelper.IsPhysicalRightDown())
					return;

				if (!TryConsumePhysicalMiddleJustPressedForRowReset())
					return;

				ClearPendingUnsetConfirm();
				// CreatePanel passes the list's InputMode (Keyboard vs KeyboardUI); keep it.
				ResetRowToDefault(bind, mode);
			};
		}

		/// <summary>True if the clicked element is the keybinding row (or a child of it), not the Reset button.</summary>
		private static bool IsWithinKeybindRow(UIElement target)
		{
			for (UIElement e = target; e != null; e = e.Parent)
			{
				if (e is UIKeybindingListItem or UIKeybindingToggleListItem)
					return true;
			}

			return false;
		}

		/// <summary>
		/// Only real keybind rows open ComboPanel. Headers, SimpleListItem (Undo/Profile), and
		/// default-reset rows must not read <c>UIKeybindingListItem._keybind</c>.
		/// </summary>
		private static bool IsComboPanelEligibleKeybindRow(object self)
		{
			return self is UIKeybindingListItem or UIKeybindingToggleListItem;
		}

		private static void HookListItem(Type itemType)
		{
			if (itemType == null)
				return;

			MethodInfo onClick = itemType.GetMethod("OnClickMethod", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			if (onClick != null)
			{
				ModDetours.Apply(onClick, (orig_OnClick orig, object self, UIMouseEvent evt, UIElement listeningElement) => {
					if (AkDiag.Enabled) AkDiag.Log($"Keybinding row click type='{itemType.Name}' panelOpen={ComboPanel.IsOpen}");

					// While the panel is open, swallow row clicks (the panel handles its own input).
					if (ComboPanel.IsOpen)
					{
						if (AkDiag.Enabled) AkDiag.Log("Keybinding row click swallowed because combo panel is already open.");
						GamePadProbeSystem.LogKeybindRowOpenEarlyReturn("ComboPanelAlreadyOpen");
						return;
					}

					// Never treat SimpleListItem / HeaderElement / unknown as ListItem rows.
					if (!IsComboPanelEligibleKeybindRow(self))
					{
						string runtimeName = self?.GetType().Name ?? "null";
						if (AkDiag.Enabled)
							AkDiag.Log($"Keybinding row click ignored: ineligible type='{runtimeName}' (hooked as '{itemType.Name}').");
						GamePadProbeSystem.LogKeybindRowOpenEarlyReturn("IneligibleRowType:" + runtimeName);
						return;
					}

					bool mousePhysical = MouseInputHelper.IsPhysicalLeftDown() || _physicalLeftOpenArmed || _physicalLeftOpenJustPressed;
					GamePadProbeSystem.LogKeybindRowClickHookEnter(
						itemType.Name,
						mousePhysical,
						FnaGamePadProvider.WasJustPressed(GamePadToken.A),
						UILinkPointNavigator.CurrentPoint,
						ControlsGamepadAutoScrollPatches.IsManualScrollActive);

						string actionId = ResolveActionId(self, itemType, forClick: true);
						if (string.IsNullOrEmpty(actionId))
						{
							// Reflection couldn't find the id. Instead of falling back to a real vanilla
							// rebind (which causes the input-lock bug), let vanilla briefly set its
							// ListeningTrigger, read it, and cancel the listen immediately.
							if (AkDiag.Enabled) AkDiag.Log($"actionId unresolved via reflection for type='{itemType.Name}'; probing via vanilla ListeningTrigger.");
							actionId = ProbeTriggerViaVanilla(self, evt, listeningElement, orig);

							if (string.IsNullOrEmpty(actionId))
							{
								if (AkDiag.Enabled) AkDiag.Log("Probe failed: ListeningTrigger was empty. Ignoring click (panel not opened).");
								GamePadProbeSystem.LogKeybindRowActionResolve(success: false, action: null, reason: "Unresolved");
								GamePadProbeSystem.LogKeybindRowOpenEarlyReturn("ActionIdUnresolved");
								return;
							}

							if (AkDiag.Enabled) AkDiag.Log($"Probe resolved action='{actionId}' via vanilla ListeningTrigger.");
						}

						GamePadProbeSystem.LogKeybindRowActionResolve(success: true, action: actionId, reason: null);
						GamePadProbeSystem.LogKeybindRowOpenResolveEnter(actionId);

						if (!TryResolveComboPanelOpenIntent(
							actionId,
							out bool openedByGamePad,
							out string openSource,
							out string blockReason,
							out string resolvedOpenToken))
						{
							#if AK_DIAGNOSTICS
							PadConfirmAfterComboSaveDiagSystem.ObserveOpenIntent(
								actionId, accepted: false, openSource, blockReason, openedByGamePad: false);
							#endif
							GamePadProbeSystem.LogPanelOpenAttempt(
								actionId,
								openSource,
								FnaGamePadProvider.WasJustPressed(GamePadToken.A),
								FnaGamePadProvider.IsDown(GamePadToken.A),
								ComboPanelCloseInputConsumeGate.ShouldBlockParentKeybindRowOpen(),
								FnaGamePadProvider.InputTickEpoch);
							GamePadProbeSystem.LogPanelOpenBlocked(actionId, blockReason ?? "Unknown");
							GamePadProbeSystem.LogKeybindRowOpenEarlyReturn(blockReason ?? "OpenIntentRejected");
							if (AkDiag.Enabled)
								AkDiag.Log($"ComboPanel open blocked action='{actionId}' source={openSource} reason={blockReason}");
							return;
						}

						#if AK_DIAGNOSTICS
						PadConfirmAfterComboSaveDiagSystem.ObserveOpenIntent(
							actionId, accepted: true, openSource, blockReason: null, openedByGamePad);
						#endif

						GamePadProbeSystem.LogPanelOpenAttempt(
							actionId,
							openSource,
							FnaGamePadProvider.WasJustPressed(GamePadToken.A),
							FnaGamePadProvider.IsDown(GamePadToken.A),
							ComboPanelCloseInputConsumeGate.ShouldBlockParentKeybindRowOpen(),
							FnaGamePadProvider.InputTickEpoch);

						// Open our combo panel instead of starting a vanilla listen. This is the key change
						// that eliminates the old input-lock / stuck-listen problems entirely.
						if (AkDiag.Enabled) AkDiag.Log($"Opening combo panel for action='{actionId}' source={openSource}.");
						string gamePadOpenToken = openedByGamePad
							? (resolvedOpenToken ?? AkUiPadNavigationPolicy.ConfirmToken)
							: null;
						ComboPanelOpenSource panelOpenSource = MapComboPanelOpenSource(openSource, openedByGamePad);

						if (!TryGetRowInputMode(self as UIElement, out InputMode rowMode))
							rowMode = InputMode.Keyboard;
						GamePadControlTabProbe.NoteComboPanelOpenedFromRow(actionId, rowMode);
						ControlsSectionHeaderSupport.TryResolveRowSectionSemantic(
							self as UIElement, out string sectionSemantic);
						ComboPanel.Open(
							actionId,
							GetRowLabel(self as UIElement, actionId),
							GetRowAccent(self),
							openedByGamePad,
							gamePadOpenToken,
							panelOpenSource,
							rowMode,
							sectionSemantic);
				});
			}

			MethodInfo drawSelf = itemType.GetMethod("DrawSelf", BindingFlags.Instance | BindingFlags.NonPublic);
			if (drawSelf == null)
				return;

			ModDetours.Apply(drawSelf, (orig_DrawSelf orig, UIElement self, SpriteBatch spriteBatch) => {
				bool drawOverlay = ShouldDrawKeybindRowOverlay(self, itemType);
				if (drawOverlay)
					_suppressVanillaKeyLabelDraw = true;
				try
				{
					orig(self, spriteBatch);
				}
				finally
				{
					_suppressVanillaKeyLabelDraw = false;
				}

				if (drawOverlay)
					DrawKeybindRowOverlay(self, spriteBatch, itemType);
			});
		}

		/// <summary>
		/// Draws advanced combo labels. Clear/reset tip is shown as a hover tooltip on the
		/// Controls header via <see cref="DrawControlsHeaderTip"/>.
		/// </summary>
		private static bool ShouldDrawKeybindRowOverlay(UIElement self, Type itemType)
		{
			string actionId = ResolveActionId(self, itemType, forClick: false);
			if (string.IsNullOrEmpty(actionId))
				return false;

			if (!TryGetRowInputMode(self, out InputMode rowMode))
				rowMode = InputMode.Keyboard;
			string storageKey = ControlsEditTargetPolicy.ToStorageKey(
				ControlsEditTargetPolicy.FromTerrariaInputModeOrdinal((int)rowMode),
				actionId);
			storageKey = ControlsGameUiSeparationPolicy.ResolveStorageKey((int)rowMode, actionId);

			ComboBinding combo = BindingStorage.Get(storageKey);
			if (combo == null)
				return false;

			// Terraria-OR two-key: KeyStatus owns display/fire — no advanced overlay.
			if (combo.UseTerrariaKeyStatusOr
				|| GamePadTwoKeyTerrariaOrPolicy.IsTargetStorageKey(storageKey))
				return false;

			// Plain single-key taps use vanilla's label path. GenInput already remaps raw tokens
			// (VK:/SC:/Oem*) to the same glyphs as the combo-panel slots, so no overlay is needed.
			// DT is a Controls display override only; shared single-tap input policy stays unchanged.
			if (DoubleTapControlsDisplay.IsActive(combo))
				return true;
			if (combo.IsPlainSingleTap)
				return false;
			// Must use OrPair (not Single): Alternate 2-key GamePad rows need the overlay so
			// Menu Controls (XBoxGamepadUI) get [g:] icons like Game Controls — Single would
			// skip overlay and GenInput can fall back to "R2/Pad ←" text.
			if (ControlsEditTargetPolicy.IsGamePadStorageKey(storageKey)
				&& ControlsEditTargetPolicy.IsVanillaOwnedGamePadOrPair(
					combo.Hold,
					combo.Keys,
					combo.AlternateGamePadToken))
				return false;

			return true;
		}

		private static void DrawKeybindRowOverlay(UIElement self, SpriteBatch spriteBatch, Type itemType)
		{
			string actionId = ResolveActionId(self, itemType, forClick: false);
			if (string.IsNullOrEmpty(actionId))
				return;

#if AK_DIAGNOSTICS
			BindingMutationDiag.ObserveControlsRow(actionId);
#endif

			if (!TryGetRowInputMode(self, out InputMode rowMode))
				rowMode = InputMode.Keyboard;
			string storageKey = ControlsEditTargetPolicy.ToStorageKey(
				ControlsEditTargetPolicy.FromTerrariaInputModeOrdinal((int)rowMode),
				actionId);
			storageKey = ControlsGameUiSeparationPolicy.ResolveStorageKey((int)rowMode, actionId);

			ComboBinding combo = BindingStorage.Get(storageKey);
			if (combo == null)
				return;

			string fullLabel = BuildComboLabelForControlsRow(combo, rowMode);
			string comboLabel = fullLabel;
			if (string.IsNullOrEmpty(fullLabel))
				fullLabel = Lang.menu[195].Value;

			// Pixel-match UIKeybindingListItem.DrawSelf; shrink then abbreviate if it would cover the name.
			CalculatedStyle dimensions = self.GetDimensions();
			float maxWidth = dimensions.Width + 1f;
			var font = FontAssets.ItemStack.Value;
			FitAdvancedKeyLabel(
				self,
				actionId,
				combo,
				fullLabel,
				dimensions,
				font,
				out string display,
				out float drawScale);

			Vector2 baseScale = new(drawScale);
			Vector2 stringSize = ChatManager.GetStringSize(font, display, baseScale);
			Vector2 position = new(
				dimensions.X + dimensions.Width - stringSize.X - VanillaKeyLabelRightPad,
				dimensions.Y + VanillaKeyLabelTopPad + VanillaKeyLabelNameOffsetY);

			if (TryGetRowInputMode(self, out InputMode inputMode)
				&& (inputMode == InputMode.XBoxGamepad || inputMode == InputMode.XBoxGamepadUI))
			{
				position += new Vector2(0f, -3f);
			}

			float previousGlyphScale = GlyphTagHandler.GlyphsScale;
			GlyphTagHandler.GlyphsScale = VanillaGlyphsScaleWhileDrawing;
			try
			{
				ChatManager.DrawColorCodedStringWithShadow(
					spriteBatch,
					font,
					display,
					position,
					DoubleTapControlsDisplay.TextColor(combo, AdvancedKeyTextColor),
					0f,
					Vector2.Zero,
					baseScale,
					maxWidth);
			}
			finally
			{
				GlyphTagHandler.GlyphsScale = previousGlyphScale;
			}

			if (!self.IsMouseHovering || ComboPanel.IsOpen)
				return;

			var keySlotRect = new Rectangle(
				(int)Math.Floor(position.X),
				(int)Math.Floor(position.Y),
				Math.Max(1, (int)Math.Ceiling(stringSize.X)),
				Math.Max(1, (int)Math.Ceiling(stringSize.Y)));
			var mouse = new Point(Main.mouseX, Main.mouseY);
			if (!keySlotRect.Contains(mouse))
				return;

			// Tooltip always shows the full (pre-abbreviate) label.
			string tooltip = BuildHoverTooltip(actionId, combo, comboLabel);
			if (!string.IsNullOrEmpty(tooltip))
				Main.instance.MouseText(tooltip);
		}

		/// <summary>
		/// 1) Shrink toward <see cref="KeyLabelMinScale"/> while the key label would overlap the
		///    vanilla action name. 2) If still too wide, abbreviate joined parts (e.g. A+…+C).
		/// Measurement uses the same ChatManager + ItemStack path as vanilla DrawSelf.
		/// </summary>
		private static void FitAdvancedKeyLabel(
			UIElement self,
			string actionId,
			ComboBinding combo,
			string fullLabel,
			CalculatedStyle dimensions,
			DynamicSpriteFont font,
			out string display,
			out float scale)
		{
			display = fullLabel ?? string.Empty;
			scale = VanillaKeyLabelScale;

			float available = ComputeKeyLabelAvailableWidth(self, actionId, dimensions, font);
			if (available <= 0f || string.IsNullOrEmpty(display))
				return;

			if (ChatManager.GetStringSize(font, display, new Vector2(scale)).X <= available)
				return;

			for (float s = VanillaKeyLabelScale - KeyLabelScaleStep; s >= KeyLabelMinScale - 0.0001f; s -= KeyLabelScaleStep)
			{
				float use = Math.Max(KeyLabelMinScale, s);
				if (ChatManager.GetStringSize(font, display, new Vector2(use)).X <= available)
				{
					scale = use;
					return;
				}
			}

			scale = KeyLabelMinScale;
			display = AbbreviateKeyLabel(combo, fullLabel, font, scale, available);
		}

		private static float ComputeKeyLabelAvailableWidth(
			UIElement self,
			string actionId,
			CalculatedStyle dimensions,
			ReLogic.Graphics.DynamicSpriteFont font)
		{
			string friendly = ResolveDrawnFriendlyName(self, actionId);
			float leftWidth = 0f;
			if (!string.IsNullOrEmpty(friendly))
				leftWidth = ChatManager.GetStringSize(font, friendly, new Vector2(VanillaKeyLabelScale)).X;

			// Right-aligned key ends at Width-10; left name starts at +8. Leave OverlapGap between them.
			float available = dimensions.Width
				- VanillaKeyLabelRightPad
				- VanillaFriendlyNameLeftPad
				- leftWidth
				- KeyLabelOverlapGap;
			return Math.Max(KeyLabelMinAvailableWidth, available);
		}

		private static string ResolveDrawnFriendlyName(UIElement self, string actionId)
		{
			if (self != null)
			{
				try
				{
					Type type = self.GetType();
					if (!_friendlyNameGetterCache.TryGetValue(type, out MethodInfo mi))
					{
						mi = type.GetMethod(
							"GetFriendlyName",
							BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
						if (mi != null && mi.GetParameters().Length != 0)
							mi = null;
						_friendlyNameGetterCache[type] = mi;
					}

					if (mi != null)
					{
						object value = mi.Invoke(self, null);
						if (value is string s && !string.IsNullOrEmpty(s))
							return s;
					}
				}
				catch
				{
				}
			}

			return GetRowLabel(self, actionId);
		}

		/// <summary>
		/// Shortens a multi-part combo for the row (keeps Hold prefix). Full text remains in tooltip.
		/// </summary>
		private static string AbbreviateKeyLabel(
			ComboBinding combo,
			string fullLabel,
			DynamicSpriteFont font,
			float scale,
			float available)
		{
			if (string.IsNullOrEmpty(fullLabel))
				return fullLabel;

			string prefix = string.Empty;
			string joined = fullLabel;
			if (DoubleTapControlsDisplay.IsActive(combo)
				&& fullLabel.StartsWith(DoubleTapControlsDisplay.Prefix, StringComparison.Ordinal))
			{
				prefix = DoubleTapControlsDisplay.Prefix;
				joined = fullLabel[prefix.Length..];
			}
			else if (combo != null && combo.Hold && combo.FilledSlotCount == 1)
			{
				prefix = combo.HoldOnRelease
					? Language.GetTextValue("Mods.AdvancedKeybinds.UI.Combo.HoldPreviewOnRelease")
					: Language.GetTextValue("Mods.AdvancedKeybinds.UI.Combo.HoldPreviewWhileHeld");
				if (!string.IsNullOrEmpty(prefix) && fullLabel.StartsWith(prefix, StringComparison.Ordinal))
					joined = fullLabel[prefix.Length..];
			}

			float prefixWidth = string.IsNullOrEmpty(prefix)
				? 0f
				: ChatManager.GetStringSize(font, prefix, new Vector2(scale)).X;
			float budget = Math.Max(8f, available - prefixWidth);

			string[] parts = joined.Split('+');
			if (parts.Length >= 3)
			{
				string candidate = parts[0] + "+…+" + parts[parts.Length - 1];
				if (ChatManager.GetStringSize(font, candidate, new Vector2(scale)).X <= budget)
					return prefix + candidate;
			}

			if (parts.Length >= 2)
			{
				string candidate = parts[0] + "+…";
				if (ChatManager.GetStringSize(font, candidate, new Vector2(scale)).X <= budget)
					return prefix + candidate;
			}

			return prefix + TruncateWithEllipsis(font, parts.Length > 0 ? parts[0] : joined, scale, budget);
		}

		private static string TruncateWithEllipsis(
			DynamicSpriteFont font,
			string text,
			float scale,
			float budget)
		{
			if (string.IsNullOrEmpty(text))
				return text;

			Vector2 scaleV = new(scale);
			if (ChatManager.GetStringSize(font, text, scaleV).X <= budget)
				return text;

			const string ellipsis = "…";
			float ellipsisW = ChatManager.GetStringSize(font, ellipsis, scaleV).X;
			if (ellipsisW >= budget)
				return ellipsis;

			for (int len = text.Length - 1; len >= 1; len--)
			{
				string candidate = text[..len] + ellipsis;
				if (ChatManager.GetStringSize(font, candidate, scaleV).X <= budget)
					return candidate;
			}

			return ellipsis;
		}

		private static UIElement _cachedControlsTitle;
		private static HashSet<string> _cachedTitleCandidates;
		private static GameCulture _cachedTitleCulture;

		/// <summary>
		/// Hover tip for the Controls ("操作設定") header button.
		/// Static text to the right of the header was removed; show the same lines as MouseText.
		/// </summary>
		internal static void DrawControlsHeaderTip(UIElement root, SpriteBatch spriteBatch)
		{
			if (root == null || ComboPanel.IsOpen)
				return;

			// Pending unset confirm must be applied in Draw (not OnUpdate): vanilla/UI redraw
			// clears MouseText set during Update before the tooltip pass.
			if (DrawPendingUnsetConfirmTip())
				return;

			UIElement title = _cachedControlsTitle;
			if (title == null || !IsUsableTitleElement(title))
			{
				title = FindControlsTitleElement(root);
				_cachedControlsTitle = title;
			}

			if (title == null)
				return;

			CalculatedStyle dims = title.GetDimensions();
			if (dims.Width <= 0f || dims.Height <= 0f)
				return;

			var mouse = new Point(Main.mouseX, Main.mouseY);
			var titleRect = new Rectangle((int)dims.X, (int)dims.Y, (int)dims.Width, (int)dims.Height);
			if (!title.IsMouseHovering && !titleRect.Contains(mouse))
				return;

			string tip = BuildControlsHeaderHoverTip();
			if (!string.IsNullOrEmpty(tip))
				Main.instance.MouseText(tip);
		}

		/// <summary>
		/// Mouse-following confirm after first right-click on a keybind row.
		/// Uses a latch set while the cursor stays on the armed row; leaving that row clears pending
		/// in OnUpdate so the tip disappears on other rows without breaking the 2nd-click unset.
		/// Clears HoverItem so item/translate tooltips (e.g. on custom display names) do not win.
		/// </summary>
		private static bool DrawPendingUnsetConfirmTip()
			=> TryForcePendingUnsetConfirmTipPriority();

		/// <summary>
		/// Re-assert the unset-confirm MouseText just before Vanilla: Mouse Text so other UI/mods
		/// cannot replace it with item or translation tips while the confirm is armed.
		/// </summary>
		internal static bool TryForcePendingUnsetConfirmTipPriority()
		{
			if (string.IsNullOrEmpty(_pendingUnsetConfirmActionId)
				|| !_pendingUnsetConfirmTipLatch
				|| ComboPanel.IsOpen)
				return false;

			string tip = Language.GetTextValue("Mods.AdvancedKeybinds.UI.KeybindRow.ConfirmUnsetHover");
			if (string.IsNullOrEmpty(tip))
				return false;

			// Custom display names can be treated as item/hover text by other mods ("press LeftControl to translate").
			Main.HoverItem = new Item();
			Main.hoverItemName = string.Empty;
			Main.instance.MouseText(tip);
			return true;
		}

		private static string BuildControlsHeaderHoverTip()
			=> Language.GetTextValue("Mods.AdvancedKeybinds.UI.KeybindRow.OutsideSlotTip");

		private static bool IsUsableTitleElement(UIElement title)
		{
			try
			{
				CalculatedStyle dims = title.GetDimensions();
				return dims.Width > 40f && dims.Height > 16f;
			}
			catch
			{
				return false;
			}
		}

		/// <summary>
		/// Locates the Controls header button (e.g. "操作設定" / SettingsControls) near the top-left.
		/// </summary>
		private static UIElement FindControlsTitleElement(UIElement root)
		{
			HashSet<string> titles = GetControlsTitleCandidates();
			UIElement best = null;
			float bestScore = float.MaxValue;
			FindControlsTitleElementCore(root, titles, depth: 0, ref best, ref bestScore);
			return best;
		}

		private static HashSet<string> GetControlsTitleCandidates()
		{
			GameCulture culture = Language.ActiveCulture;
			if (_cachedTitleCandidates != null && ReferenceEquals(_cachedTitleCulture, culture))
				return _cachedTitleCandidates;

			var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			AddTitleCandidate(set, Language.GetTextValue("UI.SettingsControls"));
			AddTitleCandidate(set, Language.GetTextValue("UI.Controls"));
			AddTitleCandidate(set, Language.GetTextValue("UI.ControlSettings"));
			AddTitleCandidate(set, "操作設定");
			AddTitleCandidate(set, "Controls");
			_cachedTitleCandidates = set;
			_cachedTitleCulture = culture;
			return set;
		}

		private static void AddTitleCandidate(HashSet<string> set, string value)
		{
			if (!string.IsNullOrWhiteSpace(value))
				set.Add(value.Trim());
		}

		private static void FindControlsTitleElementCore(
			UIElement element,
			HashSet<string> titles,
			int depth,
			ref UIElement best,
			ref float bestScore)
		{
			if (element == null || depth > 40)
				return;

			if (element.GetType().Name.Contains("UITextPanel"))
			{
				string text = TryGetUiTextPanelText(element);
				if (!string.IsNullOrEmpty(text) && titles.Contains(text.Trim()))
				{
					CalculatedStyle dims = element.GetDimensions();
					if (dims.Width > 40f && dims.Height > 16f)
					{
						float score = dims.Y * 10000f + dims.X;
						if (score < bestScore)
						{
							bestScore = score;
							best = element;
						}
					}
				}
			}

			foreach (UIElement child in element.Children)
				FindControlsTitleElementCore(child, titles, depth + 1, ref best, ref bestScore);
		}

		private static string TryGetUiTextPanelText(UIElement element)
		{
			if (element == null)
				return null;

			try
			{
				PropertyInfo textProp = element.GetType().GetProperty("Text", BindingFlags.Instance | BindingFlags.Public);
				object value = textProp?.GetValue(element);
				if (value is string s && !string.IsNullOrEmpty(s))
					return s;
				if (value is LocalizedText lt)
					return lt.Value;

				FieldInfo textField = element.GetType().GetField("_text", BindingFlags.Instance | BindingFlags.NonPublic);
				object fieldVal = textField?.GetValue(element);
				if (fieldVal is string fs && !string.IsNullOrEmpty(fs))
					return fs;
				if (fieldVal is LocalizedText flt)
					return flt.Value;
			}
			catch
			{
			}

			return null;
		}

		/// <summary>
		/// Combo line, then same-key on-release Hold tiers (ms ascending) so multi-Hold keys are visible without opening the panel.
		/// </summary>
		private static string BuildHoverTooltip(string actionId, ComboBinding combo, string comboLabel)
		{
			string tiers = BuildSameKeyReleaseHoldTooltip(actionId, combo);
			if (string.IsNullOrEmpty(tiers))
				return comboLabel;

			return comboLabel + "\n" + tiers;
		}

		private static string BuildSameKeyReleaseHoldTooltip(string actionId, ComboBinding combo)
		{
			if (combo == null || !combo.Hold || !combo.HoldOnRelease)
				return null;

			List<string> keys = combo.Keys;
			if (keys.Count != 1)
				return null;

			string keyToken = keys[0];
			List<(int ms, string id)> tiers = new();
			foreach (KeyValuePair<string, ComboBinding> pair in BindingStorage.AllBindings)
			{
				ComboBinding other = pair.Value;
				if (!other.Hold || !other.HoldOnRelease)
					continue;

				List<string> otherKeys = other.Keys;
				if (otherKeys.Count != 1 || !string.Equals(otherKeys[0], keyToken, StringComparison.OrdinalIgnoreCase))
					continue;

				tiers.Add((other.EffectiveHoldMs, pair.Key));
			}

			if (tiers.Count == 0)
				return null;

			tiers.Sort((a, b) => a.ms != b.ms ? a.ms.CompareTo(b.ms) : string.Compare(a.id, b.id, StringComparison.OrdinalIgnoreCase));

			StringBuilder sb = new();
			for (int i = 0; i < tiers.Count; i++)
			{
				if (i > 0)
					sb.Append('\n');

				string name = FormatActionTooltipName(tiers[i].id);
				sb.Append(tiers[i].ms).Append("ms: ").Append(name);
			}

			return sb.ToString();
		}

		private static string FormatActionTooltipName(string actionId)
		{
			if (string.IsNullOrEmpty(actionId))
				return "?";

			int slash = actionId.LastIndexOf('/');
			if (slash >= 0 && slash < actionId.Length - 1)
				return actionId[(slash + 1)..];

			return actionId;
		}

		private static string BuildComboLabel(ComboBinding combo)
			=> BindingDisplayResolver.FormatComboDisplayLabel(combo);

		/// <summary>
		/// Overlay labels: on GamePad Controls rows, prefer GlyphTagHandler Buttons names
		/// (including stick directions). Keyboard / ComboPanel keep plain FormatComboDisplayLabel.
		/// </summary>
		private static string BuildComboLabelForControlsRow(ComboBinding combo, InputMode rowMode)
		{
			if (combo == null || combo.IsEmpty)
				return string.Empty;

			string modeName = rowMode.ToString();
			if (!ControlsGamePadGlyphDisplayPolicy.IsTargetControlsGamePadMode(modeName))
				return BindingDisplayResolver.FormatControlsDisplayLabel(combo);

			GamePadControllerFamily padFamily = GamePadControllerFamilySnapshot.Current;
			if (combo.HasAlternateGamePad
				&& combo.FilledSlotCount == 1
				&& GamePadToken.IsPadToken(combo.Keys[0])
				&& GamePadToken.IsPadToken(combo.AlternateGamePadToken)
				&& AlternateGamePadControlsGlyphPolicy.TryBuildGenInputPair(
					combo.Keys[0],
					combo.AlternateGamePadToken,
					pad => ControlsGamePadGlyphDisplayPolicy.TryResolveGlyphButtonsNameForFamily(
						padFamily, pad, out string b)
						? b
						: null,
					pad => ControlsGamePadGlyphDisplayPolicy.FormatControlsFallbackForFamily(padFamily, pad),
					out AlternateGamePadControlsGlyphPolicy.DisplayEntry left,
					out AlternateGamePadControlsGlyphPolicy.DisplayEntry right))
			{
				string joined = AlternateGamePadControlsGlyphPolicy.JoinForChatOverlay(
					left,
					right,
					buttonsName => GlyphTagHandler.GenerateTag(buttonsName));
				return ApplyControlsHoldPrefix(joined, combo);
			}

			if (TryBuildControlsGamePadGlyphJoinedLabel(combo, modeName, padFamily, out string glyphJoined))
				return ApplyControlsHoldPrefix(glyphJoined, combo);

			return BindingDisplayResolver.FormatControlsDisplayLabel(combo);
		}

		private static string ApplyControlsHoldPrefix(string joined, ComboBinding combo)
		{
			string whileHeld = Language.GetTextValue("Mods.AdvancedKeybinds.UI.Combo.HoldPreviewWhileHeld");
			string onRelease = Language.GetTextValue("Mods.AdvancedKeybinds.UI.Combo.HoldPreviewOnRelease");
			string withHold = BindingDisplayText.ApplyHoldPrefix(
				joined,
				combo.Hold,
				combo.HoldOnRelease,
				combo.FilledSlotCount,
				whileHeld,
				onRelease);
			return DoubleTapControlsDisplay.ApplyPrefix(withHold, combo);
		}

		/// <summary>
		/// Builds overlay text for GamePad Controls: glyph tags when Buttons names exist; aliases/text otherwise.
		/// Returns false when nothing benefited from glyph resolution (caller uses plain FormatComboDisplayLabel).
		/// </summary>
		private static bool TryBuildControlsGamePadGlyphJoinedLabel(
			ComboBinding combo,
			string modeName,
			GamePadControllerFamily family,
			out string joined)
		{
			joined = null;
			if (combo == null || combo.IsEmpty)
				return false;

			bool anyResolved = false;
			var parts = new List<string>(4);

			if (combo.HasAlternateGamePad && combo.FilledSlotCount == 1)
			{
				AppendToken(combo.AlternateGamePadToken, null);
				AppendToken(combo.Keys[0], combo.GetSlotDisplay(1));
				if (!anyResolved || parts.Count == 0)
					return false;
				joined = string.Join(AlternateGamePadControlsGlyphPolicy.VanillaGenInputSeparator, parts);
				return true;
			}

			AppendToken(combo.Slot1, combo.Slot1Display);
			AppendToken(combo.Slot2, combo.Slot2Display);
			AppendToken(combo.Slot3, combo.Slot3Display);
			if (!anyResolved || parts.Count == 0)
				return false;
			joined = string.Join("+", parts);
			return true;

			void AppendToken(string token, string slotDisplayOverride)
			{
				if (string.IsNullOrEmpty(token))
					return;
				string raw = BindingDisplayText.UnwrapOwnershipMarkerForDisplay(token);
				if (!GamePadToken.IsPadToken(raw))
				{
					parts.Add(KeyToken.FormatDisplayForToken(token, slotDisplayOverride));
					return;
				}

				string alias = TryGetUserDisplayAliasOnly(raw);
				if (string.IsNullOrEmpty(alias) && !string.IsNullOrWhiteSpace(slotDisplayOverride))
					alias = slotDisplayOverride.Trim();

				ControlsGamePadGlyphDisplayPolicy.DisplayPath path =
					ControlsGamePadGlyphDisplayPolicy.ResolveDisplay(
						modeName, raw, alias, family, out string value);
				if (path == ControlsGamePadGlyphDisplayPolicy.DisplayPath.GlyphButtonsName)
				{
					anyResolved = true;
					parts.Add(GlyphTagHandler.GenerateTag(value));
				}
				else if (path == ControlsGamePadGlyphDisplayPolicy.DisplayPath.FallbackText
					|| path == ControlsGamePadGlyphDisplayPolicy.DisplayPath.UserAlias)
				{
					anyResolved = true;
					parts.Add(value);
				}
				else
					parts.Add(value);
			}
		}

		/// <summary>
		/// Reads the row's private InputMode so gamepad Y offset matches vanilla DrawSelf.
		/// </summary>
		private static bool TryGetRowInputMode(UIElement self, out InputMode mode)
		{
			mode = InputMode.Keyboard;
			if (self == null)
				return false;

			Type type = self.GetType();
			if (_inputModeFieldMissingTypes.Contains(type))
				return false;

			if (!_inputModeFieldCache.TryGetValue(type, out FieldInfo field))
			{
				field = type.GetField(
					"_inputmode",
					BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
				if (field == null || field.FieldType != typeof(InputMode))
				{
					_inputModeFieldMissingTypes.Add(type);
					return false;
				}

				_inputModeFieldCache[type] = field;
			}

			object value = field.GetValue(self);
			if (value is not InputMode inputMode)
				return false;

			mode = inputMode;
			return true;
		}

		private static string GetRowLabel(UIElement self, string actionId)
		{
			if (self == null)
				return actionId;

			try
			{
				foreach (UIElement child in self.Children)
				{
					if (child.GetType().Name.Contains("UIText"))
					{
						PropertyInfo textProp = child.GetType().GetProperty("Text", BindingFlags.Instance | BindingFlags.Public);
						if (textProp?.GetValue(child) is string text && !string.IsNullOrEmpty(text))
							return text;
					}
				}
			}
			catch
			{
			}

			return actionId;
		}

		/// <summary>
		/// Reads the row's own background/tint color so the combo panel can echo the category color
		/// (vanilla rows are greenish, hotbar purple, map gray, mod rows yellowish, etc.).
		/// </summary>
		private static Color GetRowAccent(object self)
		{
			if (self == null)
				return DefaultAccent;

			Type type = self.GetType();
			const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

			for (Type t = type; t != null; t = t.BaseType)
			{
				foreach (FieldInfo fi in t.GetFields(flags | BindingFlags.DeclaredOnly))
				{
					if (fi.FieldType != typeof(Color))
						continue;
					if (fi.GetValue(self) is not Color c || c.R + c.G + c.B == 0)
						continue;

					if (_dumpedColorTypes.Add(type))
						if (AkDiag.Enabled) AkDiag.Log($"Row color field '{fi.Name}' = ({c.R},{c.G},{c.B},{c.A}) for '{type.Name}'.");
					return c;
				}
			}

			return DefaultAccent;
		}

		/// <summary>Registers the action id on the keybinding row created inside a CreatePanel row.</summary>
		private static void RegisterRowActionId(UIElement panel, string bind)
		{
			RegisterRowActionIdRecursive(panel, bind);
		}

		private static void RegisterRowActionIdRecursive(UIElement element, string bind)
		{
			if (element == null || string.IsNullOrEmpty(bind))
				return;

			if (element is UIKeybindingListItem)
				RememberRowActionId(element, bind);

			foreach (UIElement child in element.Children)
				RegisterRowActionIdRecursive(child, bind);
		}

		private static void RememberRowActionId(object row, string bind)
		{
			if (row == null || string.IsNullOrEmpty(bind))
				return;

			if (_rowActionIds.TryGetValue(row, out _))
				_rowActionIds.Remove(row);

			_rowActionIds.Add(row, bind);
		}

		/// <summary>
		/// Resolves the input-action name (e.g. "QuickHeal" or "ModName/MyBind") stored on a keybinding row.
		/// Priority:
		///   1. Row instance registered when UIManageControls.CreatePanel built the row.
		///   2. Read the known private string field by name (_keybind / _bind / ...), only when
		///      <see cref="KeybindRowReflectionSafety.CanReadInstanceField"/> passes.
		///   3. Scan string fields/properties that look like vanilla or mod action ids (same guard).
		/// </summary>
		private static string ResolveActionId(object self, Type itemType, bool forClick)
		{
			if (self == null)
				return null;

			if (_rowActionIds.TryGetValue(self, out string remembered) && !string.IsNullOrEmpty(remembered))
				return remembered;

			Type type = self.GetType();
			const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

			// Headers / SimpleListItem / unknown rows never expose UIKeybindingListItem._keybind.
			if (!IsComboPanelEligibleKeybindRow(self))
				return null;

			if (!forClick && _bindReflectionFailedTypes.Contains(type))
				return null;

			// Fast path: reuse the field we resolved earlier for this row type.
			if (_bindFieldCache.TryGetValue(type, out FieldInfo cached)
				&& KeybindRowReflectionSafety.TryReadStringField(cached, self, out string cachedValue))
			{
				return cachedValue;
			}

			// Drop a stale cache entry that pointed at an incompatible FieldInfo.
			if (cached != null && !KeybindRowReflectionSafety.CanReadInstanceField(cached, self))
				_bindFieldCache.Remove(type);

			// 1) Try the well-known field names directly, walking the inheritance chain.
			foreach (string name in _bindFieldNames)
			{
				for (Type t = type; t != null; t = t.BaseType)
				{
					FieldInfo fi = t.GetField(name, flags | BindingFlags.DeclaredOnly);
					if (fi == null || fi.FieldType != typeof(string))
						continue;
					if (!KeybindRowReflectionSafety.TryReadStringField(fi, self, out string value))
						continue;

					if (AkDiag.Enabled) AkDiag.Log($"Resolved keybind field '{fi.Name}' = '{value}' for '{type.Name}'.");
					_bindFieldCache[type] = fi;
					RememberRowActionId(self, value);
					return value;
				}
			}

			// 2) Fallback: accept string fields that look like action ids (vanilla or ModName/BindName).
			for (Type t = type; t != null; t = t.BaseType)
			{
				foreach (FieldInfo fi in t.GetFields(flags | BindingFlags.DeclaredOnly))
				{
					if (fi.FieldType != typeof(string))
						continue;
					if (!KeybindRowReflectionSafety.TryReadStringField(fi, self, out string value))
						continue;
					if (!LooksLikeActionId(value))
						continue;

					if (AkDiag.Enabled) AkDiag.Log($"Resolved keybind field (scan) '{fi.Name}' = '{value}' for '{type.Name}'.");
					_bindFieldCache[type] = fi;
					RememberRowActionId(self, value);
					return value;
				}
			}

			// 3) Same scan for properties (some tML builds may expose the bind as a property).
			for (Type t = type; t != null; t = t.BaseType)
			{
				foreach (PropertyInfo pi in t.GetProperties(flags | BindingFlags.DeclaredOnly))
				{
					if (pi.PropertyType != typeof(string) || !pi.CanRead)
						continue;

					string value;
					try { value = pi.GetValue(self) as string; }
					catch { continue; }

					if (string.IsNullOrEmpty(value) || !LooksLikeActionId(value))
						continue;

					if (AkDiag.Enabled) AkDiag.Log($"Resolved keybind property '{pi.Name}' = '{value}' for '{type.Name}'.");
					RememberRowActionId(self, value);
					return value;
				}
			}

			// Couldn't validate any field. Dump the row's string fields once so we can diagnose if needed.
			if (forClick || _bindReflectionFailedTypes.Add(type))
				DumpStringFieldsOnce(self, type, flags);

			return null;
		}

		private static bool LooksLikeActionId(string value)
		{
			if (string.IsNullOrEmpty(value))
				return false;

			if (IsKnownAction(value))
				return true;

			// Mod keybind ids use "ModInternalName/BindName".
			int slash = value.IndexOf('/');
			if (slash > 0 && slash < value.Length - 1)
				return !value.Contains('\\') && !value.Contains("://");

			// Vanilla action ids are short identifiers without spaces (QuickHeal, MouseLeft, ...).
			if (value.Contains(' ') || value.StartsWith('$'))
				return false;

			foreach (char c in value)
			{
				if (!char.IsLetterOrDigit(c) && c != '_')
					return false;
			}

			return true;
		}

		/// <summary>Returns true if the name is a real action registered in the current input profile.</summary>
		private static bool IsKnownAction(string name)
		{
			if (string.IsNullOrEmpty(name))
				return false;

			PlayerInputProfile profile = PlayerInput.CurrentProfile;
			if (profile?.InputModes == null)
				return false;

			foreach (KeyValuePair<InputMode, KeyConfiguration> pair in profile.InputModes)
			{
				if (pair.Value?.KeyStatus != null && pair.Value.KeyStatus.ContainsKey(name))
					return true;
			}

			return false;
		}

		/// <summary>
		/// Last-resort click-path resolver: let vanilla's own OnClick run (which stores the trigger it wants
		/// to rebind in PlayerInput.ListeningTrigger), grab that value, then immediately cancel the vanilla
		/// listen so no input-lock / stuck "waiting for key" state can occur.
		/// </summary>
		private static string ProbeTriggerViaVanilla(object self, UIMouseEvent evt, UIElement listeningElement, orig_OnClick orig)
		{
			ResolveRebindFields();

			try
			{
				orig(self, evt, listeningElement);
			}
			catch (Exception ex)
			{
				if (AkDiag.Enabled) AkDiag.Log("ProbeTriggerViaVanilla: orig threw: " + ex.Message);
			}

			string trigger = ReadListeningTrigger();

			// Cancel vanilla's rebind session no matter what, so the game never gets stuck listening.
			ClearVanillaRebindSession();
			PlayerInput.WritingText = false;

			return trigger;
		}

		private static string ReadListeningTrigger()
		{
			ResolveRebindFields();

			if (_listeningTriggerField != null)
			{
				try { return _listeningTriggerField.GetValue(null) as string; }
				catch { }
			}

			if (_listeningTriggerProperty != null)
			{
				try { return _listeningTriggerProperty.GetValue(null) as string; }
				catch { }
			}

			return null;
		}

		private static void ClearVanillaRebindSession()
		{
			ResolveRebindFields();

			try { _listeningTriggerField?.SetValue(null, string.Empty); } catch { }
			try { _listeningTriggerProperty?.SetValue(null, string.Empty); } catch { }
			try { _currentlyRebindingField?.SetValue(null, false); } catch { }
			try { _currentlyRebindingProperty?.SetValue(null, false); } catch { }
		}

		private static void ResolveRebindFields()
		{
			if (_rebindFieldsResolved)
				return;

			_rebindFieldsResolved = true;
			const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
			Type playerInput = typeof(PlayerInput);
			_listeningTriggerField = playerInput.GetField("ListeningTrigger", flags);
			_listeningTriggerProperty = playerInput.GetProperty("ListeningTrigger", flags);
			_currentlyRebindingField = playerInput.GetField("CurrentlyRebinding", flags);
			_currentlyRebindingProperty = playerInput.GetProperty("CurrentlyRebinding", flags);
			if (AkDiag.Enabled)
			{
				AkDiag.Log(
				$"Rebind fields resolved: ListeningTriggerField={_listeningTriggerField != null}, ListeningTriggerProperty={_listeningTriggerProperty != null}, CurrentlyRebindingField={_currentlyRebindingField != null}, CurrentlyRebindingProperty={_currentlyRebindingProperty != null}");
			}
		}

		private static void DumpStringFieldsOnce(object self, Type type, BindingFlags flags)
		{
			if (!_dumpedTypes.Add(type))
				return;

			for (Type t = type; t != null; t = t.BaseType)
			{
				foreach (FieldInfo fi in t.GetFields(flags | BindingFlags.DeclaredOnly))
				{
					object value;
					try { value = fi.GetValue(self); }
					catch { value = "<err>"; }
					if (AkDiag.Enabled) AkDiag.Log($"Field dump '{type.Name}': {fi.FieldType.Name} {fi.Name} = '{value}'");
				}
			}
		}
	}
}
