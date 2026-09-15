// TEMPORARY investigation-only probe. DELETE before public release (after Windows GamePad verify).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using AdvancedKeybinds.Binding;
using AdvancedKeybinds.Core;
using Microsoft.Xna.Framework;
using Terraria.ModLoader;

namespace AdvancedKeybinds.Diagnostics
{
	/// <summary>
	/// TEMPORARY: rotated GamePadProbe_00..04 (10MiB × 5). Never touches legacy LIVE / dated history.
	/// Source of truth for edges: <see cref="FnaGamePadProvider"/>.
	/// </summary>
	public sealed class GamePadProbeSystem : ModSystem
	{
		private static readonly object Gate = new();
		private static GamePadProbeRotatedLogStore _store;
		private static GamePadProbeLogRotationOptions _rotationOptions;
		private static readonly GamePadProbeLogEmitGate EmitGate = new();
		private static bool _writersReady;
		private static bool _connSampled;
		private static bool _lastConnected;
		private static PlayerIndex? _lastActiveIndex;
		private static bool _fileErrorLoggedOnce;
		private static bool _probeErrorLoggedOnce;
		private static readonly List<string> _pressBuf = new(16);
		private static readonly List<string> _releaseBuf = new(16);
		private static string _lastDedupLine;
		private static string _lastCaptureWaitingDedupe;
		private static readonly HashSet<string> _sessionDedupKeys = new(StringComparer.Ordinal);
		/// <summary>When false, no probe files are created or appended (public / disabled).</summary>
		private static bool _loggingEnabled = true;

		private static string GamePadLogDir => Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
			"My Games",
			"Terraria",
			"tModLoader",
			"deck log",
			"GamePad");

		/// <summary>Test hook: replace rotation limits (e.g. tiny byte/gen caps). Null restores product defaults.</summary>
		internal static void ConfigureRotationOptionsForTests(GamePadProbeLogRotationOptions options)
		{
			lock (Gate)
			{
				CloseStoreQuiet();
				_rotationOptions = options;
				_writersReady = false;
			}
		}

		/// <summary>Test hook: disable all probe file I/O.</summary>
		internal static void SetLoggingEnabledForTests(bool enabled)
		{
			lock (Gate)
			{
				_loggingEnabled = enabled;
				if (_store != null)
					_store.SetLoggingDisabled(!enabled);
				if (!enabled)
				{
					CloseStoreQuiet();
					_writersReady = false;
				}
			}
		}

		/// <summary>Test hook: classify emit mode (default Off).</summary>
		internal static void SetClassifyModeForTests(GamePadProbeLogEmitGate.ClassifyMode mode)
		{
			lock (Gate)
			{
				EmitGate.BindingRowClassifyMode = mode;
			}
		}

		public override void OnModLoad()
		{
			ResetRuntimeState(closeWriters: true);
		}

		public override void Unload()
		{
			lock (Gate)
			{
				try
				{
					WriteProbeLine("=== GamePadProbe END (Unload) ===", flushImmediate: true);
					FlushAll();
				}
				catch
				{
					// ignore
				}

				ResetRuntimeState(closeWriters: true);
			}
		}

		/// <summary>
		/// TEMPORARY: after <see cref="FnaGamePadProvider.AdvanceInputTick"/>.
		/// Idle ticks: no string alloc / no file I/O beyond edge buffer fill.
		/// </summary>
		public static void AdvanceProbeFromInputUpdate()
		{
			lock (Gate)
			{
				try
				{
					_pressBuf.Clear();
					_releaseBuf.Clear();
					FnaGamePadProvider.CopyJustPressed(_pressBuf);
					FnaGamePadProvider.CopyJustReleased(_releaseBuf);

					bool connected = FnaGamePadProvider.IsConnected;
					PlayerIndex? active = FnaGamePadProvider.ActivePlayerIndex;
					bool connChanged = false;
					if (_writersReady && _connSampled)
					{
						connChanged = connected != _lastConnected
							|| (connected && active.HasValue && _lastActiveIndex != active);
					}

					bool hasEdges = _pressBuf.Count > 0 || _releaseBuf.Count > 0;
					if (_writersReady && !hasEdges && !connChanged && _connSampled)
						return;

					EnsureWriters();
					if (!_writersReady || _store == null || !_store.IsOpen)
						return;

					if (!_connSampled)
					{
						_connSampled = true;
						_lastConnected = connected;
						_lastActiveIndex = active;
						if (connected && active.HasValue)
							WriteProbeLine(FormatTs() + " | CONNECT    | PlayerIndex." + active.Value, flushImmediate: true);
						// Fall through so same-tick PRESS lines still write.
					}
					else if (connChanged)
					{
						if (!_lastConnected && connected && active.HasValue)
							WriteProbeLine(FormatTs() + " | CONNECT    | PlayerIndex." + active.Value, flushImmediate: true);
						else if (_lastConnected && !connected)
							WriteProbeLine(FormatTs() + " | DISCONNECT | PlayerIndex."
								+ (_lastActiveIndex.HasValue ? _lastActiveIndex.Value.ToString() : "(none)"), flushImmediate: true);
						else if (connected && active.HasValue && _lastActiveIndex != active)
						{
							if (_lastActiveIndex.HasValue)
								WriteProbeLine(FormatTs() + " | DISCONNECT | PlayerIndex." + _lastActiveIndex.Value, flushImmediate: true);
							WriteProbeLine(FormatTs() + " | CONNECT    | PlayerIndex." + active.Value, flushImmediate: true);
						}

						_lastConnected = connected;
						_lastActiveIndex = active;
					}

					string indexLabel = active.HasValue
						? "PlayerIndex." + active.Value
						: "PlayerIndex.(none)";

					for (int i = 0; i < _pressBuf.Count; i++)
						WriteTokenEdge("PRESS  ", indexLabel, _pressBuf[i]);
					for (int i = 0; i < _releaseBuf.Count; i++)
						WriteTokenEdge("RELEASE", indexLabel, _releaseBuf[i]);
				}
				catch (Exception ex)
				{
					LogProbeErrorOnce("AdvanceProbeFromInputUpdate", ex);
				}
			}
		}

		/// <summary>
		/// TEMPORARY: one-line CAPTURE result during purple slot wait.
		/// </summary>
		public static void LogCapture(
			int slotNumber,
			string gamePadToken,
			bool accepted,
			bool purpleCleared,
			string reason = null)
		{
			lock (Gate)
			{
				try
				{
					if (string.IsNullOrEmpty(gamePadToken) || !GamePadToken.IsPadToken(gamePadToken))
						return;

					EnsureWriters();
					if (!_writersReady || _store == null || !_store.IsOpen)
						return;

					if (!GamePadToken.TryCanonicalize(gamePadToken, out string canonical))
						canonical = gamePadToken.Trim();

					string reasonKey = reason ?? "";
					bool releasePhase = reasonKey.IndexOf("waiting-for-release", StringComparison.OrdinalIgnoreCase) >= 0
						|| reasonKey.IndexOf("blocked-edge-during-release", StringComparison.OrdinalIgnoreCase) >= 0
						|| reasonKey.IndexOf("post-recover-waiting-for-release", StringComparison.OrdinalIgnoreCase) >= 0;
					if (!accepted && releasePhase)
					{
						string dedupe = slotNumber + "|" + canonical + "|" + SanitizeReason(reasonKey);
						if (string.Equals(dedupe, _lastCaptureWaitingDedupe, StringComparison.Ordinal))
							return;
						_lastCaptureWaitingDedupe = dedupe;
					}
					else if (accepted || !releasePhase)
					{
						_lastCaptureWaitingDedupe = null;
					}

					var sb = new StringBuilder(128);
					sb.Append(FormatTs());
					sb.Append(" | CAPTURE | Slot=");
					sb.Append(slotNumber >= 0 ? slotNumber.ToString(CultureInfo.InvariantCulture) : "?");
					sb.Append(" | ");
					sb.Append(canonical);
					sb.Append(" | Accepted=");
					sb.Append(accepted ? "True" : "False");
					sb.Append(" | PurpleCleared=");
					sb.Append(purpleCleared ? "True" : "False");
					if (!accepted && !string.IsNullOrEmpty(reason))
					{
						sb.Append(" | Reason=");
						sb.Append(SanitizeReason(reason));
					}

					WriteProbeLine(sb.ToString());
				}
				catch (Exception ex)
				{
					LogProbeErrorOnce("LogCapture", ex);
				}
			}
		}

		/// <summary>TEMPORARY: UI reuse suppress begin (Pad token held after capture).</summary>
		public static void LogUiSuppressBegin(string gamePadToken)
			=> LogUiSuppressLine("UI_SUPPRESS_BEGIN", gamePadToken);

		/// <summary>TEMPORARY: UI reuse suppress end (Pad token released).</summary>
		public static void LogUiSuppressEnd(string gamePadToken)
			=> LogUiSuppressLine("UI_SUPPRESS_END", gamePadToken);

		/// <summary>
		/// TEMPORARY: ComboPanel close path observed while a Pad parent-UI block may apply.
		/// State-change / attempt only — not per-frame.
		/// </summary>
		public static void LogPanelCloseAttempt(string gamePadToken, string source, bool blocked)
		{
			lock (Gate)
			{
				try
				{
					EnsureWriters();
					if (!_writersReady || _store == null || !_store.IsOpen)
						return;

					string tokenLabel = "-";
					if (!string.IsNullOrEmpty(gamePadToken) && GamePadToken.TryCanonicalize(gamePadToken, out string canonical))
						tokenLabel = canonical;

					WriteProbeLine(
						FormatTs()
						+ " | PANEL_CLOSE_ATTEMPT | "
						+ tokenLabel
						+ " | Source="
						+ SanitizeReason(source ?? "?")
						+ " | Blocked="
						+ (blocked ? "True" : "False"));
				}
				catch (Exception ex)
				{
					LogProbeErrorOnce("LogPanelCloseAttempt", ex);
				}
			}
		}

		/// <summary>
		/// TEMPORARY: parent UI trigger cleared for GamePad capture/hold. State-change only.
		/// </summary>
		public static void LogParentUiTrigger(string gamePadToken, string triggerName, bool cleared)
		{
			lock (Gate)
			{
				try
				{
					if (string.IsNullOrEmpty(triggerName))
						return;

					EnsureWriters();
					if (!_writersReady || _store == null || !_store.IsOpen)
						return;

					string tokenLabel = "-";
					if (!string.IsNullOrEmpty(gamePadToken) && GamePadToken.TryCanonicalize(gamePadToken, out string canonical))
						tokenLabel = canonical;

					WriteProbeLine(
						FormatTs()
						+ " | PARENT_UI_TRIGGER | "
						+ tokenLabel
						+ " | Trigger="
						+ SanitizeReason(triggerName)
						+ " | Cleared="
						+ (cleared ? "True" : "False"));
				}
				catch (Exception ex)
				{
					LogProbeErrorOnce("LogParentUiTrigger", ex);
				}
			}
		}

		private static void LogUiSuppressLine(string kind, string gamePadToken)
		{
			lock (Gate)
			{
				try
				{
					if (string.IsNullOrEmpty(gamePadToken) || !GamePadToken.IsPadToken(gamePadToken))
						return;
					if (!GamePadToken.TryCanonicalize(gamePadToken, out string canonical))
						return;

					EnsureWriters();
					if (!_writersReady || _store == null || !_store.IsOpen)
						return;

					WriteProbeLine(FormatTs() + " | " + kind + " | " + canonical);
				}
				catch (Exception ex)
				{
					LogProbeErrorOnce(kind, ex);
				}
			}
		}
		/// <summary>Backward-compatible wrapper for older call sites. </summary>
		public static void LogCaptureEdge(
			string gamePadToken,
			int slotNumber,
			string phaseBefore,
			string phaseAfter,
			bool captureCompleted,
			bool purpleWaitCleared,
			bool keyboardOrMouseAlsoPresent,
			string note)
		{
			_ = phaseBefore;
			_ = phaseAfter;
			_ = keyboardOrMouseAlsoPresent;
			LogCapture(slotNumber, gamePadToken, captureCompleted, purpleWaitCleared, note);
		}

		private static void WriteTokenEdge(string kind, string indexLabel, string token)
		{
			if (!GamePadToken.TryCanonicalize(token, out string canonical))
				return;

			string display = GamePadToken.FormatForDisplay(canonical);
			WriteProbeLine(
				FormatTs() + " | " + kind + " | " + indexLabel + " | " + canonical + " | " + display,
				flushImmediate: true);
		}

		private static string FormatTs()
			=> DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

		private static string SanitizeReason(string reason)
		{
			if (string.IsNullOrEmpty(reason))
				return string.Empty;
			return reason.Replace('\r', ' ').Replace('\n', ' ').Trim();
		}

		private static void EnsureWriters()
		{
			if (!_loggingEnabled)
				return;
			if (_writersReady && _store != null && _store.IsOpen)
				return;

			try
			{
				Directory.CreateDirectory(GamePadLogDir);
				CloseStoreQuiet();
				var options = _rotationOptions ?? new GamePadProbeLogRotationOptions();
				_store = new GamePadProbeRotatedLogStore(GamePadLogDir, options);
				_store.SetLoggingDisabled(!_loggingEnabled);
				_store.EnsureOpen();
				_writersReady = _store.IsOpen;
				if (_writersReady)
				{
					WriteProbeLine(
						"# " + FormatTs()
						+ " GamePadProbe rotated start gen="
						+ _store.CurrentGenerationIndex.ToString("00", CultureInfo.InvariantCulture)
						+ " (legacy LIVE/history not used)",
						flushImmediate: true);
				}
			}
			catch (Exception ex)
			{
				CloseStoreQuiet();
				_writersReady = false;
				LogFileErrorOnce(ex);
			}
		}

		private static void WriteProbeLine(string line, bool flushImmediate = false)
		{
			if (!_loggingEnabled)
				return;
			try
			{
				EnsureWriters();
				if (_store == null || !_store.IsOpen)
					return;

				if (!flushImmediate)
				{
					int pipe = line != null ? line.IndexOf('|') : -1;
					string body = pipe >= 0 ? line.Substring(pipe + 1).TrimStart() : line;
					if (GamePadProbeLogEmitGate.IsImmediateFlushKind(body))
						flushImmediate = true;
				}

				if (!_store.TryWriteLine(line, flushImmediate) && !string.IsNullOrEmpty(_store.LastError))
					LogFileErrorOnce(new IOException(_store.LastError));
			}
			catch (Exception ex)
			{
				LogFileErrorOnce(ex);
			}
		}

		private static void FlushAll()
		{
			try { _store?.Flush(); } catch { /* ignore */ }
		}

		private static void CloseStoreQuiet()
		{
			try { _store?.Dispose(); } catch { /* ignore */ }
			_store = null;
		}

		private static void ResetRuntimeState(bool closeWriters)
		{
			if (closeWriters)
				CloseStoreQuiet();

			_writersReady = false;
			_connSampled = false;
			_lastConnected = false;
			_lastActiveIndex = null;
			_fileErrorLoggedOnce = false;
			_probeErrorLoggedOnce = false;
			_pressBuf.Clear();
			_releaseBuf.Clear();
			_lastDedupLine = null;
			_sessionDedupKeys.Clear();
			EmitGate.Reset();
			EmitGate.BindingRowClassifyMode = GamePadProbeLogEmitGate.ClassifyMode.Off;
		}

		/// <summary>TEMPORARY: controller-tab open / switch (state change only).</summary>
		public static void LogPadTabOpen(Terraria.GameInput.InputMode mode)
		{
			lock (Gate)
			{
				try
				{
					EnsureWriters();
					if (!_writersReady || _store == null || !_store.IsOpen)
						return;
					WriteProbeLine(FormatTs() + " | PAD_TAB_OPEN | InputMode=" + mode);
				}
				catch (Exception ex)
				{
					LogProbeErrorOnce("LogPadTabOpen", ex);
				}
			}
		}

		/// <summary>TEMPORARY: vanilla ListenFor or diverted ComboPanel wait begin.</summary>
		public static void LogPadBindWaitBegin(string action, Terraria.GameInput.InputMode mode, string source)
		{
			lock (Gate)
			{
				try
				{
					EnsureWriters();
					if (!_writersReady || _store == null || !_store.IsOpen)
						return;
					WriteProbeLine(
						FormatTs()
						+ " | PAD_BIND_WAIT_BEGIN | Action="
						+ SanitizeReason(action ?? "?")
						+ " | Mode="
						+ mode
						+ " | Source="
						+ SanitizeReason(source ?? "?"));
				}
				catch (Exception ex)
				{
					LogProbeErrorOnce("LogPadBindWaitBegin", ex);
				}
			}
		}

		public static void LogPadBindWaitEnd(string action, Terraria.GameInput.InputMode mode)
		{
			lock (Gate)
			{
				try
				{
					EnsureWriters();
					if (!_writersReady || _store == null || !_store.IsOpen)
						return;
					WriteProbeLine(
						FormatTs()
						+ " | PAD_BIND_WAIT_END | Action="
						+ SanitizeReason(action ?? "?")
						+ " | Mode="
						+ mode);
				}
				catch (Exception ex)
				{
					LogProbeErrorOnce("LogPadBindWaitEnd", ex);
				}
			}
		}

		public static void LogPadBindAttempt(string action, string padToken, string vanillaValue, Terraria.GameInput.InputMode mode)
		{
			lock (Gate)
			{
				try
				{
					EnsureWriters();
					if (!_writersReady || _store == null || !_store.IsOpen)
						return;
					string pad = padToken;
					if (GamePadToken.TryCanonicalize(padToken, out string c))
						pad = c;
					WriteProbeLine(
						FormatTs()
						+ " | PAD_BIND_ATTEMPT | Action="
						+ SanitizeReason(action ?? "?")
						+ " | "
						+ pad
						+ " | VanillaValue="
						+ SanitizeReason(vanillaValue ?? "?")
						+ " | Mode="
						+ mode);
				}
				catch (Exception ex)
				{
					LogProbeErrorOnce("LogPadBindAttempt", ex);
				}
			}
		}

		/// <param name="accepted">null = observation only (no write).</param>
		public static void LogPadBindResult(
			string action,
			bool? accepted,
			Terraria.GameInput.InputMode storedMode,
			string note)
		{
			lock (Gate)
			{
				try
				{
					EnsureWriters();
					if (!_writersReady || _store == null || !_store.IsOpen)
						return;
					string acc = accepted.HasValue
						? (accepted.Value ? "True" : "False")
						: "Observe";
					WriteProbeLine(
						FormatTs()
						+ " | PAD_BIND_RESULT | Action="
						+ SanitizeReason(action ?? "?")
						+ " | Accepted="
						+ acc
						+ " | StoredMode="
						+ storedMode
						+ (string.IsNullOrEmpty(note) ? string.Empty : " | Note=" + SanitizeReason(note)));
				}
				catch (Exception ex)
				{
					LogProbeErrorOnce("LogPadBindResult", ex);
				}
			}
		}

		public static void LogPanelPadNavBegin(ComboPanelPadFocus focus, string openToken)
			=> WriteProbeLineSafe(FormatTs() + " | PANEL_PAD_NAV_BEGIN | Focus=" + focus + " | OpenToken=" + (openToken ?? "-"));

		public static void LogPanelPadOpen(ComboPanelPadFocus focus, string openToken)
			=> WriteProbeLineSafe(FormatTs() + " | PANEL_PAD_OPEN | Focus=" + focus + " | OpenToken=" + (openToken ?? "-"));

		public static void LogPanelOpenSource(string source, bool displayNameEnabled)
			=> WriteProbeLineSafe(
				FormatTs() + " | PANEL_OPEN_SOURCE | Source=" + SanitizeReason(source ?? "?")
				+ " | DisplayNameEnabled=" + (displayNameEnabled ? "True" : "False"));

		public static void LogPanelMouseSessionOpen(string openSource, bool mouseInputEnabled, bool displayNameEnabled)
			=> WriteProbeLineSafe(
				FormatTs() + " | PANEL_MOUSE_SESSION_OPEN | OpenSource=" + SanitizeReason(openSource ?? "?")
				+ " | MouseInputEnabled=" + (mouseInputEnabled ? "True" : "False")
				+ " | DisplayNameEnabled=" + (displayNameEnabled ? "True" : "False"));

		public static void LogPanelMouseLeftPress(
			string openSource,
			int mouseX,
			int mouseY,
			bool insidePanel,
			string hoveredElement,
			string consumedBy)
			=> WriteProbeLineSafe(
				FormatTs() + " | PANEL_MOUSE_LEFT_PRESS | OpenSource=" + SanitizeReason(openSource ?? "?")
				+ " | MouseX=" + mouseX.ToString(CultureInfo.InvariantCulture)
				+ " | MouseY=" + mouseY.ToString(CultureInfo.InvariantCulture)
				+ " | InsidePanel=" + (insidePanel ? "True" : "False")
				+ " | HoveredElement=" + SanitizeReason(hoveredElement ?? "?")
				+ " | ConsumedBy=" + SanitizeReason(consumedBy ?? "?"));

		public static void LogPanelMouseSaveClick(
			string openSource,
			bool insideSaveButton,
			bool saveInvoked,
			bool closeInvoked,
			bool returnToControls,
			bool inputConsumed)
			=> WriteProbeLineSafe(
				FormatTs() + " | PANEL_MOUSE_SAVE_CLICK | OpenSource=" + SanitizeReason(openSource ?? "?")
				+ " | InsideSaveButton=" + (insideSaveButton ? "True" : "False")
				+ " | SaveInvoked=" + (saveInvoked ? "True" : "False")
				+ " | CloseInvoked=" + (closeInvoked ? "True" : "False")
				+ " | ReturnToControls=" + (returnToControls ? "True" : "False")
				+ " | InputConsumed=" + (inputConsumed ? "True" : "False"));

		public static void LogPanelOutsideClick(
			string openSource,
			bool insidePanel,
			bool saveInvoked,
			bool closeInvoked,
			bool returnToControls,
			string restoreAction,
			bool inputConsumed)
			=> WriteProbeLineSafe(
				FormatTs() + " | PANEL_OUTSIDE_CLICK | OpenSource=" + SanitizeReason(openSource ?? "?")
				+ " | InsidePanel=" + (insidePanel ? "True" : "False")
				+ " | SaveInvoked=" + (saveInvoked ? "True" : "False")
				+ " | CloseInvoked=" + (closeInvoked ? "True" : "False")
				+ " | ReturnToControls=" + (returnToControls ? "True" : "False")
				+ " | RestoreAction=" + SanitizeReason(restoreAction ?? "?")
				+ " | InputConsumed=" + (inputConsumed ? "True" : "False"));

		public static void LogPanelMouseBlocked(string openSource, string target, string reason)
			=> WriteProbeLineSafe(
				FormatTs() + " | PANEL_MOUSE_BLOCKED | OpenSource=" + SanitizeReason(openSource ?? "?")
				+ " | Target=" + SanitizeReason(target ?? "?")
				+ " | Reason=" + SanitizeReason(reason ?? "Unknown"));

		public static void LogPanelDisplayNameDisabled(
			string reason,
			bool noticeVisible,
			bool navigationRegistered,
			bool mouseClickEnabled)
			=> WriteProbeLineSafe(
				FormatTs() + " | PANEL_DISPLAY_NAME_DISABLED | Reason=" + SanitizeReason(reason ?? "?")
				+ " | NoticeVisible=" + (noticeVisible ? "True" : "False")
				+ " | NavigationRegistered=" + (navigationRegistered ? "True" : "False")
				+ " | MouseClickEnabled=" + (mouseClickEnabled ? "True" : "False"));

		public static void LogPanelDisplayNameEnabled(
			string source,
			bool noticeVisible,
			bool navigationRegistered)
			=> WriteProbeLineSafe(
				FormatTs() + " | PANEL_DISPLAY_NAME_ENABLED | Source=" + SanitizeReason(source ?? "?")
				+ " | NoticeVisible=" + (noticeVisible ? "True" : "False")
				+ " | NavigationRegistered=" + (navigationRegistered ? "True" : "False"));

		public static void LogPanelDisplayNameBlocked(string source, string input)
			=> WriteProbeLineSafe(
				FormatTs() + " | PANEL_DISPLAY_NAME_BLOCKED | Source=" + SanitizeReason(source ?? "?")
				+ " | Input=" + SanitizeReason(input ?? "Other")
				+ " | Opened=False");

		public static void LogPanelOpenSourceClear(string previousSource, string reason)
			=> WriteProbeLineSafe(
				FormatTs() + " | PANEL_OPEN_SOURCE_CLEAR | PreviousSource=" + SanitizeReason(previousSource ?? "?")
				+ " | Reason=" + SanitizeReason(reason ?? "?"));

		public static void LogPanelPadOpenGateEnd(string token)
			=> WriteProbeLineSafe(FormatTs() + " | PANEL_PAD_OPEN_GATE_END | Token=" + (token ?? "-"));

		public static void LogPanelPadFocus(ComboPanelPadFocus from, ComboPanelPadFocus to, string input)
			=> WriteProbeLineSafe(FormatTs() + " | PANEL_PAD_FOCUS | From=" + from + " | To=" + to + " | Input=" + SanitizeReason(input ?? "?"));

		public static void LogPanelPadConfirm(ComboPanelPadFocus focus)
			=> WriteProbeLineSafe(FormatTs() + " | PANEL_PAD_CONFIRM | Focus=" + focus);

		public static void LogPanelPadCaptureBegin(int slot)
			=> WriteProbeLineSafe(FormatTs() + " | PANEL_PAD_CAPTURE_BEGIN | Slot=" + slot.ToString(CultureInfo.InvariantCulture));

		public static void LogPanelPadCaptureEnd(int slot, string token, bool accepted, string note = null)
		{
			var sb = new StringBuilder(96);
			sb.Append(FormatTs());
			sb.Append(" | PANEL_PAD_CAPTURE_END | Slot=");
			sb.Append(slot.ToString(CultureInfo.InvariantCulture));
			sb.Append(" | Token=");
			sb.Append(string.IsNullOrEmpty(token) ? "-" : token);
			sb.Append(" | Accepted=");
			sb.Append(accepted ? "True" : "False");
			if (!string.IsNullOrEmpty(note))
			{
				sb.Append(" | Note=");
				sb.Append(SanitizeReason(note));
			}
			WriteProbeLineSafe(sb.ToString());
		}

		public static void LogPanelPadCaptureConsumeBegin(string token, int slot, bool accepted)
			=> WriteProbeLineSafe(
				FormatTs() + " | PANEL_PAD_CAPTURE_CONSUME_BEGIN | Token=" + (token ?? "-")
				+ " | Slot=" + slot.ToString(CultureInfo.InvariantCulture)
				+ " | Accepted=" + (accepted ? "True" : "False")
				+ " | Epoch=" + FnaGamePadProvider.InputTickEpoch.ToString(CultureInfo.InvariantCulture));

		public static void LogPanelPadNavSkipped(string token, string reason)
			=> WriteProbeLineSafe(
				FormatTs() + " | PANEL_PAD_NAV_SKIPPED | Token=" + (token ?? "-")
				+ " | Reason=" + SanitizeReason(reason ?? "?"));

		public static void LogPanelPadCaptureReleaseGateEnd(string token)
			=> WriteProbeLineSafe(
				FormatTs() + " | PANEL_PAD_CAPTURE_RELEASE_GATE_END | Token=" + (token ?? "-"));

		public static void LogPanelPadNumberEditBegin(int value)
			=> WriteProbeLineSafe(FormatTs() + " | PANEL_PAD_NUMBER_EDIT_BEGIN | Value=" + value.ToString(CultureInfo.InvariantCulture));

		public static void LogPanelPadNumberChange(int from, int to, string input)
			=> WriteProbeLineSafe(
				FormatTs() + " | PANEL_PAD_NUMBER_CHANGE | From=" + from.ToString(CultureInfo.InvariantCulture)
				+ " | To=" + to.ToString(CultureInfo.InvariantCulture) + " | Input=" + SanitizeReason(input ?? "?"));

		public static void LogPanelPadNumberEditEnd(int value, bool committed)
			=> WriteProbeLineSafe(
				FormatTs() + " | PANEL_PAD_NUMBER_EDIT_END | Value=" + value.ToString(CultureInfo.InvariantCulture)
				+ " | Committed=" + (committed ? "True" : "False"));

		public static void LogPanelPadClearConfirmBegin()
			=> LogPanelPadClearConfirmBegin(ComboPanelPadFocus.Clear);

		public static void LogPanelPadClearConfirmBegin(ComboPanelPadFocus focus)
			=> WriteProbeLineSafe(FormatTs() + " | PANEL_PAD_CLEAR_CONFIRM_BEGIN | Focus=" + focus);

		public static void LogPanelPadClearConfirmEnd(bool confirmed)
			=> LogPanelPadClearConfirmEnd(confirmed, ComboPanelPadFocus.Clear);

		public static void LogPanelPadClearConfirmEnd(bool confirmed, ComboPanelPadFocus focus)
			=> WriteProbeLineSafe(FormatTs() + " | PANEL_PAD_CLEAR_CONFIRM_END | Confirmed=" + (confirmed ? "True" : "False") + " | Focus=" + focus);

		public static void LogPanelClearConfirmArm(string input, bool bindingChanged, int epoch)
			=> WriteProbeLineSafe(
				FormatTs() + " | PANEL_CLEAR_CONFIRM_ARM | Input=" + SanitizeReason(input ?? "?")
				+ " | BindingChanged=" + (bindingChanged ? "True" : "False")
				+ " | Epoch=" + epoch.ToString(CultureInfo.InvariantCulture));

		public static void LogPanelClearConfirmReleaseReady(int epoch)
			=> WriteProbeLineSafe(
				FormatTs() + " | PANEL_CLEAR_CONFIRM_RELEASE_READY | Epoch="
				+ epoch.ToString(CultureInfo.InvariantCulture));

		public static void LogPanelClearConfirmCommit(string input, bool bindingChanged, int epoch)
			=> WriteProbeLineSafe(
				FormatTs() + " | PANEL_CLEAR_CONFIRM_COMMIT | Input=" + SanitizeReason(input ?? "?")
				+ " | BindingChanged=" + (bindingChanged ? "True" : "False")
				+ " | Epoch=" + epoch.ToString(CultureInfo.InvariantCulture));

		public static void LogPanelClearConfirmCancel(string input)
			=> WriteProbeLineSafe(
				FormatTs() + " | PANEL_CLEAR_CONFIRM_CANCEL | Input=" + SanitizeReason(input ?? "?")
				+ " | BindingChanged=False");

		public static void LogPanelClearDoubleUseBlocked(string token)
			=> WriteProbeLineSafe(
				FormatTs() + " | PANEL_CLEAR_DOUBLE_USE_BLOCKED | Token=" + SanitizeReason(token ?? "?"));

		public static void LogPanelClearChangedBeforeCommit(string reason)
			=> WriteProbeLineSafe(
				FormatTs() + " | PANEL_CLEAR_CHANGED_BEFORE_COMMIT | Reason=" + SanitizeReason(reason ?? "?"));

		public static void LogPanelPadUnexpectedCaptureBegin(ComboPanelPadFocus focus)
			=> WriteProbeLineSafe(FormatTs() + " | PANEL_PAD_UNEXPECTED_CAPTURE_BEGIN | Focus=" + focus);

		public static void LogPanelPadSave()
			=> WriteProbeLineSafe(FormatTs() + " | PANEL_PAD_SAVE");

		public static void LogPanelSaveCloseBegin(string action, string host, string focus, int epoch)
			=> WriteProbeLineSafe(
				FormatTs() + " | PANEL_SAVE_CLOSE_BEGIN | Action=" + SanitizeReason(action ?? "?")
				+ " | Host=" + SanitizeReason(host ?? "?")
				+ " | Focus=" + SanitizeReason(focus ?? "?")
				+ " | Epoch=" + epoch.ToString(CultureInfo.InvariantCulture));

		public static void LogPanelSaveCloseEnd(
			bool isOpen,
			bool navigationActive,
			bool actionCleared,
			bool parentControlsOpen,
			int parentPoint)
			=> WriteProbeLineSafe(
				FormatTs() + " | PANEL_SAVE_CLOSE_END | IsOpen=" + (isOpen ? "True" : "False")
				+ " | NavigationActive=" + (navigationActive ? "True" : "False")
				+ " | ActionCleared=" + (actionCleared ? "True" : "False")
				+ " | ParentControlsOpen=" + (parentControlsOpen ? "True" : "False")
				+ " | ParentPoint=" + parentPoint.ToString(CultureInfo.InvariantCulture));

		public static void LogPanelOpenAttempt(
			string action,
			string source,
			bool padAJustPressed,
			bool padADown,
			bool closeConsumeGate,
			int epoch)
			=> WriteProbeLineSafe(
				FormatTs() + " | PANEL_OPEN_ATTEMPT | Action=" + SanitizeReason(action ?? "?")
				+ " | Source=" + SanitizeReason(source ?? "?")
				+ " | PadAJustPressed=" + (padAJustPressed ? "True" : "False")
				+ " | PadADown=" + (padADown ? "True" : "False")
				+ " | CloseConsumeGate=" + (closeConsumeGate ? "True" : "False")
				+ " | Epoch=" + epoch.ToString(CultureInfo.InvariantCulture));

		public static void LogPanelOpenBlocked(string action, string reason)
			=> WriteProbeLineSafe(
				FormatTs() + " | PANEL_OPEN_BLOCKED | Action=" + SanitizeReason(action ?? "?")
				+ " | Reason=" + SanitizeReason(reason ?? "?"));

		public static void LogPanelRowConfirmPendingBegin(string host, int point, string action, int epoch)
			=> WriteProbeLineSafe(
				FormatTs() + " | PANEL_ROW_CONFIRM_PENDING_BEGIN | Host=" + SanitizeReason(host ?? "?")
				+ " | Point=" + point.ToString(CultureInfo.InvariantCulture)
				+ " | Action=" + SanitizeReason(action ?? "?")
				+ " | Epoch=" + epoch.ToString(CultureInfo.InvariantCulture));

		public static void LogPanelRowConfirmPendingConsume(string host, int point, string action, int epochDelta)
			=> WriteProbeLineSafe(
				FormatTs() + " | PANEL_ROW_CONFIRM_PENDING_CONSUME | Host=" + SanitizeReason(host ?? "?")
				+ " | Point=" + point.ToString(CultureInfo.InvariantCulture)
				+ " | Action=" + SanitizeReason(action ?? "?")
				+ " | EpochDelta=" + epochDelta.ToString(CultureInfo.InvariantCulture));

		public static void LogPanelRowConfirmPendingReject(
			int expectedPoint,
			int actualPoint,
			string expectedAction,
			string actualAction,
			string reason)
			=> WriteProbeLineSafe(
				FormatTs() + " | PANEL_ROW_CONFIRM_PENDING_REJECT | ExpectedPoint=" + expectedPoint.ToString(CultureInfo.InvariantCulture)
				+ " | ActualPoint=" + actualPoint.ToString(CultureInfo.InvariantCulture)
				+ " | ExpectedAction=" + SanitizeReason(expectedAction ?? "?")
				+ " | ActualAction=" + SanitizeReason(actualAction ?? "?")
				+ " | Reason=" + SanitizeReason(reason ?? "?"));

		public static void LogPanelRowConfirmPendingExpired(string action, int ageEpochs)
			=> WriteProbeLineSafe(
				FormatTs() + " | PANEL_ROW_CONFIRM_PENDING_EXPIRED | Action=" + SanitizeReason(action ?? "?")
				+ " | AgeEpochs=" + ageEpochs.ToString(CultureInfo.InvariantCulture));

		public static void LogPanelRowConfirmPendingNotArmed(string reason)
		{
			string key = SanitizeReason(reason ?? "?");
			lock (Gate)
			{
				if (!EmitGate.ShouldEmitPanelRowConfirmPendingNotArmed(key))
					return;
				WriteProbeLineSafeUnlocked(
					FormatTs() + " | PANEL_ROW_CONFIRM_PENDING_NOT_ARMED | Reason=" + key);
			}
		}

		/// <summary>Temporary: Pad:A PRESS poll for Pending arm (state-change only).</summary>
		public static void LogPanelRowConfirmPoll(
			int currentPoint,
			string rowKind,
			string action,
			bool armAllowed,
			string reason)
		{
			string stateKey = currentPoint.ToString(CultureInfo.InvariantCulture)
				+ "|" + (rowKind ?? "")
				+ "|" + (action ?? "")
				+ "|" + (armAllowed ? "1" : "0")
				+ "|" + (reason ?? "");
			lock (Gate)
			{
				if (!EmitGate.ShouldEmitPanelRowConfirmPoll(stateKey))
					return;
				WriteProbeLineSafeUnlocked(
					FormatTs() + " | PANEL_ROW_CONFIRM_POLL | CurrentPoint=" + currentPoint.ToString(CultureInfo.InvariantCulture)
					+ " | RowKind=" + SanitizeReason(rowKind ?? "?")
					+ " | Action=" + SanitizeReason(action ?? "?")
					+ " | ArmAllowed=" + (armAllowed ? "True" : "False")
					+ " | Reason=" + SanitizeReason(reason ?? "?"));
			}
		}
		/// <summary>Temporary: keybind row OnClick hook entry.</summary>
		public static void LogKeybindRowClickHookEnter(
			string elementType,
			bool mousePhysical,
			bool padAJustPressed,
			int currentPoint,
			bool manualScrollActive)
			=> WriteProbeLineSafe(
				FormatTs() + " | KEYBIND_ROW_CLICK_HOOK_ENTER | ElementType=" + SanitizeReason(elementType ?? "?")
				+ " | MousePhysical=" + (mousePhysical ? "True" : "False")
				+ " | PadAJustPressed=" + (padAJustPressed ? "True" : "False")
				+ " | CurrentPoint=" + currentPoint.ToString(CultureInfo.InvariantCulture)
				+ " | ManualScrollActive=" + (manualScrollActive ? "True" : "False"));

		/// <summary>Temporary: actionId resolve for keybind row click.</summary>
		public static void LogKeybindRowActionResolve(bool success, string action, string reason)
			=> WriteProbeLineSafe(
				success
					? FormatTs() + " | KEYBIND_ROW_ACTION_RESOLVE | Success=True | Action=" + SanitizeReason(action ?? "?")
					: FormatTs() + " | KEYBIND_ROW_ACTION_RESOLVE | Success=False | Reason=" + SanitizeReason(reason ?? "?"));

		/// <summary>Temporary: entering Open intent resolve.</summary>
		public static void LogKeybindRowOpenResolveEnter(string action)
			=> WriteProbeLineSafe(
				FormatTs() + " | KEYBIND_ROW_OPEN_RESOLVE_ENTER | Action=" + SanitizeReason(action ?? "?"));

		/// <summary>Temporary: early return before ComboPanel.Open.</summary>
		public static void LogKeybindRowOpenEarlyReturn(string reason)
			=> WriteProbeLineSafe(
				FormatTs() + " | KEYBIND_ROW_OPEN_EARLY_RETURN | Reason=" + SanitizeReason(reason ?? "?"));

		public static void LogPanelUnexpectedReopen(string action, string previousCloseReason, int epochDelta)
			=> WriteProbeLineSafe(
				FormatTs() + " | PANEL_UNEXPECTED_REOPEN | Action=" + SanitizeReason(action ?? "?")
				+ " | PreviousCloseReason=" + SanitizeReason(previousCloseReason ?? "?")
				+ " | EpochDelta=" + epochDelta.ToString(CultureInfo.InvariantCulture));

		public static void LogPanelSaveContext(
			string host,
			string action,
			int parentPointBefore,
			int parentPointAfter,
			bool reopened)
			=> WriteProbeLineSafe(
				FormatTs() + " | PANEL_SAVE_CONTEXT | Host=" + SanitizeReason(host ?? "?")
				+ " | Action=" + SanitizeReason(action ?? "?")
				+ " | ParentPointBefore=" + parentPointBefore.ToString(CultureInfo.InvariantCulture)
				+ " | ParentPointAfter=" + parentPointAfter.ToString(CultureInfo.InvariantCulture)
				+ " | Reopened=" + (reopened ? "True" : "False"));

		public static void LogPanelPadCancel(string source)
			=> WriteProbeLineSafe(FormatTs() + " | PANEL_PAD_CANCEL | Source=" + SanitizeReason(source ?? "?"));

		public static void LogPanelCancelDetail(
			bool pressed,
			bool captureActive,
			string comboBefore,
			string comboAfter,
			bool closeBlockedByCaptureGate)
			=> WriteProbeLineSafe(
				FormatTs() + " | PANEL_CANCEL | Pressed=" + (pressed ? "True" : "False")
				+ " | CaptureActive=" + (captureActive ? "True" : "False")
				+ " | ComboBefore=" + SanitizeReason(comboBefore ?? "?")
				+ " | ComboAfter=" + SanitizeReason(comboAfter ?? "?")
				+ " | CloseBlockedByCaptureGate=" + (closeBlockedByCaptureGate ? "True" : "False"));

		public static void LogCaptureBeginDiag(int captureId, string combo)
			=> WriteProbeLineSafe(
				FormatTs() + " | CAPTURE_BEGIN | CaptureId=" + captureId.ToString(CultureInfo.InvariantCulture)
				+ " | Combo=" + SanitizeReason(combo ?? "?"));

		public static void LogCaptureTokenDiag(string device, string token, string source)
			=> WriteProbeLineSafe(
				FormatTs() + " | CAPTURE_TOKEN | Device=" + SanitizeReason(device ?? "?")
				+ " | Token=" + SanitizeReason(token ?? "?")
				+ " | Source=" + SanitizeReason(source ?? "?"));

		public static void LogPanelCloseComboDiag(string reason, string comboBeforeCommit)
			=> WriteProbeLineSafe(
				FormatTs() + " | PANEL_CLOSE | Reason=" + SanitizeReason(reason ?? "?")
				+ " | ComboBeforeCommit=" + SanitizeReason(comboBeforeCommit ?? "?"));

		public static void LogCommitBeginDiag(string combo)
			=> WriteProbeLineSafe(
				FormatTs() + " | COMMIT_BEGIN | Combo=" + SanitizeReason(combo ?? "?"));

		public static void LogSaveBindingDiag(string action, string combo)
			=> WriteProbeLineSafe(
				FormatTs() + " | SAVE_BINDING | Action=" + SanitizeReason(action ?? "?")
				+ " | Combo=" + SanitizeReason(combo ?? "?"));

		public static void LogComboChangedDiag(string before, string after, string reason)
			=> WriteProbeLineSafe(
				FormatTs() + " | COMBO_CHANGED | Before=" + SanitizeReason(before ?? "?")
				+ " | After=" + SanitizeReason(after ?? "?")
				+ " | Reason=" + SanitizeReason(reason ?? "?"));

		public static void LogControlNavMove(string from, string to, string direction)
			=> WriteProbeLineSafe(
				FormatTs() + " | CONTROL_NAV_MOVE | From=" + SanitizeReason(from ?? "?")
				+ " | To=" + SanitizeReason(to ?? "?")
				+ " | Direction=" + SanitizeReason(direction ?? "?"));

		public static void LogControlRowCatalog(int totalSnap, int navigable, int collapsedChildrenSkipped)
			=> WriteProbeLineSafe(
				FormatTs() + " | CONTROL_ROW_CATALOG | TotalSnap=" + totalSnap.ToString(CultureInfo.InvariantCulture)
				+ " | Navigable=" + navigable.ToString(CultureInfo.InvariantCulture)
				+ " | CollapsedChildrenSkipped=" + collapsedChildrenSkipped.ToString(CultureInfo.InvariantCulture));

		public static void LogControlRowMapSummary(
			int totalSnap,
			int elementMapCount,
			int mapped,
			int unmapped,
			int hasDimensions,
			int navigable)
			=> WriteProbeLineSafe(
				FormatTs() + " | CONTROL_ROW_MAP_SUMMARY | TotalSnap=" + totalSnap.ToString(CultureInfo.InvariantCulture)
				+ " | ElementMapCount=" + elementMapCount.ToString(CultureInfo.InvariantCulture)
				+ " | Mapped=" + mapped.ToString(CultureInfo.InvariantCulture)
				+ " | Unmapped=" + unmapped.ToString(CultureInfo.InvariantCulture)
				+ " | HasDimensions=" + hasDimensions.ToString(CultureInfo.InvariantCulture)
				+ " | Navigable=" + navigable.ToString(CultureInfo.InvariantCulture));

		public static void LogControlRowMapSample(string fields)
			=> WriteProbeLineSafe(FormatTs() + " | CONTROL_ROW_MAP_SAMPLE | " + (fields ?? "-"));

		public static void LogControlRowFailsafe(string reason, int totalSnap, string action)
			=> WriteProbeLineSafe(
				FormatTs() + " | CONTROL_ROW_FAILSAFE | Reason=" + SanitizeReason(reason ?? "?")
				+ " | TotalSnap=" + totalSnap.ToString(CultureInfo.InvariantCulture)
				+ " | Action=" + SanitizeReason(action ?? "?"));

		public static void LogControlNavCenterScroll(
			string target,
			float oldView,
			float newView,
			bool clamped,
			string clamp)
			=> WriteProbeLineSafe(
				FormatTs() + " | CONTROL_NAV_CENTER_SCROLL | Target=" + SanitizeReason(target ?? "?")
				+ " | Old=" + oldView.ToString("0.#", CultureInfo.InvariantCulture)
				+ " | New=" + newView.ToString("0.#", CultureInfo.InvariantCulture)
				+ " | Clamped=" + (clamped ? "True" : "False")
				+ (clamped && !string.IsNullOrEmpty(clamp) ? " | Clamp=" + SanitizeReason(clamp) : string.Empty));

		public static void LogControlAccordionChildSkipped(string parent, string child, string reason)
			=> WriteProbeLineSafe(
				FormatTs() + " | CONTROL_ACCORDION_CHILD_SKIPPED | Parent=" + SanitizeReason(parent ?? "?")
				+ " | Child=" + SanitizeReason(child ?? "?")
				+ " | Reason=" + SanitizeReason(reason ?? "?"));

		public static void LogControlSectionHeaderSample(
			string stableId,
			string elementType,
			string snapName,
			bool isNavigable,
			bool isHorizontalOnly,
			bool? expanded)
			=> WriteProbeLineSafe(
				FormatTs() + " | CONTROL_SECTION_HEADER_SAMPLE | StableId=" + SanitizeReason(stableId ?? "?")
				+ " | ElementType=" + SanitizeReason(elementType ?? "-")
				+ " | SnapName=" + SanitizeReason(snapName ?? "-")
				+ " | IsNavigable=" + (isNavigable ? "True" : "False")
				+ " | IsHorizontalOnly=" + (isHorizontalOnly ? "True" : "False")
				+ " | Expanded=" + (expanded.HasValue ? (expanded.Value ? "True" : "False") : "?"));

		public static void LogControlSectionHeaderSkipped(string stableId, string reason)
			=> WriteProbeLineSafe(
				FormatTs() + " | CONTROL_SECTION_HEADER_SKIPPED | StableId=" + SanitizeReason(stableId ?? "?")
				+ " | Reason=" + SanitizeReason(reason ?? "?"));

		public static void LogControlPadBHoldBegin(int thresholdMs, string host = null)
			=> WriteProbeLineSafe(
				FormatTs() + " | CONTROL_PAD_B_HOLD_BEGIN | ThresholdMs="
				+ thresholdMs.ToString(CultureInfo.InvariantCulture)
				+ " | Host=" + SanitizeReason(host ?? "?"));

		public static void LogControlPadBHoldTrigger(long elapsedMs, string target, int pointId = int.MinValue)
		{
			string line = FormatTs() + " | CONTROL_PAD_B_HOLD_TRIGGER | ElapsedMs="
				+ elapsedMs.ToString(CultureInfo.InvariantCulture)
				+ " | Target=" + SanitizeReason(target ?? "?");
			if (pointId != int.MinValue)
				line += " | PointId=" + pointId.ToString(CultureInfo.InvariantCulture);
			WriteProbeLineSafe(line);
		}

		public static void LogControlPadBHoldEnd(bool triggered, long? elapsedMs)
		{
			if (triggered)
			{
				WriteProbeLineSafe(FormatTs() + " | CONTROL_PAD_B_HOLD_END | Triggered=True");
				return;
			}

			WriteProbeLineSafe(
				FormatTs() + " | CONTROL_PAD_B_HOLD_END | Triggered=False | ElapsedMs="
				+ (elapsedMs.HasValue ? elapsedMs.Value.ToString(CultureInfo.InvariantCulture) : "-"));
		}

		public static void LogControlPadBHoldUnavailable(string reason)
			=> WriteProbeLineSafe(
				FormatTs() + " | CONTROL_PAD_B_HOLD_UNAVAILABLE | Reason=" + SanitizeReason(reason ?? "?"));

		public static void LogPanelLeftStickNav(
			float rawY,
			float normalizedY,
			string logicalDirection,
			string currentFocus,
			string targetFocus)
			=> WriteProbeLineSafe(
				FormatTs() + " | PANEL_LEFT_STICK_NAV | RawY=" + rawY.ToString("0.###", CultureInfo.InvariantCulture)
				+ " | NormalizedY=" + normalizedY.ToString("0.###", CultureInfo.InvariantCulture)
				+ " | LogicalDirection=" + SanitizeReason(logicalDirection ?? "?")
				+ " | CurrentFocus=" + SanitizeReason(currentFocus ?? "?")
				+ " | TargetFocus=" + SanitizeReason(targetFocus ?? "-"));

		public static void LogPanelLeftStickDirectionMismatch(float rawY, string expected, string actual)
			=> WriteProbeLineSafe(
				FormatTs() + " | PANEL_LEFT_STICK_DIRECTION_MISMATCH | RawY="
				+ rawY.ToString("0.###", CultureInfo.InvariantCulture)
				+ " | Expected=" + SanitizeReason(expected ?? "?")
				+ " | Actual=" + SanitizeReason(actual ?? "?"));

		public static void LogPanelRowActionIdentity(
			int point,
			string focusedAction,
			string pendingAction,
			string clickAction,
			bool matched)
			=> WriteProbeLineSafe(
				FormatTs() + " | PANEL_ROW_ACTION_IDENTITY | Point=" + point.ToString(CultureInfo.InvariantCulture)
				+ " | FocusedAction=" + SanitizeReason(focusedAction ?? "?")
				+ " | PendingAction=" + SanitizeReason(pendingAction ?? "?")
				+ " | ClickAction=" + SanitizeReason(clickAction ?? "?")
				+ " | Matched=" + (matched ? "True" : "False"));

		public static void LogPanelRowActionIdentityMismatch(
			int point,
			string focusedAction,
			string clickAction,
			bool allowed)
			=> WriteProbeLineSafe(
				FormatTs() + " | PANEL_ROW_ACTION_IDENTITY_MISMATCH | Point="
				+ point.ToString(CultureInfo.InvariantCulture)
				+ " | FocusedAction=" + SanitizeReason(focusedAction ?? "?")
				+ " | ClickAction=" + SanitizeReason(clickAction ?? "?")
				+ " | Allowed=" + (allowed ? "True" : "False"));

		public static void LogIngameTriggerNavInput(
			string token,
			bool mainGameMenu,
			bool playerInventory,
			bool comboPanelOpen,
			bool controlsOpen,
			string inputMode,
			int currentPoint,
			bool navigatorInUse)
			=> WriteProbeLineSafe(
				FormatTs() + " | INGAME_TRIGGER_NAV_INPUT | Token=" + SanitizeReason(token ?? "?")
				+ " | MainGameMenu=" + (mainGameMenu ? "True" : "False")
				+ " | PlayerInventory=" + (playerInventory ? "True" : "False")
				+ " | ComboPanelOpen=" + (comboPanelOpen ? "True" : "False")
				+ " | ControlsOpen=" + (controlsOpen ? "True" : "False")
				+ " | InputMode=" + SanitizeReason(inputMode ?? "?")
				+ " | CurrentPoint=" + currentPoint.ToString(CultureInfo.InvariantCulture)
				+ " | NavigatorInUse=" + (navigatorInUse ? "True" : "False"));

		public static void LogIngameTriggerNavState(
			string token,
			bool menuLeft,
			bool menuRight,
			bool hotbarMinus,
			bool hotbarPlus,
			bool mouseLeft,
			bool mouseRight,
			bool allowGamepadInstructions,
			bool gamepadAllowScrolling)
			=> WriteProbeLineSafe(
				FormatTs() + " | INGAME_TRIGGER_NAV_STATE | Token=" + SanitizeReason(token ?? "?")
				+ " | MenuLeft=" + (menuLeft ? "True" : "False")
				+ " | MenuRight=" + (menuRight ? "True" : "False")
				+ " | HotbarMinus=" + (hotbarMinus ? "True" : "False")
				+ " | HotbarPlus=" + (hotbarPlus ? "True" : "False")
				+ " | MouseLeft=" + (mouseLeft ? "True" : "False")
				+ " | MouseRight=" + (mouseRight ? "True" : "False")
				+ " | AllowGamepadInstructions=" + (allowGamepadInstructions ? "True" : "False")
				+ " | GamepadAllowScrolling=" + (gamepadAllowScrolling ? "True" : "False"));

		public static void LogIngameTriggerNavSuppress(
			string token,
			bool beforeMenuLeft,
			bool afterMenuLeft,
			bool beforeMenuRight,
			bool afterMenuRight,
			string suppressOwner,
			bool suppressApplied)
			=> WriteProbeLineSafe(
				FormatTs() + " | INGAME_TRIGGER_NAV_SUPPRESS | Token=" + SanitizeReason(token ?? "?")
				+ " | BeforeMenuLeft=" + (beforeMenuLeft ? "True" : "False")
				+ " | AfterMenuLeft=" + (afterMenuLeft ? "True" : "False")
				+ " | BeforeMenuRight=" + (beforeMenuRight ? "True" : "False")
				+ " | AfterMenuRight=" + (afterMenuRight ? "True" : "False")
				+ " | SuppressOwner=" + SanitizeReason(suppressOwner ?? "None")
				+ " | SuppressApplied=" + (suppressApplied ? "True" : "False"));

		public static void LogIngameTriggerNavResult(string token, int beforePoint, int afterPoint)
			=> WriteProbeLineSafe(
				FormatTs() + " | INGAME_TRIGGER_NAV_RESULT | Token=" + SanitizeReason(token ?? "?")
				+ " | BeforePoint=" + beforePoint.ToString(CultureInfo.InvariantCulture)
				+ " | AfterPoint=" + afterPoint.ToString(CultureInfo.InvariantCulture)
				+ " | Moved=" + (beforePoint != afterPoint ? "True" : "False"));

		public static void LogIngameTriggerNavMode(string before, string after, string changedBy)
			=> WriteProbeLineSafe(
				FormatTs() + " | INGAME_TRIGGER_NAV_MODE | Before=" + SanitizeReason(before ?? "?")
				+ " | After=" + SanitizeReason(after ?? "?")
				+ " | ChangedBy=" + SanitizeReason(changedBy ?? "?"));

		public static void LogForeignUiNavInput(
			string uiType,
			string token,
			int currentPointBefore,
			string currentInputMode,
			bool navigatorInUse)
			=> WriteProbeLineSafe(
				FormatTs() + " | FOREIGN_UI_NAV_INPUT | UiType=" + SanitizeReason(uiType ?? "?")
				+ " | Token=" + SanitizeReason(token ?? "?")
				+ " | CurrentPointBefore=" + currentPointBefore.ToString(CultureInfo.InvariantCulture)
				+ " | CurrentInputMode=" + SanitizeReason(currentInputMode ?? "?")
				+ " | NavigatorInUse=" + (navigatorInUse ? "True" : "False"));

		public static void LogForeignUiNavResult(
			string uiType,
			int before,
			int after,
			int up,
			int down,
			int left,
			int right,
			float targetX,
			float targetY)
			=> WriteProbeLineSafe(
				FormatTs() + " | FOREIGN_UI_NAV_RESULT | UiType=" + SanitizeReason(uiType ?? "?")
				+ " | CurrentPointBefore=" + before.ToString(CultureInfo.InvariantCulture)
				+ " | CurrentPointAfter=" + after.ToString(CultureInfo.InvariantCulture)
				+ " | Up=" + up.ToString(CultureInfo.InvariantCulture)
				+ " | Down=" + down.ToString(CultureInfo.InvariantCulture)
				+ " | Left=" + left.ToString(CultureInfo.InvariantCulture)
				+ " | Right=" + right.ToString(CultureInfo.InvariantCulture)
				+ " | Moved=" + (before != after ? "True" : "False")
				+ " | TargetPosition=" + targetX.ToString("0.#", CultureInfo.InvariantCulture)
				+ "," + targetY.ToString("0.#", CultureInfo.InvariantCulture));

		public static void LogForeignUiAkInterference(string uiType, string owner, string trigger, bool cleared)
			=> WriteProbeLineSafe(
				FormatTs() + " | FOREIGN_UI_AK_INTERFERENCE | UiType=" + SanitizeReason(uiType ?? "?")
				+ " | Owner=" + SanitizeReason(owner ?? "None")
				+ " | Trigger=" + SanitizeReason(trigger ?? "-")
				+ " | Cleared=" + (cleared ? "True" : "False"));

		public static void LogGlobalUiNavInput(
			string uiType,
			string token,
			int beforePoint,
			string inputMode,
			bool navigatorInUse,
			bool akControlsUiActive,
			bool akSuppressActive)
			=> WriteProbeLineSafe(
				FormatTs() + " | GLOBAL_UI_NAV_INPUT | UiType=" + SanitizeReason(uiType ?? "?")
				+ " | Token=" + SanitizeReason(token ?? "?")
				+ " | BeforePoint=" + beforePoint.ToString(CultureInfo.InvariantCulture)
				+ " | InputMode=" + SanitizeReason(inputMode ?? "?")
				+ " | NavigatorInUse=" + (navigatorInUse ? "True" : "False")
				+ " | AkControlsUiActive=" + (akControlsUiActive ? "True" : "False")
				+ " | AkSuppressActive=" + (akSuppressActive ? "True" : "False"));

		public static void LogGlobalUiNavLink(
			int point,
			int up,
			int down,
			int left,
			int right,
			bool targetExists,
			bool targetVisible,
			string targetUiType,
			string pointIdName = null,
			float posX = 0f,
			float posY = 0f)
		{
			string line = FormatTs() + " | GLOBAL_UI_NAV_LINK | Point=" + point.ToString(CultureInfo.InvariantCulture)
				+ " | PointIdName=" + SanitizeReason(pointIdName ?? "?")
				+ " | Pos=" + posX.ToString("0.#", CultureInfo.InvariantCulture)
				+ "," + posY.ToString("0.#", CultureInfo.InvariantCulture)
				+ " | Up=" + up.ToString(CultureInfo.InvariantCulture)
				+ " | Down=" + down.ToString(CultureInfo.InvariantCulture)
				+ " | Left=" + left.ToString(CultureInfo.InvariantCulture)
				+ " | Right=" + right.ToString(CultureInfo.InvariantCulture)
				+ " | TargetExists=" + (targetExists ? "True" : "False")
				+ " | TargetVisible=" + (targetVisible ? "True" : "False")
				+ " | TargetUiType=" + SanitizeReason(targetUiType ?? "-");
			WriteProbeLineSafe(line);
		}

		public static void LogGlobalUiNavResult(
			int beforePoint,
			int afterPoint,
			bool moved,
			bool officialLinkUsed,
			bool akInterference)
			=> WriteProbeLineSafe(
				FormatTs() + " | GLOBAL_UI_NAV_RESULT | BeforePoint=" + beforePoint.ToString(CultureInfo.InvariantCulture)
				+ " | AfterPoint=" + afterPoint.ToString(CultureInfo.InvariantCulture)
				+ " | Moved=" + (moved ? "True" : "False")
				+ " | OfficialLinkUsed=" + (officialLinkUsed ? "True" : "False")
				+ " | AkInterference=" + (akInterference ? "True" : "False"));

		public static void LogGlobalUiNavAkSuppress(string uiType, string owner, string trigger, bool cleared)
			=> WriteProbeLineSafe(
				FormatTs() + " | GLOBAL_UI_NAV_AK_SUPPRESS | UiType=" + SanitizeReason(uiType ?? "?")
				+ " | Trigger=" + SanitizeReason(trigger ?? "-")
				+ " | Owner=" + SanitizeReason(owner ?? "None")
				+ " | Cleared=" + (cleared ? "True" : "False"));

		public static void LogControlUiContext(
			string host,
			string currentStateType,
			bool manageControlsFound,
			string currentInputMode,
			int currentPoint,
			int pointCount,
			int highestPoint)
			=> WriteProbeLineSafe(
				FormatTs() + " | CONTROL_UI_CONTEXT | Host=" + SanitizeReason(host ?? "?")
				+ " | CurrentStateType=" + SanitizeReason(currentStateType ?? "-")
				+ " | ManageControlsFound=" + (manageControlsFound ? "True" : "False")
				+ " | CurrentInputMode=" + SanitizeReason(currentInputMode ?? "?")
				+ " | CurrentPoint=" + currentPoint.ToString(CultureInfo.InvariantCulture)
				+ " | PointCount=" + pointCount.ToString(CultureInfo.InvariantCulture)
				+ " | HighestPoint=" + highestPoint.ToString(CultureInfo.InvariantCulture));

		public static void LogControlUiDpad(string host, string token, bool menuFlag, int beforePoint, int afterPoint)
			=> WriteProbeLineSafe(
				FormatTs() + " | CONTROL_UI_DPAD | Host=" + SanitizeReason(host ?? "?")
				+ " | Token=" + SanitizeReason(token ?? "?")
				+ " | MenuDown=" + (menuFlag ? "True" : "False")
				+ " | BeforePoint=" + beforePoint.ToString(CultureInfo.InvariantCulture)
				+ " | AfterPoint=" + afterPoint.ToString(CultureInfo.InvariantCulture));

		public static void LogControlSectionLink(
			string stableId,
			string elementType,
			int snapId,
			int listIndex,
			int pointId,
			bool navigable,
			int up,
			int down,
			float posX,
			float posY)
			=> WriteProbeLineSafe(
				FormatTs() + " | CONTROL_SECTION_LINK | StableId=" + SanitizeReason(stableId ?? "?")
				+ " | ElementType=" + SanitizeReason(elementType ?? "-")
				+ " | SnapId=" + snapId.ToString(CultureInfo.InvariantCulture)
				+ " | ListIndex=" + listIndex.ToString(CultureInfo.InvariantCulture)
				+ " | PointId=" + pointId.ToString(CultureInfo.InvariantCulture)
				+ " | Navigable=" + (navigable ? "True" : "False")
				+ " | Up=" + up.ToString(CultureInfo.InvariantCulture)
				+ " | Down=" + down.ToString(CultureInfo.InvariantCulture)
				+ " | PositionX=" + posX.ToString("0.#", CultureInfo.InvariantCulture)
				+ " | PositionY=" + posY.ToString("0.#", CultureInfo.InvariantCulture));

		public static void LogControlSectionLinkFailed(string stableId, string reason)
			=> WriteProbeLineSafe(
				FormatTs() + " | CONTROL_SECTION_LINK_FAILED | StableId=" + SanitizeReason(stableId ?? "?")
				+ " | Reason=" + SanitizeReason(reason ?? "?"));

		public static void LogControlGridLink(
			string leftAction,
			string rightAction,
			int logicalRow,
			int leftPoint,
			int rightPoint)
			=> WriteProbeLineSafe(
				FormatTs() + " | CONTROL_GRID_LINK | LeftAction=" + SanitizeReason(leftAction ?? "?")
				+ " | RightAction=" + SanitizeReason(rightAction ?? "?")
				+ " | LogicalRow=" + logicalRow.ToString(CultureInfo.InvariantCulture)
				+ " | LeftPoint=" + leftPoint.ToString(CultureInfo.InvariantCulture)
				+ " | RightPoint=" + rightPoint.ToString(CultureInfo.InvariantCulture));

		public static void LogControlGridVertical(string from, string to, string column, string direction)
			=> WriteProbeLineSafe(
				FormatTs() + " | CONTROL_GRID_VERTICAL | From=" + SanitizeReason(from ?? "?")
				+ " | To=" + SanitizeReason(to ?? "?")
				+ " | Column=" + SanitizeReason(column ?? "?")
				+ " | Direction=" + SanitizeReason(direction ?? "?"));

		public static void LogControlGridHorizontal(string from, string to, string direction)
			=> WriteProbeLineSafe(
				FormatTs() + " | CONTROL_GRID_HORIZONTAL | From=" + SanitizeReason(from ?? "?")
				+ " | To=" + SanitizeReason(to ?? "?")
				+ " | Direction=" + SanitizeReason(direction ?? "?"));

		public static void LogControlGridColumnLost(string from, string actualTo, string expectedColumn)
			=> WriteProbeLineSafe(
				FormatTs() + " | CONTROL_GRID_COLUMN_LOST | From=" + SanitizeReason(from ?? "?")
				+ " | ActualTo=" + SanitizeReason(actualTo ?? "?")
				+ " | ExpectedColumn=" + SanitizeReason(expectedColumn ?? "?"));

		public static void LogControlManualScrollBegin(int preferredColumn)
			=> WriteProbeLineSafe(
				FormatTs() + " | CONTROL_MANUAL_SCROLL_BEGIN | PreferredColumn="
				+ preferredColumn.ToString(CultureInfo.InvariantCulture));

		public static void LogControlManualScrollEnd(int focusPoint)
			=> WriteProbeLineSafe(
				FormatTs() + " | CONTROL_MANUAL_SCROLL_END | FocusPoint="
				+ focusPoint.ToString(CultureInfo.InvariantCulture));

		public static void LogPanelInputConsumeBegin(string token, string reason, int epoch)
			=> WriteProbeLineSafe(
				FormatTs() + " | PANEL_INPUT_CONSUME_BEGIN | Token=" + SanitizeReason(token ?? "?")
				+ " | Reason=" + SanitizeReason(reason ?? "?")
				+ " | Epoch=" + epoch.ToString(CultureInfo.InvariantCulture));

		public static void LogPanelInputConsumeBlock(string token, string target)
			=> WriteProbeLineSafe(
				FormatTs() + " | PANEL_INPUT_CONSUME_BLOCK | Token=" + SanitizeReason(token ?? "?")
				+ " | Target=" + SanitizeReason(target ?? "?"));

		public static void LogPanelInputConsumeEnd(string token, string reason)
			=> WriteProbeLineSafe(
				FormatTs() + " | PANEL_INPUT_CONSUME_END | Token=" + SanitizeReason(token ?? "?")
				+ " | Reason=" + SanitizeReason(reason ?? "?"));

		public static void LogControlBackDuplicateBlocked(string token, string firstAction, string blockedAction)
			=> WriteProbeLineSafe(
				FormatTs() + " | CONTROL_BACK_DUPLICATE_BLOCKED | Token=" + SanitizeReason(token ?? "?")
				+ " | FirstAction=" + SanitizeReason(firstAction ?? "?")
				+ " | BlockedAction=" + SanitizeReason(blockedAction ?? "?"));

		public static void LogControlNavSelectedNotVisible(string target)
			=> WriteProbeLineSafe(
				FormatTs() + " | CONTROL_NAV_SELECTED_NOT_VISIBLE | Target=" + SanitizeReason(target ?? "?"));

		public static void LogControlNavScroll(float oldView, float newView, string target)
			=> WriteProbeLineSafe(
				FormatTs() + " | CONTROL_NAV_SCROLL | Old=" + oldView.ToString("0.###", CultureInfo.InvariantCulture)
				+ " | New=" + newView.ToString("0.###", CultureInfo.InvariantCulture)
				+ " | Target=" + SanitizeReason(target ?? "?"));

		public static void LogControlNavSkipScrollItem(string from, string to, string direction)
			=> WriteProbeLineSafe(
				FormatTs() + " | CONTROL_NAV_SKIP_SCROLL_ITEM | From=" + SanitizeReason(from ?? "?")
				+ " | To=" + SanitizeReason(to ?? "?")
				+ " | Direction=" + SanitizeReason(direction ?? "?"));

		public static void LogControlAccordionToggleBegin(string current, string stableId, bool? expandedBefore)
			=> WriteProbeLineSafe(
				FormatTs() + " | CONTROL_ACCORDION_TOGGLE_BEGIN | Current=" + SanitizeReason(current ?? "?")
				+ " | StableId=" + SanitizeReason(stableId ?? "?")
				+ " | ExpandedBefore=" + (expandedBefore.HasValue ? (expandedBefore.Value ? "True" : "False") : "-"));

		public static void LogControlAccordionToggleEnd(string stableId, bool? expandedAfter, string restored)
			=> WriteProbeLineSafe(
				FormatTs() + " | CONTROL_ACCORDION_TOGGLE_END | StableId=" + SanitizeReason(stableId ?? "?")
				+ " | ExpandedAfter=" + (expandedAfter.HasValue ? (expandedAfter.Value ? "True" : "False") : "-")
				+ " | Restored=" + SanitizeReason(restored ?? "?"));

		public static void LogControlNavRestore(string oldPoint, string newPoint, string reason)
			=> WriteProbeLineSafe(
				FormatTs() + " | CONTROL_NAV_RESTORE | Old=" + SanitizeReason(oldPoint ?? "?")
				+ " | New=" + SanitizeReason(newPoint ?? "?")
				+ " | Reason=" + SanitizeReason(reason ?? "?"));

		public static void LogControlNavUnexpectedFixedUi(string from, string to, string reason)
			=> WriteProbeLineSafe(
				FormatTs() + " | CONTROL_NAV_UNEXPECTED_FIXED_UI | From=" + SanitizeReason(from ?? "?")
				+ " | To=" + SanitizeReason(to ?? "?")
				+ " | Reason=" + SanitizeReason(reason ?? "?"));

		public static void LogPanelPadBackBegin(string mode, string parentScreen)
			=> WriteProbeLineSafe(
				FormatTs() + " | PANEL_PAD_BACK_BEGIN | Mode=" + SanitizeReason(mode ?? "?")
				+ " | ParentScreen=" + SanitizeReason(parentScreen ?? "?"));

		public static void LogPanelClose()
			=> WriteProbeLineSafe(FormatTs() + " | PANEL_CLOSE | Closed=ComboPanelOnly");

		public static void LogReturnParentControls()
			=> WriteProbeLineSafe(FormatTs() + " | RETURN_PARENT_CONTROLS | ParentControlsStillOpen=True");

		public static void LogPanelParentRestore(
			string actionId,
			string bindingStableId,
			string parentHeaderStableId,
			string matchKind,
			int restorePoint,
			float viewPosition)
			=> WriteProbeLineSafe(
				FormatTs() + " | PANEL_PARENT_RESTORE"
				+ " | ActionId=" + SanitizeReason(actionId ?? "?")
				+ " | BindingStableId=" + SanitizeReason(bindingStableId ?? "?")
				+ " | ParentHeaderStableId=" + SanitizeReason(parentHeaderStableId ?? "?")
				+ " | MatchKind=" + SanitizeReason(matchKind ?? "?")
				+ " | RestorePoint=" + (restorePoint == int.MinValue ? "-" : restorePoint.ToString(CultureInfo.InvariantCulture))
				+ " | ViewPosition=" + (float.IsNaN(viewPosition) ? "-" : viewPosition.ToString("0.###", CultureInfo.InvariantCulture)));

		public static void LogPanelPadBackEnd(string token)
			=> WriteProbeLineSafe(
				FormatTs() + " | END | Token=" + (token ?? "-"));

		public static void LogNavRebuildBegin(string currentStableId)
			=> WriteProbeLineSafe(
				FormatTs() + " | NAV_REBUILD_BEGIN | CurrentStableId=" + SanitizeReason(currentStableId ?? "?"));

		public static void LogNavRebuildEnd(string restoredStableId)
			=> WriteProbeLineSafe(
				FormatTs() + " | NAV_REBUILD_END | RestoredStableId=" + SanitizeReason(restoredStableId ?? "?"));

		public static void LogRestoreResult(bool success, bool fallback)
			=> WriteProbeLineSafe(
				FormatTs() + " | RESTORE_RESULT | Success=" + (success ? "True" : "False")
				+ " | Fallback=" + (fallback ? "True" : "False"));

		public static void LogNavRestoreFailed(string currentStableId, string reason)
			=> WriteProbeLineSafe(
				FormatTs() + " | NAV_RESTORE_FAILED | CurrentStableId=" + SanitizeReason(currentStableId ?? "?")
				+ " | Reason=" + SanitizeReason(reason ?? "?"));

		public static void LogScrollDuplicate(string target, int epoch)
			=> WriteProbeLineSafe(
				FormatTs() + " | SCROLL_DUPLICATE | Target=" + SanitizeReason(target ?? "?")
				+ " | Epoch=" + epoch.ToString(CultureInfo.InvariantCulture));

		public static void LogControlsClose(string source)
			=> WriteProbeLineSafe(
				FormatTs() + " | ControlsClose | Source=" + SanitizeReason(source ?? "?"));

		public static void LogControlsCloseLeak(string source)
			=> WriteProbeLineSafe(
				FormatTs() + " | ControlsClose | LEAK | Source=" + SanitizeReason(source ?? "?"));

		private static void WriteProbeLineSafe(string line)
		{
			lock (Gate)
			{
				WriteProbeLineSafeUnlocked(line);
			}
		}

		private static void WriteProbeLineSafeUnlocked(string line)
		{
			try
			{
				if (!_loggingEnabled)
					return;
				EnsureWriters();
				if (!_writersReady || _store == null || !_store.IsOpen)
					return;

				// Dedup structural spam (SetupGamepadPoints runs every Draw).
				int pipe = line.IndexOf('|');
				string body = pipe >= 0 ? line.Substring(pipe + 1).TrimStart() : line;
				if (GamePadProbeLogEmitGate.IsExemptFromAdjacentDedupe(body))
				{
					WriteProbeLine(line, flushImmediate: true);
					return;
				}

				if (body.StartsWith("CONTROL_SECTION_", StringComparison.Ordinal)
					|| body.StartsWith("CONTROL_ROW_", StringComparison.Ordinal)
					|| body.StartsWith("CONTROL_GRID_", StringComparison.Ordinal)
					|| body.StartsWith("CONTROL_NAV_", StringComparison.Ordinal)
					|| body.StartsWith("CONTROL_ACCORDION_", StringComparison.Ordinal)
					|| body.StartsWith("GLOBAL_UI_NAV_LINK", StringComparison.Ordinal)
					|| body.StartsWith("CONTROL_UI_CONTEXT", StringComparison.Ordinal))
				{
					// Drop volatile Position* so layout jitter does not re-log every frame.
					string dedupKey = StripVolatileProbeFields(body);
					if (!_sessionDedupKeys.Add(dedupKey))
						return;
				}
				else if (string.Equals(body, _lastDedupLine, StringComparison.Ordinal))
				{
					return;
				}
				else
				{
					_lastDedupLine = body;
				}

				WriteProbeLine(line);
			}
			catch (Exception ex)
			{
				LogProbeErrorOnce("PanelPadLog", ex);
			}
		}

		private static string StripVolatileProbeFields(string body)
		{
			if (string.IsNullOrEmpty(body))
				return body;
			// Remove PositionX=… PositionY=… (and rounded pixel noise).
			var sb = new StringBuilder(body.Length);
			string[] parts = body.Split('|');
			for (int i = 0; i < parts.Length; i++)
			{
				string p = parts[i].Trim();
				if (p.StartsWith("PositionX=", StringComparison.Ordinal)
					|| p.StartsWith("PositionY=", StringComparison.Ordinal)
					|| p.StartsWith("Old=", StringComparison.Ordinal)
					|| p.StartsWith("New=", StringComparison.Ordinal))
					continue;
				if (sb.Length > 0)
					sb.Append(" | ");
				sb.Append(p);
			}

			return sb.ToString();
		}

		private static void LogFileErrorOnce(Exception ex)
		{
			if (_fileErrorLoggedOnce)
				return;
			_fileErrorLoggedOnce = true;
			try
			{
				AdvancedKeybinds.Instance?.Logger?.Warn(
					"[GamePadProbe TEMP] log file error: " + ex.GetType().Name + ": " + ex.Message);
			}
			catch { /* ignore */ }
		}

		public static void LogPanelBackgroundInputBlockBegin(string host)
			=> WriteProbeLineSafe(
				FormatTs() + " | PANEL_BACKGROUND_INPUT_BLOCK_BEGIN | Host=" + SanitizeReason(host ?? "?"));

		public static void LogPanelBackgroundInputBlockEnd(string reason)
			=> WriteProbeLineSafe(
				FormatTs() + " | PANEL_BACKGROUND_INPUT_BLOCK_END | Reason=" + SanitizeReason(reason ?? "?"));

		public static void LogPanelBackgroundInputBlock(string token, string target, string trigger, bool cleared)
			=> WriteProbeLineSafe(
				FormatTs() + " | PANEL_BACKGROUND_INPUT_BLOCK | Token=" + SanitizeReason(token ?? "?")
				+ " | Target=" + SanitizeReason(target ?? "?")
				+ " | Trigger=" + SanitizeReason(trigger ?? "?")
				+ " | Cleared=" + (cleared ? "True" : "False"));

		public static void LogPanelInputOwnership(string token, string owner, bool parentBlocked, bool gameplayBlocked)
			=> WriteProbeLineSafe(
				FormatTs() + " | PANEL_INPUT_OWNERSHIP | Token=" + SanitizeReason(token ?? "?")
				+ " | Owner=" + SanitizeReason(owner ?? "?")
				+ " | ParentBlocked=" + (parentBlocked ? "True" : "False")
				+ " | GameplayBlocked=" + (gameplayBlocked ? "True" : "False"));

		public static void LogPanelOutsideClickBack(bool physicalMouse, bool panelClosed, bool parentActionBlocked)
			=> WriteProbeLineSafe(
				FormatTs() + " | PANEL_OUTSIDE_CLICK_BACK | PhysicalMouse=" + (physicalMouse ? "True" : "False")
				+ " | PanelClosed=" + (panelClosed ? "True" : "False")
				+ " | ParentActionBlocked=" + (parentActionBlocked ? "True" : "False"));

		public static void LogPanelEditHistoryBegin(string action, string initialSlots, string holdMode, int holdMs)
			=> WriteProbeLineSafe(
				FormatTs() + " | PANEL_EDIT_HISTORY_BEGIN | Action=" + SanitizeReason(action ?? "?")
				+ " | InitialSlots=" + SanitizeReason(initialSlots ?? "?")
				+ " | HoldMode=" + SanitizeReason(holdMode ?? "?")
				+ " | HoldMs=" + holdMs.ToString(CultureInfo.InvariantCulture));

		public static void LogPanelEditHistoryPush(string reason, int depth)
			=> WriteProbeLineSafe(
				FormatTs() + " | PANEL_EDIT_HISTORY_PUSH | Reason=" + SanitizeReason(reason ?? "?")
				+ " | Depth=" + depth.ToString(CultureInfo.InvariantCulture));

		public static void LogPanelEditHistoryEnd(string reason, int discardedDepth, string action = null)
			=> WriteProbeLineSafe(
				FormatTs() + " | PANEL_EDIT_HISTORY_END | Reason=" + SanitizeReason(reason ?? "?")
				+ " | DiscardedDepth=" + discardedDepth.ToString(CultureInfo.InvariantCulture)
				+ (string.IsNullOrEmpty(action) ? string.Empty : " | Action=" + SanitizeReason(action)));

		public static void LogPanelUndoApply(string reason, int depthBefore, int depthAfter, bool panelStillOpen)
			=> WriteProbeLineSafe(
				FormatTs() + " | PANEL_UNDO_APPLY | Reason=" + SanitizeReason(reason ?? "?")
				+ " | DepthBefore=" + depthBefore.ToString(CultureInfo.InvariantCulture)
				+ " | DepthAfter=" + depthAfter.ToString(CultureInfo.InvariantCulture)
				+ " | PanelStillOpen=" + (panelStillOpen ? "True" : "False"));

		public static void LogPanelUndoUnavailable(string reason)
			=> WriteProbeLineSafe(
				FormatTs() + " | PANEL_UNDO_UNAVAILABLE | Reason=" + SanitizeReason(reason ?? "?")
				+ " | PanelStillOpen=True");

		public static void LogControlTopNavElement(
			int index,
			string stableId,
			string elementType,
			string snapName,
			int snapId,
			int pointId,
			Vector2 position,
			bool visible,
			bool navigable,
			int left,
			int right)
			=> WriteProbeLineSafe(
				FormatTs() + " | CONTROL_TOP_NAV_ELEMENT | Index=" + index.ToString(CultureInfo.InvariantCulture)
				+ " | StableId=" + SanitizeReason(stableId ?? "?")
				+ " | ElementType=" + SanitizeReason(elementType ?? "?")
				+ " | SnapName=" + SanitizeReason(snapName ?? "-")
				+ " | SnapId=" + snapId.ToString(CultureInfo.InvariantCulture)
				+ " | PointId=" + pointId.ToString(CultureInfo.InvariantCulture)
				+ " | Position=" + position.X.ToString("0.#", CultureInfo.InvariantCulture)
				+ "," + position.Y.ToString("0.#", CultureInfo.InvariantCulture)
				+ " | Visible=" + (visible ? "True" : "False")
				+ " | Navigable=" + (navigable ? "True" : "False")
				+ " | Left=" + left.ToString(CultureInfo.InvariantCulture)
				+ " | Right=" + right.ToString(CultureInfo.InvariantCulture));

		public static void LogControlTopNavInput(string token, int beforePoint, string beforeStableId, string expectedNeighbor)
			=> WriteProbeLineSafe(
				FormatTs() + " | CONTROL_TOP_NAV_INPUT | Token=" + SanitizeReason(token ?? "?")
				+ " | BeforePoint=" + beforePoint.ToString(CultureInfo.InvariantCulture)
				+ " | BeforeStableId=" + SanitizeReason(beforeStableId ?? "?")
				+ " | ExpectedNeighbor=" + SanitizeReason(expectedNeighbor ?? "?"));

		public static void LogControlTopNavResult(
			int beforePoint,
			int afterPoint,
			string afterStableId,
			bool moved,
			bool akUndoSkipped)
			=> WriteProbeLineSafe(
				FormatTs() + " | CONTROL_TOP_NAV_RESULT | BeforePoint=" + beforePoint.ToString(CultureInfo.InvariantCulture)
				+ " | AfterPoint=" + afterPoint.ToString(CultureInfo.InvariantCulture)
				+ " | AfterStableId=" + SanitizeReason(afterStableId ?? "?")
				+ " | Moved=" + (moved ? "True" : "False")
				+ " | AkUndoSkipped=" + (akUndoSkipped ? "True" : "False"));

		public static void LogControlTopNavSkip(string skippedStableId, string reason)
			=> WriteProbeLineSafe(
				FormatTs() + " | CONTROL_TOP_NAV_SKIP | SkippedStableId=" + SanitizeReason(skippedStableId ?? "?")
				+ " | Reason=" + SanitizeReason(reason ?? "?"));

		public static void LogControlTopNavFocus(string stableId, int pointId, bool highlighted)
			=> WriteProbeLineSafe(
				FormatTs() + " | CONTROL_TOP_NAV_FOCUS | StableId=" + SanitizeReason(stableId ?? "?")
				+ " | PointId=" + pointId.ToString(CultureInfo.InvariantCulture)
				+ " | Highlighted=" + (highlighted ? "True" : "False"));

		public static void LogControlTopNavConfirm(
			string stableId,
			int pointId,
			string token,
			bool invoked,
			bool executedOnce)
			=> WriteProbeLineSafe(
				FormatTs() + " | CONTROL_TOP_NAV_CONFIRM | StableId=" + SanitizeReason(stableId ?? "?")
				+ " | PointId=" + pointId.ToString(CultureInfo.InvariantCulture)
				+ " | Token=" + SanitizeReason(token ?? "?")
				+ " | Invoked=" + (invoked ? "True" : "False")
				+ " | ExecutedOnce=" + (executedOnce ? "True" : "False"));

		public static void LogAkSectionConfirmLifetimeBegin(
			int epoch,
			int pointId,
			string stableId,
			bool padAPressed,
			bool? expandedBefore)
			=> WriteProbeLineSafe(
				FormatTs() + " | AK_SECTION_CONFIRM_LIFETIME_BEGIN | Epoch=" + epoch.ToString(CultureInfo.InvariantCulture)
				+ " | PointId=" + pointId.ToString(CultureInfo.InvariantCulture)
				+ " | StableId=" + SanitizeReason(stableId ?? "?")
				+ " | PadAPressed=" + (padAPressed ? "True" : "False")
				+ " | ExpandedBefore=" + (expandedBefore.HasValue ? (expandedBefore.Value ? "True" : "False") : "?"));

		public static void LogAkSectionConfirmExactRestore(
			int epoch,
			string stableId,
			int pointId,
			bool padACurrent,
			bool sessionActive)
			=> WriteProbeLineSafe(
				FormatTs() + " | AK_SECTION_CONFIRM_EXACT_RESTORE | Epoch=" + epoch.ToString(CultureInfo.InvariantCulture)
				+ " | StableId=" + SanitizeReason(stableId ?? "?")
				+ " | PointId=" + pointId.ToString(CultureInfo.InvariantCulture)
				+ " | PadACurrent=" + (padACurrent ? "True" : "False")
				+ " | SessionActive=" + (sessionActive ? "True" : "False"));

		public static void LogAkSectionConfirmRelease(
			int epoch,
			string stableId,
			int currentPoint,
			string currentStableId,
			bool sessionActive)
			=> WriteProbeLineSafe(
				FormatTs() + " | AK_SECTION_CONFIRM_RELEASE | Epoch=" + epoch.ToString(CultureInfo.InvariantCulture)
				+ " | StableId=" + SanitizeReason(stableId ?? "?")
				+ " | CurrentPoint=" + currentPoint.ToString(CultureInfo.InvariantCulture)
				+ " | CurrentStableId=" + SanitizeReason(currentStableId ?? "?")
				+ " | SessionActive=" + (sessionActive ? "True" : "False"));

		public static void LogAkSectionPostRelease3002(
			int epoch,
			int msSincePress,
			int msSinceRelease,
			int fromPoint,
			string fromStableId,
			int toPoint,
			string callerType,
			string callerMethod,
			string stack,
			bool padAPressed,
			bool padACurrent,
			bool padAReleased,
			bool menuConfirmPressed,
			bool menuConfirmCurrent,
			bool menuConfirmReleased,
			bool duringSetup,
			bool duringOnActivate,
			bool duringUiUpdate,
			bool duringDraw,
			bool restoreSessionActive,
			bool manualScrollActive,
			float rightStickY,
			bool manualScrollSyncRan,
			int manualScrollSelectedPoint,
			string manualScrollSelectedStableId,
			bool applied)
			=> WriteProbeLineSafe(
				FormatTs() + " | AK_SECTION_POST_RELEASE_3002 | Epoch=" + epoch.ToString(CultureInfo.InvariantCulture)
				+ " | MillisecondsSincePress=" + msSincePress.ToString(CultureInfo.InvariantCulture)
				+ " | MillisecondsSinceRelease=" + msSinceRelease.ToString(CultureInfo.InvariantCulture)
				+ " | FromPoint=" + fromPoint.ToString(CultureInfo.InvariantCulture)
				+ " | FromStableId=" + SanitizeReason(fromStableId ?? "?")
				+ " | ToPoint=" + toPoint.ToString(CultureInfo.InvariantCulture)
				+ " | CallerType=" + SanitizeReason(callerType ?? "?")
				+ " | CallerMethod=" + SanitizeReason(callerMethod ?? "?")
				+ " | Stack=" + SanitizeReason(stack ?? "?")
				+ " | PadAPressed=" + (padAPressed ? "True" : "False")
				+ " | PadACurrent=" + (padACurrent ? "True" : "False")
				+ " | PadAReleased=" + (padAReleased ? "True" : "False")
				+ " | MenuConfirmPressed=" + (menuConfirmPressed ? "True" : "False")
				+ " | MenuConfirmCurrent=" + (menuConfirmCurrent ? "True" : "False")
				+ " | MenuConfirmReleased=" + (menuConfirmReleased ? "True" : "False")
				+ " | DuringSetupGamepadPoints=" + (duringSetup ? "True" : "False")
				+ " | DuringOnActivate=" + (duringOnActivate ? "True" : "False")
				+ " | DuringUiUpdate=" + (duringUiUpdate ? "True" : "False")
				+ " | DuringDraw=" + (duringDraw ? "True" : "False")
				+ " | RestoreSessionActive=" + (restoreSessionActive ? "True" : "False")
				+ " | ManualScrollActive=" + (manualScrollActive ? "True" : "False")
				+ " | RightStickY=" + rightStickY.ToString("0.###", CultureInfo.InvariantCulture)
				+ " | ManualScrollSyncRan=" + (manualScrollSyncRan ? "True" : "False")
				+ " | ManualScrollSelectedPoint=" + manualScrollSelectedPoint.ToString(CultureInfo.InvariantCulture)
				+ " | ManualScrollSelectedStableId=" + SanitizeReason(manualScrollSelectedStableId ?? "?")
				+ " | Applied=" + (applied ? "True" : "False"));

		public static void LogAkSectionConfirmLifetimeEnd(
			string originalStableId,
			int finalPoint,
			string finalStableId,
			bool movedToGamepadTab,
			string cause,
			bool fixedOk)
			=> WriteProbeLineSafe(
				FormatTs() + " | AK_SECTION_CONFIRM_LIFETIME_END | OriginalStableId=" + SanitizeReason(originalStableId ?? "?")
				+ " | FinalPoint=" + finalPoint.ToString(CultureInfo.InvariantCulture)
				+ " | FinalStableId=" + SanitizeReason(finalStableId ?? "?")
				+ " | MovedToGamepadTab=" + (movedToGamepadTab ? "True" : "False")
				+ " | Cause=" + SanitizeReason(cause ?? "?")
				+ " | Fixed=" + (fixedOk ? "True" : "False"));

		public static void LogAkManualScrollFocusSync(
			int epoch,
			int currentPointBefore,
			string currentStableIdBefore,
			float rightStickY,
			int selectedPoint,
			string selectedStableId,
			int currentPointAfter,
			bool triggeredDuringSectionConfirm)
			=> WriteProbeLineSafe(
				FormatTs() + " | AK_MANUAL_SCROLL_FOCUS_SYNC | Epoch=" + epoch.ToString(CultureInfo.InvariantCulture)
				+ " | CurrentPointBefore=" + currentPointBefore.ToString(CultureInfo.InvariantCulture)
				+ " | CurrentStableIdBefore=" + SanitizeReason(currentStableIdBefore ?? "?")
				+ " | RightStickY=" + rightStickY.ToString("0.###", CultureInfo.InvariantCulture)
				+ " | SelectedPoint=" + selectedPoint.ToString(CultureInfo.InvariantCulture)
				+ " | SelectedStableId=" + SanitizeReason(selectedStableId ?? "?")
				+ " | CurrentPointAfter=" + currentPointAfter.ToString(CultureInfo.InvariantCulture)
				+ " | TriggeredDuringSectionConfirm=" + (triggeredDuringSectionConfirm ? "True" : "False"));

		public static void LogAkModBindingRowClassify(
			int point,
			string elementType,
			string action,
			string parentType,
			string parentStableId,
			string kind,
			bool hasBindingFlag,
			bool hasSectionChildFlag,
			bool navigable)
		{
			// Default: Off — input diagnosis must not flood with per-row classify spam.
			string structureFp = (parentStableId ?? "") + "|" + (parentType ?? "");
			string rowKey = (action ?? "?")
				+ "|" + (kind ?? "")
				+ "|" + point.ToString(CultureInfo.InvariantCulture)
				+ "|" + (elementType ?? "");
			lock (Gate)
			{
				if (!EmitGate.ShouldEmitBindingRowClassify(structureFp, rowKey))
					return;
				WriteProbeLineSafeUnlocked(
					FormatTs() + " | AK_MOD_BINDING_ROW_CLASSIFY | Point=" + point.ToString(CultureInfo.InvariantCulture)
					+ " | ElementType=" + SanitizeReason(elementType ?? "?")
					+ " | Action=" + SanitizeReason(action ?? "?")
					+ " | ParentType=" + SanitizeReason(parentType ?? "?")
					+ " | ParentStableId=" + SanitizeReason(parentStableId ?? "?")
					+ " | Kind=" + SanitizeReason(kind ?? "?")
					+ " | HasBindingFlag=" + (hasBindingFlag ? "True" : "False")
					+ " | HasSectionChildFlag=" + (hasSectionChildFlag ? "True" : "False")
					+ " | Navigable=" + (navigable ? "True" : "False"));
			}
		}
		public static void LogAkModBindingConfirm(
			int point,
			string action,
			string kind,
			bool bindingAllowed,
			string reason)
			=> WriteProbeLineSafe(
				FormatTs() + " | AK_MOD_BINDING_CONFIRM | Point=" + point.ToString(CultureInfo.InvariantCulture)
				+ " | Action=" + SanitizeReason(action ?? "?")
				+ " | Kind=" + SanitizeReason(kind ?? "?")
				+ " | BindingAllowed=" + (bindingAllowed ? "True" : "False")
				+ " | Reason=" + SanitizeReason(reason ?? "?"));

		public static void LogAkSectionFocusSave(
			int epoch,
			int pointId,
			string stableId,
			string elementType,
			bool? expandedBefore)
			=> WriteProbeLineSafe(
				FormatTs() + " | AK_SECTION_FOCUS_SAVE | Epoch=" + epoch.ToString(CultureInfo.InvariantCulture)
				+ " | PointId=" + pointId.ToString(CultureInfo.InvariantCulture)
				+ " | StableId=" + SanitizeReason(stableId ?? "?")
				+ " | ElementType=" + SanitizeReason(elementType ?? "?")
				+ " | ExpandedBefore=" + (expandedBefore.HasValue ? (expandedBefore.Value ? "True" : "False") : "?"));

		public static void LogAkControlsRebuildDetected(
			int epoch,
			string savedStableId,
			int currentPoint,
			string currentStableId,
			bool gamepadTabTransient)
			=> WriteProbeLineSafe(
				FormatTs() + " | AK_CONTROLS_REBUILD_DETECTED | Epoch=" + epoch.ToString(CultureInfo.InvariantCulture)
				+ " | SavedStableId=" + SanitizeReason(savedStableId ?? "?")
				+ " | CurrentPoint=" + currentPoint.ToString(CultureInfo.InvariantCulture)
				+ " | CurrentStableId=" + SanitizeReason(currentStableId ?? "?")
				+ " | GamepadTabTransient=" + (gamepadTabTransient ? "True" : "False"));

		public static void LogAkControlsCatalogReady(
			int epoch,
			string savedStableId,
			int resolvedPoint,
			string resolvedStableId,
			bool exactStableIdMatch)
			=> WriteProbeLineSafe(
				FormatTs() + " | AK_CONTROLS_CATALOG_READY | Epoch=" + epoch.ToString(CultureInfo.InvariantCulture)
				+ " | SavedStableId=" + SanitizeReason(savedStableId ?? "?")
				+ " | ResolvedPoint=" + resolvedPoint.ToString(CultureInfo.InvariantCulture)
				+ " | ResolvedStableId=" + SanitizeReason(resolvedStableId ?? "?")
				+ " | ExactStableIdMatch=" + (exactStableIdMatch ? "True" : "False"));

		public static void LogAkSectionFocusRestore(
			int epoch,
			int fromPoint,
			string fromStableId,
			int toPoint,
			string toStableId,
			bool exactStableIdMatch,
			bool beforeDraw)
			=> WriteProbeLineSafe(
				FormatTs() + " | AK_SECTION_FOCUS_RESTORE | Epoch=" + epoch.ToString(CultureInfo.InvariantCulture)
				+ " | FromPoint=" + fromPoint.ToString(CultureInfo.InvariantCulture)
				+ " | FromStableId=" + SanitizeReason(fromStableId ?? "?")
				+ " | ToPoint=" + toPoint.ToString(CultureInfo.InvariantCulture)
				+ " | ToStableId=" + SanitizeReason(toStableId ?? "?")
				+ " | ExactStableIdMatch=" + (exactStableIdMatch ? "True" : "False")
				+ " | BeforeDraw=" + (beforeDraw ? "True" : "False"));

		public static void LogAkSectionFocusVerify(
			int epoch,
			string requestedStableId,
			int finalPoint,
			string finalStableId,
			bool success,
			bool fallbackUsed)
			=> WriteProbeLineSafe(
				FormatTs() + " | AK_SECTION_FOCUS_VERIFY | Epoch=" + epoch.ToString(CultureInfo.InvariantCulture)
				+ " | RequestedStableId=" + SanitizeReason(requestedStableId ?? "?")
				+ " | FinalPoint=" + finalPoint.ToString(CultureInfo.InvariantCulture)
				+ " | FinalStableId=" + SanitizeReason(finalStableId ?? "?")
				+ " | Success=" + (success ? "True" : "False")
				+ " | FallbackUsed=" + (fallbackUsed ? "True" : "False"));

		public static void LogAkSectionFocusMismatch(
			int epoch,
			string requestedStableId,
			string resolvedStableId,
			int pointId,
			string reason)
			=> WriteProbeLineSafe(
				FormatTs() + " | AK_SECTION_FOCUS_MISMATCH | Epoch=" + epoch.ToString(CultureInfo.InvariantCulture)
				+ " | RequestedStableId=" + SanitizeReason(requestedStableId ?? "?")
				+ " | ResolvedStableId=" + SanitizeReason(resolvedStableId ?? "?")
				+ " | PointId=" + pointId.ToString(CultureInfo.InvariantCulture)
				+ " | Reason=" + SanitizeReason(reason ?? "?"));

		public static void LogControlTopNavChange(
			int epoch,
			int sequence,
			string caller,
			int from,
			int to,
			string reason)
			=> WriteProbeLineSafe(
				FormatTs() + " | CONTROL_TOP_NAV_CHANGE | Epoch=" + epoch.ToString(CultureInfo.InvariantCulture)
				+ " | Sequence=" + sequence.ToString(CultureInfo.InvariantCulture)
				+ " | Caller=" + SanitizeReason(caller ?? "?")
				+ " | From=" + from.ToString(CultureInfo.InvariantCulture)
				+ " | To=" + to.ToString(CultureInfo.InvariantCulture)
				+ " | Reason=" + SanitizeReason(reason ?? "?"));

		public static void LogControlTopNavResultDetail(
			int epoch,
			int beforePoint,
			int expectedPoint,
			int afterPoint,
			int moveCount,
			bool undoSkipped,
			bool duplicateInput,
			bool pointOverwritten)
			=> WriteProbeLineSafe(
				FormatTs() + " | CONTROL_TOP_NAV_RESULT | Epoch=" + epoch.ToString(CultureInfo.InvariantCulture)
				+ " | BeforePoint=" + beforePoint.ToString(CultureInfo.InvariantCulture)
				+ " | ExpectedPoint=" + expectedPoint.ToString(CultureInfo.InvariantCulture)
				+ " | AfterPoint=" + afterPoint.ToString(CultureInfo.InvariantCulture)
				+ " | MoveCount=" + moveCount.ToString(CultureInfo.InvariantCulture)
				+ " | UndoSkipped=" + (undoSkipped ? "True" : "False")
				+ " | DuplicateInput=" + (duplicateInput ? "True" : "False")
				+ " | PointOverwritten=" + (pointOverwritten ? "True" : "False"));

		public static void LogControlResetElement(
			string action,
			string elementType,
			bool visible,
			bool navigable,
			int pointId,
			Vector2 position,
			int matchedBindingPoint,
			string matchMethod)
			=> WriteProbeLineSafe(
				FormatTs() + " | CONTROL_RESET_ELEMENT | Action=" + SanitizeReason(action ?? "?")
				+ " | ElementType=" + SanitizeReason(elementType ?? "?")
				+ " | Visible=" + (visible ? "True" : "False")
				+ " | Navigable=" + (navigable ? "True" : "False")
				+ " | PointId=" + pointId.ToString(CultureInfo.InvariantCulture)
				+ " | Position=" + position.X.ToString("0.#", CultureInfo.InvariantCulture)
				+ "," + position.Y.ToString("0.#", CultureInfo.InvariantCulture)
				+ " | MatchedBindingPoint=" + matchedBindingPoint.ToString(CultureInfo.InvariantCulture)
				+ " | MatchMethod=" + SanitizeReason(matchMethod ?? "None"));

		public static void LogControlResetLink(
			string action,
			int bindingPoint,
			int resetPoint,
			int bindingRight,
			int resetLeft,
			int resetUp,
			int resetDown)
			=> WriteProbeLineSafe(
				FormatTs() + " | CONTROL_RESET_LINK | Action=" + SanitizeReason(action ?? "?")
				+ " | BindingPoint=" + bindingPoint.ToString(CultureInfo.InvariantCulture)
				+ " | ResetPoint=" + resetPoint.ToString(CultureInfo.InvariantCulture)
				+ " | BindingRight=" + bindingRight.ToString(CultureInfo.InvariantCulture)
				+ " | ResetLeft=" + resetLeft.ToString(CultureInfo.InvariantCulture)
				+ " | ResetUp=" + resetUp.ToString(CultureInfo.InvariantCulture)
				+ " | ResetDown=" + resetDown.ToString(CultureInfo.InvariantCulture));

		public static void LogControlResetNav(
			string direction,
			string fromAction,
			string fromColumn,
			string toAction,
			string toColumn,
			bool moved)
			=> WriteProbeLineSafe(
				FormatTs() + " | CONTROL_RESET_NAV | Direction=" + SanitizeReason(direction ?? "?")
				+ " | FromAction=" + SanitizeReason(fromAction ?? "?")
				+ " | FromColumn=" + SanitizeReason(fromColumn ?? "?")
				+ " | ToAction=" + SanitizeReason(toAction ?? "?")
				+ " | ToColumn=" + SanitizeReason(toColumn ?? "?")
				+ " | Moved=" + (moved ? "True" : "False"));

		public static void LogControlResetConfirm(
			string action,
			string token,
			bool invoked,
			bool executedOnce,
			bool focusRestored)
			=> WriteProbeLineSafe(
				FormatTs() + " | CONTROL_RESET_CONFIRM | Action=" + SanitizeReason(action ?? "?")
				+ " | Token=" + SanitizeReason(token ?? "?")
				+ " | Invoked=" + (invoked ? "True" : "False")
				+ " | ExecutedOnce=" + (executedOnce ? "True" : "False")
				+ " | FocusRestored=" + (focusRestored ? "True" : "False"));

		public static void LogControlResetLeftInput(
			int epoch,
			string token,
			int beforePoint,
			string beforeAction,
			int linkedLeftPoint,
			string linkedLeftAction)
			=> WriteProbeLineSafe(
				FormatTs() + " | CONTROL_RESET_LEFT_INPUT | Epoch=" + epoch.ToString(CultureInfo.InvariantCulture)
				+ " | Token=" + SanitizeReason(token ?? "?")
				+ " | BeforePoint=" + beforePoint.ToString(CultureInfo.InvariantCulture)
				+ " | BeforeAction=" + SanitizeReason(beforeAction ?? "?")
				+ " | BeforeColumn=Reset"
				+ " | LinkedLeftPoint=" + linkedLeftPoint.ToString(CultureInfo.InvariantCulture)
				+ " | LinkedLeftAction=" + SanitizeReason(linkedLeftAction ?? "?"));

		public static void LogControlResetLeftChange(
			int epoch,
			int sequence,
			string caller,
			int fromPoint,
			string fromColumn,
			int toPoint,
			string toColumn,
			string reason)
			=> WriteProbeLineSafe(
				FormatTs() + " | CONTROL_RESET_LEFT_CHANGE | Epoch=" + epoch.ToString(CultureInfo.InvariantCulture)
				+ " | Sequence=" + sequence.ToString(CultureInfo.InvariantCulture)
				+ " | Caller=" + SanitizeReason(caller ?? "?")
				+ " | FromPoint=" + fromPoint.ToString(CultureInfo.InvariantCulture)
				+ " | FromColumn=" + SanitizeReason(fromColumn ?? "?")
				+ " | ToPoint=" + toPoint.ToString(CultureInfo.InvariantCulture)
				+ " | ToColumn=" + SanitizeReason(toColumn ?? "?")
				+ " | Reason=" + SanitizeReason(reason ?? "?"));

		public static void LogControlResetLeftResult(
			int epoch,
			int finalPoint,
			string finalAction,
			string finalColumn,
			int moveCount,
			bool duplicateInput,
			bool restoredToReset,
			bool rebuildOccurred)
			=> WriteProbeLineSafe(
				FormatTs() + " | CONTROL_RESET_LEFT_RESULT | Epoch=" + epoch.ToString(CultureInfo.InvariantCulture)
				+ " | ExpectedColumn=Binding"
				+ " | FinalPoint=" + finalPoint.ToString(CultureInfo.InvariantCulture)
				+ " | FinalAction=" + SanitizeReason(finalAction ?? "?")
				+ " | FinalColumn=" + SanitizeReason(finalColumn ?? "?")
				+ " | MoveCount=" + moveCount.ToString(CultureInfo.InvariantCulture)
				+ " | DuplicateInput=" + (duplicateInput ? "True" : "False")
				+ " | RestoredToReset=" + (restoredToReset ? "True" : "False")
				+ " | RebuildOccurred=" + (rebuildOccurred ? "True" : "False"));

		public static void LogControlResetFocusVisual(
			int currentPoint,
			string currentColumn,
			bool bindingHighlighted,
			bool resetHighlighted,
			string mouseHoverElement)
			=> WriteProbeLineSafe(
				FormatTs() + " | CONTROL_RESET_FOCUS_VISUAL | CurrentPoint="
				+ currentPoint.ToString(CultureInfo.InvariantCulture)
				+ " | CurrentColumn=" + SanitizeReason(currentColumn ?? "?")
				+ " | BindingHighlighted=" + (bindingHighlighted ? "True" : "False")
				+ " | ResetHighlighted=" + (resetHighlighted ? "True" : "False")
				+ " | MouseHoverElement=" + SanitizeReason(mouseHoverElement ?? "-"));

		public static void LogControlResetVerticalNav(
			string token,
			string fromAction,
			string toAction,
			int fromPoint,
			int toPoint)
			=> WriteProbeLineSafe(
				FormatTs() + " | CONTROL_RESET_VERTICAL_NAV | Token=" + SanitizeReason(token ?? "?")
				+ " | FromAction=" + SanitizeReason(fromAction ?? "?")
				+ " | ToAction=" + SanitizeReason(toAction ?? "?")
				+ " | FromPoint=" + fromPoint.ToString(CultureInfo.InvariantCulture)
				+ " | ToPoint=" + toPoint.ToString(CultureInfo.InvariantCulture)
				+ " | Column=Reset");

		public static void LogControlResetCenterScroll(
			string targetAction,
			int targetPoint,
			float targetTop,
			float targetCenter,
			float viewportTop,
			float viewportBottom,
			float oldScroll,
			float newScroll,
			bool clamped,
			bool sharedWithBindingColumn)
			=> WriteProbeLineSafe(
				FormatTs() + " | CONTROL_RESET_CENTER_SCROLL | TargetAction=" + SanitizeReason(targetAction ?? "?")
				+ " | TargetPoint=" + targetPoint.ToString(CultureInfo.InvariantCulture)
				+ " | TargetTop=" + targetTop.ToString("0.#", CultureInfo.InvariantCulture)
				+ " | TargetCenter=" + targetCenter.ToString("0.#", CultureInfo.InvariantCulture)
				+ " | ViewportTop=" + viewportTop.ToString("0.#", CultureInfo.InvariantCulture)
				+ " | ViewportBottom=" + viewportBottom.ToString("0.#", CultureInfo.InvariantCulture)
				+ " | OldScroll=" + oldScroll.ToString("0.#", CultureInfo.InvariantCulture)
				+ " | NewScroll=" + newScroll.ToString("0.#", CultureInfo.InvariantCulture)
				+ " | Clamped=" + (clamped ? "True" : "False")
				+ " | SharedWithBindingColumn=" + (sharedWithBindingColumn ? "True" : "False"));

		public static void LogControlResetScrollSkipped(string targetAction, string reason)
			=> WriteProbeLineSafe(
				FormatTs() + " | CONTROL_RESET_SCROLL_SKIPPED | TargetAction=" + SanitizeReason(targetAction ?? "?")
				+ " | Reason=" + SanitizeReason(reason ?? "Unknown"));

		private static void LogProbeErrorOnce(string where, Exception ex)
		{
			if (_probeErrorLoggedOnce)
				return;
			_probeErrorLoggedOnce = true;
			try
			{
				AdvancedKeybinds.Instance?.Logger?.Warn(
					"[GamePadProbe TEMP] " + where + ": " + ex.GetType().Name + ": " + ex.Message);
			}
			catch { /* ignore */ }
		}
	}
}
