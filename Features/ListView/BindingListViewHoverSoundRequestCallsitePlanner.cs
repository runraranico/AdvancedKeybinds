using System;
using System.Collections.Generic;

namespace AdvancedKeybinds.Features.ListView
{
	/// <summary>Minimal IL view for hover-sound callsite matching (Cecil + MonoMod).</summary>
	internal readonly struct BindingListViewHoverSoundRequestIlInsn
	{
		public readonly string OpCode;
		public readonly string Operand;

		public BindingListViewHoverSoundRequestIlInsn(string opCode, string operand)
		{
			OpCode = opCode ?? string.Empty;
			Operand = operand ?? string.Empty;
		}
	}

	internal enum BindingListViewHoverSoundRequestSiteKind : byte
	{
		None = 0,
		QuickStackEnter = 1,
		QuickStackExit = 2,
		SortTransition = 3,
		ChestButtonHoverEnter = 4,
	}

	internal readonly struct BindingListViewHoverSoundRequestCallsite
	{
		public readonly BindingListViewHoverSoundRequestSiteKind Kind;
		public readonly string FieldFullName;
		public readonly int CallIndex;

		public BindingListViewHoverSoundRequestCallsite(
			BindingListViewHoverSoundRequestSiteKind kind,
			string fieldFullName,
			int callIndex)
		{
			Kind = kind;
			FieldFullName = fieldFullName ?? string.Empty;
			CallIndex = callIndex;
		}
	}

	/// <summary>
	/// Exact QuickStack enter/exit and Sort hover-transition PlaySound requests in DrawInventory,
	/// plus the shared ChestUI.UpdateHover ButtonHovered enter PlaySound.
	/// Requires exact field FullName, sound-id-12 arg sequence, and strict adjacent stores (NOP only).
	/// Not suffix / stfld / foreign types.
	/// </summary>
	internal static class BindingListViewHoverSoundRequestCallsitePlanner
	{
		public const string ExactFieldAllChestStackHover = "System.Boolean Terraria.Main::allChestStackHover";
		public const string ExactFieldInventorySortMouseOver = "System.Boolean Terraria.Main::inventorySortMouseOver";
		public const string ExactFieldChestButtonHovered = "System.Boolean[] Terraria.UI.ChestUI::ButtonHovered";
		public const int ExpectedDrawInventoryHoverSoundCalls = 3;
		public const int ExpectedChestUpdateHoverSoundCalls = 1;
		public const int ExpectedSoundType = 12;
		public const int ExpectedSoundX = -1;
		public const int ExpectedSoundY = -1;
		public const int ExpectedSoundStyle = 1;
		public const float ExpectedVolumeScale = 1f;
		public const float ExpectedPitchOffset = 0f;

		// Legacy short names kept only as documentation; matching must use ExactField* FullName.
		public const string FieldAllChestStackHover = "allChestStackHover";
		public const string FieldInventorySortMouseOver = "inventorySortMouseOver";

		public static List<BindingListViewHoverSoundRequestCallsite> FindDrawInventoryHoverSoundCalls(
			IReadOnlyList<BindingListViewHoverSoundRequestIlInsn> body)
		{
			var result = new List<BindingListViewHoverSoundRequestCallsite>(3);
			if (body == null || body.Count == 0)
				return result;

			for (int i = 0; i < body.Count; i++)
			{
				if (!IsPlaySoundCall(body[i]))
					continue;
				if (!HasExactSoundId12ArgSequence(body, i))
					continue;
				if (!TryMatchStrictAdjacentStore(body, i, out BindingListViewHoverSoundRequestCallsite site))
					continue;
				result.Add(site);
			}

			return result;
		}

		/// <summary>
		/// Exact installed ChestUI.UpdateHover shared hover enter: ldarg.1/brfalse,
		/// ButtonHovered[ldarg.0] ldelem.u1/brtrue skip, sound12 Int6+pop, then stelem.i1 true.
		/// </summary>
		public static List<BindingListViewHoverSoundRequestCallsite> FindChestUpdateHoverSoundCalls(
			IReadOnlyList<BindingListViewHoverSoundRequestIlInsn> body)
		{
			var result = new List<BindingListViewHoverSoundRequestCallsite>(1);
			if (body == null || body.Count == 0)
				return result;

			for (int i = 0; i < body.Count; i++)
			{
				if (!IsPlaySoundCall(body[i]))
					continue;
				if (!HasExactSoundId12ArgSequence(body, i))
					continue;
				if (!TryMatchChestUpdateHoverEnter(body, i, out BindingListViewHoverSoundRequestCallsite site))
					continue;
				result.Add(site);
			}

			return result;
		}

		/// <summary>
		/// True only when the three sites are QS enter + QS exit + Sort (one each).
		/// </summary>
		public static bool HasExactEnterExitSortComposition(
			IReadOnlyList<BindingListViewHoverSoundRequestCallsite> sites)
		{
			if (sites == null || sites.Count != ExpectedDrawInventoryHoverSoundCalls)
				return false;

			int enter = 0, exit = 0, sort = 0;
			for (int i = 0; i < sites.Count; i++)
			{
				switch (sites[i].Kind)
				{
					case BindingListViewHoverSoundRequestSiteKind.QuickStackEnter:
						enter++;
						break;
					case BindingListViewHoverSoundRequestSiteKind.QuickStackExit:
						exit++;
						break;
					case BindingListViewHoverSoundRequestSiteKind.SortTransition:
						sort++;
						break;
					default:
						return false;
				}
			}

			return enter == 1 && exit == 1 && sort == 1;
		}

		/// <summary>True only when exactly one ChestUI.UpdateHover ButtonHovered-enter site.</summary>
		public static bool HasExactChestUpdateHoverComposition(
			IReadOnlyList<BindingListViewHoverSoundRequestCallsite> sites)
		{
			if (sites == null || sites.Count != ExpectedChestUpdateHoverSoundCalls)
				return false;
			return sites[0].Kind == BindingListViewHoverSoundRequestSiteKind.ChestButtonHoverEnter
				&& string.Equals(sites[0].FieldFullName, ExactFieldChestButtonHovered, StringComparison.Ordinal);
		}

		private static bool TryMatchStrictAdjacentStore(
			IReadOnlyList<BindingListViewHoverSoundRequestIlInsn> body,
			int callIndex,
			out BindingListViewHoverSoundRequestCallsite site)
		{
			site = default;
			int i = callIndex + 1;
			i = SkipNops(body, i);
			if (i >= body.Count || !string.Equals(body[i].OpCode, "pop", StringComparison.Ordinal))
				return false;
			i++;
			i = SkipNops(body, i);
			if (i >= body.Count || !TryReadBoolValueLoad(body[i], out bool? constBool))
				return false;
			i++;
			i = SkipNops(body, i);
			if (i >= body.Count || !string.Equals(body[i].OpCode, "stsfld", StringComparison.Ordinal))
				return false;

			string field = body[i].Operand;
			if (string.Equals(field, ExactFieldAllChestStackHover, StringComparison.Ordinal))
			{
				if (!constBool.HasValue)
					return false;
				var kind = constBool.Value
					? BindingListViewHoverSoundRequestSiteKind.QuickStackEnter
					: BindingListViewHoverSoundRequestSiteKind.QuickStackExit;
				site = new BindingListViewHoverSoundRequestCallsite(kind, field, callIndex);
				return true;
			}

			if (string.Equals(field, ExactFieldInventorySortMouseOver, StringComparison.Ordinal))
			{
				site = new BindingListViewHoverSoundRequestCallsite(
					BindingListViewHoverSoundRequestSiteKind.SortTransition, field, callIndex);
				return true;
			}

			return false;
		}

		/// <summary>
		/// Installed shape: ldarg.1/brfalse; ldsfld ButtonHovered/ldarg.0/ldelem.u1/brtrue;
		/// sound12+call+pop; ldsfld ButtonHovered/ldarg.0/ldc.i4.1/stelem.i1.
		/// </summary>
		private static bool TryMatchChestUpdateHoverEnter(
			IReadOnlyList<BindingListViewHoverSoundRequestIlInsn> body,
			int callIndex,
			out BindingListViewHoverSoundRequestCallsite site)
		{
			site = default;

			// After call: pop then ButtonHovered[ID] = true (stelem.i1).
			int i = callIndex + 1;
			i = SkipNops(body, i);
			if (i >= body.Count || !string.Equals(body[i].OpCode, "pop", StringComparison.Ordinal))
				return false;
			i++;
			i = SkipNops(body, i);
			if (i >= body.Count || !string.Equals(body[i].OpCode, "ldsfld", StringComparison.Ordinal)
			    || !string.Equals(body[i].Operand, ExactFieldChestButtonHovered, StringComparison.Ordinal))
				return false;
			i++;
			i = SkipNops(body, i);
			if (i >= body.Count || !IsLdarg(body[i], 0))
				return false;
			i++;
			i = SkipNops(body, i);
			if (i >= body.Count || !IsLdcI4(body[i], 1))
				return false;
			i++;
			i = SkipNops(body, i);
			if (i >= body.Count || !string.Equals(body[i].OpCode, "stelem.i1", StringComparison.Ordinal))
				return false;

			// Before sound-id args: brtrue skip; ldelem.u1; ldarg.0; ldsfld ButtonHovered.
			int b = callIndex - 1;
			b = SkipNopsBackward(body, b);
			if (b < 0 || !IsLdcR4(body[b], ExpectedPitchOffset))
				return false;
			b = SkipNopsBackward(body, b - 1);
			if (b < 0 || !IsLdcR4(body[b], ExpectedVolumeScale))
				return false;
			b = SkipNopsBackward(body, b - 1);
			if (b < 0 || !IsLdcI4(body[b], ExpectedSoundStyle))
				return false;
			b = SkipNopsBackward(body, b - 1);
			if (b < 0 || !IsLdcI4(body[b], ExpectedSoundY))
				return false;
			b = SkipNopsBackward(body, b - 1);
			if (b < 0 || !IsLdcI4(body[b], ExpectedSoundX))
				return false;
			b = SkipNopsBackward(body, b - 1);
			if (b < 0 || !IsLdcI4(body[b], ExpectedSoundType))
				return false;

			b = SkipNopsBackward(body, b - 1);
			if (b < 0 || !IsBrtrue(body[b]))
				return false;
			b = SkipNopsBackward(body, b - 1);
			if (b < 0 || !string.Equals(body[b].OpCode, "ldelem.u1", StringComparison.Ordinal))
				return false;
			b = SkipNopsBackward(body, b - 1);
			if (b < 0 || !IsLdarg(body[b], 0))
				return false;
			b = SkipNopsBackward(body, b - 1);
			if (b < 0 || !string.Equals(body[b].OpCode, "ldsfld", StringComparison.Ordinal)
			    || !string.Equals(body[b].Operand, ExactFieldChestButtonHovered, StringComparison.Ordinal))
				return false;

			// hovering gate: ldarg.1 / brfalse immediately before the !ButtonHovered check.
			b = SkipNopsBackward(body, b - 1);
			if (b < 0 || !IsBrfalse(body[b]))
				return false;
			b = SkipNopsBackward(body, b - 1);
			if (b < 0 || !IsLdarg(body[b], 1))
				return false;

			site = new BindingListViewHoverSoundRequestCallsite(
				BindingListViewHoverSoundRequestSiteKind.ChestButtonHoverEnter,
				ExactFieldChestButtonHovered,
				callIndex);
			return true;
		}

		private static bool IsLdarg(BindingListViewHoverSoundRequestIlInsn insn, int index)
		{
			if (insn.OpCode == "ldarg." + index)
				return true;
			if (insn.OpCode != "ldarg" && insn.OpCode != "ldarg.s")
				return false;
			return int.TryParse(insn.Operand, out int parsed) && parsed == index;
		}

		private static bool IsBrtrue(BindingListViewHoverSoundRequestIlInsn insn)
			=> insn.OpCode == "brtrue" || insn.OpCode == "brtrue.s";

		private static bool IsBrfalse(BindingListViewHoverSoundRequestIlInsn insn)
			=> insn.OpCode == "brfalse" || insn.OpCode == "brfalse.s";

		private static bool HasExactSoundId12ArgSequence(
			IReadOnlyList<BindingListViewHoverSoundRequestIlInsn> body,
			int callIndex)
		{
			// Reading backward from call: pitch, vol, Style, y, x, type (NOP only).
			int i = callIndex - 1;
			i = SkipNopsBackward(body, i);
			if (i < 0 || !IsLdcR4(body[i], ExpectedPitchOffset))
				return false;
			i = SkipNopsBackward(body, i - 1);
			if (i < 0 || !IsLdcR4(body[i], ExpectedVolumeScale))
				return false;
			i = SkipNopsBackward(body, i - 1);
			if (i < 0 || !IsLdcI4(body[i], ExpectedSoundStyle))
				return false;
			i = SkipNopsBackward(body, i - 1);
			if (i < 0 || !IsLdcI4(body[i], ExpectedSoundY))
				return false;
			i = SkipNopsBackward(body, i - 1);
			if (i < 0 || !IsLdcI4(body[i], ExpectedSoundX))
				return false;
			i = SkipNopsBackward(body, i - 1);
			if (i < 0 || !IsLdcI4(body[i], ExpectedSoundType))
				return false;
			return true;
		}

		private static int SkipNops(IReadOnlyList<BindingListViewHoverSoundRequestIlInsn> body, int index)
		{
			while (index < body.Count && string.Equals(body[index].OpCode, "nop", StringComparison.Ordinal))
				index++;
			return index;
		}

		private static int SkipNopsBackward(IReadOnlyList<BindingListViewHoverSoundRequestIlInsn> body, int index)
		{
			while (index >= 0 && string.Equals(body[index].OpCode, "nop", StringComparison.Ordinal))
				index--;
			return index;
		}

		private static bool TryReadBoolValueLoad(BindingListViewHoverSoundRequestIlInsn insn, out bool? constBool)
		{
			constBool = null;
			if (IsLdcI4(insn, 0))
			{
				constBool = false;
				return true;
			}

			if (IsLdcI4(insn, 1))
			{
				constBool = true;
				return true;
			}

			string op = insn.OpCode;
			if (op.StartsWith("ldloc", StringComparison.Ordinal)
			    || op.StartsWith("ldarg", StringComparison.Ordinal))
				return true;

			return false;
		}

		private static bool IsLdcI4(BindingListViewHoverSoundRequestIlInsn insn, int value)
		{
			string op = insn.OpCode;
			if (value == -1 && (op == "ldc.i4.m1" || op == "ldc.i4.M1"))
				return true;
			if (value >= 0 && value <= 8 && op == "ldc.i4." + value)
				return true;
			if (op != "ldc.i4" && op != "ldc.i4.s")
				return false;
			return int.TryParse(insn.Operand, out int parsed) && parsed == value;
		}

		private static bool IsLdcR4(BindingListViewHoverSoundRequestIlInsn insn, float value)
		{
			if (insn.OpCode != "ldc.r4")
				return false;
			if (!float.TryParse(
				    insn.Operand,
				    System.Globalization.NumberStyles.Float,
				    System.Globalization.CultureInfo.InvariantCulture,
				    out float parsed))
				return false;
			return parsed == value;
		}

		private static bool IsPlaySoundCall(BindingListViewHoverSoundRequestIlInsn insn)
		{
			if (!IsCallOp(insn.OpCode))
				return false;
			string name = insn.Operand;
			if (string.IsNullOrEmpty(name))
				return false;
			if (name.IndexOf("ConditionalPlaySound", StringComparison.Ordinal) >= 0)
				return false;
			return name == "PlaySound"
				|| name.EndsWith(".PlaySound", StringComparison.Ordinal)
				|| name.IndexOf("::PlaySound", StringComparison.Ordinal) >= 0
				|| name.IndexOf("PlaySound(", StringComparison.Ordinal) >= 0;
		}

		private static bool IsCallOp(string opCode)
			=> opCode == "call" || opCode == "callvirt";
	}
}
