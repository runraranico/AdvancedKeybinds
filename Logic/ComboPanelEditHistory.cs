using System;
using System.Collections.Generic;
using AdvancedKeybinds.Binding;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Per ComboPanel session undo stack for unsaved edits (slots / hold / clear).
	/// Not tied to bindings.json or ControlsBindingUndo.
	/// </summary>
	internal static class ComboPanelEditHistory
	{
		private static readonly List<ComboBinding> _stack = new(8);
		private static bool _active;

		internal static int Depth => _stack.Count;

		internal static bool CanUndo => _active && _stack.Count > 0;

		internal static void Begin(string actionId, ComboBinding initial)
		{
			_stack.Clear();
			_active = true;
		}

		internal static void End(string reason)
		{
			_stack.Clear();
			_active = false;
		}

		internal static void Reset()
		{
			if (_active || _stack.Count > 0)
				End("Reset");
			else
			{
				_stack.Clear();
				_active = false;
			}
		}

		/// <summary>
		/// Push a clone of <paramref name="before"/> if it differs from the last pushed state
		/// (or from empty stack). No-op when inactive or equal to current top.
		/// </summary>
		internal static void PushBeforeChange(ComboBinding before, string reason)
		{
			if (!_active || before == null)
				return;

			if (_stack.Count > 0 && ComboBinding.ContentEquals(_stack[_stack.Count - 1], before))
				return;

			_stack.Add(before.Clone());
		}

		internal static bool TryUndo(out ComboBinding restored)
		{
			restored = null;
			if (!_active)
			{
				return false;
			}

			if (_stack.Count == 0)
			{
				return false;
			}

			int before = _stack.Count;
			restored = _stack[before - 1];
			_stack.RemoveAt(before - 1);
			return true;
		}
	}
}
