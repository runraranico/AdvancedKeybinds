namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Pure mouse UI resolve used by ComboPanel when background UI poisons Main.mouse.
	/// </summary>
	public static class ComboPanelMouseUiResolve
	{
		public static bool TryResolve(
			int mouseX,
			int mouseY,
			int poisonThreshold,
			bool hasLastGood,
			int lastGoodX,
			int lastGoodY,
			int fallbackX,
			int fallbackY,
			out int x,
			out int y,
			out bool usedLastGood,
			out bool usedFallback)
		{
			usedLastGood = false;
			usedFallback = false;

			if (mouseX > poisonThreshold && mouseY > poisonThreshold)
			{
				x = mouseX;
				y = mouseY;
				return true;
			}

			if (hasLastGood)
			{
				x = lastGoodX;
				y = lastGoodY;
				usedLastGood = true;
				return true;
			}

			x = fallbackX;
			y = fallbackY;
			usedFallback = true;
			return true;
		}
	}
}
