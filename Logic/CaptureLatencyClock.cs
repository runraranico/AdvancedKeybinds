using System;
using System.Text;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Frame + millisecond capture latency markers (T0–T8). Injectable for tests; optional live use under AKDIAG.
	/// </summary>
	public sealed class CaptureLatencyClock
	{
		public int Frame { get; private set; }
		public long NowMs { get; private set; }

		public long? T0Ms { get; private set; }
		public long? T1Ms { get; private set; }
		public long? T2Ms { get; private set; }
		public long? T3Ms { get; private set; }
		public long? T4Ms { get; private set; }
		public long? T5Ms { get; private set; }
		public long? T6Ms { get; private set; }
		public long? T7Ms { get; private set; }
		public long? T8Ms { get; private set; }

		public int? F0 { get; private set; }
		public int? F1 { get; private set; }
		public int? F2 { get; private set; }
		public int? F3 { get; private set; }
		public int? F4 { get; private set; }
		public int? F5 { get; private set; }
		public int? F6 { get; private set; }
		public int? F7 { get; private set; }
		public int? F8 { get; private set; }

		public string AcceptedToken { get; private set; }

		public void Reset(int startFrame = 0, long startMs = 0)
		{
			Frame = startFrame;
			NowMs = startMs;
			T0Ms = T1Ms = T2Ms = T3Ms = T4Ms = T5Ms = T6Ms = T7Ms = T8Ms = null;
			F0 = F1 = F2 = F3 = F4 = F5 = F6 = F7 = F8 = null;
			AcceptedToken = null;
		}

		public void AdvanceFrame(long msDelta = 16)
		{
			Frame++;
			NowMs += msDelta;
		}

		public void MarkT0_Click() => Mark(0);
		public void MarkT1_WaitingForRelease() => Mark(1);
		public void MarkT2_AllReleased() => Mark(2);
		public void MarkT3_WaitingForNewInput() => Mark(3);
		public void MarkT4_NewDownDetected() => Mark(4);
		public void MarkT5_Accepted(string token)
		{
			AcceptedToken = token;
			Mark(5);
		}

		public void MarkT6_InternalValueUpdated() => Mark(6);
		public void MarkT7_UiUpdated() => Mark(7);
		public void MarkT8_SaveCompleted() => Mark(8);

		public int? DeltaFrames(int fromMarker, int toMarker)
		{
			int? a = FrameOf(fromMarker);
			int? b = FrameOf(toMarker);
			if (a == null || b == null)
				return null;
			return b.Value - a.Value;
		}

		public long? DeltaMs(int fromMarker, int toMarker)
		{
			long? a = MsOf(fromMarker);
			long? b = MsOf(toMarker);
			if (a == null || b == null)
				return null;
			return b.Value - a.Value;
		}

		/// <summary>Down(T4) → Accepted(T5) frames. Target: 0 (same frame).</summary>
		public int? DownToAcceptedFrames => DeltaFrames(4, 5);

		/// <summary>Down(T4) → UI(T7) frames. Target: 0 or 1.</summary>
		public int? DownToUiFrames => DeltaFrames(4, 7);

		public string FormatSummary()
		{
			var sb = new StringBuilder(256);
			sb.Append("frames F0=").Append(F0).Append(" F1=").Append(F1)
				.Append(" F2=").Append(F2).Append(" F3=").Append(F3)
				.Append(" F4=").Append(F4).Append(" F5=").Append(F5)
				.Append(" F6=").Append(F6).Append(" F7=").Append(F7)
				.Append(" F8=").Append(F8);
			sb.Append(" ms T0=").Append(T0Ms).Append(" T1=").Append(T1Ms)
				.Append(" T2=").Append(T2Ms).Append(" T3=").Append(T3Ms)
				.Append(" T4=").Append(T4Ms).Append(" T5=").Append(T5Ms)
				.Append(" T6=").Append(T6Ms).Append(" T7=").Append(T7Ms)
				.Append(" T8=").Append(T8Ms);
			sb.Append(" dF T2-T0=").Append(DeltaFrames(0, 2))
				.Append(" T3-T2=").Append(DeltaFrames(2, 3))
				.Append(" T4-T3=").Append(DeltaFrames(3, 4))
				.Append(" T5-T4=").Append(DeltaFrames(4, 5))
				.Append(" T6-T5=").Append(DeltaFrames(5, 6))
				.Append(" T7-T6=").Append(DeltaFrames(6, 7))
				.Append(" T8-T7=").Append(DeltaFrames(7, 8))
				.Append(" T7-T4=").Append(DeltaFrames(4, 7));
			if (!string.IsNullOrEmpty(AcceptedToken))
				sb.Append(" token=").Append(AcceptedToken);
			return sb.ToString();
		}

		private void Mark(int marker)
		{
			switch (marker)
			{
				case 0: T0Ms = NowMs; F0 = Frame; break;
				case 1: T1Ms = NowMs; F1 = Frame; break;
				case 2: T2Ms = NowMs; F2 = Frame; break;
				case 3: T3Ms = NowMs; F3 = Frame; break;
				case 4: T4Ms = NowMs; F4 = Frame; break;
				case 5: T5Ms = NowMs; F5 = Frame; break;
				case 6: T6Ms = NowMs; F6 = Frame; break;
				case 7: T7Ms = NowMs; F7 = Frame; break;
				case 8: T8Ms = NowMs; F8 = Frame; break;
			}
		}

		private int? FrameOf(int marker) => marker switch {
			0 => F0, 1 => F1, 2 => F2, 3 => F3, 4 => F4,
			5 => F5, 6 => F6, 7 => F7, 8 => F8, _ => null
		};

		private long? MsOf(int marker) => marker switch {
			0 => T0Ms, 1 => T1Ms, 2 => T2Ms, 3 => T3Ms, 4 => T4Ms,
			5 => T5Ms, 6 => T6Ms, 7 => T7Ms, 8 => T8Ms, _ => null
		};
	}
}
