using AdvancedKeybinds.Core;
using Microsoft.Xna.Framework;

namespace AdvancedKeybinds.Binding
{
	/// <summary>
	/// One input-tick digital GamePad snapshot.
	///
	/// Instances are pooled and rewritten in place (see <see cref="FnaGamePadProvider"/>'s A/B pair),
	/// so nothing here may be treated as immutable across ticks. The digital state is carried as
	/// <see cref="GamePadDigitalBits"/> instead of a token set; canonical <c>Pad:</c> strings are
	/// produced only at the binding / capture / display boundaries.
	/// </summary>
	internal sealed class GamePadDigitalSnapshot
	{
		public bool IsConnected;
		public PlayerIndex? ActivePlayerIndex;

		/// <summary>Digital state for this tick, including trigger and stick-direction bits.</summary>
		public ulong Digital;

		/// <summary>Raw left trigger 0..1. Not a direction bind.</summary>
		public float LeftTriggerAnalog;

		/// <summary>Raw right trigger 0..1. Not a direction bind.</summary>
		public float RightTriggerAnalog;

		/// <summary>Left thumbstick X (−1..1). Also used for Pad:LS:* classification.</summary>
		public float LeftStickX;

		/// <summary>Left thumbstick Y (−1..1, FNA raw; +Y = up). Also used for Pad:LS:* classification.</summary>
		public float LeftStickY;

		/// <summary>Right thumbstick X (−1..1). Also used for Pad:RS:* classification.</summary>
		public float RightStickX;

		/// <summary>Right thumbstick Y (−1..1, FNA raw). Controls scroll + Pad:RS:* classification.</summary>
		public float RightStickY;

		/// <summary>Shared read-only stand-in. Never written to.</summary>
		public static GamePadDigitalSnapshot Disconnected { get; } = new();

		public bool IsDown(string canonicalToken)
			=> GamePadDigitalBits.TryGetBit(canonicalToken, out ulong bit)
				&& GamePadDigitalBits.IsDown(Digital, bit);

		public void Clear()
		{
			IsConnected = false;
			ActivePlayerIndex = null;
			Digital = GamePadDigitalBits.None;
			LeftTriggerAnalog = 0f;
			RightTriggerAnalog = 0f;
			LeftStickX = 0f;
			LeftStickY = 0f;
			RightStickX = 0f;
			RightStickY = 0f;
		}

		public void SetConnected(PlayerIndex index, in GamePadRawFrame raw, ulong digital)
		{
			IsConnected = true;
			ActivePlayerIndex = index;
			Digital = digital;
			LeftTriggerAnalog = raw.LeftTriggerAnalog;
			RightTriggerAnalog = raw.RightTriggerAnalog;
			LeftStickX = raw.LeftStickX;
			LeftStickY = raw.LeftStickY;
			RightStickX = raw.RightStickX;
			RightStickY = raw.RightStickY;
		}
	}
}
