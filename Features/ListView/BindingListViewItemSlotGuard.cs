using System;
using System.Reflection;
using Terraria;
using Terraria.ModLoader;
using Terraria.UI;

namespace AdvancedKeybinds.Features.ListView
{
	/// <summary>
	/// ItemSlot front-ownership guards (same claimFront gate as mouse capture).
	/// LeftClick alone does not stop hover highlight — ItemSlot.MouseHover / Draw use mouse
	/// rects (not UIElement.ContainsPoint). Skip MouseHover when AK claims front.
	/// No mouse coordinate park. No per-mod branches.
	/// </summary>
	internal static class BindingListViewItemSlotGuard
	{
		private static bool _applied;

		private delegate void OrigLeftClickArray(Item[] inv, int context, int slot);
		private delegate void OrigLeftClickRef(ref Item inv, int context);
		private delegate void OrigRightClickArray(Item[] inv, int context, int slot);
		private delegate void OrigRightClickRef(ref Item inv, int context);
		private delegate void OrigMouseHoverArray(Item[] inv, int context, int slot);
		private delegate void OrigMouseHoverRef(ref Item inv, int context);
		private delegate void OrigMouseHoverContext(int context);

		public static void Apply()
		{
			if (_applied)
				return;

			MethodInfo leftArray = typeof(ItemSlot).GetMethod(
				nameof(ItemSlot.LeftClick),
				BindingFlags.Public | BindingFlags.Static,
				binder: null,
				types: new[] { typeof(Item[]), typeof(int), typeof(int) },
				modifiers: null);
			MethodInfo leftRef = typeof(ItemSlot).GetMethod(
				nameof(ItemSlot.LeftClick),
				BindingFlags.Public | BindingFlags.Static,
				binder: null,
				types: new[] { typeof(Item).MakeByRefType(), typeof(int) },
				modifiers: null);
			MethodInfo rightArray = typeof(ItemSlot).GetMethod(
				nameof(ItemSlot.RightClick),
				BindingFlags.Public | BindingFlags.Static,
				binder: null,
				types: new[] { typeof(Item[]), typeof(int), typeof(int) },
				modifiers: null);
			MethodInfo rightRef = typeof(ItemSlot).GetMethod(
				nameof(ItemSlot.RightClick),
				BindingFlags.Public | BindingFlags.Static,
				binder: null,
				types: new[] { typeof(Item).MakeByRefType(), typeof(int) },
				modifiers: null);
			MethodInfo hoverArray = typeof(ItemSlot).GetMethod(
				nameof(ItemSlot.MouseHover),
				BindingFlags.Public | BindingFlags.Static,
				binder: null,
				types: new[] { typeof(Item[]), typeof(int), typeof(int) },
				modifiers: null);
			MethodInfo hoverRef = typeof(ItemSlot).GetMethod(
				nameof(ItemSlot.MouseHover),
				BindingFlags.Public | BindingFlags.Static,
				binder: null,
				types: new[] { typeof(Item).MakeByRefType(), typeof(int) },
				modifiers: null);
			MethodInfo hoverCtx = typeof(ItemSlot).GetMethod(
				nameof(ItemSlot.MouseHover),
				BindingFlags.Public | BindingFlags.Static,
				binder: null,
				types: new[] { typeof(int) },
				modifiers: null);

			if (leftArray == null || leftRef == null)
				throw new InvalidOperationException(
					"ListView ItemSlotGuard: ItemSlot.LeftClick overloads not found for MonoModHooks.Add.");
			if (hoverArray == null || hoverRef == null || hoverCtx == null)
				throw new InvalidOperationException(
					"ListView ItemSlotGuard: ItemSlot.MouseHover overloads not found for MonoModHooks.Add.");

			MonoModHooks.Add(leftArray, HookLeftClickArray);
			MonoModHooks.Add(leftRef, HookLeftClickRef);
			if (rightArray != null)
				MonoModHooks.Add(rightArray, HookRightClickArray);
			if (rightRef != null)
				MonoModHooks.Add(rightRef, HookRightClickRef);
			MonoModHooks.Add(hoverArray, HookMouseHoverArray);
			MonoModHooks.Add(hoverRef, HookMouseHoverRef);
			MonoModHooks.Add(hoverCtx, HookMouseHoverContext);
			_applied = true;
		}

		public static void UnapplyNote()
		{
			_applied = false;
		}

		private static bool Block()
			=> BindingListViewSystem.ShouldBlockInventoryItemSlotLeftClick();

		private static void HookLeftClickArray(OrigLeftClickArray orig, Item[] inv, int context, int slot)
		{
			if (Block())
			{
				BindingListViewClickPathDiag.TraceItemSlotLeftClickBlocked("LeftClick(Item[])", context, slot);
				return;
			}

			orig(inv, context, slot);
		}

		private static void HookLeftClickRef(OrigLeftClickRef orig, ref Item inv, int context)
		{
			if (Block())
			{
				BindingListViewClickPathDiag.TraceItemSlotLeftClickBlocked("LeftClick(ref Item)", context, -1);
				return;
			}

			orig(ref inv, context);
		}

		private static void HookRightClickArray(OrigRightClickArray orig, Item[] inv, int context, int slot)
		{
			if (Block())
				return;
			orig(inv, context, slot);
		}

		private static void HookRightClickRef(OrigRightClickRef orig, ref Item inv, int context)
		{
			if (Block())
				return;
			orig(ref inv, context);
		}

		private static void HookMouseHoverArray(OrigMouseHoverArray orig, Item[] inv, int context, int slot)
		{
			if (Block())
				return;
			orig(inv, context, slot);
		}

		private static void HookMouseHoverRef(OrigMouseHoverRef orig, ref Item inv, int context)
		{
			if (Block())
				return;
			orig(ref inv, context);
		}

		private static void HookMouseHoverContext(OrigMouseHoverContext orig, int context)
		{
			if (Block())
				return;
			orig(context);
		}
	}
}
