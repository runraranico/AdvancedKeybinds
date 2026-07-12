using System;
using System.Collections.Generic;
using System.Linq;
using AdvancedKeybinds.Patches;
using Terraria;

namespace AdvancedKeybinds.Binding
{
	/// <summary>
	/// Evaluates advanced combo bindings each frame and exposes just-pressed / current state per action.
	/// A combo fires when every filled slot key is held. A one-slot hold fires after the hold duration
	/// (while held, or on release depending on <see cref="ComboBinding.HoldOnRelease"/>).
	/// </summary>
	public static class BindingInputEngine
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

		private static HashSet<string> _previousPressed = new(StringComparer.OrdinalIgnoreCase);

		/// <summary>Set while a slot is being captured so gameplay bindings don't fire during setup.</summary>
		public static bool IsRecording { get; set; }

		public static void InvalidateBindings()
		{
			HoldTrackers.Clear();
			JustPressedActions.Clear();
			CurrentActions.Clear();
		}

		public static void Unload()
		{
			InvalidateBindings();
		}

		private static bool _hookManagedPromotionAttempted;

		public static void Update()
		{
			if (Main.gameMenu || Main.LocalPlayer == null || IsRecording)
				return;

			if (!_hookManagedPromotionAttempted)
			{
				_hookManagedPromotionAttempted = true;
				BindingStorage.PromoteHookManagedVanillaBindings();
			}

			JustPressedActions.Clear();
			CurrentActions.Clear();
			SuppressedComboTokens.Clear();

			WindowsKeySuppressor.PollGameplayToggleLockEdges();

			HashSet<string> toggleEdges = new(StringComparer.OrdinalIgnoreCase);
			foreach (string edge in WindowsKeySuppressor.DrainGameplayToggleLockEdges())
				toggleEdges.Add(edge);

			HashSet<string> winEdges = new(StringComparer.OrdinalIgnoreCase);
			foreach (string edge in WindowsKeySuppressor.DrainGameplayWinEdges())
				winEdges.Add(edge);

			HashSet<string> pressed = KeyToken.SnapshotPressedTokens();
			foreach (string edge in toggleEdges)
				pressed.Add(edge);
			foreach (string edge in winEdges)
				pressed.Add(edge);

			SustainToggleLatchesForPartialCombos(pressed);

			HashSet<string> latchedToggles = WindowsKeySuppressor.SnapshotLatchedToggleTokens();
			foreach (string token in latchedToggles)
				pressed.Add(token);

			FireHookManagedTapBindings(toggleEdges, winEdges);

			// Release-to-fire holds are resolved before trackers are cleared for the released key.
			ProcessHoldReleases(pressed);

			foreach (KeyValuePair<string, ComboBinding> pair in BindingStorage.AllBindings)
			{
				if (pair.Value.IsPlainSingleTap)
					continue;

				EvaluateCombo(pair.Key, pair.Value, pressed, toggleEdges, winEdges);
			}

			PruneHoldTrackers();

			WindowsKeySuppressor.EndToggleComboLatchFrame(latchedToggles);
			_previousPressed = pressed;
		}

		private static void FireHookManagedTapBindings(HashSet<string> toggleEdges, HashSet<string> winEdges)
		{
			foreach (KeyValuePair<string, ComboBinding> pair in BindingStorage.AllBindings)
			{
				ComboBinding combo = pair.Value;
				List<string> keys = combo.Keys;
				if (keys.Count != 1 || combo.Hold)
					continue;

				string key = keys[0];
				if (BindingRestrictions.IsToggleLockToken(key) && toggleEdges.Contains(key))
					JustPressedActions[pair.Key] = true;
				else if (BindingRestrictions.IsWindowsKeyToken(key) && winEdges.Contains(key))
					JustPressedActions[pair.Key] = true;
			}
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
		/// </summary>
		private static void FireReleaseHoldWinner(string releasedKey)
		{
			string winnerId = null;
			int winnerMs = -1;

			foreach (KeyValuePair<string, ComboBinding> pair in BindingStorage.AllBindings)
			{
				ComboBinding combo = pair.Value;
				if (!combo.Hold || !combo.HoldOnRelease)
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

			JustPressedActions[winnerId] = true;
			// Vanilla map keys (MapFull / MapStyle) and similar actions fire on a Current rising edge
			// (armed while up, fire when Current becomes true) — not on JustPressed alone.
			// Pulse Current for one frame so Hold-on-release works for those actions too.
			CurrentActions[winnerId] = true;

			// Drop every release-hold tracker on this key so nothing else fires from the same release.
			List<string> toRemove = new();
			foreach (KeyValuePair<string, HoldTracker> pair in HoldTrackers)
			{
				ComboBinding combo = BindingStorage.Get(pair.Key);
				if (combo?.Hold == true && combo.HoldOnRelease && combo.Keys.Count == 1
					&& string.Equals(combo.Keys[0], releasedKey, StringComparison.OrdinalIgnoreCase))
				{
					toRemove.Add(pair.Key);
				}
			}

			foreach (string id in toRemove)
				HoldTrackers.Remove(id);
		}

		private static void EvaluateCombo(string actionId, ComboBinding combo, HashSet<string> pressed, HashSet<string> toggleEdges, HashSet<string> winEdges)
		{
			List<string> keys = combo.Keys;
			if (keys.Count == 0)
				return;

			if (BindingRestrictions.UsesToggleLockHold(combo))
			{
				return;
			}

			bool allDown = keys.All(k => IsBindingKeyDown(k, pressed, combo));

			if (!allDown)
			{
				HoldTrackers.Remove(actionId);
				return;
			}

			// While every slot key is held, claim those tokens so a plain vanilla single-key binding
			// on the same physical key does not also fire (e.g. "I" alone vs "Ctrl+Alt+I").
			foreach (string key in keys)
				SuppressedComboTokens.Add(key);

			bool isHold = combo.Hold && keys.Count == 1;

			if (isHold)
			{
				if (combo.HoldOnRelease)
					EvaluateHoldCharge(actionId);
				else
					EvaluateHoldWhileHeld(actionId, combo);

				return;
			}

			bool justPressed = keys.Count == 1
				? IsBindingJustPressed(keys[0], pressed, toggleEdges, winEdges)
				: !keys.All(WasBindingKeyDown);

			if (justPressed)
			{
				JustPressedActions[actionId] = true;

				if (keys.Count > 1)
				{
					foreach (string key in keys)
					{
						if (BindingRestrictions.IsToggleLockToken(key))
							WindowsKeySuppressor.ReleaseToggleLockComboState(key);
					}
				}
			}

			CurrentActions[actionId] = true;
		}

		/// <summary>
		/// While assembling a multi-key combo, keep toggle-lock keys latched if a sibling modifier is held.
		/// JP Caps Lock is a momentary edge, not a holdable key like Ctrl.
		/// </summary>
		private static void SustainToggleLatchesForPartialCombos(HashSet<string> pressed)
		{
			foreach (KeyValuePair<string, ComboBinding> pair in BindingStorage.AllBindings)
			{
				List<string> keys = pair.Value.Keys;
				if (keys.Count <= 1)
					continue;

				foreach (string toggleToken in keys)
				{
					if (!BindingRestrictions.IsToggleLockToken(toggleToken))
						continue;

					if (!WindowsKeySuppressor.IsToggleTokenActiveForCombo(toggleToken))
						continue;

					bool anySiblingHeld = false;
					foreach (string key in keys)
					{
						if (BindingRestrictions.IsToggleLockToken(key))
							continue;

						if (pressed.Contains(key))
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

		private static bool WasBindingKeyDown(string token)
		{
			if (BindingRestrictions.IsToggleLockToken(token))
				return WindowsKeySuppressor.WasLatchedToggleToken(token);

			return _previousPressed.Contains(token);
		}

		private static bool IsBindingKeyDown(string token, HashSet<string> pressed, ComboBinding combo)
		{
			if (BindingRestrictions.IsToggleLockToken(token))
			{
				if (BindingRestrictions.SupportsCapsLockPhysicalHold(token, combo))
					return KeyToken.IsDown(token);

				return WindowsKeySuppressor.IsToggleTokenActiveForCombo(token);
			}

			return pressed.Contains(token);
		}

		private static bool IsBindingJustPressed(string token, HashSet<string> pressed, HashSet<string> toggleEdges, HashSet<string> winEdges)
		{
			if (BindingRestrictions.IsToggleLockToken(token))
				return toggleEdges.Contains(token);

			if (BindingRestrictions.IsWindowsKeyToken(token))
				return winEdges.Contains(token);

			return pressed.Contains(token) && !_previousPressed.Contains(token);
		}

		private static void EvaluateHoldWhileHeld(string actionId, ComboBinding combo)
		{
			if (!HoldTrackers.TryGetValue(actionId, out HoldTracker tracker))
			{
				tracker = new HoldTracker();
				HoldTrackers[actionId] = tracker;
			}

			tracker.HeldMs += Main.GameUpdateCount > 0 ? 1000.0 / 60.0 : 16.0;

			if (!tracker.Fired && tracker.HeldMs >= combo.EffectiveHoldMs)
			{
				tracker.Fired = true;
				JustPressedActions[actionId] = true;
			}

			if (tracker.Fired)
				CurrentActions[actionId] = true;
		}

		/// <summary>Accumulates hold time only; firing happens in <see cref="FireReleaseHoldWinner"/>.</summary>
		private static void EvaluateHoldCharge(string actionId)
		{
			if (!HoldTrackers.TryGetValue(actionId, out HoldTracker tracker))
			{
				tracker = new HoldTracker();
				HoldTrackers[actionId] = tracker;
			}

			tracker.HeldMs += Main.GameUpdateCount > 0 ? 1000.0 / 60.0 : 16.0;
		}

		private static void PruneHoldTrackers()
		{
			if (HoldTrackers.Count == 0)
				return;

			List<string> stale = new();
			foreach (KeyValuePair<string, HoldTracker> pair in HoldTrackers)
			{
				ComboBinding combo = BindingStorage.Get(pair.Key);
				if (combo == null || !combo.Keys.All(KeyToken.IsDown))
					stale.Add(pair.Key);
			}

			foreach (string key in stale)
				HoldTrackers.Remove(key);
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
		/// True when a vanilla single-key trigger should be suppressed because one of its keys is
		/// currently being consumed by an advanced combo that is fully held.
		/// </summary>
		public static bool ShouldSuppressVanillaTrigger(string actionId, IReadOnlyList<string> vanillaKeys)
		{
			if (vanillaKeys == null || vanillaKeys.Count == 0 || SuppressedComboTokens.Count == 0)
				return false;

			return vanillaKeys.Any(SuppressedComboTokens.Contains);
		}
	}
}
