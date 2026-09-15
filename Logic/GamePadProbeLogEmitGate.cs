using System;
using System.Collections.Generic;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Suppresses GamePadProbe spam that is not useful for physical input diagnosis.
	/// CLASSIFY defaults to fully off (not time-throttled).
	/// </summary>
	public sealed class GamePadProbeLogEmitGate
	{
		public enum ClassifyMode
		{
			/// <summary>Never emit AK_MOD_BINDING_ROW_CLASSIFY (default for input diagnosis).</summary>
			Off = 0,
			/// <summary>Emit each row key once per controls-structure fingerprint.</summary>
			OncePerStructure = 1
		}

		private readonly HashSet<string> _classifySeen = new(StringComparer.Ordinal);
		private string _classifyStructureFingerprint;
		private string _lastPollStateKey;
		private string _lastNotArmedKey;

		public ClassifyMode BindingRowClassifyMode { get; set; } = ClassifyMode.Off;

		public void Reset()
		{
			_classifySeen.Clear();
			_classifyStructureFingerprint = null;
			_lastPollStateKey = null;
			_lastNotArmedKey = null;
		}

		/// <summary>
		/// Returns true only when classify logging is enabled and this row has not yet been
		/// emitted for the current structure fingerprint.
		/// </summary>
		public bool ShouldEmitBindingRowClassify(string structureFingerprint, string rowKey)
		{
			if (BindingRowClassifyMode == ClassifyMode.Off)
				return false;

			string fingerprint = structureFingerprint ?? string.Empty;
			string key = rowKey ?? string.Empty;
			if (!string.Equals(_classifyStructureFingerprint, fingerprint, StringComparison.Ordinal))
			{
				_classifyStructureFingerprint = fingerprint;
				_classifySeen.Clear();
			}

			return _classifySeen.Add(key);
		}

		/// <summary>
		/// Emit only when poll state fields change (point/action/kind/arm/reason/pending/button).
		/// </summary>
		public bool ShouldEmitPanelRowConfirmPoll(string stateKey)
		{
			string key = stateKey ?? string.Empty;
			if (string.Equals(_lastPollStateKey, key, StringComparison.Ordinal))
				return false;
			_lastPollStateKey = key;
			return true;
		}

		/// <summary>Emit only when target+reason (or reason alone) changes.</summary>
		public bool ShouldEmitPanelRowConfirmPendingNotArmed(string targetAndReasonKey)
		{
			string key = targetAndReasonKey ?? string.Empty;
			if (string.Equals(_lastNotArmedKey, key, StringComparison.Ordinal))
				return false;
			_lastNotArmedKey = key;
			return true;
		}

		/// <summary>Important edge kinds must never be blocked by adjacent-line dedupe.</summary>
		public static bool IsImmediateFlushKind(string lineBody)
		{
			if (string.IsNullOrEmpty(lineBody))
				return false;
			return lineBody.StartsWith("PRESS", StringComparison.Ordinal)
				|| lineBody.StartsWith("RELEASE", StringComparison.Ordinal)
				|| lineBody.StartsWith("CONNECT", StringComparison.Ordinal)
				|| lineBody.StartsWith("DISCONNECT", StringComparison.Ordinal)
				|| lineBody.StartsWith("PROBE_ROTATE", StringComparison.Ordinal)
				|| lineBody.IndexOf("Exception", StringComparison.OrdinalIgnoreCase) >= 0
				|| lineBody.StartsWith("=== GamePadProbe END", StringComparison.Ordinal);
		}

		/// <summary>Adjacent-body dedupe must not swallow input / lifecycle edges.</summary>
		public static bool IsExemptFromAdjacentDedupe(string lineBody)
		{
			if (string.IsNullOrEmpty(lineBody))
				return false;
			return lineBody.StartsWith("PRESS", StringComparison.Ordinal)
				|| lineBody.StartsWith("RELEASE", StringComparison.Ordinal)
				|| lineBody.StartsWith("CONNECT", StringComparison.Ordinal)
				|| lineBody.StartsWith("DISCONNECT", StringComparison.Ordinal)
				|| lineBody.StartsWith("CAPTURE", StringComparison.Ordinal)
				|| lineBody.StartsWith("PROBE_ROTATE", StringComparison.Ordinal)
				|| lineBody.StartsWith("=== GamePadProbe", StringComparison.Ordinal);
		}
	}
}
