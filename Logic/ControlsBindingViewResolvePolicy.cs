using System;
using System.Collections.Generic;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Single source of truth for Controls row label vs ComboPanel contents.
	/// Never merges Keyboard + KeyboardUI. For keyboard devices, applies the same
	/// product write contract as middle-click restore (Mouse1 / WASD — not Space / arrows).
	/// </summary>
	public static class ControlsBindingViewResolvePolicy
	{
		public enum SourceKind
		{
			Unset = 0,
			AkRecord = 1,
			TerrariaKeyStatus = 2
		}

		/// <summary>
		/// Canonical storage tokens for one InputMode row (outer + panel must both use this).
		/// AK non-empty record wins; otherwise that mode's KeyStatus list, then keyboard-device contract.
		/// </summary>
		public static List<string> ResolveCanonicalStorageTokens(
			bool hasAkRecord,
			IReadOnlyList<string> akKeys,
			IReadOnlyList<string> keyStatusForMode,
			int inputModeOrdinal = -1,
			string actionId = null)
		{
			var result = new List<string>();
			if (hasAkRecord && akKeys != null && akKeys.Count > 0)
			{
				AppendUnique(result, akKeys);
				return result;
			}

			if (keyStatusForMode != null)
				AppendUnique(result, keyStatusForMode);

			if (inputModeOrdinal >= 0 && !string.IsNullOrEmpty(actionId))
			{
				return OfficialDefaultKeyRestoreLogic.ApplyKeyboardGameMovementWriteContract(
					inputModeOrdinal,
					actionId,
					result);
			}

			return result;
		}

		/// <summary>
		/// Legacy broken seed: first KeyStatus token only (reproduces LeftClick vs LeftClick/Space).
		/// </summary>
		public static List<string> LegacyFirstKeyStatusSeedOnly(IReadOnlyList<string> keyStatusForMode)
		{
			var result = new List<string>();
			if (keyStatusForMode == null || keyStatusForMode.Count == 0)
				return result;
			if (!string.IsNullOrEmpty(keyStatusForMode[0]))
				result.Add(keyStatusForMode[0]);
			return result;
		}

		/// <summary>
		/// How ComboPanel should open when there is no AK record.
		/// Single key → Slot3 plain seed. Multiple → Terraria OR view (all tokens, not AND combo).
		/// </summary>
		public static void ResolveComboOpenFromKeyStatus(
			IReadOnlyList<string> keyStatusForMode,
			out SourceKind source,
			out List<string> canonicalTokens,
			out bool seedPlainSlot3,
			out string plainSlot3Token,
			int inputModeOrdinal = -1,
			string actionId = null)
		{
			canonicalTokens = ResolveCanonicalStorageTokens(
				hasAkRecord: false,
				akKeys: null,
				keyStatusForMode: keyStatusForMode,
				inputModeOrdinal: inputModeOrdinal,
				actionId: actionId);
			plainSlot3Token = null;
			seedPlainSlot3 = false;

			if (canonicalTokens.Count == 0)
			{
				source = SourceKind.Unset;
				return;
			}

			source = SourceKind.TerrariaKeyStatus;
			if (canonicalTokens.Count == 1)
			{
				seedPlainSlot3 = true;
				plainSlot3Token = canonicalTokens[0];
			}
		}

		public static bool AreSameTokenLists(IReadOnlyList<string> a, IReadOnlyList<string> b)
		{
			if (a == null && b == null)
				return true;
			if (a == null || b == null)
				return false;
			if (a.Count != b.Count)
				return false;
			for (int i = 0; i < a.Count; i++)
			{
				if (!string.Equals(a[i], b[i], StringComparison.Ordinal))
					return false;
			}
			return true;
		}

		/// <summary>
		/// True when KeyStatus still has Redigit UI dual tokens that the keyboard product contract removes.
		/// </summary>
		public static bool NeedsKeyboardDeviceKeyStatusRepair(
			int inputModeOrdinal,
			string actionId,
			IReadOnlyList<string> keyStatusForMode)
		{
			if (keyStatusForMode == null || keyStatusForMode.Count == 0)
				return false;
			List<string> contracted = OfficialDefaultKeyRestoreLogic.ApplyKeyboardGameMovementWriteContract(
				inputModeOrdinal,
				actionId,
				keyStatusForMode);
			return !AreSameTokenLists(keyStatusForMode, contracted);
		}

		private static void AppendUnique(List<string> dest, IReadOnlyList<string> source)
		{
			for (int i = 0; i < source.Count; i++)
			{
				string t = source[i];
				if (string.IsNullOrEmpty(t))
					continue;
				bool exists = false;
				for (int j = 0; j < dest.Count; j++)
				{
					if (string.Equals(dest[j], t, StringComparison.Ordinal))
					{
						exists = true;
						break;
					}
				}
				if (!exists)
					dest.Add(t);
			}
		}
	}
}
