using System;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Separates Controls "ゲーム操作" vs "メニューコントロール" storage and KeyStatus sync.
	/// Mod keybinds (ActionId contains '/') keep shared storage + dual-mode sync (existing OK behavior).
	/// Vanilla actions use mode-specific storage keys and sync only that InputMode.
	/// </summary>
	public static class ControlsGameUiSeparationPolicy
	{
		public const string KeyboardUiStoragePrefix = "KU:";
		public const string GamePadGamePrefix = "GP:G:";
		public const string GamePadUiPrefix = "GP:U:";
		public const string GamePadLegacyPrefix = "GP:";

		public const int ModeKeyboard = 0;
		public const int ModeKeyboardUi = 1;
		public const int ModeGamePad = 3;
		public const int ModeGamePadUi = 4;

		public static bool IsModKeybindActionId(string actionId)
			=> !string.IsNullOrEmpty(actionId) && actionId.IndexOf('/') >= 0;

		/// <summary>
		/// Storage key for ComboPanel / BindingStorage for the Controls row being edited.
		/// </summary>
		public static string ResolveStorageKey(int inputModeOrdinal, string actionId)
		{
			if (string.IsNullOrEmpty(actionId))
				return actionId;

			string bare = ToTriggerActionId(actionId);
			bool mod = IsModKeybindActionId(bare);

			if (inputModeOrdinal == ModeGamePad || inputModeOrdinal == ModeGamePadUi)
			{
				if (mod)
					return GamePadLegacyPrefix + bare;
				return inputModeOrdinal == ModeGamePadUi
					? GamePadUiPrefix + bare
					: GamePadGamePrefix + bare;
			}

			if (inputModeOrdinal == ModeKeyboardUi)
			{
				if (mod)
					return bare;
				return KeyboardUiStoragePrefix + bare;
			}

			// Keyboard (0) and Mouse (2): bare / legacy device key
			if (inputModeOrdinal == ModeKeyboard)
				return bare;

			return ControlsEditTargetPolicy.ToStorageKey(
				ControlsEditTargetPolicy.FromTerrariaInputModeOrdinal(inputModeOrdinal),
				bare);
		}

		public static bool IsKeyboardUiStorageKey(string storageKey)
			=> !string.IsNullOrEmpty(storageKey)
				&& storageKey.StartsWith(KeyboardUiStoragePrefix, StringComparison.Ordinal);

		public static bool IsGamePadGameStorageKey(string storageKey)
			=> !string.IsNullOrEmpty(storageKey)
				&& storageKey.StartsWith(GamePadGamePrefix, StringComparison.Ordinal);

		public static bool IsGamePadUiStorageKey(string storageKey)
			=> !string.IsNullOrEmpty(storageKey)
				&& storageKey.StartsWith(GamePadUiPrefix, StringComparison.Ordinal);

		/// <summary>Legacy GP:{action} that is not GP:G: / GP:U:.</summary>
		public static bool IsGamePadLegacySharedStorageKey(string storageKey)
		{
			if (string.IsNullOrEmpty(storageKey))
				return false;
			if (IsGamePadGameStorageKey(storageKey) || IsGamePadUiStorageKey(storageKey))
				return false;
			return storageKey.StartsWith(GamePadLegacyPrefix, StringComparison.Ordinal);
		}

		/// <summary>
		/// Strip KU: / GP:U: / GP:G: / GP: — does not use ControlsEditTargetPolicy.ToTriggerActionId
		/// (that API must keep documenting GP:G: → G:… for investigation tests).
		/// </summary>
		public static string ToTriggerActionId(string storageKey)
		{
			if (string.IsNullOrEmpty(storageKey))
				return storageKey;
			if (IsKeyboardUiStorageKey(storageKey))
				return storageKey.Substring(KeyboardUiStoragePrefix.Length);
			if (IsGamePadUiStorageKey(storageKey))
				return storageKey.Substring(GamePadUiPrefix.Length);
			if (IsGamePadGameStorageKey(storageKey))
				return storageKey.Substring(GamePadGamePrefix.Length);
			if (storageKey.StartsWith(GamePadLegacyPrefix, StringComparison.Ordinal))
				return storageKey.Substring(GamePadLegacyPrefix.Length);
			return storageKey;
		}

		public enum SyncModeKind
		{
			/// <summary>Write Keyboard + KeyboardUI (Mod keybinds).</summary>
			KeyboardBoth,
			/// <summary>Write a single Terraria InputMode ordinal.</summary>
			SingleInputMode,
			/// <summary>Write both GamePad InputModes (Mod GamePad legacy GP:).</summary>
			GamePadBoth
		}

		/// <summary>
		/// How BindingStorage should sync KeyStatus for this Combos key.
		/// </summary>
		public static SyncModeKind ResolveSyncKind(string storageKey, out int singleInputModeOrdinal)
		{
			singleInputModeOrdinal = -1;
			string trigger = ToTriggerActionId(storageKey);
			bool mod = IsModKeybindActionId(trigger);

			if (IsGamePadGameStorageKey(storageKey))
			{
				singleInputModeOrdinal = ModeGamePad;
				return SyncModeKind.SingleInputMode;
			}

			if (IsGamePadUiStorageKey(storageKey))
			{
				singleInputModeOrdinal = ModeGamePadUi;
				return SyncModeKind.SingleInputMode;
			}

			if (IsGamePadLegacySharedStorageKey(storageKey))
			{
				if (mod)
					return SyncModeKind.GamePadBoth;
				// Vanilla legacy GP: — Game only (stops Ensure from copying into Menu).
				singleInputModeOrdinal = ModeGamePad;
				return SyncModeKind.SingleInputMode;
			}

			if (IsKeyboardUiStorageKey(storageKey))
			{
				singleInputModeOrdinal = ModeKeyboardUi;
				return SyncModeKind.SingleInputMode;
			}

			// Bare keyboard / mouse storage
			if (mod)
				return SyncModeKind.KeyboardBoth;

			singleInputModeOrdinal = ModeKeyboard;
			return SyncModeKind.SingleInputMode;
		}

		public static bool ShouldCrossFallbackSeedOtherMode(int inputModeOrdinal, string actionId)
		{
			// Never seed Game from Menu or Menu from Game for vanilla Controls separation.
			// Open-time UI fallback stays off; Game→UI copy on save uses TryGetGameToUiMirrorStorageKey.
			_ = inputModeOrdinal;
			_ = actionId;
			return false;
		}

		/// <summary>
		/// One-way save mirror: Game Controls storage → Menu Controls storage.
		/// Keyboard bare vanilla → <c>KU:</c>; <c>GP:G:</c> → <c>GP:U:</c>.
		/// Menu keys, MOD keys, and legacy <c>GP:</c> do not mirror (no UI→Game).
		/// </summary>
		public static bool TryGetGameToUiMirrorStorageKey(string gameStorageKey, out string uiStorageKey)
		{
			uiStorageKey = null;
			if (string.IsNullOrEmpty(gameStorageKey))
				return false;

			if (IsGamePadGameStorageKey(gameStorageKey))
			{
				string padAction = ToTriggerActionId(gameStorageKey);
				if (string.IsNullOrEmpty(padAction) || IsModKeybindActionId(padAction))
					return false;
				uiStorageKey = GamePadUiPrefix + padAction;
				return true;
			}

			if (IsKeyboardUiStorageKey(gameStorageKey)
				|| IsGamePadUiStorageKey(gameStorageKey)
				|| IsGamePadLegacySharedStorageKey(gameStorageKey))
				return false;

			// Bare keyboard/mouse game storage only (no prefix other than identity).
			string bare = ToTriggerActionId(gameStorageKey);
			if (!string.Equals(bare, gameStorageKey, StringComparison.Ordinal))
				return false;
			if (IsModKeybindActionId(bare))
				return false;

			uiStorageKey = KeyboardUiStoragePrefix + bare;
			return true;
		}

		/// <summary>
		/// When <paramref name="gameStorageKey"/> is a Game→UI mirror source, returns the Menu InputMode ordinal.
		/// </summary>
		public static bool TryGetGameToUiMirrorInputModeOrdinal(string gameStorageKey, out int uiInputModeOrdinal)
		{
			uiInputModeOrdinal = -1;
			if (!TryGetGameToUiMirrorStorageKey(gameStorageKey, out string uiKey))
				return false;
			if (IsGamePadUiStorageKey(uiKey))
			{
				uiInputModeOrdinal = ModeGamePadUi;
				return true;
			}
			if (IsKeyboardUiStorageKey(uiKey))
			{
				uiInputModeOrdinal = ModeKeyboardUi;
				return true;
			}
			return false;
		}

		/// <summary>Legacy shared GamePad key for a bare trigger (e.g. Loadout2 → GP:Loadout2).</summary>
		public static string ToLegacyGamePadStorageKey(string triggerActionId)
		{
			if (string.IsNullOrEmpty(triggerActionId))
				return triggerActionId;
			if (IsGamePadLegacySharedStorageKey(triggerActionId)
				|| IsGamePadGameStorageKey(triggerActionId)
				|| IsGamePadUiStorageKey(triggerActionId))
				return triggerActionId;
			return GamePadLegacyPrefix + triggerActionId;
		}

		/// <summary>
		/// When GP:G: or GP:U: suppresses legacy for the same trigger, legacy GP:{action}
		/// must not evaluate. Pass <c>BindingStorage.HasLegacyEvaluateSuppressingSibling</c>
		/// (effective inputs, or ClearBindings evaluate-only mark). Empty Persist alone does not suppress.
		/// Does not delete Combos / bindings.json rows.
		/// </summary>
		public static bool ShouldSuppressLegacyGamePadEvaluate(
			string storageKey,
			System.Func<string, bool> hasEffectiveTwoKeySibling)
		{
			if (hasEffectiveTwoKeySibling == null || !IsGamePadLegacySharedStorageKey(storageKey))
				return false;

			string trigger = ToTriggerActionId(storageKey);
			if (string.IsNullOrEmpty(trigger))
				return false;

			return hasEffectiveTwoKeySibling(GamePadGamePrefix + trigger)
				|| hasEffectiveTwoKeySibling(GamePadUiPrefix + trigger);
		}
	}
}
