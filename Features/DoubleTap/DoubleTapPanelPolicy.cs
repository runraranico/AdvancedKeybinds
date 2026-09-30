using System;
using AdvancedKeybinds.Binding;
using AdvancedKeybinds.Core;

namespace AdvancedKeybinds.Features.DoubleTap
{
	/// <summary>
	/// Panel-local horizontal geometry for ComboPanel. Pure; no I/O.
	/// Baseline width 822 = 600+16+22+170+14. Narrow widths scale by PanelWidth/822.
	/// </summary>
	public readonly struct DoubleTapHorizontalMetrics
	{
		public readonly int PanelWidth;
		public readonly int KeyInfoLeft;
		public readonly int KeyInfoWidth;
		public readonly int NameLeft;
		public readonly int NameWidth;
		public readonly int ResetLeft;
		public readonly int ResetWidth;
		public readonly int SlotsLeft;
		public readonly int SlotsWidth;
		public readonly int SliderLeft;
		public readonly int SliderWidth;
		public readonly int ButtonsLeft;
		public readonly int ButtonsWidth;
		public readonly int ContentMargin;
		public readonly int LeftBlockWidth;
		public readonly int HoldColumnWidth;
		public readonly int HoldControlBoxWidth;
		public readonly int HoldControlGap;
		public readonly int PanelButtonWidth;
		public readonly int SlotRowGap;
		public readonly int KeyInfoPad;
		public readonly int KeyInfoSpliceGap;
		public readonly int KeyInfoLeftInset;
		public readonly int KeyInfoRightPad;

		public DoubleTapHorizontalMetrics(
			int panelWidth,
			int keyInfoLeft, int keyInfoWidth,
			int nameLeft, int nameWidth,
			int resetLeft, int resetWidth,
			int slotsLeft, int slotsWidth,
			int sliderLeft, int sliderWidth,
			int buttonsLeft, int buttonsWidth,
			int contentMargin, int leftBlockWidth, int holdColumnWidth,
			int holdControlBoxWidth, int holdControlGap, int panelButtonWidth,
			int slotRowGap, int keyInfoPad,
			int keyInfoSpliceGap, int keyInfoLeftInset, int keyInfoRightPad)
		{
			PanelWidth = panelWidth;
			KeyInfoLeft = keyInfoLeft;
			KeyInfoWidth = keyInfoWidth;
			NameLeft = nameLeft;
			NameWidth = nameWidth;
			ResetLeft = resetLeft;
			ResetWidth = resetWidth;
			SlotsLeft = slotsLeft;
			SlotsWidth = slotsWidth;
			SliderLeft = sliderLeft;
			SliderWidth = sliderWidth;
			ButtonsLeft = buttonsLeft;
			ButtonsWidth = buttonsWidth;
			ContentMargin = contentMargin;
			LeftBlockWidth = leftBlockWidth;
			HoldColumnWidth = holdColumnWidth;
			HoldControlBoxWidth = holdControlBoxWidth;
			HoldControlGap = holdControlGap;
			PanelButtonWidth = panelButtonWidth;
			SlotRowGap = slotRowGap;
			KeyInfoPad = keyInfoPad;
			KeyInfoSpliceGap = keyInfoSpliceGap;
			KeyInfoLeftInset = keyInfoLeftInset;
			KeyInfoRightPad = keyInfoRightPad;
		}
	}

	/// <summary>
	/// Pure UI edit rules for 1-key Double Tap (ComboPanel will call later). No I/O.
	/// </summary>
	public static class DoubleTapPanelPolicy
	{
		public const int BasePanelHeight = 444;
		public const int BaseSlotColumnHeight = 131;
		public const int LayoutMarginTop = 40;
		public const int MaxExtraHeight = 0;
		public const int LayoutSegmentCount = 3;
		public const int SideMargin = 20;
		public const int BaselinePanelWidth = 600 + 16 + 22 + 170 + 14;

		static int FilledSlotCount(ComboBinding working)
		{
			if (working == null)
				return 0;
			int n = 0;
			if (!string.IsNullOrEmpty(working.Slot1))
				n++;
			if (!string.IsNullOrEmpty(working.Slot2))
				n++;
			if (!string.IsNullOrEmpty(working.Slot3))
				n++;
			return n;
		}

		static string SingleFilledSlotToken(ComboBinding working)
		{
			if (working == null)
				return null;
			string token = null;
			int n = 0;
			if (!string.IsNullOrEmpty(working.Slot1))
			{
				token = working.Slot1;
				n++;
			}
			if (!string.IsNullOrEmpty(working.Slot2))
			{
				token = working.Slot2;
				n++;
			}
			if (!string.IsNullOrEmpty(working.Slot3))
			{
				token = working.Slot3;
				n++;
			}
			return n == 1 ? token : null;
		}

		static bool NameEquals(string tokenOrName, string expected)
			=> string.Equals(tokenOrName, expected, StringComparison.OrdinalIgnoreCase);

		/// <summary>
		/// UI-only primary-key forbid flag. Pure / non-mutating.
		/// CapsLock / Win / Enter / NumLock / ScrollLock: all contexts. Kana-family: japaneseContext only.
		/// Normal SC 0x45/0x46 = NumLock/ScrollLock; extended SC 0x46 (Pause candidate) is not rejected.
		/// </summary>
		public static bool IsForbiddenPrimaryForUi(ComboBinding working, bool japaneseContext)
		{
			string token = SingleFilledSlotToken(working);
			if (string.IsNullOrWhiteSpace(token))
				return false;

			token = token.Trim();
			string name = PhysicalKeyToken.StripKeyAliasPrefix(token);

			if (NameEquals(name, "CapsLock")
				|| NameEquals(name, "LeftWindows")
				|| NameEquals(name, "RightWindows")
				|| NameEquals(name, "Enter")
				|| NameEquals(name, "NumLock")
				|| NameEquals(name, "ScrollLock"))
				return true;

			if (PhysicalKeyToken.TryParseVk(token, out uint vk))
			{
				if (vk == 0x14u || vk == 0x5Bu || vk == 0x5Cu || vk == 0x0Du
					|| vk == 0x90u || vk == 0x91u)
					return true;
				if (japaneseContext && (vk == 0x15u || vk == 0xF2u))
					return true;
			}

			if (PhysicalKeyToken.TryParseScan(token, out ushort scan, out bool extended))
			{
				if (scan == 0x3A && !extended)
					return true;
				if (scan == 0x5B && extended)
					return true;
				if (scan == 0x5C && extended)
					return true;
				if (scan == 0x1C)
					return true;
				if (scan == 0x45 && !extended)
					return true;
				if (scan == 0x46 && !extended)
					return true;
				if (japaneseContext && scan == 0x70 && !extended)
					return true;
			}

			if (japaneseContext
				&& (NameEquals(name, "Kana") || NameEquals(name, "OemCopy")))
				return true;

			return false;
		}

		public static bool CanEnableForUi(
			ComboBinding working,
			bool eligible,
			bool additionalOrTab,
			bool twoKeyLocked,
			bool japaneseContext)
			=> CanEnable(working, eligible, additionalOrTab, twoKeyLocked)
				&& !IsForbiddenPrimaryForUi(working, japaneseContext);

		static int RoundHoldSlider(int value)
		{
			int clamped = Math.Clamp(value, ComboBinding.MinHoldMs, ComboBinding.SliderMaxHoldMs);
			return (int)(Math.Round(clamped / 10.0) * 10.0);
		}

		public static bool CanEnable(
			ComboBinding working,
			bool eligible,
			bool additionalOrTab,
			bool twoKeyLocked)
		{
			if (working == null || !eligible || additionalOrTab || twoKeyLocked)
				return false;
			return FilledSlotCount(working) == 1;
		}

		public static bool Toggle(
			ComboBinding working,
			bool eligible,
			bool additionalOrTab,
			bool twoKeyLocked)
		{
			if (working == null)
				return false;

			if (working.DoubleTap)
			{
				working.DoubleTap = false;
				return true;
			}

			if (!CanEnable(working, eligible, additionalOrTab, twoKeyLocked))
				return false;

			working.DoubleTap = true;
			working.Hold = false;
			working.HoldOnRelease = false;
			return true;
		}

		public static bool OnSlotsEdited(ComboBinding working)
		{
			if (working == null)
				return false;
			if (FilledSlotCount(working) == 1)
				return false;
			if (!working.DoubleTap)
				return false;
			working.DoubleTap = false;
			return true;
		}

		/// <summary>
		/// UI slot-edit path. Keeps <see cref="OnSlotsEdited"/> multi-key clear contract,
		/// then clears <see cref="ComboBinding.DoubleTap"/> only when the remaining single
		/// primary is forbidden for UI. Does not mutate slots, Hold, HoldOnRelease, or DoubleTapMs.
		/// </summary>
		public static bool OnSlotsEditedForUi(ComboBinding working, bool japaneseContext)
		{
			if (working == null)
				return false;

			bool changed = OnSlotsEdited(working);
			if (!working.DoubleTap)
				return changed;

			if (!IsForbiddenPrimaryForUi(working, japaneseContext))
				return changed;

			working.DoubleTap = false;
			return true;
		}

		public static bool SetHoldMode(ComboBinding working, bool hold, bool onRelease)
		{
			if (working == null)
				return false;

			bool nextHold = hold;
			bool nextOnRelease = hold && onRelease;
			bool changed = working.Hold != nextHold || working.HoldOnRelease != nextOnRelease;

			// Repair abnormal Hold+DT coexisting even when flags already match the request.
			if (nextHold && working.DoubleTap)
			{
				working.DoubleTap = false;
				changed = true;
			}

			if (!changed)
				return false;

			working.Hold = nextHold;
			working.HoldOnRelease = nextOnRelease;
			return true;
		}

		public static int Duration(ComboBinding working)
		{
			if (working == null)
				return ComboBinding.DefaultHoldMs;
			if (working.DoubleTap)
				return DoubleTapMsPolicy.Clamp(working.DoubleTapMs);
			if (working.Hold)
				return working.EffectiveHoldMs;
			return working.EffectiveHoldMs;
		}

		public static int DefaultDuration(ComboBinding working)
		{
			if (working != null && working.DoubleTap)
				return DoubleTapMsPolicy.DefaultMs;
			return ComboBinding.DefaultHoldMs;
		}

		public static bool SetDuration(ComboBinding working, int value, bool slider)
		{
			if (working == null)
				return false;

			if (working.DoubleTap)
			{
				int next = slider ? DoubleTapMsPolicy.Slider(value) : DoubleTapMsPolicy.Clamp(value);
				if (working.DoubleTapMs == next)
					return false;
				working.DoubleTapMs = next;
				return true;
			}

			// Matches ComboPanel: hold duration controls inactive when Hold is off.
			if (!working.Hold)
				return false;

			int holdNext = slider
				? RoundHoldSlider(value)
				: Math.Clamp(value, ComboBinding.MinTypedHoldMs, ComboBinding.MaxHoldMs);
			if (working.HoldMs == holdNext)
				return false;
			working.HoldMs = holdNext;
			return true;
		}

		// Pad number editing has always used Hold's typed range, not its slider range.
		public static int PadNumberValue(ComboBinding working, int value)
		{
			if (working?.DoubleTap == true)
				return DoubleTapMsPolicy.Slider(value);
			int rounded = (int)(Math.Round(value / 10.0) * 10.0);
			return Math.Clamp(rounded, ComboBinding.MinTypedHoldMs, ComboBinding.MaxHoldMs);
		}

		public static int PadSliderValue(ComboBinding working, int value)
			=> working?.DoubleTap == true ? DoubleTapMsPolicy.Slider(value) : value;

		public static bool ResetDuration(ComboBinding working)
		{
			if (working == null)
				return false;

			if (working.DoubleTap)
			{
				if (working.DoubleTapMs == DoubleTapMsPolicy.DefaultMs)
					return false;
				working.DoubleTapMs = DoubleTapMsPolicy.DefaultMs;
				return true;
			}

			if (!working.Hold)
				return false;

			if (working.HoldMs == ComboBinding.DefaultHoldMs)
				return false;
			working.HoldMs = ComboBinding.DefaultHoldMs;
			return true;
		}

		public static int ExtraHeight(int screenHeight)
		{
			int extra = screenHeight - LayoutMarginTop - BasePanelHeight;
			return Math.Clamp(extra, 0, MaxExtraHeight);
		}

		public static int PanelHeight(int screenHeight) => BasePanelHeight + ExtraHeight(screenHeight);

		public static int SlotColumnHeight(int screenHeight) => BaseSlotColumnHeight + ExtraHeight(screenHeight);

		public static void SegmentHeights(int screenHeight, out int top, out int middle, out int bottom)
		{
			int total = SlotColumnHeight(screenHeight);
			top = total / LayoutSegmentCount;
			middle = total / LayoutSegmentCount;
			bottom = total - top - middle;
		}

		/// <summary>
		/// Pure horizontal layout for screen width. Panel-local X (origin = panel left).
		/// Wide: exact baseline 822. Narrow: panelW=min(822,screenWidth-40), scale by panelW/822.
		/// </summary>
		public static DoubleTapHorizontalMetrics GetHorizontalMetrics(int screenWidth)
		{
			int panelW = Math.Min(BaselinePanelWidth, screenWidth - SideMargin * 2);
			if (panelW < 0)
				panelW = 0;

			double factor = BaselinePanelWidth > 0 ? panelW / (double)BaselinePanelWidth : 0.0;
			int Round(int value)
				=> panelW == BaselinePanelWidth ? value : (int)Math.Round(value * factor);

			int splice = Round(16);
			int inset = Round(22);
			int info = Round(170);
			int rightPad = Round(14);
			int leftBlock = panelW - splice - inset - info - rightPad;
			int margin = Round(18);
			int holdCol = Round(168);
			int control = Round(112);
			int btn = Round(120);
			int gap = Round(10);
			int keyPad = Round(8);
			int holdGap = Round(10);

			int contentLeft = margin;
			int leftContentRight = leftBlock - margin;
			int holdColRight = contentLeft + holdCol;
			int slotsLeft = holdColRight + gap;
			int slotsWidth = leftContentRight - slotsLeft;
			int sliderLeft = contentLeft + control + holdGap;
			int sliderWidth = (leftContentRight - control - holdGap) - sliderLeft;
			int buttonsLeft = contentLeft;
			int buttonsWidth = leftContentRight - contentLeft;
			int keyInfoLeft = leftContentRight + splice + inset;
			int keyInfoWidth = info;
			int nameLeft = keyInfoLeft + keyPad;
			int nameWidth = keyInfoWidth - keyPad * 2;
			int resetWidth = nameWidth;
			int resetLeft = keyInfoLeft + (keyInfoWidth - resetWidth) / 2;

			return new DoubleTapHorizontalMetrics(
				panelW,
				keyInfoLeft, keyInfoWidth,
				nameLeft, nameWidth,
				resetLeft, resetWidth,
				slotsLeft, slotsWidth,
				sliderLeft, sliderWidth,
				buttonsLeft, buttonsWidth,
				margin, leftBlock, holdCol,
				control, holdGap, btn,
				gap, keyPad,
				splice, inset, rightPad);
		}
	}
}
