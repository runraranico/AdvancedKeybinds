using System;
using System.Collections.Generic;
using System.Text;

namespace AdvancedKeybinds.Binding
{
	/// <summary>
	/// Breadcrumbs for keybind clear / overwrite / empty-edge. Gated by <see cref="AkDiag.Enabled"/>.
	/// Logs only when the assigned key(s) actually change (no per-frame spam).
	/// </summary>
	internal static class BindingMutationDiag
	{
		/// <summary>Actions highlighted when they become unset (report case: Hotbar2 + movement).</summary>
		private static readonly string[] WatchedActionOrder =
		{
			"Hotbar1", "Hotbar2", "Hotbar3", "Hotbar4", "Hotbar5",
			"Hotbar6", "Hotbar7", "Hotbar8", "Hotbar9", "Hotbar10",
			"Left", "Right", "Up", "Down", "Jump"
		};

		private static readonly HashSet<string> WatchedActions = new(WatchedActionOrder, StringComparer.OrdinalIgnoreCase);

		/// <summary>Last observed "vanilla|advanced" snapshot while the controls UI draws a row.</summary>
		private static readonly Dictionary<string, string> LastObserved = new(StringComparer.OrdinalIgnoreCase);

		public static void LogVanillaMutation(string actionId, string before, string after, string reason)
		{
			if (!AkDiag.Enabled || string.IsNullOrEmpty(actionId))
				return;

			string b = Normalize(before);
			string a = Normalize(after);
			if (string.Equals(b, a, StringComparison.Ordinal))
				return;

			RememberObservation(actionId, a, FormatAdvancedSnapshot(actionId));
			if (AkDiag.Enabled) AkDiag.Log(BuildMessage("vanilla", actionId, b, a, reason));
		}

		public static void LogAdvancedMutation(string actionId, string before, string after, string reason)
		{
			if (!AkDiag.Enabled || string.IsNullOrEmpty(actionId))
				return;

			string b = Normalize(before);
			string a = Normalize(after);
			if (string.Equals(b, a, StringComparison.Ordinal))
				return;

			RememberObservation(actionId, VanillaRebindHelper.FormatAssignedKeys(actionId), a);
			if (AkDiag.Enabled) AkDiag.Log(BuildMessage("advanced", actionId, b, a, reason));
		}

		/// <summary>
		/// Call from controls-row draw. Detects KeyStatus / advanced storage drifting without going through
		/// our helpers (vanilla rebind, profile reload, other mods). Logs only on change.
		/// </summary>
		public static void ObserveControlsRow(string actionId)
		{
			if (!AkDiag.Enabled || string.IsNullOrEmpty(actionId))
				return;

			string vanilla = VanillaRebindHelper.FormatAssignedKeys(actionId);
			string advanced = FormatAdvancedSnapshot(actionId);
			string snap = vanilla + "\n" + advanced;

			if (!LastObserved.TryGetValue(actionId, out string previous))
			{
				LastObserved[actionId] = snap;
				return;
			}

			if (string.Equals(previous, snap, StringComparison.Ordinal))
				return;

			string[] parts = previous.Split('\n');
			string prevVan = parts.Length > 0 ? parts[0] : "(unset)";
			string prevAdv = parts.Length > 1 ? parts[1] : "(none)";
			LastObserved[actionId] = snap;

			if (!string.Equals(prevVan, vanilla, StringComparison.Ordinal))
				if (AkDiag.Enabled) AkDiag.Log(BuildMessage("observe-vanilla", actionId, prevVan, vanilla, "controls-ui-draw-drift"));

			if (!string.Equals(prevAdv, advanced, StringComparison.Ordinal))
				if (AkDiag.Enabled) AkDiag.Log(BuildMessage("observe-advanced", actionId, prevAdv, advanced, "controls-ui-draw-drift"));
		}

		public static void ResetObservationBaseline()
		{
			if (!AkDiag.Enabled)
				return;
			LastObserved.Clear();
		}

		/// <summary>One-shot after load: watched actions' vanilla/advanced state (not spam; startup only).</summary>
		public static void LogWatchedBaseline(string phase)
		{
			if (!AkDiag.Enabled)
				return;

			var sb = new StringBuilder(256);
			sb.Append("BindMut baseline phase=").Append(string.IsNullOrEmpty(phase) ? "?" : phase);
			foreach (string actionId in WatchedActionOrder)
			{
				string vanilla = VanillaRebindHelper.FormatAssignedKeys(actionId);
				string advanced = FormatAdvancedSnapshot(actionId);
				sb.Append(" | ").Append(actionId).Append('=')
					.Append(vanilla);
				if (!string.Equals(advanced, "(none)", StringComparison.Ordinal))
					sb.Append('/').Append(advanced);
				if (IsUnset(vanilla))
					sb.Append(string.Equals(actionId, "Hotbar2", StringComparison.OrdinalIgnoreCase)
						? "{EMPTY,HOTBAR2}"
						: "{EMPTY}");
				RememberObservation(actionId, vanilla, advanced);
			}

			if (AkDiag.Enabled) AkDiag.Log(sb.ToString());
		}

		internal static string FormatComboSnapshot(ComboBinding combo)
		{
			if (combo == null || combo.IsEmpty)
				return "(none)";

			var keys = combo.Keys;
			var sb = new StringBuilder();
			sb.Append(string.Join("+", keys));
			if (combo.Hold)
			{
				sb.Append(combo.HoldOnRelease ? "[HoldRelease:" : "[Hold:");
				sb.Append(combo.EffectiveHoldMs).Append("ms]");
			}

			if (combo.IsPlainSingleTap)
				sb.Append("(plain)");

			return sb.ToString();
		}

		private static string FormatAdvancedSnapshot(string actionId)
			=> FormatComboSnapshot(BindingStorage.Get(actionId));

		private static void RememberObservation(string actionId, string vanilla, string advanced)
		{
			LastObserved[actionId] = Normalize(vanilla) + "\n" + Normalize(advanced);
		}

		private static string BuildMessage(string kind, string actionId, string before, string after, string reason)
		{
			bool emptyEdge = !IsUnset(before) && IsUnset(after);
			bool watched = WatchedActions.Contains(actionId);
			var sb = new StringBuilder(160);
			sb.Append("BindMut ").Append(kind)
				.Append(" action=").Append(actionId)
				.Append(' ').Append(before).Append("->").Append(after)
				.Append(" reason=").Append(string.IsNullOrEmpty(reason) ? "?" : reason);
			if (emptyEdge)
				sb.Append(" EMPTY_EDGE");
			if (watched)
				sb.Append(" WATCH");
			if (string.Equals(actionId, "Hotbar2", StringComparison.OrdinalIgnoreCase))
				sb.Append(" HOTBAR2");
			return sb.ToString();
		}

		private static bool IsUnset(string value)
			=> string.IsNullOrEmpty(value)
				|| string.Equals(value, "(unset)", StringComparison.Ordinal)
				|| string.Equals(value, "(none)", StringComparison.Ordinal);

		private static string Normalize(string value)
			=> string.IsNullOrEmpty(value) ? "(unset)" : value;
	}
}
