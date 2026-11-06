using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GerGit.Core;
using GerGit.Core.Git;
using GerGit.ViewModels;
using GerGit.ViewModels.Dialogs;
using GerGit.ViewModels.Preferences;
using GerGit.Views;
using GerGit.Views.Preferences;
using static GerGit.Tests.Core.ConflictStateTests;

namespace GerGit.Tests.Ui;

/// <summary>Tools stored in the settings are global: these tests run alone, never next to tests that read menus.</summary>
[CollectionDefinition("ExternalTools", DisableParallelization = true)]
public sealed class ExternalToolsCollection;

/// <summary>External diff and merge tools (External diff, External merge tools). Fake tools are shell scripts in a temp folder that record their arguments; the built-in search only sees that
/// folder.</summary>
[Collection("ExternalTools")]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class ExternalToolsTests : IDisposable
{
    readonly string _bin = Directory.CreateTempSubdirectory("gergit-tools-").FullName;
    readonly List<string> _errors = [];
    readonly List<RepoTabViewModel> _tabs = [];
    readonly List<TempRepo> _repos = [];

    public ExternalToolsTests()
    {
        ExternalTools.FindBuiltIn = n => n.Contains('/') ? null : ProgramPath.Find(n, _bin);
        ExternalTools.ToolsRoot = Path.Combine(_bin, "cache", "gergit", "tools"); // made by the first run
        Settings.Set<ToolSetting[]?>(ExternalTools.DiffToolsKey, null);
        Settings.Set<ToolSetting[]?>(ExternalTools.MergeToolsKey, null);
    }

    public void Dispose()
    {
        ExternalTools.FindBuiltIn = _ => null;
        ExternalTools.ToolsRoot = ExternalTools.DefaultToolsRoot();
        ExternalTools.RandomName = () => Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(6));
        OpenIn.UseSetsid = GitRunner.HasSetsid;
        Settings.Set<ToolSetting[]?>(ExternalTools.DiffToolsKey, null);
        Settings.Set<ToolSetting[]?>(ExternalTools.MergeToolsKey, null);
        foreach (var t in _tabs) t.Dispose();
        foreach (var r in _repos) r.Dispose();
        Directory.Delete(_bin, recursive: true);
    }

    /// <summary>A fake tool: writes its arguments (one per line) to &lt;tool&gt;.out once <paramref name="after"/> ran, and
    /// exits with its status.</summary>
    string Tool(string name, string after = "")
    {
        var path = Path.Combine(_bin, name);
        File.WriteAllText(path, "#!/bin/sh\n{ for a in \"$@\"; do printf '%s\\n' \"$a\"; done; } > \"$0.out.tmp\"\n" + after +
            "\nstatus=$?\nmv \"$0.out.tmp\" \"$0.out\"\nexit $status\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    static async Task<string[]> Recorded(string tool)
    {
        var sw = Stopwatch.StartNew();
        while (!File.Exists(tool + ".out"))
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException(tool);
            await Task.Delay(20);
        }
        var lines = await File.ReadAllLinesAsync(tool + ".out");
        File.Delete(tool + ".out");
        return lines;
    }

    static void Store(ToolKind kind, params ToolSetting[] tools) =>
        Settings.Set(kind == ToolKind.Diff ? ExternalTools.DiffToolsKey : ExternalTools.MergeToolsKey, tools);

    static ToolSetting Custom(string name, string path, string args) => new(ToolSetting.CustomId, name, path, args);

    const string DiffArgs = "\"$REMOTE\" \"$LOCAL\"";
    const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    const string MergeArgs = "\"$LOCAL\" \"$REMOTE\" \"$BASE\" \"$MERGED\"";

    TempRepo NewRepo()
    {
        var r = new TempRepo();
        _repos.Add(r);
        return r;
    }

    async Task<RepoTabViewModel> OpenAsync(TempRepo repo)
    {
        var tab = new RepoTabViewModel(await GitRepository.OpenAsync(repo.Path)) { IsActive = false };
        _tabs.Add(tab);
        tab.ShowError = (title, text) => { _errors.Add($"{title}: {text}"); return Task.CompletedTask; };
        tab.ShowGitError = (r, _) => { _errors.Add(r.ReadableOutput); return Task.CompletedTask; };
        await RefreshAsync(tab);
        return tab;
    }

    static async Task RefreshAsync(RepoTabViewModel tab)
    {
        await tab.RefreshAsync();
        while (!tab.Refreshing.IsCompleted) await tab.Refreshing;
        await tab.LocalChanges.CardLoading;
        Dispatcher.UIThread.RunJobs();
    }

    static void Select(LocalChangesViewModel vm, string path, bool staged = false)
    {
        var (sel, rows) = staged ? (vm.StagedSelection, vm.StagedRows) : (vm.UnstagedSelection, vm.UnstagedRows);
        sel.Clear();
        sel.Select(rows.Select((r, i) => (r, i)).Single(x => x.r.Path == path).i);
        Dispatcher.UIThread.RunJobs();
    }

    static string Flat(IEnumerable<MenuEntry> menu) => string.Join("|", menu.Select(e =>
        e.Kind == MenuEntryKind.Separator ? "-" : e.Header + (e.Gesture is { } g ? $" [{g}]" : "") + (e.IsEnabled ? "" : " (disabled)")
        + (e.Children is { } c ? "{" + Flat(c) + "}" : "")));

    // ------------------------------------------------------------------ arguments

    [Fact]
    public void Template_is_split_first_and_each_variable_is_replaced_inside_its_own_argument()
    {
        var values = new Dictionary<string, string>
        {
            ["REMOTE"] = "/t/a b.txt",
            ["LOCAL"] = "/t/$(x) ;rm -rf ~ `id` --option",
            ["BASE"] = "/t/$LOCAL$MERGED.txt", // a value is never searched for variables again
            ["MERGED"] = "/w/../x \"q\"",
        };
        Assert.Equal(["-n", "--wait", "/t/a b.txt", "/t/$(x) ;rm -rf ~ `id` --option", "/t/$LOCAL$MERGED.txt", "--output=/w/../x \"q\""],
            ExternalTools.BuildArgs("-n  --wait \"$REMOTE\" \"$LOCAL\" \"$BASE\" --output=\"$MERGED\"", values));
        // Unquoted variables stay one argument too; a variable the kind does not have is kept; "" is an empty argument.
        Assert.Equal(["/t/a b.txt", "$BASE", "", "x/t/a b.txty"],
            ExternalTools.BuildArgs("$REMOTE $BASE \"\" x$REMOTE\"y\"", new Dictionary<string, string> { ["REMOTE"] = "/t/a b.txt" }));
        Assert.Empty(ExternalTools.BuildArgs("   ", values));
    }

    [Fact]
    public void Temp_names_are_safe_file_names_that_keep_the_extension()
    {
        Assert.Equal("config~Local.json", ExternalTools.TempName("dir/config.json", "Local"));
        Assert.Equal("x~Base", ExternalTools.TempName("../x", "Base"));
        Assert.Equal("__x___rm~Remote.txt", ExternalTools.TempName("a/$(x) ;rm.txt", "Remote"));
        Assert.Equal("--option~abc1234.c", ExternalTools.TempName("--option.c", "abc1234"));
        Assert.Equal("a_b~HEAD~.txt", ExternalTools.TempName("a b.txt", "HEAD~"));
        Assert.Equal("file~Index.gitignore", ExternalTools.TempName(".gitignore", "Index"));
        Assert.Equal("_LOCAL~Working.txt", ExternalTools.TempName("$LOCAL.txt", "Working"));
        foreach (var hostile in (string[])["../../etc/passwd", "a/..", "..", "/abs/x", "x/\n.txt"])
        {
            var name = ExternalTools.TempName(hostile, "Local");
            Assert.DoesNotContain('/', name);
            Assert.NotEqual("..", name);
            Assert.All(name, c => Assert.True(char.IsLetterOrDigit(c) || c is '.' or '_' or '-' or '~' or '^', name));
        }
    }

    // ------------------------------------------------------------------ detection

    [Fact]
    public void Tools_are_found_by_configured_path_or_built_in_candidates()
    {
        var meld = Tool("meld");
        var mine = Tool("mine");
        File.WriteAllText(Path.Combine(_bin, "kdiff3"), "not executable");
        Store(ToolKind.Diff,
            new ToolSetting("KDiff3", Path: $"/nowhere/kdiff3;{mine}"), // the first existing candidate wins
            new ToolSetting("P4Merge", Hidden: true, Path: mine),
            Custom("Zed", "relative/zed", DiffArgs),
            Custom("Bare", "mine", DiffArgs));

        var tools = ExternalTools.Load(ToolKind.Diff).ToDictionary(t => t.Name);
        Assert.Equal(meld, tools["Meld"].Exe);
        Assert.Equal(meld, tools["Meld"].ShownPath);
        Assert.Equal(mine, tools["KDiff3"].Exe);
        Assert.Null(tools["VS Code"].Exe);
        Assert.False(tools["VS Code"].IsAvailable);
        Assert.Null(tools["Zed"].Exe); // relative paths depend on the current directory: refused
        Assert.True(tools["Zed"].IsAvailable); // configured: listed, and running it reports the missing file
        Assert.Null(tools["Bare"].Exe); // PATH of the test process does not hold the fake folder

        // Menu list: available and visible, alphabetical; primary = the first without a flag.
        Assert.Equal(["Bare", "KDiff3", "Meld", "Zed"], ExternalTools.Visible(ToolKind.Diff).Select(t => t.Name));
        Assert.Equal("Bare", ExternalTools.Primary(ExternalTools.Visible(ToolKind.Diff))!.Name);

        Assert.Null(ExternalTools.Locate("bin/meld"));
        Assert.Null(ExternalTools.Locate(""));
        Assert.Equal(meld, ExternalTools.Locate(" " + meld + " "));
        Assert.Null(ExternalTools.Locate(Path.Combine(_bin, "kdiff3")));
    }

    // ------------------------------------------------------------------ preferences

    [AvaloniaFact]
    public async Task Preferences_editor_round_trip()
    {
        Tool("meld");
        Tool("kdiff3");
        var dialogs = new List<DialogViewModel>();
        var answer = true;
        var editor = new ExternalToolsEditorViewModel(ToolKind.Merge, d => { dialogs.Add(d); return Task.FromResult(answer); });

        // Found tools first, then the rest; alphabetical within.
        Assert.Equal(["KDiff3", "Meld", "Beyond Compare", "CodeBuddy", "Cursor", "P4Merge", "VS Code", "YAMLMerge"], editor.Tools.Select(t => t.Name));
        Assert.Equal("Available variables: $LOCAL, $REMOTE, $BASE, $MERGED", editor.Hint);
        Assert.Equal("Available variables: $LOCAL, $REMOTE", new ExternalToolsEditorViewModel(ToolKind.Diff, _ => Task.FromResult(true)).Hint);
        Assert.Same(editor.Tools[0], editor.Selected);
        Assert.Null(Settings.Get<ToolSetting[]?>(ExternalTools.MergeToolsKey, null)); // opening stores nothing

        var meld = editor.Tools.Single(t => t.Name == "Meld");
        var kdiff = editor.Tools.Single(t => t.Name == "KDiff3");
        var cursor = editor.Tools.Single(t => t.Name == "Cursor");
        Assert.Equal("Primary|Visible|-|Reset to default", Flat(editor.RowMenu(meld)));
        Assert.False(editor.RowMenu(cursor)[0].IsEnabled); // not found: cannot become primary
        await editor.RowMenu(meld).Single(e => e.Header == "Primary").Invoke();
        await editor.RowMenu(kdiff).Single(e => e.Header == "Visible").Invoke();
        meld.Args = "--auto \"$MERGED\"";
        await editor.RowMenu(kdiff).Single(e => e.Header == "Primary").Invoke(); // one primary at most
        Assert.False(meld.IsPrimary);
        await editor.RowMenu(meld).Single(e => e.Header == "Primary").Invoke();
        Assert.Equal((true, false), (meld.IsPrimary, kdiff.IsPrimary));
        Assert.True(editor.RowMenu(meld)[0].IsChecked);

        editor.AddCustom();
        Assert.Equal("Custom", editor.Selected!.Name);
        Assert.True(editor.Selected.IsCustom);
        Assert.Equal("Primary (disabled)|Visible", Flat(editor.RowMenu(editor.Selected))); // no path yet
        editor.Selected.Name = "Mine";
        editor.SetPath("/opt/mine;/usr/local/bin/mine");
        editor.Selected.Args = "\"$MERGED\"";
        editor.AddCustom();
        Assert.Equal("Custom", editor.Selected.Name); // "Mine" does not start with "custom"
        editor.AddCustom();
        Assert.Equal("Custom1", editor.Selected.Name);

        // Stored: only what differs; incomplete custom tools are not stored.
        Assert.Equal(
            [new ToolSetting("KDiff3", Hidden: true), new ToolSetting("Meld", Args: "--auto \"$MERGED\"", Primary: true),
             new ToolSetting(ToolSetting.CustomId, "Mine", "/opt/mine;/usr/local/bin/mine", "\"$MERGED\"")],
            Settings.Get<ToolSetting[]>(ExternalTools.MergeToolsKey, []));
        Settings.Reload(); // read back from the file
        var loaded = ExternalTools.Load(ToolKind.Merge).ToDictionary(t => t.Name);
        Assert.Equal(("--auto \"$MERGED\"", true, true), (loaded["Meld"].Args, loaded["Meld"].IsPrimary, loaded["Meld"].IsVisible));
        Assert.False(loaded["KDiff3"].IsVisible);
        Assert.Equal(("/opt/mine;/usr/local/bin/mine", "\"$MERGED\"", true), (loaded["Mine"].ShownPath, loaded["Mine"].Args, loaded["Mine"].IsCustom));
        Assert.Equal(["Meld", "Mine"], ExternalTools.Visible(ToolKind.Merge).Select(t => t.Name));
        Assert.Equal("Meld", ExternalTools.Primary(ExternalTools.Visible(ToolKind.Merge))!.Name);

        // Reset to default: searched path, default arguments, visible (the primary flag stays).
        await editor.RowMenu(kdiff).Single(e => e.Header == "Reset to default").Invoke();
        meld.Args = "\"$LOCAL\" \"$BASE\" \"$REMOTE\" --output \"$MERGED\""; // the default again: no override
        Assert.Null(meld.ArgsOverride);
        Assert.Equal([new ToolSetting("Meld", Primary: true), new ToolSetting(ToolSetting.CustomId, "Mine", "/opt/mine;/usr/local/bin/mine", "\"$MERGED\"")],
            Settings.Get<ToolSetting[]>(ExternalTools.MergeToolsKey, []));

        // Remove: confirmation; Cancel keeps the tool; afterwards the previous entry is selected.
        var mine = editor.Tools.Single(t => t.Name == "Mine");
        editor.Selected = mine;
        answer = false;
        await editor.RemoveAsync();
        Assert.Contains(mine, editor.Tools);
        answer = true;
        int index = editor.Tools.IndexOf(mine);
        await editor.RemoveAsync();
        Assert.DoesNotContain(mine, editor.Tools);
        Assert.Same(editor.Tools[index - 1], editor.Selected);
        Assert.Equal(("Do you want to remove the selected external tool?", "You can't undo this action", "Remove", 550.0),
            (dialogs[^1].Title, dialogs[^1].Description, dialogs[^1].SubmitText, dialogs[^1].Width));
        Assert.Equal([new ToolSetting("Meld", Primary: true)], Settings.Get<ToolSetting[]>(ExternalTools.MergeToolsKey, []));
        while (editor.Tools.Count > 0) await editor.RemoveAsync();
        Assert.Null(editor.Selected);
        Assert.False(editor.HasTools);
    }

    [AvaloniaFact]
    public void Preferences_integration_page_shows_both_editors()
    {
        Tool("meld");
        PreferencesViewModel.IdentityScope = ["--file", Path.Combine(_bin, "gitconfig")]; // never the real ~/.gitconfig
        var vm = new PreferencesViewModel { ShowDialog = _ => Task.FromResult(true) };
        var window = new Window { Content = new PreferencesView { DataContext = vm }, Width = 700, Height = 700 };
        try
        {
        window.Show();
        var tabs = window.GetVisualDescendants().OfType<TabControl>().Single();
        tabs.SelectedIndex = tabs.Items.OfType<TabItem>().ToList().FindIndex(t => (string)t.Header! == "Integration");
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("External Merge Tools", texts);
        Assert.Contains("External Diff Tools", texts);
        var editors = window.GetVisualDescendants().OfType<ExternalToolsEditorView>().ToList();
        Assert.Equal([vm.MergeTools, vm.DiffTools], editors.Select(e => e.DataContext));
        var path = editors[1].GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "ToolPath");
        Assert.Equal(Path.Combine(_bin, "meld"), path.Text); // Meld (found) is first and selected
        Assert.True(path.IsReadOnly);
        }
        finally
        {
            window.Close();
            PreferencesViewModel.IdentityScope = ["--global"];
        }
    }

    // ------------------------------------------------------------------ menus

    [AvaloniaFact]
    public async Task Menu_items_for_zero_one_and_several_tools()
    {
        var repo = NewRepo();
        repo.Commit("one", 0, ("f.txt", "1\n"), ("dir/g.txt", "g\n"));
        var c2 = repo.Commit("two", 10, ("f.txt", "2\n"), ("dir/g.txt", "G\n"));
        repo.Write("f.txt", "3\n");
        repo.Write("h.txt", "untracked\n");
        repo.Write("dir/g.txt", "work\n");
        repo.Git("add", "dir/g.txt");
        var tab = await OpenAsync(repo);
        await tab.Details.ShowAsync([c2]);
        var f = tab.Details.Files.Single(x => x.Path == "f.txt");
        var g = tab.Details.Files.Single(x => x.Path == "dir/g.txt");
        IReadOnlyList<MenuEntry> Changes(bool commitTab, params ChangedFile[] files) =>
            CommitFileMenu.Build(tab.Details, files, commitTab, _ => Task.CompletedTask, (_, _) => Task.FromResult<string?>(null));
        var history = new FileHistoryViewModel(tab, "f.txt", false, null);
        await history.LoadAsync();
        var lc = tab.LocalChanges;

        // None: no items at all; two history rows give no menu.
        Assert.DoesNotContain("Diff", Flat(Changes(false, f)));
        history.Selection.SelectAll();
        Assert.Empty(history.Menu());
        Select(lc, "f.txt");
        Assert.Empty(ExternalTools.LocalChangesEntries(lc, false));

        // One (a hidden tool and a tool that is not found do not count).
        Tool("meld");
        Store(ToolKind.Diff, new ToolSetting("P4Merge", Path: Tool("p4"), Hidden: true));
        Assert.Equal("Open [Ctrl+Alt+Shift+O]|Diff in Meld [Ctrl+D]|Show in File Explorer", string.Join("|", Flat(Changes(false, f)).Split('|').Take(3)));
        Assert.StartsWith("Open|Diff in Meld|Show in File Explorer|", Flat(Changes(true, f))); // commit tab: no shortcut texts
        Assert.Contains("Diff in Meld [Ctrl+D] (disabled)", Flat(Changes(false, f, g))); // several files
        Assert.Equal("Diff in Meld [Ctrl+D]", Flat(history.Menu())); // two rows: only the diff items
        history.Selection.Clear();
        history.Selection.Select(0);
        Assert.StartsWith("Reveal in GerGit|-|Open|Diff in Meld [Ctrl+D]|Show in File Explorer|", Flat(history.Menu()));
        Assert.Equal("Diff in Meld [Ctrl+D]", Flat(ExternalTools.LocalChangesEntries(lc, false)));

        // Several: the submenu, alphabetical, the primary tool shows Ctrl+D.
        Tool("kdiff3");
        Store(ToolKind.Diff, new ToolSetting("Meld", Primary: true), Custom("Alpha", Tool("alpha"), DiffArgs));
        Assert.Equal("External Diff{Alpha|KDiff3|Meld [Ctrl+D]}", Flat(ExternalTools.LocalChangesEntries(lc, false)));
        Assert.Contains("External Diff{Alpha|KDiff3|Meld}", Flat(Changes(true, f)));

        // Local Changes: several rows disable; an Unstaged folder row (first) shows nothing; a Staged folder disables.
        lc.UnstagedSelection.SelectAll();
        Assert.Equal("External Diff (disabled){Alpha|KDiff3|Meld [Ctrl+D]}", Flat(ExternalTools.LocalChangesEntries(lc, false)));
        lc.IsTreeMode = true;
        Dispatcher.UIThread.RunJobs();
        Select(lc, "dir", staged: true);
        Assert.Equal("External Diff (disabled){Alpha|KDiff3|Meld [Ctrl+D]}", Flat(ExternalTools.LocalChangesEntries(lc, true)));
        Select(lc, "dir/g.txt", staged: true);
        Assert.Equal("External Diff{Alpha|KDiff3|Meld [Ctrl+D]}", Flat(ExternalTools.LocalChangesEntries(lc, true)));

        // The Local Changes view fills its XAML menu item from the entry: a submenu, or a leaf whose click runs it.
        var item = new MenuItem();
        MenuControls.Apply(item, null);
        Assert.False(item.IsVisible);
        MenuControls.Apply(item, ExternalTools.LocalChangesEntries(lc, true)[0]);
        Assert.Equal(("External Diff", 3), ((string)item.Header!, item.ItemsSource!.Cast<object>().Count()));
        int runs = 0;
        MenuControls.Apply(item, Menus.Item("Diff in X", () => { runs++; return Task.CompletedTask; }, "Ctrl+D"));
        Assert.Equal(("Diff in X", "Ctrl+D", true), ((string)item.Header!, item.InputGesture!.ToString(), item.IsVisible));
        Assert.Null(item.ItemsSource);
        item.Click += MenuControls.InvokeTag;
        item.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent, item));
        Assert.Equal(1, runs);
    }

    [AvaloniaFact]
    public async Task Unstaged_folder_row_has_no_tool_item()
    {
        var repo = NewRepo();
        repo.Commit("one", 0, ("dir/g.txt", "g\n"));
        repo.Write("dir/g.txt", "work\n");
        Tool("meld");
        var tab = await OpenAsync(repo);
        var lc = tab.LocalChanges;
        lc.IsTreeMode = true;
        Dispatcher.UIThread.RunJobs();
        Select(lc, "dir");
        Assert.Empty(ExternalTools.LocalChangesEntries(lc, false));
        await ExternalTools.LocalChangesShortcutAsync(lc, false); // Ctrl+D on a folder does nothing
        Assert.False(File.Exists(Path.Combine(_bin, "meld.out")));
    }

    // ------------------------------------------------------------------ diff flow

    [AvaloniaFact]
    public async Task External_diff_writes_temp_files_and_passes_each_file_as_one_argument()
    {
        var repo = NewRepo();
        string[] names = ["a b.txt", "$(x).txt", ";rm -rf ~.txt", "--option.txt", "$LOCAL.txt"];
        repo.Commit("one", 0, [.. names.Select(n => (n, "old " + n + "\n"))]);
        var c2 = repo.Commit("two", 10, [.. names.Select(n => (n, "new " + n + "\n")), ("added.json", "{}\n")]);
        var tool = Tool("fake");
        Store(ToolKind.Diff, Custom("Fake", tool, "--diff " + DiffArgs));
        var t = ExternalTools.Visible(ToolKind.Diff).Single();
        var tab = await OpenAsync(repo);

        foreach (var name in names)
        {
            var (left, right) = ExternalTools.CommitSides(null, c2, name, null, ChangeKind.Modified);
            await ExternalTools.DiffAsync(tab, t, left, right);
            var args = await Recorded(tool);
            Assert.Equal(3, args.Length);
            Assert.Equal("--diff", args[0]);
            Assert.Equal("old " + name + "\n", File.ReadAllText(args[1]));
            Assert.Equal("new " + name + "\n", File.ReadAllText(args[2]));
            Assert.All(args[1..], a => Assert.StartsWith(ExternalTools.ToolsRoot + "/", a));
            Assert.EndsWith($"~{c2[..7]}~.txt", args[1]); // <name>~<sha7>~.txt: the commit's parent
            Assert.EndsWith($"~{c2[..7]}.txt", args[2]);
        }
        Assert.Empty(_errors);

        var job = tab.Jobs.History.First(j => j.Name == "External diff");
        Assert.Equal(JobOutcome.Succeeded, job.Outcome);
        Assert.StartsWith($"Running '{tool} --diff /", job.Output);

        // An added file: an empty left side (no git read, no error). History pair: older left, newer right.
        var (l2, r2) = ExternalTools.CommitSides(null, c2, "added.json", null, ChangeKind.Added);
        await ExternalTools.DiffAsync(tab, t, l2, r2);
        var added = await Recorded(tool);
        Assert.Equal(("", "{}\n"), (File.ReadAllText(added[1]), File.ReadAllText(added[2])));
        Assert.EndsWith(".json", added[1]);
        var runFolder = Path.GetDirectoryName(added[1])!;
        Assert.Equal(ExternalTools.ToolsRoot, Path.GetDirectoryName(runFolder)); // its own folder below the tools root
        Assert.Equal(Private, File.GetUnixFileMode(runFolder));
        Assert.StartsWith("gergit-", Path.GetFileName(runFolder));

        var history = new FileHistoryViewModel(tab, "a b.txt", false, null) { ShowError = (_, text) => { _errors.Add(text); return Task.CompletedTask; } };
        await history.LoadAsync();
        history.Selection.SelectAll();
        await history.ExternalDiffShortcutAsync(); // Ctrl+D with two rows
        var pair = await Recorded(tool);
        Assert.Equal(("old a b.txt\n", "new a b.txt\n"), (File.ReadAllText(pair[1]), File.ReadAllText(pair[2])));

        // Changes list Ctrl+D: the primary tool for the one selected file.
        await tab.Details.ShowAsync([c2]);
        await CommitFileMenu.ExternalDiffAsync(tab.Details, [tab.Details.Files.Single(f => f.Path == "--option.txt")]);
        Assert.Equal("new --option.txt\n", File.ReadAllText((await Recorded(tool))[2]));
        int jobs = tab.Jobs.History.Count(j => j.Name == "External diff");
        await CommitFileMenu.ExternalDiffAsync(tab.Details, [.. tab.Details.Files]); // several files: nothing
        Assert.Equal(jobs, tab.Jobs.History.Count(j => j.Name == "External diff"));

        // Cleaned up with the process (here: on demand).
        ExternalTools.CleanUp();
        Assert.False(Directory.Exists(runFolder));
    }

    [AvaloniaFact]
    public async Task Open_at_a_commit_copies_into_a_private_run_folder_that_is_removed_at_exit()
    {
        var repo = NewRepo();
        var sha = repo.Commit("one", 0, ("notes.txt", "old\n"), ("build.sh", "echo hi\n"));
        repo.Commit("two", 1, ("notes.txt", "new\n"));
        var tab = await OpenAsync(repo);
        var opened = new List<string>();
        tab.Hosting = new HostingService(new GerGit.Hosting.AccountStore(Path.Combine(_bin, "accounts.json")), new GerGit.Tests.Hosting.FakeSecretStore())
        {
            OpenPath = p => { opened.Add(p); return Task.CompletedTask; },
            ShowNotice = _ => Task.CompletedTask,
        };

        await RevisionActions.OpenAtAsync(tab, sha, "notes.txt");
        // The copy itself, or its folder when this machine's xdg-mime reports no passive type (no xdg-mime on a build server).
        var shown = Assert.Single(opened);
        var runFolder = File.Exists(shown) ? Path.GetDirectoryName(shown)! : shown;
        var file = Path.Combine(runFolder, "notes.txt");
        Assert.Equal(("notes.txt", "old\n"), (Path.GetFileName(file), File.ReadAllText(file)));
        Assert.Equal(ExternalTools.ToolsRoot, Path.GetDirectoryName(runFolder));
        Assert.Equal(Private, File.GetUnixFileMode(runFolder));

        // Not a passive type: its folder is shown instead.
        await RevisionActions.OpenAtAsync(tab, sha, "build.sh");
        var scriptFolder = opened[1];
        Assert.True(File.Exists(Path.Combine(scriptFolder, "build.sh")));
        Assert.Empty(_errors);

        ExternalTools.CleanUp(); // what GerGit does when it exits
        Assert.False(Directory.Exists(runFolder));
        Assert.False(Directory.Exists(scriptFolder));
    }

    [AvaloniaFact]
    public async Task External_diff_of_local_changes_uses_index_working_tree_and_head()
    {
        var repo = NewRepo();
        repo.Commit("one", 0, ("x y.txt", "head\n"), ("gone.txt", "g\n"));
        repo.Write("x y.txt", "index\n");
        repo.Git("add", "x y.txt");
        repo.Write("x y.txt", "work\n");
        repo.Write("new.txt", "untracked\n");
        File.Delete(Path.Combine(repo.Path, "gone.txt"));
        var tool = Tool("fake");
        Store(ToolKind.Diff, Custom("Fake", tool, DiffArgs));
        var tab = await OpenAsync(repo);
        var lc = tab.LocalChanges;
        lc.IsTreeMode = false;
        Dispatcher.UIThread.RunJobs();

        // Unstaged: index → the working-tree file itself.
        Select(lc, "x y.txt");
        await ExternalTools.LocalChangesEntries(lc, false).Single().Invoke();
        var args = await Recorded(tool);
        Assert.Equal("index\n", File.ReadAllText(args[0]));
        Assert.EndsWith("x_y~Index.txt", args[0]);
        Assert.Equal(Path.Combine(repo.Path, "x y.txt"), args[1]);

        // Staged (Ctrl+D): HEAD → index.
        Select(lc, "x y.txt", staged: true);
        await ExternalTools.LocalChangesShortcutAsync(lc, staged: true);
        args = await Recorded(tool);
        Assert.Equal(("head\n", "index\n"), (File.ReadAllText(args[0]), File.ReadAllText(args[1])));

        // Untracked: empty left; deleted: empty right.
        Select(lc, "new.txt");
        await ExternalTools.LocalChangesShortcutAsync(lc, staged: false);
        args = await Recorded(tool);
        Assert.Equal(("", Path.Combine(repo.Path, "new.txt")), (File.ReadAllText(args[0]), args[1]));
        Select(lc, "gone.txt");
        await ExternalTools.LocalChangesShortcutAsync(lc, staged: false);
        args = await Recorded(tool);
        Assert.Equal(("g\n", ""), (File.ReadAllText(args[0]), File.ReadAllText(args[1])));
        Assert.Empty(_errors);
    }

    [AvaloniaFact]
    public async Task External_diff_errors()
    {
        var repo = NewRepo();
        var c1 = repo.Commit("one", 0, ("f.txt", "1\n"));
        var tab = await OpenAsync(repo);
        var missing = Path.Combine(_bin, "missing");
        Store(ToolKind.Diff, Custom("Gone", missing, DiffArgs));
        var gone = ExternalTools.Visible(ToolKind.Diff).Single();
        await ExternalTools.DiffAsync(tab, gone, new(c1, "f.txt"), new(c1, "f.txt"));
        Assert.Equal($"Error: Cannot find external diff tool at '{missing}'", _errors.Single());

        // A git error (no such revision) is shown as git's text; the tool does not start.
        _errors.Clear();
        var tool = Tool("fake");
        Store(ToolKind.Diff, Custom("Fake", tool, DiffArgs));
        await ExternalTools.DiffAsync(tab, ExternalTools.Visible(ToolKind.Diff).Single(), new("0000000", "f.txt"), new(c1, "f.txt"));
        Assert.StartsWith("Git Error: ", _errors.Single());
        Assert.Equal(JobOutcome.Failed, tab.Jobs.History.First(j => j.Name == "External diff").Outcome);
        Assert.False(File.Exists(tool + ".out"));

        // A file that cannot be executed (without setsid the start itself fails).
        _errors.Clear();
        OpenIn.UseSetsid = false;
        var bad = Path.Combine(_bin, "bad");
        File.WriteAllBytes(bad, [0x7f, 0x45, 0x4c, 0x46, 1, 2, 3]);
        File.SetUnixFileMode(bad, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        Store(ToolKind.Diff, Custom("Bad", bad, DiffArgs));
        await ExternalTools.DiffAsync(tab, ExternalTools.Visible(ToolKind.Diff).Single(), new(c1, "f.txt"), new(null, "f.txt"));
        Assert.StartsWith($"Error: Cannot run '{bad}'.\n", _errors.Single());
    }

    [AvaloniaFact]
    public async Task Every_run_gets_its_own_private_folder()
    {
        var repo = NewRepo();
        var c1 = repo.Commit("one", 0, ("f.txt", "1\n"));
        var tool = Tool("fake");
        Store(ToolKind.Diff, Custom("Fake", tool, DiffArgs));
        var t = ExternalTools.Visible(ToolKind.Diff).Single();
        var tab = await OpenAsync(repo);
        async Task<string> RunFolderAsync()
        {
            await ExternalTools.DiffAsync(tab, t, new(c1, "f.txt", Missing: true), new(c1, "f.txt"));
            Assert.Empty(_errors);
            return Path.GetDirectoryName((await Recorded(tool))[0])!;
        }
        // Default: below the cache folder in the home folder (flatpak tools cannot see the host's /tmp).
        Assert.EndsWith("/gergit/tools", ExternalTools.DefaultToolsRoot());
        Assert.True(Path.IsPathRooted(ExternalTools.DefaultToolsRoot()));
        if (Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is not { Length: > 0 })
            Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache/gergit/tools"), ExternalTools.DefaultToolsRoot());

        var first = await RunFolderAsync();
        var second = await RunFolderAsync();
        Assert.NotEqual(first, second);
        Assert.Equal(Private, File.GetUnixFileMode(ExternalTools.ToolsRoot)); // created 0700, also the gergit/ folder
        Assert.Equal(Private, File.GetUnixFileMode(Path.GetDirectoryName(ExternalTools.ToolsRoot)!));
        Assert.All((string[])[first, second], f =>
        {
            Assert.Equal(Private, File.GetUnixFileMode(f));
            Assert.Equal(ExternalTools.ToolsRoot, Path.GetDirectoryName(f));
        });
        // A taken name (a folder, a file or a link of that name) is never reused: mkdir fails, the next name is tried.
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(tab.Path)))[..8];
        string Named(string random) => Path.Combine(ExternalTools.ToolsRoot, $"gergit-{hash}-{random}");
        var open = Directory.CreateDirectory(Named("taken1")).FullName;
        File.SetUnixFileMode(open, Private | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
        File.WriteAllText(Named("taken2"), "");
        var outside = Directory.CreateDirectory(Path.Combine(_bin, "outside")).FullName;
        Directory.CreateSymbolicLink(Named("taken3"), outside);
        var names = new Queue<string>(["taken1", "taken2", "taken3", "fresh"]);
        ExternalTools.RandomName = names.Dequeue;
        var third = await RunFolderAsync();
        Assert.Equal(Named("fresh"), third);
        Assert.Equal(Private, File.GetUnixFileMode(third));
        Assert.Empty(Directory.GetFileSystemEntries(open)); // nothing written into the taken ones
        Assert.Empty(Directory.GetFileSystemEntries(outside));
        ExternalTools.RandomName = () => Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(6));
        Assert.Empty(_errors);

        // A wider root is tightened; a root that is a symbolic link is refused and the tool does not run.
        File.SetUnixFileMode(ExternalTools.ToolsRoot, Private | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
        Assert.Equal(Private, File.GetUnixFileMode(Path.GetDirectoryName(await RunFolderAsync())!));
        var elsewhere = Directory.CreateDirectory(Path.Combine(_bin, "elsewhere")).FullName;
        var linked = Path.Combine(_bin, "linked-tools");
        Directory.CreateSymbolicLink(linked, elsewhere);
        ExternalTools.ToolsRoot = linked;
        await ExternalTools.DiffAsync(tab, t, new(c1, "f.txt", Missing: true), new(c1, "f.txt"));
        Assert.Equal($"Error: '{linked}' is a symbolic link.", _errors.Single());
        Assert.Empty(Directory.GetFileSystemEntries(elsewhere));
        Assert.False(File.Exists(tool + ".out"));

        // The gergit/ folder as a link is refused too.
        _errors.Clear();
        var linkedParent = Path.Combine(_bin, "cache2", "gergit");
        Directory.CreateDirectory(Path.GetDirectoryName(linkedParent)!);
        Directory.CreateSymbolicLink(linkedParent, elsewhere);
        ExternalTools.ToolsRoot = Path.Combine(linkedParent, "tools");
        await ExternalTools.DiffAsync(tab, t, new(c1, "f.txt", Missing: true), new(c1, "f.txt"));
        Assert.Equal($"Error: '{linkedParent}' is a symbolic link.", _errors.Single());
        Assert.Empty(Directory.GetFileSystemEntries(elsewhere));
    }

    [AvaloniaFact]
    public async Task First_run_removes_leftover_run_folders_older_than_a_day()
    {
        var root = ExternalTools.ToolsRoot = Path.Combine(_bin, "sweep", "gergit", "tools");
        Directory.CreateDirectory(root);
        var outside = Directory.CreateDirectory(Path.Combine(_bin, "outside")).FullName;
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "keep\n");
        var old = Directory.CreateDirectory(Path.Combine(root, "gergit-0000-old")).FullName;
        File.WriteAllText(Path.Combine(old, "f~Local.txt"), "x");
        Directory.CreateSymbolicLink(Path.Combine(old, "link"), outside); // inside: unlinked, never followed
        var fresh = Directory.CreateDirectory(Path.Combine(root, "gergit-0000-fresh")).FullName;
        var link = Directory.CreateSymbolicLink(Path.Combine(root, "gergit-0000-link"), outside).FullName;
        var file = Path.Combine(root, "gergit-0000-file");
        File.WriteAllText(file, "");
        var other = Directory.CreateDirectory(Path.Combine(root, "other-old")).FullName;
        foreach (var d in (string[])[old, other]) Directory.SetLastWriteTimeUtc(d, DateTime.UtcNow.AddDays(-2));
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-2));

        var repo = NewRepo();
        repo.Commit("one", 0, ("f.txt", "1\n"));
        var tool = Tool("fake");
        Store(ToolKind.Diff, Custom("Fake", tool, DiffArgs));
        var tab = await OpenAsync(repo);
        await ExternalTools.DiffAsync(tab, ExternalTools.Visible(ToolKind.Diff).Single(), new(GitRepository.IndexRev, "f.txt"), new(null, "f.txt"));
        await Recorded(tool);

        Assert.False(Directory.Exists(old));
        Assert.True(Directory.Exists(fresh));
        Assert.NotNull(new DirectoryInfo(link).LinkTarget);
        Assert.True(File.Exists(file));
        Assert.True(Directory.Exists(other)); // not a run folder
        Assert.Equal("keep\n", File.ReadAllText(Path.Combine(outside, "keep.txt")));
        Assert.Empty(_errors);
    }

    [AvaloniaFact]
    public async Task Amend_diff_of_a_root_commit_has_an_empty_left_side()
    {
        var repo = NewRepo();
        repo.Commit("root", 0, ("f.txt", "root\n"));
        repo.Write("f.txt", "amended\n");
        repo.Git("add", "f.txt");
        var e = new StatusEntry("f.txt", null, ChangeKind.Modified, Staged: true);
        Assert.Equal(new DiffSide("HEAD~", "f.txt", Missing: true), ExternalTools.WorkingSides(e, amend: true, headHasParent: false).Left);
        Assert.Equal(new DiffSide("HEAD~", "f.txt"), ExternalTools.WorkingSides(e, amend: true).Left);

        var tool = Tool("fake");
        Store(ToolKind.Diff, Custom("Fake", tool, DiffArgs));
        var tab = await OpenAsync(repo);
        var lc = tab.LocalChanges;
        lc.IsTreeMode = false;
        lc.Amend = true;
        await lc.Loading;
        await RefreshAsync(tab);
        Select(lc, "f.txt", staged: true);
        await ExternalTools.LocalChangesShortcutAsync(lc, staged: true);
        var args = await Recorded(tool);
        Assert.Equal(("", "amended\n"), (File.ReadAllText(args[0]), File.ReadAllText(args[1])));
        Assert.Empty(_errors);
    }

    [AvaloniaFact]
    public async Task A_file_name_never_selects_an_index_stage()
    {
        // `:0:x` is stage 0 of "x": the index side of "0:x" must be read as `:0:0:x`, never as `:0:x`.
        var repo = NewRepo();
        var c1 = repo.Commit("one", 0, ("x", "x head\n"), ("0:x", "0:x head\n"));
        repo.Write("x", "x index\n");
        repo.Write("0:x", "0:x index\n");
        repo.Git("add", "-A");
        repo.Write("0:x", "0:x work\n");
        var git = await GitRepository.OpenAsync(repo.Path);
        Assert.Equal("0:x index\n", System.Text.Encoding.UTF8.GetString(await git.ReadFileAtAsync(GitRepository.IndexRev, "0:x")));
        Assert.Equal("0:x head\n", System.Text.Encoding.UTF8.GetString(await git.ReadFileAtAsync(c1, "0:x")));
        await Assert.ThrowsAsync<ArgumentException>(() => git.ReadFileAtAsync("", "0:x"));
        Assert.Equal(":0:0:x", GitRepository.ObjectName(GitRepository.IndexRev, "0:x"));
        Assert.Equal(":2:0:x", GitRepository.ObjectName(GitRepository.Stage(2), "0:x"));

        var tool = Tool("fake");
        Store(ToolKind.Diff, Custom("Fake", tool, DiffArgs));
        var tab = await OpenAsync(repo);
        var lc = tab.LocalChanges;
        lc.IsTreeMode = false;
        Dispatcher.UIThread.RunJobs();
        Select(lc, "0:x");
        await ExternalTools.LocalChangesShortcutAsync(lc, staged: false);
        var args = await Recorded(tool);
        Assert.Equal(("0:x index\n", "0:x work\n"), (File.ReadAllText(args[0]), File.ReadAllText(args[1])));
        Assert.EndsWith("~Index", args[0]);
        Select(lc, "0:x", staged: true);
        await ExternalTools.LocalChangesShortcutAsync(lc, staged: true);
        args = await Recorded(tool);
        Assert.Equal(("0:x head\n", "0:x index\n"), (File.ReadAllText(args[0]), File.ReadAllText(args[1])));

        // Save as at a commit (the other caller of the same read).
        var target = Path.Combine(_bin, "saved");
        await RevisionActions.SaveAsAsync(tab, (_, _) => Task.FromResult<string?>(target), c1, "0:x");
        Assert.Equal("0:x head\n", File.ReadAllText(target));
        Assert.Empty(_errors);
    }

    [AvaloniaFact]
    public async Task Symbolic_links_in_the_work_tree()
    {
        var repo = NewRepo();
        repo.Write("a.txt", "a\n");
        File.CreateSymbolicLink(Path.Combine(repo.Path, "l"), "a.txt");
        repo.Write("d/x.txt", "x\n");
        repo.Commit("one", 0);
        File.Delete(Path.Combine(repo.Path, "l"));
        File.CreateSymbolicLink(Path.Combine(repo.Path, "l"), "b.txt");
        var tool = Tool("fake");
        Store(ToolKind.Diff, Custom("Fake", tool, DiffArgs));
        var tab = await OpenAsync(repo);
        var lc = tab.LocalChanges;
        lc.IsTreeMode = false;
        Dispatcher.UIThread.RunJobs();

        // An unstaged link reaches the tool as a temp file with the link text (as git shows it).
        Select(lc, "l");
        await ExternalTools.LocalChangesShortcutAsync(lc, staged: false);
        var args = await Recorded(tool);
        Assert.Equal(("a.txt", "b.txt"), (File.ReadAllText(args[0]), File.ReadAllText(args[1])));
        Assert.EndsWith("l~Working", args[1]);

        // A folder on the way that is a link: refused, the tool does not run.
        var outside = Directory.CreateDirectory(Path.Combine(_bin, "outside")).FullName;
        File.WriteAllText(Path.Combine(outside, "x.txt"), "secret\n");
        Directory.Delete(Path.Combine(repo.Path, "d"), recursive: true);
        Directory.CreateSymbolicLink(Path.Combine(repo.Path, "d"), outside);
        await ExternalTools.DiffAsync(tab, ExternalTools.Visible(ToolKind.Diff).Single(), new(GitRepository.IndexRev, "d/x.txt"), new(null, "d/x.txt"));
        Assert.Equal("Error: 'd/x.txt' is below a symbolic link.", _errors.Single());
        Assert.False(File.Exists(tool + ".out"));
    }

    // ------------------------------------------------------------------ merge flow

    async Task<(TempRepo Repo, RepoTabViewModel Tab)> ConflictAsync()
    {
        var repo = Diverged();
        _repos.Add(repo);
        GitFails(repo, "merge", "side");
        var tab = await OpenAsync(repo);
        Select(tab.LocalChanges, "f.txt");
        await tab.LocalChanges.CardLoading;
        return (repo, tab);
    }

    [AvaloniaFact]
    public async Task Merge_tool_success_leaves_the_file_unstaged_and_the_card_turns_resolved()
    {
        // The tool takes theirs as the result (no markers left).
        var tool = Tool("fake", "cp \"$2\" \"$4\"");
        Store(ToolKind.Merge, Custom("Fake", tool, MergeArgs));
        var (repo, tab) = await ConflictAsync();
        var lc = tab.LocalChanges;

        Assert.Equal("Merge in Fake [Ctrl+D]", Flat(ExternalTools.LocalChangesEntries(lc, false)));
        var card = lc.Card!;
        Assert.Equal(("Merge in Fake", true, false), (card.MergeToolText, card.ShowMergeTool, card.ShowMergeToolArrow));
        card.TheirsChecked = false;
        Assert.False(card.ShowMergeTool); // both cards must be checked
        card.TheirsChecked = true;

        await card.MergeInToolCommand.ExecuteAsync(null);
        var args = await Recorded(tool);
        Assert.Equal(4, args.Length);
        Assert.Equal(("a\nO\nc\n", "a\nT\nc\n", "a\nb\nc\n"), (File.ReadAllText(args[0]), File.ReadAllText(args[1]), File.ReadAllText(args[2])));
        Assert.Equal(["f~Local.txt", "f~Remote.txt", "f~Base.txt"], args[..3].Select(Path.GetFileName));
        Assert.Equal(Path.Combine(repo.Path, "f.txt"), args[3]);
        Assert.Empty(_errors);
        Assert.StartsWith($"Running '{tool} /", tab.Jobs.History.First(j => j.Name == "External Merge").Output);

        // GerGit does nothing after the tool: the file is still unmerged (not staged), the card shows "resolved".
        Assert.Equal("a\nT\nc\n", File.ReadAllText(Path.Combine(repo.Path, "f.txt")));
        await RefreshAsync(tab);
        Assert.True(lc.Card!.IsResolved);
        Assert.NotEmpty(repo.Git("ls-files", "-u"));
        Assert.StartsWith("UU", repo.Git("status", "--porcelain"));
    }

    [AvaloniaFact]
    public async Task Merge_tool_failure_keeps_the_conflict()
    {
        var tool = Tool("fake", "false");
        Store(ToolKind.Merge, Custom("Fake", tool, MergeArgs), new ToolSetting(ToolSetting.CustomId, "Other", Tool("other"), MergeArgs, Primary: true));
        var (repo, tab) = await ConflictAsync();
        var lc = tab.LocalChanges;
        Assert.Equal("External Merge{Fake|Other [Ctrl+D]}", Flat(ExternalTools.LocalChangesEntries(lc, false)));
        Assert.True(lc.Card!.ShowMergeToolArrow);
        Assert.Equal("Merge in Other", lc.Card.MergeToolText);
        Assert.Equal("Fake|Other", Flat(lc.Card.MergeToolMenu()));

        // The drop-down item runs the chosen tool; it exits with an error and writes nothing.
        await lc.Card.MergeToolMenu()[0].Invoke();
        Assert.Equal(4, (await Recorded(tool)).Length);
        await RefreshAsync(tab);
        Assert.False(lc.Card!.IsResolved);
        Assert.StartsWith("UU", repo.Git("status", "--porcelain"));
        Assert.Contains("<<<<<<<", File.ReadAllText(Path.Combine(repo.Path, "f.txt")));
        Assert.Empty(_errors);

        // A tool that is gone: the error text, nothing runs.
        var missing = Path.Combine(_bin, "gone");
        Store(ToolKind.Merge, Custom("Gone", missing, MergeArgs));
        await ExternalTools.LocalChangesShortcutAsync(lc, staged: false); // Ctrl+D on the unmerged file
        Assert.Equal($"Error: Cannot find external merge tool at '{missing}'", _errors.Single());
    }

    [AvaloniaFact]
    public async Task Merge_without_base_gets_an_empty_base_and_modify_delete_has_no_merge_item()
    {
        // Both added: no stage 1, so an empty base file and no error.
        var repo = NewRepo();
        repo.Commit("base", 0, ("other.txt", "o\n"));
        repo.Git("checkout", "-q", "-b", "side");
        repo.Commit("side adds", 10, ("f.txt", "side\n"), ("g.txt", "g\n"));
        repo.Git("checkout", "-q", "main");
        repo.Commit("main adds", 20, ("f.txt", "main\n"));
        GitFails(repo, "merge", "side");
        var tool = Tool("fake");
        Store(ToolKind.Merge, Custom("Fake", tool, MergeArgs));
        var tab = await OpenAsync(repo);
        Select(tab.LocalChanges, "f.txt");
        Assert.Equal("Merge in Fake [Ctrl+D]", Flat(ExternalTools.LocalChangesEntries(tab.LocalChanges, false)));
        await ExternalTools.LocalChangesShortcutAsync(tab.LocalChanges, staged: false);
        var args = await Recorded(tool);
        Assert.Equal(("main\n", "side\n", ""), (File.ReadAllText(args[0]), File.ReadAllText(args[1]), File.ReadAllText(args[2])));
        Assert.Empty(_errors);

        // Modify/delete: no merge item, Ctrl+D does nothing (like the conflict card).
        var md = Diverged();
        _repos.Add(md);
        md.Git("checkout", "-q", "side");
        md.Git("rm", "-q", "f.txt");
        md.Commit("side deletes", 30);
        md.Git("checkout", "-q", "main");
        GitFails(md, "merge", "side");
        var tab2 = await OpenAsync(md);
        Select(tab2.LocalChanges, "f.txt");
        Assert.False(tab2.LocalChanges.Card!.File.MergeAllowed);
        Assert.Empty(ExternalTools.LocalChangesEntries(tab2.LocalChanges, false));
        int jobs = tab2.Jobs.History.Count;
        await ExternalTools.LocalChangesShortcutAsync(tab2.LocalChanges, staged: false);
        Assert.Equal(jobs, tab2.Jobs.History.Count);
        Assert.Empty(_errors);
    }

    [AvaloniaFact]
    public async Task Merge_of_an_added_on_one_side_conflict_gets_an_empty_missing_side()
    {
        // rename/rename: main renames f to g3, side to g2 → "AU g3" (only stage 2) and "UA g2" (only stage 3).
        var repo = NewRepo();
        repo.Commit("base", 0, ("f", "a\nb\nc\nd\n"));
        repo.Git("checkout", "-q", "-b", "side");
        repo.Git("mv", "f", "g2");
        repo.Commit("side renames", 10);
        repo.Git("checkout", "-q", "main");
        repo.Git("mv", "f", "g3");
        repo.Commit("main renames", 20);
        GitFails(repo, "merge", "side");
        var tool = Tool("fake");
        Store(ToolKind.Merge, Custom("Fake", tool, MergeArgs));
        var tab = await OpenAsync(repo);
        Assert.Contains(tab.State.Unmerged, f => f.Path == "g3" && f.Ours == 'A');

        foreach (var (path, ours, theirs) in ((string, string, string)[])[("g3", "a\nb\nc\nd\n", ""), ("g2", "", "a\nb\nc\nd\n")])
        {
            Select(tab.LocalChanges, path);
            Assert.Equal("Merge in Fake [Ctrl+D]", Flat(ExternalTools.LocalChangesEntries(tab.LocalChanges, false)));
            await ExternalTools.LocalChangesShortcutAsync(tab.LocalChanges, staged: false);
            var args = await Recorded(tool);
            Assert.Equal((ours, theirs, ""), (File.ReadAllText(args[0]), File.ReadAllText(args[1]), File.ReadAllText(args[2])));
        }
        Assert.Empty(_errors);
    }

    [AvaloniaFact]
    public async Task Merge_shows_a_base_read_error()
    {
        var tool = Tool("fake");
        Store(ToolKind.Merge, Custom("Fake", tool, MergeArgs));
        var (repo, tab) = await ConflictAsync();
        // The base blob is gone (a broken repository): stage 1 exists, but reading it fails.
        var baseSha = repo.Git("ls-files", "-u", "--", "f.txt").Split('\n').Single(l => l.EndsWith("1\tf.txt", StringComparison.Ordinal)).Split(' ')[1];
        File.Delete(Path.Combine(repo.Path, ".git", "objects", baseSha[..2], baseSha[2..]));
        await ExternalTools.MergeAsync(tab, ExternalTools.Visible(ToolKind.Merge).Single(), "f.txt");
        Assert.StartsWith("Git Error: ", _errors.Single());
        Assert.Equal(JobOutcome.Failed, tab.Jobs.History.First(j => j.Name == "External Merge").Outcome);
        Assert.False(File.Exists(tool + ".out")); // failed before the start
    }

    [AvaloniaFact]
    public async Task Merge_tool_never_writes_through_a_link()
    {
        var tool = Tool("fake", "cp \"$2\" \"$4\"");
        Store(ToolKind.Merge, Custom("Fake", tool, MergeArgs));
        var (repo, tab) = await ConflictAsync();
        var victim = Path.Combine(_bin, "victim");
        File.WriteAllText(victim, "keep\n");
        var merged = Path.Combine(repo.Path, "f.txt");
        File.Delete(merged);
        File.CreateSymbolicLink(merged, victim);

        await ExternalTools.MergeAsync(tab, ExternalTools.Visible(ToolKind.Merge).Single(), "f.txt");
        Assert.Equal("Error: 'f.txt' is a symbolic link.", _errors.Single());
        Assert.Equal(JobOutcome.Failed, tab.Jobs.History.First(j => j.Name == "External Merge").Outcome);
        Assert.False(File.Exists(tool + ".out")); // refused before the start: the job failed without starting it
        Assert.Equal("keep\n", File.ReadAllText(victim));
    }
}
