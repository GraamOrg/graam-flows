namespace GraamFlows.Util.Collections;

/// <summary>
/// Name-keyed lookup helpers that re-throw LINQ's
/// "Sequence contains no matching element" with the missing key, the
/// available candidates, and a Levenshtein-nearest suggestion.
///
/// Background: graam-harmony issue #1164. Anywhere the engine does
/// <c>list.Single(x =&gt; x.Name == target)</c> on a missing target,
/// the resulting exception text is entirely generic and forensic to
/// debug. These helpers wrap that pattern so the caller sees what
/// was being looked up, where, and what was actually available.
/// </summary>
public static class LookupExtensions
{
    /// <summary>
    /// Equivalent of <see cref="Enumerable.Single{T}(IEnumerable{T}, Func{T, bool})"/>
    /// for a name-keyed predicate. Throws <see cref="InvalidOperationException"/>
    /// with a diagnostic message including the missing key, the
    /// available candidates, and the closest match.
    /// </summary>
    public static T SingleByName<T>(
        this IEnumerable<T> source,
        Func<T, string?> keySelector,
        string? targetName,
        string contextLabel,
        StringComparison comparison = StringComparison.Ordinal)
    {
        var items = source.ToList();
        var matches = items.Where(x => string.Equals(keySelector(x), targetName, comparison)).ToList();
        if (matches.Count == 1)
            return matches[0];
        if (matches.Count == 0)
            throw new InvalidOperationException(BuildNotFoundMessage(items, keySelector, targetName, contextLabel));
        throw new InvalidOperationException(
            $"{contextLabel}: '{targetName}' matched {matches.Count} candidates (expected exactly one).");
    }

    /// <summary>
    /// Equivalent of <see cref="Enumerable.First{T}(IEnumerable{T}, Func{T, bool})"/>
    /// for a name-keyed predicate. Throws <see cref="InvalidOperationException"/>
    /// with a diagnostic message including the missing key, the
    /// available candidates, and the closest match.
    /// </summary>
    public static T FirstByName<T>(
        this IEnumerable<T> source,
        Func<T, string?> keySelector,
        string? targetName,
        string contextLabel,
        StringComparison comparison = StringComparison.Ordinal)
    {
        var items = source.ToList();
        var match = items.FirstOrDefault(x => string.Equals(keySelector(x), targetName, comparison));
        if (match != null)
            return match;
        throw new InvalidOperationException(BuildNotFoundMessage(items, keySelector, targetName, contextLabel));
    }

    private static string BuildNotFoundMessage<T>(
        IReadOnlyList<T> items,
        Func<T, string?> keySelector,
        string? targetName,
        string contextLabel)
    {
        var available = items.Select(keySelector).Where(n => n != null).Cast<string>().ToList();
        var availableList = available.Count == 0 ? "(empty)" : string.Join(", ", available);
        var nearest = NearestMatch(available, targetName);
        var nearestSuffix = nearest != null ? $" Closest match: '{nearest}'." : string.Empty;
        var key = targetName ?? "(null)";
        return $"{contextLabel}: '{key}' not found. Available: [{availableList}].{nearestSuffix}";
    }

    private static string? NearestMatch(IReadOnlyList<string> candidates, string? target)
    {
        if (string.IsNullOrEmpty(target) || candidates.Count == 0)
            return null;

        string? best = null;
        var bestDist = int.MaxValue;
        foreach (var candidate in candidates)
        {
            var d = LevenshteinDistance(candidate, target);
            if (d < bestDist)
            {
                bestDist = d;
                best = candidate;
            }
        }

        // Only suggest when the edit distance is small relative to the
        // longer of the two strings — otherwise the "closest" is more
        // misleading than helpful.
        var maxLen = Math.Max(target.Length, best?.Length ?? 0);
        if (maxLen == 0 || bestDist > Math.Max(2, maxLen / 2))
            return null;
        return best;
    }

    private static int LevenshteinDistance(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(
                    Math.Min(curr[j - 1] + 1, prev[j] + 1),
                    prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }
        return prev[b.Length];
    }
}
