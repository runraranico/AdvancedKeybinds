using System.Collections.Generic;
using AdvancedKeybinds.UI;
using Terraria.ModLoader;
using Terraria.UI;

namespace AdvancedKeybinds.Patches
{
	/// <summary>
	/// Closes the combo panel when a world loads or unloads so gameplay bindings are not left paused.
	/// Also re-asserts the controls unset-confirm tip immediately before Vanilla: Mouse Text.
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

		public override void ModifyInterfaceLayers(List<GameInterfaceLayer> layers)
		{
			int mouseTextIndex = layers.FindIndex(layer => layer.Name.Equals("Vanilla: Mouse Text"));
			if (mouseTextIndex == -1)
				return;

			layers.Insert(mouseTextIndex, new LegacyGameInterfaceLayer(
				"AdvancedKeybinds: UnsetConfirmTipPriority",
				delegate
				{
					KeybindingUiPatches.TryForcePendingUnsetConfirmTipPriority();
					return true;
				},
				InterfaceScaleType.UI));
		}
	}
}
