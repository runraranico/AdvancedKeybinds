using System;
using System.Collections.Generic;
using AdvancedKeybinds.Binding;
using AdvancedKeybinds.Core;
using Terraria.GameInput;

namespace AdvancedKeybinds.Features.DoubleTap
{
	/// <summary>
	/// Stage2C: DT KeyStatus sync/match for eligible MOD storage keys.
	/// Vanilla DT targets never write or require AKAdv markers.
	/// </summary>
	internal static class DoubleTapOwnershipBridge
	{
		internal static void Sync(string id, ComboBinding combo)
		{
			if (combo == null || !combo.DoubleTap || !DoubleTapBindingBridge.IsEligibleStorage(id))
				return;
			if (DoubleTapTargetPolicy.IsVanillaTarget(id))
				return;
			VanillaRebindHelper.WriteKeyStatusForInputModes(
				ControlsGameUiSeparationPolicy.ToTriggerActionId(id),
				DoubleTapBindingBridge.ExpectedMarkers(combo),
				DoubleTapBindingBridge.Modes(id));
		}

		internal static bool Matches(string id, ComboBinding combo)
		{
			if (!DoubleTapBindingBridge.IsEligibleStorage(id))
				return true;
			if (DoubleTapTargetPolicy.IsVanillaTarget(id))
				return true;
			if (combo == null || !combo.DoubleTap)
				return true;
			List<string> expected = DoubleTapBindingBridge.ExpectedMarkers(combo);
			string trigger = ControlsGameUiSeparationPolicy.ToTriggerActionId(id);
			foreach (InputMode mode in DoubleTapBindingBridge.Modes(id))
			{
				if (!BindingVanillaOwnershipSync.MatchesExpectedKeyStatus(
					    VanillaRebindHelper.GetAssignedKeysForInputMode(trigger, mode),
					    expected))
					return false;
			}
			return true;
		}
	}
}
