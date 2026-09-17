using System;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// 表示名変更で打ったキーが、キャラクター操作へ戻ったあと Jump/Throw/QuickMount 等として
	/// 発火する不具合の契約（LIVE 2026-09-16）。
	/// Keyboard / Main.keyState が離されていても、scan-held ゴースト → ApplyAdvanced が
	/// Triggers Current を立て続ける。設定 ESC 離脱での長い Jump クリアは禁止。
	/// </summary>
	public static class DisplayNameEditGameplayResiduePolicy
	{
		/// <summary>表示名終了直後の短フレーム settle（長時間ゲームプレイ無効化はしない）。</summary>
		public const int SettleFramesAfterNameEditEnd = 6;

		/// <summary>
		/// 表示名でよく打たれ、バニラ／Advanced に紐づきやすい Triggers。
		/// LIVE: T→Throw、Space→Jump、R→QuickMount。
		/// </summary>
		public static readonly string[] GameplayTriggersToClear = {
			"Jump",
			"Throw",
			"Grapple",
			"QuickMount",
			"QuickHeal",
			"QuickMana",
			"QuickBuff"
		};

		/// <summary>
		/// 表示名セッション終了時に Win32 scan-held / gameplay edges を掃除するか。
		/// IME 欠落 KEYUP 対策。既存 <c>ClearUnconfirmedScanHeldFlags</c> 呼び出し契約。
		/// </summary>
		public static bool ShouldClearScanHeldFlagsOnNameEditEnd()
			=> true;

		/// <summary>表示名編集終了からのみ settle を武装してよい。</summary>
		public static bool ShouldArmSettleFromNameEditEnd()
			=> true;

		/// <summary>
		/// 設定 ESC 離脱ゲートから settle を武装してはならない
		/// （過去に Jump が操作画面で長時間死ぬ退行を起こした）。
		/// </summary>
		public static bool ShouldArmSettleFromSettingsEscapeLeave()
			=> false;

		/// <summary>
		/// ApplyAdvanced の後に Jump/Throw 等をクリアすべきか。
		/// パネルオープン中の settle、短フレーム、および
		/// HandleEscape が orig より前に閉じる閉じフレームを含む。
		/// </summary>
		public static bool ShouldClearGameplayTriggersThisTick(
			bool settleFramesRemaining,
			bool settleWhilePanelOpenArmed,
			bool comboPanelOpen)
		{
			if (settleFramesRemaining)
				return true;
			if (settleWhilePanelOpenArmed && comboPanelOpen)
				return true;
			// Close frame after name edit: BackgroundInputBlock skipped this tick.
			if (settleWhilePanelOpenArmed && !comboPanelOpen)
				return true;
			return false;
		}

		/// <summary>
		/// 閉じフレームのクリア後に「パネル開中 settle」フラグを降ろすか。
		/// </summary>
		public static bool ShouldDisarmSettleWhilePanelOpen(
			bool settleWhilePanelOpenArmed,
			bool comboPanelOpen,
			bool settleFramesRemaining)
			=> settleWhilePanelOpenArmed && !comboPanelOpen && !settleFramesRemaining;

		/// <summary>
		/// LIVE 症状: 物理キーも Main.keyState も上がっているのに Triggers が立っている。
		/// </summary>
		public static bool IsTypedKeyResidueSymptom(
			bool keyboardKeyDown,
			bool mainKeyStateDown,
			bool triggerJustPressedOrCurrent)
			=> !keyboardKeyDown
				&& !mainKeyStateDown
				&& triggerJustPressedOrCurrent;

		public static bool IsProtectedGameplayTrigger(string triggerName)
		{
			if (string.IsNullOrEmpty(triggerName))
				return false;
			for (int i = 0; i < GameplayTriggersToClear.Length; i++)
			{
				if (string.Equals(GameplayTriggersToClear[i], triggerName, StringComparison.OrdinalIgnoreCase))
					return true;
			}

			return false;
		}
	}
}
