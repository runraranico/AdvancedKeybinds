using System;
using AdvancedKeybinds.Core;
using ReLogic.Localization.IME;
using ReLogic.OS;
using Terraria.ModLoader;

namespace AdvancedKeybinds.Binding
{
	/// <summary>
	/// Runtime guard: while SlotCapture is active and Input Method Fixer is present with IME ON,
	/// call <see cref="IImeService.Disable"/> so SDL text input stops and letter keys can bind.
	/// Always restores prior Enable on capture end. Never modifies IMF.
	/// </summary>
	internal static class ImfCaptureImeGuard
	{
		private static bool _holding;
		private static bool _restoreEnable;

		/// <summary>True while this guard currently holds IME disabled for capture.</summary>
		internal static bool IsHolding => _holding;

		internal static void OnCaptureBegin()
		{
			if (_holding)
				return;

			try
			{
				bool configEnabled = true;
				try
				{
					var cfg = ModContent.GetInstance<AdvancedKeybindsConfig>();
					if (cfg != null)
						configEnabled = cfg.SuppressImeDuringKeyCapture;
				}
				catch
				{
					configEnabled = true;
				}

				bool imfPresent = ModLoader.HasMod(ImfCaptureImeGuardPolicy.InputMethodFixerModName);
				IImeService ime = null;
				try
				{
					ime = Platform.Get<IImeService>();
				}
				catch
				{
					ime = null;
				}

				bool imeEnabled = ime != null && ime.IsEnabled;
				if (!ImfCaptureImeGuardPolicy.ShouldSuppressImeDuringCapture(
					    configEnabled, imfPresent, imeEnabled))
					return;

				_restoreEnable = imeEnabled;
				ime.Disable();
				_holding = true;
			}
			catch
			{
				// Never break capture if IME/Platform fails.
				_holding = false;
				_restoreEnable = false;
			}
		}

		internal static void OnCaptureEnd()
		{
			if (!_holding)
				return;

			_holding = false;
			bool restore = _restoreEnable;
			_restoreEnable = false;

			if (!ImfCaptureImeGuardPolicy.ShouldRestoreImeAfterCapture(restore))
				return;

			try
			{
				IImeService ime = Platform.Get<IImeService>();
				if (ime != null && !ime.IsEnabled)
					ime.Enable();
			}
			catch
			{
				// Best-effort restore only.
			}
		}

		/// <summary>Test/diag helper: clear hold without touching IME (host teardown).</summary>
		internal static void ResetForTests()
		{
			_holding = false;
			_restoreEnable = false;
		}
	}
}
