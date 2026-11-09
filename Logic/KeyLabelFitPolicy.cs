using System;
using System.Globalization;

namespace AdvancedKeybinds.Core
{
    /// <summary>Presentation only. The complete binding label is never changed in storage.</summary>
    internal static class KeyLabelFitPolicy
    {
        internal readonly struct FitResult
        {
            public string Text { get; }
            public float Scale { get; }
            public bool NeedsHover { get; }
            internal FitResult(string text, float scale, bool hover)
            { Text = text; Scale = scale; NeedsHover = hover; }
        }

        public static FitResult Fit(string text, float maxWidth, Func<string, float> measure,
            float normalScale = 1f, float minimumScale = .65f)
        {
            text ??= string.Empty;
            if (text.Length == 0)
                return new FitResult(text, normalScale, false);
            if (!float.IsFinite(maxWidth) || maxWidth <= 0 || measure == null
                || !float.IsFinite(normalScale) || normalScale <= 0
                || !float.IsFinite(minimumScale) || minimumScale <= 0 || minimumScale > normalScale)
                return new FitResult(string.Empty, 1f, true);
            float width = measure(text);
            if (!float.IsFinite(width) || width < 0)
                return new FitResult(string.Empty, normalScale, true);
            if (width * normalScale <= maxWidth)
                return new FitResult(text, normalScale, false);
            float scale = Math.Max(minimumScale, maxWidth / width);
            if (width * scale <= maxWidth)
                return new FitResult(text, scale, true);

            const string ellipsis = "…";
            float tailWidth = measure(ellipsis);
            if (!float.IsFinite(tailWidth) || tailWidth < 0 || tailWidth * scale > maxWidth)
                return new FitResult(string.Empty, scale, true);
            string prefix = string.Empty;
            var elements = StringInfo.GetTextElementEnumerator(text);
            while (elements.MoveNext())
            {
                string next = prefix + elements.GetTextElement() + ellipsis;
                float nextWidth = measure(next);
                if (!float.IsFinite(nextWidth) || nextWidth < 0 || nextWidth * scale > maxWidth)
                    break;
                prefix += elements.GetTextElement();
            }
            return new FitResult(prefix + ellipsis, scale, true);
        }
    }
}
