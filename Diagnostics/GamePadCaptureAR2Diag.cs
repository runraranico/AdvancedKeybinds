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
	/// TEMPORARY event-driven diagnostic for ComboPanel Pad capture / R2 investigation.
	/// Output: deck log\GamePad\AK_GamePadCapture_A_R2_Diag.txt
	/// Must never call state-mutating APIs (e.g. IsCaptureInputBlocked). Snapshot is read-only.
	/// Delete this file and every call site after Steam Deck / Windows+PS5 verification completes.
	/// </summary>
	internal static class GamePadCaptureAR2Diag
	{
		internal const bool Enabled = true;

		private const string Folder =
			@"C:\Users\runra\Documents\My Games\Terraria\tModLoader\deck log\GamePad";
		private const string FileName = "AK_GamePadCapture_A_R2_Diag.txt";
		private const int MaxLines = 4000;

		private static readonly object Gate = new();
		private static readonly StringBuilder Line = new();
		private static string _lastPayload;
		private static int _written;
		private static bool _broken;
		private static bool _reentry;

		private static float _r2Peak;
		private static bool _r2SessionActive;
		private static bool _r2WasAbovePress;
		private static bool _r2WasAboveRelease;
		private static int _startSlotCaptureCountThisEpoch = -1;
		private static int _trackedEpoch = int.MinValue;

		internal static void ResetDedupe()
		{
			lock (Gate)
			{
				_lastPayload = null;
			}
		}

		internal static void Event(string tag, string detail = null)
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

		internal static void NoteStartSlotCapture(string caller, int slot)
		{
			if (!Enabled || _broken || _reentry)
				return;

			int epoch = FnaGamePadProvider.InputTickEpoch;
			if (epoch != _trackedEpoch)
			{
				_trackedEpoch = epoch;
				_startSlotCaptureCountThisEpoch = 0;
			}

			_startSlotCaptureCountThisEpoch++;
			Event(
				"StartSlotCapture",
				"caller=" + (caller ?? "?")
				+ " slot=" + slot
				+ " countThisEpoch=" + _startSlotCaptureCountThisEpoch
				+ SnapshotCommonReadonly());
		}

		internal static void NoteCompleteSlotCapture(
			string token,
			int slot,
			bool accepted,
			bool finishKeyCaptureCalled,
			bool purpleCleared,
			bool notifyPadCalled)
		{
			if (!Enabled || _broken || _reentry)
				return;

			Event(
				"CompleteSlotCapture",
				"token=" + (token ?? "-")
				+ " slot=" + slot
				+ " accepted=" + accepted
				+ " FinishKeyCapture=" + finishKeyCaptureCalled
				+ " purpleCleared=" + purpleCleared
				+ " NotifyPadCaptureConsumed=" + notifyPadCalled
				+ SnapshotCommonReadonly()
				+ SnapshotAReadonly()
				+ SnapshotMouseReadonly());
		}

		internal static void NoteReleaseGate(string phase, string token, string reason = null)
		{
			if (!Enabled || _broken || _reentry)
				return;

			_reentry = true;
			try
			{
				Event(
					"ReleaseGate",
					"phase=" + phase
					+ " token=" + (token ?? "-")
					+ (reason == null ? string.Empty : " reason=" + reason)
					+ SnapshotCommonReadonly()
					+ SnapshotAReadonly());
			}
			finally
			{
				_reentry = false;
			}
		}

		internal static void NoteHandleClicksSlotRestart(
			int slot,
			bool physicalLeftEdge,
			bool gameLeftEdge,
			bool blocked)
		{
			if (!Enabled || _broken || _reentry)
				return;

			Event(
				"HandleClicks.SlotRestart",
				"slot=" + slot
				+ " physicalLeftEdge=" + physicalLeftEdge
				+ " gameLeftEdge=" + gameLeftEdge
				+ " blocked=" + blocked
				+ SnapshotCommonReadonly()
				+ SnapshotAReadonly()
				+ SnapshotMouseReadonly()
				+ " captureGateRaw=" + ComboPanelGamePadNavigation.CaptureReleaseGateActiveRaw
				+ " uiReuseActive=?");
		}

		internal static void NoteR2Analog(float analog)
		{
			if (!Enabled || _broken || _reentry)
				return;

			bool abovePress = analog >= GamePadTriggerPolicy.PressThreshold;
			bool aboveRelease = analog >= GamePadTriggerPolicy.ReleaseThreshold;
			bool digital = GamePadTriggerPolicy.EvaluateDigital(analog, _r2WasAboveRelease && _r2SessionActive);

			bool start = !_r2SessionActive && analog > 0.02f;
			bool peak = analog > _r2Peak + 0.001f;
			bool crossedPress = abovePress != _r2WasAbovePress;
			bool crossedRelease = aboveRelease != _r2WasAboveRelease;
			bool end = _r2SessionActive && analog <= 0.02f && !_r2WasAboveRelease;

			if (!(start || peak || crossedPress || crossedRelease || end))
				return;

			if (start)
			{
				_r2SessionActive = true;
				_r2Peak = analog;
			}

			if (peak)
				_r2Peak = analog;

			Event(
				"R2Analog",
				"v=" + analog.ToString("0.000")
				+ " peak=" + _r2Peak.ToString("0.000")
				+ " digital=" + digital
				+ " justPressed=" + FnaGamePadProvider.WasJustPressed(GamePadToken.RightTrigger)
				+ " justReleased=" + FnaGamePadProvider.WasJustReleased(GamePadToken.RightTrigger)
				+ " start=" + start
				+ " peakUpdate=" + peak
				+ " crossPress=" + crossedPress
				+ " crossRelease=" + crossedRelease
				+ " end=" + end
				+ SnapshotCommonReadonly());

			_r2WasAbovePress = abovePress;
			_r2WasAboveRelease = aboveRelease;
			if (end)
			{
				_r2SessionActive = false;
				_r2Peak = 0f;
				_r2WasAbovePress = false;
				_r2WasAboveRelease = false;
			}
		}

		internal static void PollR2FromSnapshot()
		{
			if (!Enabled || _broken || _reentry || !FnaGamePadProvider.Current.IsConnected)
				return;

			NoteR2Analog(FnaGamePadProvider.Current.RightTriggerAnalog);
		}

		/// <summary>
		/// Read-only snapshot. Must not call <see cref="ComboPanelGamePadNavigation.IsCaptureInputBlocked"/>
		/// (that method can clear the gate and log — recursion with NoteReleaseGate).
		/// </summary>
		private static string SnapshotCommonReadonly()
		{
			var snap = FnaGamePadProvider.Current;
			return " epoch=" + FnaGamePadProvider.InputTickEpoch
				+ " focus=" + ComboPanelGamePadNavigation.Focus
				+ " padNav=" + ComboPanelGamePadNavigation.IsPadNavVisible
				+ " captureGateRaw=" + ComboPanelGamePadNavigation.CaptureReleaseGateActiveRaw
				+ " gateTok=" + (ComboPanelGamePadNavigation.CaptureReleaseToken ?? "-")
				+ " connected=" + snap.IsConnected
				+ " playerIndex=" + (snap.ActivePlayerIndex.HasValue ? snap.ActivePlayerIndex.Value.ToString() : "-")
				+ " gpMode=" + GamePadSubsystemRuntime.Mode
				+ " inputMode=" + PlayerInput.CurrentInputMode
				+ " usingPad=" + PlayerInput.UsingGamepad;
		}

		private static string SnapshotAReadonly()
		{
			return " A.down=" + FnaGamePadProvider.IsDown(GamePadToken.A)
				+ " A.jp=" + FnaGamePadProvider.WasJustPressed(GamePadToken.A)
				+ " A.jr=" + FnaGamePadProvider.WasJustReleased(GamePadToken.A);
		}

		private static string SnapshotMouseReadonly()
		{
			bool physLeft = false;
			try { physLeft = MouseInputHelper.IsPhysicalLeftDown(); }
			catch { /* ignore */ }

			return " Mouse1.phys=" + physLeft
				+ " Main.mouseLeft=" + Main.mouseLeft;
		}

		private static void WriteUnlocked(string tag, string detail)
		{
			try
			{
				Directory.CreateDirectory(Folder);
				string path = Path.Combine(Folder, FileName);
				Line.Clear();
				Line.Append(DateTime.Now.ToString("HH:mm:ss.fff"));
				Line.Append(" | ");
				Line.Append(tag);
				if (!string.IsNullOrEmpty(detail))
				{
					Line.Append(' ');
					Line.Append(detail);
				}

				File.AppendAllText(path, Line.ToString() + Environment.NewLine, Encoding.UTF8);
				_written++;
			}
			catch
			{
				_broken = true;
			}
		}
	}
}
