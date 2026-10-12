using System;
using System.Reflection;
using AdvancedKeybinds.Binding;
using AdvancedKeybinds.Core;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ReLogic.Content;
using Terraria;
using Terraria.GameContent.UI.Elements;

namespace AdvancedKeybinds.Patches
{
	/// <summary>
	/// Before <c>UIManageControls.OnActivate</c> → <c>FillList</c>, on Steam Deck force the
	/// controller device tab (<c>OnKeyboard=false</c>) so Deck sessions match pad-open UX.
	/// Frames/borders mirror vanilla <c>GamepadButtonClick</c> / idle <c>ManageBorderGamepadOff</c>.
	/// </summary>
	internal static class ControlsManageOpenGamepadDeviceTab
	{
		private static FieldInfo _onKeyboard;
		private static FieldInfo _buttonKeyboard;
		private static FieldInfo _buttonGamepad;
		private static FieldInfo _buttonBorder1;
		private static FieldInfo _buttonBorder2;
		private static FieldInfo _keyboardGamepadTexture;
		private static bool _fieldsResolved;
		private static bool _fieldsOk;

		/// <summary>
		/// Call before vanilla OnActivate body so AssembleBindPanels/FillList see OnKeyboard=false.
		/// </summary>
		public static void ForceGamepadDeviceTabBeforeActivate(object manageControls, bool isSteamDeck)
		{
			if (manageControls == null
				|| !ControlsManageOpenGamepadDeviceTabPolicy.ShouldForceGamepadDeviceTab(isSteamDeck))
				return;

			EnsureFields(manageControls.GetType());
			if (!_fieldsOk)
				return;

			try
			{
				bool current = (bool)_onKeyboard.GetValue(manageControls);
				_onKeyboard.SetValue(manageControls, false);

				// Same frames as UIManageControls.GamepadButtonClick (Frame is on Texture2D, not Asset).
				if (_keyboardGamepadTexture.GetValue(manageControls) is Asset<Texture2D> asset
					&& asset.IsLoaded
					&& asset.Value != null)
				{
					Texture2D tex = asset.Value;
					var btnKb = _buttonKeyboard.GetValue(manageControls) as UIImageFramed;
					var btnGp = _buttonGamepad.GetValue(manageControls) as UIImageFramed;
					if (btnKb != null)
						btnKb.SetFrame(tex.Frame(2, 2, 0, 1));
					if (btnGp != null)
						btnGp.SetFrame(tex.Frame(2, 2, 1, 0));
				}

				// Idle borders matching ManageBorderGamepadOff when OnKeyboard=false.
				var border1 = _buttonBorder1.GetValue(manageControls) as UIImageFramed;
				var border2 = _buttonBorder2.GetValue(manageControls) as UIImageFramed;
				if (border1 != null)
					border1.Color = Color.Black;
				if (border2 != null)
					border2.Color = Color.Silver;

				if (AkDiag.Enabled
					&& ControlsManageOpenGamepadDeviceTabPolicy.NeedsForceGamepadDeviceTab(isSteamDeck, current))
				{
					AkDiag.Log("ControlsManageOpenGamepadDeviceTab: forced OnKeyboard=false (Steam Deck).");
				}
			}
			catch (Exception ex)
			{
#if AK_DIAGNOSTICS
				if (global::AdvancedKeybinds.Binding.AkDiag.Enabled)
					AdvancedKeybinds.Instance?.Logger.Debug(
						"ControlsManageOpenGamepadDeviceTab: " + ex.GetType().Name + ": " + ex.Message);
#endif
			}
		}

		private static void EnsureFields(Type manageType)
		{
			if (_fieldsResolved)
				return;
			_fieldsResolved = true;

			const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
			_onKeyboard = manageType.GetField("OnKeyboard", flags);
			_buttonKeyboard = manageType.GetField("_buttonKeyboard", flags);
			_buttonGamepad = manageType.GetField("_buttonGamepad", flags);
			_buttonBorder1 = manageType.GetField("_buttonBorder1", flags);
			_buttonBorder2 = manageType.GetField("_buttonBorder2", flags);
			_keyboardGamepadTexture = manageType.GetField("_KeyboardGamepadTexture", flags);

			_fieldsOk = _onKeyboard != null
				&& _onKeyboard.FieldType == typeof(bool)
				&& _buttonKeyboard != null
				&& _buttonGamepad != null
				&& _buttonBorder1 != null
				&& _buttonBorder2 != null
				&& _keyboardGamepadTexture != null;

			if (!_fieldsOk)
			{
				AdvancedKeybinds.Instance?.Logger.Warn(
					"ControlsManageOpenGamepadDeviceTab: UIManageControls OnKeyboard/_button* fields missing; cannot force gamepad device tab.");
			}
		}
	}
}
