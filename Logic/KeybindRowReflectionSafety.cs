using System;
using System.Reflection;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Safe instance-field reflection for keybind rows.
	/// Never call <see cref="FieldInfo.GetValue(object)"/> unless DeclaringType matches the target.
	/// </summary>
	public static class KeybindRowReflectionSafety
	{
		/// <summary>
		/// True when <paramref name="field"/> may be read from <paramref name="target"/>
		/// without ArgumentException (DeclaringType mismatch).
		/// </summary>
		public static bool CanReadInstanceField(FieldInfo field, object target)
		{
			if (field == null || target == null)
				return false;
			if (field.DeclaringType == null)
				return false;
			return field.DeclaringType.IsInstanceOfType(target);
		}

		/// <summary>
		/// Reads a string instance field only when the target is an instance of the declaring type.
		/// Does not catch or swallow reflection errors from a valid GetValue.
		/// </summary>
		public static bool TryReadStringField(FieldInfo field, object target, out string value)
		{
			value = null;
			if (!CanReadInstanceField(field, target))
				return false;

			object raw = field.GetValue(target);
			if (raw is not string s || string.IsNullOrEmpty(s))
				return false;

			value = s;
			return true;
		}

		/// <summary>
		/// Types that may open ComboPanel / expose UIKeybindingListItem._keybind semantics.
		/// SimpleListItem / HeaderElement / unknown → false.
		/// </summary>
		public static bool IsComboPanelEligibleKeybindTypeName(string typeName)
		{
			if (string.IsNullOrEmpty(typeName))
				return false;

			// Exact names only — do not use Contains("UIKeybindingListItem")
			// (would be fragile if a future type embeds that substring).
			return string.Equals(typeName, "UIKeybindingListItem", StringComparison.Ordinal)
				|| string.Equals(typeName, "UIKeybindingToggleListItem", StringComparison.Ordinal);
		}

		public static bool IsKnownNonKeybindRowTypeName(string typeName)
		{
			if (string.IsNullOrEmpty(typeName))
				return false;

			return string.Equals(typeName, "UIKeybindingSimpleListItem", StringComparison.Ordinal)
				|| string.Equals(typeName, "HeaderElement", StringComparison.Ordinal)
				|| string.Equals(typeName, "UIHeaderElement", StringComparison.Ordinal)
				|| typeName.IndexOf("SectionCollapse", StringComparison.OrdinalIgnoreCase) >= 0
				|| typeName.IndexOf("CollapseHeader", StringComparison.OrdinalIgnoreCase) >= 0
				|| typeName.IndexOf("DefaultReset", StringComparison.OrdinalIgnoreCase) >= 0;
		}

		/// <summary>
		/// Whether ComboPanel may be opened for this runtime object type name.
		/// </summary>
		public static bool ShouldAttemptKeybindFieldRead(string typeName)
		{
			if (IsKnownNonKeybindRowTypeName(typeName))
				return false;
			return IsComboPanelEligibleKeybindTypeName(typeName);
		}
	}
}
