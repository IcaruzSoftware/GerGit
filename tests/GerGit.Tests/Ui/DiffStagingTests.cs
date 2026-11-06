using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using GerGit.Core;
using GerGit.Core.Diff;
using GerGit.Core.Git;
using GerGit.ViewModels;
using GerGit.Views;

namespace GerGit.Tests.Ui;

/// <summary>Hunk and line staging (diff pane): groups, hover, floating buttons, keys.</summary>
public sealed class DiffStagingTests : IDisposable
{
    readonly TempRepo _repo = new();
    readonly List<(PatchAction Action, HunkLine[] Lines)> _calls = [];

    static readonly DiffRequest Unstaged = new(null, "", "f.txt", null, Working: WorkingDiff.Unstaged);

    public DiffStagingTests()
    {
        var lines = Enumerable.Range(1, 40).Select(i => $"line {i}").ToList();
        _repo.Commit("init", 0, ("f.txt", string.Join("\n", lines) + "\n"));
        lines[4] = "five";
        lines[11] = "twelve";
        lines[34] = "thirty-five";
        _repo.Write("f.txt", string.Join("\n", lines) + "\n");
        // Document of the unstaged diff (context 3):
        //  0 @@ -2,14 +2,14 @@ | 1-3 line 2..4 | 4 -line 5 | 5 +five | 6-11 line 6..11 | 12 -line 12 | 13 +twelve
        // 14-16 line 13..15 | 17 @@ -32,7 +32,7 @@ | 18-20 line 32..34 | 21 -line 35 | 22 +thirty-five | 23-25 line 36..38
    }

    public void Dispose() => _repo.Dispose();

    async Task<(DiffViewModel Vm, Window Window, DiffView View)> OpenAsync(DiffRequest q, LineActionSet set)
    {
        var vm = new DiffViewModel(await GitRepository.OpenAsync(_repo.Path))
        {
            LineActions = set,
            LineActionHandler = (a, l) => { _calls.Add((a, l.ToArray())); return Task.CompletedTask; },
        };
        var view = new DiffView { DataContext = vm };
        var window = new Window { Width = 800, Height = 600, Content = view };
        window.Show();
        vm.Show(q);
        await vm.Loading;
        Settle(window);
        return (vm, window, view);
    }

    static void Settle(Window w)
    {
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    static TextEditor Editor(DiffView v) => v.GetVisualDescendants().OfType<TextEditor>().Single(e => e.Name == "Editor");

    /// <summary>Moves the mouse over document line <paramref name="line"/> (0-based) of the editor.</summary>
    static void HoverLine(Window w, DiffView v, int line)
    {
        var tv = Editor(v).TextArea.TextView;
        var y = tv.GetVisualTopByDocumentLine(line + 1) - tv.ScrollOffset.Y + tv.DefaultLineHeight / 2;
        w.MouseMove(tv.TranslatePoint(new Point(200, y), w)!.Value);
        Settle(w);
    }

    static Border Buttons(DiffView v) => v.FindControl<Border>("LineButtons")!;

    static string[] VisibleButtons(DiffView v) =>
        Buttons(v).IsVisible
            ? Buttons(v).GetVisualDescendants().OfType<Button>().Where(b => b.IsVisible).Select(b => (string)b.Content!).ToArray()
            : [];

    static void Click(Window w, Control c)
    {
        var at = c.TranslatePoint(new Point(c.Bounds.Width / 2, c.Bounds.Height / 2), w)!.Value;
        w.MouseMove(at);
        w.MouseDown(at, MouseButton.Left);
        w.MouseUp(at, MouseButton.Left);
        Settle(w);
    }

    /// <summary>Pixel at x of 0-based document line <paramref name="line"/> (same reading as DetailsTests).</summary>
    static Color Pixel(Avalonia.Media.Imaging.WriteableBitmap frame, Window window, AvaloniaEdit.Rendering.TextView tv, int line, double x)
    {
        var y = tv.GetVisualTopByDocumentLine(line + 1) - tv.ScrollOffset.Y + 1;
        var p = tv.TranslatePoint(new Point(x, y), window)!.Value;
        using var fb = frame.Lock();
        var at = (int)p.Y * fb.RowBytes + (int)p.X * 4;
        byte B(int i) => Marshal.ReadByte(fb.Address, at + i);
        return fb.Format == PixelFormat.Rgba8888 ? Color.FromArgb(B(3), B(0), B(1), B(2)) : Color.FromArgb(B(3), B(2), B(1), B(0));
    }

    sealed class ColorClose : IEqualityComparer<Color>
    {
        public bool Equals(Color a, Color b) => Math.Abs(a.R - b.R) <= 1 && Math.Abs(a.G - b.G) <= 1 && Math.Abs(a.B - b.B) <= 1;
        public int GetHashCode(Color c) => 0;
    }

    [Fact]
    public void Groups_join_gaps_of_three_context_lines_and_split_at_four()
    {
        DocLine L(DocLineKind k, int hunk = 0) => new(k, "", null, null, [], k == DocLineKind.HunkHeader ? -1 : hunk, 0);
        var c = DocLineKind.Context; var a = DocLineKind.Added; var r = DocLineKind.Removed; var h = DocLineKind.HunkHeader;

        // Example: 3 changed, 3 context, 2 changed = one group (plus 2 context lines on each side).
        var one = new DiffDocument(new[] { h, c, c, c, r, r, a, c, c, c, a, a, c, c, c }.Select(k => L(k)).ToList(), []);
        Assert.Equal([(2, 13)], DiffViewModel.StagingGroups(one));

        // 4 context lines between: two groups; the second starts right after the first one ends.
        var two = new DiffDocument(new[] { h, r, r, a, c, c, c, c, a, a, c }.Select(k => L(k)).ToList(), []);
        Assert.Equal([(1, 5), (6, 10)], DiffViewModel.StagingGroups(two));

        // Never across hunk boundaries: a header, or (entire-file mode) another hunk index.
        var hunks = new DiffDocument(new[] { L(h), L(a), L(c), L(h), L(c, 1), L(r, 1) }, []);
        Assert.Equal([(1, 2), (4, 5)], DiffViewModel.StagingGroups(hunks));
        var entire = new DiffDocument(new[] { L(a), L(c), L(c, 1), L(r, 1) }, []);
        Assert.Equal([(0, 1), (2, 3)], DiffViewModel.StagingGroups(entire));
    }

    [Fact]
    public void Document_lines_point_to_their_hunk_line()
    {
        var diff = DiffParser.Parse("""
            diff --git gergitsrc/a gergitdst/a
            --- gergitsrc/a
            +++ gergitdst/a
            @@ -1,3 +1,3 @@
             c1
            +a1
            -r1
             c2
            @@ -9,1 +9,1 @@
            -last
            \ No newline at end of file
            +last!

            """);
        var doc = DiffDocumentBuilder.Build(diff, entireFile: false);
        // The removed line is shown before the added one, but still points to its own source line.
        Assert.Equal(
            [(DocLineKind.HunkHeader, -1, -1), (DocLineKind.Context, 0, 0), (DocLineKind.Removed, 0, 2), (DocLineKind.Added, 0, 1),
             (DocLineKind.Context, 0, 3), (DocLineKind.HunkHeader, -1, -1), (DocLineKind.Removed, 1, 0), (DocLineKind.Service, -1, -1),
             (DocLineKind.Added, 1, 1)],
            doc.Lines.Select(l => (l.Kind, l.Hunk, l.Line)));
    }

    [AvaloniaFact]
    public async Task Hover_shows_stage_and_discard_and_stage_acts_on_the_group()
    {
        var (vm, window, view) = await OpenAsync(Unstaged, LineActionSet.StageDiscard);
        Assert.Equal(DiffState.Text, vm.State);
        Assert.Empty(VisibleButtons(view));

        HoverLine(window, view, 13); // +twelve: second group of the first hunk
        Assert.Equal(["Stage", "Discard..."], VisibleButtons(view));
        // Container: top = first group line + 15, right edge 20 px left of the text view's right edge.
        var tv = Editor(view).TextArea.TextView;
        var box = Buttons(view).TranslatePoint(default, tv)!.Value;
        Assert.Equal(tv.GetVisualTopByDocumentLine(11) - tv.ScrollOffset.Y + 15, box.Y, tolerance: 1);
        Assert.Equal(tv.Bounds.Width - 20, box.X + Buttons(view).Bounds.Width, tolerance: 1);
        Assert.Equal(22, view.FindControl<Button>("StageButton")!.Bounds.Height);

        // Tint (#358CFF at alpha 20 over the #282828 editor) on the group's context lines only.
        using (var frame = window.CaptureRenderedFrame()!)
        {
            Assert.Equal(Color.FromRgb(41, 48, 57), Pixel(frame, window, tv, 11, tv.Bounds.Width - 5), new ColorClose());
            Assert.Equal(Color.Parse("#282828"), Pixel(frame, window, tv, 9, tv.Bounds.Width - 5));
        }

        Click(window, view.FindControl<Button>("StageButton")!);
        Assert.Equal(PatchAction.Stage, Assert.Single(_calls).Action);
        Assert.Equal([new HunkLine(0, 11), new HunkLine(0, 12)], _calls[0].Lines);

        HoverLine(window, view, 3); // context line inside the first group (2..7)
        Assert.Equal(["Stage", "Discard..."], VisibleButtons(view));
        HoverLine(window, view, 8); // context line between the groups
        Assert.Empty(VisibleButtons(view));
        window.MouseMove(new Point(-5, -5));
        Settle(window);
        Assert.Empty(VisibleButtons(view)); // mouse left the editor
        window.Close();
    }

    [AvaloniaFact]
    public async Task Selection_across_hunks_with_enter_stages_touched_lines_and_delete_discards()
    {
        var (vm, window, view) = await OpenAsync(Unstaged, LineActionSet.StageDiscard);
        var editor = Editor(view);
        var doc = editor.Document;
        // From the last character of "+twelve" to the very start of "+thirty-five" (that line is not touched).
        int start = doc.GetLineByNumber(14).EndOffset - 1, end = doc.GetLineByNumber(23).Offset;
        editor.TextArea.Focus();
        editor.Select(start, end - start);
        Settle(window);
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Assert.Equal(PatchAction.Stage, Assert.Single(_calls).Action);
        Assert.Equal([new HunkLine(0, 12), new HunkLine(1, 3)], _calls[0].Lines);

        window.KeyPress(Key.S, RawInputModifiers.Control | RawInputModifiers.Alt, PhysicalKey.S, null); // Alt blocks the toggle
        Assert.Single(_calls);

        // While the handler runs, further keys are ignored.
        var pending = new TaskCompletionSource();
        vm.LineActionHandler = (a, l) => { _calls.Add((a, l.ToArray())); return pending.Task; };
        window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
        window.KeyPress(Key.D, RawInputModifiers.Control | RawInputModifiers.Shift | RawInputModifiers.Alt, PhysicalKey.D, null);
        Assert.Equal(2, _calls.Count);
        Assert.Equal(PatchAction.Discard, _calls[1].Action);
        Assert.Equal(_calls[0].Lines, _calls[1].Lines);
        pending.SetResult();
        Settle(window);
        window.KeyPress(Key.D, RawInputModifiers.Control | RawInputModifiers.Shift | RawInputModifiers.Alt, PhysicalKey.D, null);
        Assert.Equal(PatchAction.Discard, _calls[2].Action);

        // Without a selection the keys do nothing.
        editor.Select(0, 0);
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Assert.Equal(3, _calls.Count);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Staged_diff_shows_only_unstage()
    {
        _repo.Git("add", "f.txt");
        var (_, window, view) = await OpenAsync(Unstaged with { Working = WorkingDiff.Staged }, LineActionSet.Unstage);
        HoverLine(window, view, 22);
        Assert.Equal(["Unstage"], VisibleButtons(view));
        Click(window, view.FindControl<Button>("UnstageButton")!);
        Assert.Equal(PatchAction.Unstage, Assert.Single(_calls).Action);
        Assert.Equal([new HunkLine(1, 3), new HunkLine(1, 4)], _calls[0].Lines);

        // Delete never discards in a staged diff.
        var editor = Editor(view);
        editor.TextArea.Focus();
        editor.SelectAll();
        window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
        Assert.Single(_calls);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Commit_diffs_binary_and_invalid_utf8_show_no_buttons()
    {
        var (vm, window, view) = await OpenAsync(Unstaged, LineActionSet.None);
        HoverLine(window, view, 5);
        Assert.Empty(VisibleButtons(view));

        _repo.Write("bin.dat", "x\0\0\0y");
        _repo.Git("add", "-N", "bin.dat");
        vm.LineActions = LineActionSet.StageDiscard;
        vm.Show(Unstaged with { Path = "bin.dat" });
        await vm.Loading;
        Settle(window);
        Assert.Equal(DiffState.Binary, vm.State);
        Assert.False(vm.ShowsLineActions);
        Assert.Empty(VisibleButtons(view));

        File.WriteAllBytes(Path.Combine(_repo.Path, "f.txt"), [.. "line 1\nbad "u8, 0xFF, (byte)'\n']);
        vm.Show(Unstaged);
        await vm.Loading;
        Settle(window);
        Assert.Equal(DiffState.Text, vm.State);
        Assert.False(vm.ShowsLineActions);
        HoverLine(window, view, 1);
        Assert.Empty(VisibleButtons(view));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Reloading_the_same_file_keeps_the_scroll_position()
    {
        var lines = Enumerable.Range(1, 300).Select(i => $"line {i}").ToList();
        _repo.Commit("long", 1, ("long.txt", string.Join("\n", lines) + "\n"), ("other.txt", string.Join("\n", lines) + "\n"));
        for (int i = 0; i < 300; i += 10) lines[i] = "changed";
        _repo.Write("long.txt", string.Join("\n", lines) + "\n");
        _repo.Write("other.txt", string.Join("\n", lines) + "\n");
        var q = Unstaged with { Path = "long.txt" };
        var (vm, window, view) = await OpenAsync(q, LineActionSet.StageDiscard);
        var sv = Editor(view).FindDescendantOfType<ScrollViewer>()!;
        sv.Offset = sv.Offset.WithY(800);
        Settle(window);

        vm.Show(q);
        await vm.Loading;
        Settle(window);
        Assert.Equal(800, sv.Offset.Y, tolerance: 1);

        vm.Show(q with { Path = "other.txt" }); // another file: from the top
        await vm.Loading;
        Settle(window);
        Assert.Equal(0, sv.Offset.Y, tolerance: 1);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Reload_with_identical_lines_keeps_the_selection_and_a_changed_result_clears_it()
    {
        var (vm, window, view) = await OpenAsync(Unstaged, LineActionSet.StageDiscard);
        var editor = Editor(view);
        var text = editor.Document;
        editor.TextArea.Focus();
        int start = text.GetLineByNumber(3).Offset, length = text.GetLineByNumber(5).Offset + 1 - start; // "line 3" .. first character of "-line 5"
        editor.Select(start, length);
        Settle(window);
        var oldDiff = vm.FileDiff;

        vm.Show(Unstaged); // e.g. the index watcher's refresh after a stage
        await vm.Loading;
        Settle(window);
        Assert.Same(text, editor.Document);
        Assert.Equal((start, length), (editor.SelectionStart, editor.SelectionLength));
        Assert.NotSame(oldDiff, vm.FileDiff); // the new parse, describing the same lines
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Assert.Equal([new HunkLine(0, 3)], Assert.Single(_calls).Lines);

        _repo.Write("f.txt", "other\n");
        vm.Show(Unstaged);
        await vm.Loading;
        Settle(window);
        Assert.NotSame(text, editor.Document);
        Assert.Equal(0, editor.SelectionLength);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Untracked_file_above_5_MiB_shows_the_size_fallback_until_load_diff()
    {
        var line = new string('x', 99) + "\n";
        _repo.Write("big.txt", string.Concat(Enumerable.Repeat(line, 52_429)) + "y"); // 5,242,901 bytes
        var (vm, window, view) = await OpenAsync(Unstaged with { Path = "big.txt", Working = WorkingDiff.Untracked }, LineActionSet.StageDiscard);
        Assert.Equal(DiffState.TooLarge, vm.State);
        Assert.Equal("Changes are too large (5.0 MB)", vm.TooLargeTitle);
        Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == vm.TooLargeTitle);

        vm.LoadDiffCommand.Execute(null);
        await vm.Loading;
        Assert.Equal(DiffState.Text, vm.State);

        // A status refresh re-shows the same request: "Load Diff" must stick.
        vm.Show(Unstaged with { Path = "big.txt", Working = WorkingDiff.Untracked });
        await vm.Loading;
        Assert.Equal(DiffState.Text, vm.State);
        window.Close();
    }
}
