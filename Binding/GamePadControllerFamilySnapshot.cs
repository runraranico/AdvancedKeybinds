using System;
using AdvancedKeybinds.Core;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace AdvancedKeybinds.Binding
{
	/// <summary>
	/// Display-only cache of the active pad's controller family.
	/// Reads <see cref="FnaGamePadProvider.ActivePlayerIndex"/> + <c>GetGUIDEXT</c> / Capabilities;
	/// does not alter GetState / digital capture paths.
	/// </summary>
	internal static class GamePadControllerFamilySnapshot
	{
		private static bool _hasCache;
		private static PlayerIndex? _cachedIndex;
		private static string _cachedGuid;
		private static GamePadControllerFamily _cachedFamily = GamePadControllerFamily.Unknown;

		/// <summary>Current family for Controls glyph display. Safe when pad inactive.</summary>
		internal static GamePadControllerFamily Current
		{
			get
			{
				RefreshIfNeeded();
				return _cachedFamily;
			}
		}

		/// <summary>Test / disconnect hook.</summary>
		internal static void ClearCache()
		{
			_hasCache = false;
			_cachedIndex = null;
			_cachedGuid = null;
			_cachedFamily = GamePadControllerFamily.Unknown;
		}

		private static void RefreshIfNeeded()
		{
			PlayerIndex? active = FnaGamePadProvider.ActivePlayerIndex;
			if (!active.HasValue)
			{
				if (_hasCache && _cachedIndex == null && _cachedFamily == GamePadControllerFamily.Unknown)
					return;
				_hasCache = true;
				_cachedIndex = null;
				_cachedGuid = null;
				_cachedFamily = GamePadControllerFamily.Unknown;
				return;
			}

			PlayerIndex index = active.Value;
			string guid = null;
			bool touchPad = false;
			try
			{
				guid = GamePad.GetGUIDEXT(index);
			}
			catch
			{
				guid = null;
			}

			try
			{
				GamePadCapabilities caps = GamePad.GetCapabilities(index);
				touchPad = caps.IsConnected && caps.HasTouchPadEXT;
			}
			catch
			{
				touchPad = false;
			}

			if (_hasCache
				&& _cachedIndex == index
				&& string.Equals(_cachedGuid, guid, StringComparison.Ordinal))
				return;

			_hasCache = true;
			_cachedIndex = index;
			_cachedGuid = guid;
			_cachedFamily = GamePadControllerFamilyPolicy.Resolve(guid, touchPad);
		}
	}
}
