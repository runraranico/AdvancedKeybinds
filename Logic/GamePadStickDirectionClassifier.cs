using System;
using System.Collections.Generic;

namespace AdvancedKeybinds.Core
{
	public enum GamePadStickDirection : byte
	{
		None = 0,
		Up = 1,
		Down = 2,
		Left = 3,
		Right = 4
	}

	/// <summary>
	/// Runtime digital state for one stick: at most one horizontal and one vertical token.
	/// </summary>
	public struct GamePadStickDigitalAxes : IEquatable<GamePadStickDigitalAxes>
	{
		/// <summary><see cref="GamePadStickDirection.None"/>, <see cref="GamePadStickDirection.Left"/>, or <see cref="GamePadStickDirection.Right"/>.</summary>
		public GamePadStickDirection Horizontal;

		/// <summary><see cref="GamePadStickDirection.None"/>, <see cref="GamePadStickDirection.Up"/>, or <see cref="GamePadStickDirection.Down"/>.</summary>
		public GamePadStickDirection Vertical;

		public static GamePadStickDigitalAxes Empty => default;

		public bool Equals(GamePadStickDigitalAxes other)
			=> Horizontal == other.Horizontal && Vertical == other.Vertical;

		public override bool Equals(object obj)
			=> obj is GamePadStickDigitalAxes other && Equals(other);

		public override int GetHashCode()
			=> ((int)Horizontal << 8) | (int)Vertical;
	}

	/// <summary>
	/// Capture: strong-axis one direction. Runtime: independent X/Y digital axes with per-axis hysteresis.
	/// FNA convention: +Y = stick up.
	/// </summary>
	public static class GamePadStickDirectionClassifier
	{
		/// <summary>
		/// Capture-only strong-axis pick. Runtime must use <see cref="ApplyRuntimeAxes"/>.
		/// </summary>
		public static GamePadStickDirection ClassifyStrongAxis(
			float x,
			float y,
			float pressThreshold,
			float epsilon = GamePadStickDirectionPolicy.AxisTieEpsilon)
		{
			if (pressThreshold < 0f)
				throw new ArgumentOutOfRangeException(nameof(pressThreshold));

			float ax = x < 0f ? -x : x;
			float ay = y < 0f ? -y : y;
			float max = ax > ay ? ax : ay;
			if (max < pressThreshold)
				return GamePadStickDirection.None;

			if (ax > ay + epsilon)
				return x >= 0f ? GamePadStickDirection.Right : GamePadStickDirection.Left;

			if (ay > ax + epsilon)
				return y >= 0f ? GamePadStickDirection.Up : GamePadStickDirection.Down;

			// Tie / near-tie: horizontal priority (Capture only).
			return x >= 0f ? GamePadStickDirection.Right : GamePadStickDirection.Left;
		}

		/// <summary>Backward-compatible alias for Capture strong-axis classification.</summary>
		public static GamePadStickDirection ClassifyAxes(
			float x,
			float y,
			float pressThreshold,
			float epsilon = GamePadStickDirectionPolicy.AxisTieEpsilon)
			=> ClassifyStrongAxis(x, y, pressThreshold, epsilon);

		/// <summary>
		/// Runtime: X and Y independently.
		/// Hold uses <paramref name="releaseThreshold"/>; new polarity needs <paramref name="pressThreshold"/>.
		/// </summary>
		public static GamePadStickDigitalAxes ApplyRuntimeAxes(
			GamePadStickDigitalAxes previous,
			float x,
			float y,
			float pressThreshold = GamePadStickDirectionPolicy.RuntimePressThreshold,
			float releaseThreshold = GamePadStickDirectionPolicy.ReleaseThreshold)
		{
			return new GamePadStickDigitalAxes {
				Horizontal = ApplyHorizontalAxis(previous.Horizontal, x, pressThreshold, releaseThreshold),
				Vertical = ApplyVerticalAxis(previous.Vertical, y, pressThreshold, releaseThreshold)
			};
		}

		/// <summary>
		/// Single signed axis with hysteresis.
		/// Negative polarity / Positive polarity map via <paramref name="negativeDir"/> / <paramref name="positiveDir"/>.
		/// </summary>
		public static GamePadStickDirection ApplySignedAxis(
			GamePadStickDirection previous,
			float axisValue,
			GamePadStickDirection negativeDir,
			GamePadStickDirection positiveDir,
			float pressThreshold,
			float releaseThreshold)
		{
			float abs = axisValue < 0f ? -axisValue : axisValue;
			bool wasNegative = previous == negativeDir;
			bool wasPositive = previous == positiveDir;

			if (wasNegative || wasPositive)
			{
				bool sameSide = (wasNegative && axisValue < 0f) || (wasPositive && axisValue > 0f);
				if (sameSide && abs >= releaseThreshold)
					return previous;

				// Left previous side (or crossed). New side only at press threshold.
				if (abs < pressThreshold)
					return GamePadStickDirection.None;

				return axisValue < 0f ? negativeDir : positiveDir;
			}

			if (abs < pressThreshold)
				return GamePadStickDirection.None;

			return axisValue < 0f ? negativeDir : positiveDir;
		}

		public static GamePadStickDirection ApplyHorizontalAxis(
			GamePadStickDirection previous,
			float x,
			float pressThreshold = GamePadStickDirectionPolicy.RuntimePressThreshold,
			float releaseThreshold = GamePadStickDirectionPolicy.ReleaseThreshold)
			=> ApplySignedAxis(
				previous,
				x,
				GamePadStickDirection.Left,
				GamePadStickDirection.Right,
				pressThreshold,
				releaseThreshold);

		public static GamePadStickDirection ApplyVerticalAxis(
			GamePadStickDirection previous,
			float y,
			float pressThreshold = GamePadStickDirectionPolicy.RuntimePressThreshold,
			float releaseThreshold = GamePadStickDirectionPolicy.ReleaseThreshold)
			=> ApplySignedAxis(
				previous,
				y,
				GamePadStickDirection.Down,
				GamePadStickDirection.Up,
				pressThreshold,
				releaseThreshold);

		public static string ToToken(bool leftStick, GamePadStickDirection direction)
		{
			if (direction == GamePadStickDirection.None)
				return null;

			if (leftStick)
			{
				return direction switch {
					GamePadStickDirection.Up => GamePadToken.LeftStickUp,
					GamePadStickDirection.Down => GamePadToken.LeftStickDown,
					GamePadStickDirection.Left => GamePadToken.LeftStickLeft,
					GamePadStickDirection.Right => GamePadToken.LeftStickRight,
					_ => null
				};
			}

			return direction switch {
				GamePadStickDirection.Up => GamePadToken.RightStickUp,
				GamePadStickDirection.Down => GamePadToken.RightStickDown,
				GamePadStickDirection.Left => GamePadToken.RightStickLeft,
				GamePadStickDirection.Right => GamePadToken.RightStickRight,
				_ => null
			};
		}

		public static void AppendTokens(bool leftStick, GamePadStickDigitalAxes axes, HashSet<string> dest)
		{
			if (dest == null)
				return;

			string h = ToToken(leftStick, axes.Horizontal);
			if (!string.IsNullOrEmpty(h))
				dest.Add(h);

			string v = ToToken(leftStick, axes.Vertical);
			if (!string.IsNullOrEmpty(v))
				dest.Add(v);
		}

		public static bool TryGetDirectionFromToken(string token, out bool leftStick, out GamePadStickDirection direction)
		{
			leftStick = false;
			direction = GamePadStickDirection.None;
			if (string.IsNullOrEmpty(token) || !GamePadToken.TryCanonicalize(token, out string canonical))
				return false;

			switch (canonical)
			{
				case GamePadToken.LeftStickUp: leftStick = true; direction = GamePadStickDirection.Up; return true;
				case GamePadToken.LeftStickDown: leftStick = true; direction = GamePadStickDirection.Down; return true;
				case GamePadToken.LeftStickLeft: leftStick = true; direction = GamePadStickDirection.Left; return true;
				case GamePadToken.LeftStickRight: leftStick = true; direction = GamePadStickDirection.Right; return true;
				case GamePadToken.RightStickUp: leftStick = false; direction = GamePadStickDirection.Up; return true;
				case GamePadToken.RightStickDown: leftStick = false; direction = GamePadStickDirection.Down; return true;
				case GamePadToken.RightStickLeft: leftStick = false; direction = GamePadStickDirection.Left; return true;
				case GamePadToken.RightStickRight: leftStick = false; direction = GamePadStickDirection.Right; return true;
				default: return false;
			}
		}

		public static GamePadStickDigitalAxes ExtractDigitalAxesFromHeld(IEnumerable<string> held, bool leftStick)
		{
			var axes = GamePadStickDigitalAxes.Empty;
			if (held == null)
				return axes;

			foreach (string token in held)
			{
				if (!TryGetDirectionFromToken(token, out bool isLeft, out GamePadStickDirection dir))
					continue;
				if (isLeft != leftStick)
					continue;

				if (dir is GamePadStickDirection.Left or GamePadStickDirection.Right)
					axes.Horizontal = dir;
				else if (dir is GamePadStickDirection.Up or GamePadStickDirection.Down)
					axes.Vertical = dir;
			}

			return axes;
		}
	}
}
