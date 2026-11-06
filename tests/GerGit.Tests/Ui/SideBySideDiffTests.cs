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
using GerGit.Core;
using GerGit.Core.Diff;
using GerGit.Core.Git;
using GerGit.ViewModels;
using GerGit.Views;

namespace GerGit.Tests.Ui;

/// <summary>Side-by-side layout: two panes, layout per place, synced scrolling, navigation and
/// line staging in both panes.</summary>
public sealed class SideBySideDiffTests : IDisposable
{
    readonly TempRepo _repo = new();
    readonly List<(PatchAction Action, HunkLine[] Lines)> _calls = [];
    // Each test its own place key: tests of other classes run at the same time and use the real places.
    readonly string _key = "test.diffLayout." + Guid.NewGuid().ToString("N");

    static readonly DiffRequest Unstaged = new(null, "", "f.txt", null, Working: WorkingDiff.Unstaged);

    public SideBySideDiffTests()
    {
        var lines = Enumerable.Range(1, 60).Select(i => $"line {i}").ToList();
        lines[12] = "thirteen " + new string('y', 150); // a long context line: both panes can scroll sideways
        _repo.Commit("init", 0, ("f.txt", string.Join("\n", lines) + "\n"));
        lines[4] = "five";
        lines.Insert(5, "five and a half");
        lines[12] = "twelve " + new string('x', 400); // line 12, after the insert at index 12
        lines.RemoveAt(40);                            // "line 40": deletion only
        _repo.Write("f.txt", string.Join("\n", lines) + "\n");
        // Rows of the side-by-side documents (context 3):
        //  0 @@ | 1-3 line 2..4 | 4 old "line 5" / new "five" | 5 alignment / "five and a half" | 6-11 line 6..11
        //  12 "line 12" / "twelve xxx…" | 13-15 line 13..15 | 16 @@ | 17-19 line 37..39 | 20 "line 40" / alignment | 21-23 line 41..43
    }

    public void Dispose()
    {
        Settings.Set<string?>(_key, null);
        _repo.Dispose();
    }

    async Task<(DiffViewModel Vm, Window Window, DiffView View)> OpenAsync(bool sideBySide = true, double height = 600,
        LineActionSet actions = LineActionSet.StageDiscard, DiffRequest? request = null)
    {
        Settings.Set(_key, sideBySide ? "parallel" : "unified");
        var vm = new DiffViewModel(await GitRepository.OpenAsync(_repo.Path))
        {
            LayoutKey = _key,
            LineActions = actions,
            LineActionHandler = (a, l) => { _calls.Add((a, l.ToArray())); return Task.CompletedTask; },
        };
        var view = new DiffView { DataContext = vm };
        var window = new Window { Width = 1000, Height = height, Content = view };
        window.Show();
        vm.Show(request ?? Unstaged);
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

    static TextEditor Pane(DiffView v, string name) => v.GetVisualDescendants().OfType<TextEditor>().Single(e => e.Name == name);
    static TextEditor Left(DiffView v) => Pane(v, "OldEditor");
    static TextEditor Right(DiffView v) => Pane(v, "Editor");
    static ScrollViewer Scroller(TextEditor e) => e.GetVisualDescendants().OfType<ScrollViewer>().First();
    static double TopLine(TextEditor e) => e.TextArea.TextView.ScrollOffset.Y / e.TextArea.TextView.DefaultLineHeight;

    static void HoverLine(Window w, TextEditor pane, int line)
    {
        var tv = pane.TextArea.TextView;
        var y = tv.GetVisualTopByDocumentLine(line + 1) - tv.ScrollOffset.Y + tv.DefaultLineHeight / 2;
        w.MouseMove(tv.TranslatePoint(new Point(100, y), w)!.Value);
        Settle(w);
    }

    static void Click(Window w, Control c)
    {
        var at = c.TranslatePoint(new Point(c.Bounds.Width / 2, c.Bounds.Height / 2), w)!.Value;
        w.MouseMove(at);
        w.MouseDown(at, MouseButton.Left);
        w.MouseUp(at, MouseButton.Left);
        Settle(w);
    }

    [AvaloniaFact]
    public async Task Toggle_switches_to_two_equal_panes_with_their_own_numbers_and_back()
    {
        var (vm, window, view) = await OpenAsync(sideBySide: false);
        try
        {
            var toggle = view.FindControl<ToggleButton>("LayoutButton")!;
            Assert.True(toggle.IsEffectivelyVisible);
            Assert.Equal("Side by side diff", ToolTip.GetTip(toggle));
            Assert.Equal("f.txt", ToolTip.GetTip(view.FindControl<TextBlock>("PathText")!)); // shown only when trimmed
            Assert.False(TrimmedToolTip.IsTrimmed(view.FindControl<TextBlock>("PathText")!));
            Assert.False(Left(view).IsVisible);
            Assert.Equal(2, Grid.GetColumnSpan(Right(view)));
            int unifiedLines = Right(view).Document.LineCount;

            Click(window, toggle);
            Assert.True(vm.SideBySide);
            Assert.Equal("Split diff", ToolTip.GetTip(toggle));
            Assert.True(Left(view).IsEffectivelyVisible);
            Assert.Equal((1, 1), (Grid.GetColumn(Right(view)), Grid.GetColumnSpan(Right(view))));
            Assert.Equal(Left(view).Bounds.Width, Right(view).Bounds.Width, 1); // equal widths
            Assert.Equal(Left(view).Document.LineCount, Right(view).Document.LineCount);
            Assert.Equal(24, Right(view).Document.LineCount);
            Assert.True(unifiedLines > 24);
            Assert.Equal("line 5", Left(view).Document.GetText(Left(view).Document.GetLineByNumber(5)));
            Assert.Equal("five", Right(view).Document.GetText(Right(view).Document.GetLineByNumber(5)));
            Assert.Equal("", Left(view).Document.GetText(Left(view).Document.GetLineByNumber(6))); // alignment line
            Assert.Equal(DocLineKind.Alignment, vm.Sides!.Old.Lines[5].Kind);
            Assert.Equal(DocLineKind.Alignment, vm.Sides.New.Lines[20].Kind);
            // Only the right pane has a vertical scrollbar; the gutters show one number column each.
            Assert.Equal(ScrollBarVisibility.Hidden, Left(view).VerticalScrollBarVisibility);
            Assert.Equal(NumberColumns.Old, Left(view).TextArea.LeftMargins.OfType<DiffLineNumberMargin>().Single().Columns);
            Assert.Equal(NumberColumns.New, Right(view).TextArea.LeftMargins.OfType<DiffLineNumberMargin>().Single().Columns);

            Click(window, toggle);
            Assert.False(vm.SideBySide);
            Assert.False(Left(view).IsVisible);
            Assert.Equal(unifiedLines, Right(view).Document.LineCount);
            Assert.Equal(NumberColumns.Both, Right(view).TextArea.LeftMargins.OfType<DiffLineNumberMargin>().Single().Columns);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task The_layout_is_stored_per_place()
    {
        var (vm, window, _) = await OpenAsync(sideBySide: false);
        try
        {
            vm.SideBySide = true;
            Assert.Equal("parallel", Settings.Get(_key, ""));
        }
        finally
        {
            window.Close();
        }

        // Another viewer of the same place opens side by side; another place keeps its own (default unified) layout.
        var repo = await GitRepository.OpenAsync(_repo.Path);
        var same = new DiffViewModel(repo) { LayoutKey = _key };
        var other = new DiffViewModel(repo) { LayoutKey = _key + ".other" };
        same.Show(Unstaged);
        other.Show(Unstaged);
        await Task.WhenAll(same.Loading, other.Loading);
        Assert.NotNull(same.Sides);
        Assert.Null(other.Sides);
        Assert.False(other.SideBySide);

        // The places of the app; Blame has no layout button.
        using var tab = new RepoTabViewModel(repo);
        Assert.Equal("views.diffLayout.revision", tab.Details.Diff.LayoutKey);
        Assert.Equal("views.diffLayout.commit", tab.LocalChanges.Diff.LayoutKey);
        Assert.Equal("views.diffLayout.history", new FileHistoryViewModel(tab, "f.txt", false, null).Diff.LayoutKey);
        Assert.False(new BlameViewModel(tab, "f.txt", null).Diff.ShowLayoutToggle);
    }

    [AvaloniaFact]
    public async Task Panes_scroll_together_and_a_shorter_pane_does_not_pull_the_other_back()
    {
        var (_, window, view) = await OpenAsync(height: 200);
        try
        {
            var (left, right) = (Left(view), Right(view));

            Scroller(right).Offset = new Vector(0, 3 * right.TextArea.TextView.DefaultLineHeight);
            Settle(window);
            Assert.Equal(3, TopLine(left), 2);
            Assert.Equal(right.TextArea.TextView.ScrollOffset, left.TextArea.TextView.ScrollOffset);

            Scroller(left).Offset = new Vector(0, 7 * left.TextArea.TextView.DefaultLineHeight); // the wheel over the left pane
            Settle(window);
            Assert.Equal(7, TopLine(right), 2);

            // Horizontal: the right pane has the longer line. The left pane stops at its own end, and its echo does not
            // pull the right pane back.
            double wanted = Scroller(right).Extent.Width - Scroller(right).Viewport.Width;
            double leftMax = Scroller(left).Extent.Width - Scroller(left).Viewport.Width;
            Assert.True(leftMax > 0 && wanted > leftMax + 100);
            Scroller(right).Offset = new Vector(wanted, Scroller(right).Offset.Y);
            Settle(window);
            Assert.Equal(wanted, right.TextArea.TextView.ScrollOffset.X, 1);
            Assert.Equal(leftMax, left.TextArea.TextView.ScrollOffset.X, 1);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Next_and_previous_change_use_the_right_pane_and_the_left_follows()
    {
        var (_, window, view) = await OpenAsync(height: 100);
        try
        {
            var (left, right) = (Left(view), Right(view));
            // Navigation groups of the right pane (alignment lines count as changes): rows 2 (line 3 before "five") and 10
            // (two lines before row 12), 18 (two lines before the deleted "line 40", an alignment row on the right).
            view.GoToChange(next: true);
            Settle(window);
            Assert.Equal(10, TopLine(right), 1);
            Assert.Equal(10, TopLine(left), 1);
            view.GoToChange(next: true);
            Settle(window);
            Assert.Equal(18, TopLine(right), 1);
            Assert.Equal(18, TopLine(left), 1);
            view.GoToChange(next: false);
            Settle(window);
            Assert.Equal(10, TopLine(right), 1);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Hover_outlines_a_group_in_both_panes_buttons_go_right_and_act_on_both_sides()
    {
        var (vm, window, view) = await OpenAsync();
        try
        {
            var (left, right) = (Left(view), Right(view));
            var buttons = view.FindControl<Border>("LineButtons")!;

            HoverLine(window, left, 4); // over the deleted "line 5" in the left pane
            Assert.True(buttons.IsVisible);
            // Over the right (new) pane: the container's right edge is 20 px from the right pane's text view.
            var rightTv = right.TextArea.TextView;
            double rightEdge = rightTv.TranslatePoint(new Point(rightTv.Bounds.Width, 0), view)!.Value.X;
            double buttonsRight = buttons.TranslatePoint(new Point(buttons.Bounds.Width, 0), view)!.Value.X;
            Assert.Equal(rightEdge - 20, buttonsRight, 1);

            Click(window, view.FindControl<Button>("StageButton")!);
            var call = Assert.Single(_calls);
            Assert.Equal(PatchAction.Stage, call.Action);
            var hunk = vm.FileDiff!.Hunks[0].Lines;
            Assert.Equal(["five", "five and a half", "line 5"], call.Lines.Select(l => hunk[l.Line].Text).Order(StringComparer.Ordinal));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task A_selection_acts_on_its_own_pane_only_and_clears_the_other_pane()
    {
        var (vm, window, view) = await OpenAsync();
        try
        {
            var (left, right) = (Left(view), Right(view));
            var buttons = view.FindControl<Border>("LineButtons")!;

            right.TextArea.Selection = AvaloniaEdit.Editing.Selection.Create(right.TextArea, Offset(right, 4), Offset(right, 6) + 2);
            left.TextArea.Selection = AvaloniaEdit.Editing.Selection.Create(left.TextArea, Offset(left, 4), Offset(left, 4) + 3);
            HoverLine(window, left, 4);
            Assert.True(right.TextArea.Selection.IsEmpty); // selecting in one pane clears the other
            Assert.True(buttons.IsVisible);
            // Next to the selection in the left pane.
            var leftTv = left.TextArea.TextView;
            double leftEdge = leftTv.TranslatePoint(new Point(leftTv.Bounds.Width, 0), view)!.Value.X;
            Assert.Equal(leftEdge - 20, buttons.TranslatePoint(new Point(buttons.Bounds.Width, 0), view)!.Value.X, 1);

            // Keys with a selection in the left pane: only its deleted line.
            left.TextArea.Focus();
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Settle(window);
            var call = Assert.Single(_calls);
            Assert.Equal(PatchAction.Stage, call.Action);
            Assert.Equal(["line 5"], call.Lines.Select(l => vm.FileDiff!.Hunks[l.Hunk].Lines[l.Line].Text));
        }
        finally
        {
            window.Close();
        }
    }
    [AvaloniaFact]
    public async Task Alignment_rows_alone_act_on_nothing_and_Delete_in_the_left_pane_discards_its_deleted_lines()
    {
        var (vm, window, view) = await OpenAsync();
        try
        {
            var left = Left(view);
            left.TextArea.Focus();
            // Row 5 of the left pane is an alignment row (across from "five and a half").
            left.TextArea.Selection = AvaloniaEdit.Editing.Selection.Create(left.TextArea, Offset(left, 5), Offset(left, 6));
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
            Settle(window);
            Assert.Empty(_calls);

            left.TextArea.Selection = AvaloniaEdit.Editing.Selection.Create(left.TextArea, Offset(left, 4), Offset(left, 6));
            window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
            Settle(window);
            var call = Assert.Single(_calls);
            Assert.Equal(PatchAction.Discard, call.Action);
            Assert.Equal(["line 5"], call.Lines.Select(l => vm.FileDiff!.Hunks[l.Hunk].Lines[l.Line].Text));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Unstage_in_side_by_side_acts_on_both_sides_of_the_hovered_group()
    {
        _repo.Git("add", "f.txt");
        var (vm, window, view) = await OpenAsync(actions: LineActionSet.Unstage,
            request: new DiffRequest(null, "", "f.txt", null, Working: WorkingDiff.Staged));
        try
        {
            HoverLine(window, Right(view), 4);
            var unstage = view.FindControl<Button>("UnstageButton")!;
            Assert.True(unstage.IsEffectivelyVisible);
            Assert.False(view.FindControl<Button>("StageButton")!.IsVisible);
            Click(window, unstage);
            var call = Assert.Single(_calls);
            Assert.Equal(PatchAction.Unstage, call.Action);
            Assert.Equal(["five", "five and a half", "line 5"],
                call.Lines.Select(l => vm.FileDiff!.Hunks[l.Hunk].Lines[l.Line].Text).Order(StringComparer.Ordinal));
        }
        finally
        {
            window.Close();
        }
    }

    const string NoNewlineFixture = """
        diff --git gergitsrc/a gergitdst/a
        --- gergitsrc/a
        +++ gergitdst/a
        @@ -1,12 +1,13 @@
         c1
        -r1
        +a1
        +a2
         c2
         c3
         c4
         c5
        -r2
         c6
         c7
         c8
         c9
        +a3
         c10
        @@ -20,2 +20,2 @@
         c20
        -last
        \ No newline at end of file
        +last!
        \ No newline at end of file

        """;

    /// <summary>A hovered group acts on the same lines in both layouts, so every action builds the same patch.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Side_by_side_groups_build_the_same_patches_as_unified_groups(bool entireFile)
    {
        var diff = DiffParser.Parse(NoNewlineFixture);
        var unified = DiffDocumentBuilder.Build(diff, entireFile);
        var sides = DiffDocumentBuilder.SideBySide(unified);
        var unifiedGroups = DiffViewModel.StagingGroups(unified);
        var splitGroups = DiffViewModel.StagingGroups(sides.New);
        Assert.Equal(4, unifiedGroups.Count);
        Assert.Equal(unifiedGroups.Count, splitGroups.Count);
        for (int g = 0; g < unifiedGroups.Count; g++)
        {
            var (uf, ul) = unifiedGroups[g];
            var (sf, sl) = splitGroups[g];
            var unifiedLines = DiffViewModel.ChangedLines(unified, uf, ul);
            var splitLines = DiffViewModel.ChangedLines(sides.Old, sf, sl).Concat(DiffViewModel.ChangedLines(sides.New, sf, sl)).ToList();
            Assert.Equal(unifiedLines.ToHashSet(), splitLines.ToHashSet());
            foreach (var action in new[] { PatchAction.Stage, PatchAction.Unstage, PatchAction.Discard })
            {
                var patch = PatchBuilder.Build(diff, splitLines, action);
                Assert.NotNull(patch);
                Assert.Equal(PatchBuilder.Build(diff, unifiedLines, action), patch);
            }
        }
        Assert.Contains("\\ No newline at end of file", PatchBuilder.Build(diff, DiffViewModel.ChangedLines(sides.New, splitGroups[3].First, splitGroups[3].Last), PatchAction.Stage));
    }

    static int Offset(TextEditor e, int line) => e.Document.GetLineByNumber(line + 1).Offset;
}
