using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.GameContent;
using Terraria.UI.Chat;

namespace AdvancedKeybinds.Features.ListView
{
	/// <summary>
	/// Resize-edge cursor hint for AK ALL KEY. Drawn in-game (no OS SetCursor / IsMouseVisible)
	/// so it does not fight Terraria's software cursor every frame.
	/// Only bottom (height) resize is interactive — right / corner hints are not shown.
	/// </summary>
	internal static class BindingListViewResizeCursor
	{
		private enum Kind : byte
		{
			None = 0,
			Ns = 1
		}

		private static Kind _kind;
		private static readonly Color GlyphColor = new(230, 240, 255, 255);

		public static void Clear()
		{
			_kind = Kind.None;
		}

		/// <summary>Update which resize hint to show. Matches bottom height-resize hitbox only.</summary>
		public static void UpdateKind(BindingListViewState state, Point mouseUi, bool resizingBottom)
		{
			if (state == null || !state.IsOpen)
			{
				_kind = Kind.None;
				return;
			}

			if (resizingBottom)
			{
				_kind = Kind.Ns;
				return;
			}

			if (state.IsMaximized
				&& BindingListViewLayout.ResizeBottomRect(state).Contains(mouseUi))
			{
				_kind = Kind.Ns;
				return;
			}

			_kind = Kind.None;
		}

		/// <summary>Draw after Vanilla: Cursor so the hint sits on top without toggling OS cursor.</summary>
		public static void Present(SpriteBatch spriteBatch)
		{
			if (_kind == Kind.None || spriteBatch == null)
				return;

			const string glyph = "⇕";

			// Slightly past the vanilla cursor hotspot so both remain readable.
			Vector2 pos = new(Main.mouseX + 14f, Main.mouseY + 14f);
			ChatManager.DrawColorCodedStringWithShadow(
				spriteBatch,
				FontAssets.MouseText.Value,
				glyph,
				pos,
				GlyphColor,
				0f,
				Vector2.Zero,
				Vector2.One);
		}
	}
}
