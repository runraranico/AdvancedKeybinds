using System;
using System.Collections.Generic;
using AdvancedKeybinds.Binding;
using AdvancedKeybinds.Diagnostics;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Per ComboPanel session undo stack for unsaved edits (slots / hold / clear).
	/// Not tied to bindings.json or ControlsBindingUndo.
	/// </summary>
	internal static class ComboPanelEditHistory
	{
		private static readonly List<ComboBinding> _stack = new(8);
		private static string _actionId;
		private static bool _active;

		internal static int Depth => _stack.Count;

		internal static bool CanUndo => _active && _stack.Count > 0;

		internal static void Begin(string actionId, ComboBinding initial)
		{
			_stack.Clear();
			_actionId = actionId ?? "?";
			_active = true;
			GamePadProbeSystem.LogPanelEditHistoryBegin(
				_actionId,
				FormatSlots(initial),
				FormatHoldMode(initial),
				initial?.EffectiveHoldMs ?? ComboBinding.DefaultHoldMs);
		}

		internal static void End(string reason)
		{
			int discarded = _stack.Count;
			_stack.Clear();
			_active = false;
			string action = _actionId;
			_actionId = null;
			GamePadProbeSystem.LogPanelEditHistoryEnd(reason ?? "PanelClose", discarded, action);
		}

		internal static void Reset()
		{
			if (_active || _stack.Count > 0)
				End("Reset");
			else
			{
				_stack.Clear();
				_actionId = null;
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
			GamePadProbeSystem.LogPanelEditHistoryPush(reason ?? "Edit", _stack.Count);
		}

		internal static bool TryUndo(out ComboBinding restored)
		{
			restored = null;
			if (!_active)
			{
				GamePadProbeSystem.LogPanelUndoUnavailable("Inactive");
				return false;
			}

			if (_stack.Count == 0)
			{
				GamePadProbeSystem.LogPanelUndoUnavailable("NoHistory");
				return false;
			}

			int before = _stack.Count;
			restored = _stack[before - 1];
			_stack.RemoveAt(before - 1);
			GamePadProbeSystem.LogPanelUndoApply("CancelButton", before, _stack.Count, panelStillOpen: true);
			return true;
		}

		private static string FormatSlots(ComboBinding b)
		{
			if (b == null)
				return "-/-/-";
			return (b.Slot1 ?? "-") + "/" + (b.Slot2 ?? "-") + "/" + (b.Slot3 ?? "-");
		}

		private static string FormatHoldMode(ComboBinding b)
		{
			if (b == null || !b.Hold)
				return "Off";
			return b.HoldOnRelease ? "OnRelease" : "WhileHeld";
		}
	}
}
