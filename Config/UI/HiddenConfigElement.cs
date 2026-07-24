using Microsoft.Xna.Framework.Graphics;
using Terraria.ModLoader.Config.UI;

namespace AdvancedKeybinds.Config.UI
{
	/// <summary>
	/// Zero-height ConfigElement: field stays on ModConfig (JSON + Restore Defaults) but is not shown in the UI.
	/// Used for drag-only values such as Hold gauge custom screen coordinates.
	/// </summary>
	public class HiddenConfigElement : ConfigElement
	{
		public override void OnBind()
		{
			base.OnBind();
			Height.Set(0f, 0f);
		}

		protected override void DrawSelf(SpriteBatch spriteBatch)
		{
			// Intentionally blank — hide from Config UI while keeping serialization.
		}
	}
}
