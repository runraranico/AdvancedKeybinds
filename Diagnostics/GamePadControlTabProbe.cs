// TEMPORARY investigation-only. DELETE before public release (with GamePadProbeSystem).
using System;
using System.Collections.Generic;
using System.Reflection;
using AdvancedKeybinds.Binding;
using AdvancedKeybinds.Core;
using AdvancedKeybinds.Patches;
using Terraria.GameInput;
using Terraria.ModLoader;
using Terraria.UI;

namespace AdvancedKeybinds.Diagnostics
{
	/// <summary>
	/// TEMPORARY: observes UIManageControls controller-tab / vanilla ListenFor gamepad rebind
	/// without changing save paths. Feeds <see cref="GamePadProbeSystem"/> LIVE lines.
	/// </summary>
	internal static class GamePadControlTabProbe
	{
		private delegate void orig_MouseClick(object self, UIMouseEvent evt, UIElement listeningElement);

		private static bool _hooksApplied;
		private static bool _onKeyboardTab = true;
		private static bool _onGameplay = true;
		private static bool _wasRebinding;
		private static string _lastListeningTrigger;
		private static InputMode _lastListeningMode;
		private static readonly List<string> _pressScratch = new(8);

		public static bool IsControllerTabVisible => !_onKeyboardTab;

		public static InputMode ExpectedListInputMode
		{
			get
			{
				if (_onKeyboardTab)
					return _onGameplay ? InputMode.Keyboard : InputMode.KeyboardUI;
				return _onGameplay ? InputMode.XBoxGamepad : InputMode.XBoxGamepadUI;
			}
		}

		public static void Apply()
		{
			if (_hooksApplied)
				return;
			_hooksApplied = true;

			try
			{
				Type manageType = typeof(UIState).Assembly.GetType("Terraria.GameContent.UI.States.UIManageControls");
				if (manageType == null)
				{
					AdvancedKeybinds.Instance?.Logger?.Warn(
						"[GamePadControlTabProbe TEMP] UIManageControls type not found.");
					return;
				}

				HookNamedClick(manageType, "GamepadButtonClick", onGamepad: true);
				HookNamedClick(manageType, "KeyboardButtonClick", onGamepad: false);
				HookNamedClick(manageType, "VsGameplayButtonClick", gameplay: true);
				HookNamedClick(manageType, "VsMenuButtonClick", gameplay: false);
			}
			catch (Exception ex)
			{
				AdvancedKeybinds.Instance?.Logger?.Warn(
					"[GamePadControlTabProbe TEMP] Apply failed: " + ex.GetType().Name + ": " + ex.Message);
			}
		}

		private static void HookNamedClick(Type manageType, string methodName, bool? onGamepad = null, bool? gameplay = null)
		{
			MethodInfo mi = manageType.GetMethod(
				methodName,
				BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
				null,
				new[] { typeof(UIMouseEvent), typeof(UIElement) },
				null);
			if (mi == null)
			{
				AdvancedKeybinds.Instance?.Logger?.Warn(
					"[GamePadControlTabProbe TEMP] missing method " + methodName);
				return;
			}

			ModDetours.Apply(mi, (orig_MouseClick orig, object self, UIMouseEvent evt, UIElement listeningElement) => {
				orig(self, evt, listeningElement);

				if (onGamepad.HasValue)
				{
					_onKeyboardTab = !onGamepad.Value;
					GamePadProbeSystem.LogPadTabOpen(ExpectedListInputMode);
				}

				if (gameplay.HasValue)
				{
					_onGameplay = gameplay.Value;
					if (!_onKeyboardTab)
						GamePadProbeSystem.LogPadTabOpen(ExpectedListInputMode);
				}
			});
		}

		/// <summary>
		/// Call after PlayerInput.orig when observing vanilla gamepad ListenFor.
		/// State-change + press-edge only.
		/// </summary>
		public static void ObserveAfterVanillaInputRebuild()
		{
			try
			{
				bool rebinding = PlayerInput.CurrentlyRebinding;
				string trigger = PlayerInput.ListeningTrigger;
				InputMode mode = ReadListeningInputMode();

				bool gamepadListen = rebinding
					&& (mode == InputMode.XBoxGamepad || mode == InputMode.XBoxGamepadUI);

				if (gamepadListen && !_wasRebinding)
				{
					_lastListeningTrigger = trigger;
					_lastListeningMode = mode;
					GamePadProbeSystem.LogPadBindWaitBegin(trigger, mode, "PlayerInput.ListenFor");
				}
				else if (!gamepadListen && _wasRebinding
					&& (_lastListeningMode == InputMode.XBoxGamepad || _lastListeningMode == InputMode.XBoxGamepadUI))
				{
					GamePadProbeSystem.LogPadBindWaitEnd(_lastListeningTrigger, _lastListeningMode);
					_lastListeningTrigger = null;
				}

				_wasRebinding = gamepadListen;
				if (gamepadListen)
				{
					_lastListeningTrigger = trigger;
					_lastListeningMode = mode;

					_pressScratch.Clear();
					FnaGamePadProvider.CopyJustPressed(_pressScratch);
					for (int i = 0; i < _pressScratch.Count; i++)
					{
						string pad = _pressScratch[i];
						GamePadVanillaBindingKey.TryPadTokenToVanillaKey(pad, out string vanilla);
						GamePadProbeSystem.LogPadBindAttempt(
							trigger,
							pad,
							vanilla ?? "?",
							mode);
						// Observation only — do not write KeyStatus here.
						GamePadProbeSystem.LogPadBindResult(
							trigger,
							accepted: null,
							storedMode: mode,
							note: "observe-only(no-write)");
					}
				}
			}
			catch
			{
				// Never break input.
			}
		}

		/// <summary>
		/// When AK opens ComboPanel from a controller-tab row (current AK behavior).
		/// </summary>
		public static void NoteComboPanelOpenedFromRow(string actionId, InputMode rowMode)
		{
			if (rowMode != InputMode.XBoxGamepad && rowMode != InputMode.XBoxGamepadUI)
				return;

			GamePadProbeSystem.LogPadBindWaitBegin(
				actionId ?? "?",
				rowMode,
				"KeybindingUiPatches.ComboPanel.Open");
			GamePadProbeSystem.LogPadBindResult(
				actionId ?? "?",
				accepted: null,
				storedMode: rowMode,
				note: "diverted-to-AK-ComboPanel(not-vanilla-KeyStatus)");
		}

		public static void NoteComboPanelCapture(string actionId, string padToken, bool accepted)
		{
			if (!IsControllerTabVisible)
				return;

			GamePadVanillaBindingKey.TryPadTokenToVanillaKey(padToken, out string vanilla);
			GamePadProbeSystem.LogPadBindAttempt(
				actionId ?? "?",
				padToken,
				vanilla ?? "?",
				ExpectedListInputMode);
			GamePadProbeSystem.LogPadBindResult(
				actionId ?? "?",
				accepted,
				storedMode: ExpectedListInputMode,
				note: accepted
					? "stored-in-AK-BindingStorage(Pad-token)"
					: "rejected-AK-ComboPanel");
		}

		private static FieldInfo _listeningInputModeField;

		private static InputMode ReadListeningInputMode()
		{
			try
			{
				if (_listeningInputModeField == null)
				{
					_listeningInputModeField = typeof(PlayerInput).GetField(
						"_listeningInputMode",
						BindingFlags.Static | BindingFlags.NonPublic);
				}

				if (_listeningInputModeField?.GetValue(null) is InputMode mode)
					return mode;
			}
			catch
			{
				// ignore
			}

			return InputMode.Keyboard;
		}
	}
}
