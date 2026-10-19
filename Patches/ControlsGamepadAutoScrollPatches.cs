using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using AdvancedKeybinds.Binding;
using AdvancedKeybinds.Core;
using AdvancedKeybinds.Diagnostics;
using AdvancedKeybinds.UI;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.GameContent.UI.Elements;
using Terraria.GameInput;
using Terraria.ModLoader;
using Terraria.UI;
using Terraria.UI.Gamepad;

namespace AdvancedKeybinds.Patches
{
	/// <summary>
	/// Controls screen GamePad nav: navigable-only Up/Down links (skip structurally collapsed
	/// children) and one-shot center-follow scroll using measured UIList / UIElement dimensions.
	/// Does not move UIElement layout, mouse hitboxes, or ComboPanel.
	/// </summary>
	internal static class ControlsGamepadAutoScrollPatches
	{
		private const int BackPointId = 3000;
		private const int KeyboardTabPointId = 3001;
		private const int GamepadTabPointId = 3002;
		private const int ListPointsStart = 3006;
		private const int ListPointsEndInclusive = 4999;
		private const int ResetPointsStart = 5000;
		private const int ResetPointsEndInclusive = 6999;
		/// <summary>Exposed for Undo/Cancel fixed-UI Down link.</summary>
		internal static int ListPointsStartPublic => ListPointsStart;
		/// <summary>UI units (layout space). Collapsed section roots are ~40f in UI_plus.</summary>
		private const float CollapsedSectionMaxHeightUi = 48f;
		private const float MinStructuralSizeUi = 1f;
		private const float EdgeFallbackMarginUi = 8f;

		private delegate void orig_SetupGamepadPoints(object self, SpriteBatch spriteBatch);
		private delegate void orig_ChangePoint(int id);
		private delegate void orig_OnActivate(object self);

		private static FieldInfo _uilistField;
		private static FieldInfo _keybindField;
		private static FieldInfo _headerField;
		private static FieldInfo _uiTextTextField;
		private static FieldInfo _snapPointField;
		private static PropertyInfo _elementsProp;
		private static PropertyInfo _childrenProp;
		private static MethodInfo _getTotalHeightMethod;
		private static MethodInfo _getSnapPointMethod;
		private static bool _snapPointFieldExists;
		private static bool _catalogFailsafeActive;
		private static string _lastMapSummaryKey;

		private static readonly List<ResetColumnEntry> _resetEntries = new(64);
		private static string _lastResetLinkKey;
		private static int _focusResetPoint = int.MinValue;
		private static bool _resetPadAWasDown;
		private static int _topNavChangeSeq;
		private static int _topNavChangeEpoch = int.MinValue;
		private static int _topNavBeforePoint = int.MinValue;
		private static bool _topNavExpectUndo;
		private static int _topNavMoveCount;
		private static bool _pendingResetVerticalScroll;
		private static int _resetLeftTravelEpoch = int.MinValue;
		private static int _resetLeftBeforePoint = int.MinValue;
		private static int _resetLeftChangeSeq;
		private static int _resetLeftMoveCount;
		private static bool _resetLeftRebuildFlag;

		/// <summary>
		/// Focus save for Controls list rebuild (accordion / FillList). StableId only — never trust PointId across rebuild.
		/// </summary>
		private static bool _focusRestoreSessionActive;
		private static int _focusSaveEpoch = int.MinValue;
		private static string _savedFocusStableId;
		private static string _savedFocusParentId;
		private static int _savedFocusPoint = int.MinValue;
		private static string _savedFocusElementType;
		private static bool? _savedFocusExpandedBefore;
		private static bool _focusRestoreLoggedRebuild;
		/// <summary>
		/// SectionHeader Pad:A open/close session. Survives Exact restore until delayed
		/// OnActivate→ChangePoint(GamepadTab) is held (or OnActivate completes while still on section).
		/// Premature Setup must not clear this — UI click often lands after PostUpdateInput.
		/// </summary>
		private static bool _sectionConfirmAwaitingRebuild;
		/// <summary>True after Exact StableId restore for the current section-confirm session.</summary>
		private static bool _sectionConfirmExactDone;
		private static long _sectionConfirmPressTickMs;
		private static long _sectionConfirmReleaseTickMs;
		private static bool _sectionConfirmSawPadADown;
		private static bool _duringOnActivate;
		private static int _manualScrollSelectedPoint = int.MinValue;
		private static string _manualScrollSelectedStableId;

		/// <summary>ComboPanel close → restore Mod section / scroll (not GamepadTab / ゲーム操作).</summary>
		private static ComboPanelParentRestoreSnapshot _comboPanelParentRestore;
		private static bool _comboPanelParentRestoreValid;
		private static int _comboPanelParentRestoreFramesLeft;

		/// <summary>
		/// ManageControls session only: last good UIList.ViewPosition.
		/// Cleared when the Controls screen closes (ClearRuntimeState). Not persisted.
		/// </summary>
		private static float _sessionListViewPosition;
		private static bool _sessionListViewPositionValid;

		internal struct ComboPanelParentRestoreSnapshot
		{
			public string ActionId;
			public string BindingStableId;
			public string ParentHeaderStableId;
			public float ViewPosition;
			public int PointIdHint;
			/// <summary>UIKeybindingListItem (or snap owner) at open time — preferred restore target.</summary>
			public UIElement FocusElement;
			/// <summary>0=left/full, 1=right Thin half, -1=unknown.</summary>
			public int PreferredColumn;
		}

		private struct ResetColumnEntry
		{
			public string ActionId;
			public int BindingPoint;
			public int ResetPoint;
		}

		private static int _prevPoint = int.MinValue;
		private static int _lastLoggedFrom = int.MinValue;
		private static int _lastLoggedTo = int.MinValue;
		private static string _lastLoggedFromId;
		private static string _lastLoggedToId;

		private static string _lastKnownStableId;
		private static string _lastKnownParentHeaderId;
		private static int _lastKnownListIndex = -1;
		private static int _prevLogicalFingerprint;
		private static bool _rebuildThisFrame;
		private static bool _blockFixedUiFocus;
		private static string _pendingRestoreStableId;
		private static string _pendingRestoreParentId;
		private static int _pendingRestoreListIndex = -1;

		private static int _scrolledForPoint = int.MinValue;
		private static int _scrollCountThisEpoch;
		private static string _scrollTargetThisEpoch;
		private static int _scrollEpoch = int.MinValue;
		private static string _lastCatalogLogKey;
		private static string _lastSkippedLogKey;
		private static string _lastModBindingClassifyKey;
		private static bool _centerScrollUnavailableLogged;

		/// <summary>
		/// Right-stick manual list scroll: suppress center-follow while active; sync focus on release.
		/// Dead zone 0.35 matches left-stick release threshold used in ComboPanel pad nav.
		/// </summary>
		private const float RightStickScrollDeadZone = 0.35f;
		private static bool _manualScrollActive;
		private static int _manualScrollPreferredColumn; // 0=left/full, 1=right

		public static void Apply()
		{
			ClearRuntimeState();

			Type manageType = typeof(UIState).Assembly.GetType("Terraria.GameContent.UI.States.UIManageControls");
			if (manageType == null)
			{
				AdvancedKeybinds.Instance?.Logger.Warn(
					"ControlsGamepadAutoScrollPatches: UIManageControls type not found.");
				return;
			}

			_uilistField = manageType.GetField("_uilist", BindingFlags.Instance | BindingFlags.NonPublic);
			_keybindField = typeof(UIKeybindingListItem).GetField("_keybind", BindingFlags.Instance | BindingFlags.NonPublic);
			if (_keybindField == null)
			{
				AdvancedKeybinds.Instance?.Logger.Warn(
					"ControlsGamepadAutoScrollPatches: UIKeybindingListItem._keybind FieldInfo is null; bind id fallback disabled.");
			}
			Type headerType = typeof(UIState).Assembly.GetType("Terraria.ModLoader.Config.UI.HeaderElement");
			_headerField = headerType?.GetField("header", BindingFlags.Instance | BindingFlags.NonPublic)
				?? headerType?.GetField("_header", BindingFlags.Instance | BindingFlags.NonPublic);
			_uiTextTextField = typeof(UIText).GetField("_text", BindingFlags.Instance | BindingFlags.NonPublic)
				?? typeof(UIText).GetField("Text", BindingFlags.Instance | BindingFlags.NonPublic);
			_snapPointField = typeof(UIElement).GetField("_snapPoint", BindingFlags.Instance | BindingFlags.NonPublic);
			_snapPointFieldExists = _snapPointField != null;
			_elementsProp = typeof(UIElement).GetProperty(
				"Elements",
				BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			_childrenProp = typeof(UIElement).GetProperty(
				"Children",
				BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			_getTotalHeightMethod = typeof(UIList).GetMethod(
				"GetTotalHeight",
				BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			_getSnapPointMethod = typeof(UIElement).GetMethod(
				"GetSnapPoint",
				BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
				null,
				new[] { typeof(SnapPoint).MakeByRefType() },
				null);

			ControlsSectionHeaderSupport.ApplyReflection(manageType);


			MethodInfo setup = manageType.GetMethod(
				"SetupGamepadPoints",
				BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
				null,
				new[] { typeof(SpriteBatch) },
				null);

			MethodInfo changePoint = typeof(UILinkPointNavigator).GetMethod(
				"ChangePoint",
				BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
				null,
				new[] { typeof(int) },
				null);

			if (_uilistField == null || setup == null)
			{
				AdvancedKeybinds.Instance?.Logger.Warn(
					"ControlsGamepadAutoScrollPatches: _uilist / SetupGamepadPoints not found.");
				return;
			}

			if (changePoint != null)
			{
				ModDetours.Apply(changePoint, (orig_ChangePoint orig, int id) =>
				{
					// ComboPanel owns input: do not move parent Controls link points (WASD / DPad / stick).
					if (ComboPanel.IsOpen)
						return;

					int before = SafeCurrentPoint();
					string reason = "OfficialLink";

					// After ComboPanel close: hold delayed GamepadTab / fixed-UI default so Mod section restore wins.
					if (ShouldHoldComboPanelParentRestoreAgainstDefault(id))
						return;

					// Section open/close: OnActivate may ChangePoint(3002) on a LATER frame than Pad:A PRESS
					// (UI click is often processed after PostUpdateInput). Keep list focus until rebuild Exact-restores.
					if (ShouldHoldSectionFocusAgainstGamepadTabDefault(before, id))
					{
						LogSectionPostRelease3002(before, id, applied: false);
						EndSectionConfirmLifetime(
							"HeldDelayedOnActivateGamepadTab",
							movedToGamepadTab: false,
							fixedOk: true);
						return;
					}

					// Intentional Reset→Binding Left: clear sticky reset focus BEFORE any later reassert.
					if (ControlsUiHost.IsManageControlsOpen()
						&& !ComboPanel.IsOpen
						&& before >= ResetPointsStart
						&& before <= ResetPointsEndInclusive
						&& id >= ListPointsStart
						&& id <= ListPointsEndInclusive)
					{
						NoteResetLeftTravel(before, id, "OfficialLink");
						_focusResetPoint = int.MinValue;
					}

					// Never block AK Undo focus point (outside 3000..3005 fixed band).
					if (id == ControlsPadBHoldCancelFocus.UndoCancelPointId)
					{
						reason = "AKLink";
						LogTopNavChange(before, id, reason);
						orig(id);
						MaybeLogTopNavResultAfterChange(before, id);
						MaybeFinishResetLeftTravel(before, id);
						return;
					}

					// Defense: only while ManageControls is open AND mid-setup restore.
					// Must not affect inventory / foreign-mod UILinkPoint navigation.
					if (_blockFixedUiFocus
						&& ControlsUiHost.IsManageControlsOpen()
						&& id >= BackPointId
						&& id < ListPointsStart
						&& !string.IsNullOrEmpty(_pendingRestoreStableId))
						return;

					// Defense: if Right-from-三本線 somehow collapses to P0 / GamepadTab, force AkUndo once.
					if (ControlsUiHost.IsManageControlsOpen()
						&& !ComboPanel.IsOpen
						&& TryRedirectFailedUndoTravel(before, ref id, ref reason))
					{
						LogTopNavChange(before, id, reason);
						orig(id);
						MaybeLogTopNavResultAfterChange(before, id);
						return;
					}

					// Do not let sticky Reset reassert path re-enter via ChangePoint(id=reset)
					// after user already left to Binding this epoch.
					if (ControlsUiHost.IsManageControlsOpen()
						&& id >= ResetPointsStart
						&& id <= ResetPointsEndInclusive
						&& before >= ListPointsStart
						&& before <= ListPointsEndInclusive
						&& _resetLeftTravelEpoch == FnaGamePadProvider.InputTickEpoch
						&& _focusResetPoint == int.MinValue)
					{
						GamePadProbeSystem.LogControlResetLeftChange(
							FnaGamePadProvider.InputTickEpoch,
							++_resetLeftChangeSeq,
							"ChangePoint",
							before,
							"Binding",
							id,
							"Reset",
							"Restore");
						return; // swallow restore-to-reset
					}

					if (ControlsUiHost.IsManageControlsOpen()
						&& (IsTopBarNavPoint(before) || IsTopBarNavPoint(id) || id == 0))
					{
						LogTopNavChange(before, id, reason);
					}

					if (id == GamepadTabPointId
						&& before >= ListPointsStart
						&& before <= ListPointsEndInclusive
						&& ControlsUiHost.IsManageControlsOpen()
						&& (_sectionConfirmAwaitingRebuild || _focusRestoreSessionActive))
					{
						LogSectionPostRelease3002(before, id, applied: true);
					}

					if (ControlsUiHost.IsManageControlsOpen()
						&& before >= ResetPointsStart
						&& before <= ResetPointsEndInclusive
						&& id >= ResetPointsStart
						&& id <= ResetPointsEndInclusive
						&& id != before)
					{
						_pendingResetVerticalScroll = true;
					}

					orig(id);
					MaybeLogTopNavResultAfterChange(before, id);
					MaybeFinishResetLeftTravel(before, id);
				});
			}

			MethodInfo onActivate = manageType.GetMethod(
				"OnActivate",
				BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
				null,
				Type.EmptyTypes,
				null);
			if (onActivate != null)
			{
				ModDetours.Apply(onActivate, (orig_OnActivate orig, object self) =>
				{
					_duringOnActivate = true;
					try
					{
						// Reused ManageControlsMenu keeps OnGameplay=false after Menu Controls close.
						// Force Gameplay (剣) before AssembleBindPanels/FillList inside OnActivate.
						ControlsManageOpenGameplayTab.ForceGameplayTabBeforeActivate(self);
						// Steam Deck: force controller device tab (OnKeyboard=false) so open matches pad UX.
						ControlsManageOpenGamepadDeviceTab.ForceGamepadDeviceTabBeforeActivate(
							self,
							AkPlatform.IsSteamDeck);
						orig(self);
					}
					finally
					{
						_duringOnActivate = false;
						// Exact already restored; OnActivate did not leave us on GamepadTab (held or skipped).
						if (_sectionConfirmAwaitingRebuild
							&& _sectionConfirmExactDone
							&& !string.IsNullOrEmpty(_savedFocusStableId)
							&& string.Equals(
								_lastKnownStableId,
								_savedFocusStableId,
								StringComparison.Ordinal))
						{
							EndSectionConfirmLifetime(
								"OnActivateCompleteStillOnSection",
								movedToGamepadTab: false,
								fixedOk: true);
						}
					}
				});
			}

			ModDetours.Apply(setup, (orig_SetupGamepadPoints orig, object self, SpriteBatch spriteBatch) =>
			{
				try { PrepareBeforeVanillaSetup(self); }
				catch (Exception)
				{
				}

				_blockFixedUiFocus = !_catalogFailsafeActive && !string.IsNullOrEmpty(_pendingRestoreStableId);
				try { orig(self, spriteBatch); }
				finally
				{
					_blockFixedUiFocus = false;
				}

				try { AfterVanillaSetup(self, spriteBatch); }
				catch (Exception)
				{
				}
			});
		}

		public static void ClearRuntimeState()
		{
			_prevPoint = int.MinValue;
			_lastLoggedFrom = int.MinValue;
			_lastLoggedTo = int.MinValue;
			_lastLoggedFromId = null;
			_lastLoggedToId = null;
			_lastKnownStableId = null;
			_lastKnownParentHeaderId = null;
			_lastKnownListIndex = -1;
			_prevLogicalFingerprint = 0;
			_rebuildThisFrame = false;
			_blockFixedUiFocus = false;
			_pendingRestoreStableId = null;
			_pendingRestoreParentId = null;
			_pendingRestoreListIndex = -1;
			_scrolledForPoint = int.MinValue;
			_scrollCountThisEpoch = 0;
			_scrollTargetThisEpoch = null;
			_scrollEpoch = int.MinValue;
			_lastCatalogLogKey = null;
			_lastSkippedLogKey = null;
			_lastMapSummaryKey = null;
			_lastModBindingClassifyKey = null;
			_centerScrollUnavailableLogged = false;
			_catalogFailsafeActive = false;
			_manualScrollActive = false;
			_manualScrollPreferredColumn = 0;
			_resetEntries.Clear();
			_lastResetLinkKey = null;
			_focusResetPoint = int.MinValue;
			_resetPadAWasDown = false;
			_topNavChangeSeq = 0;
			_topNavChangeEpoch = int.MinValue;
			_topNavBeforePoint = int.MinValue;
			_topNavExpectUndo = false;
			_topNavMoveCount = 0;
			_pendingResetVerticalScroll = false;
			_resetLeftTravelEpoch = int.MinValue;
			_resetLeftBeforePoint = int.MinValue;
			_resetLeftChangeSeq = 0;
			_resetLeftMoveCount = 0;
			_resetLeftRebuildFlag = false;
			_focusRestoreSessionActive = false;
			_focusSaveEpoch = int.MinValue;
			_savedFocusStableId = null;
			_savedFocusParentId = null;
			_savedFocusPoint = int.MinValue;
			_savedFocusElementType = null;
			_savedFocusExpandedBefore = null;
			_focusRestoreLoggedRebuild = false;
			_sectionConfirmAwaitingRebuild = false;
			_sectionConfirmExactDone = false;
			_sectionConfirmPressTickMs = 0;
			_sectionConfirmReleaseTickMs = 0;
			_sectionConfirmSawPadADown = false;
			_duringOnActivate = false;
			_manualScrollSelectedPoint = int.MinValue;
			_manualScrollSelectedStableId = null;
			_sessionListViewPosition = 0f;
			_sessionListViewPositionValid = false;
			ClearComboPanelParentRestore();
			ControlsSectionHeaderSupport.ClearRuntimeState();
			ControlsPadBHoldCancelFocus.ClearRuntimeState();
		}

		private static void NoteSessionListViewPosition(float viewPosition)
		{
			if (float.IsNaN(viewPosition) || float.IsInfinity(viewPosition))
				return;
			if (viewPosition < 0f)
				viewPosition = 0f;
			_sessionListViewPosition = viewPosition;
			_sessionListViewPositionValid = true;
		}

		private static void NoteSessionListViewPositionFromList(UIList uiList)
		{
			if (uiList == null)
				return;
			try { NoteSessionListViewPosition(uiList.ViewPosition); }
			catch { /* ignore */ }
		}

		/// <summary>
		/// Pad:A on a SectionHeader: capture StableId before list rebuild mutates PointId mapping.
		/// </summary>
		internal static void PollSaveFocusOnSectionConfirm()
		{
			if (!ControlsUiHost.IsManageControlsOpen() || ComboPanel.IsOpen)
				return;
			if (!FnaGamePadProvider.WasJustPressed(GamePadToken.A))
				return;
			if (!TryDescribeFocusedSectionRow(
				out int pointId,
				out string stableId,
				out string elementType,
				out bool? expanded))
				return;
			if (string.IsNullOrEmpty(stableId) || stableId.StartsWith("snap:", StringComparison.Ordinal))
				return;

			_focusRestoreSessionActive = true;
			_sectionConfirmAwaitingRebuild = true;
			_sectionConfirmExactDone = false;
			_focusSaveEpoch = FnaGamePadProvider.InputTickEpoch;
			_savedFocusStableId = stableId;
			_savedFocusParentId = _lastKnownParentHeaderId;
			_savedFocusPoint = pointId;
			_savedFocusElementType = elementType ?? "?";
			_savedFocusExpandedBefore = expanded;
			_focusRestoreLoggedRebuild = false;
			_sectionConfirmPressTickMs = Environment.TickCount64;
			_sectionConfirmReleaseTickMs = 0;
			_sectionConfirmSawPadADown = true;
			_pendingRestoreStableId = stableId;
			_pendingRestoreParentId = _lastKnownParentHeaderId;
			_pendingRestoreListIndex = pointId >= ListPointsStart
				? pointId - ListPointsStart
				: _lastKnownListIndex;
			_lastKnownStableId = stableId;

			GamePadProbeSystem.LogAkSectionFocusSave(
				_focusSaveEpoch,
				pointId,
				stableId,
				_savedFocusElementType,
				expanded);
			GamePadProbeSystem.LogAkSectionConfirmLifetimeBegin(
				_focusSaveEpoch,
				pointId,
				stableId,
				padAPressed: true,
				expanded);
		}

		/// <summary>Track Pad:A RELEASE for section-confirm lifetime diagnostics.</summary>
		internal static void PollSectionConfirmRelease()
		{
			if (!_focusRestoreSessionActive && !_sectionConfirmAwaitingRebuild)
				return;
			if (!ControlsUiHost.IsManageControlsOpen())
			{
				EndSectionConfirmLifetime("LeftControls", movedToGamepadTab: false, fixedOk: false);
				return;
			}

			bool aDown = FnaGamePadProvider.IsDown(GamePadToken.A);
			if (_sectionConfirmSawPadADown && !aDown)
			{
				_sectionConfirmSawPadADown = false;
				_sectionConfirmReleaseTickMs = Environment.TickCount64;
				GamePadProbeSystem.LogAkSectionConfirmRelease(
					FnaGamePadProvider.InputTickEpoch,
					_savedFocusStableId ?? "?",
					SafeCurrentPoint(),
					_lastKnownStableId ?? "?",
					_focusRestoreSessionActive || _sectionConfirmAwaitingRebuild);
			}
			else if (aDown)
			{
				_sectionConfirmSawPadADown = true;
			}
		}

		private static bool TryDescribeFocusedSectionRow(
			out int pointId,
			out string stableId,
			out string elementType,
			out bool? expanded)
		{
			pointId = int.MinValue;
			stableId = null;
			elementType = null;
			expanded = null;

			if (!ControlsUiHost.TryGetManageControls(out UIState state, out _))
				return false;
			if (_uilistField == null || state == null)
				return false;
			if (_uilistField.GetValue(state) is not UIList uiList)
				return false;

			pointId = UILinkPointNavigator.CurrentPoint;
			if (pointId < ListPointsStart || pointId > ListPointsEndInclusive)
				return false;

			int idx = pointId - ListPointsStart;
			List<RowInfo> rows;
			try { rows = BuildRowCatalog(uiList); }
			catch { return false; }

			if (idx < 0 || idx >= rows.Count)
				return false;

			RowInfo row = rows[idx];
			if (row.Kind != ControlRowKind.SectionHeader || !row.IsNavigable)
				return false;

			stableId = row.StableId;
			elementType = row.Element?.GetType().Name;
			expanded = row.IsExpanded;
			_lastKnownParentHeaderId = row.ParentHeaderId;
			_lastKnownListIndex = idx;
			return !string.IsNullOrEmpty(stableId);
		}

		/// <summary>Current navigable row StableId if known (for ComboPanel parent restore).</summary>
		internal static string TryGetCurrentStableId()
			=> _lastKnownStableId;

		/// <summary>
		/// Resolves the focused list row when it is a keybind Binding row (not header/tab/cancel).
		/// Used to arm <see cref="PendingGamePadRowConfirm"/> on Pad:A PRESS.
		/// Right-column Thin keeps its own actionId (never borrow the left partner).
		/// Snap owners may be wrappers under Mod/Section headers — nested UIKeybindingListItem still counts.
		/// </summary>
		internal static bool TryGetFocusedBindingRow(
			out int pointId,
			out string actionId,
			out string stableId,
			out string host)
			=> TryGetFocusedBindingRow(out pointId, out actionId, out stableId, out host, out _, out _);

		internal static bool TryGetFocusedBindingRow(
			out int pointId,
			out string actionId,
			out string stableId,
			out string host,
			out bool isGamePadControlsRow)
			=> TryGetFocusedBindingRow(
				out pointId, out actionId, out stableId, out host, out isGamePadControlsRow, out _);

		internal static bool TryGetFocusedBindingRow(
			out int pointId,
			out string actionId,
			out string stableId,
			out string host,
			out bool isGamePadControlsRow,
			out UIElement rowElement)
		{
			pointId = int.MinValue;
			actionId = null;
			stableId = null;
			host = null;
			isGamePadControlsRow = false;
			rowElement = null;

			if (!ControlsUiHost.TryGetManageControls(out UIState state, out host))
				return false;
			if (_uilistField == null || state == null)
				return false;
			if (_uilistField.GetValue(state) is not UIList uiList)
				return false;

			pointId = UILinkPointNavigator.CurrentPoint;
			if (pointId < ListPointsStart || pointId > ListPointsEndInclusive)
				return false;

			int idx = pointId - ListPointsStart;
			List<RowInfo> rows;
			try { rows = BuildRowCatalog(uiList); }
			catch { return false; }

			if (idx < 0 || idx >= rows.Count)
				return false;

			RowInfo row = rows[idx];

			if (row.Kind == ControlRowKind.SectionHeader
				|| row.Kind == ControlRowKind.DefaultReset)
				return false;

			UIElement keybindEl = FindKeybindListItemInSubtree(row.Element);
			bool keybindElOk = keybindEl != null || IsKeybindListRowElement(row.Element);
			bool bindingKind = row.Kind == ControlRowKind.Binding
				|| row.Kind == ControlRowKind.HorizontalRight
				|| row.IsHorizontalOnly;
			// Binding attribute wins over SectionChild — never treat "under section" as exclusive.
			bool bindingLike = bindingKind || keybindElOk;
			if (!bindingLike)
				return false;

			if (!row.IsNavigable && !keybindElOk && !row.IsHorizontalOnly)
				return false;

			UIElement resolveEl = keybindEl ?? row.Element;
			actionId = KeybindingUiPatches.TryResolveActionIdFromRowElement(resolveEl);
			if (string.IsNullOrEmpty(actionId))
				actionId = TryReadListItemKeybind(resolveEl);

			if (string.IsNullOrEmpty(actionId))
			{
				MaybeLogModBindingConfirm(pointId, row, resolveEl, actionId: null, allowed: false, "NoActionId");
				return false;
			}

			stableId = row.StableId;
			if (string.IsNullOrEmpty(stableId) || stableId.StartsWith("snap:", StringComparison.Ordinal))
			{
				string fromBind = BuildStableIdForBind(actionId, row.ParentHeaderId);
				if (!string.IsNullOrEmpty(fromBind))
					stableId = fromBind;
			}

			if (string.IsNullOrEmpty(stableId))
				stableId = _lastKnownStableId;

			isGamePadControlsRow = KeybindingUiPatches.IsGamePadControlsListItem(resolveEl);
			rowElement = resolveEl;
			MaybeLogModBindingConfirm(pointId, row, resolveEl, actionId, allowed: true, "Ok");
			return true;
		}

		internal static bool IsManualScrollActive => _manualScrollActive;

		internal static string DescribeFocusedRowKindForDiag()
		{
			try
			{
				if (!ControlsUiHost.TryGetManageControls(out UIState state, out _))
					return "NoHost";
				if (_uilistField?.GetValue(state) is not UIList uiList)
					return "NoList";
				int pointId = UILinkPointNavigator.CurrentPoint;
				int idx = pointId - ListPointsStart;
				List<RowInfo> rows = BuildRowCatalog(uiList);
				if (idx < 0 || idx >= rows.Count)
					return "OutOfRange";
				RowInfo row = rows[idx];
				string kind = row.Kind.ToString();
				// Binding under a section/mod header keeps ParentHeaderId — surface both for diagnostics.
				if (row.Kind == ControlRowKind.Binding
					&& !string.IsNullOrEmpty(row.ParentHeaderId))
					kind = "Binding+SectionChild";
				return kind
					+ (row.IsHorizontalOnly ? "+Horiz" : "")
					+ (row.IsNavigable ? "+Nav" : "+NonNav");
			}
			catch
			{
				return "Error";
			}
		}

		/// <summary>
		/// Snap owner may be a wrapper (section/mod child panel). Prefer the nested keybind list item.
		/// </summary>
		internal static bool HasNestedKeybindListItem(UIElement root)
			=> FindKeybindListItemInSubtree(root) != null;

		/// <summary>
		/// Snap owner may be a wrapper (section/mod child panel). Prefer the nested keybind list item.
		/// </summary>
		private static UIElement FindKeybindListItemInSubtree(UIElement root)
		{
			if (root == null)
				return null;
			if (IsKeybindListRowElement(root))
				return root;

			const int maxDepth = 6;
			const int maxVisit = 48;
			int visited = 0;
			return FindKeybindListItemInSubtreeCore(root, 0, maxDepth, ref visited, maxVisit);
		}

		private static UIElement FindKeybindListItemInSubtreeCore(
			UIElement el,
			int depth,
			int maxDepth,
			ref int visited,
			int maxVisit)
		{
			if (el == null || depth > maxDepth || visited >= maxVisit)
				return null;
			visited++;
			if (depth > 0 && IsKeybindListRowElement(el))
				return el;

			foreach (UIElement child in EnumerateChildren(el))
			{
				UIElement found = FindKeybindListItemInSubtreeCore(
					child,
					depth + 1,
					maxDepth,
					ref visited,
					maxVisit);
				if (found != null)
					return found;
			}

			return null;
		}

		private static string BuildStableIdForBind(string actionId, string parentHeaderId)
		{
			if (string.IsNullOrEmpty(actionId))
				return null;
			string prefix = string.IsNullOrEmpty(parentHeaderId)
				? "Bind"
				: parentHeaderId.Replace(".Header", "", StringComparison.Ordinal);
			return prefix + "." + SanitizeToken(actionId);
		}

		private static void MaybeLogModBindingClassify(
			int pointHint,
			UIElement snapEl,
			UIElement keybindEl,
			string actionId,
			string parentHeaderId,
			ControlRowKind kind,
			bool navigable)
		{
			if (keybindEl == null && string.IsNullOrEmpty(actionId))
				return;
			bool underSection = !string.IsNullOrEmpty(parentHeaderId);
			bool wrapped = keybindEl != null && snapEl != null && !ReferenceEquals(snapEl, keybindEl);
			if (!underSection && !wrapped)
				return;

			string key = (actionId ?? "?")
				+ "|" + kind
				+ "|" + (parentHeaderId ?? "")
				+ "|" + (snapEl?.GetType().Name ?? "")
				+ "|" + (keybindEl?.GetType().Name ?? "");
			if (string.Equals(key, _lastModBindingClassifyKey, StringComparison.Ordinal))
				return;
			_lastModBindingClassifyKey = key;

			GamePadProbeSystem.LogAkModBindingRowClassify(
				pointHint,
				keybindEl?.GetType().Name ?? snapEl?.GetType().Name ?? "?",
				actionId ?? "?",
				snapEl?.GetType().Name ?? "?",
				parentHeaderId ?? "?",
				kind.ToString(),
				hasBindingFlag: kind == ControlRowKind.Binding || kind == ControlRowKind.HorizontalRight,
				hasSectionChildFlag: underSection,
				navigable);
		}

		private static void MaybeLogModBindingConfirm(
			int pointId,
			RowInfo row,
			UIElement resolveEl,
			string actionId,
			bool allowed,
			string reason)
		{
			bool underSection = !string.IsNullOrEmpty(row.ParentHeaderId);
			bool wrapped = resolveEl != null
				&& row.Element != null
				&& !ReferenceEquals(resolveEl, row.Element);
			if (!underSection && !wrapped && row.Kind != ControlRowKind.SectionChild)
				return;

			GamePadProbeSystem.LogAkModBindingConfirm(
				pointId,
				actionId ?? "?",
				row.Kind == ControlRowKind.Binding && underSection
					? "Binding+SectionChild"
					: row.Kind.ToString(),
				allowed,
				reason ?? "?");
		}

		private static bool IsKeybindListRowElement(UIElement el)
		{
			if (el == null)
				return false;
			// Exact assignable types only — never treat SimpleListItem / HeaderElement as ListItem.
			return el is UIKeybindingListItem or UIKeybindingToggleListItem;
		}

		/// <summary>
		/// Reads UIKeybindingListItem._keybind only when the runtime target is that declaring type.
		/// Does not throw ArgumentException for SimpleListItem / HeaderElement.
		/// </summary>
		private static string TryReadListItemKeybind(UIElement el)
		{
			if (el == null || _keybindField == null)
				return null;
			if (!KeybindRowReflectionSafety.TryReadStringField(_keybindField, el, out string kb))
				return null;
			return kb;
		}

		internal static bool TryCaptureListViewPosition(out float viewPosition)
		{
			viewPosition = 0f;
			if (!ControlsUiHost.TryGetManageControls(out UIState state, out _))
				return false;
			if (_uilistField == null || state == null)
				return false;
			if (_uilistField.GetValue(state) is not UIList uiList)
				return false;
			viewPosition = uiList.ViewPosition;
			return true;
		}

		internal static void TryRestoreListViewPosition(float viewPosition)
		{
			if (float.IsNaN(viewPosition) || float.IsInfinity(viewPosition))
				return;
			if (!ControlsUiHost.TryGetManageControls(out UIState state, out _))
				return;
			if (_uilistField == null || state == null)
				return;
			if (_uilistField.GetValue(state) is not UIList uiList)
				return;
			try
			{
				float max = GetMaxViewPosition(uiList, uiList.GetInnerDimensions().Height);
				viewPosition = ControlsListScrollSessionPolicy.ClampViewPosition(viewPosition, max);
				uiList.ViewPosition = viewPosition;
				NoteSessionListViewPosition(viewPosition);
			}
			catch
			{
				// ignore
			}
		}

		internal static void TryRestoreStableIdFocus(string stableId)
		{
			if (string.IsNullOrEmpty(stableId))
				return;
			if (!ControlsUiHost.TryGetManageControls(out UIState state, out _))
				return;
			if (_uilistField == null || state == null)
				return;
			if (_uilistField.GetValue(state) is not UIList uiList)
				return;

			try
			{
				List<RowInfo> rows = BuildRowCatalog(uiList);
				for (int i = 0; i < rows.Count; i++)
				{
					if (!rows[i].IsNavigable)
						continue;
					if (!string.Equals(rows[i].StableId, stableId, StringComparison.Ordinal))
						continue;
					UILinkPointNavigator.ChangePoint(ListPointsStart + i);
					_lastKnownStableId = stableId;
					_lastKnownListIndex = i;
					return;
				}
			}
			catch
			{
				// ignore
			}
		}

		/// <summary>
		/// Capture Controls list identity for ComboPanel close restore.
		/// Prefer ActionId / StableId / parent header / ViewPosition — not raw CurrentPoint alone
		/// (mouse-open leaves CurrentPoint on GamepadTab / ゲーム操作).
		/// </summary>
		internal static bool TryCaptureComboPanelParentRestore(
			string actionId,
			out ComboPanelParentRestoreSnapshot snap)
		{
			snap = default;
			snap.ActionId = actionId;
			snap.PointIdHint = int.MinValue;
			snap.ViewPosition = float.NaN;
			snap.PreferredColumn = -1;
			snap.FocusElement = null;

			if (!ControlsUiHost.TryGetManageControls(out UIState state, out _))
				return !string.IsNullOrEmpty(actionId);
			if (_uilistField == null || state == null)
				return !string.IsNullOrEmpty(actionId);
			if (_uilistField.GetValue(state) is not UIList uiList)
				return !string.IsNullOrEmpty(actionId);

			try
			{
				snap.ViewPosition = uiList.ViewPosition;
				List<RowInfo> rows = BuildRowCatalog(uiList);

				// Prefer the currently focused binding row when it matches the opened action
				// (preserves right-column Thin identity; ActionId-only search used to miss HorizontalRight).
				if (TryGetFocusedBindingRow(out int focusPoint, out string focusAction, out string focusStable, out _)
					&& (string.IsNullOrEmpty(actionId)
						|| string.Equals(focusAction, actionId, StringComparison.OrdinalIgnoreCase)))
				{
					int focusIdx = focusPoint - ListPointsStart;
					if (focusIdx >= 0 && focusIdx < rows.Count)
					{
						FillParentRestoreFromRow(ref snap, rows[focusIdx], focusIdx, uiList);
						return true;
					}
				}

				int preferredColumn = snap.PreferredColumn;
				if (TryFindRestorableBindingRowIndexByAction(rows, actionId, preferredColumn, out int idx))
				{
					FillParentRestoreFromRow(ref snap, rows[idx], idx, uiList);
					return true;
				}

				// Fallback: focused binding row / last-known ids (still better than PointId alone).
				if (TryGetFocusedBindingRow(out int pointId, out string focusAction2, out string focusStable2, out _))
				{
					if (string.IsNullOrEmpty(snap.ActionId))
						snap.ActionId = focusAction2;
					snap.BindingStableId = focusStable2;
					snap.PointIdHint = pointId;
					for (int i = 0; i < rows.Count; i++)
					{
						if (ListPointsStart + i != pointId)
							continue;
						FillParentRestoreFromRow(ref snap, rows[i], i, uiList);
						return true;
					}
				}
				else
				{
					snap.BindingStableId = _lastKnownStableId;
					snap.ParentHeaderStableId = _lastKnownParentHeaderId;
					int cur = SafeCurrentPoint();
					if (cur >= ListPointsStart && cur <= ListPointsEndInclusive)
						snap.PointIdHint = cur;
				}

				return !string.IsNullOrEmpty(snap.ActionId)
					|| !string.IsNullOrEmpty(snap.BindingStableId)
					|| snap.FocusElement != null
					|| !float.IsNaN(snap.ViewPosition);
			}
			catch
			{
				return !string.IsNullOrEmpty(actionId);
			}
		}

		private static void FillParentRestoreFromRow(
			ref ComboPanelParentRestoreSnapshot snap,
			RowInfo row,
			int idx,
			UIList uiList)
		{
			snap.BindingStableId = row.StableId;
			snap.ParentHeaderStableId = row.ParentHeaderId;
			snap.PointIdHint = ListPointsStart + idx;
			snap.PreferredColumn = row.IsHorizontalOnly ? 1 : 0;
			UIElement keybindEl = FindKeybindListItemInSubtree(row.Element) ?? row.Element;
			snap.FocusElement = keybindEl;
			_lastKnownStableId = row.StableId;
			_lastKnownParentHeaderId = row.ParentHeaderId;
			_lastKnownListIndex = idx;
			_ = uiList;
		}

		/// <summary>Arm deferred restore after ComboPanel close (immediate apply + re-assert frames).</summary>
		internal static void ArmComboPanelParentRestore(in ComboPanelParentRestoreSnapshot snap)
		{
			_comboPanelParentRestore = snap;
			_comboPanelParentRestoreValid = true;
			_comboPanelParentRestoreFramesLeft = ComboPanelParentRestorePolicy.DeferredRestoreFrames;
			if (!float.IsNaN(snap.ViewPosition) && !float.IsInfinity(snap.ViewPosition))
				NoteSessionListViewPosition(snap.ViewPosition);

			// Immediate once: ChangePoint (+ ViewPosition) so pad UI nav returns to a valid parent link.
			TryRestoreComboPanelParent(in snap, log: true);
		}

		internal static void PollComboPanelParentRestore()
		{
			if (!_comboPanelParentRestoreValid || _comboPanelParentRestoreFramesLeft <= 0)
				return;
			if (ComboPanel.IsOpen)
				return;
			if (!ControlsUiHost.IsManageControlsOpen())
			{
				ClearComboPanelParentRestore();
				return;
			}

			// Diagnosis-only: skip DeferredRestoreFrames re-apply (ChangePoint/ViewPosition repeat).
			// Still tick frames so ShouldHoldComboPanelParentRestoreAgainstDefault can block GamepadTab/Back.
			if (!ParentRestoreFlickerTest.DeferredReapplyDisabled)
				TryRestoreComboPanelParent(in _comboPanelParentRestore, log: false);

			_comboPanelParentRestoreFramesLeft--;
			if (_comboPanelParentRestoreFramesLeft <= 0)
				ClearComboPanelParentRestore();
		}

		internal static void ClearComboPanelParentRestore()
		{
			_comboPanelParentRestoreValid = false;
			_comboPanelParentRestoreFramesLeft = 0;
			_comboPanelParentRestore = default;
		}

		private static bool ShouldHoldComboPanelParentRestoreAgainstDefault(int id)
		{
			if (!_comboPanelParentRestoreValid || _comboPanelParentRestoreFramesLeft <= 0)
				return false;
			if (!ControlsUiHost.IsManageControlsOpen() || ComboPanel.IsOpen)
				return false;

			// Hold GamepadTab / Back / fixed FancyUI defaults — not in-list navigable points.
			if (id == GamepadTabPointId)
				return true;
			if (id >= BackPointId && id < ListPointsStart)
				return true;
			return false;
		}

		private static bool TryRestoreComboPanelParent(in ComboPanelParentRestoreSnapshot snap, bool log)
		{
			if (!ControlsUiHost.TryGetManageControls(out UIState state, out _))
				return false;
			if (_uilistField == null || state == null)
				return false;
			if (_uilistField.GetValue(state) is not UIList uiList)
				return false;

			try
			{
				List<RowInfo> rows = BuildRowCatalog(uiList);

				bool elementFound = TryFindRestorableRowIndexByFocusElement(
					rows,
					uiList,
					snap.FocusElement,
					out int elementIdx);

				bool actionFound = TryFindRestorableBindingRowIndexByAction(
					rows,
					snap.ActionId,
					snap.PreferredColumn,
					out int actionIdx);

				bool stableFound = TryFindRestorableRowIndexByStableId(
					rows,
					snap.BindingStableId,
					snap.PreferredColumn,
					out int stableIdx);

				bool pointOk = false;
				int pointIdx = -1;
				if (snap.PointIdHint >= ListPointsStart && snap.PointIdHint <= ListPointsEndInclusive)
				{
					pointIdx = snap.PointIdHint - ListPointsStart;
					if (pointIdx >= 0 && pointIdx < rows.Count && IsRestorableBindingRow(rows[pointIdx]))
					{
						string atPoint = ResolveRowActionId(rows[pointIdx]);
						pointOk = !string.IsNullOrEmpty(snap.ActionId)
							&& string.Equals(atPoint, snap.ActionId, StringComparison.OrdinalIgnoreCase);
					}
				}

				ComboPanelParentRestoreMatchKind kind = ComboPanelParentRestorePolicy.ChooseBest(
					elementFound,
					actionFound,
					stableFound,
					pointOk);

				int restoreIdx = kind switch {
					ComboPanelParentRestoreMatchKind.ElementReference => elementIdx,
					ComboPanelParentRestoreMatchKind.ActionId => actionIdx,
					ComboPanelParentRestoreMatchKind.BindingStableId => stableIdx,
					ComboPanelParentRestoreMatchKind.PointIdHintVerified => pointIdx,
					_ => -1
				};

				if (restoreIdx >= 0 && restoreIdx < rows.Count)
				{
					int point = ListPointsStart + restoreIdx;
					if (!ComboPanelParentRestorePolicy.IsForbiddenRestorePointId(
						point,
						BackPointId,
						ListPointsStart,
						KeyboardTabPointId,
						GamepadTabPointId))
					{
						UILinkPointNavigator.ChangePoint(point);
						_lastKnownStableId = rows[restoreIdx].StableId;
						_lastKnownParentHeaderId = rows[restoreIdx].ParentHeaderId;
						_lastKnownListIndex = restoreIdx;
					}
					else
					{
						kind = ComboPanelParentRestoreMatchKind.None;
						restoreIdx = -1;
					}
				}

				// Scroll after focus so we keep the captured ViewPosition (no jump to list top / ゲーム操作).
				if (!float.IsNaN(snap.ViewPosition) && !float.IsInfinity(snap.ViewPosition))
					TryRestoreListViewPosition(snap.ViewPosition);

				if (log)
				{
					GamePadProbeSystem.LogPanelParentRestore(
						snap.ActionId ?? "?",
						snap.BindingStableId ?? "?",
						snap.ParentHeaderStableId ?? "?",
						kind.ToString(),
						restoreIdx >= 0 ? (ListPointsStart + restoreIdx) : int.MinValue,
						snap.ViewPosition);
				}

				return kind != ComboPanelParentRestoreMatchKind.None
					|| !float.IsNaN(snap.ViewPosition);
			}
			catch
			{
				return false;
			}
		}

		private static bool IsRestorableBindingRow(RowInfo row)
		{
			if (row.Kind == ControlRowKind.SectionHeader
				|| row.Kind == ControlRowKind.DefaultReset)
				return false;

			return ComboPanelParentRestorePolicy.IsRestorableBindingRow(
				row.IsNavigable,
				row.IsHorizontalOnly,
				row.Kind == ControlRowKind.Binding,
				row.Kind == ControlRowKind.HorizontalRight);
		}

		private static bool TryFindRestorableRowIndexByFocusElement(
			List<RowInfo> rows,
			UIList uiList,
			UIElement focus,
			out int index)
		{
			index = -1;
			if (focus == null || rows == null || uiList == null)
				return false;
			if (!IsFocusElementStillRestorable(focus, uiList))
				return false;

			for (int i = 0; i < rows.Count; i++)
			{
				if (!IsRestorableBindingRow(rows[i]))
					continue;
				UIElement rowEl = rows[i].Element;
				UIElement keybindEl = FindKeybindListItemInSubtree(rowEl) ?? rowEl;
				if (ReferenceEquals(keybindEl, focus) || ReferenceEquals(rowEl, focus))
				{
					index = i;
					return true;
				}
			}

			return false;
		}

		private static bool IsFocusElementStillRestorable(UIElement el, UIList uiList)
		{
			if (el == null || uiList == null)
				return false;

			bool underList = false;
			for (UIElement e = el; e != null; e = e.Parent)
			{
				if (ReferenceEquals(e, uiList))
				{
					underList = true;
					break;
				}
			}

			bool visible = false;
			try
			{
				CalculatedStyle d = el.GetDimensions();
				visible = d.Width > MinStructuralSizeUi && d.Height > MinStructuralSizeUi;
			}
			catch
			{
				visible = false;
			}

			// Terraria UIElement has no public IsDisposed; leaving the tree (Parent chain) covers disposal.
			bool notDisposed = el.Parent != null || underList;
			return ComboPanelParentRestorePolicy.CanRestoreElementReference(
				elementNonNull: true,
				notDisposed: notDisposed,
				stillUnderParentList: underList,
				visible: visible);
		}

		private static bool TryFindRestorableBindingRowIndexByAction(
			List<RowInfo> rows,
			string actionId,
			int preferredColumn,
			out int index)
		{
			index = -1;
			if (string.IsNullOrEmpty(actionId) || rows == null)
				return false;

			int fallback = -1;
			for (int i = 0; i < rows.Count; i++)
			{
				if (!IsRestorableBindingRow(rows[i]))
					continue;
				string rowAction = ResolveRowActionId(rows[i]);
				if (string.IsNullOrEmpty(rowAction))
					continue;
				if (!string.Equals(rowAction, actionId, StringComparison.OrdinalIgnoreCase))
					continue;

				int col = rows[i].IsHorizontalOnly ? 1 : 0;
				if (preferredColumn == 0 || preferredColumn == 1)
				{
					if (col == preferredColumn)
					{
						index = i;
						return true;
					}

					if (fallback < 0)
						fallback = i;
					continue;
				}

				index = i;
				return true;
			}

			if (fallback >= 0)
			{
				index = fallback;
				return true;
			}

			return false;
		}

		private static bool TryFindRestorableRowIndexByStableId(
			List<RowInfo> rows,
			string stableId,
			int preferredColumn,
			out int index)
		{
			index = -1;
			if (string.IsNullOrEmpty(stableId) || rows == null)
				return false;

			int fallback = -1;
			for (int i = 0; i < rows.Count; i++)
			{
				if (!IsRestorableBindingRow(rows[i]))
					continue;
				if (!string.Equals(rows[i].StableId, stableId, StringComparison.Ordinal))
					continue;

				int col = rows[i].IsHorizontalOnly ? 1 : 0;
				if (preferredColumn == 0 || preferredColumn == 1)
				{
					if (col == preferredColumn)
					{
						index = i;
						return true;
					}

					if (fallback < 0)
						fallback = i;
					continue;
				}

				index = i;
				return true;
			}

			if (fallback >= 0)
			{
				index = fallback;
				return true;
			}

			return false;
		}

		private static string ResolveRowActionId(RowInfo row)
		{
			UIElement keybindEl = FindKeybindListItemInSubtree(row.Element);
			UIElement resolveEl = keybindEl ?? row.Element;
			string actionId = KeybindingUiPatches.TryResolveActionIdFromRowElement(resolveEl);
			if (!string.IsNullOrEmpty(actionId))
				return actionId;

			return TryReadListItemKeybind(resolveEl);
		}

		private static void PrepareBeforeVanillaSetup(object manageControls)
		{
			_rebuildThisFrame = false;
			_pendingRestoreStableId = null;
			_pendingRestoreParentId = null;
			_pendingRestoreListIndex = -1;

			if (manageControls == null || _uilistField == null)
				return;
			if (_uilistField.GetValue(manageControls) is not UIList uiList)
				return;

			// Inject SnapPoints onto section titles before vanilla GetSnapPoints.
			ControlsSectionHeaderSupport.EnsureSectionHeaderSnapPoints(uiList);
			try { uiList.Recalculate(); }
			catch { /* ignore */ }

			List<RowInfo> rows = BuildRowCatalog(uiList);
			int fingerprint = ComputeLogicalFingerprint(rows);
			if (_prevLogicalFingerprint != 0 && fingerprint != _prevLogicalFingerprint)
				_rebuildThisFrame = true;
			_prevLogicalFingerprint = fingerprint;

			// Capture before After restores: never learn wiped ViewPosition==0 on rebuild frames.
			if (ControlsListScrollSessionPolicy.ShouldCaptureSessionViewPosition(
				_rebuildThisFrame,
				listAvailable: true))
			{
				NoteSessionListViewPositionFromList(uiList);
			}

			int navCount = CountNavigable(rows);
			_catalogFailsafeActive = rows.Count > 0 && navCount == 0;

			LogCatalogIfChanged(rows);

			int current = UILinkPointNavigator.CurrentPoint;
			bool onGamepadTab = current == GamepadTabPointId;
			string currentStableGuess = null;
			if (current >= ListPointsStart && current <= ListPointsEndInclusive)
			{
				int idx = current - ListPointsStart;
				if (idx >= 0 && idx < rows.Count && rows[idx].IsNavigable)
				{
					RowInfo row = rows[idx];
					_lastKnownStableId = row.StableId;
					_lastKnownParentHeaderId = row.ParentHeaderId;
					_lastKnownListIndex = idx;
					currentStableGuess = row.StableId;
				}
			}

			// Prefer StableId captured on Section confirm — do not overwrite from GamepadTab / stale PointId.
			if ((_focusRestoreSessionActive || _sectionConfirmAwaitingRebuild)
				&& !string.IsNullOrEmpty(_savedFocusStableId))
			{
				_pendingRestoreStableId = _savedFocusStableId;
				_pendingRestoreParentId = _savedFocusParentId;
				_pendingRestoreListIndex = _savedFocusPoint >= ListPointsStart
					? _savedFocusPoint - ListPointsStart
					: _lastKnownListIndex;
			}
			else if (!string.IsNullOrEmpty(_lastKnownStableId)
				&& !_catalogFailsafeActive
				&& (current >= ListPointsStart && current <= ListPointsEndInclusive || _rebuildThisFrame))
			{
				_pendingRestoreStableId = _lastKnownStableId;
				_pendingRestoreParentId = _lastKnownParentHeaderId;
				_pendingRestoreListIndex = _lastKnownListIndex;
			}

			if (_catalogFailsafeActive)
			{
				_pendingRestoreStableId = null;
				_rebuildThisFrame = false;
			}

			if (_rebuildThisFrame && !string.IsNullOrEmpty(_pendingRestoreStableId))
			{
				GamePadProbeSystem.LogNavRebuildBegin(_pendingRestoreStableId);
				if (_focusRestoreSessionActive && !_focusRestoreLoggedRebuild)
				{
					_focusRestoreLoggedRebuild = true;
					GamePadProbeSystem.LogAkControlsRebuildDetected(
						_focusSaveEpoch,
						_savedFocusStableId ?? _pendingRestoreStableId,
						current,
						currentStableGuess ?? (onGamepadTab ? "GamepadTab" : FormatPoint(current)),
						onGamepadTab);
				}
			}
		}

		private static void AfterVanillaSetup(object manageControls, SpriteBatch spriteBatch)
		{
			if (manageControls == null || _uilistField == null)
				return;
			if (_uilistField.GetValue(manageControls) is not UIList uiList)
				return;

			if (manageControls is UIElement manageEl)
				ControlsPadBHoldCancelFocus.EnsureUndoLinkPoint(manageEl);

			// Re-ensure in case accordion rebuild replaced headers after Prepare.
			ControlsSectionHeaderSupport.EnsureSectionHeaderSnapPoints(uiList);

			List<RowInfo> rows = BuildRowCatalog(uiList);
			int navigableCount = CountNavigable(rows);

			if (rows.Count > 0 && navigableCount == 0)
			{
				_catalogFailsafeActive = true;
				_pendingRestoreStableId = null;
				_rebuildThisFrame = false;
				GamePadProbeSystem.LogControlRowFailsafe("NavigableZero", rows.Count, "UseFullSnapLinks");
				RebuildFullSnapLinks(uiList);
				LogNavStateChanges(rows);
				return;
			}

			_catalogFailsafeActive = false;
			RebuildNavigableLinks(uiList, rows);
			WireResetColumn(rows);

			// Rebuild wipes UIList.ViewPosition; put session scroll back before focus/scroll helpers.
			if (ControlsListScrollSessionPolicy.ShouldRestoreSessionViewPosition(
				_sessionListViewPositionValid,
				_rebuildThisFrame))
			{
				TryRestoreListViewPosition(_sessionListViewPosition);
			}

			bool restored = RestoreSelectionAfterRebuild(uiList, rows);
			ReassertResetColumnFocus();
			CenterFollowOrFallback(uiList, rows, restored);

			if (ControlsListScrollSessionPolicy.ShouldCaptureSessionViewPosition(
				rebuildThisFrame: false,
				listAvailable: true))
			{
				NoteSessionListViewPositionFromList(uiList);
			}

			LogNavStateChanges(rows);
		}

		private static int CountNavigable(List<RowInfo> rows)
		{
			int n = 0;
			for (int i = 0; i < rows.Count; i++)
			{
				if (rows[i].IsNavigable)
					n++;
			}

			return n;
		}

		private static void LogCatalogIfChanged(List<RowInfo> rows)
		{
			int total = rows.Count;
			int navigable = 0;
			int skipped = 0;
			for (int i = 0; i < rows.Count; i++)
			{
				if (rows[i].IsNavigable)
					navigable++;
				else if (rows[i].IsInsideCollapsedGroup)
					skipped++;
			}

			string key = total + "|" + navigable + "|" + skipped;
			if (string.Equals(key, _lastCatalogLogKey, StringComparison.Ordinal))
				return;
			_lastCatalogLogKey = key;
			GamePadProbeSystem.LogControlRowCatalog(total, navigable, skipped);
		}

		private static bool RestoreSelectionAfterRebuild(UIList uiList, List<RowInfo> rows)
		{
			_ = uiList;
			if (rows.Count == 0)
				return false;

			int current = UILinkPointNavigator.CurrentPoint;
			bool onFixedUi = current == ControlsPadBHoldCancelFocus.UndoCancelPointId
				|| (current >= BackPointId && current < ListPointsStart);
			bool intentionalUp = IsIntentionalNavigateToFixedUi();

			// Stay on AkUndo while SetupGamepadPoints rebuilds list links.
			if (current == ControlsPadBHoldCancelFocus.UndoCancelPointId)
			{
				if (!_sectionConfirmAwaitingRebuild)
					ClearFocusRestoreSession();
				return false;
			}

			// Stay on Reset column while list links rebuild.
			if (current >= ResetPointsStart && current <= ResetPointsEndInclusive)
			{
				_focusResetPoint = current;
				if (!_sectionConfirmAwaitingRebuild)
					ClearFocusRestoreSession();
				return false;
			}

			string requested = _focusRestoreSessionActive || _sectionConfirmAwaitingRebuild
				? _savedFocusStableId
				: _pendingRestoreStableId ?? _lastKnownStableId;

			// Premature Setup (before FillList/OnActivate mutates the list): keep session, do not Clear.
			if (_sectionConfirmAwaitingRebuild && !_rebuildThisFrame && !onFixedUi)
				return false;

			bool needsRestore = !_catalogFailsafeActive
				&& (_rebuildThisFrame
					|| (_sectionConfirmAwaitingRebuild && onFixedUi && !string.IsNullOrEmpty(requested))
					|| (!string.IsNullOrEmpty(_pendingRestoreStableId) && onFixedUi && !intentionalUp));

			if (!needsRestore)
				return false;

			if (intentionalUp && onFixedUi && !_rebuildThisFrame && !_sectionConfirmAwaitingRebuild)
				return false;

			bool fallback;
			string matchReason;
			int restoreIndex = FindRestoreIndex(rows, requested, out fallback, out matchReason);
			if (restoreIndex < 0)
			{
				GamePadProbeSystem.LogNavRestoreFailed(requested ?? "?", "no-match");
				if (_focusRestoreSessionActive)
				{
					GamePadProbeSystem.LogAkSectionFocusVerify(
						_focusSaveEpoch,
						requested ?? "?",
						current,
						FormatPoint(current),
						success: false,
						fallbackUsed: true);
					ClearFocusRestoreSession();
				}

				return false;
			}

			int restorePoint = ListPointsStart + restoreIndex;
			// Do not clamp to FANCYUI_HIGHEST_INDEX — that remaps StableId to a different row.
			if (restorePoint > ListPointsEndInclusive
				|| !UILinkPointNavigator.Points.ContainsKey(restorePoint))
			{
				GamePadProbeSystem.LogNavRestoreFailed(requested ?? "?", "point-missing");
				ClearFocusRestoreSession();
				return false;
			}

			string resolvedStable = rows[restoreIndex].StableId ?? FormatPoint(restorePoint);
			bool exact = !string.IsNullOrEmpty(requested)
				&& string.Equals(requested, resolvedStable, StringComparison.Ordinal);

			if (_focusRestoreSessionActive || _rebuildThisFrame)
			{
				GamePadProbeSystem.LogAkControlsCatalogReady(
					_focusSaveEpoch != int.MinValue ? _focusSaveEpoch : FnaGamePadProvider.InputTickEpoch,
					requested ?? "?",
					restorePoint,
					resolvedStable,
					exact);
			}

			if (!exact && !string.IsNullOrEmpty(requested))
			{
				GamePadProbeSystem.LogAkSectionFocusMismatch(
					_focusSaveEpoch != int.MinValue ? _focusSaveEpoch : FnaGamePadProvider.InputTickEpoch,
					requested,
					resolvedStable,
					restorePoint,
					matchReason ?? "Unknown");
			}

			int fromPoint = current;
			string fromStable = onFixedUi
				? FormatPoint(current)
				: (_lastKnownStableId ?? FormatPoint(current));

			UILinkPointNavigator.ChangePoint(restorePoint);

			GamePadProbeSystem.LogAkSectionFocusRestore(
				_focusSaveEpoch != int.MinValue ? _focusSaveEpoch : FnaGamePadProvider.InputTickEpoch,
				fromPoint,
				fromStable,
				restorePoint,
				resolvedStable,
				exact,
				beforeDraw: true);

			// Success requires exact StableId match — PointId coincidence alone is not success.
			bool success = exact;
			if (_rebuildThisFrame || _focusRestoreSessionActive)
			{
				GamePadProbeSystem.LogNavRebuildEnd(resolvedStable);
				GamePadProbeSystem.LogRestoreResult(success: success, fallback: fallback || !exact);
				GamePadProbeSystem.LogAkSectionFocusVerify(
					_focusSaveEpoch != int.MinValue ? _focusSaveEpoch : FnaGamePadProvider.InputTickEpoch,
					requested ?? "?",
					restorePoint,
					resolvedStable,
					success,
					fallback || !exact);
			}

			_lastKnownStableId = resolvedStable;
			_lastKnownParentHeaderId = rows[restoreIndex].ParentHeaderId;
			_lastKnownListIndex = restoreIndex;
			_prevPoint = restorePoint;
			_pendingRestoreStableId = null;

			// Exact restore must NOT end the section-confirm session: ProcessPendingRebuild may call
			// OnActivate→ChangePoint(3002) on a later frame (often after Pad:A RELEASE).
			if (_sectionConfirmAwaitingRebuild && (_rebuildThisFrame || onFixedUi))
			{
				if (exact)
				{
					_sectionConfirmExactDone = true;
					GamePadProbeSystem.LogAkSectionConfirmExactRestore(
						_focusSaveEpoch,
						resolvedStable,
						restorePoint,
						FnaGamePadProvider.IsDown(GamePadToken.A),
						sessionActive: true);
				}

				_focusRestoreSessionActive = false;
			}
			else if (!_sectionConfirmAwaitingRebuild)
			{
				ClearFocusRestoreSession();
			}

			return success || fallback;
		}

		private static void ClearFocusRestoreSession()
		{
			_focusRestoreSessionActive = false;
			_sectionConfirmAwaitingRebuild = false;
			_sectionConfirmExactDone = false;
			_savedFocusStableId = null;
			_savedFocusParentId = null;
			_savedFocusPoint = int.MinValue;
			_savedFocusElementType = null;
			_savedFocusExpandedBefore = null;
			_focusRestoreLoggedRebuild = false;
			_focusSaveEpoch = int.MinValue;
			_sectionConfirmSawPadADown = false;
		}

		private static void EndSectionConfirmLifetime(string cause, bool movedToGamepadTab, bool fixedOk)
		{
			int finalPoint = SafeCurrentPoint();
			string finalStable = _lastKnownStableId ?? FormatPoint(finalPoint);
			string original = _savedFocusStableId ?? "?";
			GamePadProbeSystem.LogAkSectionConfirmLifetimeEnd(
				original,
				finalPoint,
				finalStable,
				movedToGamepadTab,
				cause ?? "?",
				fixedOk);
			ClearFocusRestoreSession();
		}

		private static bool ShouldHoldSectionFocusAgainstGamepadTabDefault(int before, int id)
		{
			if (id != GamepadTabPointId)
				return false;
			if (!_sectionConfirmAwaitingRebuild || string.IsNullOrEmpty(_savedFocusStableId))
				return false;
			if (!ControlsUiHost.IsManageControlsOpen() || ComboPanel.IsOpen)
				return false;
			if (IsIntentionalNavigateToFixedUi())
				return false;
			// Only the delayed OnActivate default GamepadTab focus — not every list→3002 move.
			if (!_duringOnActivate)
				return false;
			bool sameStable = string.Equals(
				_lastKnownStableId,
				_savedFocusStableId,
				StringComparison.Ordinal);
			if (sameStable)
				return true;
			// Before Exact, PointId/StableId may not match yet; still hold OnActivate default from list.
			if (!_sectionConfirmExactDone
				&& before >= ListPointsStart
				&& before <= ListPointsEndInclusive)
				return true;
			return false;
		}

		private static void LogSectionPostRelease3002(int from, int to, bool applied)
		{
		}


		private static int FindRestoreIndex(
			List<RowInfo> rows,
			string requestedStable,
			out bool fallback,
			out string matchReason)
		{
			fallback = false;
			matchReason = null;
			string stable = requestedStable ?? _pendingRestoreStableId ?? _lastKnownStableId;

			// 1. Exact StableId
			if (!string.IsNullOrEmpty(stable) && !stable.StartsWith("snap:", StringComparison.Ordinal))
			{
				for (int i = 0; i < rows.Count; i++)
				{
					if (rows[i].IsNavigable
						&& string.Equals(rows[i].StableId, stable, StringComparison.Ordinal))
					{
						matchReason = "ExactStableId";
						return i;
					}
				}
			}

			// 2. Same parent section header
			string parent = _savedFocusParentId ?? _pendingRestoreParentId ?? _lastKnownParentHeaderId;
			if (!string.IsNullOrEmpty(parent))
			{
				for (int i = 0; i < rows.Count; i++)
				{
					if (rows[i].IsNavigable
						&& rows[i].Kind == ControlRowKind.SectionHeader
						&& string.Equals(rows[i].StableId, parent, StringComparison.Ordinal))
					{
						fallback = true;
						matchReason = "ParentHeader";
						return i;
					}
				}
			}

			string headerGuess = TryHeaderFromStable(stable);
			if (!string.IsNullOrEmpty(headerGuess))
			{
				for (int i = 0; i < rows.Count; i++)
				{
					if (rows[i].IsNavigable
						&& rows[i].Kind == ControlRowKind.SectionHeader
						&& string.Equals(rows[i].StableId, headerGuess, StringComparison.Ordinal))
					{
						fallback = true;
						matchReason = "HeaderGuess";
						return i;
					}
				}
			}

			// 3. Nearest navigable SectionHeader around prior index (not any row — avoids wrong section body)
			int idx = _pendingRestoreListIndex >= 0 ? _pendingRestoreListIndex : _lastKnownListIndex;
			if (idx >= 0)
			{
				for (int d = 0; d < rows.Count; d++)
				{
					int a = idx - d;
					int b = idx + d;
					if (a >= 0 && a < rows.Count
						&& rows[a].IsNavigable
						&& rows[a].Kind == ControlRowKind.SectionHeader)
					{
						fallback = true;
						matchReason = "AdjacentHeader";
						return a;
					}

					if (b >= 0 && b < rows.Count
						&& rows[b].IsNavigable
						&& rows[b].Kind == ControlRowKind.SectionHeader)
					{
						fallback = true;
						matchReason = "AdjacentHeader";
						return b;
					}
				}
			}

			// 4. First navigable list row (never GamepadTab)
			for (int i = 0; i < rows.Count; i++)
			{
				if (rows[i].IsNavigable)
				{
					fallback = true;
					matchReason = "FirstNavigable";
					return i;
				}
			}

			matchReason = "NotFound";
			return -1;
		}

		private static string TryHeaderFromStable(string stable)
		{
			if (string.IsNullOrEmpty(stable))
				return null;
			if (stable.EndsWith(".Header", StringComparison.Ordinal))
				return stable;
			int dot = stable.IndexOf('.');
			if (dot > 0)
				return stable.Substring(0, dot) + ".Header";
			if (stable.StartsWith("bind:", StringComparison.Ordinal))
			{
				string rest = stable.Substring(5);
				int colon = rest.IndexOf(':');
				if (colon > 0)
					return SanitizeToken(rest.Substring(0, colon)) + ".Header";
			}

			return null;
		}

		private static bool IsIntentionalNavigateToFixedUi()
		{
			try
			{
				var jp = PlayerInput.Triggers?.JustPressed;
				if (jp?.KeyStatus == null)
					return false;
				return (jp.KeyStatus.TryGetValue("MenuUp", out bool up) && up)
					|| (jp.KeyStatus.TryGetValue("MenuLeft", out bool left) && left);
			}
			catch
			{
				return false;
			}
		}

		/// <summary>
		/// Link Up/Down within the same column for Thin left/right pairs; Left/Right across the pair.
		/// Full-width rows (Wide / headers) act as shared column anchors.
		/// </summary>
		private static void RebuildNavigableLinks(UIList uiList, List<RowInfo> rows)
		{
			if (uiList == null || rows.Count == 0)
				return;

			if (!UILinkPointNavigator.Points.TryGetValue(BackPointId, out UILinkPoint backPoint))
				return;

			// column: 0=left or full, 1=right horizontal sibling; partnerRow = pair sibling index or -1
			int[] column = new int[rows.Count];
			int[] partner = new int[rows.Count];
			int[] logicalRow = new int[rows.Count];
			for (int i = 0; i < rows.Count; i++)
			{
				column[i] = 0;
				partner[i] = -1;
				logicalRow[i] = -1;
			}

			int logical = 0;
			for (int i = 0; i < rows.Count; i++)
			{
				if (!rows[i].IsNavigable && !rows[i].IsHorizontalOnly)
					continue;

				if (rows[i].IsNavigable
					&& i + 1 < rows.Count
					&& rows[i + 1].IsHorizontalOnly
					&& IsGridPair(rows[i], rows[i + 1]))
				{
					column[i] = 0;
					column[i + 1] = 1;
					partner[i] = i + 1;
					partner[i + 1] = i;
					logicalRow[i] = logical;
					logicalRow[i + 1] = logical;
					GamePadProbeSystem.LogControlGridLink(
						ResolveRowActionLabel(rows[i]),
						ResolveRowActionLabel(rows[i + 1]),
						logical,
						ListPointsStart + i,
						ListPointsStart + i + 1);
					logical++;
					i++; // skip right half
					continue;
				}

				if (rows[i].IsNavigable)
				{
					column[i] = 0;
					logicalRow[i] = logical++;
				}
			}

			// Per-column ordered point lists (include Full/left navigable in both chains via col 0;
			// right chain = right halves + full-width navigables).
			var leftChain = new List<int>();
			var rightChain = new List<int>();
			for (int i = 0; i < rows.Count; i++)
			{
				if (!rows[i].IsNavigable && !rows[i].IsHorizontalOnly)
					continue;
				int pointId = ListPointsStart + i;
				if (pointId > ListPointsEndInclusive)
					break;

				bool isPairRight = rows[i].IsHorizontalOnly && partner[i] >= 0;
				bool isPairLeft = rows[i].IsNavigable && partner[i] >= 0;
				bool isFull = rows[i].IsNavigable && partner[i] < 0;

				if (isPairLeft || isFull)
					leftChain.Add(i);
				if (isPairRight || isFull)
					rightChain.Add(i);
			}

			int lastPoint = ListPointsStart - 1;

			void WirePoint(int rowIndex, int upId, int downId, int leftId, int rightId)
			{
				int pointId = ListPointsStart + rowIndex;
				if (pointId > ListPointsEndInclusive)
					return;
				if (!UILinkPointNavigator.Points.TryGetValue(pointId, out UILinkPoint point))
					return;

				point.Unlink();
				UILinkPointNavigator.SetPosition(pointId, rows[rowIndex].SnapPosition);
				point.Up = upId;
				point.Down = downId;
				if (leftId != int.MinValue)
					point.Left = leftId;
				if (rightId != int.MinValue)
					point.Right = rightId;

				UILinkPointNavigator.Shortcuts.FANCYUI_HIGHEST_INDEX = Math.Max(
					UILinkPointNavigator.Shortcuts.FANCYUI_HIGHEST_INDEX,
					pointId);
				backPoint.Up = pointId;
				lastPoint = Math.Max(lastPoint, pointId);

				if (rows[rowIndex].Kind == ControlRowKind.SectionHeader)
				{
					GamePadProbeSystem.LogControlSectionLink(
						rows[rowIndex].StableId,
						rows[rowIndex].Element?.GetType().Name,
						rows[rowIndex].SnapId,
						rowIndex,
						pointId,
						rows[rowIndex].IsNavigable,
						upId,
						downId,
						rows[rowIndex].SnapPosition.X,
						rows[rowIndex].SnapPosition.Y);
				}
			}

			int ChainPrev(List<int> chain, int pos)
				=> pos > 0 ? ListPointsStart + chain[pos - 1] : KeyboardTabPointId;
			int ChainNext(List<int> chain, int pos)
				=> pos < chain.Count - 1 ? ListPointsStart + chain[pos + 1] : BackPointId;

			for (int c = 0; c < leftChain.Count; c++)
			{
				int rowIndex = leftChain[c];
				int rightId = partner[rowIndex] >= 0 ? ListPointsStart + partner[rowIndex] : int.MinValue;
				WirePoint(rowIndex, ChainPrev(leftChain, c), ChainNext(leftChain, c), int.MinValue, rightId);
			}

			for (int c = 0; c < rightChain.Count; c++)
			{
				int rowIndex = rightChain[c];
				if (rows[rowIndex].IsNavigable && partner[rowIndex] < 0)
					continue; // full-width already wired on left chain

				int leftId = partner[rowIndex] >= 0 ? ListPointsStart + partner[rowIndex] : int.MinValue;
				int upId = ChainPrev(rightChain, c);
				int downId = ChainNext(rightChain, c);
				WirePoint(rowIndex, upId, downId, leftId, int.MinValue);
			}

			for (int id = lastPoint + 1; id <= ListPointsEndInclusive; id++)
			{
				if (!UILinkPointNavigator.Points.TryGetValue(id, out UILinkPoint leftover))
					break;
				leftover.Unlink();
			}

			for (int i = 0; i < rows.Count; i++)
			{
				if (rows[i].IsNavigable || rows[i].IsHorizontalOnly)
					continue;
				int id = ListPointsStart + i;
				if (id > ListPointsEndInclusive)
					break;
				if (UILinkPointNavigator.Points.TryGetValue(id, out UILinkPoint dead))
					dead.Unlink();
			}
		}

		private static bool IsGridPair(RowInfo left, RowInfo right)
		{
			if (left.Kind == ControlRowKind.SectionHeader || right.Kind == ControlRowKind.SectionHeader)
				return false;
			if (!string.Equals(left.SnapName, "Thin", StringComparison.Ordinal)
				|| !string.Equals(right.SnapName, "Thin", StringComparison.Ordinal))
				return false;
			if (left.Element == null || right.Element == null)
				return false;
			if (left.Element.Parent == null || !ReferenceEquals(left.Element.Parent, right.Element.Parent))
				return false;

			float yTol = Math.Max(4f, Math.Min(
				left.DimH > 0f ? left.DimH : 40f,
				right.DimH > 0f ? right.DimH : 40f) * 0.4f);
			if (Math.Abs(left.DimY - right.DimY) > yTol
				&& Math.Abs(left.SnapPosition.Y - right.SnapPosition.Y) > yTol)
				return false;
			if (right.DimX + 1f < left.DimX + (left.DimW > 0f ? left.DimW * 0.35f : 20f)
				&& right.SnapPosition.X + 1f < left.SnapPosition.X)
				return false;
			return true;
		}

		private static string ResolveRowActionLabel(RowInfo row)
		{
			if (!string.IsNullOrEmpty(row.StableId))
				return row.StableId;
			string action = KeybindingUiPatches.TryResolveActionIdFromRowElement(row.Element);
			return string.IsNullOrEmpty(action) ? (row.SnapName ?? "?") : action;
		}

		/// <summary>
		/// Detect right-stick manual scroll; suppress center-follow while active; sync focus on release.
		/// </summary>
		internal static void PollManualRightStickScroll()
		{
			PollResetColumnConfirm();
			PollManualRightStickScrollCore();
		}

		private static void PollManualRightStickScrollCore()
		{
			// ComboPanel open (including RS direction Capture): do not consume RightStickY as list scroll.
			if (!ControlsUiHost.IsManageControlsOpen() || ComboPanel.IsOpen)
			{
				if (_manualScrollActive)
					_manualScrollActive = false;
				return;
			}

			var snap = FnaGamePadProvider.Current;
			if (snap == null || !snap.IsConnected)
			{
				if (_manualScrollActive)
				{
					TrySyncFocusAfterManualScroll();
					_manualScrollActive = false;
				}
				return;
			}

			float y = snap.RightStickY;
			bool active = Math.Abs(y) >= RightStickScrollDeadZone;
			if (active)
			{
				if (!_manualScrollActive)
				{
					_manualScrollActive = true;
					_manualScrollPreferredColumn = 0;
					int cur = UILinkPointNavigator.CurrentPoint;
					int idx = cur - ListPointsStart;
					if (ControlsUiHost.TryGetManageControls(out UIState st, out _)
						&& _uilistField?.GetValue(st) is UIList list)
					{
						try
						{
							List<RowInfo> rowsNow = BuildRowCatalog(list);
							if (idx >= 0 && idx < rowsNow.Count && rowsNow[idx].IsHorizontalOnly)
								_manualScrollPreferredColumn = 1;
						}
						catch { /* ignore */ }
					}

					GamePadProbeSystem.LogControlManualScrollBegin(_manualScrollPreferredColumn);
				}

				// Vanilla / host scrolls UIList while RS is held — remember for rebuild restore.
				if (ControlsUiHost.TryGetManageControls(out UIState stScroll, out _)
					&& _uilistField?.GetValue(stScroll) is UIList listScroll)
				{
					NoteSessionListViewPositionFromList(listScroll);
				}

				return;
			}

			if (_manualScrollActive)
			{
				TrySyncFocusAfterManualScroll();
				_manualScrollActive = false;
				if (ControlsUiHost.TryGetManageControls(out UIState stEnd, out _)
					&& _uilistField?.GetValue(stEnd) is UIList listEnd)
				{
					NoteSessionListViewPositionFromList(listEnd);
				}

				GamePadProbeSystem.LogControlManualScrollEnd(UILinkPointNavigator.CurrentPoint);
			}
		}

		private static void TrySyncFocusAfterManualScroll()
		{
			if (!ControlsUiHost.TryGetManageControls(out UIState state, out _))
				return;
			if (_uilistField == null || state == null)
				return;
			if (_uilistField.GetValue(state) is not UIList uiList)
				return;

			List<RowInfo> rows;
			try { rows = BuildRowCatalog(uiList); }
			catch { return; }

			CalculatedStyle view;
			try { view = uiList.GetInnerDimensions(); }
			catch { return; }

			float viewTop = view.Y;
			float viewBottom = view.Y + view.Height;
			float viewCenterY = view.Y + view.Height * 0.5f;

			int bestBinding = -1;
			float bestBindingDist = float.MaxValue;
			int bestOther = -1;
			float bestOtherDist = float.MaxValue;
			int bestHeader = -1;
			float bestHeaderDist = float.MaxValue;

			for (int i = 0; i < rows.Count; i++)
			{
				RowInfo row = rows[i];
				bool use = false;
				if (_manualScrollPreferredColumn == 1 && row.IsHorizontalOnly)
					use = true;
				else if (_manualScrollPreferredColumn != 1 && row.IsNavigable)
					use = true;
				else if (row.IsNavigable
					&& (i + 1 >= rows.Count || !rows[i + 1].IsHorizontalOnly || !IsGridPair(row, rows[i + 1])))
					use = true; // full-width always candidate

				if (!use)
					continue;

				float cy = row.DimH > 1f ? row.DimY + row.DimH * 0.5f : row.SnapPosition.Y;
				if (cy < viewTop - 8f || cy > viewBottom + 8f)
					continue;

				float dist = Math.Abs(cy - viewCenterY);
				int pointId = ListPointsStart + i;
				if (pointId > ListPointsEndInclusive)
					continue;

				if (row.Kind == ControlRowKind.SectionHeader)
				{
					if (dist < bestHeaderDist)
					{
						bestHeaderDist = dist;
						bestHeader = pointId;
					}
				}
				else if (row.Kind == ControlRowKind.Binding || row.Element is UIKeybindingListItem)
				{
					if (dist < bestBindingDist)
					{
						bestBindingDist = dist;
						bestBinding = pointId;
					}
				}
				else if (dist < bestOtherDist)
				{
					bestOtherDist = dist;
					bestOther = pointId;
				}
			}

			int choose = bestBinding >= 0 ? bestBinding
				: bestOther >= 0 ? bestOther
				: bestHeader;
			if (choose < 0)
				return;

			int beforePoint = SafeCurrentPoint();
			string beforeStable = _lastKnownStableId ?? FormatPoint(beforePoint);
			string selectedStable = "?";
			int selIdx = choose - ListPointsStart;
			if (selIdx >= 0 && selIdx < rows.Count)
				selectedStable = rows[selIdx].StableId ?? FormatPoint(choose);

			_manualScrollSelectedPoint = choose;
			_manualScrollSelectedStableId = selectedStable;

			float stickY = 0f;
			try
			{
				var pad = FnaGamePadProvider.Current;
				if (pad != null && pad.IsConnected)
					stickY = pad.RightStickY;
			}
			catch { /* ignore */ }

			try
			{
				UILinkPointNavigator.ChangePoint(choose);
				_scrolledForPoint = choose;
			}
			catch
			{
				// ignore
			}

			GamePadProbeSystem.LogAkManualScrollFocusSync(
				FnaGamePadProvider.InputTickEpoch,
				beforePoint,
				beforeStable,
				stickY,
				choose,
				selectedStable,
				SafeCurrentPoint(),
				_sectionConfirmAwaitingRebuild || _focusRestoreSessionActive);
		}

		private static void CenterFollowOrFallback(UIList uiList, List<RowInfo> rows, bool restoredThisFrame)
		{
			if (_manualScrollActive)
				return;
			if (_catalogFailsafeActive)
				return;
			int current = UILinkPointNavigator.CurrentPoint;

			bool fromResetColumn = current >= ResetPointsStart && current <= ResetPointsEndInclusive;
			bool fromBindingColumn = current >= ListPointsStart && current <= ListPointsEndInclusive;
			if (!fromResetColumn && !fromBindingColumn)
				return;
			if (current > UILinkPointNavigator.Shortcuts.FANCYUI_HIGHEST_INDEX)
				return;

			bool focusChanged = current != _prevPoint && _prevPoint != int.MinValue;
			bool allowCenter = ControlsListScrollSessionPolicy.ShouldAllowCenterFollow(
				_manualScrollActive,
				focusChanged,
				restoredThisFrame,
				_rebuildThisFrame,
				_pendingResetVerticalScroll);
			bool edgeAfterRebuild = ControlsListScrollSessionPolicy.ShouldEnsureEdgeVisibilityAfterRebuild(
				_rebuildThisFrame,
				restoredThisFrame);
			if (!allowCenter && !edgeAfterRebuild)
				return;
			if (!restoredThisFrame && !_rebuildThisFrame && current == _scrolledForPoint)
				return;

			int rowIndex = fromResetColumn
				? current - ResetPointsStart
				: current - ListPointsStart;
			if (rowIndex < 0 || rowIndex >= rows.Count)
			{
				if (fromResetColumn)
				{
					GamePadProbeSystem.LogControlResetScrollSkipped(
						ResolveResetOrBindingAction(current),
						"NoElement");
				}

				return;
			}

			if (!rows[rowIndex].IsNavigable && !rows[rowIndex].IsHorizontalOnly)
			{
				if (fromResetColumn)
				{
					GamePadProbeSystem.LogControlResetScrollSkipped(
						ResolveResetOrBindingAction(current),
						"InvalidDimensions");
				}

				return;
			}

			RowInfo row = rows[rowIndex];
			string targetId = row.StableId ?? FormatPoint(current);

			if (fromResetColumn && focusChanged)
			{
				GamePadProbeSystem.LogControlResetVerticalNav(
					token: "Navigator",
					fromAction: ResolveResetOrBindingAction(_prevPoint),
					toAction: ResolveResetOrBindingAction(current),
					fromPoint: _prevPoint,
					toPoint: current);
			}

			// Rebuild / ComboPanel return: keep session ViewPosition; only nudge if selection is off-screen.
			if (!allowCenter && edgeAfterRebuild)
			{
				TryEdgeVisibilityFallback(uiList, row, current, targetId);
				NoteSessionListViewPositionFromList(uiList);
				_pendingResetVerticalScroll = false;
				return;
			}

			if (!TryGetListViewport(uiList, out float viewportTop, out float viewportHeight))
			{
				LogCenterUnavailableOnce("UIList.GetInnerDimensions unavailable or Height<=0");
				if (fromResetColumn)
					GamePadProbeSystem.LogControlResetScrollSkipped(targetId, "NoScrollHost");
				TryEdgeVisibilityFallback(uiList, row, current, targetId);
				NoteSessionListViewPositionFromList(uiList);
				_pendingResetVerticalScroll = false;
				return;
			}

			if (!TryGetTargetCenterY(row, out float targetCenterY, out bool measuredRect))
			{
				LogCenterUnavailableOnce("target GetDimensions unavailable; using edge fallback");
				if (fromResetColumn)
					GamePadProbeSystem.LogControlResetScrollSkipped(targetId, "InvalidDimensions");
				TryEdgeVisibilityFallback(uiList, row, current, targetId);
				NoteSessionListViewPositionFromList(uiList);
				_pendingResetVerticalScroll = false;
				return;
			}

			if (!measuredRect)
			{
				if (fromResetColumn)
					GamePadProbeSystem.LogControlResetScrollSkipped(targetId, "InvalidDimensions");
				TryEdgeVisibilityFallback(uiList, row, current, targetId);
				NoteSessionListViewPositionFromList(uiList);
				_pendingResetVerticalScroll = false;
				return;
			}

			float viewportCenter = viewportTop + viewportHeight * 0.5f;
			float delta = targetCenterY - viewportCenter;
			float oldView = uiList.ViewPosition;
			float newView = oldView + delta;

			float maxView = GetMaxViewPosition(uiList, viewportHeight);
			string clamp = null;
			if (newView < 0f)
			{
				newView = 0f;
				clamp = "Top";
			}
			else if (newView > maxView)
			{
				newView = maxView;
				clamp = "Bottom";
			}

			newView = ControlsListScrollSessionPolicy.ClampViewPosition(newView, maxView);

			if (Math.Abs(newView - oldView) < 0.5f)
			{
				_scrolledForPoint = current;
				if (fromResetColumn)
				{
					GamePadProbeSystem.LogControlResetScrollSkipped(targetId, "AlreadyVisible");
					GamePadProbeSystem.LogControlResetCenterScroll(
						targetId,
						current,
						targetTop: targetCenterY - (row.DimH > 0f ? row.DimH * 0.5f : 0f),
						targetCenter: targetCenterY,
						viewportTop: viewportTop,
						viewportBottom: viewportTop + viewportHeight,
						oldScroll: oldView,
						newScroll: newView,
						clamped: false,
						sharedWithBindingColumn: true);
				}

				_pendingResetVerticalScroll = false;
				NoteSessionListViewPosition(newView);
				return;
			}

			NoteScrollEpoch(targetId);
			uiList.ViewPosition = newView;
			NoteSessionListViewPosition(newView);
			_scrolledForPoint = current;
			_pendingResetVerticalScroll = false;

			uiList.Recalculate();
			float afterCenter = targetCenterY;
			bool afterOk = false;
			if (row.Element != null)
			{
				try
				{
					CalculatedStyle d = row.Element.GetDimensions();
					if (d.Width > MinStructuralSizeUi && d.Height > MinStructuralSizeUi)
					{
						afterCenter = d.Y + d.Height * 0.5f;
						afterOk = true;
					}
				}
				catch
				{
					afterOk = false;
				}
			}

			if (afterOk
				&& TryGetListViewport(uiList, out float vt2, out float vh2)
				&& (afterCenter < vt2 - 1f || afterCenter > vt2 + vh2 + 1f))
			{
				GamePadProbeSystem.LogControlNavSelectedNotVisible(targetId);
			}

			GamePadProbeSystem.LogControlNavCenterScroll(
				targetId,
				oldView,
				newView,
				clamped: clamp != null,
				clamp);

			if (fromResetColumn)
			{
				GamePadProbeSystem.LogControlResetCenterScroll(
					targetId,
					current,
					targetTop: targetCenterY - (row.DimH > 0f ? row.DimH * 0.5f : 0f),
					targetCenter: targetCenterY,
					viewportTop: viewportTop,
					viewportBottom: viewportTop + viewportHeight,
					oldScroll: oldView,
					newScroll: newView,
					clamped: clamp != null,
					sharedWithBindingColumn: true);
			}
		}

		private static void TryEdgeVisibilityFallback(UIList uiList, RowInfo row, int current, string targetId)
		{
			if (!TryGetListViewport(uiList, out float viewportTop, out float viewportHeight))
				return;

			float y = row.DimH > MinStructuralSizeUi
				? row.DimY
				: row.SnapPosition.Y;
			float h = row.DimH > MinStructuralSizeUi ? row.DimH : 20f;
			float bottom = y + h;

			float oldView = uiList.ViewPosition;
			float newView = oldView;
			bool scrolled = false;

			if (bottom > viewportTop + viewportHeight - EdgeFallbackMarginUi)
			{
				newView = oldView + (bottom - (viewportTop + viewportHeight - EdgeFallbackMarginUi));
				scrolled = true;
			}
			else if (y < viewportTop + EdgeFallbackMarginUi)
			{
				newView = oldView - ((viewportTop + EdgeFallbackMarginUi) - y);
				scrolled = true;
			}

			if (!scrolled)
			{
				_scrolledForPoint = current;
				NoteSessionListViewPositionFromList(uiList);
				return;
			}

			float maxView = GetMaxViewPosition(uiList, viewportHeight);
			string clamp = null;
			if (newView < 0f)
			{
				newView = 0f;
				clamp = "Top";
			}
			else if (newView > maxView)
			{
				newView = maxView;
				clamp = "Bottom";
			}

			newView = ControlsListScrollSessionPolicy.ClampViewPosition(newView, maxView);

			if (Math.Abs(newView - oldView) < 0.5f)
			{
				_scrolledForPoint = current;
				NoteSessionListViewPosition(newView);
				return;
			}

			NoteScrollEpoch(targetId);
			uiList.ViewPosition = newView;
			NoteSessionListViewPosition(newView);
			_scrolledForPoint = current;
			GamePadProbeSystem.LogControlNavCenterScroll(targetId, oldView, newView, clamp != null, clamp);
		}

		private static void NoteScrollEpoch(string targetId)
		{
			int epoch = FnaGamePadProvider.InputTickEpoch;
			if (epoch != _scrollEpoch)
			{
				_scrollEpoch = epoch;
				_scrollCountThisEpoch = 0;
				_scrollTargetThisEpoch = null;
			}

			if (string.Equals(targetId, _scrollTargetThisEpoch, StringComparison.Ordinal))
			{
				_scrollCountThisEpoch++;
				if (_scrollCountThisEpoch >= 2)
					GamePadProbeSystem.LogScrollDuplicate(targetId, epoch);
			}
			else
			{
				_scrollTargetThisEpoch = targetId;
				_scrollCountThisEpoch = 1;
			}
		}

		private static void LogCenterUnavailableOnce(string reason)
		{
			if (_centerScrollUnavailableLogged)
				return;
			_centerScrollUnavailableLogged = true;
			AdvancedKeybinds.Instance?.Logger.Warn(
				"ControlsGamepadAutoScrollPatches: center-follow unavailable — " + reason
				+ " (using edge visibility fallback; see Report.md).");
		}

		private static bool TryGetListViewport(UIList uiList, out float top, out float height)
		{
			top = 0f;
			height = 0f;
			if (uiList == null)
				return false;
			try
			{
				CalculatedStyle inner = uiList.GetInnerDimensions();
				if (inner.Height <= MinStructuralSizeUi || float.IsNaN(inner.Height))
					return false;
				top = inner.Y;
				height = inner.Height;
				return true;
			}
			catch
			{
				return false;
			}
		}

		private static bool TryGetTargetCenterY(RowInfo row, out float centerY, out bool measuredRect)
		{
			centerY = 0f;
			measuredRect = false;
			if (row.DimW > MinStructuralSizeUi && row.DimH > MinStructuralSizeUi
				&& !float.IsNaN(row.DimY) && !float.IsNaN(row.DimH))
			{
				centerY = row.DimY + row.DimH * 0.5f;
				measuredRect = true;
				return true;
			}

			if (row.Element != null)
			{
				try
				{
					CalculatedStyle d = row.Element.GetDimensions();
					if (d.Width > MinStructuralSizeUi && d.Height > MinStructuralSizeUi)
					{
						centerY = d.Y + d.Height * 0.5f;
						measuredRect = true;
						return true;
					}
				}
				catch
				{
					// fall through
				}
			}

			centerY = row.SnapPosition.Y;
			return true;
		}

		private static float GetMaxViewPosition(UIList uiList, float viewportHeight)
		{
			float content = TryGetTotalHeight(uiList);
			if (content <= MinStructuralSizeUi)
				return Math.Max(0f, uiList.ViewPosition);
			return Math.Max(0f, content - viewportHeight);
		}

		private static float TryGetTotalHeight(UIList uiList)
		{
			if (uiList == null)
				return 0f;
			try
			{
				if (_getTotalHeightMethod != null
					&& _getTotalHeightMethod.Invoke(uiList, null) is float h
					&& !float.IsNaN(h))
					return h;
			}
			catch
			{
				// ignore
			}

			return 0f;
		}

		private static List<RowInfo> BuildRowCatalog(UIList uiList)
		{
			var rows = new List<RowInfo>(64);
			List<SnapPoint> snaps = uiList.GetSnapPoints();
			if (snaps == null || snaps.Count == 0)
				return rows;

			// Visual / layout order (not Snap Id). Header snaps allocated late get high Ids but
			// must sit above their children for DPad vertical nav.
			snaps.Sort(CompareSnapVisualOrder);

			var elementByRef = new Dictionary<SnapPoint, UIElement>(ReferenceEqualityComparer.Instance);
			var elementById = new Dictionary<int, UIElement>();
			int visitedElements = 0;
			CollectSnapElements(uiList, elementByRef, elementById, ref visitedElements);
			int elementMapCount = elementByRef.Count;
			var headerStableOccurrence = new Dictionary<string, int>(StringComparer.Ordinal);

			int mapped = 0;
			int unmapped = 0;
			int hasDimensions = 0;
			var sampleBuf = new List<string>(10);

			// Precompute collapsed top-level section roots (only when we can measure children).
			var collapsedRoots = new HashSet<UIElement>();
			foreach (UIElement child in ControlsSectionHeaderSupport.EnumerateUiListItems(uiList))
			{
				try
				{
					CalculatedStyle d = child.GetDimensions();
					if (d.Height > MinStructuralSizeUi && d.Height <= CollapsedSectionMaxHeightUi)
						collapsedRoots.Add(child);
				}
				catch
				{
					// ignore
				}
			}

			string currentHeader = null;
			bool currentHeaderExpanded = true;
			var seenElements = new HashSet<int>();
			int logicalOrder = 0;
			var skippedLogBuffer = new List<(string parent, string child)>(8);

			for (int i = 0; i < snaps.Count; i++)
			{
				SnapPoint snap = snaps[i];
				UIElement el = null;
				if (!elementByRef.TryGetValue(snap, out el))
					elementById.TryGetValue(snap.Id, out el);

				bool elementFound = el != null;
				if (elementFound)
					mapped++;
				else
					unmapped++;

				float dimX = 0f, dimY = 0f, dimW = 0f, dimH = 0f;
				bool hasDim = false;
				if (el != null)
				{
					try
					{
						CalculatedStyle d = el.GetDimensions();
						dimX = d.X;
						dimY = d.Y;
						dimW = d.Width;
						dimH = d.Height;
						hasDim = dimW > MinStructuralSizeUi && dimH > MinStructuralSizeUi;
						if (hasDim)
							hasDimensions++;
					}
					catch
					{
						hasDim = false;
					}
				}

				if (sampleBuf.Count < 10)
				{
					sampleBuf.Add(
						"SnapIndex=" + i.ToString(CultureInfo.InvariantCulture)
						+ " | SnapId=" + snap.Id.ToString(CultureInfo.InvariantCulture)
						+ " | SnapName=" + (snap.Name ?? "-")
						+ " | ElementFound=" + (elementFound ? "True" : "False")
						+ " | ElementType=" + (el != null ? el.GetType().Name : "-")
						+ " | Width=" + (hasDim ? dimW.ToString("0.#", CultureInfo.InvariantCulture) : "-")
						+ " | Height=" + (hasDim ? dimH.ToString("0.#", CultureInfo.InvariantCulture) : "-"));
				}

				ControlRowKind kind = ControlRowKind.Unknown;
				bool? headerExpanded = null;
				string stable;
				string parentHeader;
				bool isHeader;
				UIElement nestedKeybind = null;
				string nestedActionId = null;

				if (ControlsSectionHeaderSupport.TryClassifySectionHeader(
					el,
					snap,
					uiList,
					out string sectionStable,
					out _,
					out headerExpanded))
				{
					kind = ControlRowKind.SectionHeader;
					stable = sectionStable;
					isHeader = true;
					parentHeader = null;
					currentHeader = sectionStable;
					currentHeaderExpanded = headerExpanded ?? !collapsedRoots.Contains(FindOuterListItem(el, uiList) ?? el);
				}
				else
				{
					stable = ResolveStableId(
						el,
						snap,
						ref currentHeader,
						ref currentHeaderExpanded,
						out parentHeader,
						out isHeader);
					nestedKeybind = FindKeybindListItemInSubtree(el);
					if (nestedKeybind != null)
						nestedActionId = KeybindingUiPatches.TryResolveActionIdFromRowElement(nestedKeybind);

					if (isHeader)
						kind = ControlRowKind.SectionHeader;
					else if (stable != null && stable.EndsWith(".DefaultReset", StringComparison.Ordinal))
						kind = ControlRowKind.DefaultReset;
					else if (nestedKeybind != null || el is UIKeybindingListItem || !string.IsNullOrEmpty(nestedActionId))
					{
						// Keybind rows under Mod HeaderElement / section wrappers stay Binding.
						// ParentHeaderId preserves SectionChild relationship without excluding Binding.
						kind = ControlRowKind.Binding;
						if ((string.IsNullOrEmpty(stable) || stable.StartsWith("snap:", StringComparison.Ordinal))
							&& !string.IsNullOrEmpty(nestedActionId))
							stable = BuildStableIdForBind(nestedActionId, parentHeader);
					}
					else if (!string.IsNullOrEmpty(parentHeader))
						kind = ControlRowKind.SectionChild;
					else
						kind = ControlRowKind.Unknown;
				}

				UIElement outerItem = FindOuterListItem(el, uiList);
				bool sectionCollapsed = outerItem != null && collapsedRoots.Contains(outerItem);

				bool zeroSize = false;
				if (el != null)
				{
					try
					{
						CalculatedStyle d = el.GetDimensions();
						zeroSize = d.Width <= MinStructuralSizeUi || d.Height <= MinStructuralSizeUi;
					}
					catch
					{
						zeroSize = false;
					}
				}

				bool ancestorCollapsed = el != null && IsUnderCollapsedAncestor(el, uiList, collapsedRoots);
				bool provenCollapsedChild = false;
				if (sectionCollapsed && !isHeader && !IsDirectSectionChrome(el, outerItem))
					provenCollapsedChild = true;
				if (ancestorCollapsed && !IsDirectSectionChrome(el, outerItem))
					provenCollapsedChild = true;
				if (el != null && zeroSize && !IsDirectSectionChrome(el, outerItem) && !isHeader)
					provenCollapsedChild = true;

				bool duplicate = false;
				if (el != null)
				{
					int uid = el.UniqueId;
					if (!string.Equals(snap.Name, "Thin", StringComparison.Ordinal)
						&& !seenElements.Add(uid))
						duplicate = true;
				}

				ControlRowKind prevKind = i > 0 ? rows[i - 1].Kind : ControlRowKind.Unknown;
				UIElement prevEl = i > 0 ? rows[i - 1].Element : null;
				SnapPoint prevSnap = i > 0 ? snaps[i - 1] : null;
				bool isRightThinHalf = ControlsSectionHeaderSupport.IsStructuralRightThinHalf(
					snap,
					el,
					kind,
					prevSnap,
					prevEl,
					prevKind);
				if (isRightThinHalf)
					kind = ControlRowKind.HorizontalRight;

				if (kind == ControlRowKind.SectionHeader && !string.IsNullOrEmpty(stable))
				{
					headerStableOccurrence.TryGetValue(stable, out int occ);
					occ++;
					headerStableOccurrence[stable] = occ;
					if (occ > 1)
					{
						// Disambiguate duplicate base ids without using ephemeral UniqueId.
						string baseId = stable.EndsWith(".Header", StringComparison.Ordinal)
							? stable.Substring(0, stable.Length - ".Header".Length)
							: stable;
						stable = baseId + ".N" + occ.ToString(CultureInfo.InvariantCulture) + ".Header";
					}
				}

				bool visibleByStructure = !provenCollapsedChild;
				bool isExpanded = isHeader
					? (headerExpanded ?? !sectionCollapsed)
					: currentHeaderExpanded && !provenCollapsedChild;

				// Fail-open: Snap alone is navigable unless positively proven excluded.
				// Section headers are always navigable unless clear duplicate / decorative.
				bool navigable = !duplicate && !isRightThinHalf && !provenCollapsedChild;
				if (kind == ControlRowKind.SectionHeader && !duplicate)
				{
					navigable = true;
					provenCollapsedChild = false;
					visibleByStructure = true;
					isRightThinHalf = false;
				}

				if ((isHeader && sectionCollapsed || IsDirectSectionChrome(el, outerItem))
					&& !duplicate)
				{
					navigable = true;
					provenCollapsedChild = false;
					visibleByStructure = true;
				}

				if (!navigable && provenCollapsedChild && !string.IsNullOrEmpty(parentHeader) && !isRightThinHalf)
					skippedLogBuffer.Add((parentHeader, stable));

				if (isHeader)
					currentHeaderExpanded = isExpanded;

				if (kind == ControlRowKind.Binding || nestedKeybind != null)
				{
					MaybeLogModBindingClassify(
						ListPointsStart + rows.Count,
						el,
						nestedKeybind ?? (el is UIKeybindingListItem ? el : null),
						nestedActionId,
						parentHeader,
						kind,
						navigable);
				}

				rows.Add(new RowInfo
				{
					SnapId = snap.Id,
					SnapName = snap.Name,
					SnapPosition = snap.Position,
					Element = el,
					StableId = stable,
					ParentHeaderId = parentHeader,
					IsHeader = isHeader || kind == ControlRowKind.SectionHeader,
					Kind = kind,
					IsExpanded = isExpanded,
					IsVisibleByStructure = visibleByStructure,
					IsInsideCollapsedGroup = provenCollapsedChild,
					IsNavigable = navigable,
					IsHorizontalOnly = isRightThinHalf && !provenCollapsedChild && kind != ControlRowKind.SectionHeader,
					LogicalOrder = navigable ? logicalOrder++ : -1,
					DimX = dimX,
					DimY = dimY,
					DimW = dimW,
					DimH = dimH
				});
			}

			LogMapSummaryIfChanged(
				snaps.Count,
				elementMapCount,
				mapped,
				unmapped,
				hasDimensions,
				CountNavigable(rows),
				sampleBuf);

			FlushSkippedChildLogs(skippedLogBuffer);
			return rows;
		}

		private static int CompareSnapVisualOrder(SnapPoint a, SnapPoint b)
		{
			if (ReferenceEquals(a, b))
				return 0;
			if (a == null)
				return 1;
			if (b == null)
				return -1;

			int cy = a.Position.Y.CompareTo(b.Position.Y);
			if (cy != 0)
				return cy;
			int cx = a.Position.X.CompareTo(b.Position.X);
			if (cx != 0)
				return cx;
			return a.Id.CompareTo(b.Id);
		}

		private static void LogMapSummaryIfChanged(
			int totalSnap,
			int elementMapCount,
			int mapped,
			int unmapped,
			int hasDimensions,
			int navigable,
			List<string> samples)
		{
			string key = totalSnap + "|" + elementMapCount + "|" + mapped + "|" + unmapped + "|" + hasDimensions + "|" + navigable;
			if (string.Equals(key, _lastMapSummaryKey, StringComparison.Ordinal))
				return;
			_lastMapSummaryKey = key;

			GamePadProbeSystem.LogControlRowMapSummary(
				totalSnap,
				elementMapCount,
				mapped,
				unmapped,
				hasDimensions,
				navigable);

			if (samples == null)
				return;
			for (int i = 0; i < samples.Count; i++)
				GamePadProbeSystem.LogControlRowMapSample(samples[i]);
		}

		private static void FlushSkippedChildLogs(List<(string parent, string child)> skipped)
		{
			if (skipped == null || skipped.Count == 0)
				return;

			var sb = new StringBuilder(skipped.Count * 16);
			for (int i = 0; i < skipped.Count; i++)
				sb.Append(skipped[i].parent).Append('>').Append(skipped[i].child).Append(';');
			string key = sb.ToString();
			if (string.Equals(key, _lastSkippedLogKey, StringComparison.Ordinal))
				return;
			_lastSkippedLogKey = key;

			int limit = Math.Min(12, skipped.Count);
			for (int i = 0; i < limit; i++)
			{
				GamePadProbeSystem.LogControlAccordionChildSkipped(
					skipped[i].parent,
					skipped[i].child,
					"collapsed");
			}
		}

		private static bool IsDirectSectionChrome(UIElement el, UIElement outerItem)
		{
			if (el == null || outerItem == null)
				return false;
			if (ReferenceEquals(el, outerItem))
				return true;
			if (el.Parent != null && ReferenceEquals(el.Parent, outerItem))
				return true;
			string name = el.GetType().Name;
			return name.IndexOf("SectionCollapse", StringComparison.OrdinalIgnoreCase) >= 0
				|| name.IndexOf("CollapseHeader", StringComparison.OrdinalIgnoreCase) >= 0;
		}

		private static UIElement FindOuterListItem(UIElement el, UIList outerList)
		{
			for (UIElement cur = el; cur != null; cur = cur.Parent)
			{
				if (cur.Parent != null && ReferenceEquals(cur.Parent, outerList))
					return cur;
			}

			return null;
		}

		private static bool IsUnderCollapsedAncestor(
			UIElement el,
			UIList outerList,
			HashSet<UIElement> collapsedRoots)
		{
			for (UIElement cur = el; cur != null && !ReferenceEquals(cur, outerList); cur = cur.Parent)
			{
				if (collapsedRoots.Contains(cur))
					return true;
				try
				{
					CalculatedStyle d = cur.GetDimensions();
					if (d.Height <= MinStructuralSizeUi && !ReferenceEquals(cur, el))
						return true;
				}
				catch
				{
					// ignore
				}
			}

			return false;
		}

		private static IEnumerable<UIElement> EnumerateChildren(UIElement root)
		{
			List<UIElement> list = null;
			try
			{
				// Prefer public Children; fall back to protected Elements.
				if (_childrenProp?.GetValue(root) is IEnumerable<UIElement> viaChildren)
					list = new List<UIElement>(viaChildren);
				else if (_elementsProp?.GetValue(root) is IEnumerable<UIElement> viaElements)
					list = new List<UIElement>(viaElements);
			}
			catch
			{
				yield break;
			}

			if (list == null)
				yield break;
			for (int i = 0; i < list.Count; i++)
				yield return list[i];
		}

		/// <summary>
		/// Maps SnapPoint → owning UIElement using official GetSnapPoint (same instance as GetSnapPoints),
		/// with Id and optional _snapPoint field as secondary.
		/// </summary>
		private static void CollectSnapElements(
			UIElement root,
			Dictionary<SnapPoint, UIElement> byRef,
			Dictionary<int, UIElement> byId,
			ref int visitCount)
		{
			if (root == null)
				return;

			visitCount++;
			TryRegisterSnapOwner(root, byRef, byId);

			foreach (UIElement child in EnumerateChildren(root))
				CollectSnapElements(child, byRef, byId, ref visitCount);
		}

		private static void TryRegisterSnapOwner(
			UIElement el,
			Dictionary<SnapPoint, UIElement> byRef,
			Dictionary<int, UIElement> byId)
		{
			SnapPoint sp = null;

			// 1) Official API — same SnapPoint instance returned by GetSnapPoints().
			if (_getSnapPointMethod != null)
			{
				try
				{
					object[] args = { null };
					object result = _getSnapPointMethod.Invoke(el, args);
					if (result is true && args[0] is SnapPoint fromApi)
						sp = fromApi;
				}
				catch
				{
					sp = null;
				}
			}

			// 2) Direct call if method resolve failed but type has it (compile-time).
			if (sp == null)
			{
				try
				{
					if (el.GetSnapPoint(out SnapPoint direct))
						sp = direct;
				}
				catch
				{
					sp = null;
				}
			}

			// 3) Legacy field (may be missing on current tML).
			if (sp == null && _snapPointFieldExists)
			{
				try
				{
					if (_snapPointField.GetValue(el) is SnapPoint fromField)
						sp = fromField;
				}
				catch
				{
					sp = null;
				}
			}

			if (sp == null)
				return;

			byRef[sp] = el;
			byId[sp.Id] = el;
		}

		/// <summary>
		/// Fail-safe: link every SnapPoint in order (previous auto-scroll behaviour) when catalog yields Navigable=0.
		/// </summary>
		private static void RebuildFullSnapLinks(UIList uiList)
		{
			if (uiList == null)
				return;

			List<SnapPoint> snapPoints = uiList.GetSnapPoints();
			if (snapPoints == null || snapPoints.Count == 0)
				return;

			snapPoints.Sort((a, b) => a.Id.CompareTo(b.Id));

			if (!UILinkPointNavigator.Points.TryGetValue(BackPointId, out UILinkPoint backPoint))
				return;

			int lastListPointId = ListPointsStart - 1;

			for (int j = 0; j < snapPoints.Count; j++)
			{
				int key = ListPointsStart + j;
				if (key > ListPointsEndInclusive)
					break;
				if (!UILinkPointNavigator.Points.TryGetValue(key, out UILinkPoint point))
					break;

				SnapPoint snap = snapPoints[j];
				point.Unlink();
				UILinkPointNavigator.SetPosition(key, snap.Position);

				if (string.Equals(snap.Name, "Thin", StringComparison.Ordinal))
				{
					point.Right = key + 1;
					point.Down = j < snapPoints.Count - 2 ? key + 2 : BackPointId;
					point.Up = j < 2
						? KeyboardTabPointId
						: string.Equals(snapPoints[j - 1].Name, "Wide", StringComparison.Ordinal)
							? key - 1
							: key - 2;
					backPoint.Up = key;
					UILinkPointNavigator.Shortcuts.FANCYUI_HIGHEST_INDEX = key;
					lastListPointId = key;

					j++;
					if (j >= snapPoints.Count)
						break;

					key = ListPointsStart + j;
					if (key > ListPointsEndInclusive
						|| !UILinkPointNavigator.Points.TryGetValue(key, out UILinkPoint pointRight))
						break;

					pointRight.Unlink();
					UILinkPointNavigator.SetPosition(key, snapPoints[j].Position);
					pointRight.Left = key - 1;
					pointRight.Down = j >= snapPoints.Count - 1
						? BackPointId
						: string.Equals(snapPoints[j + 1].Name, "Wide", StringComparison.Ordinal)
							? key + 1
							: key + 2;
					pointRight.Up = j < 2 ? KeyboardTabPointId : key - 2;
					UILinkPointNavigator.Shortcuts.FANCYUI_HIGHEST_INDEX = key;
					lastListPointId = key;
				}
				else
				{
					point.Down = j < snapPoints.Count - 1 ? key + 1 : BackPointId;
					point.Up = j < 1
						? KeyboardTabPointId
						: string.Equals(snapPoints[j - 1].Name, "Wide", StringComparison.Ordinal)
							? key - 1
							: key - 2;
					UILinkPointNavigator.Shortcuts.FANCYUI_HIGHEST_INDEX = key;
					backPoint.Up = key;
					lastListPointId = key;
				}
			}

			for (int id = lastListPointId + 1; id <= ListPointsEndInclusive; id++)
			{
				if (!UILinkPointNavigator.Points.TryGetValue(id, out UILinkPoint leftover))
					break;
				leftover.Unlink();
			}
		}

		private static string ResolveStableId(
			UIElement el,
			SnapPoint snap,
			ref string currentHeader,
			ref bool currentHeaderExpanded,
			out string parentHeader,
			out bool isHeader)
		{
			_ = currentHeaderExpanded;
			isHeader = false;
			parentHeader = currentHeader;

			if (el is UIKeybindingListItem)
			{
				string bind = TryReadListItemKeybind(el);
				if (!string.IsNullOrEmpty(bind))
				{
					parentHeader = currentHeader;
					string prefix = string.IsNullOrEmpty(currentHeader)
						? "Bind"
						: currentHeader.Replace(".Header", "", StringComparison.Ordinal);
					// Prefer internal bind id (not localized label).
					return prefix + "." + SanitizeToken(bind);
				}
			}

			// Snap may sit on a wrapper under Mod/Section; resolve nested UIKeybindingListItem.
			UIElement nestedBind = FindKeybindListItemInSubtree(el);
			if (nestedBind != null && !ReferenceEquals(nestedBind, el))
			{
				string nestBind = TryReadListItemKeybind(nestedBind);
				if (!string.IsNullOrEmpty(nestBind))
				{
					parentHeader = currentHeader;
					string prefix = string.IsNullOrEmpty(currentHeader)
						? "Bind"
						: currentHeader.Replace(".Header", "", StringComparison.Ordinal);
					return prefix + "." + SanitizeToken(nestBind);
				}
			}

			// Special action ids used as binds (reset etc.)
			if (el != null && _keybindField == null)
			{
				// no-op
			}

			string typeName = el?.GetType().Name ?? string.Empty;
			if (ControlsSectionHeaderSupport.IsSectionHeaderElement(el)
				|| typeName.IndexOf("SectionCollapse", StringComparison.OrdinalIgnoreCase) >= 0
				|| typeName.IndexOf("CollapseHeader", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				// Prefer structural StableId; BuildRowCatalog usually classifies via TryClassifySectionHeader first.
				string sectionStable = ControlsSectionHeaderSupport.ResolveSectionStableId(
					el?.Parent,
					null,
					el);
				currentHeader = sectionStable;
				isHeader = true;
				parentHeader = null;
				return currentHeader;
			}

			if (el != null && _headerField != null && typeName == "HeaderElement")
			{
				string header = _headerField.GetValue(el) as string;
				if (!string.IsNullOrEmpty(header))
				{
					currentHeader = SanitizeToken(header) + ".Header";
					isHeader = true;
					parentHeader = null;
					return currentHeader;
				}
			}

			string headerText = TryReadHeaderText(el);
			bool likelyHeader = el != null
				&& (typeName.IndexOf("Header", StringComparison.OrdinalIgnoreCase) >= 0
					|| (!(el is UIKeybindingListItem)
						&& string.Equals(snap.Name, "Wide", StringComparison.Ordinal)
						&& !string.IsNullOrEmpty(headerText)));

			if (likelyHeader && !string.IsNullOrEmpty(headerText))
			{
				currentHeader = SanitizeToken(headerText) + ".Header";
				isHeader = true;
				parentHeader = null;
				return currentHeader;
			}

			// Reset / clear rows under current header.
			string anyText = headerText ?? TryReadDeepestText(el);
			if (!string.IsNullOrEmpty(anyText))
			{
				string token = SanitizeToken(anyText);
				if (token.IndexOf("Reset", StringComparison.OrdinalIgnoreCase) >= 0
					|| token.IndexOf("Default", StringComparison.OrdinalIgnoreCase) >= 0
					|| token.IndexOf("クリア", StringComparison.OrdinalIgnoreCase) >= 0
					|| token.IndexOf("リセット", StringComparison.OrdinalIgnoreCase) >= 0)
				{
					parentHeader = currentHeader;
					string prefix = string.IsNullOrEmpty(currentHeader)
						? "Section"
						: currentHeader.Replace(".Header", "", StringComparison.Ordinal);
					return prefix + ".DefaultReset";
				}
			}

			if (!string.IsNullOrEmpty(anyText))
			{
				parentHeader = currentHeader;
				string prefix = string.IsNullOrEmpty(currentHeader)
					? "Row"
					: currentHeader.Replace(".Header", "", StringComparison.Ordinal);
				return prefix + "." + SanitizeToken(anyText);
			}

			parentHeader = currentHeader;
			return "snap:" + snap.Id.ToString(CultureInfo.InvariantCulture);
		}

		private static string TryReadHeaderText(UIElement el)
		{
			if (el == null)
				return null;

			if (_headerField != null && el.GetType().Name == "HeaderElement")
			{
				try
				{
					if (_headerField.GetValue(el) is string h && !string.IsNullOrWhiteSpace(h))
						return h.Trim();
				}
				catch
				{
					// ignore
				}
			}

			return TryReadDeepestText(el);
		}

		private static string TryReadDeepestText(UIElement el)
		{
			if (el == null)
				return null;

			if (el is UIText uiText)
			{
				try
				{
					if (_uiTextTextField?.GetValue(uiText) is string t && !string.IsNullOrWhiteSpace(t))
						return t.Trim();
				}
				catch
				{
					// ignore
				}

				try
				{
					PropertyInfo textProp = typeof(UIText).GetProperty("Text", BindingFlags.Instance | BindingFlags.Public);
					if (textProp?.GetValue(uiText) is string prop && !string.IsNullOrWhiteSpace(prop))
						return prop.Trim();
				}
				catch
				{
					// ignore
				}
			}

			try
			{
				PropertyInfo textProp = el.GetType().GetProperty("Text", BindingFlags.Instance | BindingFlags.Public);
				if (textProp?.GetValue(el) is string direct && !string.IsNullOrWhiteSpace(direct))
					return direct.Trim();
			}
			catch
			{
				// ignore
			}

			foreach (UIElement child in EnumerateChildren(el))
			{
				string nested = TryReadDeepestText(child);
				if (!string.IsNullOrEmpty(nested))
					return nested;
			}

			return null;
		}

		private static string SanitizeToken(string raw)
		{
			if (string.IsNullOrWhiteSpace(raw))
				return "Unknown";

			// Strip accordion glyphs / spaces for stable-ish tokens (not used as sole identity for binds).
			raw = raw.Trim();
			if (raw.Length > 0 && (raw[0] == '▶' || raw[0] == '▽' || raw[0] == '▼' || raw[0] == '►'))
				raw = raw.Substring(1).Trim();

			var sb = new StringBuilder(raw.Length);
			bool pendingDot = false;
			for (int i = 0; i < raw.Length; i++)
			{
				char c = raw[i];
				if (char.IsLetterOrDigit(c))
				{
					if (pendingDot && sb.Length > 0)
						sb.Append('.');
					pendingDot = false;
					sb.Append(c);
				}
				else if (c is ' ' or '_' or '-' or ':' or '/' or '\\' or '\'')
				{
					pendingDot = sb.Length > 0;
				}
			}

			return sb.Length > 0 ? sb.ToString() : "Unknown";
		}

		private static int ComputeLogicalFingerprint(List<RowInfo> rows)
		{
			unchecked
			{
				int hash = 17;
				for (int i = 0; i < rows.Count; i++)
				{
					if (!rows[i].IsNavigable)
						continue;
					hash = hash * 31 + StringComparer.Ordinal.GetHashCode(rows[i].StableId ?? "");
				}

				return hash;
			}
		}

		private static void LogNavStateChanges(List<RowInfo> rows)
		{
			int current = UILinkPointNavigator.CurrentPoint;
			if (current != _prevPoint && _prevPoint != int.MinValue)
			{
				string fromId = ResolvePointStable(_prevPoint, rows) ?? FormatPoint(_prevPoint);
				string toId = ResolvePointStable(current, rows) ?? FormatPoint(current);
				if (current != _lastLoggedTo
					|| _prevPoint != _lastLoggedFrom
					|| !string.Equals(fromId, _lastLoggedFromId, StringComparison.Ordinal)
					|| !string.Equals(toId, _lastLoggedToId, StringComparison.Ordinal))
				{
					string dir = current > _prevPoint ? "Down" : "Up";
					if (current == BackPointId)
						dir = "Down";
					else if (_prevPoint == BackPointId)
						dir = "Up";

					GamePadProbeSystem.LogControlNavMove(fromId, toId, dir);
					LogResetNavIfNeeded(_prevPoint, current);
					if (current >= ResetPointsStart && current <= ResetPointsEndInclusive)
						_focusResetPoint = current;
					else if (current >= ListPointsStart && current <= ListPointsEndInclusive)
						_focusResetPoint = int.MinValue;
					_lastLoggedFrom = _prevPoint;
					_lastLoggedTo = current;
					_lastLoggedFromId = fromId;
					_lastLoggedToId = toId;

					if (IsTopBarNavPoint(_prevPoint) || IsTopBarNavPoint(current))
					{
						bool skipped = _prevPoint != ControlsPadBHoldCancelFocus.UndoCancelPointId
							&& current == 3003
							&& IsLikelyUndoLeftNeighbor(_prevPoint);
						GamePadProbeSystem.LogControlTopNavResult(
							_prevPoint,
							current,
							toId,
							moved: true,
							akUndoSkipped: skipped);
						if (skipped)
							GamePadProbeSystem.LogControlTopNavSkip("AkUndo", "BadLink");
						if (current == ControlsPadBHoldCancelFocus.UndoCancelPointId)
							GamePadProbeSystem.LogControlTopNavFocus("AkUndo", current, highlighted: true);
					}

					if (current != _scrolledForPoint)
						_scrolledForPoint = int.MinValue;
				}
			}

			_prevPoint = current;
		}

		private static bool IsTopBarNavPoint(int id)
			=> id == ControlsPadBHoldCancelFocus.UndoCancelPointId
				|| (id >= BackPointId && id < ListPointsStart);

		private static bool IsLikelyUndoLeftNeighbor(int id)
			=> id == 3004 || id == 3005;

		private static string ResolvePointStable(int pointId, List<RowInfo> rows)
		{
			if (pointId < ListPointsStart || pointId > ListPointsEndInclusive)
				return FormatPoint(pointId);
			int idx = pointId - ListPointsStart;
			if (idx < 0 || idx >= rows.Count)
				return FormatPoint(pointId);
			return rows[idx].StableId ?? FormatPoint(pointId);
		}

		private static string FormatPoint(int id)
		{
			if (id == BackPointId)
				return "Back";
			if (id == KeyboardTabPointId)
				return "KeyboardTab";
			if (id == GamepadTabPointId)
				return "GamepadTab";
			if (id == 3003)
				return "Profile";
			if (id == ControlsPadBHoldCancelFocus.UndoCancelPointId)
				return "AkUndo";
			if (id >= ResetPointsStart && id <= ResetPointsEndInclusive)
				return "Reset#" + (id - ResetPointsStart).ToString(CultureInfo.InvariantCulture);
			if (id >= ListPointsStart && id <= ListPointsEndInclusive)
				return "List#" + (id - ListPointsStart).ToString(CultureInfo.InvariantCulture);
			return "P" + id.ToString(CultureInfo.InvariantCulture);
		}

		private static int SafeCurrentPoint()
		{
			try { return UILinkPointNavigator.CurrentPoint; }
			catch { return int.MinValue; }
		}

		private static bool TryRedirectFailedUndoTravel(int before, ref int id, ref string reason)
		{
			const int undoId = ControlsPadBHoldCancelFocus.UndoCancelPointId;
			if (!UILinkPointNavigator.Points.ContainsKey(undoId))
				return false;

			bool beforeLinkedToUndo = UILinkPointNavigator.Points.TryGetValue(before, out UILinkPoint fromPt)
				&& fromPt.Right == undoId;

			// 3005.Right=2999 but ChangePoint collapsed to page-0 default (0).
			if (id == 0 && beforeLinkedToUndo)
			{
				id = undoId;
				reason = "Fallback";
				return true;
			}

			// Same input epoch: P0 → GamepadTab after failed Undo travel.
			if (id == GamepadTabPointId
				&& before == 0
				&& _topNavExpectUndo
				&& _topNavChangeEpoch == FnaGamePadProvider.InputTickEpoch)
			{
				id = undoId;
				reason = "Restore";
				return true;
			}

			return false;
		}

		private static void LogTopNavChange(int from, int to, string reason)
		{
			int epoch = FnaGamePadProvider.InputTickEpoch;
			if (epoch != _topNavChangeEpoch)
			{
				_topNavChangeEpoch = epoch;
				_topNavChangeSeq = 0;
				_topNavBeforePoint = from;
				_topNavMoveCount = 0;
				_topNavExpectUndo = (from == 3004 || from == 3005)
					&& UILinkPointNavigator.Points.TryGetValue(from, out UILinkPoint p)
					&& p.Right == ControlsPadBHoldCancelFocus.UndoCancelPointId;
			}

			_topNavChangeSeq++;
			_topNavMoveCount++;
			GamePadProbeSystem.LogControlTopNavChange(
				epoch,
				_topNavChangeSeq,
				"ChangePoint",
				from,
				to,
				reason);
		}

		private static void MaybeLogTopNavResultAfterChange(int before, int requested)
		{
			if (_topNavChangeEpoch != FnaGamePadProvider.InputTickEpoch)
				return;
			if (!_topNavExpectUndo && before != 3005 && before != 3004)
				return;

			int after = SafeCurrentPoint();
			bool undoSkipped = _topNavExpectUndo
				&& after != ControlsPadBHoldCancelFocus.UndoCancelPointId
				&& after != before;
			GamePadProbeSystem.LogControlTopNavResultDetail(
				_topNavChangeEpoch,
				_topNavBeforePoint != int.MinValue ? _topNavBeforePoint : before,
				ControlsPadBHoldCancelFocus.UndoCancelPointId,
				after,
				_topNavMoveCount,
				undoSkipped,
				duplicateInput: _topNavMoveCount > 1,
				pointOverwritten: requested != after);
		}

		private static void WireResetColumn(List<RowInfo> rows)
		{
			_resetEntries.Clear();
			if (rows == null || rows.Count == 0)
				return;
			if (!UILinkPointNavigator.Points.TryGetValue(ListPointsStart, out UILinkPoint pageRef)
				&& !UILinkPointNavigator.Points.TryGetValue(KeyboardTabPointId, out pageRef))
				return;

			var built = new List<(int BindingPoint, int ResetPoint, string ActionId, UIElement ResetEl, string MatchMethod)>(32);

			for (int i = 0; i < rows.Count; i++)
			{
				RowInfo row = rows[i];
				if (!row.IsNavigable && !row.IsHorizontalOnly)
					continue;
				if (row.Kind == ControlRowKind.SectionHeader)
					continue;

				int bindingPoint = ListPointsStart + i;
				if (bindingPoint > ListPointsEndInclusive)
					break;
				if (!UILinkPointNavigator.Points.ContainsKey(bindingPoint))
					continue;

				if (!TryFindRowResetElement(row.Element, out UIElement resetEl, out string matchMethod))
					continue;

				int resetPointId = ResetPointsStart + i;
				if (resetPointId > ResetPointsEndInclusive)
					break;

				UILinkPoint resetPt = ControlsFancyUiLinkRegistration.EnsurePointOnSamePage(resetPointId, pageRef);
				if (resetPt == null)
					continue;

				CalculatedStyle d;
				try
				{
					d = resetEl.GetInnerDimensions();
					if (d.Width <= 1f || d.Height <= 1f)
						d = resetEl.GetDimensions();
				}
				catch
				{
					continue;
				}

				if (d.Width <= 1f || d.Height <= 1f)
					continue;

				resetPt.Unlink();
				UILinkPointNavigator.SetPosition(
					resetPointId,
					new Vector2(d.X + d.Width * 0.5f, d.Y + d.Height * 0.5f));

				string action = KeybindingUiPatches.TryResolveActionIdFromRowElement(row.Element)
					?? ResolveRowActionLabel(row);

				built.Add((bindingPoint, resetPointId, action, resetEl, matchMethod));
			}

			string key = built.Count.ToString(CultureInfo.InvariantCulture);
			for (int i = 0; i < built.Count; i++)
				key += "|" + built[i].ActionId + ":" + built[i].ResetPoint;
			bool catalogChanged = !string.Equals(key, _lastResetLinkKey, StringComparison.Ordinal);
			if (catalogChanged)
				_lastResetLinkKey = key;

			for (int i = 0; i < built.Count; i++)
			{
				int up = i > 0 ? built[i - 1].ResetPoint : KeyboardTabPointId;
				int down = i < built.Count - 1 ? built[i + 1].ResetPoint : BackPointId;
				int bindingPoint = built[i].BindingPoint;
				int resetPoint = built[i].ResetPoint;

				if (!UILinkPointNavigator.Points.TryGetValue(bindingPoint, out UILinkPoint bindingPt))
					continue;
				if (!UILinkPointNavigator.Points.TryGetValue(resetPoint, out UILinkPoint resetPt))
					continue;

				bindingPt.Right = resetPoint;
				resetPt.Left = bindingPoint;
				resetPt.Right = -4;
				resetPt.Up = up;
				resetPt.Down = down;

				UILinkPointNavigator.Shortcuts.FANCYUI_HIGHEST_INDEX = Math.Max(
					UILinkPointNavigator.Shortcuts.FANCYUI_HIGHEST_INDEX,
					resetPoint);

				_resetEntries.Add(new ResetColumnEntry
				{
					ActionId = built[i].ActionId,
					BindingPoint = bindingPoint,
					ResetPoint = resetPoint
				});

				if (catalogChanged)
				{
					GamePadProbeSystem.LogControlResetElement(
						built[i].ActionId,
						built[i].ResetEl.GetType().Name,
						visible: true,
						navigable: true,
						pointId: resetPoint,
						position: resetPt.Position,
						matchedBindingPoint: bindingPoint,
						matchMethod: built[i].MatchMethod);
					GamePadProbeSystem.LogControlResetLink(
						built[i].ActionId,
						bindingPoint,
						resetPoint,
						bindingRight: bindingPt.Right,
						resetLeft: resetPt.Left,
						resetUp: resetPt.Up,
						resetDown: resetPt.Down);
				}
			}

			// Unlink stale reset points beyond current catalog.
			for (int id = ResetPointsStart; id <= ResetPointsEndInclusive; id++)
			{
				bool keep = false;
				for (int i = 0; i < built.Count; i++)
				{
					if (built[i].ResetPoint == id)
					{
						keep = true;
						break;
					}
				}

				if (keep)
					continue;
				if (!UILinkPointNavigator.Points.TryGetValue(id, out UILinkPoint leftover))
					break;
				leftover.Unlink();
				leftover.Enabled = false;
			}
		}

		/// <summary>
		/// Finds the per-row Reset sibling (UIKeybindingSimpleListItem next to UIKeybindingListItem).
		/// Does not use display text. Section-only SimpleListItem rows are not treated as Reset columns.
		/// </summary>
		private static bool TryFindRowResetElement(UIElement rowEl, out UIElement resetEl, out string matchMethod)
		{
			resetEl = null;
			matchMethod = "None";
			if (rowEl == null)
				return false;

			UIElement panel = rowEl;
			if (!HasListItemAndSimpleSibling(panel) && panel.Parent != null && HasListItemAndSimpleSibling(panel.Parent))
			{
				panel = panel.Parent;
				matchMethod = "Parent";
			}
			else if (HasListItemAndSimpleSibling(panel))
			{
				matchMethod = "ExistingReference";
			}
			else
			{
				return false;
			}

			UIElement listItem = null;
			UIElement simple = null;
			foreach (UIElement child in EnumerateChildren(panel))
			{
				string name = child.GetType().Name;
				if (name.IndexOf("UIKeybindingListItem", StringComparison.Ordinal) >= 0
					|| name.IndexOf("UIKeybindingToggleListItem", StringComparison.Ordinal) >= 0)
					listItem = child;
				else if (name.IndexOf("UIKeybindingSimpleListItem", StringComparison.Ordinal) >= 0)
					simple = child;
			}

			if (listItem == null || simple == null)
			{
				matchMethod = "None";
				return false;
			}

			string action = KeybindingUiPatches.TryResolveActionIdFromRowElement(listItem)
				?? KeybindingUiPatches.TryResolveActionIdFromRowElement(panel);
			if (!string.IsNullOrEmpty(action))
				matchMethod = "Action";

			resetEl = simple;
			return true;
		}

		private static bool HasListItemAndSimpleSibling(UIElement panel)
		{
			bool hasList = false;
			bool hasSimple = false;
			foreach (UIElement child in EnumerateChildren(panel))
			{
				string name = child.GetType().Name;
				if (name.IndexOf("UIKeybindingListItem", StringComparison.Ordinal) >= 0
					|| name.IndexOf("UIKeybindingToggleListItem", StringComparison.Ordinal) >= 0)
					hasList = true;
				else if (name.IndexOf("UIKeybindingSimpleListItem", StringComparison.Ordinal) >= 0)
					hasSimple = true;
			}

			return hasList && hasSimple;
		}

		private static void ReassertResetColumnFocus()
		{
			if (_focusResetPoint < ResetPointsStart || _focusResetPoint > ResetPointsEndInclusive)
				return;
			if (!UILinkPointNavigator.Points.TryGetValue(_focusResetPoint, out UILinkPoint pt) || !pt.Enabled)
			{
				_focusResetPoint = int.MinValue;
				return;
			}

			int current = SafeCurrentPoint();
			if (current == _focusResetPoint)
				return;

			// User moved to this Reset's linked Binding: never sticky-pull back to Reset.
			if (current >= ListPointsStart && current <= ListPointsEndInclusive)
			{
				if (IsLinkedBindingOfFocusReset(current)
					|| _resetLeftTravelEpoch == FnaGamePadProvider.InputTickEpoch)
				{
					_focusResetPoint = int.MinValue;
					return;
				}

				// Accidental other list land during rebuild while sticky Reset desired: restore below.
			}

			if (current >= BackPointId && current < ListPointsStart)
				return;
			if (current == ControlsPadBHoldCancelFocus.UndoCancelPointId)
				return;

			// Swallow if this would undo an intentional Left this input epoch.
			if (_resetLeftTravelEpoch == FnaGamePadProvider.InputTickEpoch
				&& _focusResetPoint == int.MinValue)
				return;

			if (_rebuildThisFrame)
				_resetLeftRebuildFlag = true;

			UILinkPointNavigator.ChangePoint(_focusResetPoint);
		}

		private static bool IsLinkedBindingOfFocusReset(int bindingPoint)
		{
			if (_focusResetPoint < ResetPointsStart || _focusResetPoint > ResetPointsEndInclusive)
				return false;
			if (!UILinkPointNavigator.Points.TryGetValue(_focusResetPoint, out UILinkPoint resetPt))
				return false;
			return resetPt.Left == bindingPoint;
		}

		private static void NoteResetLeftTravel(int fromReset, int toBinding, string reason)
		{
			int epoch = FnaGamePadProvider.InputTickEpoch;
			if (_resetLeftTravelEpoch != epoch)
			{
				_resetLeftTravelEpoch = epoch;
				_resetLeftChangeSeq = 0;
				_resetLeftMoveCount = 0;
				_resetLeftBeforePoint = fromReset;
				_resetLeftRebuildFlag = false;

				int linkedLeft = -1;
				if (UILinkPointNavigator.Points.TryGetValue(fromReset, out UILinkPoint rp))
					linkedLeft = rp.Left;

				string token = FnaGamePadProvider.WasJustPressed(GamePadToken.DPadLeft)
					? GamePadToken.DPadLeft
					: "Pad:LeftStickLeft";

				GamePadProbeSystem.LogControlResetLeftInput(
					epoch,
					token,
					fromReset,
					ResolveResetOrBindingAction(fromReset),
					linkedLeft,
					ResolveResetOrBindingAction(linkedLeft));
			}

			_resetLeftChangeSeq++;
			_resetLeftMoveCount++;
			GamePadProbeSystem.LogControlResetLeftChange(
				epoch,
				_resetLeftChangeSeq,
				"ChangePoint",
				fromReset,
				"Reset",
				toBinding,
				"Binding",
				reason);
		}

		private static void MaybeFinishResetLeftTravel(int before, int requested)
		{
			if (_resetLeftTravelEpoch != FnaGamePadProvider.InputTickEpoch)
				return;
			if (_resetLeftBeforePoint == int.MinValue)
				return;

			int after = SafeCurrentPoint();
			bool onReset = after >= ResetPointsStart && after <= ResetPointsEndInclusive;
			bool onBinding = after >= ListPointsStart && after <= ListPointsEndInclusive;
			string col = onReset ? "Reset" : (onBinding ? "Binding" : "Other");

			GamePadProbeSystem.LogControlResetLeftResult(
				_resetLeftTravelEpoch,
				after,
				ResolveResetOrBindingAction(after),
				col,
				_resetLeftMoveCount,
				duplicateInput: _resetLeftMoveCount > 1,
				restoredToReset: onReset && before >= ListPointsStart && before <= ListPointsEndInclusive,
				rebuildOccurred: _resetLeftRebuildFlag || _rebuildThisFrame);

			GamePadProbeSystem.LogControlResetFocusVisual(
				after,
				col,
				bindingHighlighted: onBinding,
				resetHighlighted: onReset,
				mouseHoverElement: "-");
		}

		private static void PollResetColumnConfirm()
		{
			bool aDown = FnaGamePadProvider.IsDown(GamePadToken.A);
			bool rising = FnaGamePadProvider.WasJustPressed(GamePadToken.A) || (aDown && !_resetPadAWasDown);
			_resetPadAWasDown = aDown;

			if (!rising)
				return;
			if (ComboPanel.IsOpen || !ControlsUiHost.IsManageControlsOpen())
				return;

			int current = SafeCurrentPoint();
			if (current < ResetPointsStart || current > ResetPointsEndInclusive)
				return;

			ResetColumnEntry entry = default;
			bool found = false;
			for (int i = 0; i < _resetEntries.Count; i++)
			{
				if (_resetEntries[i].ResetPoint == current)
				{
					entry = _resetEntries[i];
					found = true;
					break;
				}
			}

			if (!found || string.IsNullOrEmpty(entry.ActionId))
				return;

			_focusResetPoint = current;
			bool executed = KeybindingUiPatches.TryExecuteRowResetToDefault(entry.ActionId);

			try
			{
				ClearTrigger(PlayerInput.Triggers?.JustPressed, "MouseLeft");
				ClearTrigger(PlayerInput.Triggers?.Current, "MouseLeft");
			}
			catch
			{
				// ignore
			}

			if (SafeCurrentPoint() != current)
				UILinkPointNavigator.ChangePoint(current);

			GamePadProbeSystem.LogControlResetConfirm(
				entry.ActionId,
				GamePadToken.A,
				invoked: true,
				executedOnce: executed,
				focusRestored: SafeCurrentPoint() == current);
		}

		private static void ClearTrigger(TriggersSet set, string name)
		{
			if (set?.KeyStatus == null || string.IsNullOrEmpty(name))
				return;
			if (set.KeyStatus.ContainsKey(name))
				set.KeyStatus[name] = false;
		}

		private static void LogResetNavIfNeeded(int from, int to)
		{
			bool fromReset = from >= ResetPointsStart && from <= ResetPointsEndInclusive;
			bool toReset = to >= ResetPointsStart && to <= ResetPointsEndInclusive;
			bool fromBind = from >= ListPointsStart && from <= ListPointsEndInclusive;
			bool toBind = to >= ListPointsStart && to <= ListPointsEndInclusive;
			if (!fromReset && !toReset)
				return;

			string fromCol = fromReset ? "Reset" : "Binding";
			string toCol = toReset ? "Reset" : (toBind ? "Binding" : "Other");
			string dir;
			if (fromBind && toReset)
				dir = "Right";
			else if (fromReset && toBind)
				dir = "Left";
			else if (to > from)
				dir = "Down";
			else
				dir = "Up";

			string fromAction = ResolveResetOrBindingAction(from);
			string toAction = ResolveResetOrBindingAction(to);
			GamePadProbeSystem.LogControlResetNav(dir, fromAction, fromCol, toAction, toCol, moved: from != to);
		}

		private static string ResolveResetOrBindingAction(int pointId)
		{
			for (int i = 0; i < _resetEntries.Count; i++)
			{
				if (_resetEntries[i].ResetPoint == pointId || _resetEntries[i].BindingPoint == pointId)
					return _resetEntries[i].ActionId ?? FormatPoint(pointId);
			}

			return FormatPoint(pointId);
		}

		private struct RowInfo
		{
			public int SnapId;
			public string SnapName;
			public Vector2 SnapPosition;
			public UIElement Element;
			public string StableId;
			public string ParentHeaderId;
			public bool IsHeader;
			public ControlRowKind Kind;
			public bool IsExpanded;
			public bool IsVisibleByStructure;
			public bool IsInsideCollapsedGroup;
			public bool IsNavigable;
			public bool IsHorizontalOnly;
			public int LogicalOrder;
			public float DimX;
			public float DimY;
			public float DimW;
			public float DimH;
		}
	}
}
