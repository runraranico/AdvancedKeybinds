using Terraria;

namespace AdvancedKeybinds.Features.ListView
{
	/// <summary>
	/// Targets of callsite IL replacement. Invokes original hover request only when
	/// AK ALL KEY is not claiming front ownership. Not a shared final-draw guard.
	/// </summary>
	internal static class BindingListViewHoverRequestSuppressWrappers
	{
		public static void ConditionalMouseText8(
			Main self,
			string cursorText,
			int rare,
			byte diff,
			int hackedMouseX,
			int hackedMouseY,
			int hackedScreenWidth,
			int hackedScreenHeight,
			int pushWidthX)
		{
			if (BindingListViewHoverRequestSuppressGate.ShouldSuppressRequest(
				BindingListViewSystem.ShouldBlockInventoryItemSlotLeftClick()))
				return;
			self.MouseText(
				cursorText, rare, diff,
				hackedMouseX, hackedMouseY, hackedScreenWidth, hackedScreenHeight, pushWidthX);
		}

		public static void ConditionalMouseTextHackZoom2(Main self, string text, string buffTooltip)
		{
			if (BindingListViewHoverRequestSuppressGate.ShouldSuppressRequest(
				BindingListViewSystem.ShouldBlockInventoryItemSlotLeftClick()))
				return;
			self.MouseTextHackZoom(text, buffTooltip);
		}
	}
}
