using System;
using System.Collections.Generic;
using AdvancedKeybinds.Core;
using Microsoft.Xna.Framework.Input;
using Terraria;

namespace AdvancedKeybinds.Binding
{
	/// <summary>
	/// Non-Windows keyboard provider for keys already exposed by FNA <see cref="Keyboard.GetState"/>.
	/// Intended for general Linux (and other non-Windows) keyboard use.
	/// Windows continues to use the measured Win32 SC/VK capture path; this type is a no-op there.
	/// Reads <see cref="Keyboard.GetState"/> once per PlayerInput update tick (not <see cref="Main.keyState"/>).
	/// </summary>
	internal static class FnaKeyboardProvider
	{
		private readonly struct Entry
		{
			public readonly Keys Key;
			public readonly string Token;

			public Entry(Keys key, string token)
			{
				Key = key;
				Token = token;
			}
		}

		private static readonly Entry[] Entries = BuildEntries();

		/// <summary>
		/// Advanced by <see cref="AdvanceInputTickCache"/> at each PlayerInput.UpdateInput detour.
		/// Independent of <see cref="Main.GameUpdateCount"/> (which can stay 0 on menus / Controls UI).
		/// </summary>
		private static int _inputTickEpoch;

		private static int _keyboardEpoch = int.MinValue;
		private static KeyboardState _keyboard;

		/// <summary>
		/// Call once at the start of each PlayerInput.UpdateInput detour (before capture polls).
		/// Invalidates the KeyboardState cache for this input tick.
		/// </summary>
		internal static void AdvanceInputTickCache()
		{
			unchecked
			{
				_inputTickEpoch++;
			}
		}

		/// <summary>
		/// Read-only tick serial for other FNA input samplers (e.g. wheel) that must not use
		/// <see cref="Main.GameUpdateCount"/>. Behavior of keyboard caching is unchanged.
		/// </summary>
		internal static int InputTickEpoch => _inputTickEpoch;

		/// <summary>
		/// One <see cref="Keyboard.GetState"/> per PlayerInput update tick (shared before/after polls).
		/// Does not use <see cref="Main.GameUpdateCount"/> — menus often leave that at 0.
		/// </summary>
		private static KeyboardState GetKeyboardStateCached()
		{
			int epoch = _inputTickEpoch;
			if (epoch != _keyboardEpoch)
			{
				_keyboardEpoch = epoch;
				_keyboard = Keyboard.GetState();
			}

			return _keyboard;
		}

		public static void SupplementHeld(HashSet<string> pressed)
		{
			if (pressed == null || OperatingSystem.IsWindows() || !Main.hasFocus)
				return;

			KeyboardState keyboard = GetKeyboardStateCached();
			for (int i = 0; i < Entries.Length; i++)
			{
				Entry entry = Entries[i];
				if (keyboard.IsKeyDown(entry.Key))
					pressed.Add(entry.Token);
			}
		}

		/// <summary>
		/// Non-Windows fire / held check using the same cached <see cref="Keyboard.GetState"/> as capture.
		/// </summary>
		public static bool IsKeyDown(Keys key)
		{
			if (OperatingSystem.IsWindows() || !Main.hasFocus || key == Keys.None)
				return false;

			return GetKeyboardStateCached().IsKeyDown(key);
		}

		private static Entry[] BuildEntries()
		{
			string[] names = NonWindowsFnaKeyboardCatalog.Names;
			var entries = new List<Entry>(names.Length);
			for (int i = 0; i < names.Length; i++)
			{
				string name = names[i];
				if (Enum.TryParse(name, ignoreCase: false, out Keys key) && key != Keys.None)
					entries.Add(new Entry(key, name));
			}

			return entries.ToArray();
		}
	}
}
