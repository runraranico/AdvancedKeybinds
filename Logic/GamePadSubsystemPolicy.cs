using System.Collections.Generic;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Runtime activation mode for the AK native GamePad subsystem.
	/// Persisted by name through ModConfig, so the enum member names are a save contract.
	/// </summary>
	public enum GamePadInputMode : byte
	{
		/// <summary>Follow Terraria's own gamepad usage state. Never polls FNA on its own.</summary>
		Auto = 0,

		/// <summary>Always poll FNA directly (needed when Steam Input is OFF and Terraria stays in KBM mode).</summary>
		Enabled = 1,

		/// <summary>Never poll. Saved <c>Pad:</c> tokens are kept but cannot fire.</summary>
		Disabled = 2
	}

	/// <summary>
	/// Pure activation policy for the GamePad subsystem — no FNA, no Terraria, no I/O.
	///
	/// Known limitation of <see cref="GamePadInputMode.Auto"/>: AK never calls
	/// <c>GamePad.GetState</c> speculatively, so while Terraria itself has not entered gamepad
	/// mode AK cannot discover an attached controller at all. That is intentional — background
	/// polling would put gamepad cost on keyboard/mouse-only players. Players whose setup keeps
	/// Terraria in keyboard mode (typically Steam Input OFF) must select
	/// <see cref="GamePadInputMode.Enabled"/>.
	/// </summary>
	public static class GamePadSubsystemPolicy
	{
		/// <summary>
		/// True when the subsystem may call into FNA this input tick.
		/// </summary>
		/// <param name="sessionAlreadyActivated">
		/// Auto latch from <see cref="NextSessionLatch"/>. Once Terraria has reported gamepad use,
		/// Auto stays active for the rest of the session so a Bluetooth/USB blip cannot make the
		/// subsystem flap on and off every frame.
		/// </param>
		public static bool IsActive(
			GamePadInputMode mode,
			bool terrariaUsingGamePad,
			bool sessionAlreadyActivated)
		{
			return mode switch {
				GamePadInputMode.Enabled => true,
				GamePadInputMode.Disabled => false,
				_ => terrariaUsingGamePad || sessionAlreadyActivated
			};
		}

		/// <summary>
		/// Per-tick activation including ComboPanel <paramref name="slotCaptureActive"/>.
		/// Capture opened with the mouse must still poll FNA so Pad:/stick edges can register.
		/// Disabled mode stays off.
		/// </summary>
		public static bool IsActiveForTick(
			GamePadInputMode mode,
			bool terrariaUsingGamePad,
			bool sessionAlreadyActivated,
			bool slotCaptureActive)
		{
			if (mode == GamePadInputMode.Disabled)
				return false;
			if (slotCaptureActive)
				return true;
			return IsActive(mode, terrariaUsingGamePad, sessionAlreadyActivated);
		}

		/// <summary>
		/// Next value of the Auto session latch. Disabled clears it so that switching
		/// Disabled → Auto starts from a clean evaluation instead of an old latch.
		/// </summary>
		public static bool NextSessionLatch(
			GamePadInputMode mode,
			bool terrariaUsingGamePad,
			bool sessionAlreadyActivated)
		{
			if (mode == GamePadInputMode.Disabled)
				return false;

			return sessionAlreadyActivated || terrariaUsingGamePad;
		}

		/// <summary>
		/// True when held / pressed / released state must be flushed because the subsystem just
		/// stopped polling. Without this a button that was down at the moment of deactivation
		/// would stay latched as held forever.
		/// </summary>
		public static bool RequiresStateFlush(bool wasActive, bool isActive)
			=> wasActive && !isActive;

		/// <summary>
		/// One-time migration for installs that predate this setting.
		/// Existing players who already bound <c>Pad:</c> tokens keep working without touching the
		/// new option; everyone else starts on Auto and pays no gamepad cost.
		/// </summary>
		public static GamePadInputMode ResolveMigratedMode(
			bool alreadyMigrated,
			GamePadInputMode savedMode,
			bool hasSavedPadTokens)
		{
			if (alreadyMigrated)
				return savedMode;

			return hasSavedPadTokens ? GamePadInputMode.Enabled : savedMode;
		}

		/// <summary>
		/// True when the migration should rewrite the saved setting (avoids a pointless config write).
		/// </summary>
		public static bool MigrationChangesSavedMode(
			bool alreadyMigrated,
			GamePadInputMode savedMode,
			bool hasSavedPadTokens)
			=> ResolveMigratedMode(alreadyMigrated, savedMode, hasSavedPadTokens) != savedMode;

		/// <summary>
		/// True when any saved binding key is a <c>Pad:</c> token. Used only by the one-time migration.
		/// </summary>
		public static bool ContainsPadToken(IEnumerable<string> savedTokens)
		{
			if (savedTokens == null)
				return false;

			foreach (string token in savedTokens)
			{
				if (GamePadToken.IsPadToken(token))
					return true;
			}

			return false;
		}
	}
}
