using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework.Input;
using Terraria.GameInput;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Clears Terraria parent-UI back/cancel triggers that are currently mapped from a specific
	/// FNA <see cref="Buttons"/> value (e.g. Pad:B → Inventory on XBoxGamepadUI).
	/// Independent of Escape swallow / ConsumeEscape state.
	/// </summary>
	public static class GamePadParentUiTriggerSuppress
	{
		/// <summary>
		/// Triggers that Terraria uses for menu back / cancel / close on gamepad UI
		/// (see PlayerInput BuildCommand Back = Inventory | MouseRight; Escape path also uses MenuInquiry).
		/// Names verified against KnownTriggers / ProfileGamepadUI — not invented.
		/// </summary>
		private static readonly string[] ParentNavTriggerNames = {
			"Inventory",
			"MouseRight",
			"MenuInquiry",
			"MenuUp",
			"MenuDown",
			"MenuLeft",
			"MenuRight"
		};

		/// <summary>
		/// Vanilla KeyStatus binding key: <c>Buttons.ToString()</c> (e.g. <c>"A"</c>, <c>"B"</c>),
		/// matching <c>CheckRebindingProcessGamepad</c> / preset <c>Concat((Buttons)…)</c>.
		/// </summary>
		public static bool TryGetButtonsBindingKey(string padToken, out string buttonsKey)
		{
			buttonsKey = null;
			if (!GamePadToken.TryCanonicalize(padToken, out string canonical))
				return false;

			Buttons? button = CanonicalToButtons(canonical);
			if (!button.HasValue)
				return false;

			buttonsKey = button.Value.ToString();
			return !string.IsNullOrEmpty(buttonsKey);
		}

		public static Buttons? CanonicalToButtons(string canonical)
		{
			if (string.Equals(canonical, GamePadToken.A, StringComparison.OrdinalIgnoreCase))
				return Buttons.A;
			if (string.Equals(canonical, GamePadToken.B, StringComparison.OrdinalIgnoreCase))
				return Buttons.B;
			if (string.Equals(canonical, GamePadToken.X, StringComparison.OrdinalIgnoreCase))
				return Buttons.X;
			if (string.Equals(canonical, GamePadToken.Y, StringComparison.OrdinalIgnoreCase))
				return Buttons.Y;
			if (string.Equals(canonical, GamePadToken.Back, StringComparison.OrdinalIgnoreCase))
				return Buttons.Back;
			if (string.Equals(canonical, GamePadToken.Start, StringComparison.OrdinalIgnoreCase))
				return Buttons.Start;
			if (string.Equals(canonical, GamePadToken.Guide, StringComparison.OrdinalIgnoreCase))
				return Buttons.BigButton;
			if (string.Equals(canonical, GamePadToken.LeftShoulder, StringComparison.OrdinalIgnoreCase))
				return Buttons.LeftShoulder;
			if (string.Equals(canonical, GamePadToken.RightShoulder, StringComparison.OrdinalIgnoreCase))
				return Buttons.RightShoulder;
			if (string.Equals(canonical, GamePadToken.LeftStick, StringComparison.OrdinalIgnoreCase))
				return Buttons.LeftStick;
			if (string.Equals(canonical, GamePadToken.RightStick, StringComparison.OrdinalIgnoreCase))
				return Buttons.RightStick;
			if (string.Equals(canonical, GamePadToken.DPadUp, StringComparison.OrdinalIgnoreCase))
				return Buttons.DPadUp;
			if (string.Equals(canonical, GamePadToken.DPadDown, StringComparison.OrdinalIgnoreCase))
				return Buttons.DPadDown;
			if (string.Equals(canonical, GamePadToken.DPadLeft, StringComparison.OrdinalIgnoreCase))
				return Buttons.DPadLeft;
			if (string.Equals(canonical, GamePadToken.DPadRight, StringComparison.OrdinalIgnoreCase))
				return Buttons.DPadRight;
			// Triggers are analog — not stored as Buttons enum in KeyStatus the same way.
			return null;
		}

		/// <summary>
		/// During purple registration wait: strip all known parent back/cancel triggers
		/// (gamepad or otherwise) so capture cannot also dismiss the parent menu.
		/// </summary>
		public static void ClearAllParentNavTriggers(TriggersSet set, List<string> clearedOut)
		{
			if (set?.KeyStatus == null)
				return;

			for (int i = 0; i < ParentNavTriggerNames.Length; i++)
			{
				string name = ParentNavTriggerNames[i];
				if (!set.KeyStatus.TryGetValue(name, out bool on) || !on)
					continue;
				set.KeyStatus[name] = false;
				clearedOut?.Add(name);
			}
		}

		/// <summary>
		/// Clear only parent-nav triggers whose XBoxGamepadUI binding list contains this pad button.
		/// Does not clear unrelated GamePad UI (e.g. Pad:A → MouseLeft stays).
		/// </summary>
		public static void ClearParentNavTriggersMappedToPad(
			TriggersSet set,
			string padToken,
			List<string> clearedOut)
		{
			if (set?.KeyStatus == null || string.IsNullOrEmpty(padToken))
				return;

			if (!TryGetButtonsBindingKey(padToken, out string buttonsKey))
				return;

			KeyConfiguration ui = null;
			try
			{
				ui = PlayerInput.CurrentProfile?.InputModes?[InputMode.XBoxGamepadUI];
			}
			catch
			{
				ui = null;
			}

			if (ui?.KeyStatus == null)
			{
				// Profile unavailable: fall back to clearing all parent-nav (safe for capture hold).
				ClearAllParentNavTriggers(set, clearedOut);
				return;
			}

			for (int i = 0; i < ParentNavTriggerNames.Length; i++)
			{
				string name = ParentNavTriggerNames[i];
				if (!set.KeyStatus.TryGetValue(name, out bool on) || !on)
					continue;
				if (!ui.KeyStatus.TryGetValue(name, out List<string> bindings) || bindings == null)
					continue;
				if (!BindingsContainButtonsKey(bindings, buttonsKey))
					continue;

				set.KeyStatus[name] = false;
				clearedOut?.Add(name);
			}
		}

		/// <summary>
		/// While ComboPanel pad-nav is active, also clear MouseLeft so Pad:A confirm does not
		/// click through to parent UILinkPoint rows.
		/// </summary>
		public static void ClearMouseLeft(TriggersSet set, List<string> clearedOut)
		{
			if (set?.KeyStatus == null)
				return;
			if (!set.KeyStatus.TryGetValue("MouseLeft", out bool on) || !on)
				return;
			set.KeyStatus["MouseLeft"] = false;
			clearedOut?.Add("MouseLeft");
		}

		private static bool BindingsContainButtonsKey(List<string> bindings, string buttonsKey)
		{
			for (int i = 0; i < bindings.Count; i++)
			{
				string b = bindings[i];
				if (string.IsNullOrEmpty(b))
					continue;
				if (string.Equals(b, buttonsKey, StringComparison.OrdinalIgnoreCase))
					return true;

				// Older / alternate writers may store numeric enum values ("4096").
				if (Enum.TryParse(buttonsKey, ignoreCase: true, out Buttons named)
					&& int.TryParse(b, out int numeric)
					&& numeric == (int)named)
					return true;
			}

			return false;
		}
	}
}
