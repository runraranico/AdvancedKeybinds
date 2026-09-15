using System;
using System.Collections.Generic;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Logical gamepad save tokens (<c>Pad:A</c>, <c>Pad:L1</c> aliases, …).
	/// Distinct from Keyboard / Mouse tokens. String-only — no FNA dependency.
	/// Includes LS/RS direction tokens (<c>Pad:LS:Up</c>, …) separate from L3/R3 click.
	/// </summary>
	public static class GamePadToken
	{
		public const string Prefix = "Pad:";

		// Canonical persisted forms (stable; do not rename — breaks saves).
		// Face positions (FNA/SDL): A=South, B=East, X=West, Y=North.
		public const string A = "Pad:A";
		public const string B = "Pad:B";
		public const string X = "Pad:X";
		public const string Y = "Pad:Y";
		public const string Back = "Pad:Back";
		public const string Start = "Pad:Start";
		public const string Guide = "Pad:Guide";
		public const string LeftShoulder = "Pad:LeftShoulder";
		public const string RightShoulder = "Pad:RightShoulder";
		public const string LeftTrigger = "Pad:LeftTrigger";
		public const string RightTrigger = "Pad:RightTrigger";
		public const string LeftStick = "Pad:LeftStick";
		public const string RightStick = "Pad:RightStick";

		// Stick *direction* (not L3/R3 click). One strong-axis token per stick.
		public const string LeftStickUp = "Pad:LS:Up";
		public const string LeftStickDown = "Pad:LS:Down";
		public const string LeftStickLeft = "Pad:LS:Left";
		public const string LeftStickRight = "Pad:LS:Right";
		public const string RightStickUp = "Pad:RS:Up";
		public const string RightStickDown = "Pad:RS:Down";
		public const string RightStickLeft = "Pad:RS:Left";
		public const string RightStickRight = "Pad:RS:Right";
		public const string DPadUp = "Pad:DPadUp";
		public const string DPadDown = "Pad:DPadDown";
		public const string DPadLeft = "Pad:DPadLeft";
		public const string DPadRight = "Pad:DPadRight";

		/// <summary>Upper/primary left rear paddle when exposed as FNA LeftPaddle1 (Paddle2EXT).</summary>
		public const string LeftPaddle1 = "Pad:LeftPaddle1";
		/// <summary>Lower/secondary left rear paddle when exposed as FNA LeftPaddle2 (Paddle4EXT).</summary>
		public const string LeftPaddle2 = "Pad:LeftPaddle2";
		/// <summary>Upper/primary right rear paddle when exposed as FNA RightPaddle1 (Paddle1EXT).</summary>
		public const string RightPaddle1 = "Pad:RightPaddle1";
		/// <summary>Lower/secondary right rear paddle when exposed as FNA RightPaddle2 (Paddle3EXT).</summary>
		public const string RightPaddle2 = "Pad:RightPaddle2";

		// Friendly aliases (canonicalized to LeftShoulder / …). Not separate save forms.
		public const string L1 = LeftShoulder;
		public const string R1 = RightShoulder;
		public const string L2 = LeftTrigger;
		public const string R2 = RightTrigger;
		public const string L3 = LeftStick;
		public const string R3 = RightStick;

		private static readonly HashSet<string> Known = new(StringComparer.OrdinalIgnoreCase) {
			A, B, X, Y,
			Back, Start, Guide,
			LeftShoulder, RightShoulder,
			LeftTrigger, RightTrigger,
			LeftStick, RightStick,
			LeftStickUp, LeftStickDown, LeftStickLeft, LeftStickRight,
			RightStickUp, RightStickDown, RightStickLeft, RightStickRight,
			DPadUp, DPadDown, DPadLeft, DPadRight,
			LeftPaddle1, LeftPaddle2, RightPaddle1, RightPaddle2
		};

		/// <summary>
		/// Compact / alternate spellings → canonical <c>Pad:…</c>.
		/// Accepts <c>PadA</c>, <c>PadL1</c>, <c>Pad:L1</c>, etc. without changing save canonicals.
		/// </summary>
		private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase) {
			["PadA"] = A,
			["PadB"] = B,
			["PadX"] = X,
			["PadY"] = Y,
			["PadBack"] = Back,
			["PadSelect"] = Back,
			["Pad:Select"] = Back,
			["PadStart"] = Start,
			["PadGuide"] = Guide,
			["PadBigButton"] = Guide,
			["PadL1"] = LeftShoulder,
			["PadR1"] = RightShoulder,
			["PadL2"] = LeftTrigger,
			["PadR2"] = RightTrigger,
			["PadL3"] = LeftStick,
			["PadR3"] = RightStick,
			["PadDPadUp"] = DPadUp,
			["PadDPadDown"] = DPadDown,
			["PadDPadLeft"] = DPadLeft,
			["PadDPadRight"] = DPadRight,
			["Pad:L1"] = LeftShoulder,
			["Pad:R1"] = RightShoulder,
			["Pad:L2"] = LeftTrigger,
			["Pad:R2"] = RightTrigger,
			["Pad:L3"] = LeftStick,
			["Pad:R3"] = RightStick,
			["Pad:LB"] = LeftShoulder,
			["Pad:RB"] = RightShoulder,
			["Pad:LT"] = LeftTrigger,
			["Pad:RT"] = RightTrigger,
			["Pad:BigButton"] = Guide
		};


		public static bool IsStickDirectionToken(string token)
		{
			if (!TryCanonicalize(token, out string canonical))
				return false;
			return canonical is LeftStickUp or LeftStickDown or LeftStickLeft or LeftStickRight
				or RightStickUp or RightStickDown or RightStickLeft or RightStickRight;
		}
		public static bool IsPadToken(string token)
		{
			if (string.IsNullOrWhiteSpace(token))
				return false;

			token = token.Trim();
			if (token.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
				return true;

			return Aliases.ContainsKey(token);
		}

		public static bool TryCanonicalize(string token, out string canonical)
		{
			canonical = null;
			if (string.IsNullOrWhiteSpace(token))
				return false;

			token = token.Trim();

			if (Aliases.TryGetValue(token, out string fromAlias))
			{
				canonical = fromAlias;
				return true;
			}

			if (!token.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
				return false;

			foreach (string name in Known)
			{
				if (!string.Equals(name, token, StringComparison.OrdinalIgnoreCase))
					continue;
				canonical = name;
				return true;
			}

			return false;
		}

		/// <summary>
		/// UI display only — never persist.
		/// Face buttons use positional Pad arrows (South/East/West/North), not A/B/X/Y or maker glyphs.
		/// Terraria Controls glyph path uses Buttons names separately and must not call this for icons.
		/// </summary>
		public static string FormatForDisplay(string token)
		{
			if (!TryCanonicalize(token, out string canonical))
				return token ?? string.Empty;

			return canonical switch {
				A => "Pad \u2193",
				B => "Pad \u2192",
				X => "Pad \u2190",
				Y => "Pad \u2191",
				// Persist Pad:Back (FNA Buttons.Back). Display shows the storage token form.
				Back => "Pad:Back",
				Start => "Start",
				Guide => "Guide",
				LeftShoulder => "L1",
				RightShoulder => "R1",
				LeftTrigger => "L2",
				RightTrigger => "R2",
				LeftStickUp => "LS ↑",
				LeftStickDown => "LS ↓",
				LeftStickLeft => "LS ←",
				LeftStickRight => "LS →",
				RightStickUp => "RS ↑",
				RightStickDown => "RS ↓",
				RightStickLeft => "RS ←",
				RightStickRight => "RS →",
				LeftStick => "L3",
				RightStick => "R3",
				DPadUp => "D-Up",
				DPadDown => "D-Down",
				DPadLeft => "D-Left",
				DPadRight => "D-Right",
				LeftPaddle1 => "L-Paddle1",
				LeftPaddle2 => "L-Paddle2",
				RightPaddle1 => "R-Paddle1",
				RightPaddle2 => "R-Paddle2",
				_ => canonical
			};
		}
	}
}
