using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AdvancedKeybinds.Binding;
using AdvancedKeybinds.Core;
using AdvancedKeybinds.Patches;
using AdvancedKeybinds.UI;
using Terraria;
using Terraria.GameInput;
using Terraria.ModLoader;
using Terraria.UI.Gamepad;

namespace AdvancedKeybinds.Diagnostics
{
	/// <summary>
	/// TEMPORARY read-only session log: ComboPanel Save → list return → Pad:A direct open.
	/// Product path is DirectPadAOpen (no MouseLeft). Remove after in-game confirmation.
	/// </summary>
	internal sealed class PadConfirmAfterComboSaveDiagSystem : ModSystem
	{
		internal const bool Enabled = true;

		private const string Folder =
			@"C:\Users\runra\Documents\My Games\Terraria\tModLoader\deck log\GamePad";
		private const string LiveFileName = "AK_PadConfirmAfterComboSave_Diag_LIVE.txt";
		private const int MaxLines = 4000;
		private const int SessionIdleEndEpochs = 90;

		private static readonly object Gate = new();
		private static readonly StringBuilder Line = new(2048);
		private static readonly List<string> ItemUseScratch = new(4);

		private static string _historyPath;
		private static int _written;
		private static bool _filesInitialized;
		private static bool _broken;
		private static bool _reentry;
		private static bool _sessionActive;
		private static int _sessionStartEpoch = int.MinValue;
		private static int _lastEventEpoch = int.MinValue;
		private static int _lastPoint = int.MinValue;
		private static int _lastPolledEpoch = int.MinValue;
		private static bool _prevPadADown;
		private static bool _prevPending;
		private static bool _prevCloseGate;
		private static bool _prevPanelOpen;
		private static bool _loggedReturnedToList;

		public override void PostUpdateInput()
		{
			PollTick("postUpdateInput");
		}

		public override void Unload() => Reset();

		internal static void Reset()
		{
			lock (Gate)
			{
				FinalizeHistoryUnlocked();
				_written = 0;
				_filesInitialized = false;
				_broken = false;
				_reentry = false;
				_sessionActive = false;
				_sessionStartEpoch = int.MinValue;
				_lastEventEpoch = int.MinValue;
				_lastPoint = int.MinValue;
				_lastPolledEpoch = int.MinValue;
				_prevPadADown = false;
				_prevPending = false;
				_prevCloseGate = false;
				_prevPanelOpen = false;
				_loggedReturnedToList = false;
				_historyPath = null;
				ItemUseScratch.Clear();
			}
		}

		internal static void ObserveBeforeComboSave(string actionId, string focus)
		{
			if (!Enabled || _broken)
				return;
			BeginSession();
			WriteNamed(
				PadConfirmAfterComboSavePolicy.EventBeforeComboSave,
				"action=" + (actionId ?? "?")
				+ " focus=" + (focus ?? "?")
				+ " point=" + UILinkPointNavigator.CurrentPoint);
		}

		internal static void ObserveComboSaveAccepted(string actionId, int epoch)
		{
			if (!Enabled || _broken)
				return;
			BeginSession();
			WriteNamed(
				PadConfirmAfterComboSavePolicy.EventComboSaveAccepted,
				"action=" + (actionId ?? "?")
				+ " epoch=" + epoch
				+ " closeGate=" + (ComboPanelCloseInputConsumeGate.IsActive ? 1 : 0)
				+ " closeTok=" + (ComboPanelCloseInputConsumeGate.ActiveToken ?? "-")
				+ " closeReason=" + (ComboPanelCloseInputConsumeGate.ActiveReason ?? "-"));
		}

		internal static void ObserveAfterComboSave(bool panelStillOpen, int parentPoint)
		{
			if (!Enabled || _broken)
				return;
			BeginSession();
			WriteNamed(
				PadConfirmAfterComboSavePolicy.EventAfterComboSave,
				"panelOpen=" + (panelStillOpen ? 1 : 0)
				+ " parentPoint=" + parentPoint
				+ " saveEpochGate=" + (ComboPanel.ShouldBlockOpenAfterSaveClose() ? 1 : 0)
				+ " closeGate=" + (ComboPanelCloseInputConsumeGate.IsActive ? 1 : 0)
				+ " captureGate=" + (ComboPanelGamePadNavigation.CaptureReleaseGateActiveRaw ? 1 : 0)
				+ " openGate=" + (ComboPanelGamePadNavigation.IsOpenGateActive ? 1 : 0));
		}

		internal static void ObservePendingArm(string actionId, bool armed, string denyReason)
		{
			if (!Enabled || _broken || !_sessionActive)
				return;
			if (armed)
			{
				WriteNamed(
					PadConfirmAfterComboSavePolicy.EventPadAObserved,
					"pendingArm=1 action=" + (actionId ?? "?")
					+ " " + SnapshotPadAndItemUse()
					+ " classify=" + ClassifyNow(padAJustPressed: true, pendingActive: true, mouseLeftClick: false));
			}
			else if (!string.IsNullOrEmpty(denyReason))
			{
				WriteNamed(
					PadConfirmAfterComboSavePolicy.EventPadAConfirmBlocked,
					"blockedBy=" + denyReason
					+ " pendingArm=0 action=" + (actionId ?? "?")
					+ " " + SnapshotPadAndItemUse());
			}
		}

		internal static void ObserveOpenIntent(
			string actionId,
			bool accepted,
			string source,
			string blockReason,
			bool openedByGamePad)
		{
			if (!Enabled || _broken || !_sessionActive)
				return;

			if (accepted)
			{
				WriteNamed(
					PadConfirmAfterComboSavePolicy.EventPadAConfirmAccepted,
					"action=" + (actionId ?? "?")
					+ " source=" + (source ?? "?")
					+ " byPad=" + (openedByGamePad ? 1 : 0)
					+ " " + SnapshotPadAndItemUse());
			}
			else
			{
				string reason = string.IsNullOrEmpty(blockReason) ? "Unknown" : blockReason;
				WriteNamed(
					PadConfirmAfterComboSavePolicy.EventPadAConfirmBlocked,
					"blockedBy=" + reason
					+ " source=" + (source ?? "?")
					+ " action=" + (actionId ?? "?")
					+ " " + SnapshotPadAndItemUse()
					+ " classify=" + ClassifyNow(
						FnaGamePadProvider.WasJustPressed(GamePadToken.A),
						PendingGamePadRowConfirm.IsActive,
						mouseLeftClick: true));
			}
		}

		internal static void ObserveAfterVanillaInput()
		{
			PollTick("afterOrig");
		}

		private static void PollTick(string source)
		{
			if (!Enabled || _broken || _reentry)
				return;
			try
			{
				_reentry = true;
				int epoch = FnaGamePadProvider.InputTickEpoch;
				if (_lastPolledEpoch == epoch)
					return;
				_lastPolledEpoch = epoch;

				if (!_sessionActive)
					return;

				if (epoch - _lastEventEpoch > SessionIdleEndEpochs)
				{
					EndSession("idle");
					return;
				}

				bool panelOpen = ComboPanel.IsOpen;
				if (_prevPanelOpen && !panelOpen && !_loggedReturnedToList)
				{
					_loggedReturnedToList = true;
					WriteNamed(
						PadConfirmAfterComboSavePolicy.EventReturnedToKeybindList,
						"point=" + UILinkPointNavigator.CurrentPoint
						+ " " + SnapshotPadAndItemUse());
				}
				_prevPanelOpen = panelOpen;

				int point = UILinkPointNavigator.CurrentPoint;
				if (point != _lastPoint && _lastPoint != int.MinValue)
				{
					WriteNamed(
						PadConfirmAfterComboSavePolicy.EventFocusChanged,
						"oldPoint=" + _lastPoint + " newPoint=" + point);
				}
				_lastPoint = point;

				bool closeGate = ComboPanelCloseInputConsumeGate.ShouldBlockParentKeybindRowOpen();
				if (closeGate != _prevCloseGate)
				{
					WriteNamed(
						closeGate
							? PadConfirmAfterComboSavePolicy.EventSuppressionEntered
							: PadConfirmAfterComboSavePolicy.EventSuppressionReleased,
						"kind=CloseInputConsumeGate"
						+ " tok=" + (ComboPanelCloseInputConsumeGate.ActiveToken ?? "-")
						+ " reason=" + (ComboPanelCloseInputConsumeGate.ActiveReason ?? "-"));
					_prevCloseGate = closeGate;
				}

				bool aDown = FnaGamePadProvider.IsDown(GamePadToken.A);
				bool aPressed = FnaGamePadProvider.WasJustPressed(GamePadToken.A);
				bool aReleased = FnaGamePadProvider.WasJustReleased(GamePadToken.A);
				bool xPressed = FnaGamePadProvider.WasJustPressed(GamePadToken.X);
				bool r2Pressed = FnaGamePadProvider.WasJustPressed(GamePadToken.RightTrigger);
				bool pending = PendingGamePadRowConfirm.IsActive;

				if (aPressed || aReleased || xPressed || r2Pressed || pending != _prevPending)
				{
					FillItemUseScratch();
					bool mouseLeftWould = false;
					if (aPressed)
						mouseLeftWould = PadConfirmAfterComboSavePolicy.WouldItemUseOrRaiseMouseLeft(ItemUseScratch, GamePadToken.A);
					else if (xPressed)
						mouseLeftWould = PadConfirmAfterComboSavePolicy.WouldItemUseOrRaiseMouseLeft(ItemUseScratch, GamePadToken.X);
					else if (r2Pressed)
						mouseLeftWould = PadConfirmAfterComboSavePolicy.WouldItemUseOrRaiseMouseLeft(ItemUseScratch, GamePadToken.RightTrigger);

					bool trigMl = TriggerOn(PlayerInput.Triggers?.JustPressed, "MouseLeft")
						|| TriggerOn(PlayerInput.Triggers?.Current, "MouseLeft");

					if (aPressed)
					{
						string classify = PadConfirmAfterComboSavePolicy.ClassifyOpenAttempt(
							closeGate,
							ComboPanel.ShouldBlockOpenAfterSaveClose(),
							padAJustPressed: true,
							pendingActive: pending || _prevPending,
							mouseLeftClickArrived: trigMl || mouseLeftWould,
							focusIsBindingRow: ControlsGamepadAutoScrollPatches.TryGetFocusedBindingRow(out _, out _, out _, out _),
							comboPanelAlreadyOpen: panelOpen);

						WriteNamed(
							PadConfirmAfterComboSavePolicy.EventPadAObserved,
							"src=" + source
							+ " A=" + Face(aDown, aPressed, aReleased)
							+ " Xjp=" + (xPressed ? 1 : 0)
							+ " R2jp=" + (r2Pressed ? 1 : 0)
							+ " pending=" + (pending ? 1 : 0)
							+ " mouseLeftTrig=" + (trigMl ? 1 : 0)
							+ " itemUseWouldMl=" + (mouseLeftWould ? 1 : 0)
							+ " classify=" + classify
							+ " " + SnapshotPadAndItemUse());

						if (classify.StartsWith("blockedBy=", StringComparison.Ordinal))
						{
							WriteNamed(
								PadConfirmAfterComboSavePolicy.EventPadAConfirmBlocked,
								classify + " " + SnapshotPadAndItemUse());
						}
					}
					if ((xPressed || r2Pressed) && pending)
					{
						bool canStale = PadConfirmAfterComboSavePolicy.CanOpenViaStalePendingAndItemUseMouseLeft(
							pending,
							trigMl || mouseLeftWould);
						WriteNamed(
							"ItemUsePadWhilePending",
							"Xjp=" + (xPressed ? 1 : 0)
							+ " R2jp=" + (r2Pressed ? 1 : 0)
							+ " pending=1"
							+ " canOpenViaStalePending=" + (canStale ? 1 : 0)
							+ " mouseLeftTrig=" + (trigMl ? 1 : 0)
							+ " " + SnapshotPadAndItemUse());
						if (canStale && !panelOpen)
						{
							WriteNamed(
								PadConfirmAfterComboSavePolicy.EventUnexpectedComboPanelReopened,
								"via=StalePendingPlusItemUseMouseLeft " + SnapshotPadAndItemUse());
						}
					}
				}

				_prevPadADown = aDown;
				_prevPending = pending;
			}
			catch
			{
				_broken = true;
			}
			finally
			{
				_reentry = false;
			}
		}

		private static string Face(bool down, bool pressed, bool released)
		{
			if (pressed) return "JP";
			if (released) return "JR";
			if (down) return "DN";
			return "--";
		}

		private static string ClassifyNow(bool padAJustPressed, bool pendingActive, bool mouseLeftClick)
		{
			return PadConfirmAfterComboSavePolicy.ClassifyOpenAttempt(
				ComboPanelCloseInputConsumeGate.ShouldBlockParentKeybindRowOpen(),
				ComboPanel.ShouldBlockOpenAfterSaveClose(),
				padAJustPressed,
				pendingActive,
				mouseLeftClick,
				ControlsGamepadAutoScrollPatches.TryGetFocusedBindingRow(out _, out _, out _, out _),
				ComboPanel.IsOpen);
		}

		private static void FillItemUseScratch()
		{
			ItemUseScratch.Clear();
			try
			{
				string key = ControlsEditTargetPolicy.ToStorageKey(
					ControlsEditTargetPolicy.DeviceKind.GamePad,
					"MouseLeft");
				ComboBinding combo = BindingStorage.Get(key);
				if (combo == null || combo.IsEmpty || combo.FilledSlotCount < 1)
					return;
				PadConfirmAfterComboSavePolicy.CollectItemUseStyleTokens(
					combo.Keys[0],
					combo.HasAlternateGamePad ? combo.AlternateGamePadToken : null,
					ItemUseScratch);
			}
			catch
			{
			}
		}

		private static string SnapshotPadAndItemUse()
		{
			FillItemUseScratch();
			bool a = FnaGamePadProvider.IsDown(GamePadToken.A);
			bool b = FnaGamePadProvider.IsDown(GamePadToken.B);
			bool x = FnaGamePadProvider.IsDown(GamePadToken.X);
			bool y = FnaGamePadProvider.IsDown(GamePadToken.Y);
			bool l2 = FnaGamePadProvider.IsDown(GamePadToken.LeftTrigger);
			bool r2 = FnaGamePadProvider.IsDown(GamePadToken.RightTrigger);
			return "mode=" + PlayerInput.CurrentInputMode
				+ " usingPad=" + (PlayerInput.UsingGamepad ? 1 : 0)
				+ " usingPadUI=" + (PlayerInput.UsingGamepadUI ? 1 : 0)
				+ " sub=" + (FnaGamePadProvider.IsSubsystemActive ? 1 : 0)
				+ " conn=" + (FnaGamePadProvider.IsConnected ? 1 : 0)
				+ " A=" + (a ? 1 : 0) + "B=" + (b ? 1 : 0) + "X=" + (x ? 1 : 0) + "Y=" + (y ? 1 : 0)
				+ " L2=" + (l2 ? 1 : 0) + "R2=" + (r2 ? 1 : 0)
				+ " Ajp=" + (FnaGamePadProvider.WasJustPressed(GamePadToken.A) ? 1 : 0)
				+ " Xjp=" + (FnaGamePadProvider.WasJustPressed(GamePadToken.X) ? 1 : 0)
				+ " R2jp=" + (FnaGamePadProvider.WasJustPressed(GamePadToken.RightTrigger) ? 1 : 0)
				+ " uiConfirmTok=" + AkUiPadNavigationPolicy.ConfirmToken
				+ " itemUse=[" + string.Join(",", ItemUseScratch) + "]"
				+ " mlCur=" + (TriggerOn(PlayerInput.Triggers?.Current, "MouseLeft") ? 1 : 0)
				+ " mlJp=" + (TriggerOn(PlayerInput.Triggers?.JustPressed, "MouseLeft") ? 1 : 0)
				+ " jumpJp=" + (TriggerOn(PlayerInput.Triggers?.JustPressed, "Jump") ? 1 : 0)
				+ " mainML=" + (Main.mouseLeft ? 1 : 0)
				+ " point=" + UILinkPointNavigator.CurrentPoint
				+ " panel=" + (ComboPanel.IsOpen ? 1 : 0)
				+ " pending=" + (PendingGamePadRowConfirm.IsActive ? 1 : 0)
				+ " closeGate=" + (ComboPanelCloseInputConsumeGate.IsActive ? 1 : 0)
				+ " captureGate=" + (ComboPanelGamePadNavigation.CaptureReleaseGateActiveRaw ? 1 : 0)
				+ " openGate=" + (ComboPanelGamePadNavigation.IsOpenGateActive ? 1 : 0)
				+ " saveEpochGate=" + (ComboPanel.ShouldBlockOpenAfterSaveClose() ? 1 : 0);
		}

		private static bool TriggerOn(TriggersSet set, string name)
		{
			try
			{
				return set?.KeyStatus != null
					&& set.KeyStatus.TryGetValue(name, out bool v)
					&& v;
			}
			catch
			{
				return false;
			}
		}

		private static void BeginSession()
		{
			_sessionActive = true;
			_sessionStartEpoch = FnaGamePadProvider.InputTickEpoch;
			_lastEventEpoch = _sessionStartEpoch;
			_loggedReturnedToList = false;
			_lastPoint = UILinkPointNavigator.CurrentPoint;
			_prevPanelOpen = ComboPanel.IsOpen;
			_prevCloseGate = ComboPanelCloseInputConsumeGate.ShouldBlockParentKeybindRowOpen();
			_prevPending = PendingGamePadRowConfirm.IsActive;
			EnsureFiles();
		}

		private static void EndSession(string why)
		{
			if (!_sessionActive)
				return;
			WriteNamed("SessionEnd", "why=" + why);
			_sessionActive = false;
		}

		private static void WriteNamed(string eventName, string detail)
		{
			EnsureFiles();
			_lastEventEpoch = FnaGamePadProvider.InputTickEpoch;
			Line.Clear();
			Line.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
			Line.Append(" frame=").Append(Main.GameUpdateCount);
			Line.Append(" epoch=").Append(FnaGamePadProvider.InputTickEpoch);
			Line.Append(" evt=").Append(eventName);
			Line.Append(' ').Append(detail ?? string.Empty);
			Line.AppendLine();
			string text = Line.ToString();
			lock (Gate)
			{
				if (_written >= MaxLines)
					return;
				AppendUnlocked(text);
				_written++;
			}
		}

		private static void EnsureFiles()
		{
			if (_filesInitialized)
				return;
			Directory.CreateDirectory(Folder);
			string live = Path.Combine(Folder, LiveFileName);
			_historyPath = Path.Combine(
				Folder,
				"AK_PadConfirmAfterComboSave_Diag_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt");
			string header =
				"# AK PadConfirmAfterComboSave TEMP diag (read-only). Session: Save → list → Pad:A.\r\n"
				+ "# Hypothesis: ItemUse remapped off Pad:A ⇒ MouseLeft OnClick missing while Pending arms.\r\n"
				+ "# started=" + DateTime.Now.ToString("o") + "\r\n";
			File.WriteAllText(live, header);
			File.WriteAllText(_historyPath, header);
			_filesInitialized = true;
			_written = 0;
		}

		private static void AppendUnlocked(string text)
		{
			string live = Path.Combine(Folder, LiveFileName);
			File.AppendAllText(live, text);
			if (!string.IsNullOrEmpty(_historyPath))
				File.AppendAllText(_historyPath, text);
		}

		private static void FinalizeHistoryUnlocked()
		{
			if (!_filesInitialized || string.IsNullOrEmpty(_historyPath))
				return;
			try
			{
				File.AppendAllText(
					_historyPath,
					"# ended=" + DateTime.Now.ToString("o") + " lines=" + _written + "\r\n");
			}
			catch
			{
			}
		}
	}
}
