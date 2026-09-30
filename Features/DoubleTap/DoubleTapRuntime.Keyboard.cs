using System;
using AdvancedKeybinds.Core;

namespace AdvancedKeybinds.Features.DoubleTap
{
	/// <summary>
	/// Windows keyboard DT via Session events → PressSequence (Window) / ObservedKey (LL5).
	/// No OS. Matcher and generation gated; initial held state uses the observed physical key.
	/// </summary>
	public sealed partial class DoubleTapRuntime
	{
		static readonly uint[] LlCanonicalVks = { 0x5Bu, 0x5Cu, 0x14u, 0x90u, 0x91u };

		DoubleTapPressSequence _keyboardPress;
		DoubleTapObservedKey _keyboardObserved;
		long _keyboardGeneration;
		bool _keyboardObservedSeeded;

		void ResetKeyboard()
		{
			_keyboardPress?.Reset();
			_keyboardObserved?.Reset(initiallyHeld: false);
			_keyboardGeneration = 0L;
			_keyboardObservedSeeded = false;
		}

		void EnsureKeyboardAdapters()
		{
			_keyboardPress ??= new DoubleTapPressSequence();
			_keyboardObserved ??= new DoubleTapObservedKey();
		}

		/// <summary>
		/// One Session event. Requires matching generation + primary KeyId.
		/// Window downs → PressSequence; LL5 down/up → ObservedKey.
		/// </summary>
		public bool ObserveKeyboard(
			DoubleTapObservedEvent ev,
			long generation,
			int seedHeldMask,
			string primaryToken,
			int windowMs,
			bool japanesePhysical,
			bool japaneseLayout)
		{
			if (generation <= 0L || ev.Generation != generation)
				return false;
			if (string.IsNullOrEmpty(primaryToken))
				return false;
			if (!DoubleTapPrimaryMatch.Matches(
				primaryToken, ev.KeyId, japanesePhysical, japaneseLayout))
				return false;

			EnsureKeyboardAdapters();
			if (_keyboardGeneration != generation)
			{
				_keyboardGeneration = generation;
				_keyboardPress.Reset();
				_keyboardObservedSeeded = false;
			}

			if (IsLowLevelFiveKeyId(ev.KeyId))
			{
				if (!_keyboardObservedSeeded)
				{
					_keyboardObserved.Reset(initiallyHeld: IsEventSeedHeld(ev.KeyId, seedHeldMask));
					_keyboardObservedSeeded = true;
				}
				return _keyboardObserved.Observe(
					ev.Sequence,
					ev.Update,
					ev.TimestampMs,
					windowMs,
					ev.Down,
					blocked: false);
			}

			// Window: genuine non-repeat downs only (Session already filtered repeats).
			if (!ev.Down)
				return false;

			return _keyboardPress.Press(
				ev.Sequence,
				ev.Update,
				ev.TimestampMs,
				windowMs);
		}

		static bool IsLowLevelFiveKeyId(int keyId)
		{
			if (keyId < 0 || (keyId & ~0x1FFFF) != 0)
				return false;
			uint vk = (uint)(keyId & 0xFF);
			for (int i = 0; i < LlCanonicalVks.Length; i++)
			{
				if (vk == LlCanonicalVks[i])
					return true;
			}

			return false;
		}

		static bool IsEventSeedHeld(int keyId, int seedHeldMask)
		{
			uint vk = (uint)(keyId & 0xFF);
			for (int bit = 0; bit < LlCanonicalVks.Length; bit++)
				if (vk == LlCanonicalVks[bit])
					return (seedHeldMask & (1 << bit)) != 0;
			return false;
		}

		internal static bool IsLowLevelFiveKeyIdPublic(int keyId)
			=> IsLowLevelFiveKeyId(keyId);
	}
}
