using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GerGit.Core;
using GerGit.Tests.Core;
using GerGit.ViewModels;
using GerGit.Views;

namespace GerGit.Tests.Ui;

/// <summary>File menu on the rows of the Changes tab and the Commit tab of the details pane, the
/// commit-side entry to Blame and File History.</summary>
public sealed class CommitFileMenuTests : IDisposable
{
    readonly TempRepo _repo = new();
    readonly string _c1, _c2, _c3, _c4;

    /// <summary>Ann adds f.txt, Bob edits it, Cid renames it to g.txt, Dee edits g.txt and adds h.txt. Clean working tree.</summary>
    public CommitFileMenuTests()
    {
        _c1 = BlameHistoryTests.Commit(_repo, "one", "Ann", 0, ("f.txt", "a\nb\nc\n"));
        _c2 = BlameHistoryTests.Commit(_repo, "two", "Bob", 10, ("f.txt", "a\nB\nc\nd\n"));
        _repo.Git("mv", "f.txt", "g.txt");
        _c3 = BlameHistoryTests.Commit(_repo, "three", "Cid", 20, ("g.txt", "A\nB\nc\nd\ne\n"));
        _c4 = BlameHistoryTests.Commit(_repo, "four", "Dee", 30, ("g.txt", "A\nB\nc\nd\ne\nf\n"), ("h.txt", "h\n"));
    }

    public void Dispose() => _repo.Dispose();

    sealed record Ui(MainWindow Window, RepoTabViewModel Tab, DetailsView View)
    {
        public ListBox Files => View.FindControl<ListBox>("FileList")!;
        public ChangedFile File(string path) => Tab.Details.Files.Single(f => f.Path == path);
    }

    async Task<Ui> ShowAsync(params string[] shas)
    {
        var main = new MainWindowViewModel();
        var window = new MainWindow { DataContext = main };
        window.Show();
        Assert.Null(await main.OpenAsync(_repo.Path));
        var tab = main.SelectedTab!;
        tab.IsActive = false; // no watcher refreshes in between; the tab view stays shown
        Settle(window);
        window.GetVisualDescendants().OfType<RepoTabView>().Single().IsVisible = true;
        await tab.Details.ShowAsync(shas);
        tab.Details.SelectedTab = DetailsViewModel.ChangesTab;
        Settle(window);
        return new Ui(window, tab, window.GetVisualDescendants().OfType<DetailsView>().First(v => v.DataContext == tab.Details));
    }

    static void Settle(TopLevel w)
    {
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    static string Flat(IEnumerable<MenuEntry> menu) => string.Join("|", menu.Select(e =>
        e.Kind == MenuEntryKind.Separator ? "-" : e.Header + (e.Gesture is { } g ? $" [{g}]" : "") + (e.IsEnabled ? "" : " (disabled)")));

    static Task Click(IEnumerable<MenuEntry> menu, params string[] path)
    {
        var e = menu.Single(x => x.Header == path[0]);
        return path.Length == 1 ? e.Invoke() : Click(e.Children!, path[1..]);
    }

    [AvaloniaFact]
    public async Task Changes_and_commit_tab_menus_for_one_file_several_files_and_a_range()
    {
        var ui = await ShowAsync(_c4);
        var g = ui.File("g.txt");
        var h = ui.File("h.txt");

        var one = ui.View.FileMenu([g], commitTab: false);
        Assert.Equal("Open [Ctrl+Alt+Shift+O]|Show in File Explorer|-|Reset File to|-|Blame/Timeline...|History...|-|Save as...|-|" +
            "Copy Path [Ctrl+C]|Copy Full Path [Ctrl+Shift+C]", Flat(one));
        Assert.Equal("State At Commit...|State Before Commit...", Flat(one.Single(e => e.Header == "Reset File to").Children!));

        var two = ui.View.FileMenu([g, h], commitTab: false);
        Assert.Equal("Open [Ctrl+Alt+Shift+O]|Show in File Explorer|-|Reset 2 Files to|-|Blame/Timeline... (disabled)|History... (disabled)|-|" +
            "Save as... (disabled)|-|Copy Path [Ctrl+C]|Copy Full Path [Ctrl+Shift+C]", Flat(two));

        // Commit tab rows: no shortcut texts, "Reset File To".
        Assert.Equal("Open|Show in File Explorer|-|Reset File To|-|Blame/Timeline...|History...|-|Save as...|-|Copy Path|Copy Full Path",
            Flat(ui.View.FileMenu([g], commitTab: true)));
        Assert.Empty(ui.View.FileMenu([], commitTab: false));

        // Both rows selected in the list: Ctrl+C copies the relative paths, Ctrl+Shift+C the full ones.
        ui.Files.SelectedItems!.Clear();
        ui.Files.SelectedItems.Add(g);
        ui.Files.SelectedItems.Add(h);
        Settle(ui.Window);
        Assert.True(ui.Files.ContainerFromIndex(0)!.Focus());
        ui.Window.KeyPress(Key.C, RawInputModifiers.Control, PhysicalKey.None, null);
        await Task.Delay(50);
        Assert.Equal("g.txt\nh.txt", await Clipboard(ui));
        ui.Window.KeyPress(Key.C, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.None, null);
        await Task.Delay(50);
        Assert.Equal($"{Path.Combine(_repo.Path, "g.txt")}\n{Path.Combine(_repo.Path, "h.txt")}", await Clipboard(ui));

        // A range: the two "State at Commit '<sha7>'" items, destination first.
        await ui.Tab.Details.ShowAsync([_c4, _c2]);
        var range = ui.View.FileMenu([ui.File("g.txt")], commitTab: false);
        Assert.Equal($"State at Commit '{_c4[..7]}'...|State at Commit '{_c2[..7]}'...", Flat(range.Single(e => e.Header == "Reset File to").Children!));
        ui.Window.Close();

        static async Task<string?> Clipboard(Ui ui) =>
            await Avalonia.Input.Platform.ClipboardExtensions.TryGetTextAsync(TopLevel.GetTopLevel(ui.View)!.Clipboard!);
    }

    [AvaloniaFact]
    public async Task Blame_and_history_open_at_the_commit_for_a_file_without_local_changes()
    {
        var ui = await ShowAsync(_c3);
        var menu = ui.View.FileMenu([ui.File("g.txt")], commitTab: false);

        await Click(menu, "Blame/Timeline...");
        var b = BlameWindow.Instances.Last();
        await b.Vm.Loading;
        Assert.Equal(_c3, b.Vm.SelectedRevision!.Sha);
        Assert.Equal([_c4, _c3, _c2, _c1], b.Vm.Revisions.Select(r => r.Sha)); // read from the best tip (main)
        Assert.Equal("Ann,Cid,Bob,Ann,Bob,Cid", string.Join(",", b.Vm.Rows.Select(r => (r.IsGroupStart ? "" : "_") + r.Commit?.AuthorName)));
        b.Close();

        await Click(menu, "History...");
        var h = FileHistoryWindow.Instances.Last();
        await h.Vm.Loading;
        await h.Vm.Diff.Loading;
        Assert.Equal([_c4, _c3, _c2, _c1], h.Vm.Rows.Select(r => r.Revision.Sha));
        Assert.Equal([1], h.Vm.Selection.SelectedIndexes); // the commit it was opened from
        Assert.Equal("f.txt", h.Vm.Diff.OldName);

        // Closing the repository tab closes its windows.
        await Click(menu, "Blame/Timeline...");
        b = BlameWindow.Instances.Last();
        ui.Tab.Main!.CloseTab(ui.Tab);
        Assert.DoesNotContain(h, FileHistoryWindow.Instances);
        Assert.DoesNotContain(b, BlameWindow.Instances);
        ui.Window.Close();
    }

    [AvaloniaFact]
    public async Task Reset_file_to_the_state_at_and_before_the_commit()
    {
        var g = Path.Combine(_repo.Path, "g.txt");
        File.WriteAllText(g, "local\n");
        var ui = await ShowAsync(_c3);
        var asked = new List<(string Title, string Text, string Button)>();
        bool answer = false;
        ui.Tab.Confirm = (title, text, button, _) => { asked.Add((title, text, button)); return Task.FromResult(answer); };
        var menu = ui.View.FileMenu([ui.File("g.txt")], commitTab: false);

        // Cancelled: the local change stays, no job runs.
        await Click(menu, "Reset File to", "State At Commit...");
        Assert.Equal(("Do you want to reset the file to the state it is at the commit?", "The current file changes will be discarded.", "Reset File"), asked[0]);
        Assert.Equal("local\n", File.ReadAllText(g));
        Assert.Empty(ui.Tab.Jobs.History);

        // Confirmed: the locally modified file gets the commit's content.
        answer = true;
        await Click(menu, "Reset File to", "State At Commit...");
        Assert.Equal("A\nB\nc\nd\ne\n", File.ReadAllText(g));
        Assert.Equal("Reset File", ui.Tab.Jobs.History[0].Name);
        Assert.True(ui.Tab.IsLocalChangesView);

        // Before the commit (which renamed f.txt to g.txt): f.txt comes back, g.txt goes; the text counts one file.
        await Click(menu, "Reset File to", "State Before Commit...");
        Assert.Equal(("Do you want to reset the file to the state it was before the commit?", "Reset File"), (asked[2].Title, asked[2].Button));
        Assert.False(File.Exists(g));
        Assert.Equal("a\nB\nc\nd\n", File.ReadAllText(Path.Combine(_repo.Path, "f.txt")));
        Assert.Equal(JobOutcome.Succeeded, ui.Tab.Jobs.History[0].Outcome);
        ui.Window.Close();
    }
}
