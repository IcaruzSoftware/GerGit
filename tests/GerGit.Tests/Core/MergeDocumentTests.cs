using GerGit.Core.Conflicts;

namespace GerGit.Tests.Core;

/// <summary>The resolver model, pure.</summary>
public class MergeDocumentTests
{
    const string Header = "diff --cc f.txt\nindex 1111111,2222222..0000000\n--- a/f.txt\n+++ b/f.txt\n@@@ -1,6 -1,5 +1,10 @@@\n";

    // Real git output (merge of `side` into `main`): an ours-only line, a conflict, and an auto-merged change at the end.
    const string Simple = Header +
        " +a0\n" +
        "  a\n" +
        "++<<<<<<< HEAD\n" +
        " +B-ours\n" +
        "++=======\n" +
        "+ B-theirs\n" +
        "++>>>>>>> side\n" +
        "  c\n" +
        "- e\n" +
        "+ E2\n";

    static MergeDocument Doc(string diff, bool noNewline = false) =>
        new("f.txt", MergeMode.Text, MergeDocument.ParseChunks(diff), noNewline);

    static string[] Texts(IEnumerable<MergeLine> lines) => lines.Select(l => l.Text).ToArray();

    [Fact]
    public void Simple_conflict_chunks()
    {
        var d = Doc(Simple);
        Assert.False(d.Failed);
        Assert.Equal([MergeChunkKind.Change, MergeChunkKind.Context, MergeChunkKind.Conflict, MergeChunkKind.Context, MergeChunkKind.Change],
            d.Chunks.Select(c => c.Kind));

        // " +a0": `+` in column 2 = an ours line of type added, pre-selected.
        var first = d.Chunks[0];
        Assert.Equal(["a0\n"], Texts(first.Ours));
        Assert.Equal(MergeLineKind.Added, first.Ours[0].Kind);
        Assert.Empty(first.Theirs);

        var conflict = d.Chunks[2];
        Assert.Equal(["B-ours\n"], Texts(conflict.Ours));
        Assert.Equal(["B-theirs\n"], Texts(conflict.Theirs));
        Assert.Equal("HEAD", conflict.OursLabel);
        Assert.Equal("side", conflict.TheirsLabel);
        Assert.False(conflict.IsResolved);

        // "- e": ours removed (not in the result); "+ E2": theirs added (in the result).
        var last = d.Chunks[4];
        Assert.Equal(MergeLineKind.Removed, last.Ours.Single().Kind);
        Assert.Equal(MergeLineKind.Added, last.Theirs.Single().Kind);
        Assert.Equal(["E2\n"], last.Result.Select(r => r.Text));

        Assert.Equal(1, d.ConflictCount);
        Assert.Equal(0, d.ResolvedCount);
        Assert.False(d.AllResolved);
        Assert.Equal("a0\na\nc\nE2\n", d.BuildResult()); // the conflict has no result lines yet
    }

    [Fact]
    public void Both_sides_added_the_same_region()
    {
        var d = Doc("diff --cc aa.txt\n@@@ -1,1 -1,1 +1,5 @@@\n++<<<<<<< HEAD\n +add-ours\n++=======\n+ add-theirs\n++>>>>>>> side\n");
        var c = Assert.Single(d.Chunks);
        Assert.Equal(["add-ours\n"], Texts(c.Ours));
        Assert.Equal(["add-theirs\n"], Texts(c.Theirs));
    }

    [Fact]
    public void Diff3_base_lines_are_skipped_and_the_next_separator_counts()
    {
        var d = Doc(Header + "  l1\n++<<<<<<< HEAD\n +O\n++||||||| 2ff03bb\n++l2\n++=======\n+ T\n++>>>>>>> side\n  l3\n");
        var c = d.Conflicts.Single();
        Assert.Equal(["O\n"], Texts(c.Ours));
        Assert.Equal(["T\n"], Texts(c.Theirs));
    }

    [Fact]
    public void Empty_side_gets_a_selectable_placeholder()
    {
        var d = Doc(Header + "  p\n++<<<<<<< HEAD\n +Q\n++||||||| base\n++q\n++=======\n++>>>>>>> side\n  r\n");
        var c = d.Conflicts.Single();
        var placeholder = Assert.Single(c.Theirs);
        Assert.True(placeholder.IsPlaceholder);
        Assert.Equal("", placeholder.Text);

        // "Take nothing from this side": resolved, nothing added to the text.
        d.ToggleLine(c, placeholder);
        Assert.True(d.AllResolved);
        Assert.Equal("p\nr\n", d.BuildResult());
        Assert.True(d.TheirsChecked);
    }

    [Fact]
    public void Lines_outside_markers_with_minus_are_dropped_inside_conflicts()
    {
        // `-` in column 1 inside the ours section and in column 2 inside the theirs section: not side lines.
        var d = Doc(Header + "++<<<<<<< HEAD\n-+gone-ours\n +kept-ours\n++=======\n+-gone-theirs\n+ kept-theirs\n++>>>>>>> side\n");
        var c = d.Conflicts.Single();
        Assert.Equal(["kept-ours\n"], Texts(c.Ours));
        Assert.Equal(["kept-theirs\n"], Texts(c.Theirs));
    }

    [Fact]
    public void Change_chunk_classification_order()
    {
        // `++` outside markers: `+` in column 2 wins (ours added); `+-`: theirs removed; `-+`: ours added.
        var d = Doc(Header + "++both\n+-x\n-+y\n -z\n");
        var c = Assert.Single(d.Chunks);
        Assert.Equal(MergeChunkKind.Change, c.Kind);
        Assert.Equal([("both\n", MergeLineKind.Added), ("y\n", MergeLineKind.Added)], c.Ours.Select(l => (l.Text, l.Kind)));
        Assert.Equal([("x\n", MergeLineKind.Removed), ("z\n", MergeLineKind.Removed)], c.Theirs.Select(l => (l.Text, l.Kind)));
        Assert.Equal("both\ny\n", d.BuildResult());
    }

    [Fact]
    public void Change_chunk_result_keeps_diff_order()
    {
        var d = Doc(Header + "+ t1\n +o1\n+ t2\n");
        Assert.Equal("t1\no1\nt2\n", d.BuildResult());
        // Change chunks cannot be toggled.
        var c = d.Chunks[0];
        d.ToggleLine(c, c.Ours[0]);
        Assert.Equal("t1\no1\nt2\n", d.BuildResult());
    }

    [Fact]
    public void Header_lines_backslash_lines_and_later_hunk_headers_are_ignored()
    {
        var d = Doc("diff --cc f\n  not-a-body-line\n@@@ -1 -1 +1 @@@\n  a\n\\ No newline at end of file\n@@@ -9 -9 +9 @@@\n  b\n");
        Assert.Equal("a\nb\n", d.BuildResult());
    }

    [Fact]
    public void No_newline_at_end_removes_one_trailing_newline()
    {
        var d = Doc(Header + "  k\n++<<<<<<< HEAD\n++lastO\n++=======\n++lastT\n++>>>>>>> side\n", noNewline: true);
        var c = d.Conflicts.Single();
        d.ToggleLine(c, c.Ours[0]);
        Assert.Equal("k\nlastO", d.BuildResult());
    }

    [Fact]
    public void Crlf_lines_keep_their_carriage_return()
    {
        // The \r stays in the text and in the saved result; labels and row texts have none.
        var d = Doc(Header + "  a\r\n++<<<<<<< HEAD\r\n +O\r\n++=======\r\n+ T\r\n++>>>>>>> side\r\n  c\r\n");
        var c = d.Conflicts.Single();
        Assert.Equal("HEAD", c.OursLabel);
        Assert.Equal("side", c.TheirsLabel);
        d.ToggleLine(c, c.Theirs[0]);
        Assert.Equal("a\r\nT\r\nc\r\n", d.BuildResult());
        Assert.Equal("T", c.Result[0].Body);
        Assert.Equal(["a", "T", "c"], MergeLayout.Build(d).Result.Select(r => r.Text));
    }

    [Theory]
    [InlineData("++<<<<<<< a\n +x\n++<<<<<<< b\n")]                        // start marker inside a conflict
    [InlineData("  a\n++>>>>>>> side\n")]                                  // end marker without a conflict
    [InlineData("++<<<<<<< a\n +x\n++=======\n+ y\n")]                     // conflict never closed
    [InlineData("++<<<<<<< a\n +x\n++=======\n+ y\n++=======\n++>>>>>>> b\n")] // second separator
    [InlineData("++<<<<<<< a\n +x\n++=======\n++||||||| b\n++>>>>>>> b\n")]   // base start after the separator
    [InlineData("++<<<<<<< a\n +x\n++>>>>>>> b\n")]                        // end marker before the separator
    [InlineData("++<<<<<<< a\n +x\n++=======\n+ y\n++>>>>>>>")]            // marker line shorter than 10 characters
    public void Parse_errors(string body) => Assert.True(Doc(Header + body).Failed);

    [Fact]
    public void No_combined_hunk_header_is_a_failed_parse()
    {
        Assert.True(Doc("* Unmerged path del.txt\n").Failed);
        Assert.Null(MergeDocument.ParseChunks(""));
    }

    [Fact]
    public void Classify_modes()
    {
        Assert.Equal(MergeMode.Text, MergeDocument.Classify(Simple, false));
        Assert.Equal(MergeMode.Binary, MergeDocument.Classify("diff --cc b.bin\nindex 1,2..0\nBinary files differ\n", false));
        Assert.Equal(MergeMode.Submodule, MergeDocument.Classify("diff --cc sm\nindex 1,2..0\n--- a/sm\n+++ b/sm\n", true));
        Assert.Equal(MergeMode.Lfs, MergeDocument.Classify(
            Header + "++<<<<<<< HEAD\n +version https://git-lfs.github.com/spec/v1\n++=======\n+ version https://git-lfs.github.com/spec/v1\n++>>>>>>> x\n", false));
    }

    [Fact]
    public void Selected_placeholder_is_an_unnumbered_protected_result_row()
    {
        var d = Doc(Header + "  p\n++<<<<<<< HEAD\n +Q\n++=======\n++>>>>>>> side\n  r\n");
        var c = d.Conflicts.Single();
        d.ToggleLine(c, c.Theirs[0]);
        var rows = MergeLayout.Build(d).Result;
        Assert.Equal([1, null, 2], rows.Select(r => r.Number));
        Assert.Equal([MergeRowKind.Line, MergeRowKind.Empty, MergeRowKind.Line], rows.Select(r => r.Kind));
        Assert.Equal(["p", "", "r"], rows.Select(r => r.Text));
        Assert.Equal("p\nr\n", d.BuildResult());
    }

    [Fact]
    public void Edit_gives_a_line_without_ending_a_newline()
    {
        var d = Doc(Header + "  p\n  r\n", noNewline: true);
        var inserted = new ResultLine(null, "");
        d.Chunks[0].Result.Insert(1, inserted);
        inserted.Edit("x");
        Assert.Equal("p\nx\nr", d.BuildResult());
        d.Chunks[0].Result[2].Edit("last");
        Assert.Equal("p\nx\nlast", d.BuildResult()); // the file's last line keeps having no newline
    }

    // ---------------------------------------------------------------- selection

    const string TwoLines = Header +
        "++<<<<<<< HEAD\n +o1\n +o2\n++=======\n+ t1\n+ t2\n++>>>>>>> side\n" +
        "  mid\n" +
        "++<<<<<<< HEAD\n +o3\n++=======\n+ t3\n++>>>>>>> side\n";

    [Fact]
    public void Toggle_appends_in_selection_order_and_deselect_drops_the_edit()
    {
        var d = Doc(TwoLines);
        var c = d.Conflicts.First();
        d.ToggleLine(c, c.Ours[1]);
        d.ToggleLine(c, c.Theirs[0]);
        d.ToggleLine(c, c.Ours[0]);
        Assert.Equal(["o2\n", "t1\n", "o1\n"], c.Result.Select(r => r.Text)); // selection order
        Assert.Equal(1, d.ResolvedCount);

        c.Result[1].Edit("T1 edited");
        Assert.Equal("T1 edited\n", c.Result[1].Text);
        d.ToggleLine(c, c.Theirs[0]); // deselect: the edit is gone
        d.ToggleLine(c, c.Theirs[0]); // select again: original text
        Assert.Equal(["o2\n", "o1\n", "t1\n"], c.Result.Select(r => r.Text));
    }

    [Fact]
    public void Chunk_button_selects_in_original_order_and_keeps_the_other_side()
    {
        var d = Doc(TwoLines);
        var c = d.Conflicts.First();
        d.ToggleLine(c, c.Theirs[1]);
        Assert.False(MergeDocument.IsSideSelected(c, MergeSide.Theirs)); // label: Select Left
        d.ToggleChunkSide(c, MergeSide.Ours);
        d.ToggleChunkSide(c, MergeSide.Theirs); // re-selects t1, t2 in their order after the ours lines
        Assert.Equal(["o1\n", "o2\n", "t1\n", "t2\n"], c.Result.Select(r => r.Text));
        Assert.True(MergeDocument.IsSideSelected(c, MergeSide.Theirs)); // label: Remove Left
        d.ToggleChunkSide(c, MergeSide.Theirs);
        Assert.Equal(["o1\n", "o2\n"], c.Result.Select(r => r.Text));
    }

    [Fact]
    public void Whole_side_checkboxes()
    {
        var d = Doc(TwoLines);
        Assert.False(d.OursChecked);
        Assert.False(d.TheirsChecked);

        d.SetSide(MergeSide.Ours, true);
        Assert.True(d.OursChecked);
        Assert.False(d.TheirsChecked);
        Assert.True(d.AllResolved);
        Assert.Equal("o1\no2\nmid\no3\n", d.BuildResult());

        // "all theirs" after "all ours" only adds; both boxes show unchecked.
        d.SetSide(MergeSide.Theirs, true);
        Assert.False(d.OursChecked);
        Assert.False(d.TheirsChecked);
        Assert.Equal("o1\no2\nt1\nt2\nmid\no3\nt3\n", d.BuildResult());

        d.SetSide(MergeSide.Ours, false);
        Assert.True(d.TheirsChecked);
        Assert.Equal("t1\nt2\nmid\nt3\n", d.BuildResult());

        d.SetSide(MergeSide.Theirs, false);
        Assert.Equal(0, d.ResolvedCount);
        Assert.Equal(2, d.ConflictCount);
    }

    // ---------------------------------------------------------------- layout

    [Fact]
    public void Layout_aligns_chunks_and_numbers_real_lines()
    {
        var d = Doc(Header + "  a\n++<<<<<<< HEAD\n +o1\n +o2\n++=======\n++>>>>>>> side\n  z\n");
        var l = MergeLayout.Build(d);
        Assert.Equal(4, l.Theirs.Count);
        Assert.Equal(4, l.Ours.Count);
        Assert.Equal(4, l.Result.Count);

        Assert.Equal([MergeRowKind.Line, MergeRowKind.Empty, MergeRowKind.Filler, MergeRowKind.Line], l.Theirs.Select(r => r.Kind));
        Assert.Equal(["a", MergeLayout.EmptyText, "", "z"], l.Theirs.Select(r => r.Text));
        Assert.Equal([1, null, null, 2], l.Theirs.Select(r => r.Number));
        Assert.Equal([1, 2, 3, 4], l.Ours.Select(r => r.Number));
        Assert.Equal(["a", MergeLayout.ConflictText, "", "z"], l.Result.Select(r => r.Text));
        Assert.Equal([MergeRowKind.Line, MergeRowKind.Unresolved, MergeRowKind.Unresolved, MergeRowKind.Line], l.Result.Select(r => r.Kind));

        var c = d.Conflicts.Single();
        d.ToggleLine(c, c.Ours[1]);
        l = MergeLayout.Build(d);
        Assert.Equal(["a", "o2", "", "z"], l.Result.Select(r => r.Text));
        Assert.Equal([1, 2, null, 3], l.Result.Select(r => r.Number));
        Assert.Equal(MergeRowKind.Filler, l.Result[2].Kind);
        Assert.Same(c.Result[0], l.Result[1].Result);
        Assert.Equal(1, l.Result[1].Chunk);
    }
}
