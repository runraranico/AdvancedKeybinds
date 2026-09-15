using System.Collections.Generic;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Per-stick Neutral Armed gate for ComboPanel Capture only.
	/// Runtime Held uses RuntimePressThreshold independently; Capture edges require Armed + CapturePress.
	/// </summary>
	public static class GamePadStickCaptureArming
	{
		private static bool _sessionActive;
		private static StickArm _left;
		private static StickArm _right;

		private struct StickArm
		{
			public bool WaitingForNeutral;
			public bool Armed;
			public bool LatchUntilNeutral;
		}

		public static bool IsSessionActive => _sessionActive;

		public static void BeginSession(float leftX, float leftY, float rightX, float rightY)
		{
			_sessionActive = true;
			Init(ref _left, leftX, leftY);
			Init(ref _right, rightX, rightY);
		}

		public static void EndSession()
		{
			_sessionActive = false;
			_left = default;
			_right = default;
		}

		/// <summary>Append at most one Capture-qualified direction edge per stick this tick.</summary>
		public static void AppendCaptureEdges(
			float leftX,
			float leftY,
			float rightX,
			float rightY,
			List<string> edges)
		{
			if (!_sessionActive || edges == null)
				return;

			TryAppend(ref _left, leftStick: true, leftX, leftY, edges);
			TryAppend(ref _right, leftStick: false, rightX, rightY, edges);
		}

		public static bool IsLeftArmed => _sessionActive && _left.Armed && !_left.LatchUntilNeutral && !_left.WaitingForNeutral;
		public static bool IsRightArmed => _sessionActive && _right.Armed && !_right.LatchUntilNeutral && !_right.WaitingForNeutral;

		private static void Init(ref StickArm arm, float x, float y)
		{
			arm.LatchUntilNeutral = false;
			if (GamePadStickDirectionPolicy.IsNeutral(x, y))
			{
				arm.WaitingForNeutral = false;
				arm.Armed = true;
			}
			else
			{
				arm.WaitingForNeutral = true;
				arm.Armed = false;
			}
		}

		private static void TryAppend(ref StickArm arm, bool leftStick, float x, float y, List<string> edges)
		{
			if (GamePadStickDirectionPolicy.IsNeutral(x, y))
			{
				arm.WaitingForNeutral = false;
				arm.Armed = true;
				arm.LatchUntilNeutral = false;
				return;
			}

			if (arm.WaitingForNeutral || arm.LatchUntilNeutral || !arm.Armed)
				return;

			// Capture stays strong-axis one-token (Runtime uses independent axes).
			GamePadStickDirection dir = GamePadStickDirectionClassifier.ClassifyStrongAxis(
				x,
				y,
				GamePadStickDirectionPolicy.CapturePressThreshold,
				GamePadStickDirectionPolicy.AxisTieEpsilon);
			if (dir == GamePadStickDirection.None)
				return;

			string token = GamePadStickDirectionClassifier.ToToken(leftStick, dir);
			if (string.IsNullOrEmpty(token))
				return;

			bool exists = false;
			for (int i = 0; i < edges.Count; i++)
			{
				if (string.Equals(edges[i], token, System.StringComparison.OrdinalIgnoreCase))
				{
					exists = true;
					break;
				}
			}

			if (!exists)
				edges.Add(token);

			arm.LatchUntilNeutral = true;
			arm.Armed = false;
		}
	}
}