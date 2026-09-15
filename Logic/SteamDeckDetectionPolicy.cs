using System;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Pure decision rules for "is this a Steam Deck" and for the ComboPanel display-name gate.
	/// Collects nothing itself: the caller passes already-read evidence so the runtime side can
	/// resolve once and cache, and so the rules stay testable without Terraria.
	/// </summary>
	public static class SteamDeckDetectionPolicy
	{
		/// <summary>Environment variable the Steam client exports inside a Deck game session.</summary>
		public const string SteamDeckEnvName = "SteamDeck";

		/// <summary>
		/// Primary evidence is <c>PlayerInput.SteamDeckIsUsed</c>, which Terraria sets once from
		/// <c>SteamUtils.IsSteamRunningOnSteamDeck()</c>. The Linux env fallback only ever adds
		/// detections; a plain Linux box has neither signal and stays non-Deck.
		/// </summary>
		public static bool ResolveIsSteamDeck(
			bool gameSteamDeckIsUsed,
			bool detectionFailed,
			bool isLinux,
			string steamDeckEnvValue)
		{
			if (detectionFailed)
				return false;

			if (gameSteamDeckIsUsed)
				return true;

			if (!isLinux)
				return false;

			return IsEnvValueTrue(steamDeckEnvValue);
		}

		/// <summary>Only the exact "1" Steam writes counts; "0" and anything unexpected do not.</summary>
		public static bool IsEnvValueTrue(string value)
		{
			return !string.IsNullOrEmpty(value)
				&& value.Length == 1
				&& value[0] == '1';
		}

		public static bool IsDisplayNameChangeAllowed(
			bool panelOpen,
			bool openedByGamePad,
			bool isSteamDeck)
		{
			if (!panelOpen)
				return false;
			if (openedByGamePad)
				return false;
			if (isSteamDeck)
				return false;
			return true;
		}

		/// <summary>Null when the change is allowed. Diagnostic label only.</summary>
		public static string DisplayNameBlockReason(
			bool panelOpen,
			bool openedByGamePad,
			bool isSteamDeck)
		{
			if (!panelOpen)
				return "PanelClosed";
			if (openedByGamePad)
				return "OpenedByGamePad";
			if (isSteamDeck)
				return "SteamDeck";
			return null;
		}

		/// <summary>Stable one-line evidence string for the single startup log entry.</summary>
		public static string DescribeEvidence(
			bool gameSteamDeckIsUsed,
			bool detectionFailed,
			bool isLinux,
			string steamDeckEnvValue,
			bool resolvedIsSteamDeck)
		{
			return "steamDeckIsUsed=" + (gameSteamDeckIsUsed ? "1" : "0")
				+ " detectionFailed=" + (detectionFailed ? "1" : "0")
				+ " linux=" + (isLinux ? "1" : "0")
				+ " env" + SteamDeckEnvName + "=" + (steamDeckEnvValue ?? "-")
				+ " => " + (resolvedIsSteamDeck ? "SteamDeck" : "NotSteamDeck");
		}
	}
}
