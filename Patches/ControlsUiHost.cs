using System;
using System.Reflection;
using AdvancedKeybinds.Binding;
using AdvancedKeybinds.Core;
using AdvancedKeybinds.Diagnostics;
using AdvancedKeybinds.UI;
using Terraria;
using Terraria.GameInput;
using Terraria.UI;
using Terraria.UI.Gamepad;

namespace AdvancedKeybinds.Patches
{
	/// <summary>
	/// Shared host detection for UIManageControls on MenuUI and InGameUI / FancyUI.
	/// Also keeps in-world GamePad UI mode so UILinkPointNavigator stays InUse.
	/// </summary>
	internal static class ControlsUiHost
	{
		private static InputMode? _restoreInputMode;
		private static string _lastContextKey;
		private static bool _loggedContextThisOpen;
		private static bool _wasOpen;

		internal static void ClearRuntimeState()
		{
			RestoreInputModeIfNeeded();
			_lastContextKey = null;
			_loggedContextThisOpen = false;
			_wasOpen = false;
			PendingGamePadRowConfirm.ClearRuntimeState();
		}

		internal static bool TryGetManageControls(out UIState state, out string host)
		{
			state = null;
			host = null;

			if (Main.gameMenu)
			{
				UIState menu = Main.MenuUI?.CurrentState;
				if (IsManageControls(menu))
				{
					state = menu;
					host = "MenuUI";
					return true;
				}
			}

			UIState ingame = Main.InGameUI?.CurrentState;
			// In-world: require inFancyUI so a leftover ManageControls CurrentState after Close
			// does not keep "Controls open" and kill world ESC / Inventory.
			if (Main.inFancyUI && IsManageControls(ingame))
			{
				state = ingame;
				host = "InGameUI";
				return true;
			}

			try
			{
				if (Main.ManageControlsMenu != null
					&& Main.inFancyUI
					&& IsManageControls(Main.ManageControlsMenu))
				{
					state = Main.ManageControlsMenu;
					host = Main.gameMenu ? "MenuUI" : "InGameUI";
					return true;
				}
			}
			catch
			{
				// ignore
			}

			return false;
		}

		internal static bool IsManageControlsOpen()
			=> TryGetManageControls(out _, out _)
				|| KeybindUiWorldInputGuard.IsManageControlsUiOpen();

		internal static bool IsManageControls(UIState state)
		{
			if (state == null)
				return false;
			if (state.GetType().Name.IndexOf("UIManageControls", StringComparison.Ordinal) >= 0)
				return true;
			try
			{
				return Main.ManageControlsMenu != null && ReferenceEquals(state, Main.ManageControlsMenu);
			}
			catch
			{
				return false;
			}
		}

		/// <summary>
		/// While ManageControls is open: bump XBoxGamepad → XBoxGamepadUI for FancyUI nav;
		/// after keyboard/mouse entry, promote only on D-Pad / left-stick navigation intent
		/// (before vanilla UpdateInput so Menu*/movement triggers use the Gamepad UI profile).
		/// Restore the previous mode when Controls closes. Does not change keyboard/mouse capture.
		/// </summary>
		internal static void EnsureInWorldGamepadUiModeForControls()
		{
			bool open = IsManageControlsOpen();
			if (open && !_wasOpen)
			{
				_loggedContextThisOpen = false;
				LogContextOnce();
			}
			else if (!open && _wasOpen)
			{
				RestoreInputModeIfNeeded();
				_loggedContextThisOpen = false;
				PendingGamePadRowConfirm.ClearRuntimeState();
				ControlsGamepadAutoScrollPatches.ClearRuntimeState();
				ControlsPadBHoldCancelFocus.ClearRuntimeState();
			}

			_wasOpen = open;

			if (!open)
			{
				RestoreInputModeIfNeeded();
				return;
			}

			if (!FnaGamePadProvider.IsConnected)
				return;

			InputMode cur = PlayerInput.CurrentInputMode;
			SamplePadNavigation(out bool dpadEdge, out bool stickEdge, out _, out _);
			bool padIntent = ControlsGamepadUiModePolicy.HasPadNavigationIntent(dpadEdge, stickEdge);

			if (!ControlsGamepadUiModePolicy.ShouldPromoteToXboxGamepadUi(
				manageControlsOpen: true,
				gamePadConnected: true,
				currentIsXboxGamepadUi: cur == InputMode.XBoxGamepadUI,
				currentIsXboxGamepad: cur == InputMode.XBoxGamepad,
				padNavigationIntent: padIntent))
			{
				LogContextOnce();
				return;
			}

			ApplyXboxGamepadUiAndCursor(cur);
			LogContextOnce();
		}

		/// <summary>
		/// After vanilla UpdateInput: MouseInput may demote CursorMode/InputMode on residual mouse
		/// deltas. While D-Pad / left-stick remain active, re-assert Gamepad UI + CursorMode so the
		/// selection cursor stays visible, and mark UsedMovementKey so UILinkPointNavigator snaps
		/// Mouse to CurrentPoint on the same frame (FancyUI Menu* alone does not set that flag).
		/// Idle connected pad does not reassert (KB/mouse return).
		/// </summary>
		internal static void ReassertGamepadUiCursorAfterVanillaInput()
		{
			if (!IsManageControlsOpen() || !FnaGamePadProvider.IsConnected)
				return;

			SamplePadNavigation(out bool dpadEdge, out bool stickEdge, out bool dpadHeld, out bool stickHeld);
			bool padActive = ControlsGamepadUiModePolicy.HasPadNavigationActive(
				dpadEdge, stickEdge, dpadHeld, stickHeld);
			if (!ControlsGamepadUiModePolicy.ShouldReassertGamepadUiCursor(
				manageControlsOpen: true,
				gamePadConnected: true,
				padNavigationActive: padActive))
				return;

			ApplyXboxGamepadUiAndCursor(PlayerInput.CurrentInputMode);

			if (ControlsGamepadUiModePolicy.ShouldMarkUsedMovementKeyForSelectionFollow(
				manageControlsOpen: true,
				gamePadConnected: true,
				padNavigationActive: padActive))
			{
				try
				{
					if (PlayerInput.Triggers?.Current != null)
						PlayerInput.Triggers.Current.UsedMovementKey = true;
				}
				catch
				{
					// ignore
				}
			}
		}

		private static void SamplePadNavigation(
			out bool dpadEdge,
			out bool stickEdge,
			out bool dpadHeld,
			out bool stickHeld)
		{
			dpadEdge =
				FnaGamePadProvider.WasJustPressed(GamePadToken.DPadUp)
				|| FnaGamePadProvider.WasJustPressed(GamePadToken.DPadDown)
				|| FnaGamePadProvider.WasJustPressed(GamePadToken.DPadLeft)
				|| FnaGamePadProvider.WasJustPressed(GamePadToken.DPadRight);
			stickEdge =
				FnaGamePadProvider.WasJustPressed(GamePadToken.LeftStickUp)
				|| FnaGamePadProvider.WasJustPressed(GamePadToken.LeftStickDown)
				|| FnaGamePadProvider.WasJustPressed(GamePadToken.LeftStickLeft)
				|| FnaGamePadProvider.WasJustPressed(GamePadToken.LeftStickRight);
			dpadHeld =
				FnaGamePadProvider.IsDown(GamePadToken.DPadUp)
				|| FnaGamePadProvider.IsDown(GamePadToken.DPadDown)
				|| FnaGamePadProvider.IsDown(GamePadToken.DPadLeft)
				|| FnaGamePadProvider.IsDown(GamePadToken.DPadRight);
			stickHeld =
				FnaGamePadProvider.IsDown(GamePadToken.LeftStickUp)
				|| FnaGamePadProvider.IsDown(GamePadToken.LeftStickDown)
				|| FnaGamePadProvider.IsDown(GamePadToken.LeftStickLeft)
				|| FnaGamePadProvider.IsDown(GamePadToken.LeftStickRight);
		}

		private static void ApplyXboxGamepadUiAndCursor(InputMode previousOrCurrent)
		{
			if (!_restoreInputMode.HasValue)
				_restoreInputMode = previousOrCurrent;
			PlayerInput.CurrentInputMode = InputMode.XBoxGamepadUI;
			try
			{
				PlayerInput.SettingsForUI.SetCursorMode(CursorMode.Gamepad);
			}
			catch
			{
				// ignore
			}
		}

		internal static void LogDpadEdgeIfNeeded(string token, int beforePoint)
		{
			if (!IsManageControlsOpen())
				return;
			if (!TryGetManageControls(out _, out string host))
				host = Main.gameMenu ? "MenuUI" : "InGameUI";

			bool menuDown = false;
			bool menuUp = false;
			try
			{
				var jp = PlayerInput.Triggers?.JustPressed?.KeyStatus;
				if (jp != null)
				{
					jp.TryGetValue("MenuDown", out menuDown);
					jp.TryGetValue("MenuUp", out menuUp);
				}
			}
			catch
			{
				// ignore
			}

			int after = UILinkPointNavigator.CurrentPoint;
			bool relevant = string.Equals(token, GamePadToken.DPadDown, StringComparison.OrdinalIgnoreCase)
				|| string.Equals(token, GamePadToken.DPadUp, StringComparison.OrdinalIgnoreCase);
			if (!relevant)
				return;

			bool menu = string.Equals(token, GamePadToken.DPadDown, StringComparison.OrdinalIgnoreCase)
				? menuDown
				: menuUp;

			GamePadProbeSystem.LogControlUiDpad(host, token, menu, beforePoint, after);
		}

		private static void LogContextOnce()
		{
			if (_loggedContextThisOpen)
				return;
			if (!TryGetManageControls(out UIState state, out string host))
				return;

			_loggedContextThisOpen = true;
			int pointCount = 0;
			try { pointCount = UILinkPointNavigator.Points?.Count ?? 0; }
			catch { pointCount = 0; }

			string key = host
				+ "|" + (state?.GetType().Name ?? "-")
				+ "|" + PlayerInput.CurrentInputMode
				+ "|" + UILinkPointNavigator.CurrentPoint
				+ "|" + pointCount
				+ "|" + UILinkPointNavigator.Shortcuts.FANCYUI_HIGHEST_INDEX;
			if (string.Equals(key, _lastContextKey, StringComparison.Ordinal))
				return;
			_lastContextKey = key;

			GamePadProbeSystem.LogControlUiContext(
				host,
				state?.GetType().Name,
				manageControlsFound: true,
				PlayerInput.CurrentInputMode.ToString(),
				UILinkPointNavigator.CurrentPoint,
				pointCount,
				UILinkPointNavigator.Shortcuts.FANCYUI_HIGHEST_INDEX);
		}

		private static void RestoreInputModeIfNeeded()
		{
			if (!_restoreInputMode.HasValue)
				return;
			try
			{
				PlayerInput.CurrentInputMode = _restoreInputMode.Value;
			}
			catch
			{
				// ignore
			}

			_restoreInputMode = null;
		}
	}
}
