using System;
using System.Collections.Generic;
using AdvancedKeybinds.Core;
using AdvancedKeybinds.Features.DoubleTap;

namespace AdvancedKeybinds.Binding
{
	/// <summary>
	/// Compiled binding table + Hook/Multi index lists for the full evaluation path.
	/// </summary>
	public static partial class BindingInputEngine
	{
		private sealed class CompiledBinding
		{
			public string ActionId;
			public string[] Keys;
			public string AdditionalOrToken;
			public bool IsPlainSingleTap;
			public bool RequiresAdvancedStorage;
			public bool IsHookManagedTap;
			public bool IsMultiKeyToggleCombo;
			public bool IsDoubleTap;
			public bool OwnsDoubleTapInput;
			public ComboBinding InactiveDoubleTapView;
			/// <summary>Active 1-key DT fire state; null when Hold / multi-slot / ineligible / bad window.</summary>
			public DoubleTapRuntime DoubleTapRuntime;
			public string DoubleTapPrimaryToken;
			public int DoubleTapWindowMs;
		}

		private static CompiledBinding[] _compiled = Array.Empty<CompiledBinding>();
		private static readonly List<int> HookManagedTapIndices = new();
		private static readonly List<int> MultiToggleComboIndices = new();
		private static readonly HashSet<string> PreviousCurrentActions = new(StringComparer.OrdinalIgnoreCase);
		/// <summary>
		/// Eligible DT storage keys rebuilt with compiled table only (no per-call registration Reflection).
		/// </summary>
		private static readonly HashSet<string> EligibleDoubleTapStorageKeys =
			new(StringComparer.Ordinal);

		private static bool _compiledDirty = true;

		private static int _lastEvalBindCount;
		public static int LastEvaluatedBindingCount => _lastEvalBindCount;

		internal static void MarkCompiledDirty()
		{
			_compiledDirty = true;
		}

		private static void EnsureCompiled()
		{
			if (!_compiledDirty && _compiled != null)
				return;
			RebuildCompiledBindings();
			_compiledDirty = false;
		}

		private static void RebuildCompiledBindings()
		{
			HookManagedTapIndices.Clear();
			MultiToggleComboIndices.Clear();
			EligibleDoubleTapStorageKeys.Clear();
			int activeDt = 0;

			int count = 0;
			foreach (KeyValuePair<string, ComboBinding> _ in BindingStorage.AllBindings)
				count++;

			var arr = new CompiledBinding[count];
			int i = 0;
			foreach (KeyValuePair<string, ComboBinding> pair in BindingStorage.AllBindings)
			{
				ComboBinding combo = pair.Value;

				// DT first: classify every DoubleTap row (including UseTerrariaKeyStatusOr / TwoKey keys).
				// Ineligible → empty keys, no OR, no ownership, no hook/multi/fallback primary indexing.
				// Eligible → existing inactive OR-only view; RequiresAdvancedStorage = eligible only.
				if (combo.DoubleTap)
				{
					bool dtEligible = DoubleTapBindingBridge.IsEligibleStorage(pair.Key);
					if (dtEligible)
						EligibleDoubleTapStorageKeys.Add(pair.Key);

					ComboBinding evalCombo = dtEligible
						? DoubleTapBindingBridge.CreateInactiveView(combo, eligible: true)
						: new ComboBinding();
					List<string> dtKeys = evalCombo.Keys;
					var dtKeyArr = new string[dtKeys.Count];
					for (int k = 0; k < dtKeys.Count; k++)
						dtKeyArr[k] = dtKeys[k];

					// Active fire cache only. OR-only inactive view stays independent (Bridge unchanged).
					string primary = null;
					int windowMs = 0;
					DoubleTapRuntime runtime = null;
					int slots = combo.FilledSlotCount;
					int rawWindow = combo.DoubleTapMs;
					bool windowOk = rawWindow >= DoubleTapMsPolicy.MinMs
						&& rawWindow <= DoubleTapMsPolicy.MaxMs;
					if (dtEligible
						&& !combo.Hold
						&& slots == 1
						&& windowOk)
					{
						primary = !string.IsNullOrEmpty(combo.Slot1) ? combo.Slot1
							: !string.IsNullOrEmpty(combo.Slot2) ? combo.Slot2 : combo.Slot3;
						windowMs = rawWindow;
						runtime = new DoubleTapRuntime();
						activeDt++;
					}

					arr[i] = new CompiledBinding
					{
						ActionId = pair.Key,
						Keys = dtKeyArr,
						AdditionalOrToken = dtEligible ? evalCombo.AdditionalOrToken : null,
						IsPlainSingleTap = false,
						RequiresAdvancedStorage = dtEligible,
						IsHookManagedTap = false,
						IsMultiKeyToggleCombo = false,
						IsDoubleTap = true,
						OwnsDoubleTapInput = dtEligible,
						InactiveDoubleTapView = dtEligible ? evalCombo : null,
						DoubleTapRuntime = runtime,
						DoubleTapPrimaryToken = primary,
						DoubleTapWindowMs = windowMs
					};
					// Never index DT primary into HookManaged / MultiToggle lists.
					i++;
					continue;
				}

				// Non-DT only: 28-record Terraria-OR / UseTerrariaKeyStatusOr skip AK Trigger evaluation.
				if (GamePadTwoKeyTerrariaOrPolicy.IsTargetStorageKey(pair.Key)
					|| combo.UseTerrariaKeyStatusOr)
				{
					arr[i] = new CompiledBinding
					{
						ActionId = pair.Key,
						Keys = Array.Empty<string>(),
						AdditionalOrToken = null,
						IsPlainSingleTap = true,
						RequiresAdvancedStorage = false,
					IsHookManagedTap = false,
					IsMultiKeyToggleCombo = false
				};
					i++;
					continue;
				}

				List<string> keys = combo.Keys;
				var keyArr = new string[keys.Count];
				for (int k = 0; k < keys.Count; k++)
					keyArr[k] = keys[k];

				bool hookTap = keys.Count == 1 && !combo.Hold
					&& (BindingRestrictions.IsToggleLockToken(keys[0])
						|| BindingRestrictions.IsWindowsKeyToken(keys[0]));

				bool multiToggle = false;
				if (keys.Count > 1)
				{
					for (int k = 0; k < keys.Count; k++)
					{
						if (BindingRestrictions.IsToggleLockToken(keys[k]))
						{
							multiToggle = true;
							break;
						}
					}
				}

				arr[i] = new CompiledBinding
				{
					ActionId = pair.Key,
					Keys = keyArr,
					AdditionalOrToken = combo.AdditionalOrToken,
					IsPlainSingleTap = combo.IsPlainSingleTap,
					RequiresAdvancedStorage = combo.RequiresAdvancedStorage(),
					IsHookManagedTap = hookTap,
					IsMultiKeyToggleCombo = multiToggle,
					IsDoubleTap = false,
					OwnsDoubleTapInput = false,
					InactiveDoubleTapView = null,
					DoubleTapRuntime = null,
					DoubleTapPrimaryToken = null,
					DoubleTapWindowMs = 0
				};

				if (hookTap)
					HookManagedTapIndices.Add(i);
				if (multiToggle)
					MultiToggleComboIndices.Add(i);
				i++;
			}

			_compiled = arr;
			_activeDoubleTapCount = activeDt;
		}

		private static void CapturePreviousCurrent()
		{
			PreviousCurrentActions.Clear();
			foreach (KeyValuePair<string, bool> pair in CurrentActions)
			{
				if (pair.Value)
					PreviousCurrentActions.Add(pair.Key);
			}
		}
	}
}
