using AdvancedKeybinds.UI;
using Terraria.ModLoader;

namespace AdvancedKeybinds.Patches
{
	/// <summary>
	/// Closes the combo panel when a world loads or unloads so gameplay bindings are not left paused.
	/// </summary>
	public class ComboPanelSystem : ModSystem
	{
		public override void OnWorldLoad()
		{
			ComboPanel.ForceClose();
		}

		public override void OnWorldUnload()
		{
			ComboPanel.ForceClose();
		}
	}
}
