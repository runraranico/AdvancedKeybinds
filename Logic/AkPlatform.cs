using System;
using Terraria.GameInput;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Process-wide platform facts resolved exactly once. Nothing here runs per frame: the Deck
	/// answer cannot change inside a session, so the first read caches it and every later read is
	/// a field compare.
	/// </summary>
	/// <remarks>
	/// Primary source is <c>Terraria.GameInput.PlayerInput.SteamDeckIsUsed</c>. Terraria assigns it
	/// during <c>Terraria.Social.Steam.PlatformSocialModule.Initialize()</c> from
	/// <c>SteamUtils.IsSteamRunningOnSteamDeck()</c>, long before mods load. Any failure falls back
	/// to "not a Deck", which keeps current behaviour rather than disabling features by accident.
	/// </remarks>
	internal static class AkPlatform
	{
		private const int Unresolved = 0;
		private const int SteamDeck = 1;
		private const int NotSteamDeck = 2;

		private static int _steamDeckState = Unresolved;
		private static string _steamDeckEvidence;

		internal static bool IsSteamDeck
		{
			get
			{
				if (_steamDeckState == Unresolved)
					Resolve();
				return _steamDeckState == SteamDeck;
			}
		}

		/// <summary>One-line evidence for the single startup log entry / real-device verification.</summary>
		internal static string SteamDeckEvidence
		{
			get
			{
				if (_steamDeckState == Unresolved)
					Resolve();
				return _steamDeckEvidence;
			}
		}

		internal static void ResetForReload()
		{
			_steamDeckState = Unresolved;
			_steamDeckEvidence = null;
		}

		private static void Resolve()
		{
			bool detectionFailed = false;

			bool gameFlag = false;
			try
			{
				gameFlag = PlayerInput.SteamDeckIsUsed;
			}
			catch (Exception)
			{
				detectionFailed = true;
			}

			bool isLinux = false;
			try
			{
				isLinux = OperatingSystem.IsLinux();
			}
			catch (Exception)
			{
				detectionFailed = true;
			}

			string env = null;
			if (!detectionFailed && !gameFlag && isLinux)
			{
				try
				{
					env = Environment.GetEnvironmentVariable(SteamDeckDetectionPolicy.SteamDeckEnvName);
				}
				catch (Exception)
				{
					env = null;
				}
			}

			bool deck = SteamDeckDetectionPolicy.ResolveIsSteamDeck(gameFlag, detectionFailed, isLinux, env);
			_steamDeckEvidence = SteamDeckDetectionPolicy.DescribeEvidence(
				gameFlag,
				detectionFailed,
				isLinux,
				env,
				deck);
			_steamDeckState = deck ? SteamDeck : NotSteamDeck;
		}
	}
}
