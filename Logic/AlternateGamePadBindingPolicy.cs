using System;
using System.Collections.Generic;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Controller-only alternate (OR) GamePad binding — not AND combo.
	/// Pure logic; no Terraria / file I/O.
	/// </summary>
	public static class AlternateGamePadBindingPolicy
	{
		public const string DisplaySeparator = " / ";

		/// <summary>Live tModLoader InputMode ordinals (tModLoader.dll).</summary>
		public const int InputModeXBoxGamepad = 3;
		public const int InputModeXBoxGamepadUi = 4;

		public enum RejectReason
		{
			None = 0,
			NotEligible,
			NotPadToken,
			DuplicatePrimary,
			EmptyToken,
			SubsystemInactive,
			AndComboSlots
		}

		/// <summary>
		/// Exact two-key allowlist (controller Game / Menu rows). Display names → ids from
		/// UIManageControls / PlayerInput: アイテムの使用=MouseLeft, ミドルクリック=MouseMiddle,
		/// マウス5=MouseXButton2, 下=Down, 右=Right, フック=Grapple, スマートカーソル=SmartCursor,
		/// 対話・動作=MouseRight, マウス4=MouseXButton1, 上=Up, 左=Left, ジャンプ=Jump,
		/// オートセレクト=SmartSelect, クイックマウント=QuickMount.
		/// </summary>
		public static readonly string[] TwoKeyAllowlistedActionIds = {
			"MouseLeft",
			"MouseMiddle",
			"MouseXButton2",
			"Down",
			"Right",
			"Grapple",
			"SmartCursor",
			"MouseRight",
			"MouseXButton1",
			"Up",
			"Left",
			"Jump",
			"SmartSelect",
			"QuickMount",
		};

		/// <summary>
		/// Sections where the allowlisted actions are opened from vanilla Controls rows.
		/// Positive allowlist only — null / empty / unknown / Map / Hotbar / Gamepad / Mod → false.
		/// </summary>
		private static readonly string[] TwoKeyAllowlistedSectionSemantics = {
			"Movement",
			"Inventory",
		};

		/// <summary>
		/// Two-key UI only for the explicit 14 GamePad action ids (see
		/// <see cref="TwoKeyAllowlistedActionIds"/>), on GamePad edit device, InputMode 3/4,
		/// known Movement/Inventory section, non-mod. Registration count is not used.
		/// </summary>
		public static bool IsTwoKeySetupEligible(
			bool editDeviceIsGamePad,
			int inputModeOrdinal,
			string sectionSemantic,
			bool isModKeybind,
			string actionId = null)
		{
			if (!editDeviceIsGamePad || isModKeybind)
				return false;

			if (inputModeOrdinal != InputModeXBoxGamepad
				&& inputModeOrdinal != InputModeXBoxGamepadUi)
				return false;

			if (!IsTwoKeyAllowlistedSection(sectionSemantic))
				return false;

			return IsTwoKeyAllowlistedAction(actionId);
		}

		/// <summary>
		/// Master audit catalog of vanilla GamePad actions (superset). Collect filters via
		/// <see cref="IsTwoKeySetupEligible"/>.
		/// </summary>
		public static readonly string[] TwoKeyAuditCandidateActionIds = {
			"Up", "Down", "Left", "Right",
			"Jump", "Grapple", "QuickMount", "QuickHeal", "QuickMana", "QuickBuff",
			"Throw", "Inventory", "SmartCursor", "LockOn",
			"HotbarMinus", "HotbarPlus", "Hotbar1", "Hotbar2", "Hotbar3", "Hotbar4",
			"Hotbar5", "Hotbar6", "Hotbar7", "Hotbar8", "Hotbar9", "Hotbar10",
			"MouseLeft", "MouseRight", "MouseMiddle", "MouseXButton1", "MouseXButton2",
			"SmartSelect",
			"DpadRadial1", "DpadRadial2", "DpadRadial3", "DpadRadial4",
			"RadialHotbar", "RadialQuickbar",
			"Loadout1", "Loadout2", "Loadout3", "ToggleCameraMode",
		};

		/// <summary>True when action id is not one of the 14 allowlisted two-key actions.</summary>
		public static bool IsExcludedAction(string actionId)
		{
			if (string.IsNullOrEmpty(actionId))
				return false;
			return !IsTwoKeyAllowlistedAction(actionId);
		}

		public static bool IsTwoKeyAllowlistedAction(string actionId)
		{
			if (string.IsNullOrEmpty(actionId))
				return false;

			if (actionId.StartsWith("GP:", StringComparison.Ordinal))
				actionId = actionId.Substring(3);

			for (int i = 0; i < TwoKeyAllowlistedActionIds.Length; i++)
			{
				if (string.Equals(actionId, TwoKeyAllowlistedActionIds[i], StringComparison.OrdinalIgnoreCase))
					return true;
			}

			return false;
		}

		public static bool IsTwoKeyAllowlistedSection(string sectionSemantic)
		{
			if (string.IsNullOrEmpty(sectionSemantic))
				return false;

			for (int i = 0; i < TwoKeyAllowlistedSectionSemantics.Length; i++)
			{
				if (string.Equals(
					sectionSemantic,
					TwoKeyAllowlistedSectionSemantics[i],
					StringComparison.OrdinalIgnoreCase))
					return true;
			}

			return false;
		}

		/// <summary>
		/// Action ids from <see cref="TwoKeyAuditCandidateActionIds"/> that are two-key eligible
		/// for the given GamePad edit context (same gate as ComboPanel open).
		/// </summary>
		public static int CollectTwoKeyEligibleFromAuditCatalog(
			bool editDeviceIsGamePad,
			int inputModeOrdinal,
			string sectionSemantic,
			bool isModKeybind,
			string[] destination,
			int destinationOffset)
		{
			if (destination == null || destinationOffset < 0)
				return 0;

			int written = 0;
			for (int i = 0; i < TwoKeyAuditCandidateActionIds.Length; i++)
			{
				string id = TwoKeyAuditCandidateActionIds[i];
				if (!IsTwoKeySetupEligible(
					editDeviceIsGamePad, inputModeOrdinal, sectionSemantic, isModKeybind, id))
					continue;
				int idx = destinationOffset + written;
				if (idx >= destination.Length)
					break;
				destination[idx] = id;
				written++;
			}

			return written;
		}

		/// <summary>
		/// Non-HOLD + convertible primary alone → vanilla KeyStatus.
		/// Any Alternate present → Advanced engine owns OR (reliable primary+alternate Trigger fire).
		/// </summary>
		public static bool RequiresAdvancedOwnershipForAlternate(
			bool hold,
			string alternateToken,
			int andFilledSlotCount)
		{
			if (!HasAlternate(alternateToken))
				return false;
			_ = hold;
			_ = andFilledSlotCount;
			// Always Advanced when alternate is set — vanilla KeyStatus OR alone was insufficient
			// for Jump (display/save OK, alternate did not fire).
			return true;
		}

		/// <summary>
		/// Maps a storage key (possibly GP:Jump) and OR edge result onto trigger write intent.
		/// Pure helper for tests and ApplyResults path contracts.
		/// </summary>
		public static void ResolveTriggerFireIntent(
			string storageActionId,
			bool orHeld,
			bool orJustPressed,
			out string triggerActionId,
			out bool writeJustPressed,
			out bool writeCurrent)
		{
			triggerActionId = storageActionId;
			if (!string.IsNullOrEmpty(storageActionId)
				&& storageActionId.StartsWith("GP:", StringComparison.Ordinal))
				triggerActionId = storageActionId.Substring(3);

			writeJustPressed = orJustPressed;
			writeCurrent = orHeld;
		}

		public static bool IsExcludedSection(string sectionSemantic)
		{
			if (string.IsNullOrEmpty(sectionSemantic))
				return false;

			return string.Equals(sectionSemantic, "Map", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(sectionSemantic, "Hotbar", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(sectionSemantic, "Gamepad", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(sectionSemantic, "GamepadExtended", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(sectionSemantic, "Mod", StringComparison.OrdinalIgnoreCase);
		}

		public static bool HasAlternate(string alternateToken)
			=> !string.IsNullOrEmpty(alternateToken);

		/// <summary>
		/// Accept only canonical Pad:* tokens distinct from the primary (first filled AND slot).
		/// </summary>
		public static bool TryNormalizeAlternateToken(
			string candidate,
			string primaryToken,
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

			if (!GamePadToken.TryCanonicalize(candidate.Trim(), out canonical)
				|| !GamePadToken.IsPadToken(canonical))
			{
				canonical = null;
				reject = RejectReason.NotPadToken;
				return false;
			}

			if (!string.IsNullOrEmpty(primaryToken)
				&& GamePadToken.TryCanonicalize(primaryToken, out string primaryCanon)
				&& string.Equals(canonical, primaryCanon, StringComparison.OrdinalIgnoreCase))
			{
				canonical = null;
				reject = RejectReason.DuplicatePrimary;
				return false;
			}

			return true;
		}

		/// <summary>Primary for OR display / compare = sole filled AND slot when count==1.</summary>
		public static string ResolvePrimaryToken(IReadOnlyList<string> andSlots)
		{
			if (andSlots == null || andSlots.Count != 1)
				return null;
			return andSlots[0];
		}

		public static bool AllowsHoldWithAlternate(int andFilledSlotCount, string alternateToken)
			=> andFilledSlotCount == 1;

		public static bool IsAndComboBlockingAlternate(int andFilledSlotCount)
			=> andFilledSlotCount > 1;

		/// <summary>Display order: alternate left, primary right.</summary>
		public static string FormatAlternateOverPrimaryDisplay(
			string alternateDisplay,
			string primaryDisplay)
		{
			if (string.IsNullOrEmpty(alternateDisplay))
				return primaryDisplay ?? string.Empty;
			if (string.IsNullOrEmpty(primaryDisplay))
				return alternateDisplay;
			return alternateDisplay + DisplaySeparator + primaryDisplay;
		}

		/// <summary>
		/// Logical OR edge merge for primary + alternate physical downs.
		/// JustPressed only on rising OR; JustReleased only when both released.
		/// </summary>
		public readonly struct OrEdgeResult
		{
			public readonly bool Held;
			public readonly bool JustPressed;
			public readonly bool JustReleased;

			public OrEdgeResult(bool held, bool justPressed, bool justReleased)
			{
				Held = held;
				JustPressed = justPressed;
				JustReleased = justReleased;
			}
		}

		public static OrEdgeResult EvaluateOrEdges(
			bool primaryDown,
			bool alternateDown,
			bool previousOrHeld)
		{
			bool held = primaryDown || alternateDown;
			bool justPressed = held && !previousOrHeld;
			bool justReleased = !held && previousOrHeld;
			return new OrEdgeResult(held, justPressed, justReleased);
		}

		/// <summary>
		/// HOLD progress must not reset when switching which physical key keeps OR held.
		/// </summary>
		public static bool ShouldResetHoldTimer(
			bool previousOrHeld,
			bool currentOrHeld,
			bool primaryDown,
			bool alternateDown)
		{
			if (!previousOrHeld || !currentOrHeld)
				return !currentOrHeld && previousOrHeld;
			return false;
		}

		public static bool ShouldEvaluateRuntime(
			bool gamePadSubsystemActive,
			string alternateToken)
			=> gamePadSubsystemActive && HasAlternate(alternateToken);

		public static void ClearSessionState(ref bool orHeldLatch)
			=> orHeldLatch = false;

		/// <summary>
		/// Builds two-key OR seed tokens from a vanilla KeyStatus entry list (same region the list shows).
		/// Does not write files — open-path alignment only. Accepts Buttons names, Pad:*, or AKAdv: markers.
		/// </summary>
		public static bool TrySeedTwoKeyFromVanillaKeyStatusEntries(
			IReadOnlyList<string> rawEntries,
			out string primaryPadToken,
			out string alternatePadToken)
		{
			primaryPadToken = null;
			alternatePadToken = null;
			if (rawEntries == null || rawEntries.Count != 2)
				return false;

			if (!TryEntryToPadToken(rawEntries[0], out string primary)
				|| !TryEntryToPadToken(rawEntries[1], out string alternate))
				return false;

			if (string.Equals(primary, alternate, StringComparison.OrdinalIgnoreCase))
				return false;

			primaryPadToken = primary;
			alternatePadToken = alternate;
			return true;
		}

		private static bool TryEntryToPadToken(string raw, out string padToken)
		{
			padToken = null;
			if (string.IsNullOrWhiteSpace(raw))
				return false;

			raw = raw.Trim();
			if (raw.StartsWith("AKAdv:", StringComparison.Ordinal))
				raw = raw.Substring(6);

			if (ControlsEditTargetPolicy.TryVanillaButtonsNameToPadToken(raw, out padToken))
				return true;
			if (GamePadToken.TryCanonicalize(raw, out padToken) && GamePadToken.IsPadToken(padToken))
				return true;

			padToken = null;
			return false;
		}
	}
}
