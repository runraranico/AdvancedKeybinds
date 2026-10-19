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
			RejectedDisplayAlias,
			/// <summary>Same-stick opposite directions (Up+Down / Left+Right) across slots.</summary>
			RejectedOppositeStickDirection
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

			if (GamePadStickOppositeDirectionRules.ConflictsWithOtherSlots(
				binding.Slot1, binding.Slot2, binding.Slot3, index, canonical))
				return SetSlotResult.RejectedOppositeStickDirection;

			for (int i = 1; i <= 3; i++)
			{
				if (i == index)
					continue;

				string existing = binding.GetSlot(i);
				if (string.IsNullOrEmpty(existing))
					continue;

				bool tokensEqual = StableKeyToken.TokensEqual(existing, canonical);
				bool samePhysical = PhysicalKeyIdentity.AreSamePhysical(existing, canonical);

				if (tokensEqual || samePhysical)
				{
					return SetSlotResult.RejectedDuplicate;
				}

			}

			if (KeyboardAdditionalOrPolicy.HasToken(binding.AdditionalOrToken)
				&& (StableKeyToken.TokensEqual(binding.AdditionalOrToken, canonical)
					|| PhysicalKeyIdentity.AreSamePhysical(binding.AdditionalOrToken, canonical)))
			{
				return SetSlotResult.RejectedDuplicate;
			}


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
