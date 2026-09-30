using System;
using System.Reflection;
using Microsoft.Xna.Framework.Audio;
using Terraria.Audio;

namespace AdvancedKeybinds.Features.ListView
{
	/// <summary>
	/// Target of DrawInventory hover-sound callsite IL replacement.
	/// Invokes original SoundEngine.PlaySound only when AK ALL KEY is not claiming front ownership.
	/// Does not mutate hover-state fields, mouse, or volume.
	/// Delegate to assembly-internal PlaySoundInt6 is bound only via
	/// <see cref="TryInitializeOrigDelegate"/> (Apply-time), never via type initializer.
	/// </summary>
	internal static class BindingListViewHoverSoundRequestSuppressWrappers
	{
		private static Func<int, int, int, int, float, float, SoundEffectInstance> _playSoundInt6Orig;
		private static bool _initAttempted;
		private static string _initError;

		public static bool OrigDelegateReady => _playSoundInt6Orig != null;
		public static string InitError => _initError;

		/// <summary>
		/// Bind the installed assembly-internal PlaySound(int×4,float×2) once.
		/// Safe to call from Apply before any MonoMod hook install.
		/// </summary>
		public static bool TryInitializeOrigDelegate(out string error)
		{
			if (_initAttempted)
			{
				error = _initError;
				return _playSoundInt6Orig != null;
			}

			_initAttempted = true;
			MethodInfo mi = typeof(SoundEngine).GetMethod(
				"PlaySound",
				BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
				binder: null,
				types: new[] { typeof(int), typeof(int), typeof(int), typeof(int), typeof(float), typeof(float) },
				modifiers: null);
			return TryBindPlaySoundInt6(mi, out error);
		}

		/// <summary>Offline / Apply helper — bind a specific MethodInfo (null ⇒ fail, no throw).</summary>
		public static bool TryBindPlaySoundInt6(MethodInfo method, out string error)
		{
			_playSoundInt6Orig = null;
			_initError = null;
			if (method == null)
			{
				error = _initError = "PlaySound(int,int,int,int,float,float)->SoundEffectInstance missing";
				_initAttempted = true;
				return false;
			}

			try
			{
				_playSoundInt6Orig =
					(Func<int, int, int, int, float, float, SoundEffectInstance>)Delegate.CreateDelegate(
						typeof(Func<int, int, int, int, float, float, SoundEffectInstance>), method);
				error = null;
				_initAttempted = true;
				return true;
			}
			catch (Exception ex)
			{
				_playSoundInt6Orig = null;
				error = _initError = ex.GetType().Name + ": " + ex.Message;
				_initAttempted = true;
				return false;
			}
		}

		/// <summary>
		/// Callsite replacement target — stack/signature must match the installed PlaySoundInt6.
		/// If orig was never bound, returns null without throwing (inventory-safe).
		/// </summary>
		public static SoundEffectInstance ConditionalPlaySoundInt6(
			int type,
			int x,
			int y,
			int Style,
			float volumeScale,
			float pitchOffset)
		{
			if (BindingListViewHoverSoundRequestSuppressGate.ShouldSuppressRequest(
				BindingListViewSystem.ShouldBlockInventoryItemSlotLeftClick()))
				return null;

			Func<int, int, int, int, float, float, SoundEffectInstance> orig = _playSoundInt6Orig;
			if (orig == null)
				return null;
			return orig(type, x, y, Style, volumeScale, pitchOffset);
		}
	}
}
