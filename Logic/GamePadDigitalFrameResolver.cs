namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// One tick of raw gamepad input, already lifted out of FNA. Pure value type — copying it
	/// allocates nothing, so the input path can run without touching the heap.
	/// </summary>
	public struct GamePadRawFrame
	{
		public bool Connected;

		/// <summary>
		/// Plain button bits only: no trigger bits and no stick-direction bits, because those
		/// two are hysteresis-driven and are derived by <see cref="GamePadDigitalFrameResolver"/>.
		/// </summary>
		public ulong ButtonBits;

		public float LeftTriggerAnalog;
		public float RightTriggerAnalog;
		public float LeftStickX;
		public float LeftStickY;
		public float RightStickX;
		public float RightStickY;

		public static GamePadRawFrame Disconnected => default;
	}

	/// <summary>
	/// Raw frame + previous digital state → this tick's digital state.
	///
	/// Same rules as the original token pipeline (<c>GamePadTokenMapper.AppendDigitalHeld</c>):
	/// triggers use <see cref="GamePadTriggerPolicy"/> hysteresis and each stick resolves one
	/// horizontal plus one vertical direction through <see cref="GamePadStickDirectionClassifier"/>.
	/// The only difference is the carrier — bits instead of a freshly allocated string set.
	/// </summary>
	public static class GamePadDigitalFrameResolver
	{
		public static ulong Resolve(in GamePadRawFrame raw, ulong previousDigital)
		{
			if (!raw.Connected)
				return GamePadDigitalBits.None;

			ulong mask = raw.ButtonBits;

			if (GamePadTriggerPolicy.EvaluateDigital(
					raw.LeftTriggerAnalog,
					GamePadDigitalBits.IsDown(previousDigital, GamePadDigitalBits.LeftTrigger)))
			{
				mask |= GamePadDigitalBits.LeftTrigger;
			}

			if (GamePadTriggerPolicy.EvaluateDigital(
					raw.RightTriggerAnalog,
					GamePadDigitalBits.IsDown(previousDigital, GamePadDigitalBits.RightTrigger)))
			{
				mask |= GamePadDigitalBits.RightTrigger;
			}

			mask |= ResolveStick(
				leftStick: true,
				raw.LeftStickX,
				raw.LeftStickY,
				GamePadDigitalBits.ToStickAxes(previousDigital, leftStick: true));

			mask |= ResolveStick(
				leftStick: false,
				raw.RightStickX,
				raw.RightStickY,
				GamePadDigitalBits.ToStickAxes(previousDigital, leftStick: false));

			return mask;
		}

		private static ulong ResolveStick(
			bool leftStick,
			float x,
			float y,
			GamePadStickDigitalAxes previousAxes)
		{
			GamePadStickDigitalAxes axes = GamePadStickDirectionClassifier.ApplyRuntimeAxes(
				previousAxes,
				x,
				y,
				GamePadStickDirectionPolicy.RuntimePressThreshold,
				GamePadStickDirectionPolicy.ReleaseThreshold);

			return GamePadDigitalBits.FromStickAxes(leftStick, axes);
		}
	}
}
