using System;
using System.Collections.Generic;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Reconcile AK PersistAsEmpty with Terraria <c>PlayerInput.FixDerpedRebinds</c> restore
	/// for exactly four GamePad Use/Interact records:
	/// (Gamepad, GAME|UI, MouseLeft|MouseRight).
	/// Never invents defaults — only converts the current profile's KeyStatus after Terraria restore.
	/// </summary>
	public static class GamePadTwoKeyMandatoryReconcilePolicy
	{
		public const string ActionMouseLeft = "MouseLeft";
		public const string ActionMouseRight = "MouseRight";

		/// <summary>
		/// Exact allowlist: GamePad InputMode + MouseLeft/MouseRight only (4 triples).
		/// </summary>
		public static bool IsUseInteractMandatoryTarget(int inputModeOrdinal, string actionId)
		{
			if (string.IsNullOrEmpty(actionId))
				return false;
			if (!string.Equals(actionId, ActionMouseLeft, StringComparison.Ordinal)
				&& !string.Equals(actionId, ActionMouseRight, StringComparison.Ordinal))
				return false;
			if (!GamePadTwoKeyTerrariaOrPolicy.TryMapInputModeOrdinal(inputModeOrdinal, out string mode))
				return false;
			return GamePadTwoKeyTerrariaOrPolicy.IsExactAllowlisted(
				GamePadTwoKeyTerrariaOrPolicy.DeviceGamepad,
				mode,
				actionId);
		}

		/// <summary>
		/// True when AK has an empty Persist record for a mandatory target and KeyStatus
		/// already holds 1–2 convertible pad entries (Terraria restore completed).
		/// </summary>
		public static bool ShouldReconcileEmptyAkFromKeyStatus(
			int inputModeOrdinal,
			string actionId,
			bool hasAkRecord,
			string slot1,
			string slot2,
			IReadOnlyList<string> keyStatusEntries)
		{
			if (!hasAkRecord)
				return false;
			if (!IsUseInteractMandatoryTarget(inputModeOrdinal, actionId))
				return false;
			if (!string.IsNullOrEmpty(slot1) || !string.IsNullOrEmpty(slot2))
				return false;
			return TryBuildSlotsFromKeyStatus(keyStatusEntries, out _, out _);
		}

		/// <summary>
		/// Builds Slot1/Slot2 from Terraria KeyStatus order. Supports 1 or 2 convertible entries.
		/// More than 2 convertible entries → false (do not discard or reorder).
		/// </summary>
		public static bool TryBuildSlotsFromKeyStatus(
			IReadOnlyList<string> rawEntries,
			out string slot1PadToken,
			out string slot2PadToken)
		{
			slot1PadToken = null;
			slot2PadToken = null;
			if (rawEntries == null || rawEntries.Count == 0)
				return false;

			string first = null;
			string second = null;
			int convertible = 0;

			for (int i = 0; i < rawEntries.Count; i++)
			{
				string raw = rawEntries[i];
				if (string.IsNullOrWhiteSpace(raw))
					continue;
				raw = raw.Trim();
				if (raw.StartsWith("AKAdv:", StringComparison.Ordinal))
					raw = raw.Substring(6);

				string pad;
				if (!GamePadTwoKeyTerrariaOrPolicy.TryKeyStatusButtonsNameToPadToken(raw, out pad))
				{
					if (!GamePadToken.TryCanonicalize(raw, out pad) || !GamePadToken.IsPadToken(pad))
						continue;
				}

				convertible++;
				if (convertible == 1)
					first = pad;
				else if (convertible == 2)
					second = pad;
				else
				{
					// >2: unsafe for TwoKey panel — refuse rather than drop keys.
					slot1PadToken = null;
					slot2PadToken = null;
					return false;
				}
			}

			if (convertible == 0 || first == null)
				return false;

			slot1PadToken = first;
			slot2PadToken = second;
			return true;
		}
	}
}
