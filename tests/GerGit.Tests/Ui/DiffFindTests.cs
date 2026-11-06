using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using GerGit.Core;
using GerGit.Core.Git;
using GerGit.ViewModels;
using GerGit.Views;

namespace GerGit.Tests.Ui;

/// <summary>Search inside a diff (and file view): the find bar of the diff editor (one per pane).</summary>
public sealed class DiffFindTests : IDisposable
{
    static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);
    readonly TempRepo _repo = new();
    readonly string _key = "test.diffLayout." + Guid.NewGuid().ToString("N");
    readonly List<PatchAction> _calls = [];

    static readonly DiffRequest Unstaged = new(null, "", "f.txt", null, Working: WorkingDiff.Unstaged);

    public DiffFindTests()
    {
        _repo.Commit("init", 0, ("f.txt", "a\nfoo one\nb\nc\nd\n"));
        _repo.Write("f.txt", "a\nFOO two\nb\nc\nd\nfoo three\n");
        // Unified document: 0 @@ | 1 a | 2 -foo one | 3 +FOO two | 4-6 b c d | 7 +foo three
    }

    public void Dispose()
    {
        Settings.Set<string?>(_key, null);
        _repo.Dispose();
    }

    async Task<(DiffViewModel Vm, Window Window, DiffView View)> OpenAsync(bool sideBySide = false)
    {
        Settings.Set(_key, sideBySide ? "parallel" : "unified");
        var vm = new DiffViewModel(await GitRepository.OpenAsync(_repo.Path).WaitAsync(Limit))
        {
            LayoutKey = _key,
            LineActions = LineActionSet.StageDiscard,
            LineActionHandler = (a, _) => { _calls.Add(a); return Task.CompletedTask; },
        };
        var view = new DiffView { DataContext = vm };
        var window = new Window { Width = 900, Height = 500, Content = view };
        window.Show();
        vm.Show(Unstaged);
        await vm.Loading.WaitAsync(Limit);
        Settle(window);
        return (vm, window, view);
    }

    static void Settle(Window w)
    {
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    static TextEditor Pane(Visual root, string name) => root.GetVisualDescendants().OfType<TextEditor>().Single(e => e.Name == name);

    static TextBox Box(TextEditor e) => e.SearchPanel.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "PART_searchTextBox");

    static void Press(Window w, Key key, RawInputModifiers m = RawInputModifiers.None)
    {
        w.KeyPress(key, m, key switch { Key.F => PhysicalKey.F, Key.F3 => PhysicalKey.F3, Key.Enter => PhysicalKey.Enter,
            Key.Escape => PhysicalKey.Escape, Key.Delete => PhysicalKey.Delete, _ => PhysicalKey.None }, key == Key.F ? "f" : null);
        Settle(w);
    }

    static (int Line, string Text) Selected(TextEditor e) =>
        (e.Document.GetLineByOffset(e.TextArea.Selection.SurroundingSegment.Offset).LineNumber - 1, e.TextArea.Selection.GetText());

    static void Select(TextEditor e, int line, int start, int length)
    {
        int offset = e.Document.GetLineByNumber(line + 1).Offset + start;
        e.TextArea.Selection = AvaloniaEdit.Editing.Selection.Create(e.TextArea, offset, offset + length);
        e.TextArea.Caret.Offset = offset + length;
    }

    static bool Highlighting(TextEditor e) => e.TextArea.TextView.BackgroundRenderers.Any(r => r.GetType().Name == "SearchResultBackgroundRenderer");

    [AvaloniaFact]
    public async Task Ctrl_F_opens_with_the_selection_matches_ignore_case_and_Enter_and_F3_wrap_around()
    {
        var (_, window, view) = await OpenAsync();
        try
        {
            var editor = Pane(view, "Editor");
            editor.TextArea.Focus();
            Select(editor, 2, 0, 3); // "foo" of "foo one"
            Press(window, Key.F, RawInputModifiers.Control);

            var bar = editor.SearchPanel;
            Assert.False(bar.IsClosed);
            Assert.True(bar.IsEffectivelyVisible);
            Assert.Equal("foo", bar.SearchPattern);
            Assert.True(Box(editor).IsKeyboardFocusWithin);
            Assert.Equal("foo", Box(editor).SelectedText); // pre-filled and selected
            Assert.Equal("Find", Box(editor).PlaceholderText);
            Assert.True(Highlighting(editor));

            // Matches: "foo one" (2), "FOO two" (3), "foo three" (7). Next from the selection, then wrap to the first.
            Press(window, Key.Enter);
            Assert.Equal((3, "FOO"), Selected(editor));
            Press(window, Key.F3);
            Assert.Equal((7, "foo"), Selected(editor));
            Press(window, Key.Enter);
            Assert.Equal((2, "foo"), Selected(editor)); // wrapped
            Press(window, Key.Enter, RawInputModifiers.Shift);
            Assert.Equal((7, "foo"), Selected(editor)); // wrapped back
            Press(window, Key.F3, RawInputModifiers.Shift);
            Assert.Equal((3, "FOO"), Selected(editor));
            Assert.True(Box(editor).IsKeyboardFocusWithin); // the focus stays in the box

            // Typing searches at once; Esc closes, clears the highlights and gives the focus back to the editor.
            Box(editor).Text = "";
            window.KeyTextInput("thr");
            Settle(window);
            Assert.Equal((7, "thr"), Selected(editor));
            Press(window, Key.Escape);
            Assert.True(bar.IsClosed);
            Assert.False(Highlighting(editor));
            Assert.True(editor.TextArea.IsFocused);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task F3_opens_an_empty_bar_without_a_selection_and_the_buttons_navigate_and_close()
    {
        var (_, window, view) = await OpenAsync();
        try
        {
            var editor = Pane(view, "Editor");
            editor.TextArea.Focus();
            editor.SearchPanel.SearchPattern = "old text of an earlier search";
            Press(window, Key.F3);
            Assert.False(editor.SearchPanel.IsClosed);
            Assert.Equal("", editor.SearchPanel.SearchPattern); // freshly opened: empty

            window.KeyTextInput("foo");
            Settle(window);
            Assert.Equal((2, "foo"), Selected(editor)); // the first match at or after the caret
            Button Find(string name) => editor.SearchPanel.GetVisualDescendants().OfType<Button>().Single(b => b.Name == name);
            Find("FindNext").Command!.Execute(null);
            Assert.Equal((3, "FOO"), Selected(editor));
            Find("FindPrevious").Command!.Execute(null);
            Assert.Equal((2, "foo"), Selected(editor));
            Find("CloseFind").Command!.Execute(null);
            Assert.True(editor.SearchPanel.IsClosed);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Keys_typed_in_the_find_box_never_stage_or_discard_the_selected_match()
    {
        var (vm, window, view) = await OpenAsync();
        try
        {
            Assert.True(vm.ShowsLineActions);
            var editor = Pane(view, "Editor");
            editor.TextArea.Focus();
            Select(editor, 3, 0, 3);
            Press(window, Key.F, RawInputModifiers.Control);
            Press(window, Key.Enter);  // next match, not "stage the selected lines"
            Press(window, Key.Delete); // deletes in the box, not "discard"
            Assert.Empty(_calls);
            Press(window, Key.Escape);
            // Back in the editor the staging keys work again on a selection.
            Select(editor, 3, 0, 3);
            Press(window, Key.Enter);
            Assert.Equal([PatchAction.Stage], _calls);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Side_by_side_each_pane_has_its_own_bar()
    {
        var (_, window, view) = await OpenAsync(sideBySide: true);
        try
        {
            var (left, right) = (Pane(view, "OldEditor"), Pane(view, "Editor"));
            right.TextArea.Focus();
            Press(window, Key.F, RawInputModifiers.Control);
            Assert.False(right.SearchPanel.IsClosed);
            Assert.True(left.SearchPanel.IsClosed);
            window.KeyTextInput("foo");
            Settle(window);
            Assert.Equal((2, "FOO"), Selected(right)); // right pane rows: 2 "FOO two", 6 "foo three"; left 2 "foo one"

            left.TextArea.Focus();
            Select(left, 2, 4, 3); // "one"
            Press(window, Key.F, RawInputModifiers.Control);
            Assert.False(left.SearchPanel.IsClosed);
            Assert.Equal("one", left.SearchPanel.SearchPattern);
            Assert.Equal("foo", right.SearchPanel.SearchPattern);
            Press(window, Key.Escape);
            Assert.True(left.SearchPanel.IsClosed);
            Assert.False(right.SearchPanel.IsClosed); // only the left bar closed
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Ctrl_F_goes_to_the_focused_control_commit_list_search_or_diff_find_bar()
    {
        var sha = _repo.Commit("second", 10, ("f.txt", "a\nfoo two\nb\n"));
        using var tab = new RepoTabViewModel(await GitRepository.OpenAsync(_repo.Path).WaitAsync(Limit));
        await tab.CommitList.LoadAsync(await tab.Repo.ReadRefsAsync(), await tab.Repo.ReadStashesAsync()).WaitAsync(Limit);
        var view = new RepoTabView { DataContext = tab };
        var window = new Window { Width = 1200, Height = 800, Content = view };
        window.Show();
        try
        {
            await tab.Details.ShowAsync([sha]).WaitAsync(Limit);
            tab.Details.SelectedTab = DetailsViewModel.ChangesTab;
            await tab.Details.Diff.Loading.WaitAsync(Limit);
            Settle(window);
            var list = view.GetVisualDescendants().OfType<GerGit.Controls.CommitListPanel>().Single();
            var editor = Pane(view, "Editor");
            Assert.True(editor.IsEffectivelyVisible);

            // Commit list focused: its own search bar opens, the diff's stays closed.
            list.Focus();
            Press(window, Key.F, RawInputModifiers.Control);
            Assert.True(tab.CommitList.IsSearchOpen);
            Assert.True(editor.SearchPanel.IsClosed);
            Press(window, Key.Escape);
            Assert.False(tab.CommitList.IsSearchOpen);

            // Diff editor focused: the find bar opens (Ctrl+F and F3), the commit list search does not.
            editor.TextArea.Focus();
            Press(window, Key.F3);
            Assert.False(editor.SearchPanel.IsClosed);
            Assert.False(tab.CommitList.IsSearchOpen);
            Press(window, Key.Escape);
            Press(window, Key.F, RawInputModifiers.Control);
            Assert.False(editor.SearchPanel.IsClosed);
            Assert.False(tab.CommitList.IsSearchOpen);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task In_the_Blame_window_Esc_in_the_box_closes_the_bar_and_only_then_the_window()
    {
        using var tab = new RepoTabViewModel(await GitRepository.OpenAsync(_repo.Path).WaitAsync(Limit));
        var window = BlameWindow.Open(tab, "f.txt", null);
        try
        {
            await window.Vm.Loading.WaitAsync(Limit);
            Settle(window);
            var editor = Pane(window, "Editor");
            editor.TextArea.Focus();
            Press(window, Key.F, RawInputModifiers.Control);
            Assert.False(editor.SearchPanel.IsClosed);
            Assert.True(Box(editor).IsKeyboardFocusWithin);
            Press(window, Key.Escape);
            Assert.True(editor.SearchPanel.IsClosed);
            Assert.True(window.IsVisible);
            Press(window, Key.Escape); // Esc closes the Blame window
            Assert.False(window.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }
    [AvaloniaFact]
    public async Task The_stage_buttons_never_cover_the_find_bar_and_hide_while_the_pointer_is_over_it()
    {
        var (_, window, view) = await OpenAsync();
        try
        {
            var editor = Pane(view, "Editor");
            var host = view.FindControl<Grid>("EditorHost")!;
            var buttons = view.FindControl<Border>("LineButtons")!;
            var tv = editor.TextArea.TextView;
            // The pointer rests on the text, well below the bar.
            window.MouseMove(tv.TranslatePoint(new Point(100, tv.GetVisualTopByDocumentLine(6) + 2), window)!.Value);
            editor.TextArea.Focus();
            Select(editor, 2, 0, 3);
            Press(window, Key.F, RawInputModifiers.Control);
            Assert.False(editor.SearchPanel.IsClosed);

            Button Find(string name) => editor.SearchPanel.GetVisualDescendants().OfType<Button>().Single(b => b.Name == name);
            Rect InHost(Control c) => new(c.TranslatePoint(new Point(0, 0), host)!.Value, c.Bounds.Size);
            // Enter, F3, Enter: around to the match on row 2, the first line under the bar.
            Press(window, Key.Enter);
            Press(window, Key.F3);
            Press(window, Key.Enter);
            Assert.Equal((2, "foo"), Selected(editor));
            Assert.True(buttons.IsVisible);
            Assert.False(InHost(buttons).Intersects(InHost(Find("FindNext"))));
            Assert.True(InHost(buttons).Top >= InHost(editor.SearchPanel).Bottom - 0.5);

            // Over the bar: no buttons, so a click on › can never hit Stage.
            var next = Find("FindNext");
            window.MouseMove(next.TranslatePoint(new Point(next.Bounds.Width / 2, next.Bounds.Height / 2), window)!.Value);
            Settle(window);
            Assert.False(buttons.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }
}
