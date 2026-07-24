using System;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// One WM_KEY*/SYSKEY* observation. Registration for normal keys uses measured SC only.
	/// Layout OEM / Keys / VK aliases from the same event are diagnostic — never separate newcomers.
	/// </summary>
	public readonly struct MeasuredPhysicalKeyEvent
	{
		public readonly uint Vk;
		public readonly ushort ScanCode;
		public readonly bool Extended;
		public readonly bool IsDown;
		public readonly string Source;
		public readonly string CanonicalToken;
		public readonly string ResolverFallbackToken;
		public readonly long EventSequence;
		public readonly bool HasMeasuredScan;

		public MeasuredPhysicalKeyEvent(
			uint vk,
			ushort scanCode,
			bool extended,
			bool isDown,
			string source,
			string canonicalToken,
			string resolverFallbackToken,
			long eventSequence,
			bool hasMeasuredScan)
		{
			Vk = vk;
			ScanCode = (ushort)(scanCode & 0xFF);
			Extended = extended;
			IsDown = isDown;
			Source = source;
			CanonicalToken = canonicalToken;
			ResolverFallbackToken = resolverFallbackToken;
			EventSequence = eventSequence;
			HasMeasuredScan = hasMeasuredScan;
		}

		/// <summary>
		/// Build from a measured key-down. Normal keys with scan≠0 → single SC: canonical.
		/// </summary>
		public static MeasuredPhysicalKeyEvent FromKeyDown(
			uint vk,
			ushort scan,
			bool extended,
			string source,
			string resolverFallbackToken,
			long eventSequence)
		{
			scan = (ushort)(scan & 0xFF);
			bool special = MeasuredPhysicalKeyNormalizer.IsSpecialHandledVirtualKey(vk);
			bool hasMeasured = scan != 0 && !special;
			string canonical = MeasuredPhysicalKeyNormalizer.ChooseMeasuredCaptureToken(
				vk, scan, extended, resolverFallbackToken);

			return new MeasuredPhysicalKeyEvent(
				vk,
				scan,
				extended,
				isDown: true,
				source,
				canonical,
				resolverFallbackToken,
				eventSequence,
				hasMeasured);
		}

		/// <summary>
		/// Tokens allowed as registration / capture newcomers for this event.
		/// Normal measured keys: exactly one SC:. Special / scan=0: CanonicalToken only.
		/// Never includes layout OEM / VK aliases as additional newcomers.
		/// </summary>
		public int CopyRegistrationCandidates(string[] destination)
		{
			if (destination == null || destination.Length == 0 || string.IsNullOrEmpty(CanonicalToken))
				return 0;

			destination[0] = CanonicalToken;
			return 1;
		}

		public bool IsSameEventOrigin(long otherSequence)
			=> EventSequence != 0 && EventSequence == otherSequence;
	}
}
