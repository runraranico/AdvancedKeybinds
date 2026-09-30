using System.Collections.Generic;

namespace AdvancedKeybinds.Features.ListView
{
	/// <summary>
	/// Read-only display row for the binding list overlay. Does not own storage.
	/// </summary>
	internal sealed class BindingListEntry
	{
		public string ActionId { get; init; }
		public string KeyComboDisplay { get; init; }
		/// <summary>Key combo label without DT prefix; used for row sort so DT does not change order.</summary>
		public string KeyComboSortDisplay { get; init; }
		public string ActionDisplay { get; init; }
		public string ModDisplay { get; init; }
		/// <summary>Save tokens used for filter matching (slots + AdditionalOr).</summary>
		public IReadOnlyList<string> MatchTokens { get; init; }
		/// <summary>Primary sort / group token (first filled slot, else AdditionalOr).</summary>
		public string PrimaryToken { get; init; }
		public string PrimaryDisplay { get; init; }
		public string GroupSortKey { get; init; }
	}
}
