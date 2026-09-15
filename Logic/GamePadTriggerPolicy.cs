namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Digital press policy for analog gamepad triggers (L2 / R2).
	/// Shared constants only — no Keyboard / Win32 / Steam Input.
	/// </summary>
	public static class GamePadTriggerPolicy
	{
		/// <summary>Analog value at or above this becomes pressed when previously up.</summary>
		public const float PressThreshold = 0.50f;

		/// <summary>
		/// Analog value below this becomes released when previously down.
		/// Slightly under <see cref="PressThreshold"/> to reduce edge chatter (light hysteresis).
		/// </summary>
		public const float ReleaseThreshold = 0.40f;

		public static bool EvaluateDigital(float analog01, bool wasDown)
		{
			if (wasDown)
				return analog01 >= ReleaseThreshold;
			return analog01 >= PressThreshold;
		}
	}
}
