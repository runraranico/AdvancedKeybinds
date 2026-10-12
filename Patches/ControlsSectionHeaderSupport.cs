using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using AdvancedKeybinds.Core;
using AdvancedKeybinds.Diagnostics;
using Microsoft.Xna.Framework;
using Terraria.GameContent.UI.Elements;
using Terraria.Localization;
using Terraria.ModLoader;
using Terraria.UI;
using Terraria.UI.Gamepad;

namespace AdvancedKeybinds.Patches
{
	/// <summary>
	/// Ensures section titles (マップ操作 / ホットバー / ゲームパッド…）participate in GamePad nav.
	/// Vanilla CreateBindingGroup never SetSnapPoint on the title; UI_plus replaces it with
	/// SectionCollapseHeaderPanel which also has no snap — so DPad skipped those headers.
	/// </summary>
	internal static class ControlsSectionHeaderSupport
	{
		private static FieldInfo _snapPointIndexField;
		private static FieldInfo _collapseKeyField;
		private static FieldInfo _labelField;
		private static FieldInfo _keybindField;
		private static FieldInfo _headerField;

		internal static void ApplyReflection(Type manageType)
		{
			_snapPointIndexField = manageType?.GetField(
				"SnapPointIndex",
				BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
			_keybindField = typeof(UIKeybindingListItem).GetField(
				"_keybind",
				BindingFlags.Instance | BindingFlags.NonPublic);
			if (_keybindField == null)
			{
				AdvancedKeybinds.Instance?.Logger.Warn(
					"ControlsSectionHeaderSupport: UIKeybindingListItem._keybind FieldInfo is null; bind collection disabled.");
			}
			Type headerType = typeof(UIState).Assembly.GetType("Terraria.ModLoader.Config.UI.HeaderElement");
			_headerField = headerType?.GetField("header", BindingFlags.Instance | BindingFlags.NonPublic)
				?? headerType?.GetField("_header", BindingFlags.Instance | BindingFlags.NonPublic);
		}

		internal static void ClearRuntimeState()
		{
			_collapseKeyField = null;
			_labelField = null;
		}

		/// <summary>
		/// Assign Wide SnapPoints to section title elements that lack them, using UIManageControls.SnapPointIndex.
		/// Call before vanilla SetupGamepadPoints so GetSnapPoints includes them.
		/// </summary>
		internal static void EnsureSectionHeaderSnapPoints(UIList uiList)
		{
			if (uiList == null)
				return;

			int found = 0;
			int snapped = 0;
			foreach (UIElement sectionRoot in EnumerateUiListItems(uiList))
			{
				UIElement header = FindSectionHeaderElement(sectionRoot);
				if (header == null)
					continue;
				found++;

				if (TryGetExistingSnap(header, out _))
				{
					snapped++;
					continue;
				}

				int id = AllocateSnapPointId();
				try
				{
					header.SetSnapPoint("Wide", id);
					snapped++;
				}
				catch (Exception ex)
				{
#if AK_DIAGNOSTICS
					if (global::AdvancedKeybinds.Binding.AkDiag.Enabled)
						AdvancedKeybinds.Instance?.Logger.Debug(
							"EnsureSectionHeaderSnapPoints: " + ex.GetType().Name + ": " + ex.Message);
#endif
					GamePadProbeSystem.LogControlSectionLinkFailed(
						"Controls.Section?.Header",
						"SetSnapPointFailed:" + ex.GetType().Name);
				}
			}

			if (found == 0)
			{
				GamePadProbeSystem.LogControlSectionLinkFailed(
					"Controls.*.Header",
					"NoSectionHeaderElementsInUiListItems");
			}
		}

		/// <summary>
		/// UIList stores rows in _items / _innerList — not as direct Children of the UIList.
		/// </summary>
		internal static IEnumerable<UIElement> EnumerateUiListItems(UIList uiList)
		{
			if (uiList == null)
				yield break;

			FieldInfo itemsField = typeof(UIList).GetField(
				"_items",
				BindingFlags.Instance | BindingFlags.NonPublic);
			if (itemsField?.GetValue(uiList) is System.Collections.IList items && items.Count > 0)
			{
				for (int i = 0; i < items.Count; i++)
				{
					if (items[i] is UIElement el)
						yield return el;
				}

				yield break;
			}

			FieldInfo innerField = typeof(UIList).GetField(
				"_innerList",
				BindingFlags.Instance | BindingFlags.NonPublic);
			if (innerField?.GetValue(uiList) is UIElement inner)
			{
				foreach (UIElement child in EnumerateChildren(inner))
					yield return child;
			}
		}

		internal static bool TryClassifySectionHeader(
			UIElement el,
			SnapPoint snap,
			UIList uiList,
			out string stableId,
			out string collapseKey,
			out bool? expanded)
		{
			stableId = null;
			collapseKey = null;
			expanded = null;
			if (el == null)
				return false;

			if (!IsSectionHeaderElement(el))
			{
				// HeaderElement (mod group titles) — already snapped by vanilla/TML.
				if (el.GetType().Name == "HeaderElement" && _headerField != null)
				{
					try
					{
						if (_headerField.GetValue(el) is string modName && !string.IsNullOrWhiteSpace(modName))
						{
							stableId = "Controls.Mod." + SanitizeToken(modName) + ".Header";
							collapseKey = "mod:" + modName;
							expanded = null;
							return true;
						}
					}
					catch
					{
						// fall through
					}
				}

				return false;
			}

			collapseKey = TryReadCollapseKey(el);
			UIElement sectionRoot = FindOuterListItem(el, uiList);
			stableId = ResolveSectionStableId(sectionRoot, collapseKey, el);
			expanded = TryReadExpanded(collapseKey, sectionRoot);
			return true;
		}

		internal static string ResolveSectionStableId(UIElement sectionRoot, string collapseKey, UIElement headerEl)
		{
			// Prefer collapseKey so expand/collapse (child bind set changes) does not rename the header.
			// Format: Controls.{Semantic}.{CollapseKey}.Header — unique per section, stable across rebuilds.
			string keyPart = !string.IsNullOrEmpty(collapseKey) ? SanitizeToken(collapseKey) : null;
			HashSet<string> binds = CollectBindIdsUnder(sectionRoot);
			string semantic = InferSectionSemantic(binds, collapseKey);

			if (!string.IsNullOrEmpty(keyPart))
			{
				if (string.IsNullOrEmpty(semantic))
					semantic = "Section";
				return "Controls." + semantic + "." + keyPart + ".Header";
			}

			if (!string.IsNullOrEmpty(semantic))
				return "Controls." + semantic + ".Header";

			string typeName = headerEl?.GetType().Name ?? "Header";
			return "Controls." + SanitizeToken(typeName) + ".Header";
		}

		/// <summary>
		/// Walks parents from a Controls keybind row to resolve section semantic
		/// (Movement / Inventory / Map / Hotbar / Gamepad / Mod / …) for Open-time gating.
		/// Reflection read-only; safe to call once per ComboPanel.Open.
		/// </summary>
		public static bool TryResolveRowSectionSemantic(UIElement row, out string semantic)
		{
			semantic = null;
			if (row == null)
				return false;

			for (UIElement cur = row; cur != null; cur = cur.Parent)
			{
				if (TryResolveSemanticFromElement(cur, out semantic))
					return true;

				// Section roots often wrap a header child; scan siblings under the same parent.
				UIElement parent = cur.Parent;
				if (parent == null)
					continue;

				foreach (UIElement sibling in EnumerateChildren(parent))
				{
					if (ReferenceEquals(sibling, cur))
						continue;
					if (TryResolveSemanticFromElement(sibling, out semantic))
						return true;
				}
			}

			return false;
		}

		private static bool TryResolveSemanticFromElement(UIElement el, out string semantic)
		{
			semantic = null;
			if (el == null)
				return false;

			if (el.GetType().Name == "HeaderElement" && _headerField != null)
			{
				try
				{
					if (_headerField.GetValue(el) is string modName && !string.IsNullOrWhiteSpace(modName))
					{
						semantic = "Mod";
						return true;
					}
				}
				catch
				{
					// fall through
				}
			}

			string collapseKey = TryReadCollapseKey(el);
			if (string.IsNullOrEmpty(collapseKey) && IsSectionHeaderElement(el))
				collapseKey = TryReadCollapseKey(el);

			if (string.IsNullOrEmpty(collapseKey) && !IsSectionHeaderElement(el))
			{
				UIElement nestedHeader = FindSectionHeaderElement(el);
				if (nestedHeader != null)
				{
					collapseKey = TryReadCollapseKey(nestedHeader);
					el = nestedHeader;
				}
			}

			if (string.IsNullOrEmpty(collapseKey) && !IsSectionHeaderElement(el))
				return false;

			HashSet<string> binds = CollectBindIdsUnder(el.Parent ?? el);
			semantic = InferSectionSemantic(binds, collapseKey);
			return !string.IsNullOrEmpty(semantic);
		}

		private static string InferSectionSemantic(HashSet<string> binds, string collapseKey)
		{
			if (!string.IsNullOrEmpty(collapseKey)
				&& collapseKey.StartsWith("mod:", StringComparison.OrdinalIgnoreCase))
				return "Mod";

			// Prefer collapseKey index for vanilla sections — child binds under a root are unstable
			// across expand/collapse and caused Movement.section.1 ↔ Map.section.1 mismatches.
			if (!string.IsNullOrEmpty(collapseKey)
				&& collapseKey.StartsWith("section:", StringComparison.Ordinal)
				&& int.TryParse(collapseKey.AsSpan("section:".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx))
			{
				return idx switch
				{
					0 => "Movement",
					1 => "Inventory",
					2 => "Map",
					3 => "Hotbar",
					4 => "Gamepad",
					5 => "Mod",
					_ => "Section" + idx.ToString(CultureInfo.InvariantCulture)
				};
			}

			if (binds != null)
			{
				if (binds.Contains("MapZoomIn")
					|| binds.Contains("MapZoomOut")
					|| binds.Contains("MapFull")
					|| binds.Contains("MapStyle"))
					return "Map";

				if (binds.Contains("Hotbar1")
					|| binds.Contains("HotbarMinus")
					|| binds.Contains("HotbarPlus")
					|| binds.Contains("Hotbar10"))
					return "Hotbar";

				if (binds.Contains("RadialHotbar")
					|| binds.Contains("RadialQuickbar"))
					return "Gamepad";

				if ((binds.Contains("sp14") || binds.Contains("sp15") || binds.Contains("sp16")
						|| binds.Contains("sp3") || binds.Contains("sp4"))
					&& !binds.Contains("RadialHotbar"))
					return "GamepadExtended";

				if (binds.Contains("ToggleCameraMode") && binds.Contains("Jump"))
					return "Movement";

				if (binds.Contains("Inventory") || binds.Contains("SmartSelect") || binds.Contains("Throw"))
					return "Inventory";
			}

			return null;
		}

		/// <summary>
		/// Thin left/right pair only when both are binding rows, same parent, similar Y, left-then-right.
		/// Section headers must never be treated as horizontal-only.
		/// </summary>
		internal static bool IsStructuralRightThinHalf(
			SnapPoint snap,
			UIElement el,
			ControlRowKind kind,
			SnapPoint prevSnap,
			UIElement prevEl,
			ControlRowKind prevKind)
		{
			if (kind == ControlRowKind.SectionHeader || prevKind == ControlRowKind.SectionHeader)
				return false;
			if (snap == null || prevSnap == null)
				return false;
			if (!string.Equals(snap.Name, "Thin", StringComparison.Ordinal)
				|| !string.Equals(prevSnap.Name, "Thin", StringComparison.Ordinal))
				return false;
			if (el == null || prevEl == null)
				return false;
			if (!IsBindingLikeRow(el) || !IsBindingLikeRow(prevEl))
				return false;
			if (el.Parent == null || !ReferenceEquals(el.Parent, prevEl.Parent))
				return false;

			try
			{
				CalculatedStyle a = prevEl.GetDimensions();
				CalculatedStyle b = el.GetDimensions();
				float yTol = Math.Max(4f, Math.Min(a.Height, b.Height) * 0.4f);
				if (Math.Abs(a.Y - b.Y) > yTol)
					return false;
				// Current snap should sit to the right of the previous.
				if (b.X + 1f < a.X + a.Width * 0.35f)
					return false;
			}
			catch
			{
				return false;
			}

			return true;
		}

		internal static bool IsSectionHeaderElement(UIElement el)
		{
			if (el == null)
				return false;

			string name = el.GetType().Name;
			if (name.IndexOf("SectionCollapse", StringComparison.OrdinalIgnoreCase) >= 0
				|| name.IndexOf("CollapseHeader", StringComparison.OrdinalIgnoreCase) >= 0)
				return true;

			Type type = el.GetType();
			if (type.IsGenericType
				&& type.GetGenericTypeDefinition() == typeof(UITextPanel<>)
				&& type.GenericTypeArguments.Length == 1
				&& type.GenericTypeArguments[0] == typeof(LocalizedText)
				&& el.Parent != null
				&& el.Parent.GetType().Name.IndexOf("UISortable", StringComparison.OrdinalIgnoreCase) >= 0)
				return true;

			return false;
		}

		private static UIElement FindSectionHeaderElement(UIElement sectionRoot)
		{
			if (sectionRoot == null)
				return null;

			foreach (UIElement child in EnumerateChildren(sectionRoot))
			{
				if (IsSectionHeaderElement(child))
					return child;
			}

			return null;
		}

		private static string TryReadCollapseKey(UIElement header)
		{
			if (header == null)
				return null;

			EnsureCollapseFields(header.GetType());
			try
			{
				if (_collapseKeyField?.GetValue(header) is string key && !string.IsNullOrEmpty(key))
					return key;
			}
			catch
			{
				// ignore
			}

			return null;
		}

		private static bool? TryReadExpanded(string collapseKey, UIElement sectionRoot)
		{
			if (string.IsNullOrEmpty(collapseKey))
			{
				// Heuristic: collapsed UI_plus roots are short (~40–48 UI units).
				try
				{
					if (sectionRoot != null)
					{
						CalculatedStyle d = sectionRoot.GetDimensions();
						if (d.Height > 1f && d.Height <= 48f)
							return false;
						if (d.Height > 48f)
							return true;
					}
				}
				catch
				{
					// ignore
				}

				return null;
			}

			// Prefer UI_plus CollapseStateStore via reflection without hard assembly reference.
			try
			{
				foreach (Mod mod in ModLoader.Mods)
				{
					if (mod?.Code == null)
						continue;
					Type store = mod.Code.GetType("UI_plus.CollapseStateStore");
					MethodInfo isCollapsed = store?.GetMethod(
						"IsCollapsed",
						BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
						null,
						new[] { typeof(string) },
						null);
					if (isCollapsed == null)
						continue;
					object result = isCollapsed.Invoke(null, new object[] { collapseKey });
					if (result is bool collapsed)
						return !collapsed;
				}
			}
			catch
			{
				// ignore
			}

			return null;
		}

		private static void EnsureCollapseFields(Type headerType)
		{
			if (headerType == null)
				return;
			if (_collapseKeyField != null && _collapseKeyField.DeclaringType == headerType)
				return;

			_collapseKeyField = headerType.GetField(
				"_collapseKey",
				BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
			_labelField = headerType.GetField(
				"_label",
				BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
		}

		private static HashSet<string> CollectBindIdsUnder(UIElement root)
		{
			var set = new HashSet<string>(StringComparer.Ordinal);
			if (root == null || _keybindField == null)
				return set;

			CollectBindIdsRecursive(root, set, depth: 0);
			return set;
		}

		private static void CollectBindIdsRecursive(UIElement el, HashSet<string> set, int depth)
		{
			if (el == null || depth > 24)
				return;

			if (el is UIKeybindingListItem && _keybindField != null)
			{
				if (KeybindRowReflectionSafety.TryReadStringField(_keybindField, el, out string bind))
					set.Add(bind);
			}

			foreach (UIElement child in EnumerateChildren(el))
				CollectBindIdsRecursive(child, set, depth + 1);
		}

		private static bool IsBindingLikeRow(UIElement el)
		{
			if (el == null)
				return false;
			if (el is UIKeybindingListItem)
				return true;
			string name = el.GetType().Name;
			if (name.IndexOf("Keybinding", StringComparison.OrdinalIgnoreCase) >= 0
				|| name.IndexOf("UIKeybinding", StringComparison.OrdinalIgnoreCase) >= 0)
				return true;
			// Wrapper under Mod/Section may own the Snap while the list item is nested.
			return ControlsGamepadAutoScrollPatches.HasNestedKeybindListItem(el);
		}

		private static UIElement FindOuterListItem(UIElement el, UIList outerList)
		{
			for (UIElement cur = el; cur != null; cur = cur.Parent)
			{
				if (cur.Parent != null && ReferenceEquals(cur.Parent, outerList))
					return cur;
			}

			return null;
		}

		private static bool TryGetExistingSnap(UIElement el, out SnapPoint snap)
		{
			snap = null;
			if (el == null)
				return false;
			try
			{
				if (el.GetSnapPoint(out SnapPoint sp) && sp != null)
				{
					snap = sp;
					return true;
				}
			}
			catch
			{
				// ignore
			}

			return false;
		}

		private static int AllocateSnapPointId()
		{
			if (_snapPointIndexField != null)
			{
				try
				{
					object cur = _snapPointIndexField.GetValue(null);
					if (cur is int id)
					{
						_snapPointIndexField.SetValue(null, id + 1);
						return id;
					}
				}
				catch
				{
					// fall through
				}
			}

			// Fallback: high unique-ish id (catalog sorts by Y, not Id).
			return unchecked(Environment.TickCount & 0x3FFFFFFF) + 500000;
		}

		private static IEnumerable<UIElement> EnumerateChildren(UIElement root)
		{
			if (root == null)
				yield break;

			IEnumerable<UIElement> list = null;
			try
			{
				list = root.Children;
			}
			catch
			{
				list = null;
			}

			if (list == null)
				yield break;

			foreach (UIElement child in list)
				yield return child;
		}

		private static string SanitizeToken(string raw)
		{
			if (string.IsNullOrWhiteSpace(raw))
				return "Unknown";

			raw = raw.Trim();
			var sb = new System.Text.StringBuilder(raw.Length);
			bool pendingDot = false;
			for (int i = 0; i < raw.Length; i++)
			{
				char c = raw[i];
				if (char.IsLetterOrDigit(c))
				{
					if (pendingDot && sb.Length > 0)
						sb.Append('.');
					pendingDot = false;
					sb.Append(c);
				}
				else if (c is ' ' or '_' or '-' or ':' or '/' or '\\' or '\'')
				{
					pendingDot = sb.Length > 0;
				}
			}

			return sb.Length > 0 ? sb.ToString() : "Unknown";
		}
	}
}
