using System;
using System.Collections.Generic;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace AdvancedKeybinds.Features.ListView
{
	internal enum BindingListViewHoverRequestSuppressStatus : byte
	{
		NotApplied = 0,
		Applied = 1,
		UnsupportedCountMismatch = 2,
		UnsupportedMissingMethod = 3,
		UnsupportedTransformFailed = 4,
		BankNotInstalled = 5,
		BankApplied = 6,
		BankUnsupported = 7,
	}

	internal enum BindingListViewHoverRequestMethodOutcome : byte
	{
		NotRun = 0,
		Applied = 1,
		CountMismatch = 2,
		SignatureMismatch = 3,
		TargetNotCall = 4,
	}

	/// <summary>
	/// Shared production Cecil emitter for hover-request callsite replacement.
	/// Prevalidates every selected target (declaring type + signature) before any IL write.
	/// Used by offline fixtures and by runtime apply (via ILContext adapter).
	/// </summary>
	internal static class BindingListViewHoverRequestSuppressEmitter
	{
		public const string ExpectedMouseTextDeclaringType = "Terraria.Main";
		public const string ExpectedMouseTextName = "MouseText";
		public const int ExpectedMouseTextParamCount = 8;

		public const string ExpectedHackZoomDeclaringType = "Terraria.Main";
		public const string ExpectedHackZoomName = "MouseTextHackZoom";
		public const int ExpectedHackZoomParamCount = 2;

		public static List<BindingListViewHoverRequestIlInsn> Snapshot(MethodDefinition method)
		{
			var list = new List<BindingListViewHoverRequestIlInsn>();
			if (method?.Body == null)
				return list;
			foreach (Instruction i in method.Body.Instructions)
			{
				string op = i.OpCode.Name;
				string operand = string.Empty;
				if (i.Operand is string s)
					operand = s;
				else if (i.Operand is MethodReference mr)
					operand = mr.Name;
				else if (i.Operand != null)
					operand = i.Operand.ToString();
				list.Add(new BindingListViewHoverRequestIlInsn(op, operand));
			}
			return list;
		}

		/// <summary>Offline fixture entry — delegates to the single IList mutation implementation.</summary>
		public static BindingListViewHoverRequestMethodOutcome TryReplaceVanillaUtilityMouseTextCalls(
			MethodDefinition method,
			MethodReference wrapper,
			int expectedCount)
		{
			if (method?.Body == null)
				return BindingListViewHoverRequestMethodOutcome.SignatureMismatch;
			return TryReplaceVanillaUtilityMouseTextCalls(method.Body.Instructions, wrapper, expectedCount);
		}

		/// <summary>Offline fixture entry — delegates to the single IList mutation implementation.</summary>
		public static BindingListViewHoverRequestMethodOutcome TryReplaceBankGetHoverTextHackZoomCalls(
			MethodDefinition method,
			MethodReference wrapper,
			int expectedCount)
		{
			if (method?.Body == null)
				return BindingListViewHoverRequestMethodOutcome.SignatureMismatch;
			return TryReplaceBankGetHoverTextHackZoomCalls(method.Body.Instructions, wrapper, expectedCount);
		}

		/// <summary>Sole mutation implementation (runtime MonoMod Instrs + offline MethodDefinition body).</summary>
		public static BindingListViewHoverRequestMethodOutcome TryReplaceVanillaUtilityMouseTextCalls(
			IList<Instruction> instructions,
			MethodReference wrapper,
			int expectedCount)
		{
			if (instructions == null || wrapper == null)
				return BindingListViewHoverRequestMethodOutcome.SignatureMismatch;

			var snap = SnapshotInstructions(instructions);
			var sites = BindingListViewHoverRequestCallsitePlanner.FindVanillaUtilityMouseTextCalls(snap);
			if (sites.Count != expectedCount)
				return BindingListViewHoverRequestMethodOutcome.CountMismatch;

			var targets = new List<Instruction>(sites.Count);
			foreach (BindingListViewHoverRequestCallsite site in sites)
			{
				Instruction call = instructions[site.CallIndex];
				if (call.OpCode != OpCodes.Call && call.OpCode != OpCodes.Callvirt)
					return BindingListViewHoverRequestMethodOutcome.TargetNotCall;
				if (!IsExactMouseText8(call.Operand as MethodReference))
					return BindingListViewHoverRequestMethodOutcome.SignatureMismatch;
				targets.Add(call);
			}

			foreach (Instruction call in targets)
			{
				call.OpCode = OpCodes.Call;
				call.Operand = wrapper;
			}

			return BindingListViewHoverRequestMethodOutcome.Applied;
		}

		/// <summary>Sole bank mutation implementation (runtime MonoMod Instrs + offline MethodDefinition body).</summary>
		public static BindingListViewHoverRequestMethodOutcome TryReplaceBankGetHoverTextHackZoomCalls(
			IList<Instruction> instructions,
			MethodReference wrapper,
			int expectedCount)
		{
			if (instructions == null || wrapper == null)
				return BindingListViewHoverRequestMethodOutcome.SignatureMismatch;

			var snap = SnapshotInstructions(instructions);
			List<int> sites = BindingListViewHoverRequestCallsitePlanner.FindBankButtonsGetHoverTextHackZoomCalls(snap);
			if (sites.Count != expectedCount)
				return BindingListViewHoverRequestMethodOutcome.CountMismatch;

			var targets = new List<Instruction>(sites.Count);
			foreach (int index in sites)
			{
				Instruction call = instructions[index];
				if (call.OpCode != OpCodes.Call && call.OpCode != OpCodes.Callvirt)
					return BindingListViewHoverRequestMethodOutcome.TargetNotCall;
				if (!IsExactHackZoom2(call.Operand as MethodReference))
					return BindingListViewHoverRequestMethodOutcome.SignatureMismatch;
				targets.Add(call);
			}

			foreach (Instruction call in targets)
			{
				call.OpCode = OpCodes.Call;
				call.Operand = wrapper;
			}

			return BindingListViewHoverRequestMethodOutcome.Applied;
		}

		public static bool IsExactMouseText8(MethodReference mr)
		{
			if (mr == null || mr.Name != ExpectedMouseTextName)
				return false;
			if (mr.DeclaringType == null || mr.DeclaringType.FullName != ExpectedMouseTextDeclaringType)
				return false;
			if (!mr.HasThis)
				return false;
			if (mr.ReturnType == null || mr.ReturnType.FullName != "System.Void")
				return false;
			if (mr.Parameters.Count != ExpectedMouseTextParamCount)
				return false;
			return ParamIs(mr.Parameters[0], "System.String")
				&& ParamIs(mr.Parameters[1], "System.Int32")
				&& ParamIs(mr.Parameters[2], "System.Byte")
				&& ParamIs(mr.Parameters[3], "System.Int32")
				&& ParamIs(mr.Parameters[4], "System.Int32")
				&& ParamIs(mr.Parameters[5], "System.Int32")
				&& ParamIs(mr.Parameters[6], "System.Int32")
				&& ParamIs(mr.Parameters[7], "System.Int32");
		}

		public static bool IsExactHackZoom2(MethodReference mr)
		{
			if (mr == null || mr.Name != ExpectedHackZoomName)
				return false;
			if (mr.DeclaringType == null || mr.DeclaringType.FullName != ExpectedHackZoomDeclaringType)
				return false;
			if (!mr.HasThis)
				return false;
			if (mr.ReturnType == null || mr.ReturnType.FullName != "System.Void")
				return false;
			if (mr.Parameters.Count != ExpectedHackZoomParamCount)
				return false;
			return ParamIs(mr.Parameters[0], "System.String")
				&& ParamIs(mr.Parameters[1], "System.String");
		}

		/// <summary>
		/// Applied only when every reported method outcome is Applied.
		/// Any CountMismatch/SignatureMismatch/TargetNotCall wins over partial success.
		/// Partial Applied+NotRun is TransformFailed (never false Applied).
		/// </summary>
		public static BindingListViewHoverRequestSuppressStatus AggregateVanillaStatus(
			params BindingListViewHoverRequestMethodOutcome[] outcomes)
		{
			if (outcomes == null || outcomes.Length == 0)
				return BindingListViewHoverRequestSuppressStatus.NotApplied;

			int applied = 0;
			int notRun = 0;
			foreach (BindingListViewHoverRequestMethodOutcome o in outcomes)
			{
				if (o == BindingListViewHoverRequestMethodOutcome.Applied)
				{
					applied++;
					continue;
				}

				if (o == BindingListViewHoverRequestMethodOutcome.NotRun)
				{
					notRun++;
					continue;
				}

				if (o == BindingListViewHoverRequestMethodOutcome.CountMismatch)
					return BindingListViewHoverRequestSuppressStatus.UnsupportedCountMismatch;
				if (o == BindingListViewHoverRequestMethodOutcome.SignatureMismatch
				    || o == BindingListViewHoverRequestMethodOutcome.TargetNotCall)
					return BindingListViewHoverRequestSuppressStatus.UnsupportedMissingMethod;
				return BindingListViewHoverRequestSuppressStatus.UnsupportedTransformFailed;
			}

			if (applied == outcomes.Length)
				return BindingListViewHoverRequestSuppressStatus.Applied;
			if (notRun == outcomes.Length)
				return BindingListViewHoverRequestSuppressStatus.NotApplied;
			// Partial: some Applied, some NotRun — never claim Applied.
			return BindingListViewHoverRequestSuppressStatus.UnsupportedTransformFailed;
		}

		private static bool ParamIs(ParameterDefinition p, string fullName)
			=> p?.ParameterType != null && p.ParameterType.FullName == fullName;

		private static List<BindingListViewHoverRequestIlInsn> SnapshotInstructions(IList<Instruction> instructions)
		{
			var list = new List<BindingListViewHoverRequestIlInsn>(instructions.Count);
			foreach (Instruction i in instructions)
			{
				string op = i.OpCode.Name;
				string operand = string.Empty;
				if (i.Operand is string s)
					operand = s;
				else if (i.Operand is MethodReference mr)
					operand = mr.Name;
				else if (i.Operand != null)
					operand = i.Operand.ToString();
				list.Add(new BindingListViewHoverRequestIlInsn(op, operand));
			}
			return list;
		}
	}
}
