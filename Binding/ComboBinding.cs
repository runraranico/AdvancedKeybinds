using System.Collections.Generic;
using AdvancedKeybinds.Core;
using Newtonsoft.Json;

namespace AdvancedKeybinds.Binding
{
	/// <summary>
	/// A single action's advanced binding, expressed as up to three ordered slots plus a hold flag.
	/// Slot1 = left, Slot2 = middle, Slot3 = right. Any slot may be empty. When all filled slots are
	/// pressed together the action fires. A one-slot combo may additionally be a hold.
	/// Keys-name plain single-key taps are also stored for slot placement; gameplay for those stays
	/// with vanilla. Measured SC:/VK: singles use Advanced storage and BindingInputEngine.
	/// </summary>
	public sealed class ComboBinding
	{
		/// <summary>Hold-duration bounds, slider cap, and default shared by the engine and combo panel UI.</summary>
		public const int MinHoldMs = 100;
		/// <summary>Lower bound when the hold duration is typed in the number box (slider still starts at <see cref="MinHoldMs"/>).</summary>
		public const int MinTypedHoldMs = 10;
		public const int MaxHoldMs = 10000;
		public const int DefaultHoldMs = 400;
		/// <summary>Upper limit when adjusting with the slider (typed input may go up to <see cref="MaxHoldMs"/>).</summary>
		public const int SliderMaxHoldMs = 1000;

		public string Slot1;
		public string Slot2;
		public string Slot3;

		/// <summary>Optional display override for <see cref="Slot1"/> (empty = use FormatToken).</summary>
		public string Slot1Display;
		/// <summary>Optional display override for <see cref="Slot2"/> (empty = use FormatToken).</summary>
		public string Slot2Display;
		/// <summary>Optional display override for <see cref="Slot3"/> (empty = use FormatToken).</summary>
		public string Slot3Display;

		public bool Hold;

		/// <summary>
		/// When <see cref="Hold"/> is true: false = fire after duration while still held (default),
		/// true = fire on key release once duration was reached.
		/// </summary>
		public bool HoldOnRelease;

		/// <summary>
		/// Per-binding hold duration in milliseconds (only meaningful when this is a single-key Hold).
		/// Stored in bindings.json. A value outside [MinTypedHoldMs, MaxHoldMs] (e.g. 0 from an old file that
		/// predates this field) falls back to <see cref="DefaultHoldMs"/> via <see cref="EffectiveHoldMs"/>.
		/// </summary>
		public int HoldMs;

		[JsonIgnore]
		public bool IsEmpty => string.IsNullOrEmpty(Slot1)
			&& string.IsNullOrEmpty(Slot2)
			&& string.IsNullOrEmpty(Slot3);

		/// <summary>The hold duration to actually use, clamped to the valid range with a default fallback.</summary>
		[JsonIgnore]
		public int EffectiveHoldMs => HoldMs >= MinTypedHoldMs && HoldMs <= MaxHoldMs ? HoldMs : DefaultHoldMs;

		/// <summary>Ordered, non-empty slot keys (left -> middle -> right).</summary>
		[JsonIgnore]
		public List<string> Keys
		{
			get
			{
				List<string> keys = new(3);
				if (!string.IsNullOrEmpty(Slot1))
					keys.Add(Slot1);
				if (!string.IsNullOrEmpty(Slot2))
					keys.Add(Slot2);
				if (!string.IsNullOrEmpty(Slot3))
					keys.Add(Slot3);
				return keys;
			}
		}

		[JsonIgnore]
		public int FilledSlotCount => Keys.Count;

		/// <summary>
		/// True when this is a vanilla-ownable plain single-key tap (Keys name, no hold / mouse /
		/// hook-managed / non-vanilla measured token). See <see cref="PlainSingleTapPolicy"/>.
		/// </summary>
		[JsonIgnore]
		public bool IsPlainSingleTap
			=> PlainSingleTapPolicy.IsPlainSingleTap(Hold, Keys);

		/// <summary>
		/// True when this combo must drive input through our engine (multi-key, hold, mouse,
		/// hook-managed, or SC/VK/measured tokens). Keys-name single taps stay in vanilla.
		/// </summary>
		public bool RequiresAdvancedStorage()
			=> PlainSingleTapPolicy.RequiresAdvancedStorage(Hold, Keys, IsEmpty);

		public string GetSlot(int index) => index switch {
			1 => Slot1,
			2 => Slot2,
			3 => Slot3,
			_ => null
		};

		public void SetSlot(int index, string value)
		{
			value = string.IsNullOrEmpty(value) ? null : value;
			switch (index)
			{
				case 1: Slot1 = value; break;
				case 2: Slot2 = value; break;
				case 3: Slot3 = value; break;
			}
		}

		public string GetSlotDisplay(int index) => index switch {
			1 => Slot1Display,
			2 => Slot2Display,
			3 => Slot3Display,
			_ => null
		};

		public void SetSlotDisplay(int index, string value)
		{
			value = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
			switch (index)
			{
				case 1: Slot1Display = value; break;
				case 2: Slot2Display = value; break;
				case 3: Slot3Display = value; break;
			}
		}

		/// <summary>Display label for a filled slot: global key alias, per-slot override, else <see cref="KeyToken.FormatToken"/>.</summary>
		public string FormatSlotLabel(int index)
		{
			string token = GetSlot(index);
			if (string.IsNullOrEmpty(token))
				return null;

			return KeyToken.FormatDisplayForToken(token, GetSlotDisplay(index));
		}

		/// <summary>Joined combo label using per-slot display overrides where set.</summary>
		public string FormatJoinedLabel(bool compact)
		{
			List<string> parts = new(3);
			for (int i = 1; i <= 3; i++)
			{
				string label = FormatSlotLabel(i);
				if (!string.IsNullOrEmpty(label))
					parts.Add(label);
			}

			if (parts.Count == 0)
				return string.Empty;

			return string.Join(compact ? "+" : " + ", parts);
		}

		public ComboBinding Clone() => new() {
			Slot1 = Slot1,
			Slot2 = Slot2,
			Slot3 = Slot3,
			Slot1Display = Slot1Display,
			Slot2Display = Slot2Display,
			Slot3Display = Slot3Display,
			Hold = Hold,
			HoldOnRelease = HoldOnRelease,
			HoldMs = HoldMs
		};
	}
}
