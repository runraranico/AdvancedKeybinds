using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using AdvancedKeybinds.Binding;
using Terraria;

namespace AdvancedKeybinds.Patches
{
	/// <summary>
	/// Detects the physical Caps Lock key on Japanese keyboards where Caps alone does not emit
	/// VK_CAPITAL (IME mode switch only). Uses Win32 keyboard state, scan-code mapping, and IME
	/// conversion changes as fallbacks below the low-level hook.
	/// </summary>
	internal static class JpCapsLockDetector
	{
		private const uint MAPVK_VK_TO_VSC_EX = 0;
		private const uint VK_SHIFT = 0x10;
		private const ushort SC_CAPITAL = 0x3A;
		private const ushort SC_NUMLOCK = 0x45;
		private const ushort SC_SCROLL = 0x46;

		private static readonly HashSet<string> PendingEdges = new(StringComparer.OrdinalIgnoreCase);
		private static readonly byte[] PrevKeyState = new byte[256];
		private static bool _keyStateReady;
		private static int _prevImeConversion;
		private static bool _imeReady;
		private static bool _hadOtherKeyEdge;

		public static void Reset()
		{
			PendingEdges.Clear();
			Array.Clear(PrevKeyState, 0, PrevKeyState.Length);
			_keyStateReady = false;
			_prevImeConversion = 0;
			_imeReady = false;
			_hadOtherKeyEdge = false;
		}

		public static void Poll(bool allowImeEdges)
		{
			_hadOtherKeyEdge = false;
			PollKeyboardStateEdges();
			if (allowImeEdges)
				PollImeConversionEdge();
		}

		public static IReadOnlyCollection<string> DrainEdges()
		{
			if (PendingEdges.Count == 0)
				return Array.Empty<string>();

			string[] edges = new string[PendingEdges.Count];
			PendingEdges.CopyTo(edges);
			PendingEdges.Clear();
			return edges;
		}

		private static void PollKeyboardStateEdges()
		{
			byte[] state = new byte[256];
			if (!GetKeyboardState(state))
				return;

			if (!_keyStateReady)
			{
				Buffer.BlockCopy(state, 0, PrevKeyState, 0, 256);
				_keyStateReady = true;
				return;
			}

			IntPtr layout = GetKeyboardLayout(0);

			for (int vk = 1; vk < 256; vk++)
			{
				bool down = (state[vk] & 0x80) != 0;
				bool wasDown = (PrevKeyState[vk] & 0x80) != 0;
				if (!down || wasDown)
					continue;

				uint scan = MapVirtualKeyEx((uint)vk, MAPVK_VK_TO_VSC_EX, layout) & 0xFF;
				string token = ScanToToggleToken(scan);
				if (token != null)
				{
					if (!PendingEdges.Add(token))
						continue;

					AkDiag.Log($"KeyState toggle edge token={token} vk=0x{vk:X2} scan=0x{scan:X2}");
					continue;
				}

				if (!IsModifierVk(vk))
					_hadOtherKeyEdge = true;
			}

			Buffer.BlockCopy(state, 0, PrevKeyState, 0, 256);
		}

		private static void PollImeConversionEdge()
		{
			if (_hadOtherKeyEdge || IsShiftDown())
				return;

			if (!TryReadImeConversion(out int conversion))
				return;

			if (!_imeReady)
			{
				_prevImeConversion = conversion;
				_imeReady = true;
				return;
			}

			if (conversion == _prevImeConversion)
				return;

			_prevImeConversion = conversion;
			if (!PendingEdges.Add("CapsLock"))
				return;

			AkDiag.Log("JP IME conversion edge mapped to CapsLock conv=0x" + conversion.ToString("X"));
		}

		private static string ScanToToggleToken(uint scan) => scan switch {
			SC_CAPITAL => "CapsLock",
			SC_NUMLOCK => "NumLock",
			SC_SCROLL => BindingRestrictions.ScrollLockToken,
			_ => null,
		};

		private static bool IsShiftDown()
			=> (GetAsyncKeyState((int)VK_SHIFT) & 0x8000) != 0;

		private static bool IsModifierVk(int vk) => vk switch {
			0x10 or 0x11 or 0x12 or 0x5B or 0x5C => true, // Shift/Ctrl/Alt/Win
			_ => false,
		};

		private static bool TryReadImeConversion(out int conversion)
		{
			conversion = 0;
			foreach (IntPtr hwnd in EnumerateImeHosts())
			{
				if (hwnd == IntPtr.Zero)
					continue;

				IntPtr context = ImmGetContext(hwnd);
				if (context == IntPtr.Zero)
					continue;

				try
				{
					if (ImmGetConversionStatus(context, out conversion, out _))
						return true;
				}
				finally
				{
					ImmReleaseContext(hwnd, context);
				}
			}

			return false;
		}

		private static IEnumerable<IntPtr> EnumerateImeHosts()
		{
			yield return GameWindowHandle.TryGet(preferForeground: WindowsKeySuppressor.IsSlotCapturePassthrough);
			yield return GetForegroundWindow();
			yield return GetFocus();
			yield return ImmGetDefaultIMEWnd(0);
		}

		[DllImport("user32.dll")]
		private static extern bool GetKeyboardState(byte[] lpKeyState);

		[DllImport("user32.dll")]
		private static extern IntPtr GetForegroundWindow();

		[DllImport("user32.dll")]
		private static extern IntPtr GetFocus();

		[DllImport("imm32.dll")]
		private static extern IntPtr ImmGetDefaultIMEWnd(uint idThread);

		[DllImport("user32.dll")]
		private static extern uint MapVirtualKeyEx(uint uCode, uint uMapType, IntPtr dwhkl);

		[DllImport("user32.dll")]
		private static extern IntPtr GetKeyboardLayout(uint idThread);

		[DllImport("user32.dll")]
		private static extern short GetAsyncKeyState(int vKey);

		[DllImport("imm32.dll")]
		private static extern IntPtr ImmGetContext(IntPtr hWnd);

		[DllImport("imm32.dll")]
		private static extern bool ImmGetConversionStatus(IntPtr hIMC, out int lpfdwConversion, out int lpfdwSentence);

		[DllImport("imm32.dll")]
		private static extern bool ImmReleaseContext(IntPtr hWnd, IntPtr hIMC);
	}
}
