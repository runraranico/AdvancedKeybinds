using AdvancedKeybinds.Binding;
using Terraria.ModLoader;

namespace AdvancedKeybinds.Features.DoubleTap
{
	/// <summary>
	/// After late mod keybind registration / world load, force DT eligibility
	/// recompile on the next normal Update. Does not InvalidateBindings or force input.
	/// </summary>
	public sealed class DoubleTapRegistrationRefreshSystem : ModSystem
	{
		public override void PostSetupContent()
		{
			BindingInputEngine.MarkCompiledDirty();
		}

		public override void OnWorldLoad()
		{
			BindingInputEngine.MarkCompiledDirty();
		}
	}
}
