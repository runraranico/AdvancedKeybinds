namespace AdvancedKeybinds.Features.DoubleTap
{
	/// <summary>
	/// DTフラグ時の経路：既存（通常/Hold）／無効（AND無発動）／専用Judge接続要求。
	/// InactiveはDTのAND側のみ。OR・他操作・バニラ入力を消す権限ではない。
	/// DedicatedJudgeは即発動ではなく第1段階Judgeへの接続要求。
	/// </summary>
	public enum DoubleTapRoute
	{
		Existing = 0,
		Inactive = 1,
		DedicatedJudge = 2
	}

	public static class DoubleTapRoutePolicy
	{
		public static DoubleTapRoute Decide(
			bool doubleTap,
			bool hold,
			int slotCount,
			bool targetEligible,
			bool featureEnabled,
			bool judgeConnected)
		{
			if (!doubleTap)
				return DoubleTapRoute.Existing;
			if (hold || slotCount != 1 || !targetEligible || !featureEnabled || !judgeConnected)
				return DoubleTapRoute.Inactive;
			return DoubleTapRoute.DedicatedJudge;
		}
	}
}
