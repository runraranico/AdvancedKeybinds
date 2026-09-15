using System;
using System.Collections.Generic;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Aligns <c>ApplyOwnershipToTriggers</c> with Evaluate.
	/// GamePad storage (<c>GP:*</c>) never ownership-replaces shared Triggers — two-key OR
	/// fires via ApplyResults only; false replace killed MouseLeft ItemUse / Jump while Pad:X was down.
	/// Inventory / MenuInquiry keyboard Advanced entries also never false-replace.
	/// </summary>
	public static class BindingOwnershipTriggerPolicy
	{
		/// <summary>
		/// Shared UI back triggers that Terraria arms from Keyboard Escape / Pad Start.
		/// AK must never force these false via ownership replace.
		/// </summary>
		public static bool IsVanillaSharedUiBackTrigger(string triggerId)
		{
			if (string.IsNullOrEmpty(triggerId))
				return false;

			return string.Equals(triggerId, "Inventory", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(triggerId, "MenuInquiry", StringComparison.OrdinalIgnoreCase);
		}

		/// <summary>
		/// True when AK may replace vanilla Trigger edges with AK-only results for this storage key.
		/// Must match EvaluateAllCompiledBindings skip for <see cref="ControlsEditTargetPolicy.IsVanillaOwnedGamePadOrPair"/>.
		/// </summary>
		/// <param name="currentInputIsGamePad">
		/// When true (XBoxGamepad / XBoxGamepadUI), keyboard / KU advanced must not false-replace
		/// shared Triggers — LIVE: Pad:Back (Select) armed MapStyle / RecipeBrowser then
		/// Keyboard SC/VK Advanced wiped them every frame.
		/// </param>
		public static bool ShouldApplyOwnershipReplace(
			string storageKey,
			bool requiresAdvancedStorage,
			bool hold,
			IReadOnlyList<string> keys,
			string alternateGamePadToken,
			bool currentInputIsGamePad = false)
		{
			if (!requiresAdvancedStorage)
				return false;

			if (string.IsNullOrEmpty(storageKey))
				return false;

			// GP:* maps onto shared trigger ids (MouseLeft / Jump / …). Ownership false-replace
			// after ApplyResults wiped pad OR edges and physical mouse / Space.
			if (ControlsEditTargetPolicy.IsGamePadStorageKey(storageKey))
				return false;

			// Device separation: Keyboard Advanced owns keyboard edges only.
			if (currentInputIsGamePad)
				return false;

			string triggerId = ControlsEditTargetPolicy.ToTriggerActionId(storageKey);
			if (IsVanillaSharedUiBackTrigger(triggerId))
				return false;

			// Terraria KeyStatus OR owns GamePadTwoKeyTerrariaOrPolicy.ActionIds (sole allowlist).
			// Never false-replace Triggers vanilla already armed (LIVE: Jump=1→ApplyOwnership→0).
			// Must strip KU: / GP:G: / GP:U: / GP: to bare ActionId (TwoKey.ToTriggerActionId leaves
			// KU:Jump as KU:Jump via ControlsEditTargetPolicy — allowlist miss wiped Jump).
			if (GamePadTwoKeyTerrariaOrPolicy.IsAllowlistedActionId(
				    ControlsGameUiSeparationPolicy.ToTriggerActionId(storageKey)))
				return false;

			_ = hold;
			_ = keys;
			_ = alternateGamePadToken;
			return true;
		}

		/// <summary>
		/// Mirrors product ApplyOwnershipToTriggers against plain KeyStatus dictionaries
		/// (Terraria TriggersSet.KeyStatus shape) for Core.Tests without Terraria references.
		/// </summary>
		public static void ApplyOwnershipToKeyStatus(
			IReadOnlyList<OwnershipCompiledEntry> compiled,
			IDictionary<string, bool> justPressed,
			IDictionary<string, bool> current,
			IDictionary<string, bool> justReleased,
			Func<string, bool> akWasJustPressed,
			Func<string, bool> akIsCurrent,
			Func<string, bool> akWasJustReleased,
			bool currentInputIsGamePad = false)
		{
			if (compiled == null)
				return;

			for (int i = 0; i < compiled.Count; i++)
			{
				OwnershipCompiledEntry cb = compiled[i];
				if (cb == null)
					continue;

				if (!ShouldApplyOwnershipReplace(
					    cb.StorageKey,
					    cb.RequiresAdvancedStorage,
					    cb.Hold,
					    cb.Keys,
					    cb.AlternateGamePadToken,
					    currentInputIsGamePad))
					continue;

				string triggerId = ControlsGameUiSeparationPolicy.ToTriggerActionId(cb.StorageKey);
				bool akPressed = akWasJustPressed != null
					&& (akWasJustPressed(triggerId) || akWasJustPressed(cb.StorageKey));
				bool akCurrent = akIsCurrent != null
					&& (akIsCurrent(triggerId) || akIsCurrent(cb.StorageKey));
				bool akReleased = akWasJustReleased != null
					&& (akWasJustReleased(triggerId) || akWasJustReleased(cb.StorageKey));

				if (justPressed != null && justPressed.ContainsKey(triggerId))
					justPressed[triggerId] = akPressed;

				if (current != null && current.ContainsKey(triggerId))
					current[triggerId] = akCurrent;

				if (justReleased != null && justReleased.ContainsKey(triggerId))
					justReleased[triggerId] = akReleased;
			}
		}

		public sealed class OwnershipCompiledEntry
		{
			public string StorageKey;
			public bool RequiresAdvancedStorage;
			public bool Hold;
			public IReadOnlyList<string> Keys;
			public string AlternateGamePadToken;
		}
	}
}
