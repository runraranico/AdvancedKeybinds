using System;
using AdvancedKeybinds.Binding;
using AdvancedKeybinds.Core;
using AdvancedKeybinds.Diagnostics;
using AdvancedKeybinds.UI;
using Terraria;
using Terraria.GameInput;

namespace AdvancedKeybinds.Patches
{
	/// <summary>
	/// One physical GamePad press → one UI action. After ComboPanel consumes Pad:A/B to close,
	/// the same hold cannot reopen a row or Back the parent until a real RELEASE edge.
	/// </summary>
	internal static class ComboPanelCloseInputConsumeGate
	{
		/// <summary>If IsDown never samples true after Arm (JustPressed-only), keep gate this many epochs.</summary>
		private const int MinEpochsWhenNeverDown = 8;
		/// <summary>After RELEASE, keep blocking parent open a few ticks (MenuUI click can lag PlayerInput).</summary>
		private const int PostReleaseBlockEpochs = 4;

		private static string _token;
		private static string _reason;
		private static bool _active;
		private static int _armedEpoch = int.MinValue;
		private static bool _seenDownAfterArm;
		private static bool _wasDownAfterArm;
		private static string _lastBlockTarget;
		private static int _postReleaseBlockRemaining;
		private static string _postReleaseToken;

		internal static bool IsActive => _active && !string.IsNullOrEmpty(_token);

		internal static string ActiveToken => _active ? _token : null;

		internal static string ActiveReason => _active ? _reason : null;

		internal static void ClearRuntimeState()
		{
			_token = null;
			_reason = null;
			_active = false;
			_armedEpoch = int.MinValue;
			_seenDownAfterArm = false;
			_wasDownAfterArm = false;
			_lastBlockTarget = null;
			_postReleaseBlockRemaining = 0;
			_postReleaseToken = null;
		}

		internal static void Arm(string token, string reason)
		{
			if (!GamePadToken.TryCanonicalize(token, out string canonical))
				return;

			// Already armed for the same button: keep RELEASE tracking; only refresh suppress + reason.
			if (_active && string.Equals(_token, canonical, StringComparison.OrdinalIgnoreCase))
			{
				_reason = reason ?? _reason ?? "?";
				if (FnaGamePadProvider.IsDown(canonical) || FnaGamePadProvider.WasJustPressed(canonical))
				{
					_seenDownAfterArm = true;
					_wasDownAfterArm = true;
				}
				SuppressParentForActiveToken();
				return;
			}

			_token = canonical;
			_reason = reason ?? "?";
			_active = true;
			_armedEpoch = FnaGamePadProvider.InputTickEpoch;
			_lastBlockTarget = null;
			_postReleaseBlockRemaining = 0;
			_postReleaseToken = null;

			_seenDownAfterArm = FnaGamePadProvider.IsDown(canonical)
				|| FnaGamePadProvider.WasJustPressed(canonical);
			_wasDownAfterArm = _seenDownAfterArm;

			GamePadProbeSystem.LogPanelInputConsumeBegin(
				canonical,
				_reason,
				_armedEpoch);

			SuppressParentForActiveToken();
		}

		internal static void PollAndSuppress()
		{
			if (_postReleaseBlockRemaining > 0)
			{
				_postReleaseBlockRemaining--;
				if (_postReleaseBlockRemaining <= 0)
					_postReleaseToken = null;
				else if (string.Equals(_postReleaseToken, GamePadToken.A, StringComparison.OrdinalIgnoreCase))
				{
					ClearTrigger(PlayerInput.Triggers?.JustPressed, "MouseLeft");
					ClearTrigger(PlayerInput.Triggers?.Current, "MouseLeft");
				}
			}

			if (!_active)
				return;

			if (string.IsNullOrEmpty(_token))
			{
				End("Invalid");
				return;
			}

			bool down = FnaGamePadProvider.IsDown(_token);
			// Title / MenuUI: also honor KeyToken in case FNA snapshot flickers for one tick.
			if (!down)
			{
				try { down = KeyToken.IsDown(_token); }
				catch { down = false; }
			}

			if (down)
			{
				_seenDownAfterArm = true;
				_wasDownAfterArm = true;
				SuppressParentForActiveToken();
				return;
			}

			// Falling edge after we observed a hold / JustPressed → real RELEASE.
			if (_wasDownAfterArm)
			{
				End("Released");
				return;
			}

			// JustPressed-only tap: IsDown never sampled. Keep gate for several epochs so
			// MenuUI / parent row cannot reuse the same confirm, then end.
			int age = FnaGamePadProvider.InputTickEpoch - _armedEpoch;
			if (age >= MinEpochsWhenNeverDown)
			{
				End("Released");
				return;
			}

			SuppressParentForActiveToken();
		}

		/// <summary>Block reopening keybind rows with the same Pad:A that saved/closed the panel.</summary>
		internal static bool ShouldBlockParentKeybindRowOpen()
		{
			if (_postReleaseBlockRemaining > 0
				&& string.Equals(_postReleaseToken, GamePadToken.A, StringComparison.OrdinalIgnoreCase))
			{
				LogBlockOnce("ParentKeybindRow");
				return true;
			}

			if (!_active || string.IsNullOrEmpty(_token))
				return false;
			if (!string.Equals(_token, GamePadToken.A, StringComparison.OrdinalIgnoreCase))
				return false;

			// While armed, always block — do not require IsDown (title MenuUI often clicks after JustPressed).
			LogBlockOnce("ParentKeybindRow");
			return true;
		}

		/// <summary>Block parent Back / FancyUI close / Pad:B hold while CancelPanel owns Pad:B.</summary>
		internal static bool ShouldBlockParentBack()
		{
			if (!_active || string.IsNullOrEmpty(_token))
				return false;
			if (!string.Equals(_token, GamePadToken.B, StringComparison.OrdinalIgnoreCase)
				&& !string.Equals(_token, GamePadToken.Back, StringComparison.OrdinalIgnoreCase))
				return false;

			LogBlockOnce("ParentBack");
			return true;
		}

		internal static void LogDuplicateBackBlocked(string firstAction, string blockedAction)
		{
			GamePadProbeSystem.LogControlBackDuplicateBlocked(
				_token ?? GamePadToken.B,
				firstAction,
				blockedAction);
		}

		private static void SuppressParentForActiveToken()
		{
			try
			{
				bool allowInventoryClear = TerrariaCancelInputPolicy.ShouldSuppressPadCancelTriggersForParent(
					ComboPanel.IsOpen,
					manageControlsOpen: ControlsUiHost.IsManageControlsOpen(),
					inFancyUi: false,
					ingameOptions: Main.ingameOptionsWindow);

				if (!allowInventoryClear && !ComboPanel.IsOpen)
				{
					ClearRuntimeState();
					return;
				}

				var cleared = new System.Collections.Generic.List<string>(4);
				if (allowInventoryClear)
				{
					GamePadParentUiTriggerSuppress.ClearParentNavTriggersMappedToPad(
						PlayerInput.Triggers?.JustPressed,
						_token,
						cleared);
					GamePadParentUiTriggerSuppress.ClearParentNavTriggersMappedToPad(
						PlayerInput.Triggers?.Current,
						_token,
						cleared);
				}

				if (allowInventoryClear
					&& (string.Equals(_token, GamePadToken.B, StringComparison.OrdinalIgnoreCase)
						|| string.Equals(_token, GamePadToken.Back, StringComparison.OrdinalIgnoreCase)))
				{
					ClearTrigger(PlayerInput.Triggers?.JustPressed, "Inventory");
					ClearTrigger(PlayerInput.Triggers?.Current, "Inventory");
					ClearTrigger(PlayerInput.Triggers?.JustPressed, "MenuInquiry");
					ClearTrigger(PlayerInput.Triggers?.Current, "MenuInquiry");
					ClearTrigger(PlayerInput.Triggers?.JustPressed, "MouseRight");
					ClearTrigger(PlayerInput.Triggers?.Current, "MouseRight");
				}

				if (string.Equals(_token, GamePadToken.A, StringComparison.OrdinalIgnoreCase))
				{
					ClearTrigger(PlayerInput.Triggers?.JustPressed, "MouseLeft");
					ClearTrigger(PlayerInput.Triggers?.Current, "MouseLeft");
				}
			}
			catch
			{
				// ignore
			}
		}

		private static void End(string reason)
		{
			if (!_active)
				return;
			string tok = _token;
			_active = false;
			_token = null;
			_reason = null;
			_armedEpoch = int.MinValue;
			_seenDownAfterArm = false;
			_wasDownAfterArm = false;
			_lastBlockTarget = null;

			// Catch MenuUI row activate that runs after PlayerInput saw RELEASE.
			if (string.Equals(tok, GamePadToken.A, StringComparison.OrdinalIgnoreCase))
			{
				_postReleaseToken = tok;
				_postReleaseBlockRemaining = PostReleaseBlockEpochs;
			}

			GamePadProbeSystem.LogPanelInputConsumeEnd(tok, reason);
		}

		private static void LogBlockOnce(string target)
		{
			if (string.Equals(_lastBlockTarget, target, StringComparison.Ordinal))
				return;
			_lastBlockTarget = target;
			GamePadProbeSystem.LogPanelInputConsumeBlock(_token ?? _postReleaseToken, target);
		}

		private static void ClearTrigger(TriggersSet set, string name)
		{
			if (set?.KeyStatus == null || string.IsNullOrEmpty(name))
				return;
			if (set.KeyStatus.ContainsKey(name))
				set.KeyStatus[name] = false;
		}
	}
}
