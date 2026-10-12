using System;
using System.Collections.Generic;
using System.IO;
using AdvancedKeybinds.Core;
using AdvancedKeybinds.Diagnostics;
using AdvancedKeybinds.Features.DoubleTap;
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

		/// <summary>
		/// Incremented whenever <see cref="Combos"/> contents change (Set / Clear / Load).
		/// ListView watches this to rebuild AK ALL KEY without per-frame full scans.
		/// </summary>
		public static int CombosMutationRevision { get; private set; }

		private static void NoteCombosMutated()
		{
			unchecked
			{
				CombosMutationRevision++;
			}
		}

		/// <summary>
		/// Set when LoadFromFile hits InvalidJson after Combos.Clear — blocks empty SaveToFile wipe.
		/// Cleared on any successful load that applies bindings (including empty-but-valid files).
		/// </summary>
		private static bool _bindingsLoadFailedKeepDisk;

		private static string FilePath => Path.Combine(Main.SavePath, "AdvancedKeybinds", "bindings.json");

		private static string ProfilesFilePath
			=> Path.Combine(Main.SavePath ?? string.Empty, "input profiles.json");

		/// <summary>Reusable buffer for GamePad two-key Terraria-OR KeyStatus writes (not per-frame).</summary>
		private static readonly string[] TwoKeyKeyStatusScratch = new string[2];

		/// <summary>
		/// Trigger ActionIds whose legacy GP:{action} must not evaluate after explicit ClearBindings.
		/// Memory-only — never written to bindings.json; never removes Combos legacy rows.
		/// </summary>
		private static readonly HashSet<string> LegacyEvaluateSuppressedTriggers =
			new(StringComparer.Ordinal);

		/// <summary>
		/// True when this Combos key syncs GamePad / GamePad UI KeyStatus and must reach
		/// <c>input profiles.json</c> via <see cref="PlayerInput.Save"/> on user commit.
		/// </summary>
		private static bool ShouldPersistPlayerInputProfiles(string storageKey)
		{
			if (string.IsNullOrEmpty(storageKey))
				return false;
			return ControlsGameUiSeparationPolicy.IsGamePadGameStorageKey(storageKey)
				|| ControlsGameUiSeparationPolicy.IsGamePadUiStorageKey(storageKey)
				|| ControlsGameUiSeparationPolicy.IsGamePadLegacySharedStorageKey(storageKey)
				|| ControlsEditTargetPolicy.IsGamePadStorageKey(storageKey);
		}

		/// <summary>
		/// Official <see cref="PlayerInput.Save"/> for GamePad KeyStatus commits.
		/// Call only inside a user edit transaction (ComboPanel save / undo), never per-frame.
		/// </summary>
		internal static bool TryPersistPlayerInputProfilesAfterGamePadEdit(string reason = null)
		{
			_ = reason;
			try
			{
				return PlayerInput.Save();
			}
			catch (Exception ex)
			{
				AdvancedKeybinds.Instance?.Logger.Warn(
					"Advanced Keybinds failed PlayerInput.Save after GamePad edit: " + ex.Message);
				return false;
			}
		}

		private static Dictionary<string, ComboBindingData> CloneAllCombosData()
		{
			var data = new Dictionary<string, ComboBindingData>(Combos.Count, StringComparer.Ordinal);
			foreach (KeyValuePair<string, ComboBinding> pair in Combos)
			{
				ComboBindingData dto = ToData(pair.Key, pair.Value);
				if (dto != null)
					data[pair.Key] = dto.Clone();
			}
			return data;
		}

		private static void ReplaceAllCombosFromData(Dictionary<string, ComboBindingData> snapshot)
		{
			Combos.Clear();
			if (snapshot == null)
			{
				NoteCombosMutated();
				return;
			}
			foreach (KeyValuePair<string, ComboBindingData> pair in snapshot)
			{
				ComboBinding combo = FromData(pair.Value?.Clone());
				if (combo != null)
					Combos[pair.Key] = combo;
			}
			NoteCombosMutated();
		}

		private static bool TryCaptureGamePadPreEdit(
			string storageKey,
			out GamePadUserEditTransaction.PreEditSnapshot snapshot,
			out string error)
		{
			snapshot = null;
			error = null;
			_ = storageKey;
			var io = GamePadUserEditTransaction.CreateDefaultIo();

			if (!GamePadUserEditTransaction.TryCaptureFileBackup(
				    FilePath, io, out GamePadUserEditTransaction.FileBackup bindingsBak, out string bindingsErr))
			{
				error = "bindings-backup:" + bindingsErr;
				return false;
			}

			if (!GamePadUserEditTransaction.TryCaptureFileBackup(
				    ProfilesFilePath, io, out GamePadUserEditTransaction.FileBackup profilesBak, out string profilesErr))
			{
				error = "profiles-backup:" + profilesErr;
				return false;
			}

			snapshot = new GamePadUserEditTransaction.PreEditSnapshot {
				Bindings = bindingsBak,
				Profiles = profilesBak,
				Combos = CloneAllCombosData(),
				KeyStatusGamepad = VanillaRebindHelper.CloneKeyStatusDictionary(InputMode.XBoxGamepad),
				KeyStatusGamepadUi = VanillaRebindHelper.CloneKeyStatusDictionary(InputMode.XBoxGamepadUI),
				OpenKeybindMenuCustomized = ClientPrefs.OpenKeybindMenuCustomized,
				HoldTierHudHotkeyCustomized = ClientPrefs.HoldTierHudHotkeyCustomized
			};
			return true;
		}

		private static void RestoreGamePadPreEditMemory(
			string storageKey,
			GamePadUserEditTransaction.PreEditSnapshot before)
		{
			_ = storageKey;
			ReplaceAllCombosFromData(before.Combos);
			VanillaRebindHelper.ReplaceKeyStatusDictionary(
				InputMode.XBoxGamepad, before.KeyStatusGamepad);
			VanillaRebindHelper.ReplaceKeyStatusDictionary(
				InputMode.XBoxGamepadUI, before.KeyStatusGamepadUi);
			ClientPrefs.OpenKeybindMenuCustomized = before.OpenKeybindMenuCustomized;
			ClientPrefs.HoldTierHudHotkeyCustomized = before.HoldTierHudHotkeyCustomized;
		}

		private static GamePadUserEditTransaction.Io CreateProductionIo()
		{
			GamePadUserEditTransaction.Io io = GamePadUserEditTransaction.CreateDefaultIo();
			io.TrySaveProfiles = () => TryPersistPlayerInputProfilesAfterGamePadEdit("txn");
			io.TryWriteBindingsAtomic = (path, contents) => {
				try
				{
					bool diskHasContent = false;
					try
					{
						if (File.Exists(path))
						{
							FileInfo fi = new FileInfo(path);
							diskHasContent = fi.Length > 0;
						}
					}
					catch
					{
						diskHasContent = false;
					}

					var dataProbe = BindingJsonCodec.Load(contents ?? string.Empty);
					int comboCount = dataProbe.Bindings?.Count ?? 0;
					if (!BindingSaveIntegrityPolicy.ShouldAllowSaveAfterLoadFailure(
						    loadSucceeded: !_bindingsLoadFailedKeepDisk,
						    memoryCombosEmpty: Combos.Count == 0 && comboCount == 0,
						    diskFileExistsWithContent: diskHasContent))
					{
						return false;
					}

					_bindingsLoadFailedKeepDisk = false;
					if (!LegacyGamePadAtomicFileIo.TryWriteAllTextAtomic(path, contents ?? string.Empty))
						return false;

					// Optional validity check: re-read JSON.
					if (File.Exists(path))
					{
						BindingJsonCodec.LoadResult verify = BindingJsonCodec.Load(File.ReadAllText(path));
						if (verify.Kind == BindingJsonCodec.LoadKind.InvalidJson)
							return false;
					}

					return true;
				}
				catch
				{
					return false;
				}
			};
			return io;
		}

		/// <summary>
		/// One GamePad/GamePad UI user-edit transaction: capture → apply memory → bindings → profiles once.
		/// </summary>
		internal static bool TryCommitGamePadUserEdit(
			string storageKey,
			string reason,
			Action applyMemoryMutation)
		{
			if (string.IsNullOrEmpty(storageKey) || applyMemoryMutation == null)
				return false;

			if (!TryCaptureGamePadPreEdit(storageKey, out GamePadUserEditTransaction.PreEditSnapshot before, out string captureError))
			{
				AdvancedKeybinds.Instance?.Logger.Error(
					"Advanced Keybinds refused GamePad edit: backup capture failed ("
					+ (captureError ?? "?") + ") reason=" + (reason ?? "edit"));
				return false;
			}

			GamePadUserEditTransaction.Io io = CreateProductionIo();
			GamePadUserEditTransaction.Outcome outcome = GamePadUserEditTransaction.Run(
				before,
				FilePath,
				ProfilesFilePath,
				applyMemoryMutation,
				() => {
					var data = new Dictionary<string, ComboBindingData>(Combos.Count, StringComparer.Ordinal);
					foreach (KeyValuePair<string, ComboBinding> pair in Combos)
					{
						ComboBindingData dto = ToData(pair.Key, pair.Value);
						if (dto == null)
							continue;
						if (dto.IsEmpty && !dto.DoubleTap
							&& !(dto.PersistAsEmptyRecord
								&& GamePadTwoKeyTerrariaOrPolicy.IsTargetStorageKey(pair.Key)))
							continue;
						data[pair.Key] = dto;
					}
					return BindingJsonCodec.Save(data);
				},
				() => RestoreGamePadPreEditMemory(storageKey, before),
				io);

			BindingInputEngine.InvalidateBindings();

			if (!outcome.Success)
			{
				string detail = outcome.Stage + ":" + (outcome.Message ?? "");
				if (!outcome.RollbackFilesOk || !outcome.RollbackMemoryApplied
					|| !outcome.BindingsBytesMatchAfterRollback
					|| !outcome.ProfilesBytesMatchAfterRollback)
				{
					AdvancedKeybinds.Instance?.Logger.Error(
						"Advanced Keybinds GamePad edit failed and rollback incomplete ("
						+ detail + ") reason=" + (reason ?? "edit"));
				}
				else
				{
					AdvancedKeybinds.Instance?.Logger.Warn(
						"Advanced Keybinds GamePad edit rolled back ("
						+ detail + ") reason=" + (reason ?? "edit"));
				}

				return false;
			}

			return true;
		}

		/// <summary>Memory-only Set mutation (no disk). Used inside <see cref="TryCommitGamePadUserEdit"/>.</summary>
		private static void ApplySetMemoryOnly(string actionId, ComboBinding combo, string syncReason)
		{
			if (combo != null && combo.DoubleTap)
			{
				Combos[actionId] = combo.Clone();
				NoteCombosMutated();
				DoubleTapOwnershipBridge.Sync(actionId, Combos[actionId]);
				return;
			}

			if (combo == null || combo.IsEmpty)
			{
				Combos.Remove(actionId);
				NoteCombosMutated();
				SyncVanillaKeyStatusForAction(actionId, null, syncReason + "-empty");
				MirrorGameBindingToUiIfNeeded(actionId, null, syncReason);
				return;
			}

			ComboBinding toStore = combo.Clone();
			if (!ControlsEditTargetPolicy.IsGamePadStorageKey(actionId))
			{
				ComboBindingData data = ToData(actionId, toStore);
				if (KeyboardMousePadIsolationPolicy.TrySanitizeKeyboardMouseBinding(actionId, data))
					toStore = FromData(data);
				bool defaultVanillaMouse = KeyboardMousePadIsolationPolicy.TryGetDefaultVanillaMouseToken(
					actionId,
					toStore != null && toStore.Hold,
					toStore?.Slot1,
					toStore?.Slot2,
					toStore?.Slot3,
					toStore?.AlternateGamePadToken,
					out string defaultMouseToken);
				if (toStore == null || toStore.IsEmpty || defaultVanillaMouse)
				{
					Combos.Remove(actionId);
					NoteCombosMutated();
					SyncVanillaKeyStatusForAction(actionId, null, syncReason + "-kb-pad-strip-empty");
					if (defaultVanillaMouse
						|| KeyboardMousePadIsolationPolicy.TryMapMouseActionToDefaultToken(
							actionId, out defaultMouseToken))
					{
						VanillaRebindHelper.AssignVanillaBinding(
							ControlsEditTargetPolicy.ToTriggerActionId(actionId),
							defaultMouseToken,
							syncReason + "-default-vanilla-mouse");
					}
					MirrorGameBindingToUiIfNeeded(actionId, null, syncReason);
					return;
				}
			}

			Combos[actionId] = toStore;
			NoteCombosMutated();
			SyncVanillaKeyStatusForAction(actionId, Combos[actionId], syncReason);
			MirrorGameBindingToUiIfNeeded(actionId, Combos[actionId], syncReason);
		}

		private static void ApplyClearMemoryOnly(string actionId, string syncReason)
		{
			if (!Combos.Remove(actionId))
			{
				SyncVanillaKeyStatusForAction(actionId, null, syncReason + "-orphan-markers");
				MirrorGameBindingToUiIfNeeded(actionId, null, syncReason);
				return;
			}

			NoteCombosMutated();
			SyncVanillaKeyStatusForAction(actionId, null, syncReason + "-empty");
			MirrorGameBindingToUiIfNeeded(actionId, null, syncReason);
		}

		/// <summary>
		/// After a Game Controls save/clear: clone Combos + sync Menu KeyStatus.
		/// No-op for Menu storage keys (one-way Game→UI only).
		/// </summary>
		private static void MirrorGameBindingToUiIfNeeded(
			string gameStorageKey,
			ComboBinding sourceOrNull,
			string reason)
		{
			if (!ControlsGameUiSeparationPolicy.TryGetGameToUiMirrorStorageKey(
				    gameStorageKey, out string uiKey)
				|| string.IsNullOrEmpty(uiKey))
				return;

			string mirrorReason = (reason ?? "mirror") + "-game-to-ui";

			if (GamePadTwoKeyTerrariaOrPolicy.IsTargetStorageKey(uiKey))
			{
				ComboBinding toStore;
				if (sourceOrNull == null)
				{
					toStore = new ComboBinding {
						UseTerrariaKeyStatusOr = true,
						PersistAsEmptyRecord = true
					};
					Combos[uiKey] = toStore;
					ExplicitClearGamePadTwoKeyKeyStatus(uiKey, mirrorReason);
					MarkLegacyEvaluateSuppressedForTrigger(
						ControlsGameUiSeparationPolicy.ToTriggerActionId(uiKey));
					return;
				}

				toStore = sourceOrNull.Clone();
				toStore.AlternateGamePadToken = null;
				toStore.Hold = false;
				toStore.HoldOnRelease = false;
				toStore.UseTerrariaKeyStatusOr = true;
				toStore.PersistAsEmptyRecord = toStore.IsEmpty;
				Combos[uiKey] = toStore;
				if (toStore.IsEmpty)
				{
					ExplicitClearGamePadTwoKeyKeyStatus(uiKey, mirrorReason);
					MarkLegacyEvaluateSuppressedForTrigger(
						ControlsGameUiSeparationPolicy.ToTriggerActionId(uiKey));
					return;
				}

				SyncGamePadTwoKeyTerrariaOrKeyStatus(uiKey, toStore, mirrorReason);
				ClearLegacyEvaluateSuppressedForTrigger(
					ControlsGameUiSeparationPolicy.ToTriggerActionId(uiKey));
				return;
			}

			ApplySetMemoryOnly(uiKey, sourceOrNull, mirrorReason);
		}

		/// <summary>
		/// After middle-click / reset-to-default wrote vanilla KeyStatus, drop empty PersistAsEmpty
		/// Combos left by ClearBindings so Menu does not keep showing Unset over restored defaults.
		/// Does not clear KeyStatus.
		/// </summary>
		internal static void DiscardEmptyPersistAfterVanillaDefaultRestore(string storageKey)
		{
			if (string.IsNullOrEmpty(storageKey))
				return;
			if (!Combos.TryGetValue(storageKey, out ComboBinding combo) || combo == null)
				return;
			if (combo.DoubleTap || !combo.PersistAsEmptyRecord || !combo.IsEmpty)
				return;
			Combos.Remove(storageKey);
		}

		/// <summary>Persist Combos after discarding empty Persist rows post default-restore.</summary>
		internal static void SaveBindingsFileAfterMirrorDefaultRestore()
			=> SaveToFile();

		/// <summary>
		/// Undo memory apply: restore Combos entry from snapshot + KeyStatus list for mode
		/// (full mode dict replaced via single-trigger write after combo sync).
		/// </summary>
		internal static void ApplyUndoMemoryOnly(
			string storageKey,
			ComboBinding advanced,
			IReadOnlyList<string> vanillaKeys,
			InputMode mode,
			bool openMenuCustomized,
			bool holdHudCustomized)
		{
			string syncReason = "controls-undo-memory";
			if (advanced == null || (advanced.IsEmpty && !advanced.DoubleTap))
				ApplyClearMemoryOnly(storageKey, syncReason);
			else
				ApplySetMemoryOnly(storageKey, advanced, syncReason);

			if (advanced != null && advanced.DoubleTap)
			{
				ClientPrefs.OpenKeybindMenuCustomized = openMenuCustomized;
				ClientPrefs.HoldTierHudHotkeyCustomized = holdHudCustomized;
				return;
			}

			string trigger = ControlsGameUiSeparationPolicy.ToTriggerActionId(storageKey);
			VanillaRebindHelper.RestoreAssignedKeysForInputMode(
				trigger, vanillaKeys, mode, syncReason);
			ClientPrefs.OpenKeybindMenuCustomized = openMenuCustomized;
			ClientPrefs.HoldTierHudHotkeyCustomized = holdHudCustomized;
		}

		public static void Initialize()
		{
			LoadFromFile();
			if (SanitizeLoadedBindings())
				SaveToFile();
			// Fixed order: Combos migrate/save first; orphan AKAdv strip last (never before migrate).
			RunLegacyGamePadStorageMigration();
			SyncPlainSingleKeyLayoutsToVanilla();
			SyncAdvancedOwnershipMarkersToVanilla();
			SyncAllGamePadTwoKeyTerrariaOrRecords("load-sync-two-key-terraria-or");
			PromoteHookManagedVanillaBindings();
			// Profile may be null during Mod.Load / Build+Reload — repair again when ready.
			EnsureVanillaOwnershipConsistent("initialize-ensure");
			BindingInputEngine.InvalidateBindings();
#if AK_DIAGNOSTICS
			BindingMutationDiag.ResetObservationBaseline();
			BindingMutationDiag.LogWatchedBaseline("BindingStorage.Initialize");
#endif
		}

		/// <summary>True when Combos contains the storage key (including PersistAsEmptyRecord).</summary>
		public static bool HasRecord(string storageKey)
			=> !string.IsNullOrEmpty(storageKey) && Combos.ContainsKey(storageKey);

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

			// Profile became ready: finish deferred orphan AKAdv strip (once) before ownership ensure.
			TryResumeLegacyGamePadOrphanStrip(reason == null ? "ensure-orphan-retry" : reason + "-orphan-retry");

			string syncReason = reason ?? "ensure-ownership";
			int repaired = 0;
			foreach (KeyValuePair<string, ComboBinding> pair in Combos)
			{
				ComboBinding combo = pair.Value;
				if (combo == null)
					continue;

				if (GamePadTwoKeyTerrariaOrPolicy.IsTargetStorageKey(pair.Key))
				{
					if (GamePadTwoKeyTerrariaOrOwnershipMatches(pair.Key, combo))
						continue;
					SyncVanillaKeyStatusForAction(pair.Key, combo, syncReason + "-repair-two-key");
					repaired++;
					continue;
				}

				if (combo.DoubleTap)
				{
					if (!DoubleTapOwnershipBridge.Matches(pair.Key, combo))
					{
						DoubleTapOwnershipBridge.Sync(pair.Key, combo);
						repaired++;
					}
					continue;
				}

				if (combo.IsEmpty)
					continue;

				if (OwnershipMatchesForStorageKey(pair.Key, combo))
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
			List<string> removeKeys = null;

			foreach (KeyValuePair<string, ComboBinding> pair in Combos)
			{
				ComboBinding combo = pair.Value;
				if (combo == null)
					continue;

				if (combo.DoubleTap)
					continue;

				bool holdBefore = combo.Hold;
				bool holdOnReleaseBefore = combo.HoldOnRelease;
				string slot1Before = combo.Slot1;
				string slot2Before = combo.Slot2;
				string slot3Before = combo.Slot3;
				string orBefore = combo.AdditionalOrToken;
				BindingRestrictions.ClearUnsupportedHoldFlags(combo);
				NormalizeSharedUiBackAdditionalOrInPlace(pair.Key, combo);
				if (holdBefore != combo.Hold
					|| holdOnReleaseBefore != combo.HoldOnRelease
					|| !string.Equals(slot1Before, combo.Slot1, StringComparison.Ordinal)
					|| !string.Equals(slot2Before, combo.Slot2, StringComparison.Ordinal)
					|| !string.Equals(slot3Before, combo.Slot3, StringComparison.Ordinal)
					|| !string.Equals(orBefore, combo.AdditionalOrToken, StringComparison.Ordinal))
				{
					changed = true;
					if (AkDiag.Enabled) AkDiag.Log("Sanitized unsupported Hold/AdditionalOr on load action=" + pair.Key);
				}

				// Keyboard/Mouse storage must never keep Pad:* (kills click + Pad UI confirm via MouseLeft).
				// Default MouseLeft=Mouse1 must not stay in Combos (AKAdv markers break title UI;
				// BindingInputEngine skips Main.gameMenu).
				if (!ControlsEditTargetPolicy.IsGamePadStorageKey(pair.Key))
				{
					ComboBindingData data = ToData(pair.Key, combo);
					bool padStripped = KeyboardMousePadIsolationPolicy.TrySanitizeKeyboardMouseBinding(pair.Key, data);
					bool defaultVanillaMouse = KeyboardMousePadIsolationPolicy.TryGetDefaultVanillaMouseToken(
						pair.Key, data.Hold, data.Slot1, data.Slot2, data.Slot3, data.AlternateGamePadToken,
						out _);
					if (padStripped || defaultVanillaMouse)
					{
						changed = true;
						if (data.IsEmpty || defaultVanillaMouse)
						{
							removeKeys ??= new List<string>();
							removeKeys.Add(pair.Key);
						}
						else
							Combos[pair.Key] = FromData(data);
					}
				}

				// Legacy GP:{vanilla} full migration runs in RunLegacyGamePadStorageMigration (after sanitize save).
			}

			if (removeKeys != null)
			{
				for (int i = 0; i < removeKeys.Count; i++)
				{
					string removeId = removeKeys[i];
					Combos.Remove(removeId);
					if (KeyboardMousePadIsolationPolicy.TryMapMouseActionToDefaultToken(
						    removeId, out string defaultMouseToken))
					{
						VanillaRebindHelper.AssignVanillaBinding(
							ControlsEditTargetPolicy.ToTriggerActionId(removeId),
							defaultMouseToken,
							"sanitize-default-vanilla-mouse");
					}
				}
			}

			if (changed)
				NoteCombosMutated();

			return changed;
		}

		private static readonly LegacyGamePadMigrationDriver.State LegacyGpMigrationState = new();

		/// <summary>
		/// When CurrentProfile was unavailable during Initialize migration, retry orphan AKAdv strip once.
		/// Called from ownership Ensure (PostSetup / WorldLoad / watch) — not every frame blindly.
		/// </summary>
		public static void TryResumeLegacyGamePadOrphanStrip(string reason = null)
		{
			if (!LegacyGpMigrationState.OrphanStripPending)
				return;
			if (!VanillaRebindHelper.IsProfileReady())
				return;
			ExecuteLegacyGamePadMigrationViaDriver(reason ?? "legacy-gp-orphan-retry");
		}

		/// <summary>
		/// Existing-user update path — delegates to tested
		/// <see cref="LegacyGamePadMigrationDriver"/> / Orchestrator (no duplicate migration logic).
		/// </summary>
		private static void RunLegacyGamePadStorageMigration()
			=> ExecuteLegacyGamePadMigrationViaDriver("legacy-gp-migrate");

		private static void ExecuteLegacyGamePadMigrationViaDriver(string reason)
		{
			LegacyGamePadMigrationOrchestrator.Session session = BuildLegacyGpMigrationSession();
			var paths = new LegacyGamePadMigrationDriver.HostPaths {
				BindingsPath = FilePath,
				ProfilesPath = ProfilesFilePath,
				SaveRoot = Main.SavePath ?? string.Empty
			};
			string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");

			Dictionary<string, List<string>> gpSnap = CloneKeyStatusMap(session.KeyStatusGamepad);
			Dictionary<string, List<string>> gpUiSnap = CloneKeyStatusMap(session.KeyStatusGamepadUi);

			LegacyGamePadMigrationCommitPipeline.Outcome outcome =
				LegacyGamePadMigrationDriver.ExecutePipeline(
					session, paths, stamp, LegacyGpMigrationState,
					applyMemory: plan => {
						if (plan == null || !session.ProfileReady)
							return;
						ApplyModeMutation(InputMode.XBoxGamepad, plan.Gamepad);
						ApplyModeMutation(InputMode.XBoxGamepadUI, plan.GamepadUi);
					},
					restoreMemory: (gp, gpUi) => {
						RestoreFullModeKeyStatus(InputMode.XBoxGamepad, gp ?? gpSnap);
						RestoreFullModeKeyStatus(InputMode.XBoxGamepadUI, gpUi ?? gpUiSnap);
					});

			ForwardLegacyGpMigrationLogs(outcome.Log);

			if (outcome.CriticalRollbackFailure || outcome.CriticalRecoveryConflict)
			{
				try
				{
					AdvancedKeybinds.Instance?.Logger?.Error(
						"[LegacyGpMigrate] CRITICAL rollback/recovery failure — disk/memory may be inconsistent");
				}
				catch { /* ignore */ }
			}

			if (outcome.RecoveryStopped)
			{
				// Journal recovery already restored files; memory from journal snapshots when present.
				if (outcome.GpResult != null)
					RestoreFullModeKeyStatus(InputMode.XBoxGamepad, outcome.GpResult);
				else
					RestoreFullModeKeyStatus(InputMode.XBoxGamepad, gpSnap);
				if (outcome.GpUiResult != null)
					RestoreFullModeKeyStatus(InputMode.XBoxGamepadUI, outcome.GpUiResult);
				else
					RestoreFullModeKeyStatus(InputMode.XBoxGamepadUI, gpUiSnap);
				return;
			}

			if (outcome.Aborted)
			{
				// Pipeline RestoreMemoryFull callback should have run; enforce snapshot if not.
				if (!outcome.MemoryApplied)
				{
					RestoreFullModeKeyStatus(InputMode.XBoxGamepad, gpSnap);
					RestoreFullModeKeyStatus(InputMode.XBoxGamepadUI, gpUiSnap);
				}
				return;
			}

			if (outcome.Completed && outcome.Plan != null && !outcome.Plan.IsNoOp)
			{
				ReplaceCombosFromDto(outcome.CombosResult);
				// KeyStatus already applied at pipeline step 3 via ApplyMemoryMutation.
				BindingInputEngine.InvalidateBindings();
			}
		}

		private static LegacyGamePadMigrationOrchestrator.Session BuildLegacyGpMigrationSession()
		{
			var session = new LegacyGamePadMigrationOrchestrator.Session {
				ProfileReady = VanillaRebindHelper.IsProfileReady(),
				OrphanPendingRequested = LegacyGpMigrationState.OrphanStripPending
			};
			session.Combos = CaptureCombosDto();
			if (session.ProfileReady)
			{
				CaptureGamepadKeyStatusMaps(session.KeyStatusGamepad, session.KeyStatusGamepadUi);
				if (File.Exists(ProfilesFilePath))
				{
					try
					{
						session.ProfilesRoot = LegacyGamePadProfilesDocument.LoadOrEmpty(ProfilesFilePath);
					}
					catch
					{
						session.ProfilesRoot = null;
					}
				}
			}

			return session;
		}

		private static Dictionary<string, ComboBindingData> CaptureCombosDto()
		{
			var data = new Dictionary<string, ComboBindingData>(Combos.Count, StringComparer.Ordinal);
			foreach (KeyValuePair<string, ComboBinding> pair in Combos)
			{
				ComboBindingData dto = ToData(pair.Key, pair.Value);
				if (dto != null)
					data[pair.Key] = dto;
			}

			return data;
		}

		private static void ReplaceCombosFromDto(Dictionary<string, ComboBindingData> data)
		{
			Combos.Clear();
			if (data == null)
				return;
			foreach (KeyValuePair<string, ComboBindingData> pair in data)
			{
				ComboBinding combo = FromData(pair.Value);
				if (combo != null)
					Combos[pair.Key] = combo;
			}
		}

		private static void CaptureGamepadKeyStatusMaps(
			Dictionary<string, List<string>> gamepad,
			Dictionary<string, List<string>> gamepadUi)
		{
			CaptureModeKeyStatus(InputMode.XBoxGamepad, gamepad);
			CaptureModeKeyStatus(InputMode.XBoxGamepadUI, gamepadUi);
		}

		private static void CaptureModeKeyStatus(InputMode mode, Dictionary<string, List<string>> map)
		{
			if (map == null || !VanillaRebindHelper.IsProfileReady())
				return;
			PlayerInputProfile profile = PlayerInput.CurrentProfile;
			if (profile?.InputModes == null
				|| !profile.InputModes.TryGetValue(mode, out KeyConfiguration config)
				|| config?.KeyStatus == null)
				return;
			foreach (KeyValuePair<string, List<string>> pair in config.KeyStatus)
			{
				if (string.IsNullOrEmpty(pair.Key) || pair.Value == null)
					continue;
				map[pair.Key] = new List<string>(pair.Value);
			}
		}

		private static void SyncKeyStatusFromMigrationSession(
			LegacyGamePadMigrationOrchestrator.Session session)
		{
			if (session == null || !VanillaRebindHelper.IsProfileReady())
				return;
			ApplyKeyStatusMap(session.KeyStatusGamepad, InputMode.XBoxGamepad);
			ApplyKeyStatusMap(session.KeyStatusGamepadUi, InputMode.XBoxGamepadUI);
		}

		private static void ApplyKeyStatusMap(Dictionary<string, List<string>> map, InputMode mode)
		{
			if (map == null)
				return;
			foreach (KeyValuePair<string, List<string>> pair in map)
			{
				VanillaRebindHelper.WriteKeyStatusForInputModes(
					pair.Key,
					pair.Value ?? (IReadOnlyList<string>)Array.Empty<string>(),
					mode);
			}
		}

		private static void ApplyModeMutation(InputMode mode, ModeMutation mutation)
		{
			if (mutation == null)
				return;
			foreach (string action in mutation.RemoveActions)
			{
				// PlayerInput exposes clear, not dictionary remove; persisted clear is the remove-equivalent.
				VanillaRebindHelper.ClearVanillaBindingForInputModes(
					action, "legacy-gp-migration-remove", mode);
			}
			foreach (KeyValuePair<string, List<string>> pair in mutation.UpsertActions)
			{
				VanillaRebindHelper.WriteKeyStatusForInputModes(
					pair.Key,
					pair.Value ?? (IReadOnlyList<string>)Array.Empty<string>(),
					mode);
			}
		}

		private static void RestoreFullModeKeyStatus(
			InputMode mode,
			Dictionary<string, List<string>> snapshot)
		{
			var current = new Dictionary<string, List<string>>(StringComparer.Ordinal);
			CaptureModeKeyStatus(mode, current);
			foreach (string action in current.Keys)
			{
				VanillaRebindHelper.ClearVanillaBindingForInputModes(
					action, "legacy-gp-migration-restore-clear", mode);
			}
			ApplyKeyStatusMap(snapshot, mode);
		}

		private static bool TryWriteBindingsDtoAtomic(
			string path,
			Dictionary<string, ComboBindingData> data)
		{
			try
			{
				string json = BindingJsonCodec.Save(data ?? new Dictionary<string, ComboBindingData>());
				return LegacyGamePadAtomicFileIo.TryWriteAllTextAtomic(path, json);
			}
			catch (Exception ex)
			{
				LogMigrationLine("SAVE_FAIL", path, null, ex.GetType().Name);
				return false;
			}
		}

		private static Dictionary<string, List<string>> CloneKeyStatusMap(Dictionary<string, List<string>> src)
		{
			var dst = new Dictionary<string, List<string>>(StringComparer.Ordinal);
			if (src == null)
				return dst;
			foreach (KeyValuePair<string, List<string>> pair in src)
				dst[pair.Key] = pair.Value == null ? null : new List<string>(pair.Value);
			return dst;
		}

		private static void ForwardLegacyGpMigrationLogs(LegacyGamePadMigrationOrchestrator.Result result)
		{
#if AK_DIAGNOSTICS
			if (!global::AdvancedKeybinds.Binding.AkDiag.Enabled)
				return;
			if (result?.Log == null)
				return;
			for (int i = 0; i < result.Log.Count; i++)
			{
				try { AdvancedKeybinds.Instance?.Logger?.Info(result.Log[i]); }
				catch { /* ignore */ }
			}
#endif
		}

		private static void ForwardLegacyGpMigrationLogs(IReadOnlyList<string> lines)
		{
#if AK_DIAGNOSTICS
			if (!global::AdvancedKeybinds.Binding.AkDiag.Enabled)
				return;
			if (lines == null)
				return;
			for (int i = 0; i < lines.Count; i++)
			{
				try { AdvancedKeybinds.Instance?.Logger?.Info(lines[i]); }
				catch { /* ignore */ }
			}
#endif
		}

		private static void LogMigrationLine(string op, string from, string to, string reason)
		{
#if AK_DIAGNOSTICS
			if (!global::AdvancedKeybinds.Binding.AkDiag.Enabled)
				return;
			try
			{
				string msg = "[LegacyGpMigrate] " + op
					+ " from=" + (from ?? "-")
					+ " to=" + (to ?? "-")
					+ " reason=" + (reason ?? "-");
				AdvancedKeybinds.Instance?.Logger?.Info(msg);
			}
			catch
			{
				/* ignore */
			}
#endif
		}

		/// <summary>Mod GamePad dual sync only (vanilla GAME/UI use single-mode arrays).</summary>
		private static readonly InputMode[] GamePadSyncModes = {
			InputMode.XBoxGamepad,
			InputMode.XBoxGamepadUI
		};

		private static readonly InputMode[] KeyboardBothSyncModes = {
			InputMode.Keyboard,
			InputMode.KeyboardUI
		};

		/// <summary>
		/// Writes KeyStatus to match a stored combo (or clears when null/empty).
		/// Empty = user unset — never restores RegisterKeybind defaults here.
		/// Vanilla GAME/UI use mode-specific storage and sync one InputMode only.
		/// Mod keybinds keep dual Keyboard or dual GamePad sync.
		/// </summary>
		private static void SyncVanillaKeyStatusForAction(string actionId, ComboBinding combo, string reason)
		{
			if (string.IsNullOrEmpty(actionId))
				return;

			if (GamePadTwoKeyTerrariaOrPolicy.IsTargetStorageKey(actionId))
			{
				SyncGamePadTwoKeyTerrariaOrKeyStatus(actionId, combo, reason);
				return;
			}

			if (combo?.DoubleTap == true)
			{
				DoubleTapOwnershipBridge.Sync(actionId, combo);
				return;
			}

			string trigger = ControlsGameUiSeparationPolicy.ToTriggerActionId(actionId);
			var syncKind = ControlsGameUiSeparationPolicy.ResolveSyncKind(actionId, out int singleOrdinal);
			InputMode[] modes = ResolveSyncModesArray(syncKind, singleOrdinal);
			bool gamePadStorage = ControlsEditTargetPolicy.IsGamePadStorageKey(actionId)
				|| ControlsGameUiSeparationPolicy.IsGamePadGameStorageKey(actionId)
				|| ControlsGameUiSeparationPolicy.IsGamePadUiStorageKey(actionId);

			if (combo == null || combo.IsEmpty)
			{
				ClearForSync(trigger, reason ?? "sync-empty", syncKind, modes);
				return;
			}

			// Inventory/MenuInquiry + AdditionalOr: drop leftover Hold so Escape stays in KeyStatus.
			BindingRestrictions.ClearUnsupportedHoldFlags(combo);
			NormalizeSharedUiBackAdditionalOrInPlace(actionId, combo);

			if (gamePadStorage)
			{
				if (ControlsEditTargetPolicy.IsVanillaOwnedGamePadOrPair(
					combo.Hold, combo.Keys, combo.AlternateGamePadToken))
				{
					if (combo.HasAlternateGamePad)
					{
						if (syncKind == ControlsGameUiSeparationPolicy.SyncModeKind.GamePadBoth)
						{
							VanillaRebindHelper.AssignVanillaBindingListForInputModes(
								trigger,
								new[] { combo.Keys[0], combo.AlternateGamePadToken },
								reason ?? "sync-plain-gamepad-or",
								GamePadSyncModes);
						}
						else
						{
							VanillaRebindHelper.AssignVanillaBindingListForInputModes(
								trigger,
								new[] { combo.Keys[0], combo.AlternateGamePadToken },
								reason ?? "sync-plain-gamepad-or",
								modes);
						}
					}
					else if (syncKind == ControlsGameUiSeparationPolicy.SyncModeKind.GamePadBoth)
					{
						VanillaRebindHelper.AssignVanillaBindingForInputModes(
							trigger,
							combo.Keys[0],
							reason ?? "sync-plain-gamepad",
							GamePadSyncModes);
					}
					else
					{
						VanillaRebindHelper.AssignVanillaBindingForInputModes(
							trigger,
							combo.Keys[0],
							reason ?? "sync-plain-gamepad",
							modes);
					}
					return;
				}

				if (combo.RequiresAdvancedStorage())
				{
					var markerKeys = new List<string>(combo.Keys);
					if (combo.HasAlternateGamePad)
						markerKeys.Add(combo.AlternateGamePadToken);
					if (syncKind == ControlsGameUiSeparationPolicy.SyncModeKind.GamePadBoth)
					{
						VanillaRebindHelper.AssignAdvancedOwnershipMarkersForInputModes(
							trigger,
							markerKeys,
							reason ?? "sync-advanced-markers-gamepad",
							GamePadSyncModes);
					}
					else
					{
						VanillaRebindHelper.AssignAdvancedOwnershipMarkersForInputModes(
							trigger,
							markerKeys,
							reason ?? "sync-advanced-markers-gamepad",
							modes);
					}
					return;
				}

				ClearForSync(trigger, reason ?? "sync-empty-shape", syncKind, modes);
				return;
			}

			if (combo.IsPlainSingleTap)
			{
				AssignKeyboardPlain(trigger, combo.Keys[0], reason ?? "sync-plain", syncKind, modes);
				return;
			}

			// Default sole mouse binds (MouseLeft=Mouse1, MouseRight=Mouse2, …): vanilla must own
			// the Keys name. AKAdv markers break Main.mouseRight / UI OnRightClick (Magic Storage).
			if (KeyboardMousePadIsolationPolicy.TryGetDefaultVanillaMouseToken(
				trigger, combo.Hold, combo.Slot1, combo.Slot2, combo.Slot3, combo.AlternateGamePadToken,
				out string defaultMouseToken))
			{
				AssignKeyboardPlain(
					trigger,
					defaultMouseToken,
					reason ?? "sync-default-vanilla-mouse",
					syncKind,
					modes);
				return;
			}

			if (combo.RequiresAdvancedStorage())
			{
				// Inventory/MenuInquiry + plain Escape AND + AdditionalOr:
				// write vanilla OR KeyStatus (Escape, Tab) on Keyboard and KeyboardUI.
				// Title Back = JustPressed.Inventory (must keep Escape on KeyboardUI).
				if (KeyboardAdditionalOrPolicy.TryBuildVanillaOrKeyStatusEntries(
					    actionId, combo.Hold, combo.Keys, combo.AdditionalOrToken,
					    AdditionalOrVanillaOrScratch))
				{
                    var orSyncLease = global::AdvancedKeybinds.Patches.AdditionalOrPersistencePatches.BeginPlainOrSync(
                        trigger,
                        AdditionalOrVanillaOrScratch,
                        syncKind,
                        modes);
                    try
                    {
                        AssignKeyboardPlainKeys(
                            trigger,
                            AdditionalOrVanillaOrScratch,
                            reason ?? "sync-additional-or-vanilla-or-list",
                            syncKind,
                            modes);
                        global::AdvancedKeybinds.Patches.AdditionalOrPersistencePatches.CompletePlainOrSync(orSyncLease);
                    }
                    finally
                    {
                        orSyncLease.Dispose();
                    }
					return;
				}

				var markerKeys = new List<string>(combo.Keys);
				if (combo.HasAdditionalOr)
					markerKeys.Add(combo.AdditionalOrToken);
				if (markerKeys.Count == 0 && combo.HasAdditionalOr)
					markerKeys.Add(combo.AdditionalOrToken);

				if (syncKind == ControlsGameUiSeparationPolicy.SyncModeKind.KeyboardBoth)
				{
					VanillaRebindHelper.AssignAdvancedOwnershipMarkers(
						trigger,
						markerKeys,
						reason ?? "sync-advanced-markers");
				}
				else
				{
					VanillaRebindHelper.AssignAdvancedOwnershipMarkersForInputModes(
						trigger,
						markerKeys,
						reason ?? "sync-advanced-markers-mode",
						modes);
				}
				return;
			}

			ClearForSync(trigger, reason ?? "sync-empty-shape", syncKind, modes);
		}

		private static InputMode[] ResolveSyncModesArray(
			ControlsGameUiSeparationPolicy.SyncModeKind kind,
			int singleOrdinal)
		{
			switch (kind)
			{
				case ControlsGameUiSeparationPolicy.SyncModeKind.GamePadBoth:
					return GamePadSyncModes;
				case ControlsGameUiSeparationPolicy.SyncModeKind.KeyboardBoth:
					return KeyboardBothSyncModes;
				default:
					return new[] { (InputMode)singleOrdinal };
			}
		}

		private static void ClearForSync(
			string trigger,
			string reason,
			ControlsGameUiSeparationPolicy.SyncModeKind kind,
			InputMode[] modes)
		{
			if (kind == ControlsGameUiSeparationPolicy.SyncModeKind.KeyboardBoth)
				VanillaRebindHelper.ClearVanillaBinding(trigger, reason);
			else if (kind == ControlsGameUiSeparationPolicy.SyncModeKind.GamePadBoth)
				VanillaRebindHelper.ClearVanillaBindingForInputModes(trigger, reason, GamePadSyncModes);
			else
				VanillaRebindHelper.ClearVanillaBindingForInputModes(trigger, reason, modes);
		}

		private static void AssignKeyboardPlain(
			string trigger,
			string key,
			string reason,
			ControlsGameUiSeparationPolicy.SyncModeKind kind,
			InputMode[] modes)
		{
			if (kind == ControlsGameUiSeparationPolicy.SyncModeKind.KeyboardBoth)
				VanillaRebindHelper.AssignVanillaBinding(trigger, key, reason);
			else
				VanillaRebindHelper.AssignVanillaBindingForInputModes(trigger, key, reason, modes);
		}

		private static void AssignKeyboardPlainKeys(
			string trigger,
			IReadOnlyList<string> keys,
			string reason,
			ControlsGameUiSeparationPolicy.SyncModeKind kind,
			InputMode[] modes)
		{
			if (keys == null || keys.Count == 0)
			{
				ClearForSync(trigger, reason ?? "sync-empty-additional-or", kind, modes);
				return;
			}

			if (keys.Count == 1)
			{
				AssignKeyboardPlain(trigger, keys[0], reason, kind, modes);
				return;
			}

			if (kind == ControlsGameUiSeparationPolicy.SyncModeKind.KeyboardBoth)
				VanillaRebindHelper.RestoreAssignedKeys(trigger, keys, reason);
			else
			{
				for (int i = 0; i < modes.Length; i++)
					VanillaRebindHelper.RestoreAssignedKeysForInputMode(trigger, keys, modes[i], reason);
			}
		}

		private static void SyncAllGamePadTwoKeyTerrariaOrRecords(string reason)
		{
			foreach (KeyValuePair<string, ComboBinding> pair in Combos)
			{
				if (pair.Value?.DoubleTap == true)
					continue;
				if (!GamePadTwoKeyTerrariaOrPolicy.IsTargetStorageKey(pair.Key))
					continue;
				SyncGamePadTwoKeyTerrariaOrKeyStatus(pair.Key, pair.Value, reason);
			}
		}

		private static readonly List<string> AdditionalOrVanillaOrScratch = new(2);

		private static bool OwnershipMatchesForStorageKey(string storageKey, ComboBinding combo)
		{
			if (combo?.DoubleTap == true)
				return DoubleTapOwnershipBridge.Matches(storageKey, combo);

			string trigger = ControlsGameUiSeparationPolicy.ToTriggerActionId(storageKey);
			var kind = ControlsGameUiSeparationPolicy.ResolveSyncKind(storageKey, out int singleOrdinal);

			if (KeyboardAdditionalOrPolicy.ShouldPreserveVanillaAndKeyWithAdditionalOr(
				    storageKey, combo.Hold, combo.Keys, combo.AdditionalOrToken))
			{
				if (kind == ControlsGameUiSeparationPolicy.SyncModeKind.KeyboardBoth)
				{
					return AdditionalOrVanillaOrMatchesMode(storageKey, trigger, InputMode.Keyboard, combo)
						&& AdditionalOrVanillaOrMatchesMode(storageKey, trigger, InputMode.KeyboardUI, combo);
				}

				if (kind == ControlsGameUiSeparationPolicy.SyncModeKind.GamePadBoth)
					return false;

				InputMode mode = (InputMode)singleOrdinal;
				if (mode == InputMode.Keyboard || mode == InputMode.KeyboardUI)
					return AdditionalOrVanillaOrMatchesMode(storageKey, trigger, mode, combo);

				return false;
			}

			if (kind == ControlsGameUiSeparationPolicy.SyncModeKind.KeyboardBoth)
			{
				return VanillaRebindHelper.AssignedOwnershipMatchesSlots(
					trigger, combo.Hold, combo.Slot1, combo.Slot2, combo.Slot3);
			}

			if (kind == ControlsGameUiSeparationPolicy.SyncModeKind.GamePadBoth)
			{
				return GamePadOwnershipMatchesMode(trigger, InputMode.XBoxGamepad, combo)
					&& GamePadOwnershipMatchesMode(trigger, InputMode.XBoxGamepadUI, combo);
			}

			InputMode singleMode = (InputMode)singleOrdinal;
			if (singleMode == InputMode.Keyboard || singleMode == InputMode.KeyboardUI)
			{
				return VanillaRebindHelper.AssignedOwnershipMatchesSlotsForInputMode(
					trigger, singleMode, combo.Hold, combo.Slot1, combo.Slot2, combo.Slot3);
			}

			return GamePadOwnershipMatchesMode(trigger, singleMode, combo);
		}

		private static bool AdditionalOrVanillaOrMatchesMode(
			string storageKey,
			string trigger,
			InputMode mode,
			ComboBinding combo)
		{
			PlayerInputProfile profile = PlayerInput.CurrentProfile;
			if (profile?.InputModes == null || !profile.InputModes.ContainsKey(mode))
				return true;

			if (!KeyboardAdditionalOrPolicy.TryBuildVanillaOrKeyStatusEntries(
				    storageKey, combo.Hold, combo.Keys, combo.AdditionalOrToken,
				    AdditionalOrVanillaOrScratch, (int)mode))
				return false;

			IReadOnlyList<string> assigned =
				VanillaRebindHelper.GetAssignedKeysForInputMode(trigger, mode);
			return BindingVanillaOwnershipSync.MatchesExpectedKeyStatus(
				assigned, AdditionalOrVanillaOrScratch);
		}

		private static bool GamePadOwnershipMatchesMode(string trigger, InputMode mode, ComboBinding combo)
		{
			IReadOnlyList<string> assigned =
				VanillaRebindHelper.GetAssignedKeysForInputMode(trigger, mode);
			return BindingVanillaOwnershipSync.MatchesOwnershipFromSlots(
				assigned,
				combo.Hold,
				combo.Slot1,
				combo.Slot2,
				combo.Slot3);
		}

		private static void SyncGamePadTwoKeyTerrariaOrKeyStatus(
			string storageKey,
			ComboBinding combo,
			string reason)
		{
			if (combo?.DoubleTap == true)
				return;

			string trigger = ControlsGameUiSeparationPolicy.ToTriggerActionId(storageKey);
			int modeOrdinal = GamePadTwoKeyTerrariaOrPolicy.ResolveSyncInputModeOrdinal(storageKey);
			if (modeOrdinal < 0 || string.IsNullOrEmpty(trigger))
				return;

			InputMode mode = (InputMode)modeOrdinal;

			// PersistAsEmpty / no effective inputs: do not Clear living KeyStatus (state A).
			// Explicit ClearBindings clears KeyStatus on its own path (state C).
			if (combo == null
				|| (combo.IsEmpty && combo.PersistAsEmptyRecord)
				|| !GamePadTwoKeyTerrariaOrPolicy.TryGetEffectiveTwoKeyInputs(
					combo.Slot1, combo.Slot2, combo.Slot3, out string input1, out string input2))
				return;

			int n = GamePadTwoKeyTerrariaOrPolicy.BuildKeyStatusEntries(
				input1, input2, TwoKeyKeyStatusScratch);
			if (n <= 0)
				return;

			if (n == 1)
			{
				VanillaRebindHelper.AssignVanillaBindingForInputModes(
					trigger,
					TwoKeyKeyStatusScratch[0],
					reason ?? "sync-two-key-slot",
					mode);
				return;
			}

			VanillaRebindHelper.AssignVanillaBindingListForInputModes(
				trigger,
				new[] { TwoKeyKeyStatusScratch[0], TwoKeyKeyStatusScratch[1] },
				reason ?? "sync-two-key-or",
				mode);
		}

		private static bool GamePadTwoKeyTerrariaOrOwnershipMatches(string storageKey, ComboBinding combo)
		{
			string trigger = ControlsGameUiSeparationPolicy.ToTriggerActionId(storageKey);
			int modeOrdinal = GamePadTwoKeyTerrariaOrPolicy.ResolveSyncInputModeOrdinal(storageKey);
			if (modeOrdinal < 0 || string.IsNullOrEmpty(trigger))
				return true;

			IReadOnlyList<string> assigned =
				VanillaRebindHelper.GetAssignedKeysForInputMode(trigger, (InputMode)modeOrdinal);
			if (combo == null
				|| !GamePadTwoKeyTerrariaOrPolicy.TryGetEffectiveTwoKeyInputs(
					combo.Slot1, combo.Slot2, combo.Slot3, out string input1, out string input2))
				return assigned == null || assigned.Count == 0;

			int n = GamePadTwoKeyTerrariaOrPolicy.BuildKeyStatusEntries(
				input1, input2, TwoKeyKeyStatusScratch);
			if (n <= 0)
				return assigned == null || assigned.Count == 0;
			if (assigned == null || assigned.Count != n)
				return false;
			for (int i = 0; i < n; i++)
			{
				if (!string.Equals(assigned[i], TwoKeyKeyStatusScratch[i], StringComparison.OrdinalIgnoreCase))
					return false;
			}
			return true;
		}

		/// <summary>
		/// Commit a GP:G: / GP:U: two-key record: write disk first, then swap memory + KeyStatus.
		/// Returns false when save fails — Combos and KeyStatus stay unchanged.
		/// </summary>
		public static bool TryCommitGamePadTwoKeyTerrariaOr(
			string storageKey,
			ComboBinding combo,
			string reason = null)
		{
			if (!GamePadTwoKeyTerrariaOrPolicy.IsTargetStorageKey(storageKey))
				return false;

			if (combo?.DoubleTap == true)
				return false;

#if AK_DIAGNOSTICS
			ControlsGameUiSeparationTrace.LogStage("4_before_ak_save", storageKey, reason ?? "TryCommitGamePadTwoKey");
#endif
			string syncReason = reason ?? "BindingStorage.TryCommitGamePadTwoKeyTerrariaOr";
			ComboBinding toStore = combo == null
				? new ComboBinding { UseTerrariaKeyStatusOr = true, PersistAsEmptyRecord = true }
				: combo.Clone();
			// Keep Slot3 / Slot3Display as provided (no Slot3→Slot1 move, no discard).
			toStore.AlternateGamePadToken = null;
			toStore.Hold = false;
			toStore.HoldOnRelease = false;
			toStore.UseTerrariaKeyStatusOr = true;
			// IsEmpty already includes Slot3; do not force empty Persist when Slot3 has input.
			toStore.PersistAsEmptyRecord = toStore.IsEmpty;

			bool ok = TryCommitGamePadUserEdit(storageKey, syncReason, () => {
				Combos[storageKey] = toStore;
				NoteCombosMutated();
				// Empty PersistAsEmpty must match ClearBindings (state C): Controls GenInput reads
				// KeyStatus, so leaving living pad tokens makes the list show glyphs after Clear+Save.
				if (toStore.IsEmpty)
				{
					ExplicitClearGamePadTwoKeyKeyStatus(storageKey, syncReason);
					MarkLegacyEvaluateSuppressedForTrigger(
						ControlsGameUiSeparationPolicy.ToTriggerActionId(storageKey));
					MirrorGameBindingToUiIfNeeded(storageKey, null, syncReason);
				}
				else
				{
					SyncGamePadTwoKeyTerrariaOrKeyStatus(storageKey, toStore, syncReason);
					ClearLegacyEvaluateSuppressedForTrigger(
						ControlsGameUiSeparationPolicy.ToTriggerActionId(storageKey));
					MirrorGameBindingToUiIfNeeded(storageKey, toStore, syncReason);
				}
			});
			if (ok)
			{
#if AK_DIAGNOSTICS
				AkEscStartSaveSessionDiagSystem.NoteBindingOverwrite(
					syncReason,
					storageKey,
					"(two-key-commit)",
					FormatComboSlots(toStore));
#endif
			}

#if AK_DIAGNOSTICS
			ControlsGameUiSeparationTrace.LogStage("5_after_ak_save", storageKey, syncReason);
#endif
			return ok;
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
				if (combo == null || combo.DoubleTap || !combo.IsPlainSingleTap)
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
				if (combo == null)
					continue;

				if (combo.DoubleTap)
				{
					DoubleTapOwnershipBridge.Sync(pair.Key, combo);
					continue;
				}

				if (!combo.RequiresAdvancedStorage())
					continue;

				SyncVanillaKeyStatusForAction(pair.Key, combo, "load-sync-advanced-markers");
			}
		}

		public static void Unload()
		{
			global::AdvancedKeybinds.Patches.AdditionalOrPersistencePatches.RestoreOwnedProfiles();
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

			// Always clone: callers must never mutate the live Combos entry in place.
			// ComboPanel open/snapshot/cancel rely on Working ≠ Saved reference separation.
			return combo.Clone();
		}

		public static bool HasAdvancedBindings(string actionId)
		{
			return Get(actionId) != null;
		}

		/// <summary>
		/// Stores the combo for an action. Plain single-key taps are kept so the combo panel can restore
		/// left/middle/right slot placement; input/list display for those still go through vanilla.
		/// KeyStatus is always synced here (advanced markers / plain Keys name / empty).
		/// Returns false when a GamePad profiles persist transaction fails and state was rolled back.
		/// </summary>
		public static bool Set(string actionId, ComboBinding combo, string reason = null)
		{
			if (string.IsNullOrEmpty(actionId))
				return false;

#if AK_DIAGNOSTICS
			ControlsGameUiSeparationTrace.LogStage("4_before_ak_save", actionId, reason ?? "BindingStorage.Set");
#endif

			// TwoKey Terraria-OR commit is non-DT only. DT rows use ApplySetMemoryOnly
			// (clone preserve + eligible-only ownership sync) so out-of-scope GP:G:/GP:U: keep originals.
			if (GamePadTwoKeyTerrariaOrPolicy.IsTargetStorageKey(actionId)
				&& combo?.DoubleTap != true)
				return TryCommitGamePadTwoKeyTerrariaOr(actionId, combo, reason ?? "BindingStorage.Set");

			string syncReason = reason ?? "BindingStorage.Set";
#if AK_DIAGNOSTICS
			string beforeSnap = FormatComboSlots(Get(actionId));
#endif

			if (ShouldPersistPlayerInputProfiles(actionId))
			{
				bool ok = TryCommitGamePadUserEdit(actionId, syncReason, () => {
					ApplySetMemoryOnly(actionId, combo, syncReason);
#if AK_DIAGNOSTICS
					string afterSnap = FormatComboSlots(Get(actionId));
					AkEscStartSaveSessionDiagSystem.NoteBindingOverwrite(
						syncReason, actionId, beforeSnap, afterSnap);
#endif
				});
#if AK_DIAGNOSTICS
				ControlsGameUiSeparationTrace.LogStage("5_after_ak_save", actionId, syncReason);
#endif
				return ok;
			}

			ApplySetMemoryOnly(actionId, combo, syncReason);
#if AK_DIAGNOSTICS
			string afterKb = FormatComboSlots(Get(actionId));
			AkEscStartSaveSessionDiagSystem.NoteBindingOverwrite(syncReason, actionId, beforeSnap, afterKb);
#endif
			bool saved = SaveToFile();
			BindingInputEngine.InvalidateBindings();
#if AK_DIAGNOSTICS
			ControlsGameUiSeparationTrace.LogStage("5_after_ak_save", actionId, syncReason);
#endif
			return saved;
		}

		private static string FormatComboSlots(ComboBinding combo)
		{
			if (combo == null || combo.IsEmpty)
				return "(none)";
			return (combo.Slot1 ?? "-") + "/" + (combo.Slot2 ?? "-") + "/" + (combo.Slot3 ?? "-");
		}

		public static bool ClearBindings(string actionId, string reason = null)
		{
			if (string.IsNullOrEmpty(actionId))
				return false;

			if (GamePadTwoKeyTerrariaOrPolicy.IsTargetStorageKey(actionId))
			{
				string syncReason = reason ?? "BindingStorage.ClearBindings";
				return TryCommitGamePadUserEdit(actionId, syncReason, () => {
					Combos[actionId] = new ComboBinding {
						UseTerrariaKeyStatusOr = true,
						PersistAsEmptyRecord = true
					};
					NoteCombosMutated();
					ExplicitClearGamePadTwoKeyKeyStatus(actionId, syncReason);
					// Evaluation-only: do not Remove legacy Combos / do not rewrite legacy out of JSON.
					MarkLegacyEvaluateSuppressedForTrigger(
						ControlsGameUiSeparationPolicy.ToTriggerActionId(actionId));
					MirrorGameBindingToUiIfNeeded(actionId, null, syncReason);
				});
			}

			string clearReason = reason ?? "BindingStorage.ClearBindings";
			if (ShouldPersistPlayerInputProfiles(actionId))
				return TryCommitGamePadUserEdit(actionId, clearReason, () => ApplyClearMemoryOnly(actionId, clearReason));

			ApplyClearMemoryOnly(actionId, clearReason);
			bool saved = SaveToFile();
			BindingInputEngine.InvalidateBindings();
			return saved;
		}

		/// <summary>
		/// True when a GP:G: / GP:U: record exists and has effective TwoKey inputs (read-only).
		/// Used for legacy suppress — empty PersistAsEmptyRecord alone is not enough.
		/// </summary>
		public static bool HasEffectiveGamePadTwoKeyRecord(string storageKey)
		{
			if (!GamePadTwoKeyTerrariaOrPolicy.IsTargetStorageKey(storageKey))
				return false;
			if (!Combos.TryGetValue(storageKey, out ComboBinding combo) || combo == null)
				return false;
			// DT metadata alone must not suppress legacy GP assignment.
			if (combo.DoubleTap)
				return false;
			return GamePadTwoKeyTerrariaOrPolicy.TryGetEffectiveTwoKeyInputs(
				combo.Slot1, combo.Slot2, combo.Slot3, out _, out _);
		}

		/// <summary>
		/// Legacy suppress sibling check: effective TwoKey inputs, or ClearBindings eval-suppress mark
		/// for the same trigger. Never deletes Combos rows.
		/// </summary>
		public static bool HasLegacyEvaluateSuppressingSibling(string twoKeyStorageKey)
		{
			if (HasEffectiveGamePadTwoKeyRecord(twoKeyStorageKey))
				return true;
			if (!GamePadTwoKeyTerrariaOrPolicy.IsTargetStorageKey(twoKeyStorageKey))
				return false;
			return IsLegacyEvaluateSuppressed(
				ControlsGameUiSeparationPolicy.ToTriggerActionId(twoKeyStorageKey));
		}

		/// <summary>True when ClearBindings marked this trigger for legacy evaluate skip (memory-only).</summary>
		public static bool IsLegacyEvaluateSuppressed(string triggerActionId)
		{
			return !string.IsNullOrEmpty(triggerActionId)
				&& LegacyEvaluateSuppressedTriggers.Contains(triggerActionId);
		}

		/// <summary>Test / reload helper: clears memory-only legacy evaluate suppress marks.</summary>
		public static void ClearLegacyEvaluateSuppressState()
			=> LegacyEvaluateSuppressedTriggers.Clear();

		/// <summary>State C: ClearBindings clears KeyStatus for the TwoKey storage mode.</summary>
		private static void ExplicitClearGamePadTwoKeyKeyStatus(string storageKey, string reason)
		{
			string trigger = ControlsGameUiSeparationPolicy.ToTriggerActionId(storageKey);
			int modeOrdinal = GamePadTwoKeyTerrariaOrPolicy.ResolveSyncInputModeOrdinal(storageKey);
			if (modeOrdinal < 0 || string.IsNullOrEmpty(trigger))
				return;
			VanillaRebindHelper.ClearVanillaBindingForInputModes(
				trigger, reason ?? "clear-two-key-explicit", (InputMode)modeOrdinal);
		}

		private static void MarkLegacyEvaluateSuppressedForTrigger(string triggerActionId)
		{
			if (string.IsNullOrEmpty(triggerActionId))
				return;
			LegacyEvaluateSuppressedTriggers.Add(triggerActionId);
		}

		private static void ClearLegacyEvaluateSuppressedForTrigger(string triggerActionId)
		{
			if (string.IsNullOrEmpty(triggerActionId))
				return;
			LegacyEvaluateSuppressedTriggers.Remove(triggerActionId);
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
			NoteCombosMutated();
			_bindingsLoadFailedKeepDisk = false;

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
					_bindingsLoadFailedKeepDisk = true;
#if AK_DIAGNOSTICS
					AkEscStartSaveSessionDiagSystem.NoteBindingLoad(
						"BindingStorage.LoadFromFile",
						"InvalidJson",
						0,
						loaded.Message ?? "invalid json");
#endif
					return;
				}

				if (loaded.Bindings == null || loaded.Bindings.Count == 0)
				{
#if AK_DIAGNOSTICS
					AkEscStartSaveSessionDiagSystem.NoteBindingLoad(
						"BindingStorage.LoadFromFile",
						loaded.Kind.ToString(),
						0,
						"empty-or-null-bindings");
#endif
					return;
				}

				foreach (KeyValuePair<string, ComboBindingData> pair in loaded.Bindings)
				{
					ComboBinding combo = FromData(pair.Value);
					if (combo == null)
						continue;
					if (combo.IsEmpty && !combo.DoubleTap
						&& !(combo.PersistAsEmptyRecord
							&& GamePadTwoKeyTerrariaOrPolicy.IsTargetStorageKey(pair.Key)))
						continue;

					Combos[pair.Key] = combo;
				}
				NoteCombosMutated();

#if AK_DIAGNOSTICS
				AkEscStartSaveSessionDiagSystem.NoteBindingLoad(
					"BindingStorage.LoadFromFile",
					loaded.Kind.ToString(),
					Combos.Count,
					Combos.ContainsKey("Jump") ? "hasJump=1" : "hasJump=0");
#endif

#if AK_DIAGNOSTICS
				ControlsGameUiSeparationTrace.LogStage(
					"6_after_ak_reload",
					"*",
					"LoadFromFile comboCount=" + Combos.Count);
#endif
			}
			catch (Exception ex)
			{
				AdvancedKeybinds.Instance?.Logger.Warn("Advanced Keybinds failed to load bindings: " + ex.Message);
#if AK_DIAGNOSTICS
				AkEscStartSaveSessionDiagSystem.NoteBindingLoad(
					"BindingStorage.LoadFromFile",
					"Exception",
					Combos.Count,
					ex.GetType().Name + ":" + ex.Message);
#endif
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
				AlternateGamePadToken = data.AlternateGamePadToken,
				AdditionalOrToken = data.AdditionalOrToken,
				PersistAsEmptyRecord = data.PersistAsEmptyRecord,
				UseTerrariaKeyStatusOr = data.UseTerrariaKeyStatusOr,
				Hold = data.Hold,
				HoldOnRelease = data.HoldOnRelease,
				HoldMs = data.HoldMs,
				DoubleTap = data.DoubleTap,
				DoubleTapMs = data.DoubleTapMs
			};
		}

		private static ComboBindingData ToData(string storageKey, ComboBinding combo)
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
				AlternateGamePadToken = combo.AlternateGamePadToken,
				AdditionalOrToken = combo.AdditionalOrToken,
				PersistAsEmptyRecord = combo.PersistAsEmptyRecord,
				UseTerrariaKeyStatusOr = combo.UseTerrariaKeyStatusOr,
				Hold = combo.Hold,
				HoldOnRelease = combo.HoldOnRelease,
				HoldMs = combo.HoldMs,
				DoubleTap = combo.DoubleTap,
				DoubleTapMs = combo.DoubleTapMs
			}, storageKey);
		}

		/// <summary>
		/// Inventory/MenuInquiry + AdditionalOr: rewrite VK:0x1B / SC:0x0F into Escape / Tab
		/// on the live ComboBinding before KeyStatus sync (matches JSON sanitize).
		/// </summary>
		private static void NormalizeSharedUiBackAdditionalOrInPlace(string storageOrTriggerId, ComboBinding combo)
		{
			if (combo == null || !combo.HasAdditionalOr)
				return;

			var data = new ComboBindingData {
				Slot1 = combo.Slot1,
				Slot2 = combo.Slot2,
				Slot3 = combo.Slot3,
				AdditionalOrToken = combo.AdditionalOrToken,
				Hold = combo.Hold,
				HoldOnRelease = combo.HoldOnRelease,
				HoldMs = combo.HoldMs
			};
			KeyboardAdditionalOrPolicy.NormalizeSharedUiBackAdditionalOr(storageOrTriggerId, data);
			combo.Slot1 = data.Slot1;
			combo.Slot2 = data.Slot2;
			combo.Slot3 = data.Slot3;
			combo.AdditionalOrToken = data.AdditionalOrToken;
			combo.Hold = data.Hold;
			combo.HoldOnRelease = data.HoldOnRelease;
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

		private static bool SaveToFile()
		{
				var data = new Dictionary<string, ComboBindingData>(Combos.Count, StringComparer.Ordinal);
				foreach (KeyValuePair<string, ComboBinding> pair in Combos)
				{
				ComboBindingData dto = ToData(pair.Key, pair.Value);
				if (dto == null)
					continue;
				if (dto.IsEmpty && !dto.DoubleTap
					&& !(dto.PersistAsEmptyRecord
						&& GamePadTwoKeyTerrariaOrPolicy.IsTargetStorageKey(pair.Key)))
						continue;
					data[pair.Key] = dto;
				}

			return TryWriteBindingsDictionary(data, "BindingStorage.SaveToFile");
		}

		/// <summary>
		/// Writes the given dictionary to bindings.json. On failure returns false without mutating Combos.
		/// </summary>
		private static bool TryWriteBindingsDictionary(
			Dictionary<string, ComboBindingData> data,
			string reason)
		{
			try
			{
				bool diskHasContent = false;
				try
				{
					if (File.Exists(FilePath))
					{
						FileInfo fi = new FileInfo(FilePath);
						diskHasContent = fi.Length > 0;
					}
				}
				catch
				{
					diskHasContent = false;
				}

				if (!BindingSaveIntegrityPolicy.ShouldAllowSaveAfterLoadFailure(
					    loadSucceeded: !_bindingsLoadFailedKeepDisk,
					    memoryCombosEmpty: Combos.Count == 0 && (data == null || data.Count == 0),
					    diskFileExistsWithContent: diskHasContent))
				{
					AdvancedKeybinds.Instance?.Logger.Warn(
						"Advanced Keybinds skipped SaveToFile after failed load to avoid wiping bindings.json");
#if AK_DIAGNOSTICS
					AkEscStartSaveSessionDiagSystem.NoteBindingSave(
						reason ?? "BindingStorage.SaveToFile",
						"Jump",
						success: false,
						detail: "skipped-after-InvalidJson-keep-disk");
#endif
					return false;
				}

				_bindingsLoadFailedKeepDisk = false;

				string directory = Path.GetDirectoryName(FilePath);
				if (!string.IsNullOrEmpty(directory))
					Directory.CreateDirectory(directory);

				data ??= new Dictionary<string, ComboBindingData>(StringComparer.Ordinal);
				File.WriteAllText(FilePath, BindingJsonCodec.Save(data));
#if AK_DIAGNOSTICS
				AkEscStartSaveSessionDiagSystem.NoteBindingSave(
					reason ?? "BindingStorage.SaveToFile",
					data.ContainsKey("Jump") ? "Jump" : "-",
					success: true,
					detail: "comboCount=" + data.Count + (data.ContainsKey("Jump") ? " hasJump=1" : " hasJump=0"));
#endif
				return true;
			}
			catch (Exception ex)
			{
				AdvancedKeybinds.Instance?.Logger.Warn("Advanced Keybinds failed to save bindings: " + ex.Message);
#if AK_DIAGNOSTICS
				AkEscStartSaveSessionDiagSystem.NoteBindingSave(
					reason ?? "BindingStorage.SaveToFile",
					"Jump",
					success: false,
					detail: ex.GetType().Name + ":" + ex.Message);
#endif
				return false;
			}
		}
	}
}
