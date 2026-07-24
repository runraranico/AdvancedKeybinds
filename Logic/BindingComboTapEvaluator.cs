using System;
using System.Collections.Generic;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Pure tap/combo evaluation shared by <c>BindingInputEngine</c> tests and (optionally) production.
	/// Input acquisition (Mouse/keyboard/hooks) stays outside; callers pass a pressed-token snapshot.
	/// Match rules: exact string, then <see cref="BindingPhysicalHeldMatch"/> / PhysicalKeyIdentity.
	/// </summary>
	public static class BindingComboTapEvaluator
	{
		public readonly struct FrameResult
		{
			public readonly bool AllDown;
			public readonly bool JustPressed;
			public readonly bool IsCurrent;
			public readonly bool SkippedPlainSingleTap;
			public readonly bool SkippedEmpty;

			public FrameResult(
				bool allDown = false,
				bool justPressed = false,
				bool isCurrent = false,
				bool skippedPlainSingleTap = false,
				bool skippedEmpty = false)
			{
				AllDown = allDown;
				JustPressed = justPressed;
				IsCurrent = isCurrent;
				SkippedPlainSingleTap = skippedPlainSingleTap;
				SkippedEmpty = skippedEmpty;
			}
		}

		/// <summary>
		/// Non-HOLD tap evaluation for one binding. Plain single-tap Keys-name bindings are skipped
		/// (vanilla owns them) — same contract as BindingInputEngine compiled path.
		/// </summary>
		public static FrameResult EvaluateNonHold(
			bool hold,
			IReadOnlyList<string> keys,
			HashSet<string> pressedNow,
			HashSet<string> pressedPrevious)
		{
			if (keys == null || keys.Count == 0)
			{
				return new FrameResult(skippedEmpty: true);
			}

			if (PlainSingleTapPolicy.IsPlainSingleTap(hold, keys))
			{
				return new FrameResult(skippedPlainSingleTap: true);
			}

			if (hold)
			{
				// HOLD uses a different state machine; callers must use EvaluateHoldFrame.
				return default;
			}

			bool allDown = true;
			for (int i = 0; i < keys.Count; i++)
			{
				if (!BindingPhysicalHeldMatch.IsHeld(keys[i], pressedNow))
				{
					allDown = false;
					break;
				}
			}

			if (!allDown)
				return new FrameResult(allDown: false, justPressed: false, isCurrent: false);

			bool justPressed;
			if (keys.Count == 1)
			{
				justPressed = BindingPhysicalHeldMatch.IsJustPressed(keys[0], pressedNow, pressedPrevious);
			}
			else
			{
				justPressed = false;
				for (int i = 0; i < keys.Count; i++)
				{
					if (!BindingPhysicalHeldMatch.IsHeld(keys[i], pressedPrevious))
					{
						justPressed = true;
						break;
					}
				}
			}

			return new FrameResult(
				allDown: true,
				justPressed: justPressed,
				isCurrent: true);
		}

		/// <summary>
		/// Minimal HOLD (while-held) frame. <paramref name="heldMs"/> is caller-accumulated duration.
		/// </summary>
		public static bool EvaluateHoldWhileHeldFire(
			bool hold,
			bool holdOnRelease,
			int holdMs,
			double heldMsAccumulated,
			bool alreadyFired,
			IReadOnlyList<string> keys,
			HashSet<string> pressedNow)
		{
			if (!hold || holdOnRelease || keys == null || keys.Count != 1 || alreadyFired)
				return false;

			if (!BindingPhysicalHeldMatch.IsHeld(keys[0], pressedNow))
				return false;

			int need = holdMs >= ComboBindingData.MinTypedHoldMs && holdMs <= ComboBindingData.MaxHoldMs
				? holdMs
				: ComboBindingData.DefaultHoldMs;
			return heldMsAccumulated >= need;
		}
	}

	/// <summary>
	/// In-memory binding table + frame-by-frame evaluation for automated tests (no Terraria/Win32).
	/// Uses the same match / plain-skip / ownership-marker rules as production contracts.
	/// </summary>
	public sealed class FakeBindingSession
	{
		private readonly Dictionary<string, ComboBindingData> _bindings =
			new(StringComparer.OrdinalIgnoreCase);

		private readonly Dictionary<string, List<string>> _vanillaKeyStatus =
			new(StringComparer.OrdinalIgnoreCase);

		private readonly Dictionary<string, bool> _justPressed = new(StringComparer.OrdinalIgnoreCase);
		private readonly Dictionary<string, bool> _current = new(StringComparer.OrdinalIgnoreCase);
		private readonly Dictionary<string, double> _holdMs = new(StringComparer.OrdinalIgnoreCase);
		private readonly Dictionary<string, bool> _holdFired = new(StringComparer.OrdinalIgnoreCase);

		private HashSet<string> _previousPressed = new(StringComparer.OrdinalIgnoreCase);
		private readonly Dictionary<string, List<int>> _tokenIndex = new(StringComparer.OrdinalIgnoreCase);
		private string[] _compiledIds = Array.Empty<string>();
		private bool _compiledDirty = true;

		public IReadOnlyDictionary<string, ComboBindingData> Bindings => _bindings;
		public IReadOnlyDictionary<string, List<string>> VanillaKeyStatus => _vanillaKeyStatus;

		public void SetBinding(string actionId, ComboBindingData combo)
		{
			if (string.IsNullOrEmpty(actionId))
				return;

			if (combo == null || combo.IsEmpty)
			{
				_bindings.Remove(actionId);
				ApplyKeyStatus(actionId, BindingVanillaOwnershipSync.BuildKeyStatusEntries(null));
			}
			else
			{
				_bindings[actionId] = Clone(combo);
				ApplyKeyStatus(actionId, BindingVanillaOwnershipSync.BuildKeyStatusEntries(_bindings[actionId]));
			}

			InvalidateCompiled();
		}

		public void ClearBinding(string actionId)
		{
			if (string.IsNullOrEmpty(actionId))
				return;

			_bindings.Remove(actionId);
			// Same as BindingStorage.ClearBindings: unset → empty KeyStatus (no default restore).
			ApplyKeyStatus(actionId, new List<string>());
			InvalidateCompiled();
		}

		/// <summary>
		/// Mirrors <c>PromoteHookManagedVanillaBindings</c>: raw hook token in KeyStatus → advanced combo + markers.
		/// </summary>
		public bool TryPromoteRawHookToken(string actionId, string rawToken)
		{
			if (string.IsNullOrEmpty(actionId) || _bindings.ContainsKey(actionId))
				return false;

			if (!BindingVanillaOwnershipSync.TryBuildPromoteFromRawHookToken(
				rawToken, out ComboBindingData advanced, out List<string> keyStatusAfter))
				return false;

			_bindings[actionId] = advanced;
			ApplyKeyStatus(actionId, keyStatusAfter);
			InvalidateCompiled();
			return true;
		}

		/// <summary>Simulates load of bindings.json then syncing KeyStatus from stored combos.</summary>
		public void LoadBindingsAndSync(IReadOnlyDictionary<string, ComboBindingData> loaded)
		{
			_bindings.Clear();
			_vanillaKeyStatus.Clear();
			if (loaded != null)
			{
				foreach (KeyValuePair<string, ComboBindingData> pair in loaded)
				{
					if (pair.Value == null || pair.Value.IsEmpty)
						continue;
					_bindings[pair.Key] = Clone(pair.Value);
					ApplyKeyStatus(pair.Key, BindingVanillaOwnershipSync.BuildKeyStatusEntries(_bindings[pair.Key]));
				}
			}

			InvalidateCompiled();
		}

		/// <summary>
		/// Simulates Bindings.json load while PlayerInput profile is unavailable (KeyStatus untouched),
		/// or a later profile rebuild that left stale empty / default residues.
		/// </summary>
		public void LoadBindingsWithoutKeyStatusSync(IReadOnlyDictionary<string, ComboBindingData> loaded)
		{
			_bindings.Clear();
			if (loaded != null)
			{
				foreach (KeyValuePair<string, ComboBindingData> pair in loaded)
				{
					if (pair.Value == null || pair.Value.IsEmpty)
						continue;
					_bindings[pair.Key] = Clone(pair.Value);
				}
			}

			InvalidateCompiled();
		}

		/// <summary>Overwrite KeyStatus for an action (empty list / null → unbound).</summary>
		public void SeedVanillaKeyStatus(string actionId, params string[] entries)
		{
			if (string.IsNullOrEmpty(actionId))
				return;
			if (entries == null || entries.Length == 0)
				ApplyKeyStatus(actionId, new List<string>());
			else
				ApplyKeyStatus(actionId, new List<string>(entries));
		}

		/// <summary>
		/// Other-mod EnsureDefault pattern: if KeyStatus is effectively unbound, write <paramref name="defaultKey"/>.
		/// Does nothing when markers / any non-None entry is present.
		/// </summary>
		public bool SimulateOtherModEnsureDefault(string actionId, string defaultKey)
		{
			if (string.IsNullOrEmpty(actionId) || string.IsNullOrEmpty(defaultKey))
				return false;

			List<string> current = GetVanillaAssigned(actionId);
			if (AdvancedVanillaOwnershipMarker.IsEffectivelyBound(current))
				return false;

			ApplyKeyStatus(actionId, new List<string> { defaultKey });
			return true;
		}

		/// <summary>
		/// Mirrors production <c>BindingStorage.EnsureVanillaOwnershipConsistent</c>:
		/// rewrite KeyStatus from stored combos when mismatched. Does not alter bindings.json contents.
		/// </summary>
		public int RepairOwnershipFromBindings()
		{
			int repaired = 0;
			foreach (KeyValuePair<string, ComboBindingData> pair in _bindings)
			{
				ComboBindingData combo = pair.Value;
				if (combo == null)
					continue;

				List<string> current = GetVanillaAssigned(pair.Key);
				if (BindingVanillaOwnershipSync.MatchesOwnershipFromSlots(
					current, combo.Hold, combo.Slot1, combo.Slot2, combo.Slot3))
					continue;

				ApplyKeyStatus(pair.Key, BindingVanillaOwnershipSync.BuildKeyStatusEntries(combo));
				repaired++;
			}

			return repaired;
		}

		/// <summary>
		/// Consumer that skips AK JustPressed when KeyStatus is a sole raw default token (e.g. Mouse3),
		/// matching common "physical mouse path vs remapped JustPressed" forks.
		/// </summary>
		public bool ConsumerJustPressedAvoidingSoleRawDefault(string actionId, string soleRawDefault)
		{
			List<string> assigned = GetVanillaAssigned(actionId);
			if (!AdvancedVanillaOwnershipMarker.IsEffectivelyBound(assigned))
				return false;
			if (AdvancedVanillaOwnershipMarker.IsSoleRawToken(assigned, soleRawDefault))
				return false;
			return WasJustPressed(actionId);
		}

		/// <summary>
		/// Simulates AK Unload: replace advanced markers with one vanilla-fireable key from the combo
		/// (Slot3→2→1, non-modifier preferred). Clears KeyStatus when nothing converts.
		/// </summary>
		public void Unload(Func<string, string> tryGetDefault = null)
		{
			// tryGetDefault retained for call-site compatibility; unload now prefers combo keys.
			_ = tryGetDefault;

			foreach (KeyValuePair<string, ComboBindingData> pair in _bindings)
			{
				if (pair.Value == null || !pair.Value.RequiresAdvancedStorage)
					continue;

				if (UnloadVanillaKeyRestorePolicy.TrySelectPrimaryVanillaKey(pair.Value, out string vanillaKey)
					&& !string.IsNullOrEmpty(vanillaKey))
					ApplyKeyStatus(pair.Key, new List<string> { vanillaKey });
				else
					ApplyKeyStatus(pair.Key, new List<string>());
			}

			_bindings.Clear();
			InvalidateCompiled();
		}

		private void ApplyKeyStatus(string actionId, List<string> entries)
		{
			if (entries == null || entries.Count == 0)
			{
				_vanillaKeyStatus.Remove(actionId);
				return;
			}

			_vanillaKeyStatus[actionId] = new List<string>(entries);
		}

		public ComboBindingData GetBinding(string actionId)
			=> _bindings.TryGetValue(actionId, out ComboBindingData c) ? c : null;

		public List<string> GetVanillaAssigned(string actionId)
		{
			if (!_vanillaKeyStatus.TryGetValue(actionId, out List<string> keys) || keys == null)
				return new List<string>();
			return new List<string>(keys);
		}

		public bool HasConsumerVisibleBinding(string actionId)
			=> AdvancedVanillaOwnershipMarker.IsEffectivelyBound(GetVanillaAssigned(actionId));

		public void InvalidateCompiled()
		{
			_compiledDirty = true;
			_justPressed.Clear();
			_current.Clear();
			_holdMs.Clear();
			_holdFired.Clear();
		}

		public void EnsureCompiled()
		{
			if (!_compiledDirty)
				return;

			_tokenIndex.Clear();
			var ids = new List<string>(_bindings.Count);
			foreach (KeyValuePair<string, ComboBindingData> pair in _bindings)
			{
				ids.Add(pair.Key);
				List<string> keys = pair.Value.Keys;
				for (int i = 0; i < keys.Count; i++)
				{
					string token = keys[i];
					if (string.IsNullOrEmpty(token))
						continue;
					if (!_tokenIndex.TryGetValue(token, out List<int> list))
					{
						list = new List<int>();
						_tokenIndex[token] = list;
					}

					list.Add(ids.Count - 1);
				}
			}

			_compiledIds = ids.ToArray();
			_compiledDirty = false;
		}

		public IReadOnlyDictionary<string, List<int>> TokenIndex
		{
			get
			{
				EnsureCompiled();
				return _tokenIndex;
			}
		}

		public string[] CompiledActionIds
		{
			get
			{
				EnsureCompiled();
				return _compiledIds;
			}
		}

		/// <summary>One gameplay frame: evaluate all non-plain bindings against the snapshot.</summary>
		public void Update(HashSet<string> pressedNow, double frameDeltaMs = 16.666)
		{
			EnsureCompiled();
			_justPressed.Clear();
			_current.Clear();

			BindingPhysicalHeldMatch.BeginFrame(Environment.TickCount);

			for (int i = 0; i < _compiledIds.Length; i++)
			{
				string actionId = _compiledIds[i];
				ComboBindingData combo = _bindings[actionId];
				List<string> keys = combo.Keys;

				if (combo.Hold && keys.Count == 1 && !combo.HoldOnRelease)
				{
					bool down = BindingPhysicalHeldMatch.IsHeld(keys[0], pressedNow);
					if (!down)
					{
						_holdMs.Remove(actionId);
						_holdFired.Remove(actionId);
						continue;
					}

					_holdMs.TryGetValue(actionId, out double acc);
					acc += frameDeltaMs;
					_holdMs[actionId] = acc;
					_current[actionId] = true;

					_holdFired.TryGetValue(actionId, out bool fired);
					if (BindingComboTapEvaluator.EvaluateHoldWhileHeldFire(
						combo.Hold, combo.HoldOnRelease, combo.HoldMs, acc, fired, keys, pressedNow))
					{
						_justPressed[actionId] = true;
						_holdFired[actionId] = true;
					}

					continue;
				}

				BindingComboTapEvaluator.FrameResult r = BindingComboTapEvaluator.EvaluateNonHold(
					combo.Hold, keys, pressedNow, _previousPressed);

				if (r.SkippedPlainSingleTap || r.SkippedEmpty || !r.AllDown)
					continue;

				if (r.JustPressed)
					_justPressed[actionId] = true;
				if (r.IsCurrent)
					_current[actionId] = true;
			}

			_previousPressed = new HashSet<string>(pressedNow, StringComparer.OrdinalIgnoreCase);
		}

		public bool WasJustPressed(string actionId)
			=> _justPressed.TryGetValue(actionId, out bool v) && v;

		public bool IsCurrent(string actionId)
			=> _current.TryGetValue(actionId, out bool v) && v;

		/// <summary>
		/// Simulates ModKeybind consumer: require visible KeyStatus binding, then AK JustPressed.
		/// </summary>
		public bool ConsumerJustPressed(string actionId)
		{
			if (!HasConsumerVisibleBinding(actionId))
				return false;
			return WasJustPressed(actionId);
		}

		private static ComboBindingData Clone(ComboBindingData src)
		{
			return new ComboBindingData {
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
	}
}
