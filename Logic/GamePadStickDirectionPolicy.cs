namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Shared thresholds for LS/RS direction digitalization (Capture vs Runtime).
	/// No OS branching; FNA ThumbStick space only.
	/// </summary>
	public static class GamePadStickDirectionPolicy
	{
		/// <summary>max(|X|,|Y|) below this is Neutral (per stick). Capture Armed only.</summary>
		public const float NeutralThreshold = 0.20f;

		/// <summary>Runtime per-axis press floor (|axis| &gt;= this).</summary>
		public const float RuntimePressThreshold = 0.50f;

		/// <summary>ComboPanel Capture strong-axis press floor.</summary>
		public const float CapturePressThreshold = 0.65f;

		/// <summary>Runtime per-axis release floor (stay held while |axis| &gt;= this).</summary>
		public const float ReleaseThreshold = 0.30f;

		/// <summary>
		/// Capture strong-axis tie-break only (|ax-ay| &lt;= epsilon → horizontal).
		/// Not used by Runtime independent-axis digitalization.
		/// </summary>
		public const float AxisTieEpsilon = 0.02f;

		public static bool IsNeutral(float x, float y)
		{
			float ax = x < 0f ? -x : x;
			float ay = y < 0f ? -y : y;
			float max = ax > ay ? ax : ay;
			return max < NeutralThreshold;
		}
	}
}
