using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using AdvancedKeybinds.Binding;
using AdvancedKeybinds.Core;
using AdvancedKeybinds.UI;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.GameContent.UI.Elements;
using Terraria.GameContent.UI.States;
using Terraria.GameInput;
using Terraria.ModLoader;
using Terraria.UI;
using Terraria.UI.Gamepad;

namespace AdvancedKeybinds.Patches
{
	/// <summary>
	/// Soft dependency: while an official AlchemistNPCLite <c>ShopChangeUI*</c> list belonging to the NPC
	/// you are talking to is open, D-Pad up/down moves an AK-only tentative selection, confirm clicks
	/// that row's real left checkbox button, and cancel goes through the panel's own close button.
	/// DialogueTweak is <em>not</em> required — the shop list UI is Alchemist's own. DialogueTweak chat
	/// button navigation remains a separate soft dependency.
	/// The mod paints the <em>confirmed</em> shop name Lime and all other shop names White. AK temporarily
	/// changes only a <em>different</em> tentative shop name's <c>UIText.TextColor</c> to Yellow; when the
	/// tentative cursor sits on the confirmed shop, Lime is left alone. Moving or releasing the
	/// tentative selection first calls the panel's official <c>ReCheckColor</c>, so the mod itself restores
	/// Lime / White from its Shops value. AK owns no drawing or screen geometry and never writes Shops.
	/// Committing only selects the shop and closes the list; the buy screen still needs the chat shop
	/// button afterwards, and that press is gated on the physical confirm being released first.
	/// Panels are selected by the currently-visible UI and its owning ModNPC type, never by NPC display
	/// name.
	/// </summary>
	internal static class AlchemistNpcLiteShopListGamepadCompat
	{
		public const string ModName = "AlchemistNPCLite";
		public const string DialogueTweakModName = "DialogueTweak";
		private const string InterfaceNamespace = "AlchemistNPCLite.Interface";
		private const string WorldSystemTypeName = "AlchemistNPCLite.AlchemistNPCLiteWorld";
		private const string ArchitectUiTypeName = "ShopChangeUIA";
		private const int MaxClickHandlersScan = 32;
		private const int MaxRowSearchDepth = 4;
		/// <summary>Row checkboxes sit at Left.Pixels 10; the close button sits far to the right.</summary>
		private const float RowButtonMaxLeftPixels = 40f;

		private delegate void UiClick(UIMouseEvent evt, UIElement listeningElement);

		private sealed class InstanceSource
		{
			public object Owner;
			public string OwnerLabel;
			public FieldInfo InstanceField;
			public FieldInfo[] UserInterfaceFields;
		}

		private sealed class PanelBinding
		{
			public Type UiType;
			/// <summary>ModNPC type that declares the panel's selection field (e.g. Architect for Architect.Shops).</summary>
			public Type OwnerNpcType;
			/// <summary>Compiled static-field readers 窶・no reflection on the per-frame path.</summary>
			public Func<bool> ReadVisible;
			public Func<int> ReadShops;

			public MethodInfo ReCheckColorMethod;
			public MethodInfo CloseClickMethod;

			public int ItemCount;
			public bool IsArchitectPanel;

			public object Instance;
			public InstanceSource[] InstanceSources = Array.Empty<InstanceSource>();
			public bool WasVisible;
			public bool NeedsInstanceValidation;

			// Bound once the UIState instance is known.
			public Action ReCheckColor;
			public UiClick CloseClick;
			public UIText[] Rows;               // 0-based, visual order
			public UIImageButton[] RowButtons;  // 0-based, visual order 窶・the only clickable row element
			public UIImageButton CloseButton;
			/// <summary>Stable id used to compare "row AK marked" against "row AK wants" without object churn.</summary>
			public int PanelId = -1;

			public bool Bound;
		}

		private static readonly AlchemistNpcLiteShopListGamepadCompatLayout.SessionState State = new();
		private static readonly List<string> TriggerClearScratch = new();
		private static PanelBinding[] _panels = Array.Empty<PanelBinding>();
		private static bool[] _visibleScratch = Array.Empty<bool>();
		private static object _worldSystemOwner;
		private static PanelBinding _active;
		private static PanelBinding _engaged;

		/// <summary>Panel and 1-based shop-name row AK currently tints Yellow; 0 / null means none.</summary>
		private static PanelBinding _tintedPanel;
		private static int _tintedRow;
		/// <summary>
		/// Exact colour observed immediately before Yellow. Used only if the official ReCheckColor throws.
		/// </summary>
		private static Color _tintedOriginalColor;

		private static readonly Color TentativeShopNameColor = new Color(
			AlchemistNpcLiteShopListTentativeTextColor.YellowR,
			AlchemistNpcLiteShopListTentativeTextColor.YellowG,
			AlchemistNpcLiteShopListTentativeTextColor.YellowB,
			AlchemistNpcLiteShopListTentativeTextColor.YellowA);

		public static bool Apply()
		{
			var gate = AlchemistNpcLiteShopListApplyPolicy.Decide(
				alchemistNpcLitePresent: ModLoader.HasMod(ModName),
				dialogueTweakPresent: ModLoader.HasMod(DialogueTweakModName),
				alreadyApplied: State.Applied,
				alreadyDisabled: State.Disabled);

			if (gate == AlchemistNpcLiteShopListApplyPolicy.ApplyGate.SkipAlreadyApplied)
				return true;
			if (gate != AlchemistNpcLiteShopListApplyPolicy.ApplyGate.Proceed)
				return false;

			try
			{
				if (!ModLoader.TryGetMod(ModName, out Mod mod) || mod?.Code == null)
					return Disable("mod-code-null", null);

				_worldSystemOwner = TryFindWorldSystemOwner(mod);
				var list = new List<PanelBinding>();
				foreach (Type type in mod.Code.GetTypes())
				{
					if (type == null || !type.IsClass)
						continue;
					if (type.Namespace != InterfaceNamespace)
						continue;
					if (type.Name == null || !type.Name.StartsWith("ShopChangeUI", StringComparison.Ordinal))
						continue;

					if (!TryBuildPanelBinding(type, out PanelBinding binding))
						continue;

					CacheDisplayedInstanceSources(_worldSystemOwner, binding);
					binding.PanelId = list.Count;
					list.Add(binding);
				}

				if (list.Count == 0)
					return Disable("no-shopchange-panels", null);

				_panels = list.ToArray();
				_visibleScratch = new bool[_panels.Length];
				ClearActive();
				State.Applied = true;
				State.Disabled = false;
				return true;
			}
			catch (Exception ex)
			{
				return Disable("apply-failed", ex);
			}
		}

		public static void ResetAppliedFlag()
		{
			// During normal Unload the UI objects are still available here. Restore first; if another mod
			// has already invalidated them, the method catches and clears AK's references safely.
			RestoreTentativeShopNameTextColor();
			State.ResetForReload();
			_panels = Array.Empty<PanelBinding>();
			_visibleScratch = Array.Empty<bool>();
			_active = null;
			_engaged = null;
			_tintedPanel = null;
			_tintedRow = 0;
			_tintedOriginalColor = default;
			_worldSystemOwner = null;
		}

		public static void TryProcessInput()
		{
			if (!State.Applied || State.Disabled)
			{
				ReleaseSession();
				return;
			}

			try
			{
				bool cancelHeld = FnaGamePadProvider.IsDown(GamePadToken.B);
				if (AlchemistNpcLiteShopListGamepadCompatLayout.PollCancelReleaseGate(State, cancelHeld))
				{
					SuppressCancelParentInput();
					return;
				}

				Player player = Main.LocalPlayer;
				int talkNpc = player?.talkNPC ?? -1;
				NPC npc = talkNpc >= 0 && talkNpc < Main.npc.Length ? Main.npc[talkNpc] : null;
				bool talking = npc != null && npc.active;

				bool confirmHeld = FnaGamePadProvider.IsDown(GamePadToken.A);
				if (HandleConfirmReleaseGate(confirmHeld, talking, talkNpc))
					return;

				bool relevantPadInput = confirmHeld
					|| cancelHeld
					|| FnaGamePadProvider.IsDown(GamePadToken.DPadUp)
					|| FnaGamePadProvider.IsDown(GamePadToken.DPadDown);
				// IsConnected is false whenever AK's pad subsystem cannot read the controller, which keeps
				// the list on its normal mouse / keyboard behaviour instead of a take-over AK cannot drive.
				bool controllerInputMode = AlchemistNpcLiteShopListGamepadCompatLayout.IsControllerInputMode(
					PlayerInput.UsingGamepad,
					FnaGamePadProvider.IsConnected,
					relevantPadInput);

				RefreshActivePanel();
				TryArchitectRecheckOnOpenEdge();

				bool listVisible = _active != null;
				if (!talking || !listVisible)
				{
					ReleaseSession();
					return;
				}

				bool owned = _active.OwnerNpcType != null
					&& npc.ModNPC != null
					&& npc.ModNPC.GetType() == _active.OwnerNpcType;

				if (!owned || !EnsureBound(_active))
				{
					ReleaseSession();
					return;
				}

				UpdateMouseHover(_active);

				bool chatOrModal = Main.drawingPlayerChat
					|| Main.editSign
					|| Main.editChest
					|| Main.blockInput
					|| (Main.InGameUI?.CurrentState is UIVirtualKeyboard);

				var gate = AlchemistNpcLiteShopListGamepadCompatLayout.DecideFrame(
					applied: State.Applied,
					disabled: State.Disabled,
					listVisible: true,
					talkingToNpc: true,
					panelOwnedByTalkNpc: true,
					usingGamepad: controllerInputMode,
					comboPanelOpen: ComboPanel.IsOpen,
					chatOrModalBlocks: chatOrModal,
					itemCount: _active.ItemCount);

				if (gate != AlchemistNpcLiteShopListGamepadCompatLayout.FrameGate.Process)
				{
					ReleaseSession();
					return;
				}

				if (_engaged != _active)
				{
					ReleaseSession();
					AlchemistNpcLiteShopListGamepadCompatLayout.Engage(
						State,
						_active.ReadShops(),
						_active.ItemCount,
						SafeCurrentPoint());
					_engaged = _active;

					#if AK_DIAGNOSTICS
					AlchemistNpcLiteShopListTempDiag.ResetDedupe();
					#endif
					#if AK_DIAGNOSTICS
					AlchemistNpcLiteShopListTempDiag.Event(
						"list-open",
						"panel=" + _active.UiType.Name
						+ " items=" + _active.ItemCount
						+ " shops=" + _active.ReadShops()
						+ " tentative=" + State.TentativeIndex
						+ " talkNPC=" + talkNpc);
					#endif
				}

				SuppressShoulderInput(controllerInputMode);

				var input = new AlchemistNpcLiteShopListGamepadCompatLayout.ListInput(
					dpadUpJustPressed: FnaGamePadProvider.WasJustPressed(GamePadToken.DPadUp),
					dpadDownJustPressed: FnaGamePadProvider.WasJustPressed(GamePadToken.DPadDown),
					confirmJustPressed: FnaGamePadProvider.WasJustPressed(GamePadToken.A),
					confirmHeld: confirmHeld,
					cancelJustPressed: FnaGamePadProvider.WasJustPressed(GamePadToken.B),
					dpadUpHeld: FnaGamePadProvider.IsDown(GamePadToken.DPadUp),
					dpadDownHeld: FnaGamePadProvider.IsDown(GamePadToken.DPadDown),
					cancelHeld: cancelHeld,
					nowMs: Environment.TickCount64);

				var step = AlchemistNpcLiteShopListGamepadCompatLayout.Step(State, input);
				ApplyTentativeShopNameTextColor(_active);

				switch (step.Action)
				{
					case AlchemistNpcLiteShopListGamepadCompatLayout.ListAction.Move:
						#if AK_DIAGNOSTICS
						AlchemistNpcLiteShopListTempDiag.Event(
							"tentative", "index=" + State.TentativeIndex);
						#endif
						break;

					case AlchemistNpcLiteShopListGamepadCompatLayout.ListAction.Confirm:
						CommitTentativeSelection(_active);
						break;

					case AlchemistNpcLiteShopListGamepadCompatLayout.ListAction.Cancel:
						CancelSelection(_active);
						break;
				}

				if (step.SuppressNavigation)
					SuppressListNavigation(step.Action);
			}
			catch (Exception ex)
			{
				#if AK_DIAGNOSTICS
				AlchemistNpcLiteShopListTempDiag.Event("exception", ex.GetType().Name + ": " + ex.Message);
				#endif
				Disable("process-failed", ex);
			}
		}

		/// <summary>
		/// Confirms by clicking the highlighted row's own left checkbox button 窶・the only element
		/// AlchemistNPCLite attaches a click handler to. The handler sets the shop, closes the list and
		/// refreshes the chat buttons; it never opens the buy screen.
		/// </summary>
		private static void CommitTentativeSelection(PanelBinding panel)
		{
			var highlight = AlchemistNpcLiteShopListGamepadCompatLayout.ResolveHighlight(
				State.MouseHoverIndex,
				State.Engaged,
				State.TentativeIndex,
				panel.ItemCount);
			var click = AlchemistNpcLiteShopListGamepadCompatLayout.ResolveConfirmClick(
				highlight,
				panel.ItemCount,
				panel.RowButtons != null && panel.RowButtons.Length == panel.ItemCount);

			if (click.Target != AlchemistNpcLiteShopListGamepadCompatLayout.ConfirmClickTarget.RowCheckboxButton)
			{
				#if AK_DIAGNOSTICS
				AlchemistNpcLiteShopListTempDiag.Event(
					"confirm-refused", "highlight=" + highlight.Index + " source=" + highlight.Source);
				#endif
				return;
			}

			UIImageButton button = panel.RowButtons[click.RowIndex];
			if (button == null)
				return;

			int shopsBefore = panel.ReadShops();
			bool visibleBefore = panel.ReadVisible();
			int returnPoint = State.ReturnPointId;

			// Official Lime / White is back before the mod handler updates Shops and calls ReCheckColor
			// itself. A closed list can therefore never retain AK's temporary Yellow.
			RestoreTentativeShopNameTextColor();
			InvokeOfficialLeftClick(button);

			int shopsAfter = panel.ReadShops();
			bool visibleAfter = panel.ReadVisible();

			#if AK_DIAGNOSTICS
			AlchemistNpcLiteShopListTempDiag.Event(
				"confirm",
				"row=" + click.RowIndex
				+ " ordinal=" + click.Ordinal
				+ " shops=" + shopsBefore + "->" + shopsAfter
				+ " visible=" + visibleBefore + "->" + visibleAfter);
			#endif

			// The panel enforces its own ShopChangeDelay; if it refused, stay engaged and keep the cursor.
			if (visibleAfter)
			{
				ApplyTentativeShopNameTextColor(panel);
				return;
			}

			// The confirm press is still physically down. Clearing MouseLeft only for this frame would
			// fabricate a fresh press next frame (Terraria derives JustPressed from "not down last frame"),
			// which would immediately click the chat shop button and open the buy screen. So the focus move
			// waits and the parent confirm stays blocked until the button is physically released.
			AlchemistNpcLiteShopListGamepadCompatLayout.ArmConfirmReleaseGate(
				State, returnPoint, Main.LocalPlayer?.talkNPC ?? -1);
			#if AK_DIAGNOSTICS
			AlchemistNpcLiteShopListTempDiag.Event(
				"confirm-gate-armed", "pending=" + returnPoint + " held=" + FnaGamePadProvider.IsDown(GamePadToken.A));
			#endif
			ReleaseSession();
		}

		/// <summary>
		/// While the confirm that selected a shop is still held, the parent chat UI must not see it, and the
		/// parked focus move must not happen yet. Release is decided by the physical button only.
		/// Returns true when this frame belongs to the gate and nothing else may run.
		/// </summary>
		private static bool HandleConfirmReleaseGate(bool confirmHeld, bool talking, int talkNpc)
		{
			if (!State.ConfirmReleaseGate)
				return false;

			if (AlchemistNpcLiteShopListGamepadCompatLayout.ConfirmGateContextLost(
				    State, inPlay: !Main.gameMenu, talking: talking, talkNpc: talkNpc))
			{
				AlchemistNpcLiteShopListGamepadCompatLayout.AbandonConfirmReleaseGate(State);
				#if AK_DIAGNOSTICS
				AlchemistNpcLiteShopListTempDiag.Event("confirm-gate-abandoned", "talkNPC=" + talkNpc);
				#endif
				return false;
			}

			if (AlchemistNpcLiteShopListGamepadCompatLayout.PollConfirmReleaseGate(State, confirmHeld))
			{
				SuppressConfirmParentInput();
				return true;
			}

			int pending = AlchemistNpcLiteShopListGamepadCompatLayout.TakePendingFocusPoint(State);
			#if AK_DIAGNOSTICS
			AlchemistNpcLiteShopListTempDiag.Event(
				"confirm-gate-released", "focus=" + pending + " current=" + SafeCurrentPoint());
			#endif
			RestoreFocusToShopButton(pending);
			return false;
		}

		/// <summary>Cancel goes through the panel's own close button, so the shop selection is untouched.</summary>
		private static void CancelSelection(PanelBinding panel)
		{
			int returnPoint = State.ReturnPointId;
			int shopsBefore = panel.ReadShops();

			RestoreTentativeShopNameTextColor();
			if (panel.CloseButton != null)
				InvokeOfficialLeftClick(panel.CloseButton);
			else
				panel.CloseClick?.Invoke(null, null);

			#if AK_DIAGNOSTICS
			AlchemistNpcLiteShopListTempDiag.Event(
				"cancel",
				"shops=" + shopsBefore + "->" + panel.ReadShops() + " visible=" + panel.ReadVisible());
			#endif

			if (panel.ReadVisible())
				return;

			AlchemistNpcLiteShopListGamepadCompatLayout.ArmCancelReleaseGate(State);
			RestoreFocus(returnPoint);
			ReleaseSession();
		}

		/// <summary>
		/// Raises the element's official left-click event with a real <see cref="UIMouseEvent"/> built from
		/// the element's own layout, so no screen-resolution maths and no virtual cursor move are needed.
		/// </summary>
		private static void InvokeOfficialLeftClick(UIElement element)
		{
			CalculatedStyle dim = element.GetDimensions();
			var at = new Vector2(dim.X + dim.Width * 0.5f, dim.Y + dim.Height * 0.5f);
			element.LeftClick(new UIMouseEvent(element, at));
		}

		/// <summary>
		/// Only the left checkbox is clickable in AlchemistNPCLite, so only the checkbox counts as hover.
		/// Hovering the shop name must not suggest it can be clicked.
		/// IsMouseHovering is maintained by the panel's own UserInterface, so AK needs no mouse-coordinate
		/// conversion of its own and stays correct at any resolution or UI scale.
		/// </summary>
		private static void UpdateMouseHover(PanelBinding panel)
		{
			State.MouseHoverIndex = 0;
			if (panel?.RowButtons == null)
				return;

			for (int i = 0; i < panel.RowButtons.Length; i++)
			{
				UIImageButton button = panel.RowButtons[i];
				if (button == null || !button.IsMouseHovering)
					continue;

				State.MouseHoverIndex = i + AlchemistNpcLiteShopListGamepadCompatLayout.DefaultMinIndex;
				return;
			}
		}

		/// <summary>
		/// Changes only the tentative shop name's <see cref="UIText.TextColor"/> to Yellow. Before moving the
		/// temporary colour, AK calls the panel's official ReCheckColor so confirmed Lime and unconfirmed
		/// White are restored by AlchemistNPCLite itself. AK computes no position, size, font or string
		/// width. Equal state writes nothing.
		/// </summary>
		private static void ApplyTentativeShopNameTextColor(PanelBinding panel)
		{
			int confirmedShop = 0;
			try
			{
				if (panel != null)
					confirmedShop = panel.ReadShops();
			}
			catch
			{
				confirmedShop = 0;
			}

			int wantedRow = panel == null
				? 0
				: AlchemistNpcLiteShopListTentativeTextColor.ResolveTintedShopNameRow(
					applied: State.Applied,
					disabled: State.Disabled,
					listVisible: true,
					engaged: State.Engaged,
					tentativeIndex: AlchemistNpcLiteShopListGamepadCompatLayout.ResolveHighlight(
						State.MouseHoverIndex,
						State.Engaged,
						State.TentativeIndex,
						panel.ItemCount).Index,
					itemCount: panel.ItemCount,
					boundShopNameCount: panel.Rows?.Length ?? 0,
					confirmedShop: confirmedShop);

			int wantedPanelId = wantedRow > 0 ? panel.PanelId : -1;

			if (!AlchemistNpcLiteShopListTentativeTextColor.NeedsTintMove(
				    _tintedPanel?.PanelId ?? -1, _tintedRow, wantedPanelId, wantedRow))
				return;

			RestoreTentativeShopNameTextColor();
			if (wantedRow <= 0)
				return;

			int index = AlchemistNpcLiteShopListTentativeTextColor.ToShopNameElementIndex(
				wantedRow, panel.ItemCount);
			UIText row = index >= 0 && panel.Rows != null && index < panel.Rows.Length ? panel.Rows[index] : null;
			if (row == null)
				return;

			_tintedOriginalColor = row.TextColor;
			row.TextColor = TentativeShopNameColor;
			_tintedPanel = panel;
			_tintedRow = wantedRow;

			#if AK_DIAGNOSTICS
			AlchemistNpcLiteShopListTempDiag.Event(
				"tentative-color", "row=" + wantedRow + " color=Yellow");
			#endif
		}

		/// <summary>
		/// Removes AK's temporary Yellow through the panel's official ReCheckColor. The exact colour seen
		/// before Yellow is only a failure fallback; it is never preferred over the mod's current Shops.
		/// </summary>
		private static void RestoreTentativeShopNameTextColor()
		{
			PanelBinding panel = _tintedPanel;
			int row = _tintedRow;
			Color fallback = _tintedOriginalColor;
			_tintedPanel = null;
			_tintedRow = 0;
			_tintedOriginalColor = default;

			if (panel == null || row <= 0)
				return;

			try
			{
				if (panel.ReCheckColor == null)
					throw new InvalidOperationException("ReCheckColor delegate unavailable.");

				panel.ReCheckColor();
			}
			catch
			{
				// Best effort for unload-order / invalidated-mod-object failures. This value was captured
				// immediately before Yellow, not at bind time, and only this one shop name is touched.
				try
				{
					int index = AlchemistNpcLiteShopListTentativeTextColor.ToShopNameElementIndex(
						row, panel.ItemCount);
					UIText text = index >= 0 && panel.Rows != null && index < panel.Rows.Length
						? panel.Rows[index]
						: null;
					if (text != null)
						text.TextColor = fallback;
				}
				catch
				{
					// Display restoration must never take the input path down.
				}
			}

			#if AK_DIAGNOSTICS
			AlchemistNpcLiteShopListTempDiag.Event("tentative-color-restored", "row=" + row);
			#endif
		}

		private static void TryArchitectRecheckOnOpenEdge()
		{
			if (_panels == null)
				return;

			for (int i = 0; i < _panels.Length; i++)
			{
				PanelBinding panel = _panels[i];
				if (panel == null || !panel.IsArchitectPanel)
					continue;

				bool visible;
				try
				{
					visible = panel.ReadVisible();
				}
				catch
				{
					return;
				}

				if (!AlchemistNpcLiteShopListGamepadCompatLayout.ShouldCallArchitectRecheckOnOpen(State, visible))
					return;

				if (!EnsureBound(panel))
					return;

				try
				{
					panel.ReCheckColor?.Invoke();
				}
				catch
				{
					// Architect colour repair is best-effort; input must keep working.
				}

				return;
			}
		}

		/// <summary>Drops the AK session and restores the temporary Yellow shop-name colour.</summary>
		private static void ReleaseSession()
		{
			RestoreTentativeShopNameTextColor();
			if (_engaged != null)
				#if AK_DIAGNOSTICS
				AlchemistNpcLiteShopListTempDiag.Event("list-release", "panel=" + _engaged.UiType.Name);
				#endif
			_engaged = null;
			State.MouseHoverIndex = 0;
			State.Disengage();
		}

		private static void RestoreFocusToShopButton(int returnPoint)
		{
			if (returnPoint < 0)
				return;
			if (SafeCurrentPoint() != returnPoint)
				return;
			if (!UILinkPointNavigator.Points.TryGetValue(returnPoint, out UILinkPoint point))
				return;

			int right = point.Right;
			if (right < 0 || !UILinkPointNavigator.Points.ContainsKey(right))
				return;

			UILinkPointNavigator.ChangePoint(right);
		}

		private static void RestoreFocus(int pointId)
		{
			if (pointId < 0 || !UILinkPointNavigator.Points.ContainsKey(pointId))
				return;
			UILinkPointNavigator.ChangePoint(pointId);
		}

		private static int SafeCurrentPoint()
		{
			try
			{
				return UILinkPointNavigator.CurrentPoint;
			}
			catch
			{
				return -1;
			}
		}

		/// <summary>
		/// On XBoxGamepadUI the shoulder buttons carry parent actions (L1 is bound to MouseRight by
		/// default, which is what talks to a nearby NPC). While AK drives an open list they must not reach
		/// the parent UI; every other state keeps Terraria's normal behaviour.
		/// </summary>
		private static void SuppressShoulderInput(bool controllerInputMode)
		{
			TrySuppressShoulder(GamePadToken.LeftShoulder, controllerInputMode);
			TrySuppressShoulder(GamePadToken.RightShoulder, controllerInputMode);
		}

		private static void TrySuppressShoulder(string padToken, bool controllerInputMode)
		{
			bool down = FnaGamePadProvider.IsDown(padToken);
			if (!AlchemistNpcLiteShopListGamepadCompatLayout.ShouldSuppressShoulderInput(
				    applied: State.Applied,
				    disabled: State.Disabled,
				    listVisible: _active != null,
				    engaged: State.Engaged,
				    controllerInputMode: controllerInputMode,
				    shoulderDown: down))
				return;

			TriggerClearScratch.Clear();
			GamePadParentUiTriggerSuppress.ClearParentNavTriggersMappedToPad(
				PlayerInput.Triggers?.JustPressed, padToken, TriggerClearScratch);
			GamePadParentUiTriggerSuppress.ClearParentNavTriggersMappedToPad(
				PlayerInput.Triggers?.Current, padToken, TriggerClearScratch);

			// Main.mouseRight is copied from the trigger before PostUpdateInput runs, so the NPC-hover
			// talk path needs the already-latched value cleared too.
			Main.mouseRight = false;
			Main.mouseRightRelease = false;

			#if AK_DIAGNOSTICS
			AlchemistNpcLiteShopListTempDiag.Event(
				"shoulder-suppressed",
				padToken + " cleared=" + (TriggerClearScratch.Count == 0
					? "none"
					: string.Join(",", TriggerClearScratch)));
			#endif
		}

		/// <summary>
		/// Keeps the owned up/down axis out of chat navigation and player movement, and 窶・on the frame a row
		/// is confirmed or cancelled 窶・stops the same press from also activating the focused chat button.
		/// </summary>
		private static void SuppressListNavigation(AlchemistNpcLiteShopListGamepadCompatLayout.ListAction action)
		{
			TriggerClearScratch.Clear();
			GamePadParentUiTriggerSuppress.ClearParentNavTriggersMappedToPad(
				PlayerInput.Triggers?.JustPressed, GamePadToken.DPadUp, TriggerClearScratch);
			GamePadParentUiTriggerSuppress.ClearParentNavTriggersMappedToPad(
				PlayerInput.Triggers?.Current, GamePadToken.DPadUp, TriggerClearScratch);
			GamePadParentUiTriggerSuppress.ClearParentNavTriggersMappedToPad(
				PlayerInput.Triggers?.JustPressed, GamePadToken.DPadDown, TriggerClearScratch);
			GamePadParentUiTriggerSuppress.ClearParentNavTriggersMappedToPad(
				PlayerInput.Triggers?.Current, GamePadToken.DPadDown, TriggerClearScratch);
			ClearTrigger("MenuUp");
			ClearTrigger("MenuDown");

			// Pad:A is MouseLeft on XBoxGamepadUI: while it is physically held it must not also click the
			// chat button behind the list. Physical mouse clicks are left untouched.
			if (FnaGamePadProvider.IsDown(GamePadToken.A))
			{
				ClearTrigger("MouseLeft");
				Main.mouseLeft = false;
				Main.mouseLeftRelease = false;
			}

			if (action != AlchemistNpcLiteShopListGamepadCompatLayout.ListAction.Confirm
			    && action != AlchemistNpcLiteShopListGamepadCompatLayout.ListAction.Cancel)
				return;

			if (action == AlchemistNpcLiteShopListGamepadCompatLayout.ListAction.Cancel)
				SuppressCancelParentInput();

			ClearTrigger("MouseLeft");
			ClearTrigger("MouseRight");
			ClearTrigger("Inventory");

			Main.mouseLeft = false;
			Main.mouseLeftRelease = false;
			Main.mouseRight = false;
			Main.mouseRightRelease = false;
		}

		/// <summary>
		/// Blocks the Pad:A parent confirm path through physical release. Pad:A is MouseLeft on
		/// XBoxGamepadUI, so both the trigger sets and the already-latched Main mouse flags are held down;
		/// clearing them every frame of the gate is what stops a fabricated fresh press. Physical mouse and
		/// keyboard input is only affected while Pad:A itself is held right after a list confirm.
		/// </summary>
		private static void SuppressConfirmParentInput()
		{
			TriggerClearScratch.Clear();
			GamePadParentUiTriggerSuppress.ClearParentNavTriggersMappedToPad(
				PlayerInput.Triggers?.JustPressed, GamePadToken.A, TriggerClearScratch);
			GamePadParentUiTriggerSuppress.ClearParentNavTriggersMappedToPad(
				PlayerInput.Triggers?.Current, GamePadToken.A, TriggerClearScratch);
			ClearTrigger("MouseLeft");
			Main.mouseLeft = false;
			Main.mouseLeftRelease = false;
		}

		/// <summary>Blocks the Pad:B parent cancel path through physical release.</summary>
		private static void SuppressCancelParentInput()
		{
			TriggerClearScratch.Clear();
			GamePadParentUiTriggerSuppress.ClearParentNavTriggersMappedToPad(
				PlayerInput.Triggers?.JustPressed, GamePadToken.B, TriggerClearScratch);
			GamePadParentUiTriggerSuppress.ClearParentNavTriggersMappedToPad(
				PlayerInput.Triggers?.Current, GamePadToken.B, TriggerClearScratch);
			ClearTrigger("Inventory");
			ClearTrigger("MouseRight");
			ClearTrigger("MenuInquiry");
			if (Main.LocalPlayer != null)
				Main.LocalPlayer.releaseInventory = false;
			Main.mouseRight = false;
			Main.mouseRightRelease = false;
		}

		private static void ClearTrigger(string key)
		{
			ClearTrigger(PlayerInput.Triggers?.JustPressed, key);
			ClearTrigger(PlayerInput.Triggers?.Current, key);
		}

		private static void ClearTrigger(TriggersSet set, string key)
		{
			if (set?.KeyStatus == null || key == null)
				return;
			if (set.KeyStatus.ContainsKey(key))
				set.KeyStatus[key] = false;
		}

		private static void RefreshActivePanel()
		{
			if (_panels == null || _panels.Length == 0)
			{
				ClearActive();
				return;
			}

			if (_visibleScratch.Length < _panels.Length)
				_visibleScratch = new bool[_panels.Length];

			for (int i = 0; i < _panels.Length; i++)
			{
				PanelBinding panel = _panels[i];
				bool visible = panel.ReadVisible();
				_visibleScratch[i] = visible;
				if (AlchemistNpcLiteDisplayedUiSelectionPolicy.ShouldResolveOnOpen(
					    visible, panel.WasVisible))
					panel.NeedsInstanceValidation = true;
				panel.WasVisible = visible;
			}

			int next = AlchemistNpcLiteShopListGamepadCompatLayout.SelectActivePanelSlot(
				_visibleScratch, _panels.Length);
			State.ActivePanelSlot = next;
			_active = next >= 0 ? _panels[next] : null;
		}

		private static void ClearActive()
		{
			State.ActivePanelSlot = -1;
			_active = null;
		}

		private static bool TryBuildPanelBinding(Type uiType, out PanelBinding binding)
		{
			binding = null;

			FieldInfo visible = uiType.GetField("visible", BindingFlags.Public | BindingFlags.Static);
			if (visible == null || visible.FieldType != typeof(bool))
				return false;

			MethodInfo recheck = uiType.GetMethod(
				"ReCheckColor",
				BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
			if (recheck == null)
				return false;

			MethodInfo close = uiType.GetMethod(
				"CloseButtonClicked",
				BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
			if (close == null)
				return false;

			var clicks = new List<MethodInfo>();
			for (int i = 1; i <= MaxClickHandlersScan; i++)
			{
				MethodInfo m = uiType.GetMethod(
					"PlayButtonClicked" + i,
					BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
				if (m == null)
					break;
				clicks.Add(m);
			}

			if (clicks.Count < 1)
				return false;

			FieldInfo shops = TryFindShopsField(uiType, clicks[0]);
			if (shops == null || shops.DeclaringType == null)
				return false;

			// The selection field lives on the owning ModNPC; that link is what proves the open panel
			// belongs to the NPC being talked to, without any name / id branching.
			if (!typeof(ModNPC).IsAssignableFrom(shops.DeclaringType))
				return false;

			binding = new PanelBinding {
				UiType = uiType,
				OwnerNpcType = shops.DeclaringType,
				ReadVisible = CompileStaticGetter<bool>(visible),
				ReadShops = CompileStaticGetter<int>(shops),
				ReCheckColorMethod = recheck,
				CloseClickMethod = close,
				ItemCount = clicks.Count,
				IsArchitectPanel = string.Equals(uiType.Name, ArchitectUiTypeName, StringComparison.Ordinal)
			};
			return true;
		}

		private static Func<T> CompileStaticGetter<T>(FieldInfo field)
			=> Expression.Lambda<Func<T>>(Expression.Field(null, field)).Compile();

		/// <summary>
		/// Discovers the static int selection field written by the panel (e.g. Architect.Shops) from the IL of
		/// CheckColor / PlayButtonClicked1 窶・not from NPC display names.
		/// </summary>
		private static FieldInfo TryFindShopsField(Type uiType, MethodInfo playButton1)
		{
			MethodInfo checkColor = uiType.GetMethod(
				"CheckColor",
				BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
			return TryFindStaticIntFieldInMethodIl(checkColor) ?? TryFindStaticIntFieldInMethodIl(playButton1);
		}

		private static FieldInfo TryFindStaticIntFieldInMethodIl(MethodInfo method)
		{
			if (method == null)
				return null;
			MethodBody body = method.GetMethodBody();
			byte[] il = body?.GetILAsByteArray();
			if (il == null || il.Length == 0)
				return null;

			Module module = method.Module;
			for (int i = 0; i < il.Length; i++)
			{
				byte op = il[i];
				// ldsfld 0x7E, stsfld 0x80
				if (op != 0x7E && op != 0x80)
					continue;
				if (i + 4 >= il.Length)
					break;
				int token = BitConverter.ToInt32(il, i + 1);
				i += 4;
				try
				{
					FieldInfo field = module.ResolveField(token);
					if (field != null && field.IsStatic && field.FieldType == typeof(int))
						return field;
				}
				catch
				{
					// Not a field token reachable from this module 窶・keep scanning.
				}
			}

			return null;
		}

		private static object TryFindWorldSystemOwner(Mod mod)
		{
			Type worldType = mod?.Code?.GetType(WorldSystemTypeName);
			if (worldType == null)
				return null;

			try
			{
				foreach (ModSystem system in ModContent.GetContent<ModSystem>())
				{
					if (system != null && system.GetType() == worldType)
						return system;
				}
			}
			catch
			{
				// Content enumeration may be unavailable during an early load phase. The first list-open
				// edge retries once; the steady-state frame path never reflects.
			}

			return null;
		}

		/// <summary>
		/// Caches reflection metadata for the only owner that officially updates and draws ShopChangeUI*:
		/// AlchemistNPCLiteWorld. The stale duplicate fields on Mod.AlchemistNPCLite are deliberately not
		/// candidates.
		/// </summary>
		private static void CacheDisplayedInstanceSources(object worldOwner, PanelBinding binding)
		{
			binding.InstanceSources = Array.Empty<InstanceSource>();
			if (worldOwner == null || binding?.UiType == null)
				return;

			Type ownerType = worldOwner.GetType();
			FieldInfo[] fields = ownerType.GetFields(
				BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
			var userInterfaces = new List<FieldInfo>();
			for (int i = 0; i < fields.Length; i++)
			{
				if (typeof(UserInterface).IsAssignableFrom(fields[i].FieldType))
					userInterfaces.Add(fields[i]);
			}

			if (userInterfaces.Count == 0)
				return;

			var sources = new List<InstanceSource>();
			for (int i = 0; i < fields.Length; i++)
			{
				FieldInfo field = fields[i];
				if (field.FieldType == null || !binding.UiType.IsAssignableFrom(field.FieldType))
					continue;

				sources.Add(new InstanceSource {
					Owner = worldOwner,
					OwnerLabel = "ModSystem." + ownerType.Name,
					InstanceField = field,
					UserInterfaceFields = userInterfaces.ToArray()
				});
			}

			binding.InstanceSources = sources.ToArray();
		}

		/// <summary>
		/// Selects the unique AlchemistNPCLiteWorld field whose value is reference-equal to one of that
		/// same owner's UserInterface.CurrentState values. Null, no match and ambiguity all fail closed.
		/// Called only on a list-open edge or after world invalidation.
		/// </summary>
		private static bool TryResolveDisplayedInstance(PanelBinding binding)
		{
			if (binding == null)
				return false;

			if (binding.InstanceSources == null || binding.InstanceSources.Length == 0)
			{
				if (_worldSystemOwner == null
				    && ModLoader.TryGetMod(ModName, out Mod mod)
				    && mod?.Code != null)
					_worldSystemOwner = TryFindWorldSystemOwner(mod);
				CacheDisplayedInstanceSources(_worldSystemOwner, binding);
			}

			InstanceSource[] sources = binding.InstanceSources;
			if (sources == null || sources.Length == 0)
				return false;

			var candidates = new object[sources.Length];
			var matches = new bool[sources.Length];
			for (int i = 0; i < sources.Length; i++)
			{
				InstanceSource source = sources[i];
				object owner = source.InstanceField.IsStatic ? null : source.Owner;
				object candidate = source.InstanceField.GetValue(owner);
				candidates[i] = candidate;
				if (candidate == null)
					continue;

				FieldInfo[] uiFields = source.UserInterfaceFields;
				for (int u = 0; u < uiFields.Length; u++)
				{
					FieldInfo uiField = uiFields[u];
					var userInterface = uiField.GetValue(uiField.IsStatic ? null : source.Owner) as UserInterface;
					if (ReferenceEquals(userInterface?.CurrentState, candidate))
					{
						matches[i] = true;
						break;
					}
				}
			}

			int selected = AlchemistNpcLiteDisplayedUiSelectionPolicy.SelectUniqueCurrentStateCandidate(matches);
			if (selected < 0 || selected >= sources.Length || candidates[selected] == null)
				return false;

			InstanceSource chosen = sources[selected];
			object displayed = candidates[selected];
			bool currentStateStillMatches = ReferenceEquals(binding.Instance, displayed);
			bool rebound = binding.Bound
				&& AlchemistNpcLiteDisplayedUiSelectionPolicy.ShouldDiscardBoundReference(
					worldInvalidated: false,
					currentStateStillMatches: currentStateStillMatches);
			if (!currentStateStillMatches)
			{
				InvalidateBinding(binding);
				binding.Instance = displayed;
			}

			if (!binding.Bound || rebound)
			{
				#if AK_DIAGNOSTICS
				AlchemistNpcLiteShopListTempDiag.Event(
					rebound ? "ui-rebind" : "ui-selected",
					"panel=" + binding.UiType.Name
					+ " owner=" + chosen.OwnerLabel
					+ " instance=" + RuntimeHelpers.GetHashCode(displayed).ToString("X8")
					+ " currentState=True");
				#endif
			}

			return true;
		}

		private static void InvalidateBinding(PanelBinding binding)
		{
			if (binding == null)
				return;
			if (ReferenceEquals(_tintedPanel, binding))
				RestoreTentativeShopNameTextColor();

			binding.Instance = null;
			binding.ReCheckColor = null;
			binding.CloseClick = null;
			binding.Rows = null;
			binding.RowButtons = null;
			binding.CloseButton = null;
			binding.Bound = false;
		}

		/// <summary>Resolves the displayed UIState and binds every per-frame member once.</summary>
		private static bool EnsureBound(PanelBinding binding)
		{
			if (binding == null)
				return false;

			if (binding.NeedsInstanceValidation)
			{
				binding.NeedsInstanceValidation = false;
				if (!TryResolveDisplayedInstance(binding))
				{
					InvalidateBinding(binding);
					return false;
				}
			}

			if (binding.Bound)
				return true;
			if (binding.Instance == null)
				return false;

			UIText[] rows = CollectRows(binding.Instance as UIElement);
			// Without a one-to-one row mapping there is no honest tentative display, so stay out of the way.
			if (rows == null || rows.Length != binding.ItemCount)
				return false;

			if (!TryCollectButtons(
				    binding.Instance as UIElement,
				    binding.ItemCount,
				    out UIImageButton[] rowButtons,
				    out UIImageButton closeButton))
				return false;

			binding.ReCheckColor = (Action)Delegate.CreateDelegate(
				typeof(Action), binding.Instance, binding.ReCheckColorMethod);
			binding.CloseClick = (UiClick)Delegate.CreateDelegate(
				typeof(UiClick), binding.Instance, binding.CloseClickMethod);
			binding.Rows = rows;
			binding.RowButtons = rowButtons;
			binding.CloseButton = closeButton;
			binding.Bound = true;
			return true;
		}

		/// <summary>Reads the panel's real UIText children in visual order 窶・no fixed screen coordinates.</summary>
		private static UIText[] CollectRows(UIElement root)
		{
			if (root == null)
				return null;

			var found = new List<UIText>();
			Collect(root, 0);
			if (found.Count == 0)
				return null;

			found.Sort(static (a, b) => a.Top.Pixels.CompareTo(b.Top.Pixels));
			return found.ToArray();

			void Collect(UIElement element, int depth)
			{
				if (element == null || depth > MaxRowSearchDepth)
					return;
				foreach (UIElement child in element.Children)
				{
					if (child is UIText text)
						found.Add(text);
					else
						Collect(child, depth + 1);
				}
			}
		}

		/// <summary>
		/// Splits the panel's UIImageButtons into the left row checkboxes (Left.Pixels near 10) and the
		/// close button on the right, both in the panel's own layout units.
		/// </summary>
		private static bool TryCollectButtons(
			UIElement root,
			int expectedRowCount,
			out UIImageButton[] rowButtons,
			out UIImageButton closeButton)
		{
			rowButtons = null;
			closeButton = null;
			if (root == null || expectedRowCount <= 0)
				return false;

			var rows = new List<UIImageButton>();
			UIImageButton close = null;
			Collect(root, 0);

			if (rows.Count != expectedRowCount)
				return false;

			rows.Sort(static (a, b) => a.Top.Pixels.CompareTo(b.Top.Pixels));
			rowButtons = rows.ToArray();
			closeButton = close;
			return true;

			void Collect(UIElement element, int depth)
			{
				if (element == null || depth > MaxRowSearchDepth)
					return;
				foreach (UIElement child in element.Children)
				{
					if (child is UIImageButton button)
					{
						if (button.Left.Pixels < RowButtonMaxLeftPixels)
							rows.Add(button);
						else
							close ??= button;
						continue;
					}

					Collect(child, depth + 1);
				}
			}
		}

		private static bool Disable(string reason, Exception ex)
		{
			State.Disabled = true;
			State.Applied = false;
			State.LastDisableReason = reason;
			RestoreTentativeShopNameTextColor();
			_engaged = null;
			State.Disengage();
			AlchemistNpcLiteShopListGamepadCompatLayout.AbandonConfirmReleaseGate(State);
			ClearActive();

			if (!AlchemistNpcLiteShopListGamepadCompatLayout.ShouldLogDisable(State))
				return false;

			try
			{
				string msg = "[AlchemistNpcLiteShopListGamepadCompat] Disabled (" + reason + "). "
					+ (ex != null ? ex.GetType().Name + ": " + ex.Message : "");
				AdvancedKeybinds.Instance?.Logger?.Warn(msg);
			}
			catch
			{
				// ignore
			}

			return false;
		}

		/// <summary>
		/// AlchemistNPCLiteWorld recreates every ShopChangeUI* during OnWorldLoad. Drop only runtime
		/// references; cached type/method metadata remains valid and the next list-open edge resolves the
		/// new official UserInterface.CurrentState.
		/// </summary>
		public static void InvalidateWorldReferences()
		{
			ReleaseSession();
			State.ArchitectPanelWasVisible = false;
			State.ArchitectRecheckDoneThisOpen = false;
			_worldSystemOwner = null;
			if (_panels == null)
				return;

			for (int i = 0; i < _panels.Length; i++)
			{
				PanelBinding panel = _panels[i];
				if (panel == null)
					continue;
				InvalidateBinding(panel);
				panel.InstanceSources = Array.Empty<InstanceSource>();
				panel.WasVisible = false;
				panel.NeedsInstanceValidation = false;
			}

			ClearActive();
		}
	}

	/// <summary>Drives D-Pad tentative selection for any open AlchemistNPCLite ShopChangeUI* list.</summary>
	public sealed class AlchemistNpcLiteShopListGamepadCompatSystem : ModSystem
	{
		public override void OnWorldLoad()
		{
			AlchemistNpcLiteShopListGamepadCompat.InvalidateWorldReferences();
		}

		public override void OnWorldUnload()
		{
			AlchemistNpcLiteShopListGamepadCompat.InvalidateWorldReferences();
		}

		public override void PostUpdateInput()
		{
			AlchemistNpcLiteShopListGamepadCompat.TryProcessInput();
		}

	}
}
