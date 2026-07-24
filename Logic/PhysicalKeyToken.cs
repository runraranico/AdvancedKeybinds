using System;
using System.Globalization;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Fallback save tokens when FNA <see cref="Microsoft.Xna.Framework.Input.Keys"/> is unavailable.
	/// <list type="bullet">
	/// <item><c>VK:0xDF</c> — Windows virtual-key</item>
	/// <item><c>SC:0x35:E0</c> / <c>SC:0x35:Normal</c> — make-code + extended prefix flag</item>
	/// <item><c>Key:OemComma</c> — optional alias; canonicalizes to plain Keys name</item>
	/// </list>
	/// </summary>
	public static class PhysicalKeyToken
	{
		public const string VkPrefix = "VK:";
		public const string ScPrefix = "SC:";
		public const string KeyPrefix = "Key:";

		public static bool IsFallbackToken(string token)
		{
			if (string.IsNullOrWhiteSpace(token))
				return false;

			token = token.Trim();
			return token.StartsWith(VkPrefix, StringComparison.OrdinalIgnoreCase)
				|| token.StartsWith(ScPrefix, StringComparison.OrdinalIgnoreCase);
		}

		/// <summary>
		/// Fallback / measured / diagnostic tokens that Terraria's vanilla KeyConfiguration cannot match
		/// (<c>Keys.ToString()</c> names only). Delegates to <see cref="PlainSingleTapPolicy"/>.
		/// </summary>
		public static bool IsNonVanillaConfigurationToken(string token)
			=> PlainSingleTapPolicy.IsNonVanillaConfigurationToken(token);

		public static bool IsKeyAliasPrefix(string token)
			=> !string.IsNullOrWhiteSpace(token)
				&& token.Trim().StartsWith(KeyPrefix, StringComparison.OrdinalIgnoreCase);

		public static string StripKeyAliasPrefix(string token)
		{
			if (string.IsNullOrWhiteSpace(token))
				return token;

			token = token.Trim();
			if (!token.StartsWith(KeyPrefix, StringComparison.OrdinalIgnoreCase))
				return token;

			return token.Substring(KeyPrefix.Length);
		}

		public static bool IsValidVirtualKey(uint vk)
			=> vk >= 0x01 && vk <= 0xFE && vk != 0xFF;

		public static string FormatVk(uint vk)
			=> VkPrefix + "0x" + vk.ToString("X2", CultureInfo.InvariantCulture);

		public static string FormatScan(ushort scan, bool extended)
			=> ScPrefix
				+ "0x" + (scan & 0xFF).ToString("X2", CultureInfo.InvariantCulture)
				+ ":" + (extended ? "E0" : "Normal");

		public static bool TryParseVk(string token, out uint vk)
		{
			vk = 0;
			if (string.IsNullOrWhiteSpace(token))
				return false;

			token = token.Trim();
			if (!token.StartsWith(VkPrefix, StringComparison.OrdinalIgnoreCase))
				return false;

			string hex = token.Substring(VkPrefix.Length).Trim();
			if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
				hex = hex.Substring(2);

			if (!uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint parsed))
				return false;

			if (!IsValidVirtualKey(parsed))
				return false;

			vk = parsed;
			return true;
		}

		public static bool TryParseScan(string token, out ushort scan, out bool extended)
		{
			scan = 0;
			extended = false;
			if (string.IsNullOrWhiteSpace(token))
				return false;

			token = token.Trim();
			if (!token.StartsWith(ScPrefix, StringComparison.OrdinalIgnoreCase))
				return false;

			string body = token.Substring(ScPrefix.Length);
			int colon = body.IndexOf(':');
			if (colon <= 0)
				return false;

			string scanHex = body.Substring(0, colon).Trim();
			if (scanHex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
				scanHex = scanHex.Substring(2);

			if (!uint.TryParse(scanHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint scanParsed)
				|| scanParsed == 0
				|| scanParsed > 0xFF)
				return false;

			string extTag = body.Substring(colon + 1).Trim();
			if (string.Equals(extTag, "E0", StringComparison.OrdinalIgnoreCase))
				extended = true;
			else if (string.Equals(extTag, "Normal", StringComparison.OrdinalIgnoreCase))
				extended = false;
			else
				return false;

			scan = (ushort)scanParsed;
			return true;
		}

		public static bool TryCanonicalize(string token, out string canonical)
		{
			canonical = null;
			if (string.IsNullOrWhiteSpace(token))
				return false;

			token = token.Trim();

			if (TryParseVk(token, out uint vk))
			{
				canonical = FormatVk(vk);
				return true;
			}

			if (TryParseScan(token, out ushort scan, out bool extended))
			{
				canonical = FormatScan(scan, extended);
				return true;
			}

			return false;
		}
	}
}
