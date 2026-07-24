using System;
using System.Collections.Generic;
using AdvancedKeybinds.Core;

namespace AdvancedKeybinds.Binding
{
	/// <summary>
	/// AKDIAG-only observation of raw keydown -> capture candidate -> accepted/rejected timing.
	/// This class never changes input snapshots, capture tokens, registration, or saved data.
	/// </summary>
	internal static class CaptureResponsivenessDiag
	{
		private const long UnmatchedWarnMs = 100;
		private const long UnmatchedSlowMs = 300;

		private static readonly HashSet<string> PreviousHeld =
			new(StringComparer.OrdinalIgnoreCase);

		private static bool _active;
		private static int _sequence;
		private static long _beginMs;
		private static long _readyMs;
		private static long _lastPollMs;
		private static string _initialBlocked = string.Empty;

		private static long _rawDownMs;
		private static uint _rawVk;
		private static ushort _rawScan;
		private static bool _rawExtended;
		private static string _rawIdentity;
		private static string _rawFirstSource;
		private static string _rawAdditionalSource;
		private static bool _rawIsDown;
		private static bool _rawMatched;
		private static bool _rawWarned;
		private static bool _rawSlowWarned;

		private static bool _awaitingUi;
		private static long _acceptedMs;
		private static string _acceptedToken;

		public static void Begin(
			IEnumerable<string> heldAtBegin,
			RegisterCaptureSession.Phase phase)
		{
			if (!AkDiag.Enabled)
				return;

			try
			{
				_active = true;
				_awaitingUi = false;
				_acceptedMs = 0;
				_acceptedToken = null;
				_sequence++;
				_beginMs = Environment.TickCount64;
				_lastPollMs = _beginMs;
				_readyMs = phase == RegisterCaptureSession.Phase.WaitingForNewInput
					? _beginMs
					: 0;
				_initialBlocked = Join(heldAtBegin);

				PreviousHeld.Clear();
				AddAll(PreviousHeld, heldAtBegin);
				ClearRaw();

				if (AkDiag.Enabled)
				{
					AkDiag.Log(
					"KeyResponse BEGIN seq=" + _sequence
					+ " phase=" + phase
					+ " blocked=[" + _initialBlocked + "]"
					+ " " + KeyboardLayoutHelper.FormatCaptureLayoutDiag());
				}
			}
			catch
			{
				// Diagnostics must never affect registration.
			}
		}

		public static void Cancel(string reason)
		{
			if (!AkDiag.Enabled || !_active)
				return;

			try
			{
				long now = Environment.TickCount64;
				if (AkDiag.Enabled)
				{
					AkDiag.Log(
					"KeyResponse END seq=" + _sequence
					+ " result=cancel"
					+ " reason=" + (reason ?? "-")
					+ " totalMs=" + Math.Max(0, now - _beginMs)
					+ FormatPendingRaw(now));
				}
			}
			catch
			{
				// Diagnostics must never affect registration.
			}
			finally
			{
				Reset();
			}
		}

		public static void NoteRawKeyDown(
			uint vk,
			ushort scan,
			bool extended,
			string source,
			bool isRepeat)
		{
			if (!AkDiag.Enabled || !_active || isRepeat)
				return;

			try
			{
				long now = Environment.TickCount64;
				if (_rawIsDown
					&& vk == _rawVk
					&& scan == _rawScan
					&& extended == _rawExtended)
				{
					if (string.Equals(source, _rawFirstSource, StringComparison.Ordinal)
						|| string.Equals(source, _rawAdditionalSource, StringComparison.Ordinal))
						return;

					_rawAdditionalSource = source;
					if (AkDiag.Enabled)
					{
						AkDiag.Log(
						"KeyResponse RAW_PATH seq=" + _sequence
						+ " vk=0x" + vk.ToString("X2")
						+ " scan=0x" + scan.ToString("X2")
						+ " ext=" + extended
						+ " firstSource=" + (_rawFirstSource ?? "-")
						+ " additionalSource=" + (source ?? "-")
						+ " pathDeltaMs=" + Math.Max(0, now - _rawDownMs));
					}
					return;
				}

				_rawDownMs = now;
				_rawVk = vk;
				_rawScan = scan;
				_rawExtended = extended;
				_rawFirstSource = source;
				_rawIsDown = true;
				_rawIdentity =
					"vk=0x" + vk.ToString("X2")
					+ " scan=0x" + scan.ToString("X2")
					+ " ext=" + extended
					+ " source=" + (source ?? "-");
				_rawMatched = false;
				_rawWarned = false;
				_rawSlowWarned = false;

				if (AkDiag.Enabled)
				{
					AkDiag.Log(
					"KeyResponse RAW_DOWN seq=" + _sequence
					+ " " + _rawIdentity
					+ " sinceBeginMs=" + Math.Max(0, _rawDownMs - _beginMs)
					+ " ready=" + (_readyMs != 0));
				}
			}
			catch
			{
				// Diagnostics must never affect registration.
			}
		}

		public static void NoteRawKeyUp(uint vk, ushort scan, bool extended, string source)
		{
			if (!AkDiag.Enabled || !_active || _rawDownMs == 0 || !_rawIsDown)
				return;

			try
			{
				if (vk != _rawVk || scan != _rawScan || extended != _rawExtended)
					return;

				long now = Environment.TickCount64;
				long heldMs = Math.Max(0, now - _rawDownMs);
				_rawIsDown = false;
				if (AkDiag.Enabled)
				{
					AkDiag.Log(
					"KeyResponse RAW_UP seq=" + _sequence
					+ " vk=0x" + vk.ToString("X2")
					+ " scan=0x" + scan.ToString("X2")
					+ " ext=" + extended
					+ " source=" + (source ?? "-")
					+ " heldMs=" + heldMs
					+ " matchedCandidate=" + _rawMatched
					+ (_rawMatched
					? string.Empty
					: " outcome=raw-tap-not-seen-by-capture-yet"));
				}
			}
			catch
			{
				// Diagnostics must never affect registration.
			}
		}

		public static void Poll(
			RegisterCaptureSession.Phase phaseBefore,
			RegisterCaptureSession.Phase phaseAfter,
			IEnumerable<string> held,
			IEnumerable<string> edges,
			bool accepted,
			string acceptedToken,
			string decisionReason,
			IEnumerable<string> blocked)
		{
			if (!AkDiag.Enabled || !_active)
				return;

			try
			{
				long now = Environment.TickCount64;
				long pollGap = _lastPollMs == 0 ? 0 : Math.Max(0, now - _lastPollMs);
				_lastPollMs = now;
				if (pollGap >= UnmatchedWarnMs)
				{
					if (AkDiag.Enabled)
					{
						AkDiag.Log(
						"KeyResponse POLL_GAP seq=" + _sequence
						+ " gapMs=" + pollGap
						+ " phase=" + phaseBefore
						+ " meaning=game-update-or-main-thread-stall");
					}
				}

				if (_readyMs == 0
					&& phaseAfter == RegisterCaptureSession.Phase.WaitingForNewInput)
				{
					_readyMs = now;
					if (AkDiag.Enabled)
					{
						AkDiag.Log(
						"KeyResponse READY seq=" + _sequence
						+ " releaseWaitMs=" + Math.Max(0, now - _beginMs)
						+ " initialBlocked=[" + _initialBlocked + "]");
					}
				}

				var newcomers = new List<string>(4);
				if (held != null)
				{
					foreach (string token in held)
					{
						if (!string.IsNullOrEmpty(token)
							&& !PreviousHeld.Contains(token)
							&& !Contains(newcomers, token))
						{
							newcomers.Add(token);
						}
					}
				}

				if (edges != null)
				{
					foreach (string token in edges)
					{
						if (!string.IsNullOrEmpty(token) && !Contains(newcomers, token))
							newcomers.Add(token);
					}
				}

				PreviousHeld.Clear();
				AddAll(PreviousHeld, held);

				if (newcomers.Count > 0 || accepted)
				{
					long rawAge = _rawDownMs != 0 ? Math.Max(0, now - _rawDownMs) : -1;
					_rawMatched = _rawDownMs != 0;

					if (AkDiag.Enabled)
					{
						AkDiag.Log(
						"KeyResponse RESULT seq=" + _sequence
						+ " candidates=[" + string.Join(",", newcomers) + "]"
						+ " accepted=" + accepted
						+ " token=" + (acceptedToken ?? "-")
						+ " phase=" + phaseBefore + "->" + phaseAfter
						+ " reason=" + (decisionReason ?? "-")
						+ " blocked=[" + Join(blocked) + "]"
						+ " rawToDecisionMs=" + rawAge
						+ " raw={" + (_rawIdentity ?? "-") + "}"
						+ " display=" + (string.IsNullOrEmpty(acceptedToken)
						? "-"
						: KeyToken.FormatToken(acceptedToken)));
					}
				}

				WarnIfRawUnmatched(now, phaseAfter, decisionReason, blocked);

				if (accepted)
				{
					if (AkDiag.Enabled)
					{
						AkDiag.Log(
						"KeyResponse END seq=" + _sequence
						+ " result=accepted"
						+ " token=" + (acceptedToken ?? "-")
						+ " totalMs=" + Math.Max(0, now - _beginMs)
						+ " readyToAcceptMs=" + (_readyMs == 0
						? -1
						: Math.Max(0, now - _readyMs)));
					}
					_active = false;
					_awaitingUi = true;
					_acceptedMs = now;
					_acceptedToken = acceptedToken;
					PreviousHeld.Clear();
					ClearRaw();
				}
			}
			catch
			{
				// Diagnostics must never affect registration.
			}
		}

		public static void NoteInternalUpdated()
		{
			if (!AkDiag.Enabled || !_awaitingUi)
				return;

			try
			{
				long now = Environment.TickCount64;
				if (AkDiag.Enabled)
				{
					AkDiag.Log(
					"KeyResponse INTERNAL_UPDATED seq=" + _sequence
					+ " token=" + (_acceptedToken ?? "-")
					+ " acceptedToInternalMs=" + Math.Max(0, now - _acceptedMs));
				}
			}
			catch
			{
				// Diagnostics must never affect registration.
			}
		}

		public static void NoteUiUpdated()
		{
			if (!AkDiag.Enabled || !_awaitingUi)
				return;

			try
			{
				long now = Environment.TickCount64;
				if (AkDiag.Enabled)
				{
					AkDiag.Log(
					"KeyResponse UI_UPDATED seq=" + _sequence
					+ " token=" + (_acceptedToken ?? "-")
					+ " acceptedToUiMs=" + Math.Max(0, now - _acceptedMs));
				}
			}
			catch
			{
				// Diagnostics must never affect registration.
			}
			finally
			{
				Reset();
			}
		}

		private static void WarnIfRawUnmatched(
			long now,
			RegisterCaptureSession.Phase phase,
			string reason,
			IEnumerable<string> blocked)
		{
			if (_rawDownMs == 0 || _rawMatched)
				return;

			long age = Math.Max(0, now - _rawDownMs);
			if (!_rawWarned && age >= UnmatchedWarnMs)
			{
				_rawWarned = true;
				LogRawUnmatched("delayed", age, phase, reason, blocked);
			}

			if (!_rawSlowWarned && age >= UnmatchedSlowMs)
			{
				_rawSlowWarned = true;
				LogRawUnmatched("slow", age, phase, reason, blocked);
			}
		}

		private static void LogRawUnmatched(
			string severity,
			long age,
			RegisterCaptureSession.Phase phase,
			string reason,
			IEnumerable<string> blocked)
		{
			if (AkDiag.Enabled)
			{
				AkDiag.Log(
				"KeyResponse RAW_UNMATCHED seq=" + _sequence
				+ " severity=" + severity
				+ " ageMs=" + age
				+ " phase=" + phase
				+ " reason=" + (reason ?? "-")
				+ " blocked=[" + Join(blocked) + "]"
				+ " raw={" + (_rawIdentity ?? "-") + "}");
			}
		}

		private static string FormatPendingRaw(long now)
		{
			if (_rawDownMs == 0 || _rawMatched)
				return string.Empty;

			return " pendingRawAgeMs=" + Math.Max(0, now - _rawDownMs)
				+ " raw={" + (_rawIdentity ?? "-") + "}";
		}

		private static void ClearRaw()
		{
			_rawDownMs = 0;
			_rawVk = 0;
			_rawScan = 0;
			_rawExtended = false;
			_rawIdentity = null;
			_rawFirstSource = null;
			_rawAdditionalSource = null;
			_rawIsDown = false;
			_rawMatched = false;
			_rawWarned = false;
			_rawSlowWarned = false;
		}

		private static void Reset()
		{
			_active = false;
			_beginMs = 0;
			_readyMs = 0;
			_lastPollMs = 0;
			_initialBlocked = string.Empty;
			_awaitingUi = false;
			_acceptedMs = 0;
			_acceptedToken = null;
			PreviousHeld.Clear();
			ClearRaw();
		}

		private static void AddAll(HashSet<string> destination, IEnumerable<string> source)
		{
			if (destination == null || source == null)
				return;

			foreach (string token in source)
			{
				if (!string.IsNullOrEmpty(token))
					destination.Add(token);
			}
		}

		private static string Join(IEnumerable<string> values)
			=> values == null ? string.Empty : string.Join(",", values);

		private static bool Contains(List<string> values, string token)
		{
			for (int i = 0; i < values.Count; i++)
			{
				if (string.Equals(values[i], token, StringComparison.OrdinalIgnoreCase))
					return true;
			}

			return false;
		}
	}
}
