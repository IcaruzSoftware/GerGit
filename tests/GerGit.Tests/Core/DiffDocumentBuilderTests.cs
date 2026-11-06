using GerGit.Core;
using GerGit.Core.Diff;

namespace GerGit.Tests.Core;

public class DiffDocumentBuilderTests
{
    const string Text = """
        diff --git gergitsrc/a gergitdst/a
        old mode 100644
        new mode 100755
        --- gergitsrc/a
        +++ gergitdst/a
        @@ -10,6 +10,7 @@ class X
         c1
        -r1
        -r2
        +a1
        +a2
         c2
        +a3
         c3
        @@ -30,2 +31,2 @@
        -last
        \ No newline at end of file
        +last!
        \ No newline at end of file

        """;

    static string Render(DiffDocument doc) => string.Join("\n", doc.Lines.Select(l =>
        $"{l.Kind}|{l.OldNumber}|{l.NewNumber}|{l.Text}"));

    [Fact]
    public void UnifiedLayoutWithHeadersNumbersAndGroups()
    {
        var doc = DiffDocumentBuilder.Build(DiffParser.Parse(Text), entireFile: false);
        Assert.Equal("""
            Service||| changed file mode 100644 → 100755
            HunkHeader|||@@ -10,6 +10,7 @@ class X
            Context|10|10|c1
            Removed|11||r1
            Removed|12||r2
            Added||11|a1
            Added||12|a2
            Context|13|13|c2
            Added||14|a3
            Context|14|15|c3
            HunkHeader|||@@ -30,2 +31,2 @@
            Removed|30||last
            Service|||No newline at end of file
            Added||31|last!
            Service|||No newline at end of file
            """, Render(doc));
        Assert.Equal([new DocChangeGroup(3, 2, 2), new DocChangeGroup(8, 0, 1), new DocChangeGroup(11, 1, 1)], doc.Groups);
    }

    [Fact]
    public void EntireFileOmitsHunkHeaders()
    {
        var doc = DiffDocumentBuilder.Build(DiffParser.Parse(Text), entireFile: true);
        Assert.DoesNotContain(doc.Lines, l => l.Kind == DocLineKind.HunkHeader);
        Assert.Equal(DocLineKind.Service, doc.Lines[0].Kind);
        Assert.Equal(new DocChangeGroup(2, 2, 2), doc.Groups[0]);
    }

    [Fact]
    public void WordRangesOnlyForEqualCounts()
    {
        var doc = DiffDocumentBuilder.Build(DiffParser.Parse(Text), entireFile: false);
        Assert.Equal([new TextRange(0, 2)], doc.Lines[3].WordRanges);   // r1 vs a1
        Assert.Equal([new TextRange(0, 2)], doc.Lines[5].WordRanges);
        Assert.Empty(doc.Lines[8].WordRanges);                           // a3 alone: 0 deleted vs 1 added
        Assert.Equal([new TextRange(4, 1)], doc.Lines[13].WordRanges);   // "last!" vs "last": only "!"
        Assert.Empty(doc.Lines[11].WordRanges);
    }

    [Fact]
    public void NoNewlineAfterContextLineIsShown()
    {
        var doc = DiffDocumentBuilder.Build(DiffParser.Parse("""
            diff --git gergitsrc/a gergitdst/a
            @@ -1,2 +1,2 @@
            -x
            +y
             end
            \ No newline at end of file

            """), entireFile: false);
        Assert.Equal(DocLineKind.Service, doc.Lines[^1].Kind);
        Assert.Equal("end", doc.Lines[^2].Text);
    }

    [Fact]
    public void CrlfIsNotShown()
    {
        var doc = DiffDocumentBuilder.Build(DiffParser.Parse("diff --git gergitsrc/a gergitdst/a\n@@ -1 +1 @@\n-a\r\n+b\r\n"), false);
        Assert.Equal("a", doc.Lines[1].Text);
        Assert.Equal("b", doc.Lines[2].Text);
    }

    [Fact]
    public void LoneCrIsShownAsSymbolSoTheEditorKeepsOneLinePerDocLine()
    {
        var doc = DiffDocumentBuilder.Build(DiffParser.Parse("diff --git gergitsrc/a gergitdst/a\n@@ -1,2 +1,2 @@\n-x\ry\r\n+x\rz\r\n c\n"), false);
        Assert.Equal(["x␍y", "x␍z", "c"], doc.Lines.Skip(1).Select(l => l.Text));
        // The word diff ran on the real text (\r separates words) and its ranges fit the shown text.
        Assert.Equal([new TextRange(2, 1)], doc.Lines[1].WordRanges);
        Assert.Equal([new TextRange(2, 1)], doc.Lines[2].WordRanges);
    }
    [Fact]
    public void SideBySideAlignsGroupsWithEqualLineCountsNumbersAndHeadersInBothPanes()
    {
        var unified = DiffDocumentBuilder.Build(DiffParser.Parse(Text), entireFile: false);
        var (old, @new) = DiffDocumentBuilder.SideBySide(unified);
        Assert.Equal(old.Lines.Count, @new.Lines.Count);
        // Left = context + deleted lines, right = context + added lines; a group with fewer lines on one side is
        // padded with alignment lines so the next context line is level; headers (and the mode line) in both.
        Assert.Equal("""
            Service||| changed file mode 100644 → 100755
            HunkHeader|||@@ -10,6 +10,7 @@ class X
            Context|10|10|c1
            Removed|11||r1
            Removed|12||r2
            Context|13|13|c2
            Alignment|||
            Context|14|15|c3
            HunkHeader|||@@ -30,2 +31,2 @@
            Removed|30||last
            Service|||No newline at end of file
            """, Render(old));
        Assert.Equal("""
            Service||| changed file mode 100644 → 100755
            HunkHeader|||@@ -10,6 +10,7 @@ class X
            Context|10|10|c1
            Added||11|a1
            Added||12|a2
            Context|13|13|c2
            Added||14|a3
            Context|14|15|c3
            HunkHeader|||@@ -30,2 +31,2 @@
            Added||31|last!
            Service|||No newline at end of file
            """, Render(@new));
        // Groups by row, shared by both panes; word ranges stay with their lines (r1/a1 on one row).
        Assert.Equal([new DocChangeGroup(3, 2, 2), new DocChangeGroup(6, 0, 1), new DocChangeGroup(9, 1, 1)], @new.Groups);
        Assert.Same(old.Groups, @new.Groups);
        Assert.Equal([new TextRange(0, 2)], old.Lines[3].WordRanges);
        Assert.Equal([new TextRange(0, 2)], @new.Lines[3].WordRanges);
        Assert.Equal([new TextRange(4, 1)], @new.Lines[9].WordRanges);
        // Hunk lines for staging carry over (Hunk/Line of the parsed diff).
        Assert.Equal((0, 1), (old.Lines[3].Hunk, old.Lines[3].Line));
        Assert.Equal((1, 1), (@new.Lines[9].Hunk, @new.Lines[9].Line));
    }

    [Fact]
    public void SideBySidePadsTheShorterSideOfUnequalGroupsAndKeepsEntireFileWithoutHeaders()
    {
        var diff = DiffParser.Parse("""
            diff --git gergitsrc/a gergitdst/a
            @@ -1,5 +1,3 @@
             c1
            -r1
            -r2
            -r3
            +a1
             c2
            -r4
             c3

            """);
        var (old, @new) = DiffDocumentBuilder.SideBySide(DiffDocumentBuilder.Build(diff, entireFile: true));
        Assert.Equal(["c1", "r1", "r2", "r3", "c2", "r4", "c3"], old.Lines.Select(l => l.Text));
        Assert.Equal([DocLineKind.Context, DocLineKind.Added, DocLineKind.Alignment, DocLineKind.Alignment, DocLineKind.Context,
            DocLineKind.Alignment, DocLineKind.Context], @new.Lines.Select(l => l.Kind));
        Assert.Equal([1, 2, 3, 4, 5, 6, 7], old.Lines.Select(l => l.OldNumber ?? 0));
        Assert.Equal([1, 2, 0, 0, 3, 0, 4], @new.Lines.Select(l => l.NewNumber ?? 0)); // alignment lines have no number
        Assert.Empty(@new.Lines[1].WordRanges); // 3 deleted vs 1 added: no word highlighting
        Assert.Equal("c1\nr1\nr2\nr3\nc2\nr4\nc3", old.Text);
    }
}
