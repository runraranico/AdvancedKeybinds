using System;
using System.Collections.Generic;
using System.Reflection;
using AdvancedKeybinds.Binding;
using AdvancedKeybinds.Core;
using Terraria.GameInput;
using Terraria.ModLoader;

namespace AdvancedKeybinds.Patches
{
	/// <summary>
	/// Soft dependency: replaces Machine Translate's raw GetAssignedKeys display string only.
	/// Does not modify GetAssignedKeys, KeyStatus, ownership markers, or input processing.
	/// </summary>
	internal static class MachineTranslateDisplayCompat
	{
		private const string MachineTranslateModName = "MachineTranslate";
		private const string HelperTypeName = "MachineTranslate.Helper";

		private delegate string orig_GetKeybindAssigned(ModKeybind keybind);
		private delegate string hook_GetKeybindAssigned(orig_GetKeybindAssigned orig, ModKeybind keybind);

		private static bool _applied;
		private static bool _disabled;
		private static bool _loggedDisableOnce;

		public static void Apply()
		{
			if (_applied || _disabled || !ModLoader.HasMod(MachineTranslateModName))
				return;

			if (!ModLoader.TryGetMod(MachineTranslateModName, out Mod machineTranslate)
				|| machineTranslate?.Code == null)
				return;

			try
			{
				Type helperType = machineTranslate.Code.GetType(HelperTypeName);
				MethodInfo getKeybindAssigned = helperType?.GetMethod(
					"GetKeybindAssigned",
					BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
					binder: null,
					types: new[] { typeof(ModKeybind) },
					modifiers: null);
				if (getKeybindAssigned == null || getKeybindAssigned.ReturnType != typeof(string))
					return;

				ModDetours.Apply(
					getKeybindAssigned,
					new hook_GetKeybindAssigned(OnGetKeybindAssigned));
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
		}

		private static string OnGetKeybindAssigned(
			orig_GetKeybindAssigned orig,
			ModKeybind keybind)
		{
			if (_disabled || keybind == null)
				return orig(keybind);

			try
			{
				List<string> assigned = keybind.GetAssignedKeys(InputMode.Keyboard);
				if (!BindingDisplayText.NeedsOverrideFromAssignedKeys(assigned))
					return orig(keybind);

				string actionId = ModKeybindReflection.GetActionId(keybind);
				if (BindingDisplayResolver.TryGetBindingDisplayText(actionId, assigned, out string bindingDisplay)
					&& !string.IsNullOrEmpty(bindingDisplay))
				{
					return bindingDisplay;
				}

				string joined = BindingDisplayText.JoinAssignedKeyDisplays(
					assigned,
					BindingDisplayResolver.ResolveDisplayName);
				return !string.IsNullOrEmpty(joined) ? joined : orig(keybind);
			}
			catch (Exception ex)
			{
				DisableCompat("display-failed", ex);
				return orig(keybind);
			}
		}

		private static void DisableCompat(string reason, Exception ex)
		{
			_disabled = true;
			if (_loggedDisableOnce)
				return;

			_loggedDisableOnce = true;
			try
			{
				AdvancedKeybinds.Instance?.Logger.Warn(
					"Advanced Keybinds: Machine Translate display compat disabled ("
					+ reason + "): " + (ex?.Message ?? "unknown"));
			}
			catch
			{
			}
		}
	}
}
