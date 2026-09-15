using System;
using AdvancedKeybinds.Logic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ReLogic.Content;
using Terraria;
using Terraria.GameContent;
using Terraria.ModLoader.Config.UI;
using Terraria.UI;

namespace AdvancedKeybinds.Config.UI
{
	/// <summary>
	/// Bool ModConfig row that is fully interactive on Windows and collapsed (height 0, no draw,
	/// no hit test contribution) on Linux / macOS. Serialization of the bound property is unchanged —
	/// this element never writes the saved value based on host OS.
	/// Do not reuse <see cref="HiddenConfigElement"/> (that type owns Hold-gauge Custom coordinates).
	/// </summary>
	public class WindowsOnlyBoolElement : ConfigElement<bool>
	{
		private Asset<Texture2D> _toggleTexture;
		private bool _visible;

		public override void OnBind()
		{
			base.OnBind();
			_visible = WindowsKeySuppressConfigPolicy.ShouldDisplaySuppressWindowsKey(OperatingSystem.IsWindows());
			if (!_visible)
			{
				Height.Set(0f, 0f);
				return;
			}

			Height.Set(30f, 0f);
			_toggleTexture = Main.Assets.Request<Texture2D>("Images/UI/Settings_Toggle");
			OnLeftClick += (_, _) => Value = !Value;
		}

		public override bool ContainsPoint(Vector2 point)
		{
			if (!_visible)
				return false;
			return base.ContainsPoint(point);
		}

		protected override void DrawSelf(SpriteBatch spriteBatch)
		{
			if (!_visible)
				return;

			base.DrawSelf(spriteBatch);

			CalculatedStyle dimensions = GetDimensions();
			string label = Value ? Lang.menu[126].Value : Lang.menu[124].Value;
			Terraria.UI.Chat.ChatManager.DrawColorCodedStringWithShadow(
				spriteBatch,
				FontAssets.ItemStack.Value,
				label,
				new Vector2(dimensions.X + dimensions.Width - 60f, dimensions.Y + 8f),
				Color.White,
				0f,
				Vector2.Zero,
				new Vector2(0.8f));

			if (_toggleTexture?.Value == null)
				return;

			Texture2D texture = _toggleTexture.Value;
			Rectangle sourceRectangle = new(
				Value ? (texture.Width - 2) / 2 + 2 : 0,
				0,
				(texture.Width - 2) / 2,
				texture.Height);
			Vector2 drawPosition = new(
				dimensions.X + dimensions.Width - sourceRectangle.Width - 10f,
				dimensions.Y + 8f);
			spriteBatch.Draw(texture, drawPosition, sourceRectangle, Color.White);
		}
	}
}
