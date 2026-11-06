namespace GerGit.Core;

/// <summary>Plain-text search helpers (no UI types, safe on any thread).</summary>
public static class TextSearch
{
    /// <summary>Limitation: only the first 1000 occurrences are returned (a one-letter search in a long message); raise
    /// the cap if marking more is ever needed.</summary>
    public const int MaxMatches = 1000;

    /// <summary>The non-overlapping case-insensitive occurrences of <paramref name="search"/> in <paramref name="text"/>,
    /// in text order, at most <see cref="MaxMatches"/>.</summary>
    public static List<(int Start, int Length)> Matches(string text, string? search)
    {
        var found = new List<(int, int)>();
        if (string.IsNullOrEmpty(search)) return found;
        for (int i = text.IndexOf(search, StringComparison.OrdinalIgnoreCase); i >= 0 && found.Count < MaxMatches;
             i = text.IndexOf(search, i + search.Length, StringComparison.OrdinalIgnoreCase))
            found.Add((i, search.Length));
        return found;
    }
}
