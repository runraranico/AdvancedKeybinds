using System;
using System.Collections.Generic;
using AdvancedKeybinds.Core;
using AdvancedKeybinds.Features.DoubleTap;
using AdvancedKeybinds.UI;
using Terraria;
using Terraria.GameInput;

namespace AdvancedKeybinds.Binding
{
	/// <summary>
	/// Double-tap fire path hooked from <see cref="BindingInputEngine.Update"/>.
 	/// Windows keyboard DT uses DoubleTapNativeInput.Session only.
	/// No snapshot/edge fallback on Windows keyboard.
	/// Does not change normal input Apply/Queue/return/hook install.
	/// </summary>
	public static partial class BindingInputEngine
	{
		const int NativeDrainCapacity = 128;

		static readonly HashSet<string> _dtRawSnapshotScratch =
			new(StringComparer.OrdinalIgnoreCase);
		static readonly HashSet<string> _dtEdgeScratch =
			new(StringComparer.OrdinalIgnoreCase);

		static readonly HashSet<string> _dtAddedJustPressedScratch =
			new(StringComparer.OrdinalIgnoreCase);
		static readonly HashSet<string> _dtAddedCurrentScratch =
			new(StringComparer.OrdinalIgnoreCase);
		static readonly HashSet<string> _dtOutputSeenTriggersScratch =
			new(StringComparer.OrdinalIgnoreCase);
		static ulong _dtOutputContributionFrame = ulong.MaxValue;

		/// <summary>Continuous Current latch for the six zoom/alpha vanilla IDs (non-wheel only).</summary>
		static readonly HashSet<string> _dtContinuousHeld =
			new(StringComparer.Ordinal);

		static readonly DoubleTapObservedEvent[] _dtNativeDrainScratch =
			new DoubleTapObservedEvent[NativeDrainCapacity];

		static int _activeDoubleTapCount;
		static bool _hasDtInputMode;
		static InputMode _dtTrackedInputMode;
		static int _dtNativeSourceMask;

		/// <summary>
		/// Diag SourceState: whether current subscribers already received the current Window/LL pair.
		/// Cleared on Reset and whenever a valid DT Update runs with zero subscribers.
		/// </summary>
		static bool _dtDiagSourceStateSynced;
		static bool _dtDiagSourceStatePrevWindow;
		static bool _dtDiagSourceStatePrevLowLevel;

		static void ResetAllDoubleTapRuntimes()
		{
#if AK_DOUBLETAP_DIAGNOSTICS
			DoubleTapTrace.State("DT:リセット直前");
#endif
			CancelDoubleTapOutputContributionsIfCurrentFrame();
			ClearDoubleTapOutputContributionScratch();
			_dtContinuousHeld.Clear();
			_hasDtInputMode = false;
			_dtNativeSourceMask = 0;
			_dtDiagSourceStateSynced = false;
			// Always end native session (independent of active DT count).
			if (!DoubleTapNativeInput.LowLevelAvailable)
				DoubleTapNativeInput.Session.EndAndForgetPhysicalState();
			else
				DoubleTapNativeInput.Session.End();
			if (_activeDoubleTapCount <= 0)
				return;
			CompiledBinding[] compiled = _compiled;
			if (compiled == null)
				return;
			for (int i = 0; i < compiled.Length; i++)
				compiled[i].DoubleTapRuntime?.Reset();
		}

		static void ClearDoubleTapFrameScratches()
		{
			_dtRawSnapshotScratch.Clear();
			_dtEdgeScratch.Clear();
			ClearDoubleTapOutputContributionScratch();
		}

		static void ClearDoubleTapOutputContributionScratch()
		{
			_dtAddedJustPressedScratch.Clear();
			_dtAddedCurrentScratch.Clear();
			_dtOutputSeenTriggersScratch.Clear();
			_dtOutputContributionFrame = ulong.MaxValue;
		}

		static void CancelDoubleTapOutputContributionsIfCurrentFrame()
		{
			if (_dtOutputContributionFrame != Main.GameUpdateCount)
				return;

			foreach (string triggerId in _dtAddedJustPressedScratch)
				JustPressedActions.Remove(triggerId);

			foreach (string triggerId in _dtAddedCurrentScratch)
				CurrentActions.Remove(triggerId);
		}

		static bool IsDoubleTapInputModeMatch(string storageKey)
		{
			if (DoubleTapTargetPolicy.IsVanillaTarget(storageKey))
				return CanReplaceVanillaDoubleTap(storageKey);
			bool pad = ControlsEditTargetPolicy.IsGamePadStorageKey(storageKey)
				|| ControlsGameUiSeparationPolicy.IsGamePadLegacySharedStorageKey(storageKey);
			bool gpMode = PlayerInput.CurrentInputMode == InputMode.XBoxGamepad
				|| PlayerInput.CurrentInputMode == InputMode.XBoxGamepadUI;
			return pad == gpMode;
		}

		/// <summary>
		/// Vanilla DT may replace/fire only on Keyboard gameplay: not blocked, Inventory only when closed.
		/// </summary>
		static bool CanReplaceVanillaDoubleTap(string id)
		{
			if (PlayerInput.CurrentInputMode != InputMode.Keyboard || IsDoubleTapEnvironmentBlocked())
				return false;
			return id != "Inventory" || !Main.playerInventory;
		}

		internal static bool IsDoubleTapEnvironmentBlocked()
		{
			if (!Main.hasFocus)
				return true;
			if (Main.drawingPlayerChat || Main.editSign || Main.editChest)
				return true;
			if (Main.ingameOptionsWindow || Main.inFancyUI || Main.blockInput)
				return true;
			if (ComboPanel.IsOpen)
				return true;
			if (IsRecording || Main.gameMenu || Main.LocalPlayer == null)
				return true;
			return false;
		}

		static bool IsDoubleTapPrimaryDown(string primaryToken, HashSet<string> rawSnapshot)
		{
			if (string.IsNullOrEmpty(primaryToken))
				return false;
			if (StableKeyToken.IsMouseWheelToken(primaryToken))
				return false;
			return BindingPhysicalHeldMatch.IsHeld(primaryToken, rawSnapshot);
		}

		static bool IsDoubleTapPrimaryPulse(string primaryToken, HashSet<string> dtEdges)
			=> BindingPhysicalHeldMatch.IsHeld(primaryToken, dtEdges);

		static void CopySnapshot(HashSet<string> source, HashSet<string> dest)
		{
			dest.Clear();
			foreach (string token in source)
				dest.Add(token);
		}

		static void CaptureDoubleTapRawSnapshot(HashSet<string> pressedAfterFill)
		{
			CopySnapshot(pressedAfterFill, _dtRawSnapshotScratch);
		}

		static void AddDoubleTapEdge(string token)
		{
			if (!string.IsNullOrEmpty(token))
				_dtEdgeScratch.Add(token);
		}

		static void CaptureDoubleTapWheelEdgesFromPressed(HashSet<string> pressedAfterWheelAppend)
		{
			foreach (string token in pressedAfterWheelAppend)
			{
				if (StableKeyToken.IsMouseWheelToken(token))
					_dtEdgeScratch.Add(token);
			}
		}

		static bool IsWheelPrimary(string primary)
			=> !string.IsNullOrEmpty(primary) && StableKeyToken.IsMouseWheelToken(primary);

		static bool IsMouseButtonPrimary(string primary)
			=> !string.IsNullOrEmpty(primary) && StableKeyToken.IsMouseToken(primary);

		static bool IsPadPrimary(string primary)
			=> !string.IsNullOrEmpty(primary) && GamePadToken.IsPadToken(primary);

		static bool IsKeyboardPrimary(string primary)
		{
			if (string.IsNullOrEmpty(primary))
				return false;
			if (IsWheelPrimary(primary) || IsMouseButtonPrimary(primary) || IsPadPrimary(primary))
				return false;
			return true;
		}

		/// <summary>
		/// Gate symbol for external ConnectionProbe. Called after EvaluateAllCompiledBindings.
		/// </summary>
		private static void EvaluateDoubleTapUpdatePass(
			ulong update,
			double nowMs,
			HashSet<string> rawSnapshot,
			HashSet<string> dtEdges)
		{
			if (_activeDoubleTapCount <= 0)
				return;

			InputMode mode = PlayerInput.CurrentInputMode;
			if (_hasDtInputMode && mode != _dtTrackedInputMode)
				ResetAllDoubleTapRuntimes();
			_hasDtInputMode = true;
			_dtTrackedInputMode = mode;

			if (IsDoubleTapEnvironmentBlocked())
			{
				ResetAllDoubleTapRuntimes();
				return;
			}

			CompiledBinding[] compiled = _compiled;
			if (compiled == null)
				return;

			bool windows = DoubleTapNativeInput.IsWindows;
			bool windowAvail = DoubleTapNativeInput.WindowAvailable;
			bool llAvail = DoubleTapNativeInput.LowLevelAvailable;
			int sourceMask = (windowAvail ? 1 : 0) | (llAvail ? 2 : 0);
			if (sourceMask != _dtNativeSourceMask)
			{
				if ((_dtNativeSourceMask & 2) != 0 && !llAvail)
					DoubleTapNativeInput.Session.EndAndForgetPhysicalState();
				else
					DoubleTapNativeInput.Session.End();
				_dtContinuousHeld.Clear();
				_dtNativeSourceMask = sourceMask;
			}

			PublishDoubleTapDiagSourceStateIfNeeded(windowAvail, llAvail, update);

			bool needNativeKeyboard = false;
			if (windows)
			{
				for (int i = 0; i < compiled.Length; i++)
				{
					CompiledBinding cb = compiled[i];
					if (cb.DoubleTapRuntime == null)
						continue;
					if (!IsDoubleTapInputModeMatch(cb.ActionId))
						continue;
					if (IsKeyboardPrimary(cb.DoubleTapPrimaryToken))
					{
						needNativeKeyboard = true;
						break;
					}
				}
			}

			int drainCount = 0;
			long drainGeneration = 0L;
			bool nativeReady = false;
			int seedHeldMask = 0;

			if (needNativeKeyboard)
			{
				DoubleTapObservationSession session = DoubleTapNativeInput.Session;
				if (!windowAvail && !llAvail)
				{
					session.End();
				}
				else
				{
					if (!session.IsActive)
					{
						long begun = session.Begin(
							DoubleTapNativeInput.ReadInitialHeldMask(),
							DoubleTapNativeInput.NowMs);
#if AK_DOUBLETAP_DIAGNOSTICS
						DoubleTapTrace.State("DT:セッション開始結果");
#endif
						if (begun <= 0L)
							session.End();
					}

					if (session.IsActive)
					{
						long genBefore = session.Generation;
						seedHeldMask = session.SeedHeldMask;
						drainCount = session.Drain(_dtNativeDrainScratch, 0);
						if (!session.IsActive || session.Generation != genBefore)
						{
							// Overflow / End: drop drained set; do not revive old pair.
							drainCount = 0;
						}
						else
						{
							drainGeneration = genBefore;
							nativeReady = true;
						}
					}
				}
			}

			// Read after Begin: initial seed acquisition refreshes the cached context.
			bool jpPhysical = DoubleTapNativeInput.JapanesePhysical;
			bool jpLayout = DoubleTapNativeInput.JapaneseLayout;

			if (!needNativeKeyboard)
				DoubleTapNativeInput.Session.End();

			for (int i = 0; i < compiled.Length; i++)
			{
				CompiledBinding cb = compiled[i];
				DoubleTapRuntime runtime = cb.DoubleTapRuntime;
				if (runtime == null)
					continue;

				if (!IsDoubleTapInputModeMatch(cb.ActionId))
				{
					runtime.Reset();
					_dtContinuousHeld.Remove(cb.ActionId);
					continue;
				}

				string primary = cb.DoubleTapPrimaryToken;
				int windowMs = cb.DoubleTapWindowMs;
				bool fired;
				bool stage3Matched = false;
				DoubleTapObservedEvent stage3Ev = default;
				DoubleTapDiagSource stage3Source = DoubleTapDiagSource.Unknown;
				bool stage3SourceAvailable = false;
				bool stage3SeedHeld = false;

				if (IsWheelPrimary(primary))
				{
					bool pulse = IsDoubleTapPrimaryPulse(primary, dtEdges);
					fired = runtime.Step(new DoubleTapInput(
						update, nowMs, windowMs, down: false, pulse, blocked: false));
				}
				else if (IsMouseButtonPrimary(primary) || IsPadPrimary(primary)
					|| (IsKeyboardPrimary(primary) && !windows))
				{
					// Snapshot-only (no edge mix). Non-Windows keyboard included.
					bool down = IsDoubleTapPrimaryDown(primary, rawSnapshot);
					fired = runtime.Step(new DoubleTapInput(
						update, nowMs, windowMs, down, pulse: false, blocked: false));
				}
				else if (IsKeyboardPrimary(primary) && windows)
				{
					fired = false;
					bool heldMatched = false;
					DoubleTapObservedEvent matchedEv = default;
					if (nativeReady && drainCount > 0)
					{
						for (int e = 0; e < drainCount; e++)
						{
							DoubleTapObservedEvent ev = _dtNativeDrainScratch[e];
							bool ll = DoubleTapRuntime.IsLowLevelFiveKeyIdPublic(ev.KeyId);
							if (ll)
							{
								if (!llAvail)
									continue;
							}
							else if (!windowAvail)
							{
								continue;
							}

							if (runtime.ObserveKeyboard(
								ev,
								drainGeneration,
								seedHeldMask,
								primary,
								windowMs,
								jpPhysical,
								jpLayout))
							{
								fired = true;
								if (DoubleTapDiagFeed.HasSubscribers)
								{
									matchedEv = ev;
									heldMatched = true;
								}
							}
#if AK_DOUBLETAP_DIAGNOSTICS
							DoubleTapTrace.Evaluated(cb.ActionId, ev.KeyId, ev.TimestampMs, fired);
#endif
						}
					}
					if (!DoubleTapNativeInput.Session.IsActive
						|| DoubleTapNativeInput.Session.Generation != drainGeneration)
					{
						fired = false;
						heldMatched = false;
						runtime.Reset();
						_dtContinuousHeld.Remove(cb.ActionId);
					}
					else if (fired && heldMatched)
					{
						try
						{
							bool ll = DoubleTapRuntime.IsLowLevelFiveKeyIdPublic(matchedEv.KeyId);
							DoubleTapDiagSource source = ll
								? DoubleTapDiagSource.LowLevel
								: DoubleTapDiagSource.Window;
							bool sourceAvailable = ll ? llAvail : windowAvail;
							bool seedHeld = ll
								&& IsDiagLlSeedHeld(matchedEv.KeyId, seedHeldMask);
							stage3Matched = DoubleTapDiagPolicy.TryPublishMatched(
								matchedEv.KeyId,
								cb.ActionId,
								source,
								matchedEv.TimestampMs,
								matchedEv.Update,
								sessionActive: true,
								sourceAvailable,
								seedHeld,
								matchedEv.Down);
							if (stage3Matched)
							{
								stage3Ev = matchedEv;
								stage3Source = source;
								stage3SourceAvailable = sourceAvailable;
								stage3SeedHeld = seedHeld;
							}
						}
						catch
						{
							stage3Matched = false;
						}
					}
					// No snapshot/edge fallback when native unavailable.
				}
				else
				{
					fired = false;
				}

				string triggerId = ControlsGameUiSeparationPolicy.ToTriggerActionId(cb.ActionId);
				bool continuous = DoubleTapTargetPolicy.IsContinuousVanillaTarget(cb.ActionId)
					&& !IsWheelPrimary(primary);

				if (fired)
				{
					if (continuous)
						_dtContinuousHeld.Add(cb.ActionId);
					RecordDoubleTapOutputContribution(update, triggerId);
					JustPressedActions[triggerId] = true;
					CurrentActions[triggerId] = true;
#if AK_DOUBLETAP_DIAGNOSTICS
				DoubleTapTrace.Output(cb.ActionId);
#endif
					if (stage3Matched)
					{
						try
						{
							DoubleTapDiagPolicy.TryPublishFired(
								stage3Ev.KeyId,
								cb.ActionId,
								stage3Source,
								stage3Ev.TimestampMs,
								stage3Ev.Update,
								sessionActive: true,
								stage3SourceAvailable,
								stage3SeedHeld,
								stage3Ev.Down,
								evaluationFrame: update == 0UL ? -1 : unchecked((int)update));
						}
						catch { /* diag must not affect input */ }
					}
				}
				else if (continuous && _dtContinuousHeld.Contains(cb.ActionId))
				{
					if (IsDoubleTapPrimaryDown(primary, rawSnapshot))
					{
						RecordDoubleTapOutputContribution(update, triggerId);
						CurrentActions[triggerId] = true;
					}
					else
						_dtContinuousHeld.Remove(cb.ActionId);
				}
			}
		}

		static void RecordDoubleTapOutputContribution(ulong update, string triggerId)
		{
			if (_dtOutputContributionFrame != update)
			{
				ClearDoubleTapOutputContributionScratch();
				_dtOutputContributionFrame = update;
			}

			if (!_dtOutputSeenTriggersScratch.Add(triggerId))
				return;

			bool hadJust =
				JustPressedActions.TryGetValue(triggerId, out bool just) && just;
			bool hadCurrent =
				CurrentActions.TryGetValue(triggerId, out bool current) && current;

			if (!hadJust)
				_dtAddedJustPressedScratch.Add(triggerId);
			if (!hadCurrent)
				_dtAddedCurrentScratch.Add(triggerId);
		}

		/// <summary>
		/// LL seed bits: LWin=1&lt;&lt;0, RWin=1&lt;&lt;1, Caps=1&lt;&lt;2. Non-LL / other LL → false.
		/// </summary>
		static bool IsDiagLlSeedHeld(int keyId, int seedHeldMask)
		{
			if (!DoubleTapRuntime.IsLowLevelFiveKeyIdPublic(keyId))
				return false;

			uint vk = (uint)(keyId & 0xFF);
			int bit;
			if (vk == 0x5Bu)
				bit = 1 << 0;
			else if (vk == 0x5Cu)
				bit = 1 << 1;
			else if (vk == 0x14u)
				bit = 1 << 2;
			else
				return false;

			return (seedHeldMask & bit) != 0;
		}

		/// <summary>
		/// Passive SourceState for Window/LL availability. Valid DT Update only (caller is past block gate).
		/// Zero subscribers → synced=false and return. Unsynced → current pair once. Synced → change only.
		/// </summary>
		static void PublishDoubleTapDiagSourceStateIfNeeded(
			bool windowAvailable,
			bool lowLevelAvailable,
			ulong update)
		{
			if (!DoubleTapDiagFeed.HasSubscribers)
			{
				_dtDiagSourceStateSynced = false;
				return;
			}

			if (!_dtDiagSourceStateSynced)
			{
				long timestampMs = ToDoubleTapDiagTimestampMs(DoubleTapNativeInput.NowMs);
				bool sessionActive = DoubleTapNativeInput.Session.IsActive;
				PublishDoubleTapDiagSourceState(
					DoubleTapDiagSource.Window, windowAvailable, timestampMs, update, sessionActive);
				PublishDoubleTapDiagSourceState(
					DoubleTapDiagSource.LowLevel, lowLevelAvailable, timestampMs, update, sessionActive);
				_dtDiagSourceStatePrevWindow = windowAvailable;
				_dtDiagSourceStatePrevLowLevel = lowLevelAvailable;
				_dtDiagSourceStateSynced = true;
				return;
			}

			bool windowChanged = windowAvailable != _dtDiagSourceStatePrevWindow;
			bool lowLevelChanged = lowLevelAvailable != _dtDiagSourceStatePrevLowLevel;
			if (!windowChanged && !lowLevelChanged)
				return;

			long changeTimestampMs = ToDoubleTapDiagTimestampMs(DoubleTapNativeInput.NowMs);
			bool changeSessionActive = DoubleTapNativeInput.Session.IsActive;
			if (windowChanged)
			{
				PublishDoubleTapDiagSourceState(
					DoubleTapDiagSource.Window,
					windowAvailable,
					changeTimestampMs,
					update,
					changeSessionActive);
				_dtDiagSourceStatePrevWindow = windowAvailable;
			}

			if (lowLevelChanged)
			{
				PublishDoubleTapDiagSourceState(
					DoubleTapDiagSource.LowLevel,
					lowLevelAvailable,
					changeTimestampMs,
					update,
					changeSessionActive);
				_dtDiagSourceStatePrevLowLevel = lowLevelAvailable;
			}
		}

		static void PublishDoubleTapDiagSourceState(
			DoubleTapDiagSource source,
			bool sourceAvailable,
			long timestampMs,
			ulong update,
			bool sessionActive)
		{
			DoubleTapDiagFeed.Publish(new DoubleTapDiagEvent(
				DoubleTapDiagKey.None,
				DoubleTapDiagStage.SourceState,
				DoubleTapDiagReason.None,
				source,
				timestampMs,
				update,
				vk: 0u,
				scan: 0,
				extended: false,
				sessionActive,
				sourceAvailable,
				seedHeld: false,
				actionId: string.Empty,
				isDown: false));
		}

		/// <summary>Same safe conversion as DoubleTapDiagPolicy.ToDiagTimestampMs (NaN/Inf/neg → -1).</summary>
		static long ToDoubleTapDiagTimestampMs(double eventMs)
		{
			if (double.IsNaN(eventMs) || double.IsInfinity(eventMs) || eventMs < 0.0)
				return -1L;
			return (long)eventMs;
		}
	}
}
