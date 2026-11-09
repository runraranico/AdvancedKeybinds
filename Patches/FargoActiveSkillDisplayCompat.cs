using System;
using System.Reflection;
using AdvancedKeybinds.Binding;
using AdvancedKeybinds.Core;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.GameContent;
using Terraria.GameInput;
using Terraria.Localization;
using Terraria.ModLoader;
using Terraria.UI;

namespace AdvancedKeybinds.Patches
{
	/// <summary>
	/// Soft dependency: FargowiltasSouls Active Skills key labels (display only).
	/// Replaces Fargo PreDraw key text with Controls-equivalent labels from BindingStorage.
	/// </summary>
	internal static class FargoActiveSkillDisplayCompat
	{
		private const string FargoModName = "FargowiltasSouls";
		private const string EquippedSkillBoxTypeName = "FargowiltasSouls.Content.UI.Elements.EquippedSkillBox";
		private const string FargoModTypeName = "FargowiltasSouls.FargowiltasSouls";

		/// <summary>Matches Fargo PreDraw IL: GetAssignedKeys((InputMode)0) = Keyboard.</summary>
		private static readonly InputMode FargoInputMode = InputMode.Keyboard;

		// MonoMod instance detour: object self (same pattern as KeybindingUiPatches OnClick / CreatePanel).
		private delegate void orig_PreDraw(object self, SpriteBatch spriteBatch, Vector2 position);
		private delegate void orig_DrawSelf(object self, SpriteBatch spriteBatch);

		private static bool _applied;
		/// <summary>Fail-closed: after one draw exception, skip override for the rest of the session.</summary>
		private static bool _disabled;
		private static bool _loggedDisableOnce;
		private static Type _equippedBoxType;
		private static FieldInfo _slotField;
		private static PropertyInfo _activeSkillKeysProperty;
		private static FieldInfo[] _activeSkillKeyFields;

		public static void Apply()
		{
			if (_applied || _disabled)
				return;

			if (!ModLoader.HasMod(FargoModName))
				return;

			if (!ModLoader.TryGetMod(FargoModName, out Mod fargo) || fargo?.Code == null)
				return;

			try
			{
				Assembly asm = fargo.Code;
				if (asm.ManifestModule.ModuleVersionId != new Guid("535d0777-d3df-4436-ac3f-e1d1fd0ad83b"))
					return; // The cell geometry and original PreDraw were audited for this binary.

				_equippedBoxType = asm.GetType(EquippedSkillBoxTypeName);
				Type modType = asm.GetType(FargoModTypeName);
				if (_equippedBoxType == null || modType == null)
					return;

				MethodInfo preDraw = _equippedBoxType.GetMethod(
					"PreDraw",
					BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
					binder: null,
					types: new[] { typeof(SpriteBatch), typeof(Vector2) },
					modifiers: null);
				if (preDraw == null)
					return;
				MethodInfo drawSelf = _equippedBoxType.GetMethod("DrawSelf",
					BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(SpriteBatch) }, null);
				if (drawSelf?.ReturnType != typeof(void)) return;

				_slotField = _equippedBoxType.GetField("Slot", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
				if (_slotField == null)
					return;

				_activeSkillKeysProperty = modType.GetProperty(
					"ActiveSkillKeys",
					BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
				if (_activeSkillKeysProperty == null)
				{
					_activeSkillKeyFields = new[] {
						modType.GetField("ActiveSkill1Key", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic),
						modType.GetField("ActiveSkill2Key", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic),
						modType.GetField("ActiveSkill3Key", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic),
						modType.GetField("ActiveSkill4Key", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
					};
				}

				bool keysOk = _activeSkillKeysProperty != null
					|| (_activeSkillKeyFields != null
						&& _activeSkillKeyFields[0] != null
						&& _activeSkillKeyFields[1] != null
						&& _activeSkillKeyFields[2] != null);
				if (!keysOk)
					return;

				ModDetours.Apply(preDraw, new Action<orig_PreDraw, object, SpriteBatch, Vector2>(OnPreDraw));
				ModDetours.Apply(drawSelf, new Action<orig_DrawSelf, object, SpriteBatch>(OnDrawSelf));
				KeyLabelHoverSystem.AttachFargo(_equippedBoxType);
				_applied = true;
			}
			catch (Exception ex)
			{
				DisableCompat("apply-failed", ex);
			}
		}

		public static void ResetAppliedFlag()
		{
			_applied = false;
			_disabled = false;
			_loggedDisableOnce = false;
			_slotField = null;
			_activeSkillKeysProperty = null;
			_activeSkillKeyFields = null;
			_equippedBoxType = null;
		}

		private static void DisableCompat(string reason, Exception ex)
		{
			_disabled = true;
			_slotField = null;
			_activeSkillKeysProperty = null;
			_activeSkillKeyFields = null;
			_equippedBoxType = null;

			if (_loggedDisableOnce)
				return;
			_loggedDisableOnce = true;
			try
			{
				AdvancedKeybinds.Instance?.Logger.Warn(
					"Advanced Keybinds: Fargo Active Skills display compat disabled (" + reason + "): "
					+ (ex?.Message ?? "unknown"));
			}
			catch
			{
			}
		}

		private static ModKeybind TryGetKeybindForSlot(int slot)
		{
			if (_activeSkillKeysProperty != null)
			{
				object keysObj = _activeSkillKeysProperty.GetValue(null);
				if (keysObj is Array keyArray && slot >= 0 && slot < keyArray.Length)
					return keyArray.GetValue(slot) as ModKeybind;
				return null;
			}

			if (_activeSkillKeyFields == null || slot < 0 || slot >= _activeSkillKeyFields.Length)
				return null;
			FieldInfo field = _activeSkillKeyFields[slot];
			return field?.GetValue(null) as ModKeybind;
		}

		private static string Readable(string text)
			=> string.IsNullOrEmpty(text) || text.IndexOf("AKAdv:", StringComparison.OrdinalIgnoreCase) >= 0
				|| text.IndexOf("SC:", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("VK:", StringComparison.OrdinalIgnoreCase) >= 0 ? "?" : text.Replace('\r', ' ').Replace('\n', ' ');

		internal static Rectangle GetKeyHoverBounds(object self, Vector2 position)
		{
			float width = self is UIElement element ? element.GetDimensions().Width : 42f;
			float height = self is UIElement owner ? owner.GetDimensions().Height : 42f;
			return new Rectangle((int)position.X, (int)position.Y, Math.Max(0, (int)width), Math.Max(0, (int)height));
		}

		private static bool TryGetOwnedCombo(object self, out ComboBinding combo)
		{
			combo = null;
			if (_disabled || _slotField == null || _equippedBoxType == null || !_equippedBoxType.IsInstanceOfType(self)) return false;
			ModKeybind keybind = TryGetKeybindForSlot((int)_slotField.GetValue(self));
			if (keybind == null) return false;
			string actionId = ModKeybindReflection.GetActionId(keybind);
			if (string.IsNullOrEmpty(actionId) || !BindingDisplayText.NeedsOverrideFromAssignedKeys(keybind.GetAssignedKeys(FargoInputMode))) return false;
			combo = BindingStorage.Get(actionId);
			return combo != null && !combo.IsEmpty;
		}

		private static void OnPreDraw(orig_PreDraw orig, object self, SpriteBatch spriteBatch, Vector2 position)
		{
			try
			{
				if (spriteBatch != null && TryGetOwnedCombo(self, out ComboBinding combo))
				{
					// Register the readable hint before Fargo's effect tooltip; suppress only its raw AK label.
					KeyLabelHoverSystem.Queue(self, GetKeyHoverBounds(self, position), Language.GetTextValue("Mods.AdvancedKeybinds.UI.AssignedKeysHint", Readable(BindingDisplayResolver.FormatControlsDisplayLabel(combo))));
					return;
				}
			}
			catch (Exception ex) { DisableCompat("predraw-exception", ex); }
			orig(self, spriteBatch, position);
		}

		private static void OnDrawSelf(orig_DrawSelf orig, object self, SpriteBatch spriteBatch)
		{
			// Fargo draws skill icons AFTER PreDraw. AK text must be drawn after the original too.
			orig(self, spriteBatch);
			try
			{
				if (spriteBatch == null || !TryGetOwnedCombo(self, out ComboBinding combo)) return;
				Vector2 position = ((UIElement)self).GetDimensions().Position();
				Rectangle frame = GetKeyHoverBounds(self, position);
				var font = FontAssets.MouseText.Value;
				string display = Readable(BindingDisplayResolver.FormatComboDisplayLabel(combo));
				var fit = KeyLabelFitPolicy.Fit(display, Math.Max(0f, frame.Width - 2f), s => font.MeasureString(s).X);
				float drawWidth = font.MeasureString(fit.Text).X * fit.Scale;
				Utils.DrawBorderString(spriteBatch, fit.Text, position + new Vector2((frame.Width - drawWidth) / 2f, 32f), Color.White, fit.Scale);
			}
			catch (Exception ex) { DisableCompat("draw-exception", ex); }
		}
	}
}
