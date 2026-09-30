using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using AdvancedKeybinds.Binding;
using AdvancedKeybinds.Core;
using Terraria.GameInput;
using Terraria.ModLoader;

namespace AdvancedKeybinds.Features.DoubleTap
{
	/// <summary>
	/// Stage2C: registration check, inactive OR-only execution view, expected markers, target modes.
	/// Does not connect Judge / UI / live double-tap fire.
	/// </summary>
	internal static class DoubleTapBindingBridge
	{
		private static readonly FieldInfo RegisteredField = typeof(KeybindLoader).GetField(
			"modKeybinds", BindingFlags.NonPublic | BindingFlags.Static);

		internal static bool IsEligibleStorage(string storageKey)
		{
			if (string.IsNullOrEmpty(storageKey))
				return false;
			string bare = storageKey;
			if (storageKey.StartsWith("KU:", StringComparison.Ordinal)
				|| storageKey.StartsWith("GP:G:", StringComparison.Ordinal)
				|| storageKey.StartsWith("GP:U:", StringComparison.Ordinal))
				return false;
			if (DoubleTapTargetPolicy.IsVanillaTarget(storageKey))
				return true;
			if (storageKey.StartsWith("GP:", StringComparison.Ordinal))
				bare = storageKey.Substring(3);
			if (DoubleTapTargetPolicy.IsVanillaTarget(bare))
				return false;
			if (RegisteredField?.GetValue(null) is not IDictionary map)
				return false;
			bool registered = map.Contains(bare) && map[bare] != null;
			return DoubleTapTargetPolicy.IsEligible(bare, registered);
		}

		internal static ComboBinding CreateInactiveView(ComboBinding stored, bool eligible)
		{
			if (stored == null || !stored.DoubleTap)
				return stored;
			DoubleTapRoute route = DoubleTapRoutePolicy.Decide(
				stored.DoubleTap,
				stored.Hold,
				stored.FilledSlotCount,
				eligible,
				featureEnabled: false,
				judgeConnected: false);
			if (route != DoubleTapRoute.Inactive)
				throw new InvalidOperationException("Stage2C must remain inactive");
			if (!eligible)
				return new ComboBinding();
			return new ComboBinding {
				Slot1 = stored.HasAlternateGamePad ? stored.AlternateGamePadToken : null,
				AdditionalOrToken = stored.AdditionalOrToken
			};
		}

		internal static List<string> ExpectedMarkers(ComboBinding stored)
		{
			if (stored == null)
				return new List<string> { AdvancedVanillaOwnershipMarker.OwnedFallback };
			var keys = new List<string>(stored.Keys);
			if (stored.HasAlternateGamePad)
				keys.Add(stored.AlternateGamePadToken);
			if (stored.HasAdditionalOr)
				keys.Add(stored.AdditionalOrToken);
			return AdvancedVanillaOwnershipMarker.BuildMarkers(keys);
		}

		internal static bool OwnsDoubleTapInput(string storageKey, ComboBinding stored)
			=> stored != null && stored.DoubleTap && IsEligibleStorage(storageKey);

		internal static InputMode[] Modes(string storageKey)
		{
			if (string.IsNullOrEmpty(storageKey)
				|| storageKey.StartsWith("KU:", StringComparison.Ordinal)
				|| storageKey.StartsWith("GP:G:", StringComparison.Ordinal)
				|| storageKey.StartsWith("GP:U:", StringComparison.Ordinal))
				return Array.Empty<InputMode>();
			bool pad = storageKey.StartsWith("GP:", StringComparison.Ordinal);
			string bare = pad ? storageKey.Substring(3) : storageKey;
			if (DoubleTapTargetPolicy.IsVanillaTarget(bare))
				return pad ? Array.Empty<InputMode>() : new[] { InputMode.Keyboard };
			if (!DoubleTapTargetPolicy.IsEligible(bare, true))
				return Array.Empty<InputMode>();
			return pad
				? new[] { InputMode.XBoxGamepad, InputMode.XBoxGamepadUI }
				: new[] { InputMode.Keyboard, InputMode.KeyboardUI };
		}
	}
}
