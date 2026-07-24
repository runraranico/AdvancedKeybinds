using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.GameContent;
using Terraria.ModLoader.Config;
using Terraria.ModLoader.Config.UI;
using Terraria.UI;

namespace AdvancedKeybinds.Config.UI
{
	/// <summary>
	/// Same layout as tML's ColorElement (preview + R/G/B sliders), with mod-localizable channel labels.
	/// Alpha is not exposed in the UI; stored A stays at its current/default value (typically 255).
	/// </summary>
	public class LocalizedColorElement : ConfigElement
	{
		private class ColorObject
		{
			private readonly PropertyFieldWrapper memberInfo;
			private readonly object item;
			private readonly IList<Color> list;
			private readonly int index;

			internal Color current;

			[LabelKey("$Mods.AdvancedKeybinds.Config.ColorChannel.Red.Label")]
			[SliderColor(255, 0, 0)]
			public byte R
			{
				get => current.R;
				set
				{
					current.R = value;
					Update();
				}
			}

			[LabelKey("$Mods.AdvancedKeybinds.Config.ColorChannel.Green.Label")]
			[SliderColor(0, 255, 0)]
			public byte G
			{
				get => current.G;
				set
				{
					current.G = value;
					Update();
				}
			}

			[LabelKey("$Mods.AdvancedKeybinds.Config.ColorChannel.Blue.Label")]
			[SliderColor(0, 0, 255)]
			public byte B
			{
				get => current.B;
				set
				{
					current.B = value;
					Update();
				}
			}

			private void Update()
			{
				if (list == null)
					memberInfo.SetValue(item, current);
				else
					list[index] = current;
			}

			public ColorObject(PropertyFieldWrapper memberInfo, object item)
			{
				this.item = item;
				this.memberInfo = memberInfo;
				current = (Color)memberInfo.GetValue(item);
			}

			public ColorObject(IList<Color> list, int index)
			{
				this.list = list;
				this.index = index;
				current = list[index];
			}
		}

		private int height;
		private ColorObject c;

		public override void OnBind()
		{
			base.OnBind();

			height = 30;
			if (List is IList<Color> colorList)
			{
				DrawLabel = false;
				c = new ColorObject(colorList, Index);
			}
			else
			{
				c = new ColorObject(MemberInfo, Item);
			}

			int order = 0;
			foreach (PropertyFieldWrapper variable in ConfigManager.GetFieldsAndProperties(c))
			{
				var wrapped = ConfigManager.WrapIt(this, ref height, variable, c, order++);
				if (List != null)
				{
					wrapped.Item1.Left.Pixels -= 20;
					wrapped.Item1.Width.Pixels += 20;
				}
			}
		}

		public override void Draw(SpriteBatch spriteBatch)
		{
			base.Draw(spriteBatch);

			// Match ModConfig slider bar width and right alignment (RangeElement uses ColorBar + 10px inset).
			CalculatedStyle dims = GetInnerDimensions();
			int width = TextureAssets.ColorBar.Value.Width;
			const int rightPad = 10;
			var preview = new Rectangle(
				(int)(dims.X + dims.Width - rightPad - width),
				(int)dims.Y,
				width,
				30);
			Main.spriteBatch.Draw(TextureAssets.MagicPixel.Value, preview, c.current);
		}
	}
}
