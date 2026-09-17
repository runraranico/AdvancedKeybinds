using System;
using System.Collections.Generic;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Keyboard/Mouse "Additional Key (OR)" for a fixed allowlist of vanilla actions.
	/// Independent of AND combo slots and of controller <c>AlternateGamePadToken</c>.
	/// Pure logic; no Terraria / file I/O.
	/// </summary>
	public static class KeyboardAdditionalOrPolicy
	{
		public const string DisplaySeparator = " / ";

		/// <summary>Live tModLoader InputMode ordinals.</summary>
		public const int InputModeKeyboard = 0;
		public const int InputModeKeyboardUI = 1;

		/// <summary>
		/// User-specified 9 Controls rows (Keyboard sword / list icons):
		/// Hook, Auto Select, Smart Cursor, Quick Mount, Quick Heal, Quick Mana,
		/// Quick Buff, Throw, Inventory.
		/// </summary>
		public static readonly string[] AllowlistedActionIds = {
			"Grapple",
			"SmartSelect",
			"SmartCursor",
			"QuickMount",
			"QuickHeal",
			"QuickMana",
			"QuickBuff",
			"Throw",
			"Inventory",
		};

		private static readonly string[] AllowlistedSectionSemantics = {
			"Movement",
			"Inventory",
		};

		public enum RejectReason
		{
			None = 0,
			NotEligible,
			EmptyToken,
			InvalidToken,
			PadToken,
			DuplicateAndSlot,
			F11,
			DisplayAlias
		}

		public static bool HasToken(string additionalOrToken)
			=> !string.IsNullOrEmpty(additionalOrToken);

		public static bool IsAllowlistedAction(string actionId)
		{
			if (string.IsNullOrEmpty(actionId))
				return false;

			for (int i = 0; i < AllowlistedActionIds.Length; i++)
			{
				if (string.Equals(actionId, AllowlistedActionIds[i], StringComparison.OrdinalIgnoreCase))
					return true;
			}

			return false;
		}

		public static bool IsAllowlistedSection(string sectionSemantic)
		{
			if (string.IsNullOrEmpty(sectionSemantic))
				return false;

			for (int i = 0; i < AllowlistedSectionSemantics.Length; i++)
			{
				if (string.Equals(
					sectionSemantic,
					AllowlistedSectionSemantics[i],
					StringComparison.OrdinalIgnoreCase))
					return true;
			}

			return false;
		}

		/// <summary>
		/// ComboPanel AND/OR tabs: Keyboard or KeyboardUI edit, non-mod, allowlisted action+section.
		/// </summary>
		public static bool IsPanelEligible(
			bool editDeviceIsGamePad,
			int inputModeOrdinal,
			string sectionSemantic,
			bool isModKeybind,
			string actionId)
		{
			if (editDeviceIsGamePad || isModKeybind)
				return false;

			if (inputModeOrdinal != InputModeKeyboard && inputModeOrdinal != InputModeKeyboardUI)
				return false;

			if (!IsAllowlistedSection(sectionSemantic))
				return false;

			return IsAllowlistedAction(actionId);
		}

		/// <summary>
		/// Canonicalize a keyboard/mouse/SC/VK additional OR token.
		/// Rejects Pad tokens and any token that matches an AND slot (token or physical identity).
		/// </summary>
		public static bool TryNormalizeToken(
			string candidate,
			IReadOnlyList<string> andSlots,
			out string canonical,
			out RejectReason reject)
		{
			canonical = null;
			reject = RejectReason.None;

			if (string.IsNullOrWhiteSpace(candidate))
			{
				reject = RejectReason.EmptyToken;
				return false;
			}

			if (StableKeyToken.IsDisplayAlias(candidate))
			{
				reject = RejectReason.DisplayAlias;
				return false;
			}

			canonical = StableKeyToken.CanonicalizeOrNull(candidate.Trim());
			if (canonical == null)
			{
				reject = RejectReason.InvalidToken;
				return false;
			}

			if (GamePadToken.IsPadToken(canonical))
			{
				canonical = null;
				reject = RejectReason.PadToken;
				return false;
			}

			if (string.Equals(canonical, "F11", StringComparison.OrdinalIgnoreCase))
			{
				canonical = null;
				reject = RejectReason.F11;
				return false;
			}

			if (ConflictsWithAndSlots(canonical, andSlots))
			{
				canonical = null;
				reject = RejectReason.DuplicateAndSlot;
				return false;
			}

			return true;
		}

		public static bool ConflictsWithAndSlots(string canonicalOrToken, IReadOnlyList<string> andSlots)
		{
			if (string.IsNullOrEmpty(canonicalOrToken) || andSlots == null)
				return false;

			for (int i = 0; i < andSlots.Count; i++)
			{
				string existing = andSlots[i];
				if (string.IsNullOrEmpty(existing))
					continue;

				if (StableKeyToken.TokensEqual(existing, canonicalOrToken))
					return true;
				if (PhysicalKeyIdentity.AreSamePhysical(existing, canonicalOrToken))
					return true;
			}

			return false;
		}

		/// <summary>
		/// Inventory / MenuInquiry: AdditionalOr must not replace a plain Keys-name AND key
		/// (especially Escape) with AKAdv markers — that kills vanilla ESC close.
		/// Keep the AND key in KeyStatus; AK evaluates only the AdditionalOr edge.
		/// Hold on the AND key is ignored for this ownership decision: AdditionalOr on shared
		/// UI-back triggers forces a vanilla OR KeyStatus list (Escape, Tab) so menus still close.
		/// Escape-equivalent measured tokens (VK:0x1B / SC:0x01:*) count as preservable AND —
		/// ComboPanel Slot3 capture often saves VK:0x1B while the UI still shows "Escape".
		/// </summary>
		public static bool ShouldPreserveVanillaAndKeyWithAdditionalOr(
			string storageOrTriggerId,
			bool hold,
			IReadOnlyList<string> andSlots,
			string additionalOrToken)
		{
			if (!HasToken(additionalOrToken))
				return false;

			if (andSlots == null || andSlots.Count != 1)
				return false;

			string triggerId = ControlsGameUiSeparationPolicy.ToTriggerActionId(storageOrTriggerId);
			if (!BindingOwnershipTriggerPolicy.IsVanillaSharedUiBackTrigger(triggerId))
				return false;

			// Hold must not veto Escape/Tab KeyStatus — ComboPanel AND tab can leave Hold lit
			// while AdditionalOr is saved (UI locks Hold only on the OR tab).
			_ = hold;
			return TryResolveSharedUiBackVanillaAndKey(andSlots[0], out _);
		}

		/// <summary>
		/// Shared UI-back (Inventory / MenuInquiry) + AdditionalOr: Hold is unsupported.
		/// Clear so sync never writes AKAdv markers and ESC menu leave stays alive.
		/// </summary>
		public static bool ShouldClearHoldForSharedUiBackAdditionalOr(
			string storageOrTriggerId,
			bool hold,
			string additionalOrToken)
		{
			if (!hold || !HasToken(additionalOrToken))
				return false;

			string triggerId = ControlsGameUiSeparationPolicy.ToTriggerActionId(storageOrTriggerId);
			return BindingOwnershipTriggerPolicy.IsVanillaSharedUiBackTrigger(triggerId);
		}

		/// <summary>
		/// Normalize Inventory/MenuInquiry + AdditionalOr: drop Hold flags and rewrite
		/// Escape/Tab-equivalent measured tokens to Keys names before KeyStatus sync.
		/// </summary>
		public static void NormalizeSharedUiBackAdditionalOr(
			string storageOrTriggerId,
			ComboBindingData combo)
		{
			if (combo == null || !HasToken(combo.AdditionalOrToken))
				return;

			string triggerId = ControlsGameUiSeparationPolicy.ToTriggerActionId(storageOrTriggerId);
			if (!BindingOwnershipTriggerPolicy.IsVanillaSharedUiBackTrigger(triggerId))
				return;

			if (ShouldClearHoldForSharedUiBackAdditionalOr(
				storageOrTriggerId, combo.Hold, combo.AdditionalOrToken))
			{
				combo.Hold = false;
				combo.HoldOnRelease = false;
			}

			RewriteSoleAndSlotToVanillaEscapeIfNeeded(combo);
			if (PlainSingleTapPolicy.IsTabEquivalentToken(combo.AdditionalOrToken)
				&& TryResolveSharedUiBackVanillaOrKey(combo.AdditionalOrToken, out string orVanilla))
				combo.AdditionalOrToken = orVanilla;
		}

		/// <summary>
		/// When AND + AdditionalOr are both plain Keys names (or Escape/Tab equivalents) on
		/// Inventory/MenuInquiry, vanilla KeyStatus is an OR list (Escape, Tab).
		/// Keyboard and KeyboardUI both keep Escape — title Back uses JustPressed.Inventory
		/// (UILinksInitializer → BackButtonInUse). Omitting Escape from KeyboardUI kills player-select ESC.
		/// <paramref name="inputModeOrdinal"/> retained for call-site compatibility; both modes use the same list.
		/// </summary>
		public static bool TryBuildVanillaOrKeyStatusEntries(
			string storageOrTriggerId,
			bool hold,
			IReadOnlyList<string> andSlots,
			string additionalOrToken,
			List<string> destination,
			int inputModeOrdinal = InputModeKeyboard)
		{
			_ = inputModeOrdinal;
			if (destination == null)
				return false;

			destination.Clear();
			if (!ShouldPreserveVanillaAndKeyWithAdditionalOr(
				    storageOrTriggerId, hold, andSlots, additionalOrToken))
				return false;

			if (!TryResolveSharedUiBackVanillaAndKey(andSlots[0], out string andVanilla))
				return false;

			destination.Add(andVanilla);
			if (TryResolveSharedUiBackVanillaOrKey(additionalOrToken, out string orVanilla))
				destination.Add(orVanilla);
			return destination.Count > 0;
		}

		/// <summary>
		/// True when vanilla KeyStatus OR already owns both AND and AdditionalOr
		/// (plain Keys AdditionalOr or Tab-equivalent) — menus use KeyStatus Escape/Tab.
		/// Production: Escape AND may AK-mirror once (SharedUi latch); world REARM disabled.
		/// AK mirrors Tab OR rising.
		/// </summary>
		public static bool VanillaKeyStatusOwnsAdditionalOr(
			string storageOrTriggerId,
			bool hold,
			IReadOnlyList<string> andSlots,
			string additionalOrToken)
		{
			if (!ShouldPreserveVanillaAndKeyWithAdditionalOr(
				    storageOrTriggerId, hold, andSlots, additionalOrToken))
				return false;
			return TryResolveSharedUiBackVanillaOrKey(additionalOrToken, out _);
		}

		/// <summary>
		/// When <see cref="VanillaKeyStatusOwnsAdditionalOr"/> is true, AK must mirror-fire
		/// Inventory edges for the AdditionalOr key (e.g. Tab) without SuppressedComboTokens.
		/// Escape AND: latch-guarded AK mirror (world REARM disabled). Tab OR still mirrored here.
		/// </summary>
		public static bool ShouldAkMirrorFireSharedUiBackVanillaOr(
			string storageOrTriggerId,
			bool hold,
			IReadOnlyList<string> andSlots,
			string additionalOrToken)
			=> VanillaKeyStatusOwnsAdditionalOr(
				storageOrTriggerId, hold, andSlots, additionalOrToken);

		/// <summary>
		/// Sole AND → vanilla Keys name for shared UI-back KeyStatus.
		/// Accepts plain Keys names and Escape-equivalent measured tokens.
		/// </summary>
		public static bool TryResolveSharedUiBackVanillaAndKey(string andToken, out string vanillaKeysName)
		{
			vanillaKeysName = null;
			if (string.IsNullOrEmpty(andToken))
				return false;

			if (PlainSingleTapPolicy.IsEscapeEquivalentToken(andToken))
			{
				vanillaKeysName = "Escape";
				return true;
			}

			if (PlainSingleTapPolicy.IsPlainSingleTapSole(hold: false, andToken))
			{
				vanillaKeysName = andToken;
				return true;
			}

			return false;
		}

		/// <summary>
		/// AdditionalOr → vanilla Keys name when it can join Escape in KeyStatus OR.
		/// Tab-equivalent measured tokens become "Tab"; other plain Keys names stay as-is.
		/// Exotic non-plain OR returns false (AK evaluates the OR edge; Escape stays vanilla).
		/// </summary>
		public static bool TryResolveSharedUiBackVanillaOrKey(string additionalOrToken, out string vanillaKeysName)
		{
			vanillaKeysName = null;
			if (string.IsNullOrEmpty(additionalOrToken))
				return false;

			if (PlainSingleTapPolicy.IsTabEquivalentToken(additionalOrToken))
			{
				vanillaKeysName = "Tab";
				return true;
			}

			if (PlainSingleTapPolicy.IsPlainSingleTapSole(hold: false, additionalOrToken))
			{
				vanillaKeysName = additionalOrToken;
				return true;
			}

			return false;
		}

		private static void RewriteSoleAndSlotToVanillaEscapeIfNeeded(ComboBindingData combo)
		{
			int filled = 0;
			int soleIndex = 0;
			string sole = null;
			if (!string.IsNullOrEmpty(combo.Slot1)) { filled++; soleIndex = 1; sole = combo.Slot1; }
			if (!string.IsNullOrEmpty(combo.Slot2)) { filled++; soleIndex = 2; sole = combo.Slot2; }
			if (!string.IsNullOrEmpty(combo.Slot3)) { filled++; soleIndex = 3; sole = combo.Slot3; }
			if (filled != 1 || !PlainSingleTapPolicy.IsEscapeEquivalentToken(sole))
				return;
			if (string.Equals(sole, "Escape", StringComparison.OrdinalIgnoreCase))
				return;

			switch (soleIndex)
			{
				case 1: combo.Slot1 = "Escape"; break;
				case 2: combo.Slot2 = "Escape"; break;
				case 3: combo.Slot3 = "Escape"; break;
			}
		}

		/// <summary>Controls / overlay: AND label then additional OR, slash-separated.</summary>
		public static string FormatAndWithAdditionalOrDisplay(string andDisplay, string orDisplay)
		{
			if (string.IsNullOrEmpty(orDisplay))
				return andDisplay ?? string.Empty;
			if (string.IsNullOrEmpty(andDisplay))
				return orDisplay;
			return andDisplay + DisplaySeparator + orDisplay;
		}

		public static bool RequiresAdvancedOwnership(string additionalOrToken)
			=> HasToken(additionalOrToken);
	}
}
