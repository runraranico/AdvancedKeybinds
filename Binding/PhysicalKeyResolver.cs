using System;
using AdvancedKeybinds.Core;
using AdvancedKeybinds.Patches;
using Microsoft.Xna.Framework.Input;
using Terraria;

namespace AdvancedKeybinds.Binding
{
	/// <summary>
	/// Layout-neutral physical key identity for capture and the same tokens used at activation.
	/// Priority follows Microsoft Keyboard Input Overview: virtual-key (and Keys name when mapped),
	/// then scan+extended. Never uses ToUnicodeEx or FNA KeyboardState for acceptance.
	/// </summary>
	internal static class PhysicalKeyResolver
	{
		public readonly struct ResolveResult
		{
			public bool Success { get; init; }
			public string Token { get; init; }
			public string Tier { get; init; }
			public string FailReason { get; init; }
			public uint Vk { get; init; }
			public ushort Scan { get; init; }
			public bool Extended { get; init; }
			public Keys? MonoGameKey { get; init; }
			public bool FnaReportsKey { get; init; }
		}

		public static ResolveResult ResolveForCapture(
			uint vk,
			ushort scan,
			bool extended,
			KeyboardState fna,
			bool glyphOnly = false)
		{
			scan = (ushort)(scan & 0xFF);
			Keys? monoKey = null;
			bool fnaDown = false;

			if (vk != 0 && Enum.IsDefined(typeof(Keys), (int)vk))
			{
				monoKey = (Keys)vk;
				// FNA is not an acquisition source — only recorded for diagnostics.
				fnaDown = fna.IsKeyDown(monoKey.Value);
			}

			if (glyphOnly)
			{
				return new ResolveResult {
					Success = false,
					FailReason = "glyph-only-no-internal-key",
					Vk = vk,
					Scan = scan,
					Extended = extended,
					MonoGameKey = monoKey,
					FnaReportsKey = fnaDown
				};
			}

			// 1. Virtual-key → MonoGame Keys name when the VK is a defined Keys value
			//    (Microsoft: applications typically use virtual-key codes).
			if (monoKey.HasValue && monoKey.Value != Keys.None)
			{
				return new ResolveResult {
					Success = true,
					Token = monoKey.Value.ToString(),
					Tier = "Keys",
					Vk = vk,
					Scan = scan,
					Extended = extended,
					MonoGameKey = monoKey,
					FnaReportsKey = fnaDown
				};
			}

			// 2. Virtual-Key fallback token
			if (PhysicalKeyToken.IsValidVirtualKey(vk))
			{
				return new ResolveResult {
					Success = true,
					Token = PhysicalKeyToken.FormatVk(vk),
					Tier = "VK",
					Vk = vk,
					Scan = scan,
					Extended = extended,
					MonoGameKey = monoKey,
					FnaReportsKey = fnaDown
				};
			}

			// 3. Scan code + extended (layout-independent physical identity)
			if (scan != 0)
			{
				return new ResolveResult {
					Success = true,
					Token = PhysicalKeyToken.FormatScan(scan, extended),
					Tier = "SC",
					Vk = vk,
					Scan = scan,
					Extended = extended,
					MonoGameKey = monoKey,
					FnaReportsKey = fnaDown
				};
			}

			return new ResolveResult {
				Success = false,
				FailReason = "no-internal-key",
				Vk = vk,
				Scan = scan,
				Extended = extended,
				MonoGameKey = monoKey,
				FnaReportsKey = fnaDown
			};
		}

		/// <summary>
		/// After Win32-only Keys-name capture, upgrade to VK when make/break still holds the key.
		/// </summary>
		public static string NormalizeWin32OnlyKeysCapture(string token)
		{
			if (string.IsNullOrEmpty(token) || PhysicalKeyToken.IsFallbackToken(token))
				return token;

			if (!Enum.TryParse(token, true, out Keys key) || key == Keys.None)
				return token;

			if (!OperatingSystem.IsWindows())
				return token;

			uint vk = (uint)key;
			if (!PhysicalKeyToken.IsValidVirtualKey(vk))
				return token;

			if (!WindowsKeySuppressor.IsFallbackVkHeld(vk))
				return token;

			return PhysicalKeyToken.FormatVk(vk);
		}
	}
}
