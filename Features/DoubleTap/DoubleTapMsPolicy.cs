using System;

namespace AdvancedKeybinds.Features.DoubleTap
{
	/// <summary>
	/// DT時間設定の範囲補正。UI／保存／入力へ未接続。I/Oなし。
	/// </summary>
	public static class DoubleTapMsPolicy
	{
		public const int DefaultMs = 250;
		public const int MinMs = 250;
		public const int MaxMs = 1000;

		public static int Clamp(int value)
		{
			return Math.Clamp(value, MinMs, MaxMs);
		}

		public static int Slider(int value)
		{
			return ((Clamp(value) + 5) / 10) * 10;
		}

		public static bool TryTyped(string text, out int value)
		{
			if (!int.TryParse(text, out int parsed))
			{
				value = default;
				return false;
			}

			value = Clamp(parsed);
			return true;
		}
	}
}
