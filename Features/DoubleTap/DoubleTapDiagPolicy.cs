using System;

namespace AdvancedKeybinds.Features.DoubleTap
{
	/// <summary>
	/// Pure DT diag classify + passive Publish gates. No Native/Engine/OS/I/O.
	/// Does not change Apply/Queue/return. Future Native may pass source/extended/timestamp/update.
	/// Classify order matches DoubleTapObserveSourcePolicy.TryClassifyLowLevelPhysical for Win/Caps,
	/// then NonConvert, then Kana (scan 0x3A already claimed by Caps).
	/// </summary>
	public static class DoubleTapDiagPolicy
	{
		const uint VkNonConvert = 0x1D;
		const uint VkKana = 0x15;
		const uint VkOemCopy = 0xF2;
		const uint VkCapital = 0x14;
		const uint VkLWin = 0x5B;
		const uint VkRWin = 0x5C;

		const ushort ScNonConvert = 0x7B;
		const ushort ScHiragana = 0x70;
		const ushort ScCapital = 0x3A;
		const ushort ScLWin = 0x5B;
		const ushort ScRWin = 0x5C;

		const int KeyIdBitMask = 0x1FFFF;

		public static DoubleTapDiagKey Classify(uint vk, ushort scan)
		{
			ushort sc = (ushort)(scan & 0xFF);

			// Win VK → Win scan → Caps VK/scan (same order as TryClassifyLowLevelPhysical).
			if (vk == VkLWin)
				return DoubleTapDiagKey.LWin;

			if (vk == VkRWin)
				return DoubleTapDiagKey.RWin;

			if (sc == ScLWin)
				return DoubleTapDiagKey.LWin;

			if (sc == ScRWin)
				return DoubleTapDiagKey.RWin;

			if (vk == VkCapital || sc == ScCapital)
				return DoubleTapDiagKey.CapsLock;

			if (vk == VkNonConvert || sc == ScNonConvert)
				return DoubleTapDiagKey.NonConvert;

			if (vk == VkKana || vk == VkOemCopy || sc == ScHiragana)
				return DoubleTapDiagKey.Kana;

			return DoubleTapDiagKey.None;
		}

		/// <summary>
		/// Anonymous LL callback pulse. Key.None / Vk0 / Scan0 / Source.LowLevel / Stage.LowLevelPulse.
		/// Gates: no subscribers, blockedOrChat, or non-foreground → false. Does not reuse SourceState.
		/// </summary>
		internal static bool TryPublishLowLevelPulse(
			long timestampMs,
			ulong update,
			bool sessionActive,
			bool blockedOrChat,
			bool foreground)
		{
			if (blockedOrChat)
				return false;

			if (!foreground)
				return false;

			if (!DoubleTapDiagFeed.HasSubscribers)
				return false;

			DoubleTapDiagFeed.Publish(new DoubleTapDiagEvent(
				DoubleTapDiagKey.None,
				DoubleTapDiagStage.LowLevelPulse,
				DoubleTapDiagReason.None,
				DoubleTapDiagSource.LowLevel,
				timestampMs,
				update,
				vk: 0u,
				scan: 0,
				extended: false,
				sessionActive,
				sourceAvailable: true,
				seedHeld: false,
				actionId: string.Empty,
				isDown: true));

			return true;
		}

		/// <summary>
		/// Passive LL raw candidate for DT target keys only. Preserves raw vk/scan/extended/isDown.
		/// Gates: no subscribers, ownMarker, blockedOrChat, non-foreground, or Classify None → false.
		/// Does not reuse SourceState. No Native/Engine side effects.
		/// </summary>
		internal static bool TryPublishLowLevelRawCandidate(
			uint vk,
			ushort scan,
			bool extended,
			bool isDown,
			long timestampMs,
			ulong update,
			bool sessionActive,
			bool ownMarker,
			bool blockedOrChat,
			bool foreground)
		{
			if (!DoubleTapDiagFeed.HasSubscribers)
				return false;

			if (ownMarker)
				return false;

			if (blockedOrChat)
				return false;

			if (!foreground)
				return false;

			DoubleTapDiagKey key = Classify(vk, scan);
			if (key == DoubleTapDiagKey.None)
				return false;

			DoubleTapDiagReason reason = sessionActive
				? DoubleTapDiagReason.None
				: DoubleTapDiagReason.Inactive;

			DoubleTapDiagFeed.Publish(new DoubleTapDiagEvent(
				key,
				DoubleTapDiagStage.LowLevelRawCandidate,
				reason,
				DoubleTapDiagSource.LowLevel,
				timestampMs,
				update,
				vk,
				scan,
				extended,
				sessionActive,
				sourceAvailable: true,
				seedHeld: false,
				actionId: string.Empty,
				isDown));

			return true;
		}

		/// <summary>
		/// Passive target-key Arrived publish. Short-circuits on allow/blocked/foreground/subscribers/None.
		/// Inactive session still publishes Arrived with <see cref="DoubleTapDiagReason.Inactive"/>.
		/// Internal so external mods cannot inject fake events (Tests link this source).
		/// </summary>
		internal static bool TryPublishTargetArrival(
			uint vk,
			ushort scan,
			DoubleTapDiagSource source,
			bool extended,
			long timestampMs,
			ulong update,
			bool sessionActive,
			bool sourceAvailable,
			bool seedHeld,
			bool blockedOrChat,
			bool foreground,
			bool observationAllowed)
		{
			if (!observationAllowed)
				return false;

			if (blockedOrChat)
				return false;

			if (!foreground)
				return false;

			if (!sourceAvailable)
				return false;

			if (!DoubleTapDiagFeed.HasSubscribers)
				return false;

			DoubleTapDiagKey key = Classify(vk, scan);
			if (key == DoubleTapDiagKey.None)
				return false;

			DoubleTapDiagReason reason = sessionActive
				? DoubleTapDiagReason.None
				: DoubleTapDiagReason.Inactive;

			DoubleTapDiagFeed.Publish(new DoubleTapDiagEvent(
				key,
				DoubleTapDiagStage.Arrived,
				reason,
				source,
				timestampMs,
				update,
				vk,
				scan,
				extended,
				sessionActive,
				sourceAvailable,
				seedHeld,
				string.Empty,
				isDown: true));

			return true;
		}

		/// <summary>
		/// Window message ingress for fixed DT candidates only. No OS/input mutation.
		/// Gates: no subscribers, blockedOrText, non-foreground, or Classify None → false.
		/// Reason: Repeat when down+wasKeyDown; else Inactive when session inactive; else None.
		/// </summary>
		internal static bool TryPublishWindowIngress(
			uint vk,
			ushort scan,
			bool extended,
			bool isDown,
			bool wasKeyDown,
			long timestampMs,
			ulong update,
			bool sessionActive,
			bool blockedOrText,
			bool foreground)
		{
			if (blockedOrText)
				return false;

			if (!foreground)
				return false;

			if (!DoubleTapDiagFeed.HasSubscribers)
				return false;

			DoubleTapDiagKey key = Classify(vk, scan);
			if (key == DoubleTapDiagKey.None)
				return false;

			DoubleTapDiagReason reason;
			if (isDown && wasKeyDown)
				reason = DoubleTapDiagReason.Repeat;
			else if (!sessionActive)
				reason = DoubleTapDiagReason.Inactive;
			else
				reason = DoubleTapDiagReason.None;

			DoubleTapDiagFeed.Publish(new DoubleTapDiagEvent(
				key,
				DoubleTapDiagStage.WindowIngress,
				reason,
				DoubleTapDiagSource.Window,
				timestampMs,
				update,
				vk,
				scan,
				extended,
				sessionActive,
				sourceAvailable: true,
				seedHeld: false,
				actionId: string.Empty,
				isDown: isDown));

			return true;
		}

		/// <summary>
		/// After Session.Observe: accepted → Queued; false → Rejected + rejectReason.
		/// Default SessionRejected keeps existing callers opaque.
		/// Target keys only. Caller supplies real isDown (LL up → false).
		/// HasSubscribers short-circuit before Classify.
		/// </summary>
		internal static bool TryPublishSessionObserveOutcome(
			uint vk,
			ushort scan,
			DoubleTapDiagSource source,
			bool extended,
			long timestampMs,
			ulong update,
			bool sessionActive,
			bool sourceAvailable,
			bool seedHeld,
			bool accepted,
			bool isDown,
			DoubleTapDiagReason rejectReason = DoubleTapDiagReason.SessionRejected)
		{
			if (!DoubleTapDiagFeed.HasSubscribers)
				return false;

			DoubleTapDiagKey key = Classify(vk, scan);
			if (key == DoubleTapDiagKey.None)
				return false;

			DoubleTapDiagFeed.Publish(new DoubleTapDiagEvent(
				key,
				accepted ? DoubleTapDiagStage.Queued : DoubleTapDiagStage.Rejected,
				accepted ? DoubleTapDiagReason.None : rejectReason,
				source,
				timestampMs,
				update,
				vk,
				scan,
				extended,
				sessionActive,
				sourceAvailable,
				seedHeld,
				string.Empty,
				isDown));

			return true;
		}

		/// <summary>
		/// Diag-only Window rejection classification. This never changes observation or input.
		/// A single representative reason is chosen when multiple conditions are true.
		/// </summary>
		internal static DoubleTapDiagReason ClassifyWindowObserveRejectReason(
			bool wasKeyDown,
			bool ownInjected,
			bool sentMessage,
			bool blocked,
			bool validTimestamp)
		{
			if (!validTimestamp)
				return DoubleTapDiagReason.BadTimestamp;
			if (blocked)
				return DoubleTapDiagReason.Blocked;
			if (wasKeyDown)
				return DoubleTapDiagReason.Repeat;
			if (ownInjected)
				return DoubleTapDiagReason.OwnInjected;
			if (sentMessage)
				return DoubleTapDiagReason.SentMessage;
			return DoubleTapDiagReason.SessionRejected;
		}

		/// <summary>
		/// Engine Matched. Caller supplies real source / sourceAvailable / seedHeld / isDown.
		/// HasSubscribers return before KeyId unpack.
		/// </summary>
		internal static bool TryPublishMatched(
			int keyId,
			string actionId,
			DoubleTapDiagSource source,
			double timestampMs,
			ulong update,
			bool sessionActive,
			bool sourceAvailable,
			bool seedHeld,
			bool isDown)
		{
			return TryPublishEngineStage(
				DoubleTapDiagStage.Matched,
				keyId,
				actionId,
				source,
				timestampMs,
				update,
				sessionActive,
				sourceAvailable,
				seedHeld,
				isDown);
		}

		/// <summary>
		/// Engine Fired. Same caller-supplied metadata contract as Matched.
		/// HasSubscribers return before KeyId unpack.
		/// </summary>
		internal static bool TryPublishFired(
			int keyId,
			string actionId,
			DoubleTapDiagSource source,
			double timestampMs,
			ulong update,
			bool sessionActive,
			bool sourceAvailable,
			bool seedHeld,
			bool isDown,
			int? evaluationFrame = null)
		{
			return TryPublishEngineStage(
				DoubleTapDiagStage.Fired,
				keyId,
				actionId,
				source,
				timestampMs,
				update,
				sessionActive,
				sourceAvailable,
				seedHeld,
				isDown,
				evaluationFrame);
		}

		static bool TryPublishEngineStage(
			DoubleTapDiagStage stage,
			int keyId,
			string actionId,
			DoubleTapDiagSource source,
			double timestampMs,
			ulong update,
			bool sessionActive,
			bool sourceAvailable,
			bool seedHeld,
			bool isDown,
			int? evaluationFrame = null)
		{
			if (!DoubleTapDiagFeed.HasSubscribers)
				return false;

			if (string.IsNullOrEmpty(actionId))
				return false;

			if (!TryUnpackKeyId(keyId, out uint vk, out ushort scan, out bool extended))
				return false;

			DoubleTapDiagKey key = Classify(vk, scan);
			if (key == DoubleTapDiagKey.None)
				return false;

			DoubleTapDiagFeed.Publish(new DoubleTapDiagEvent(
				key,
				stage,
				DoubleTapDiagReason.None,
				source,
				ToDiagTimestampMs(timestampMs),
				update,
				vk,
				scan,
				extended,
				sessionActive,
				sourceAvailable,
				seedHeld,
				actionId,
				isDown,
				evaluationFrame));

			return true;
		}

		static bool TryUnpackKeyId(int keyId, out uint vk, out ushort scan, out bool extended)
		{
			vk = 0;
			scan = 0;
			extended = false;
			if (keyId < 0)
				return false;
			if ((keyId & ~KeyIdBitMask) != 0)
				return false;

			vk = (uint)(keyId & 0xFF);
			scan = (ushort)((keyId >> 8) & 0xFF);
			extended = (keyId & (1 << 16)) != 0;
			if (vk < 1u || vk > 0xFEu)
				return false;

			return true;
		}

		static long ToDiagTimestampMs(double eventMs)
		{
			if (double.IsNaN(eventMs) || double.IsInfinity(eventMs) || eventMs < 0.0)
				return -1L;
			return (long)eventMs;
		}
	}
}
