using System;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Opposite stick-direction pairs cannot be newly registered together
	/// (Up+Down / Left+Right on the same stick). Diagonal (vertical+horizontal) is allowed.
	/// </summary>
	public static class GamePadStickOppositeDirectionRules
	{
		public static bool AreOppositeDirections(string tokenA, string tokenB)
		{
			if (!GamePadStickDirectionClassifier.TryGetDirectionFromToken(tokenA, out bool leftA, out GamePadStickDirection dirA))
				return false;
			if (!GamePadStickDirectionClassifier.TryGetDirectionFromToken(tokenB, out bool leftB, out GamePadStickDirection dirB))
				return false;
			if (leftA != leftB)
				return false;

			return IsOppositePair(dirA, dirB);
		}

		public static bool IsOppositePair(GamePadStickDirection a, GamePadStickDirection b)
		{
			if (a == GamePadStickDirection.None || b == GamePadStickDirection.None)
				return false;

			return (a == GamePadStickDirection.Up && b == GamePadStickDirection.Down)
				|| (a == GamePadStickDirection.Down && b == GamePadStickDirection.Up)
				|| (a == GamePadStickDirection.Left && b == GamePadStickDirection.Right)
				|| (a == GamePadStickDirection.Right && b == GamePadStickDirection.Left);
		}

		/// <summary>
		/// True when placing <paramref name="incomingCanonical"/> into <paramref name="targetSlot"/>
		/// would conflict with another filled slot (target slot itself is excluded).
		/// </summary>
		public static bool ConflictsWithOtherSlots(
			string slot1,
			string slot2,
			string slot3,
			int targetSlot,
			string incomingCanonical)
		{
			if (string.IsNullOrEmpty(incomingCanonical))
				return false;
			if (!GamePadToken.TryCanonicalize(incomingCanonical, out string incoming))
				return false;
			if (!GamePadToken.IsStickDirectionToken(incoming))
				return false;

			for (int i = 1; i <= 3; i++)
			{
				if (i == targetSlot)
					continue;

				string existing = i switch {
					1 => slot1,
					2 => slot2,
					_ => slot3
				};
				if (string.IsNullOrEmpty(existing))
					continue;

				if (AreOppositeDirections(existing, incoming))
					return true;
			}

			return false;
		}

		/// <summary>
		/// True when any two filled slots form an opposite stick-direction pair.
		/// Load paths must not auto-repair; callers use this for Save/Commit reject only.
		/// </summary>
		public static bool HasOppositeConflict(string slot1, string slot2, string slot3)
		{
			string[] slots = { slot1, slot2, slot3 };
			for (int i = 0; i < slots.Length; i++)
			{
				string a = slots[i];
				if (string.IsNullOrEmpty(a) || !GamePadToken.IsStickDirectionToken(a))
					continue;

				for (int j = i + 1; j < slots.Length; j++)
				{
					string b = slots[j];
					if (string.IsNullOrEmpty(b))
						continue;

					if (AreOppositeDirections(a, b))
						return true;
				}
			}

			return false;
		}
	}
}
