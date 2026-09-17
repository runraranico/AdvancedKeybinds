using System;
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
		/// <summary>Controller-only OR alternate (<c>Pad:*</c>). Not an AND combo slot.</summary>
		public string AlternateGamePadToken;
		/// <summary>Keyboard/Mouse additional OR key (allowlisted actions). Independent of AND.</summary>
		public string AdditionalOrToken;
		public bool Hold;
		public bool HoldOnRelease;
		public int HoldMs;

		/// <summary>
		/// Explicit empty two-key Terraria-OR record (Slot1/Slot2 both unset) must remain in JSON.
		/// Distinct from missing key (= never saved).
		/// </summary>
		public bool PersistAsEmptyRecord;

		/// <summary>
		/// Slot1/Slot2 sync to Terraria KeyStatus OR; BindingInputEngine must skip.
		/// </summary>
		public bool UseTerrariaKeyStatusOr;

		[JsonIgnore]
		public bool IsEmpty => string.IsNullOrEmpty(Slot1)
			&& string.IsNullOrEmpty(Slot2)
			&& string.IsNullOrEmpty(Slot3)
			&& string.IsNullOrEmpty(AdditionalOrToken);

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
			=> PlainSingleTapPolicy.IsPlainSingleTap(Hold, Keys)
				&& !KeyboardAdditionalOrPolicy.HasToken(AdditionalOrToken);

		[JsonIgnore]
		public bool RequiresAdvancedStorage
		{
			get
			{
				if (UseTerrariaKeyStatusOr || PersistAsEmptyRecord)
					return false;
				return PlainSingleTapPolicy.RequiresAdvancedStorage(Hold, Keys, IsEmpty)
					|| AlternateGamePadBindingPolicy.RequiresAdvancedOwnershipForAlternate(
						Hold, AlternateGamePadToken, FilledSlotCount)
					|| KeyboardAdditionalOrPolicy.RequiresAdvancedOwnership(AdditionalOrToken);
			}
		}

		[JsonIgnore]
		public bool HasAlternateGamePad
			=> AlternateGamePadBindingPolicy.HasAlternate(AlternateGamePadToken);

		[JsonIgnore]
		public bool HasAdditionalOr
			=> KeyboardAdditionalOrPolicy.HasToken(AdditionalOrToken);

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
			AlternateGamePadToken = AlternateGamePadToken,
			AdditionalOrToken = AdditionalOrToken,
			Hold = Hold,
			HoldOnRelease = HoldOnRelease,
			HoldMs = HoldMs,
			PersistAsEmptyRecord = PersistAsEmptyRecord,
			UseTerrariaKeyStatusOr = UseTerrariaKeyStatusOr
		};

		/// <summary>Value equality for edit-session snapshot / cancel checks (slots, displays, hold).</summary>
		public static bool ContentEquals(ComboBindingData a, ComboBindingData b)
		{
			if (ReferenceEquals(a, b))
				return true;
			if (a == null || b == null)
				return false;

			return string.Equals(a.Slot1, b.Slot1, StringComparison.Ordinal)
				&& string.Equals(a.Slot2, b.Slot2, StringComparison.Ordinal)
				&& string.Equals(a.Slot3, b.Slot3, StringComparison.Ordinal)
				&& string.Equals(a.Slot1Display, b.Slot1Display, StringComparison.Ordinal)
				&& string.Equals(a.Slot2Display, b.Slot2Display, StringComparison.Ordinal)
				&& string.Equals(a.Slot3Display, b.Slot3Display, StringComparison.Ordinal)
				&& string.Equals(a.AlternateGamePadToken, b.AlternateGamePadToken, StringComparison.Ordinal)
				&& string.Equals(a.AdditionalOrToken, b.AdditionalOrToken, StringComparison.Ordinal)
				&& a.Hold == b.Hold
				&& a.HoldOnRelease == b.HoldOnRelease
				&& a.HoldMs == b.HoldMs
				&& a.PersistAsEmptyRecord == b.PersistAsEmptyRecord
				&& a.UseTerrariaKeyStatusOr == b.UseTerrariaKeyStatusOr;
		}

		/// <summary>Compact slot join for asserts / diagnostics (empty → "-").</summary>
		public string FormatSlotsDiag()
			=> (Slot1 ?? "-") + "/" + (Slot2 ?? "-") + "/" + (Slot3 ?? "-");
	}
}
