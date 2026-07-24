using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using AdvancedKeybinds.Core;
using AdvancedKeybinds.Patches;
using Microsoft.Xna.Framework.Input;
using Terraria;
using Terraria.Localization;

namespace AdvancedKeybinds.Binding
{
	public static class KeyToken
	{
		/// <summary>
		/// XNA <see cref="Keys"/> OEM values match Win32 VK_OEM_*. FNA maps SDL keycodes, so JIS
		/// <c>@</c>/<c>^</c>/<c>ﾂ･</c> (SDLK_AT / CARET / yen) never appear in <see cref="Main.keyState"/>.
		/// </summary>
		private static readonly Keys[] WindowsOemKeys = {
			Keys.OemSemicolon,
			Keys.OemPlus,
			Keys.OemComma,
			Keys.OemMinus,
			Keys.OemPeriod,
			Keys.OemQuestion,
			Keys.OemTilde,
			Keys.OemOpenBrackets,
			Keys.OemPipe,
			Keys.OemCloseBrackets,
			Keys.OemQuotes,
			Keys.OemBackslash,
			// VK_OEM_8 (0xDF): French AZERTY !/? slash scan — SDL/FNA often missing (SDL_SCANCODE_SLASH).
			Keys.Oem8
		};

		/// <summary>
		/// JP IME-adjacent keys next to Space. FNA/SDL rarely reports them in <see cref="Main.keyState"/>;
		/// numeric values match Win32 <c>VK_NONCONVERT</c> / <c>VK_CONVERT</c> / <c>VK_KANA</c>.
		/// </summary>
		private static readonly Keys[] WindowsImeNeighborKeys = {
			Keys.ImeNoConvert,
			Keys.ImeConvert,
			Keys.Kana
		};

		/// <summary>
		/// Same physical JP 縲後き繧ｿ繧ｫ繝奇ｼ上・繧峨′縺ｪ縲・key as <see cref="Keys.Kana"/> (<c>VK_KANA</c> = 0x15).
		/// kbd106 maps scan 0x70 竊・<c>VK_OEM_COPY</c> (0xF2); US layouts do not emit these VKs.
		/// Note: NLS toggle often never sticks in GetAsyncKeyState 窶・slot capture also latches KEYDOWN
		/// (see <see cref="WindowsKeySuppressor.QueueKanaEdge"/>).
		/// </summary>
		private static readonly uint[] KanaAliasVirtualKeys = {
			0xF2 // VK_OEM_COPY
		};

		private static readonly HashSet<string> PrevWin32OnlySupplementTokens =
			new(StringComparer.OrdinalIgnoreCase);

		public static bool TryParse(string token, out Keys key, out bool isMouse, out int mouseButton)
		{
			key = Keys.None;
			isMouse = false;
			mouseButton = -1;

			if (string.IsNullOrWhiteSpace(token))
				return false;

			// Display labels (LeftClick / L-Ctrl / MB4 / 窶ｦ) are never valid save/parse tokens.
			if (StableKeyToken.IsDisplayAlias(token))
				return false;

			if (StableKeyToken.TryParse(token, out isMouse, out mouseButton, out string canonical))
			{
				if (isMouse)
					return true;

				// Custom stable ids that are not XNA Keys names.
				if (StableKeyToken.IsHankakuZenkakuToken(canonical)
					|| string.Equals(canonical, StableKeyToken.ScrollLockToken, StringComparison.OrdinalIgnoreCase)
					|| StableKeyToken.IsMouseWheelToken(canonical))
					return false;

				return Enum.TryParse(canonical, true, out key) && key != Keys.None;
			}

			// Forward-compat: accept a runtime Keys enum name not yet listed in StableKeyToken.
			if (Enum.TryParse(token.Trim(), true, out key) && key != Keys.None)
				return true;

			return false;
		}

		/// <summary>Win32 <c>VK_OEM_3</c> 窶・real JP <c>@</c> / US grave when the VK is actually down.</summary>
		private const uint VkOemTilde = 0xC0;
		private static long _lastRealOemTildeActivityMs;

		public static bool IsRealOemTildeVirtualKeyDown()
			=> CanPollWin32KeySupplement()
				&& WindowsKeySuppressor.IsVirtualKeyPhysicallyDown(VkOemTilde);

		/// <summary>
		/// Call once per capture poll. Real JP @ (VK_OEM_3) release can leave FNA OemTilde sticky;
		/// recent activity suppresses hankaku masquerade on that frame.
		/// </summary>
		public static void PollRealOemTildeActivity()
		{
			if (IsRealOemTildeVirtualKeyDown())
				_lastRealOemTildeActivityMs = Environment.TickCount64;
		}

		public static bool WasRecentRealOemTilde(int withinMs = 300)
		{
			if (_lastRealOemTildeActivityMs == 0)
				return false;
			long age = Environment.TickCount64 - _lastRealOemTildeActivityMs;
			return age >= 0 && age <= withinMs;
		}

		/// <summary>
		/// On JP hankaku context, FNA maps 縲悟濠隗抵ｼ丞・隗偵・(grave scancode) to <see cref="Keys.OemTilde"/>.
		/// That must never be stored as OemTilde 窶・only real <c>VK_OEM_3</c> may.
		/// </summary>
		public static string NormalizeCapturedToken(string token)
		{
			if (string.IsNullOrEmpty(token))
				return token;

			if (PhysicalKeyToken.IsFallbackToken(token))
				return StableKeyToken.CanonicalizeOrNull(token) ?? token;

			token = PhysicalKeyResolver.NormalizeWin32OnlyKeysCapture(token);
			if (PhysicalKeyToken.IsFallbackToken(token))
				return token;

			// kbd106 縲後き繧ｿ繧ｫ繝奇ｼ上・繧峨′縺ｪ縲・may surface as OemCopy (VK_OEM_COPY) 窶・always bind as Kana.
			if (string.Equals(token, "OemCopy", StringComparison.OrdinalIgnoreCase))
			{
				if (AkDiag.Enabled) AkDiag.Log("NormalizeCapturedToken OemCopy->Kana");
				return nameof(Keys.Kana);
			}

			// Rare VK forms of 縲悟・隗・蜊願ｧ偵・when not already latched as HankakuZenkaku.
			// Do not map Kanji 窶・on Korean boards that is Hangul/Hanja.
			if (string.Equals(token, "OemAuto", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(token, "OemEnlW", StringComparison.OrdinalIgnoreCase))
			{
				if (AkDiag.Enabled) AkDiag.Log("NormalizeCapturedToken " + token + "->" + BindingRestrictions.HankakuZenkakuToken);
				return BindingRestrictions.HankakuZenkakuToken;
			}

			if (!string.Equals(token, "OemTilde", StringComparison.OrdinalIgnoreCase))
				return token;

			if (!KeyboardLayoutHelper.IsJapaneseHankakuKeyContext())
				return token;

			if (IsRealOemTildeVirtualKeyDown() || WasRecentRealOemTilde())
				return token;

			if (AkDiag.Enabled)
			{
				AkDiag.Log(
				"NormalizeCapturedToken OemTilde->" + BindingRestrictions.HankakuZenkakuToken
				+ " (FNA masquerade; no VK_OEM_3)");
			}
			return BindingRestrictions.HankakuZenkakuToken;
		}

		public static bool IsDown(string token)
		{
			if (PhysicalKeyToken.TryParseVk(token, out uint vk))
			{
				if (!CanPollWin32KeySupplement())
					return false;

				// Prefer hook make/break — AsyncKeyState alone is unreliable for NLS/OEM.
				if (WindowsKeySuppressor.IsFallbackVkHeld(vk))
					return true;

				return WindowsKeySuppressor.IsVirtualKeyPhysicallyDown(vk);
			}

			if (PhysicalKeyToken.TryParseScan(token, out _, out _))
				return WindowsKeySuppressor.IsGenericScanTokenDown(token);

			if (StableKeyToken.IsMouseWheelToken(token))
				return false;

			if (string.Equals(token, BindingRestrictions.ScrollLockToken, StringComparison.OrdinalIgnoreCase))
				return WindowsKeySuppressor.IsScrollLockPhysicallyDown;

			if (BindingRestrictions.IsHankakuZenkakuToken(token))
				return WindowsKeySuppressor.IsHankakuZenkakuPhysicallyDown;

			if (!TryParse(token, out Keys key, out bool isMouse, out int mouseButton))
				return false;

			if (isMouse)
				return IsMouseDown(mouseButton);

			if (key == Keys.LeftWindows)
				return WindowsKeySuppressor.IsLeftWindowsDown;

			if (key == Keys.RightWindows)
				return WindowsKeySuppressor.IsRightWindowsDown;

			if (key == Keys.CapsLock)
				return WindowsKeySuppressor.IsCapsLockPhysicallyDown;

			if (key == Keys.NumLock)
				return WindowsKeySuppressor.IsNumLockPhysicallyDown;

			// Pause/Break share make-code 0x45 with NumLock; VK_PAUSE / VK_CANCEL (Break) 窶・never NumLock.
			if (key == Keys.Pause)
				return Main.keyState.IsKeyDown(Keys.Pause) || WindowsKeySuppressor.IsPausePhysicallyDown;

			if (key == Keys.Escape)
				return Main.keyState.IsKeyDown(key) || WindowsKeySuppressor.IsShellBlockedEscapeDown;

			if (key == Keys.Kana)
				return IsKanaPhysicallyDown();

			if (IsWindowsOemKey(key) || IsWindowsImeNeighborKey(key))
			{
				// JP: never treat FNA OemTilde (hankaku grave scancode) as held OemTilde / @.
				if (key == Keys.OemTilde && KeyboardLayoutHelper.IsJapaneseHankakuKeyContext())
				{
					return Main.hasFocus
						&& WindowsKeySuppressor.IsVirtualKeyPhysicallyDown(VkOemTilde);
				}

				if (Main.keyState.IsKeyDown(key))
					return true;
				if (!Main.hasFocus)
					return false;
				if (WindowsKeySuppressor.IsFallbackVkHeld((uint)key)
					|| WindowsKeySuppressor.IsVirtualKeyPhysicallyDown((uint)key))
					return true;
				return WindowsKeySuppressor.IsJisIntlScanTokenDown(key.ToString());
			}

			// Browser / Media / PrintScreen / Kanji(Hanja) / etc.: FNA often misses these.
			if (Main.keyState.IsKeyDown(key))
				return true;
			if (!CanPollWin32KeySupplement())
				return false;
			if (WindowsKeySuppressor.IsFallbackVkHeld((uint)key))
				return true;
			return WindowsKeySuppressor.IsVirtualKeyPhysicallyDown((uint)key);
		}

		public static bool IsMouseDown(int mouseButton)
		{
			return mouseButton switch {
				1 => MouseInputHelper.IsPhysicalLeftDown(),
				2 => MouseInputHelper.IsPhysicalRightDown(),
				3 => MouseInputHelper.IsPhysicalMiddleDown(),
				4 => MouseInputHelper.IsMouseX1Down(),
				5 => MouseInputHelper.IsMouseX2Down(),
				_ => false
			};
		}

		/// <summary>
		/// UI display for a token: global key alias (Option A), optional per-slot override, then <see cref="FormatToken"/>.
		/// </summary>
		public static string FormatDisplayForToken(string token, string slotDisplayOverride = null)
		{
			if (string.IsNullOrWhiteSpace(token))
				return string.Empty;

			string global = KeyDisplayAliasStorage.GetDisplay(token);
			if (!string.IsNullOrEmpty(global))
				return global;

			if (!string.IsNullOrWhiteSpace(slotDisplayOverride))
				return slotDisplayOverride.Trim();

			return FormatToken(token);
		}

		/// <summary>
		/// UI display only. Persist/capture tokens are unchanged.
		/// Priority: layout map → Windows glyph → locale label → internal name → SC/VK id.
		/// </summary>
		public static string FormatToken(string token)
		{
			if (string.IsNullOrWhiteSpace(token))
				return string.Empty;

			if (StableKeyToken.IsHankakuZenkakuToken(token))
			{
				if (JapaneseJisKeyDisplayMap.TryGetDisplay(token, out string hzDisplay))
					return hzDisplay;
				return StableKeyToken.FormatForDisplay(token);
			}

			if (StableKeyToken.IsMouseWheelToken(token))
			{
				string locKey = StableKeyToken.IsMouseWheelUpToken(token)
					? "Mods.AdvancedKeybinds.UI.Combo.MouseWheelUp"
					: "Mods.AdvancedKeybinds.UI.Combo.MouseWheelDown";
				string loc = Language.GetTextValue(locKey);
				if (!string.IsNullOrEmpty(loc)
					&& !string.Equals(loc, locKey, StringComparison.Ordinal))
					return loc;

				return StableKeyToken.FormatForDisplay(token);
			}

			if (JapaneseJisKeyDisplayMap.TryGetDisplay(token, out string jisDisplay))
				return jisDisplay;

			if (PhysicalKeyDisplayResolver.TryGetDisplay(token, out string physicalDisplay))
				return physicalDisplay;

			if (PhysicalKeyToken.TryParseVk(token, out uint vk))
				return PhysicalKeyToken.FormatVk(vk);

			if (PhysicalKeyToken.TryParseScan(token, out ushort scan, out bool extended))
				return PhysicalKeyToken.FormatScan(scan, extended);

			if (!TryParse(token, out Keys key, out bool isMouse, out _))
				return token;

			if (isMouse)
				return StableKeyToken.FormatForDisplay(token);

			if (TryGetFixedLocaleLabel(key, out string fixedLabel))
				return fixedLabel;

			if (IsInternalNameOnlyKeyForDisplay(key))
				return StableKeyToken.FormatForDisplay(token);

			if (KeyLayoutDisplayResolver.TryGetLayoutLegend(key, out string layoutLegend))
				return layoutLegend;

			return StableKeyToken.FormatForDisplay(token);
		}

		/// <summary>Developer detail: raw ids for diagnostic / detail panels (not slot labels).</summary>
		public static string FormatDeveloperDetail(
			string token,
			string monoGameKey = null,
			uint? virtualKey = null,
			ushort? scanCode = null,
			bool? extended = null)
		{
			if (string.IsNullOrWhiteSpace(token))
				return string.Empty;

			string mono = string.IsNullOrEmpty(monoGameKey) ? "-" : monoGameKey;
			string vk = virtualKey.HasValue ? "0x" + virtualKey.Value.ToString("X2") : "-";
			string scan = scanCode.HasValue ? "0x" + (scanCode.Value & 0xFF).ToString("X2") : "-";
			string ext = extended.HasValue ? extended.Value.ToString() : "-";
			return "MonoGame=" + mono
				+ " VK=" + vk
				+ " Scan=" + scan
				+ " Extended=" + ext
				+ " PhysicalKeyId=" + token.Trim();
		}

		internal static bool IsInternalNameOnlyKeyForDisplay(Keys key)
			=> IsInternalNameOnlyKey(key);

		private static bool TryGetFixedLocaleLabel(Keys key, out string label)
		{
			label = null;
			switch (key)
			{
				case Keys.Kana:
					if (KeyboardLayoutHelper.IsKoreanKeyboardLayout())
						label = "Hangul";
					else if (KeyboardLayoutHelper.IsJapaneseHankakuKeyContext())
						label = "カタカナ/ひらがな";
					else
						label = "Kana";
					return true;
				case Keys.Kanji:
					if (KeyboardLayoutHelper.IsKoreanKeyboardLayout())
						label = "Hanja";
					else
						label = "Kanji";
					return true;
				case Keys.ImeConvert:
					label = KeyboardLayoutHelper.IsJapaneseHankakuKeyContext() ? "変換" : "ImeConvert";
					return true;
				case Keys.ImeNoConvert:
					label = KeyboardLayoutHelper.IsJapaneseHankakuKeyContext() ? "無変換" : "ImeNoConvert";
					return true;
				default:
					return false;
			}
		}

		private static bool IsInternalNameOnlyKey(Keys key)
		{
			if (key >= Keys.F1 && key <= Keys.F24)
				return true;
			if (key >= Keys.NumPad0 && key <= Keys.NumPad9)
				return true;

			return key switch {
				Keys.Tab or Keys.Enter or Keys.Back or Keys.Escape or Keys.Space => true,
				Keys.CapsLock or Keys.NumLock or Keys.Scroll => true,
				Keys.Pause or Keys.PrintScreen or Keys.Print or Keys.Execute => true,
				Keys.Insert or Keys.Delete or Keys.Home or Keys.End or Keys.PageUp or Keys.PageDown => true,
				Keys.Left or Keys.Up or Keys.Right or Keys.Down => true,
				Keys.LeftShift or Keys.RightShift or Keys.LeftControl or Keys.RightControl => true,
				Keys.LeftAlt or Keys.RightAlt or Keys.LeftWindows or Keys.RightWindows => true,
				Keys.Apps or Keys.Sleep or Keys.Select or Keys.Help => true,
				Keys.Multiply or Keys.Add or Keys.Separator or Keys.Subtract or Keys.Decimal or Keys.Divide => true,
				Keys.BrowserBack or Keys.BrowserForward or Keys.BrowserRefresh or Keys.BrowserStop => true,
				Keys.BrowserSearch or Keys.BrowserFavorites or Keys.BrowserHome => true,
				Keys.VolumeMute or Keys.VolumeDown or Keys.VolumeUp => true,
				Keys.MediaNextTrack or Keys.MediaPreviousTrack or Keys.MediaStop or Keys.MediaPlayPause => true,
				Keys.LaunchMail or Keys.SelectMedia or Keys.LaunchApplication1 or Keys.LaunchApplication2 => true,
				Keys.ProcessKey or Keys.OemClear => true,
				Keys.OemCopy or Keys.OemAuto or Keys.OemEnlW => true,
				Keys.Attn or Keys.Crsel or Keys.Exsel or Keys.EraseEof or Keys.Play or Keys.Zoom => true,
				Keys.Pa1 or Keys.ChatPadGreen or Keys.ChatPadOrange => true,
				_ => false
			};
		}

		public static string FormatSet(IReadOnlyList<string> keys, bool compact)
		{
			if (keys == null || keys.Count == 0)
				return string.Empty;

			string separator = compact ? "+" : " + ";
			return string.Join(separator, keys.Select(KeyToken.FormatToken));
		}

		public static HashSet<string> SnapshotPressedTokens()
		{
			HashSet<string> pressed = new(StringComparer.OrdinalIgnoreCase);
			FillSnapshotPressedTokens(pressed, includeUnconfirmedScanHeld: true);
			return pressed;
		}

		/// <summary>
		/// Held tokens corroborated by FNA / Win32 VK / mouse (no scan-flag-only ghosts).
		/// Used by capture recovery — acceptance still uses the full snapshot + edges.
		/// </summary>
		public static HashSet<string> SnapshotConfirmedPressedTokens()
		{
			HashSet<string> pressed = new(StringComparer.OrdinalIgnoreCase);
			FillSnapshotPressedTokens(pressed, includeUnconfirmedScanHeld: false);
			return pressed;
		}

		/// <summary>
		/// Clears <paramref name="destination"/> and fills held tokens (no new HashSet allocation).
		/// </summary>
		public static void FillSnapshotPressedTokens(HashSet<string> destination, bool includeUnconfirmedScanHeld = true)
		{
			destination.Clear();
			FillSnapshotPressedTokensCore(destination, includeUnconfirmedScanHeld);
		}

		private static void FillSnapshotPressedTokensCore(HashSet<string> pressed, bool includeUnconfirmedScanHeld)
		{
			// Measured SC: tokens are the canonical held set for normal keys.
			if (OperatingSystem.IsWindows() && Main.hasFocus)
			{
				if (includeUnconfirmedScanHeld)
				{
					WindowsKeySuppressor.SupplementJisIntlScanTokens(pressed);
					WindowsKeySuppressor.SupplementGenericScanTokens(pressed);
				}

				// Fallback VK / Keys names only for special-handled keys (modifiers etc.),
				// or when a VK is held without a corresponding measured scan alias in pressed.
				WindowsKeySuppressor.SupplementSpecialOrOrphanFallbackTokens(pressed);
			}

			if (WindowsKeySuppressor.IsSlotCapturePassthrough)
			{
				if (WindowsKeySuppressor.IsToggleLockPhysicallyDownForCapture("CapsLock"))
					pressed.Add("CapsLock");
				if (WindowsKeySuppressor.IsToggleLockPhysicallyDownForCapture("NumLock"))
					pressed.Add("NumLock");
				if (WindowsKeySuppressor.IsToggleLockPhysicallyDownForCapture(BindingRestrictions.ScrollLockToken))
					pressed.Add(BindingRestrictions.ScrollLockToken);
			}

			if (WindowsKeySuppressor.IsLeftWindowsDown)
				pressed.Add("LeftWindows");
			if (WindowsKeySuppressor.IsRightWindowsDown)
				pressed.Add("RightWindows");

			if (WindowsKeySuppressor.IsShellBlockedEscapeDown)
				pressed.Add("Escape");

			if (WindowsKeySuppressor.IsPausePhysicallyDown)
				pressed.Add("Pause");

			if (WindowsKeySuppressor.IsKanaPhysicallyDown)
				pressed.Add(nameof(Keys.Kana));

			if (IsMouseDown(1)) pressed.Add("Mouse1");
			if (IsMouseDown(2)) pressed.Add("Mouse2");
			if (IsMouseDown(3)) pressed.Add("Mouse3");
			if (IsMouseDown(4)) pressed.Add("Mouse4");
			if (IsMouseDown(5)) pressed.Add("Mouse5");
		}

		public static bool IsMouseToken(string token)
			=> StableKeyToken.IsMouseToken(token);

		public static bool IsMouseButtonToken(string token)
			=> StableKeyToken.IsMouseButtonToken(token);

		public static bool IsMouseWheelToken(string token)
			=> StableKeyToken.IsMouseWheelToken(token);

		private static bool IsWindowsOemKey(Keys key)
		{
			for (int i = 0; i < WindowsOemKeys.Length; i++)
			{
				if (WindowsOemKeys[i] == key)
					return true;
			}

			return false;
		}

		private static bool IsWindowsImeNeighborKey(Keys key)
		{
			for (int i = 0; i < WindowsImeNeighborKeys.Length; i++)
			{
				if (WindowsImeNeighborKeys[i] == key)
					return true;
			}

			return false;
		}

		/// <summary>Win32 VK poll only while the game is focused (avoids Alt-Tab false downs).</summary>
		private static bool CanPollWin32KeySupplement()
			=> OperatingSystem.IsWindows() && Main.hasFocus;

		private static void SupplementWindowsOemKeys(HashSet<string> pressed)
		{
			if (!CanPollWin32KeySupplement())
				return;

			for (int i = 0; i < WindowsOemKeys.Length; i++)
			{
				Keys key = WindowsOemKeys[i];
				if (!WindowsKeySuppressor.IsVirtualKeyPhysicallyDown((uint)key))
					continue;

				// JP: only VK_OEM_3 may add OemTilde (already gated by GetAsyncKeyState above).
				pressed.Add(key.ToString());
			}
		}

		private static void SupplementWindowsImeNeighborKeys(HashSet<string> pressed)
		{
			if (!CanPollWin32KeySupplement())
				return;

			for (int i = 0; i < WindowsImeNeighborKeys.Length; i++)
			{
				Keys key = WindowsImeNeighborKeys[i];
				if (WindowsKeySuppressor.IsVirtualKeyPhysicallyDown((uint)key))
					pressed.Add(key.ToString());
			}

			// kbd106 縲後き繧ｿ繧ｫ繝奇ｼ上・繧峨′縺ｪ縲・is VK_OEM_COPY, not VK_KANA 窶・normalize to "Kana".
			if (IsAnyKanaAliasVirtualKeyDown())
				pressed.Add(nameof(Keys.Kana));
		}

		/// <summary>KeyboardState / VK_KANA / kbd106 VK_OEM_COPY aliases for the kana key.</summary>
		private static bool IsKanaPhysicallyDown()
		{
			if (Main.keyState.IsKeyDown(Keys.Kana))
				return true;

			if (!CanPollWin32KeySupplement())
				return false;

			if (WindowsKeySuppressor.IsVirtualKeyPhysicallyDown((uint)Keys.Kana))
				return true;

			return IsAnyKanaAliasVirtualKeyDown();
		}

		private static bool IsAnyKanaAliasVirtualKeyDown()
		{
			for (int i = 0; i < KanaAliasVirtualKeys.Length; i++)
			{
				if (WindowsKeySuppressor.IsVirtualKeyPhysicallyDown(KanaAliasVirtualKeys[i]))
					return true;
			}

			return false;
		}

		/// <summary>Clears Win32-only supplement edge tracking (call on capture begin).</summary>
		internal static void ResetWin32SupplementCaptureDiag()
		{
			PrevWin32OnlySupplementTokens.Clear();
		}

		/// <summary>True when <paramref name="vk"/> matches a defined XNA <see cref="Keys"/> value (VK-aligned).</summary>
		internal static bool TryGetKeysTokenForVirtualKey(uint vk, out string token)
		{
			token = null;
			if (vk == 0 || vk > 0xFF)
				return false;
			if (!Enum.IsDefined(typeof(Keys), (int)vk))
				return false;

			var key = (Keys)vk;
			if (key == Keys.None)
				return false;

			token = key.ToString();
			return true;
		}

		/// <summary>
		/// WndProc/WM_INPUT path: latch a Keys token when Win32 reports KEYDOWN but FNA KeyboardState does not.
		/// Registration uses the internal Keys id — never the ToUnicodeEx glyph.
		/// </summary>
		internal static bool TryQueueWin32KeysCaptureFromVk(uint vk, ushort scan, string source)
		{
			if (!SlotCapture.IsCapturing || !CanPollWin32KeySupplement())
				return false;

			bool extended = false;
			if (OperatingSystem.IsWindows())
				KeyboardLayoutHelper.TryGetVkScanEx(vk, KeyboardLayoutHelper.GetActiveLayoutHandle(), out _, out extended);

			PhysicalKeyResolver.ResolveResult resolved = PhysicalKeyResolver.ResolveForCapture(
				vk,
				scan,
				extended,
				Main.keyState);
			if (!resolved.Success)
				return false;

			WindowsKeySuppressor.QueuePhysicalCaptureEdge(resolved.Token, vk, scan, extended, source);
			return true;
		}

		private static void LogWin32SupplementEdge(string token, uint vk, string src, List<string> edges)
		{
			if (AkDiag.Enabled)
			{
				AkDiag.Log(
				"Win32Supplement edge token=" + token
				+ " vk=0x" + vk.ToString("X2")
				+ " src=" + src
				+ " " + KeyboardLayoutHelper.FormatPhysicalKeyCaptureDiag(vk, adoptedToken: token));
			}

			if (edges != null && !ContainsIgnoreCase(edges, token))
				edges.Add(token);
		}

		private static bool ContainsIgnoreCase(List<string> list, string token)
		{
			if (list == null || token == null)
				return false;
			for (int i = 0; i < list.Count; i++)
			{
				if (string.Equals(list[i], token, StringComparison.OrdinalIgnoreCase))
					return true;
			}

			return false;
		}

		/// <summary>
		/// Discarded under Microsoft keystroke-message-only policy (GetAsyncKeyState / FNA polls).
		/// Capture edges come from WM_KEY* via ApplyPhysicalKeyEvent.
		/// </summary>
		internal static void AppendWin32SupplementCaptureEdges(List<string> edges)
		{
			PrevWin32OnlySupplementTokens.Clear();
			if (Win32KeyboardAcquisitionPolicy.KeystrokeMessagesOnly)
				return;

			if (!SlotCapture.IsCapturing || !CanPollWin32KeySupplement())
				return;

			KeyboardState keyboard = Main.keyState;
			var current = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

			for (int i = 0; i < WindowsOemKeys.Length; i++)
			{
				Keys key = WindowsOemKeys[i];
				if (keyboard.IsKeyDown(key))
					continue;
				if (!WindowsKeySuppressor.IsVirtualKeyPhysicallyDown((uint)key)
					&& !WindowsKeySuppressor.IsJisIntlScanTokenDown(key.ToString()))
					continue;

				string token = key.ToString();
				PhysicalKeyResolver.ResolveResult resolved = PhysicalKeyResolver.ResolveForCapture(
					(uint)key,
					0,
					false,
					keyboard);
				if (resolved.Success && !string.Equals(resolved.Token, token, StringComparison.OrdinalIgnoreCase))
					token = resolved.Token;

				current.Add(token);
				if (PrevWin32OnlySupplementTokens.Contains(token))
					continue;

				string src = WindowsKeySuppressor.IsVirtualKeyPhysicallyDown((uint)key)
					? "oem"
					: "jisIntlScan";
				LogWin32SupplementEdge(token, (uint)key, src, edges);
			}

			for (int i = 0; i < WindowsImeNeighborKeys.Length; i++)
			{
				Keys key = WindowsImeNeighborKeys[i];
				if (key == Keys.Kana)
					continue;
				if (keyboard.IsKeyDown(key))
					continue;
				if (!WindowsKeySuppressor.IsVirtualKeyPhysicallyDown((uint)key)
					&& !WindowsKeySuppressor.IsJisIntlScanTokenDown(key.ToString()))
					continue;

				string token = key.ToString();
				PhysicalKeyResolver.ResolveResult resolved = PhysicalKeyResolver.ResolveForCapture(
					(uint)key,
					0,
					false,
					keyboard);
				if (resolved.Success && !string.Equals(resolved.Token, token, StringComparison.OrdinalIgnoreCase))
					token = resolved.Token;

				current.Add(token);
				if (PrevWin32OnlySupplementTokens.Contains(token))
					continue;

				string src = WindowsKeySuppressor.IsVirtualKeyPhysicallyDown((uint)key)
					? "ime"
					: "jisIntlScan";
				LogWin32SupplementEdge(token, (uint)key, src, edges);
			}

			if (!keyboard.IsKeyDown(Keys.Kana))
			{
				bool aliasDown = IsAnyKanaAliasVirtualKeyDown();
				bool kanaVkDown = WindowsKeySuppressor.IsVirtualKeyPhysicallyDown((uint)Keys.Kana);
				if (aliasDown || kanaVkDown)
				{
					const string token = nameof(Keys.Kana);
					current.Add(token);
					if (!PrevWin32OnlySupplementTokens.Contains(token))
					{
						string src = aliasDown ? "kanaAlias(0xF2)" : "ime";
						uint vk = aliasDown ? 0xF2u : (uint)Keys.Kana;
						LogWin32SupplementEdge(token, vk, src, edges);
					}
				}
			}

			PrevWin32OnlySupplementTokens.Clear();
			foreach (string token in current)
				PrevWin32OnlySupplementTokens.Add(token);
		}
	}
}
