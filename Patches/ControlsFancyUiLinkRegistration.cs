using Terraria.UI.Gamepad;

namespace AdvancedKeybinds.Patches
{
	/// <summary>
	/// Registers custom UILinkPoints onto the same FancyUI page / LinkMap as an existing point.
	/// Without SetPage + LinkMap, ChangePoint switches CurrentPage to 0 and focus collapses to P0.
	/// </summary>
	internal static class ControlsFancyUiLinkRegistration
	{
		internal static UILinkPoint EnsurePointOnSamePage(int pointId, UILinkPoint pageReference)
		{
			if (pageReference == null)
				return null;

			int pageId = pageReference.Page;
			if (!UILinkPointNavigator.Points.TryGetValue(pointId, out UILinkPoint point))
			{
				point = new UILinkPoint(pointId, enabled: true, left: -1, right: -1, up: -1, down: -1);
				UILinkPointNavigator.Points[pointId] = point;
			}

			point.Enabled = true;
			point.SetPage(pageId);

			if (UILinkPointNavigator.Pages.TryGetValue(pageId, out UILinkPage page))
				page.LinkMap[pointId] = point;

			// Keep Points and LinkMap as the same instance.
			UILinkPointNavigator.Points[pointId] = point;
			return point;
		}
	}
}
