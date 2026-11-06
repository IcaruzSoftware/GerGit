using GerGit.Core;
using GerGit.Core.Diff;

namespace GerGit.Tests.Core;

public class DiffParserTests
{
    const string Z = "0000000000000000000000000000000000000000";
    const string A = "1111111111111111111111111111111111111111";
    const string B = "2222222222222222222222222222222222222222";

    [Fact]
    public void AddedFile()
    {
        var d = DiffParser.Parse($"""
            diff --git gergitsrc/new.txt gergitdst/new.txt
            new file mode 100644
            index {Z}..{B}
            --- /dev/null
            +++ gergitdst/new.txt
            @@ -0,0 +1,2 @@
            +one
            +two

            """);
        Assert.Null(d.OldPath);
        Assert.Equal("new.txt", d.NewPath);
        Assert.Null(d.OldMode);
        Assert.Equal(100644, d.NewMode);
        Assert.Equal((Z, B), (d.OldBlob, d.NewBlob));
        var h = Assert.Single(d.Hunks);
        Assert.Equal((0, 0, 1, 2), (h.OldStart, h.OldLength, h.NewStart, h.NewLength));
        Assert.Equal([new DiffLine(DiffLineKind.Added, "one", null, 1), new DiffLine(DiffLineKind.Added, "two", null, 2)], h.Lines);
        Assert.Equal(6, d.TotalLength);
        Assert.False(d.IsEmpty);
    }

    [Fact]
    public void DeletedFile_HunkWithoutLength()
    {
        var d = DiffParser.Parse($"""
            diff --git gergitsrc/old.txt gergitdst/old.txt
            deleted file mode 100755
            index {A}..{Z}
            --- gergitsrc/old.txt
            +++ /dev/null
            @@ -1 +0,0 @@
            -gone

            """);
        Assert.Equal("old.txt", d.OldPath);
        Assert.Null(d.NewPath);
        Assert.Equal(100755, d.OldMode);
        Assert.Null(d.NewMode);
        var h = Assert.Single(d.Hunks);
        Assert.Equal((1, 1, 0, 0), (h.OldStart, h.OldLength, h.NewStart, h.NewLength));
        Assert.Equal(new DiffLine(DiffLineKind.Removed, "gone", 1, null), Assert.Single(h.Lines));
    }

    [Fact]
    public void ModeChangeOnly()
    {
        var d = DiffParser.Parse("""
            diff --git gergitsrc/run.sh gergitdst/run.sh
            old mode 100644
            new mode 100755

            """);
        Assert.Equal((100644, 100755), (d.OldMode, d.NewMode));
        Assert.Empty(d.Hunks);
        Assert.False(d.IsEmpty);
    }

    [Fact]
    public void IndexLineModeAppliesToBothSides()
    {
        var d = DiffParser.Parse($"""
            diff --git gergitsrc/a gergitdst/a
            index {A}..{B} 100644
            --- gergitsrc/a
            +++ gergitdst/a
            @@ -2,3 +2,3 @@ void Main()
             x
            -y
            +z
             w

            """);
        Assert.Equal((100644, 100644), (d.OldMode, d.NewMode));
        var h = Assert.Single(d.Hunks);
        Assert.Equal("void Main()", h.Context);
        Assert.Equal([
            new DiffLine(DiffLineKind.Context, "x", 2, 2),
            new DiffLine(DiffLineKind.Removed, "y", 3, null),
            new DiffLine(DiffLineKind.Added, "z", null, 3),
            new DiffLine(DiffLineKind.Context, "w", 4, 4)], h.Lines);
    }

    [Fact]
    public void Binary()
    {
        var d = DiffParser.Parse($"""
            diff --git gergitsrc/img.png gergitdst/img.png
            index {A}..{B} 100644
            Binary files gergitsrc/img.png and gergitdst/img.png differ

            """);
        Assert.True(d.IsBinary);
        Assert.Empty(d.Hunks);
        Assert.False(d.IsEmpty);
    }

    [Fact]
    public void Submodule()
    {
        var d = DiffParser.Parse($"""
            diff --git gergitsrc/lib gergitdst/lib
            index {A}..{B} 160000
            --- gergitsrc/lib
            +++ gergitdst/lib
            @@ -1 +1 @@
            -Subproject commit {A}
            +Subproject commit {B}

            """);
        Assert.True(d.IsSubmodule);
    }

    [Fact]
    public void NoNewlineMarksPrecedingLine()
    {
        var d = DiffParser.Parse("""
            diff --git gergitsrc/a gergitdst/a
            --- gergitsrc/a
            +++ gergitdst/a
            @@ -1,2 +1,2 @@
            -old
            \ No newline at end of file
            +new
            \ No newline at end of file
             same
            \ No newline at end of file

            """);
        var lines = Assert.Single(d.Hunks).Lines;
        Assert.All(lines, l => Assert.True(l.NoNewlineAtEnd));
        Assert.Equal(3, lines.Count);
    }

    [Fact]
    public void Rename()
    {
        var d = DiffParser.Parse($"""
            diff --git gergitsrc/old name.txt gergitdst/new name.txt
            similarity index 90%
            rename from old name.txt
            rename to new name.txt
            index {A}..{B} 100644

            """);
        Assert.Equal(("old name.txt", "new name.txt"), (d.OldPath, d.NewPath));
        Assert.True(d.IsEmpty);
    }

    [Fact]
    public void CQuotedUnicodePath()
    {
        // "dir/ä\tb.txt" with an umlaut as octal UTF-8 bytes and a tab escape.
        var d = DiffParser.Parse($"""
            diff --git "gergitsrc/dir/\303\244\tb.txt" "gergitdst/dir/\303\244\tb.txt"
            new file mode 100644
            index {Z}..{B}

            """);
        Assert.Null(d.OldPath);
        Assert.Equal("dir/ä\tb.txt", d.NewPath);
    }

    [Fact]
    public void UnquoteEscapes() =>
        Assert.Equal("a\"b\\c\nü", DiffParser.Unquote("\"a\\\"b\\\\c\\n\\303\\274\""));

    [Fact]
    public void OnlyFirstSectionIsUsed_AndMinifiedDetected()
    {
        var longLine = new string('x', 2048);
        var d = DiffParser.Parse($"diff --git gergitsrc/a gergitdst/a\n@@ -1 +1 @@\n-{longLine}\n+y\n" +
                                 "diff --git gergitsrc/b gergitdst/b\n@@ -1 +1 @@\n-q\n+r\n");
        Assert.Equal("a", d.NewPath);
        Assert.Equal(2, Assert.Single(d.Hunks).Lines.Count);
        Assert.True(d.IsMinified);
        Assert.Equal(2049, d.TotalLength);
    }

    [Fact]
    public void EmptyOutput() => Assert.True(DiffParser.Parse("").IsEmpty);
}
