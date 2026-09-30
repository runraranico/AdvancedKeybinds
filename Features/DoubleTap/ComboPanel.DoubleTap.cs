using System;
using AdvancedKeybinds.Binding;
using AdvancedKeybinds.Core;
using AdvancedKeybinds.Features.DoubleTap;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.Audio;
using Terraria.GameContent;
using Terraria.ID;
using Terraria.Localization;

namespace AdvancedKeybinds.UI
{
	/// <summary>
	/// Double Tap column / duration wiring for ComboPanel. Uses <see cref="DoubleTapPanelPolicy"/>.
	/// No new diagnostic logs. Mechanical UI connection only.
	/// </summary>
	internal static partial class ComboPanel
	{
		static readonly Color DoubleTapSelectedFill = new(255, 100, 0);

		static Rectangle _doubleTapBox;
		static bool _hoverDoubleTap;

		static bool IsDoubleTapUiContextLocked
			=> _twoKeySetupEligible || IsKeyboardAdditionalOrTab;

		static bool IsDoubleTapStorageEligible
		{
			get
			{
				string key = _storageKey
					?? ControlsGameUiSeparationPolicy.ResolveStorageKey((int)_editInputMode, _actionId);
				return DoubleTapBindingBridge.IsEligibleStorage(key);
			}
		}

		static bool CanEnableDoubleTapUi
			=> DoubleTapPanelPolicy.CanEnableForUi(
				_working,
				IsDoubleTapStorageEligible,
				IsKeyboardAdditionalOrTab,
				_twoKeySetupEligible,
				KeyboardLayoutHelper.IsJapaneseHankakuKeyContext());

		static bool IsWorkingPrimaryForbiddenForDoubleTapUi
			=> DoubleTapPanelPolicy.IsForbiddenPrimaryForUi(
				_working,
				KeyboardLayoutHelper.IsJapaneseHankakuKeyContext());

		static bool DurationEditable
			=> _working != null
				&& !IsKeyboardAdditionalOrTab
				&& ((_working.DoubleTap && !IsDoubleTapUiContextLocked)
					|| HoldEditable);

		static int ActiveDurationMs
			=> DoubleTapPanelPolicy.Duration(_working);

		static int ActiveDefaultDurationMs
			=> DoubleTapPanelPolicy.DefaultDuration(_working);

		static string ActiveDurationDefaultButtonLabel
		{
			get
			{
				string baseLabel = Language.GetTextValue("Mods.AdvancedKeybinds.UI.Combo.HoldDefaultLabel");
				if (_working != null && _working.DoubleTap)
					return baseLabel + " : " + DoubleTapMsPolicy.DefaultMs + "ms";
				return baseLabel + " : " + ComboBinding.DefaultHoldMs + "ms";
			}
		}

		static string ActiveDurationTooltip
		{
			get
			{
				if (_working != null && _working.DoubleTap)
					return Language.GetTextValue("Mods.AdvancedKeybinds.UI.Combo.DoubleTapMsInputTooltip");
				return Language.GetTextValue("Mods.AdvancedKeybinds.UI.Combo.HoldMsInputTooltip");
			}
		}

		static void SyncDurationBufferFromWorking()
		{
			_holdBuffer = ActiveDurationMs.ToString();
		}

		static void ApplyDoubleTapAfterSlotsEdited()
		{
			if (_working == null)
				return;
			if (DoubleTapPanelPolicy.OnSlotsEditedForUi(
				    _working,
				    KeyboardLayoutHelper.IsJapaneseHankakuKeyContext()))
				SyncDurationBufferFromWorking();
		}

		static bool WorkingNeedsClearIncludingDoubleTap()
		{
			if (_working == null)
				return false;
			if (_working.DoubleTap)
				return true;
			return ComboPanelClearPolicy.ShouldClearWorkingBinding(
				_working.IsEmpty,
				_working.HasAlternateGamePad,
				_working.Hold,
				_working.HoldOnRelease);
		}

		static void ToggleDoubleTapFromUi()
		{
			if (_working == null || IsDoubleTapUiContextLocked)
			{
				SoundEngine.PlaySound(SoundID.MenuTick);
				return;
			}

			// Only block forbidden primaries. Keep Toggle path for saved non-forbidden DT
			// even when eligible=false / CanEnableForUi is false (OFF must remain possible).
			if (IsWorkingPrimaryForbiddenForDoubleTapUi)
			{
				SoundEngine.PlaySound(SoundID.MenuTick);
				return;
			}

			ComboBinding trial = _working.Clone();
			if (!DoubleTapPanelPolicy.Toggle(
				    trial,
				    IsDoubleTapStorageEligible,
				    IsKeyboardAdditionalOrTab,
				    _twoKeySetupEligible))
			{
				SoundEngine.PlaySound(SoundID.MenuTick);
				return;
			}

			PushEditHistoryBeforeMutation("DoubleTapChange");
			DoubleTapPanelPolicy.Toggle(
				_working,
				IsDoubleTapStorageEligible,
				IsKeyboardAdditionalOrTab,
				_twoKeySetupEligible);
			_dirty = true;
			SyncDurationBufferFromWorking();
			SoundEngine.PlaySound(SoundID.MenuTick);
		}

		static void ApplyHoldModeViaPolicy(bool hold, bool onRelease)
		{
			if (_working == null)
				return;
			ComboBinding trial = _working.Clone();
			if (!DoubleTapPanelPolicy.SetHoldMode(trial, hold, onRelease))
			{
				SoundEngine.PlaySound(SoundID.MenuTick);
				return;
			}
			PushEditHistoryBeforeMutation("HoldChange");
			DoubleTapPanelPolicy.SetHoldMode(_working, hold, onRelease);
			_dirty = true;
			SyncDurationBufferFromWorking();
			SoundEngine.PlaySound(SoundID.MenuTick);
		}

		static void SetActiveDuration(int value, bool slider)
		{
			if (_working == null || !DurationEditable)
				return;

			ComboBinding trial = _working.Clone();
			if (!DoubleTapPanelPolicy.SetDuration(trial, value, slider))
			{
				SyncDurationBufferFromWorking();
				return;
			}

			PushEditHistoryBeforeMutation("DurationChange");
			DoubleTapPanelPolicy.SetDuration(_working, value, slider);
			_dirty = true;
			SyncDurationBufferFromWorking();
		}

		static void ResetActiveDurationToDefault()
		{
			if (_working == null || !DurationEditable)
				return;
			ComboBinding trial = _working.Clone();
			if (!DoubleTapPanelPolicy.ResetDuration(trial))
			{
				SyncDurationBufferFromWorking();
				return;
			}
			PushEditHistoryBeforeMutation("DurationReset");
			DoubleTapPanelPolicy.ResetDuration(_working);
			_dirty = true;
			SyncDurationBufferFromWorking();
		}

		static void LayoutHoldModeColumn(int contentLeft, int rowY, int holdColW, int slotH)
		{
			_holdColumn = new Rectangle(contentLeft, rowY, holdColW, slotH);
			int topH = slotH / 3;
			int midH = slotH / 3;
			int bottomH = slotH - topH - midH;
			_holdWhileHeldBox = new Rectangle(contentLeft, rowY, holdColW, topH);
			_holdOnReleaseBox = new Rectangle(contentLeft, rowY + topH, holdColW, midH);
			_doubleTapBox = new Rectangle(contentLeft, rowY + topH + midH, holdColW, bottomH);
		}

		static void DrawDoubleTapModeBox(SpriteBatch spriteBatch)
		{
			bool locked = IsDoubleTapUiContextLocked;
			bool forbiddenPrimary = IsWorkingPrimaryForbiddenForDoubleTapUi;
			// Keep legacy active exception for saved DT, except forbidden primaries (grey only).
			bool canEnable = !locked
				&& (CanEnableDoubleTapUi
					|| (_working != null && _working.DoubleTap && !forbiddenPrimary));
			bool active = _working != null && _working.DoubleTap && !locked && !forbiddenPrimary;

			Color fill = locked
				? new Color(28, 30, 40)
				: (!canEnable && !active
					? new Color(40, 40, 46)
					: (active ? DoubleTapSelectedFill : new Color(48, 50, 66)));
			Color border = locked
				? new Color(60, 60, 78)
				: (!canEnable && !active
					? new Color(72, 72, 84)
					: (_hoverDoubleTap ? Color.White : new Color(110, 110, 140)));

			if (!locked)
			{
				Color? pad = ComboPanelGamePadNavigation.TryGetFocusBorder(
					ComboPanelPadFocus.DoubleTap, border, capturing: false);
				if (pad.HasValue)
					border = pad.Value;
			}

			DrawBox(spriteBatch, _doubleTapBox, fill, border);

			Color textColor = locked
				? new Color(90, 90, 105)
				: ((canEnable || active) ? Color.White : new Color(120, 120, 120));
			string title = Language.GetTextValue("Mods.AdvancedKeybinds.UI.Combo.DoubleTapTitle");
			string hint = locked
				? Language.GetTextValue("Mods.AdvancedKeybinds.UI.TwoKeySetup.HoldUnavailableHint")
				: Language.GetTextValue("Mods.AdvancedKeybinds.UI.Combo.DoubleTapHint");
			DrawEnlargedDoubleTapModeText(spriteBatch, _doubleTapBox, title, hint, textColor);
		}

		/// <summary>
		/// DT box title/hint: maximize each line independently inside equal bands using
		/// glyph ink union (not MeasureString.Y / LineSpacing). Self-contained; not shared with Hold.
		/// </summary>
		private static void DrawEnlargedDoubleTapModeText(SpriteBatch spriteBatch, Rectangle rect, string title, string hint, Color color)
		{
			const float inset = 2f;
			const float bandSep = 2f;
			const float shadow = 1.5f;
			var font = FontAssets.MouseText.Value;

			float innerW = rect.Width - inset * 2f;
			float innerH = rect.Height - inset * 2f - bandSep;
			if (innerW <= 0f || innerH <= 0f)
				return;

			float bandH = innerH * 0.5f;
			float budgetW = innerW - shadow * 2f;
			float budgetH = bandH - shadow * 2f;
			if (budgetW <= 0f || budgetH <= 0f)
				return;

			float contentLeft = rect.X + inset;
			float titleBandTop = rect.Y + inset;
			float hintBandTop = titleBandTop + bandH + bandSep;

			bool TryGlyphUnion(string text, out float minX, out float minY, out float maxX, out float maxY)
			{
				minX = minY = maxX = maxY = 0f;
				if (string.IsNullOrEmpty(text))
					return false;

				bool hasInk = false;
				float penX = 0f;
				float penY = 0f;
				bool firstOnLine = true;
				float uMinX = float.PositiveInfinity;
				float uMinY = float.PositiveInfinity;
				float uMaxX = float.NegativeInfinity;
				float uMaxY = float.NegativeInfinity;

				for (int i = 0; i < text.Length; i++)
				{
					char c = text[i];
					if (c == '\r')
						continue;
					if (c == '\n')
					{
						penX = 0f;
						penY += font.LineSpacing;
						firstOnLine = true;
						continue;
					}

					bool foundGlyph = font.SpriteCharacters.TryGetValue(c, out var sc);
					if (!foundGlyph)
						sc = font.DefaultCharacterData;

					float left = firstOnLine ? Math.Max(sc.Kerning.X, 0f) : sc.Kerning.X;
					if (!firstOnLine)
						penX += font.CharacterSpacing;
					firstOnLine = false;

					bool skipInk = char.IsWhiteSpace(c) && foundGlyph;
					if (!skipInk && sc.Glyph.Width > 0 && sc.Glyph.Height > 0)
					{
						float gx = penX + left + sc.Padding.X;
						float gy = penY + sc.Padding.Y;
						float gw = sc.Glyph.Width;
						float gh = sc.Glyph.Height;
						if (gx < uMinX) uMinX = gx;
						if (gy < uMinY) uMinY = gy;
						if (gx + gw > uMaxX) uMaxX = gx + gw;
						if (gy + gh > uMaxY) uMaxY = gy + gh;
						hasInk = true;
					}

					penX += left + sc.Kerning.Y + sc.Kerning.Z;
				}

				if (!hasInk)
					return false;

				minX = uMinX;
				minY = uMinY;
				maxX = uMaxX;
				maxY = uMaxY;
				return true;
			}

			void DrawBand(string text, float bandTop, Color drawColor)
			{
				if (string.IsNullOrEmpty(text))
					return;
				if (!TryGlyphUnion(text, out float minX, out float minY, out float maxX, out float maxY))
					return;

				float unionW = maxX - minX;
				float unionH = maxY - minY;
				if (unionW <= 0f || unionH <= 0f)
					return;

				float scale = Math.Min(budgetW / unionW, budgetH / unionH);
				if (scale <= 0f)
					return;

				float posX = contentLeft + innerW * 0.5f - (minX + maxX) * 0.5f * scale;
				float posY = bandTop + bandH * 0.5f - (minY + maxY) * 0.5f * scale;
				Vector2 pos = new Vector2(posX, posY);
				Color shadowColor = new Color(0, 0, 0, (int)drawColor.A);
				ReLogic.Graphics.DynamicSpriteFontExtensionMethods.DrawString(
					spriteBatch, font, text, pos + new Vector2(-1.5f, 0f), shadowColor, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
				ReLogic.Graphics.DynamicSpriteFontExtensionMethods.DrawString(
					spriteBatch, font, text, pos + new Vector2(1.5f, 0f), shadowColor, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
				ReLogic.Graphics.DynamicSpriteFontExtensionMethods.DrawString(
					spriteBatch, font, text, pos + new Vector2(0f, -1.5f), shadowColor, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
				ReLogic.Graphics.DynamicSpriteFontExtensionMethods.DrawString(
					spriteBatch, font, text, pos + new Vector2(0f, 1.5f), shadowColor, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
				ReLogic.Graphics.DynamicSpriteFontExtensionMethods.DrawString(
					spriteBatch, font, text, pos, drawColor, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
			}

			DrawBand(title, titleBandTop, color);
			DrawBand(hint, hintBandTop, color * 0.92f);
		}

		static void DrawHoldColumnDividers(SpriteBatch spriteBatch, bool locked)
		{
			Texture2D pixel = TextureAssets.MagicPixel.Value;
			Color divider = locked ? new Color(60, 60, 78) : new Color(110, 110, 140);
			spriteBatch.Draw(
				pixel,
				new Rectangle(_holdColumn.X, _holdWhileHeldBox.Bottom - 1, _holdColumn.Width, 2),
				divider);
			spriteBatch.Draw(
				pixel,
				new Rectangle(_holdColumn.X, _holdOnReleaseBox.Bottom - 1, _holdColumn.Width, 2),
				divider);
		}

		static string BuildDoubleTapPreviewPrefix()
		{
			if (_working == null || !_working.DoubleTap)
				return null;
			return Language.GetTextValue("Mods.AdvancedKeybinds.UI.Combo.DoubleTapPreview");
		}
	}
}
