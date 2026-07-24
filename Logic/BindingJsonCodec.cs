using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Serialize/deserialize advanced bindings JSON without Terraria.IO.
	/// Unknown tokens stay in the DTO (never crash); callers may drop them for gameplay separately.
	/// </summary>
	public static class BindingJsonCodec
	{
		public enum LoadKind
		{
			Ok,
			Empty,
			OldListFormat,
			InvalidJson,
			NullDocument
		}

		public readonly struct LoadResult
		{
			public LoadKind Kind { get; init; }
			public Dictionary<string, ComboBindingData> Bindings { get; init; }
			public string Message { get; init; }
		}

		public static string Save(Dictionary<string, ComboBindingData> bindings)
		{
			bindings ??= new Dictionary<string, ComboBindingData>();
			return JsonConvert.SerializeObject(bindings, Formatting.Indented);
		}

		public static LoadResult Load(string json)
		{
			if (string.IsNullOrWhiteSpace(json))
				return new LoadResult { Kind = LoadKind.Empty, Bindings = new Dictionary<string, ComboBindingData>() };

			JToken root;
			try
			{
				root = JToken.Parse(json);
			}
			catch (JsonException ex)
			{
				return new LoadResult {
					Kind = LoadKind.InvalidJson,
					Bindings = new Dictionary<string, ComboBindingData>(),
					Message = ex.Message
				};
			}

			if (root == null || root.Type == JTokenType.Null)
			{
				return new LoadResult {
					Kind = LoadKind.NullDocument,
					Bindings = new Dictionary<string, ComboBindingData>()
				};
			}

			// Old format was a JSON array (list of bindings), not an object dictionary.
			if (root.Type == JTokenType.Array)
			{
				return new LoadResult {
					Kind = LoadKind.OldListFormat,
					Bindings = new Dictionary<string, ComboBindingData>(),
					Message = "list-based bindings format"
				};
			}

			if (root.Type != JTokenType.Object)
			{
				return new LoadResult {
					Kind = LoadKind.InvalidJson,
					Bindings = new Dictionary<string, ComboBindingData>(),
					Message = "expected object dictionary"
				};
			}

			try
			{
				var data = root.ToObject<Dictionary<string, ComboBindingData>>();
				var result = new Dictionary<string, ComboBindingData>(StringComparer.Ordinal);
				if (data == null)
				{
					return new LoadResult {
						Kind = LoadKind.NullDocument,
						Bindings = result
					};
				}

				foreach (KeyValuePair<string, ComboBindingData> pair in data)
				{
					if (string.IsNullOrEmpty(pair.Key) || pair.Value == null || pair.Value.IsEmpty)
						continue;

					ComboBindingData sanitized = SanitizeBinding(pair.Value);
					if (sanitized == null || sanitized.IsEmpty)
						continue;

					result[pair.Key] = sanitized;
				}

				return new LoadResult { Kind = LoadKind.Ok, Bindings = result };
			}
			catch (JsonException ex)
			{
				// Object that cannot map to ComboBindingData (e.g. wrong field shapes).
				return new LoadResult {
					Kind = LoadKind.OldListFormat,
					Bindings = new Dictionary<string, ComboBindingData>(),
					Message = ex.Message
				};
			}
		}

		/// <summary>
		/// Canonicalize known tokens; leave unknown strings as-is (safe for load, ignored at runtime).
		/// Never converts display aliases into slots.
		/// </summary>
		public static ComboBindingData SanitizeBinding(ComboBindingData source)
		{
			if (source == null)
				return null;

			var copy = source.Clone();
			for (int i = 1; i <= 3; i++)
			{
				string raw = copy.GetSlot(i);
				if (string.IsNullOrEmpty(raw))
				{
					copy.SetSlotDisplay(i, null);
					continue;
				}

				if (StableKeyToken.IsDisplayAlias(raw))
				{
					copy.SetSlot(i, null);
					copy.SetSlotDisplay(i, null);
					continue;
				}

				string canonical = StableKeyToken.CanonicalizeOrNull(raw);
				if (canonical != null)
					copy.SetSlot(i, canonical);
				// Unknown: keep raw text so we do not destroy user data / odd future tokens.

				string display = copy.GetSlotDisplay(i);
				if (!string.IsNullOrEmpty(display))
					copy.SetSlotDisplay(i, display);
			}

			if (copy.FilledSlotCount != 1)
			{
				copy.Hold = false;
				copy.HoldOnRelease = false;
			}

			return copy;
		}
	}
}
