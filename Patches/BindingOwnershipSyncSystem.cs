using AdvancedKeybinds.Binding;
using Terraria.ModLoader;

namespace AdvancedKeybinds.Patches
{
	/// <summary>
	/// Re-syncs vanilla KeyStatus ownership markers after other mods' PostSetup / world-load
	/// EnsureDefault runs, without requiring the user to open the combo panel.
	/// </summary>
	public sealed class BindingOwnershipSyncSystem : ModSystem
	{
		public override void PostSetupContent()
		{
			BindingInputEngine.RequestVanillaOwnershipEnsure();
			BindingStorage.EnsureVanillaOwnershipConsistent("post-setup-content");
		}

		public override void OnWorldLoad()
		{
			BindingInputEngine.RequestVanillaOwnershipEnsure();
			BindingStorage.EnsureVanillaOwnershipConsistent("world-load");
		}
	}
}
