namespace AdvancedKeybinds.Features.DoubleTap
{
	/// <summary>
	/// DT対象候補の判定。仕様のバニラ22 ID、または登録済みMOD。登録表は読まない（呼出側が bool を渡す）。
	/// </summary>
	public static class DoubleTapTargetPolicy
	{
		static readonly System.Collections.Generic.HashSet<string> VanillaTargets =
			new(System.StringComparer.Ordinal) {
				"MapFull", "MapStyle", "MapZoomIn", "MapZoomOut", "MapAlphaUp", "MapAlphaDown",
				"Grapple", "SmartSelect", "SmartCursor", "QuickMount", "QuickHeal", "QuickMana",
				"QuickBuff", "Throw", "Inventory", "ToggleCreativeMenu", "ViewZoomIn", "ViewZoomOut",
				"Loadout1", "Loadout2", "Loadout3", "ToggleCameraMode"
			};

		static readonly System.Collections.Generic.HashSet<string> ContinuousTargets =
			new(System.StringComparer.Ordinal) {
				"ViewZoomIn", "ViewZoomOut", "MapZoomIn", "MapZoomOut", "MapAlphaUp", "MapAlphaDown"
			};

		public static bool IsVanillaTarget(string id)
			=> id != null && VanillaTargets.Contains(id);

		public static bool IsContinuousVanillaTarget(string id)
			=> id != null && ContinuousTargets.Contains(id);

		public static bool IsEligible(string actionId, bool isRegisteredModKeybind)
		{
			if (string.IsNullOrWhiteSpace(actionId))
				return false;
			if (IsVanillaTarget(actionId))
				return true;
			if (!isRegisteredModKeybind)
				return false;
			int slash = actionId.IndexOf('/');
			return slash > 0 && slash < actionId.Length - 1
				&& !actionId.StartsWith("GP:", System.StringComparison.Ordinal)
				&& !actionId.StartsWith("KU:", System.StringComparison.Ordinal);
		}
	}
}
