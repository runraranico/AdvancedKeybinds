namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Pure decision policy for selecting the AlchemistNPCLite ShopChangeUI instance that is actually
	/// connected to the official AlchemistNPCLiteWorld UserInterface.
	/// </summary>
	public static class AlchemistNpcLiteDisplayedUiSelectionPolicy
	{
		/// <summary>
		/// Returns the unique candidate whose reference equals a UserInterface.CurrentState, or -1 when
		/// there is no safe, unambiguous displayed candidate.
		/// </summary>
		public static int SelectUniqueCurrentStateCandidate(bool[] candidateMatchesCurrentState)
		{
			if (candidateMatchesCurrentState == null || candidateMatchesCurrentState.Length == 0)
				return -1;

			int selected = -1;
			for (int i = 0; i < candidateMatchesCurrentState.Length; i++)
			{
				if (!candidateMatchesCurrentState[i])
					continue;
				if (selected >= 0)
					return -1;
				selected = i;
			}

			return selected;
		}

		public static bool ShouldResolveOnOpen(bool visibleNow, bool visiblePreviously)
			=> visibleNow && !visiblePreviously;

		public static bool ShouldDiscardBoundReference(bool worldInvalidated, bool currentStateStillMatches)
			=> worldInvalidated || !currentStateStillMatches;
	}
}
