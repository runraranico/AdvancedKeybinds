using AdvancedKeybinds.Patches;
using Terraria.ModLoader;

namespace AdvancedKeybinds
{
	public class AdvancedKeybindsPlayer : ModPlayer
	{
		public override void OnEnterWorld()
		{
			WinKeySuppressHintSystem.TryShowOnWorldEnter();
		}

		public override void PreUpdate()
		{
			// PostUpdateInput / PreUpdatePlayers may already have armed hidden-shortcut flags.
			// Roll presence / sticky end here — do not clear the one-frame shortcut consume flag.
			KeybindUiWorldInputGuard.BeginInputFrame();
			// Claim mouseInterface early so vanilla SetControls does not arm UseTile from RMB
			// while the Controls UI session is open (cursor may be outside the panel).
			KeybindUiWorldInputGuard.ApplyEarlyWorldMouseClaim(Player);
		}

		/// <summary>
		/// After vanilla maps MouseRight → controlUseTile: while Controls UI session is open
		/// (entire FancyUI lifetime after ManageControls, independent of cursor position), or after
		/// Ctrl+Shift+Alt+RMB hidden shortcut (until RMB up), clear world Use-Tile only.
		/// Main.mouseRight is left intact for UI / AK.
		/// </summary>
		public override void SetControls()
		{
			KeybindUiWorldInputGuard.ApplyWorldRightClickBlock(Player);
		}

		public override void PostUpdate()
		{
			// HoverOverNPCs runs in draw; keep talkNPC / npcChatRelease suppressed while latch/UI is active.
			KeybindUiWorldInputGuard.ApplyPostUpdateNpcChatGuard(Player);
		}
	}
}
