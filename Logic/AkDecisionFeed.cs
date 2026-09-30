using System;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Decision kind for the minimal AK notification mouthpiece (Task 1 contract).
	/// </summary>
	public enum AkDecisionKind
	{
		CaptureAccepted = 0,
		CaptureRejected = 1,
		BindingAccepted = 2,
		BindingRejected = 3,
		CommitAccepted = 4,
		CommitRejected = 5,
		AkFireDecision = 6,
		TriggerAfterApply = 7,
		CommitUnverified = 8
	}

	/// <summary>
	/// Fixed rejection / outcome reasons. DuplicateToken is required by current contract tests;
	/// remaining members are major reject candidates for later call sites.
	/// </summary>
	public enum AkDecisionReason
	{
		None = 0,
		DuplicateToken = 1,
		EmptyToken = 2,
		InvalidSlot = 3,
		UnknownToken = 4,
		UnsupportedBinding = 5,
		OppositeStick = 6,
		DebugHotkeyBlocked = 7,
		AmbiguousCapture = 8,
		EmptyResolution = 9,
		UnresolvedCapture = 10,
		AdditionalOrRejected = 11,
		TerrariaOrRejected = 12,
		GamePadAlternateRejected = 13,
		CommitRejected = 14,
		DisplayAlias = 15,
		PadTokenNotAllowed = 16,
		NotPadToken = 17,
		MissingWorkingBinding = 18,
		PersistReportedOk = 19,
		PersistReportedFail = 20,
		VanillaOrWriteAttempted = 21
	}

	/// <summary>Immutable decision payload delivered to subscribers.</summary>
	public readonly struct AkDecisionEvent
	{
		public AkDecisionKind Kind { get; }
		public AkDecisionReason Reason { get; }
		public string ActionId { get; }
		public int Slot { get; }
		public string Token { get; }
		public int Frame { get; }

		public AkDecisionEvent(
			AkDecisionKind kind,
			AkDecisionReason reason,
			string actionId,
			int slot,
			string token,
			int frame,
			bool? triggerJustPressed = null)
		{
			Kind = kind;
			Reason = reason;
			ActionId = actionId;
			Slot = slot;
			Token = token;
			Frame = frame;
			TriggerJustPressed = triggerJustPressed;
		}

		public bool? TriggerJustPressed { get; }
	}

	/// <summary>
	/// Event-driven, read-only notification mouthpiece.
	/// No logging, file I/O, timers, per-frame scanning, or input mutation.
	/// </summary>
	public static class AkDecisionFeed
	{
		public static event Action<AkDecisionEvent> Decided;

		internal static bool HasSubscribers => Decided != null;

		internal static void Publish(AkDecisionEvent decision)
		{
			Action<AkDecisionEvent> handlers = Decided;
			if (handlers == null)
				return;

			Delegate[] list = handlers.GetInvocationList();
			for (int i = 0; i < list.Length; i++)
			{
				var handler = (Action<AkDecisionEvent>)list[i];
				try
				{
					handler(decision);
				}
				catch (Exception)
				{
					Decided -= handler;
				}
			}
		}
	}
}
