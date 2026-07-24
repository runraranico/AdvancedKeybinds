using System;
using System.Diagnostics;

namespace AdvancedKeybinds.Diagnostics
{
	public sealed class DiagnosticKeyMessageEventArgs
	{
		public string MessageName { get; init; }
		public uint VirtualKey { get; init; }
		public ushort ScanCode { get; init; }
		public bool Extended { get; init; }
		public bool IsKeyDown { get; init; }
		public bool IsRepeat { get; init; }
		public long TimestampTicks { get; init; } = DateTime.UtcNow.Ticks;
		public long MonotonicTimestamp { get; init; } = Stopwatch.GetTimestamp();
		public string DeviceIdentifier { get; init; } = "UNAVAILABLE";
		public bool Injected { get; init; }
	}

	public sealed class DiagnosticRawInputEventArgs
	{
		public uint VirtualKey { get; init; }
		public ushort MakeCode { get; init; }
		public bool Extended { get; init; }
		public bool E0 { get; init; }
		public bool E1 { get; init; }
		public bool IsKeyUp { get; init; }
		public long TimestampTicks { get; init; } = DateTime.UtcNow.Ticks;
		public long MonotonicTimestamp { get; init; } = Stopwatch.GetTimestamp();
		public string DeviceIdentifier { get; init; } = "UNAVAILABLE";
		public bool Injected { get; init; }
	}

	public sealed class DiagnosticImeStateEventArgs
	{
		public DiagnosticImeState State { get; init; }
		public string Source { get; init; }
		public long TimestampTicks { get; init; } = DateTime.UtcNow.Ticks;
	}

	public sealed class DiagnosticCaptureEvaluationEventArgs
	{
		public DiagnosticRegistrationResult Result { get; init; }
		public DiagnosticKeyInput Input { get; init; }
		public long TimestampTicks { get; init; } = DateTime.UtcNow.Ticks;
	}
}
