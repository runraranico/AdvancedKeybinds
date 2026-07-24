using System;
using System.Collections.Generic;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Pure edge decision for one measured WM_KEY* down.
	/// WindowsKeySuppressor.ApplyPhysicalKeyEvent must call this so
	/// normal keys emit exactly one SC: token (no simultaneous VK:/Keys/SC: gameplay edges).
	/// </summary>
	public static class MeasuredPhysicalKeyEdgePolicy
	{
		public readonly struct Decision
		{
			public readonly string CanonicalToken;
			public readonly bool LatchCapture;
			public readonly bool AddCanonicalGameplayEdge;
			public readonly bool AddVkGameplayEdge;
			public readonly bool AddResolverGameplayEdge;
			public readonly string VkToken;
			public readonly string ResolverToken;

			public Decision(
				string canonicalToken,
				bool latchCapture,
				bool addCanonicalGameplayEdge,
				bool addVkGameplayEdge,
				bool addResolverGameplayEdge,
				string vkToken,
				string resolverToken)
			{
				CanonicalToken = canonicalToken;
				LatchCapture = latchCapture;
				AddCanonicalGameplayEdge = addCanonicalGameplayEdge;
				AddVkGameplayEdge = addVkGameplayEdge;
				AddResolverGameplayEdge = addResolverGameplayEdge;
				VkToken = vkToken;
				ResolverToken = resolverToken;
			}

			/// <summary>Tokens that would be appended to gameplay edges this frame (order preserved).</summary>
			public List<string> CollectGameplayEdgeTokens()
			{
				var list = new List<string>(3);
				if (AddVkGameplayEdge && !string.IsNullOrEmpty(VkToken))
					list.Add(VkToken);
				if (AddResolverGameplayEdge && !string.IsNullOrEmpty(ResolverToken)
					&& !string.Equals(ResolverToken, VkToken, StringComparison.OrdinalIgnoreCase))
					list.Add(ResolverToken);
				if (AddCanonicalGameplayEdge && !string.IsNullOrEmpty(CanonicalToken)
					&& !ContainsIgnoreCase(list, CanonicalToken))
					list.Add(CanonicalToken);
				return list;
			}

			public List<string> CollectCaptureEdgeTokens()
			{
				var list = new List<string>(1);
				if (LatchCapture && !string.IsNullOrEmpty(CanonicalToken))
					list.Add(CanonicalToken);
				return list;
			}
		}

		/// <summary>
		/// Decide capture/gameplay edges for a measured key-down.
		/// Normal keys (measured scan != 0, not special): one SC: only.
		/// Special keys keep dedicated VK/resolver tokens.
		/// </summary>
		public static Decision DecideDown(
			uint vk,
			ushort measuredScan,
			bool measuredExtended,
			string resolverFallbackToken,
			bool scanRising)
		{
			string canonical = MeasuredPhysicalKeyNormalizer.ChooseMeasuredCaptureToken(
				vk,
				measuredScan,
				measuredExtended,
				resolverFallbackToken);

			string vkToken = PhysicalKeyToken.IsValidVirtualKey(vk)
				? PhysicalKeyToken.FormatVk(vk)
				: null;

			bool special = MeasuredPhysicalKeyNormalizer.IsSpecialHandledVirtualKey(vk);
			bool rising = scanRising;

			// Normal keys: never dual-publish VK: + SC: / Keys name on the same press.
			if (!special)
			{
				return new Decision(
					canonicalToken: canonical,
					latchCapture: rising,
					addCanonicalGameplayEdge: rising && !string.IsNullOrEmpty(canonical),
					addVkGameplayEdge: false,
					addResolverGameplayEdge: false,
					vkToken: vkToken,
					resolverToken: resolverFallbackToken);
			}

			// Special keys: keep dedicated tokens (may include VK + resolver name).
			bool addVk = rising && !string.IsNullOrEmpty(vkToken);
			bool addResolver = rising
				&& !string.IsNullOrEmpty(resolverFallbackToken)
				&& !string.Equals(resolverFallbackToken, vkToken, StringComparison.OrdinalIgnoreCase);

			return new Decision(
				canonicalToken: canonical,
				latchCapture: rising,
				addCanonicalGameplayEdge: false,
				addVkGameplayEdge: addVk,
				addResolverGameplayEdge: addResolver,
				vkToken: vkToken,
				resolverToken: resolverFallbackToken);
		}

		private static bool ContainsIgnoreCase(List<string> list, string value)
		{
			foreach (string item in list)
			{
				if (string.Equals(item, value, StringComparison.OrdinalIgnoreCase))
					return true;
			}

			return false;
		}
	}
}
