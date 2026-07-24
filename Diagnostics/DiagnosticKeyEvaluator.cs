using System;
using AdvancedKeybinds.Binding;
using AdvancedKeybinds.Core;
using Microsoft.Xna.Framework.Input;
using Terraria;

namespace AdvancedKeybinds.Diagnostics
{
	internal static class DiagnosticKeyEvaluator
	{
		public static DiagnosticRegistrationResult Evaluate(DiagnosticKeyInput input)
		{
			if (input == null)
			{
				return new DiagnosticRegistrationResult {
					EvaluationSucceeded = false,
					FailureStage = "InputValidation",
					FailureReason = "input-null",
					RelevantMethod = "DiagnosticKeyEvaluator.Evaluate"
				};
			}

			if (string.Equals(input.DeviceType, "Mouse", StringComparison.OrdinalIgnoreCase))
				return EvaluateMouse(input);

			return EvaluateKeyboard(input);
		}

		private static DiagnosticRegistrationResult EvaluateKeyboard(DiagnosticKeyInput input)
		{
			PhysicalKeyResolver.ResolveResult resolved = PhysicalKeyResolver.ResolveForCapture(
				input.VirtualKey,
				input.ScanCode,
				input.Extended,
				Main.keyState);

			var result = new DiagnosticRegistrationResult {
				EvaluationSucceeded = true,
				MonoGameKey = resolved.MonoGameKey?.ToString(),
				VirtualKey = resolved.Vk != 0 ? resolved.Vk : input.VirtualKey,
				ScanCode = resolved.Scan != 0 ? resolved.Scan : input.ScanCode,
				Extended = resolved.Extended || input.Extended,
				ResolvedTier = MapTier(resolved.Tier),
				CaptureCompatibility = SlotCapture.IsCapturing
					? "capture-session-active"
					: "idle"
			};

			if (!resolved.Success)
			{
				result.IsSupported = false;
				result.CanRegister = false;
				result.FailureStage = "PhysicalKeyResolver";
				result.FailureReason = resolved.FailReason ?? "no-internal-key";
				result.RelevantMethod = "PhysicalKeyResolver.ResolveForCapture";
				return result;
			}

			result.PhysicalKeyId = resolved.Token;
			result.IsSupported = true;

			// Match real capture path — DryRun must report the same token SlotCapture would save.
			string normalized = KeyToken.NormalizeCapturedToken(resolved.Token);
			if (!string.IsNullOrEmpty(normalized))
			{
				result.PhysicalKeyId = normalized;
				result.DisplayLabel = KeyToken.FormatDisplayForToken(normalized);
				result.DeveloperDetail = KeyToken.FormatDeveloperDetail(
					normalized,
					result.MonoGameKey,
					result.VirtualKey,
					result.ScanCode,
					result.Extended);
				return ApplySlotAndRestrictionRules(result, normalized);
			}

			result.DisplayLabel = KeyToken.FormatDisplayForToken(resolved.Token);
			result.DeveloperDetail = KeyToken.FormatDeveloperDetail(
				resolved.Token,
				result.MonoGameKey,
				result.VirtualKey,
				result.ScanCode,
				result.Extended);
			return ApplySlotAndRestrictionRules(result, resolved.Token);
		}

		private static DiagnosticRegistrationResult EvaluateMouse(DiagnosticKeyInput input)
		{
			string token = ResolveMouseToken(input);
			var result = new DiagnosticRegistrationResult {
				EvaluationSucceeded = true,
				PhysicalKeyId = token,
				DisplayLabel = KeyToken.FormatDisplayForToken(token),
				DeveloperDetail = KeyToken.FormatDeveloperDetail(token, input.MonoGameKey),
				ResolvedTier = "Unknown",
				MonoGameKey = input.MonoGameKey,
				CaptureCompatibility = SlotCapture.IsCapturing
					? "capture-session-active"
					: "idle"
			};

			if (string.IsNullOrEmpty(token))
			{
				result.IsSupported = false;
				result.CanRegister = false;
				result.FailureStage = "MouseToken";
				result.FailureReason = "unknown-mouse-button";
				result.RelevantMethod = "DiagnosticKeyEvaluator.EvaluateMouse";
				return result;
			}

			result.IsSupported = true;
			return ApplySlotAndRestrictionRules(result, token);
		}

		private static string ResolveMouseToken(DiagnosticKeyInput input)
		{
			if (!string.IsNullOrEmpty(input.MonoGameKey))
			{
				if (string.Equals(input.MonoGameKey, "XButton1", StringComparison.OrdinalIgnoreCase))
					return "Mouse4";
				if (string.Equals(input.MonoGameKey, "XButton2", StringComparison.OrdinalIgnoreCase))
					return "Mouse5";
			}

			return input.MonoGameKey switch {
				"Mouse4" => "Mouse4",
				"Mouse5" => "Mouse5",
				"XButton1" => "Mouse4",
				"XButton2" => "Mouse5",
				_ => null
			};
		}

		private static DiagnosticRegistrationResult ApplySlotAndRestrictionRules(
			DiagnosticRegistrationResult result,
			string token)
		{
			if (StableKeyToken.IsDisplayAlias(token))
			{
				result.CanRegister = false;
				result.RestrictionResult = "display-alias";
				result.FailureStage = "StableKeyToken";
				result.FailureReason = "display-alias-not-a-save-token";
				result.RelevantMethod = "StableKeyToken.IsDisplayAlias";
				result.RelevantRule = "DisplayAlias";
				return result;
			}

			var bindingData = new ComboBindingData();
			ComboSlotRules.SetSlotResult slotResult = ComboSlotRules.TrySetSlot(bindingData, 1, token);
			if (slotResult != ComboSlotRules.SetSlotResult.Accepted)
			{
				result.CanRegister = false;
				result.RestrictionResult = slotResult.ToString();
				result.FailureStage = "ComboSlotRules";
				result.FailureReason = slotResult.ToString();
				result.RelevantMethod = "ComboSlotRules.TrySetSlot";
				result.RelevantRule = slotResult.ToString();
				return result;
			}

			var comboBinding = new ComboBinding {
				Slot1 = bindingData.Slot1,
				Hold = false,
				HoldOnRelease = false
			};

			if (BindingRestrictions.IsF11Token(token))
			{
				result.CanRegister = false;
				result.RestrictionResult = "F11-reserved";
				result.FailureStage = "BindingRestrictions";
				result.FailureReason = "F11 is reserved";
				result.RelevantMethod = "BindingRestrictions.IsF11Token";
				result.RelevantRule = "IsF11Token";
				return result;
			}

			if (BindingRestrictions.RejectsBinding("_diagnostics_dry_run", comboBinding))
			{
				result.CanRegister = false;
				result.RestrictionResult = "toggle-lock-hold-rejected";
				result.FailureStage = "BindingRestrictions";
				result.FailureReason = "binding rejected by restrictions";
				result.RelevantMethod = "BindingRestrictions.RejectsBinding";
				result.RelevantRule = "RejectsBinding";
				return result;
			}

			if (BindingRestrictions.IsWindowsKeyToken(token))
			{
				result.RestrictionResult = "windows-key-hook-managed";
			}
			else if (BindingRestrictions.IsHookManagedTapToken(token))
			{
				result.RestrictionResult = "hook-managed-tap";
			}
			else
			{
				result.RestrictionResult = "ok";
			}

			result.CanRegister = true;
			result.FailureStage = null;
			result.FailureReason = null;
			return result;
		}

		private static string MapTier(string tier)
		{
			return tier switch {
				"Keys" => "MonoGame",
				"VK" => "VirtualKey",
				"SC" => "ScanCode",
				_ => "Unknown"
			};
		}
	}
}
