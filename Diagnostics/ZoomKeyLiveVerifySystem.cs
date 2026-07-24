// Investigation-only live Zoom key verifier. Intentionally NOT behind AK_DIAGNOSTICS
// so in-game Build+Reload always packages it (defines can differ from CLI builds).
// Runtime default is OFF via ForceEnabled (no per-frame file I/O until manually armed).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using AdvancedKeybinds.Binding;
using AdvancedKeybinds.Core;
using Microsoft.Xna.Framework.Input;
using Terraria;
using Terraria.GameInput;
using Terraria.ModLoader;

namespace AdvancedKeybinds.Diagnostics
{
	/// <summary>
	/// Live Zoom In/Out binding verification driven by a request file (investigation only).
	/// Default OFF (no per-frame file I/O). Set <see cref="ForceEnabled"/> to re-arm.
	/// Does not change product behavior except while a case is armed.
	/// </summary>
	public sealed class ZoomKeyLiveVerifySystem : ModSystem
	{
		/// <summary>Manual arm for Zoom live verification. Default false.</summary>
		public static bool ForceEnabled;

		private const string ActionIn = "ViewZoomIn";
		private const string ActionOut = "ViewZoomOut";

		private enum Phase
		{
			Idle,
			Armed,
			Sampling,
			DoneCase
		}

		private static readonly object Gate = new();
		private static Phase _phase = Phase.Idle;
		private static string _label = "";
		private static string _token = "";
		private static string _actionId = ActionIn;
		private static string _sendHint = "";
		private static int _sampleFrames;
		private static int _armCooldown;
		private static bool _sawTrigger;
		private static bool _sawZoomDelta;
		private static float _zoomAtArm;
		private static bool _plain;
		private static bool _advanced;
		private static string _vanillaAssigned = "";
		private static ComboBinding _savedIn;
		private static ComboBinding _savedOut;
		private static bool _haveSaved;
		private static readonly List<string> ResultLines = new();
		private static DateTime _lastRequestWriteUtc = DateTime.MinValue;

		private static string Dir => Path.Combine(Main.SavePath, "AdvancedKeybinds");
		private static string RequestPath => Path.Combine(Dir, "zoom-live-verify-request.txt");
		private static string StatusPath => Path.Combine(Dir, "zoom-live-verify-status.txt");
		private static string ResultsPath => Path.Combine(Dir, "zoom-live-verify-results.txt");

		private static int _heartbeatFrames;

		public override void OnModLoad()
		{
			if (!ForceEnabled)
				return;
			try { WriteStatus("ONMODLOAD"); }
			catch { /* ignore */ }
		}

		public override void PostUpdateEverything()
		{
			TickFromGameLoop("PostUpdateEverything");
		}

		/// <summary>Also called from PlayerInputPatches so verification runs even if ModSystem timing differs.</summary>
		public static void TickFromGameLoop(string source)
		{
			if (!ForceEnabled)
				return;

			try
			{
				_heartbeatFrames++;
				if (_heartbeatFrames >= 60)
				{
					_heartbeatFrames = 0;
					WriteHeartbeat(source);
				}

				// Allow arming even on menu; sampling still records menu/fancy flags.
				PollRequestFile();
				if (!Main.gameMenu)
					TickPhase();
			}
			catch (Exception ex)
			{
				WriteStatus("ERROR " + ex.GetType().Name + ": " + ex.Message + " src=" + source);
			}
		}

		private static string _lastRequestRaw = "";

		private static void PollRequestFile()
		{
			if (!File.Exists(RequestPath))
				return;

			string raw = File.ReadAllText(RequestPath, Encoding.UTF8);
			DateTime writeUtc = File.GetLastWriteTimeUtc(RequestPath);
			// Accept either newer mtime OR changed content (mtime can be coarse / unchanged on some FS).
			if (writeUtc <= _lastRequestWriteUtc && string.Equals(raw, _lastRequestRaw, StringComparison.Ordinal))
				return;

			_lastRequestWriteUtc = writeUtc;
			_lastRequestRaw = raw;
			string line = null;
			foreach (string part in raw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
			{
				string t = part.Trim();
				if (t.Length == 0 || t.StartsWith("#", StringComparison.Ordinal))
					continue;
				line = t;
				break;
			}

			if (string.IsNullOrEmpty(line))
				return;

			if (line.Equals("ABORT", StringComparison.OrdinalIgnoreCase))
			{
				RestoreSaved();
				_phase = Phase.Idle;
				WriteStatus("ABORTED");
				return;
			}

			if (line.Equals("FINISH", StringComparison.OrdinalIgnoreCase))
			{
				RestoreSaved();
				FlushResults();
				_phase = Phase.Idle;
				WriteStatus("FINISHED");
				return;
			}

			if (line.Equals("PRESS", StringComparison.OrdinalIgnoreCase)
				|| line.StartsWith("PRESS\t", StringComparison.OrdinalIgnoreCase)
				|| line.StartsWith("PRESS ", StringComparison.OrdinalIgnoreCase))
			{
				if (_phase == Phase.Armed || _phase == Phase.DoneCase)
				{
					_phase = Phase.Sampling;
					_sampleFrames = 0;
					_sawTrigger = false;
					_sawZoomDelta = false;
					WriteStatus("SAMPLING " + _label);
				}
				return;
			}

			if (line.StartsWith("CASE\t", StringComparison.OrdinalIgnoreCase)
				|| line.StartsWith("CASE ", StringComparison.OrdinalIgnoreCase))
			{
				string[] bits = line.Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
				// CASE label token action sendHint
				if (bits.Length < 4)
				{
					WriteStatus("BAD_CASE " + line);
					return;
				}

				EnsureSavedSnapshot();
				_label = bits[1];
				_token = bits[2];
				_actionId = bits[3];
				_sendHint = bits.Length >= 5 ? bits[4] : "";
				ApplyCaseBinding(_actionId, _token);
				_zoomAtArm = Main.GameZoomTarget;
				_phase = Phase.Armed;
				_armCooldown = 8;
				_sampleFrames = 0;
				_sawTrigger = false;
				_sawZoomDelta = false;
				WriteStatus("ARMED " + _label + " token=" + _token + " action=" + _actionId
					+ " plain=" + _plain + " advanced=" + _advanced
					+ " vanilla=[" + _vanillaAssigned + "]"
					+ " zoom=" + _zoomAtArm.ToString("0.000", CultureInfo.InvariantCulture)
					+ " send=" + _sendHint);
			}
		}

		private static void TickPhase()
		{
			if (_phase == Phase.Armed)
			{
				if (_armCooldown > 0)
					_armCooldown--;
				return;
			}

			if (_phase != Phase.Sampling)
				return;

			_sampleFrames++;
			bool trigger = false;
			if (PlayerInput.Triggers?.Current?.KeyStatus != null
				&& PlayerInput.Triggers.Current.KeyStatus.TryGetValue(_actionId, out bool on))
				trigger = on;

			if (trigger)
				_sawTrigger = true;

			float zoom = Main.GameZoomTarget;
			if (Math.Abs(zoom - _zoomAtArm) > 0.0005f)
				_sawZoomDelta = true;

			// Pass as soon as trigger latches (primary gameplay criterion).
			if (_sawTrigger || _sampleFrames >= 45)
			{
				bool pass = _sawTrigger;
				string verdict = pass ? "PASS" : "FAIL";
				string row = string.Join("\t",
					_label,
					verdict,
					_token,
					_actionId,
					"plain=" + _plain,
					"advanced=" + _advanced,
					"vanilla=[" + _vanillaAssigned + "]",
					"trigger=" + _sawTrigger,
					"zoomDelta=" + _sawZoomDelta,
					"zoom0=" + _zoomAtArm.ToString("0.000", CultureInfo.InvariantCulture),
					"zoom1=" + zoom.ToString("0.000", CultureInfo.InvariantCulture),
					"frames=" + _sampleFrames,
					"menu=" + Main.gameMenu,
					"fancy=" + Main.inFancyUI);
				lock (Gate)
					ResultLines.Add(row);
				AppendResultFile(row);
				WriteStatus("RESULT " + row);
				_phase = Phase.DoneCase;
			}
		}

		private static void EnsureSavedSnapshot()
		{
			if (_haveSaved)
				return;

			_savedIn = CloneOrEmpty(BindingStorage.Get(ActionIn));
			_savedOut = CloneOrEmpty(BindingStorage.Get(ActionOut));
			_haveSaved = true;
		}

		private static ComboBinding CloneOrEmpty(ComboBinding src)
		{
			if (src == null)
				return new ComboBinding();
			return new ComboBinding {
				Slot1 = src.Slot1,
				Slot2 = src.Slot2,
				Slot3 = src.Slot3,
				Slot1Display = src.Slot1Display,
				Slot2Display = src.Slot2Display,
				Slot3Display = src.Slot3Display,
				Hold = src.Hold,
				HoldOnRelease = src.HoldOnRelease,
				HoldMs = src.HoldMs
			};
		}

		private static void ApplyCaseBinding(string actionId, string token)
		{
			var working = new ComboBinding {
				Slot3 = token,
				Hold = false,
				HoldOnRelease = false,
				HoldMs = 0
			};
			_plain = working.IsPlainSingleTap;
			_advanced = working.RequiresAdvancedStorage();

			if (_advanced)
			{
				BindingStorage.Set(actionId, working, "zoom-live-verify-advanced");
			}
			else
			{
				BindingStorage.Set(actionId, working, "zoom-live-verify-plain");
				VanillaRebindHelper.AssignVanillaBinding(actionId, token, "zoom-live-verify-plain");
			}

			BindingInputEngine.InvalidateBindings();
			_vanillaAssigned = VanillaRebindHelper.FormatAssignedKeys(actionId);
		}

		private static void RestoreSaved()
		{
			if (!_haveSaved)
				return;

			RestoreOne(ActionIn, _savedIn);
			RestoreOne(ActionOut, _savedOut);
			BindingInputEngine.InvalidateBindings();
			_haveSaved = false;
		}

		private static void RestoreOne(string actionId, ComboBinding saved)
		{
			if (saved == null || saved.IsEmpty)
			{
				BindingStorage.Set(actionId, new ComboBinding(), "zoom-live-verify-restore-empty");
				VanillaRebindHelper.ClearVanillaBinding(actionId, "zoom-live-verify-restore-empty");
				return;
			}

			if (saved.RequiresAdvancedStorage())
			{
				BindingStorage.Set(actionId, saved, "zoom-live-verify-restore-advanced");
			}
			else
			{
				BindingStorage.Set(actionId, saved, "zoom-live-verify-restore-plain");
				var keys = saved.Keys;
				if (keys.Count == 1 && !KeyToken.IsMouseToken(keys[0]))
					VanillaRebindHelper.AssignVanillaBinding(actionId, keys[0], "zoom-live-verify-restore-plain");
				else
					VanillaRebindHelper.ClearVanillaBinding(actionId, "zoom-live-verify-restore-empty");
			}
		}

		private static void AppendResultFile(string row)
		{
			Directory.CreateDirectory(Dir);
			File.AppendAllText(ResultsPath, row + Environment.NewLine, new UTF8Encoding(true));
		}

		private static void FlushResults()
		{
			Directory.CreateDirectory(Dir);
			var sb = new StringBuilder();
			sb.AppendLine("label\tverdict\ttoken\taction\tmeta...");
			lock (Gate)
			{
				foreach (string line in ResultLines)
					sb.AppendLine(line);
			}
			File.WriteAllText(ResultsPath, sb.ToString(), new UTF8Encoding(true));
		}

		private static void WriteStatus(string text)
		{
			Directory.CreateDirectory(Dir);
			File.WriteAllText(StatusPath, DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + text + Environment.NewLine, new UTF8Encoding(true));
		}

		private static void WriteHeartbeat(string source)
		{
			Directory.CreateDirectory(Dir);
			string path = Path.Combine(Dir, "zoom-live-verify-heartbeat.txt");
			string line = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)
				+ " alive src=" + source
				+ " menu=" + Main.gameMenu
				+ " fancy=" + Main.inFancyUI
				+ " phase=" + _phase
				+ Environment.NewLine;
			File.WriteAllText(path, line, new UTF8Encoding(true));
		}
	}
}
