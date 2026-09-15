using System;
using System.Collections.Generic;
using AdvancedKeybinds.Core;
using Terraria.GameInput;

namespace AdvancedKeybinds.Binding
{
	/// <summary>
	/// One-level undo for edits committed from the controls screen.
	/// Keeps vanilla and advanced storage together so a row is restored exactly as it was.
	/// Vanilla KeyStatus snapshot/restore is scoped to the edited <see cref="InputMode"/> only.
	/// GamePad / GamePad UI undo uses one shared persist transaction (bindings + profiles once).
	/// </summary>
	internal static class ControlsBindingUndo
	{
		internal sealed class Snapshot
		{
			public string ActionId;
			public InputMode KeyStatusMode;
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

		/// <summary>
		/// Infers KeyStatus InputMode from a Combos/storage key (GP:G: / GP:U: / KU: / GP: / bare).
		/// </summary>
		internal static InputMode InferKeyStatusMode(string storageKey)
		{
			if (ControlsGameUiSeparationPolicy.IsGamePadGameStorageKey(storageKey))
				return InputMode.XBoxGamepad;
			if (ControlsGameUiSeparationPolicy.IsGamePadUiStorageKey(storageKey))
				return InputMode.XBoxGamepadUI;
			if (ControlsGameUiSeparationPolicy.IsKeyboardUiStorageKey(storageKey))
				return InputMode.KeyboardUI;
			if (ControlsGameUiSeparationPolicy.IsGamePadLegacySharedStorageKey(storageKey))
				return InputMode.XBoxGamepad;
			return InputMode.Keyboard;
		}

		internal static Snapshot Capture(string storageKey)
			=> Capture(storageKey, InferKeyStatusMode(storageKey));

		internal static Snapshot Capture(string storageKey, InputMode keyStatusMode)
		{
			if (string.IsNullOrEmpty(storageKey) || PlayerInput.CurrentProfile == null)
				return null;

			string trigger = ControlsGameUiSeparationPolicy.ToTriggerActionId(storageKey);
			return new Snapshot {
				ActionId = storageKey,
				KeyStatusMode = keyStatusMode,
				Profile = PlayerInput.CurrentProfile,
				Advanced = BindingStorage.Get(storageKey)?.Clone(),
				VanillaKeys = VanillaRebindHelper.GetAssignedKeysSnapshotForInputMode(trigger, keyStatusMode),
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

			Snapshot after = Capture(before.ActionId, before.KeyStatusMode);
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

			// Keep snapshot until the whole undo transaction succeeds (retryable on failure).
			Snapshot restore = _last;
			bool isGamePadMode = restore.KeyStatusMode == InputMode.XBoxGamepad
				|| restore.KeyStatusMode == InputMode.XBoxGamepadUI;

			if (isGamePadMode)
			{
				bool ok = BindingStorage.TryCommitGamePadUserEdit(
					restore.ActionId,
					"controls-undo",
					() => BindingStorage.ApplyUndoMemoryOnly(
						restore.ActionId,
						restore.Advanced,
						restore.VanillaKeys,
						restore.KeyStatusMode,
						restore.OpenKeybindMenuCustomized,
						restore.HoldTierHudHotkeyCustomized));
				if (ok)
					_last = null;
				else if (AkDiag.Enabled)
					AkDiag.Log("Controls undo aborted: GamePad transaction failed for '" + restore.ActionId + "'.");
				return ok;
			}

			if (restore.Advanced == null || restore.Advanced.IsEmpty)
			{
				if (!BindingStorage.ClearBindings(restore.ActionId, "controls-undo"))
				{
					if (AkDiag.Enabled) AkDiag.Log("Controls undo aborted: ClearBindings/persist failed for '" + restore.ActionId + "'.");
					return false;
				}
			}
			else if (!BindingStorage.Set(restore.ActionId, restore.Advanced, "controls-undo"))
			{
				if (AkDiag.Enabled) AkDiag.Log("Controls undo aborted: Set/persist failed for '" + restore.ActionId + "'.");
				return false;
			}

			string trigger = ControlsGameUiSeparationPolicy.ToTriggerActionId(restore.ActionId);
			VanillaRebindHelper.RestoreAssignedKeysForInputMode(
				trigger,
				restore.VanillaKeys,
				restore.KeyStatusMode,
				"controls-undo");

			ClientPrefs.OpenKeybindMenuCustomized = restore.OpenKeybindMenuCustomized;
			ClientPrefs.HoldTierHudHotkeyCustomized = restore.HoldTierHudHotkeyCustomized;

			_last = null;
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
				&& a.KeyStatusMode == b.KeyStatusMode
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
