using System.Collections.Generic;
using Newtonsoft.Json;

namespace AdvancedKeybinds.Binding
{
	/// <summary>
	/// A single action's advanced binding, expressed as up to three ordered slots plus a hold flag.
	/// Slot1 = left, Slot2 = middle, Slot3 = right. Any slot may be empty. When all filled slots are
	/// pressed together the action fires. A one-slot combo may additionally be a hold.
	/// Note: a plain single-key TAP (any slot, no hold) is NOT stored here — it lives in
	/// vanilla's key configuration so the controls list shows the normal centered key label.
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
		/// True when this is a plain single-key tap (no hold / mouse / hook-managed key).
		/// Input and the controls-list label stay with vanilla; we may still remember slot layout.
		/// </summary>
		[JsonIgnore]
		public bool IsPlainSingleTap
		{
			get
			{
				if (FilledSlotCount != 1 || Hold)
					return false;
				string key = Keys[0];
				if (KeyToken.IsMouseToken(key))
					return false;
				if (BindingRestrictions.IsHookManagedTapToken(key))
					return false;
				return true;
			}
		}

		/// <summary>
		/// True when this combo must drive input through our engine (multi-key, hold, mouse, hook-managed).
		/// Plain single-key taps stay in vanilla so the list row keeps the normal centered label.
		/// </summary>
		public bool RequiresAdvancedStorage()
		{
			if (IsEmpty)
				return false;
			return !IsPlainSingleTap;
		}

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

		public ComboBinding Clone() => new() {
			Slot1 = Slot1,
			Slot2 = Slot2,
			Slot3 = Slot3,
			Hold = Hold,
			HoldOnRelease = HoldOnRelease,
			HoldMs = HoldMs
		};
	}
}
