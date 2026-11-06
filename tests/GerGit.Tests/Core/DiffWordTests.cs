using GerGit.Core;
using GerGit.Core.Diff;

namespace GerGit.Tests.Core;

public class DiffWordTests
{
    /// <summary>"old ranges|new ranges", each range's text, comma separated.</summary>
    static string Words(string a, string b)
    {
        var (o, n) = WordDiff.Compute(a, b);
        return string.Join(",", o.Select(r => a.Substring(r.Start, r.Length))) + "|" +
               string.Join(",", n.Select(r => b.Substring(r.Start, r.Length)));
    }

    [Fact]
    public void WorkedExample() => Assert.Equal("b|c", Words("foo(a, b)", "foo(a, c)"));

    [Fact]
    public void TieStepsBackOnNewSideFirst()
    {
        // LCS of [a b] and [b a] has length 1; the tie rule keeps "b" and marks "a" on both sides.
        var (o, n) = WordDiff.Compute("a b", "b a");
        Assert.Equal([new TextRange(0, 1)], o);
        Assert.Equal([new TextRange(2, 1)], n);
    }

    [Fact]
    public void AdjacentTokensMergeIncludingWhitespace() =>
        Assert.Equal("x  y|p q", Words("call(x  y)", "call(p q)"));

    [Fact]
    public void Tokenizer()
    {
        var s = "äb_1 +=\tq";
        Assert.Equal(["äb_1", "+", "=", "q"], WordDiff.Tokenize(s).Select(t => s.Substring(t.Start, t.Length)));
    }

    [Fact]
    public void IdenticalLinesHaveNoRanges() => Assert.Equal("|", Words("same", "same"));
}
