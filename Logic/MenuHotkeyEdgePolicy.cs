namespace AdvancedKeybinds.Logic
{
	/// <summary>
	/// Remembers one consumed evaluation generation for the three-menu batch.
	/// Same generation is refused; a different generation is accepted and stored.
	/// Generation 0 is valid on the first consume. There is no reset API.
	/// </summary>
	public sealed class MenuHotkeyEdgePolicy
	{
		private bool _hasConsumedEvaluationGeneration;
		private ulong _consumedEvaluationGeneration;

		/// <summary>
		/// Allows each evaluation generation once. Callers pass the engine read-only generation.
		/// </summary>
		public bool TryConsumeGeneration(ulong generation)
		{
			if (_hasConsumedEvaluationGeneration && generation == _consumedEvaluationGeneration)
				return false;

			_hasConsumedEvaluationGeneration = true;
			_consumedEvaluationGeneration = generation;
			return true;
		}
	}
}
