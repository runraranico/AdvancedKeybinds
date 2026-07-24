using System;
using System.Collections.Generic;
using System.Reflection;
using AdvancedKeybinds.Binding;
using AdvancedKeybinds.Core;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.GameInput;
using Terraria.ModLoader;

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

		private static void OnPreDraw(orig_PreDraw orig, object self, SpriteBatch spriteBatch, Vector2 position)
		{
			if (_disabled || self == null || spriteBatch == null || _slotField == null)
			{
				orig(self, spriteBatch, position);
				return;
			}

			try
			{
				int slot = (int)_slotField.GetValue(self);
				ModKeybind keybind = TryGetKeybindForSlot(slot);
				if (keybind == null)
				{
					orig(self, spriteBatch, position);
					return;
				}

				string actionId = ModKeybindReflection.GetActionId(keybind);

				List<string> assigned;
				try
				{
					assigned = keybind.GetAssignedKeys(FargoInputMode);
				}
				catch
				{
					orig(self, spriteBatch, position);
					return;
				}

				ComboBinding combo = string.IsNullOrEmpty(actionId) ? null : BindingStorage.Get(actionId);
				bool storageHit = combo != null && !combo.IsEmpty;

				if (!BindingDisplayText.NeedsOverrideFromAssignedKeys(assigned))
				{
					orig(self, spriteBatch, position);
					return;
				}

				if (!storageHit)
				{
					orig(self, spriteBatch, position);
					return;
				}

				string display = BindingDisplayResolver.FormatComboDisplayLabel(combo);
				if (string.IsNullOrEmpty(display))
				{
					orig(self, spriteBatch, position);
					return;
				}

				// Suppress Fargo AKAdv DrawBorderString; draw Controls-equivalent text only.
				Utils.DrawBorderString(
					spriteBatch,
					display,
					position + new Vector2(-3f, 32f),
					Color.White);
			}
			catch (Exception ex)
			{
				DisableCompat("predraw-exception", ex);
				orig(self, spriteBatch, position);
			}
		}
	}
}
