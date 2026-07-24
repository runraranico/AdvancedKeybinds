using System;
using System.Collections.Generic;

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
			public bool IsPlainSingleTap;
			public bool RequiresAdvancedStorage;
			public bool IsHookManagedTap;
			public bool IsMultiKeyToggleCombo;
		}

		private static CompiledBinding[] _compiled = Array.Empty<CompiledBinding>();
		private static readonly List<int> HookManagedTapIndices = new();
		private static readonly List<int> MultiToggleComboIndices = new();
		private static readonly HashSet<string> PreviousCurrentActions = new(StringComparer.OrdinalIgnoreCase);

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

			int count = 0;
			foreach (KeyValuePair<string, ComboBinding> _ in BindingStorage.AllBindings)
				count++;

			var arr = new CompiledBinding[count];
			int i = 0;
			foreach (KeyValuePair<string, ComboBinding> pair in BindingStorage.AllBindings)
			{
				ComboBinding combo = pair.Value;
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
					IsPlainSingleTap = combo.IsPlainSingleTap,
					RequiresAdvancedStorage = combo.RequiresAdvancedStorage(),
					IsHookManagedTap = hookTap,
					IsMultiKeyToggleCombo = multiToggle
				};

				if (hookTap)
					HookManagedTapIndices.Add(i);
				if (multiToggle)
					MultiToggleComboIndices.Add(i);
				i++;
			}

			_compiled = arr;
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
