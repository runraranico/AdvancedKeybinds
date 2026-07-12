using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using AdvancedKeybinds.Binding;
using AdvancedKeybinds.UI;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.Audio;
using Terraria.GameContent;
using Terraria.GameContent.UI.Elements;
using Terraria.GameInput;
using Terraria.ID;
using Terraria.Localization;
using Terraria.ModLoader;
using Terraria.UI;

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
		private delegate void orig_OnClick(object self, UIMouseEvent evt, UIElement listeningElement);
		private delegate UIElement orig_CreatePanel(object self, string bind, InputMode mode, Color color);

		private static readonly Dictionary<Type, FieldInfo> _bindFieldCache = new();
		private static readonly HashSet<Type> _bindReflectionFailedTypes = new();
		private static readonly ConditionalWeakTable<object, string> _rowActionIds = new();
		private static readonly HashSet<Type> _dumpedTypes = new();
		private static readonly HashSet<Type> _dumpedColorTypes = new();

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

		private const float KeyTextScale = 0.8f;
		private const float KeySlotWidthRatio = 0.36f;
		private static readonly Color AdvancedKeyTextColor = new(210, 190, 255);

		// Physical mouse edges for row clear / reset: sampled once per tick, consumed by one row.
		private static bool _physicalRightClearJustPressed;
		private static bool _prevPhysicalRightForRowClear;
		private static bool _physicalMiddleResetJustPressed;
		private static bool _prevPhysicalMiddleForRowReset;

		public static void Apply()
		{
			Type listItemType = typeof(UIKeybindingListItem);
			Type toggleItemType = typeof(UIKeybindingToggleListItem);

			HookRowConstructor(listItemType);
			HookRowConstructor(toggleItemType);
			HookListItem(listItemType);
			HookListItem(toggleItemType);
			HookCreatePanel();
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
					HookRowRightClickClear(row, bind);
					HookRowMiddleClickReset(row, bind);
				}
			});
		}

		/// <summary>
		/// Clear a keybind row to 未設定 on physical right mouse button press (hardware Mouse2).
		/// Edge is sampled once per input tick and consumed on use so moving the cursor across rows
		/// cannot clear more than one binding per press. Ignored while the left button is held
		/// (scroll/drag). Does not use UI OnRightClick / 対話・動作.
		/// </summary>
		private static void HookRowRightClickClear(UIElement row, string bind)
		{
			row.OnUpdate += _ => {
				if (ComboPanel.IsOpen || !row.IsMouseHovering)
					return;

				// Left-drag / scrollbar drag must never clear rows.
				if (MouseInputHelper.IsPhysicalLeftDown())
					return;

				if (!TryConsumePhysicalRightJustPressedForRowClear())
					return;

				ClearRowToUnset(bind);
			};
		}

		/// <summary>
		/// Restore a single row to its profile default on physical middle mouse button (hardware wheel click).
		/// Uses the device button, not the Middle Mouse action — remapping that action must not break this.
		/// </summary>
		private static void HookRowMiddleClickReset(UIElement row, string bind)
		{
			row.OnUpdate += _ => {
				if (ComboPanel.IsOpen || !row.IsMouseHovering)
					return;

				if (MouseInputHelper.IsPhysicalLeftDown() || MouseInputHelper.IsPhysicalRightDown())
					return;

				if (!TryConsumePhysicalMiddleJustPressedForRowReset())
					return;

				ResetRowToDefault(bind);
			};
		}

		/// <summary>
		/// Call once per input tick (before UI row updates) so press edges are stable.
		/// </summary>
		internal static void SyncPhysicalRowMouseEdges()
		{
			bool rightDown = MouseInputHelper.IsPhysicalRightDown();
			_physicalRightClearJustPressed = rightDown && !_prevPhysicalRightForRowClear;
			_prevPhysicalRightForRowClear = rightDown;

			bool middleDown = MouseInputHelper.IsPhysicalMiddleDown();
			_physicalMiddleResetJustPressed = middleDown && !_prevPhysicalMiddleForRowReset;
			_prevPhysicalMiddleForRowReset = middleDown;
		}

		/// <summary>Obsolete name kept for call-site clarity during transition; prefer <see cref="SyncPhysicalRowMouseEdges"/>.</summary>
		internal static void SyncPhysicalRightClearEdge() => SyncPhysicalRowMouseEdges();

		private static bool TryConsumePhysicalRightJustPressedForRowClear()
		{
			if (!_physicalRightClearJustPressed)
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

		private static void ClearRowToUnset(string actionId)
		{
			if (string.IsNullOrEmpty(actionId))
				return;

			bool hadAdvanced = BindingStorage.HasAdvancedBindings(actionId);
			bool hadVanilla = !string.IsNullOrEmpty(VanillaRebindHelper.GetAssignedKey(actionId));
			if (!hadAdvanced && !hadVanilla)
				return;

			if (OpenKeybindMenuDefaults.IsOpenKeybindMenuAction(actionId))
				OpenKeybindMenuDefaults.MarkCustomized();

			BindingStorage.ClearBindings(actionId);
			VanillaRebindHelper.ClearVanillaBinding(actionId);
			SoundEngine.PlaySound(SoundID.MenuClose);
			AkDiag.Log($"Row right-click cleared binding to unset for '{actionId}'.");
		}

		private static void ResetRowToDefault(string actionId)
		{
			if (string.IsNullOrEmpty(actionId))
				return;

			if (OpenKeybindMenuDefaults.IsOpenKeybindMenuAction(actionId))
				OpenKeybindMenuDefaults.MarkCustomized();

			BindingStorage.ClearBindings(actionId);

			if (OpenKeybindMenuDefaults.TryGetRegisteredDefaultKey(actionId, out string registeredDefault))
			{
				VanillaRebindHelper.AssignVanillaBinding(actionId, registeredDefault);
				AkDiag.Log($"Row middle-click restored default binding for '{actionId}' via open-menu default '{registeredDefault}'.");
			}
			else if (ModKeybindReflection.TryGetRegisteredDefaultBinding(actionId, out string modDefault))
			{
				VanillaRebindHelper.AssignVanillaBinding(actionId, modDefault);
				AkDiag.Log($"Row middle-click restored default binding for '{actionId}' via ModKeybind default '{modDefault}'.");
			}
			else
			{
				VanillaRebindHelper.ResetVanillaBindingToDefault(actionId);
				AkDiag.Log($"Row middle-click restored default binding for '{actionId}' via OriginalProfiles.");
			}

			SoundEngine.PlaySound(SoundID.MenuOpen);
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
					HookResetButton(panel, bind);
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
		private static void HookResetButton(UIElement panel, string bind)
		{
			panel.OnLeftClick += (evt, listeningElement) => {
				if (ComboPanel.IsOpen)
					return;
				if (IsWithinKeybindRow(evt.Target))
					return;

				// Full row reset (same as middle-click), not only clearing advanced storage.
				ResetRowToDefault(bind);
			};

			// Middle-click anywhere on the CreatePanel row (key slot or Reset label) restores default.
			panel.OnUpdate += _ => {
				if (ComboPanel.IsOpen || !panel.IsMouseHovering)
					return;

				if (MouseInputHelper.IsPhysicalLeftDown() || MouseInputHelper.IsPhysicalRightDown())
					return;

				if (!TryConsumePhysicalMiddleJustPressedForRowReset())
					return;

				ResetRowToDefault(bind);
			};
		}

		/// <summary>True if the clicked element is the keybinding row (or a child of it), not the Reset button.</summary>
		private static bool IsWithinKeybindRow(UIElement target)
		{
			for (UIElement e = target; e != null; e = e.Parent)
			{
				string name = e.GetType().Name;
				if (name.Contains("UIKeybindingListItem") || name.Contains("UIKeybindingToggleListItem"))
					return true;
			}

			return false;
		}

		private static void HookListItem(Type itemType)
		{
			if (itemType == null)
				return;

			MethodInfo onClick = itemType.GetMethod("OnClickMethod", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			if (onClick != null)
			{
				ModDetours.Apply(onClick, (orig_OnClick orig, object self, UIMouseEvent evt, UIElement listeningElement) => {
					AkDiag.Log($"Keybinding row click type='{itemType.Name}' panelOpen={ComboPanel.IsOpen}");

					// While the panel is open, swallow row clicks (the panel handles its own input).
					if (ComboPanel.IsOpen)
					{
						AkDiag.Log("Keybinding row click swallowed because combo panel is already open.");
						return;
					}

						string actionId = ResolveActionId(self, itemType, forClick: true);
						if (string.IsNullOrEmpty(actionId))
						{
							// Reflection couldn't find the id. Instead of falling back to a real vanilla
							// rebind (which causes the input-lock bug), let vanilla briefly set its
							// ListeningTrigger, read it, and cancel the listen immediately.
							AkDiag.Log($"actionId unresolved via reflection for type='{itemType.Name}'; probing via vanilla ListeningTrigger.");
							actionId = ProbeTriggerViaVanilla(self, evt, listeningElement, orig);

							if (string.IsNullOrEmpty(actionId))
							{
								AkDiag.Log("Probe failed: ListeningTrigger was empty. Ignoring click (panel not opened).");
								return;
							}

							AkDiag.Log($"Probe resolved action='{actionId}' via vanilla ListeningTrigger.");
						}

						// Open our combo panel instead of starting a vanilla listen. This is the key change
						// that eliminates the old input-lock / stuck-listen problems entirely.
						AkDiag.Log($"Opening combo panel for action='{actionId}'.");
						ComboPanel.Open(actionId, GetRowLabel(self as UIElement, actionId), GetRowAccent(self));
				});
			}

			MethodInfo drawSelf = itemType.GetMethod("DrawSelf", BindingFlags.Instance | BindingFlags.NonPublic);
			if (drawSelf == null)
				return;

			ModDetours.Apply(drawSelf, (orig_DrawSelf orig, UIElement self, SpriteBatch spriteBatch) => {
				orig(self, spriteBatch);
				DrawComboLabel(self, spriteBatch, itemType);
			});
		}

		private static void DrawComboLabel(UIElement self, SpriteBatch spriteBatch, Type itemType)
		{
			string actionId = ResolveActionId(self, itemType, forClick: false);

			if (string.IsNullOrEmpty(actionId))
				return;

			ComboBinding combo = BindingStorage.Get(actionId);
			// Plain single-key taps are stored only for combo-panel slot memory; vanilla draws the list label.
			if (combo == null || combo.IsPlainSingleTap)
				return;

			CalculatedStyle dims = self.GetDimensions();
			float slotWidth = dims.Width * KeySlotWidthRatio;

			string display = BuildComboLabel(combo);
			string tooltip = display;
			float textScale = KeyTextScale;
			display = FitTextToSlot(display, slotWidth - 8f, ref textScale);

			LayoutKeyLabel(dims, display, textScale, out Rectangle keySlotRect, out Vector2 textPosition);

			// Erase vanilla's key label under the slot using the row's own background tint (not a visible box).
			spriteBatch.Draw(TextureAssets.MagicPixel.Value, keySlotRect, GetRowBackgroundColor(self));
			Utils.DrawBorderString(spriteBatch, display, textPosition, AdvancedKeyTextColor, textScale);

			// Never surface a hover tooltip while the combo panel is open; it must sit fully in front and
			// nothing behind it should react to the moving cursor.
			if (self.IsMouseHovering && !ComboPanel.IsOpen && !string.IsNullOrEmpty(tooltip))
				Main.instance.MouseText(tooltip);
		}

		private static string BuildComboLabel(ComboBinding combo)
		{
			var keys = combo.Keys;
			string joined = KeyToken.FormatSet(keys, compact: true);
			bool isHold = combo.Hold && keys.Count == 1;
			if (!isHold)
				return joined;

			string prefix = combo.HoldOnRelease
				? Language.GetTextValue("Mods.AdvancedKeybinds.UI.Combo.HoldPreviewOnRelease")
				: Language.GetTextValue("Mods.AdvancedKeybinds.UI.Combo.HoldPreviewWhileHeld");
			return prefix + joined;
		}

		private static string FitTextToSlot(string text, float slotWidth, ref float scale)
		{
			if (MeasureText(text, scale) <= slotWidth)
				return text;

			for (float s = scale - 0.05f; s >= 0.55f; s -= 0.05f)
			{
				if (MeasureText(text, s) <= slotWidth)
				{
					scale = s;
					return text;
				}
			}

			scale = 0.55f;
			return text;
		}

		private static float MeasureText(string text, float scale)
			=> FontAssets.MouseText.Value.MeasureString(text).X * scale;

		private static void LayoutKeyLabel(CalculatedStyle dims, string display, float scale, out Rectangle keySlotRect, out Vector2 textPosition)
		{
			Vector2 textSize = FontAssets.MouseText.Value.MeasureString(display) * scale;

			const int padX = 4;
			const int padY = 2;
			int rightEdge = (int)(dims.X + dims.Width - 2f);
			int slotWidth = (int)(dims.Width * KeySlotWidthRatio);
			int left = rightEdge - slotWidth;
			int boxHeight = Math.Max((int)Math.Ceiling(textSize.Y) + padY * 2, (int)dims.Height - 4);
			int top = (int)(dims.Y + (dims.Height - boxHeight) / 2f);

			keySlotRect = new Rectangle(left, top, slotWidth, boxHeight);

			float textX = rightEdge - padX - textSize.X;
			float minX = left + padX;
			if (textX < minX)
				textX = minX;

			textPosition = new Vector2(textX, top + (boxHeight - textSize.Y) / 2f);
		}

		/// <summary>Matches UIKeybindingListItem hover brightening so the vanilla-key erase is invisible.</summary>
		private static Color GetRowBackgroundColor(UIElement self)
		{
			Color color = GetRowAccent(self);
			if (self.IsMouseHovering)
			{
				color.R = (byte)Math.Min(255, color.R + 40);
				color.G = (byte)Math.Min(255, color.G + 40);
				color.B = (byte)Math.Min(255, color.B + 40);
			}

			return color;
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
						AkDiag.Log($"Row color field '{fi.Name}' = ({c.R},{c.G},{c.B},{c.A}) for '{type.Name}'.");
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
		///   2. Read the known private string field by name (_keybind / _bind / ...).
		///   3. Scan string fields/properties that look like vanilla or mod action ids.
		/// </summary>
		private static string ResolveActionId(object self, Type itemType, bool forClick)
		{
			if (self == null)
				return null;

			if (_rowActionIds.TryGetValue(self, out string remembered) && !string.IsNullOrEmpty(remembered))
				return remembered;

			Type type = self.GetType();
			const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

			if (!forClick && _bindReflectionFailedTypes.Contains(type))
				return null;

			// Fast path: reuse the field we resolved earlier for this row type.
			if (_bindFieldCache.TryGetValue(type, out FieldInfo cached)
				&& cached != null
				&& cached.GetValue(self) is string cachedValue
				&& !string.IsNullOrEmpty(cachedValue))
			{
				return cachedValue;
			}

			// 1) Try the well-known field names directly, walking the inheritance chain.
			foreach (string name in _bindFieldNames)
			{
				for (Type t = type; t != null; t = t.BaseType)
				{
					FieldInfo fi = t.GetField(name, flags | BindingFlags.DeclaredOnly);
					if (fi == null || fi.FieldType != typeof(string))
						continue;
					if (fi.GetValue(self) is not string value || string.IsNullOrEmpty(value))
						continue;

					AkDiag.Log($"Resolved keybind field '{fi.Name}' = '{value}' for '{type.Name}'.");
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
					if (fi.GetValue(self) is not string value || string.IsNullOrEmpty(value))
						continue;
					if (!LooksLikeActionId(value))
						continue;

					AkDiag.Log($"Resolved keybind field (scan) '{fi.Name}' = '{value}' for '{type.Name}'.");
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

					AkDiag.Log($"Resolved keybind property '{pi.Name}' = '{value}' for '{type.Name}'.");
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
				AkDiag.Log("ProbeTriggerViaVanilla: orig threw: " + ex.Message);
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
			AkDiag.Log(
				$"Rebind fields resolved: ListeningTriggerField={_listeningTriggerField != null}, ListeningTriggerProperty={_listeningTriggerProperty != null}, CurrentlyRebindingField={_currentlyRebindingField != null}, CurrentlyRebindingProperty={_currentlyRebindingProperty != null}");
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
					AkDiag.Log($"Field dump '{type.Name}': {fi.FieldType.Name} {fi.Name} = '{value}'");
				}
			}
		}
	}
}
