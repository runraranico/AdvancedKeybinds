using AdvancedKeybinds.Binding;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Terraria;
using Terraria.GameContent;
using Terraria.Localization;
using Terraria.ModLoader;
using Terraria.UI.Chat;

namespace AdvancedKeybinds.Patches
{
	/// <summary>
	/// One-shot cyan tip near the player's feet when entering a world with Win-key binds
	/// while Windows-key suppression is off. Esc dismisses immediately.
	/// </summary>
	public class WinKeySuppressHintSystem : ModSystem
	{
		private const int DurationFrames = 60 * 20;
		private static readonly Color HintColor = new(0, 255, 255);

		private static int _remainingFrames;
		private static bool _escapeWasDown;

		public static void TryShowOnWorldEnter()
		{
			_remainingFrames = 0;
			_escapeWasDown = true;

			AdvancedKeybindsConfig config = AdvancedKeybindsConfig.Instance;
			if (config == null || config.SuppressWindowsKey)
				return;

			if (!BindingStorage.AnyBindingUsesWindowsKey())
				return;

			_remainingFrames = DurationFrames;
			if (AkDiag.Enabled) AkDiag.Log("WinKey suppress hint shown (20s, Esc to dismiss)");
		}

		public override void PostUpdateInput()
		{
			if (Main.gameMenu)
			{
				_remainingFrames = 0;
				return;
			}

			if (_remainingFrames <= 0)
				return;

			bool escDown = Main.keyState.IsKeyDown(Keys.Escape);
			if (escDown && !_escapeWasDown)
			{
				_remainingFrames = 0;
				if (AkDiag.Enabled) AkDiag.Log("WinKey suppress hint dismissed (Esc)");
				_escapeWasDown = escDown;
				return;
			}

			_escapeWasDown = escDown;
			_remainingFrames--;
		}

		public override void PostDrawInterface(SpriteBatch spriteBatch)
		{
			if (_remainingFrames <= 0 || Main.gameMenu || Main.LocalPlayer == null || !Main.LocalPlayer.active)
				return;

			string text = Language.GetTextValue("Mods.AdvancedKeybinds.Hints.WinKeySuppress");
			if (string.IsNullOrEmpty(text))
				return;

			Player player = Main.LocalPlayer;
			Vector2 feetWorld = new(player.position.X + player.width * 0.5f, player.position.Y + player.height);
			Vector2 hudPx = Vector2.Transform(
				feetWorld - Main.screenPosition,
				Main.GameViewMatrix.ZoomMatrix);
			float uiScale = Main.UIScale <= 0.01f ? 1f : Main.UIScale;
			Vector2 uiPos = hudPx / uiScale + new Vector2(0f, 8f);

			Vector2 scale = new(3f);
			Vector2 size = ChatManager.GetStringSize(FontAssets.MouseText.Value, text, scale);
			Vector2 pos = new(uiPos.X - size.X * 0.5f, uiPos.Y);

			ChatManager.DrawColorCodedStringWithShadow(
				spriteBatch,
				FontAssets.MouseText.Value,
				text,
				pos,
				HintColor,
				0f,
				Vector2.Zero,
				scale);
		}
	}
}
