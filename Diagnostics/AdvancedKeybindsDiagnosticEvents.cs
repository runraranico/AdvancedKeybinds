using System;

namespace AdvancedKeybinds.Diagnostics
{
	/// <summary>Read-only diagnostic observation events for AdvancedKeybindsDiagnostics.</summary>
	public static class AdvancedKeybindsDiagnosticEvents
	{
		public static event Action<DiagnosticKeyMessageEventArgs> KeyMessageObserved;
		public static event Action<DiagnosticRawInputEventArgs> RawInputObserved;
		public static event Action<DiagnosticImeStateEventArgs> ImeStateObserved;
		public static event Action<DiagnosticCaptureEvaluationEventArgs> CaptureEvaluationCompleted;

		/// <summary>True when any diagnostic listener is attached (Live Input etc.).</summary>
		public static bool HasKeyMessageSubscribers => KeyMessageObserved != null;
		public static bool HasRawInputSubscribers => RawInputObserved != null;
		public static bool HasImeStateSubscribers => ImeStateObserved != null;
		public static bool HasCaptureEvaluationSubscribers => CaptureEvaluationCompleted != null;

		internal static void RaiseKeyMessage(DiagnosticKeyMessageEventArgs args)
		{
			if (args == null || KeyMessageObserved == null)
				return;

			InvokeSafe(KeyMessageObserved, args);
		}

		internal static void RaiseRawInput(DiagnosticRawInputEventArgs args)
		{
			if (args == null)
				return;

			InvokeSafe(RawInputObserved, args);
		}

		internal static void RaiseImeState(DiagnosticImeStateEventArgs args)
		{
			if (args == null)
				return;

			InvokeSafe(ImeStateObserved, args);
		}

		internal static void RaiseCaptureEvaluation(DiagnosticCaptureEvaluationEventArgs args)
		{
			if (args == null)
				return;

			InvokeSafe(CaptureEvaluationCompleted, args);
		}

		internal static void ClearAllSubscribers()
		{
			KeyMessageObserved = null;
			RawInputObserved = null;
			ImeStateObserved = null;
			CaptureEvaluationCompleted = null;
		}

		private static void InvokeSafe<T>(Action<T> handler, T args)
		{
			if (handler == null)
				return;

			Delegate[] list = handler.GetInvocationList();
			for (int i = 0; i < list.Length; i++)
			{
				try
				{
					((Action<T>)list[i]).Invoke(args);
				}
				catch
				{
					// Never let diagnostics subscribers break input handling.
				}
			}
		}
	}
}
