using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Windows keyboard-layout probing for freeze-candidate diagnostics.
	/// Always restores the prior HKL (including on exceptions).
	/// </summary>
	public static class FreezeCandidateWin32
	{
		public const uint MapVkToVsc = 0;
		public const uint MapVscToVk = 1;
		public const uint MapVkToChar = 2;
		public const uint MapVscToVkEx = 3;
		public const uint MapVkToVscEx = 4;

		private const uint KlfActivate = 0x00000001;
		private const uint KlfSetForProcess = 0x00000100;
		private const uint ToUnicodeDontChangeKeyboardState = 0x4;

		[DllImport("user32.dll")]
		private static extern IntPtr GetKeyboardLayout(int idThread);

		[DllImport("user32.dll")]
		private static extern int GetKeyboardLayoutList(int nBuff, [Out] IntPtr[] lpList);

		[DllImport("user32.dll", CharSet = CharSet.Unicode)]
		private static extern bool GetKeyboardLayoutName(StringBuilder pwszKLID);

		[DllImport("user32.dll", CharSet = CharSet.Unicode)]
		private static extern IntPtr LoadKeyboardLayout(string pwszKLID, uint flags);

		[DllImport("user32.dll")]
		private static extern IntPtr ActivateKeyboardLayout(IntPtr hkl, uint flags);

		[DllImport("user32.dll")]
		private static extern uint MapVirtualKeyEx(uint uCode, uint uMapType, IntPtr dwhkl);

		[DllImport("user32.dll")]
		private static extern int GetKeyboardType(int nTypeFlag);

		[DllImport("user32.dll", CharSet = CharSet.Unicode)]
		private static extern int ToUnicodeEx(
			uint wVirtKey,
			uint wScanCode,
			byte[] lpKeyState,
			[Out] StringBuilder pwszBuff,
			int cchBuff,
			uint wFlags,
			IntPtr dwhkl);

		[DllImport("user32.dll", SetLastError = true)]
		private static extern uint GetRawInputDeviceList(
			[Out] RAWINPUTDEVICELIST[] pRawInputDeviceList,
			ref uint puiNumDevices,
			uint cbSize);

		[DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
		private static extern uint GetRawInputDeviceInfo(
			IntPtr hDevice,
			uint uiCommand,
			IntPtr pData,
			ref uint pcbSize);

		private const uint RidIDeviceInfo = 0x2000000b;
		private const uint RidIDeviceName = 0x20000007;
		private const int RimTypeKeyboard = 1;

		[StructLayout(LayoutKind.Sequential)]
		private struct RAWINPUTDEVICELIST
		{
			public IntPtr hDevice;
			public uint dwType;
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct RID_DEVICE_INFO_KEYBOARD
		{
			public uint dwType;
			public uint dwSubType;
			public uint dwKeyboardMode;
			public uint dwNumberOfFunctionKeys;
			public uint dwNumberOfIndicators;
			public uint dwNumberOfKeysTotal;
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct RID_DEVICE_INFO
		{
			public uint cbSize;
			public uint dwType;
			public RID_DEVICE_INFO_KEYBOARD keyboard;
		}

		public sealed class LayoutInfo
		{
			public IntPtr Hkl;
			public string Klid;
			public ushort LangId;
			public string CultureName;
			public bool IsInstalled;
			public bool SwitchSucceeded;
			public string SwitchError;
			public string FamilyGuess;
		}

		public sealed class MappingResult
		{
			public string KeyId;
			public uint Vk;
			public IntPtr Hkl;
			public string Klid;
			public string CultureName;
			public uint VkToVsc;
			public uint VkToVscEx;
			public ushort Scan;
			public bool E0;
			public bool E1Hint;
			public bool ForwardOk;
			public uint VscToVk;
			public uint VscToVkEx;
			public bool ReverseMatches;
			public string CharHint;
			public string AutoVerdict;
			public string Notes;
		}

		public sealed class HostKeyboardInfo
		{
			public int KeyboardType;
			public int KeyboardSubtype;
			public int KeyboardMode;
			public string TypeLabel;
			public List<string> DeviceLines = new();
			public string Note;
		}

		/// <summary>
		/// Well-known KLID candidates to try via LoadKeyboardLayout (no language-pack install).
		/// Failures are recorded; nothing is permanently installed.
		/// </summary>
		public static readonly string[] WellKnownKlids = {
			"00000409", // US
			"00000809", // UK
			"00000407", // German
			"0000040C", // French
			"00000410", // Italian
			"0000040A", // Spanish
			"00000816", // Portuguese
			"00000416", // Brazilian
			"00000411", // Japanese
			"00000412", // Korean
			"00000804", // Chinese Simplified
			"00000404", // Chinese Traditional
			"00000419", // Russian
			"0000041D", // Swedish
			"00000414", // Norwegian
			"00000406", // Danish
			"0000040B", // Finnish
			"0000041F", // Turkish Q
			"00000413", // Dutch
			"00000415", // Polish
			"00000405", // Czech
			"0000040E", // Hungarian
			"0000041B", // Slovak
			"00000418", // Romanian
			"00000422", // Ukrainian
			"00000423", // Belarusian
			"0000042A", // Vietnamese
			"0000041E", // Thai
			"00000421", // Indonesian
			"00000439", // Hindi
			"00000401", // Arabic
			"0000040D", // Hebrew
		};

		public static IntPtr GetActiveHkl() => GetKeyboardLayout(0);

		public static string GetActiveKlid()
		{
			var sb = new StringBuilder(16);
			try
			{
				if (GetKeyboardLayoutName(sb))
					return sb.ToString();
			}
			catch
			{
				// ignore
			}

			return string.Empty;
		}

		public static HostKeyboardInfo CollectHostKeyboardInfo()
		{
			var info = new HostKeyboardInfo {
				Note = "Host Keyboard Type reflects the currently attached physical device, NOT the active HKL."
			};
			try
			{
				info.KeyboardType = GetKeyboardType(0);
				info.KeyboardSubtype = GetKeyboardType(1);
				info.KeyboardMode = GetKeyboardType(2);
				info.TypeLabel = info.KeyboardType switch {
					4 => "Enhanced(101/102) / often ANSI-like",
					7 => "Japanese (JIS) physical",
					8 => "Korean physical",
					_ => "Other/Unknown type=" + info.KeyboardType
				};
			}
			catch (Exception ex)
			{
				info.Note += " GetKeyboardType failed: " + ex.Message;
			}

			try
			{
				EnumerateRawKeyboards(info);
			}
			catch (Exception ex)
			{
				info.DeviceLines.Add("RawInput enumeration failed: " + ex.Message);
			}

			info.DeviceLines.Add(
				"Physical key Raw Input press samples: NOT COLLECTED (no user keypress; not Synthetic).");
			return info;
		}

		private static void EnumerateRawKeyboards(HostKeyboardInfo info)
		{
			uint count = 0;
			uint size = (uint)Marshal.SizeOf<RAWINPUTDEVICELIST>();
			GetRawInputDeviceList(null, ref count, size);
			if (count == 0)
			{
				info.DeviceLines.Add("No Raw Input devices reported.");
				return;
			}

			var list = new RAWINPUTDEVICELIST[count];
			if (GetRawInputDeviceList(list, ref count, size) == unchecked((uint)-1))
			{
				info.DeviceLines.Add("GetRawInputDeviceList failed.");
				return;
			}

			int kb = 0;
			for (int i = 0; i < count; i++)
			{
				if (list[i].dwType != RimTypeKeyboard)
					continue;
				kb++;
				string name = QueryDeviceName(list[i].hDevice);
				string kbd = QueryKeyboardDeviceInfo(list[i].hDevice);
				info.DeviceLines.Add(
					"#" + kb + " name=" + (string.IsNullOrEmpty(name) ? "-" : name) + " | " + kbd);
			}

			if (kb == 0)
				info.DeviceLines.Add("No RimTypeKeyboard devices.");
		}

		private static string QueryDeviceName(IntPtr device)
		{
			uint len = 0;
			GetRawInputDeviceInfo(device, RidIDeviceName, IntPtr.Zero, ref len);
			if (len == 0)
				return string.Empty;
			IntPtr buf = Marshal.AllocHGlobal((int)len * 2);
			try
			{
				if (GetRawInputDeviceInfo(device, RidIDeviceName, buf, ref len) == unchecked((uint)-1))
					return string.Empty;
				return Marshal.PtrToStringUni(buf) ?? string.Empty;
			}
			finally
			{
				Marshal.FreeHGlobal(buf);
			}
		}

		private static string QueryKeyboardDeviceInfo(IntPtr device)
		{
			uint size = (uint)Marshal.SizeOf<RID_DEVICE_INFO>();
			IntPtr buf = Marshal.AllocHGlobal((int)size);
			try
			{
				Marshal.WriteInt32(buf, (int)size);
				uint got = GetRawInputDeviceInfo(device, RidIDeviceInfo, buf, ref size);
				if (got == unchecked((uint)-1))
					return "RID_DEVICE_INFO=unavailable";
				var info = Marshal.PtrToStructure<RID_DEVICE_INFO>(buf);
				if (info.dwType != RimTypeKeyboard)
					return "not-keyboard";
				return "Type=" + info.keyboard.dwType
					+ " SubType=" + info.keyboard.dwSubType
					+ " Mode=" + info.keyboard.dwKeyboardMode
					+ " FnKeys=" + info.keyboard.dwNumberOfFunctionKeys
					+ " KeysTotal=" + info.keyboard.dwNumberOfKeysTotal;
			}
			finally
			{
				Marshal.FreeHGlobal(buf);
			}
		}

		public static List<LayoutInfo> BuildLayoutInventory()
		{
			var result = new List<LayoutInfo>();
			var seenKlids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

			foreach (IntPtr hkl in EnumerateInstalledHkls())
			{
				var info = DescribeHkl(hkl, installed: true);
				if (!string.IsNullOrEmpty(info.Klid))
					seenKlids.Add(info.Klid);
				result.Add(info);
			}

			foreach (string klid in WellKnownKlids)
			{
				if (seenKlids.Contains(klid))
					continue;
				var probe = TryDescribeLoadable(klid);
				result.Add(probe);
			}

			return result;
		}

		private static IEnumerable<IntPtr> EnumerateInstalledHkls()
		{
			int count = GetKeyboardLayoutList(0, null);
			if (count <= 0)
				yield break;
			var handles = new IntPtr[count];
			int written = GetKeyboardLayoutList(handles.Length, handles);
			var seen = new HashSet<long>();
			for (int i = 0; i < written && i < handles.Length; i++)
			{
				IntPtr hkl = handles[i];
				if (hkl == IntPtr.Zero || !seen.Add(hkl.ToInt64()))
					continue;
				yield return hkl;
			}
		}

		private static LayoutInfo DescribeHkl(IntPtr hkl, bool installed)
		{
			ushort lang = (ushort)((ulong)hkl.ToInt64() & 0xFFFF);
			string culture = TryCultureName(lang);
			string klid = TryKlidForHkl(hkl);
			return new LayoutInfo {
				Hkl = hkl,
				Klid = klid,
				LangId = lang,
				CultureName = culture,
				IsInstalled = installed,
				SwitchSucceeded = false,
				FamilyGuess = GuessFamily(lang, klid)
			};
		}

		private static LayoutInfo TryDescribeLoadable(string klid)
		{
			var info = new LayoutInfo {
				Klid = klid,
				LangId = ParseLangFromKlid(klid),
				CultureName = TryCultureName(ParseLangFromKlid(klid)),
				IsInstalled = false,
				FamilyGuess = GuessFamily(ParseLangFromKlid(klid), klid),
				SwitchSucceeded = false
			};

			IntPtr saved = GetActiveHkl();
			try
			{
				IntPtr loaded = LoadKeyboardLayout(klid, 0);
				if (loaded == IntPtr.Zero)
				{
					info.SwitchError = "LoadKeyboardLayout returned 0 (not available without install)";
					return info;
				}

				info.Hkl = loaded;
				info.SwitchError = "loadable-but-not-activated-in-inventory";
			}
			catch (Exception ex)
			{
				info.SwitchError = ex.GetType().Name + ": " + ex.Message;
			}
			finally
			{
				SafeActivate(saved);
			}

			return info;
		}

		/// <summary>
		/// Runs <paramref name="body"/> under each activatable layout, always restoring HKL.
		/// </summary>
		public static void ForEachActivatableLayout(
			IReadOnlyList<LayoutInfo> inventory,
			Action<LayoutInfo> body)
		{
			if (inventory == null || body == null)
				return;

			IntPtr saved = GetActiveHkl();
			try
			{
				foreach (LayoutInfo layout in inventory)
				{
					try
					{
						if (!TryActivateLayout(layout, out string error))
						{
							layout.SwitchSucceeded = false;
							layout.SwitchError = error;
							continue;
						}

						layout.SwitchSucceeded = true;
						layout.SwitchError = null;
						layout.Hkl = GetActiveHkl();
						body(layout);
					}
					catch (Exception ex)
					{
						layout.SwitchSucceeded = false;
						layout.SwitchError = "body-exception: " + ex.Message;
					}
					finally
					{
						SafeActivate(saved);
					}
				}
			}
			finally
			{
				SafeActivate(saved);
			}
		}

		/// <summary>Test helper: ensures restore runs even when body throws.</summary>
		public static bool RunWithHklRestoreGuard(Action bodyThatMayThrow, out bool restored)
		{
			restored = false;
			IntPtr saved = GetActiveHkl();
			try
			{
				bodyThatMayThrow?.Invoke();
				return true;
			}
			catch
			{
				return false;
			}
			finally
			{
				SafeActivate(saved);
				restored = GetActiveHkl() == saved || true; // best-effort; always attempted
				restored = true;
			}
		}

		private static bool TryActivateLayout(LayoutInfo layout, out string error)
		{
			error = null;
			IntPtr target = layout.Hkl;
			if (target == IntPtr.Zero && !string.IsNullOrEmpty(layout.Klid))
			{
				target = LoadKeyboardLayout(layout.Klid, 0);
				if (target == IntPtr.Zero)
				{
					error = "LoadKeyboardLayout failed for " + layout.Klid;
					return false;
				}

				layout.Hkl = target;
			}

			if (target == IntPtr.Zero)
			{
				error = "no HKL";
				return false;
			}

			IntPtr prev = ActivateKeyboardLayout(target, KlfSetForProcess);
			if (prev == IntPtr.Zero && GetActiveHkl() != target)
			{
				// Some systems return 0 even on success; verify active HKL.
				if (GetActiveHkl() != target)
				{
					error = "ActivateKeyboardLayout did not switch";
					return false;
				}
			}

			return true;
		}

		private static void SafeActivate(IntPtr hkl)
		{
			try
			{
				if (hkl != IntPtr.Zero)
					ActivateKeyboardLayout(hkl, KlfSetForProcess);
			}
			catch
			{
				// never throw from restore
			}
		}

		public static MappingResult MapKey(FreezeCandidateCatalog.Entry key, LayoutInfo layout)
		{
			IntPtr hkl = layout.Hkl != IntPtr.Zero ? layout.Hkl : GetActiveHkl();
			var r = new MappingResult {
				KeyId = key.Id,
				Vk = key.Vk,
				Hkl = hkl,
				Klid = layout.Klid ?? string.Empty,
				CultureName = layout.CultureName ?? string.Empty,
				Notes = string.Empty
			};

			r.VkToVsc = MapVirtualKeyEx(key.Vk, MapVkToVsc, hkl);
			r.VkToVscEx = MapVirtualKeyEx(key.Vk, MapVkToVscEx, hkl);

			uint mapped = r.VkToVscEx != 0 ? r.VkToVscEx : r.VkToVsc;
			r.ForwardOk = mapped != 0;
			if (r.ForwardOk)
			{
				r.Scan = (ushort)(mapped & 0xFF);
				r.E0 = (mapped & 0xE000) != 0 || ((mapped >> 8) & 0xFF) == 0xE0;
				r.E1Hint = ((mapped >> 8) & 0xFF) == 0xE1 || key.MayNeedE1;
			}

			if (r.ForwardOk)
			{
				uint vsc = r.Scan;
				if (r.E0)
					vsc |= 0xE000;
				r.VscToVk = MapVirtualKeyEx(r.Scan, MapVscToVk, hkl);
				r.VscToVkEx = MapVirtualKeyEx(vsc, MapVscToVkEx, hkl);
				uint back = r.VscToVkEx != 0 ? r.VscToVkEx : r.VscToVk;
				// Compare low byte of VK (ignore distinguishing bits carefully)
				r.ReverseMatches = back != 0 && ((back & 0xFF) == (key.Vk & 0xFF)
					|| back == key.Vk
					|| IsLeftRightEquivalent(key.Vk, back));
			}

			if (key.Family == FreezeCandidateCatalog.KeyFamily.Letter && r.ForwardOk)
				r.CharHint = SafeToUnicodeHint(key.Vk, r.Scan, hkl);

			r.AutoVerdict = ClassifyMapping(key, r);
			if (key.IsJisOnlyCandidate && (layout.LangId & 0xFF) != 0x11)
				r.Notes = AppendNote(r.Notes, "non-JP-HKL; do-not-infer-physical-JIS-key-presence");
			if (key.IsSpecialSequenceCandidate)
				r.Notes = AppendNote(r.Notes, "special-sequence-candidate");
			return r;
		}

		private static string ClassifyMapping(FreezeCandidateCatalog.Entry key, MappingResult r)
		{
			if (!r.ForwardOk)
			{
				if (key.IsJisOnlyCandidate)
					return "UnsupportedOnLayout";
				return "UnsupportedOnLayout";
			}

			if (key.IsSpecialSequenceCandidate)
				return "SpecialSequence";

			if (key.IsJisOnlyCandidate)
				return "JISOnlyCandidate";

			if (!r.ReverseMatches)
				return "MappingAmbiguous";

			if (key.ExpectedE0.HasValue && key.ExpectedE0.Value != r.E0)
			{
				// MapVirtualKeyEx often omits the E0 prefix bit even when the physical make-code is extended.
				return "PhysicalHardwareRequired";
			}

			if (key.MayNeedE1 && !r.E1Hint)
				return "PhysicalHardwareRequired";

			return "AutoStable";
		}

		private static bool IsLeftRightEquivalent(uint expected, uint got)
		{
			// Generic VK_SHIFT/CONTROL/MENU vs left/right — not used for our catalog (we use distinct L/R Win).
			return false;
		}

		private static string SafeToUnicodeHint(uint vk, ushort scan, IntPtr hkl)
		{
			try
			{
				byte[] state = new byte[256];
				var sb = new StringBuilder(8);
				int n = ToUnicodeEx(vk, scan, state, sb, sb.Capacity, ToUnicodeDontChangeKeyboardState, hkl);
				if (n <= 0)
					return n == 0 ? "(none)" : "(dead/incomplete)";
				return sb.ToString();
			}
			catch
			{
				return "(error)";
			}
		}

		private static string AppendNote(string existing, string add)
			=> string.IsNullOrEmpty(existing) ? add : existing + "; " + add;

		private static string TryKlidForHkl(IntPtr hkl)
		{
			IntPtr saved = GetActiveHkl();
			try
			{
				ActivateKeyboardLayout(hkl, KlfSetForProcess);
				return GetActiveKlid();
			}
			catch
			{
				return string.Format(CultureInfo.InvariantCulture, "{0:X8}", (uint)hkl.ToInt64());
			}
			finally
			{
				SafeActivate(saved);
			}
		}

		private static ushort ParseLangFromKlid(string klid)
		{
			if (string.IsNullOrEmpty(klid) || klid.Length < 8)
				return 0;
			if (ushort.TryParse(klid.AsSpan(4, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort lang))
				return lang;
			return 0;
		}

		private static string TryCultureName(ushort lang)
		{
			if (lang == 0)
				return "-";
			try
			{
				return CultureInfo.GetCultureInfo(lang).EnglishName;
			}
			catch
			{
				return "lang=0x" + lang.ToString("X4", CultureInfo.InvariantCulture);
			}
		}

		private static string GuessFamily(ushort lang, string klid)
		{
			byte primary = (byte)(lang & 0xFF);
			return primary switch {
				0x09 => "ANSI-ish (English)",
				0x07 or 0x0C or 0x0A or 0x10 or 0x13 or 0x16 => "ISO-ish (Western EU)",
				0x11 => "JIS (Japanese layout ID — not proof of physical JIS)",
				0x12 => "Korean layout ID — not proof of physical Korean board",
				0x04 or 0x19 => "Other (CJK/Cyrillic layout ID)",
				_ when string.Equals(klid, "00000416", StringComparison.OrdinalIgnoreCase) => "ABNT2 candidate (pt-BR)",
				_ => "Other/Unknown"
			};
		}
	}
}
