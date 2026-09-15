using System;
using System.IO;
using System.Text;
using AdvancedKeybinds.Binding;
using AdvancedKeybinds.Core;
using Terraria;
using Terraria.GameInput;

namespace AdvancedKeybinds.Diagnostics
{
	/// <summary>
	/// TEMPORARY event-driven diagnostic: ComboPanel registration-wait ↔ HOLD lock investigation.
	/// State-change only. No per-frame I/O / Reflection / StackTrace.
	/// Output: deck log\GamePad\AK_ComboPanelRegistrationWait_Diag_LIVE.txt (+ dated history).
	/// Must never call <see cref="ComboPanelGamePadNavigation.IsCaptureInputBlocked"/> (mutating).
	/// Delete after the HOLD-after-Pad capture root cause is confirmed on device.
	/// </summary>
	internal static class ComboPanelRegistrationWaitDiag
	{
		internal const bool Enabled = true;

		private const string Folder =
			@"C:\Users\runra\Documents\My Games\Terraria\tModLoader\deck log\GamePad";
		private const string LiveFileName = "AK_ComboPanelRegistrationWait_Diag_LIVE.txt";
		private const int MaxLines = 4000;

		private static readonly object Gate = new();
		private static readonly StringBuilder Line = new(512);
		private static readonly StringBuilder TokenBuf = new(128);

		private static string _historyPath;
		private static string _lastPayload;
		private static int _written;
		private static bool _broken;
		private static bool _reentry;

		private static bool _prevRegistrationWait;
		private static bool _hasPrevRegistrationWait;

		private static string _lastCompletedToken;
		private static int _lastCompletedSlot;
		private static int _lastCompletedEpoch = int.MinValue;
		private static bool _hasRecentPadComplete;

		private static string _lastHoldDeniedPayload;
		private static int _lastHoldDeniedEpoch = int.MinValue;

		internal static void Unload()
		{
			lock (Gate)
			{
				_historyPath = null;
				_lastPayload = null;
				_written = 0;
				_broken = false;
				_reentry = false;
				_prevRegistrationWait = false;
				_hasPrevRegistrationWait = false;
				_lastCompletedToken = null;
				_lastCompletedSlot = 0;
				_lastCompletedEpoch = int.MinValue;
				_hasRecentPadComplete = false;
				_lastHoldDeniedPayload = null;
				_lastHoldDeniedEpoch = int.MinValue;
			}
		}

		/// <summary>
		/// Observe registration-wait flag each DrawAndHandle / capture path.
		/// Emits Enter / Leave only on False↔True transitions.
		/// </summary>
		internal static void ObserveRegistrationWait(
			bool registrationWaitNow,
			string caller,
			string reason,
			string actionId,
			int slot,
			int capturingSlot,
			bool awaitingSlotKey,
			bool slotCaptureIsCapturing,
			string capturePhase,
			string acceptedToken,
			string blockedAtBegin)
		{
			if (!Enabled || _broken || _reentry)
				return;
			if (!GamePadSubsystemRuntime.IsActive)
				return;

			bool entered = false;
			bool left = false;
			lock (Gate)
			{
				if (!_hasPrevRegistrationWait)
				{
					_hasPrevRegistrationWait = true;
					_prevRegistrationWait = registrationWaitNow;
					if (registrationWaitNow)
						entered = true;
					else
						return;
				}
				else if (registrationWaitNow == _prevRegistrationWait)
					return;
				else if (registrationWaitNow)
					entered = true;
				else
					left = true;

				_prevRegistrationWait = registrationWaitNow;
			}

			if (entered)
			{
				NoteEnter(
					caller,
					reason,
					actionId,
					slot,
					capturingSlot,
					awaitingSlotKey,
					slotCaptureIsCapturing,
					capturePhase,
					acceptedToken,
					blockedAtBegin);

				MaybeNoteUnexpectedRestart(caller, slot);
			}
			else if (left)
			{
				NoteLeave(
					caller,
					reason,
					actionId,
					slot,
					capturingSlot,
					awaitingSlotKey,
					slotCaptureIsCapturing,
					capturePhase,
					acceptedToken);
			}
		}

		internal static void ForceLeave(string caller, string reason, string actionId)
		{
			if (!Enabled || _broken || _reentry)
				return;

			bool was;
			lock (Gate)
			{
				was = _prevRegistrationWait;
				_prevRegistrationWait = false;
				_hasPrevRegistrationWait = true;
			}

			if (was)
			{
				NoteLeave(
					caller,
					reason,
					actionId,
					slot: 0,
					capturingSlot: 0,
					awaitingSlotKey: false,
					slotCaptureIsCapturing: false,
					capturePhase: "Inactive",
					acceptedToken: null);
			}
		}

		internal static void NoteCaptureCompleted(
			string token,
			int slot,
			bool accepted,
			bool finishKeyCaptureCalled,
			bool slotCaptureCancelled,
			int capturingSlotAfter,
			bool awaitingSlotKeyAfter,
			bool slotCaptureIsCapturingAfter,
			string capturePhaseAfter,
			string focusAfter,
			bool padNavVisibleAfter,
			bool confirmButtonStillHeld)
		{
			if (!Enabled || _broken || _reentry)
				return;
			if (!accepted || !GamePadToken.IsPadToken(token))
				return;

			bool violation = ComboPanelRegistrationWaitDiagPolicy.CaptureCompletedInvariantViolation(
				capturingSlotAfter,
				awaitingSlotKeyAfter,
				slotCaptureIsCapturingAfter);

			lock (Gate)
			{
				_lastCompletedToken = token;
				_lastCompletedSlot = slot;
				_lastCompletedEpoch = FnaGamePadProvider.InputTickEpoch;
				_hasRecentPadComplete = true;
			}

			Event(
				"CaptureCompleted",
				"token=" + (token ?? "-")
				+ " slot=" + slot
				+ " accepted=" + accepted
				+ " finishKeyCaptureCalled=" + finishKeyCaptureCalled
				+ " slotCaptureCancelled=" + slotCaptureCancelled
				+ " capturingSlotAfter=" + capturingSlotAfter
				+ " awaitingSlotKeyAfter=" + awaitingSlotKeyAfter
				+ " slotCaptureIsCapturingAfter=" + slotCaptureIsCapturingAfter
				+ " capturePhaseAfter=" + (capturePhaseAfter ?? "-")
				+ " focusAfter=" + (focusAfter ?? "-")
				+ " padNavVisibleAfter=" + padNavVisibleAfter
				+ " confirmButtonStillHeld=" + confirmButtonStillHeld
				+ (violation ? " invariantViolation=true" : " invariantViolation=false")
				+ SnapshotCommon());
		}

		/// <summary>
		/// HOLD toggle attempt (mouse or pad). When denied by registration wait, records once per
		/// (epoch, target, reason) so a held confirm button cannot spam.
		/// </summary>
		internal static void NoteHoldToggleRequest(
			string source,
			string target,
			string requestedValue,
			bool accepted,
			string deniedReason,
			bool registrationWait,
			int capturingSlot,
			bool awaitingSlotKey,
			bool slotCaptureIsCapturing,
			string capturePhase,
			string focus,
			bool padNavVisible,
			bool captureGateRaw,
			bool confirmButtonHeld)
		{
			if (!Enabled || _broken || _reentry)
				return;

			// Skip quiet accepts with no registration wait — not the investigation target.
			if (accepted && !registrationWait)
				return;

			int epoch = FnaGamePadProvider.InputTickEpoch;
			string payload = (source ?? "?")
				+ "|" + (target ?? "?")
				+ "|" + (deniedReason ?? "ok")
				+ "|" + registrationWait
				+ "|" + epoch;

			lock (Gate)
			{
				if (string.Equals(payload, _lastHoldDeniedPayload, StringComparison.Ordinal)
					&& epoch == _lastHoldDeniedEpoch)
					return;
				_lastHoldDeniedPayload = payload;
				_lastHoldDeniedEpoch = epoch;
			}

			Event(
				"HoldToggleRequest",
				"source=" + (source ?? "?")
				+ " target=" + (target ?? "?")
				+ " requestedValue=" + (requestedValue ?? "-")
				+ " accepted=" + accepted
				+ " deniedReason=" + (deniedReason ?? "-")
				+ " registrationWait=" + registrationWait
				+ " capturingSlot=" + capturingSlot
				+ " awaitingSlotKey=" + awaitingSlotKey
				+ " slotCaptureIsCapturing=" + slotCaptureIsCapturing
				+ " capturePhase=" + (capturePhase ?? "-")
				+ " focus=" + (focus ?? "-")
				+ " padNavVisible=" + padNavVisible
				+ " captureGateRaw=" + captureGateRaw
				+ " confirmButtonHeld=" + confirmButtonHeld
				+ SnapshotCommon());
		}

		private static void MaybeNoteUnexpectedRestart(string caller, int newSlot)
		{
			string prevToken;
			int prevSlot;
			int prevEpoch;
			bool hasRecent;
			lock (Gate)
			{
				hasRecent = _hasRecentPadComplete;
				prevToken = _lastCompletedToken;
				prevSlot = _lastCompletedSlot;
				prevEpoch = _lastCompletedEpoch;
			}

			if (!ComboPanelRegistrationWaitDiagPolicy.IsUnexpectedSameSlotRestart(
				hasRecent,
				prevSlot,
				newSlot,
				registrationWaitJustEntered: true))
				return;

			int epoch = FnaGamePadProvider.InputTickEpoch;
			int elapsed = prevEpoch == int.MinValue ? -1 : epoch - prevEpoch;
			string confirmTok = prevToken;
			bool held = !string.IsNullOrEmpty(confirmTok) && FnaGamePadProvider.IsDown(confirmTok);
			bool jp = !string.IsNullOrEmpty(confirmTok) && FnaGamePadProvider.WasJustPressed(confirmTok);
			bool jr = !string.IsNullOrEmpty(confirmTok) && FnaGamePadProvider.WasJustReleased(confirmTok);

			Event(
				"UnexpectedCaptureRestart",
				"previousToken=" + (prevToken ?? "-")
				+ " previousSlot=" + prevSlot
				+ " newSlot=" + newSlot
				+ " caller=" + (caller ?? "?")
				+ " focus=" + ComboPanelGamePadNavigation.Focus
				+ " confirmButtonHeld=" + held
				+ " confirmJustPressed=" + jp
				+ " confirmJustReleased=" + jr
				+ " elapsedInputTicksSinceCapture=" + elapsed
				+ SnapshotCommon());
		}

		private static void NoteEnter(
			string caller,
			string reason,
			string actionId,
			int slot,
			int capturingSlot,
			bool awaitingSlotKey,
			bool slotCaptureIsCapturing,
			string capturePhase,
			string acceptedToken,
			string blockedAtBegin)
		{
			Event(
				"RegistrationWaitEnter",
				"caller=" + (caller ?? "?")
				+ " reason=" + (reason ?? "-")
				+ " actionId=" + (actionId ?? "-")
				+ " slot=" + slot
				+ " capturingSlot=" + capturingSlot
				+ " awaitingSlotKey=" + awaitingSlotKey
				+ " slotCaptureIsCapturing=" + slotCaptureIsCapturing
				+ " capturePhase=" + (capturePhase ?? "-")
				+ " acceptedToken=" + (acceptedToken ?? "-")
				+ " blockedAtBegin=[" + (blockedAtBegin ?? "") + "]"
				+ " focus=" + ComboPanelGamePadNavigation.Focus
				+ " padNavVisible=" + ComboPanelGamePadNavigation.IsPadNavVisible
				+ " captureGateRaw=" + ComboPanelGamePadNavigation.CaptureReleaseGateActiveRaw
				+ SnapshotPadEdges()
				+ SnapshotMouse()
				+ SnapshotCommon());
		}

		private static void NoteLeave(
			string caller,
			string reason,
			string actionId,
			int slot,
			int capturingSlot,
			bool awaitingSlotKey,
			bool slotCaptureIsCapturing,
			string capturePhase,
			string acceptedToken)
		{
			Event(
				"RegistrationWaitLeave",
				"caller=" + (caller ?? "?")
				+ " reason=" + (reason ?? "-")
				+ " actionId=" + (actionId ?? "-")
				+ " slot=" + slot
				+ " capturingSlot=" + capturingSlot
				+ " awaitingSlotKey=" + awaitingSlotKey
				+ " slotCaptureIsCapturing=" + slotCaptureIsCapturing
				+ " capturePhase=" + (capturePhase ?? "-")
				+ " acceptedToken=" + (acceptedToken ?? "-")
				+ " focus=" + ComboPanelGamePadNavigation.Focus
				+ " padNavVisible=" + ComboPanelGamePadNavigation.IsPadNavVisible
				+ SnapshotCommon());
		}

		private static void Event(string tag, string detail)
		{
			if (!Enabled || _broken || tag == null || _reentry)
				return;

			lock (Gate)
			{
				if (_written >= MaxLines || _reentry)
					return;

				string payload = detail == null ? tag : tag + " " + detail;
				if (string.Equals(payload, _lastPayload, StringComparison.Ordinal))
					return;

				_lastPayload = payload;
				WriteUnlocked(tag, detail);
			}
		}

		private static string SnapshotCommon()
		{
			int guc = 0;
			try { guc = (int)Main.GameUpdateCount; }
			catch { /* ignore */ }

			return " inputTickEpoch=" + FnaGamePadProvider.InputTickEpoch
				+ " gameUpdateCount=" + guc
				+ " timestamp=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")
				+ " gpMode=" + GamePadSubsystemRuntime.Mode
				+ " inputMode=" + PlayerInput.CurrentInputMode
				+ " usingPad=" + PlayerInput.UsingGamepad
				+ " connected=" + FnaGamePadProvider.IsConnected;
		}

		private static string SnapshotMouse()
		{
			bool phys = false;
			try { phys = MouseInputHelper.IsPhysicalLeftDown(); }
			catch { /* ignore */ }

			return " Main.mouseLeft=" + Main.mouseLeft
				+ " physicalMouseLeft=" + phys;
		}

		private static string SnapshotPadEdges()
		{
			TokenBuf.Clear();
			AppendMaskTokens("currentHeldPadTokens", FnaGamePadProvider.Current.Digital, TokenBuf);
			AppendJust("justPressedPadTokens", FnaGamePadProvider.CopyJustPressed, TokenBuf);
			AppendJust("justReleasedPadTokens", FnaGamePadProvider.CopyJustReleased, TokenBuf);
			return TokenBuf.ToString();
		}

		private static void AppendMaskTokens(string name, ulong mask, StringBuilder dest)
		{
			dest.Append(' ').Append(name).Append("=[");
			bool first = true;
			var tmp = new System.Collections.Generic.List<string>(8);
			GamePadDigitalBits.AppendTokens(mask, tmp);
			for (int i = 0; i < tmp.Count; i++)
			{
				if (!first)
					dest.Append(',');
				first = false;
				dest.Append(tmp[i]);
			}
			dest.Append(']');
		}

		private static void AppendJust(
			string name,
			Action<System.Collections.Generic.List<string>> copy,
			StringBuilder dest)
		{
			var tmp = new System.Collections.Generic.List<string>(8);
			try { copy(tmp); }
			catch { /* ignore */ }

			dest.Append(' ').Append(name).Append("=[");
			for (int i = 0; i < tmp.Count; i++)
			{
				if (i > 0)
					dest.Append(',');
				dest.Append(tmp[i]);
			}
			dest.Append(']');
		}

		private static void WriteUnlocked(string tag, string detail)
		{
			try
			{
				Directory.CreateDirectory(Folder);
				if (_historyPath == null)
				{
					string stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss_fff");
					_historyPath = Path.Combine(
						Folder,
						"AK_ComboPanelRegistrationWait_Diag_" + stamp + ".txt");
				}

				Line.Clear();
				Line.Append(DateTime.Now.ToString("HH:mm:ss.fff"));
				Line.Append(" | event=");
				Line.Append(tag);
				if (!string.IsNullOrEmpty(detail))
				{
					Line.Append(' ');
					Line.Append(detail);
				}

				string text = Line.ToString() + Environment.NewLine;
				File.AppendAllText(Path.Combine(Folder, LiveFileName), text, Encoding.UTF8);
				File.AppendAllText(_historyPath, text, Encoding.UTF8);
				_written++;
			}
			catch
			{
				_broken = true;
			}
		}
	}
}
