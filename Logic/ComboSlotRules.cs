using System;

namespace AdvancedKeybinds.Core
{
	/// <summary>Slot mutation rules shared by UI and tests (duplicates, F11, token validity).</summary>
	public static class ComboSlotRules
	{
		public enum SetSlotResult
		{
			Accepted,
			RejectedEmpty,
			RejectedInvalidToken,
			RejectedDuplicate,
			RejectedF11,
			RejectedDisplayAlias
		}

		/// <summary>
		/// Attempts to place a stable token into a slot. Does not evaluate LeftClick-alone UI confirm.
		/// </summary>
		public static SetSlotResult TrySetSlot(ComboBindingData binding, int index, string token)
		{
			if (binding == null || index < 1 || index > 3)
				return SetSlotResult.RejectedEmpty;

			if (string.IsNullOrWhiteSpace(token))
				return SetSlotResult.RejectedEmpty;

			if (StableKeyToken.IsDisplayAlias(token))
				return SetSlotResult.RejectedDisplayAlias;

			string canonical = StableKeyToken.CanonicalizeOrNull(token);
			if (canonical == null)
				return SetSlotResult.RejectedInvalidToken;

			if (string.Equals(canonical, "F11", StringComparison.OrdinalIgnoreCase))
				return SetSlotResult.RejectedF11;

#if AK_DIAGNOSTICS
			bool comparedAny = false;
#endif
			for (int i = 1; i <= 3; i++)
			{
				if (i == index)
					continue;

				string existing = binding.GetSlot(i);
				if (string.IsNullOrEmpty(existing))
					continue;

#if AK_DIAGNOSTICS
				comparedAny = true;
#endif
				bool tokensEqual = StableKeyToken.TokensEqual(existing, canonical);
				bool samePhysical = PhysicalKeyIdentity.AreSamePhysical(existing, canonical);

				if (tokensEqual || samePhysical)
				{
#if AK_DIAGNOSTICS
					bool resolveA = PhysicalKeyIdentity.TryResolve(existing, out string idA);
					bool resolveB = PhysicalKeyIdentity.TryResolve(canonical, out string idB);
					bool physicalIdEqual = resolveA && resolveB
						&& string.Equals(idA, idB, StringComparison.OrdinalIgnoreCase);
					PhysicalKeyDupDiag.LogDupCheck(
						caller: "ComboSlotRules.TrySetSlot",
						targetSlot: index,
						existingSlot: i,
						existingToken: existing,
						incomingToken: canonical,
						tokensEqual: tokensEqual,
						samePhysical: samePhysical,
						physicalIdEqual: physicalIdEqual,
						result: "RejectedDuplicate",
						reason: tokensEqual ? "tokens-equal" : "same-physical-id");
#endif
					return SetSlotResult.RejectedDuplicate;
				}

#if AK_DIAGNOSTICS
				{
					bool resolveA = PhysicalKeyIdentity.TryResolve(existing, out string idA);
					bool resolveB = PhysicalKeyIdentity.TryResolve(canonical, out string idB);
					bool physicalIdEqual = resolveA && resolveB
						&& string.Equals(idA, idB, StringComparison.OrdinalIgnoreCase);
					PhysicalKeyDupDiag.LogDupCheck(
						caller: "ComboSlotRules.TrySetSlot",
						targetSlot: index,
						existingSlot: i,
						existingToken: existing,
						incomingToken: canonical,
						tokensEqual: tokensEqual,
						samePhysical: samePhysical,
						physicalIdEqual: physicalIdEqual,
						result: "Accepted",
						reason: !resolveA || !resolveB
							? "pair-ok-but-resolve-incomplete"
							: "different-physical-id");
				}
#endif
			}

#if AK_DIAGNOSTICS
			if (!comparedAny)
			{
				PhysicalKeyDupDiag.LogDupCheck(
					caller: "ComboSlotRules.TrySetSlot",
					targetSlot: index,
					existingSlot: 0,
					existingToken: string.Empty,
					incomingToken: canonical,
					tokensEqual: false,
					samePhysical: false,
					physicalIdEqual: false,
					result: "Accepted",
					reason: "no-other-filled-slots");
			}
#endif

			binding.SetSlot(index, canonical);

			if (binding.FilledSlotCount != 1)
			{
				binding.Hold = false;
				binding.HoldOnRelease = false;
			}

			return SetSlotResult.Accepted;
		}

		public static bool TryApplyHold(ComboBindingData binding, bool hold, bool holdOnRelease, int holdMs)
		{
			if (binding == null || binding.FilledSlotCount != 1)
			{
				if (binding != null)
				{
					binding.Hold = false;
					binding.HoldOnRelease = false;
				}

				return false;
			}

			binding.Hold = hold;
			binding.HoldOnRelease = hold && holdOnRelease;
			binding.HoldMs = holdMs;
			return hold;
		}
	}
}
