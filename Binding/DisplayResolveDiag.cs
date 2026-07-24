using System;
using System.Text;
using AdvancedKeybinds.Core;
using Microsoft.Xna.Framework.Input;

namespace AdvancedKeybinds.Binding
{
	/// <summary>
	/// AKDIAG: which key was registered, what the UI shows, and how display was resolved.
	/// Helps trace odd glyphs (SC:/VK: fallbacks, layout glyphs, user aliases).
	/// </summary>
	internal static class DisplayResolveDiag
	{
		public static void LogCapture(string token)
		{
			Log("capture", token, null, null, null);
		}

		public static void LogSlotSet(string actionId, int slotIndex, string token)
		{
			Log("slot-set action=" + actionId + " slot=" + slotIndex, token, null, null, slotIndex);
		}

		public static void LogSlotClear(string actionId, int slotIndex, string previousToken)
		{
			if (!AkDiag.Enabled || string.IsNullOrEmpty(previousToken))
				return;

			Log("slot-clear action=" + actionId + " slot=" + slotIndex, previousToken, null, null, slotIndex);
		}

		public static void LogBindingSnapshot(string context, string actionId, ComboBinding binding)
		{
			if (!AkDiag.Enabled || binding == null)
				return;

			for (int i = 1; i <= 3; i++)
			{
				string token = binding.GetSlot(i);
				if (string.IsNullOrEmpty(token))
					continue;

				string slotOverride = binding.GetSlotDisplay(i);
				Log(context + " action=" + actionId + " slot=" + i, token, slotOverride, binding, i);
			}

			string joined = binding.FormatJoinedLabel(compact: true);
			if (!string.IsNullOrEmpty(joined))
			{
				if (AkDiag.Enabled)
				{
					AkDiag.Log(
					"DisplayResolve " + context
					+ " action=" + actionId
					+ " comboLabel=" + FormatShown(joined)
					+ " " + KeyboardLayoutHelper.FormatCaptureLayoutDiag());
				}
			}
		}

		public static void LogAliasChange(string storageKey, string previousDisplay, string newDisplay)
		{
			if (!AkDiag.Enabled)
				return;

			if (AkDiag.Enabled)
			{
				AkDiag.Log(
				"DisplayResolve alias-change key=" + storageKey
				+ " was=" + FormatShown(previousDisplay)
				+ " now=" + FormatShown(newDisplay));
			}

			Log("alias-target", storageKey, newDisplay, null, null);
		}

		public static void LogAliasesLoaded(int count, System.Collections.Generic.IReadOnlyDictionary<string, string> aliases)
		{
			if (!AkDiag.Enabled)
				return;

			if (count == 0)
			{
				if (AkDiag.Enabled) AkDiag.Log("DisplayResolve aliases-loaded count=0");
				return;
			}

			var sb = new StringBuilder();
			sb.Append("DisplayResolve aliases-loaded count=").Append(count).Append(" entries=[");
			bool first = true;
			foreach (var pair in aliases)
			{
				if (!first)
					sb.Append(", ");
				first = false;
				sb.Append(pair.Key).Append('→').Append(FormatShown(pair.Value));
				if (IsSuspiciousDisplay(pair.Value))
					sb.Append("(suspicious)");
			}

			sb.Append(']');
			if (AkDiag.Enabled) AkDiag.Log(sb.ToString());
		}

		private static void Log(
			string context,
			string token,
			string slotDisplayOverride,
			ComboBinding binding,
			int? slotIndex)
		{
			if (!AkDiag.Enabled || string.IsNullOrWhiteSpace(token))
				return;

			DisplayTrace trace = BuildTrace(token, slotDisplayOverride, binding, slotIndex);
			if (AkDiag.Enabled) AkDiag.Log("DisplayResolve " + context + " " + trace.FormatLine());
		}

		private static DisplayTrace BuildTrace(
			string token,
			string slotDisplayOverride,
			ComboBinding binding,
			int? slotIndex)
		{
			string slotOverride = slotDisplayOverride;
			if (slotIndex is >= 1 and <= 3 && binding != null && string.IsNullOrEmpty(slotOverride))
				slotOverride = binding.GetSlotDisplay(slotIndex.Value);

			string shown = KeyToken.FormatDisplayForToken(token, slotOverride);
			string source = ResolveSource(token, slotOverride, out string jisCap, out string winGlyph);

			DescribeIdentity(token, out string keyKind, out string keysName, out string vk, out string scan, out bool extended);

			string comboPart = string.Empty;
			if (slotIndex is >= 1 and <= 3 && binding != null)
			{
				string label = binding.FormatSlotLabel(slotIndex.Value);
				if (!string.IsNullOrEmpty(label))
					comboPart = " slotLabel=" + FormatShown(label);
			}

			string alias = KeyDisplayAliasStorage.GetDisplay(token);
			string aliasPart = string.IsNullOrEmpty(alias) ? " alias=-" : " alias=" + FormatShown(alias);

			string suspicious = IsSuspiciousDisplay(shown) ? ClassifySuspicious(shown, source) : "none";

			return new DisplayTrace(
				token,
				keyKind,
				keysName,
				vk,
				scan,
				extended,
				shown,
				source,
				jisCap,
				winGlyph,
				aliasPart,
				comboPart,
				suspicious);
		}

		private static string ResolveSource(
			string token,
			string slotDisplayOverride,
			out string jisCap,
			out string winGlyph)
		{
			jisCap = "-";
			winGlyph = "-";

			if (!string.IsNullOrWhiteSpace(slotDisplayOverride))
				return "slot-override";

			string alias = KeyDisplayAliasStorage.GetDisplay(token);
			if (!string.IsNullOrEmpty(alias))
				return "user-alias";

			if (StableKeyToken.IsHankakuZenkakuToken(token))
			{
				if (JapaneseJisKeyDisplayMap.TryGetDisplay(token, out string hz))
				{
					jisCap = hz;
					return "jis-map";
				}

				return "stable-token";
			}

			if (StableKeyToken.IsMouseWheelToken(token))
				return "mouse-wheel";

			if (JapaneseJisKeyDisplayMap.TryGetDisplay(token, out string jisDisplay))
			{
				jisCap = jisDisplay;
				return "jis-map";
			}

			if (PhysicalKeyToken.TryParseVk(token, out uint vk))
			{
				if (JapaneseJisKeyDisplayMap.IsActive()
					&& JapaneseJisKeyDisplayMap.TryGetDisplay(PhysicalKeyToken.FormatVk(vk), out string jisVk))
				{
					jisCap = jisVk;
					return "jis-map";
				}

				if (OperatingSystem.IsWindows()
					&& KeyboardLayoutHelper.TryGetUnshiftedVkGlyph(vk, out string glyph))
				{
					winGlyph = glyph;
					return "windows-glyph";
				}
			}

			if (PhysicalKeyToken.TryParseScan(token, out ushort scan, out bool extended))
			{
				if (JapaneseJisKeyDisplayMap.TryGetScanDisplay(scan, extended, out string jisScan))
				{
					jisCap = jisScan;
					return "jis-map";
				}

				if (OperatingSystem.IsWindows()
					&& KeyboardLayoutHelper.TryGetUnshiftedScanGlyph(scan, extended, out string glyph))
				{
					winGlyph = glyph;
					return "windows-glyph";
				}
			}

			if (PhysicalKeyDisplayResolver.TryGetDisplay(token, out _))
				return "physical-resolver";

			if (PhysicalKeyToken.TryParseVk(token, out _))
				return "vk-fallback";

			if (PhysicalKeyToken.TryParseScan(token, out _, out _))
				return "sc-fallback";

			if (KeyToken.TryParse(token, out Keys key, out bool isMouse, out _))
			{
				if (isMouse)
					return "mouse-token";

				if (KeyLayoutDisplayResolver.TryGetLayoutLegend(key, out _))
					return "layout-legend";

				return "keys-token";
			}

			return "raw-token";
		}

		private static void DescribeIdentity(
			string token,
			out string keyKind,
			out string keysName,
			out string vk,
			out string scan,
			out bool extended)
		{
			keysName = "-";
			vk = "-";
			scan = "-";
			extended = false;

			if (PhysicalKeyToken.TryParseVk(token, out uint vkCode))
			{
				keyKind = "VK";
				vk = "0x" + vkCode.ToString("X2");
				if (Enum.IsDefined(typeof(Keys), (int)vkCode))
					keysName = ((Keys)vkCode).ToString();

				if (OperatingSystem.IsWindows()
					&& KeyboardLayoutHelper.TryGetVkScanEx(vkCode, KeyboardLayoutHelper.GetActiveLayoutHandle(),
						out uint sc, out bool ext))
				{
					scan = "0x" + sc.ToString("X2");
					extended = ext;
				}

				return;
			}

			if (PhysicalKeyToken.TryParseScan(token, out ushort scanCode, out bool extFlag))
			{
				keyKind = "SC";
				scan = "0x" + scanCode.ToString("X2");
				extended = extFlag;

				if (OperatingSystem.IsWindows()
					&& KeyboardLayoutHelper.TryGetVkFromScanEx(scanCode, extFlag,
						KeyboardLayoutHelper.GetActiveLayoutHandle(), out uint vkFromScan)
					&& vkFromScan != 0)
				{
					vk = "0x" + vkFromScan.ToString("X2");
					if (Enum.IsDefined(typeof(Keys), (int)vkFromScan))
						keysName = ((Keys)vkFromScan).ToString();
				}

				return;
			}

			if (KeyToken.TryParse(token, out Keys key, out bool isMouse, out int mouseButton))
			{
				if (isMouse)
				{
					keyKind = "Mouse";
					keysName = "btn" + mouseButton;
					return;
				}

				keyKind = "Keys";
				keysName = key.ToString();
				vk = "0x" + ((uint)key).ToString("X2");
				if (OperatingSystem.IsWindows()
					&& KeyboardLayoutHelper.TryGetVkScanEx((uint)key, KeyboardLayoutHelper.GetActiveLayoutHandle(),
						out uint sc, out bool ext))
				{
					scan = "0x" + sc.ToString("X2");
					extended = ext;
				}

				return;
			}

			if (StableKeyToken.IsHankakuZenkakuToken(token))
			{
				keyKind = "JIS-special";
				keysName = token;
				return;
			}

			keyKind = "token";
			keysName = token;
		}

		internal static string FormatShown(string display)
		{
			if (display == null)
				return "(null)";

			if (display.Length == 0)
				return "(empty)";

			return EscapeDisplay(display) + " len=" + display.Length;
		}

		internal static string EscapeDisplay(string display)
		{
			if (display == null)
				return "(null)";

			if (display.Length == 0)
				return "\"\"";

			var visible = new StringBuilder();
			visible.Append('"');
			for (int i = 0; i < display.Length; i++)
			{
				char ch = display[i];
				if (ch == '\\')
					visible.Append("\\\\");
				else if (ch == '"')
					visible.Append("\\\"");
				else if (char.IsControl(ch))
					visible.Append("\\u").Append(((int)ch).ToString("X4"));
				else
					visible.Append(ch);
			}

			visible.Append('"');

			var codes = new StringBuilder();
			codes.Append(" cp=[");
			for (int i = 0; i < display.Length; i++)
			{
				if (i > 0)
					codes.Append(' ');
				codes.Append("U+").Append(((int)display[i]).ToString("X4"));
			}

			codes.Append(']');
			return visible.ToString() + codes.ToString();
		}

		private static bool IsSuspiciousDisplay(string display)
		{
			if (string.IsNullOrEmpty(display))
				return true;

			if (display.StartsWith("SC:", StringComparison.OrdinalIgnoreCase)
				|| display.StartsWith("VK:", StringComparison.OrdinalIgnoreCase))
				return true;

			if (display.Length == 1 && !char.IsLetterOrDigit(display[0]))
				return true;

			return false;
		}

		private static string ClassifySuspicious(string display, string source)
		{
			if (string.IsNullOrEmpty(display))
				return "empty-display";

			if (display.StartsWith("SC:", StringComparison.OrdinalIgnoreCase))
				return "sc-fallback-shown";

			if (display.StartsWith("VK:", StringComparison.OrdinalIgnoreCase))
				return "vk-fallback-shown";

			if (display.Length == 1)
				return "single-char";

			return "unusual";
		}

		private readonly struct DisplayTrace
		{
			private readonly string _token;
			private readonly string _keyKind;
			private readonly string _keysName;
			private readonly string _vk;
			private readonly string _scan;
			private readonly bool _extended;
			private readonly string _shown;
			private readonly string _source;
			private readonly string _jisCap;
			private readonly string _winGlyph;
			private readonly string _aliasPart;
			private readonly string _comboPart;
			private readonly string _suspicious;

			public DisplayTrace(
				string token,
				string keyKind,
				string keysName,
				string vk,
				string scan,
				bool extended,
				string shown,
				string source,
				string jisCap,
				string winGlyph,
				string aliasPart,
				string comboPart,
				string suspicious)
			{
				_token = token;
				_keyKind = keyKind;
				_keysName = keysName;
				_vk = vk;
				_scan = scan;
				_extended = extended;
				_shown = shown;
				_source = source;
				_jisCap = jisCap;
				_winGlyph = winGlyph;
				_aliasPart = aliasPart;
				_comboPart = comboPart;
				_suspicious = suspicious;
			}

			public string FormatLine()
			{
				return "token=" + _token
					+ " keyKind=" + _keyKind
					+ " keys=" + _keysName
					+ " vk=" + _vk
					+ " scan=" + _scan
					+ " ext=" + _extended
					+ " shown=" + FormatShown(_shown)
					+ " source=" + _source
					+ " jisCap=" + (_jisCap == "-" ? "-" : FormatShown(_jisCap))
					+ " winGlyph=" + (_winGlyph == "-" ? "-" : FormatShown(_winGlyph))
					+ _aliasPart
					+ _comboPart
					+ " suspicious=" + _suspicious
					+ " " + KeyboardLayoutHelper.FormatCaptureLayoutDiag();
			}
		}
	}
}
