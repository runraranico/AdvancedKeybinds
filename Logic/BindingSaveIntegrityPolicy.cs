using System;

namespace AdvancedKeybinds.Core
{
		/// <summary>
		/// Integrity rules for bindings.json Load/Save.
		/// Enforced by BindingStorage.SaveToFile after InvalidJson load failure.
		/// </summary>
		public static class BindingSaveIntegrityPolicy
		{
		public static bool ShouldAllowSaveAfterLoadFailure(
			bool loadSucceeded,
			bool memoryCombosEmpty,
			bool diskFileExistsWithContent)
		{
			if (loadSucceeded)
				return true;
			// Failed load left memory empty while a non-empty disk file remains:
			// writing memory would wipe user bindings (Jump/Space etc.).
			if (memoryCombosEmpty && diskFileExistsWithContent)
				return false;
			return true;
		}

		public static bool ShouldCommitComboPanelClose(bool saveRequested, bool dirty)
			=> saveRequested && dirty;

		public static bool JumpKeyboardMustNotShareStorageKeyWithGamePad(
			string keyboardStorageKey,
			string gamePadStorageKey)
		{
			if (string.IsNullOrEmpty(keyboardStorageKey) || string.IsNullOrEmpty(gamePadStorageKey))
				return false;
			return !string.Equals(keyboardStorageKey, gamePadStorageKey, StringComparison.OrdinalIgnoreCase);
		}

	}
}
