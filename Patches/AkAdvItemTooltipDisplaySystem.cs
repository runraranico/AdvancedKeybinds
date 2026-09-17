using System.Collections.Generic;
using AdvancedKeybinds.Binding;
using AdvancedKeybinds.Core;
using Terraria;
using Terraria.ModLoader;

namespace AdvancedKeybinds.Patches
{
	/// <summary>
	/// Display-only: item tooltips that embed raw <c>AKAdv:</c> KeyStatus markers
	/// (Calamity Infernum Wayfinder, accessories, any mod) are rewritten to AK Controls labels.
	/// Never mutates GetAssignedKeys / KeyStatus / ownership / input.
	/// </summary>
	public sealed class AkAdvItemTooltipDisplaySystem : GlobalItem
	{
		public override bool InstancePerEntity => false;

		public override void ModifyTooltips(Item item, List<TooltipLine> tooltips)
		{
			if (tooltips == null || tooltips.Count == 0)
				return;

			for (int i = 0; i < tooltips.Count; i++)
			{
				TooltipLine line = tooltips[i];
				if (line == null || string.IsNullOrEmpty(line.Text))
					continue;
				if (line.Text.IndexOf(AdvancedVanillaOwnershipMarker.Prefix, System.StringComparison.Ordinal) < 0)
					continue;

				string replaced = BindingDisplayText.ReplaceOwnershipMarkersInDisplayText(
					line.Text,
					BindingDisplayResolver.ResolveDisplayName);
				if (!string.IsNullOrEmpty(replaced) && replaced != line.Text)
					line.Text = replaced;
			}
		}
	}
}
