using System;
using System.Collections.Generic;
using AdvancedKeybinds.Binding;
using AdvancedKeybinds.Core;
using AdvancedKeybinds.Patches;
using Microsoft.Xna.Framework.Input;
using Terraria;

namespace AdvancedKeybinds.Features.ListView
{
	/// <summary>
	/// Rising-edge key probe for list filtering. Observes only; does not consume Triggers / JustPressed.
	/// Runs exclusively while the list is open and maximized.
	/// Keyboard: FNA GetPressedKeys plus Win32-backed downs (Win / Pause / IME neighbors / etc.)
	/// via existing <see cref="KeyToken.IsDown"/> / <see cref="WindowsKeySuppressor"/> (read-only).
	/// </summary>
	internal static class BindingListKeyProbe
	{
		private static readonly HashSet<string> PrevTokenDown = new(StringComparer.OrdinalIgnoreCase);
		private static readonly List<string> RisingScratch = new(8);
		private static readonly bool[] PrevMouseDown = new bool[6]; // index 1..5
		private static bool _mousePrevSynced;

		/// <summary>
		/// Keys often missing from <see cref="Main.keyState"/> but held in BindingStorage / capture.
		/// Polled with <see cref="KeyToken.IsDown"/> (Win32 supplements). Not an exhaustive OEM list.
		/// </summary>
		private static readonly string[] Win32SupplementProbeTokens = {
			"LeftWindows",
			"RightWindows",
			"Pause",
			"CapsLock",
			"NumLock",
			StableKeyToken.ScrollLockToken,
			StableKeyToken.HankakuZenkakuToken,
			nameof(Keys.Kana),
			nameof(Keys.Kanji),
			nameof(Keys.OemTilde),
			nameof(Keys.OemPipe),
			nameof(Keys.OemOpenBrackets),
			nameof(Keys.OemCloseBrackets),
			nameof(Keys.OemQuotes),
			nameof(Keys.OemSemicolon),
			nameof(Keys.OemPlus),
			nameof(Keys.OemMinus),
			nameof(Keys.OemComma),
			nameof(Keys.OemPeriod),
			nameof(Keys.OemQuestion),
			nameof(Keys.OemBackslash),
			nameof(Keys.OemClear),
			"Apps",
		};

		public static void Reset()
		{
			PrevTokenDown.Clear();
			RisingScratch.Clear();
			for (int i = 0; i < PrevMouseDown.Length; i++)
				PrevMouseDown[i] = false;
			_mousePrevSynced = false;
		}

		/// <summary>
		/// Returns at most one rising probe token this frame, or null.
		/// Includes Escape, Mouse1–5, mouse wheel, Win, and other Win32-backed keys.
		/// When <paramref name="suppressWheel"/> is true (pointer over list chrome), wheel notches
		/// are ignored so list scrolling keeps ownership of the wheel.
		/// When <paramref name="suppressMouseButtons"/> is true (e.g. title / Show-All chrome),
		/// Mouse1–5 rising edges are ignored so UI clicks do not filter the list.
		/// When <paramref name="suppressKeyboard"/> is true (e.g. column header / MOD chrome),
		/// keyboard rising edges are ignored (prev-down state still updates).
		/// </summary>
		public static string TryGetRisingProbeToken(
			bool suppressWheel = false,
			bool suppressMouseButtons = false,
			bool suppressKeyboard = false)
		{
			string mouse = TryGetRisingMouseButtonToken(suppressMouseButtons);
			if (!string.IsNullOrEmpty(mouse))
				return mouse;

			if (!suppressWheel)
			{
				string wheel = TryGetRisingWheelToken();
				if (!string.IsNullOrEmpty(wheel))
					return wheel;
			}

			string keyboard = TryGetRisingKeyboardToken();
			return suppressKeyboard ? null : keyboard;
		}

		private static string TryGetRisingMouseButtonToken(bool suppress)
		{
			if (!_mousePrevSynced)
			{
				for (int b = 1; b <= 5; b++)
					PrevMouseDown[b] = KeyToken.IsMouseDown(b);
				_mousePrevSynced = true;
				return null;
			}

			string rising = null;
			for (int b = 1; b <= 5; b++)
			{
				bool down = KeyToken.IsMouseDown(b);
				if (down && !PrevMouseDown[b] && rising == null)
					rising = "Mouse" + b;
				PrevMouseDown[b] = down;
			}

			return suppress ? null : rising;
		}

		private static string TryGetRisingWheelToken()
		{
			var edges = new List<string>(4);
			try
			{
				MouseInputHelper.AppendFrameWheelEdges(edges);
			}
			catch
			{
				return null;
			}

			if (edges.Count == 0)
				return null;

			for (int i = 0; i < edges.Count; i++)
			{
				if (StableKeyToken.IsMouseWheelVerticalToken(edges[i]))
					return edges[i];
			}

			return edges[0];
		}

		private static string TryGetRisingKeyboardToken()
		{
			RisingScratch.Clear();
			var current = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

			Keys[] pressed = null;
			try
			{
				pressed = Main.keyState.GetPressedKeys();
			}
			catch
			{
				pressed = null;
			}

			if (pressed != null)
			{
				for (int i = 0; i < pressed.Length; i++)
				{
					Keys k = pressed[i];
					if (k == Keys.None)
						continue;
					current.Add(k.ToString());
				}
			}

			// Win / Pause / IME / OEM — often absent from GetPressedKeys; same downs capture uses.
			for (int i = 0; i < Win32SupplementProbeTokens.Length; i++)
			{
				string token = Win32SupplementProbeTokens[i];
				try
				{
					if (KeyToken.IsDown(token))
						current.Add(token);
				}
				catch
				{
					// ignore single-token poll failures
				}
			}

			// Explicit Win flags (in case IsDown path is gated while suppressor already tracked make/break).
			try
			{
				if (WindowsKeySuppressor.IsLeftWindowsDown)
					current.Add("LeftWindows");
				if (WindowsKeySuppressor.IsRightWindowsDown)
					current.Add("RightWindows");
			}
			catch
			{
				// ignore
			}

			foreach (string token in current)
			{
				if (!PrevTokenDown.Contains(token))
					RisingScratch.Add(token);
			}

			PrevTokenDown.Clear();
			foreach (string token in current)
				PrevTokenDown.Add(token);

			if (RisingScratch.Count == 0)
				return null;

			// Prefer non-modifier if a chord rises together (Escape / Win alone still probe).
			for (int i = 0; i < RisingScratch.Count; i++)
			{
				string t = RisingScratch[i];
				if (!IsModifierToken(t))
					return t;
			}

			return RisingScratch[0];
		}

		private static bool IsModifierToken(string token)
		{
			if (string.IsNullOrEmpty(token))
				return false;
			return string.Equals(token, "LeftShift", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(token, "RightShift", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(token, "LeftControl", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(token, "RightControl", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(token, "LeftAlt", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(token, "RightAlt", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(token, "LeftWindows", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(token, "RightWindows", StringComparison.OrdinalIgnoreCase);
		}
	}
}
