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
		private static int _registerCount;
		private static int _disposeCount;
		private static int _disposeAllCalls;

		public static int RegisterCount => _registerCount;
		public static int DisposeCount => _disposeCount;
		public static int DisposeAllCalls => _disposeAllCalls;
		public static int LiveHookCount => Hooks.Count;

		public static void Apply(MethodBase method, Delegate detour)
		{
			if (method == null)
				throw new ArgumentNullException(nameof(method));

			try
			{
				Hook hook = new(method, detour);
				hook.Apply();
				Hooks.Add(hook);
				_registerCount++;
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
			_disposeAllCalls++;
			for (int i = Hooks.Count - 1; i >= 0; i--)
			{
				try
				{
					Hooks[i].Dispose();
					_disposeCount++;
				}
				catch
				{
					// Best effort during teardown.
				}
			}

			Hooks.Clear();
		}

		/// <summary>TEMP diag snapshot for Build+Reload identity checks.</summary>
		public static string FormatCountsForDiag()
			=> "hookReg=" + _registerCount
				+ " hookDisp=" + _disposeCount
				+ " disposeAllCalls=" + _disposeAllCalls
				+ " liveHooks=" + Hooks.Count;
	}
}
