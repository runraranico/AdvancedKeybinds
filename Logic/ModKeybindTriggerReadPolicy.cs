using System.Collections.Generic;

namespace AdvancedKeybinds.Logic
{
	/// <summary>
	/// tModLoader's ModKeybind.Current / JustPressed / JustReleased read
	/// <c>PlayerInput.Triggers.*.KeyStatus[FullName]</c> with the raw dictionary indexer.
	/// A mod action id only enters those dictionaries when TriggersSet is rebuilt from
	/// KeybindLoader.Keybinds, which tModLoader does through PlayerInput.ReInitialize after mod load.
	/// Between "content added" and that rebuild, a perfectly valid keybind is still absent and the
	/// indexer throws KeyNotFoundException, which tModLoader records in client.log at throw time
	/// (a try/catch around the read cannot remove that log entry).
	/// Callers must therefore ask <see cref="IsActionIdReadable"/> before touching the vanilla read.
	/// </summary>
	public static class ModKeybindTriggerReadPolicy
	{
		/// <summary>True when the vanilla trigger read for this action id cannot throw.</summary>
		public static bool IsActionIdReadable(IReadOnlyDictionary<string, bool> triggerKeyStatus, string actionId)
		{
			return triggerKeyStatus != null
				&& !string.IsNullOrEmpty(actionId)
				&& triggerKeyStatus.ContainsKey(actionId);
		}

		/// <summary>
		/// Vanilla trigger state when it is readable, OR-ed with the advanced-engine state.
		/// Plain single-tap bindings are vanilla-owned and are not tracked by the advanced engine,
		/// so the vanilla read must stay in the result — it is only skipped while unreadable.
		/// </summary>
		public static bool ResolveTriggerState(
			IReadOnlyDictionary<string, bool> triggerKeyStatus,
			string actionId,
			bool advancedState)
		{
			if (advancedState)
				return true;

			return IsActionIdReadable(triggerKeyStatus, actionId) && triggerKeyStatus[actionId];
		}
	}
}
