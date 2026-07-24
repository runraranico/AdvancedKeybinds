using System;
using System.Collections.Generic;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Decides whether a held VK may add Keys/VK orphan tokens when measured SC already owns the press.
	/// Delegates to <see cref="SameEventAliasPolicy"/> (world-portable; not JIS-only).
	/// </summary>
	public static class MeasuredVkCoverage
	{
		public static bool ShouldSuppressOrphanKeysOrVk(
			uint vk,
			ushort measuredScanForVk,
			bool measuredExtendedForVk,
			IEnumerable<string> pressed)
			=> SameEventAliasPolicy.ShouldSuppressOrphanKeysOrVk(
				vk, measuredScanForVk, measuredExtendedForVk, keysName: null, pressed);

		public static bool ShouldSuppressOrphanKeysOrVk(
			uint vk,
			ushort measuredScanForVk,
			bool measuredExtendedForVk,
			string keysName,
			IEnumerable<string> pressed,
			bool sameMeasuredEventOrigin = false,
			long eventSequence = 0,
			long existingEventSequence = 0)
			=> SameEventAliasPolicy.ShouldSuppressOrphanKeysOrVk(
				vk, measuredScanForVk, measuredExtendedForVk, keysName, pressed,
				sameMeasuredEventOrigin, eventSequence, existingEventSequence);
	}
}
