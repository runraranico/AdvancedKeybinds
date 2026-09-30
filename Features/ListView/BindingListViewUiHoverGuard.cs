using System;
using System.Reflection;
using Microsoft.Xna.Framework;
using Terraria.ModLoader;
using Terraria.UI;

namespace AdvancedKeybinds.Features.ListView
{
	/// <summary>
	/// Generic front-window hover ownership for <see cref="UIElement"/> trees.
	/// 1) <see cref="UIElement.ContainsPoint"/> → false when claimFront (stops default hit tests).
	/// 2) <see cref="UIElement.IsMouseHovering"/> setter → force false when claimFront
	///    (covers subclasses that override ContainsPoint without calling base, and any path
	///    that sets hovering true by other means).
	/// 3) <see cref="UIElement.MouseOver"/> → no-op when claimFront
	///    (stops OnMouseOver listeners that play <c>SoundID.MenuTick</c> / hover SE;
	///    also covers ContainsPoint overrides that still reach UserInterface hover dispatch).
	/// Does not park mouse coordinates. Does not mute SoundEngine globally.
	/// Fast path when list closed / not claiming: one bool check.
	/// </summary>
	internal static class BindingListViewUiHoverGuard
	{
		private static bool _applied;

		private delegate bool OrigContainsPoint(UIElement self, Vector2 point);
		private delegate void OrigSetIsMouseHovering(UIElement self, bool value);
		private delegate void OrigMouseOver(UIElement self, UIMouseEvent evt);

		public static void Apply()
		{
			if (_applied)
				return;

			MethodInfo contains = typeof(UIElement).GetMethod(
				nameof(UIElement.ContainsPoint),
				BindingFlags.Public | BindingFlags.Instance,
				binder: null,
				types: new[] { typeof(Vector2) },
				modifiers: null);

			PropertyInfo hovering = typeof(UIElement).GetProperty(
				nameof(UIElement.IsMouseHovering),
				BindingFlags.Public | BindingFlags.Instance);
			MethodInfo setHovering = hovering?.GetSetMethod(nonPublic: true) ?? hovering?.GetSetMethod(nonPublic: false);

			MethodInfo mouseOver = typeof(UIElement).GetMethod(
				nameof(UIElement.MouseOver),
				BindingFlags.Public | BindingFlags.Instance,
				binder: null,
				types: new[] { typeof(UIMouseEvent) },
				modifiers: null);

			if (contains == null || setHovering == null || mouseOver == null)
				throw new InvalidOperationException(
					"ListView UiHoverGuard: UIElement.ContainsPoint / IsMouseHovering / MouseOver not found.");

			MonoModHooks.Add(contains, HookContainsPoint);
			MonoModHooks.Add(setHovering, HookSetIsMouseHovering);
			MonoModHooks.Add(mouseOver, HookMouseOver);
			_applied = true;
		}

		public static void UnapplyNote()
		{
			_applied = false;
		}

		private static bool HookContainsPoint(OrigContainsPoint orig, UIElement self, Vector2 point)
		{
			if (BindingListViewSystem.ShouldBlockInventoryItemSlotLeftClick())
				return false;

			return orig(self, point);
		}

		private static void HookSetIsMouseHovering(OrigSetIsMouseHovering orig, UIElement self, bool value)
		{
			// Fast path: value already false, or AK not claiming — one bool gate then orig.
			if (value && BindingListViewSystem.ShouldBlockInventoryItemSlotLeftClick())
				value = false;

			orig(self, value);
		}

		private static void HookMouseOver(OrigMouseOver orig, UIElement self, UIMouseEvent evt)
		{
			if (BindingListViewSystem.ShouldBlockInventoryItemSlotLeftClick())
			{
				BindingListViewHoverSoundDiag.NoteSuppressedMouseOver(self);
				// Skip IsMouseHovering=true / OnMouseOver (MenuTick) / parent bubble.
				return;
			}

			orig(self, evt);
		}
	}
}
