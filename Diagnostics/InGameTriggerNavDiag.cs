// TEMPORARY: cross-UI GamePad nav diagnosis. DELETE before public release.
using System;
using AdvancedKeybinds.Binding;
using AdvancedKeybinds.Core;
using AdvancedKeybinds.Patches;
using AdvancedKeybinds.UI;
using Terraria;
using Terraria.GameInput;
using Terraria.UI.Gamepad;

namespace AdvancedKeybinds.Diagnostics
{
	/// <summary>
	/// Observation-only for foreign / inventory / L2-R2 UI nav.
	/// Does not ChangePoint, invent links, or patch other mods.
	/// Direction / L2/R2 PRESS edge only.
	/// </summary>
	internal static class InGameTriggerNavDiag
	{
		private static int _pendingBeforePoint = int.MinValue;
		private static string _pendingToken;
		private static string _pendingUiType;
		private static bool _pendingIsL2R2;
		private static int _pendingOfficialTarget = int.MinValue;
		private static string _lastInputMode;

		// Deferred: UILinkPointNavigator often applies Move after PlayerInput returns.
		private static int _deferredBefore = int.MinValue;
		private static string _deferredToken;
		private static string _deferredUiType;
		private static bool _deferredIsL2R2;
		private static int _deferredOfficialTarget = int.MinValue;
		private static bool _deferredAkSuppress;

		internal static void ClearRuntimeState()
		{
			_pendingBeforePoint = int.MinValue;
			_pendingToken = null;
			_pendingUiType = null;
			_pendingIsL2R2 = false;
			_pendingOfficialTarget = int.MinValue;
			_lastInputMode = null;
			_deferredBefore = int.MinValue;
			_deferredToken = null;
			_deferredUiType = null;
			_deferredIsL2R2 = false;
			_deferredOfficialTarget = int.MinValue;
			_deferredAkSuppress = false;
		}

		internal static void PollBeginOfInputTick()
		{
			FlushDeferredResult();

			string modeNow = PlayerInput.CurrentInputMode.ToString();
			if (_lastInputMode != null && !string.Equals(_lastInputMode, modeNow, StringComparison.Ordinal))
				GamePadProbeSystem.LogIngameTriggerNavMode(_lastInputMode, modeNow, "Observed");
			_lastInputMode = modeNow;

			_pendingBeforePoint = int.MinValue;
			_pendingToken = null;
			_pendingUiType = null;
			_pendingIsL2R2 = false;
			_pendingOfficialTarget = int.MinValue;

			bool lt = FnaGamePadProvider.WasJustPressed(GamePadToken.LeftTrigger);
			bool rt = FnaGamePadProvider.WasJustPressed(GamePadToken.RightTrigger);
			if (lt || rt)
			{
				ArmPending(lt ? GamePadToken.LeftTrigger : GamePadToken.RightTrigger, isL2R2: true);
				return;
			}

			if (ComboPanel.IsOpen || ControlsUiHost.IsManageControlsOpen())
				return;

			string dpadTok = null;
			if (FnaGamePadProvider.WasJustPressed(GamePadToken.DPadRight))
				dpadTok = GamePadToken.DPadRight;
			else if (FnaGamePadProvider.WasJustPressed(GamePadToken.DPadLeft))
				dpadTok = GamePadToken.DPadLeft;
			else if (FnaGamePadProvider.WasJustPressed(GamePadToken.DPadUp))
				dpadTok = GamePadToken.DPadUp;
			else if (FnaGamePadProvider.WasJustPressed(GamePadToken.DPadDown))
				dpadTok = GamePadToken.DPadDown;

			if (dpadTok != null)
				ArmPending(dpadTok, isL2R2: false);
		}

		internal static void PollEndOfInputTick()
		{
			if (_pendingBeforePoint == int.MinValue || string.IsNullOrEmpty(_pendingToken))
				return;

			bool akControls = ControlsUiHost.IsManageControlsOpen();
			bool akSuppress = ComboPanel.IsOpen
				|| ComboPanelCloseInputConsumeGate.ShouldBlockParentKeybindRowOpen()
				|| ComboPanelCloseInputConsumeGate.ShouldBlockParentBack();
			string owner = akSuppress
				? (ComboPanel.IsOpen ? "ComboPanel" : "CloseInputConsumeGate")
				: "None";

			GamePadProbeSystem.LogGlobalUiNavAkSuppress(_pendingUiType, owner, _pendingToken, akSuppress);

			// Same-tick snapshot (often Moved=False; real move may be deferred).
			int afterNow = UILinkPointNavigator.CurrentPoint;
			LogLinkAndResult(
				_pendingUiType,
				_pendingToken,
				_pendingBeforePoint,
				afterNow,
				_pendingOfficialTarget,
				akSuppress,
				akControls,
				sameTick: true);

			if (_pendingIsL2R2)
			{
				GamePadProbeSystem.LogIngameTriggerNavState(
					_pendingToken,
					ReadTrigger("MenuLeft"),
					ReadTrigger("MenuRight"),
					ReadTrigger("HotbarMinus"),
					ReadTrigger("HotbarPlus"),
					ReadTrigger("MouseLeft"),
					ReadTrigger("MouseRight"),
					TryAllowInstructions(),
					TryAllowScrolling());
			}

			_deferredBefore = _pendingBeforePoint;
			_deferredToken = _pendingToken;
			_deferredUiType = _pendingUiType;
			_deferredIsL2R2 = _pendingIsL2R2;
			_deferredOfficialTarget = _pendingOfficialTarget;
			_deferredAkSuppress = akSuppress;

			_pendingBeforePoint = int.MinValue;
			_pendingToken = null;
			_pendingUiType = null;
			_pendingIsL2R2 = false;
			_pendingOfficialTarget = int.MinValue;
		}

		private static void FlushDeferredResult()
		{
			if (_deferredBefore == int.MinValue || string.IsNullOrEmpty(_deferredToken))
				return;

			int after = UILinkPointNavigator.CurrentPoint;
			LogLinkAndResult(
				_deferredUiType,
				_deferredToken,
				_deferredBefore,
				after,
				_deferredOfficialTarget,
				_deferredAkSuppress,
				ControlsUiHost.IsManageControlsOpen(),
				sameTick: false);

			_deferredBefore = int.MinValue;
			_deferredToken = null;
			_deferredUiType = null;
			_deferredIsL2R2 = false;
			_deferredOfficialTarget = int.MinValue;
			_deferredAkSuppress = false;
		}

		private static void ArmPending(string token, bool isL2R2)
		{
			_pendingToken = token;
			_pendingIsL2R2 = isL2R2;
			_pendingBeforePoint = UILinkPointNavigator.CurrentPoint;
			_pendingUiType = DescribeUiType();
			_pendingOfficialTarget = ResolveOfficialLinkTarget(_pendingBeforePoint, token);

			bool akControls = ControlsUiHost.IsManageControlsOpen();
			bool akSuppress = ComboPanel.IsOpen
				|| ComboPanelCloseInputConsumeGate.ShouldBlockParentKeybindRowOpen()
				|| ComboPanelCloseInputConsumeGate.ShouldBlockParentBack();

			GamePadProbeSystem.LogGlobalUiNavInput(
				_pendingUiType,
				token,
				_pendingBeforePoint,
				PlayerInput.CurrentInputMode.ToString(),
				UILinkPointNavigator.InUse,
				akControls,
				akSuppress);

			if (isL2R2)
			{
				GamePadProbeSystem.LogIngameTriggerNavInput(
					token,
					Main.gameMenu,
					Main.playerInventory,
					ComboPanel.IsOpen,
					akControls,
					PlayerInput.CurrentInputMode.ToString(),
					_pendingBeforePoint,
					UILinkPointNavigator.InUse);
			}
			else
			{
				GamePadProbeSystem.LogForeignUiNavInput(
					_pendingUiType,
					token,
					_pendingBeforePoint,
					PlayerInput.CurrentInputMode.ToString(),
					UILinkPointNavigator.InUse);
			}
		}

		private static void LogLinkAndResult(
			string uiType,
			string token,
			int before,
			int after,
			int officialTarget,
			bool akSuppress,
			bool akControls,
			bool sameTick)
		{
			int up = -1, down = -1, left = -1, right = -1;
			bool targetExists = false;
			bool targetVisible = false;
			string targetUi = "-";
			float tx = 0f, ty = 0f;

			float px = 0f, py = 0f;
			string pointName = ResolveOfficialPointIdName(before);
			if (UILinkPointNavigator.Points.TryGetValue(before, out UILinkPoint cur))
			{
				up = cur.Up;
				down = cur.Down;
				left = cur.Left;
				right = cur.Right;
				px = cur.Position.X;
				py = cur.Position.Y;
			}

			int linkForToken = officialTarget;
			if (linkForToken != int.MinValue && linkForToken >= 0
				&& UILinkPointNavigator.Points.TryGetValue(linkForToken, out UILinkPoint tgt))
			{
				targetExists = true;
				tx = tgt.Position.X;
				ty = tgt.Position.Y;
				targetVisible = tx > -10000f && ty > -10000f && (tx != 0f || ty != 0f);
				targetUi = "Point:" + linkForToken.ToString(System.Globalization.CultureInfo.InvariantCulture)
					+ "/" + ResolveOfficialPointIdName(linkForToken);
			}
			else if (linkForToken < 0)
			{
				targetExists = false;
				targetUi = "Special:" + linkForToken.ToString(System.Globalization.CultureInfo.InvariantCulture)
					+ "/" + ResolveOfficialPointIdName(linkForToken);
			}

			GamePadProbeSystem.LogGlobalUiNavLink(
				before, up, down, left, right, targetExists, targetVisible, targetUi, pointName, px, py);

			bool moved = before != after;
			bool officialUsed = moved && officialTarget >= 0 && after == officialTarget;

			GamePadProbeSystem.LogGlobalUiNavResult(
				before,
				after,
				moved,
				officialUsed,
				akSuppress);

			if (!sameTick && _deferredIsL2R2)
			{
				GamePadProbeSystem.LogIngameTriggerNavResult(token, before, after);
			}
			else if (!sameTick)
			{
				GamePadProbeSystem.LogForeignUiNavResult(
					uiType,
					before,
					after,
					up,
					down,
					left,
					right,
					tx,
					ty);
				GamePadProbeSystem.LogForeignUiAkInterference(
					uiType,
					akSuppress ? (ComboPanel.IsOpen ? "ComboPanel" : "CloseGate") : "None",
					token,
					akSuppress);
			}

			_ = akControls;
		}

		private static int ResolveOfficialLinkTarget(int point, string token)
		{
			if (!UILinkPointNavigator.Points.TryGetValue(point, out UILinkPoint pt))
				return int.MinValue;
			if (string.Equals(token, GamePadToken.DPadUp, StringComparison.OrdinalIgnoreCase))
				return pt.Up;
			if (string.Equals(token, GamePadToken.DPadDown, StringComparison.OrdinalIgnoreCase))
				return pt.Down;
			if (string.Equals(token, GamePadToken.DPadLeft, StringComparison.OrdinalIgnoreCase))
				return pt.Left;
			if (string.Equals(token, GamePadToken.DPadRight, StringComparison.OrdinalIgnoreCase))
				return pt.Right;
			// L2/R2 are not DPad link edges — leave unresolved for link diagnostics.
			return int.MinValue;
		}

		private static bool ReadTrigger(string name)
		{
			try
			{
				var set = PlayerInput.Triggers?.Current;
				if (set?.KeyStatus == null || string.IsNullOrEmpty(name))
					return false;
				return set.KeyStatus.TryGetValue(name, out bool v) && v;
			}
			catch
			{
				return false;
			}
		}

		private static bool TryAllowInstructions()
		{
			try { return PlayerInput.AllowExecutionOfGamepadInstructions; }
			catch { return true; }
		}

		private static bool TryAllowScrolling()
		{
			try { return PlayerInput.GamepadAllowScrolling; }
			catch { return true; }
		}

		private static string DescribeUiType()
		{
			try
			{
				if (ControlsUiHost.IsManageControlsOpen())
					return "UIManageControls";
				if (ComboPanel.IsOpen)
					return "ComboPanel";

				int cp = UILinkPointNavigator.CurrentPoint;
				if (cp >= 2500 && cp <= 2503)
					return "NPCChat:" + ResolveOfficialPointIdName(cp);
				if (cp >= 1500 && cp < 1550)
					return "CraftsSmallRegion:" + ResolveOfficialPointIdName(cp);
				if (cp >= 700 && cp < 800)
					return "CraftsBigRegion:" + ResolveOfficialPointIdName(cp);

				if (Main.InGameUI?.CurrentState != null)
				{
					string n = Main.InGameUI.CurrentState.GetType().FullName ?? Main.InGameUI.CurrentState.GetType().Name;
					return "InGameUI:" + n;
				}

				if (Main.MenuUI?.CurrentState != null)
				{
					string n = Main.MenuUI.CurrentState.GetType().FullName ?? Main.MenuUI.CurrentState.GetType().Name;
					return "MenuUI:" + n;
				}

				if (Main.playerInventory)
					return "PlayerInventory";
			}
			catch
			{
				// ignore
			}

			return "Unknown";
		}

		/// <summary>Terraria.GamepadPointID names for diagnostics only (not production branching).</summary>
		internal static string ResolveOfficialPointIdName(int id)
		{
			return id switch
			{
				-1 => "EndUp",
				-2 => "EndDown",
				-3 => "EndLeft",
				-4 => "EndRight",
				2500 => "NPCChat0",
				2501 => "NPCChat1",
				2502 => "NPCChat2",
				2503 => "NPCChat3",
				1500 => "CraftsSmall",
				700 => "CraftsBig",
				0 => "Inventory0",
				3002 => "FancyUI_GamepadTabApprox",
				_ => "Id" + id.ToString(System.Globalization.CultureInfo.InvariantCulture)
			};
		}
	}
}
