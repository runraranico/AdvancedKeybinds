using System;
using System.Collections.Generic;
using System.Reflection;
using MonoMod.RuntimeDetour;

namespace AdvancedKeybinds.Patches
{
	/// <summary>
	/// Tracks detours so they can be removed on mod unload. Without this, build-and-reload can leave
	/// stale IngameFancyUI.Close / PlayerInput hooks attached and make Escape feel random.
	/// </summary>
	internal static class ModDetours
	{
		private static readonly List<Hook> Hooks = new();

		public static void Apply(MethodBase method, Delegate detour)
		{
			if (method == null)
				throw new ArgumentNullException(nameof(method));

			try
			{
				Hook hook = new(method, detour);
				hook.Apply();
				Hooks.Add(hook);
			}
			catch (Exception ex)
			{
				string target = method.DeclaringType?.FullName + "." + method.Name
					+ "(" + string.Join(", ", Array.ConvertAll(method.GetParameters(), p => p.ParameterType.Name)) + ")";
				throw new InvalidOperationException(
					"Advanced Keybinds failed to hook " + target + ". Detour signature may not match the game method.", ex);
			}
		}

		public static void DisposeAll()
		{
			for (int i = Hooks.Count - 1; i >= 0; i--)
			{
				try
				{
					Hooks[i].Dispose();
				}
				catch
				{
					// Best effort during teardown.
				}
			}

			Hooks.Clear();
		}
	}
}
