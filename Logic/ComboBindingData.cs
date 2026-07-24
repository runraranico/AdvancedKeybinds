using System.Collections.Generic;
using Newtonsoft.Json;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Pure combo binding DTO (same JSON shape as the mod's <c>ComboBinding</c>).
	/// </summary>
	public sealed class ComboBindingData
	{
		public const int MinHoldMs = 100;
		public const int MinTypedHoldMs = 10;
		public const int MaxHoldMs = 10000;
		public const int DefaultHoldMs = 400;

		public string Slot1;
		public string Slot2;
		public string Slot3;
		public string Slot1Display;
		public string Slot2Display;
		public string Slot3Display;
		public bool Hold;
		public bool HoldOnRelease;
		public int HoldMs;

		[JsonIgnore]
		public bool IsEmpty => string.IsNullOrEmpty(Slot1)
			&& string.IsNullOrEmpty(Slot2)
			&& string.IsNullOrEmpty(Slot3);

		[JsonIgnore]
		public int EffectiveHoldMs => HoldMs >= MinTypedHoldMs && HoldMs <= MaxHoldMs ? HoldMs : DefaultHoldMs;

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

		/// <summary>A案: Keys名単押しのみ true。SC/VK 等は Advanced 経路。</summary>
		[JsonIgnore]
		public bool IsPlainSingleTap
			=> PlainSingleTapPolicy.IsPlainSingleTap(Hold, Keys);

		[JsonIgnore]
		public bool RequiresAdvancedStorage
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

		public ComboBindingData Clone() => new() {
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
