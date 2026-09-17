using System;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Display-label → storage token remap for alias buckets only (never KeyStatus writes).
	/// LeftClick/RightClick/… must share the same bucket as Mouse1/Mouse2/….
	/// </summary>
	public static class DisplayAliasStorageKeyPolicy
	{
		public static string RemapDisplayLabelToStorageToken(string token)
		{
			if (string.IsNullOrWhiteSpace(token))
				return token;

			if (string.Equals(token, "LeftClick", StringComparison.OrdinalIgnoreCase))
				return "Mouse1";
			if (string.Equals(token, "RightClick", StringComparison.OrdinalIgnoreCase))
				return "Mouse2";
			if (string.Equals(token, "MiddleClick", StringComparison.OrdinalIgnoreCase))
				return "Mouse3";
			if (string.Equals(token, "MB4", StringComparison.OrdinalIgnoreCase))
				return "Mouse4";
			if (string.Equals(token, "MB5", StringComparison.OrdinalIgnoreCase))
				return "Mouse5";
			return token;
		}
	}
}
