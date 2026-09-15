namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// When ManageControls is open, promote to XBoxGamepadUI only when appropriate:
	/// already on GamePad gameplay mode, or real D-Pad / stick navigation intent after KB/mouse entry.
	/// Does not permanently lock the screen to GamePad while idle pad is connected.
	/// After mouse demotes CursorMode/InputMode, re-assert while D-Pad/stick remains active so the
	/// vanilla selection cursor (UsingGamepad + CursorMode.Gamepad) stays visible.
	/// </summary>
	public static class ControlsGamepadUiModePolicy
	{
		public static bool HasPadNavigationIntent(bool dpadEdge, bool stickDirectionEdge)
			=> dpadEdge || stickDirectionEdge;

		public static bool HasPadNavigationActive(
			bool dpadEdge,
			bool stickEdge,
			bool dpadHeld,
			bool stickHeld)
			=> dpadEdge || stickEdge || dpadHeld || stickHeld;

		public static bool ShouldPromoteToXboxGamepadUi(
			bool manageControlsOpen,
			bool gamePadConnected,
			bool currentIsXboxGamepadUi,
			bool currentIsXboxGamepad,
			bool padNavigationIntent)
		{
			if (!manageControlsOpen || !gamePadConnected)
				return false;
			if (currentIsXboxGamepadUi)
				return false;
			if (currentIsXboxGamepad)
				return true;
			return padNavigationIntent;
		}

		/// <summary>
		/// Post-UpdateInput recovery: while the player is actively navigating with D-Pad/stick,
		/// re-apply XBoxGamepadUI + CursorMode.Gamepad so selection display survives MouseInput demotion.
		/// Idle connected pad must not reassert (keeps KB/mouse return).
		/// </summary>
		public static bool ShouldReassertGamepadUiCursor(
			bool manageControlsOpen,
			bool gamePadConnected,
			bool padNavigationActive)
		{
			if (!manageControlsOpen || !gamePadConnected)
				return false;
			return padNavigationActive;
		}

		/// <summary>
		/// Selection cursor is considered on when UsingGamepad (mode 3|4) and CursorMode is Gamepad.
		/// </summary>
		public static bool IsSelectionDisplayActive(bool usingGamepad, bool cursorModeIsGamepad)
			=> usingGamepad && cursorModeIsGamepad;

		/// <summary>
		/// Vanilla UILinkPointNavigator snaps Mouse→CurrentPoint only when
		/// <c>Triggers.Current.UsedMovementKey</c> is true. On FancyUI Controls, Processkey sets
		/// that flag for Up/Down/Left/Right but not for Menu* (D-Pad). After KB/mouse entry,
		/// MouseInput clears the flag and stick/D-Pad paths that only feed navigator directions
		/// leave it false → CurrentPoint moves but the selection cursor stays at the mouse.
		/// When pad navigation is active on ManageControls, re-assert the vanilla flag so the
		/// same-frame first D-Pad/stick both moves and shows the cursor.
		/// </summary>
		public static bool ShouldMarkUsedMovementKeyForSelectionFollow(
			bool manageControlsOpen,
			bool gamePadConnected,
			bool padNavigationActive)
		{
			if (!manageControlsOpen || !gamePadConnected)
				return false;
			return padNavigationActive;
		}
	}
}
