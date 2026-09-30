using System.Collections.Generic;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace AdvancedKeybinds.Features.ListView
{
	internal enum BindingListViewHoverSoundRequestSuppressStatus : byte
	{
		NotApplied = 0,
		Applied = 1,
		UnsupportedCountMismatch = 2,
		UnsupportedMissingMethod = 3,
		UnsupportedTransformFailed = 4,
	}

	internal enum BindingListViewHoverSoundRequestMethodOutcome : byte
	{
		NotRun = 0,
		Applied = 1,
		CountMismatch = 2,
		SignatureMismatch = 3,
		TargetNotCall = 4,
	}

	/// <summary>
	/// Shared production Cecil emitter for inventory and ChestUI.UpdateHover hover-sound callsite replacement.
	/// Inventory requires exact QS enter + QS exit + Sort composition; Chest requires exact ButtonHovered enter.
	/// Otherwise leaves IL unmodified. Validate all targets before any operand edit.
	/// </summary>
	internal static class BindingListViewHoverSoundRequestSuppressEmitter
	{
		public const string ExpectedPlaySoundDeclaringType = "Terraria.Audio.SoundEngine";
		public const string ExpectedPlaySoundName = "PlaySound";
		public const int ExpectedPlaySoundParamCount = 6;
		public const string ExpectedPlaySoundReturnType = "Microsoft.Xna.Framework.Audio.SoundEffectInstance";

		public static List<BindingListViewHoverSoundRequestIlInsn> Snapshot(MethodDefinition method)
		{
			var list = new List<BindingListViewHoverSoundRequestIlInsn>();
			if (method?.Body == null)
				return list;
			return SnapshotInstructions(method.Body.Instructions);
		}

		/// <summary>Offline fixture entry — delegates to the single IList mutation implementation.</summary>
		public static BindingListViewHoverSoundRequestMethodOutcome TryReplaceDrawInventoryHoverSoundCalls(
			MethodDefinition method,
			MethodReference wrapper,
			int expectedCount)
		{
			if (method?.Body == null)
				return BindingListViewHoverSoundRequestMethodOutcome.SignatureMismatch;
			return TryReplaceDrawInventoryHoverSoundCalls(method.Body.Instructions, wrapper, expectedCount);
		}

		/// <summary>Sole mutation implementation (runtime MonoMod Instrs + offline MethodDefinition body).</summary>
		public static BindingListViewHoverSoundRequestMethodOutcome TryReplaceDrawInventoryHoverSoundCalls(
			IList<Instruction> instructions,
			MethodReference wrapper,
			int expectedCount)
		{
			if (instructions == null || wrapper == null)
				return BindingListViewHoverSoundRequestMethodOutcome.SignatureMismatch;

			var snap = SnapshotInstructions(instructions);
			var sites = BindingListViewHoverSoundRequestCallsitePlanner.FindDrawInventoryHoverSoundCalls(snap);
			if (sites.Count != expectedCount
			    || !BindingListViewHoverSoundRequestCallsitePlanner.HasExactEnterExitSortComposition(sites))
				return BindingListViewHoverSoundRequestMethodOutcome.CountMismatch;

			return ReplaceValidatedPlaySoundSites(instructions, wrapper, sites);
		}

		/// <summary>Offline fixture entry — delegates to the IList runtime mutation implementation.</summary>
		public static BindingListViewHoverSoundRequestMethodOutcome TryReplaceChestUpdateHoverSoundCalls(
			MethodDefinition method,
			MethodReference wrapper,
			int expectedCount)
		{
			if (method?.Body == null)
				return BindingListViewHoverSoundRequestMethodOutcome.SignatureMismatch;
			return TryReplaceChestUpdateHoverSoundCalls(method.Body.Instructions, wrapper, expectedCount);
		}

		/// <summary>Sole Chest mutation implementation (runtime MonoMod Instrs + offline MethodDefinition body).</summary>
		public static BindingListViewHoverSoundRequestMethodOutcome TryReplaceChestUpdateHoverSoundCalls(
			IList<Instruction> instructions,
			MethodReference wrapper,
			int expectedCount)
		{
			if (instructions == null || wrapper == null)
				return BindingListViewHoverSoundRequestMethodOutcome.SignatureMismatch;

			var snap = SnapshotInstructions(instructions);
			var sites = BindingListViewHoverSoundRequestCallsitePlanner.FindChestUpdateHoverSoundCalls(snap);
			if (sites.Count != expectedCount
			    || !BindingListViewHoverSoundRequestCallsitePlanner.HasExactChestUpdateHoverComposition(sites))
				return BindingListViewHoverSoundRequestMethodOutcome.CountMismatch;

			return ReplaceValidatedPlaySoundSites(instructions, wrapper, sites);
		}

		/// <summary>Validate every site before mutating any operand; on mismatch leave IL untouched.</summary>
		private static BindingListViewHoverSoundRequestMethodOutcome ReplaceValidatedPlaySoundSites(
			IList<Instruction> instructions,
			MethodReference wrapper,
			List<BindingListViewHoverSoundRequestCallsite> sites)
		{
			var targets = new List<Instruction>(sites.Count);
			foreach (BindingListViewHoverSoundRequestCallsite site in sites)
			{
				Instruction call = instructions[site.CallIndex];
				if (call.OpCode != OpCodes.Call && call.OpCode != OpCodes.Callvirt)
					return BindingListViewHoverSoundRequestMethodOutcome.TargetNotCall;
				if (!IsExactPlaySoundInt6(call.Operand as MethodReference))
					return BindingListViewHoverSoundRequestMethodOutcome.SignatureMismatch;
				targets.Add(call);
			}

			foreach (Instruction call in targets)
			{
				call.OpCode = OpCodes.Call;
				call.Operand = wrapper;
			}

			return BindingListViewHoverSoundRequestMethodOutcome.Applied;
		}

		public static bool IsExactPlaySoundInt6(MethodReference mr)
		{
			if (mr == null || mr.Name != ExpectedPlaySoundName)
				return false;
			if (mr.DeclaringType == null || mr.DeclaringType.FullName != ExpectedPlaySoundDeclaringType)
				return false;
			if (mr.HasThis)
				return false;
			if (mr.ReturnType == null || mr.ReturnType.FullName != ExpectedPlaySoundReturnType)
				return false;
			if (mr.Parameters.Count != ExpectedPlaySoundParamCount)
				return false;
			return ParamIs(mr.Parameters[0], "System.Int32")
				&& ParamIs(mr.Parameters[1], "System.Int32")
				&& ParamIs(mr.Parameters[2], "System.Int32")
				&& ParamIs(mr.Parameters[3], "System.Int32")
				&& ParamIs(mr.Parameters[4], "System.Single")
				&& ParamIs(mr.Parameters[5], "System.Single");
		}

		public static BindingListViewHoverSoundRequestSuppressStatus StatusFromOutcome(
			BindingListViewHoverSoundRequestMethodOutcome outcome)
		{
			switch (outcome)
			{
				case BindingListViewHoverSoundRequestMethodOutcome.Applied:
					return BindingListViewHoverSoundRequestSuppressStatus.Applied;
				case BindingListViewHoverSoundRequestMethodOutcome.NotRun:
					return BindingListViewHoverSoundRequestSuppressStatus.NotApplied;
				case BindingListViewHoverSoundRequestMethodOutcome.CountMismatch:
					return BindingListViewHoverSoundRequestSuppressStatus.UnsupportedCountMismatch;
				case BindingListViewHoverSoundRequestMethodOutcome.SignatureMismatch:
				case BindingListViewHoverSoundRequestMethodOutcome.TargetNotCall:
					return BindingListViewHoverSoundRequestSuppressStatus.UnsupportedMissingMethod;
				default:
					return BindingListViewHoverSoundRequestSuppressStatus.UnsupportedTransformFailed;
			}
		}

		private static bool ParamIs(ParameterDefinition p, string fullName)
			=> p?.ParameterType != null && p.ParameterType.FullName == fullName;

		private static List<BindingListViewHoverSoundRequestIlInsn> SnapshotInstructions(IList<Instruction> instructions)
		{
			var list = new List<BindingListViewHoverSoundRequestIlInsn>(instructions.Count);
			foreach (Instruction i in instructions)
			{
				string op = i.OpCode.Name;
				string operand = string.Empty;
				if (i.Operand is string s)
					operand = s;
				else if (i.Operand is MethodReference mr)
					operand = mr.Name;
				else if (i.Operand is FieldReference fr)
					operand = fr.FullName;
				else if (i.Operand is float f)
					operand = f.ToString(System.Globalization.CultureInfo.InvariantCulture);
				else if (i.Operand is double d)
					operand = d.ToString(System.Globalization.CultureInfo.InvariantCulture);
				else if (i.Operand != null)
					operand = i.Operand.ToString();
				list.Add(new BindingListViewHoverSoundRequestIlInsn(op, operand));
			}
			return list;
		}
	}
}
