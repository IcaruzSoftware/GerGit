using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using GerGit.Core;
using GerGit.Tests.Core;
using GerGit.ViewModels;
using GerGit.ViewModels.Dialogs;
using GerGit.ViewModels.Dialogs.Refs;
using GerGit.ViewModels.Dialogs.Remote;
using GerGit.Views;

namespace GerGit.Tests.Ui;

/// <summary>Quick Launch: fuzzy matching, the window and its keys, the argument flow and the command catalogue
/// over throwaway repositories. In the non-parallel "RefOps" collection: workspaces, the repository list, the source
/// folders and the zoom are app-wide settings.</summary>
[Collection("RefOps")]
public sealed class QuickLaunchTests : IDisposable
{
    public QuickLaunchTests() => Clear();

    public void Dispose() => Clear();

    static void Clear()
    {
        WorkspaceTests.ClearKeys();
        Settings.Set<string[]?>(AppPreferences.SourceFoldersKey, null);
        Settings.Set<List<RepoEntry>?>(RepositoryList.EntriesKey, null);
        Settings.Set<List<string>?>(RepositoryList.IgnoredKey, null);
        Settings.Set<int?>(MainWindowViewModel.ZoomKey, null);
        QuickLaunchViewModel.BackgroundLimit = 5000;
        QuickLaunchViewModel.RunInBackground = work => Task.Run(work);
    }

    // ------------------------------------------------------------------ fuzzy matching

    [Theory]
    [InlineData("fe", "Fetch...", 1.87)]
    [InlineData("pu", "Pull...", 1.875)]
    [InlineData("br", "Create Branch...", 1.73)]
    [InlineData("cb", "Create Branch...", 1.60)]
    [InlineData("cb", "Checkout Branch", 1.595)]
    [InlineData("gfs", "Git Flow: Start Feature...", 2.345)]
    public void Fuzzy_scores_match_the_worked_examples(string needle, string candidate, double score)
    {
        Assert.Equal(score, Fuzzy.Score(needle, candidate)!.Value, 9);
        Assert.Equal(Fuzzy.Score(needle, candidate), Fuzzy.Match(needle, candidate)!.Value.Score); // same arithmetic
    }

    [Fact]
    public void Fuzzy_exact_and_missing_matches_and_the_aligned_positions()
    {
        Assert.Equal(double.PositiveInfinity, Fuzzy.Match("fetch", "Fetch")!.Value.Score);
        Assert.Equal(double.PositiveInfinity, Fuzzy.Score("fetch", "Fetch"));
        Assert.Null(Fuzzy.Match("pu", "Rebase..."));
        Assert.Null(Fuzzy.Score("pu", "Rebase..."));
        Assert.Null(Fuzzy.Match("a", new string('a', 1025)));
        Assert.Null(Fuzzy.Score("a", new string('a', 1025)));
        Assert.Equal(-0.005 * 1023, Fuzzy.Score("a", new string('b', 1023) + "a")!.Value, 9); // 1024 still matches
        Assert.Equal([0, 9], Fuzzy.Match("cb", "Checkout Branch")!.Value.Positions);
        Assert.Equal([4, 10], Fuzzy.Match("fs", "Git Flow: Start Feature...")!.Value.Positions);
        Assert.Equal([0, 1], Fuzzy.Match("pu", "Pull...")!.Value.Positions);
        // camelCase: an upper-case letter after a lower-case one earns 0.7.
        Assert.Equal(0.7 - 3 * 0.005 - 2 * 0.005, Fuzzy.Match("b", "fooBar")!.Value.Score, 9);
        // Long gaps make the score negative (such items are dropped from the lists).
        var gaps = "x" + new string('y', 200) + "b" + "a" + new string('y', 200) + "b";
        Assert.True(Fuzzy.Score("ab", gaps) < 0);
        Assert.Equal(Fuzzy.Score("ab", gaps), Fuzzy.Match("ab", gaps)!.Value.Score);
    }

    // ------------------------------------------------------------------ fixture

    sealed class Fx : IDisposable
    {
        public readonly RemoteSetup S = new();
        public MainWindow Window = null!;
        public MainWindowViewModel Main = null!;
        public readonly List<DialogViewModel> Dialogs = [];
        public Func<DialogViewModel, Task>? Fill;
        public readonly List<TempRepo> Extra = [];
        readonly int _blameWindows = BlameWindow.Instances.Count;

        public RepoTabViewModel Tab => Main.SelectedTab!;

        /// <summary>The main window with the restored (empty) "Work" workspace, then <paramref name="path"/> as a tab.</summary>
        public async Task StartAsync(string? path)
        {
            Main = new MainWindowViewModel { ShowDialog = d => { Dialogs.Add(d); return Task.FromResult(true); } };
            Window = new MainWindow { DataContext = Main };
            Window.Show();
            Window.StatusTimer.Stop();
            await Main.RestoreSessionAsync();
            if (path is not null) await OpenAsync(path);
        }

        public async Task OpenAsync(string path)
        {
            Assert.Null(await Main.OpenAsync(path));
            Tab.ShowDialog = async vm =>
            {
                Dialogs.Add(vm);
                if (Fill is not null) await Fill(vm);
                return vm.Result == true;
            };
            Settle();
        }

        /// <summary>Replaces the repository list with fixed entries. Opening a tab records its random temp folder
        /// (`gergit-test-&lt;hex&gt;`), whose name and path take part in fuzzy ranking; tests that assert an exact
        /// ranking use this list instead.</summary>
        public static void UseRepositories(params RepoEntry[] entries) => RepositoryList.Entries = [.. entries];

        public void Settle()
        {
            Dispatcher.UIThread.RunJobs();
            Window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }

        public static void Press(TopLevel w, Key key, RawInputModifiers m = RawInputModifiers.None)
        {
            w.KeyPress(key, m, PhysicalKey.None, null);
            if (w.IsVisible) w.KeyRelease(key, m, PhysicalKey.None, null); // Esc / Enter may have closed the window
            Dispatcher.UIThread.RunJobs();
        }

        public QuickLaunchWindow Ql => Window.QuickLaunch!;
        public QuickLaunchViewModel Vm => (QuickLaunchViewModel)Ql.DataContext!;

        /// <summary>Ctrl+P (or Ctrl+B) on the main window; waits for the scan and the argument list.</summary>
        public async Task<QuickLaunchViewModel> OpenQuickLaunchAsync(Key key = Key.P)
        {
            Press(Window, key, RawInputModifiers.Control);
            await Vm.Scanning;
            await Vm.Loading;
            Settle();
            return Vm;
        }

        public void Type(string text)
        {
            Vm.Text = text;
            Vm.Refresh();
        }

        /// <summary>The rows: "# header" or the item name.</summary>
        public string[] Rows => Vm.Rows.Select(r => r.IsHeader ? "# " + r.Header : r.Choice!.Name).ToArray();

        public QuickRow Row(string name) => Vm.Rows.First(r => !r.IsHeader && r.Choice!.Name == name);

        /// <summary>Activates a row and awaits the command that runs after the window closed.</summary>
        public async Task RunAsync(string name)
        {
            Vm.Activate(Row(name));
            Settle();
            await Window.LastCommand;
            Settle();
        }

        public string[] CommandNames => Vm.Commands.Select(c => c.Name).ToArray();

        /// <summary>Every step runs even when an earlier one throws (no leaked temp folders, the zoom always back to
        /// 100 %); the first exception is rethrown at the end.</summary>
        public void Dispose()
        {
            List<Action> steps =
            [
                () => Dispatcher.UIThread.RunJobs(),
                () => Window?.QuickLaunch?.Close(),
                .. BlameWindow.Instances.Skip(_blameWindows).ToList().Select(w => (Action)w.Close),
                () => { if (Main is not null) Main.ZoomPercent = 100; }, // the app-wide zoom follows the main window
                .. (Main?.Tabs.ToList() ?? []).Select(t => (Action)(() => Main!.CloseTab(t))),
                () => Main?.Manager?.Dispose(),
                () => { if (Window is { IsVisible: true }) Window.Close(); },
                S.Dispose,
                .. Extra.Select(r => (Action)r.Dispose),
            ];
            Exception? first = null;
            foreach (var step in steps)
            {
                try { step(); }
                catch (Exception e) { first ??= e; }
            }
            if (first is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(first);
        }
    }

    // ------------------------------------------------------------------ window and keys

    [AvaloniaFact]
    public async Task Keys_open_move_activate_go_back_and_close()
    {
        using var f = new Fx();
        await f.StartAsync(f.S.Local.Path);
        var vm = await f.OpenQuickLaunchAsync();
        Assert.True(f.Ql.IsVisible);
        Assert.Equal(("Command", null), (vm.Placeholder, vm.Chip));
        // Empty text: recent repositories, then all commands in natural order; the first item is selected.
        Assert.Equal(["# Recent Repositories", Path.GetFileName(f.S.Local.Path), "# Commands"], f.Rows[..3]);
        Assert.Equal(f.CommandNames, f.Rows[3..]);
        Assert.Equal(f.CommandNames.Order(StringComparer.Create(System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.CompareOptions.IgnoreCase | System.Globalization.CompareOptions.NumericOrdering)), f.CommandNames);
        Assert.Equal(Path.GetFileName(f.S.Local.Path), vm.Selected!.ToString());

        // Down / Up skip the header and wrap around.
        Fx.Press(f.Ql, Key.Down);
        Assert.Equal(f.CommandNames[0], vm.Selected!.ToString());
        Fx.Press(f.Ql, Key.Up);
        Fx.Press(f.Ql, Key.Up);
        Assert.Equal(f.CommandNames[^1], vm.Selected!.ToString());
        Assert.Single(vm.Rows, r => r.IsSelected);

        // Esc closes; the close time is kept for the activation that follows (no sticky dialog flag).
        Assert.True(f.Main.IsQuickLaunchOpen);
        MessageWindow.DialogClosed = false;
        Fx.Press(f.Ql, Key.Escape);
        Assert.Null(f.Window.QuickLaunch);
        Assert.False(f.Main.IsQuickLaunchOpen);
        Assert.InRange(Environment.TickCount64 - f.Main.QuickLaunchClosedMs!.Value, 0, 5000);
        Assert.False(MessageWindow.DialogClosed);

        // Ctrl+B: the "Checkout Branch" chip with the branch list.
        vm = await f.OpenQuickLaunchAsync(Key.B);
        Assert.Equal(("branch", "Checkout Branch"), (vm.Placeholder, vm.Chip));
        Assert.Equal(["# Branches", "main", "origin/main"], f.Rows);
        Assert.Equal("main", vm.Selected!.ToString());
        // Backspace with the caret after text deletes text; at position 0 it removes the chip.
        var input = f.Ql.FindControl<TextBox>("Input")!;
        input.Text = "x";
        input.CaretIndex = 1;
        Fx.Press(f.Ql, Key.Back);
        Assert.Equal(("", "Checkout Branch"), (input.Text, vm.Chip));
        Fx.Press(f.Ql, Key.Back);
        Assert.Equal(("Command", null), (vm.Placeholder, vm.Chip));
        Assert.Equal("# Recent Repositories", f.Rows[0]);

        // Enter right after typing first applies the pending filter, then runs the command after the window closed.
        vm.Text = "zoom in";
        Fx.Press(f.Ql, Key.Enter);
        f.Settle();
        await f.Window.LastCommand;
        Assert.Null(f.Window.QuickLaunch);
        Assert.Equal(110, f.Main.ZoomPercent); // reset in Fx.Dispose (also when an assertion fails)

        // The toolbar button and File > Quick Launch... open it too.
        f.Window.FindControl<Button>("QuickLaunchButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.NotNull(f.Window.QuickLaunch);
        f.Ql.Close();
        Assert.Null(f.Window.QuickLaunch);
    }

    [AvaloniaFact]
    public async Task Typed_text_ranks_commands_by_fuzzy_score_and_sections_by_their_best_item()
    {
        using var f = new Fx();
        await f.StartAsync(f.S.Local.Path);
        Fx.UseRepositories(new RepoEntry("/srv/ql/zzlocal"));
        await f.OpenQuickLaunchAsync();

        f.Type("cb");
        var rows = f.Rows;
        Assert.Equal("# Commands", rows[0]);
        Assert.True(Array.IndexOf(rows, "Create Branch...") < Array.IndexOf(rows, "Checkout Branch")); // 1.60 > 1.595
        Assert.Equal("Create Branch...", f.Vm.Selected!.ToString());
        Assert.Equal([0, 9], f.Row("Checkout Branch").NameHits!);

        // "pu": Push (1.89) before Pull... and Push... (1.875 each, natural order kept); Rebase... does not match.
        f.Type("pu");
        rows = f.Rows;
        Assert.Equal(["# Commands", "Push", "Pull...", "Push..."], rows[..4]);
        Assert.DoesNotContain("Rebase...", rows);

        // A repository whose name matches exactly ranks its section first; its path is the secondary text.
        f.Type("zzlocal");
        Assert.Equal(["# Repositories", "zzlocal"], f.Rows);
        Assert.Equal("/srv/ql/zzlocal", f.Row("zzlocal").Choice!.Secondary);

        f.Type("qqqqqq");
        Assert.Empty(f.Rows); // nothing matches: an empty list
    }

    // ------------------------------------------------------------------ commands end to end

    [AvaloniaFact]
    public async Task Fetch_takes_a_remote_and_opens_the_fetch_dialog_that_fetches_it()
    {
        using var f = new Fx();
        f.S.Local.Git("remote", "add", "upstream", f.S.Bare.Path);
        await f.StartAsync(f.S.Local.Path);
        Fx.UseRepositories();
        var vm = await f.OpenQuickLaunchAsync();
        f.Type("fetch");
        Assert.Equal(["# Commands", "Fetch", "Fetch..."], f.Rows); // "Fetch" is exact (infinite)

        vm.Activate(f.Row("Fetch"));
        await vm.Loading;
        Assert.Equal(("remote", "Fetch"), (vm.Placeholder, vm.Chip));
        Assert.Equal(["origin", "upstream"], f.Rows); // no header row for remotes
        Assert.True(f.Row("origin").IsRemote);

        f.S.OtherPush("theirs", 5);
        f.Fill = d => d.SubmitAsync();
        await f.RunAsync("upstream");
        Assert.Null(f.Window.QuickLaunch);
        var fetch = Assert.IsType<FetchViewModel>(Assert.Single(f.Dialogs));
        Assert.Equal("upstream", fetch.SelectedRemote);
        Assert.Equal(f.S.BareSha("main"), f.S.Local.Git("rev-parse", "refs/remotes/upstream/main"));
    }

    [AvaloniaFact]
    public async Task Checkout_Branch_checks_out_the_chosen_branch()
    {
        using var f = new Fx();
        f.S.Local.Git("branch", "feature");
        await f.StartAsync(f.S.Local.Path);
        var vm = await f.OpenQuickLaunchAsync(Key.B);
        f.Type("feat");
        Assert.Equal(["# Branches", "feature"], f.Rows);
        await f.RunAsync("feature");
        Assert.Equal("feature", f.S.Local.Git("branch", "--show-current"));
        Assert.Equal("refs/heads/feature", f.Tab.Refs.ActiveBranch);
        Assert.Empty(f.Dialogs); // clean tree: no dialog
    }

    [AvaloniaFact]
    public async Task Switch_Workspace_switches_also_from_the_Repository_Manager_tab()
    {
        using var f = new Fx();
        await f.StartAsync(f.S.Local.Path);
        var vm = await f.OpenQuickLaunchAsync();
        Assert.True(vm.ActivateCommand("Switch Workspace"));
        await vm.Loading;
        Assert.Equal(["# Workspaces", "Home", "Work"], f.Rows);
        await f.RunAsync("Home");
        Assert.Equal("Home", f.Main.ActiveWorkspace);
        Assert.True(f.Main.IsManagerShown);

        // On the Repository Manager tab only the commands without a repository are offered.
        vm = await f.OpenQuickLaunchAsync();
        Assert.Equal(["About GerGit", "Switch Workspace"], f.CommandNames);
        Assert.False(vm.ActivateCommand("Checkout Branch"));
        vm.ActivateCommand("Switch Workspace");
        await vm.Loading;
        await f.RunAsync("Work");
        Assert.Equal("Work", f.Main.ActiveWorkspace);
        Assert.Equal(f.S.Local.Path, f.Tab.Path);
    }

    [AvaloniaFact]
    public async Task About_GerGit_opens_the_About_dialog()
    {
        using var f = new Fx();
        await f.StartAsync(f.S.Local.Path);
        await f.OpenQuickLaunchAsync();
        Assert.Contains("About GerGit", f.CommandNames);
        f.Type("about");
        Assert.Equal(["# Commands", "About GerGit"], f.Rows.Take(2));
        await f.RunAsync("About GerGit");
        Assert.IsType<AboutViewModel>(Assert.Single(f.Dialogs));
    }

    [AvaloniaFact]
    public async Task A_listed_repository_opens_and_a_missing_one_offers_removal()
    {
        using var f = new Fx();
        var other = new TempRepo();
        f.Extra.Add(other);
        other.Commit("init", 0, ("a.txt", "1\n"));
        var gone = Path.Combine(Path.GetTempPath(), "gergit-gone-" + Guid.NewGuid().ToString("N")[..8]);
        RepositoryList.Add([other.Path]);
        RepositoryList.SetAlias(other.Path, "zzOther");
        await f.StartAsync(f.S.Local.Path);
        // The entry for a folder that is gone is written directly (Add would resolve the path).
        RepositoryList.Entries = [.. RepositoryList.Entries, new RepoEntry(gone, "zzGone")];

        await f.OpenQuickLaunchAsync();
        f.Type("zzother");
        Assert.Equal(["# Repositories", "zzOther"], f.Rows);
        await f.RunAsync("zzOther");
        Assert.Equal(other.Path, f.Tab.Path);
        Assert.Equal(2, f.Main.Tabs.Count);

        await f.OpenQuickLaunchAsync();
        f.Type("zzgone");
        await f.RunAsync("zzGone");
        Assert.Equal("Repository Not Found", Assert.Single(f.Dialogs).Title);
        Assert.DoesNotContain(RepositoryList.Entries, e => e.Path == gone);
    }

    [AvaloniaFact]
    public async Task Opening_scans_the_source_folders_and_lists_new_repositories()
    {
        using var f = new Fx();
        var root = Directory.CreateTempSubdirectory("gergit-ql-src-").FullName;
        try
        {
            await f.StartAsync(f.S.Local.Path);
            var found = Path.Combine(root, "found");
            Directory.CreateDirectory(found);
            f.S.Local.Git("init", "-q", found);
            AppPreferences.SourceFolders = [root];

            await f.OpenQuickLaunchAsync();
            Assert.Contains(RepositoryList.Entries, e => e.Path == found);
            Assert.Contains("found", f.Rows.TakeWhile(r => r != "# Commands")); // listed after the refresh
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // ------------------------------------------------------------------ hidden commands

    [AvaloniaFact]
    public async Task Commands_that_would_do_nothing_are_not_offered()
    {
        using var f = new Fx();
        var lone = new TempRepo();
        f.Extra.Add(lone);
        lone.Commit("init", 0, ("a.txt", "1\n"));
        lone.Git("checkout", "-q", "--detach");
        await f.StartAsync(lone.Path);
        await f.OpenQuickLaunchAsync();
        // No remote: no "Fetch" (remote) and no "Push" (branch); detached: no Merge / Rebase; no tags, no upstreams.
        var names = f.CommandNames;
        foreach (var hidden in new[] { "Fetch", "Push", "Merge...", "Rebase...", "Delete Tag...", "Fast-Forward" })
            Assert.DoesNotContain(hidden, names);
        Assert.DoesNotContain(names, n => n.StartsWith("Create Pull Request", StringComparison.Ordinal));
        foreach (var shown in new[] { "Fetch...", "Pull...", "Push...", "Checkout Branch", "Delete Branch...", "Rename Branch...",
                     "Blame...", "File History...", "Switch Workspace", "Repository Manager", "Repository Settings...", "Zoom In", "Zoom Out", "Switch Theme", "Open In File Explorer" })
            Assert.Contains(shown, names);
        f.Ql.Close();

        // With a remote, an active branch and a GitHub remote: those appear; the active branch is no merge source.
        await f.OpenAsync(f.S.Local.Path);
        f.S.Local.Git("remote", "add", "gh", "https://github.com/owner/repo.git");
        f.S.Local.Git("branch", "topic");
        f.S.Local.Git("tag", "v1");
        await f.Tab.RefreshAsync();
        var vm = await f.OpenQuickLaunchAsync();
        names = f.CommandNames;
        foreach (var shown in new[] { "Fetch", "Push", "Merge...", "Rebase...", "Delete Tag...", "Fast-Forward", "Create Pull Request on 'gh'" })
            Assert.Contains(shown, names);
        Assert.DoesNotContain("Create Pull Request on 'origin'", names); // a local path has no web page
        vm.ActivateCommand("Merge...");
        await vm.Loading;
        Assert.Equal(["# Branches and Tags", "topic", "origin/main", "v1"], f.Rows);
        vm.Back();
        vm.ActivateCommand("Create Pull Request on 'gh'");
        await vm.Loading;
        Assert.Equal(["# Branches", "topic"], f.Rows); // main tracks origin
        vm.Back();
        vm.ActivateCommand("Blame...");
        await vm.Loading;
        Assert.Equal(["# Repository Files", "a.txt"], f.Rows);
        Assert.Equal("a.txt", f.Row("a.txt").Choice!.Secondary);
    }

    // ------------------------------------------------------------------ review fixes

    [AvaloniaFact]
    public async Task Only_the_activation_right_after_a_close_skips_the_refresh()
    {
        using var f = new Fx();
        await f.StartAsync(f.S.Local.Path);
        f.Main.OnWindowActivated(0); // the first activation of a session is always ignored
        await f.Tab.Refreshing;
        MessageWindow.DialogClosed = false;

        // Open: an activation meanwhile does not refresh.
        await f.OpenQuickLaunchAsync();
        await f.Tab.Refreshing;
        var before = f.Tab.Refreshing;
        f.Main.OnWindowActivated(Environment.TickCount64 + 3_600_000);
        Assert.Same(before, f.Tab.Refreshing);

        // Closed by losing the focus: the headless platform sends no focus events, so this is the Deactivated handler's call.
        Assert.True(f.Ql.Dismiss());
        Assert.Null(f.Window.QuickLaunch);
        Assert.False(MessageWindow.DialogClosed);
        await f.Tab.Refreshing;
        before = f.Tab.Refreshing;
        var closed = f.Main.QuickLaunchClosedMs!.Value;
        f.Main.OnWindowActivated(closed + 100); // the activation that follows the close
        Assert.Same(before, f.Tab.Refreshing);
        f.Main.OnWindowActivated(closed + 5000); // a later real activation refreshes
        Assert.NotSame(before, f.Tab.Refreshing);
        await f.Tab.Refreshing;
    }

    [AvaloniaFact]
    public async Task Long_lists_filter_in_the_background_with_the_same_ranking_and_the_newest_text_wins()
    {
        using var f = new Fx();
        await f.StartAsync(f.S.Local.Path);
        var vm = await f.OpenQuickLaunchAsync();
        f.Type("pu");
        var expected = f.Rows;
        // Each background filter waits for its own gate, so the test decides the order in which they finish.
        var gates = new List<TaskCompletionSource>();
        QuickLaunchViewModel.BackgroundLimit = 0;
        QuickLaunchViewModel.RunInBackground = async work =>
        {
            var gate = new TaskCompletionSource();
            gates.Add(gate);
            await gate.Task;
            return work();
        };
        try
        {
            vm.Text = "zoom";
            vm.Refresh();
            var older = vm.Filtering;
            vm.Text = "pu";
            vm.Refresh();
            var newer = vm.Filtering;
            Assert.Equal(2, gates.Count);
            // The newer filter finishes first, the older one last: its result must be dropped.
            gates[1].SetResult();
            await newer;
            Assert.Equal(expected, f.Rows);
            gates[0].SetResult();
            await older;
            Assert.Equal(expected, f.Rows);
            Assert.Equal("Push", vm.Selected!.ToString());
            Assert.Equal([0, 1], f.Row("Push").NameHits!); // computed when shown
        }
        finally
        {
            QuickLaunchViewModel.BackgroundLimit = 5000;
            QuickLaunchViewModel.RunInBackground = work => Task.Run(work);
        }
    }

    [AvaloniaFact]
    public async Task Enter_during_a_background_filter_runs_nothing_after_Esc_and_runs_once_when_pressed_twice()
    {
        using var f = new Fx();
        await f.StartAsync(f.S.Local.Path);
        var vm = await f.OpenQuickLaunchAsync();
        QuickLaunchViewModel.BackgroundLimit = 0;
        try
        {
            // Enter while the filter runs, then Esc (or a focus loss) before it ends: nothing runs.
            var last = f.Window.LastCommand;
            vm.Text = "zoom in";
            var enter = vm.ActivateSelectedAsync();
            Assert.False(enter.IsCompleted);
            Assert.True(f.Ql.Dismiss());
            await enter;
            f.Settle();
            Assert.Same(last, f.Window.LastCommand);
            Assert.Equal(100, f.Main.ZoomPercent);
            Assert.Empty(f.Dialogs);

            // Enter twice (or Enter and a click): one run.
            vm = await f.OpenQuickLaunchAsync();
            vm.Text = "zoom in";
            var first = vm.ActivateSelectedAsync();
            var second = vm.ActivateSelectedAsync();
            await first;
            await second;
            f.Settle();
            await f.Window.LastCommand;
            f.Settle();
            Assert.Null(f.Window.QuickLaunch);
            Assert.Equal(110, f.Main.ZoomPercent);

            // Typing after Enter: the old selection is not run.
            vm = await f.OpenQuickLaunchAsync();
            vm.Text = "zoom out";
            var stale = vm.ActivateSelectedAsync();
            vm.Text = "zoom in";
            vm.Refresh();
            await stale;
            await vm.Filtering;
            f.Settle();
            Assert.NotNull(f.Window.QuickLaunch);
            Assert.Equal(110, f.Main.ZoomPercent);
        }
        finally
        {
            QuickLaunchViewModel.BackgroundLimit = 5000;
            f.Main.ZoomPercent = 100;
        }
    }

    [AvaloniaFact]
    public async Task The_source_scan_runs_once_at_a_time_and_stops_with_the_main_window()
    {
        using var f = new Fx();
        var root = Directory.CreateTempSubdirectory("gergit-ql-src-").FullName;
        try
        {
            await f.StartAsync(f.S.Local.Path);
            var a = f.Main.ScanSourcesAsync();
            Assert.Same(a, f.Main.ScanSourcesAsync()); // Ctrl+P, Esc, Ctrl+P: one walk
            await a;
            Assert.NotSame(a, f.Main.ScanSourcesAsync()); // a finished scan does not block the next one
            await f.Main.ScanSourcesAsync();

            f.S.Local.Git("init", "-q", Path.Combine(root, "late"));
            AppPreferences.SourceFolders = [root];
            f.Window.Close(); // cancels the scans
            Assert.False(await f.Main.ScanSourcesAsync());
            Assert.DoesNotContain(RepositoryList.Entries, e => e.Path.EndsWith("/late", StringComparison.Ordinal));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [AvaloniaFact]
    public async Task Fast_Forward_lists_only_branches_whose_upstream_exists()
    {
        using var f = new Fx();
        f.S.Local.Git("branch", "gone-up");
        f.S.Local.Git("push", "-q", "-u", "origin", "gone-up");
        f.S.Local.Git("push", "-q", "origin", "--delete", "gone-up"); // upstream configured, but gone
        f.S.Local.Git("branch", "plain");
        await f.StartAsync(f.S.Local.Path);
        var vm = await f.OpenQuickLaunchAsync();
        vm.ActivateCommand("Fast-Forward");
        await vm.Loading;
        Assert.Equal(["# Local Branches", "main"], f.Rows);
    }

    [AvaloniaFact]
    public async Task Delete_Merge_Pull_and_Blame_open_their_dialogs_with_safe_defaults()
    {
        using var f = new Fx();
        // A branch whose upstream has another name: "Also delete remote branch" must not be offered.
        f.S.Local.Git("branch", "feat");
        f.S.Local.Git("push", "-q", "origin", "feat:other-name");
        f.S.Local.Git("branch", "-q", "--set-upstream-to=origin/other-name", "feat");
        f.S.Local.Commit("odd name", 2, ("-x y.txt", "x\n"));
        await f.StartAsync(f.S.Local.Path);

        var vm = await f.OpenQuickLaunchAsync();
        vm.ActivateCommand("Delete Branch...");
        await vm.Loading;
        await f.RunAsync("feat");
        var delete = Assert.IsType<DeleteRefsViewModel>(Assert.Single(f.Dialogs));
        Assert.False(delete.CheckEnabled);
        Assert.False(delete.IsChecked);
        Assert.Equal("feat", f.S.Local.Git("branch", "--list", "feat").Trim(' ', '*')); // cancelled: still there
        Assert.True(f.S.BareHas("refs/heads/other-name"));

        vm = await f.OpenQuickLaunchAsync();
        vm.ActivateCommand("Merge...");
        await vm.Loading;
        await f.RunAsync("feat");
        Assert.Equal(MergeKind.NoFastForward, Assert.IsType<MergeViewModel>(f.Dialogs[^1]).Kind);

        await f.OpenQuickLaunchAsync();
        f.Type("pull...");
        await f.RunAsync("Pull...");
        Assert.IsType<PullViewModel>(f.Dialogs[^1]);

        vm = await f.OpenQuickLaunchAsync();
        vm.ActivateCommand("Blame...");
        await vm.Loading;
        Assert.Equal(["# Repository Files", "-x y.txt", "a.txt"], f.Rows);
        int open = BlameWindow.Instances.Count;
        await f.RunAsync("-x y.txt");
        var blame = BlameWindow.Instances[^1];
        Assert.Equal(open + 1, BlameWindow.Instances.Count);
        Assert.Equal("-x y.txt", blame.Vm.Path); // closed in Fx.Dispose
    }

    [AvaloniaFact]
    public async Task A_long_unfiltered_file_list_is_built_in_the_background()
    {
        using var f = new Fx();
        await f.StartAsync(f.S.Local.Path);
        var vm = await f.OpenQuickLaunchAsync();
        var gate = new TaskCompletionSource();
        QuickLaunchViewModel.BackgroundLimit = 0;
        QuickLaunchViewModel.RunInBackground = async work =>
        {
            await gate.Task;
            return work();
        };
        try
        {
            vm.ActivateCommand("File History...");
            await vm.Loading;
            Assert.Empty(f.Rows); // empty filter, but not built on the UI thread
            gate.SetResult();
            await vm.Filtering;
            Assert.Equal(["# Repository Files", "a.txt"], f.Rows);
            Assert.Equal("a.txt", vm.Selected!.ToString());
        }
        finally
        {
            QuickLaunchViewModel.BackgroundLimit = 5000;
            QuickLaunchViewModel.RunInBackground = work => Task.Run(work);
        }
    }
}
