using System;
using System.Collections.Generic;
using AdvancedKeybinds.Diagnostics;
using AdvancedKeybinds.UI;
using Terraria;
using Terraria.GameInput;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// While <see cref="ComboPanel.IsOpen"/>, strip parent-UI and gameplay Triggers so ComboPanel owns input.
	/// Does not clear FNA / Win32 / KeyToken sources used by SlotCapture — only Terraria Triggers.
	/// </summary>
	internal static class ComboPanelBackgroundInputBlock
	{
		/// <summary>Parent Controls / FancyUI navigation and dismiss.</summary>
		private static readonly string[] ParentUiTriggers = {
			"Inventory",
			"MouseRight",
			"MouseLeft",
			"MenuInquiry",
			"MenuUp",
			"MenuDown",
			"MenuLeft",
			"MenuRight",
			"HotbarMinus",
			"HotbarPlus",
			"Hotbar1",
			"Hotbar2",
			"Hotbar3",
			"Hotbar4",
			"Hotbar5",
			"Hotbar6",
			"Hotbar7",
			"Hotbar8",
			"Hotbar9",
			"Hotbar10"
		};

		/// <summary>In-world gameplay (and KeyboardUI movement that can drive list / player).</summary>
		private static readonly string[] GameplayTriggers = {
			"Up",
			"Down",
			"Left",
			"Right",
			"Jump",
			"Grapple",
			"QuickMount",
			"QuickHeal",
			"QuickMana",
			"QuickBuff",
			"Throw",
			"Inventory",
			"SmartSelect",
			"SmartCursor",
			"MouseLeft",
			"MouseRight",
			"MouseMiddle",
			"MouseXButton1",
			"MouseXButton2"
		};

		private static bool _blockActive;
		private static string _lastBlockLogKey;

		internal static void Reset()
		{
			if (_blockActive)
			{
				GamePadProbeSystem.LogPanelBackgroundInputBlockEnd("Reset");
				_blockActive = false;
			}

			_lastBlockLogKey = null;
		}

		internal static void NotifyPanelOpened(string host)
		{
			_blockActive = true;
			_lastBlockLogKey = null;
			GamePadProbeSystem.LogPanelBackgroundInputBlockBegin(host ?? "?");
		}

		internal static void NotifyPanelClosed(string reason)
		{
			if (!_blockActive && !ComboPanel.IsOpen)
			{
				_lastBlockLogKey = null;
				return;
			}

			_blockActive = false;
			_lastBlockLogKey = null;
			GamePadProbeSystem.LogPanelBackgroundInputBlockEnd(reason ?? "Other");
		}

		/// <summary>
		/// Clear parent + gameplay Triggers while ComboPanel is open.
		/// Call after ComboPanel has read input for capture / pad nav (or after vanilla rebuild, before parent UI).
		/// </summary>
		internal static void ApplyWhilePanelOpen(string source = null)
		{
			_ = source;
			if (!ComboPanel.IsOpen)
				return;

			_blockActive = true;
			var cleared = new List<string>(16);
			ClearNamed(PlayerInput.Triggers?.JustPressed, ParentUiTriggers, cleared);
			ClearNamed(PlayerInput.Triggers?.Current, ParentUiTriggers, cleared);
			ClearNamed(PlayerInput.Triggers?.JustPressed, GameplayTriggers, cleared);
			ClearNamed(PlayerInput.Triggers?.Current, GameplayTriggers, cleared);

			if (cleared.Count == 0)
				return;

			DedupSort(cleared);
			string key = string.Join(",", cleared);
			if (string.Equals(key, _lastBlockLogKey, StringComparison.Ordinal))
				return;

			_lastBlockLogKey = key;
			for (int i = 0; i < cleared.Count; i++)
			{
				string name = cleared[i];
				string target = IsGameplayName(name) ? "PlayerMovement" : "ParentControls";
				GamePadProbeSystem.LogPanelBackgroundInputBlock(
					token: "Trigger:" + name,
					target: target,
					trigger: name,
					cleared: true);
			}
		}

		/// <summary>Zero LocalPlayer movement / use controls while ComboPanel owns input (in-world only).</summary>
		internal static void SuppressLocalPlayerGameplay(Player player)
		{
			if (player == null || Main.gameMenu || !ComboPanel.IsOpen)
				return;

			player.controlLeft = false;
			player.controlRight = false;
			player.controlUp = false;
			player.controlDown = false;
			player.controlJump = false;
			player.controlUseItem = false;
			player.controlUseTile = false;
			player.controlThrow = false;
			player.controlHook = false;
			player.controlMount = false;
			player.controlSmart = false;
			player.controlTorch = false;
			player.controlInv = false;
			player.controlMap = false;
			player.mouseInterface = true;
		}

		internal static void LogOwnership(string token, string owner, bool parentBlocked, bool gameplayBlocked)
			=> GamePadProbeSystem.LogPanelInputOwnership(token, owner, parentBlocked, gameplayBlocked);

		private static bool IsGameplayName(string name)
		{
			if (string.IsNullOrEmpty(name))
				return false;
			for (int i = 0; i < GameplayTriggers.Length; i++)
			{
				if (string.Equals(GameplayTriggers[i], name, StringComparison.OrdinalIgnoreCase))
					return true;
			}

			return false;
		}

		private static void ClearNamed(TriggersSet set, string[] names, List<string> clearedOut)
		{
			if (set?.KeyStatus == null || names == null)
				return;

			for (int i = 0; i < names.Length; i++)
			{
				string name = names[i];
				if (!set.KeyStatus.TryGetValue(name, out bool on) || !on)
					continue;
				set.KeyStatus[name] = false;
				clearedOut?.Add(name);
			}
		}

		private static void DedupSort(List<string> list)
		{
			for (int i = list.Count - 1; i >= 0; i--)
			{
				string name = list[i];
				for (int j = 0; j < i; j++)
				{
					if (!string.Equals(list[j], name, StringComparison.OrdinalIgnoreCase))
						continue;
					list.RemoveAt(i);
					break;
				}
			}

			list.Sort(StringComparer.OrdinalIgnoreCase);
		}
	}
}
