using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using AdvancedKeybinds.Core;
using Terraria;

namespace AdvancedKeybinds.Binding
{
	/// <summary>
	/// In-game diagnostic for measured WM_KEY* registration (JIS 、 。 「 and other normal keys).
	/// Starts only via /akdiag jis-oem (or explicit BeginCaptureSession) — never on normal slot capture.
	/// Diagnostic build only; public builds keep AkDiagSwitch.On=false and should not auto-run this.
	/// Session caps: 500 events / 10000 lines / ~2MB / 60s — then auto-end with a single flush.
	/// </summary>
	internal static class JisOemInGameDiagnostic
	{
		public const int MaxEvents = 500;
		public const int MaxLines = 10000;
		public const int MaxFileBytes = 2 * 1024 * 1024;
		public const int MaxSessionSeconds = 60;

		private static readonly List<string> Lines = new();
		private static bool _sessionActive;
		private static int _eventCount;
		private static string _lastPath;
		private static string _lastRegisteredToken;
		private static DateTime _sessionStartedUtc;
		private static bool _flushedOnce;
		private static bool _endedByLimit;

		public static string LastReportPath => _lastPath;
		public static bool IsSessionActive => _sessionActive;

		public static void BeginCaptureSession()
		{
			_sessionActive = true;
			_eventCount = 0;
			_lastRegisteredToken = null;
			_flushedOnce = false;
			_endedByLimit = false;
			_sessionStartedUtc = DateTime.UtcNow;
			Lines.Clear();
			Lines.Add("AdvancedKeybinds_JisOemInGameDiagnostic");
			Lines.Add("Generated=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
			Lines.Add("Policy=" + MeasuredPhysicalKeyNormalizer.PolicyVersion);
			Lines.Add("SameEventPolicy=" + SameEventAliasPolicy.PolicyVersion);
			Lines.Add("EventSequencePolicy=" + EventSequenceUnifier.PolicyVersion);
			Lines.Add("Source=GameWindowInputHook WM_KEY* (in-game)");
			Lines.Add("Limits=MaxEvents=" + MaxEvents
				+ ";MaxLines=" + MaxLines
				+ ";MaxFileBytes=" + MaxFileBytes
				+ ";MaxSessionSeconds=" + MaxSessionSeconds);
			Lines.Add("Note=Records keys while session active; OEM watch marked RelevantOemWatch");
			Lines.Add("");
		}

		public static void EndCaptureSessionAndFlush(string reason)
		{
			if (!_sessionActive && Lines.Count == 0)
				return;

			if (!_endedByLimit)
				Lines.Add("SessionEndReason=" + reason);
			FlushOnce();
			_sessionActive = false;
		}

		public static void Observe(
			uint vk,
			ushort scan,
			bool extended,
			bool isDown,
			string source,
			string resolverTier,
			string resolverToken,
			string keysName,
			string vkToken,
			string scToken,
			IReadOnlyList<string> beforeCandidates,
			IReadOnlyList<string> afterCandidates,
			IReadOnlyList<string> captureResolverInput,
			string chooseMeasuredToken,
			string finalOrReject,
			long eventSequence = 0,
			string unifyNote = null)
		{
			if (!_sessionActive)
				return;
			if (CheckLimitsAndMaybeEnd())
				return;

			_eventCount++;
			bool relevant = IsOemWatch(vk, scan, keysName, scToken);

			Lines.Add("===== Event =====");
			Lines.Add("EventSequence=" + eventSequence);
			Lines.Add("WM_KEYDOWN_received=" + isDown);
			Lines.Add("WM_KEYUP_received=" + (!isDown));
			Lines.Add("Source=" + (source ?? "-"));
			Lines.Add("wParam_VK=0x" + vk.ToString("X2", CultureInfo.InvariantCulture));
			Lines.Add("lParam_ScanCode=0x" + (scan & 0xFF).ToString("X2", CultureInfo.InvariantCulture));
			Lines.Add("Extended=" + (extended ? "1" : "0"));
			Lines.Add("PhysicalKeyResolver_Tier=" + (resolverTier ?? "-"));
			Lines.Add("PhysicalKeyResolver_Token=" + (resolverToken ?? "-"));
			Lines.Add("ChooseMeasuredCaptureToken=" + (chooseMeasuredToken ?? "-"));
			Lines.Add("KeysName=" + (keysName ?? "-"));
			Lines.Add("VK_Token=" + (vkToken ?? "-"));
			Lines.Add("SC_Token=" + (scToken ?? "-"));
			Lines.Add("CollapseOrNormalize_Before=[" + Join(beforeCandidates) + "]");
			Lines.Add("CollapseOrNormalize_After=[" + Join(afterCandidates) + "]");
			Lines.Add("RegisterCaptureSession_Input=[" + Join(captureResolverInput) + "]");
			Lines.Add("EventSequenceUnify=" + (unifyNote ?? "-"));
			Lines.Add("FinalOrReject=" + (finalOrReject ?? "-"));
			Lines.Add("RelevantOemWatch=" + relevant);
			Lines.Add("");
		}

		/// <summary>Overload used from WindowsKeySuppressor with resolver result fields.</summary>
		public static void Observe(
			uint vk,
			ushort scan,
			bool extended,
			bool isDown,
			string source,
			PhysicalKeyResolver.ResolveResult resolved,
			IReadOnlyList<string> beforeCandidates,
			IReadOnlyList<string> afterCandidates,
			IReadOnlyList<string> captureResolverInput,
			string chooseMeasuredToken,
			string finalOrReject,
			long eventSequence = 0,
			string unifyNote = null)
		{
			string keysName = resolved.MonoGameKey?.ToString();
			string vkToken = PhysicalKeyToken.IsValidVirtualKey(vk) ? PhysicalKeyToken.FormatVk(vk) : "-";
			string scToken = scan != 0 ? PhysicalKeyToken.FormatScan(scan, extended) : "-";
			Observe(
				vk, scan, extended, isDown, source,
				resolved.Tier, resolved.Token, keysName, vkToken, scToken,
				beforeCandidates, afterCandidates, captureResolverInput,
				chooseMeasuredToken, finalOrReject, eventSequence, unifyNote);
		}

		public static void ObserveKeyUp(
			uint vk,
			ushort scan,
			bool extended,
			string source,
			string heldAfterUp,
			long eventSequence = 0)
		{
			if (!_sessionActive)
				return;
			if (CheckLimitsAndMaybeEnd())
				return;

			_eventCount++;
			Lines.Add("===== KeyUp =====");
			Lines.Add("EventSequence=" + eventSequence);
			Lines.Add("WM_KEYUP_received=True");
			Lines.Add("Source=" + (source ?? "-"));
			Lines.Add("wParam_VK=0x" + vk.ToString("X2", CultureInfo.InvariantCulture));
			Lines.Add("lParam_ScanCode=0x" + (scan & 0xFF).ToString("X2", CultureInfo.InvariantCulture));
			Lines.Add("Extended=" + (extended ? "1" : "0"));
			Lines.Add("HeldAfterKeyUp=" + (heldAfterUp ?? "-"));
			Lines.Add("LastRegisteredToken=" + (_lastRegisteredToken ?? "-"));
			Lines.Add("");
		}

		public static void NoteCaptureAccept(string token, string collapseDiag, string reason)
		{
			if (!_sessionActive)
				return;

			_lastRegisteredToken = token;
			Lines.Add("===== CaptureAccept =====");
			Lines.Add("FinalRegisteredToken=" + token);
			Lines.Add("CollapseDiag=" + (collapseDiag ?? "-"));
			Lines.Add("RegisterCaptureSession_Result=" + (reason ?? "-"));
			Lines.Add("Reason=" + (reason ?? "-"));
			Lines.Add("");
		}

		public static void NoteCaptureReject(string collapseDiag, string reason)
		{
			if (!_sessionActive)
				return;

			Lines.Add("===== CaptureReject =====");
			Lines.Add("CollapseDiag=" + (collapseDiag ?? "-"));
			Lines.Add("RegisterCaptureSession_Result=reject");
			Lines.Add("RejectReason=" + (reason ?? "-"));
			Lines.Add("");
		}

		/// <summary>After registration, record whether the same key fired a binding (pressed set vs saved token).</summary>
		public static void NoteActivation(string savedToken, IEnumerable<string> pressedTokens, bool wouldFire)
		{
			if (!_sessionActive)
				return;

			Lines.Add("===== Activation =====");
			Lines.Add("SavedToken=" + (savedToken ?? "-"));
			Lines.Add("Pressed=[" + Join(pressedTokens as IReadOnlyList<string> ?? ToList(pressedTokens)) + "]");
			Lines.Add("WouldFire=" + wouldFire);
			Lines.Add("MatchExactOrPhysical=" + BindingPhysicalHeldMatch.IsHeld(savedToken, pressedTokens));
			Lines.Add("");
		}

		private static bool CheckLimitsAndMaybeEnd()
		{
			if (!_sessionActive || _endedByLimit)
				return _endedByLimit;

			double elapsed = (DateTime.UtcNow - _sessionStartedUtc).TotalSeconds;
			int approxBytes = 0;
			for (int i = 0; i < Lines.Count; i++)
				approxBytes += (Lines[i]?.Length ?? 0) + 2;

			string hit = null;
			if (_eventCount >= MaxEvents)
				hit = "MaxEvents";
			else if (Lines.Count >= MaxLines)
				hit = "MaxLines";
			else if (approxBytes >= MaxFileBytes)
				hit = "MaxFileBytes";
			else if (elapsed >= MaxSessionSeconds)
				hit = "MaxSessionSeconds";

			if (hit == null)
				return false;

			_endedByLimit = true;
			Lines.Add("SessionEndReason=limit:" + hit);
			FlushOnce();
			_sessionActive = false;
			return true;
		}

		private static void FlushOnce()
		{
			if (_flushedOnce)
				return;
			_flushedOnce = true;

			try
			{
				string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
				string dir = Path.Combine(Main.SavePath, "AdvancedKeybinds");
				Directory.CreateDirectory(dir);
				_lastPath = Path.Combine(dir, "AdvancedKeybinds_JisOemInGameDiagnostic_" + stamp + ".txt");
				string body = string.Join(Environment.NewLine, Lines);
				File.WriteAllText(_lastPath, body, new UTF8Encoding(false));

				string docs = Path.Combine(
					Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
					"My Games", "Terraria", "tModLoader", "ModSources", "AdvancedKeybinds", "docs", "diagnostic-runs");
				if (Directory.Exists(Path.GetDirectoryName(docs)))
				{
					Directory.CreateDirectory(docs);
					File.WriteAllText(
						Path.Combine(docs, Path.GetFileName(_lastPath)),
						body,
						new UTF8Encoding(false));
				}

				if (AkDiag.Enabled) AkDiag.Log("JisOemInGameDiagnostic wrote " + _lastPath);
			}
			catch (Exception ex)
			{
				if (AkDiag.Enabled) AkDiag.Log("JisOemInGameDiagnostic flush failed: " + ex.Message);
			}
		}

		private static bool IsOemWatch(uint vk, ushort scan, string keysName, string scToken)
		{
			scan = (ushort)(scan & 0xFF);
			if (scan is 0x33 or 0x34 or 0x1A or 0x1B)
				return true;
			if (vk is 0xBC or 0xBE or 0xDB or 0xDD or 0xC0)
				return true;
			if (string.Equals(keysName, "OemComma", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(keysName, "OemPeriod", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(keysName, "OemOpenBrackets", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(keysName, "OemCloseBrackets", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(keysName, "OemTilde", StringComparison.OrdinalIgnoreCase))
				return true;
			if (!string.IsNullOrEmpty(scToken)
				&& (scToken.Contains("0x33", StringComparison.OrdinalIgnoreCase)
					|| scToken.Contains("0x34", StringComparison.OrdinalIgnoreCase)
					|| scToken.Contains("0x1A", StringComparison.OrdinalIgnoreCase)
					|| scToken.Contains("0x1B", StringComparison.OrdinalIgnoreCase)))
				return true;
			return false;
		}

		private static string Join(IReadOnlyList<string> items)
		{
			if (items == null || items.Count == 0)
				return string.Empty;
			return string.Join(", ", items);
		}

		private static List<string> ToList(IEnumerable<string> items)
		{
			var list = new List<string>();
			if (items == null)
				return list;
			foreach (string s in items)
				list.Add(s);
			return list;
		}
	}
}
