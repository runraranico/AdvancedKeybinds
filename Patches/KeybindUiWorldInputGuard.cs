using System;
using System.Reflection;
using AdvancedKeybinds.Binding;
using AdvancedKeybinds.Core;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.UI;

namespace AdvancedKeybinds.Patches
{
	/// <summary>
	/// Blocks world-side right-click without clearing <see cref="Main.mouseRight"/>.
	/// Covers Use-Tile (<see cref="Player.controlUseTile"/>) and NPC chat
	/// (<see cref="Main.HoverOverNPCs"/> reads mouseRight + npcChatRelease; Smart Cursor uses this path).
	/// Also skips HoverOverNPCs while an NPC buy-shop is open and Pad L1 is held, so L1→MouseRight
	/// cannot switch talkNPC / close the shop — without clearing mouseRight or talkNPC.
	/// </summary>
	internal static class KeybindUiWorldInputGuard
	{
		private delegate void orig_HoverOverNPCs(Main self, Rectangle mouseRectangle);

		private static bool _seenManageControlsThisFrame;
		private static bool _seenManageControlsLastFrame;
		/// <summary>
		/// Sticky Controls session. Set by Notify / hidden shortcut; cleared only after FancyUI is gone
		/// and open-grace has expired (not when the cursor leaves the panel).
		/// </summary>
		private static bool _inWorldControlsSession;
		/// <summary>Frames to keep sticky true while OpenKeybinds has not yet raised Main.inFancyUI.</summary>
		private static int _sessionOpenGraceFrames;
		private static bool _wasUiOpen;
		private static bool _suppressWorldUseTileUntilRightUp;
		private static bool _loggedBlockEdge;
		private static bool _loggedNpcHoverSkip;
		/// <summary>
		/// Armed by hidden shortcut; cleared only after ApplyWorldRightClickBlock has applied it
		/// (never in BeginInputFrame — that wiped the open-frame flag before SetControls).
		/// </summary>
		private static bool _hiddenShortcutConsumedRightClick;

		internal static void Apply()
		{
			MethodInfo hover = typeof(Main).GetMethod(
				"HoverOverNPCs",
				BindingFlags.Instance | BindingFlags.NonPublic,
				null,
				new[] { typeof(Rectangle) },
				null);

			if (hover == null)
			{
				AdvancedKeybinds.Instance?.Logger.Warn(
					"Advanced Keybinds could not hook Main.HoverOverNPCs; Smart Cursor NPC talk may leak on hidden shortcut.");
				return;
			}

			ModDetours.Apply(hover, (orig_HoverOverNPCs orig, Main self, Rectangle mouseRectangle) => {
				if (ShouldBlockWorldRightClick())
				{
					// NPC talk (including Smart Cursor speech-bubble targets) uses Main.mouseRight here,
					// not controlUseTile. Skip without clearing mouseRight so Controls UI / AK capture keep working.
					SuppressNpcChatState("HoverOverNPCs-skip");
					if (!_loggedNpcHoverSkip)
					{
						_loggedNpcHoverSkip = true;
						if (AkDiag.Enabled)
						{
							AkDiag.Log(
							"KeybindUI skipped HoverOverNPCs (NPC talk / SmartInteract)"
							+ " mouseRight=" + Main.mouseRight
							+ " stickySession=" + _inWorldControlsSession
							+ " suppressUntilUp=" + _suppressWorldUseTileUntilRightUp
							+ " shortcutConsumed=" + _hiddenShortcutConsumedRightClick);
						}
					}

					return;
				}

				_loggedNpcHoverSkip = false;

				// Buy-shop + Pad L1: L1 is MouseRight on XBoxGamepadUI and would SetTalkNPC on a
				// nearby NPC (also SetNPCShopIndex(0)). Skip the hover-talk path only — do not clear
				// mouseRight (shop RMB buy) and do not clear talkNPC (keep current shop open).
				if (ShouldSkipHoverOverNpcsForOpenNpcShopPadShoulder())
					return;

				orig(self, mouseRectangle);
			});
		}

		/// <summary>
		/// True while the NPC item shop is open and FNA LeftShoulder is held.
		/// Physical mouse RMB alone never triggers this.
		/// </summary>
		internal static bool ShouldSkipHoverOverNpcsForOpenNpcShopPadShoulder()
		{
			Player player = Main.LocalPlayer;
			int talk = player?.talkNPC ?? -1;
			return NpcShopPadShoulderTalkPolicy.ShouldSkipHoverOverNpcs(
				npcShopOpen: NpcShopPadShoulderTalkPolicy.IsNpcBuyShopOpen(Main.npcShop),
				talkNpcActive: NpcShopPadShoulderTalkPolicy.IsTalkNpcActive(talk),
				leftShoulderDown: FnaGamePadProvider.IsDown(GamePadToken.LeftShoulder),
				physicalMouseRightDown: MouseInputHelper.IsPhysicalRightDown());
		}

		/// <summary>Call from UIManageControls Update/Draw (any cursor position).</summary>
		internal static void NotifyManageControlsUiPresent()
		{
			_seenManageControlsThisFrame = true;
			if (!Main.gameMenu)
			{
				_inWorldControlsSession = true;
				if (Main.inFancyUI)
					_sessionOpenGraceFrames = 0;
			}
		}

		/// <summary>
		/// Call when Ctrl+Shift+Alt+physical-RMB opens Controls. Arms latch + session and
		/// immediately strips LocalPlayer world UseTile / NPC chat edge (does not clear Main.mouseRight).
		/// </summary>
		internal static void NotifyHiddenShortcutConsumedRightClick()
		{
			_hiddenShortcutConsumedRightClick = true;
			_suppressWorldUseTileUntilRightUp = true;
			if (!Main.gameMenu)
			{
				_inWorldControlsSession = true;
				// OpenKeybinds may set inFancyUI later this frame — keep sticky alive until then.
				_sessionOpenGraceFrames = Math.Max(_sessionOpenGraceFrames, 3);
			}

			ApplyImmediateWorldRightClickBlock(Main.LocalPlayer);
			SuppressNpcChatState("HiddenShortcut-notify");

			if (AkDiag.Enabled)
			{
				AkDiag.Log(
				"HiddenShortcut world UseTile+NPC armed+immediate"
				+ " mouseRight=" + Main.mouseRight
				+ " physicalRight=" + MouseInputHelper.IsPhysicalRightDown()
				+ " inFancyUI=" + Main.inFancyUI
				+ " grace=" + _sessionOpenGraceFrames);
			}
		}

		/// <summary>
		/// Strip world UseTile on LocalPlayer immediately (e.g. from shortcut hook before SetControls).
		/// </summary>
		internal static void ApplyImmediateWorldRightClickBlock(Player player)
		{
			if (player == null || Main.gameMenu)
				return;

			player.controlUseTile = false;
			player.mouseInterface = true;
		}

		/// <summary>
		/// Prevent HoverOverNPCs talk edge and clear any talk already started this frame.
		/// Does not touch Main.mouseRight.
		/// </summary>
		internal static void SuppressNpcChatState(string reason)
		{
			if (Main.gameMenu)
				return;

			// Talk requires mouseRight && npcChatRelease; force release false while blocked.
			Main.npcChatRelease = false;

			Player player = Main.LocalPlayer;
			if (player != null && player.talkNPC >= 0)
			{
				player.SetTalkNPC(-1, fromNet: false);
				Main.npcChatText = string.Empty;
				if (AkDiag.Enabled) AkDiag.Log("KeybindUI cleared talkNPC (" + reason + ")");
			}
		}

		/// <summary>
		/// Advance per-frame presence flags. Must NOT clear hidden-shortcut consume here.
		/// </summary>
		internal static void BeginInputFrame()
		{
			_seenManageControlsLastFrame = _seenManageControlsThisFrame;
			_seenManageControlsThisFrame = false;

			if (_sessionOpenGraceFrames > 0)
				_sessionOpenGraceFrames--;

			// End sticky only when FancyUI is gone and grace expired (open frame keeps sticky).
			if (_inWorldControlsSession
				&& !Main.inFancyUI
				&& !Main.gameMenu
				&& _sessionOpenGraceFrames <= 0)
			{
				_inWorldControlsSession = false;
			}
		}

		/// <summary>
		/// True for the whole Controls UI lifetime. Not based on hover, mouseInterface, or panel bounds.
		/// </summary>
		internal static bool IsManageControlsUiOpen()
		{
			if (Main.gameMenu)
			{
				if (TryIsManageControlsState(Main.MenuUI?.CurrentState))
					return true;
				if (_seenManageControlsThisFrame || _seenManageControlsLastFrame)
					return true;
				return false;
			}

			// Sticky session: do NOT require inFancyUI (may lag one frame behind OpenKeybinds).
			if (_inWorldControlsSession)
				return true;

			if (TryIsManageControlsState(Main.InGameUI?.CurrentState))
				return true;

			if (Main.inFancyUI && IsCurrentFancyStateManageControls())
				return true;

			if (_seenManageControlsThisFrame || _seenManageControlsLastFrame)
				return true;

			return false;
		}

		private static bool IsCurrentFancyStateManageControls()
		{
			UIState state = Main.InGameUI?.CurrentState;
			if (TryIsManageControlsState(state))
				return true;

			try
			{
				if (state != null && Main.ManageControlsMenu != null
					&& ReferenceEquals(state, Main.ManageControlsMenu))
					return true;
			}
			catch
			{
			}

			return false;
		}

		private static bool TryIsManageControlsState(UIState state)
		{
			if (state == null)
				return false;

			Type type = state.GetType();
			if (type.Name.IndexOf("UIManageControls", StringComparison.Ordinal) >= 0)
				return true;

			try
			{
				if (Main.ManageControlsMenu != null && ReferenceEquals(state, Main.ManageControlsMenu))
					return true;
			}
			catch
			{
			}

			return false;
		}

		internal static bool ShouldBlockWorldRightClick()
			=> _hiddenShortcutConsumedRightClick
				|| _suppressWorldUseTileUntilRightUp
				|| IsManageControlsUiOpen();

		/// <summary>
		/// Before vanilla SetControls: claim mouseInterface so world mouse paths stay quiet.
		/// </summary>
		internal static void ApplyEarlyWorldMouseClaim(Player player)
		{
			if (player == null || Main.gameMenu)
				return;

			if (!ShouldBlockWorldRightClick())
				return;

			player.mouseInterface = true;
			SuppressNpcChatState("EarlyClaim");
		}

		/// <summary>
		/// After Update: keep NPC chat suppressed while latch/UI session is active (draw-phase HoverOverNPCs is hooked separately).
		/// </summary>
		internal static void ApplyPostUpdateNpcChatGuard(Player player)
		{
			if (player == null || Main.gameMenu)
				return;

			if (ShouldBlockWorldRightClick())
				SuppressNpcChatState("PostUpdate");
		}

		/// <summary>
		/// After vanilla SetControls: strip world Use-Tile for shortcut frame + whole Controls session
		/// (cursor inside or outside). Does not clear Main.mouseRight.
		/// </summary>
		internal static void ApplyWorldRightClickBlock(Player player)
		{
			if (player == null || Main.gameMenu)
				return;

			bool uiOpen = IsManageControlsUiOpen();
			bool rightHeld = Main.mouseRight || MouseInputHelper.IsPhysicalRightDown();

			if (uiOpen)
			{
				if (rightHeld)
					_suppressWorldUseTileUntilRightUp = true;

				if (!_loggedBlockEdge)
				{
					_loggedBlockEdge = true;
					if (AkDiag.Enabled)
					{
						AkDiag.Log(
						"KeybindUI world UseTile blocked (session open)"
						+ " mouseRight=" + Main.mouseRight
						+ " inFancyUI=" + Main.inFancyUI
						+ " stickySession=" + _inWorldControlsSession
						+ " grace=" + _sessionOpenGraceFrames
						+ " shortcutConsumed=" + _hiddenShortcutConsumedRightClick
						+ " suppressUntilUp=" + _suppressWorldUseTileUntilRightUp);
					}
				}

				_wasUiOpen = true;
			}
			else
			{
				if (_wasUiOpen)
				{
					if (rightHeld)
						_suppressWorldUseTileUntilRightUp = true;

					if (AkDiag.Enabled)
					{
						AkDiag.Log(
						"KeybindUI closed; residual world UseTile latch="
						+ _suppressWorldUseTileUntilRightUp
						+ " mouseRight=" + Main.mouseRight);
					}
					_loggedBlockEdge = false;
					_loggedNpcHoverSkip = false;
				}

				_wasUiOpen = false;
			}

			if (_suppressWorldUseTileUntilRightUp && !rightHeld)
			{
				_suppressWorldUseTileUntilRightUp = false;
				if (AkDiag.Enabled) AkDiag.Log("KeybindUI residual world UseTile latch cleared (RMB up).");
			}

			bool blockWorld = ShouldBlockWorldRightClick();

			// Consume one-frame shortcut flag only after this SetControls pass used it.
			_hiddenShortcutConsumedRightClick = false;

			if (!blockWorld)
				return;

			player.controlUseTile = false;
			player.mouseInterface = true;
			SuppressNpcChatState("SetControls");
		}
	}
}
