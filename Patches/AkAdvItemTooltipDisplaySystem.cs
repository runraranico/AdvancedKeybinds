using System;
using System.Collections.Generic;
using System.Reflection;
using AdvancedKeybinds.Binding;
using AdvancedKeybinds.Core;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.GameInput;
using Terraria.GameContent.UI.Elements;
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
		private delegate List<TooltipLine> TooltipOrig(Item item, ref int count, string[] names,
			ref string[] text, ref bool[] modifier, ref bool[] badModifier, ref int logo,
			ref Color?[] colors, int prefixLine);
		private delegate List<TooltipLine> TooltipHook(TooltipOrig orig, Item item, ref int count,
			string[] names, ref string[] text, ref bool[] modifier, ref bool[] badModifier,
			ref int logo, ref Color?[] colors, int prefixLine);
		private delegate List<string> AssignedOrig(ModKeybind self, InputMode mode);
		private delegate List<string> AssignedHook(AssignedOrig orig, ModKeybind self, InputMode mode);
		private delegate string GenInputOrig(UIKeybindingListItem self, List<string> keys);
		private delegate string GenInputHook(GenInputOrig orig, UIKeybindingListItem self, List<string> keys);
		private static readonly FieldInfo GenInputMode = typeof(UIKeybindingListItem)
			.GetField("_inputmode", BindingFlags.Instance | BindingFlags.NonPublic);

		public override bool InstancePerEntity => false;

		internal static void ApplyDisplayHooks()
		{
			ModDetours.Apply(typeof(ItemLoader).GetMethod(nameof(ItemLoader.ModifyTooltips)),
				(TooltipHook)ModifyTooltipDisplay);
			ModDetours.Apply(typeof(ModKeybind).GetMethod(nameof(ModKeybind.GetAssignedKeys)),
				(AssignedHook)ReadAssignedKeys);
			ModDetours.Apply(typeof(UIKeybindingListItem).GetMethod("GenInput", BindingFlags.Instance | BindingFlags.NonPublic),
				(GenInputHook)GenerateInputDisplay);
		}

		private static List<string> ReadAssignedKeys(AssignedOrig orig, ModKeybind self, InputMode mode)
		{
			List<string> keys = orig(self, mode);
			ItemTooltipBindingContext context = ItemTooltipBindingContext.Current;
			if (context != null && IsSupportedDisplayRead(mode, keys)
				&& BindingDisplayText.NeedsOverrideFromAssignedKeys(keys))
			{
				try
				{
					string actionId = ModKeybindReflection.GetActionId(self);
					string storageKey = ControlsGameUiSeparationPolicy.ResolveStorageKey((int)mode, actionId);
					if (TryGetCurrentAssignmentDisplay(storageKey, keys, out string label))
						context.Record(storageKey, keys, label);
					else
						context.MarkUnresolved(keys);
				}
				catch (Exception)
				{
					// Display lookup must not add failures to an otherwise successful input API read.
				}
			}
			return keys; // Same object and elements, including outside tooltip generation.
		}

		internal static string ResolveTooltipKeyReference(string value, IReadOnlyList<string> otherText)
		{
			if (ItemTooltipBindingContext.Current == null || !AdvancedVanillaOwnershipMarker.IsMarker(value))
				return value;
			try
			{
				string display = null;
				foreach (var inputMode in PlayerInput.CurrentProfile.InputModes)
				{
					if (inputMode.Value?.KeyStatus == null)
						continue;
					foreach (var pair in inputMode.Value.KeyStatus)
					{
						List<string> keys = pair.Value;
						// The original string reference survives raw comparisons and inlined getters.
						// Equal text from another action is not provenance.
						if (keys == null || keys.Count == 0 || !ReferenceEquals(value, keys[0])
							|| !IsSupportedDisplayRead(inputMode.Key, keys))
							continue;
						string storageKey = ControlsGameUiSeparationPolicy.ResolveStorageKey((int)inputMode.Key, pair.Key);
						if (!TryGetCurrentAssignmentDisplay(storageKey, keys, out string label))
						{
							ItemTooltipBindingContext.Current.MarkUnresolved(keys);
							return value;
						}
						for (int i = 1; i < keys.Count; i++)
							foreach (string text in otherText)
								if (text != null && BindingDisplayText.ContainsOwnershipMarker(text, keys[i]))
									return value; // This display already contains later slots.
						if (display != null && !string.Equals(display, label, StringComparison.Ordinal))
							return value;
						display = label;
					}
				}
				return display ?? value;
			}
			catch (Exception) { return value; }
		}

		internal static string ResolveTooltipJoinedList(IEnumerable<string> values, string original)
		{
			if (ItemTooltipBindingContext.Current == null || values is not List<string> keys)
				return original;
			try
			{
				string display = null;
				foreach (var inputMode in PlayerInput.CurrentProfile.InputModes)
				{
					if (!IsSupportedDisplayRead(inputMode.Key, keys) || inputMode.Value?.KeyStatus == null)
						continue;
					foreach (var pair in inputMode.Value.KeyStatus)
					{
						if (!ReferenceEquals(keys, pair.Value))
							continue;
						string id = ControlsGameUiSeparationPolicy.ResolveStorageKey((int)inputMode.Key, pair.Key);
						if (!TryGetCurrentAssignmentDisplay(id, keys, out string label))
						{
							ItemTooltipBindingContext.Current.MarkUnresolved(keys);
							return original;
						}
						if (display != null && !string.Equals(display, label, StringComparison.Ordinal))
							return original;
						display = label;
					}
				}
				return display ?? original;
			}
			catch (Exception) { return original; }
		}

		private static bool TryGetCurrentAssignmentDisplay(string storageKey, IReadOnlyList<string> keys, out string label)
		{
			label = null;
			ComboBinding combo = BindingStorage.Get(storageKey);
			if (combo == null || combo.IsEmpty)
				return false;
			var expected = new List<string>(combo.Keys);
			string alternate = storageKey.StartsWith("GP:", StringComparison.Ordinal)
				? combo.AlternateGamePadToken : combo.AdditionalOrToken;
			if (!string.IsNullOrEmpty(alternate) && !expected.Contains(alternate))
				expected.Add(alternate);
			return SameKeys(keys, AdvancedVanillaOwnershipMarker.BuildMarkers(expected))
				&& BindingDisplayResolver.TryGetBindingDisplayText(storageKey, keys, out label)
				&& !ContainsInternalKeyName(label);
		}

		private static string GenerateInputDisplay(GenInputOrig orig, UIKeybindingListItem self, List<string> keys)
		{
			string display = orig(self, keys);
			ItemTooltipBindingContext context = ItemTooltipBindingContext.Current;
			if (context == null || GenInputMode?.GetValue(self) is not InputMode mode
				|| !IsSupportedDisplayRead(mode, keys)
				|| !BindingDisplayText.NeedsOverrideFromAssignedKeys(keys))
				return display;
			return context.RewriteGeneratedInput(keys, display);
		}

		private static bool IsSupportedDisplayRead(InputMode mode, IReadOnlyList<string> keys)
		{
			if (keys == null || keys.Count == 0)
				return false;
			bool pad = GamePadToken.IsPadToken(BindingDisplayText.UnwrapOwnershipMarkerForDisplay(keys[0]));
			return pad ? mode is InputMode.XBoxGamepad or InputMode.XBoxGamepadUI
				: mode == InputMode.Keyboard;
		}

		private static List<TooltipLine> ModifyTooltipDisplay(TooltipOrig orig, Item item, ref int count,
			string[] names, ref string[] text, ref bool[] modifier, ref bool[] badModifier,
			ref int logo, ref Color?[] colors, int prefixLine)
		{
			using var context = ItemTooltipBindingContext.Begin();
			context.SetUnobservedResolver(ResolveUnobservedAssignment);
			List<TooltipLine> lines = orig(item, ref count, names, ref text, ref modifier,
				ref badModifier, ref logo, ref colors, prefixLine);
			if (lines == null || text == null || lines.Count != count || text.Length < count)
				return lines;
			bool hasMarkers = false;
			for (int i = 0; i < count; i++)
				hasMarkers |= text[i]?.IndexOf(AdvancedVanillaOwnershipMarker.Prefix, StringComparison.Ordinal) >= 0;
			if (!hasMarkers)
				return lines;
			context.PrepareRewrite(new ArraySegment<string>(text, 0, count));
			for (int i = 0; i < count; i++)
			{
				TooltipLine line = lines[i];
				if (line == null || !string.Equals(line.Text, text[i], StringComparison.Ordinal))
					continue; // Do not rewrite another hook's unmatched array entry.
				string display = context.Rewrite(line.Text, ResolveTooltipKeyName);
				line.Text = text[i] = display;
			}
			return lines;
		}

		// GetAssignedKeys is small enough to be inlined before these hooks are installed.
		// Read current owned assignments only when that observation is missing; never edit input.
		private static string ResolveUnobservedAssignment(IReadOnlyList<string> markers, string text)
		{
			try
			{
				if (markers == null || markers.Count == 0 || PlayerInput.CurrentProfile == null)
					return null;
				string result = null;
				bool alreadyShown = false;
				foreach (var inputMode in PlayerInput.CurrentProfile.InputModes)
				{
					if (inputMode.Value?.KeyStatus == null || !IsSupportedDisplayRead(inputMode.Key, markers))
						continue;
					foreach (var pair in inputMode.Value.KeyStatus)
					{
					List<string> keys = pair.Value;
					if (keys == null || keys.Count == 0)
						continue;
					bool owned = true;
					for (int i = 0; i < keys.Count; i++)
						owned &= AdvancedVanillaOwnershipMarker.IsMarker(keys[i]);
					if (!owned)
					{
						// A mixed OR consumer can print any marker, not just its first entry.
						foreach (string key in keys)
							foreach (string marker in markers)
								if (string.Equals(key, marker, StringComparison.Ordinal))
									return null;
						continue;
					}
					if (!(markers.Count == 1
						? string.Equals(markers[0], keys[0], StringComparison.Ordinal)
						: SameKeys(markers, keys)))
						continue;
					string storageKey = ControlsGameUiSeparationPolicy.ResolveStorageKey((int)inputMode.Key, pair.Key);
					ComboBinding combo = BindingStorage.Get(storageKey);
					// A stale or unprovable matching candidate must not manufacture certainty.
					if (combo == null || combo.IsEmpty
						|| !SameKeys(keys, AdvancedVanillaOwnershipMarker.BuildMarkers(combo.Keys))
						|| !BindingDisplayResolver.TryGetBindingDisplayText(storageKey, keys, out string label)
						|| ContainsInternalKeyName(label))
						return null;
					if (result != null && !string.Equals(result, label, StringComparison.Ordinal))
						return null;
					result = label;
					if (markers.Count == 1 && text != null)
						for (int i = 1; i < keys.Count; i++)
							alreadyShown |= BindingDisplayText.ContainsOwnershipMarker(text, keys[i]);
					}
				}
				return alreadyShown ? null : result;
			}
			catch (Exception)
			{
				return null; // Missing/changing display state cannot fail tooltip generation.
			}
		}

		private static bool SameKeys(IReadOnlyList<string> left, IReadOnlyList<string> right)
		{
			if (left.Count != right.Count)
				return false;
			for (int i = 0; i < left.Count; i++)
				if (!string.Equals(left[i], right[i], StringComparison.Ordinal))
					return false;
			return true;
		}

		private static string ResolveTooltipKeyName(string marker)
		{
			try
			{
				string label = BindingDisplayResolver.ResolveDisplayName(marker);
				return string.IsNullOrEmpty(label) || ContainsInternalKeyName(label) ? "?" : label;
			}
			catch (Exception)
			{
				return "?"; // Unknown layout/key: do not invent a key or leak its internal ID.
			}
		}

		private static bool ContainsInternalKeyName(string text)
			=> text.IndexOf("SC:", StringComparison.OrdinalIgnoreCase) >= 0
				|| text.IndexOf("VK:", StringComparison.OrdinalIgnoreCase) >= 0
				|| text.IndexOf(AdvancedVanillaOwnershipMarker.Prefix, StringComparison.Ordinal) >= 0;

		public override void ModifyTooltips(Item item, List<TooltipLine> tooltips)
		{
			// The outer boundary runs after every GlobalItem and updates tML's copied array too.
			if (ItemTooltipBindingContext.Current != null)
				return;
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
					ResolveTooltipKeyName);
				if (!string.IsNullOrEmpty(replaced) && replaced != line.Text)
					line.Text = replaced;
			}
		}
	}

	// GlobalItem.Load runs during Autoload, BEFORE Mod.Load clears stale hooks.
	// Install only after all content has loaded; normal ModDetours teardown owns removal.
	public sealed class AkAdvItemTooltipDisplayHooks : ModSystem
	{
		public override void PostSetupContent()
		{
			AkAdvItemTooltipDisplaySystem.ApplyDisplayHooks();
			if (!ItemTooltipAssignedReadHooks.InstallLoadedTooltips())
				Mod.Logger.Warn("Some item tooltip key labels could not be adapted; input bindings are unchanged.");
		}

		public override void Unload()
		{
			if (!ItemTooltipAssignedReadHooks.DisposeAll())
				Mod.Logger.Warn("Some item tooltip display hooks could not be released; reload is not verified.");
		}
	}
}
