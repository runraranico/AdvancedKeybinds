using System;
using System.Collections.Generic;
using System.Reflection;
using Terraria.ModLoader;

namespace AdvancedKeybinds.Patches
{
	/// <summary>
	/// Hides developer-only diagnostic chat commands from <c>/help</c> listings
	/// without changing command registration or execution.
	/// </summary>
	internal static class DiagnosticCommandHelpHidePatches
	{
		private static readonly string[] HiddenCommands =
		{
			"akdiag",
			"akdiagallkeys",
			"akfreeze",
		};

		private delegate List<Tuple<string, string>> orig_GetHelp(CommandType type);

		public static void Apply()
		{
			MethodInfo getHelp = typeof(CommandLoader).GetMethod(
				"GetHelp",
				BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
				binder: null,
				types: new[] { typeof(CommandType) },
				modifiers: null);

			if (getHelp == null)
			{
				AdvancedKeybinds.Instance?.Logger.Warn(
					"Advanced Keybinds could not find CommandLoader.GetHelp; diagnostic commands may still appear in /help.");
				return;
			}

			ModDetours.Apply(getHelp, (orig_GetHelp orig, CommandType type) => {
				List<Tuple<string, string>> help = orig(type);
				if (help == null || help.Count == 0)
					return help;

				for (int i = help.Count - 1; i >= 0; i--)
				{
					if (IsHiddenHelpEntry(help[i].Item1))
						help.RemoveAt(i);
				}

				return help;
			});
		}

		private static bool IsHiddenHelpEntry(string entry)
		{
			if (string.IsNullOrEmpty(entry))
				return false;

			for (int i = 0; i < HiddenCommands.Length; i++)
			{
				string cmd = HiddenCommands[i];
				if (entry.Equals(cmd, StringComparison.OrdinalIgnoreCase))
					return true;

				// CommandLoader prefixes "ModName:" when multiple mods share a command name.
				int colon = entry.LastIndexOf(':');
				if (colon >= 0
					&& colon + 1 < entry.Length
					&& entry.Substring(colon + 1).Equals(cmd, StringComparison.OrdinalIgnoreCase))
					return true;
			}

			return false;
		}
	}
}
