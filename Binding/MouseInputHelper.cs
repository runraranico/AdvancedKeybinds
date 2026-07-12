using System.Reflection;
using Microsoft.Xna.Framework.Input;
using Terraria;

namespace AdvancedKeybinds.Binding
{
	internal static class MouseInputHelper
	{
		private static readonly FieldInfo MouseX1Field = typeof(Main).GetField("mouseX1", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
		private static readonly FieldInfo MouseX2Field = typeof(Main).GetField("mouseX2", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

		/// <summary>
		/// Physical mouse buttons from the OS/device. Prefer these over Main.mouseLeft/Right/Middle,
		/// which follow remapped actions (e.g. 対話・動作) rather than the hardware button.
		/// </summary>
		public static bool IsPhysicalLeftDown()
			=> Mouse.GetState().LeftButton == ButtonState.Pressed;

		public static bool IsPhysicalRightDown()
			=> Mouse.GetState().RightButton == ButtonState.Pressed;

		public static bool IsPhysicalMiddleDown()
			=> Mouse.GetState().MiddleButton == ButtonState.Pressed;

		public static bool IsMouseX1Down()
		{
			if (MouseX1Field?.GetValue(null) is bool value)
				return value;

			return false;
		}

		public static bool IsMouseX2Down()
		{
			if (MouseX2Field?.GetValue(null) is bool value)
				return value;

			return false;
		}

		/// <summary>
		/// True when the OS cursor is over the game client area. Physical
		/// <see cref="Mouse.GetState"/> buttons stay pressed for other-monitor clicks;
		/// those report coordinates outside 0..screen size and must not finish slot capture.
		/// Do not use <see cref="Main.mouseLeft"/> here — the combo panel often leaves it false
		/// even for in-window clicks, which would block LeftClick tips and Mouse binds.
		/// </summary>
		public static bool IsCursorInsideGameClient()
		{
			if (!Main.hasFocus)
				return false;

			MouseState state = Mouse.GetState();
			return state.X >= 0
				&& state.Y >= 0
				&& state.X < Main.screenWidth
				&& state.Y < Main.screenHeight;
		}
	}
}
