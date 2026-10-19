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

		static void ResetAllDoubleTapRuntimes()
		{
			CancelDoubleTapOutputContributionsIfCurrentFrame();
			ClearDoubleTapOutputContributionScratch();
			_dtContinuousHeld.Clear();
			_hasDtInputMode = false;
			_dtNativeSourceMask = 0;
			// Always end native session (independent of active DT count).
			if (!DoubleTapNativeInput.LowLevelAvailable && !DoubleTapNativeInput.ThreadWindowsAvailable)
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

		// Cancel only evaluated state, not native observations made after the boundary.
		private static void CancelDoubleTapRuntimesForFocusBoundary()
		{
			CancelDoubleTapOutputContributionsIfCurrentFrame();
			ClearDoubleTapOutputContributionScratch();
			_dtContinuousHeld.Clear();
			CompiledBinding[] compiled = _compiled;
			if (compiled != null)
				for (int i = 0; i < compiled.Length; i++)
					compiled[i].DoubleTapRuntime?.Reset();
		}

		private static void RejectStaleDoubleTapFocusBatch()
		{
			SynchronizeFocusBoundaryState(DoubleTapNativeInput.Session.FocusBoundaryToken);
			// The old batch may have stepped a runtime after the first synchronization.
			CancelDoubleTapRuntimesForFocusBoundary();
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
			bool threadWinAvail = DoubleTapNativeInput.ThreadWindowsAvailable;
			int sourceMask = (windowAvail ? 1 : 0) | (llAvail ? 2 : 0) | (threadWinAvail ? 4 : 0);
			if (sourceMask != _dtNativeSourceMask)
			{
				if (((_dtNativeSourceMask & 2) != 0 && !llAvail) || (((_dtNativeSourceMask & 4) != 0) != threadWinAvail))
					DoubleTapNativeInput.Session.EndAndForgetPhysicalState();
				else
					DoubleTapNativeInput.Session.End();
				_dtContinuousHeld.Clear();
				_dtNativeSourceMask = sourceMask;
			}

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
			object? nativeBatchToken = null;

			if (needNativeKeyboard)
			{
				DoubleTapObservationSession session = DoubleTapNativeInput.Session;
				if (!windowAvail && !llAvail && !threadWinAvail)
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
						if (begun <= 0L)
							session.End();
					}

					if (session.IsActive)
					{
						drainCount = session.DrainForFocusBoundary(_dtNativeDrainScratch, 0,
							out drainGeneration, out seedHeldMask, out nativeBatchToken);
						if (!object.ReferenceEquals(session.FocusBoundaryToken, nativeBatchToken))
						{
							RejectStaleDoubleTapFocusBatch();
							return;
						}
						if (!session.IsActive || drainGeneration <= 0L || session.Generation != drainGeneration)
						{
							// Overflow / End: drop drained set; do not revive old pair.
							drainCount = 0;
						}
						else
						{
							nativeReady = true;
							SynchronizeFocusBoundaryState(nativeBatchToken);
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
				bool fired = false;
				bool snapshotLive = object.ReferenceEquals(
					DoubleTapNativeInput.Session.FocusBoundaryToken, _evaluationFocusBoundaryToken);

				if (IsWheelPrimary(primary))
				{
					if (snapshotLive)
					{
						bool pulse = IsDoubleTapPrimaryPulse(primary, dtEdges);
						fired = runtime.Step(new DoubleTapInput(
							update, nowMs, windowMs, down: false, pulse, blocked: false));
					}
				}
				else if (IsMouseButtonPrimary(primary) || IsPadPrimary(primary)
					|| (IsKeyboardPrimary(primary) && !windows))
				{
					// Snapshot-only (no edge mix). Non-Windows keyboard included.
					if (snapshotLive)
					{
						bool down = IsDoubleTapPrimaryDown(primary, rawSnapshot);
						fired = runtime.Step(new DoubleTapInput(
							update, nowMs, windowMs, down, pulse: false, blocked: false));
					}
				}
				else if (IsKeyboardPrimary(primary) && windows)
				{
					fired = false;
					if (nativeReady && drainCount > 0)
					{
						for (int e = 0; e < drainCount; e++)
						{
							if (!object.ReferenceEquals(DoubleTapNativeInput.Session.FocusBoundaryToken, nativeBatchToken))
							{
								RejectStaleDoubleTapFocusBatch();
								return;
							}
							if (!DoubleTapNativeInput.Session.IsActive
								|| DoubleTapNativeInput.Session.Generation != drainGeneration)
								break;
							DoubleTapObservedEvent ev = _dtNativeDrainScratch[e];
							bool ll = DoubleTapRuntime.IsLowLevelFiveKeyIdPublic(ev.KeyId);
							if (ll)
							{
								if (!llAvail && !(threadWinAvail && ((uint)(ev.KeyId & 0xFF) == 0x5B || (uint)(ev.KeyId & 0xFF) == 0x5C)))
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
							}
						}
					}
					if (!DoubleTapNativeInput.Session.IsActive
						|| DoubleTapNativeInput.Session.Generation != drainGeneration)
					{
						fired = false;
						runtime.Reset();
						_dtContinuousHeld.Remove(cb.ActionId);
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
				bool continueHeld = !fired && continuous && snapshotLive
					&& _dtContinuousHeld.Contains(cb.ActionId);
				bool keepHeld = continueHeld && IsDoubleTapPrimaryDown(primary, rawSnapshot);
				if (fired || continueHeld)
				{
					DoubleTapObservationSession session = DoubleTapNativeInput.Session;
					bool entered = IsKeyboardPrimary(primary) && windows && nativeReady
						? session.TryEnterFocusBoundary(drainGeneration, nativeBatchToken)
						: session.TryEnterFocusBoundary(_evaluationFocusBoundaryToken);
					if (!entered)
					{
						RejectStaleDoubleTapFocusBatch();
						return;
					}
					try
					{
						if (fired)
						{
							if (continuous)
								_dtContinuousHeld.Add(cb.ActionId);
							RecordDoubleTapOutputContribution(update, triggerId);
							JustPressedActions[triggerId] = true;
							CurrentActions[triggerId] = true;
						}
						else if (keepHeld)
						{
							RecordDoubleTapOutputContribution(update, triggerId);
							CurrentActions[triggerId] = true;
						}
						else
							_dtContinuousHeld.Remove(cb.ActionId);
					}
					finally
					{
						session.ExitFocusBoundary();
					}
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

	}
}
