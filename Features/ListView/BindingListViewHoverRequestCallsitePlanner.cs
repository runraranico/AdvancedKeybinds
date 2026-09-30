using System;
using System.Collections.Generic;

namespace AdvancedKeybinds.Features.ListView
{
	/// <summary>Minimal IL view for callsite matching (Cecil + MonoMod).</summary>
	internal readonly struct BindingListViewHoverRequestIlInsn
	{
		public readonly string OpCode;
		public readonly string Operand;

		public BindingListViewHoverRequestIlInsn(string opCode, string operand)
		{
			OpCode = opCode ?? string.Empty;
			Operand = operand ?? string.Empty;
		}
	}

	internal readonly struct BindingListViewHoverRequestCallsite
	{
		public readonly string LanguageKey;
		public readonly int CallIndex;
		public readonly string CalledMethodName;

		public BindingListViewHoverRequestCallsite(string languageKey, int callIndex, string calledMethodName)
		{
			LanguageKey = languageKey;
			CallIndex = callIndex;
			CalledMethodName = calledMethodName;
		}
	}

	/// <summary>
	/// Exact vanilla utility / BankButtons GetHoverText request callsites.
	/// Identity is language-key ldstr (not display-text equality). CraftingWindow is never selected.
	/// </summary>
	internal static class BindingListViewHoverRequestCallsitePlanner
	{
		public const string KeyQuickStack = "GameUI.QuickStackToNearby";
		public const string KeySortInventory = "GameUI.SortInventory";
		public const string KeyEmote = "GameUI.Emote";
		public const string KeyBestiary = "GameUI.Bestiary";
		public const string KeyCraftingWindow = "GameUI.CraftingWindow";

		public const int ExpectedDrawInventoryUtilityCalls = 2;
		public const int ExpectedEmoteCalls = 1;
		public const int ExpectedBestiaryCalls = 1;
		public const int ExpectedBankGetHoverTextHackZoomCalls = 1;

		private static readonly HashSet<string> UtilityKeys = new HashSet<string>(StringComparer.Ordinal)
		{
			KeyQuickStack, KeySortInventory, KeyEmote, KeyBestiary
		};

		public static List<BindingListViewHoverRequestCallsite> FindVanillaUtilityMouseTextCalls(
			IReadOnlyList<BindingListViewHoverRequestIlInsn> body)
		{
			var result = new List<BindingListViewHoverRequestCallsite>(4);
			if (body == null || body.Count == 0)
				return result;

			for (int i = 0; i < body.Count; i++)
			{
				if (!IsMouseTextCall(body[i]))
					continue;
				if (!TryFindPrecedingUtilityLdstr(body, i, out string key))
					continue;
				result.Add(new BindingListViewHoverRequestCallsite(key, i, "MouseText"));
			}

			return result;
		}

		public static List<int> FindBankButtonsGetHoverTextHackZoomCalls(
			IReadOnlyList<BindingListViewHoverRequestIlInsn> body)
		{
			var result = new List<int>(1);
			if (body == null || body.Count == 0)
				return result;

			for (int i = 0; i < body.Count; i++)
			{
				if (!IsCallNamed(body[i], "MouseTextHackZoom"))
					continue;

				bool sawGetHoverText = false;
				int start = Math.Max(0, i - 8);
				for (int j = i - 1; j >= start; j--)
				{
					if (IsCallNamed(body[j], "get_GetHoverText") || IsCallNamed(body[j], "GetHoverText"))
					{
						sawGetHoverText = true;
						break;
					}
				}

				if (sawGetHoverText)
					result.Add(i);
			}

			return result;
		}

		private static bool TryFindPrecedingUtilityLdstr(
			IReadOnlyList<BindingListViewHoverRequestIlInsn> body,
			int callIndex,
			out string key)
		{
			key = null;
			int start = Math.Max(0, callIndex - 12);
			for (int j = callIndex - 1; j >= start; j--)
			{
				BindingListViewHoverRequestIlInsn insn = body[j];
				if (!string.Equals(insn.OpCode, "ldstr", StringComparison.Ordinal))
					continue;

				if (string.Equals(insn.Operand, KeyCraftingWindow, StringComparison.Ordinal))
					return false;

				if (!UtilityKeys.Contains(insn.Operand))
					continue;

				key = insn.Operand;
				return true;
			}

			return false;
		}

		private static bool IsMouseTextCall(BindingListViewHoverRequestIlInsn insn)
		{
			if (!IsCallOp(insn.OpCode))
				return false;
			string name = insn.Operand;
			if (name.Contains("MouseTextHackZoom"))
				return false;
			return name == "MouseText"
				|| name.EndsWith(".MouseText", StringComparison.Ordinal)
				|| name.Contains("::MouseText")
				|| name.Contains("MouseText(");
		}

		private static bool IsCallNamed(BindingListViewHoverRequestIlInsn insn, string methodName)
		{
			if (!IsCallOp(insn.OpCode))
				return false;
			string op = insn.Operand;
			return op == methodName
				|| op.EndsWith("." + methodName, StringComparison.Ordinal)
				|| op.Contains("::" + methodName)
				|| op.Contains(methodName + "(");
		}

		private static bool IsCallOp(string opCode)
			=> opCode == "call" || opCode == "callvirt";
	}
}
