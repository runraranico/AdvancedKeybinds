using System;
using System.Collections.Generic;
using System.Diagnostics;
using AdvancedKeybinds.Core;
using AdvancedKeybinds.Diagnostics;
using AdvancedKeybinds.Features.DoubleTap;
using AdvancedKeybinds.Patches;
using Microsoft.Xna.Framework.Input;
using Terraria;
using Terraria.GameInput;

namespace AdvancedKeybinds.Binding
{
	/// <summary>
	/// Evaluates advanced combo bindings each frame and exposes just-pressed / current state per action.
	/// A combo fires when every filled slot key is held. A one-slot hold fires after the hold duration
	/// (while held, or on release depending on <see cref="ComboBinding.HoldOnRelease"/>).
	/// </summary>
	public static partial class BindingInputEngine
	{
		private sealed class HoldTracker
		{
			public double HeldMs;
			public bool Fired;
		}

		private static readonly Dictionary<string, bool> JustPressedActions = new();
		private static readonly Dictionary<string, bool> CurrentActions = new();
		private static readonly HashSet<string> SuppressedComboTokens = new(StringComparer.OrdinalIgnoreCase);
		private static readonly Dictionary<string, HoldTracker> HoldTrackers = new();
		private static readonly AkDecisionFrameGate DecisionFrameGate = new();
		/// <summary>Previous-frame logical OR held for AlternateGamePad bindings (HOLD/OR edges).</summary>
		private static readonly Dictionary<string, bool> AlternateOrHeldPrevious = new(StringComparer.Ordinal);
		/// <summary>Previous-frame held for keyboard AdditionalOrToken (instant OR, independent of AND Hold).</summary>
		private static readonly Dictionary<string, bool> AdditionalOrHeldPrevious = new(StringComparer.Ordinal);
		/// <summary>Previous-frame AND held when Inventory Escape+Tab vanilla OR is mirrored by AK.</summary>
		private static readonly Dictionary<string, bool> SharedUiBackAndHeldPrevious = new(StringComparer.Ordinal);

		/// <summary>
		/// Escape hold already armed Inventory (vanilla JP or AK AND mirror) — block second AND edge
		/// until Escape is physically up (no long settle: that blocked rapid re-taps).
		/// </summary>
		private static readonly HashSet<string> SharedUiEscapeHoldLatch = new(StringComparer.Ordinal);

		private static HashSet<string> _previousPressed = new(StringComparer.OrdinalIgnoreCase);
		private static readonly HashSet<string> _pressedScratch = new(StringComparer.OrdinalIgnoreCase);
		private static readonly HashSet<string> _toggleEdgesScratch = new(StringComparer.OrdinalIgnoreCase);
		private static readonly HashSet<string> _winEdgesScratch = new(StringComparer.OrdinalIgnoreCase);
		private static readonly List<string> _removeScratch = new(8);

		private static ulong _lastUpdateGameFrame = ulong.MaxValue;
		private static bool _updatedThisGameFrame;

		/// <summary>
		/// In-world ownership watch: sparse checks via <see cref="OwnershipEnsureWatchSchedule"/>.
		/// Permanent per-frame scans are forbidden (including while profile is not ready).
		/// </summary>
		private static int _ownershipWatchElapsed;

		/// <summary>Set while a slot is being captured so gameplay bindings don't fire during setup.</summary>
		public static bool IsRecording { get; set; }

		public static void InvalidateBindings()
		{
			HoldTrackers.Clear();
			AlternateOrHeldPrevious.Clear();
			AdditionalOrHeldPrevious.Clear();
			SharedUiBackAndHeldPrevious.Clear();
			SharedUiEscapeHoldLatch.Clear();
			JustPressedActions.Clear();
			CurrentActions.Clear();
			ResetAllDoubleTapRuntimes();
			MarkCompiledDirty();
			_lastUpdateGameFrame = ulong.MaxValue;
			_updatedThisGameFrame = false;
			RequestVanillaOwnershipEnsure();
		}

		/// <summary>Re-run KeyStatus ownership repair watch (Build+Reload / world load / profile rebuild).</summary>
		public static void RequestVanillaOwnershipEnsure()
		{
			_ownershipWatchElapsed = 0;
		}

		public static void Unload()
		{
			InvalidateBindings();
			_hookManagedPromotionAttempted = false;
		}

		private static bool _hookManagedPromotionAttempted;

		public static void Update()
		{
			// Update once / read many: same GameUpdateCount skips re-evaluation.
			ulong frame = Main.GameUpdateCount;
			// Cancellation is observable even on a read-many call in this update.
			// Do not re-evaluate or drain normal input a second time.
			if (_activeDoubleTapCount > 0 && IsDoubleTapEnvironmentBlocked())
				ResetAllDoubleTapRuntimes();
			if (frame == _lastUpdateGameFrame && _updatedThisGameFrame)
				return;

			// Gameplay trigger evaluation is in-world only. Title / menu still run PlayerInput detour
			// (WinKey suppressor, ComboPanel capture, Controls UI) outside this method.
			if (Main.gameMenu || Main.LocalPlayer == null || IsRecording)
			{
				JustPressedActions.Clear();
				CurrentActions.Clear();
				SuppressedComboTokens.Clear();
				PreviousCurrentActions.Clear();
				_lastUpdateGameFrame = frame;
				_updatedThisGameFrame = true;
				_lastEvalBindCount = 0;
				ResetAllDoubleTapRuntimes();
				return;
			}

			_lastUpdateGameFrame = frame;
			_updatedThisGameFrame = true;

			// Sparse ownership watch (not every frame). Immediate Ensure still runs at
			// Initialize / PostSetupContent / OnWorldLoad. Setting changes call RequestVanillaOwnershipEnsure.
			if (OwnershipEnsureWatchSchedule.IsWatchActive(_ownershipWatchElapsed))
			{
				_ownershipWatchElapsed++;
				if (OwnershipEnsureWatchSchedule.ShouldCheck(_ownershipWatchElapsed))
				{
					int repaired = BindingStorage.EnsureVanillaOwnershipConsistent(
						"engine-update", out bool profileReady);
					_ownershipWatchElapsed = OwnershipEnsureWatchSchedule.NextElapsedAfterEnsure(
						_ownershipWatchElapsed, profileReady, repaired);
				}
			}

			EnsureCompiled();
			CapturePreviousCurrent();

			BindingPhysicalHeldMatch.BeginFrame(unchecked((int)frame));

			if (!_hookManagedPromotionAttempted)
			{
				_hookManagedPromotionAttempted = true;
				BindingStorage.PromoteHookManagedVanillaBindings();
			}

			JustPressedActions.Clear();
			CurrentActions.Clear();
			SuppressedComboTokens.Clear();
			ClearDoubleTapFrameScratches();

			WindowsKeySuppressor.PollGameplayToggleLockEdges();

			HashSet<string> toggleEdges = _toggleEdgesScratch;
			toggleEdges.Clear();
			foreach (string edge in WindowsKeySuppressor.DrainGameplayToggleLockEdges())
				toggleEdges.Add(edge);

			HashSet<string> winEdges = _winEdgesScratch;
			winEdges.Clear();
			foreach (string edge in WindowsKeySuppressor.DrainGameplayWinEdges())
				winEdges.Add(edge);

			HashSet<string> pressed = _pressedScratch;
			KeyToken.FillSnapshotPressedTokens(pressed, includeUnconfirmedScanHeld: true);
			// Raw DT snapshot before latch / edge mix-in. Skip entirely when no active DT rows.
			bool captureDt = _activeDoubleTapCount > 0;
			if (captureDt)
				CaptureDoubleTapRawSnapshot(pressed);
			WindowsKeySuppressor.ReconcileGenericScanHeldGhosts();
			foreach (string edge in toggleEdges)
			{
				pressed.Add(edge);
				if (captureDt)
					AddDoubleTapEdge(edge);
			}
			foreach (string edge in winEdges)
			{
				pressed.Add(edge);
				if (captureDt)
					AddDoubleTapEdge(edge);
			}
			foreach (string edge in WindowsKeySuppressor.DrainGameplayKanaEdges())
			{
				pressed.Add(edge);
				if (captureDt)
					AddDoubleTapEdge(edge);
			}
			foreach (string edge in WindowsKeySuppressor.DrainGameplayHankakuZenkakuEdges())
			{
				pressed.Add(edge);
				if (captureDt)
					AddDoubleTapEdge(edge);
			}
			foreach (string edge in WindowsKeySuppressor.DrainGameplayJisIntlEdges())
			{
				pressed.Add(edge);
				if (captureDt)
					AddDoubleTapEdge(edge);
			}
			foreach (string edge in WindowsKeySuppressor.DrainGameplayPhysicalEdges())
			{
				pressed.Add(edge);
				if (captureDt)
					AddDoubleTapEdge(edge);
			}
			MouseInputHelper.AppendFrameWheelEdges(pressed);
			if (captureDt)
				CaptureDoubleTapWheelEdgesFromPressed(pressed);
			SupplementActiveBindingFallbackTokens(pressed);

			SustainToggleLatchesForPartialCombos(pressed);

			HashSet<string> latchedToggles = WindowsKeySuppressor.SnapshotLatchedToggleTokens();
			foreach (string token in latchedToggles)
				pressed.Add(token);

			FireHookManagedTapBindings(toggleEdges, winEdges);

			// Release-to-fire holds are resolved before trackers are cleared for the released key.
			ProcessHoldReleases(pressed);

			// Same physical key may appear as Keys + SC (or Spain 0x040A/0x0C0A detection aliases).
			// Collapse before AND so one physical press satisfies one binding condition only.
			BindingPhysicalHeldMatch.CollapsePhysicalAliasesInPlace(pressed);

			EvaluateAllCompiledBindings(pressed, toggleEdges, winEdges);

			if (captureDt)
			{
				double nowMs = Stopwatch.GetTimestamp() * (1000.0 / Stopwatch.Frequency);
				EvaluateDoubleTapUpdatePass(
					frame,
					nowMs,
					_dtRawSnapshotScratch,
					_dtEdgeScratch);
			}

			PruneHoldTrackers(pressed);

			WindowsKeySuppressor.EndToggleComboLatchFrame(latchedToggles);

			_previousPressed.Clear();
			foreach (string token in pressed)
				_previousPressed.Add(token);
		}

		private static void EvaluateAllCompiledBindings(HashSet<string> pressed, HashSet<string> toggleEdges, HashSet<string> winEdges)
		{
			CompiledBinding[] compiled = _compiled;
			int eval = 0;
			for (int i = 0; i < compiled.Length; i++)
			{
				CompiledBinding cb = compiled[i];
				if (cb.IsPlainSingleTap)
					continue;

				if (cb.IsDoubleTap)
				{
					if (cb.OwnsDoubleTapInput && cb.InactiveDoubleTapView != null)
					{
						eval++;
						EvaluateCombo(cb.ActionId, cb.InactiveDoubleTapView, pressed, toggleEdges, winEdges);
					}
					continue;
				}

				ComboBinding combo = BindingStorage.Get(cb.ActionId);
				if (combo == null)
					continue;

				if (GamePadTwoKeyTerrariaOrPolicy.IsTargetStorageKey(cb.ActionId)
					|| combo.UseTerrariaKeyStatusOr)
					continue;

				// Vanilla Jump/MouseLeft etc.: KeyStatus Buttons name owns fire (avoid dual-fire).
				// MOD GP singles (e.g. Pad:Back / Select) must still Evaluate — Keyboard Advanced
				// ownership otherwise wipes the shared trigger and ModKeybind never sees JustPressed.
				if (ControlsEditTargetPolicy.IsGamePadStorageKey(cb.ActionId)
					&& ControlsEditTargetPolicy.IsVanillaOwnedGamePadOrPair(
						combo.Hold, combo.Keys, combo.AlternateGamePadToken))
				{
					string skipTrigger = ControlsGameUiSeparationPolicy.ToTriggerActionId(cb.ActionId);
					if (!ControlsGameUiSeparationPolicy.IsModKeybindActionId(skipTrigger))
						continue;
				}

				// Stale legacy GP:{action} must not fire when GP:G: / GP:U: has effective inputs,
				// or when ClearBindings marked the trigger for evaluate-only suppress (data kept).
				if (ControlsGameUiSeparationPolicy.ShouldSuppressLegacyGamePadEvaluate(
					    cb.ActionId,
					    BindingStorage.HasLegacyEvaluateSuppressingSibling))
					continue;

				eval++;
				EvaluateCombo(cb.ActionId, combo, pressed, toggleEdges, winEdges);
			}

			_lastEvalBindCount = eval;
		}

		private static void FireHookManagedTapBindings(HashSet<string> toggleEdges, HashSet<string> winEdges)
		{
			EnsureCompiled();
			for (int i = 0; i < HookManagedTapIndices.Count; i++)
			{
				int idx = HookManagedTapIndices[i];
				CompiledBinding cb = _compiled[idx];
				string key = cb.Keys[0];
				if (BindingRestrictions.IsToggleLockToken(key) && toggleEdges.Contains(key))
					JustPressedActions[ControlsGameUiSeparationPolicy.ToTriggerActionId(cb.ActionId)] = true;
				else if (BindingRestrictions.IsWindowsKeyToken(key) && winEdges.Contains(key))
					JustPressedActions[ControlsGameUiSeparationPolicy.ToTriggerActionId(cb.ActionId)] = true;
			}
		}

		/// <summary>
		/// Ensures saved <c>VK:</c> / <c>SC:</c> binding tokens appear in <paramref name="pressed"/>
		/// even when Snapshot only had Keys names or missed a hook edge.
		/// Registration and activation must share the same identity (spec §2E).
		/// </summary>
		private static void SupplementActiveBindingFallbackTokens(HashSet<string> pressed)
		{
			if (pressed == null || !OperatingSystem.IsWindows() || !Main.hasFocus)
				return;

			EnsureCompiled();
			CompiledBinding[] compiled = _compiled;
			for (int c = 0; c < compiled.Length; c++)
			{
				string[] keys = compiled[c].Keys;
				for (int i = 0; i < keys.Length; i++)
					TrySupplementFallbackToken(pressed, keys[i]);

				TrySupplementFallbackToken(pressed, compiled[c].AdditionalOrToken);
			}
		}

		private static void TrySupplementFallbackToken(HashSet<string> pressed, string token)
		{
			if (string.IsNullOrEmpty(token) || pressed.Contains(token))
				return;

			if (PhysicalKeyToken.TryParseVk(token, out uint vk))
			{
				if (WindowsKeySuppressor.IsFallbackVkHeld(vk)
					|| IsCorrespondingKeysNamePressed(pressed, vk))
					pressed.Add(PhysicalKeyToken.FormatVk(vk));
				return;
			}

			if (PhysicalKeyToken.TryParseScan(token, out _, out _)
				&& WindowsKeySuppressor.IsGenericScanTokenDown(token))
			{
				pressed.Add(token);
				return;
			}

			// Keys-name binding: keep down while Win32 make/break still holds the VK.
			if (KeyToken.TryParse(token, out Keys key, out bool isMouse, out _)
				&& !isMouse
				&& WindowsKeySuppressor.IsFallbackVkHeld((uint)key))
			{
				pressed.Add(token);
			}
		}

		private static bool IsCorrespondingKeysNamePressed(HashSet<string> pressed, uint vk)
		{
			if (!Enum.IsDefined(typeof(Keys), (int)vk))
				return false;
			string name = ((Keys)vk).ToString();
			return pressed.Contains(name);
		}

		private static void ProcessHoldReleases(HashSet<string> pressed)
		{
			foreach (string key in _previousPressed)
			{
				if (pressed.Contains(key))
					continue;

				FireReleaseHoldWinner(key);
			}
		}

		/// <summary>
		/// When a key goes up, fire the release-hold binding with the largest satisfied threshold on that key.
		/// Shorter thresholds are cancelled if a longer one was also reached (e.g. 500 ms wins over 200 ms).
		/// If the same-key while-held Hold with the latest (highest) ms already fired, skip all on-release Holds.
		/// </summary>
		private static void FireReleaseHoldWinner(string releasedKey)
		{
			if (LatestWhileHeldOnKeyAlreadyFired(releasedKey))
			{
				CancelSameKeyReleaseHolds(releasedKey);
				return;
			}

			string winnerId = null;
			int winnerMs = -1;

			foreach (KeyValuePair<string, ComboBinding> pair in BindingStorage.AllBindings)
			{
				ComboBinding combo = pair.Value;
				if (combo.DoubleTap || !combo.Hold || !combo.HoldOnRelease)
					continue;

				List<string> keys = combo.Keys;
				if (keys.Count != 1 || !string.Equals(keys[0], releasedKey, StringComparison.OrdinalIgnoreCase))
					continue;

				if (!HoldTrackers.TryGetValue(pair.Key, out HoldTracker tracker))
					continue;

				if (tracker.HeldMs < combo.EffectiveHoldMs)
					continue;

				if (combo.EffectiveHoldMs > winnerMs)
				{
					winnerMs = combo.EffectiveHoldMs;
					winnerId = pair.Key;
				}
			}

			if (winnerId == null)
				return;

			JustPressedActions[ControlsGameUiSeparationPolicy.ToTriggerActionId(winnerId)] = true;
			if (HoldTrackers.TryGetValue(winnerId, out HoldTracker winnerTracker)
				&& LiveInputDiagnosticSession.IsActive)
			{
				LiveInputDiagnosticSession.ObserveHold(
					winnerId,
					"HoldReleaseFired",
					winnerTracker.HeldMs,
					winnerMs,
					holdOnRelease: true);
			}
			// Vanilla map keys (MapFull / MapStyle) and similar actions fire on a Current rising edge
			// (armed while up, fire when Current becomes true) — not on JustPressed alone.
			// Pulse Current for one frame so Hold-on-release works for those actions too.
			CurrentActions[winnerId] = true;

			CancelSameKeyReleaseHolds(releasedKey);
		}

		/// <summary>
		/// True when a while-held Hold on this key has fired and that Hold's ms is strictly later
		/// than every on-release Hold on the same key (i.e. it is the last threshold overall).
		/// </summary>
		private static bool LatestWhileHeldOnKeyAlreadyFired(string keyToken)
		{
			foreach (KeyValuePair<string, HoldTracker> pair in HoldTrackers)
			{
				ComboBinding combo = BindingStorage.Get(pair.Key);
				if (combo == null || combo.DoubleTap || !combo.Hold || combo.HoldOnRelease)
					continue;

				List<string> keys = combo.Keys;
				if (keys.Count != 1 || !string.Equals(keys[0], keyToken, StringComparison.OrdinalIgnoreCase))
					continue;

				if (pair.Value.Fired && IsLatestMsWhileHeldOnKey(keyToken, combo.EffectiveHoldMs))
					return true;
			}

			return false;
		}

		/// <summary>
		/// While-held is the last threshold on this key when its ms is greater than every on-release Hold.
		/// Ties with on-release (or on-release longer) keep charging on-release / HUD.
		/// </summary>
		private static bool IsLatestMsWhileHeldOnKey(string keyToken, int whileHeldMs)
		{
			int maxOnReleaseMs = 0;
			foreach (KeyValuePair<string, ComboBinding> pair in BindingStorage.AllBindings)
			{
				ComboBinding combo = pair.Value;
				if (combo.DoubleTap || !combo.Hold || !combo.HoldOnRelease)
					continue;

				List<string> keys = combo.Keys;
				if (keys.Count != 1 || !string.Equals(keys[0], keyToken, StringComparison.OrdinalIgnoreCase))
					continue;

				maxOnReleaseMs = Math.Max(maxOnReleaseMs, combo.EffectiveHoldMs);
			}

			return whileHeldMs > maxOnReleaseMs;
		}

		/// <summary>Drop on-release Hold trackers for a key so they neither fire nor keep a HUD charge.</summary>
		private static void CancelSameKeyReleaseHolds(string keyToken)
		{
			_removeScratch.Clear();
			foreach (KeyValuePair<string, HoldTracker> pair in HoldTrackers)
			{
				ComboBinding combo = BindingStorage.Get(pair.Key);
				if (combo?.DoubleTap == true)
					continue;
				if (combo?.Hold == true && combo.HoldOnRelease && combo.Keys.Count == 1
					&& string.Equals(combo.Keys[0], keyToken, StringComparison.OrdinalIgnoreCase))
				{
					_removeScratch.Add(pair.Key);
				}
			}

			for (int i = 0; i < _removeScratch.Count; i++)
				HoldTrackers.Remove(_removeScratch[i]);
		}

		private static void EvaluateCombo(string actionId, ComboBinding combo, HashSet<string> pressed, HashSet<string> toggleEdges, HashSet<string> winEdges)
		{
			if (combo?.DoubleTap == true)
				return;

			List<string> keys = combo.Keys;
			bool hasAdditionalOr = combo.HasAdditionalOr;
			if (keys.Count == 0 && !hasAdditionalOr)
				return;

			if (BindingRestrictions.UsesToggleLockHold(combo))
			{
				return;
			}

			bool usePadAlternateOr = combo.HasAlternateGamePad
				&& keys.Count == 1
				&& AlternateGamePadBindingPolicy.ShouldEvaluateRuntime(
					FnaGamePadProvider.IsSubsystemActive, combo.AlternateGamePadToken);

			if (usePadAlternateOr)
			{
				EvaluatePadAlternateOrCombo(actionId, combo, keys, pressed, toggleEdges, winEdges);
				return;
			}

			bool andDown = keys.Count > 0
				&& ComboAndGatePolicy.AllRequiredDown(
					keys,
					key => IsBindingKeyDown(key, pressed, combo));

			bool preserveVanillaAnd = hasAdditionalOr
				&& KeyboardAdditionalOrPolicy.ShouldPreserveVanillaAndKeyWithAdditionalOr(
					actionId, combo.Hold, keys, combo.AdditionalOrToken);

			// Inventory+KU:Inventory share one Escape/OR edge state (LIVE: separate keys → dual rise).
			string sharedUiStateId = preserveVanillaAnd
				? SharedUiBackEscapeEdgePolicy.SharedUiEdgeStateId(actionId)
				: actionId;

			bool andIsEscapeEquivalent = keys.Count == 1
				&& PlainSingleTapPolicy.IsEscapeEquivalentToken(keys[0]);
			bool physicalEscapeDown = andIsEscapeEquivalent && IsPhysicalEscapeDownForSharedUi();
			bool andHeldForSharedUi = preserveVanillaAnd
				? SharedUiBackEscapeEdgePolicy.StabilizeEscapeAndHeld(
					andDown, andIsEscapeEquivalent, physicalEscapeDown)
				: andDown;

			bool orDown = hasAdditionalOr
				&& IsBindingKeyDown(combo.AdditionalOrToken, pressed, combo);
			AdditionalOrHeldPrevious.TryGetValue(sharedUiStateId, out bool prevAdditionalOr);
			bool additionalOrJustPressed = orDown && !prevAdditionalOr;
			if (hasAdditionalOr)
				AdditionalOrHeldPrevious[sharedUiStateId] = orDown;
			else
				AdditionalOrHeldPrevious.Remove(sharedUiStateId);

			if (!andHeldForSharedUi)
			{
				HoldTrackers.Remove(actionId);
				SharedUiBackAndHeldPrevious.Remove(sharedUiStateId);
				// SharedUiEscapeHoldLatch: do not clear here — Escape-up settle must keep it
				// (LIVE flicker UP then DOWN otherwise re-mirrors Inventory).
			}

			if (!andHeldForSharedUi && !orDown)
			{
				// Still advance Escape latch settle when only Escape dropped (Tab may be up too).
				if (preserveVanillaAnd && andIsEscapeEquivalent)
					UpdateSharedUiEscapeLatchSettle(sharedUiStateId, physicalEscapeDown, andPathEdge: false);
				AlternateOrHeldPrevious.Remove(actionId);
				return;
			}

			// Shared UI back (Inventory): vanilla KeyStatus OR lists Escape+Tab.
			// Escape AND: AK-mirror once per physical hold (latch + vanillaAlready + key-up settle).
			// LIVE 2026-09-16: disabling Escape mirror forced REARM_WORLD → multi ToggleInv on flicker.
			// World REARM is disabled; Tab AdditionalOr rising still mirrored separately.
			if (preserveVanillaAnd)
			{
				string preserveTriggerId = sharedUiStateId;
				SharedUiBackAndHeldPrevious.TryGetValue(sharedUiStateId, out bool prevAndHeld);
				bool andRising = andHeldForSharedUi && !prevAndHeld;
				if (andHeldForSharedUi)
					SharedUiBackAndHeldPrevious[sharedUiStateId] = true;

				bool latchActive = SharedUiEscapeHoldLatch.Contains(sharedUiStateId);
				bool vanillaInventoryAlready =
					TryReadTriggerJustPressed(preserveTriggerId);

				if (KeyboardAdditionalOrPolicy.VanillaKeyStatusOwnsAdditionalOr(
					    actionId, combo.Hold, keys, combo.AdditionalOrToken))
				{
					bool mirrorEscapeAnd =
						andIsEscapeEquivalent
						&& SharedUiBackEscapeEdgePolicy.ShouldMirrorFireEscapeAndWhenVanillaOwnsKeyStatus()
						&& SharedUiBackEscapeEdgePolicy.ShouldMirrorFireAndRising(
							andRising, latchActive, vanillaInventoryAlready);

					if (mirrorEscapeAnd)
						JustPressedActions[preserveTriggerId] = true;
					if (additionalOrJustPressed)
						JustPressedActions[preserveTriggerId] = true;
					if (andHeldForSharedUi || orDown)
						CurrentActions[preserveTriggerId] = true;

					bool andPathEdge = mirrorEscapeAnd
						|| (vanillaInventoryAlready && physicalEscapeDown);
					UpdateSharedUiEscapeLatchSettle(sharedUiStateId, physicalEscapeDown, andPathEdge);
					return;
				}

				if (orDown)
					SuppressedComboTokens.Add(combo.AdditionalOrToken);

				if (additionalOrJustPressed)
					JustPressedActions[preserveTriggerId] = true;
				if (orDown)
					CurrentActions[preserveTriggerId] = true;
				return;
			}

			if (andDown)
			{
				for (int i = 0; i < keys.Count; i++)
				{
					string key = keys[i];
					SuppressedComboTokens.Add(key);
					if (PhysicalKeyToken.TryParseVk(key, out uint vk)
						&& Enum.IsDefined(typeof(Keys), (int)vk)
						&& (Keys)vk != Keys.None)
					{
						SuppressedComboTokens.Add(((Keys)vk).ToString());
					}
				}
			}

			if (orDown)
				SuppressedComboTokens.Add(combo.AdditionalOrToken);

			string triggerId = ControlsGameUiSeparationPolicy.ToTriggerActionId(actionId);
			bool isHold = combo.Hold && keys.Count == 1;

			if (additionalOrJustPressed)
				JustPressedActions[triggerId] = true;
			if (orDown)
				CurrentActions[triggerId] = true;

			if (isHold)
			{
				if (!andDown)
					return;

				if (combo.HoldOnRelease)
				{
					if (LatestWhileHeldOnKeyAlreadyFired(keys[0]))
						return;

					EvaluateHoldCharge(actionId);
				}
				else
					EvaluateHoldWhileHeld(actionId, combo);

				return;
			}

			if (!andDown)
				return;

			bool andJustPressed;
			if (keys.Count == 1)
			{
				andJustPressed = IsBindingJustPressed(keys[0], pressed, toggleEdges, winEdges);
			}
			else
			{
				andJustPressed = false;
				for (int i = 0; i < keys.Count; i++)
				{
					if (!WasBindingKeyDown(keys[i]))
					{
						andJustPressed = true;
						break;
					}
				}
			}

			if (andJustPressed)
			{
				JustPressedActions[triggerId] = true;
				if (keys.Count == 1 && JisOemInGameDiagnostic.IsSessionActive)
					JisOemInGameDiagnostic.NoteActivation(keys[0], pressed, wouldFire: true);

				if (keys.Count > 1)
				{
					for (int i = 0; i < keys.Count; i++)
					{
						string key = keys[i];
						if (BindingRestrictions.IsToggleLockToken(key))
							WindowsKeySuppressor.ReleaseToggleLockComboState(key);
					}
				}
			}

			CurrentActions[triggerId] = true;
		}

		/// <summary>Existing GamePad AlternateGamePadToken OR path (unchanged contract).</summary>
		private static void EvaluatePadAlternateOrCombo(
			string actionId,
			ComboBinding combo,
			List<string> keys,
			HashSet<string> pressed,
			HashSet<string> toggleEdges,
			HashSet<string> winEdges)
		{
			bool primaryDown = IsBindingKeyDown(keys[0], pressed, combo);
			bool altDown = IsBindingKeyDown(combo.AlternateGamePadToken, pressed, combo);
			AlternateOrHeldPrevious.TryGetValue(actionId, out bool prevOr);
			var edges = AlternateGamePadBindingPolicy.EvaluateOrEdges(primaryDown, altDown, prevOr);
			bool allDown = edges.Held;
			bool orJustPressed = edges.JustPressed;
			bool orJustReleased = edges.JustReleased;
			AlternateOrHeldPrevious[actionId] = edges.Held;
			if (!allDown && orJustReleased)
			{
				HoldTrackers.Remove(actionId);
				return;
			}

			if (!allDown)
			{
				HoldTrackers.Remove(actionId);
				return;
			}

			for (int i = 0; i < keys.Count; i++)
			{
				string key = keys[i];
				SuppressedComboTokens.Add(key);
				if (PhysicalKeyToken.TryParseVk(key, out uint vk)
					&& Enum.IsDefined(typeof(Keys), (int)vk)
					&& (Keys)vk != Keys.None)
				{
					SuppressedComboTokens.Add(((Keys)vk).ToString());
				}
			}

			SuppressedComboTokens.Add(combo.AlternateGamePadToken);

			bool isHold = combo.Hold && keys.Count == 1;

			if (isHold)
			{
				if (combo.HoldOnRelease)
				{
					if (LatestWhileHeldOnKeyAlreadyFired(keys[0]))
						return;

					EvaluateHoldCharge(actionId);
				}
				else
					EvaluateHoldWhileHeld(actionId, combo);

				return;
			}

			if (orJustPressed)
			{
				JustPressedActions[ControlsGameUiSeparationPolicy.ToTriggerActionId(actionId)] = true;
				if (keys.Count == 1 && JisOemInGameDiagnostic.IsSessionActive)
					JisOemInGameDiagnostic.NoteActivation(keys[0], pressed, wouldFire: true);
			}

			CurrentActions[ControlsGameUiSeparationPolicy.ToTriggerActionId(actionId)] = true;
		}

		/// <summary>
		/// While assembling a multi-key combo, keep toggle-lock keys latched if a sibling modifier is held.
		/// JP Caps Lock is a momentary edge, not a holdable key like Ctrl.
		/// </summary>
		private static void SustainToggleLatchesForPartialCombos(HashSet<string> pressed)
		{
			EnsureCompiled();
			for (int i = 0; i < MultiToggleComboIndices.Count; i++)
			{
				CompiledBinding cb = _compiled[MultiToggleComboIndices[i]];
				ComboBinding combo = BindingStorage.Get(cb.ActionId);
				if (combo == null)
					continue;

				string[] keys = cb.Keys;
				for (int t = 0; t < keys.Length; t++)
				{
					string toggleToken = keys[t];
					if (!BindingRestrictions.IsToggleLockToken(toggleToken))
						continue;

					if (!WindowsKeySuppressor.IsToggleTokenActiveForCombo(toggleToken))
						continue;

					bool anySiblingHeld = false;
					for (int k = 0; k < keys.Length; k++)
					{
						if (BindingRestrictions.IsToggleLockToken(keys[k]))
							continue;

						if (IsBindingKeyDown(keys[k], pressed, combo))
						{
							anySiblingHeld = true;
							break;
						}
					}

					if (anySiblingHeld)
						WindowsKeySuppressor.LatchToggleTokenForCombo(toggleToken);
				}
			}
		}

		/// <summary>
		/// Advance SharedUi Escape hold latch. Clears immediately on physical Escape up
		/// so rapid re-taps work. Mid-hold FNA flicker must not look like up —
		/// <see cref="IsPhysicalEscapeDownForSharedUi"/> ORs GetAsyncKeyState.
		/// </summary>
		private static void UpdateSharedUiEscapeLatchSettle(
			string sharedUiStateId,
			bool physicalEscapeDown,
			bool andPathEdge)
		{
			bool latchActive = SharedUiEscapeHoldLatch.Contains(sharedUiStateId);
			if (SharedUiBackEscapeEdgePolicy.NextEscapeHoldLatch(
				    latchActive, physicalEscapeDown, andPathEdge, keyUpSettleActive: false))
				SharedUiEscapeHoldLatch.Add(sharedUiStateId);
			else
				SharedUiEscapeHoldLatch.Remove(sharedUiStateId);
		}

		/// <summary>
		/// True while shared UI-back Escape hold latch is armed for this action
		/// (Inventory already edged this Escape press).
		/// </summary>
		public static bool IsSharedUiEscapeHoldLatchActive(string actionId)
		{
			if (string.IsNullOrEmpty(actionId))
				return false;
			string sharedId = SharedUiBackEscapeEdgePolicy.SharedUiEdgeStateId(actionId);
			return SharedUiEscapeHoldLatch.Contains(sharedId)
				|| SharedUiEscapeHoldLatch.Contains(actionId);
		}

		private static bool IsPhysicalEscapeDownForSharedUi()
		{
			// FNA Keyboard.GetState can briefly report Escape UP while still held (LIVE flicker).
			// Main.keyState can be stripped by ComboPanel.ConsumeEscape.
			// Win32 GetAsyncKeyState is the hold-stable bit for latch clear.
			bool keyboardGetStateDown = false;
			try { keyboardGetStateDown = Keyboard.GetState().IsKeyDown(Keys.Escape); }
			catch { /* ignore */ }

			bool mainKeyStateDown = false;
			try { mainKeyStateDown = Main.keyState.IsKeyDown(Keys.Escape); }
			catch { /* ignore */ }

			bool asyncDown = false;
			try { asyncDown = WindowsKeySuppressor.IsEscapePhysicallyDownAsync(); }
			catch { /* ignore */ }

			if (SharedUiBackEscapeEdgePolicy.IsHardwareEscapeDown(keyboardGetStateDown, mainKeyStateDown))
				return true;
			if (asyncDown)
				return true;
			return KeyToken.IsDown("Escape");
		}

		private static bool TryReadTriggerJustPressed(string triggerId)
		{
			try
			{
				TriggersSet jp = PlayerInput.Triggers?.JustPressed;
				if (jp?.KeyStatus == null || string.IsNullOrEmpty(triggerId))
					return false;
				return jp.KeyStatus.TryGetValue(triggerId, out bool v) && v;
			}
			catch
			{
				return false;
			}
		}

		private static bool WasBindingKeyDown(string token)
		{
			if (BindingRestrictions.IsToggleLockToken(token))
				return WindowsKeySuppressor.WasLatchedToggleToken(token);

			// 1) exact  2) PhysicalKeyIdentity.AreSamePhysical (old OemComma vs SC:0x33:Normal)
			return BindingPhysicalHeldMatch.IsHeld(token, _previousPressed);
		}

		private static bool IsBindingKeyDown(string token, HashSet<string> pressed, ComboBinding combo)
		{
			// Pad:* — SupplementHeld usually adds tokens, but also query FnaGamePadProvider directly
			// so two-key Alternate (e.g. Pad:X → MouseLeft) cannot miss when the set is stale.
			if (GamePadToken.IsPadToken(token))
			{
				if (FnaGamePadProvider.IsDown(token)
					|| BindingPhysicalHeldMatch.IsHeld(token, pressed))
				{
					if (!pressed.Contains(token))
						pressed.Add(token);
					return true;
				}

				return false;
			}

			// 1) exact  2) AreSamePhysical — old Keys/VK saves vs new SC: pressed set.
			if (BindingPhysicalHeldMatch.IsHeld(token, pressed))
			{
				if (!pressed.Contains(token))
					pressed.Add(token);
				return true;
			}

			if (StableKeyToken.IsMouseWheelToken(token))
				return false;

			if (PhysicalKeyToken.TryParseVk(token, out uint vk))
			{
				if (WindowsKeySuppressor.IsFallbackVkHeld(vk)
					|| KeyToken.IsDown(token)
					|| IsCorrespondingKeysNamePressed(pressed, vk))
				{
					pressed.Add(token);
					return true;
				}

				return false;
			}

			if (PhysicalKeyToken.TryParseScan(token, out _, out _))
			{
				if (!KeyToken.IsDown(token))
					return false;

				pressed.Add(token);
				return true;
			}

			// Keys name: also true while matching VK make/break is held (FNA drop).
			if (KeyToken.TryParse(token, out Keys key, out bool isMouse, out _)
				&& !isMouse
				&& WindowsKeySuppressor.IsFallbackVkHeld((uint)key))
			{
				pressed.Add(token);
				return true;
			}

			if (BindingRestrictions.IsToggleLockToken(token))
			{
				if (BindingRestrictions.SupportsCapsLockPhysicalHold(token, combo))
					return KeyToken.IsDown(token);

				return WindowsKeySuppressor.IsToggleTokenActiveForCombo(token);
			}

			return false;
		}

		private static bool IsBindingJustPressed(string token, HashSet<string> pressed, HashSet<string> toggleEdges, HashSet<string> winEdges)
		{
			if (PhysicalKeyToken.IsFallbackToken(token))
			{
				if (!pressed.Contains(token) && KeyToken.IsDown(token))
					pressed.Add(token);

				return BindingPhysicalHeldMatch.IsJustPressed(token, pressed, _previousPressed);
			}

			if (BindingRestrictions.IsToggleLockToken(token))
				return toggleEdges.Contains(token);

			if (BindingRestrictions.IsWindowsKeyToken(token))
				return winEdges.Contains(token);

			return BindingPhysicalHeldMatch.IsJustPressed(token, pressed, _previousPressed);
		}

		private static void EvaluateHoldWhileHeld(string actionId, ComboBinding combo)
		{
			ComboBinding storedHold = BindingStorage.Get(actionId);
			if (storedHold?.DoubleTap == true)
				return;

			if (!HoldTrackers.TryGetValue(actionId, out HoldTracker tracker))
			{
				tracker = new HoldTracker();
				HoldTrackers[actionId] = tracker;
				if (LiveInputDiagnosticSession.IsActive)
				{
					LiveInputDiagnosticSession.ObserveHold(
						actionId,
						"HoldDownStarted",
						0,
						combo.EffectiveHoldMs,
						holdOnRelease: false);
				}
			}

			tracker.HeldMs += Main.GameUpdateCount > 0 ? 1000.0 / 60.0 : 16.0;

			if (!tracker.Fired && tracker.HeldMs >= combo.EffectiveHoldMs)
			{
				tracker.Fired = true;
				JustPressedActions[ControlsGameUiSeparationPolicy.ToTriggerActionId(actionId)] = true;
				if (LiveInputDiagnosticSession.IsActive)
				{
					LiveInputDiagnosticSession.ObserveHold(
						actionId,
						"HoldThresholdFired",
						tracker.HeldMs,
						combo.EffectiveHoldMs,
						holdOnRelease: false);
				}

				// Only cancel on-release when this while-held is the latest ms on the key.
				if (combo.Keys.Count == 1 && IsLatestMsWhileHeldOnKey(combo.Keys[0], combo.EffectiveHoldMs))
					CancelSameKeyReleaseHolds(combo.Keys[0]);
			}

			if (tracker.Fired)
				CurrentActions[ControlsGameUiSeparationPolicy.ToTriggerActionId(actionId)] = true;
		}

		/// <summary>Accumulates hold time only; firing happens in <see cref="FireReleaseHoldWinner"/>.</summary>
		private static void EvaluateHoldCharge(string actionId)
		{
			ComboBinding storedHold = BindingStorage.Get(actionId);
			if (storedHold?.DoubleTap == true)
				return;

			if (!HoldTrackers.TryGetValue(actionId, out HoldTracker tracker))
			{
				tracker = new HoldTracker();
				HoldTrackers[actionId] = tracker;
				if (LiveInputDiagnosticSession.IsActive)
				{
					ComboBinding combo = BindingStorage.Get(actionId);
					LiveInputDiagnosticSession.ObserveHold(
						actionId,
						"HoldDownStarted",
						0,
						combo?.EffectiveHoldMs ?? 0,
						holdOnRelease: true);
				}
			}

			tracker.HeldMs += Main.GameUpdateCount > 0 ? 1000.0 / 60.0 : 16.0;
		}

		private static void PruneHoldTrackers(HashSet<string> pressed)
		{
			if (HoldTrackers.Count == 0)
				return;

			// Same identity path as EvaluateCombo (pressed + VK/SC make/break),
			// not KeyToken.IsDown alone — AsyncKeyState-only prune kills VK HOLD.
			if (pressed == null)
				return;

			_removeScratch.Clear();
			foreach (KeyValuePair<string, HoldTracker> pair in HoldTrackers)
			{
				ComboBinding combo = BindingStorage.Get(pair.Key);
				if (combo == null || combo.DoubleTap)
				{
					_removeScratch.Add(pair.Key);
					continue;
				}

				bool anyUp = false;
				List<string> keys = combo.Keys;
				if (combo.HasAlternateGamePad && keys.Count == 1)
				{
					bool primaryDown = IsBindingKeyDown(keys[0], pressed, combo);
					bool altDown = IsBindingKeyDown(combo.AlternateGamePadToken, pressed, combo);
					anyUp = !(primaryDown || altDown);
				}
				else
				{
					for (int i = 0; i < keys.Count; i++)
					{
						if (!IsBindingKeyDown(keys[i], pressed, combo))
						{
							anyUp = true;
							break;
						}
					}
				}

				if (anyUp)
					_removeScratch.Add(pair.Key);
			}

			for (int i = 0; i < _removeScratch.Count; i++)
			{
				string key = _removeScratch[i];
				if (HoldTrackers.TryGetValue(key, out HoldTracker tracker)
					&& LiveInputDiagnosticSession.IsActive)
				{
					ComboBinding combo = BindingStorage.Get(key);
					LiveInputDiagnosticSession.ObserveHold(
						key,
						"HoldReleasedOrPruned",
						tracker.HeldMs,
						combo?.EffectiveHoldMs ?? 0,
						combo?.HoldOnRelease ?? false);
				}
				HoldTrackers.Remove(key);
			}
		}

		public static bool WasJustPressed(string actionId)
		{
			return !string.IsNullOrEmpty(actionId)
				&& JustPressedActions.TryGetValue(actionId, out bool value)
				&& value;
		}

		public static bool IsCurrent(string actionId)
		{
			return !string.IsNullOrEmpty(actionId)
				&& CurrentActions.TryGetValue(actionId, out bool value)
				&& value;
		}

		/// <summary>
		/// Writes this frame's advanced JustPressed / Current into Terraria triggers
		/// without scanning all bindings.
		/// </summary>
		public static void ApplyResultsToTriggers(TriggersSet justPressed, TriggersSet current)
		{
			foreach (KeyValuePair<string, bool> pair in JustPressedActions)
			{
				if (!pair.Value)
					continue;
				justPressed.KeyStatus[pair.Key] = true;
				if (LiveInputDiagnosticSession.IsActive)
					LiveInputDiagnosticSession.ObserveActionExecution(pair.Key);
			}

			foreach (KeyValuePair<string, bool> pair in CurrentActions)
			{
				if (pair.Value)
					current.KeyStatus[pair.Key] = true;
			}
		}

		/// <summary>
		/// Post-apply delayed decision snapshots via <see cref="AkDecisionSnapshotPublisher"/>.
		/// Not the internal decision instant; does not claim game-action execution.
		/// </summary>
		public static void PublishDecisionSnapshotsAfterApply(TriggersSet justPressed)
		{
			if (!AkDecisionFeed.HasSubscribers || justPressed == null || justPressed.KeyStatus == null)
				return;

			AkDecisionSnapshotPublisher.Publish(
				JustPressedActions,
				justPressed.KeyStatus,
				Main.GameUpdateCount,
				DecisionFrameGate);
		}

		/// <summary>
		/// True when Advanced Keybinds owns gameplay input for this action id
		/// (multi-key / HOLD / mouse / SC|VK / hook-managed — not plain Keys-name single tap, not empty).
		/// Identification is by action id (ModName/internalName), never by display name.
		/// </summary>
		public static bool OwnsActionInput(string actionId)
		{
			if (string.IsNullOrEmpty(actionId))
				return false;

			EnsureCompiled();

			ComboBinding combo = BindingStorage.Get(actionId);
			if (combo != null && combo.DoubleTap)
			{
				// Ineligible DT must not fall into Hold/OR RequiresAdvancedStorage.
				bool dtIsPad = ControlsEditTargetPolicy.IsGamePadStorageKey(actionId)
					|| ControlsGameUiSeparationPolicy.IsGamePadLegacySharedStorageKey(actionId);
				bool currentInputIsGamePad = PlayerInput.CurrentInputMode == InputMode.XBoxGamepad
					|| PlayerInput.CurrentInputMode == InputMode.XBoxGamepadUI;

				if (dtIsPad)
					return currentInputIsGamePad && EligibleDoubleTapStorageKeys.Contains(actionId);

				// bare eligible DT on Keyboard/UI owns; on GamePad do not false-exit — check GP sibling.
				if (!currentInputIsGamePad && EligibleDoubleTapStorageKeys.Contains(actionId))
					return true;
			}
			else if (combo != null && combo.RequiresAdvancedStorage())
				return true;

			// Non-DT (and bare DT under GamePad): sibling lookup matches review-base
			// (no current-mode gate / no GP-storage early false).
			string gpKey = ControlsEditTargetPolicy.ToStorageKey(
				ControlsEditTargetPolicy.DeviceKind.GamePad, actionId);
			ComboBinding gp = BindingStorage.Get(gpKey);
			if (gp == null)
				return false;
			if (gp.DoubleTap)
				return EligibleDoubleTapStorageKeys.Contains(gpKey)
					&& (PlayerInput.CurrentInputMode == InputMode.XBoxGamepad
						|| PlayerInput.CurrentInputMode == InputMode.XBoxGamepadUI);
			if (ControlsEditTargetPolicy.IsVanillaOwnedGamePadOrPair(
				    gp.Hold, gp.Keys, gp.AlternateGamePadToken))
				return false;
			return gp.RequiresAdvancedStorage();
		}

		/// <summary>
		/// AK was Current last frame and is not Current this frame (release edge owned by AK).
		/// </summary>
		public static bool WasJustReleased(string actionId)
		{
			return !string.IsNullOrEmpty(actionId)
				&& PreviousCurrentActions.Contains(actionId)
				&& !IsCurrent(actionId);
		}

		/// <summary>
		/// For AK-owned actions, replace vanilla Trigger edges with AK-only results so leftover
		/// KeyStatus keys and ModKeybind DefaultBinding cannot dual-fire the same action.
		/// Does not mutate saved KeyConfiguration — runtime Triggers only.
		/// </summary>
		public static void ApplyOwnershipToTriggers(TriggersSet justPressed, TriggersSet current, TriggersSet justReleased)
		{
			EnsureCompiled();
			bool currentInputIsGamePad = PlayerInput.CurrentInputMode == InputMode.XBoxGamepad
				|| PlayerInput.CurrentInputMode == InputMode.XBoxGamepadUI;
			CompiledBinding[] compiled = _compiled;
			for (int i = 0; i < compiled.Length; i++)
			{
				CompiledBinding cb = compiled[i];
				if (!cb.RequiresAdvancedStorage)
					continue;

				string actionId = cb.ActionId;
				bool dtMayReplace = false;
				if (cb.IsDoubleTap)
				{
					if (!cb.OwnsDoubleTapInput)
						continue;
					if (DoubleTapTargetPolicy.IsVanillaTarget(actionId) && !CanReplaceVanillaDoubleTap(actionId))
						continue;
					bool dtIsPad = ControlsEditTargetPolicy.IsGamePadStorageKey(actionId)
						|| ControlsGameUiSeparationPolicy.IsGamePadLegacySharedStorageKey(actionId);
					dtMayReplace = currentInputIsGamePad == dtIsPad;
					if (!dtMayReplace)
						continue;
				}

				ComboBinding combo = BindingStorage.Get(actionId);
				bool hold = combo != null && combo.Hold && !combo.DoubleTap;
				IReadOnlyList<string> keys = combo != null ? combo.Keys : cb.Keys;
				string alternate = combo != null ? combo.AlternateGamePadToken : null;
				// DT eligible rows replace via AK results; do not reuse vanilla GP ownership gate.
				if (!dtMayReplace
					&& !BindingOwnershipTriggerPolicy.ShouldApplyOwnershipReplace(
					    actionId,
					    requiresAdvancedStorage: true,
					    hold,
					    keys,
					    alternate,
					    currentInputIsGamePad))
					continue;

				string triggerId = ControlsGameUiSeparationPolicy.ToTriggerActionId(actionId);
				bool akPressed = WasJustPressed(triggerId) || WasJustPressed(actionId);
				bool akCurrent = IsCurrent(triggerId) || IsCurrent(actionId);
				bool akReleased = WasJustReleased(triggerId) || WasJustReleased(actionId);

				if (justPressed != null && justPressed.KeyStatus.ContainsKey(triggerId))
					justPressed.KeyStatus[triggerId] = akPressed;

				if (current != null && current.KeyStatus.ContainsKey(triggerId))
					current.KeyStatus[triggerId] = akCurrent;

				if (justReleased != null && justReleased.KeyStatus.ContainsKey(triggerId))
					justReleased.KeyStatus[triggerId] = akReleased;
			}
		}

		/// <summary>
		/// True when a vanilla single-key trigger should be suppressed because one of its keys is
		/// currently being consumed by an advanced combo that is fully held.
		/// </summary>
		public static bool ShouldSuppressVanillaTrigger(string actionId, IReadOnlyList<string> vanillaKeys)
		{
			if (vanillaKeys == null || vanillaKeys.Count == 0 || SuppressedComboTokens.Count == 0)
				return false;

			for (int i = 0; i < vanillaKeys.Count; i++)
			{
				if (SuppressedComboTokens.Contains(vanillaKeys[i]))
					return true;
			}

			return false;
		}

		/// <summary>
		/// Active on-release Hold charges for HUD: one group per physical key currently held,
		/// with all release-hold bindings on that key sorted by threshold (ascending).
		/// <paramref name="selectedIndex"/> is -1 when held time is below the first threshold.
		/// </summary>
		public static void CollectActiveReleaseHoldHudGroups(List<ReleaseHoldHudGroup> results)
		{
			results?.Clear();
			if (results == null || HoldTrackers.Count == 0)
				return;

			HashSet<string> seenKeys = new(StringComparer.OrdinalIgnoreCase);

			foreach (KeyValuePair<string, HoldTracker> trackerPair in HoldTrackers)
			{
				ComboBinding tracked = BindingStorage.Get(trackerPair.Key);
				if (tracked == null || tracked.DoubleTap || !tracked.Hold || !tracked.HoldOnRelease)
					continue;

				List<string> trackedKeys = tracked.Keys;
				if (trackedKeys.Count != 1)
					continue;

				string keyToken = trackedKeys[0];
				if (!seenKeys.Add(keyToken))
					continue;

				List<ReleaseHoldTier> tiers = new();
				foreach (KeyValuePair<string, ComboBinding> pair in BindingStorage.AllBindings)
				{
					ComboBinding combo = pair.Value;
					if (combo.DoubleTap || !combo.Hold || !combo.HoldOnRelease)
						continue;

					List<string> keys = combo.Keys;
					if (keys.Count != 1 || !string.Equals(keys[0], keyToken, StringComparison.OrdinalIgnoreCase))
						continue;

					tiers.Add(new ReleaseHoldTier(pair.Key, combo.EffectiveHoldMs));
				}

				if (tiers.Count == 0)
					continue;

				tiers.Sort((a, b) => a.HoldMs.CompareTo(b.HoldMs));

				double heldMs = trackerPair.Value.HeldMs;
				// Prefer the longest tracker among this key's release-holds (they usually match).
				foreach (ReleaseHoldTier tier in tiers)
				{
					if (HoldTrackers.TryGetValue(tier.ActionId, out HoldTracker t) && t.HeldMs > heldMs)
						heldMs = t.HeldMs;
				}

				int selectedIndex = -1;
				for (int i = 0; i < tiers.Count; i++)
				{
					if (heldMs >= tiers[i].HoldMs)
						selectedIndex = i;
				}

				results.Add(new ReleaseHoldHudGroup(keyToken, heldMs, tiers, selectedIndex));
			}
		}
	}

	public readonly struct ReleaseHoldTier
	{
		public readonly string ActionId;
		public readonly int HoldMs;

		public ReleaseHoldTier(string actionId, int holdMs)
		{
			ActionId = actionId;
			HoldMs = holdMs;
		}
	}

	public readonly struct ReleaseHoldHudGroup
	{
		public readonly string KeyToken;
		public readonly double HeldMs;
		public readonly IReadOnlyList<ReleaseHoldTier> Tiers;
		public readonly int SelectedIndex;

		public ReleaseHoldHudGroup(string keyToken, double heldMs, IReadOnlyList<ReleaseHoldTier> tiers, int selectedIndex)
		{
			KeyToken = keyToken;
			HeldMs = heldMs;
			Tiers = tiers;
			SelectedIndex = selectedIndex;
		}
	}
}
