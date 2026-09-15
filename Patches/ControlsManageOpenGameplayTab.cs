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
	/// Before <c>UIManageControls.OnActivate</c> → <c>FillList</c>, force Gameplay tab
	/// (剣アイコン／ゲーム操作) so Menu Controls is never restored from a prior close.
	/// </summary>
	internal static class ControlsManageOpenGameplayTab
	{
		private static FieldInfo _onGameplay;
		private static FieldInfo _buttonVs1;
		private static FieldInfo _buttonVs2;
		private static FieldInfo _buttonBorderVs1;
		private static FieldInfo _buttonBorderVs2;
		private static FieldInfo _gameplayVsUiTexture;
		private static bool _fieldsResolved;
		private static bool _fieldsOk;

		/// <summary>
		/// Call before vanilla OnActivate body so AssembleBindPanels/FillList see OnGameplay=true.
		/// </summary>
		public static void ForceGameplayTabBeforeActivate(object manageControls)
		{
			if (manageControls == null || !ControlsManageOpenGameplayTabPolicy.ShouldForceGameplayTabOnActivate())
				return;

			EnsureFields(manageControls.GetType());
			if (!_fieldsOk)
				return;

			try
			{
				bool current = (bool)_onGameplay.GetValue(manageControls);
				_onGameplay.SetValue(manageControls, true);

				// Same frames as UIManageControls.VsGameplayButtonClick (Asset Frame via Utils).
				if (_gameplayVsUiTexture.GetValue(manageControls) is Asset<Texture2D> tex && tex.IsLoaded)
				{
					var btn1 = _buttonVs1.GetValue(manageControls) as UIImageFramed;
					var btn2 = _buttonVs2.GetValue(manageControls) as UIImageFramed;
					if (btn1 != null)
						btn1.SetFrame(tex.Frame(2, 2));
					if (btn2 != null)
						btn2.SetFrame(tex.Frame(2, 2, 1, 1));
				}

				// Idle borders matching ManageBorderGameplayOff when OnGameplay=true.
				var border1 = _buttonBorderVs1.GetValue(manageControls) as UIImageFramed;
				var border2 = _buttonBorderVs2.GetValue(manageControls) as UIImageFramed;
				if (border1 != null)
					border1.Color = Color.Silver;
				if (border2 != null)
					border2.Color = Color.Black;

				if (AkDiag.Enabled && ControlsManageOpenGameplayTabPolicy.NeedsForceGameplayTab(current))
					AkDiag.Log("ControlsManageOpenGameplayTab: forced OnGameplay=true (was Menu Controls).");
			}
			catch (Exception ex)
			{
				AdvancedKeybinds.Instance?.Logger.Debug(
					"ControlsManageOpenGameplayTab: " + ex.GetType().Name + ": " + ex.Message);
			}
		}

		private static void EnsureFields(Type manageType)
		{
			if (_fieldsResolved)
				return;
			_fieldsResolved = true;

			const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
			_onGameplay = manageType.GetField("OnGameplay", flags);
			_buttonVs1 = manageType.GetField("_buttonVs1", flags);
			_buttonVs2 = manageType.GetField("_buttonVs2", flags);
			_buttonBorderVs1 = manageType.GetField("_buttonBorderVs1", flags);
			_buttonBorderVs2 = manageType.GetField("_buttonBorderVs2", flags);
			_gameplayVsUiTexture = manageType.GetField("_GameplayVsUITexture", flags);

			_fieldsOk = _onGameplay != null
				&& _onGameplay.FieldType == typeof(bool)
				&& _buttonVs1 != null
				&& _buttonVs2 != null
				&& _buttonBorderVs1 != null
				&& _buttonBorderVs2 != null
				&& _gameplayVsUiTexture != null;

			if (!_fieldsOk)
			{
				AdvancedKeybinds.Instance?.Logger.Warn(
					"ControlsManageOpenGameplayTab: UIManageControls OnGameplay/_buttonVs* fields missing; cannot force Gameplay tab.");
			}
		}
	}
}
