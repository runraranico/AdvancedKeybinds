using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using AdvancedKeybinds.Binding;
using AdvancedKeybinds.Core;
using Microsoft.Xna.Framework;
using MonoMod.RuntimeDetour;
using Terraria;
using Terraria.ModLoader;
using Terraria.UI.Gamepad;

namespace AdvancedKeybinds.Patches
{
	/// <summary>
	/// Soft dependency (Compat): Dialogue Panel Rework (DialogueTweak) NPC-chat gamepad assist.
	/// AK-owned compatibility only — not DialogueTweak internals.
	/// Pad:A uses SetPosition centers + DialogueTweak's existing mouseLeft hover path.
	/// </summary>
	internal static class DialogueTweakGamepadCompat
	{
		private const string ModName = "DialogueTweak";
		private const string ButtonHandlerTypeName = "DialogueTweak.Interfaces.ButtonHandler";
		private const string HandleAssetsTypeName = "DialogueTweak.HandleAssets";
		private const string ChatUiTypeName = "DialogueTweak.Interfaces.ChatUI";
		private const string ButtonInfoTypeName = "DialogueTweak.ButtonInfo";
		/// <summary>Known local/upstream type that already registers AddButton UILinks.</summary>
		private const string UpstreamNavTypeName = "DialogueTweak.NpcChatGamepadNav";

		private const int ButtonRowWidth = 375;
		private const int ButtonSpacing = 10;
		private const int ButtonHeight = 44;
		private const int MainRowOffsetX = 122;

		private delegate void orig_DrawButtons(int statY, string focusText, string focusText2);

		private static bool _applied;
		private static bool _disabled;
		private static bool _loggedDisableOnce;
		private static bool _pageUpdateHooked;
		/// <summary>AK created/owns the extended wiring this session (reapply allowed).</summary>
		private static bool _akOwnsWiring;
		/// <summary>True upstream / foreign complete nav — do not wire this session (re-evaluated).</summary>
		private static bool _skipWiringUpstream;
		/// <summary>Skip came from DialogueTweak.NpcChatGamepadNav type presence (permanent until reload).</summary>
		private static bool _upstreamTypePresent;
		/// <summary>Visible button count for which residual skip was latched; -1 when unset.</summary>
		private static int _skipLatchVisibleCount = -1;

		private static Hook _drawButtonsHook;

		private static FieldInfo _buttonInfosField;
		private static PropertyInfo _panelPositionProperty;
		private static FieldInfo _npcTypesField;
		private static FieldInfo _availableField;

		private static readonly List<Vector2> ActiveCenters = new();
		private static readonly List<int> ActivePointIds = new();
		private static Action _pageUpdateHandler;

		public static void Apply()
		{
			if (_applied || _disabled)
				return;

			if (!ModLoader.HasMod(ModName))
				return;

			if (!ModLoader.TryGetMod(ModName, out Mod dialogueTweak) || dialogueTweak == null)
				return;

			if (dialogueTweak.Code == null)
			{
				DisableCompat("DialogueTweak.Code was null.", null);
				return;
			}

			try
			{
				Assembly asm = dialogueTweak.Code;

				_upstreamTypePresent = asm.GetType(UpstreamNavTypeName) != null;
				if (_upstreamTypePresent)
				{
					_skipWiringUpstream = true;
					_skipLatchVisibleCount = -1;
					return;
				}

				Type buttonHandlerType = asm.GetType(ButtonHandlerTypeName);
				if (buttonHandlerType == null)
				{
					DisableCompat("ButtonHandler was not found.", null);
					return;
				}

				Type handleAssetsType = asm.GetType(HandleAssetsTypeName);
				if (handleAssetsType == null)
				{
					DisableCompat("HandleAssets was not found.", null);
					return;
				}

				Type chatUiType = asm.GetType(ChatUiTypeName);
				if (chatUiType == null)
				{
					DisableCompat("ChatUI was not found.", null);
					return;
				}

				MethodInfo drawButtons = buttonHandlerType.GetMethod(
					"DrawButtons",
					BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
					binder: null,
					types: new[] { typeof(int), typeof(string), typeof(string) },
					modifiers: null);
				if (drawButtons == null)
				{
					DisableCompat("ButtonHandler.DrawButtons was not found.", null);
					return;
				}

				_buttonInfosField = handleAssetsType.GetField(
					"ButtonInfos",
					BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
				if (_buttonInfosField == null)
				{
					DisableCompat("HandleAssets.ButtonInfos was not found.", null);
					return;
				}

				_panelPositionProperty = chatUiType.GetProperty(
					"PanelPosition",
					BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
				if (_panelPositionProperty == null)
				{
					DisableCompat("ChatUI.PanelPosition was not found.", null);
					return;
				}

				Type buttonInfoType = _buttonInfosField.FieldType.IsGenericType
					? _buttonInfosField.FieldType.GetGenericArguments()[0]
					: asm.GetType(ButtonInfoTypeName);
				if (buttonInfoType == null)
				{
					DisableCompat("ButtonInfo was not found.", null);
					return;
				}

				_npcTypesField = buttonInfoType.GetField(
					"NPCTypes",
					BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
				_availableField = buttonInfoType.GetField(
					"Available",
					BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
				if (_npcTypesField == null || _availableField == null)
				{
					ClearReflectionCache();
					DisableCompat("ButtonInfo.NPCTypes/Available was not found.", null);
					return;
				}

				if (_drawButtonsHook != null)
					return;

				try
				{
					_drawButtonsHook = new Hook(
						drawButtons,
						new Action<orig_DrawButtons, int, string, string>(OnDrawButtons));
					_drawButtonsHook.Apply();
				}
				catch (Exception hookEx)
				{
					DisposeDrawButtonsHook();
					ClearReflectionCache();
					DisableCompat("Detour for ButtonHandler.DrawButtons failed.", hookEx);
					return;
				}

				_applied = true;
			}
			catch (Exception ex)
			{
				DisposeDrawButtonsHook();
				ClearReflectionCache();
				DisableCompat("DialogueTweak compatibility apply failed.", ex);
			}
		}

		public static void ResetAppliedFlag()
		{
			UnhookPageUpdate();
			DisposeDrawButtonsHook();
			ClearActive();
			ClearReflectionCache();
			_applied = false;
			_disabled = false;
			_loggedDisableOnce = false;
			_akOwnsWiring = false;
			_skipWiringUpstream = false;
			_upstreamTypePresent = false;
			_skipLatchVisibleCount = -1;
		}

		private static void OnDrawButtons(orig_DrawButtons orig, int statY, string focusText, string focusText2)
		{
			orig(statY, focusText, focusText2);

			if (_disabled || !_applied)
				return;

			// Permanent only when the upstream type itself is present (hook is usually not installed then).
			if (_skipWiringUpstream && _upstreamTypePresent)
				return;

			try
			{
				AfterDrawButtons(statY, focusText, focusText2);
			}
			catch (Exception ex)
			{
				DisableCompat("After DrawButtons assist failed.", ex);
			}
		}

		private static void AfterDrawButtons(int statY, string focusText, string focusText2)
		{
			if (Main.dedServ)
				return;

			Player player = Main.LocalPlayer;
			if (player == null)
				return;

			if (player.talkNPC < 0 && player.sign == -1)
			{
				ClearActive();
				_akOwnsWiring = false;
				return;
			}

			if (!TryBuildCenters(statY, focusText, focusText2, out List<Vector2> centers))
			{
				DisableCompat("Failed to build DialogueTweak button centers.", null);
				return;
			}

			int expected = centers.Count;
			if (expected <= 0)
			{
				ClearActive();
				_akOwnsWiring = false;
				return;
			}

			if (_akOwnsWiring)
			{
				_skipWiringUpstream = false;
				_skipLatchVisibleCount = -1;
				EnsurePageUpdateHooked();
				ApplyChain(centers);
				_akOwnsWiring = ActivePointIds.Count > 0;
				return;
			}

			var residual = ClassifyLiveResidual(expected);
			if (DialogueTweakGamepadCompatNavLayout.ShouldClearResidualSkipLatch(
				    _skipWiringUpstream, _upstreamTypePresent, residual, expected, _skipLatchVisibleCount))
			{
				_skipWiringUpstream = false;
				_skipLatchVisibleCount = -1;
			}

			var action = DialogueTweakGamepadCompatNavLayout.DecideWiringAction(
				akOwnsWiring: false,
				_skipWiringUpstream,
				_upstreamTypePresent,
				residual,
				expected,
				_skipLatchVisibleCount);

			switch (action)
			{
				case DialogueTweakGamepadCompatNavLayout.WiringAction.ClearInactive:
					ClearActive();
					_akOwnsWiring = false;
					return;

				case DialogueTweakGamepadCompatNavLayout.WiringAction.SkipUpstream:
					_skipWiringUpstream = true;
					_skipLatchVisibleCount = expected;
					_akOwnsWiring = false;
					ClearActive();
					return;

				case DialogueTweakGamepadCompatNavLayout.WiringAction.KeepOwning:
				case DialogueTweakGamepadCompatNavLayout.WiringAction.WireOrReclaim:
					_skipWiringUpstream = false;
					_skipLatchVisibleCount = -1;
					EnsurePageUpdateHooked();
					ApplyChain(centers);
					_akOwnsWiring = ActivePointIds.Count > 0;
					return;
			}
		}

		private static DialogueTweakGamepadCompatNavLayout.ResidualNavKind ClassifyLiveResidual(int expectedVisibleCount)
		{
			if (!UILinkPointNavigator.Pages.TryGetValue(
				    DialogueTweakGamepadCompatNavLayout.PreferredPageId, out UILinkPage page))
				return DialogueTweakGamepadCompatNavLayout.ResidualNavKind.Incomplete;

			return DialogueTweakGamepadCompatNavLayout.ClassifyResidualNav(
				expectedVisibleCount,
				id => page.LinkMap.ContainsKey(id) || UILinkPointNavigator.Points.ContainsKey(id),
				id =>
				{
					if (TryGetPoint(page, id, out UILinkPoint p))
						return p.Left;
					return DialogueTweakGamepadCompatNavLayout.NoLink;
				},
				id =>
				{
					if (TryGetPoint(page, id, out UILinkPoint p))
						return p.Right;
					return DialogueTweakGamepadCompatNavLayout.ForbiddenExit;
				},
				id =>
				{
					if (TryGetPoint(page, id, out UILinkPoint p))
						return p.Up;
					return int.MinValue;
				},
				id =>
				{
					if (TryGetPoint(page, id, out UILinkPoint p))
						return p.Down;
					return int.MinValue;
				});
		}

		private static bool TryGetPoint(UILinkPage page, int id, out UILinkPoint point)
		{
			if (page.LinkMap.TryGetValue(id, out point))
				return true;
			return UILinkPointNavigator.Points.TryGetValue(id, out point);
		}

		private static bool TryBuildCenters(int statY, string focusText, string focusText2, out List<Vector2> centers)
		{
			centers = new List<Vector2>(8);

			Player player = Main.LocalPlayer;
			if (player.talkNPC >= 0 && (player.talkNPC >= Main.npc.Length || Main.npc[player.talkNPC] == null))
				return false;

			if (_buttonInfosField == null || _panelPositionProperty == null
				|| _npcTypesField == null || _availableField == null)
				return false;

			NPCLoader.SetChatButtons(ref focusText, ref focusText2);

			bool showHappiness = player.sign == -1
				&& player.talkNPC >= 0
				&& !string.IsNullOrEmpty(player.currentShoppingSettings.HappinessReport)
				&& Main.npc[player.talkNPC].townNPC;

			if (!TryGetPanelPosition(out Vector2 panelPos))
				return false;

			float backWidth = showHappiness ? 44f : 98f;
			var backTopLeft = new Vector2(panelPos.X + 16f, statY + 10f);
			centers.Add(backTopLeft + new Vector2(backWidth, ButtonHeight) * 0.5f);

			if (showHappiness)
			{
				var happinessTopLeft = new Vector2(panelPos.X + 68f, statY + 10f);
				centers.Add(happinessTopLeft + new Vector2(44f, ButtonHeight) * 0.5f);
			}

			int npcType = player.sign != -1 ? -1 : Main.npc[player.talkNPC].type;
			bool useShop = !string.IsNullOrWhiteSpace(focusText);
			bool useExtra = !string.IsNullOrWhiteSpace(focusText2);

			List<object> addButtons = CollectAvailableAddButtons(npcType);
			int buttonCounts = addButtons.Count + (useShop ? 1 : 0) + (useExtra ? 1 : 0);
			if (buttonCounts == 0)
				return true;

			int buttonWidth = ButtonRowWidth / buttonCounts - ButtonSpacing;
			if (buttonWidth < 1)
				buttonWidth = 1;

			int offsetX = 0;
			Vector2 GetDrawPosition() => new(panelPos.X + MainRowOffsetX + offsetX, statY + 10f);

			if (useExtra)
			{
				Vector2 pos = GetDrawPosition();
				centers.Add(pos + new Vector2(buttonWidth, ButtonHeight) * 0.5f);
				offsetX += buttonWidth + ButtonSpacing;
			}

			if (useShop)
			{
				Vector2 pos = GetDrawPosition();
				centers.Add(pos + new Vector2(buttonWidth, ButtonHeight) * 0.5f);
				offsetX += buttonWidth + ButtonSpacing;
			}

			for (int i = 0; i < addButtons.Count; i++)
			{
				Vector2 pos = GetDrawPosition();
				centers.Add(pos + new Vector2(buttonWidth, ButtonHeight) * 0.5f);
				offsetX += buttonWidth + ButtonSpacing;
			}

			return true;
		}

		private static List<object> CollectAvailableAddButtons(int npcType)
		{
			var result = new List<object>();
			object raw;
			try
			{
				raw = _buttonInfosField.GetValue(null);
			}
			catch
			{
				return result;
			}

			if (raw is not IEnumerable enumerable)
				return result;

			foreach (object button in enumerable)
			{
				if (button == null)
					continue;

				if (!TryIsButtonAvailableForNpc(button, npcType))
					continue;

				result.Add(button);
			}

			return result;
		}

		private static bool TryIsButtonAvailableForNpc(object button, int npcType)
		{
			try
			{
				object npcTypesObj = _npcTypesField.GetValue(button);
				if (npcTypesObj is not IEnumerable npcTypes)
					return false;

				bool typeMatch = false;
				foreach (object t in npcTypes)
				{
					if (t is int id && id == npcType)
					{
						typeMatch = true;
						break;
					}
				}

				if (!typeMatch)
					return false;

				object availableObj = _availableField.GetValue(button);
				if (availableObj is Func<bool> available)
					return available();

				if (availableObj is Delegate d
					&& d.Method.ReturnType == typeof(bool)
					&& d.Method.GetParameters().Length == 0)
					return (bool)d.DynamicInvoke();

				return false;
			}
			catch
			{
				return false;
			}
		}

		private static bool TryGetPanelPosition(out Vector2 panelPos)
		{
			panelPos = default;
			try
			{
				object value = _panelPositionProperty.GetValue(null);
				if (value is Vector2 v)
				{
					panelPos = v;
					return true;
				}
			}
			catch
			{
				// fall through
			}

			return false;
		}

		private static void ApplyChain(List<Vector2> centers)
		{
			int count = centers.Count;
			ActiveCenters.Clear();
			ActivePointIds.Clear();

			if (count <= 0)
			{
				UpdateShortcuts(0);
				return;
			}

			if (!UILinkPointNavigator.Pages.TryGetValue(
				    DialogueTweakGamepadCompatNavLayout.PreferredPageId, out UILinkPage page))
				return;

			var pointIds = new int[count];
			DialogueTweakGamepadCompatNavLayout.AllocatePointIds(
				count,
				id => CanUsePointId(page, id),
				pointIds);

			if (DialogueTweakGamepadCompatNavLayout.HasDuplicatePointIds(count, pointIds))
			{
				DisableCompat("Duplicate UILink point ids were allocated.", null);
				return;
			}

			EnsurePoints(page, count, pointIds);

			var left = new int[count];
			var right = new int[count];
			DialogueTweakGamepadCompatNavLayout.BuildLinks(count, pointIds, left, right);

			for (int i = 0; i < count; i++)
			{
				int id = pointIds[i];
				UILinkPointNavigator.SetPosition(id, centers[i]);

				if (!UILinkPointNavigator.Points.TryGetValue(id, out UILinkPoint point))
					continue;

				point.Enabled = true;
				point.Left = left[i];
				point.Right = right[i];
				point.Up = -1;
				point.Down = -2;

				ActiveCenters.Add(centers[i]);
				ActivePointIds.Add(id);
			}

			UpdateShortcuts(count);
		}

		private static bool CanUsePointId(UILinkPage page, int id)
		{
			if (page.LinkMap.ContainsKey(id))
				return true;

			if (!UILinkPointNavigator.Points.TryGetValue(id, out UILinkPoint existing))
				return true;

			if (existing.Page == DialogueTweakGamepadCompatNavLayout.PreferredPageId)
				return true;

			return false;
		}

		private static void EnsurePoints(UILinkPage page, int count, int[] pointIds)
		{
			for (int i = 0; i < count; i++)
			{
				int id = pointIds[i];
				if (page.LinkMap.TryGetValue(id, out UILinkPoint existing))
				{
					if (!UILinkPointNavigator.Points.ContainsKey(id))
						UILinkPointNavigator.Points[id] = existing;
					continue;
				}

				if (UILinkPointNavigator.Points.TryGetValue(id, out UILinkPoint global)
					&& global.Page == DialogueTweakGamepadCompatNavLayout.PreferredPageId)
				{
					page.LinkMap[id] = global;
					continue;
				}

				UILinkPoint pageRef = null;
				if (page.LinkMap.TryGetValue(
					    DialogueTweakGamepadCompatNavLayout.PreferredBasePointId, out UILinkPoint basePt))
					pageRef = basePt;
				else
				{
					foreach (KeyValuePair<int, UILinkPoint> kv in page.LinkMap)
					{
						pageRef = kv.Value;
						break;
					}
				}

				UILinkPoint created;
				if (pageRef != null)
					created = ControlsFancyUiLinkRegistration.EnsurePointOnSamePage(id, pageRef);
				else
				{
					created = new UILinkPoint(id, enabled: true,
						left: DialogueTweakGamepadCompatNavLayout.NoLink,
						right: DialogueTweakGamepadCompatNavLayout.NoLink, up: -1, down: -2);
					created.SetPage(DialogueTweakGamepadCompatNavLayout.PreferredPageId);
					page.LinkMap[id] = created;
					UILinkPointNavigator.Points[id] = created;
				}

				if (created != null)
				{
					created.Left = DialogueTweakGamepadCompatNavLayout.NoLink;
					created.Right = DialogueTweakGamepadCompatNavLayout.NoLink;
					created.Up = -1;
					created.Down = -2;
				}
			}
		}

		private static void EnsurePageUpdateHooked()
		{
			if (_pageUpdateHooked || Main.dedServ)
				return;

			if (!UILinkPointNavigator.Pages.TryGetValue(
				    DialogueTweakGamepadCompatNavLayout.PreferredPageId, out UILinkPage page))
				return;

			_pageUpdateHandler = OnNpcChatPageUpdate;
			page.UpdateEvent += _pageUpdateHandler;
			_pageUpdateHooked = true;
		}

		private static void UnhookPageUpdate()
		{
			if (!_pageUpdateHooked || _pageUpdateHandler == null)
			{
				_pageUpdateHooked = false;
				_pageUpdateHandler = null;
				return;
			}

			try
			{
				if (UILinkPointNavigator.Pages.TryGetValue(
					    DialogueTweakGamepadCompatNavLayout.PreferredPageId, out UILinkPage page))
					page.UpdateEvent -= _pageUpdateHandler;
			}
			catch
			{
				// Best effort during unload / reload.
			}

			_pageUpdateHooked = false;
			_pageUpdateHandler = null;
		}

		private static void OnNpcChatPageUpdate()
		{
			if (_disabled || !_akOwnsWiring || ActivePointIds.Count == 0)
				return;

			try
			{
				Player player = Main.LocalPlayer;
				bool chatOpen = player != null
					&& (player.talkNPC != -1 || player.sign != -1)
					&& (!string.IsNullOrEmpty(Main.npcChatText) || player.sign != -1);
				if (!chatOpen)
				{
					ClearActive();
					return;
				}

				ReapplyActiveLinksAndPositions();
			}
			catch (Exception ex)
			{
				DisableCompat("NPC chat page UpdateEvent assist failed.", ex);
			}
		}

		private static void ReapplyActiveLinksAndPositions()
		{
			int count = ActivePointIds.Count;
			if (count == 0 || count != ActiveCenters.Count)
				return;

			if (!UILinkPointNavigator.Pages.TryGetValue(
				    DialogueTweakGamepadCompatNavLayout.PreferredPageId, out UILinkPage page))
				return;

			var pointIds = new int[count];
			for (int i = 0; i < count; i++)
				pointIds[i] = ActivePointIds[i];

			EnsurePoints(page, count, pointIds);

			var left = new int[count];
			var right = new int[count];
			DialogueTweakGamepadCompatNavLayout.BuildLinks(count, pointIds, left, right);

			for (int i = 0; i < count; i++)
			{
				int id = pointIds[i];
				UILinkPointNavigator.SetPosition(id, ActiveCenters[i]);
				if (!UILinkPointNavigator.Points.TryGetValue(id, out UILinkPoint point))
					continue;

				point.Enabled = true;
				point.Left = left[i];
				point.Right = right[i];
				point.Up = -1;
				point.Down = -2;
			}

			UpdateShortcuts(count);
		}

		private static void ClearActive()
		{
			ActiveCenters.Clear();
			ActivePointIds.Clear();
		}

		private static void ClearReflectionCache()
		{
			_buttonInfosField = null;
			_panelPositionProperty = null;
			_npcTypesField = null;
			_availableField = null;
		}

		private static void DisposeDrawButtonsHook()
		{
			if (_drawButtonsHook == null)
				return;

			try
			{
				_drawButtonsHook.Dispose();
			}
			catch
			{
				// Best effort.
			}

			_drawButtonsHook = null;
		}

		private static void UpdateShortcuts(int count)
		{
			UILinkPointNavigator.Shortcuts.NPCCHAT_ButtonsLeft = count >= 1;
			UILinkPointNavigator.Shortcuts.NPCCHAT_ButtonsMiddle = count >= 2;
			UILinkPointNavigator.Shortcuts.NPCCHAT_ButtonsRight = count >= 3;
			UILinkPointNavigator.Shortcuts.NPCCHAT_ButtonsRight2 = count >= 4;
		}

		private static void DisableCompat(string reason, Exception ex)
		{
			_disabled = true;
			_applied = false;
			_akOwnsWiring = false;
			_skipWiringUpstream = false;
			_skipLatchVisibleCount = -1;
			ClearActive();
			UnhookPageUpdate();
			DisposeDrawButtonsHook();
			ClearReflectionCache();

			if (_loggedDisableOnce)
				return;

			_loggedDisableOnce = true;
			try
			{
				string memberLine = string.IsNullOrEmpty(reason)
					? "A required DialogueTweak member was not found."
					: reason;
				string msg = "DialogueTweak compatibility disabled:\n"
					+ memberLine
					+ "\nCompat is off for this session; the game continues normally.";
				if (ex != null)
					msg += "\n(" + ex.GetType().Name + ": " + ex.Message + ")";

				AdvancedKeybinds.Instance?.Logger?.Warn(msg);

			}
			catch
			{
				// ignore logging failures
			}
		}

	}
}