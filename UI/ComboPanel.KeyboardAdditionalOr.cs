using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using AdvancedKeybinds.Binding;
using AdvancedKeybinds.Core;
using ReLogic.Graphics;
using Terraria;
using Terraria.GameContent;
using Terraria.Localization;

namespace AdvancedKeybinds.UI
{
	/// <summary>Keyboard Additional Key (OR) tab state and chrome for allowlisted vanilla actions.</summary>
	internal static partial class ComboPanel
	{
		private enum KeyboardPanelTab
		{
			And = 0,
			AdditionalOr = 1
		}

		private static bool _keyboardAdditionalOrEligible;
		private static KeyboardPanelTab _keyboardPanelTab;
		private static Rectangle _tabAndRect;
		private static Rectangle _tabOrRect;
		private static bool _hoverTabAnd;
		private static bool _hoverTabOr;

		private static readonly Color AdditionalOrLockedFill = new(28, 30, 40);
		private static readonly Color AdditionalOrLockedBorder = new(60, 60, 78);

		private static bool IsKeyboardAdditionalOrTab
			=> _keyboardAdditionalOrEligible && _keyboardPanelTab == KeyboardPanelTab.AdditionalOr;

		private static void ResetKeyboardAdditionalOrSession()
		{
			_keyboardAdditionalOrEligible = false;
			_keyboardPanelTab = KeyboardPanelTab.And;
			_tabAndRect = default;
			_tabOrRect = default;
			_hoverTabAnd = false;
			_hoverTabOr = false;
		}

		private static void InitKeyboardAdditionalOrOnOpen(bool isModKeybind)
		{
			_keyboardAdditionalOrEligible = KeyboardAdditionalOrPolicy.IsPanelEligible(
				_editDeviceKind == ControlsEditTargetPolicy.DeviceKind.GamePad,
				(int)_editInputMode,
				_sectionSemantic,
				isModKeybind,
				_actionId);
			_keyboardPanelTab = KeyboardPanelTab.And;
		}

		/// <summary>
		/// Folder-style tabs sit on the top edge of the panel (outside the client area).
		/// Tabs are flush (no gap). Width grows with localized label length (min = Hold column).
		/// </summary>
		private static void LayoutKeyboardAdditionalOrTabs(int contentLeft, int panelTop, int holdColW)
		{
			if (!_keyboardAdditionalOrEligible)
			{
				_tabAndRect = default;
				_tabOrRect = default;
				return;
			}

			const int tabH = HoldControlBoxH;
			const int gap = 0;
			// Small overlap keeps the tab attached to the panel top without sinking into the title.
			const int overlapIntoPanel = 2;
			const float labelScale = 0.75f;
			const int labelPadX = 12;
			var font = FontAssets.MouseText.Value;
			string andLabel = Language.GetTextValue("Mods.AdvancedKeybinds.UI.AdditionalOr.TabAnd");
			string orLabel = Language.GetTextValue("Mods.AdvancedKeybinds.UI.AdditionalOr.TabOr");
			int andNeed = (int)Math.Ceiling(font.MeasureString(andLabel ?? string.Empty).X * labelScale) + labelPadX * 2;
			int orNeed = (int)Math.Ceiling(font.MeasureString(orLabel ?? string.Empty).X * labelScale) + labelPadX * 2;
			int tabW = Math.Max(holdColW, Math.Max(andNeed, orNeed));
			int y = panelTop - tabH + overlapIntoPanel;
			_tabAndRect = new Rectangle(contentLeft, y, tabW, tabH);
			_tabOrRect = new Rectangle(_tabAndRect.Right + gap, y, tabW, tabH);
		}

		/// <summary>Tabs are outside the panel; interior layout does not reserve a tab band.</summary>
		private static int KeyboardAdditionalOrTabBandHeight => 0;

		private static void UpdateKeyboardAdditionalOrTabHover(Point mouse)
		{
			_hoverTabAnd = _keyboardAdditionalOrEligible && _tabAndRect.Contains(mouse);
			_hoverTabOr = _keyboardAdditionalOrEligible && _tabOrRect.Contains(mouse);
		}

		private static bool TryClickKeyboardAdditionalOrTab(Point mouse)
		{
			if (!_keyboardAdditionalOrEligible)
				return false;

			if (_tabAndRect.Contains(mouse))
			{
				if (_keyboardPanelTab != KeyboardPanelTab.And)
				{
					_keyboardPanelTab = KeyboardPanelTab.And;
					_capturingSlot = 0;
					SlotCapture.Cancel();
					_awaitingSlotKey = false;
				}
				return true;
			}

			if (_tabOrRect.Contains(mouse))
			{
				if (_keyboardPanelTab != KeyboardPanelTab.AdditionalOr)
				{
					_keyboardPanelTab = KeyboardPanelTab.AdditionalOr;
					_capturingSlot = 0;
					SlotCapture.Cancel();
					_awaitingSlotKey = false;
					_awaitingKeyInfoPick = false;
				}
				return true;
			}

			return false;
		}

		private static void DrawKeyboardAdditionalOrTabs(SpriteBatch spriteBatch)
		{
			if (!_keyboardAdditionalOrEligible)
				return;

			bool andSelected = _keyboardPanelTab == KeyboardPanelTab.And;
			bool orSelected = _keyboardPanelTab == KeyboardPanelTab.AdditionalOr;
			DrawKeyboardAdditionalOrTabStrip(
				spriteBatch,
				_tabAndRect,
				_tabOrRect,
				Language.GetTextValue("Mods.AdvancedKeybinds.UI.AdditionalOr.TabAnd"),
				Language.GetTextValue("Mods.AdvancedKeybinds.UI.AdditionalOr.TabOr"),
				andSelected,
				orSelected,
				_hoverTabAnd,
				_hoverTabOr);

			if (_hoverTabAnd)
				DrawKeyboardAdditionalOrTabTooltip(
					spriteBatch,
					_tabAndRect,
					Language.GetTextValue("Mods.AdvancedKeybinds.UI.AdditionalOr.TabAndTooltipTitle"),
					Language.GetTextValue("Mods.AdvancedKeybinds.UI.AdditionalOr.TabAndTooltipBody"));
			else if (_hoverTabOr)
				DrawKeyboardAdditionalOrTabTooltip(
					spriteBatch,
					_tabOrRect,
					Language.GetTextValue("Mods.AdvancedKeybinds.UI.AdditionalOr.TabOrTooltipTitle"),
					Language.GetTextValue("Mods.AdvancedKeybinds.UI.AdditionalOr.TabOrTooltipBody"));
		}

		/// <summary>
		/// Draw AND+OR as one connected strip (no inter-tab gap / no double shared border).
		/// Selected tab omits the bottom edge so it merges into the panel.
		/// Highlight frame uses white (panel chrome), not blue.
		/// </summary>
		private static void DrawKeyboardAdditionalOrTabStrip(
			SpriteBatch spriteBatch,
			Rectangle andRect,
			Rectangle orRect,
			string andLabel,
			string orLabel,
			bool andSelected,
			bool orSelected,
			bool andHover,
			bool orHover)
		{
			Texture2D pixel = TextureAssets.MagicPixel.Value;
			Color andFill = andSelected ? new Color(46, 58, 88) : new Color(32, 34, 48);
			Color orFill = orSelected ? new Color(46, 58, 88) : new Color(32, 34, 48);
			Color idleBorder = new Color(90, 90, 120);
			Color selectedBorder = Color.White;
			Color andBorder = andSelected ? selectedBorder : (andHover ? Color.White : idleBorder);
			Color orBorder = orSelected ? selectedBorder : (orHover ? Color.White : idleBorder);

			Rectangle union = new Rectangle(
				andRect.X,
				andRect.Y,
				orRect.Right - andRect.X,
				andRect.Height);

			// Continuous fill first so no background seam shows between tabs.
			spriteBatch.Draw(pixel, union, andFill);
			spriteBatch.Draw(pixel, andRect, andFill);
			spriteBatch.Draw(pixel, orRect, orFill);

			const int t = 2;

			// Outer top / left / right (white when either tab is selected).
			Color outerTop = andSelected || orSelected ? selectedBorder : idleBorder;
			spriteBatch.Draw(pixel, new Rectangle(union.X, union.Y, union.Width, t), outerTop);
			spriteBatch.Draw(pixel, new Rectangle(union.X, union.Y, t, union.Height), andBorder);
			spriteBatch.Draw(pixel, new Rectangle(union.Right - t, union.Y, t, union.Height), orBorder);

			// Bottom: only under the unselected tab(s) so the active tab opens into the panel.
			if (!andSelected)
				spriteBatch.Draw(pixel, new Rectangle(andRect.X, andRect.Bottom - t, andRect.Width, t), andBorder);
			if (!orSelected)
				spriteBatch.Draw(pixel, new Rectangle(orRect.X, orRect.Bottom - t, orRect.Width, t), orBorder);

			// Single shared divider on the shared edge (no empty gap).
			int divX = andRect.Right - 1;
			Color divColor = andSelected || orSelected ? selectedBorder : idleBorder;
			spriteBatch.Draw(pixel, new Rectangle(divX, union.Y + t, 1, Math.Max(1, union.Height - t * 2)), divColor);

			if (andSelected)
				spriteBatch.Draw(pixel, new Rectangle(andRect.X + t, andRect.Y, andRect.Width - t, 3), selectedBorder);
			if (orSelected)
				spriteBatch.Draw(pixel, new Rectangle(orRect.X, orRect.Y, orRect.Width - t, 3), selectedBorder);

			DrawCentered(spriteBatch, andLabel, andRect, Color.White, maxScale: 0.75f);
			DrawCentered(spriteBatch, orLabel, orRect, Color.White, maxScale: 0.75f);
		}

		private static void DrawKeyboardAdditionalOrTabTooltip(
			SpriteBatch spriteBatch,
			Rectangle tab,
			string title,
			string body)
		{
			var font = FontAssets.MouseText.Value;
			float scale = 0.75f;
			const int padX = 8;
			const int padY = 6;
			const int lineGap = 2;
			int maxInnerW = Math.Max(160, Math.Min(420, Main.screenWidth - 32));

			title ??= string.Empty;
			body ??= string.Empty;
			if (title.StartsWith("Mods.", StringComparison.Ordinal))
				title = string.Empty;
			if (body.StartsWith("Mods.", StringComparison.Ordinal))
				body = string.Empty;

			string[] bodyLines = WrapTooltipLine(font, body, scale, maxInnerW);
			float titleW = string.IsNullOrEmpty(title) ? 0f : font.MeasureString(title).X * scale;
			float bodyMaxW = 0f;
			float bodyH = 0f;
			for (int i = 0; i < bodyLines.Length; i++)
			{
				Vector2 sz = font.MeasureString(bodyLines[i]) * scale;
				if (sz.X > bodyMaxW)
					bodyMaxW = sz.X;
				bodyH += sz.Y;
				if (i + 1 < bodyLines.Length)
					bodyH += lineGap;
			}

			float innerW = Math.Max(titleW, bodyMaxW);
			float w = innerW + padX * 2;
			float titleH = string.IsNullOrEmpty(title) ? 0f : font.MeasureString(title).Y * scale;
			float h = padY * 2 + titleH + (titleH > 0 && bodyH > 0 ? lineGap : 0) + bodyH;
			int x = tab.Center.X - (int)(w / 2);
			int y = tab.Y - (int)h - 8;
			if (x < 8)
				x = 8;
			if (x + (int)w > Main.screenWidth - 8)
				x = Math.Max(8, Main.screenWidth - 8 - (int)w);
			if (y < 8)
				y = tab.Bottom + 6;
			var box = new Rectangle(x, y, (int)Math.Ceiling(w), (int)Math.Ceiling(h));
			DrawBox(spriteBatch, box, new Color(20, 24, 36), Color.White);
			float ty = box.Y + padY;
			if (!string.IsNullOrEmpty(title))
			{
				Terraria.Utils.DrawBorderString(spriteBatch, title, new Vector2(box.X + padX, ty), Color.White, scale);
				ty += titleH + lineGap;
			}

			Color bodyColor = new Color(200, 210, 230);
			for (int i = 0; i < bodyLines.Length; i++)
			{
				Terraria.Utils.DrawBorderString(
					spriteBatch, bodyLines[i], new Vector2(box.X + padX, ty), bodyColor, scale);
				ty += font.MeasureString(bodyLines[i]).Y * scale + lineGap;
			}
		}

		/// <summary>Greedy wrap so long localized tooltip bodies stay inside an expanding frame.</summary>
		private static string[] WrapTooltipLine(DynamicSpriteFont font, string text, float scale, float maxWidth)
		{
			if (string.IsNullOrEmpty(text))
				return Array.Empty<string>();
			if (font.MeasureString(text).X * scale <= maxWidth)
				return new[] { text };

			var lines = new System.Collections.Generic.List<string>(4);
			// Prefer spaces when present; otherwise break by character (CJK / long tokens).
			if (text.IndexOf(' ') >= 0)
			{
				string[] words = text.Split(' ');
				string cur = string.Empty;
				for (int i = 0; i < words.Length; i++)
				{
					string next = string.IsNullOrEmpty(cur) ? words[i] : cur + " " + words[i];
					if (font.MeasureString(next).X * scale <= maxWidth || string.IsNullOrEmpty(cur))
					{
						cur = next;
						continue;
					}

					lines.Add(cur);
					cur = words[i];
				}

				if (!string.IsNullOrEmpty(cur))
					lines.Add(cur);
				return lines.ToArray();
			}

			string line = string.Empty;
			for (int i = 0; i < text.Length; i++)
			{
				string next = line + text[i];
				if (font.MeasureString(next).X * scale <= maxWidth || line.Length == 0)
				{
					line = next;
					continue;
				}

				lines.Add(line);
				line = text[i].ToString();
			}

			if (!string.IsNullOrEmpty(line))
				lines.Add(line);
			return lines.ToArray();
		}

		private static string ResolveAdditionalOrSlotLabel(int slotIndex)
		{
			if (!IsKeyboardAdditionalOrTab || _working == null)
				return null;

			if (slotIndex == 1)
			{
				string andLabel = FormatAndMirrorLabel();
				return string.IsNullOrEmpty(andLabel)
					? Language.GetTextValue("Mods.AdvancedKeybinds.UI.Combo.Empty")
					: andLabel;
			}

			// Locked center explanation slot: intentionally blank (no "追加キー設定").
			if (slotIndex == 2)
				return string.Empty;

			if (slotIndex == 3)
			{
				if (!string.IsNullOrEmpty(_working.AdditionalOrToken))
					return KeyToken.FormatDisplayForToken(_working.AdditionalOrToken);
				return Language.GetTextValue("Mods.AdvancedKeybinds.UI.AdditionalOr.SlotEmpty");
			}

			return null;
		}

		private static string FormatAndMirrorLabel()
		{
			if (_working == null || _working.FilledSlotCount == 0)
				return string.Empty;

			string joined = _working.FormatJoinedLabel(compact: true);
			string whileHeld = Language.GetTextValue("Mods.AdvancedKeybinds.UI.Combo.HoldPreviewWhileHeld");
			string onRelease = Language.GetTextValue("Mods.AdvancedKeybinds.UI.Combo.HoldPreviewOnRelease");
			return BindingDisplayText.ApplyHoldPrefix(
				joined,
				_working.Hold,
				_working.HoldOnRelease,
				_working.FilledSlotCount,
				whileHeld,
				onRelease);
		}

		private static bool TrySetAdditionalOrToken(string token)
		{
			if (_working == null)
				return false;

			if (!KeyboardAdditionalOrPolicy.TryNormalizeToken(
				    token, _working.Keys, out string canonical, out _))
				return false;

			if (string.Equals(_working.AdditionalOrToken, canonical, StringComparison.Ordinal))
				return true;

			PushEditHistoryBeforeMutation("AdditionalOr");
			_working.AdditionalOrToken = canonical;
			_dirty = true;
			return true;
		}

		private static void ClearAdditionalOrToken()
		{
			if (_working == null || string.IsNullOrEmpty(_working.AdditionalOrToken))
				return;
			PushEditHistoryBeforeMutation("AdditionalOrClear");
			_working.AdditionalOrToken = null;
			_dirty = true;
		}
	}
}
