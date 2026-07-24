using System;
using System.Collections.Generic;
using Terraria.GameInput;

namespace AdvancedKeybinds.Binding
{
	/// <summary>
	/// One-level undo for edits committed from the controls screen.
	/// Keeps vanilla and advanced storage together so a row is restored exactly as it was.
	/// </summary>
	internal static class ControlsBindingUndo
	{
		internal sealed class Snapshot
		{
			public string ActionId;
			public PlayerInputProfile Profile;
			public ComboBinding Advanced;
			public List<string> VanillaKeys;
			public bool OpenKeybindMenuCustomized;
			public bool HoldTierHudHotkeyCustomized;
		}

		private static Snapshot _last;

		internal static bool CanUndo
			=> _last != null
				&& ReferenceEquals(_last.Profile, PlayerInput.CurrentProfile);

		internal static Snapshot Capture(string actionId)
		{
			if (string.IsNullOrEmpty(actionId) || PlayerInput.CurrentProfile == null)
				return null;

			return new Snapshot {
				ActionId = actionId,
				Profile = PlayerInput.CurrentProfile,
				Advanced = BindingStorage.Get(actionId)?.Clone(),
				VanillaKeys = VanillaRebindHelper.GetAssignedKeysSnapshot(actionId),
				OpenKeybindMenuCustomized = ClientPrefs.OpenKeybindMenuCustomized,
				HoldTierHudHotkeyCustomized = ClientPrefs.HoldTierHudHotkeyCustomized
			};
		}

		internal static void RememberIfChanged(Snapshot before, string reason)
		{
			if (before == null
				|| string.IsNullOrEmpty(before.ActionId)
				|| !ReferenceEquals(before.Profile, PlayerInput.CurrentProfile))
			{
				return;
			}

			Snapshot after = Capture(before.ActionId);
			if (SnapshotsEqual(before, after))
				return;

			_last = before;
			if (AkDiag.Enabled) AkDiag.Log("Controls undo remembered action='" + before.ActionId + "' reason=" + (reason ?? "(none)"));
		}

		internal static bool TryUndo()
		{
			if (!CanUndo)
			{
				_last = null;
				return false;
			}

			Snapshot restore = _last;
			_last = null;

			if (restore.Advanced == null || restore.Advanced.IsEmpty)
				BindingStorage.ClearBindings(restore.ActionId, "controls-undo");
			else
				BindingStorage.Set(restore.ActionId, restore.Advanced, "controls-undo");

			VanillaRebindHelper.RestoreAssignedKeys(
				restore.ActionId,
				restore.VanillaKeys,
				"controls-undo");

			ClientPrefs.OpenKeybindMenuCustomized = restore.OpenKeybindMenuCustomized;
			ClientPrefs.HoldTierHudHotkeyCustomized = restore.HoldTierHudHotkeyCustomized;

			if (AkDiag.Enabled) AkDiag.Log("Controls undo restored action='" + restore.ActionId + "'.");
			return true;
		}

		internal static void Clear()
		{
			_last = null;
		}

		private static bool SnapshotsEqual(Snapshot a, Snapshot b)
		{
			if (a == null || b == null)
				return a == b;

			return string.Equals(a.ActionId, b.ActionId, StringComparison.Ordinal)
				&& ReferenceEquals(a.Profile, b.Profile)
				&& ComboEquals(a.Advanced, b.Advanced)
				&& KeysEqual(a.VanillaKeys, b.VanillaKeys)
				&& a.OpenKeybindMenuCustomized == b.OpenKeybindMenuCustomized
				&& a.HoldTierHudHotkeyCustomized == b.HoldTierHudHotkeyCustomized;
		}

		private static bool ComboEquals(ComboBinding a, ComboBinding b)
		{
			bool aEmpty = a == null || a.IsEmpty;
			bool bEmpty = b == null || b.IsEmpty;
			if (aEmpty || bEmpty)
				return aEmpty == bEmpty;

			return string.Equals(a.Slot1, b.Slot1, StringComparison.Ordinal)
				&& string.Equals(a.Slot2, b.Slot2, StringComparison.Ordinal)
				&& string.Equals(a.Slot3, b.Slot3, StringComparison.Ordinal)
				&& string.Equals(a.Slot1Display, b.Slot1Display, StringComparison.Ordinal)
				&& string.Equals(a.Slot2Display, b.Slot2Display, StringComparison.Ordinal)
				&& string.Equals(a.Slot3Display, b.Slot3Display, StringComparison.Ordinal)
				&& a.Hold == b.Hold
				&& a.HoldOnRelease == b.HoldOnRelease
				&& a.HoldMs == b.HoldMs;
		}

		private static bool KeysEqual(List<string> a, List<string> b)
		{
			int aCount = a?.Count ?? 0;
			int bCount = b?.Count ?? 0;
			if (aCount != bCount)
				return false;

			for (int i = 0; i < aCount; i++)
			{
				if (!string.Equals(a[i], b[i], StringComparison.Ordinal))
					return false;
			}

			return true;
		}
	}
}
