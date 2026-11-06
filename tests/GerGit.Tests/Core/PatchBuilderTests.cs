using GerGit.Core;
using GerGit.Core.Diff;

namespace GerGit.Tests.Core;

public class PatchBuilderTests
{
    const string Header = "diff --git a/a.txt b/a.txt\n--- a/a.txt\n+++ b/a.txt\n";

    // Examples: one, -two, -three, +TWO, +THREE, four, five, six (line indices 0..7).
    static readonly FileDiff Example = DiffParser.Parse("""
        diff --git gergitsrc/a.txt gergitdst/a.txt
        index 1111111111111111111111111111111111111111..2222222222222222222222222222222222222222 100644
        --- gergitsrc/a.txt
        +++ gergitdst/a.txt
        @@ -1,6 +1,6 @@ some function
         one
        -two
        -three
        +TWO
        +THREE
         four
         five
         six

        """);

    static HunkLine[] Lines(params int[] idx) => idx.Select(i => new HunkLine(0, i)).ToArray();

    [Fact]
    public void Example_StageTwoAndTWO() =>
        Assert.Equal(Header + "@@ -1,6 +1,6 @@\n one\n-two\n+TWO\n three\n four\n five\n six\n",
            PatchBuilder.Build(Example, Lines(1, 3), PatchAction.Stage));

    [Fact]
    public void Example_StageThreeAndTHREE() =>
        Assert.Equal(Header + "@@ -1,6 +1,6 @@\n one\n two\n-three\n+THREE\n four\n five\n six\n",
            PatchBuilder.Build(Example, Lines(2, 4), PatchAction.Stage));

    [Fact]
    public void Example_UnstageTwoAndTWO() =>
        Assert.Equal(Header + "@@ -1,6 +1,6 @@\n one\n-two\n+TWO\n THREE\n four\n five\n six\n",
            PatchBuilder.Build(Example, Lines(1, 3), PatchAction.Unstage));

    [Fact]
    public void Example_DiscardTwoAndTWO() =>
        Assert.Equal(Header + "@@ -1,6 +1,6 @@\n one\n-TWO\n+two\n THREE\n four\n five\n six\n",
            PatchBuilder.Build(Example, Lines(1, 3), PatchAction.Discard));

    [Fact]
    public void HunkLengths_AreRecomputed()
    {
        // Stage only the deletion of "two": old = one two three four five six (6), new = one three four five six (5).
        Assert.Equal(Header + "@@ -1,6 +1,5 @@\n one\n-two\n three\n four\n five\n six\n",
            PatchBuilder.Build(Example, Lines(1), PatchAction.Stage));
        // Stage only +THREE: the added line is inserted after the unselected deleted lines (context).
        Assert.Equal(Header + "@@ -1,6 +1,7 @@\n one\n two\n three\n+THREE\n four\n five\n six\n",
            PatchBuilder.Build(Example, Lines(4), PatchAction.Stage));
    }

    [Fact]
    public void NewFile_StageTwoOfFive()
    {
        var diff = DiffParser.Parse("""
            diff --git gergitsrc/n.txt gergitdst/n.txt
            new file mode 100755
            index 0000000000000000000000000000000000000000..2222222222222222222222222222222222222222
            --- /dev/null
            +++ gergitdst/n.txt
            @@ -0,0 +1,5 @@
            +1
            +2
            +3
            +4
            +5

            """);
        Assert.Equal("diff --git a/n.txt b/n.txt\nnew file mode 100755\n--- /dev/null\n+++ b/n.txt\n@@ -0,0 +1,2 @@\n+2\n+4\n",
            PatchBuilder.Build(diff, Lines(1, 3), PatchAction.Stage));
    }

    [Fact]
    public void NoNewlineMarker_FollowsTheLineInADiscard()
    {
        var diff = DiffParser.Parse("""
            diff --git gergitsrc/a.txt gergitdst/a.txt
            --- gergitsrc/a.txt
            +++ gergitdst/a.txt
            @@ -1,2 +1,2 @@
             x
            -old
            \ No newline at end of file
            +new
            \ No newline at end of file

            """);
        // -old becomes +old and keeps its marker; +new becomes -new and keeps its marker.
        Assert.Equal(Header + "@@ -1,2 +1,2 @@\n x\n-new\n\\ No newline at end of file\n+old\n\\ No newline at end of file\n",
            PatchBuilder.Build(diff, Lines(1, 2), PatchAction.Discard));
        // Unselected +new becomes trailing context and stays the last line of both sides; +old is no longer last.
        Assert.Equal(Header + "@@ -1,2 +1,3 @@\n x\n+old\n new\n\\ No newline at end of file\n",
            PatchBuilder.Build(diff, Lines(1), PatchAction.Discard));
    }

    [Fact]
    public void NothingToApply_ReturnsNull()
    {
        Assert.Null(PatchBuilder.Build(Example, [], PatchAction.Stage));
        Assert.Null(PatchBuilder.Build(Example, [new HunkLine(0, 0), new HunkLine(0, 7)], PatchAction.Stage)); // context only
        var binary = DiffParser.Parse("diff --git gergitsrc/b.bin gergitdst/b.bin\nindex 1..2 100644\nBinary files gergitsrc/b.bin and gergitdst/b.bin differ\n");
        Assert.True(binary.IsBinary);
        Assert.Null(PatchBuilder.Build(binary, [], PatchAction.Stage));
    }

    [Fact]
    public void TwoHunks_OnlySelectedHunkIsWritten()
    {
        var diff = DiffParser.Parse("""
            diff --git gergitsrc/a.txt gergitdst/a.txt
            --- gergitsrc/a.txt
            +++ gergitdst/a.txt
            @@ -1,2 +1,2 @@ ctx1
            -a
            +A
             b
            @@ -10,2 +10,2 @@ ctx2
             y
            -z
            +Z

            """);
        Assert.Equal(Header + "@@ -10,2 +10,2 @@\n y\n-z\n+Z\n", PatchBuilder.Build(diff, [new HunkLine(1, 1), new HunkLine(1, 2)], PatchAction.Stage));
        Assert.Equal(Header + "@@ -1,2 +1,2 @@\n-a\n+A\n b\n@@ -10,2 +10,2 @@\n y\n-z\n+Z\n",
            PatchBuilder.Build(diff, [new HunkLine(0, 0), new HunkLine(0, 1), new HunkLine(1, 1), new HunkLine(1, 2)], PatchAction.Stage));
    }

    [Fact]
    public void SpecialPath_IsQuoted()
    {
        var diff = Example with { OldPath = "x y.txt", NewPath = "x y.txt" };
        var patch = PatchBuilder.Build(diff, Lines(1, 3), PatchAction.Stage)!;
        Assert.StartsWith("diff --git \"a/x y.txt\" \"b/x y.txt\"\n--- \"a/x y.txt\"\n+++ \"b/x y.txt\"\n", patch);
    }
}
