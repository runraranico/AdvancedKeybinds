using System;
using System.Collections.Generic;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Bit representation of the digital gamepad state for one input tick.
	///
	/// Every bit maps 1:1 onto an existing canonical <c>Pad:</c> save token — this is a runtime
	/// representation only. Saved data, display names and Stable Token conversion keep using the
	/// strings in <see cref="GamePadToken"/>; nothing here changes what is written to disk.
	///
	/// 29 of 64 bits are used, so triggers, L3/R3, rear paddles and both sticks' four directions
	/// all fit with room for future devices.
	/// </summary>
	public static class GamePadDigitalBits
	{
		public const ulong None = 0UL;

		public const ulong A = 1UL << 0;
		public const ulong B = 1UL << 1;
		public const ulong X = 1UL << 2;
		public const ulong Y = 1UL << 3;
		public const ulong Back = 1UL << 4;
		public const ulong Start = 1UL << 5;
		public const ulong Guide = 1UL << 6;
		public const ulong LeftShoulder = 1UL << 7;
		public const ulong RightShoulder = 1UL << 8;
		public const ulong LeftTrigger = 1UL << 9;
		public const ulong RightTrigger = 1UL << 10;
		public const ulong LeftStick = 1UL << 11;
		public const ulong RightStick = 1UL << 12;
		public const ulong DPadUp = 1UL << 13;
		public const ulong DPadDown = 1UL << 14;
		public const ulong DPadLeft = 1UL << 15;
		public const ulong DPadRight = 1UL << 16;
		public const ulong LeftPaddle1 = 1UL << 17;
		public const ulong LeftPaddle2 = 1UL << 18;
		public const ulong RightPaddle1 = 1UL << 19;
		public const ulong RightPaddle2 = 1UL << 20;
		public const ulong LeftStickUp = 1UL << 21;
		public const ulong LeftStickDown = 1UL << 22;
		public const ulong LeftStickLeft = 1UL << 23;
		public const ulong LeftStickRight = 1UL << 24;
		public const ulong RightStickUp = 1UL << 25;
		public const ulong RightStickDown = 1UL << 26;
		public const ulong RightStickLeft = 1UL << 27;
		public const ulong RightStickRight = 1UL << 28;

		public const ulong StickDirections =
			LeftStickUp | LeftStickDown | LeftStickLeft | LeftStickRight
			| RightStickUp | RightStickDown | RightStickLeft | RightStickRight;

		/// <summary>Bit index order. Parallel to <see cref="OrderedTokens"/>.</summary>
		private static readonly ulong[] OrderedBits =
		{
			A, B, X, Y,
			Back, Start, Guide,
			LeftShoulder, RightShoulder,
			LeftTrigger, RightTrigger,
			LeftStick, RightStick,
			DPadUp, DPadDown, DPadLeft, DPadRight,
			LeftPaddle1, LeftPaddle2, RightPaddle1, RightPaddle2,
			LeftStickUp, LeftStickDown, LeftStickLeft, LeftStickRight,
			RightStickUp, RightStickDown, RightStickLeft, RightStickRight
		};

		private static readonly string[] OrderedTokens =
		{
			GamePadToken.A, GamePadToken.B, GamePadToken.X, GamePadToken.Y,
			GamePadToken.Back, GamePadToken.Start, GamePadToken.Guide,
			GamePadToken.LeftShoulder, GamePadToken.RightShoulder,
			GamePadToken.LeftTrigger, GamePadToken.RightTrigger,
			GamePadToken.LeftStick, GamePadToken.RightStick,
			GamePadToken.DPadUp, GamePadToken.DPadDown, GamePadToken.DPadLeft, GamePadToken.DPadRight,
			GamePadToken.LeftPaddle1, GamePadToken.LeftPaddle2,
			GamePadToken.RightPaddle1, GamePadToken.RightPaddle2,
			GamePadToken.LeftStickUp, GamePadToken.LeftStickDown,
			GamePadToken.LeftStickLeft, GamePadToken.LeftStickRight,
			GamePadToken.RightStickUp, GamePadToken.RightStickDown,
			GamePadToken.RightStickLeft, GamePadToken.RightStickRight
		};

		private static readonly Dictionary<string, ulong> BitByCanonicalToken = BuildLookup();

		/// <summary>Number of digital tokens that currently have a bit.</summary>
		public static int TokenCount => OrderedBits.Length;

		private static Dictionary<string, ulong> BuildLookup()
		{
			var map = new Dictionary<string, ulong>(OrderedTokens.Length, StringComparer.OrdinalIgnoreCase);
			for (int i = 0; i < OrderedTokens.Length; i++)
				map[OrderedTokens[i]] = OrderedBits[i];
			return map;
		}

		/// <summary>
		/// Canonical <c>Pad:</c> token (or alias) → bit. Returns false for non-pad tokens and for
		/// pad tokens that have no digital representation.
		/// </summary>
		public static bool TryGetBit(string token, out ulong bit)
		{
			bit = None;
			if (!GamePadToken.TryCanonicalize(token, out string canonical))
				return false;

			return BitByCanonicalToken.TryGetValue(canonical, out bit);
		}

		/// <summary>Single-bit → canonical token, or null when <paramref name="bit"/> is not a known single bit.</summary>
		public static string TokenForBit(ulong bit)
		{
			for (int i = 0; i < OrderedBits.Length; i++)
			{
				if (OrderedBits[i] == bit)
					return OrderedTokens[i];
			}

			return null;
		}

		public static bool IsDown(ulong mask, ulong bit)
			=> bit != None && (mask & bit) == bit;

		public static ulong RisingEdges(ulong previous, ulong current)
			=> current & ~previous;

		public static ulong FallingEdges(ulong previous, ulong current)
			=> previous & ~current;

		/// <summary>Allocation-free: adds canonical tokens for every set bit.</summary>
		public static void AppendTokens(ulong mask, ICollection<string> dest)
		{
			if (dest == null || mask == None)
				return;

			for (int i = 0; i < OrderedBits.Length; i++)
			{
				if ((mask & OrderedBits[i]) != 0UL)
					dest.Add(OrderedTokens[i]);
			}
		}

		/// <summary>
		/// Adds canonical tokens for every set bit, skipping the ones already present.
		/// Used for capture edge lists, which are ordered and must not contain duplicates.
		/// </summary>
		public static void AppendTokensDistinct(ulong mask, List<string> dest, bool skipStickDirections)
		{
			if (dest == null || mask == None)
				return;

			if (skipStickDirections)
				mask &= ~StickDirections;

			for (int i = 0; i < OrderedBits.Length; i++)
			{
				if ((mask & OrderedBits[i]) == 0UL)
					continue;

				string token = OrderedTokens[i];
				bool exists = false;
				for (int j = 0; j < dest.Count; j++)
				{
					if (string.Equals(dest[j], token, StringComparison.OrdinalIgnoreCase))
					{
						exists = true;
						break;
					}
				}

				if (!exists)
					dest.Add(token);
			}
		}

		public static int PopCount(ulong mask)
		{
			int count = 0;
			while (mask != 0UL)
			{
				mask &= mask - 1UL;
				count++;
			}

			return count;
		}

		/// <summary>Digital stick axes carried by <paramref name="mask"/> for one stick.</summary>
		public static GamePadStickDigitalAxes ToStickAxes(ulong mask, bool leftStick)
		{
			var axes = GamePadStickDigitalAxes.Empty;
			if (leftStick)
			{
				if ((mask & LeftStickLeft) != 0UL) axes.Horizontal = GamePadStickDirection.Left;
				else if ((mask & LeftStickRight) != 0UL) axes.Horizontal = GamePadStickDirection.Right;

				if ((mask & LeftStickUp) != 0UL) axes.Vertical = GamePadStickDirection.Up;
				else if ((mask & LeftStickDown) != 0UL) axes.Vertical = GamePadStickDirection.Down;

				return axes;
			}

			if ((mask & RightStickLeft) != 0UL) axes.Horizontal = GamePadStickDirection.Left;
			else if ((mask & RightStickRight) != 0UL) axes.Horizontal = GamePadStickDirection.Right;

			if ((mask & RightStickUp) != 0UL) axes.Vertical = GamePadStickDirection.Up;
			else if ((mask & RightStickDown) != 0UL) axes.Vertical = GamePadStickDirection.Down;

			return axes;
		}

		/// <summary>Digital stick axes → bits for one stick.</summary>
		public static ulong FromStickAxes(bool leftStick, GamePadStickDigitalAxes axes)
		{
			ulong mask = None;
			if (leftStick)
			{
				mask |= axes.Horizontal switch {
					GamePadStickDirection.Left => LeftStickLeft,
					GamePadStickDirection.Right => LeftStickRight,
					_ => None
				};
				mask |= axes.Vertical switch {
					GamePadStickDirection.Up => LeftStickUp,
					GamePadStickDirection.Down => LeftStickDown,
					_ => None
				};
				return mask;
			}

			mask |= axes.Horizontal switch {
				GamePadStickDirection.Left => RightStickLeft,
				GamePadStickDirection.Right => RightStickRight,
				_ => None
			};
			mask |= axes.Vertical switch {
				GamePadStickDirection.Up => RightStickUp,
				GamePadStickDirection.Down => RightStickDown,
				_ => None
			};
			return mask;
		}
	}
}
