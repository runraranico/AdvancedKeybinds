using System;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Pure Left/Right layout + point-id allocation for DialogueTweakGamepadCompat (AK soft dependency).
	/// No Terraria types - shared by runtime compat and Core.Tests.
	/// Name includes Compat so this is never mistaken for DialogueTweak internals.
	/// </summary>
	public static class DialogueTweakGamepadCompatNavLayout
	{
		/// <summary>Stop at edge (do not use vanilla SpecialLeft -3 or EXIT -4).</summary>
		public const int NoLink = -1;

		/// <summary>Vanilla leftmost special - forbidden for AK edge stop policy.</summary>
		public const int VanillaSpecialLeft = -3;

		/// <summary>Vanilla EXIT - must never appear on the rightmost chat button.</summary>
		public const int ForbiddenExit = -4;

		public const int PreferredPageId = 1003;
		public const int PreferredBasePointId = 2500;
		public const int VanillaSlotCount = 4;

		/// <summary>
		/// Builds Left/Right for count points using explicit pointIds.
		/// Ends use NoLink (stop in place). Never emits -3 or -4.
		/// </summary>
		public static void BuildLinks(int count, int[] pointIds, int[] left, int[] right)
		{
			if (count < 0)
				throw new ArgumentOutOfRangeException(nameof(count));
			if (pointIds == null || pointIds.Length < count)
				throw new ArgumentException("pointIds too small.", nameof(pointIds));
			if (left == null || right == null || left.Length < count || right.Length < count)
				throw new ArgumentException("left/right buffers too small.");

			for (int i = 0; i < count; i++)
			{
				left[i] = i == 0 ? NoLink : pointIds[i - 1];
				right[i] = i == count - 1 ? NoLink : pointIds[i + 1];
			}
		}

		/// <summary>
		/// Prefers PreferredBasePointId + i when canUseId allows; otherwise scans upward.
		/// Does not hard-code 2504 - grows from live count and collision checks.
		/// </summary>
		public static void AllocatePointIds(int count, Func<int, bool> canUseId, int[] outIds, int preferredBase = PreferredBasePointId)
		{
			if (count < 0)
				throw new ArgumentOutOfRangeException(nameof(count));
			if (canUseId == null)
				throw new ArgumentNullException(nameof(canUseId));
			if (outIds == null || outIds.Length < count)
				throw new ArgumentException("outIds too small.", nameof(outIds));

			for (int i = 0; i < count; i++)
			{
				int prefer = preferredBase + i;
				int id = prefer;
				while (!canUseId(id) || AlreadyChosen(outIds, i, id))
					id++;
				outIds[i] = id;
			}
		}

		/// <summary>
		/// Models Workshop DialogueTweak + vanilla page 1003: only up to 4 link slots (2500-2503).
		/// AddButtons never receive points - unreachable under D-pad Right from Back.
		/// </summary>
		public static bool[] VanillaReachableMask(bool happiness, bool extra, bool shop, int addButtonCount)
		{
			if (addButtonCount < 0)
				throw new ArgumentOutOfRangeException(nameof(addButtonCount));

			int logical = 1;
			if (happiness)
				logical++;
			if (extra)
				logical++;
			if (shop)
				logical++;
			logical += addButtonCount;

			var reachable = new bool[logical];
			int vanillaMapped = 1;
			if (happiness)
				vanillaMapped++;
			if (extra)
				vanillaMapped++;
			if (shop)
				vanillaMapped++;
			if (vanillaMapped > VanillaSlotCount)
				vanillaMapped = VanillaSlotCount;

			int firstAdd = 1 + (happiness ? 1 : 0) + (extra ? 1 : 0) + (shop ? 1 : 0);
			for (int i = 0; i < logical; i++)
			{
				bool isAdd = i >= firstAdd;
				reachable[i] = !isAdd && i < vanillaMapped;
			}

			return reachable;
		}

		public static bool VanillaCannotReachAnyAddButton(bool happiness, bool extra, bool shop, int addButtonCount)
		{
			if (addButtonCount <= 0)
				return true;

			bool[] mask = VanillaReachableMask(happiness, extra, shop, addButtonCount);
			int firstAdd = 1 + (happiness ? 1 : 0) + (extra ? 1 : 0) + (shop ? 1 : 0);
			for (int i = firstAdd; i < mask.Length; i++)
			{
				if (mask[i])
					return false;
			}

			return true;
		}

		/// <summary>
		/// Classification of leftover page-1003 points after a Mod Reload (or before first AK wire).
		/// </summary>
		public enum ResidualNavKind
		{
			None = 0,
			Incomplete,
			/// <summary>Vanilla-style chain that still ends on EXIT (-4).</summary>
			VanillaLikeWithExit,
			/// <summary>
			/// Matches the exact Left/Right / id pattern AK itself writes. Safe to reclaim and rewire
			/// after Build+Reload wiped ActivePointIds / ownership flags.
			/// </summary>
			AkOwnedResidual,
			/// <summary>
			/// A complete non-EXIT chain that is not AK's signature — treat as foreign / upstream.
			/// </summary>
			ForeignCompleteUpstream
		}

		/// <summary>What AfterDrawButtons must do for the current frame.</summary>
		public enum WiringAction
		{
			/// <summary>AK already owns wiring — keep ApplyChain.</summary>
			KeepOwning = 0,
			/// <summary>Create or reclaim points and ApplyChain.</summary>
			WireOrReclaim,
			/// <summary>True upstream / foreign complete nav — do not touch points.</summary>
			SkipUpstream,
			/// <summary>Chat closed / no buttons.</summary>
			ClearInactive
		}

		/// <summary>
		/// True when an existing page-1003 style chain already covers expectedVisibleCount
		/// without hitting EXIT (-4). Alone this cannot distinguish Reload leftovers of AK's own
		/// wiring from a real upstream implementation — use <see cref="ClassifyResidualNav"/>.
		/// </summary>
		public static bool LooksLikeCompleteUpstreamNav(
			int expectedVisibleCount,
			Func<int, bool> pointExists,
			Func<int, int> getRight,
			int basePointId = PreferredBasePointId)
		{
			if (expectedVisibleCount <= 0)
				return true;
			if (pointExists == null || getRight == null)
				throw new ArgumentNullException(pointExists == null ? nameof(pointExists) : nameof(getRight));

			if (expectedVisibleCount > VanillaSlotCount)
			{
				bool hasExtendedPoint = false;
				for (int id = basePointId + VanillaSlotCount; id < basePointId + expectedVisibleCount + 8; id++)
				{
					if (pointExists(id))
					{
						hasExtendedPoint = true;
						break;
					}
				}

				if (!hasExtendedPoint)
					return false;
			}

			if (!pointExists(basePointId))
				return false;

			int current = basePointId;
			int visited = 0;
			var seen = new System.Collections.Generic.HashSet<int>();
			while (visited < expectedVisibleCount + 2)
			{
				if (!seen.Add(current))
					return false;
				visited++;

				if (visited >= expectedVisibleCount)
				{
					int rightAtEnd = getRight(current);
					return rightAtEnd != ForbiddenExit;
				}

				if (!pointExists(current))
					return false;

				int right = getRight(current);
				if (right == ForbiddenExit)
					return false;
				if (right == NoLink || right == VanillaSpecialLeft)
					return false;
				if (!pointExists(right))
					return false;
				current = right;
			}

			return false;
		}

		/// <summary>
		/// True only when the live chain matches what <see cref="AllocatePointIds"/> +
		/// <see cref="BuildLinks"/> would have written for this count (preferred base, NoLink ends,
		/// consecutive Right/Left). Optional Up/Down must be -1 / -2 when supplied (AK's write).
		/// </summary>
		public static bool MatchesAkOwnedWiringSignature(
			int expectedVisibleCount,
			Func<int, bool> pointExists,
			Func<int, int> getLeft,
			Func<int, int> getRight,
			Func<int, int> getUp = null,
			Func<int, int> getDown = null,
			int basePointId = PreferredBasePointId)
		{
			if (expectedVisibleCount <= 0)
				return false;
			if (pointExists == null || getLeft == null || getRight == null)
				throw new ArgumentNullException(nameof(pointExists));

			var expectedIds = new int[expectedVisibleCount];
			AllocatePointIds(expectedVisibleCount, id => true, expectedIds, basePointId);

			var expectedLeft = new int[expectedVisibleCount];
			var expectedRight = new int[expectedVisibleCount];
			BuildLinks(expectedVisibleCount, expectedIds, expectedLeft, expectedRight);

			for (int i = 0; i < expectedVisibleCount; i++)
			{
				int id = expectedIds[i];
				if (!pointExists(id))
					return false;
				if (getLeft(id) != expectedLeft[i])
					return false;
				if (getRight(id) != expectedRight[i])
					return false;
				if (getUp != null && getUp(id) != -1)
					return false;
				if (getDown != null && getDown(id) != -2)
					return false;
			}

			return true;
		}

		/// <summary>
		/// Distinguishes Reload leftovers of AK wiring from vanilla EXIT chains and foreign complete nav.
		/// Incomplete leftovers must not be treated as upstream-complete.
		/// </summary>
		public static ResidualNavKind ClassifyResidualNav(
			int expectedVisibleCount,
			Func<int, bool> pointExists,
			Func<int, int> getLeft,
			Func<int, int> getRight,
			Func<int, int> getUp = null,
			Func<int, int> getDown = null,
			int basePointId = PreferredBasePointId)
		{
			if (expectedVisibleCount <= 0)
				return ResidualNavKind.None;
			if (pointExists == null || getLeft == null || getRight == null)
				throw new ArgumentNullException(nameof(pointExists));

			if (MatchesAkOwnedWiringSignature(
				    expectedVisibleCount, pointExists, getLeft, getRight, getUp, getDown, basePointId))
				return ResidualNavKind.AkOwnedResidual;

			if (!pointExists(basePointId))
				return ResidualNavKind.Incomplete;

			// Walk at most expectedVisibleCount hops looking for vanilla EXIT.
			int current = basePointId;
			var seen = new System.Collections.Generic.HashSet<int>();
			for (int step = 0; step < expectedVisibleCount + 2; step++)
			{
				if (!seen.Add(current))
					break;
				int right = getRight(current);
				if (right == ForbiddenExit)
					return ResidualNavKind.VanillaLikeWithExit;
				if (right == NoLink || right == VanillaSpecialLeft)
					break;
				if (!pointExists(right))
					break;
				current = right;
			}

			if (LooksLikeCompleteUpstreamNav(expectedVisibleCount, pointExists, getRight, basePointId))
				return ResidualNavKind.ForeignCompleteUpstream;

			return ResidualNavKind.Incomplete;
		}

		/// <summary>
		/// Pure AfterDrawButtons policy. Reload must reclaim <see cref="ResidualNavKind.AkOwnedResidual"/>
		/// instead of latching SkipUpstream (the pre-fix false positive).
		/// </summary>
		public static WiringAction DecideWiringAction(
			bool akOwnsWiring,
			bool skipWiringUpstream,
			bool upstreamTypePresent,
			ResidualNavKind residual,
			int expectedVisibleCount,
			int skipLatchVisibleCount)
		{
			if (expectedVisibleCount <= 0)
				return WiringAction.ClearInactive;

			if (akOwnsWiring)
				return WiringAction.KeepOwning;

			if (upstreamTypePresent)
				return WiringAction.SkipUpstream;

			if (residual == ResidualNavKind.AkOwnedResidual)
				return WiringAction.WireOrReclaim;

			if (skipWiringUpstream && skipLatchVisibleCount == expectedVisibleCount)
				return WiringAction.SkipUpstream;

			if (residual == ResidualNavKind.ForeignCompleteUpstream)
				return WiringAction.SkipUpstream;

			// Incomplete / vanilla EXIT / none → AK wires (reuses page points when ids allow).
			return WiringAction.WireOrReclaim;
		}

		/// <summary>
		/// Old (buggy) AfterDrawButtons gate: treated any non-EXIT complete chain as upstream,
		/// including Reload leftovers of AK's own NoLink wiring.
		/// </summary>
		public static bool OldBugWouldLatchSkipUpstream(
			bool akOwnsWiring,
			int expectedVisibleCount,
			Func<int, bool> pointExists,
			Func<int, int> getRight,
			int basePointId = PreferredBasePointId)
		{
			if (akOwnsWiring)
				return false;
			return LooksLikeCompleteUpstreamNav(expectedVisibleCount, pointExists, getRight, basePointId);
		}

		/// <summary>
		/// Whether a previously latched residual skip must be cleared (chat reopen / count change /
		/// residual now looks like AK ownership).
		/// </summary>
		public static bool ShouldClearResidualSkipLatch(
			bool skipWiringUpstream,
			bool upstreamTypePresent,
			ResidualNavKind residualNow,
			int expectedVisibleCount,
			int skipLatchVisibleCount)
		{
			if (!skipWiringUpstream || upstreamTypePresent)
				return false;
			if (residualNow == ResidualNavKind.AkOwnedResidual)
				return true;
			if (skipLatchVisibleCount != expectedVisibleCount)
				return true;
			return false;
		}

		public static bool TryTraverseRightAll(int count, int[] pointIds, out int visitedCount, out bool hitForbiddenExit)
		{
			visitedCount = 0;
			hitForbiddenExit = false;
			if (count <= 0)
				return true;

			var left = new int[count];
			var right = new int[count];
			BuildLinks(count, pointIds, left, right);

			bool[] seen = new bool[count];
			int index = 0;
			for (int step = 0; step < count + 2; step++)
			{
				if (index < 0 || index >= count)
					return false;
				if (seen[index])
					return false;
				seen[index] = true;
				visitedCount++;

				int nextId = right[index];
				if (nextId == ForbiddenExit || nextId == VanillaSpecialLeft)
				{
					hitForbiddenExit = nextId == ForbiddenExit;
					return false;
				}

				if (nextId == NoLink)
					break;

				int nextIndex = IndexOfId(pointIds, count, nextId);
				if (nextIndex < 0)
					return false;
				index = nextIndex;
			}

			for (int i = 0; i < count; i++)
			{
				if (!seen[i])
					return false;
			}

			return true;
		}

		public static bool TryTraverseLeftAll(int count, int[] pointIds)
		{
			if (count <= 0)
				return true;

			var left = new int[count];
			var right = new int[count];
			BuildLinks(count, pointIds, left, right);

			bool[] seen = new bool[count];
			int index = count - 1;
			for (int step = 0; step < count + 2; step++)
			{
				if (index < 0 || index >= count)
					return false;
				if (seen[index])
					return false;
				seen[index] = true;

				int nextId = left[index];
				if (index == 0)
					return nextId == NoLink && CountTrue(seen) == count;

				if (nextId == NoLink || nextId == ForbiddenExit || nextId == VanillaSpecialLeft)
					return false;

				int nextIndex = IndexOfId(pointIds, count, nextId);
				if (nextIndex < 0)
					return false;
				index = nextIndex;
			}

			return false;
		}

		public static bool HasDuplicatePointIds(int count, int[] pointIds)
		{
			if (count <= 0 || pointIds == null)
				return false;

			for (int i = 0; i < count; i++)
			{
				for (int j = i + 1; j < count; j++)
				{
					if (pointIds[i] == pointIds[j])
						return true;
				}
			}

			return false;
		}

		public static bool EndsAvoidForbiddenSentinels(int count, int[] pointIds)
		{
			if (count <= 0)
				return true;

			var left = new int[count];
			var right = new int[count];
			BuildLinks(count, pointIds, left, right);
			return left[0] == NoLink
				&& right[count - 1] == NoLink
				&& left[0] != VanillaSpecialLeft
				&& right[count - 1] != ForbiddenExit;
		}

		private static bool AlreadyChosen(int[] outIds, int filledCount, int id)
		{
			for (int i = 0; i < filledCount; i++)
			{
				if (outIds[i] == id)
					return true;
			}

			return false;
		}

		private static int IndexOfId(int[] pointIds, int count, int id)
		{
			for (int i = 0; i < count; i++)
			{
				if (pointIds[i] == id)
					return i;
			}

			return -1;
		}

		private static int CountTrue(bool[] a)
		{
			int n = 0;
			for (int i = 0; i < a.Length; i++)
			{
				if (a[i])
					n++;
			}

			return n;
		}
	}

	/// <summary>
	/// Pure apply/disable policy for DialogueTweakGamepadCompat (testable without Terraria).
	/// </summary>
	public static class DialogueTweakGamepadCompatPolicy
	{
		public enum ApplyOutcome
		{
			SkipModAbsent = 0,
			SkipAlreadyApplied,
			SkipDisabled,
			SkipUpstreamTypePresent,
			SkipUpstreamNavComplete,
			DisableMissingMember,
			DisableCodeNull,
			DisableDetourFailed,
			ApplyOk
		}

		public sealed class SessionState
		{
			public bool Applied;
			public bool Disabled;
			public bool LoggedDisableOnce;
			public int DetourCount;
			public int DisableLogCount;
			public string LastDisableReason;

			public void ResetForReload()
			{
				Applied = false;
				Disabled = false;
				LoggedDisableOnce = false;
				DetourCount = 0;
				DisableLogCount = 0;
				LastDisableReason = null;
			}

			public void DisposeDetours()
			{
				DetourCount = 0;
			}
		}

		public static ApplyOutcome DecideInitialApply(
			bool alreadyApplied,
			bool disabled,
			bool modPresent,
			bool codeNull,
			bool upstreamTypePresent,
			bool buttonHandlerFound,
			bool drawButtonsFound,
			bool buttonInfosFound,
			bool buttonInfoMembersFound,
			bool chatUiPanelFound)
		{
			if (alreadyApplied)
				return ApplyOutcome.SkipAlreadyApplied;
			if (disabled)
				return ApplyOutcome.SkipDisabled;
			if (!modPresent)
				return ApplyOutcome.SkipModAbsent;
			if (codeNull)
				return ApplyOutcome.DisableCodeNull;
			if (upstreamTypePresent)
				return ApplyOutcome.SkipUpstreamTypePresent;
			if (!buttonHandlerFound)
				return ApplyOutcome.DisableMissingMember;
			if (!drawButtonsFound)
				return ApplyOutcome.DisableMissingMember;
			if (!buttonInfosFound)
				return ApplyOutcome.DisableMissingMember;
			if (!buttonInfoMembersFound)
				return ApplyOutcome.DisableMissingMember;
			if (!chatUiPanelFound)
				return ApplyOutcome.DisableMissingMember;
			return ApplyOutcome.ApplyOk;
		}

		public static void ApplySession(SessionState state, ApplyOutcome outcome, string missingMember)
		{
			if (state == null)
				throw new ArgumentNullException(nameof(state));

			switch (outcome)
			{
				case ApplyOutcome.SkipModAbsent:
				case ApplyOutcome.SkipAlreadyApplied:
				case ApplyOutcome.SkipDisabled:
				case ApplyOutcome.SkipUpstreamTypePresent:
				case ApplyOutcome.SkipUpstreamNavComplete:
					return;
				case ApplyOutcome.DisableCodeNull:
					Disable(state, "DialogueTweak.Code was null.");
					return;
				case ApplyOutcome.DisableMissingMember:
					Disable(state, string.IsNullOrEmpty(missingMember)
						? "A required DialogueTweak member was not found."
						: missingMember + " was not found.");
					return;
				case ApplyOutcome.DisableDetourFailed:
					state.DisposeDetours();
					Disable(state, string.IsNullOrEmpty(missingMember)
						? "Detour creation failed."
						: missingMember);
					return;
				case ApplyOutcome.ApplyOk:
					if (state.DetourCount > 0)
						return;
					state.DetourCount = 1;
					state.Applied = true;
					return;
			}
		}

		public static void Disable(SessionState state, string reason)
		{
			if (state == null)
				throw new ArgumentNullException(nameof(state));

			state.DisposeDetours();
			state.Disabled = true;
			state.Applied = false;
			state.LastDisableReason = reason ?? "unknown";
			if (!state.LoggedDisableOnce)
			{
				state.LoggedDisableOnce = true;
				state.DisableLogCount++;
			}
		}

		public static void Unload(SessionState state)
		{
			if (state == null)
				throw new ArgumentNullException(nameof(state));

			state.DisposeDetours();
			state.Applied = false;
			state.Disabled = false;
			state.LoggedDisableOnce = false;
			state.LastDisableReason = null;
		}
	}
}