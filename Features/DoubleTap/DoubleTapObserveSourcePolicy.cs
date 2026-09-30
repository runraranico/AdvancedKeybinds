using System;

namespace AdvancedKeybinds.Features.DoubleTap
{
	/// <summary>
	/// Pure keyboard observe source gates for DT. No OS / game API / I/O.
	/// Physical Win/Toggle classification mirrors TryResolveWindowsVk /
	/// TryResolveToggleLockEvent priority (helpers are not called or changed).
	/// </summary>
	public static class DoubleTapObserveSourcePolicy
	{
		const uint VkLWin = 0x5B;
		const uint VkRWin = 0x5C;
		const uint VkCapital = 0x14;
		const uint VkNumLock = 0x90;
		const uint VkScroll = 0x91;
		const uint VkPause = 0x13;
		const uint VkCancel = 0x03;

		const uint ScLWin = 0x5B;
		const uint ScRWin = 0x5C;
		const uint ScCapital = 0x3A;
		const uint ScScroll = 0x46;

		public static bool TryWindowKey(
			uint vk,
			ushort scan,
			bool extended,
			bool wasKeyDown,
			bool ownInjected,
			bool sentMessage,
			out int keyId)
		{
			keyId = 0;
			if (wasKeyDown || ownInjected || sentMessage)
				return false;
			if (!IsValidVk(vk) || !IsValidScan(scan))
				return false;
			if (TryClassifyLowLevelPhysical(vk, scan, out _))
				return false;

			keyId = PackKeyId(vk, scan, extended);
			return true;
		}

		public static bool TryLowLevelKey(
			uint vk,
			ushort scan,
			bool extended,
			bool ownInjected,
			out int keyId)
		{
			keyId = 0;
			if (ownInjected)
				return false;
			if (!IsValidVk(vk) || !IsValidScan(scan))
				return false;
			if (!TryClassifyLowLevelPhysical(vk, scan, out uint canonicalVk))
				return false;

			keyId = PackKeyId(canonicalVk, scan, extended);
			return true;
		}

		/// <summary>
		/// Maps 32-bit event time onto TickCount64 using unsigned age.
		/// age = unchecked((uint)nowTick64 - eventTime32).
		/// Rejects now less than 0, age greater than 1000, or age greater than now.
		/// </summary>
		public static bool TryEventTimestamp(uint eventTime32, long nowTick64, out double ms)
		{
			ms = 0.0;
			if (nowTick64 < 0L)
				return false;

			uint age = unchecked((uint)nowTick64 - eventTime32);
			if (age > 1000u)
				return false;
			if ((long)age > nowTick64)
				return false;

			ms = nowTick64 - age;
			return true;
		}

		/// <summary>
		/// Priority: Win VK, Win scan, Caps VK/scan, Pause/Break exclude,
		/// Num VK only, Scroll VK/scan. Same order as existing suppressor resolvers.
		/// </summary>
		static bool TryClassifyLowLevelPhysical(uint vk, ushort scan, out uint canonicalVk)
		{
			canonicalVk = 0;
			uint sc = (uint)(scan & 0xFF);

			if (vk == VkLWin || vk == VkRWin)
			{
				canonicalVk = vk;
				return true;
			}

			if (sc == ScLWin)
			{
				canonicalVk = VkLWin;
				return true;
			}

			if (sc == ScRWin)
			{
				canonicalVk = VkRWin;
				return true;
			}

			if (vk == VkCapital || sc == ScCapital)
			{
				canonicalVk = VkCapital;
				return true;
			}

			if (vk == VkPause || vk == VkCancel)
				return false;

			if (vk == VkNumLock)
			{
				canonicalVk = VkNumLock;
				return true;
			}

			if (vk == VkScroll || sc == ScScroll)
			{
				canonicalVk = VkScroll;
				return true;
			}

			return false;
		}

		static bool IsValidVk(uint vk)
			=> vk >= 1u && vk <= 0xFEu;

		static bool IsValidScan(ushort scan)
			=> scan <= 0xFFu;

		static int PackKeyId(uint vk, ushort scan, bool extended)
			=> (int)(vk | ((uint)(scan & 0xFF) << 8) | (extended ? 1u << 16 : 0u));
	}
}
