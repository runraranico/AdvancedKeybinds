using System;
using System.Collections.Generic;
using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using Terraria;
using Terraria.ModLoader;
using Terraria.UI;

namespace AdvancedKeybinds.Patches
{
	public class MenuTransitionSoundPatches : ModSystem
	{
		[ThreadStatic]
		private static int _suppressCloseSoundBudget;

		private static ILHook _closeHook;
		private static Action<Action> _registeredScope;
		private static bool _ownsDelegate;

		public override void Load()
		{
			if (!TryCleanupOwnHookAndDelegate())
				return;

			MethodInfo closeMethod = typeof(IngameFancyUI).GetMethod(
				"Close",
				BindingFlags.Public | BindingFlags.Static,
				binder: null,
				types: Type.EmptyTypes,
				modifiers: null);
			if (closeMethod == null)
				return;

			ILHook created = null;
			try
			{
				bool applied = false;
				created = new ILHook(closeMethod, il =>
				{
					applied = TryPatchCloseSound(il);
				});
				if (!applied)
				{
					_closeHook = created;
					created = null;
					TryCleanupOwnHookAndDelegate();
					return;
				}

				_closeHook = created;
				created = null;
				_registeredScope = CloseForHiddenSettingsTransition;
				KeybindShortcutSystem.SettingsTransitionCloseSoundScope = _registeredScope;
				_ownsDelegate = true;
			}
			catch
			{
				if (created != null)
					_closeHook = created;
				TryCleanupOwnHookAndDelegate();
			}
		}

		public override void Unload()
		{
			TryCleanupOwnHookAndDelegate();
		}

		private static bool TryCleanupOwnHookAndDelegate()
		{
			bool disposeOk = true;
			try
			{
				if (_closeHook != null)
				{
					try
					{
						_closeHook.Dispose();
						_closeHook = null;
					}
					catch
					{
						disposeOk = false;
					}
				}
			}
			finally
			{
				ClearOwnDelegate();
			}

			return disposeOk;
		}

		private static void ClearOwnDelegate()
		{
			if (_ownsDelegate
				&& ReferenceEquals(KeybindShortcutSystem.SettingsTransitionCloseSoundScope, _registeredScope))
			{
				KeybindShortcutSystem.SettingsTransitionCloseSoundScope = null;
			}

			_ownsDelegate = false;
			_registeredScope = null;
		}

		internal static void CloseForHiddenSettingsTransition(Action close)
		{
			if (close == null)
				return;

			if (!Main.gameMenu
				&& Main.inFancyUI
				&& IsAllowedFancySource(Main.InGameUI?.CurrentState?.GetType().FullName))
			{
				RunWithoutCloseSound(close);
				return;
			}

			close();
		}

		private static bool IsAllowedFancySource(string fullName)
			=> fullName == "Terraria.GameContent.UI.States.UIManageControls"
				|| fullName == "Terraria.ModLoader.Config.UI.UIModConfigList"
				|| fullName == "Terraria.ModLoader.Config.UI.UIModConfig";

		internal static void RunWithoutCloseSound(Action close)
		{
			int previous = _suppressCloseSoundBudget;
			_suppressCloseSoundBudget = 1;
			try
			{
				close();
			}
			finally
			{
				_suppressCloseSoundBudget = previous;
			}
		}

		private static bool ConsumeCloseSoundSuppressionBudget()
		{
			if (_suppressCloseSoundBudget <= 0)
				return false;

			_suppressCloseSoundBudget--;
			return true;
		}

		internal static bool TryPatchCloseSound(ILContext il)
		{
			if (il?.Body?.Instructions == null)
				return false;

			var sites = new List<(Instruction start, Instruction afterPop)>();
			IList<Instruction> instrs = il.Instrs;
			for (int i = 0; i + 7 < instrs.Count; i++)
			{
				if (!instrs[i].MatchLdcI4(11))
					continue;
				if (!instrs[i + 1].MatchLdcI4(-1))
					continue;
				if (!instrs[i + 2].MatchLdcI4(-1))
					continue;
				if (!instrs[i + 3].MatchLdcI4(1))
					continue;
				if (!IsLdcR4(instrs[i + 4], 1f))
					continue;
				if (!IsLdcR4(instrs[i + 5], 0f))
					continue;
				if (!IsLegacyMenuClosePlaySoundCall(instrs[i + 6]))
					continue;
				if (instrs[i + 7].OpCode != OpCodes.Pop)
					continue;
				if (i + 8 >= instrs.Count)
					continue;

				sites.Add((instrs[i], instrs[i + 8]));
			}

			if (sites.Count != 1)
				return false;

			Instruction start = sites[0].start;
			Instruction afterPop = sites[0].afterPop;
			var suppressLocal = new VariableDefinition(il.Module.TypeSystem.Boolean);
			il.Body.Variables.Add(suppressLocal);
			il.Body.InitLocals = true;

			var cursor = new ILCursor(il);
			cursor.Goto(0, MoveType.Before);
			cursor.EmitDelegate(ConsumeCloseSoundSuppressionBudget);
			cursor.Emit(OpCodes.Stloc, suppressLocal);

			cursor.Goto(start, MoveType.Before);
			cursor.Emit(OpCodes.Ldloc, suppressLocal);
			cursor.Emit(OpCodes.Ldc_I4_0);
			cursor.Emit(OpCodes.Stloc, suppressLocal);
			cursor.Emit(OpCodes.Brtrue, afterPop);
			return true;
		}

		private static bool IsLdcR4(Instruction instruction, float expected)
			=> instruction.OpCode == OpCodes.Ldc_R4
				&& instruction.Operand is float value
				&& value.Equals(expected);

		private static bool IsLegacyMenuClosePlaySoundCall(Instruction instruction)
		{
			if (instruction.OpCode != OpCodes.Call || instruction.Operand == null)
				return false;

			if (instruction.Operand is MethodReference method)
			{
				return method.Name == "PlaySound"
					&& !method.HasThis
					&& method.DeclaringType != null
					&& method.DeclaringType.FullName == "Terraria.Audio.SoundEngine"
					&& method.ReturnType != null
					&& method.ReturnType.FullName == "Microsoft.Xna.Framework.Audio.SoundEffectInstance"
					&& method.Parameters.Count == 6
					&& method.Parameters[0].ParameterType.FullName == "System.Int32"
					&& method.Parameters[1].ParameterType.FullName == "System.Int32"
					&& method.Parameters[2].ParameterType.FullName == "System.Int32"
					&& method.Parameters[3].ParameterType.FullName == "System.Int32"
					&& method.Parameters[4].ParameterType.FullName == "System.Single"
					&& method.Parameters[5].ParameterType.FullName == "System.Single";
			}

			if (instruction.Operand is MethodInfo methodInfo)
			{
				ParameterInfo[] parameters = methodInfo.GetParameters();
				return methodInfo.Name == "PlaySound"
					&& methodInfo.IsStatic
					&& methodInfo.DeclaringType == typeof(Terraria.Audio.SoundEngine)
					&& methodInfo.ReturnType.FullName == "Microsoft.Xna.Framework.Audio.SoundEffectInstance"
					&& parameters.Length == 6
					&& parameters[0].ParameterType == typeof(int)
					&& parameters[1].ParameterType == typeof(int)
					&& parameters[2].ParameterType == typeof(int)
					&& parameters[3].ParameterType == typeof(int)
					&& parameters[4].ParameterType == typeof(float)
					&& parameters[5].ParameterType == typeof(float);
			}

			return false;
		}
	}
}
