using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Skia;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using GerGit.Controls;
using GerGit.Core;
using GerGit.Core.Git;
using GerGit.Tests.Core;
using GerGit.ViewModels;
using GerGit.ViewModels.Dialogs;
using GerGit.Views;
using GerGit.Views.Dialogs;

namespace GerGit.Tests.Ui;

/// <summary>Local Changes view: lists, file operations, line staging through the app, commit box.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class LocalChangesTests : IDisposable
{
    readonly TempRepo _repo = new();
    readonly List<(string Title, string Text, string Primary, string Cancel)> _asked = [];
    readonly List<string> _errors = [];
    bool _answer = true;

    public void Dispose() => _repo.Dispose();

    sealed record Ui(MainWindow Window, RepoTabViewModel Tab, LocalChangesViewModel Vm, LocalChangesView View)
    {
        public ListBox Unstaged => View.FindControl<ListBox>("UnstagedList")!;
        public ListBox Staged => View.FindControl<ListBox>("StagedList")!;
        public T Find<T>(string name) where T : Control => View.FindControl<T>(name)!;
    }

    /// <param name="watch">Keep the file watcher on (refreshes caused by git's own index writes run in between).</param>
    async Task<Ui> OpenAsync(bool watch = false)
    {
        var main = new MainWindowViewModel();
        var window = new MainWindow { DataContext = main };
        window.Show();
        Assert.Null(await main.OpenAsync(_repo.Path));
        var tab = main.SelectedTab!;
        if (!watch)
        {
            tab.IsActive = false; // no watcher refreshes in between; the tab view stays shown
            Settle(window);
            window.GetVisualDescendants().OfType<RepoTabView>().Single().IsVisible = true;
        }
        tab.ShowError = (_, text) => { _errors.Add(text); return Task.CompletedTask; };
        var vm = tab.LocalChanges;
        vm.Ask = (title, text, primary, cancel) => { _asked.Add((title, text, primary, cancel)); return Task.FromResult(_answer); };
        tab.ShowLocalChanges();
        // A watcher refresh posted by the first status read (it may rewrite the index) must be over before the test acts.
        await Task.Delay(400);
        await vm.Diff.Loading;
        Settle(window);
        var view = window.GetVisualDescendants().OfType<LocalChangesView>().Single();
        return new Ui(window, tab, vm, view);
    }

    static void Settle(Window w)
    {
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Waits until the running job (started by a key or click) finished and the diff reloaded.</summary>
    static async Task DoneAsync(Ui ui)
    {
        for (int i = 0; i < 500 && ui.Vm.Busy; i++) await Task.Delay(20);
        Assert.False(ui.Vm.Busy);
        await ui.Vm.Diff.Loading;
        Settle(ui.Window);
    }

    static string[] Rows(IEnumerable<FileRow> rows) => rows.Select(r => new string(' ', r.Depth) + r.Text).ToArray();
    // ListBox.SelectedItems is null with a custom selection model: read the view model's.
    static string[] Selected(ListBox l)
    {
        var vm = (LocalChangesViewModel)l.DataContext!;
        var sel = l.Name == "StagedList" ? vm.StagedSelection : vm.UnstagedSelection;
        return sel.SelectedItems.OfType<FileRow>().Select(r => r.Path).ToArray();
    }

    static void Press(Ui ui, Key key, RawInputModifiers m = RawInputModifiers.None) =>
        ui.Window.KeyPress(key, m, PhysicalKey.None, null);

    /// <summary>Selects the row with that path (and focuses it), like a click.</summary>
    static void Select(Ui ui, ListBox list, string path)
    {
        var rows = list == ui.Unstaged ? ui.Vm.UnstagedRows : ui.Vm.StagedRows;
        int i = rows.Select((r, k) => (r, k)).Single(x => x.r.Path == path).k;
        list.SelectedIndex = i;
        Settle(ui.Window);
        list.ContainerFromIndex(i)!.Focus();
        Settle(ui.Window);
    }

    static void Click(Window w, Control c, MouseButton button = MouseButton.Left, RawInputModifiers m = RawInputModifiers.None)
    {
        var at = c.TranslatePoint(new Point(c.Bounds.Width / 2, c.Bounds.Height / 2), w)!.Value;
        w.MouseMove(at, m);
        w.MouseDown(at, button, m);
        w.MouseUp(at, button, m);
        Settle(w);
    }

    string Git(params string[] args) => _repo.Git(args);
    string Work(string path) => File.ReadAllText(Path.Combine(_repo.Path, path));

    void MixedRepo()
    {
        var twenty = string.Join("\n", Enumerable.Range(1, 20).Select(i => $"line {i}")) + "\n";
        _repo.Commit("init", 1, ("a.txt", "a\n"), ("d.txt", "d\n"), ("m.txt", "m\n"), ("r.txt", twenty), ("sub/s.txt", "s\n"));
        _repo.Write("a.txt", "a2\n");                 // modified
        _repo.Write("u.txt", "u\n");                  // untracked
        _repo.Write("n.txt", "n\n");
        Git("add", "n.txt");                          // staged new
        File.Delete(Path.Combine(_repo.Path, "d.txt")); // deleted
        Git("mv", "r.txt", "r2.txt");                 // renamed (staged)
        _repo.Write("m.txt", "m2\n");
        Git("add", "m.txt");
        _repo.Write("m.txt", "m3\n");                 // MM: in both lists
        _repo.Write("sub/s.txt", "s2\n");             // file in a subfolder
    }

    [AvaloniaFact]
    public async Task Lists_show_tree_and_list_rows_icons_and_the_first_file_selected()
    {
        MixedRepo();
        var ui = await OpenAsync();
        var vm = ui.Vm;

        // Tree (default): folders first at every level, then files by name; no counts in the headers.
        Assert.Equal(["sub", " s.txt", "a.txt", "d.txt", "m.txt", "u.txt"], Rows(vm.UnstagedRows));
        Assert.Equal(["m.txt", "n.txt", "r2.txt"], Rows(vm.StagedRows));
        Assert.Equal([ChangeKind.Modified, ChangeKind.Modified, ChangeKind.Deleted, ChangeKind.Modified, ChangeKind.Untracked],
            vm.UnstagedRows.Where(r => !r.IsFolder).Select(r => r.Kind));
        Assert.Equal([ChangeKind.Modified, ChangeKind.Added, ChangeKind.Renamed], vm.StagedRows.Select(r => r.Kind));
        Assert.Equal("Old:\tr.txt\nNew:\tr2.txt", vm.StagedRows[2].ToolTip);
        Assert.Equal(new Thickness(10, 0, 0, 0), vm.UnstagedRows[1].Indent);
        Assert.Equal(["folder", "file"], vm.UnstagedRows.Take(2).Select(r => r.FileIcon));
        var texts = ui.View.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
        Assert.Contains("Unstaged", texts);
        Assert.Contains("Staged", texts);
        Assert.DoesNotContain(texts, t => t is not null && t.StartsWith("Unstaged ("));

        // Status icons: untracked = added icon, conflicted = amber triangle.
        Assert.Equal(["+", "+", "+", "M", "M", "−", "→", "++", "!"],
            new[] { ChangeKind.Added, ChangeKind.Untracked, ChangeKind.Ignored, ChangeKind.Modified, ChangeKind.TypeChanged,
                    ChangeKind.Deleted, ChangeKind.Renamed, ChangeKind.Copied, ChangeKind.Conflicted }.Select(StatusIcon.Glyph));
        Assert.Equal("Color.FileStatus.Added", StatusIcon.BrushKey(ChangeKind.Untracked));
        Assert.Equal("Color.FileStatus.Conflict", StatusIcon.BrushKey(ChangeKind.Conflicted));
        var icons = ui.Unstaged.GetVisualDescendants().OfType<StatusIcon>().Where(i => i.IsEffectivelyVisible).Select(i => i.Kind).ToList();
        Assert.Equal(5, icons.Count); // none on the folder row

        // Nothing was selected: the first file of Unstaged is, and its diff shows.
        Assert.Equal(["sub/s.txt"], Selected(ui.Unstaged));
        Assert.Equal(DiffState.Text, vm.Diff.State);
        Assert.Equal("s.txt", vm.Diff.Name);
        Assert.Equal(LineActionSet.StageDiscard, vm.Diff.LineActions);

        // A folder selected: empty diff. A staged row: the other list loses its selection, the staged diff shows.
        Select(ui, ui.Unstaged, "sub");
        Assert.Equal(DiffState.None, vm.Diff.State);
        Select(ui, ui.Staged, "r2.txt");
        Assert.Empty(Selected(ui.Unstaged));
        await vm.Diff.Loading;
        Assert.Equal(LineActionSet.Unstage, vm.Diff.LineActions);
        Assert.Equal(("r.txt", "r2.txt"), (vm.Diff.OldName, vm.Diff.Name));

        // List mode (View Mode drop-down): full relative paths sorted by path, selection kept.
        Click(ui.Window, ui.Find<Button>("ViewModeButton"));
        var modes = ((MenuFlyout)ui.Find<Button>("ViewModeButton").Flyout!).Items.OfType<MenuItem>().ToList();
        Assert.Equal(["View as Tree", "View as List"], modes.Select(m => (string)m.Header!));
        Assert.Equal([true, false], modes.Select(m => ((Control)m.Icon!).IsVisible));
        modes[1].Command!.Execute(null);
        Assert.Equal(["a.txt", "d.txt", "m.txt", "sub/s.txt", "u.txt"], Rows(vm.UnstagedRows));
        Assert.Equal(["r2.txt"], Selected(ui.Staged));
        vm.SetTreeModeCommand.Execute(null);
        Assert.Equal("sub", vm.UnstagedRows[0].Text);

        // Tree keys: Left on a file goes to its folder, Left collapses it, Right expands it again, Right goes to the child.
        Select(ui, ui.Unstaged, "sub/s.txt");
        Press(ui, Key.Left);
        Assert.Equal(["sub"], Selected(ui.Unstaged));
        Press(ui, Key.Left);
        Assert.Equal(["sub", "a.txt", "d.txt", "m.txt", "u.txt"], Rows(vm.UnstagedRows));
        Press(ui, Key.Right);
        Assert.Equal(6, vm.UnstagedRows.Count);
        Press(ui, Key.Right);
        Assert.Equal(["sub/s.txt"], Selected(ui.Unstaged));
        ui.Window.Close();
    }

    [AvaloniaFact]
    public async Task Stage_unstage_and_discard_by_keys_buttons_and_menu_move_the_selection()
    {
        MixedRepo();
        var ui = await OpenAsync();
        var vm = ui.Vm;

        // Enter stages the selection; the selection moves to the next file row and the list keeps the focus.
        Select(ui, ui.Unstaged, "a.txt");
        // Headless input runs the queued UI jobs, so on a busy machine the job may be over when Press returns:
        // the locked state is read the moment the job starts.
        (bool List, bool Subject)? locked = null;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.Busy) && vm.Busy)
                locked ??= (ui.Unstaged.IsEnabled, ui.Find<TextBox>("SubjectBox").IsEffectivelyEnabled);
        };
        Press(ui, Key.Enter);
        Assert.Equal((false, false), locked); // lists and commit box locked while the job runs
        await DoneAsync(ui);
        Assert.Equal(["a.txt", "m.txt", "n.txt", "r2.txt"], Rows(vm.StagedRows));
        Assert.Equal(["d.txt"], Selected(ui.Unstaged));
        Assert.True(ui.Unstaged.IsKeyboardFocusWithin);

        // Ctrl+S toggles as well: in the Staged list it unstages.
        Select(ui, ui.Staged, "a.txt");
        Assert.Empty(Selected(ui.Unstaged)); // only one list holds a selection
        Press(ui, Key.S, RawInputModifiers.Control);
        await DoneAsync(ui);
        Assert.DoesNotContain("a.txt", Rows(vm.StagedRows));
        Assert.Equal(["m.txt"], Selected(ui.Staged));

        // Header button texts follow Shift ("Stage" / "Stage All").
        var stageButton = ui.Find<Button>("StageHeaderButton");
        Assert.Equal("Stage", stageButton.Content);
        ui.Window.KeyPress(Key.LeftShift, RawInputModifiers.Shift, PhysicalKey.ShiftLeft, null);
        Assert.Equal("Stage All", stageButton.Content);
        Assert.Equal("Unstage All", ui.Find<Button>("UnstageHeaderButton").Content);
        ui.Window.KeyRelease(Key.LeftShift, RawInputModifiers.None, PhysicalKey.ShiftLeft, null);
        Assert.Equal("Stage", stageButton.Content);
        Assert.False(stageButton.IsEffectivelyEnabled); // no Unstaged selection

        // Delete asks first; Cancel keeps the file, Discard restores it from the index.
        Select(ui, ui.Unstaged, "d.txt");
        _answer = false;
        Press(ui, Key.Delete);
        await DoneAsync(ui);
        Assert.Equal(("Discard changes", "Do you want to discard all your changes in the selected files?", "Discard", "Cancel"), _asked.Single());
        Assert.False(File.Exists(Path.Combine(_repo.Path, "d.txt")));
        _answer = true;
        Press(ui, Key.D, RawInputModifiers.Control | RawInputModifiers.Shift);
        await DoneAsync(ui);
        Assert.True(File.Exists(Path.Combine(_repo.Path, "d.txt")));
        Assert.DoesNotContain("d.txt", Rows(vm.UnstagedRows));

        // A folder row stands for all files below it; several files give the plural button text.
        Select(ui, ui.Unstaged, "sub");
        vm.UnstagedSelection.Select(vm.UnstagedRows.IndexOf(vm.UnstagedRows.Single(r => r.Path == "u.txt")));
        _answer = false;
        await vm.DiscardCommand.ExecuteAsync(null);
        Assert.Equal("Discard Changes in 2 Files", _asked[^1].Primary);

        // Double-click stages the clicked selection.
        Select(ui, ui.Unstaged, "u.txt");
        var row = ui.Unstaged.ContainerFromIndex(ui.Unstaged.SelectedIndex)!;
        var at = row.TranslatePoint(new Point(60, 10), ui.Window)!.Value;
        ui.Window.MouseDown(at, MouseButton.Left);
        ui.Window.MouseUp(at, MouseButton.Left);
        ui.Window.MouseDown(at, MouseButton.Left);
        ui.Window.MouseUp(at, MouseButton.Left);
        await DoneAsync(ui);
        Assert.Contains("u.txt", Rows(vm.StagedRows));

        // Toolbar button: Stage All while Unstaged has rows, then Unstage All.
        var all = ui.Find<Button>("StageAllButton");
        Assert.Equal("Stage All", ToolTip.GetTip(all));
        Click(ui.Window, all);
        await DoneAsync(ui);
        Assert.Empty(vm.UnstagedRows);
        Assert.Equal("Unstage All", ToolTip.GetTip(all));
        Assert.Equal("chevrons-up", vm.StageAllIcon);
        // Ctrl+Shift+Alt+S with a Staged selection = Unstage All.
        Select(ui, ui.Staged, "m.txt");
        Press(ui, Key.S, RawInputModifiers.Control | RawInputModifiers.Shift | RawInputModifiers.Alt);
        await DoneAsync(ui);
        Assert.Empty(vm.StagedRows);
        Assert.Equal(["sub", " s.txt", "a.txt", "m.txt", "n.txt", "r.txt", "r2.txt", "u.txt"], Rows(vm.UnstagedRows));
        Assert.Empty(_errors);
        ui.Window.Close();
    }

    [AvaloniaFact]
    public async Task Context_menu_selects_on_right_click_and_offers_the_list_items()
    {
        MixedRepo();
        var ui = await OpenAsync();
        Select(ui, ui.Unstaged, "a.txt");
        var row = ui.Unstaged.ContainerFromIndex(ui.Vm.UnstagedRows.Select(r => r.Path).ToList().IndexOf("u.txt"))!;
        Click(ui.Window, row, MouseButton.Right);
        Assert.Equal(["u.txt"], Selected(ui.Unstaged));
        var menu = ui.Unstaged.ContextMenu!;
        Assert.True(menu.IsOpen);
        var items = menu.Items.OfType<Control>().Where(c => c.IsVisible)
            .Select(c => c is MenuItem m ? $"{m.Header} [{m.InputGesture?.ToString("p", null)}]" : "-").ToArray();
        // Avalonia prints Key.Enter as "Return" (same enum value); the gesture itself is Enter.
        Assert.Equal(["Blame/Timeline... []", "History... []", "-", "Stage [Return]", "Discard changes... [Delete]", "-", "Stage All [Ctrl+Shift+Alt+S]", "-",
            "Stash 1 File... []", "-", "Copy Path [Ctrl+C]", "Copy Full Path [Ctrl+Shift+C]"], items);
        menu.Close();

        // Ctrl+C copies the relative path(s), Ctrl+Shift+C the full ones.
        var row2 = ui.Unstaged.ContainerFromIndex(ui.Unstaged.SelectedIndex)!;
        row2.Focus();
        Press(ui, Key.C, RawInputModifiers.Control);
        await Task.Delay(50);
        Assert.Equal("u.txt", await Clipboard(ui));
        Press(ui, Key.C, RawInputModifiers.Control | RawInputModifiers.Shift);
        await Task.Delay(50);
        Assert.Equal(Path.Combine(_repo.Path, "u.txt"), await Clipboard(ui));

        var staged = ui.Staged.ContextMenu!.Items.OfType<Control>().Where(c => c.IsVisible)
            .Select(c => c is MenuItem m ? (string)m.Header! : "-").ToArray(); // the Stash item shows once the menu opens
        Assert.Equal(["Blame/Timeline...", "History...", "-", "Unstage", "-", "Unstage All", "-", "Copy Path", "Copy Full Path"], staged);
        ui.Window.Close();

        static async Task<string?> Clipboard(Ui ui) =>
            await Avalonia.Input.Platform.ClipboardExtensions.TryGetTextAsync(TopLevel.GetTopLevel(ui.View)!.Clipboard!);
    }

    [AvaloniaFact]
    public async Task Commit_button_text_enabled_rules_counter_and_ctrl_enter()
    {
        _repo.Commit("init", 1, ("a.txt", "a\n"), ("b.txt", "b\n"));
        _repo.Write("a.txt", "a2\n");
        _repo.Write("b.txt", "b2\n");
        var ui = await OpenAsync();
        var vm = ui.Vm;
        var button = ui.Find<Button>("CommitButton");
        var counter = ui.Find<TextBlock>("Counter");

        Assert.Equal("Commit", button.Content);
        Assert.False(button.IsEffectivelyEnabled);
        Assert.False(counter.IsVisible);

        Git("add", "a.txt");
        await ui.Tab.RefreshAsync();
        Settle(ui.Window);
        Assert.Equal("Commit 1 File", button.Content);
        Assert.False(vm.CanCommit); // empty subject
        vm.Subject = "   ";
        Assert.False(vm.CanCommit);

        // Counter: 50 - length; ok / warn above 50 / error above 70.
        vm.Subject = new string('x', 50);
        Settle(ui.Window);
        Assert.True(counter.IsVisible);
        Assert.Equal("0", counter.Text);
        Assert.DoesNotContain(counter.Classes, c => c is "warn" or "error");
        Assert.Equal("The recommended subject line should be 50 characters or less", ToolTip.GetTip(counter));
        vm.Subject = new string('x', 51);
        Assert.Equal("-1", counter.Text);
        Assert.Contains("warn", counter.Classes);
        vm.Subject = new string('x', 71);
        Assert.Contains("error", counter.Classes);
        Assert.Equal(Color.Parse("#FF0000"), ((ISolidColorBrush)counter.Foreground!).Color);

        Git("add", "b.txt");
        await ui.Tab.RefreshAsync();
        Settle(ui.Window);
        Assert.Equal("Commit 2 Files", button.Content);
        Assert.True(button.IsEffectivelyEnabled);
        Assert.Null(vm.Warning);

        // Enter in the subject moves to the description; Backspace at its start goes back to the end of the subject.
        var subject = ui.Find<TextBox>("SubjectBox");
        var description = ui.Find<TextBox>("DescriptionBox");
        vm.Subject = "";
        subject.Focus();
        ui.Window.KeyTextInput("Two files");
        Press(ui, Key.Enter);
        Assert.True(description.IsFocused);
        ui.Window.KeyTextInput("body");
        description.CaretIndex = 0;
        Press(ui, Key.Back);
        Assert.True(subject.IsFocused);
        Assert.Equal(9, subject.CaretIndex);
        Assert.Equal(("Two files", "body"), (vm.Subject, vm.Description));

        // Ctrl+Enter from the description commits; the fields are cleared and the list is empty again.
        description.Focus();
        Press(ui, Key.Enter, RawInputModifiers.Control);
        await DoneAsync(ui);
        Assert.Equal("Two files\n\nbody", Git("log", "-1", "--format=%B"));
        Assert.Equal(("", ""), (vm.Subject, vm.Description));
        Assert.Empty(vm.StagedRows);
        Assert.Equal("Commit", button.Content);

        // Detached HEAD warning.
        Git("checkout", "-q", "--detach");
        _repo.Write("a.txt", "a3\n");
        Git("add", "a.txt");
        await ui.Tab.RefreshAsync();
        vm.Subject = "x";
        Assert.Equal("Repository is in detached HEAD state", vm.Warning);
        Assert.Empty(_errors);
        ui.Window.Close();
    }

    [AvaloniaFact]
    public async Task Amend_shows_heads_files_unstages_against_the_parent_and_lands()
    {
        _repo.Commit("init", 1, ("a.txt", "a\n"));
        _repo.Commit("second\n\nbody", 2, ("a.txt", "a2\n"), ("n.txt", "n\n"));
        Git("remote", "add", "origin", "https://example.com/x.git");
        Git("update-ref", "refs/remotes/origin/main", "HEAD");
        Git("branch", "--set-upstream-to=origin/main", "main");
        var ui = await OpenAsync();
        var vm = ui.Vm;
        vm.Subject = "draft";
        Assert.False(vm.CanCommit);

        vm.Amend = true;
        await vm.Loading;
        Settle(ui.Window);
        Assert.Equal(("second", "body"), (vm.Subject, vm.Description));
        Assert.Equal(["a.txt", "n.txt"], Rows(vm.StagedRows));
        Assert.Equal("Amend Last Commit", ui.Find<Button>("CommitButton").Content);
        Assert.True(vm.CanCommit);
        Assert.Equal("Amending commit that has already been pushed", vm.Warning);

        // Off restores the draft and the normal Staged list; on again loads HEAD's message.
        vm.Amend = false;
        await vm.Loading;
        Assert.Equal(("draft", ""), (vm.Subject, vm.Description));
        Assert.Empty(vm.StagedRows);
        vm.Amend = true;
        await vm.Loading;
        Settle(ui.Window);

        // Unstage in amend mode puts the parent's state back: n.txt (new in HEAD) becomes untracked.
        Select(ui, ui.Staged, "n.txt");
        await vm.Diff.Loading;
        Assert.Equal(LineActionSet.Unstage, vm.Diff.LineActions);
        Press(ui, Key.Enter);
        await DoneAsync(ui);
        Assert.Equal(["a.txt"], Rows(vm.StagedRows));
        Assert.Equal(["n.txt"], Rows(vm.UnstagedRows));

        vm.Subject = "second amended";
        await vm.CommitCommand.ExecuteAsync(null);
        await DoneAsync(ui);
        Assert.Equal("second amended\n\nbody", Git("log", "-1", "--format=%B"));
        Assert.Equal("2", Git("rev-list", "--count", "HEAD"));
        Assert.Equal("a.txt", Git("show", "--name-only", "--format=", "HEAD"));
        Assert.False(vm.Amend);
        Assert.Equal(("", ""), (vm.Subject, vm.Description));
        Assert.Empty(vm.StagedRows);
        Assert.Empty(_errors);
        ui.Window.Close();
    }

    [AvaloniaFact]
    public async Task With_the_watcher_on_a_finished_job_leaves_lists_and_commit_button_current()
    {
        var files = Enumerable.Range(0, 6).Select(i => ($"f{i}.txt", $"{i}\n")).ToArray();
        _repo.Commit("init", 1, files);
        foreach (var (path, _) in files) _repo.Write(path, "changed\n");
        var ui = await OpenAsync(watch: true);
        using var tab = ui.Tab; // disposed before the repo: no watcher stays open (each one holds an inotify instance)
        var vm = ui.Vm;
        // What the view shows at the very moment the job ends (the lists unlock).
        (string[] Staged, string Button) atEnd = default;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.Busy) && !vm.Busy) atEnd = (Rows(vm.StagedRows), vm.CommitText);
        };
        for (int i = 0; i < 5; i++)
        {
            Select(ui, ui.Unstaged, $"f{i}.txt");
            var before = ui.Tab.Refreshing;
            Press(ui, Key.Enter);
            // As soon as the job's refresh runs, a watcher-style refresh replaces (cancels) it.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (ui.Tab.Refreshing == before && sw.ElapsedMilliseconds < 5000) await Task.Yield();
            _ = ui.Tab.RefreshAsync();
            await DoneAsync(ui);
            // The moment the job is over, the view shows what git has (the watcher's refresh replaced ours meanwhile).
            Assert.Equal(Git("diff", "--cached", "--name-only").Split('\n'), atEnd.Staged);
            Assert.Equal(i == 0 ? "Commit 1 File" : $"Commit {i + 1} Files", atEnd.Button);
            Assert.Equal(atEnd.Button, ui.Find<Button>("CommitButton").Content);
            Assert.Equal([$"f{i + 1}.txt"], Selected(ui.Unstaged));
            Assert.True(ui.Unstaged.IsEnabled);
        }
        Assert.Empty(_errors);
        ui.Window.Close();
    }

    [AvaloniaFact]
    public async Task Commit_does_not_run_when_the_index_changed_behind_the_shown_list()
    {
        _repo.Commit("init", 1, ("a.txt", "a\n"), ("b.txt", "b\n"));
        _repo.Write("a.txt", "a2\n");
        _repo.Write("b.txt", "b2\n");
        Git("add", "a.txt");
        var ui = await OpenAsync();
        var vm = ui.Vm;
        Assert.Equal("Commit 1 File", vm.CommitText);
        Git("add", "b.txt"); // no refresh: the button still says one file
        vm.Subject = "x";
        await vm.CommitCommand.ExecuteAsync(null);
        Assert.Equal("init", Git("log", "-1", "--format=%s")); // not committed
        Assert.Equal("Commit 2 Files", vm.CommitText);      // refreshed instead
        Assert.Equal("x", vm.Subject);
        await vm.CommitCommand.ExecuteAsync(null);
        Assert.Equal("x", Git("log", "-1", "--format=%s"));
        Assert.Equal("a.txt\nb.txt", Git("show", "--name-only", "--format=", "HEAD"));
        ui.Window.Close();
    }

    [AvaloniaFact]
    public async Task A_late_amend_list_read_does_not_overwrite_the_normal_list()
    {
        _repo.Commit("init", 1, ("a.txt", "a\n"));
        _repo.Commit("second", 2, ("a.txt", "a2\n"), ("n.txt", "n\n"));
        var ui = await OpenAsync();
        var vm = ui.Vm;
        vm.Amend = true;
        await vm.Loading;
        Assert.Equal(["a.txt", "n.txt"], Rows(vm.StagedRows));

        var pending = vm.OnStatusReadAsync(ui.Tab.Status); // a refresh waiting for the amend list
        Assert.False(pending.IsCompleted);
        vm.Amend = false;
        await vm.Loading;
        Assert.Empty(vm.StagedRows);
        await pending;
        Assert.Empty(vm.StagedRows);
        Assert.Equal("Commit", vm.CommitText);

        // The mirror: amend switched off and on again while an amend read waits; the newest switch's list wins.
        vm.Amend = true;
        await vm.Loading;
        pending = vm.OnStatusReadAsync(ui.Tab.Status);
        vm.Amend = false;
        vm.Amend = true;
        await vm.Loading;
        await pending;
        Assert.Equal(["a.txt", "n.txt"], Rows(vm.StagedRows));
        Assert.Equal("Amend Last Commit", vm.CommitText);
        ui.Window.Close();
    }

    [AvaloniaFact]
    public async Task Line_actions_stay_off_until_the_new_files_diff_is_loaded()
    {
        MixedRepo();
        var ui = await OpenAsync();
        var vm = ui.Vm;
        Select(ui, ui.Staged, "m.txt");
        await vm.Diff.Loading;
        Assert.Equal(LineActionSet.Unstage, vm.Diff.LineActions);

        int calls = 0;
        var handler = vm.Diff.LineActionHandler;
        vm.Diff.LineActionHandler = (_, _) => { calls++; return Task.CompletedTask; };
        vm.UnstagedSelection.Select(vm.UnstagedRows.IndexOf(vm.UnstagedRows.Single(r => r.Path == "a.txt")));
        // The staged diff of m.txt is still shown: no Discard (or anything else) may act on it now.
        Assert.False(vm.Diff.Loading.IsCompleted);
        Assert.Equal(LineActionSet.None, vm.Diff.LineActions);
        Assert.False(vm.Diff.ShowsLineActions);
        await vm.Diff.RunLineActionAsync(PatchAction.Discard, [new HunkLine(0, 0)]);
        Assert.Equal(0, calls);

        await vm.Diff.Loading;
        Assert.Equal(LineActionSet.StageDiscard, vm.Diff.LineActions);
        Assert.Equal("a.txt", vm.Diff.Name);
        await vm.Diff.RunLineActionAsync(PatchAction.Stage, [new HunkLine(0, 0)]);
        Assert.Equal(1, calls);
        vm.Diff.LineActionHandler = handler;
        ui.Window.Close();
    }

    [AvaloniaFact]
    public async Task A_nested_repository_is_one_row_without_diff_and_discard_leaves_it_intact()
    {
        _repo.Commit("init", 1, ("a.txt", "a\n"), ("t.txt", "t\n"));
        _repo.Write("outer/x.txt", "x\n");
        _repo.Write("outer/nested/n.txt", "n\n");
        Git("-C", "outer/nested", "init", "-q");
        Git("-C", "outer/nested", "add", "n.txt");
        Git("-C", "outer/nested", "-c", "user.name=T", "-c", "user.email=t@e", "commit", "-q", "-m", "nested");
        File.Delete(Path.Combine(_repo.Path, "t.txt"));
        File.CreateSymbolicLink(Path.Combine(_repo.Path, "t.txt"), "a.txt"); // type change: file -> symlink
        var ui = await OpenAsync();
        var vm = ui.Vm;
        Assert.Equal(["outer", " nested", " x.txt", "t.txt"], Rows(vm.UnstagedRows));
        var nested = vm.UnstagedRows[1];
        Assert.Equal(("outer/nested/", "folder-git-2", true), (nested.Path, nested.FileIcon, nested.IsNestedRepository));
        vm.SetListModeCommand.Execute(null);
        Assert.Equal(["outer/nested", "outer/x.txt", "t.txt"], Rows(vm.UnstagedRows));
        vm.SetTreeModeCommand.Execute(null);

        // No diff and no line actions; Delete does nothing (no confirmation either).
        Select(ui, ui.Unstaged, "outer/nested/");
        await vm.Diff.Loading;
        Assert.Equal((DiffState.None, LineActionSet.None), (vm.Diff.State, vm.Diff.LineActions));
        Press(ui, Key.Delete);
        await DoneAsync(ui);
        Assert.Empty(_asked);

        // The parent folder: only x.txt is counted and deleted.
        Select(ui, ui.Unstaged, "outer");
        Press(ui, Key.Delete);
        await DoneAsync(ui);
        Assert.Equal("Discard", _asked.Single().Primary);
        Assert.False(File.Exists(Path.Combine(_repo.Path, "outer", "x.txt")));
        Assert.Equal("nested", Git("-C", "outer/nested", "log", "-1", "--format=%s"));
        Assert.Equal(["outer", " nested", "t.txt"], Rows(vm.UnstagedRows));

        // Type change: the diff shows only one of git's two sections, so no line actions.
        Select(ui, ui.Unstaged, "t.txt");
        await vm.Diff.Loading;
        Assert.Equal(ChangeKind.TypeChanged, vm.UnstagedRows[^1].Kind);
        Assert.Equal(LineActionSet.None, vm.Diff.LineActions);
        Assert.Empty(_errors);
        ui.Window.Close();
    }

    [AvaloniaFact]
    public async Task A_failing_amend_list_keeps_the_list_and_shows_the_error_once()
    {
        _repo.Commit("init", 1, ("a.txt", "a\n"));
        _repo.Commit("second", 2, ("a.txt", "a2\n"), ("n.txt", "n\n"));
        var ui = await OpenAsync();
        var vm = ui.Vm;
        vm.Subject = "draft";
        vm.Amend = true;
        await vm.Loading;
        Assert.Equal(["a.txt", "n.txt"], Rows(vm.StagedRows));

        File.WriteAllText(Path.Combine(_repo.Path, ".git", "index"), "garbage"); // diff-index fails from now on
        await vm.OnStatusReadAsync(ui.Tab.Status);
        await vm.OnStatusReadAsync(ui.Tab.Status);
        Assert.Single(_errors);
        Assert.True(vm.Amend);
        Assert.Equal(["a.txt", "n.txt"], Rows(vm.StagedRows)); // the amend list stays

        // Switching on with a failing read: error again, and amend goes back off (no list under "Amend Last Commit").
        vm.Amend = false;
        await vm.Loading;
        vm.Amend = true;
        await vm.Loading;
        Assert.Equal(2, _errors.Count);
        Assert.False(vm.Amend);
        Assert.Equal(("draft", "Commit"), (vm.Subject, vm.CommitText));
        Assert.Empty(vm.StagedRows);
        ui.Window.Close();
    }

    [AvaloniaFact]
    public async Task Ctrl_shift_enter_commits_and_pushes_without_staging_lines()
    {
        var lines = Enumerable.Range(1, 10).Select(i => $"line {i}").ToList();
        _repo.Commit("init", 1, ("f.txt", string.Join("\n", lines) + "\n"));
        lines[4] = "five";
        _repo.Write("f.txt", string.Join("\n", lines) + "\n");
        Git("add", "f.txt");
        lines[6] = "seven";
        _repo.Write("f.txt", string.Join("\n", lines) + "\n");
        var ui = await OpenAsync();
        var vm = ui.Vm;
        var dialogs = new List<DialogViewModel>();
        ui.Tab.ShowDialog = d => { dialogs.Add(d); return Task.FromResult(false); };
        vm.Subject = "x";
        Select(ui, ui.Unstaged, "f.txt");
        await vm.Diff.Loading;
        Settle(ui.Window);
        SelectDocLine(ui, DocLine(vm, DocLineKind.Added, "seven"));
        Press(ui, Key.Enter, RawInputModifiers.Control | RawInputModifiers.Shift);
        await DoneAsync(ui);
        await vm.Pushing;
        // The diff editor never saw the key (no line staged); the staged change is committed, then the quick push opens
        // the Push dialog (no remote).
        Assert.DoesNotContain("seven", Git("show", ":f.txt"));
        Assert.Equal("x", Git("log", "-1", "--format=%s"));
        Assert.IsType<GerGit.ViewModels.Dialogs.Remote.PushViewModel>(Assert.Single(dialogs));
        Assert.Empty(_errors);
        ui.Window.Close();
    }

    [AvaloniaFact]
    public async Task An_empty_box_takes_the_message_git_prepared_and_amend_needs_a_head()
    {
        _repo.Write("a.txt", "a\n");
        File.WriteAllText(Path.Combine(_repo.Path, ".git", "MERGE_MSG"), "Merge branch 'x'\n\nbody\n");
        var ui = await OpenAsync();
        Assert.Equal(("Merge branch 'x'", "body\n"), (ui.Vm.Subject, ui.Vm.Description));
        Assert.False(ui.Vm.CanAmend); // unborn HEAD: nothing to amend
        Assert.False(ui.Find<CheckBox>("AmendBox").IsEffectivelyEnabled);
        ui.Window.Close();
    }

    [AvaloniaFact]
    public async Task Failed_commit_keeps_the_message_and_offers_to_skip_hooks()
    {
        _repo.Commit("init", 1, ("a.txt", "a\n"));
        var hooks = Path.Combine(_repo.Path, ".git", "hooks");
        Directory.CreateDirectory(hooks);
        File.WriteAllText(Path.Combine(hooks, "pre-commit"), "#!/bin/sh\necho hook says no\nexit 1\n");
        File.SetUnixFileMode(Path.Combine(hooks, "pre-commit"), (UnixFileMode)0b111_101_101);
        Git("config", "core.hooksPath", hooks);
        _repo.Write("a.txt", "a2\n");
        Git("add", "a.txt");
        var ui = await OpenAsync();
        var vm = ui.Vm;
        vm.Subject = "blocked";

        _answer = false;
        await vm.CommitCommand.ExecuteAsync(null);
        var ask = _asked.Single();
        Assert.Equal(("Git Error", "Skip pre-commit hooks and commit", "Close"), (ask.Title, ask.Primary, ask.Cancel));
        Assert.Contains("hook says no", ask.Text);
        Assert.Equal("blocked", vm.Subject);
        Assert.Equal("init", Git("log", "-1", "--format=%s"));

        _answer = true;
        await vm.CommitCommand.ExecuteAsync(null);
        await DoneAsync(ui);
        Assert.Equal("blocked", Git("log", "-1", "--format=%s"));
        Assert.Equal("", vm.Subject);
        ui.Window.Close();
    }

    /// <summary>The document line (0-based) of an added or removed line with that text.</summary>
    static int DocLine(LocalChangesViewModel vm, DocLineKind kind, string text) =>
        vm.Diff.Document!.Lines.Select((l, i) => (l, i)).Single(x => x.l.Kind == kind && x.l.Text == text).i;

    static void SelectDocLine(Ui ui, int line)
    {
        var editor = ui.Find<DiffView>("DiffPane").GetVisualDescendants().OfType<TextEditor>().Single(e => e.Name == "Editor");
        var dl = editor.Document.GetLineByNumber(line + 1);
        editor.TextArea.Focus();
        editor.Select(dl.Offset, dl.Length);
        Settle(ui.Window);
    }

    [AvaloniaFact]
    public async Task Partial_commit_end_to_end_through_the_view()
    {
        var lines = Enumerable.Range(1, 40).Select(i => $"line {i}").ToList();
        _repo.Commit("init", 1, ("f.txt", string.Join("\n", lines) + "\n"));
        lines[4] = "five";
        lines[34] = "thirty-five";
        var changed = string.Join("\n", lines) + "\n";
        _repo.Write("f.txt", changed);

        var ui = await OpenAsync();
        var vm = ui.Vm;
        Assert.Equal(["f.txt"], Selected(ui.Unstaged)); // the only file is selected and its diff shown
        Assert.Equal(DiffState.Text, vm.Diff.State);

        // One changed line selected in the diff editor + Enter stages just that line.
        SelectDocLine(ui, DocLine(vm, DocLineKind.Added, "five"));
        Press(ui, Key.Enter);
        await DoneAsync(ui);
        Assert.Equal(["f.txt"], Rows(vm.StagedRows));
        Assert.Equal(["f.txt"], Rows(vm.UnstagedRows));
        Assert.Equal(["f.txt"], Selected(ui.Unstaged)); // the file is still in Unstaged: selection kept
        Assert.Equal(DiffState.Text, vm.Diff.State);
        Assert.Throws<InvalidOperationException>(() => DocLine(vm, DocLineKind.Added, "five")); // diff reloaded

        ui.Find<TextBox>("SubjectBox").Focus();
        ui.Window.KeyTextInput("partial");
        Press(ui, Key.Enter, RawInputModifiers.Control);
        await DoneAsync(ui);
        Assert.Empty(_errors);
        Assert.Equal("partial", Git("log", "-1", "--format=%s"));
        // Exactly that one change: "five" added after "line 5"; the rest (also line 35) as before.
        var expected = Enumerable.Range(1, 40).Select(i => $"line {i}").ToList();
        expected.Insert(5, "five");
        Assert.Equal(string.Join("\n", expected), Git("show", "HEAD:f.txt"));
        Assert.Equal(changed, Work("f.txt"));

        // Ctrl+S on a diff selection stages too.
        SelectDocLine(ui, DocLine(vm, DocLineKind.Added, "thirty-five"));
        Press(ui, Key.S, RawInputModifiers.Control);
        await DoneAsync(ui);
        Assert.Equal(["f.txt"], Rows(vm.StagedRows));
        Assert.Contains("\nthirty-five\n", Git("show", ":f.txt"));
        Assert.Empty(_errors);
        ui.Window.Close();
    }

    [AvaloniaFact]
    public async Task Line_discard_asks_with_the_line_count_and_a_failed_line_stage_shows_the_error()
    {
        var lines = Enumerable.Range(1, 10).Select(i => $"line {i}").ToList();
        _repo.Commit("init", 1, ("f.txt", string.Join("\n", lines) + "\n"));
        lines[4] = "five";
        _repo.Write("f.txt", string.Join("\n", lines) + "\n");
        var ui = await OpenAsync();
        var vm = ui.Vm;

        // Removed + added line selected: "Discard 2 Lines"; Cancel keeps the file.
        int removed = DocLine(vm, DocLineKind.Removed, "line 5");
        var editor = ui.Find<DiffView>("DiffPane").GetVisualDescendants().OfType<TextEditor>().Single(e => e.Name == "Editor");
        editor.TextArea.Focus();
        editor.Select(editor.Document.GetLineByNumber(removed + 1).Offset,
            editor.Document.GetLineByNumber(removed + 2).EndOffset - editor.Document.GetLineByNumber(removed + 1).Offset);
        _answer = false;
        Press(ui, Key.Delete);
        await DoneAsync(ui);
        Assert.Equal(("Discard changes", "Do you want to discard all your changes in the selected lines?", "Discard 2 Lines", "Cancel"), _asked.Single());
        Assert.Contains("five", Work("f.txt"));

        SelectDocLine(ui, DocLine(vm, DocLineKind.Added, "five"));
        Press(ui, Key.Delete);
        await DoneAsync(ui);
        Assert.Equal("Discard Line", _asked[^1].Primary);
        _answer = true;
        Press(ui, Key.Delete);
        await DoneAsync(ui);
        Assert.Empty(_errors);
        Assert.DoesNotContain("five", Work("f.txt")); // only the added line went; the removed "line 5" stays removed
        Assert.Equal(["f.txt"], Rows(vm.UnstagedRows));

        // A line stage that git rejects shows the error window instead of failing silently.
        _repo.Write("f.txt", string.Join("\n", lines) + "\n");
        await ui.Tab.RefreshAsync();
        await vm.Diff.Loading;
        Settle(ui.Window);
        Git("add", "f.txt"); // the index changes behind the shown diff: the patch no longer applies
        SelectDocLine(ui, DocLine(vm, DocLineKind.Added, "five"));
        Press(ui, Key.Enter);
        await DoneAsync(ui);
        Assert.Contains("patch does not apply", Assert.Single(_errors));
        ui.Window.Close();
    }

    [AvaloniaFact]
    public async Task View_menu_shortcuts_switch_views_and_the_view_renders()
    {
        MixedRepo();
        var ui = await OpenAsync();
        var tab = ui.Tab;
        Press(ui, Key.D2, RawInputModifiers.Control);
        Assert.True(tab.IsAllCommitsView);
        Press(ui, Key.D1, RawInputModifiers.Control);
        Assert.True(tab.IsLocalChangesView);
        Settle(ui.Window);
        Assert.True(ui.Unstaged.IsKeyboardFocusWithin); // hidden before: the list with the selection gets the focus
        Press(ui, Key.D1, RawInputModifiers.Control);
        Settle(ui.Window);
        Assert.True(ui.Find<TextBox>("SubjectBox").IsFocused); // already visible: the subject

        // Smoke frame: the Modified icon of a.txt is drawn in #FFCC00, the layout has its 250 px left column.
        ui.Window.Width = 1100;
        ui.Window.Height = 700;
        Settle(ui.Window);
        using var frame = ui.Window.CaptureRenderedFrame()!;
        var icon = ui.Unstaged.GetVisualDescendants().OfType<StatusIcon>().First(i => i.Kind == ChangeKind.Modified && i.IsEffectivelyVisible);
        var p = icon.TranslatePoint(new Point(1.5, 7), ui.Window)!.Value;
        using (var fb = frame.Lock())
        {
            var at = (int)p.Y * fb.RowBytes + (int)p.X * 4;
            byte B(int i) => Marshal.ReadByte(fb.Address, at + i);
            var c = fb.Format == PixelFormat.Rgba8888 ? Color.FromRgb(B(0), B(1), B(2)) : Color.FromRgb(B(2), B(1), B(0));
            Assert.Equal(Color.Parse("#FFCC00"), c);
        }
        Assert.Equal(250, ui.Unstaged.Bounds.Width, tolerance: 1);
        ui.Window.Close();
    }

    // ---------------------------------------------------------------- checks window

    static string Line(string name, string status) => HookChecksTests.Line(name, status);

    /// <summary>A hook in the repository's hooks folder (core.hooksPath set, so a global one cannot hide it).</summary>
    void Hook(string name, string body)
    {
        var hooks = Path.Combine(_repo.Path, ".git", "hooks");
        Directory.CreateDirectory(hooks);
        var path = Path.Combine(hooks, name);
        File.WriteAllText(path, "#!/bin/sh\n" + body);
        File.SetUnixFileMode(path, (UnixFileMode)0b111_101_101);
        Git("config", "core.hooksPath", hooks);
    }

    /// <summary>a.txt changed and staged; the view open with the subject typed; dialogs shown over the main window.</summary>
    async Task<Ui> StagedAsync()
    {
        _repo.Commit("init", 1, ("a.txt", "a\n"));
        _repo.Write("a.txt", "a2\n");
        Git("add", "a.txt");
        var ui = await OpenAsync();
        ui.Tab.ShowDialog = d => DialogWindow.ShowAsync(d, ui.Window);
        ui.Vm.Subject = "with checks";
        return ui;
    }

    static DialogWindow? ChecksWindow(Ui ui) => ui.Window.OwnedWindows.OfType<DialogWindow>().SingleOrDefault();
    static ChecksDialogViewModel Checks(Ui ui) => (ChecksDialogViewModel)ChecksWindow(ui)!.DataContext!;

    static async Task Until(Ui ui, Func<bool> condition, string what)
    {
        for (int i = 0; i < 1500 && !condition(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
        Assert.True(condition(), what);
        Settle(ui.Window);
    }

    /// <summary>pre-commit-style lines and, like a test runner under pre-commit, the per-item progress on /dev/tty.</summary>
    static string TestsHook(string end) => $$"""
        printf '{{Line("ruff format", "Passed")}}\n{{HookChecksTests.Dots("pytest")}}'
        printf 'test_a.py ' > /dev/tty
        sleep 0.3
        printf '✓ test_b.py ' > /dev/tty
        sleep 0.7
        {{end}}
        """;

    [AvaloniaFact]
    public async Task Commit_with_hooks_shows_live_checks_and_closes_by_itself_when_green()
    {
        var ui = await StagedAsync();
        Hook("pre-commit", TestsHook("printf '✓ ' > /dev/tty\nprintf 'Passed\\n'\n"));
        var delay = LocalChangesViewModel.ChecksCloseDelay;
        LocalChangesViewModel.ChecksCloseDelay = TimeSpan.FromMinutes(1); // "still open" below cannot race the close
        try { await GreenCommitAsync(ui); }
        finally { LocalChangesViewModel.ChecksCloseDelay = delay; }
        ui.Window.Close();
    }

    async Task GreenCommitAsync(Ui ui)
    {
        var run = ui.Vm.CommitCommand.ExecuteAsync(null);
        await Until(ui, () => ChecksWindow(ui) is { } w && Checks(ui).Checks.Hooks.FirstOrDefault(h => h.Name == "pytest") is { IsRunning: true } m
            && m.Items.Any(x => x is { Name: "test_b.py", IsRunning: true }), "live checks");
        var w = ChecksWindow(ui)!;
        var vm = Checks(ui);
        Assert.False(w.IsDialog); // not modal: the user keeps browsing while the tests run
        Assert.True(ui.Window.IsEnabled);
        Assert.EndsWith(" — Commit — Checks", w.Title);
        Assert.False(w.ShowActivated); // appears without taking the focus
        Assert.Equal("Cancel", vm.CancelText);
        await Until(ui, () => run.IsCompleted, "the commit ends");
        await run;

        Assert.Equal("with checks", Git("log", "-1", "--format=%s"));
        Assert.Equal([("test_a.py", CheckState.Passed), ("test_b.py", CheckState.Passed)], vm.Checks.Hooks[1].Items.Select(m => (m.Name, m.State)));
        Assert.Equal((DialogStatus.Success, "Committed, checks passed"), (vm.Status, vm.StatusText));
        Assert.Equal("", ui.Vm.Subject); // today's success path
        Assert.Same(w, ChecksWindow(ui)); // still showing the result ...
        LocalChangesViewModel.ChecksCloseDelay = TimeSpan.FromMilliseconds(200);
        await Until(ui, () => ChecksWindow(ui) is null, "closes by itself"); // ... until the delay is over
        Assert.Empty(_asked);
    }

    [AvaloniaFact]
    public async Task Commit_cancelled_during_a_hook_lets_it_restore_the_unstaged_changes()
    {
        _repo.Commit("b", 0, ("b.txt", "b\n"));
        var ui = await StagedAsync();
        _repo.Write("b.txt", "b unstaged\n");
        // Like pre-commit: keep the unstaged change as a patch, check the file out, and apply the patch back on SIGINT.
        Hook("pre-commit", "git diff -- b.txt > .git/stash.patch\ngit checkout -- b.txt\n" +
                           "trap 'sleep 1; git apply .git/stash.patch; exit 130' INT\n" +
                           $"printf '{HookChecksTests.Dots("pytest")}'\necho ready > .git/hook-ready\nsleep 30\n");
        var head = Git("rev-parse", "HEAD");
        var run = ui.Vm.CommitCommand.ExecuteAsync(null);
        await Until(ui, () => ChecksWindow(ui) is not null && File.Exists(Path.Combine(_repo.Path, ".git", "hook-ready")), "the running hook");
        Assert.Equal("b\n", Work("b.txt")); // the hook has put the change aside
        Checks(ui).Cancel();
        await Until(ui, () => run.IsCompleted, "the commit ends");
        await run;

        Assert.Equal("b unstaged\n", Work("b.txt")); // restored, not lost
        Assert.Equal(head, Git("rev-parse", "HEAD"));
        Assert.Equal("M  a.txt\n M b.txt", Git("status", "--porcelain"));
        Assert.Equal("Cancelled during checks — not committed; your message is kept", Checks(ui).StatusText);
        ui.Window.Close();
    }

    /// <summary>The same with the real pre-commit (if installed): a partially staged file, a slow hook, Cancel. Skipped
    /// without pre-commit on PATH.</summary>
    [AvaloniaFact]
    public async Task Commit_cancelled_under_real_pre_commit_keeps_the_unstaged_part()
    {
        var preCommit = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Select(d => Path.Combine(d, "pre-commit")).FirstOrDefault(File.Exists);
        Assert.SkipWhen(preCommit is null, "pre-commit is not installed");
        File.WriteAllText(Path.Combine(_repo.Path, ".pre-commit-config.yaml"), """
            repos:
            -   repo: local
                hooks:
                -   id: slow
                    name: slow
                    entry: sh -c 'echo ready > .git/pc-ready; sleep 30'
                    language: system
                    pass_filenames: false
                    always_run: true
            """);
        var ui = await StagedAsync(); // commits the config with a.txt, then stages a2
        _repo.Write("a.txt", "a3\n"); // partially staged: a2 staged, a3 in the work tree
        var home = Directory.CreateDirectory(Path.Combine(_repo.Path, ".git", "pc-home")).FullName;
        var psi = new System.Diagnostics.ProcessStartInfo(preCommit!, ["install"]) { WorkingDirectory = _repo.Path, RedirectStandardOutput = true };
        psi.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        psi.Environment["PRE_COMMIT_HOME"] = home;
        using (var p = System.Diagnostics.Process.Start(psi)!) { await p.WaitForExitAsync(); Assert.Equal(0, p.ExitCode); }
        Git("config", "core.hooksPath", Path.Combine(_repo.Path, ".git", "hooks"));
        var oldHome = Environment.GetEnvironmentVariable("PRE_COMMIT_HOME");
        Environment.SetEnvironmentVariable("PRE_COMMIT_HOME", home); // pre-commit's cache stays in the test repository
        try
        {
            var head = Git("rev-parse", "HEAD");
            var run = ui.Vm.CommitCommand.ExecuteAsync(null);
            await Until(ui, () => ChecksWindow(ui) is not null && File.Exists(Path.Combine(_repo.Path, ".git", "pc-ready")), "the slow hook");
            Assert.Equal("a2\n", Work("a.txt")); // pre-commit stashed the unstaged part
            Checks(ui).Cancel();
            await Until(ui, () => run.IsCompleted, "the commit ends");
            await run;
            Assert.Equal("a3\n", Work("a.txt")); // pre-commit restored it
            Assert.Equal(head, Git("rev-parse", "HEAD"));
            Assert.Equal("MM a.txt", Git("status", "--porcelain"));
            Assert.Contains("Stashing unstaged files to", Checks(ui).Checks.Output);
        }
        finally { Environment.SetEnvironmentVariable("PRE_COMMIT_HOME", oldHome); }
        ui.Window.Close();
    }

    [AvaloniaFact]
    public async Task Closing_the_main_window_during_a_commit_cancels_it()
    {
        var ui = await StagedAsync();
        Hook("pre-commit", $"printf '{HookChecksTests.Dots("pytest")}'\nsleep 30 &\necho $! > .git/hook-sleep\n" +
                           "trap 'kill $! 2>/dev/null; exit 130' INT\nwait\n");
        var head = Git("rev-parse", "HEAD");
        var run = ui.Vm.CommitCommand.ExecuteAsync(null);
        await Until(ui, () => ChecksWindow(ui) is not null && File.Exists(Path.Combine(_repo.Path, ".git", "hook-sleep")), "the running hook");
        ui.Window.Close();
        await Until(ui, () => run.IsCompleted, "the commit ends");
        await run;
        Assert.False(ui.Vm.Busy);
        Assert.Equal(head, Git("rev-parse", "HEAD"));
        Assert.Equal("with checks", ui.Vm.Subject);
        foreach (var w in ui.Window.OwnedWindows.ToList()) w.Close();
        ui.Window.Close();
        Settle(ui.Window);
        Assert.Empty(ui.Window.OwnedWindows);
    }

    [AvaloniaFact]
    public async Task Touched_checks_window_stays_open_after_a_green_commit()
    {
        var ui = await StagedAsync();
        // The running line first, the status after 0.5 s: the window is open well before the commit ends.
        Hook("pre-commit", $"printf '{HookChecksTests.Dots("ruff")}'\nsleep 0.5\nprintf 'Passed\\n'\n");
        var delay = LocalChangesViewModel.ChecksCloseDelay;
        LocalChangesViewModel.ChecksCloseDelay = TimeSpan.FromMilliseconds(300);
        try
        {
            var run = ui.Vm.CommitCommand.ExecuteAsync(null);
            await Until(ui, () => ChecksWindow(ui) is not null, "the window");
            ChecksWindow(ui)!.GetVisualDescendants().OfType<Expander>().Single().IsExpanded = true; // opens Output
            await Until(ui, () => run.IsCompleted, "the commit ends");
            await Task.Delay(800);
            Settle(ui.Window);
            Assert.NotNull(ChecksWindow(ui));
            Assert.Equal("Close", Checks(ui).CancelText);
            Checks(ui).Cancel();
            await Until(ui, () => ChecksWindow(ui) is null, "closed by Close");
        }
        finally { LocalChangesViewModel.ChecksCloseDelay = delay; }
        ui.Window.Close();
    }

    string HookRuns => Path.Combine(_repo.Path, ".git", "hook-runs");

    /// <summary>A red pre-commit hook (counts its runs); commits and waits for the red checks window.</summary>
    async Task<(Ui Ui, ChecksDialogViewModel Vm, string Head)> RedAsync(Func<Ui, Task>? before = null)
    {
        var ui = await StagedAsync();
        if (before is not null) await before(ui);
        Hook("pre-commit", $"echo run >> '{HookRuns}'\n" + TestsHook(
            "printf '✗ ' > /dev/tty\nprintf 'Failed\\n- hook id: pytest\\n- exit code: 1\\n\\n=== 1 failed, 1 passed in 1.00s ===\\n'\nexit 1\n"));
        var head = Git("rev-parse", "HEAD");
        var run = ui.Vm.CommitCommand.ExecuteAsync(null);
        await Until(ui, () => run.IsCompleted, "the commit ends; the red window waits without blocking Local Changes");
        await run;
        Assert.NotNull(ChecksWindow(ui));
        return (ui, Checks(ui), head);
    }

    [AvaloniaFact]
    public async Task Red_pre_commit_hook_keeps_the_window_and_the_message_and_offers_the_skip()
    {
        var (ui, vm, head) = await RedAsync();
        Assert.Equal((DialogStatus.Error, "Checks failed: pytest — not committed; your message is kept"), (vm.Status, vm.StatusText));
        Assert.EndsWith(" — Commit — Checks", ChecksWindow(ui)!.Title);
        Assert.StartsWith(ui.Tab.Title, ChecksWindow(ui)!.Title); // the repository's name
        Assert.Equal(("test_b.py", CheckState.Failed), (vm.Checks.Hooks[1].Items[1].Name, vm.Checks.Hooks[1].Items[1].State));
        Assert.Equal("1 failed, 1 passed in 1.00s", vm.Checks.Hooks[1].Note);
        Assert.Equal(("Skip pre-commit hooks and commit", "Close"), (vm.SubmitText, vm.CancelText));
        Assert.Equal(head, Git("rev-parse", "HEAD")); // nothing committed
        Assert.Equal("with checks", ui.Vm.Subject); // the draft is kept
        Assert.False(ui.Vm.Busy); // Local Changes stays usable while the window waits
        Assert.Empty(_asked); // no second window

        await vm.SubmitAsync(); // "Skip pre-commit hooks and commit", nothing changed meanwhile
        Settle(ui.Window);
        Assert.Null(ChecksWindow(ui));
        Assert.Equal("with checks", Git("log", "-1", "--format=%s"));
        Assert.Single(File.ReadAllLines(HookRuns)); // the skip commit ran no hook
        Assert.Empty(_errors);
        ui.Window.Close();
    }

    [Fact]
    public async Task Skip_button_without_its_action_does_nothing()
    {
        var vm = new ChecksDialogViewModel("x — Commit — Checks") { SubmitText = "Skip pre-commit hooks and commit" };
        await vm.SubmitAsync(); // a click before the commit flow set the action
        Assert.Null(vm.Result); // still open
        bool ran = false;
        vm.Skip = () => { ran = true; return Task.CompletedTask; };
        await vm.SubmitAsync();
        Assert.True((vm.Result, ran) is (true, true));
    }

    [AvaloniaFact]
    public async Task Enter_in_the_red_window_closes_it_without_committing()
    {
        var (ui, vm, head) = await RedAsync();
        ChecksWindow(ui)!.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r");
        await Until(ui, () => ChecksWindow(ui) is null, "Enter = Close");
        Assert.False(vm.Result);
        Assert.Equal(head, Git("rev-parse", "HEAD"));
        Assert.Single(File.ReadAllLines(HookRuns));
        ui.Window.Close();
    }

    [AvaloniaFact]
    public async Task Skip_after_HEAD_moved_commits_nothing_and_says_so()
    {
        var (ui, vm, _) = await RedAsync();
        var outside = Git("commit-tree", "HEAD^{tree}", "-p", "HEAD", "-m", "outside"); // a commit made meanwhile
        Git("update-ref", "HEAD", outside);
        await vm.SubmitAsync();
        Settle(ui.Window);
        Assert.Null(ChecksWindow(ui));
        Assert.Equal(outside, Git("rev-parse", "HEAD"));
        Assert.Equal([LocalChangesViewModel.ChangedSinceChecks], _errors);
        Assert.Equal("with checks", ui.Vm.Subject);
        ui.Window.Close();
    }

    [AvaloniaFact]
    public async Task Skip_after_the_staged_set_changed_commits_nothing_and_says_so()
    {
        var (ui, vm, head) = await RedAsync();
        _repo.Write("b.txt", "b\n");
        Git("add", "b.txt");
        await vm.SubmitAsync();
        Settle(ui.Window);
        Assert.Null(ChecksWindow(ui));
        Assert.Equal(head, Git("rev-parse", "HEAD"));
        Assert.Equal([LocalChangesViewModel.ChangedSinceChecks], _errors);
        ui.Window.Close();
    }

    [AvaloniaFact]
    public async Task A_new_commit_replaces_the_red_window()
    {
        var (ui, old, _) = await RedAsync();
        Hook("pre-commit", $"printf '{Line("ruff", "Passed")}\\n'\n"); // fixed meanwhile
        var run = ui.Vm.CommitCommand.ExecuteAsync(null);
        await Until(ui, () => run.IsCompleted, "the new commit ends");
        await run;
        Assert.False(old.Result); // closed: its Skip no longer applies
        var current = Assert.Single(ui.Window.OwnedWindows.OfType<DialogWindow>()); // never two windows
        Assert.NotSame(old, current.DataContext);
        Assert.Equal("Committed, checks passed", ((ChecksDialogViewModel)current.DataContext!).StatusText);
        Assert.Equal("with checks", Git("log", "-1", "--format=%s"));
        ui.Window.Close();
    }

    [AvaloniaFact]
    public async Task Closing_the_tab_closes_its_checks_window()
    {
        var (ui, vm, _) = await RedAsync();
        ui.Tab.Dispose();
        Settle(ui.Window);
        Assert.Null(ChecksWindow(ui));
        Assert.False(vm.Result);
        ui.Window.Close();
    }

    [AvaloniaFact]
    public async Task Without_a_shown_window_a_red_hook_asks_as_before()
    {
        var ui = await StagedAsync();
        ui.Tab.ShowDialog = _ => Task.FromResult(false); // the window cannot be shown
        Hook("pre-commit", $"printf '{Line("pytest", "Failed")}\\n'\nexit 1\n");
        _answer = false;
        await ui.Vm.CommitCommand.ExecuteAsync(null);
        var ask = Assert.Single(_asked); // today's "Commit failed" question with Skip: never silent
        Assert.Equal(("Git Error", "Skip pre-commit hooks and commit"), (ask.Title, ask.Primary));
        Assert.Equal("with checks", ui.Vm.Subject);
        ui.Window.Close();
    }

    [AvaloniaFact]
    public async Task Red_post_commit_hook_after_the_commit_stays_open()
    {
        var ui = await StagedAsync();
        Hook("pre-commit", $"printf '{Line("ruff", "Passed")}\\n'\n");
        File.WriteAllText(Path.Combine(_repo.Path, ".git", "hooks", "post-commit"), $"#!/bin/sh\nprintf '{Line("late", "Failed")}\\n'\n");
        File.SetUnixFileMode(Path.Combine(_repo.Path, ".git", "hooks", "post-commit"), (UnixFileMode)0b111_101_101);
        var run = ui.Vm.CommitCommand.ExecuteAsync(null);
        await Until(ui, () => run.IsCompleted, "the commit ends");
        await run;
        var vm = Checks(ui);
        Assert.Equal((DialogStatus.Warning, "Committed, but checks failed: late"), (vm.Status, vm.StatusText));
        Assert.True(vm.KeepOpen);
        Assert.Equal("with checks", Git("log", "-1", "--format=%s"));
        Assert.Equal("", ui.Vm.Subject);
        vm.Cancel();
        await Until(ui, () => ChecksWindow(ui) is null, "closed by Close");
        ui.Window.Close();
    }

    [AvaloniaFact]
    public async Task Commit_without_hook_lines_opens_no_window()
    {
        var ui = await StagedAsync();
        Hook("pre-commit", "echo plain hook output\n"); // a hook, but not pre-commit's lines
        await ui.Vm.CommitCommand.ExecuteAsync(null);
        Settle(ui.Window);
        Assert.Null(ChecksWindow(ui));
        Assert.Equal("with checks", Git("log", "-1", "--format=%s"));
        ui.Window.Close();
    }

    [AvaloniaFact]
    public async Task Commit_failing_after_green_hooks_shows_the_old_error()
    {
        var ui = await StagedAsync();
        Hook("pre-commit", $"printf '{Line("ruff", "Passed")}\\n'\n");
        Git("config", "commit.gpgsign", "true"); // fails after the hooks: signing
        Git("config", "gpg.program", "false");
        _answer = false;
        var run = ui.Vm.CommitCommand.ExecuteAsync(null);
        await Until(ui, () => run.IsCompleted, "the commit ends");
        await run;
        Assert.Null(ChecksWindow(ui)); // closed again
        var ask = Assert.Single(_asked); // today's "Commit failed" question
        Assert.Equal(("Git Error", "Skip pre-commit hooks and commit"), (ask.Title, ask.Primary));
        Assert.Equal("with checks", ui.Vm.Subject);
        ui.Window.Close();
    }

    [AvaloniaFact]
    public async Task Commit_cancelled_during_a_hook_commits_nothing_and_keeps_the_message()
    {
        var ui = await StagedAsync();
        // Like pre-commit, the hook cleans up on SIGINT (here: its background sleep) and ends.
        Hook("pre-commit", $"printf '{Line("ruff", "Passed")}\\n{HookChecksTests.Dots("pytest")}'\n" +
                           "sleep 30 &\necho $! > .git/hook-sleep\ntrap 'kill $! 2>/dev/null; exit 130' INT\nwait\n");
        var head = Git("rev-parse", "HEAD");
        var index = Git("status", "--porcelain");
        var run = ui.Vm.CommitCommand.ExecuteAsync(null);
        await Until(ui, () => ChecksWindow(ui) is not null && File.Exists(Path.Combine(_repo.Path, ".git", "hook-sleep")), "the running hook");
        Checks(ui).Cancel(); // the window's Cancel while the commit runs
        await Until(ui, () => run.IsCompleted, "the commit ends");
        await run;

        var vm = Checks(ui);
        Assert.Equal((DialogStatus.Warning, "Cancelled during checks — not committed; your message is kept"), (vm.Status, vm.StatusText));
        Assert.Equal((head, index), (Git("rev-parse", "HEAD"), Git("status", "--porcelain"))); // what git leaves: nothing
        Assert.Equal("with checks", ui.Vm.Subject);
        Assert.Equal(CheckState.Failed, vm.Checks.Hooks[1].State); // never ended
        Assert.Empty(_asked);
        Assert.Empty(_errors);
        vm.Cancel();
        await Until(ui, () => ChecksWindow(ui) is null, "closed by Close");
        ui.Window.Close();
    }
}
