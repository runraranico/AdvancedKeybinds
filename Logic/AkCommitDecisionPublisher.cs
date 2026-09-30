namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Pure Commit notification mapping. Claims only API-reported persist bool or vanilla-OR attempt;
	/// never claims disk/memory identity beyond that.
	/// </summary>
	internal static class AkCommitDecisionPublisher
	{
		public static void PublishPersistReported(string actionId, bool ok, int frame)
		{
			if (!AkDecisionFeed.HasSubscribers)
				return;

			AkDecisionFeed.Publish(new AkDecisionEvent(
				ok ? AkDecisionKind.CommitAccepted : AkDecisionKind.CommitRejected,
				ok ? AkDecisionReason.PersistReportedOk : AkDecisionReason.PersistReportedFail,
				actionId,
				-1,
				null,
				frame));
		}

		public static void PublishVanillaOrWriteAttempted(string actionId, int frame)
		{
			if (!AkDecisionFeed.HasSubscribers)
				return;

			AkDecisionFeed.Publish(new AkDecisionEvent(
				AkDecisionKind.CommitUnverified,
				AkDecisionReason.VanillaOrWriteAttempted,
				actionId,
				-1,
				null,
				frame));
		}

		public static void PublishPolicyRejected(string actionId, AkDecisionReason reason, int frame)
		{
			if (!AkDecisionFeed.HasSubscribers)
				return;

			AkDecisionFeed.Publish(new AkDecisionEvent(
				AkDecisionKind.CommitRejected,
				reason,
				actionId,
				-1,
				null,
				frame));
		}

		public static void PublishMissingWorkingBinding(string actionId, int frame)
		{
			if (!AkDecisionFeed.HasSubscribers)
				return;

			AkDecisionFeed.Publish(new AkDecisionEvent(
				AkDecisionKind.CommitRejected,
				AkDecisionReason.MissingWorkingBinding,
				actionId,
				-1,
				null,
				frame));
		}
	}
}
