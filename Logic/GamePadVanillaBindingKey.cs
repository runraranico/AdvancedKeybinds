using System;
using Microsoft.Xna.Framework.Input;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Converts between AK <c>Pad:*</c> tokens and Terraria vanilla gamepad binding strings
	/// as written by <c>PlayerInput.CheckRebindingProcessGamepad(Buttons.ToString())</c>
	/// (e.g. <c>"A"</c>, <c>"LeftShoulder"</c>, <c>"LeftTrigger"</c>).
	/// Stick-direction virtual binds are out of scope.
	/// </summary>
	public static class GamePadVanillaBindingKey
	{
		public static bool TryPadTokenToVanillaKey(string padToken, out string vanillaKey)
		{
			vanillaKey = null;
			if (!GamePadToken.TryCanonicalize(padToken, out string canonical))
				return false;

			Buttons? button = GamePadParentUiTriggerSuppress.CanonicalToButtons(canonical);
			if (!button.HasValue)
				return false;

			vanillaKey = button.Value.ToString();
			return !string.IsNullOrEmpty(vanillaKey);
		}

		public static bool TryVanillaKeyToPadToken(string vanillaKey, out string padToken)
		{
			padToken = null;
			if (string.IsNullOrWhiteSpace(vanillaKey))
				return false;

			vanillaKey = vanillaKey.Trim();

			if (Enum.TryParse(vanillaKey, ignoreCase: true, out Buttons byName)
				&& Enum.IsDefined(typeof(Buttons), byName))
			{
				return TryButtonsToPad(byName, out padToken);
			}

			if (int.TryParse(vanillaKey, out int numeric)
				&& Enum.IsDefined(typeof(Buttons), numeric))
			{
				return TryButtonsToPad((Buttons)numeric, out padToken);
			}

			return false;
		}

		private static bool TryButtonsToPad(Buttons button, out string padToken)
		{
			padToken = null;
			switch (button)
			{
				case Buttons.A: padToken = GamePadToken.A; return true;
				case Buttons.B: padToken = GamePadToken.B; return true;
				case Buttons.X: padToken = GamePadToken.X; return true;
				case Buttons.Y: padToken = GamePadToken.Y; return true;
				case Buttons.Back: padToken = GamePadToken.Back; return true;
				case Buttons.Start: padToken = GamePadToken.Start; return true;
				case Buttons.BigButton: padToken = GamePadToken.Guide; return true;
				case Buttons.LeftShoulder: padToken = GamePadToken.LeftShoulder; return true;
				case Buttons.RightShoulder: padToken = GamePadToken.RightShoulder; return true;
				case Buttons.LeftTrigger: padToken = GamePadToken.LeftTrigger; return true;
				case Buttons.RightTrigger: padToken = GamePadToken.RightTrigger; return true;
				case Buttons.LeftStick: padToken = GamePadToken.LeftStick; return true;
				case Buttons.RightStick: padToken = GamePadToken.RightStick; return true;
				case Buttons.DPadUp: padToken = GamePadToken.DPadUp; return true;
				case Buttons.DPadDown: padToken = GamePadToken.DPadDown; return true;
				case Buttons.DPadLeft: padToken = GamePadToken.DPadLeft; return true;
				case Buttons.DPadRight: padToken = GamePadToken.DPadRight; return true;
				default:
					return false;
			}
		}
	}
}
