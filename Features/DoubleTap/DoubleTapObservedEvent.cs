namespace AdvancedKeybinds.Features.DoubleTap
{
	/// <summary>
	/// One DT observation slot. Pure data. No OS / game API / I/O.
	/// </summary>
	public struct DoubleTapObservedEvent
	{
		public long Generation;
		public long Sequence;
		public ulong Update;
		public double TimestampMs;
		public bool Down;
		public int KeyId;
	}
}
