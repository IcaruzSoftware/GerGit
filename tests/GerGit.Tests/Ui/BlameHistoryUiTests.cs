using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using GerGit.Core;
using GerGit.Core.Git;
using GerGit.Tests.Core;
using GerGit.ViewModels;
using GerGit.ViewModels.Dialogs;
using GerGit.Views;

namespace GerGit.Tests.Ui;

/// <summary>Blame and File History windows, opened from the Local Changes menus.</summary>
public sealed class BlameHistoryUiTests : IDisposable
{
    readonly TempRepo _repo = new();
    string _c1 = "", _c2 = "", _c3 = "";

    public void Dispose() => _repo.Dispose();

    /// <summary>Ann adds f.txt, Bob edits it, Cid renames it to g.txt and edits it; g.txt is modified in the working tree.</summary>
    void ThreeCommits()
    {
        _c1 = BlameHistoryTests.Commit(_repo, "one", "Ann", 0, ("f.txt", "a\nb\nc\n"));
        _c2 = BlameHistoryTests.Commit(_repo, "two", "Bob", 10, ("f.txt", "a\nB\nc\nd\n"));
        _repo.Git("mv", "f.txt", "g.txt");
        _c3 = BlameHistoryTests.Commit(_repo, "three", "Cid", 20, ("g.txt", "A\nB\nc\nd\ne\n"));
        _repo.Write("g.txt", "A\nB\nc\nd\ne\nwork\n");
    }

    sealed record Ui(MainWindow Window, RepoTabViewModel Tab, LocalChangesView View)
    {
        public ListBox Unstaged => View.FindControl<ListBox>("UnstagedList")!;
    }

    async Task<Ui> OpenAsync()
    {
        var main = new MainWindowViewModel();
        var window = new MainWindow { DataContext = main };
        window.Show();
        Assert.Null(await main.OpenAsync(_repo.Path));
        var tab = main.SelectedTab!;
        tab.IsActive = false; // no watcher refreshes in between
        Settle(window);
        window.GetVisualDescendants().OfType<RepoTabView>().Single().IsVisible = true;
        tab.ShowLocalChanges();
        await Task.Delay(300);
        Settle(window);
        return new Ui(window, tab, window.GetVisualDescendants().OfType<LocalChangesView>().Single());
    }

    static void Settle(TopLevel w)
    {
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Right-clicks the row of <paramref name="path"/> in the Unstaged list and clicks the menu item.</summary>
    static MenuItem MenuItemOf(Ui ui, string path, string header)
    {
        var rows = ui.Tab.LocalChanges.UnstagedRows.Select(r => r.Path).ToList();
        int i = rows.IndexOf(path);
        ui.Unstaged.SelectedIndex = i;
        Settle(ui.Window);
        var row = ui.Unstaged.ContainerFromIndex(i)!;
        var at = row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), ui.Window)!.Value;
        ui.Window.MouseDown(at, MouseButton.Right);
        ui.Window.MouseUp(at, MouseButton.Right);
        Settle(ui.Window);
        Assert.True(ui.Unstaged.ContextMenu!.IsOpen);
        return ui.Unstaged.ContextMenu.Items.OfType<MenuItem>().Single(m => (string?)m.Header == header);
    }

    static async Task UntilAsync(Func<bool> done)
    {
        for (int i = 0; i < 300 && !done(); i++)
        {
            await Task.Delay(20);
            Dispatcher.UIThread.RunJobs();
        }
        Assert.True(done());
    }

    static string Authors(BlameViewModel vm) => string.Join(",", vm.Rows.Select(r => (r.IsGroupStart ? "" : "_") + r.Commit?.AuthorName));

    [AvaloniaFact]
    public async Task Blame_window_from_the_menu_shows_lines_authors_and_steps_through_commits()
    {
        ThreeCommits();
        var ui = await OpenAsync();
        var item = MenuItemOf(ui, "g.txt", "Blame/Timeline...");
        Assert.True(item.IsEnabled);
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        var w = BlameWindow.Instances.Last();
        var vm = w.Vm;
        await vm.Loading;
        Settle(w);

        Assert.Equal("g.txt - Blame", w.Title);
        Assert.Equal([_c3, _c2, _c1], vm.Revisions.Select(r => r.Sha));
        Assert.Equal(_c3, vm.SelectedRevision!.Sha);
        // c3 as a diff over the whole file: -a +A B c d +e; deleted and context lines by the parent's blame.
        var editor = w.FindControl<DiffView>("DiffPane")!.GetVisualDescendants().OfType<TextEditor>().Single(e => e.Name == "Editor");
        Assert.Equal("a\nA\nB\nc\nd\ne", editor.Document.Text);
        Assert.Equal("Ann,Cid,Bob,Ann,Bob,Cid", Authors(vm));
        Assert.Equal([vm.SelectedRevision.Commit.AuthorDate], vm.SelectedDates);
        Assert.Null(vm.ErrorText);
        Assert.False(vm.GoBackCommand.CanExecute(null));

        // The SHA button navigates to Bob's commit (path f.txt there): a -b +B c +d.
        vm.Navigate(_c2);
        await vm.Loading;
        Settle(w);
        Assert.Equal("a\nb\nB\nc\nd", editor.Document.Text);
        Assert.Equal("Ann,_Ann,Bob,Ann,Bob", Authors(vm));

        // Alt+← back to c3, Alt+→ forward again.
        w.KeyPress(Key.Left, RawInputModifiers.Alt, PhysicalKey.None, null);
        Assert.Equal(_c3, vm.SelectedRevision!.Sha);
        Assert.True(vm.GoForwardCommand.CanExecute(null));
        w.KeyPress(Key.Right, RawInputModifiers.Alt, PhysicalKey.None, null);
        Assert.Equal(_c2, vm.SelectedRevision!.Sha);
        Assert.False(vm.GoForwardCommand.CanExecute(null));
        await vm.Loading;

        // Root commit: everything added by Ann (blame of c1~ is empty, no error).
        vm.SelectedRevision = vm.Revisions[2];
        await vm.Loading;
        Assert.Equal("Ann,_Ann,_Ann", Authors(vm));

        // Fast switching without the delay: the cancelled load of c3 must not overwrite c1's rows.
        vm.Delay = TimeSpan.Zero;
        vm.SelectedRevision = vm.Revisions[0];
        vm.SelectedRevision = vm.Revisions[2];
        await vm.Loading;
        await Task.Delay(300);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Ann,_Ann,_Ann", Authors(vm));
        Assert.Equal("a\nb\nc", editor.Document.Text);
        Assert.False(vm.IsLoading);

        // Row menu.
        var copied = new List<string>();
        vm.Copy = s => { copied.Add(s); return Task.CompletedTask; };
        var menu = vm.Menu(vm.Rows[0].Commit!);
        Assert.Equal("Reveal in GerGit|-|Save as...|-|Copy Commit SHA|Copy Commit Info", string.Join("|", menu.Select(e => e.ToString())));
        await menu.Single(e => e.Header == "Copy Commit Info").Invoke();
        Assert.Equal([$"{_c1[..7]} - one"], copied);
        await menu.Single(e => e.Header == "Reveal in GerGit").Invoke();
        Assert.True(ui.Tab.IsAllCommitsView);

        w.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);
        Assert.DoesNotContain(w, BlameWindow.Instances);
        Assert.True(vm.Diff.IsEmpty); // closing clears the diff viewer (and frees its bitmaps)
        ui.Window.Close();
    }

    [Fact]
    public void A_history_entry_without_files_takes_the_path_the_newer_entry_had_before()
    {
        CommitHeader C(string sha) => new(sha, "Ann", "a@x", DateTimeOffset.UnixEpoch, sha, false);
        var items = RevisionItem.List([
            new(C("c4"), [new FileChange(ChangeKind.Modified, "g.txt", null)]),
            new(C("c3"), [new FileChange(ChangeKind.Renamed, "g.txt", "f.txt")]),
            new(C("merge"), []),
            new(C("c1"), [new FileChange(ChangeKind.Added, "f.txt", null)])], "g.txt");
        Assert.Equal(["g.txt", "g.txt", "f.txt", "f.txt"], items.Select(i => i.Path));
        Assert.Equal(ChangeKind.Modified, items[2].Kind);
    }

    [AvaloniaFact]
    public async Task Go_to_line_scrolls_the_file_view_and_binary_files_show_the_text_only_message()
    {
        var lines = string.Concat(Enumerable.Range(1, 300).Select(i => $"line {i}\n"));
        BlameHistoryTests.Commit(_repo, "long", "Ann", 0, ("long.txt", lines), ("bin.dat", "x\0y\0z\0\0\0\0\0\0"));
        _repo.Write("long.txt", lines + "more\n");
        var ui = await OpenAsync();
        var w = BlameWindow.Open(ui.Tab, "long.txt", null);
        await w.Vm.Loading;
        Settle(w);
        var editor = w.FindControl<DiffView>("DiffPane")!.GetVisualDescendants().OfType<TextEditor>().Single(e => e.Name == "Editor");
        Assert.Equal(14, editor.FontSize);
        // Entire file, fixed: its toggle and the context buttons are hidden through ShowContextControls.
        Assert.False(w.Vm.Diff.ShowContextControls);
        var hidden = w.FindControl<DiffView>("DiffPane")!.GetVisualDescendants().OfType<Control>()
            .Where(c => ToolTip.GetTip(c) is "Show entire file" or "Decrease number of visible lines" or "Increase number of visible lines").ToList();
        Assert.Equal(3, hidden.Count);
        Assert.All(hidden, c => Assert.False(c.IsVisible));
        Assert.Equal(300, w.Vm.Rows.Count);

        string? asked = null;
        w.Vm.ShowDialog = async d =>
        {
            asked = d.Title;
            ((GoToLineViewModel)d).Text = "120";
            await d.SubmitAsync();
            return d.Result == true;
        };
        w.KeyPress(Key.G, RawInputModifiers.Control, PhysicalKey.None, null);
        await Task.Delay(50);
        Settle(w);
        Assert.Equal("Go To Line", asked);
        var tv = editor.TextArea.TextView;
        Assert.Equal(tv.GetVisualTopByDocumentLine(120), tv.ScrollOffset.Y, 1);
        Assert.Equal(120, editor.TextArea.Caret.Line);

        // Not a number: nothing happens.
        w.Vm.ShowDialog = async d => { ((GoToLineViewModel)d).Text = "abc"; await d.SubmitAsync(); return true; };
        await w.Vm.GoToLineAsync();
        Assert.Equal(120, editor.TextArea.Caret.Line);
        w.Close();

        var b = BlameWindow.Open(ui.Tab, "bin.dat", null);
        await b.Vm.Loading;
        Assert.Equal(BlameViewModel.TextOnly, b.Vm.ErrorText);
        Assert.Empty(b.Vm.Rows);
        b.Close();

        // Closed while the history is read: no revision load starts, and the load still ends (it awaited itself before).
        var c = BlameWindow.Open(ui.Tab, "long.txt", null);
        c.Close();
        await c.Vm.Loading.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(c.Vm.Diff.IsEmpty);
        Assert.Empty(c.Vm.Rows);
        ui.Window.Close();
    }

    [AvaloniaFact]
    public async Task File_history_window_from_the_menu_shows_the_selected_revisions_in_the_diff_viewer()
    {
        ThreeCommits();
        var ui = await OpenAsync();
        var item = MenuItemOf(ui, "g.txt", "History...");
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        var w = FileHistoryWindow.Instances.Last();
        var vm = w.Vm;
        await vm.Loading;
        await vm.Diff.Loading;
        Settle(w);

        Assert.Equal("g.txt - History", w.Title);
        Assert.Equal("main", vm.TipName);
        Assert.Equal([_c3, _c2, _c1], vm.Rows.Select(r => r.Revision.Sha));
        Assert.Equal(["f.txt → g.txt", "f.txt", "f.txt"], vm.Rows.Select(r => r.FilePath));
        Assert.Equal([0], vm.Selection.SelectedIndexes);
        var diffView = w.FindControl<DiffView>("DiffPane")!;
        Assert.Same(vm.Diff, diffView.DataContext);
        var editor = diffView.GetVisualDescendants().OfType<TextEditor>().Single(e => e.Name == "Editor");
        Assert.Contains("e", editor.Document.Text);
        Assert.Equal("f.txt", vm.Diff.OldName); // the rename shows in the header

        // One row: that commit's change.
        vm.Selection.SelectedIndex = 1;
        await vm.Diff.Loading;
        Settle(w);
        Assert.Equal(DiffState.Text, vm.Diff.State);
        Assert.Equal("f.txt", vm.Diff.Name);
        Assert.Contains("B", editor.Document.Text);
        Assert.DoesNotContain("A", editor.Document.Text);

        // Two rows: older as source, newer as destination; the timeline marks both.
        vm.Selection.Clear();
        vm.Selection.SelectRange(0, 1);
        await vm.Diff.Loading;
        Settle(w);
        Assert.Equal(2, vm.SelectedDates.Count);
        Assert.Contains("A", editor.Document.Text);
        Assert.Equal("f.txt", vm.Diff.OldName);
        Assert.Equal("g.txt", vm.Diff.Name);
        Assert.Empty(vm.Menu()); // two rows: only external diff items, none configured

        // Three rows: the overlay.
        vm.Selection.SelectRange(0, 2);
        Assert.Equal("Select two commits to see difference between them", vm.OverlayTitle);
        Assert.Equal("3 commits selected", vm.OverlayText);

        // One row: the menu; Ctrl+C copies the SHA; Reset File to State at Commit restores c3's g.txt.
        vm.Selection.Clear();
        vm.Selection.Select(0);
        Assert.Null(vm.OverlayTitle);
        var menu = vm.Menu();
        Assert.Equal("Reveal in GerGit|-|Open|Show in File Explorer|-|Reset File to State at Commit...|-|Save as...|-|Copy Commit SHA|Copy Commit Info",
            string.Join("|", menu.Select(e => e.ToString())));
        var copied = new List<string>();
        vm.Copy = s => { copied.Add(s); return Task.CompletedTask; };
        Settle(w);
        Assert.True(w.FindControl<ListBox>("HistoryList")!.ContainerFromIndex(0)!.Focus());
        w.KeyPress(Key.C, RawInputModifiers.Control, PhysicalKey.None, null);
        Assert.Equal([_c3], copied);

        string? asked = null;
        ui.Tab.Confirm = (_, _, _, _) => Task.FromResult(false); // not the main window's confirmation
        vm.Confirm = (title, _, _, _) => { asked = title; return Task.FromResult(true); };
        await menu.Single(e => e.Header == "Reset File to State at Commit...").Invoke();
        Assert.Equal("Do you want to reset the file to the state it is at the commit?", asked);
        Assert.Equal("A\nB\nc\nd\ne\n", File.ReadAllText(Path.Combine(_repo.Path, "g.txt")));
        Assert.True(ui.Tab.IsLocalChangesView);

        // Save as writes the file as of the commit.
        var target = Path.Combine(_repo.Path, "saved.txt");
        vm.PickSaveFile = (name, _) => Task.FromResult<string?>(name == "g.txt" ? target : null);
        await menu.Single(e => e.Header == "Save as...").Invoke();
        Assert.Equal("A\nB\nc\nd\ne\n", File.ReadAllText(target));

        w.Close();
        Assert.True(vm.Diff.IsEmpty); // closing clears the diff viewer (and frees its bitmaps)
        ui.Window.Close();
    }

    [AvaloniaFact]
    public async Task Folder_history_lists_commits_with_their_files()
    {
        BlameHistoryTests.Commit(_repo, "one", "Ann", 0, ("dir/a.txt", "1\n"), ("top.txt", "1\n"));
        var c2 = BlameHistoryTests.Commit(_repo, "two", "Bob", 10, ("dir/a.txt", "2\n"), ("dir/b.txt", "2\n"), ("top.txt", "2\n"));
        var ui = await OpenAsync();
        var w = FileHistoryWindow.Open(ui.Tab, "dir", true, null);
        var vm = w.Vm;
        await vm.Loading;
        await vm.Diff.Loading;

        Assert.Equal("dir - History", w.Title);
        Assert.Equal(["Commit two", "File dir/a.txt", "File dir/b.txt", "Commit one", "File dir/a.txt"],
            vm.Rows.Select(r => r.Kind + " " + (r.IsCommit ? r.Revision.Subject : r.FilePath)));
        Assert.Equal([1], vm.Selection.SelectedIndexes); // the first file entry
        Assert.Equal("a.txt", vm.Diff.Name);
        Assert.Equal(SelectionMode.Single, vm.SelectionMode);

        vm.Selection.Select(0); // a commit row: no diff, the revision menu
        Assert.Equal(DiffState.None, vm.Diff.State);
        Assert.Equal("Reveal in GerGit|-|Copy Commit SHA|Copy Commit Info", string.Join("|", vm.Menu().Select(e => e.ToString())));
        vm.Selection.Select(2); // a file row inside a folder entry: no copy items
        Assert.Equal("Reveal in GerGit|-|Open|Show in File Explorer|-|Reset File to State at Commit...|-|Save as...",
            string.Join("|", vm.Menu().Select(e => e.ToString())));
        Assert.Equal(c2, vm.SelectedRows.Single().Revision.Sha);
        w.Close();
        ui.Window.Close();
    }
}
