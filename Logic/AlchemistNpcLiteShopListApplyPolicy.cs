namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Apply-gate for AlchemistNPCLite ShopChangeUI* GamePad compat.
	/// The shop list UI is Alchemist's own; DialogueTweak is not required and is not consulted.
	/// DialogueTweak chat-button navigation remains a separate soft dependency.
	/// </summary>
	public static class AlchemistNpcLiteShopListApplyPolicy
	{
		public enum ApplyGate
		{
			/// <summary>AlchemistNPCLite is not loaded — Compat must stay completely idle.</summary>
			SkipAlchemistAbsent = 0,
			/// <summary>Session already disabled after a hard failure — do not retry until reload.</summary>
			SkipAlreadyDisabled = 1,
			/// <summary>Already wired this session — do not double-apply.</summary>
			SkipAlreadyApplied = 2,
			/// <summary>AlchemistNPCLite is present; attempt Reflection binding. DialogueTweak presence is irrelevant.</summary>
			Proceed = 3
		}

		/// <summary>
		/// DialogueTweak may be present or absent; neither outcome blocks the shop-list Compat.
		/// The parameter exists so tests can pin "DT disabled still Proceed" without reading ModLoader.
		/// </summary>
		public static ApplyGate Decide(
			bool alchemistNpcLitePresent,
			bool dialogueTweakPresent,
			bool alreadyApplied,
			bool alreadyDisabled)
		{
			_ = dialogueTweakPresent;

			if (alreadyDisabled)
				return ApplyGate.SkipAlreadyDisabled;
			if (alreadyApplied)
				return ApplyGate.SkipAlreadyApplied;
			if (!alchemistNpcLitePresent)
				return ApplyGate.SkipAlchemistAbsent;

			return ApplyGate.Proceed;
		}

		public static bool RequiresDialogueTweak() => false;

		public static bool UsesDialogueTweakTypes() => false;
	}
}
