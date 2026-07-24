using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.Audio;
using Terraria.GameContent;
using Terraria.ID;
using Terraria.Localization;
using Terraria.ModLoader.Config.UI;
using Terraria.UI;

namespace AdvancedKeybinds.Config.UI
{
	/// <summary>
	/// DrawTicks-like Anchor picker. Only mutates the ModConfig pending <see cref="Value"/>.
	/// Must NOT touch <c>ClientPrefs</c> or live <see cref="AdvancedKeybindsConfig.Instance"/> —
	/// Cancel without Save must leave prefs / applied config unchanged. PreferCustom / head-seed
	/// apply on Accept via <see cref="AdvancedKeybindsConfig.OnChanged"/> or the cycle hotkey.
	/// </summary>
	public class HoldTierHudAnchorElement : ConfigElement<HoldTierHudAnchor>
	{
		private static readonly HoldTierHudAnchor[] Options =
		{
			HoldTierHudAnchor.Feet,
			HoldTierHudAnchor.Mouse,
			HoldTierHudAnchor.Custom
		};

		public override void OnBind()
		{
			base.OnBind();
			Height.Set(30f, 0f);
		}

		public override void LeftMouseDown(UIMouseEvent evt)
		{
			base.LeftMouseDown(evt);
			Cycle(+1);
		}

		public override void RightMouseDown(UIMouseEvent evt)
		{
			base.RightMouseDown(evt);
			Cycle(-1);
		}

		private void Cycle(int delta)
		{
			HoldTierHudAnchor cur = Value;
			int idx = Array.IndexOf(Options, cur);
			if (idx < 0)
				idx = 0;
			int nextIdx = (idx + delta) % Options.Length;
			if (nextIdx < 0)
				nextIdx += Options.Length;
			// Pending UI value only → SetPendingChanges. No ClientPrefs / Instance side effects.
			Value = Options[nextIdx];
			SoundEngine.PlaySound(SoundID.MenuTick);
		}

		protected override void DrawSelf(SpriteBatch spriteBatch)
		{
			base.DrawSelf(spriteBatch);

			CalculatedStyle dims = GetDimensions();
			HoldTierHudAnchor value = Value;
			string text = Language.GetTextValue("Mods.AdvancedKeybinds.Configs.HoldTierHudAnchor." + value + ".Label");
			if (string.IsNullOrEmpty(text) || text.StartsWith("Mods.", StringComparison.Ordinal))
				text = value.ToString();

			Vector2 size = FontAssets.ItemStack.Value.MeasureString(text);
			float x = dims.X + dims.Width - size.X - 10f;
			float y = dims.Y + (dims.Height - size.Y) * 0.5f;
			Utils.DrawBorderString(spriteBatch, text, new Vector2(x, y), Color.White);
		}
	}
}
