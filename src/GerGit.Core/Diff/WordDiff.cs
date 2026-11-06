namespace GerGit.Core.Diff;

/// <summary>Intra-line (word) highlighting: tokenizer + LCS with a deterministic walk-back.</summary>
public static class WordDiff
{
    // Limitation: the LCS table is O(n·m); above this many cells (e.g. a "Load Diff" of a minified file) no highlighting is done.
    const long MaxCells = 4_000_000;

    /// <summary>Highlighted ranges of the removed line and of the added line.</summary>
    public static (IReadOnlyList<TextRange> Old, IReadOnlyList<TextRange> New) Compute(string oldLine, string newLine)
    {
        var a = Tokenize(oldLine);
        var b = Tokenize(newLine);
        if ((long)a.Count * b.Count > MaxCells) return ([], []);

        // len[i, j] = LCS length of a[..i] and b[..j].
        var len = new int[a.Count + 1, b.Count + 1];
        for (int i = 1; i <= a.Count; i++)
            for (int j = 1; j <= b.Count; j++)
                len[i, j] = Same(oldLine, a[i - 1], newLine, b[j - 1]) ? len[i - 1, j - 1] + 1 : Math.Max(len[i - 1, j], len[i, j - 1]);

        var oldMatched = new bool[a.Count];
        var newMatched = new bool[b.Count];
        for (int i = a.Count, j = b.Count; i > 0 && j > 0;)
        {
            if (Same(oldLine, a[i - 1], newLine, b[j - 1])) { oldMatched[--i] = true; newMatched[--j] = true; }
            else if (len[i - 1, j] > len[i, j - 1]) i--;
            else j--; // tie: step back on the new side
        }
        return (Ranges(a, oldMatched), Ranges(b, newMatched));
    }

    static bool Same(string s, TextRange x, string t, TextRange y) =>
        x.Length == y.Length && string.CompareOrdinal(s, x.Start, t, y.Start, x.Length) == 0;

    internal static List<TextRange> Tokenize(string s)
    {
        var tokens = new List<TextRange>();
        for (int i = 0; i < s.Length;)
        {
            if (s[i] is ' ' or '\t' or '\r' or '\n') { i++; continue; }
            int start = i;
            if (IsWordChar(s[i])) while (i < s.Length && IsWordChar(s[i])) i++;
            else i++;
            tokens.Add(new TextRange(start, i - start));
        }
        return tokens;
    }

    static bool IsWordChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '_' || c > '\u007f';

    /// <summary>Unmatched tokens; runs of adjacent unmatched tokens merge into one range including the whitespace between them.</summary>
    static List<TextRange> Ranges(List<TextRange> tokens, bool[] matched)
    {
        var ranges = new List<TextRange>();
        for (int k = 0; k < tokens.Count; k++)
        {
            if (matched[k]) continue;
            int first = k;
            while (k + 1 < tokens.Count && !matched[k + 1]) k++;
            int start = tokens[first].Start;
            ranges.Add(new TextRange(start, tokens[k].Start + tokens[k].Length - start));
        }
        return ranges;
    }
}
