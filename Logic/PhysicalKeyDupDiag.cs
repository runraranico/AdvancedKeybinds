using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Temporary duplicate-check diagnostics for physical-key identity investigation.
	/// Does not change acceptance logic — logging only.
	/// </summary>
	public static class PhysicalKeyDupDiag
	{
		/// <summary>Always-on emit for [DUP-CHECK] / [PHYS-RESOLVE] (wired from mod Load).</summary>
		public static Action<string> Emit;

		/// <summary>When true, <see cref="PhysicalKeyIdentity.TryResolve"/> emits [PHYS-RESOLVE] lines.</summary>
		public static bool ResolveTraceEnabled;

		private static readonly object Gate = new();
		private static readonly List<string> RecentBlocks = new();
		private static string _sessionLogPath;

		public static string SessionLogPath
		{
			get
			{
				lock (Gate)
					return _sessionLogPath;
			}
		}

		/// <param name="enableResolveTrace">
		/// When true, every <see cref="PhysicalKeyIdentity.TryResolve"/> appends [PHYS-RESOLVE].
		/// Must stay false during normal play — it causes per-frame file I/O and severe hitching.
		/// </param>
		public static void Configure(
			Action<string> emit,
			string sessionLogPath = null,
			bool enableResolveTrace = false)
		{
			Emit = emit;
			ResolveTraceEnabled = enableResolveTrace;
			lock (Gate)
			{
				if (!string.IsNullOrWhiteSpace(sessionLogPath))
					_sessionLogPath = sessionLogPath;
			}
		}

		public static void Reset()
		{
			Emit = null;
			ResolveTraceEnabled = false;
			lock (Gate)
			{
				RecentBlocks.Clear();
				_sessionLogPath = null;
			}
		}

		public static void Write(string message)
		{
			if (string.IsNullOrEmpty(message))
				return;

			bool hasSink;
			lock (Gate)
				hasSink = Emit != null || !string.IsNullOrEmpty(_sessionLogPath);

			if (!hasSink)
				return;

			try
			{
				Emit?.Invoke(message);
			}
			catch
			{
				// never throw from diag
			}

			try
			{
				lock (Gate)
				{
					RecentBlocks.Add(message);
					if (RecentBlocks.Count > 200)
						RecentBlocks.RemoveAt(0);

					if (string.IsNullOrEmpty(_sessionLogPath))
						return;

					string dir = Path.GetDirectoryName(_sessionLogPath);
					if (!string.IsNullOrEmpty(dir))
						Directory.CreateDirectory(dir);

					File.AppendAllText(_sessionLogPath, message.TrimEnd() + Environment.NewLine + Environment.NewLine, Encoding.UTF8);
				}
			}
			catch
			{
				// never throw from diag
			}
		}

		public static IReadOnlyList<string> SnapshotRecent()
		{
			lock (Gate)
				return RecentBlocks.ToArray();
		}

		public static void ClearRecent()
		{
			lock (Gate)
				RecentBlocks.Clear();
		}

		public sealed class TokenSnapshot
		{
			public string RawToken;
			public string Canonical;
			public bool ResolveSuccess;
			public string PhysicalId;
			public string ScanHex;
			public int E0;
			public int E1;
			public string VkHex;
			public string KeysName;
			public string Source;
			public string ResolveBranch;
		}

		public static TokenSnapshot Inspect(string token)
		{
			var snap = new TokenSnapshot {
				RawToken = token ?? string.Empty,
				Canonical = string.Empty,
				ResolveSuccess = false,
				PhysicalId = string.Empty,
				ScanHex = "-",
				E0 = -1,
				E1 = -1,
				VkHex = "-",
				KeysName = "-",
				Source = "unknown",
				ResolveBranch = "none"
			};

			if (string.IsNullOrWhiteSpace(token))
			{
				snap.Source = "empty";
				return snap;
			}

			string trimmed = token.Trim();
			snap.RawToken = trimmed;
			snap.Canonical = StableKeyToken.CanonicalizeOrNull(trimmed) ?? trimmed;
			ClassifyTokenForms(snap.Canonical, out snap.Source, out snap.KeysName, out snap.VkHex);

			bool prevTrace = ResolveTraceEnabled;
			ResolveTraceEnabled = false; // avoid nested [PHYS-RESOLVE] spam while building snapshot
			try
			{
				snap.ResolveSuccess = PhysicalKeyIdentity.TryResolveDetailed(
					trimmed,
					out string physicalId,
					out string branch,
					out ushort scan,
					out bool e0,
					out bool e1,
					out uint vk,
					out string keysName);
				snap.PhysicalId = physicalId ?? string.Empty;
				snap.ResolveBranch = branch ?? "none";
				if (snap.ResolveSuccess)
				{
					if (physicalId != null && physicalId.StartsWith(PhysicalKeyIdentity.Prefix + "SC=", StringComparison.OrdinalIgnoreCase))
					{
						snap.ScanHex = "0x" + (scan & 0xFF).ToString("X2", CultureInfo.InvariantCulture);
						snap.E0 = e0 ? 1 : 0;
						snap.E1 = e1 ? 1 : 0;
					}

					if (vk != 0)
						snap.VkHex = "0x" + vk.ToString("X2", CultureInfo.InvariantCulture);
					if (!string.IsNullOrEmpty(keysName) && keysName != "-")
						snap.KeysName = keysName;
				}
			}
			finally
			{
				ResolveTraceEnabled = prevTrace;
			}

			return snap;
		}

		public static void LogDupCheck(
			string caller,
			int targetSlot,
			int existingSlot,
			string existingToken,
			string incomingToken,
			bool tokensEqual,
			bool samePhysical,
			bool physicalIdEqual,
			string result,
			string reason)
		{
			TokenSnapshot existing = Inspect(existingToken);
			TokenSnapshot incoming = Inspect(incomingToken);

			var sb = new StringBuilder(1024);
			sb.AppendLine("[DUP-CHECK]");
			sb.AppendLine("caller=" + (caller ?? "-"));
			sb.AppendLine("targetSlot=" + targetSlot.ToString(CultureInfo.InvariantCulture));
			sb.AppendLine();
			AppendSide(sb, "existing", existingSlot, existing);
			sb.AppendLine();
			AppendSide(sb, "incoming", targetSlot, incoming);
			sb.AppendLine();
			sb.AppendLine("tokensEqual=" + (tokensEqual ? "true" : "false"));
			sb.AppendLine("samePhysical=" + (samePhysical ? "true" : "false"));
			sb.AppendLine("physicalIdEqual=" + (physicalIdEqual ? "true" : "false"));
			sb.AppendLine("result=" + (result ?? "-"));
			sb.AppendLine("reason=" + (reason ?? "-"));
			Write(sb.ToString());
		}

		public static void LogResolveTrace(
			string inputToken,
			string canonical,
			string branch,
			bool success,
			string physicalId)
		{
			if (!ResolveTraceEnabled)
				return;

			var sb = new StringBuilder(256);
			sb.AppendLine("[PHYS-RESOLVE]");
			sb.AppendLine("inputToken=" + (inputToken ?? "-"));
			sb.AppendLine("canonical=" + (canonical ?? "-"));
			sb.AppendLine("branch=" + (branch ?? "-"));
			sb.AppendLine("success=" + (success ? "true" : "false"));
			sb.AppendLine("physicalId=" + (physicalId ?? "-"));
			Write(sb.ToString());
		}

		private static void AppendSide(StringBuilder sb, string prefix, int slot, TokenSnapshot s)
		{
			if (prefix == "existing")
			{
				sb.AppendLine("existingSlot=" + slot.ToString(CultureInfo.InvariantCulture));
				sb.AppendLine("existingToken=" + NullDash(s.RawToken));
				sb.AppendLine("existingCanonical=" + NullDash(s.Canonical));
				sb.AppendLine("existingResolveSuccess=" + (s.ResolveSuccess ? "true" : "false"));
				sb.AppendLine("existingPhysicalId=" + NullDash(s.PhysicalId));
				sb.AppendLine("existingScan=" + NullDash(s.ScanHex));
				sb.AppendLine("existingE0=" + (s.E0 < 0 ? "-" : s.E0.ToString(CultureInfo.InvariantCulture)));
				sb.AppendLine("existingE1=" + (s.E1 < 0 ? "-" : s.E1.ToString(CultureInfo.InvariantCulture)));
				sb.AppendLine("existingVK=" + NullDash(s.VkHex));
				sb.AppendLine("existingKeysName=" + NullDash(s.KeysName));
				sb.AppendLine("existingSource=" + NullDash(s.Source));
				sb.AppendLine("existingResolveBranch=" + NullDash(s.ResolveBranch));
			}
			else
			{
				sb.AppendLine("incomingToken=" + NullDash(s.RawToken));
				sb.AppendLine("incomingCanonical=" + NullDash(s.Canonical));
				sb.AppendLine("incomingResolveSuccess=" + (s.ResolveSuccess ? "true" : "false"));
				sb.AppendLine("incomingPhysicalId=" + NullDash(s.PhysicalId));
				sb.AppendLine("incomingScan=" + NullDash(s.ScanHex));
				sb.AppendLine("incomingE0=" + (s.E0 < 0 ? "-" : s.E0.ToString(CultureInfo.InvariantCulture)));
				sb.AppendLine("incomingE1=" + (s.E1 < 0 ? "-" : s.E1.ToString(CultureInfo.InvariantCulture)));
				sb.AppendLine("incomingVK=" + NullDash(s.VkHex));
				sb.AppendLine("incomingKeysName=" + NullDash(s.KeysName));
				sb.AppendLine("incomingSource=" + NullDash(s.Source));
				sb.AppendLine("incomingResolveBranch=" + NullDash(s.ResolveBranch));
			}
		}

		private static string NullDash(string value)
			=> string.IsNullOrEmpty(value) ? "-" : value;

		private static void ClassifyTokenForms(string canonical, out string source, out string keysName, out string vkHex)
		{
			source = "unknown";
			keysName = "-";
			vkHex = "-";
			if (string.IsNullOrEmpty(canonical))
				return;

			if (PhysicalKeyToken.TryParseScan(canonical, out _, out _))
			{
				source = "ScanToken";
				return;
			}

			if (PhysicalKeyToken.TryParseVk(canonical, out uint vk))
			{
				source = "VkToken";
				vkHex = "0x" + vk.ToString("X2", CultureInfo.InvariantCulture);
				return;
			}

			if (StableKeyToken.IsMouseToken(canonical))
			{
				source = "MouseToken";
				return;
			}

			if (StableKeyToken.IsHankakuZenkakuToken(canonical))
			{
				source = "Special:HankakuZenkaku";
				keysName = StableKeyToken.HankakuZenkakuToken;
				return;
			}

			if (string.Equals(canonical, StableKeyToken.ScrollLockToken, StringComparison.OrdinalIgnoreCase))
			{
				source = "Special:ScrollLock";
				keysName = StableKeyToken.ScrollLockToken;
				return;
			}

			source = "KeysName";
			keysName = canonical;
			// Fill VK when the Keys name is known (for DUP-CHECK fields).
			if (PhysicalKeyIdentity.TryGetVkForKeysName(canonical, out uint knownVk))
				vkHex = "0x" + knownVk.ToString("X2", CultureInfo.InvariantCulture);
		}
	}
}
