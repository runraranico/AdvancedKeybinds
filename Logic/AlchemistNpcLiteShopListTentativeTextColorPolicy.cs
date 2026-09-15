namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Decision policy for AK's tentative shop-name colour on AlchemistNPCLite ShopChangeUI* lists.
	///
	/// AlchemistNPCLite owns the official colours through Shops / ReCheckColor: confirmed shop names are
	/// Lime and every other shop name is White. AK temporarily changes only a different tentative
	/// shop-name UIText.TextColor to Yellow. When tentative and confirmed are the same row, Lime wins.
	/// Before moving or releasing that temporary colour, the production adapter calls the mod's own
	/// ReCheckColor instead of reproducing its colour rules.
	///
	/// This policy owns no drawing, geometry, text, font, checkbox, background or other UI state.
	/// </summary>
	public static class AlchemistNpcLiteShopListTentativeTextColor
	{
		public enum ShopNameColorRole
		{
			OfficialUnconfirmed,
			OfficialConfirmed,
			Tentative
		}

		/// <summary>Color.Yellow RGBA, kept primitive so Core has no XNA dependency.</summary>
		public const byte YellowR = 255;
		public const byte YellowG = 255;
		public const byte YellowB = 0;
		public const byte YellowA = 255;

		public static bool UsesScreenGeometry() => false;
		public static bool WritesTentativeShopNameTextColor() => true;
		public static bool ChangesRowBackground() => false;
		public static bool ChangesCheckboxOrCheckmark() => false;
		public static bool ChangesOtherUiText() => false;
		public static bool RestoresThroughOfficialReCheckColor() => true;

		/// <summary>
		/// Resolves the visible role for one shop name.
		/// When the tentative cursor sits on the officially confirmed shop, Lime (confirmed) wins —
		/// AK must not overwrite AlchemistNPCLite's formal colour with Yellow on that same row.
		/// </summary>
		public static ShopNameColorRole ResolveShopNameColorRole(
			int row,
			int confirmedShop,
			int tentativeShop)
		{
			if (row == confirmedShop)
				return ShopNameColorRole.OfficialConfirmed;
			if (row == tentativeShop)
				return ShopNameColorRole.Tentative;
			return ShopNameColorRole.OfficialUnconfirmed;
		}

		public static bool ShouldTintShopName(
			bool applied,
			bool disabled,
			bool listVisible,
			bool engaged,
			int tentativeIndex,
			int itemCount,
			int boundShopNameCount,
			int confirmedShop = 0)
		{
			if (!applied || disabled || !listVisible || !engaged)
				return false;
			if (itemCount <= 0 || boundShopNameCount != itemCount)
				return false;
			if (tentativeIndex < AlchemistNpcLiteShopListGamepadCompatLayout.DefaultMinIndex
				|| tentativeIndex > itemCount)
				return false;

			// Same row: keep the mod's Lime. Yellow is only for a different tentative choice.
			if (confirmedShop >= AlchemistNpcLiteShopListGamepadCompatLayout.DefaultMinIndex
				&& tentativeIndex == confirmedShop)
				return false;

			return true;
		}

		/// <summary>1-based shop-name row to tint Yellow, or 0 when no shop name may be changed.</summary>
		public static int ResolveTintedShopNameRow(
			bool applied,
			bool disabled,
			bool listVisible,
			bool engaged,
			int tentativeIndex,
			int itemCount,
			int boundShopNameCount,
			int confirmedShop = 0)
			=> ShouldTintShopName(
				applied,
				disabled,
				listVisible,
				engaged,
				tentativeIndex,
				itemCount,
				boundShopNameCount,
				confirmedShop)
				? tentativeIndex
				: 0;

		public static int ToShopNameElementIndex(int row, int itemCount)
		{
			if (row < AlchemistNpcLiteShopListGamepadCompatLayout.DefaultMinIndex || row > itemCount)
				return -1;

			return row - AlchemistNpcLiteShopListGamepadCompatLayout.DefaultMinIndex;
		}

		/// <summary>
		/// Equal state writes nothing. A changed panel/row first restores official colours, then tints the
		/// new shop name.
		/// </summary>
		public static bool NeedsTintMove(int tintedPanelId, int tintedRow, int wantedPanelId, int wantedRow)
			=> tintedPanelId != wantedPanelId || tintedRow != wantedRow;

		public static bool HasTintedShopName(int tintedPanelId, int tintedRow)
			=> tintedPanelId >= 0
				&& tintedRow >= AlchemistNpcLiteShopListGamepadCompatLayout.DefaultMinIndex;
	}
}
