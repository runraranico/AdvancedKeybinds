using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using AdvancedKeybinds.Binding;
using AdvancedKeybinds.Core;
using AdvancedKeybinds.Diagnostics;
using AdvancedKeybinds.UI;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.Audio;
using Terraria.GameInput;
using Terraria.ID;
using Terraria.UI;
using Terraria.UI.Gamepad;

namespace AdvancedKeybinds.Patches
{
	/// <summary>
	/// Controls-screen Pad:B hold → ControlsBindingUndo.TryUndo (no focus move),
	/// and AK Undo button UILinkPoint (2999) top-bar insertion + Pad:A confirm.
	/// </summary>
	internal static class ControlsPadBHoldCancelFocus
	{
		internal const int FocusCancelHoldMs = 1000;
		internal const int UndoCancelPointId = 2999;
		private const int ProfilePointId = 3003;
		private const int TopBarScanMin = 3000;
		private const int TopBarScanMax = 3005;

		private static long _padBHoldStartMs;
		private static bool _padBHoldTriggered;
		private static bool _padBWasDown;
		private static bool _padBHolding;
		private static bool _unavailableLogged;
		private static UIElement _cachedManageRoot;
		private static FieldInfo _buttonBackField;
		private static string _lastTopNavCatalogKey;
		private static bool _padAUndoWasDown;
		private static int _lastTopNavBeforePoint = int.MinValue;

		internal static void ClearRuntimeState()
		{
			_padBHoldStartMs = 0;
			_padBHoldTriggered = false;
			_padBWasDown = false;
			_padBHolding = false;
			_unavailableLogged = false;
			_cachedManageRoot = null;
			_lastTopNavCatalogKey = null;
			_padAUndoWasDown = false;
			_lastTopNavBeforePoint = int.MinValue;
		}

		internal static void AfterPlayerInputTick()
		{
			PollAkUndoPointConfirm();
			PollTopNavDiagnostics();

			bool down = FnaGamePadProvider.IsDown(GamePadToken.B);
			long now = Environment.TickCount64;
			bool rising = down && !_padBWasDown;
			bool falling = !down && _padBWasDown;

			// ComboPanel / Controls Pad:B gates — never wipe Inventory/MouseRight on Guide craft / world.
			if (ComboPanelCloseInputConsumeGate.ShouldBlockParentBack()
				|| ComboPanel.IsPadBackCloseGateActive())
			{
				bool allowSuppress = TerrariaCancelInputPolicy.ShouldSuppressPadCancelTriggersForParent(
					ComboPanel.IsOpen,
					manageControlsOpen: ControlsUiHost.IsManageControlsOpen(),
					inFancyUi: false,
					ingameOptions: Main.ingameOptionsWindow);

				if (_padBHolding)
					EndHold(triggered: false, elapsedMs: -1);

				if (allowSuppress)
					SuppressParentBackTriggers();
				else if (!ComboPanel.IsOpen)
					ComboPanel.ForceClearParentBackGatesOutsideModal();

				_padBWasDown = down;
				return;
			}

			if (ComboPanel.IsOpen)
			{
				if (_padBHolding)
					EndHold(triggered: false, elapsedMs: -1);
				_padBWasDown = down;
				return;
			}

			if (!ControlsUiHost.IsManageControlsOpen())
			{
				if (_padBHolding)
					EndHold(triggered: false, elapsedMs: -1);
				_padBWasDown = down;
				return;
			}

			if (rising)
			{
				_padBHolding = true;
				_padBHoldTriggered = false;
				_padBHoldStartMs = now;
				_unavailableLogged = false;
				ControlsUiHost.TryGetManageControls(out _, out string host);
				GamePadProbeSystem.LogControlPadBHoldBegin(FocusCancelHoldMs, host ?? "?");
				SuppressParentBackTriggers();
			}

			if (_padBHolding && down)
			{
				SuppressParentBackTriggers();

				if (!_padBHoldTriggered)
				{
					long elapsed = now - _padBHoldStartMs;
					if (elapsed >= FocusCancelHoldMs)
					{
						_padBHoldTriggered = true;
						bool undone = KeybindingUiPatches.TryExecuteControlsUndo("PadBHold");
						if (undone)
						{
							try { SoundEngine.PlaySound(SoundID.MenuTick); }
							catch { /* ignore */ }
							GamePadProbeSystem.LogControlPadBHoldTrigger(elapsed, "AkUndoDirect", UndoCancelPointId);
						}
						else
						{
							if (!_unavailableLogged)
							{
								_unavailableLogged = true;
								GamePadProbeSystem.LogControlPadBHoldUnavailable("UndoUnavailable");
							}
						}
					}
				}
			}

			if (falling && _padBHolding)
			{
				long elapsed = now - _padBHoldStartMs;
				bool triggered = _padBHoldTriggered;
				EndHold(triggered, elapsed);

				if (!triggered)
					TryPerformFormalOneStepBack();
				else
					SuppressParentBackTriggers();
			}

			_padBWasDown = down;
		}

		internal static void BeforeManageControlsUpdate()
		{
			if (_padBHolding && FnaGamePadProvider.IsDown(GamePadToken.B))
				SuppressParentBackTriggers();
			if (ComboPanelCloseInputConsumeGate.ShouldBlockParentBack())
				SuppressParentBackTriggers();

			// Keep AkUndo focus after SetupGamepadPoints / rebuild within the same frame.
			if (UILinkPointNavigator.CurrentPoint == UndoCancelPointId)
				ReassertUndoPointPositionOnly();
		}

		/// <summary>One-step Back via Controls _buttonBack (works for MenuUI and InGameUI).</summary>
		internal static bool TryPerformFormalOneStepBack()
		{
			if (ComboPanel.IsOpen
				|| ComboPanelCloseInputConsumeGate.ShouldBlockParentBack()
				|| ComboPanel.IsPadBackCloseGateActive())
			{
				ComboPanelCloseInputConsumeGate.LogDuplicateBackBlocked(
					"CloseComboPanel",
					"CloseParentControls");
				return false;
			}

			UIElement root = _cachedManageRoot;
			if (root == null && ControlsUiHost.TryGetManageControls(out UIState found, out _))
				root = found;
			if (root == null)
				return false;

			_buttonBackField ??= root.GetType().GetField(
				"_buttonBack",
				BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
			if (_buttonBackField?.GetValue(root) is not UIElement back)
				return false;

			try
			{
				var evt = new UIMouseEvent(back, back.GetDimensions().Center());
				back.LeftClick(evt);
				return true;
			}
			catch
			{
				return false;
			}
		}

		/// <summary>
		/// Insert AkUndo (2999) into the top horizontal chain between the left neighbor of Profile
		/// and Profile (Custom). Vanilla SetupGamepadPoints leaves 三本線.Right → Profile; we splice Undo in.
		/// </summary>
		internal static void EnsureUndoLinkPoint(UIElement manageRoot)
		{
			if (manageRoot == null)
				return;
			_cachedManageRoot = manageRoot;

			UIElement undo = KeybindingUiPatches.TryGetControlsUndoButton(manageRoot);
			if (undo == null)
			{
				KeybindingUiPatches.EnsureControlsUndoButton(manageRoot);
				undo = KeybindingUiPatches.TryGetControlsUndoButton(manageRoot);
			}
			if (undo == null)
				return;

			if (!UILinkPointNavigator.Points.TryGetValue(ProfilePointId, out UILinkPoint profile))
				return;

			// Root cause of 三本線→P0→GamepadTab: new UILinkPoint defaults Page=0 and is absent from
			// FancyUI LinkMap. ChangePoint(2999) switches CurrentPage to 0; ProcessChanges then lands
			// on page-0 DefaultPoint (0) and the next validity pass restores FancyUI (often GamepadTab).
			UILinkPoint undoPoint = ControlsFancyUiLinkRegistration.EnsurePointOnSamePage(
				UndoCancelPointId,
				profile);

			CalculatedStyle d = default;
			bool haveDims = false;
			try
			{
				d = undo.GetInnerDimensions();
				if (d.Width > 1f && d.Height > 1f)
				{
					UILinkPointNavigator.SetPosition(
						UndoCancelPointId,
						new Vector2(d.X + d.Width * 0.5f, d.Y + d.Height * 0.5f));
					haveDims = true;
				}
			}
			catch
			{
				haveDims = false;
			}

			int leftNeighborId = FindTopBarLeftNeighborOfProfile();
			undoPoint.Unlink();

			UILinkPoint leftNeighbor = null;
			if (leftNeighborId >= 0
				&& UILinkPointNavigator.Points.TryGetValue(leftNeighborId, out leftNeighbor))
			{
				leftNeighbor.Right = UndoCancelPointId;
				undoPoint.Left = leftNeighborId;
			}
			else
			{
				undoPoint.Left = -1;
			}

			undoPoint.Right = ProfilePointId;
			profile.Left = UndoCancelPointId;
			undoPoint.Down = ControlsGamepadAutoScrollPatches.ListPointsStartPublic;
			undoPoint.Up = -1;

			// Prefer official dimensions; if layout is not ready, place between neighboring Points
			// (already UIScale-adjusted) so spatial highlight matches the top bar.
			if (!haveDims)
			{
				if (leftNeighbor != null)
				{
					undoPoint.Position = new Vector2(
						(leftNeighbor.Position.X + profile.Position.X) * 0.5f,
						profile.Position.Y);
				}
				else
				{
					undoPoint.Position = profile.Position;
				}
			}

			if (!haveDims)
			{
				d = new CalculatedStyle(
					undoPoint.Position.X,
					undoPoint.Position.Y,
					1f,
					1f);
			}

			LogTopNavCatalogIfChanged(manageRoot, undo, d, leftNeighborId);
		}

		/// <summary>
		/// Pad:A while CurrentPoint is AkUndo → shared TryUndo once; consume MouseLeft; keep focus.
		/// </summary>
		private static void PollAkUndoPointConfirm()
		{
			bool aDown = FnaGamePadProvider.IsDown(GamePadToken.A);
			bool aRising = FnaGamePadProvider.WasJustPressed(GamePadToken.A) || (aDown && !_padAUndoWasDown);
			_padAUndoWasDown = aDown;

			if (!aRising)
				return;
			if (ComboPanel.IsOpen || !ControlsUiHost.IsManageControlsOpen())
				return;
			if (UILinkPointNavigator.CurrentPoint != UndoCancelPointId)
				return;

			GamePadProbeSystem.LogControlTopNavConfirm(
				"AkUndo",
				UndoCancelPointId,
				GamePadToken.A,
				invoked: true,
				executedOnce: false);

			bool executed = KeybindingUiPatches.TryExecuteControlsUndo("TopNavPadA");
			if (executed)
			{
				try { SoundEngine.PlaySound(SoundID.MenuTick); }
				catch { /* ignore */ }
			}

			// Do not let the same Pad:A synthesize a parent row / Profile click.
			ClearTrigger(PlayerInput.Triggers?.JustPressed, "MouseLeft");
			ClearTrigger(PlayerInput.Triggers?.Current, "MouseLeft");

			if (UILinkPointNavigator.CurrentPoint != UndoCancelPointId)
				UILinkPointNavigator.ChangePoint(UndoCancelPointId);

			GamePadProbeSystem.LogControlTopNavConfirm(
				"AkUndo",
				UndoCancelPointId,
				GamePadToken.A,
				invoked: true,
				executedOnce: executed);
			GamePadProbeSystem.LogControlTopNavFocus("AkUndo", UndoCancelPointId, highlighted: true);
		}

		private static void PollTopNavDiagnostics()
		{
			if (!ControlsUiHost.IsManageControlsOpen() || ComboPanel.IsOpen)
				return;

			bool left = FnaGamePadProvider.WasJustPressed(GamePadToken.DPadLeft);
			bool right = FnaGamePadProvider.WasJustPressed(GamePadToken.DPadRight);
			if (!left && !right)
			{
				_lastTopNavBeforePoint = UILinkPointNavigator.CurrentPoint;
				return;
			}

			int before = _lastTopNavBeforePoint != int.MinValue
				? _lastTopNavBeforePoint
				: UILinkPointNavigator.CurrentPoint;
			string token = right ? GamePadToken.DPadRight : GamePadToken.DPadLeft;
			string beforeId = FormatTopStable(before);
			string expected = right
				? (before == FindTopBarLeftNeighborOfProfile() ? "AkUndo"
					: before == UndoCancelPointId ? "Profile"
					: "?")
				: (before == ProfilePointId ? "AkUndo"
					: before == UndoCancelPointId ? FormatTopStable(FindTopBarLeftNeighborOfProfile())
					: "?");

			GamePadProbeSystem.LogControlTopNavInput(
				token,
				before,
				beforeId,
				expected);

			int epoch = FnaGamePadProvider.InputTickEpoch;
			GamePadProbeSystem.LogControlTopNavChange(
				epoch,
				sequence: 0,
				caller: "PollTopNavEdge",
				from: before,
				to: before,
				reason: "InputEdge");

			// After navigator processes this tick; sample CurrentPoint at end of tick via deferred read.
			// Log result next line using current value (ChangePoint already applied in same UpdateInput).
			int after = UILinkPointNavigator.CurrentPoint;
			string afterId = FormatTopStable(after);
			bool moved = after != before;
			bool skipped = right
				&& before == FindTopBarLeftNeighborOfProfile()
				&& after == ProfilePointId
				&& after != UndoCancelPointId;

			GamePadProbeSystem.LogControlTopNavResult(before, after, afterId, moved, skipped);
			if (skipped)
			{
				GamePadProbeSystem.LogControlTopNavSkip(
					"AkUndo",
					"BadLink");
			}
			else if (after == UndoCancelPointId)
			{
				GamePadProbeSystem.LogControlTopNavFocus("AkUndo", UndoCancelPointId, highlighted: true);
			}

			_lastTopNavBeforePoint = after;
		}

		private static void ReassertUndoPointPositionOnly()
		{
			UIElement root = _cachedManageRoot;
			if (root == null)
				return;
			UIElement undo = KeybindingUiPatches.TryGetControlsUndoButton(root);
			if (undo == null)
				return;
			if (!UILinkPointNavigator.Points.TryGetValue(ProfilePointId, out UILinkPoint profile))
				return;

			ControlsFancyUiLinkRegistration.EnsurePointOnSamePage(UndoCancelPointId, profile);

			try
			{
				CalculatedStyle d = undo.GetInnerDimensions();
				if (d.Width <= 1f || d.Height <= 1f)
					d = undo.GetDimensions();
				if (d.Width <= 1f || d.Height <= 1f)
					return;
				UILinkPointNavigator.SetPosition(
					UndoCancelPointId,
					new Vector2(d.X + d.Width * 0.5f, d.Y + d.Height * 0.5f));
			}
			catch
			{
				// ignore
			}
		}

		/// <summary>
		/// Point in 3000–3005 whose Right currently targets Profile (vanilla chain before splice).
		/// </summary>
		private static int FindTopBarLeftNeighborOfProfile()
		{
			for (int id = TopBarScanMin; id <= TopBarScanMax; id++)
			{
				if (id == ProfilePointId)
					continue;
				if (!UILinkPointNavigator.Points.TryGetValue(id, out UILinkPoint p))
					continue;
				if (p.Right == ProfilePointId || p.Right == UndoCancelPointId)
					return id;
			}

			return -1;
		}

		private static void LogTopNavCatalogIfChanged(
			UIElement manageRoot,
			UIElement undoEl,
			CalculatedStyle undoDims,
			int leftNeighborId)
		{
			var parts = new List<string>(8);
			for (int id = TopBarScanMin; id <= TopBarScanMax; id++)
			{
				if (!UILinkPointNavigator.Points.TryGetValue(id, out UILinkPoint p))
					continue;
				parts.Add(id.ToString(CultureInfo.InvariantCulture)
					+ ">" + p.Left + "/" + p.Right);
			}

			if (UILinkPointNavigator.Points.TryGetValue(UndoCancelPointId, out UILinkPoint undoPt))
				parts.Add("2999>" + undoPt.Left + "/" + undoPt.Right);

			string key = string.Join("|", parts);
			if (string.Equals(key, _lastTopNavCatalogKey, StringComparison.Ordinal))
				return;
			_lastTopNavCatalogKey = key;

			int index = 0;
			EmitTopElement(index++, "KeyboardTab", 3001);
			EmitTopElement(index++, "GamepadTab", 3002);
			EmitTopElement(index++, "P3004", 3004);
			EmitTopElement(index++, "P3005", 3005);

			string undoType = undoEl?.GetType().Name ?? "?";
			bool undoVis = undoEl != null && undoDims.Width > 1f && undoDims.Height > 1f;
			int uLeft = undoPt?.Left ?? -1;
			int uRight = undoPt?.Right ?? -1;
			GamePadProbeSystem.LogControlTopNavElement(
				index++,
				"AkUndo",
				undoType,
				snapName: "-",
				snapId: -1,
				pointId: UndoCancelPointId,
				position: new Vector2(undoDims.X + undoDims.Width * 0.5f, undoDims.Y + undoDims.Height * 0.5f),
				visible: undoVis,
				navigable: undoVis && uRight == ProfilePointId && (leftNeighborId < 0 || uLeft == leftNeighborId),
				left: uLeft,
				right: uRight);

			EmitTopElement(index, "Profile", ProfilePointId);
			_ = manageRoot;
		}

		private static void EmitTopElement(int index, string stableId, int pointId)
		{
			if (!UILinkPointNavigator.Points.TryGetValue(pointId, out UILinkPoint p))
			{
				GamePadProbeSystem.LogControlTopNavElement(
					index, stableId, elementType: "-", snapName: "-", snapId: -1,
					pointId: pointId, position: Vector2.Zero, visible: false, navigable: false,
					left: -1, right: -1);
				return;
			}

			GamePadProbeSystem.LogControlTopNavElement(
				index,
				stableId,
				elementType: "UILinkPoint",
				snapName: "-",
				snapId: -1,
				pointId: pointId,
				position: p.Position,
				visible: true,
				navigable: true,
				left: p.Left,
				right: p.Right);
		}

		private static string FormatTopStable(int id)
		{
			if (id == UndoCancelPointId)
				return "AkUndo";
			if (id == 3000)
				return "Back";
			if (id == 3001)
				return "KeyboardTab";
			if (id == 3002)
				return "GamepadTab";
			if (id == 3003)
				return "Profile";
			if (id == 3004)
				return "P3004";
			if (id == 3005)
				return "P3005";
			return "P" + id.ToString(CultureInfo.InvariantCulture);
		}

		private static void EndHold(bool triggered, long elapsedMs)
		{
			if (_padBHolding)
			{
				if (triggered)
					GamePadProbeSystem.LogControlPadBHoldEnd(triggered: true, elapsedMs: null);
				else
					GamePadProbeSystem.LogControlPadBHoldEnd(triggered: false, elapsedMs: elapsedMs);
			}

			_padBHolding = false;
			_padBHoldTriggered = false;
			_padBHoldStartMs = 0;
		}

		private static void SuppressParentBackTriggers()
		{
			try
			{
				var cleared = new List<string>(4);
				GamePadParentUiTriggerSuppress.ClearParentNavTriggersMappedToPad(
					PlayerInput.Triggers?.JustPressed,
					GamePadToken.B,
					cleared);
				GamePadParentUiTriggerSuppress.ClearParentNavTriggersMappedToPad(
					PlayerInput.Triggers?.Current,
					GamePadToken.B,
					cleared);
				ClearTrigger(PlayerInput.Triggers?.JustPressed, "Inventory");
				ClearTrigger(PlayerInput.Triggers?.Current, "Inventory");
				ClearTrigger(PlayerInput.Triggers?.JustPressed, "MenuInquiry");
				ClearTrigger(PlayerInput.Triggers?.Current, "MenuInquiry");
				ClearTrigger(PlayerInput.Triggers?.JustPressed, "MouseRight");
				ClearTrigger(PlayerInput.Triggers?.Current, "MouseRight");
			}
			catch
			{
				// ignore
			}
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
