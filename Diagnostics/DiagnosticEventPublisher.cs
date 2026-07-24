using System;

namespace AdvancedKeybinds.Diagnostics
{
	/// <summary>Publishes read-only diagnostic events from internal hook sites.
	/// Allocates event args only when a subscriber exists (near-zero cost when idle).</summary>
	internal static class DiagnosticEventPublisher
	{
		private static readonly object DeviceLock = new();
		private static readonly System.Collections.Generic.Dictionary<long, string> DeviceAliases = new();

		public static void PublishKeyMessage(
			string messageName,
			uint virtualKey,
			ushort scanCode,
			bool extended,
			bool isKeyDown,
			long lParam)
		{
			if (!AdvancedKeybindsDiagnosticEvents.HasKeyMessageSubscribers)
				return;

			bool isRepeat = isKeyDown && ((lParam >> 30) & 1) != 0;
			AdvancedKeybindsDiagnosticEvents.RaiseKeyMessage(new DiagnosticKeyMessageEventArgs {
				MessageName = messageName,
				VirtualKey = virtualKey,
				ScanCode = scanCode,
				Extended = extended,
				IsKeyDown = isKeyDown,
				IsRepeat = isRepeat,
				DeviceIdentifier = "UNAVAILABLE(" + messageName + ")"
			});
		}

		public static void PublishHook(
			uint virtualKey,
			ushort scanCode,
			bool extended,
			bool isKeyDown,
			bool isRepeat,
			bool injected)
		{
			if (!AdvancedKeybindsDiagnosticEvents.HasKeyMessageSubscribers)
				return;

			AdvancedKeybindsDiagnosticEvents.RaiseKeyMessage(new DiagnosticKeyMessageEventArgs {
				MessageName = "WH_KEYBOARD_LL",
				VirtualKey = virtualKey,
				ScanCode = scanCode,
				Extended = extended,
				IsKeyDown = isKeyDown,
				IsRepeat = isRepeat,
				Injected = injected,
				DeviceIdentifier = "UNAVAILABLE(WH_KEYBOARD_LL)"
			});
		}

		public static void PublishRawInput(
			uint virtualKey,
			ushort makeCode,
			ushort rawFlags,
			bool isKeyUp,
			IntPtr deviceHandle)
		{
			if (!AdvancedKeybindsDiagnosticEvents.HasRawInputSubscribers)
				return;

			const ushort riKeyBreak = 0x0001;
			const ushort riKeyE0 = 0x0002;
			const ushort riKeyE1 = 0x0004;

			AdvancedKeybindsDiagnosticEvents.RaiseRawInput(new DiagnosticRawInputEventArgs {
				VirtualKey = virtualKey,
				MakeCode = makeCode,
				Extended = (rawFlags & riKeyE0) != 0,
				E0 = (rawFlags & riKeyE0) != 0,
				E1 = (rawFlags & riKeyE1) != 0,
				IsKeyUp = isKeyUp || (rawFlags & riKeyBreak) != 0,
				DeviceIdentifier = GetAnonymousDeviceAlias(deviceHandle)
			});
		}

		private static string GetAnonymousDeviceAlias(IntPtr handle)
		{
			if (handle == IntPtr.Zero)
				return "UNAVAILABLE(WM_INPUT hDevice=0)";

			lock (DeviceLock)
			{
				long key = handle.ToInt64();
				if (!DeviceAliases.TryGetValue(key, out string alias))
				{
					alias = "RawKeyboard#" + (DeviceAliases.Count + 1);
					DeviceAliases[key] = alias;
				}
				return alias;
			}
		}

		public static void PublishImeState(DiagnosticImeState state, string source)
		{
			if (!AdvancedKeybindsDiagnosticEvents.HasImeStateSubscribers)
				return;

			AdvancedKeybindsDiagnosticEvents.RaiseImeState(new DiagnosticImeStateEventArgs {
				State = state,
				Source = source
			});
		}

		public static void PublishCaptureEvaluation(
			DiagnosticKeyInput input,
			DiagnosticRegistrationResult result)
		{
			if (!AdvancedKeybindsDiagnosticEvents.HasCaptureEvaluationSubscribers)
				return;

			AdvancedKeybindsDiagnosticEvents.RaiseCaptureEvaluation(
				new DiagnosticCaptureEvaluationEventArgs {
					Input = input,
					Result = result
				});
		}
	}
}
