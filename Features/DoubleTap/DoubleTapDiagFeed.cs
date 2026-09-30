using System;

#nullable enable

namespace AdvancedKeybinds.Features.DoubleTap
{
	/// <summary>Fixed DT diag keys. <see cref="None"/> is keyless source/session state only.</summary>
	public enum DoubleTapDiagKey : byte
	{
		None = 0,
		NonConvert = 1,
		Kana = 2,
		CapsLock = 3,
		LWin = 4,
		RWin = 5,
	}

	public enum DoubleTapDiagStage : byte
	{
		SourceState = 0,
		SessionBegin = 1,
		Arrived = 2,
		Rejected = 3,
		Queued = 4,
		Matched = 5,
		Fired = 6,
		SessionEnd = 7,
		LowLevelPulse = 8,
		LowLevelRawCandidate = 9,
		WindowIngress = 10,
	}

	public enum DoubleTapDiagReason : byte
	{
		None = 0,
		Inactive = 1,
		Repeat = 2,
		OwnInjected = 3,
		SentMessage = 4,
		BadTimestamp = 5,
		Blocked = 6,
		SourceUnavailable = 7,
		SessionRejected = 8,
		PrimaryMismatch = 9,
		PairNotCompleted = 10,
		GenerationChanged = 11,
		QueueOverflow = 12,
	}

	public enum DoubleTapDiagSource : byte
	{
		Unknown = 0,
		Window = 1,
		LowLevel = 2,
		Engine = 3,
	}

	/// <summary>
	/// Pure DT diagnostic payload. No free-form key-string streams.
	/// ActionId is a registered binding id only (empty when not applicable).
	/// </summary>
	public readonly struct DoubleTapDiagEvent
	{
		public DoubleTapDiagEvent(
			DoubleTapDiagKey key,
			DoubleTapDiagStage stage,
			DoubleTapDiagReason reason,
			DoubleTapDiagSource source,
			long timestampMs,
			ulong update,
			uint vk,
			ushort scan,
			bool extended,
			bool sessionActive,
			bool sourceAvailable,
			bool seedHeld,
			string actionId,
			bool isDown = true,
			int? evaluationFrame = null)
		{
			Key = key;
			Stage = stage;
			Reason = reason;
			Source = source;
			TimestampMs = timestampMs;
			Update = update;
			Vk = vk;
			Scan = scan;
			Extended = extended;
			SessionActive = sessionActive;
			SourceAvailable = sourceAvailable;
			SeedHeld = seedHeld;
			ActionId = actionId ?? string.Empty;
			IsDown = isDown;
			EvaluationFrame = evaluationFrame;
		}

		public DoubleTapDiagKey Key { get; }
		public DoubleTapDiagStage Stage { get; }
		public DoubleTapDiagReason Reason { get; }
		public DoubleTapDiagSource Source { get; }
		public long TimestampMs { get; }
		public ulong Update { get; }
		public uint Vk { get; }
		public ushort Scan { get; }
		public bool Extended { get; }
		public bool SessionActive { get; }
		public bool SourceAvailable { get; }
		public bool SeedHeld { get; }
		public string ActionId { get; }
		/// <summary>Physical down=true / up=false. Existing Arrived path uses true.</summary>
		public bool IsDown { get; }
		/// <summary>
		/// Fired-only AK evaluation frame (same conversion as AkDecisionSnapshotPublisher).
		/// Null for non-Fired stages or when unavailable. Does not overwrite <see cref="Update"/>.
		/// </summary>
		public int? EvaluationFrame { get; }

		/// <summary>
		/// Target-key Arrived only. <see cref="DoubleTapDiagKey.None"/> is rejected (never coerced to Arrived).
		/// </summary>
		public static DoubleTapDiagEvent Arrived(
			DoubleTapDiagKey key,
			DoubleTapDiagSource source = DoubleTapDiagSource.Unknown,
			long timestampMs = 0L,
			ulong update = 0UL,
			uint vk = 0u,
			ushort scan = 0,
			bool extended = false,
			bool sessionActive = true,
			bool sourceAvailable = true,
			bool seedHeld = false)
		{
			if (key == DoubleTapDiagKey.None)
				throw new ArgumentOutOfRangeException(nameof(key), "None cannot be Arrived.");

			return new DoubleTapDiagEvent(
				key,
				DoubleTapDiagStage.Arrived,
				DoubleTapDiagReason.None,
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
				isDown: true);
		}
	}

	/// <summary>
	/// DT-only diagnostic mouth. Publish is internal (product DT callers / linked tests).
	/// External mods may subscribe to <see cref="Observed"/> but cannot inject via Publish.
	/// Zero subscribers → no-op. Handler exceptions are isolated and that handler is removed.
	/// </summary>
	public static class DoubleTapDiagFeed
	{
		public static event Action<DoubleTapDiagEvent>? Observed;

		public static bool HasSubscribers => Observed != null;

		internal static void Publish(DoubleTapDiagEvent diagnostic)
		{
			Action<DoubleTapDiagEvent>? handlers = Observed;
			if (handlers == null)
				return;

			Delegate[] list = handlers.GetInvocationList();
			for (int i = 0; i < list.Length; i++)
			{
				var handler = (Action<DoubleTapDiagEvent>)list[i];
				try
				{
					handler(diagnostic);
				}
				catch (Exception)
				{
					Observed -= handler;
				}
			}
		}
	}
}
