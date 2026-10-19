using System;
using AdvancedKeybinds.Binding;
using AdvancedKeybinds.Core;
using AdvancedKeybinds.Diagnostics;
using AdvancedKeybinds.UI;
using Terraria.UI;
using Terraria.UI.Gamepad;

namespace AdvancedKeybinds.Patches
{
	/// <summary>
	/// On face-South (<see cref="AkUiPadNavigationPolicy.ConfirmToken"/> / Pad:A) JustPressed while a
	/// binding keybind row is focused, opens ComboPanel directly — no MouseLeft / ItemUse OnClick wait.
	/// Never promotes Item Use / gameplay bindings to UI confirm. Never completes a confirm via R2/Pad:X.
	/// </summary>
	internal static class PendingGamePadRowConfirm
	{
		private const int MaxAgeEpochs = PadConfirmAfterComboSavePolicy.PendingTimeoutEpochs;

		private static bool _active;
		private static bool _consumed;
		private static int _armedEpoch = int.MinValue;
		private static int _pointId = int.MinValue;
		private static string _actionId;
		private static string _stableId;
		private static string _host;
		private static string _openToken;

		/// <summary>True after a successful direct Open until Confirm is released.</summary>
		private static bool _openedThisConfirmHold;

		private static bool _confirmWasDown;

		internal static bool IsActive => _active && !_consumed;

		internal static bool ShouldSuppressOnClickOpen => _openedThisConfirmHold;

		internal static void ClearRuntimeState()
		{
			_active = false;
			_consumed = false;
			_armedEpoch = int.MinValue;
			_pointId = int.MinValue;
			_actionId = null;
			_stableId = null;
			_host = null;
			_openToken = null;
			_openedThisConfirmHold = false;
			_confirmWasDown = false;
		}

		/// <summary>
		/// Call once per input tick after <see cref="FnaGamePadProvider.AdvanceInputTick"/> while
		/// Controls UI may be open. Direct-opens on a fresh UI-confirm (South) edge for a Binding row.
		/// </summary>
		internal static void PollArmFromInputTick()
		{
			if (!FnaGamePadProvider.IsSubsystemActive)
			{
				if (_active || _openedThisConfirmHold)
					ClearRuntimeState();
				return;
			}

			string confirmTok = AkUiPadNavigationPolicy.ConfirmToken;
			bool confirmDown = FnaGamePadProvider.IsDown(confirmTok);
			bool confirmReleased = _confirmWasDown && !confirmDown;
			_confirmWasDown = confirmDown;

			if (confirmReleased)
			{
				if (_openedThisConfirmHold || IsActive)
				{
					ClearPendingOnly();
					_openedThisConfirmHold = false;
				}
			}

			ExpireIfNeeded();
			MaybeDiscardOnFocusOrUiChange();

			if (FnaGamePadProvider.WasJustPressed(AkUiPadNavigationPolicy.CancelToken))
			{
				if (IsActive || _openedThisConfirmHold)
				{
					ClearPendingOnly();
					_openedThisConfirmHold = false;
				}
			}

			string rowKind = ControlsGamepadAutoScrollPatches.DescribeFocusedRowKindForDiag();
			int pointNow = UILinkPointNavigator.CurrentPoint;

			if (ComboPanel.IsOpen)
			{
				GamePadProbeSystem.LogPanelRowConfirmPoll(pointNow, rowKind, null, armAllowed: false, "ComboPanelOpen");
				LogNotArmed("ComboPanelOpen");
				if (IsActive)
					ClearPendingOnly();
				return;
			}

			if (ComboPanelCloseInputConsumeGate.ShouldBlockParentKeybindRowOpen())
			{
				GamePadProbeSystem.LogPanelRowConfirmPoll(pointNow, rowKind, null, armAllowed: false, "CloseInputConsumeGate");
				LogNotArmed("CloseInputConsumeGate");
				return;
			}

			if (ComboPanel.ShouldBlockOpenAfterSaveClose())
			{
				GamePadProbeSystem.LogPanelRowConfirmPoll(pointNow, rowKind, null, armAllowed: false, "SaveCloseEpochGate");
				LogNotArmed("SaveCloseEpochGate");
				return;
			}

			if (!ControlsUiHost.IsManageControlsOpen())
			{
				GamePadProbeSystem.LogPanelRowConfirmPoll(pointNow, rowKind, null, armAllowed: false, "ControlsClosed");
				if (IsActive || _openedThisConfirmHold)
					ClearRuntimeState();
				return;
			}

			if (!FnaGamePadProvider.WasJustPressed(confirmTok))
				return;

			if (_openedThisConfirmHold)
			{
				return;
			}

			if (!AkUiPadNavigationPolicy.ShouldArmUiConfirmFromEdge(
				confirmTok,
				alreadyPendingActive: IsActive))
				return;

			if (!ControlsGamepadAutoScrollPatches.TryGetFocusedBindingRow(
				out int pointId,
				out string actionId,
				out string stableId,
				out string host,
				out _,
				out UIElement rowElement))
			{
				GamePadProbeSystem.LogPanelRowConfirmPoll(pointNow, rowKind, null, armAllowed: false, "NotBindingRow");
				return;
			}

			GamePadProbeSystem.LogPanelRowConfirmPoll(pointId, rowKind, actionId, armAllowed: true, "Ok");

			_active = true;
			_consumed = false;
			_armedEpoch = FnaGamePadProvider.InputTickEpoch;
			_pointId = pointId;
			_actionId = actionId;
			_stableId = stableId;
			_host = host ?? "?";
			_openToken = confirmTok;

			GamePadProbeSystem.LogPanelRowConfirmPendingBegin(
				_host,
				_pointId,
				_actionId,
				_armedEpoch);

			// Product path: open immediately — do not wait for ItemUse → MouseLeft → OnClick.
			if (KeybindingUiPatches.TryOpenComboPanelFromPadConfirm(
				rowElement,
				actionId,
				confirmTok,
				out string openReject))
			{
				_openedThisConfirmHold = true;
				_consumed = true;
				_active = false;
				GamePadProbeSystem.LogPanelOpenAttempt(
					actionId,
					"DirectPadAOpen",
					true,
					true,
					false,
					FnaGamePadProvider.InputTickEpoch);
				ClearPendingFieldsKeepHoldFlag();
				return;
			}

			// Open failed (gates / invalid row) — discard pending so ItemUse cannot complete it later.
			ClearPendingOnly();
		}

		/// <summary>
		/// OnClick path: only accept when Pad:A confirm gesture is still active for this arm.
		/// ItemUse-only MouseLeft must never complete a Pad:A pending.
		/// </summary>
		internal static bool TryConsumeMatching(
			string clickActionId,
			out string sourceLabel,
			out string rejectReason,
			out string openToken)
		{
			sourceLabel = "PadConfirmPending";
			rejectReason = null;
			openToken = null;
			ExpireIfNeeded();

			if (_openedThisConfirmHold)
			{
				rejectReason = "AlreadyDirectOpened";
				ClearPendingOnly();
				return false;
			}

			if (!_active || _consumed)
				return false;

			bool padAGesture = FnaGamePadProvider.WasJustPressed(AkUiPadNavigationPolicy.ConfirmToken)
				|| FnaGamePadProvider.IsDown(AkUiPadNavigationPolicy.ConfirmToken);
			if (!PadConfirmAfterComboSavePolicy.ShouldAcceptPendingConsumeFromOnClick(
				pendingActive: true,
				padAConfirmGestureActive: padAGesture,
				mouseLeftArrived: true))
			{
				rejectReason = "StalePendingItemUseMouseLeftRejected";
				ClearPendingOnly();
				return false;
			}

			ControlsUiHost.TryGetManageControls(out _, out string hostNow);
			hostNow ??= "?";

			int pointNow = UILinkPointNavigator.CurrentPoint;
			bool hostOk = string.Equals(_host, hostNow, StringComparison.OrdinalIgnoreCase)
				|| (string.IsNullOrEmpty(hostNow) && !string.IsNullOrEmpty(_host));
			bool actionOk = !string.IsNullOrEmpty(clickActionId)
				&& !string.IsNullOrEmpty(_actionId)
				&& string.Equals(clickActionId, _actionId, StringComparison.Ordinal);

			GamePadProbeSystem.LogPanelRowActionIdentity(
				pointNow,
				focusedAction: _actionId,
				pendingAction: _actionId,
				clickAction: clickActionId,
				matched: actionOk);

			if (!hostOk)
			{
				rejectReason = "HostMismatch";
				GamePadProbeSystem.LogPanelRowConfirmPendingReject(
					_pointId, pointNow, _actionId, clickActionId, rejectReason);
				ClearPendingOnly();
				return false;
			}

			if (!actionOk)
			{
				rejectReason = "ActionMismatch";
				GamePadProbeSystem.LogPanelRowActionIdentityMismatch(
					pointNow, _actionId, clickActionId, allowed: false);
				GamePadProbeSystem.LogPanelRowConfirmPendingReject(
					_pointId, pointNow, _actionId, clickActionId, rejectReason);
				ClearPendingOnly();
				return false;
			}

			_consumed = true;
			_active = false;
			_openedThisConfirmHold = true;
			openToken = _openToken ?? AkUiPadNavigationPolicy.ConfirmToken;
			int delta = FnaGamePadProvider.InputTickEpoch - _armedEpoch;
			GamePadProbeSystem.LogPanelRowConfirmPendingConsume(
				_host, _pointId, _actionId ?? clickActionId, delta);

			ClearPendingFieldsKeepHoldFlag();
			return true;
		}

		private static void MaybeDiscardOnFocusOrUiChange()
		{
			if (!IsActive)
				return;

			int pointNow = UILinkPointNavigator.CurrentPoint;
			if (_pointId != int.MinValue && pointNow != _pointId)
			{
				if (PadConfirmAfterComboSavePolicy.ShouldDiscardPending(
					padAReleased: false,
					cancelPressed: false,
					panelClosedOrClosing: false,
					focusChanged: true,
					targetUiInvalid: false,
					timedOut: false,
					modUnloadOrReload: false))
				{
					ClearPendingOnly();
				}
			}
		}

		private static void ExpireIfNeeded()
		{
			if (!_active || _consumed)
				return;

			int age = FnaGamePadProvider.InputTickEpoch - _armedEpoch;
			if (age < 0)
				age = 0;
			if (age <= MaxAgeEpochs)
				return;

			string action = _actionId ?? "?";
			GamePadProbeSystem.LogPanelRowConfirmPendingExpired(action, age);
			ClearPendingOnly();
		}

		private static void ClearPendingOnly()
		{
			_active = false;
			_consumed = false;
			_armedEpoch = int.MinValue;
			_pointId = int.MinValue;
			_actionId = null;
			_stableId = null;
			_host = null;
			_openToken = null;
		}

		private static void ClearPendingFieldsKeepHoldFlag()
		{
			ClearPendingOnly();
		}

		private static void LogNotArmed(string reason)
		{
			GamePadProbeSystem.LogPanelRowConfirmPendingNotArmed(reason);
		}
	}
}
