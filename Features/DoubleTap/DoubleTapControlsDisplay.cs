using AdvancedKeybinds.Binding;
using Microsoft.Xna.Framework;

namespace AdvancedKeybinds.Features.DoubleTap
{
    /// <summary>Controls-only display rules. No input, persistence, hooks or I/O.</summary>
    public static class DoubleTapControlsDisplay
    {
        public const string Prefix = "DT ";

        public static bool IsActive(ComboBinding combo)
            => combo != null && combo.DoubleTap && !combo.Hold
                && !combo.UseTerrariaKeyStatusOr && combo.FilledSlotCount == 1;

        public static string ApplyPrefix(string label, ComboBinding combo)
            => IsActive(combo) && !string.IsNullOrEmpty(label)
                ? Prefix + label : label ?? string.Empty;

        public static Color TextColor(ComboBinding combo, Color fallback)
            => IsActive(combo) ? new Color(255, 100, 0) : fallback;
    }
}
