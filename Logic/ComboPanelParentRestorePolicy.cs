using System;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Priority for restoring Controls list focus after ComboPanel closes.
	/// PointId is last-resort only — list rebuild remaps indices.
	/// Parent section headers / title / Root are never valid focus restore targets.
	/// </summary>
	public enum ComboPanelParentRestoreMatchKind
	{
		None = 0,
		ElementReference = 1,
		ActionId = 2,
		BindingStableId = 3,
		PointIdHintVerified = 4
	}

	/// <summary>
	/// Column of the Controls list row that opened ComboPanel.
	/// </summary>
	public enum ComboPanelParentRestoreColumn
	{
		Unknown = -1,
		LeftOrFull = 0,
		Right = 1
	}

	/// <summary>
	/// Pure decision helper shared by production restore and self-diagnosis tests.
	/// UI Plus ON/OFF share this same policy — no host-specific branches.
	/// </summary>
	public static class ComboPanelParentRestorePolicy
	{
		/// <summary>
		/// Picks the best available match for a keybind row (UIKeybindingListItem).
		/// Never selects a section header, screen title, or Root.
		/// </summary>
		public static ComboPanelParentRestoreMatchKind ChooseBest(
			bool elementRefFound,
			bool actionIdFound,
			bool bindingStableIdFound,
			bool pointHintStillMapsToAction)
		{
			if (elementRefFound)
				return ComboPanelParentRestoreMatchKind.ElementReference;
			if (actionIdFound)
				return ComboPanelParentRestoreMatchKind.ActionId;
			if (bindingStableIdFound)
				return ComboPanelParentRestoreMatchKind.BindingStableId;
			if (pointHintStillMapsToAction)
				return ComboPanelParentRestoreMatchKind.PointIdHintVerified;
			return ComboPanelParentRestoreMatchKind.None;
		}

		/// <summary>
		/// Legacy 4-bool API: parent-header matches are ignored (headers / 操作設定 are forbidden).
		/// </summary>
		public static ComboPanelParentRestoreMatchKind ChooseBestIgnoringParentHeader(
			bool actionIdFound,
			bool bindingStableIdFound,
			bool parentHeaderFound,
			bool pointHintStillMapsToAction)
		{
			_ = parentHeaderFound;
			return ChooseBest(
				elementRefFound: false,
				actionIdFound,
				bindingStableIdFound,
				pointHintStillMapsToAction);
		}

		/// <summary>
		/// Right-column Thin halves are not IsNavigable but still own a link point and must restore.
		/// </summary>
		public static bool IsRestorableBindingRow(
			bool isNavigable,
			bool isHorizontalOnly,
			bool isBindingKind,
			bool isHorizontalRightKind)
		{
			if (isNavigable)
				return true;
			if (isHorizontalOnly)
				return true;
			if (isBindingKind)
				return true;
			if (isHorizontalRightKind)
				return true;
			return false;
		}

		/// <summary>
		/// Forbidden ChangePoint targets: Back / tabs / anything below list start (title chrome / Root).
		/// </summary>
		public static bool IsForbiddenRestorePointId(
			int pointId,
			int backPointId,
			int listPointsStart,
			int keyboardTabPointId,
			int gamepadTabPointId)
		{
			if (pointId == int.MinValue)
				return true;
			if (pointId == backPointId)
				return true;
			if (pointId == keyboardTabPointId || pointId == gamepadTabPointId)
				return true;
			if (pointId < listPointsStart)
				return true;
			return false;
		}

		public static ComboPanelParentRestoreColumn ColumnFromHorizontalOnly(bool isHorizontalOnly)
			=> isHorizontalOnly
				? ComboPanelParentRestoreColumn.Right
				: ComboPanelParentRestoreColumn.LeftOrFull;

		/// <summary>
		/// When ActionId matches multiple rows (should be rare), prefer the open-time column.
		/// </summary>
		public static int PreferColumnMatch(int preferredColumn, int candidateColumn, int currentBestIndex, int candidateIndex)
		{
			if (preferredColumn < 0)
				return currentBestIndex < 0 ? candidateIndex : currentBestIndex;

			if (currentBestIndex < 0)
				return candidateIndex;

			if (candidateColumn == preferredColumn && currentBestIndex >= 0)
			{
				// Keep existing if it already matches preferred; else take candidate.
				return currentBestIndex;
			}

			return candidateIndex;
		}

		/// <summary>
		/// Element-reference restore gate (Terraria-free). Production maps UIElement checks onto these flags.
		/// </summary>
		public static bool CanRestoreElementReference(
			bool elementNonNull,
			bool notDisposed,
			bool stillUnderParentList,
			bool visible)
		{
			return elementNonNull && notDisposed && stillUnderParentList && visible;
		}

		public static bool ShouldPreferStableIdentityOverPointId() => true;

		public const int DeferredRestoreFrames = 8;
	}
}
