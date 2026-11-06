using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using GerGit.Controls;
using GerGit.Core.Conflicts;
using GerGit.Core.Git;
using GerGit.ViewModels;
using GerGit.Views;

namespace GerGit.Tests.Ui;

/// <summary>The three-pane resolver on real conflicts, through its view model and window.</summary>
public sealed class MergeResolverTests : IDisposable
{
    readonly TempRepo _repo = new();
    RepoTabViewModel? _tab;
    readonly List<string> _errors = [];
    int _beeps;

    public void Dispose()
    {
        _tab?.Dispose();
        _repo.Dispose();
    }

    const string Base = "a\nb\nc\n";

    /// <summary>main and side change the same region; `git merge side` stops with the conflict.</summary>
    void MergeConflict(string ours, string theirs, string file = "f.txt", string? baseText = Base)
    {
        if (baseText is not null) _repo.Commit("base", 0, (file, baseText));
        else _repo.Commit("base", 0, ("other.txt", "x\n"));
        _repo.Git("checkout", "-q", "-b", "side");
        _repo.Commit("theirs", 10, (file, theirs));
        _repo.Git("checkout", "-q", "main");
        _repo.Commit("ours", 20, (file, ours));
        Assert.Throws<InvalidOperationException>(() => _repo.Git("merge", "-q", "side"));
    }

    async Task<MergeResolverViewModel> OpenAsync(string path = "f.txt")
    {
        _tab = new RepoTabViewModel(await GitRepository.OpenAsync(_repo.Path));
        await _tab.RefreshAsync();
        _tab.ShowGitError = (r, _) => { _errors.Add(r.Stderr); return Task.CompletedTask; };
        _tab.ShowError = (_, text) => { _errors.Add(text); return Task.CompletedTask; };
        var vm = new MergeResolverViewModel(_tab, path) { Beep = () => _beeps++ };
        await vm.LoadAsync();
        return vm;
    }

    string Read(string path) => File.ReadAllText(Path.Combine(_repo.Path, path));

    void AssertStaged(string path)
    {
        Assert.Equal("", _repo.Git("ls-files", "-u")); // no unmerged entries left
        Assert.Equal("", _repo.Git("diff", "--name-only", "--", path)); // the index has the working file's content
    }

    static MergeRow Row(IReadOnlyList<MergeRow> pane, string text) => pane.First(r => r.Text == text);
    static string[] Texts(IReadOnlyList<MergeRow> pane) => pane.Select(r => r.Text).ToArray();

    /// <summary>Offset of the end of the row with <paramref name="text"/> in the result pane text.</summary>
    static int EndOf(MergeResolverViewModel vm, string text)
    {
        int offset = 0;
        foreach (var r in vm.Layout.Result)
        {
            if (r.Text == text) return offset + r.Text.Length;
            offset += r.Text.Length + 1;
        }
        throw new InvalidOperationException(text);
    }

    // ---------------------------------------------------------------- view model

    [AvaloniaFact]
    public async Task Lines_in_selection_order_counter_and_resolve_writes_and_stages()
    {
        MergeConflict("a\nO1\nO2\nc\n", "a\nT\nc\n");
        var vm = await OpenAsync();

        Assert.True(vm.IsText);
        Assert.True(vm.ShowCounter);
        Assert.Equal(["a", "T", "", "c"], Texts(vm.Layout.Theirs));
        Assert.Equal(["a", "O1", "O2", "c"], Texts(vm.Layout.Ours));
        Assert.Equal(["a", MergeLayout.ConflictText, "", "c"], Texts(vm.Layout.Result));
        Assert.Equal(("0/1", false, "Resolved 0 conflicts of 1", false), (vm.Counter, vm.Complete, vm.CounterTip, vm.CanResolve));
        Assert.Equal(("main", "side"), (vm.OursName, vm.TheirsName));
        Assert.Equal("ours", vm.OursCommit?.Subject);
        Assert.Null(vm.TheirsCommit); // merge state: chip only

        vm.ToggleLine(Row(vm.Layout.Ours, "O2"));
        vm.ToggleLine(Row(vm.Layout.Theirs, "T"));
        Assert.Equal(["a", "O2", "T", "c"], Texts(vm.Layout.Result)); // selection order, not file order
        Assert.Equal(("1/1", true, "All conflicts resolved", true), (vm.Counter, vm.Complete, vm.CounterTip, vm.CanResolve));

        vm.ToggleLine(Row(vm.Layout.Ours, "O2")); // deselect
        Assert.Equal(["a", "T", "", "c"], Texts(vm.Layout.Result));
        Assert.Equal(("Remove Left", "Select Right"), (vm.ChunkButtonText(1, MergeSide.Theirs), vm.ChunkButtonText(1, MergeSide.Ours)));
        vm.ToggleChunkSide(1, MergeSide.Ours);
        Assert.Equal("Remove Right", vm.ChunkButtonText(1, MergeSide.Ours));
        Assert.Equal(["a", "T", "O1", "O2", "c"], Texts(vm.Layout.Result));
        Assert.Equal(["a", "T", "", "", "c"], Texts(vm.Layout.Theirs)); // padded to the result's row count

        bool closed = false;
        vm.CloseRequested += () => closed = true;
        Assert.True(await vm.ResolveAsync());
        Assert.True(closed);
        Assert.Empty(_errors);
        Assert.Equal("a\nT\nO1\nO2\nc\n", Read("f.txt"));
        AssertStaged("f.txt");
    }

    [AvaloniaFact]
    public async Task Header_boxes_take_all_and_both_sides_uncheck_both()
    {
        const string mid = "1\n2\n3\n4\n5\n6\n";
        MergeConflict("O\n" + mid + "O2\n", "T\n" + mid + "T2\n", baseText: "a\n" + mid + "d\n");
        var vm = await OpenAsync();
        Assert.Equal(2, vm.ConflictCount);
        Assert.False(vm.TheirsChecked || vm.OursChecked);

        vm.ToggleAll(MergeSide.Theirs);
        Assert.Equal((true, false, "2/2"), (vm.TheirsChecked, vm.OursChecked, vm.Counter));
        Assert.Equal("T\n" + mid + "T2\n", vm.Document!.BuildResult());

        vm.ToggleAll(MergeSide.Ours); // adds ours, keeps theirs
        Assert.Equal((false, false), (vm.TheirsChecked, vm.OursChecked));
        Assert.Equal("T\nO\n" + mid + "T2\nO2\n", vm.Document.BuildResult());

        vm.ToggleLine(Row(vm.Layout.Theirs, "T"));
        vm.ToggleLine(Row(vm.Layout.Theirs, "T2"));
        Assert.Equal((false, true), (vm.TheirsChecked, vm.OursChecked));
        vm.ToggleAll(MergeSide.Ours); // checked → uncheck = deselect
        Assert.Equal(("0/2", false), (vm.Counter, vm.CanResolve));
    }

    [AvaloniaFact]
    public async Task Diff3_style_skips_the_base_lines()
    {
        _repo.Git("config", "merge.conflictStyle", "diff3");
        MergeConflict("a\nO\nc\n", "a\nT\nc\n");
        Assert.Contains("|||||||", Read("f.txt"));
        var vm = await OpenAsync();
        Assert.Equal(["a", "T", "c"], Texts(vm.Layout.Theirs));
        Assert.Equal(["a", "O", "c"], Texts(vm.Layout.Ours));
        vm.ToggleAll(MergeSide.Ours);
        Assert.True(await vm.ResolveAsync());
        Assert.Equal("a\nO\nc\n", Read("f.txt"));
        AssertStaged("f.txt");
    }

    [AvaloniaFact]
    public async Task Add_add_conflict_resolves_with_theirs()
    {
        MergeConflict("n1\nours\n", "n1\ntheirs\n", "new.txt", baseText: null);
        var vm = await OpenAsync("new.txt");
        Assert.True(vm.IsText);
        Assert.Equal(1, vm.ConflictCount);
        vm.ToggleAll(MergeSide.Theirs);
        Assert.True(await vm.ResolveAsync());
        Assert.Equal("n1\ntheirs\n", Read("new.txt"));
        AssertStaged("new.txt");
    }

    [AvaloniaFact]
    public async Task Empty_side_placeholder_resolves_to_nothing_and_is_protected_in_the_result()
    {
        MergeConflict("a\nc\n", "a\nB\nc\n");
        var vm = await OpenAsync();
        var empty = Assert.Single(vm.Layout.Ours, r => r.Kind == MergeRowKind.Empty);
        Assert.Equal(MergeLayout.EmptyText, empty.Text);
        Assert.True(MergeGutter.IsTaggable(empty));

        vm.ToggleLine(empty);
        Assert.True(vm.CanResolve);
        var resultRow = vm.Layout.Result[1];
        Assert.Equal((MergeRowKind.Empty, ""), (resultRow.Kind, resultRow.Text));
        Assert.Equal(ResultEdit.Protected, vm.EditResult(EndOf(vm, "a") + 1, "", "x"));
        Assert.Equal(1, _beeps);

        Assert.True(await vm.ResolveAsync());
        Assert.Equal("a\nc\n", Read("f.txt"));
        AssertStaged("f.txt");
    }

    [AvaloniaFact]
    public async Task Edited_result_line_is_kept_and_dropped_on_deselect()
    {
        MergeConflict("a\nO\nc\n", "a\nT\nc\n");
        var vm = await OpenAsync();

        // Protected: the `--- Merge Conflict ---` row and a filler row; a wrong removed text is a mismatch.
        Assert.Equal(ResultEdit.Protected, vm.EditResult(EndOf(vm, MergeLayout.ConflictText), "", "x"));
        Assert.Equal(1, _beeps);
        Assert.Equal(ResultEdit.Mismatch, vm.EditResult(0, "zz", ""));
        Assert.Equal(1, _beeps);

        vm.ToggleLine(Row(vm.Layout.Theirs, "T"));
        Assert.Equal(ResultEdit.Applied, vm.EditResult(EndOf(vm, "T"), "", "X"));
        Assert.Equal("a\nTX\nc\n", vm.Document!.BuildResult());

        // A line break splits the line into a new line of the same chunk; the side panes get an alignment row.
        Assert.Equal(ResultEdit.Applied, vm.EditResult(EndOf(vm, "TX") - 1, "", "\n"));
        Assert.Equal(["a", "T", "X", "c"], Texts(vm.Layout.Result));
        Assert.Equal(["a", "T", "", "c"], Texts(vm.Layout.Theirs));
        // Joining two lines again (delete the line break).
        Assert.Equal(ResultEdit.Applied, vm.EditResult(EndOf(vm, "T"), "\n", ""));
        Assert.Equal(["a", "TX", "c"], Texts(vm.Layout.Result));
        // A context line is editable too.
        Assert.Equal(ResultEdit.Applied, vm.EditResult(0, "a", "A"));

        vm.ToggleLine(Row(vm.Layout.Theirs, "T")); // deselect drops the edit
        vm.ToggleLine(Row(vm.Layout.Theirs, "T"));
        Assert.Equal("A\nT\nc\n", vm.Document.BuildResult());
        Assert.True(await vm.ResolveAsync());
        Assert.Equal("A\nT\nc\n", Read("f.txt"));
    }

    [AvaloniaFact]
    public async Task Navigation_targets_follow_the_middle_row()
    {
        var lines = Enumerable.Range(0, 60).Select(i => $"l{i}").ToArray();
        string With(string a, string b) { var c = (string[])lines.Clone(); c[5] = a; c[50] = b; return string.Join("\n", c) + "\n"; }
        MergeConflict(With("O5", "O50"), With("T5", "T50"), baseText: string.Join("\n", lines) + "\n");
        var vm = await OpenAsync();
        Assert.Equal([(5, 5), (50, 50)], vm.ConflictRows());
        Assert.Equal(5, vm.TargetRow(0, 3, next: true));
        Assert.Equal(50, vm.TargetRow(5, 10, next: true));
        Assert.Null(vm.TargetRow(45, 50, next: true));  // the middle row is the conflict's start: not after it
        Assert.Equal(5, vm.TargetRow(50, 55, next: false));
        Assert.Equal(50, vm.TargetRow(55, 60, next: false));
        Assert.Null(vm.TargetRow(5, 10, next: false));   // conflict 5 is already at the top
    }

    static void MakeBinaryConflict(TempRepo repo)
    {
        File.WriteAllBytes(Path.Combine(repo.Path, "b.bin"), [0, 1, 2]);
        repo.Commit("base", 0);
        repo.Git("checkout", "-q", "-b", "side");
        File.WriteAllBytes(Path.Combine(repo.Path, "b.bin"), [0, 9, 9]);
        repo.Commit("theirs", 10);
        repo.Git("checkout", "-q", "main");
        File.WriteAllBytes(Path.Combine(repo.Path, "b.bin"), [0, 7, 7]);
        repo.Commit("ours", 20);
        Assert.Throws<InvalidOperationException>(() => repo.Git("merge", "-q", "side"));
    }

    [AvaloniaFact]
    public async Task Binary_conflict_is_an_exclusive_choice()
    {
        MakeBinaryConflict(_repo);
        var vm = await OpenAsync("b.bin");
        Assert.True(vm.IsBinary);
        Assert.False(vm.IsText);
        Assert.False(vm.ShowCounter);
        Assert.False(vm.CanResolve);
        vm.ToggleAll(MergeSide.Ours);
        Assert.Equal((false, true, true), (vm.TheirsChecked, vm.OursChecked, vm.CanResolve));
        vm.ToggleAll(MergeSide.Theirs);
        Assert.Equal((true, false, true), (vm.TheirsChecked, vm.OursChecked, vm.CanResolve));
        vm.ToggleAll(MergeSide.Theirs);
        Assert.False(vm.CanResolve);
        vm.ToggleAll(MergeSide.Theirs);
        Assert.True(await vm.ResolveAsync());
        Assert.Equal([0, 9, 9], File.ReadAllBytes(Path.Combine(_repo.Path, "b.bin")));
        AssertStaged("b.bin");
    }

    // ---------------------------------------------------------------- window

    static void Settle(Window w)
    {
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    static TextEditor Editor(Window w, string name) => w.FindControl<TextEditor>(name)!;

    static double Top(TextEditor e, int row) => e.TextArea.TextView.GetVisualTopByDocumentLine(row + 1);

    static void Click(Window w, Control c) => ClickAt(w, c.TranslatePoint(new Point(c.Bounds.Width / 2, c.Bounds.Height / 2), w)!.Value);

    static void ClickAt(Window w, Point p)
    {
        w.MouseMove(p);
        w.MouseDown(p, MouseButton.Left);
        w.MouseUp(p, MouseButton.Left);
        Settle(w);
    }

    /// <summary>Mouse over row <paramref name="row"/> of a pane at text x <paramref name="x"/> (TextView coordinates;
    /// a negative x lands in the gutter).</summary>
    static Point RowPoint(Window w, TextEditor e, int row, double x)
    {
        var tv = e.TextArea.TextView;
        return tv.TranslatePoint(new Point(x, Top(e, row) - tv.ScrollOffset.Y + tv.DefaultLineHeight / 2), w)!.Value;
    }

    async Task<(MergeResolverViewModel Vm, MergeResolverWindow W)> ShowAsync(string path = "f.txt")
    {
        var vm = await OpenAsync(path);
        var w = new MergeResolverWindow(vm) { Width = 1020, Height = 720 };
        w.Show();
        Settle(w);
        return (vm, w);
    }

    [AvaloniaFact]
    public async Task Window_shows_three_panes_navigates_toggles_orientation_and_esc_cancels()
    {
        var lines = Enumerable.Range(0, 200).Select(i => $"l{i}").ToArray();
        string With(string a, string b) { var c = (string[])lines.Clone(); c[60] = a; c[150] = b; return string.Join("\n", c) + "\n"; }
        MergeConflict(With("O60", "O150"), With("T60\nT60b", "T150"), baseText: string.Join("\n", lines) + "\n");
        var (vm, w) = await ShowAsync();
        bool vertical = vm.Vertical;
        try
        {
            vm.Vertical = false;
            Settle(w);
            var theirs = Editor(w, "TheirsEditor");
            var result = Editor(w, "ResultEditor");
            var ours = Editor(w, "OursEditor");
            Assert.Equal(string.Join("\n", vm.Layout.Theirs.Select(r => r.Text)), theirs.Document.Text);
            Assert.Equal(string.Join("\n", vm.Layout.Ours.Select(r => r.Text)), ours.Document.Text);
            Assert.Equal(string.Join("\n", vm.Layout.Result.Select(r => r.Text)), result.Document.Text);
            Assert.Equal(201, theirs.Document.LineCount); // 200 lines + the second theirs line of the first conflict
            Assert.Equal(201, ours.Document.LineCount);
            Assert.Equal(201, result.Document.LineCount);
            Assert.Equal("0/2", w.FindControl<Border>("Pill")!.FindDescendantOfType<TextBlock>()!.Text);
            Assert.False(w.FindControl<Button>("ResolveButton")!.IsEnabled);

            // Opened at the first conflict; the other panes follow.
            Assert.Equal(Top(ours, 60), ours.TextArea.TextView.ScrollOffset.Y, 1);
            Assert.Equal(ours.TextArea.TextView.ScrollOffset.Y, theirs.TextArea.TextView.ScrollOffset.Y, 1);

            Click(w, w.FindControl<Button>("NextButton")!);
            Assert.Equal(Top(ours, 151), ours.TextArea.TextView.ScrollOffset.Y, 1);
            Assert.Equal(ours.TextArea.TextView.ScrollOffset.Y, theirs.TextArea.TextView.ScrollOffset.Y, 1);
            Assert.Equal(ours.TextArea.TextView.ScrollOffset.Y, result.TextArea.TextView.ScrollOffset.Y, 1);
            Click(w, w.FindControl<Button>("NextButton")!); // no wrap-around
            Assert.Equal(Top(ours, 151), ours.TextArea.TextView.ScrollOffset.Y, 1);
            Click(w, w.FindControl<Button>("PrevButton")!);
            Assert.Equal(Top(ours, 60), ours.TextArea.TextView.ScrollOffset.Y, 1);

            // Orientation: horizontal = result below spanning all columns; vertical = middle column, saved.
            var host = w.FindControl<Border>("ResultHost")!;
            Assert.Equal((2, 0, false), (Grid.GetRow(host), Grid.GetColumn(host), vm.ShowResolvedLabel));
            Click(w, w.FindControl<ToggleButton>("OrientationToggle")!);
            Assert.True(vm.Vertical);
            Assert.True(Settings.Get(MergeResolverViewModel.VerticalKey, false));
            Assert.Equal((0, 1, true), (Grid.GetRow(host), Grid.GetColumn(host), vm.ShowResolvedLabel));
            Click(w, w.FindControl<ToggleButton>("OrientationToggle")!);
            Assert.False(Settings.Get(MergeResolverViewModel.VerticalKey, true));

            w.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Settle(w);
            Assert.False(w.IsVisible);
            Assert.False(vm.Result);
        }
        finally
        {
            Settings.Set(MergeResolverViewModel.VerticalKey, vertical);
            w.Close();
        }
    }

    [AvaloniaFact]
    public async Task Window_gutter_tags_chunk_buttons_and_typing()
    {
        MergeConflict("a\nO1\nO2\nc\n", "a\nT\nc\n");
        var (vm, w) = await ShowAsync();
        try
        {
            var theirs = Editor(w, "TheirsEditor");
            var result = Editor(w, "ResultEditor");
            var ours = Editor(w, "OursEditor");

            // Gutter click on ours row 2 (O2) selects that line.
            ClickAt(w, RowPoint(w, ours, 2, -5));
            Assert.Equal("a\nO2\n\nc", result.Document.Text);

            // Hover the theirs conflict: the floating button offers `Select Left`, a click takes the chunk.
            w.MouseMove(RowPoint(w, theirs, 1, 50));
            Settle(w);
            var buttons = w.FindControl<Border>("TheirsButtons")!;
            var chunk = w.FindControl<Button>("TheirsChunkButton")!;
            Assert.True(buttons.IsVisible);
            Assert.Equal("Select Left", chunk.Content);
            Click(w, chunk);
            Assert.Equal("a\nO2\nT\nc", result.Document.Text);
            Assert.Equal("Remove Left", chunk.Content);
            Assert.True(w.FindControl<Button>("ResolveButton")!.IsEnabled);

            // Typing at the end of the T row edits it; Enter splits the line and the side panes get a row.
            result.TextArea.Focus();
            result.CaretOffset = "a\nO2\nT".Length;
            w.KeyTextInput("X");
            Settle(w);
            Assert.Equal("a\nO2\nTX\nc", result.Document.Text);
            w.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            w.KeyTextInput("Y");
            Settle(w);
            Assert.Equal("a\nO2\nTX\nY\nc", result.Document.Text);
            Assert.Equal("a\nO1\nO2\n\nc", ours.Document.Text);
            Assert.Equal("a\nO2\nTX\nY\nc\n", vm.Document!.BuildResult());

            // Deselecting both sources keeps the inserted line Y (it has no source); the chunk's second row is filler.
            vm.ToggleLine(Row(vm.Layout.Ours, "O2"));
            vm.ToggleChunkSide(1, MergeSide.Theirs);
            Settle(w);
            Assert.Equal("a\nY\n\nc", result.Document.Text);

            // Protected: typing into the filler row beeps and is undone; the caret stays.
            result.CaretOffset = "a\nY\n".Length;
            w.KeyTextInput("Z");
            Settle(w);
            Assert.Equal(1, _beeps);
            Assert.Equal("a\nY\n\nc", result.Document.Text);
            Assert.Equal("a\nY\n".Length, result.CaretOffset);
            w.KeyPress(Key.Back, RawInputModifiers.None, PhysicalKey.Backspace, null); // joins filler into Y: protected
            Settle(w);
            Assert.Equal(2, _beeps);
            Assert.Equal("a\nY\n\nc", result.Document.Text);
        }
        finally { w.Close(); }
    }

    [AvaloniaFact]
    public async Task Window_binary_mode_resolves_with_theirs()
    {
        MakeBinaryConflict(_repo);
        var (vm, w) = await ShowAsync("b.bin");
        Assert.False(w.FindControl<Grid>("EditorGrid")!.IsVisible);
        Assert.True(w.FindControl<Grid>("BinaryView")!.IsVisible);
        Assert.False(w.FindControl<ToggleButton>("OrientationToggle")!.IsVisible);
        var resolve = w.FindControl<Button>("ResolveButton")!;
        Assert.False(resolve.IsEnabled);
        Click(w, w.FindControl<Button>("TheirsBox")!);
        Assert.Contains("checked", w.FindControl<Button>("TheirsBox")!.Classes);
        Assert.True(resolve.IsEnabled);
        Click(w, resolve);
        Assert.True(await vm.Resolving);
        Assert.False(w.IsVisible);
        Assert.Equal([0, 9, 9], File.ReadAllBytes(Path.Combine(_repo.Path, "b.bin")));
        AssertStaged("b.bin");
    }
}
