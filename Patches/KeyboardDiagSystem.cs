using System.Linq;
using AdvancedKeybinds.Binding;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.ModLoader;

namespace AdvancedKeybinds.Patches
{
	/// <summary>On-screen overlay for <see cref="KeyboardDiagMode"/> recent lines.</summary>
	public sealed class KeyboardDiagSystem : ModSystem
	{
		public override void PostUpdateEverything()
		{
			if (!KeyboardDiagMode.IsActive || Main.gameMenu)
				return;
		}

		public override void PostDrawInterface(SpriteBatch spriteBatch)
		{
			if (!KeyboardDiagMode.IsActive || Main.gameMenu)
				return;

			var lines = KeyboardDiagMode.RecentLines;
			if (lines.Count == 0 && string.IsNullOrEmpty(KeyboardDiagMode.LastLine))
				return;

			int x = 12;
			int y = 80;
			string header = "Keyboard diagnostic (Mod Config)";
			Utils.DrawBorderString(spriteBatch, header, new Vector2(x, y), Color.Gold);
			y += 22;

			int start = lines.Count > 8 ? lines.Count - 8 : 0;
			for (int i = start; i < lines.Count; i++)
			{
				string line = lines[i];
				if (line.Length > 140)
					line = line.Substring(0, 137) + "...";
				Utils.DrawBorderString(spriteBatch, line, new Vector2(x, y), Color.White);
				y += 18;
			}
		}
	}
}
