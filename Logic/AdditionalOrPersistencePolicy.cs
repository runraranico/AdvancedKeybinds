#nullable disable
using System;
using System.Collections.Generic;

namespace AdvancedKeybinds.Core
{
    internal static class AdditionalOrPersistencePolicy
    {
        internal static bool Matches(IReadOnlyList<string> expected, IReadOnlyList<string> actual)
        {
            if (expected == null || actual == null)
            {
                return false;
            }

            if (expected.Count != actual.Count)
            {
                return false;
            }

            for (var i = 0; i < expected.Count; i++)
            {
                if (!string.Equals(expected[i], actual[i], StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        internal static bool CanAcquire(IReadOnlyList<string> beforeKeys, IReadOnlyList<string> expectedKeys, bool hasAdvancedMarkers)
        {
            if (hasAdvancedMarkers)
            {
                return false;
            }

            if (beforeKeys == null || beforeKeys.Count == 0)
            {
                return false;
            }

            if (!IsValidExpectedPair(expectedKeys))
            {
                return false;
            }

            if (!ContainsOrdinal(beforeKeys, expectedKeys[0]))
            {
                return false;
            }

            if (ContainsOrdinal(beforeKeys, expectedKeys[1]))
            {
                return false;
            }

            return true;
        }

        internal static bool CanContinue(
            IReadOnlyList<string> baseKeys,
            IReadOnlyList<string> priorExpected,
            IReadOnlyList<string> beforeKeys,
            IReadOnlyList<string> nextExpected,
            bool referencesMatch,
            bool hasAdvancedMarkers)
        {
            if (!referencesMatch || hasAdvancedMarkers)
            {
                return false;
            }

            if (baseKeys == null || baseKeys.Count == 0)
            {
                return false;
            }

            if (!IsValidExpectedPair(priorExpected) || !IsValidExpectedPair(nextExpected))
            {
                return false;
            }

            if (!Matches(priorExpected, beforeKeys))
            {
                return false;
            }

            if (!string.Equals(priorExpected[0], nextExpected[0], StringComparison.Ordinal))
            {
                return false;
            }

            if (!ContainsOrdinal(baseKeys, nextExpected[0]))
            {
                return false;
            }

            if (ContainsOrdinal(baseKeys, nextExpected[1]))
            {
                return false;
            }

            return true;
        }

        internal static bool ShouldProject(
            bool ownedOrPending,
            bool referencesMatch,
            bool hasAdvancedMarkers,
            IReadOnlyList<string> expectedKeys,
            IReadOnlyList<string> liveKeys,
            IReadOnlyList<string> savedKeys)
        {
            if (!ownedOrPending || !referencesMatch || hasAdvancedMarkers)
            {
                return false;
            }

            if (!IsValidExpectedPair(expectedKeys))
            {
                return false;
            }

            if (liveKeys == null || savedKeys == null)
            {
                return false;
            }

            return Matches(expectedKeys, liveKeys) && Matches(expectedKeys, savedKeys);
        }

        private static bool IsValidExpectedPair(IReadOnlyList<string> expectedKeys)
        {
            if (expectedKeys == null || expectedKeys.Count != 2)
            {
                return false;
            }

            if (string.IsNullOrEmpty(expectedKeys[0]) || string.IsNullOrEmpty(expectedKeys[1]))
            {
                return false;
            }

            if (string.Equals(expectedKeys[0], expectedKeys[1], StringComparison.Ordinal))
            {
                return false;
            }

            return true;
        }

        private static bool ContainsOrdinal(IReadOnlyList<string> keys, string value)
        {
            if (keys == null || value == null)
            {
                return false;
            }

            for (var i = 0; i < keys.Count; i++)
            {
                if (string.Equals(keys[i], value, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
