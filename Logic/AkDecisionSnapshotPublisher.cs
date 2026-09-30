using System.Collections.Generic;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Pure-logic delayed decision batch (A+B+C): snapshot ids+final bools and claim
	/// Fire/Final on the gate before any subscriber callback. Reentrant Publish is ignored.
	/// </summary>
	internal static class AkDecisionSnapshotPublisher
	{
		private static bool _dispatching;

		public static void Publish(
			IDictionary<string, bool> akPressed,
			IDictionary<string, bool> finalPressed,
			ulong frame,
			AkDecisionFrameGate gate)
		{
			if (!AkDecisionFeed.HasSubscribers || _dispatching)
				return;
			if (akPressed == null || finalPressed == null || gate == null)
				return;

			var snapshot = new List<(string ActionId, bool Final)>();
			foreach (KeyValuePair<string, bool> pair in akPressed)
			{
				if (!pair.Value)
					continue;

				bool finalJustPressed = finalPressed.TryGetValue(pair.Key, out bool status) && status;
				snapshot.Add((pair.Key, finalJustPressed));
			}

			if (snapshot.Count == 0)
				return;

			if (!gate.TryBeginFire(frame))
				return;
			if (!gate.TryBeginFinal(frame))
				return;

			int eventFrame = frame == 0UL ? -1 : unchecked((int)frame);

			_dispatching = true;
			try
			{
				for (int i = 0; i < snapshot.Count; i++)
				{
					AkDecisionFeed.Publish(new AkDecisionEvent(
						AkDecisionKind.AkFireDecision,
						AkDecisionReason.None,
						snapshot[i].ActionId,
						-1,
						null,
						eventFrame));
				}

				for (int i = 0; i < snapshot.Count; i++)
				{
					AkDecisionFeed.Publish(new AkDecisionEvent(
						AkDecisionKind.TriggerAfterApply,
						AkDecisionReason.None,
						snapshot[i].ActionId,
						-1,
						null,
						eventFrame,
						snapshot[i].Final));
				}
			}
			finally
			{
				_dispatching = false;
			}
		}
	}
}
