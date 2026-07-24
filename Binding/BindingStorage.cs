using System;
using System.Collections.Generic;
using System.IO;
using AdvancedKeybinds.Core;
using Terraria;
using Terraria.GameInput;

namespace AdvancedKeybinds.Binding
{
	/// <summary>
	/// Persists advanced (non-vanilla) combo bindings to a JSON file, keyed by action id.
	/// Multi-key / hold / mouse / hook-managed bindings are stored here for input.
	/// Plain single-key taps are also stored so the combo panel can restore left/middle/right slot
	/// placement, while vanilla still owns the controls-list label and gameplay input for those.
	/// </summary>
	public static class BindingStorage
	{
		private static readonly Dictionary<string, ComboBinding> Combos = new();

		private static string FilePath => Path.Combine(Main.SavePath, "AdvancedKeybinds", "bindings.json");

		public static void Initialize()
		{
			LoadFromFile();
			if (SanitizeLoadedBindings())
				SaveToFile();
			SyncPlainSingleKeyLayoutsToVanilla();
			SyncAdvancedOwnershipMarkersToVanilla();
			PromoteHookManagedVanillaBindings();
			// Profile may be null during Mod.Load / Build+Reload — repair again when ready.
			EnsureVanillaOwnershipConsistent("initialize-ensure");
			BindingInputEngine.InvalidateBindings();
#if AK_DIAGNOSTICS
			BindingMutationDiag.ResetObservationBaseline();
			BindingMutationDiag.LogWatchedBaseline("BindingStorage.Initialize");
#endif
		}

		/// <summary>
		/// Re-applies KeyStatus from Combos when mismatched (empty, leftover RegisterKeybind default,
		/// stale markers, mixed residues). Does not rewrite bindings.json. Safe to call after other
		/// mods' EnsureDefault / profile rebuild. Idempotent when already consistent.
		/// Hot path (no repair): zero List alloc — compares slots to KeyStatus in place.
		/// </summary>
		public static int EnsureVanillaOwnershipConsistent(string reason = null)
			=> EnsureVanillaOwnershipConsistent(reason, out _);

		/// <inheritdoc cref="EnsureVanillaOwnershipConsistent(string)"/>
		public static int EnsureVanillaOwnershipConsistent(string reason, out bool profileReady)
		{
			profileReady = VanillaRebindHelper.IsProfileReady();
			if (!profileReady)
				return 0;

			string syncReason = reason ?? "ensure-ownership";
			int repaired = 0;
			foreach (KeyValuePair<string, ComboBinding> pair in Combos)
			{
				ComboBinding combo = pair.Value;
				if (combo == null || combo.IsEmpty)
					continue;

				if (VanillaRebindHelper.AssignedOwnershipMatchesSlots(
					pair.Key, combo.Hold, combo.Slot1, combo.Slot2, combo.Slot3))
					continue;

				SyncVanillaKeyStatusForAction(pair.Key, combo, syncReason + "-repair");
				repaired++;
			}

			return repaired;
		}

		/// <summary>
		/// Drops unsupported Hold flags. LeftClick-alone is user-confirmable and is not removed on load.
		/// Returns true when the in-memory set changed and should be persisted.
		/// </summary>
		private static bool SanitizeLoadedBindings()
		{
			bool changed = false;

			foreach (KeyValuePair<string, ComboBinding> pair in Combos)
			{
				ComboBinding combo = pair.Value;
				if (combo == null)
					continue;

				bool holdBefore = combo.Hold;
				bool holdOnReleaseBefore = combo.HoldOnRelease;
				BindingRestrictions.ClearUnsupportedHoldFlags(combo);
				if (holdBefore != combo.Hold || holdOnReleaseBefore != combo.HoldOnRelease)
				{
					changed = true;
					if (AkDiag.Enabled) AkDiag.Log("Sanitized unsupported Hold on load action=" + pair.Key);
				}
			}

			return changed;
		}

		/// <summary>
		/// Writes KeyStatus to match a stored combo (or clears when null/empty).
		/// Empty = user unset — never restores RegisterKeybind defaults here.
		/// </summary>
		private static void SyncVanillaKeyStatusForAction(string actionId, ComboBinding combo, string reason)
		{
			if (string.IsNullOrEmpty(actionId))
				return;

			if (combo == null || combo.IsEmpty)
			{
				VanillaRebindHelper.ClearVanillaBinding(actionId, reason ?? "sync-empty");
				return;
			}

			if (combo.IsPlainSingleTap)
			{
				VanillaRebindHelper.AssignVanillaBinding(
					actionId,
					combo.Keys[0],
					reason ?? "sync-plain");
				return;
			}

			if (combo.RequiresAdvancedStorage())
			{
				VanillaRebindHelper.AssignAdvancedOwnershipMarkers(
					actionId,
					combo.Keys,
					reason ?? "sync-advanced-markers");
				return;
			}

			VanillaRebindHelper.ClearVanillaBinding(actionId, reason ?? "sync-empty-shape");
		}

		/// <summary>
		/// Plain Keys-name single-key layouts are kept for combo-panel slot memory, but vanilla must
		/// own the key so the controls list shows the normal centered label.
		/// Measured SC:/VK: singles are not synced here (they are advanced-owned).
		/// </summary>
		private static void SyncPlainSingleKeyLayoutsToVanilla()
		{
			bool changed = false;
			foreach (KeyValuePair<string, ComboBinding> pair in Combos)
			{
				ComboBinding combo = pair.Value;
				if (combo == null || !combo.IsPlainSingleTap)
					continue;

				SyncVanillaKeyStatusForAction(pair.Key, combo, "sync-plain-to-vanilla");
				changed = true;
			}

			if (changed)
				if (AkDiag.Enabled) AkDiag.Log("Synced plain single-key layouts to vanilla for list display.");
		}

		/// <summary>
		/// Advanced-owned bindings (SC:/VK:/mouse/combo/HOLD) must not leave KeyStatus empty:
		/// empty lists are treated as unbound by many ModKeybind consumers. Sync non-firing
		/// ownership markers instead of clearing (see <see cref="AdvancedVanillaOwnershipMarker"/>).
		/// </summary>
		private static void SyncAdvancedOwnershipMarkersToVanilla()
		{
			foreach (KeyValuePair<string, ComboBinding> pair in Combos)
			{
				ComboBinding combo = pair.Value;
				if (combo == null || !combo.RequiresAdvancedStorage())
					continue;

				SyncVanillaKeyStatusForAction(pair.Key, combo, "load-sync-advanced-markers");
			}
		}

		public static void Unload()
		{
			// Drop AKAdv markers: write one vanilla-fireable key from each advanced combo (Keyboard +
			// KeyboardUI), then PlayerInput.Save so input profiles.json no longer holds dead markers.
			// bindings.json is not rewritten — Combos are only cleared from memory.
			UnloadVanillaKeyRestore.RestoreAdvancedOwnedFromCombos(Combos, out _);
			Combos.Clear();
			ControlsBindingUndo.Clear();
		}

		public static ComboBinding Get(string actionId)
		{
			if (string.IsNullOrEmpty(actionId) || !Combos.TryGetValue(actionId, out ComboBinding combo))
				return null;

			return combo;
		}

		public static bool HasAdvancedBindings(string actionId)
		{
			return Get(actionId) != null;
		}

		/// <summary>
		/// Stores the combo for an action. Plain single-key taps are kept so the combo panel can restore
		/// left/middle/right slot placement; input/list display for those still go through vanilla.
		/// KeyStatus is always synced here (advanced markers / plain Keys name / empty).
		/// </summary>
		public static void Set(string actionId, ComboBinding combo, string reason = null)
		{
			if (string.IsNullOrEmpty(actionId))
				return;

			string syncReason = reason ?? "BindingStorage.Set";
#if AK_DIAGNOSTICS
			string before = BindingMutationDiag.FormatComboSnapshot(Get(actionId));
#endif
			if (combo == null || combo.IsEmpty)
			{
				Combos.Remove(actionId);
				SyncVanillaKeyStatusForAction(actionId, null, syncReason + "-empty");
			}
			else
			{
				Combos[actionId] = combo.Clone();
				SyncVanillaKeyStatusForAction(actionId, Combos[actionId], syncReason);
			}

#if AK_DIAGNOSTICS
			string after = BindingMutationDiag.FormatComboSnapshot(Get(actionId));
			BindingMutationDiag.LogAdvancedMutation(actionId, before, after, syncReason);
#endif

			SaveToFile();
			BindingInputEngine.InvalidateBindings();
		}

		public static void ClearBindings(string actionId, string reason = null)
		{
			if (string.IsNullOrEmpty(actionId))
				return;

#if AK_DIAGNOSTICS
			string before = BindingMutationDiag.FormatComboSnapshot(Get(actionId));
#endif
			if (!Combos.Remove(actionId))
			{
				// Combo already gone — still drop leftover ownership markers (ghost KeyStatus).
				SyncVanillaKeyStatusForAction(actionId, null, (reason ?? "BindingStorage.ClearBindings") + "-orphan-markers");
				return;
			}

			SyncVanillaKeyStatusForAction(actionId, null, (reason ?? "BindingStorage.ClearBindings") + "-empty");
#if AK_DIAGNOSTICS
			BindingMutationDiag.LogAdvancedMutation(actionId, before, "(none)", reason ?? "BindingStorage.ClearBindings");
#endif
			SaveToFile();
			BindingInputEngine.InvalidateBindings();
		}

		public static IEnumerable<KeyValuePair<string, ComboBinding>> AllBindings => Combos;

		/// <summary>
		/// Clears every per-slot display override across all stored bindings. Key assignments and
		/// Hold flags are left unchanged. Returns how many slot-display fields were cleared.
		/// </summary>
		public static int ClearAllSlotDisplays()
		{
			int cleared = 0;
			foreach (KeyValuePair<string, ComboBinding> pair in Combos)
			{
				ComboBinding combo = pair.Value;
				if (combo == null)
					continue;

				cleared += ClearSlotDisplaysOn(combo);
			}

			if (cleared > 0)
				SaveToFile();

			return cleared;
		}

		/// <summary>
		/// Clears per-slot display overrides that target <paramref name="token"/> across all bindings.
		/// </summary>
		public static int ClearSlotDisplaysForToken(string token)
		{
			string key = KeyDisplayAliasStorage.ResolveStorageKey(token);
			if (string.IsNullOrEmpty(key))
				return 0;

			int cleared = 0;
			foreach (KeyValuePair<string, ComboBinding> pair in Combos)
			{
				ComboBinding combo = pair.Value;
				if (combo == null)
					continue;

				for (int i = 1; i <= 3; i++)
				{
					string slot = combo.GetSlot(i);
					if (string.IsNullOrEmpty(slot))
						continue;

					string slotKey = KeyDisplayAliasStorage.ResolveStorageKey(slot);
					if (!string.Equals(slotKey, key, StringComparison.OrdinalIgnoreCase))
						continue;

					if (string.IsNullOrEmpty(combo.GetSlotDisplay(i)))
						continue;

					combo.SetSlotDisplay(i, null);
					cleared++;
				}
			}

			if (cleared > 0)
				SaveToFile();

			return cleared;
		}

		private static int ClearSlotDisplaysOn(ComboBinding combo)
		{
			int cleared = 0;
			for (int i = 1; i <= 3; i++)
			{
				if (string.IsNullOrEmpty(combo.GetSlotDisplay(i)))
					continue;
				combo.SetSlotDisplay(i, null);
				cleared++;
			}

			return cleared;
		}

		/// <summary>True when any stored combo slot uses LeftWindows / RightWindows.</summary>
		public static bool AnyBindingUsesWindowsKey()
		{
			foreach (KeyValuePair<string, ComboBinding> pair in Combos)
			{
				ComboBinding combo = pair.Value;
				if (combo == null)
					continue;

				foreach (string key in combo.Keys)
				{
					if (BindingRestrictions.IsWindowsKeyToken(key))
						return true;
				}
			}

			return false;
		}

		public static void PromoteHookManagedVanillaBindings()
		{
			PlayerInputProfile profile = PlayerInput.CurrentProfile;
			if (profile?.InputModes == null)
				return;

			if (!profile.InputModes.TryGetValue(InputMode.Keyboard, out KeyConfiguration config)
				&& !profile.InputModes.TryGetValue(InputMode.KeyboardUI, out config))
			{
				foreach (KeyValuePair<InputMode, KeyConfiguration> pair in profile.InputModes)
				{
					config = pair.Value;
					break;
				}
			}

			if (config?.KeyStatus == null)
				return;

			bool changed = false;
			foreach (KeyValuePair<string, List<string>> pair in config.KeyStatus)
			{
				if (Combos.ContainsKey(pair.Key))
					continue;

				List<string> keys = pair.Value;
				if (keys == null || keys.Count != 1)
					continue;

				if (!BindingRestrictions.IsHookManagedTapToken(keys[0]))
					continue;

				string key = keys[0];
				var promoted = new ComboBinding { Slot3 = key };
				Combos[pair.Key] = promoted;
				// Must not leave KeyStatus empty after promote (same ghost-unbound failure mode).
				SyncVanillaKeyStatusForAction(pair.Key, promoted, "promote-hook-managed");
				changed = true;
#if AK_DIAGNOSTICS
				string beforeVan = VanillaRebindHelper.FormatAssignedKeys(pair.Key);
				BindingMutationDiag.LogVanillaMutation(
					pair.Key,
					beforeVan,
					VanillaRebindHelper.FormatAssignedKeys(pair.Key),
					"promote-hook-managed");
				BindingMutationDiag.LogAdvancedMutation(
					pair.Key,
					"(none)",
					BindingMutationDiag.FormatComboSnapshot(Combos[pair.Key]),
					"promote-hook-managed");
#endif
				if (AkDiag.Enabled) AkDiag.Log("Promoted hook-managed vanilla binding action=" + pair.Key + " key=" + key);
			}

			if (changed)
				SaveToFile();
		}

		private static void LoadFromFile()
		{
			Combos.Clear();

			try
			{
				if (!File.Exists(FilePath))
					return;

				string json = File.ReadAllText(FilePath);
				BindingJsonCodec.LoadResult loaded = BindingJsonCodec.Load(json);

				if (loaded.Kind == BindingJsonCodec.LoadKind.OldListFormat)
				{
					// A file from the old (list-based) format can't be read into the new combo format.
					// Discard it so we start clean instead of throwing on every launch.
					AdvancedKeybinds.Instance?.Logger.Warn("Advanced Keybinds: resetting incompatible bindings file (old format).");
					TryDeleteFile();
					return;
				}

				if (loaded.Kind == BindingJsonCodec.LoadKind.InvalidJson)
				{
					AdvancedKeybinds.Instance?.Logger.Warn(
						"Advanced Keybinds failed to load bindings: " + (loaded.Message ?? "invalid json"));
					return;
				}

				if (loaded.Bindings == null || loaded.Bindings.Count == 0)
					return;

				foreach (KeyValuePair<string, ComboBindingData> pair in loaded.Bindings)
				{
					ComboBinding combo = FromData(pair.Value);
					if (combo == null || combo.IsEmpty)
						continue;

					Combos[pair.Key] = combo;
				}
			}
			catch (Exception ex)
			{
				AdvancedKeybinds.Instance?.Logger.Warn("Advanced Keybinds failed to load bindings: " + ex.Message);
			}
		}

		private static ComboBinding FromData(ComboBindingData data)
		{
			if (data == null)
				return null;

			return new ComboBinding {
				Slot1 = data.Slot1,
				Slot2 = data.Slot2,
				Slot3 = data.Slot3,
				Slot1Display = data.Slot1Display,
				Slot2Display = data.Slot2Display,
				Slot3Display = data.Slot3Display,
				Hold = data.Hold,
				HoldOnRelease = data.HoldOnRelease,
				HoldMs = data.HoldMs
			};
		}

		private static ComboBindingData ToData(ComboBinding combo)
		{
			if (combo == null)
				return null;

			return BindingJsonCodec.SanitizeBinding(new ComboBindingData {
				Slot1 = combo.Slot1,
				Slot2 = combo.Slot2,
				Slot3 = combo.Slot3,
				Slot1Display = combo.Slot1Display,
				Slot2Display = combo.Slot2Display,
				Slot3Display = combo.Slot3Display,
				Hold = combo.Hold,
				HoldOnRelease = combo.HoldOnRelease,
				HoldMs = combo.HoldMs
			});
		}

		private static void TryDeleteFile()
		{
			try
			{
				if (File.Exists(FilePath))
					File.Delete(FilePath);
			}
			catch
			{
			}
		}

		private static void SaveToFile()
		{
			try
			{
				string directory = Path.GetDirectoryName(FilePath);
				if (!string.IsNullOrEmpty(directory))
					Directory.CreateDirectory(directory);

				var data = new Dictionary<string, ComboBindingData>(Combos.Count, StringComparer.Ordinal);
				foreach (KeyValuePair<string, ComboBinding> pair in Combos)
				{
					ComboBindingData dto = ToData(pair.Value);
					if (dto == null || dto.IsEmpty)
						continue;
					data[pair.Key] = dto;
				}

				File.WriteAllText(FilePath, BindingJsonCodec.Save(data));
			}
			catch (Exception ex)
			{
				AdvancedKeybinds.Instance?.Logger.Warn("Advanced Keybinds failed to save bindings: " + ex.Message);
			}
		}
	}
}
