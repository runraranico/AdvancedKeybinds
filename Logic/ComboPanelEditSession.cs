using System;
using System.Collections.Generic;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// In-memory BindingStorage stand-in for ComboPanel edit-session tests.
	/// Never touches disk / bindings.json / Terraria KeyStatus.
	/// </summary>
	public sealed class InMemoryComboBindingStore
	{
		private readonly Dictionary<string, ComboBindingData> _combos =
			new(StringComparer.Ordinal);

		public int StorageWriteCount { get; private set; }
		public int RuntimeUpdateCount { get; private set; }

		/// <summary>Live stored value (tests may assert ReferenceEquals against working).</summary>
		public ComboBindingData GetLive(string actionId)
		{
			if (string.IsNullOrEmpty(actionId) || !_combos.TryGetValue(actionId, out ComboBindingData combo))
				return null;
			return combo;
		}

		/// <summary>Same contract as production <c>BindingStorage.Get</c> after Clone fix: caller never shares storage.</summary>
		public ComboBindingData GetClone(string actionId)
			=> GetLive(actionId)?.Clone();

		public void Set(string actionId, ComboBindingData combo)
		{
			if (string.IsNullOrEmpty(actionId))
				return;

			StorageWriteCount++;
			RuntimeUpdateCount++;

			if (combo == null || combo.IsEmpty)
				_combos.Remove(actionId);
			else
				_combos[actionId] = combo.Clone();
		}

		public void Seed(string actionId, ComboBindingData combo)
		{
			if (string.IsNullOrEmpty(actionId))
				return;
			if (combo == null || combo.IsEmpty)
				_combos.Remove(actionId);
			else
				_combos[actionId] = combo.Clone();
		}

		public void ResetCounters()
		{
			StorageWriteCount = 0;
			RuntimeUpdateCount = 0;
		}
	}

	/// <summary>
	/// Pure ComboPanel edit session: Open snapshot / Working copy / Cancel discard / Save commit.
	/// Mirrors <c>ComboPanel.Open</c> + <c>RestoreOpenedSnapshot</c> + <c>Close(save)</c> + <c>Commit</c>
	/// without Terraria UI. Used by self-diagnosis tests and as the production decision oracle.
	/// </summary>
	public sealed class ComboPanelEditSession
	{
		private readonly InMemoryComboBindingStore _store;

		public ComboPanelEditSession(InMemoryComboBindingStore store)
		{
			_store = store ?? throw new ArgumentNullException(nameof(store));
		}

		public string ActionId { get; private set; }
		public string StorageKey { get; private set; }
		public ControlsEditTargetPolicy.DeviceKind DeviceKind { get; private set; }
		public bool IsOpen { get; private set; }
		public bool Dirty { get; private set; }
		public ComboBindingData Working { get; private set; }
		public ComboBindingData OpenedSnapshot { get; private set; }
		public ComboPanelPadFocus Focus { get; set; } = ComboPanelPadFocus.None;
		public string OpenSource { get; private set; } = "None";
		public string CloseReason { get; private set; }
		public string ReturnTarget { get; private set; } = "Controls";
		public bool CaptureReleaseGateActive { get; private set; }
		public string CaptureReleaseToken { get; private set; }
		public string PendingCaptureToken { get; private set; }

		public int CommitCount { get; private set; }
		public int SaveCount { get; private set; }
		public int CloseCount { get; private set; }
		public int CancelCount { get; private set; }
		/// <summary>True when the last Save path refused commit due to opposite stick directions.</summary>
		public bool CommitBlockedByOppositeStickDirection { get; private set; }

		public int StorageWriteCount => _store.StorageWriteCount;
		public int RuntimeUpdateCount => _store.RuntimeUpdateCount;

		/// <summary>Same rule as <c>ComboPanel.Close</c>: commit only when save &amp;&amp; dirty.</summary>
		public static bool ShouldCommitOnClose(bool save, bool dirty) => save && dirty;

		/// <summary>
		/// Legacy open: KeyboardMouse storage key = bare actionId (existing tests / KB path).
		/// </summary>
		public void Open(string actionId, string openSource = "GamePad")
			=> OpenForDevice(
				actionId,
				ControlsEditTargetPolicy.DeviceKind.KeyboardMouse,
				vanillaSeedToken: null,
				openSource: openSource);

		/// <summary>
		/// Device-aware open. GamePad uses storage key GP:{actionId} and optional vanilla pad seed.
		/// </summary>
		public void OpenForDevice(
			string actionId,
			ControlsEditTargetPolicy.DeviceKind deviceKind,
			string vanillaSeedToken,
			string openSource = "GamePad")
		{
			if (IsOpen)
				Close(save: false, reason: "ReOpen");

			ActionId = ControlsEditTargetPolicy.ToTriggerActionId(actionId);
			DeviceKind = deviceKind;
			StorageKey = ControlsEditTargetPolicy.ToStorageKey(deviceKind, ActionId);
			OpenSource = openSource ?? "None";
			CloseReason = null;
			Focus = ComboPanelPadFocus.None;
			CaptureReleaseGateActive = false;
			CaptureReleaseToken = null;
			PendingCaptureToken = null;

			ComboBindingData stored = _store.GetClone(StorageKey);
			Working = ControlsEditTargetPolicy.CreateOpenWorking(stored, vanillaSeedToken);
			OpenedSnapshot = Working.Clone();
			Dirty = false;
			IsOpen = true;
		}

		public ComboSlotRules.SetSlotResult TrySetSlot(int index, string token)
		{
			EnsureOpen();
			ComboSlotRules.SetSlotResult result = ComboSlotRules.TrySetSlot(Working, index, token);
			if (result == ComboSlotRules.SetSlotResult.Accepted)
			{
				Dirty = true;
				if (GamePadToken.IsPadToken(token) && GamePadToken.TryCanonicalize(token, out string canonical))
				{
					// Mirrors NotifyPadCaptureConsumed: gate until RELEASE for same token.
					CaptureReleaseGateActive = true;
					CaptureReleaseToken = canonical;
					PendingCaptureToken = canonical;
				}
			}

			return result;
		}

		/// <summary>
		/// Test / legacy helper: assign Working without per-slot rules (does not write storage).
		/// </summary>
		public void ReplaceWorkingUnchecked(ComboBindingData data, bool markDirty = true)
		{
			EnsureOpen();
			Working = data?.Clone() ?? new ComboBindingData();
			if (markDirty)
				Dirty = true;
		}

		public void ClearAllSlots()
		{
			EnsureOpen();
			Working = new ComboBindingData { HoldMs = ComboBindingData.DefaultHoldMs };
			Dirty = true;
		}

		public void ClearSlot(int index)
		{
			EnsureOpen();
			if (string.IsNullOrEmpty(Working.GetSlot(index)) && string.IsNullOrEmpty(Working.GetSlotDisplay(index)))
				return;
			Working.SetSlot(index, null);
			Working.SetSlotDisplay(index, null);
			Dirty = true;
		}

		/// <summary>Simulate Pad:B / ○ cancel: restore open snapshot, close without commit.</summary>
		public void CancelWithPadB()
		{
			EnsureOpen();
			CancelCount++;
			RestoreOpenedSnapshot();
			Close(save: false, reason: "PadBCancel");
		}

		/// <summary>Save button / Pad:A on Save focus.</summary>
		public void SaveAndClose()
		{
			EnsureOpen();
			SaveCount++;
			Close(save: true, reason: "Save");
		}

		/// <summary>Outside left-click: same as Save (production Close(save:true)).</summary>
		public void OutsideClickSaveAndClose()
		{
			EnsureOpen();
			SaveCount++;
			Close(save: true, reason: "OutsideClickSave");
		}

		/// <summary>Mirrors <c>RestoreOpenedSnapshot</c> — does not write storage.</summary>
		public void RestoreOpenedSnapshot()
		{
			Working = OpenedSnapshot != null ? OpenedSnapshot.Clone() : new ComboBindingData();
			Dirty = false;
			CaptureReleaseGateActive = false;
			CaptureReleaseToken = null;
			PendingCaptureToken = null;
		}

		/// <summary>
		/// Mirrors <c>ComboPanel.Close</c>. Discard path always restores snapshot before commit check
		/// so a forgotten Restore cannot leave Dirty working eligible for a later mistaken save.
		/// </summary>
		public void Close(bool save, string reason)
		{
			if (!IsOpen)
				return;

			if (!save)
				RestoreOpenedSnapshot();

			bool wasDirty = Dirty;
			CommitBlockedByOppositeStickDirection = false;
			if (save
				&& wasDirty
				&& Working != null
				&& GamePadStickOppositeDirectionRules.HasOppositeConflict(
					Working.Slot1, Working.Slot2, Working.Slot3))
			{
				// Mirror ComboPanel: refuse commit, keep panel open, do not auto-repair slots.
				CommitBlockedByOppositeStickDirection = true;
				return;
			}

			if (ShouldCommitOnClose(save, wasDirty))
				Commit();

			CloseReason = reason ?? (save ? "Save" : "Cancel");
			CloseCount++;
			LastClosedActionId = ActionId;
			LastClosedStorageKey = StorageKey;
			IsOpen = false;
			OpenSource = "None";
			Focus = ComboPanelPadFocus.None;
			Dirty = false;
			Working = null;
			OpenedSnapshot = null;
			ActionId = null;
			StorageKey = null;
			DeviceKind = ControlsEditTargetPolicy.DeviceKind.KeyboardMouse;
			CaptureReleaseGateActive = false;
			CaptureReleaseToken = null;
			PendingCaptureToken = null;
			ReturnTarget = "Controls";
		}

		/// <summary>RELEASE of captured pad token clears gate (same-token cancel unblocked).</summary>
		public void ReleaseCaptureGate()
		{
			CaptureReleaseGateActive = false;
			CaptureReleaseToken = null;
			PendingCaptureToken = null;
		}

		/// <summary>
		/// Pad:B while gate active: blocked only when gate token == Pad:B (registered B itself).
		/// Otherwise cancel proceeds (production TryPadCancelWhileCaptureReleaseGate).
		/// </summary>
		public bool TryPadBWhileCaptureGate()
		{
			EnsureOpen();
			if (CaptureReleaseGateActive
				&& string.Equals(CaptureReleaseToken, GamePadToken.B, StringComparison.OrdinalIgnoreCase))
				return false;

			CancelWithPadB();
			return true;
		}

		public ComboBindingData PeekSaved(string actionIdOrStorageKey = null)
		{
			string id = actionIdOrStorageKey
				?? StorageKey
				?? LastClosedStorageKey
				?? ActionId
				?? LastClosedActionId;
			return _store.GetClone(id);
		}

		public string LastClosedActionId { get; private set; }
		public string LastClosedStorageKey { get; private set; }

		private void Commit()
		{
			if (Working == null || string.IsNullOrEmpty(StorageKey))
				return;

			if (GamePadStickOppositeDirectionRules.HasOppositeConflict(
				Working.Slot1, Working.Slot2, Working.Slot3))
			{
				CommitBlockedByOppositeStickDirection = true;
				return;
			}

			CommitCount++;
			_store.Set(StorageKey, Working);
		}

		private void EnsureOpen()
		{
			if (!IsOpen || Working == null)
				throw new InvalidOperationException("ComboPanelEditSession is not open.");
		}

		public static ComboBindingData Slots(string s1, string s2, string s3)
			=> new() {
				Slot1 = string.IsNullOrEmpty(s1) ? null : s1,
				Slot2 = string.IsNullOrEmpty(s2) ? null : s2,
				Slot3 = string.IsNullOrEmpty(s3) ? null : s3,
				HoldMs = ComboBindingData.DefaultHoldMs
			};

		public static void AssertSlotsEqual(ComboBindingData actual, string s1, string s2, string s3, string context)
		{
			static string N(string v) => string.IsNullOrEmpty(v) ? null : v;
			bool expectEmpty = N(s1) == null && N(s2) == null && N(s3) == null;
			if (actual == null || actual.IsEmpty)
			{
				if (expectEmpty)
					return;
				throw new InvalidOperationException(context + ": actual binding is null/empty");
			}

			if (!string.Equals(actual.Slot1, N(s1), StringComparison.Ordinal)
				|| !string.Equals(actual.Slot2, N(s2), StringComparison.Ordinal)
				|| !string.Equals(actual.Slot3, N(s3), StringComparison.Ordinal))
			{
				throw new InvalidOperationException(
					context
					+ " expected=" + (N(s1) ?? "-") + "/" + (N(s2) ?? "-") + "/" + (N(s3) ?? "-")
					+ " actual=" + actual.FormatSlotsDiag());
			}
		}
	}
}
