using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GerGit.Core;
using GerGit.Tests.Core;
using GerGit.ViewModels;
using GerGit.ViewModels.Dialogs;
using GerGit.Views;

namespace GerGit.Tests.Ui;

/// <summary>Repository Manager (start screen): tree, details panel, opening, removing, renaming and the scan of
/// throwaway source folders (never the home folder). In the non-parallel "RefOps" collection: the repository list and the
/// source folders are app-wide settings, cleared before and after each test.</summary>
[Collection("RefOps")]
public sealed class RepositoryManagerTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("gergit-mgr-").FullName;
    readonly List<DialogViewModel> _dialogs = [];
    bool _answer = true;

    public RepositoryManagerTests()
    {
        Clear();
        RepositoryManagerViewModel.DetailsDelay = TimeSpan.Zero;
        RescanViewModel.DoneDelay = TimeSpan.Zero;
    }

    public void Dispose()
    {
        Clear();
        RepositoryManagerViewModel.DetailsDelay = TimeSpan.FromMilliseconds(200);
        RescanViewModel.DoneDelay = TimeSpan.FromSeconds(1.5);
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    static void Clear()
    {
        Settings.Set<string[]?>(AppPreferences.SourceFoldersKey, null);
        Settings.Set<List<RepoEntry>?>(RepositoryList.EntriesKey, null);
        Settings.Set<List<string>?>(RepositoryList.IgnoredKey, null);
        Settings.Set<List<string>?>(RepositoryManagerViewModel.ExpandedKey, null);
        Settings.Set<bool?>(RepoTabViewModel.ShowWorktreesKey, null); // set by opening a repository with worktrees
    }

    string Dir(string relative) => Path.Combine(_root, relative);

    /// <summary>A main window model whose Repository Manager tab has finished its first scan.</summary>
    async Task<(MainWindowViewModel Main, RepositoryManagerViewModel Manager)> StartAsync()
    {
        var main = new MainWindowViewModel { ShowDialog = d => { _dialogs.Add(d); return Task.FromResult(_answer); } };
        main.NewTab();
        var manager = main.Manager!;
        await manager.Scanning;
        await manager.DetailsLoading;
        return (main, manager);
    }

    static RepoNode Section(RepositoryManagerViewModel m, string key) => m.Roots.Single(r => r.Path == key);

    static RepoNode Child(RepoNode parent, string name) => parent.Children.Single(c => c.Name == name);

    static void Close(MainWindowViewModel main)
    {
        foreach (var t in main.Tabs.ToList()) t.Dispose();
        main.Manager?.Dispose();
    }

    [AvaloniaFact]
    public async Task The_scan_fills_the_tree_by_source_folder_and_folder_and_recent_comes_first()
    {
        var alpha = RepositoryScanTests.Init(_root, "one/alpha");
        var beta = RepositoryScanTests.Init(_root, "one/group/beta");
        RepositoryScanTests.Init(_root, "one/group/sub/Gamma10");
        RepositoryScanTests.Init(_root, "one/group/sub/gamma9");
        RepositoryScanTests.Init(_root, "two/delta");
        RepositoryScanTests.Init(_root, "two/delta/nested"); // inside a repository: not found
        using var outside = new TempRepo();
        outside.Commit("init", 0, ("a.txt", "1\n"));
        RepositoryList.RecordOpened(outside.Path, DateTimeOffset.UtcNow);
        AppPreferences.SourceFolders = [Dir("one") + "/", Dir("two") + "/"];

        var (main, m) = await StartAsync();
        Assert.False(m.IsEmpty);
        Assert.Equal(["Recent", "Repositories"], m.Roots.Select(r => r.Name));
        var recent = Section(m, RepositoryManagerViewModel.RecentKey);
        var repos = Section(m, RepositoryManagerViewModel.RepositoriesKey);
        Assert.Equal([Path.GetFileName(outside.Path)], recent.Children.Select(c => c.Name));
        // Two source folders: one node each, titled without the shared prefix; then a repository outside every source.
        Assert.Equal(["one", "two", Path.GetFileName(outside.Path)], repos.Children.Select(c => c.Name));
        Assert.Equal([RepoNodeKind.Source, RepoNodeKind.Source, RepoNodeKind.Repository], repos.Children.Select(c => c.Kind));
        Assert.Equal(Dir("one"), repos.Children[0].ToolTip);
        var one = Child(repos, "one");
        Assert.Equal(["group", "alpha"], one.Children.Select(c => c.Name)); // folders before repositories
        var group = Child(one, "group");
        Assert.Equal(["sub", "beta"], group.Children.Select(c => c.Name));
        Assert.Equal(["gamma9", "Gamma10"], Child(group, "sub").Children.Select(c => c.Name)); // natural, ignoring case
        Assert.Equal(["delta"], Child(repos, "two").Children.Select(c => c.Name));
        Assert.Equal(alpha, Child(one, "alpha").Path);
        Assert.Equal(Dir("one/group"), group.Path);

        // Both sections start expanded, folders collapsed; the expanded rows are remembered.
        Assert.True(recent.IsExpanded && repos.IsExpanded && !one.IsExpanded);
        one.IsExpanded = true;
        Assert.Contains(Dir("one"), Settings.Get<List<string>>(RepositoryManagerViewModel.ExpandedKey, []));
        m.Reload();
        Assert.True(Child(Section(m, RepositoryManagerViewModel.RepositoriesKey), "one").IsExpanded);

        // Initial selection: the first "Recent" entry. Typing a letter jumps to the next visible row starting with it.
        Assert.Equal(outside.Path, m.SelectedNode!.Path);
        m.JumpTo("a");
        Assert.Equal(alpha, m.SelectedNode!.Path);
        m.JumpTo("R");
        Assert.Equal("Recent", m.SelectedNode!.Name); // wraps around
        Close(main);

        // One source folder: no source node; folders sit directly under "Repositories".
        AppPreferences.SourceFolders = [Dir("one") + "/"];
        (main, m) = await StartAsync();
        repos = Section(m, RepositoryManagerViewModel.RepositoriesKey);
        Assert.Equal(["group", "alpha", "delta", Path.GetFileName(outside.Path)], repos.Children.Select(c => c.Name));
        Assert.Equal(beta, Child(Child(repos, "group"), "beta").Path);
        Close(main);
    }

    [AvaloniaFact]
    public async Task The_details_panel_shows_the_selected_repository()
    {
        using var repo = new TempRepo();
        repo.Commit("first", 0, ("readme.md", "# Hello\n"));
        repo.Commit("second subject", 1, ("a.txt", "1\n"));
        repo.Write("a.txt", "2\n");
        repo.Git("tag", "-a", "v1", "-m", "release one");
        repo.Git("remote", "add", "origin", "https://github.com/owner/repo.git");
        repo.Git("update-ref", "refs/remotes/origin/main", "HEAD");
        var gone = Dir("gone");
        RepositoryList.RecordOpened(repo.Path, DateTimeOffset.UtcNow);
        RepositoryList.Add([gone]);

        var (main, m) = await StartAsync();
        var d = m.Details!;
        Assert.Equal((Path.GetFileName(repo.Path), repo.Path, false), (d.Name, d.Path, d.NotFound));
        Assert.Equal((1, "2"), (d.Uncommitted, d.Commits));
        Assert.EndsWith("years ago", d.InitialCommit);
        Assert.EndsWith("years ago", d.LastCommit);
        var origin = Assert.Single(d.Remotes);
        Assert.Equal(("origin", "github.com", "https://github.com/owner/repo"), (origin.Name, origin.Host, origin.Page));
        Assert.Equal(("https://github.com/owner/repo/issues", "https://github.com/owner/repo/pulls"), (origin.IssuesPage, origin.PullRequestsPage));
        Assert.Equal(["origin/main"], d.Branches.Select(b => b.Name));
        Assert.Equal(("Test User", "second subject"), (d.Branches[0].Person, d.Branches[0].Subject));
        Assert.Equal([("v1", "Test User", "release one")], d.Tags.Select(t => (t.Name, t.Person, t.Subject)));
        Assert.Equal(["second subject", "first"], d.RecentCommits.Select(c => c.Subject));
        Assert.Equal("# Hello\n", d.Readme);

        var window = new MainWindow { DataContext = main };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var view = window.GetVisualDescendants().OfType<RepositoryManagerView>().Single();
        Assert.True(view.IsEffectivelyVisible);
        var texts = view.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
        Assert.Contains("Repository Manager", texts);
        Assert.Contains("Recent", texts);
        Assert.Contains("Uncommitted Files:", texts);
        Assert.Equal(d.Name, view.FindControl<TextBlock>("DetailsName")!.Text);
        Assert.Equal("1", view.FindControl<TextBlock>("UncommittedValue")!.Text);
        Assert.Contains(window.FindControl<ListBox>("TabStrip")!.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Repository Manager");
        Assert.Equal("Welcome to GerGit!", window.FindControl<TextBlock>("StatusTitle")!.Text);

        // A folder or section: the placeholder. A path that is gone: "Repository Not Found", the row gets a warning icon.
        m.Select(Section(m, RepositoryManagerViewModel.RepositoriesKey));
        await m.DetailsLoading;
        Assert.Null(m.Details);
        await m.Validating;
        var goneRow = Child(Section(m, RepositoryManagerViewModel.RepositoriesKey), "gone");
        Assert.False(goneRow.IsValid);
        m.Select(goneRow);
        await m.DetailsLoading;
        Assert.True(m.Details!.NotFound);
        Dispatcher.UIThread.RunJobs();
        Assert.True(view.FindControl<StackPanel>("NotFoundPanel")!.IsEffectivelyVisible);
        m.RemoveMissing(gone);
        Assert.Null(RepositoryList.Find(gone));
        Assert.Equal(repo.Path, m.SelectedNode!.Path); // the first repository
        window.Close();
        Close(main);
    }

    [AvaloniaFact]
    public async Task Opening_from_the_manager_records_last_opened_and_replaces_the_manager_tab()
    {
        var alpha = RepositoryScanTests.Init(_root, "src/alpha");
        RepositoryScanTests.Git(alpha, "-c", "user.name=t", "-c", "user.email=t@e", "commit", "-q", "--allow-empty", "-m", "x");
        AppPreferences.SourceFolders = [Dir("src") + "/"];
        var (main, m) = await StartAsync();
        var node = Child(Section(m, RepositoryManagerViewModel.RepositoriesKey), "alpha");
        Assert.Empty(Section(m, RepositoryManagerViewModel.RecentKey).Children);
        Assert.Null(RepositoryList.Find(alpha)!.LastOpened);

        var before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await m.OpenAsync(node);
        var tab = Assert.Single(main.Tabs);
        Assert.Equal(alpha, tab.Path);
        Assert.Null(main.Manager); // the Repository Manager tab is removed
        Assert.False(main.IsManagerShown);
        Assert.Equal([tab], main.StripTabs);
        Assert.InRange(RepositoryList.Find(alpha)!.LastOpened!.Value, before, before + 5);

        // A worktree is not recorded as opened.
        var wt = Dir("wt");
        RepositoryScanTests.Git(alpha, "worktree", "add", "-q", wt);
        Assert.Null(await main.OpenAsync(wt));
        Assert.Null(RepositoryList.Find(wt));

        // New Tab: the manager comes back at the end of the strip and is selected; the opened one is in "Recent".
        main.NewTab();
        Assert.True(main.IsManagerShown);
        Assert.Equal(main.Manager, main.StripTabs[^1]);
        Assert.Equal(main.Manager, main.SelectedStripTab);
        Assert.Equal(alpha, Section(main.Manager!, RepositoryManagerViewModel.RecentKey).Children.Single().Path);
        main.SelectedStripTab = main.Tabs[0];
        Assert.Equal(main.Tabs[0], main.SelectedTab);
        main.SelectedStripTab = main.Manager;
        main.CloseTab(null); // Ctrl+W on the manager tab: its left neighbour is selected
        Assert.Null(main.Manager);
        Assert.Equal(main.Tabs[^1], main.SelectedTab);
        foreach (var t in main.Tabs.ToList()) main.CloseTab(t);
        Assert.True(main.IsManagerShown); // no tab left: a Repository Manager tab opens

        // A folder that is gone offers to remove the entry.
        RepositoryList.Add([Dir("missing")]);
        main.Manager!.Reload();
        await main.Manager.OpenAsync(Child(Section(main.Manager, RepositoryManagerViewModel.RepositoriesKey), "missing"));
        Assert.Equal(("Repository Not Found", "Remove", "Close"), (_dialogs[^1].Title, _dialogs[^1].SubmitText, _dialogs[^1].CancelText));
        Assert.Equal($"It looks like 'missing' was deleted or moved. Do you want to remove the reference from GerGit?", _dialogs[^1].Description);
        Assert.Null(RepositoryList.Find(Dir("missing")));
        RepositoryScanTests.Git(alpha, "worktree", "remove", "--force", wt);
        Close(main);
    }

    [AvaloniaFact]
    public async Task The_menu_and_remove_follow_the_selection_and_rescan_brings_back_only_with_reset()
    {
        var alpha = RepositoryScanTests.Init(_root, "src/alpha");
        var omega = RepositoryScanTests.Init(_root, "src/omega");
        RepositoryScanTests.Init(_root, "src/group/beta");
        RepositoryScanTests.Init(_root, "src/group/gamma");
        RepositoryScanTests.Init(_root, "other/group/delta");
        AppPreferences.SourceFolders = [Dir("src") + "/", Dir("other") + "/"];
        var (main, m) = await StartAsync();
        RepoNode Src() => Child(Section(m, RepositoryManagerViewModel.RepositoriesKey), "src");

        string[] Labels(IEnumerable<MenuEntry>? menu) => menu!.Select(e => e.ToString()).ToArray();
        Assert.Equal(["Open", "Open In File Explorer", "-", "Rename", "Remove...", "-", "Rescan Repositories..."],
            Labels(m.Menu([Child(Src(), "alpha")])));
        Assert.Equal(["Open All (2)", "-", "Remove...", "-", "Rescan Repositories..."], Labels(m.Menu([Child(Src(), "group")])));
        Assert.Equal(["Remove...", "-", "Rescan Repositories..."], Labels(m.Menu([Child(Src(), "alpha"), Child(Src(), "omega")])));
        Assert.Equal(["Rescan Repositories..."], Labels(m.Menu([Src()])));
        Assert.Equal(["Rescan Repositories..."], Labels(m.Menu([Child(Src(), "alpha"), Child(Src(), "group")]))); // mixed
        Assert.Null(m.Menu([Section(m, RepositoryManagerViewModel.RecentKey)]));

        // Remove a repository: confirmed, gone, and ignored by the next scan.
        await m.RemoveAsync([Child(Src(), "alpha")]);
        Assert.Equal(("Do you want to remove the selected repository from GerGit?", "The repository will remain on disk", "Remove"),
            (_dialogs[^1].Title, _dialogs[^1].Description, _dialogs[^1].SubmitText));
        Assert.Null(RepositoryList.Find(alpha));
        Assert.Contains(alpha, RepositoryList.Ignored);
        Assert.True(Directory.Exists(alpha)); // nothing on disk is touched
        _answer = false;
        await m.RemoveAsync([Child(Src(), "omega")]); // cancelled
        Assert.NotNull(RepositoryList.Find(omega));
        _answer = true;

        // Remove a folder: the same relative folder is ignored below every source folder.
        await m.RemoveAsync([Child(Src(), "group")]);
        Assert.Equal(("Do you want to remove the selected folders from GerGit?", "Included repositories will be removed from GerGit."),
            (_dialogs[^1].Title, _dialogs[^1].Description));
        Assert.Equal([omega], RepositoryList.Entries.Select(e => e.Path));
        Assert.Contains(Dir("other/group"), RepositoryList.Ignored);
        Assert.Equal(omega, m.SelectedNode!.Path); // the first repository

        var rescan = new RescanViewModel(m);
        Assert.Equal($"Do you want to rescan repositories in '{Dir("src")}/', '{Dir("other")}/'?", rescan.Description);
        await rescan.SubmitAsync();
        Assert.Equal((DialogStatus.Success, "Done", true), (rescan.Status, rescan.StatusText, rescan.Result));
        Assert.Equal([omega], RepositoryList.Entries.Select(e => e.Path)); // the ignored ones stay away

        RepositoryList.SetAlias(omega, "Omega!");
        rescan = new RescanViewModel(m) { Reset = true };
        await rescan.SubmitAsync();
        Assert.Equal(5, RepositoryList.Entries.Count); // reset: everything found, aliases lost
        Assert.Null(RepositoryList.Find(omega)!.Alias);
        Assert.Null(Settings.Get<List<string>?>(RepositoryManagerViewModel.ExpandedKey, null));

        // Removing a source folder in Preferences (list first, then the entries) drops its entries without ignoring them.
        AppPreferences.SourceFolders = [Dir("src") + "/"];
        RepositoryList.RemoveBelow(Dir("other") + "/");
        Assert.DoesNotContain(RepositoryList.Entries, e => e.Path.StartsWith(Dir("other"), StringComparison.Ordinal));
        Close(main);
    }

    [AvaloniaFact]
    public async Task Rename_stores_an_alias_that_the_tab_title_uses()
    {
        using var repo = new TempRepo();
        repo.Commit("init", 0, ("a.txt", "1\n"));
        var main = new MainWindowViewModel();
        Assert.Null(await main.OpenAsync(repo.Path));
        var name = Path.GetFileName(repo.Path);
        main.NewTab();
        var m = main.Manager!;
        var node = Section(m, RepositoryManagerViewModel.RecentKey).Children.Single();

        m.BeginRename(node);
        Assert.Equal((true, name), (node.IsEditing, node.EditText));
        node.EditText = "Nice name";
        m.CommitRename(node);
        Assert.Equal("Nice name", RepositoryList.Find(repo.Path)!.Alias);
        Assert.Equal("Nice name", m.SelectedNode!.Name);
        Assert.Equal("Nice name", main.Tabs[0].Title);

        node = m.SelectedNode;
        m.BeginRename(node);
        node.EditText = name; // the automatic name clears the alias
        m.CommitRename(node);
        Assert.Null(RepositoryList.Find(repo.Path)!.Alias);
        Assert.Equal(name, main.Tabs[0].Title);
        Close(main);
    }

    [AvaloniaFact]
    public async Task Drops_add_repositories_or_open_them_and_a_closed_manager_stops_its_scan()
    {
        var alpha = RepositoryScanTests.Init(_root, "drop/alpha");
        RepositoryScanTests.Git(alpha, "-c", "user.name=t", "-c", "user.email=t@e", "commit", "-q", "--allow-empty", "-m", "x");
        var plain = Directory.CreateDirectory(Dir("drop/plain")).FullName;
        var (main, m) = await StartAsync();
        Assert.True(m.IsEmpty);

        var repos = Section(m, RepositoryManagerViewModel.RepositoriesKey);
        repos.IsExpanded = false;
        await m.DropAsync([alpha, plain], repos);
        Assert.Equal([alpha], RepositoryList.Entries.Select(e => e.Path)); // only valid repositories, nothing opened
        Assert.Empty(main.Tabs);
        Assert.True(Section(m, RepositoryManagerViewModel.RepositoriesKey).IsExpanded);
        Assert.False(m.IsEmpty);

        // Disposing the manager (its tab closed) cancels the background scan: nothing is added.
        RepositoryScanTests.Init(_root, "src/late");
        AppPreferences.SourceFolders = [Dir("src") + "/"];
        var other = new RepositoryManagerViewModel(main);
        other.Dispose();
        await other.Scanning;
        Assert.Null(RepositoryList.Find(Dir("src/late")));

        // A drop on a source folder or folder row opens the folder as a tab.
        AppPreferences.SourceFolders = [Dir("src") + "/", Dir("drop") + "/"];
        m.Reload();
        await m.DropAsync([alpha], Child(Section(m, RepositoryManagerViewModel.RepositoriesKey), "drop"));
        Assert.Equal([alpha], main.Tabs.Select(t => t.Path));
        Close(main);
    }

    [AvaloniaFact]
    public async Task The_README_is_read_only_from_a_regular_file_and_capped_and_a_folder_without_git_is_not_found()
    {
        using var repo = new TempRepo();
        repo.Commit("init", 0, ("a.txt", "1\n"));
        var readme = Path.Combine(repo.Path, "README.md");
        Task<RepoDetails> Load(string path) => RepoDetails.LoadAsync("x", path, DateTimeOffset.UtcNow, default);

        File.CreateSymbolicLink(readme, "/dev/zero"); // a link: never read
        Assert.Null((await Load(repo.Path)).Readme);
        File.Delete(readme);
        var mkfifo = System.Diagnostics.Process.Start("mkfifo", readme)!;
        await mkfifo.WaitForExitAsync();
        Assert.Null((await Load(repo.Path)).Readme); // a FIFO: not opened, nothing hangs
        File.Delete(readme);

        File.WriteAllText(readme, new string('a', RepoDetails.ReadmeLimit + 10));
        var text = (await Load(repo.Path)).Readme!;
        Assert.Equal(RepoDetails.ReadmeLimit + 1, text.Length);
        Assert.EndsWith("a…", text);
        File.WriteAllText(readme, new string('b', RepoDetails.ReadmeLimit));
        Assert.Equal(RepoDetails.ReadmeLimit, (await Load(repo.Path)).Readme!.Length); // exactly the limit: not cut

        // A folder whose .git is gone but which sits inside another repository: not found (not the parent's data).
        var inner = Directory.CreateDirectory(Path.Combine(repo.Path, "inner")).FullName;
        Assert.True((await Load(inner)).NotFound);
        RepositoryList.Add([inner]);
        var (main, m) = await StartAsync();
        await m.OpenAsync(Child(Section(m, RepositoryManagerViewModel.RepositoriesKey), "inner"));
        Assert.Equal("Repository Not Found", _dialogs[^1].Title);
        Assert.Empty(main.Tabs);
        Close(main);
    }

    [AvaloniaFact]
    public async Task A_background_scan_does_not_bring_back_what_was_removed_while_it_ran()
    {
        var alpha = RepositoryScanTests.Init(_root, "src/alpha");
        var beta = RepositoryScanTests.Init(_root, "src/beta");
        AppPreferences.SourceFolders = [Dir("src") + "/"];
        RepositoryList.Add([alpha, beta]);
        var main = new MainWindowViewModel { ShowDialog = _ => Task.FromResult(true) };
        main.NewTab(); // the scan starts in the background
        var m = main.Manager!;
        await m.RemoveAsync([Child(Section(m, RepositoryManagerViewModel.RepositoriesKey), "alpha")]);
        await m.Scanning;
        Assert.Equal([beta], RepositoryList.Entries.Select(e => e.Path));

        // A source folder removed while the scan ran: nothing from it is added.
        var gamma = RepositoryScanTests.Init(_root, "other/gamma");
        AppPreferences.SourceFolders = [Dir("src") + "/", Dir("other") + "/"];
        m.Refresh();
        AppPreferences.SourceFolders = [Dir("src") + "/"];
        await m.Scanning;
        Assert.Null(RepositoryList.Find(gamma));
        Close(main);
    }

    [AvaloniaFact]
    public async Task A_symlinked_source_folder_gives_one_entry_with_the_path_git_reports()
    {
        var real = Directory.CreateDirectory(Dir("real")).FullName;
        var alpha = RepositoryScanTests.Init(real, "src/alpha");
        RepositoryScanTests.Git(alpha, "-c", "user.name=t", "-c", "user.email=t@e", "commit", "-q", "--allow-empty", "-m", "x");
        var link = Dir("link");
        Directory.CreateSymbolicLink(link, real);
        var viaLink = Path.Combine(link, "src", "alpha");

        // Entries stored before links were resolved merge into the real path.
        Settings.Set(RepositoryList.EntriesKey, new List<RepoEntry> { new(viaLink, "Old"), new(alpha + "/", null, 5) });
        Assert.True(await RepositoryList.NormalizeStoredAsync());
        Assert.Equal([new RepoEntry(alpha, "Old", 5)], RepositoryList.Entries);
        Settings.Set<List<RepoEntry>?>(RepositoryList.EntriesKey, null);

        AppPreferences.SourceFolders = [Path.Combine(link, "src") + "/"];
        var (main, m) = await StartAsync();
        var node = Child(Section(m, RepositoryManagerViewModel.RepositoriesKey), "alpha"); // no source node, no folder
        Assert.Equal(alpha, node.Path);
        await m.OpenAsync(node);
        Assert.Equal(alpha, main.Tabs.Single().Path);
        var entry = Assert.Single(RepositoryList.Entries);
        Assert.Equal(alpha, entry.Path);
        Assert.NotNull(entry.LastOpened);

        RepositoryList.RecordOpened(viaLink, DateTimeOffset.UtcNow); // through the link: the same entry
        Assert.Single(RepositoryList.Entries);
        RepositoryList.SetAlias(viaLink, "Linked");
        main.UpdateTitles();
        Assert.Equal("Linked", main.Tabs[0].Title);
        Assert.Equal("Linked", RepositoryList.Find(viaLink)!.Alias);
        Close(main);
    }

    [AvaloniaFact]
    public async Task The_background_normalisation_repeats_when_the_list_changed_meanwhile()
    {
        using var a = new TempRepo();
        using var b = new TempRepo();
        Settings.Set(RepositoryList.EntriesKey, new List<RepoEntry> { new(a.Path + "/", "A") });
        using var gate = new ManualResetEventSlim();
        RepositoryList.Normalizer = p => { if (p.EndsWith('/')) gate.Wait(); return GerGit.Core.RepositoryScan.Normalize(p); };
        try
        {
            var pass = RepositoryList.NormalizeStoredAsync(); // waits on the gate in the background
            RepositoryList.Add([b.Path]);                      // the list changes meanwhile
            gate.Set();
            Assert.True(await pass);                           // a second pass over the new list
            Assert.Equal([new RepoEntry(a.Path, "A"), new RepoEntry(b.Path)], RepositoryList.Entries);
        }
        finally
        {
            gate.Set();
            RepositoryList.Normalizer = GerGit.Core.RepositoryScan.Normalize;
        }
    }

    [AvaloniaFact]
    public async Task A_git_error_other_than_not_found_shows_its_text_and_keeps_Open()
    {
        using var repo = new TempRepo();
        repo.Commit("init", 0, ("a.txt", "1\n"));
        File.AppendAllText(Path.Combine(repo.Path, ".git", "config"), "\n[broken\n");
        var d = await RepoDetails.LoadAsync("x", repo.Path, DateTimeOffset.UtcNow, default);
        Assert.False(d.NotFound);
        Assert.True(d.Found && d.HasError && !d.HasData);
        Assert.Contains("bad config", d.Error);
    }

    [Fact]
    public void Only_gits_own_answer_means_not_a_repository()
    {
        Assert.Equal("The '/x' directory is not a git repository", GerGit.Core.Git.GitRepository.OpenFailure("/x",
            new(128, "", "fatal: not a git repository (or any of the parent directories): .git\n")).Message);
        Assert.Equal("The '/x' directory is not a git repository", GerGit.Core.Git.GitRepository.OpenFailure("/x",
            new(128, "", "fatal: not a git repository (or any parent up to mount point /)\n")).Message);
        // A .git file pointing nowhere is a broken repository, not "no repository": git's text in both wordings.
        Assert.StartsWith("fatal: not a git repository: /x/missing", GerGit.Core.Git.GitRepository.OpenFailure("/x",
            new(128, "", "fatal: not a git repository: /x/missing\n")).Message);
        Assert.StartsWith("fatal: gitfile does not point to a valid repository", GerGit.Core.Git.GitRepository.OpenFailure("/x",
            new(128, "", "fatal: gitfile does not point to a valid repository: /x/missing\n")).Message);
        Assert.Equal("git rev-parse failed in '/x' (exit status 137)",
            GerGit.Core.Git.GitRepository.OpenFailure("/x", new(137, "", "")).Message); // killed: kept by the restore
        Assert.StartsWith("fatal: cannot change to", GerGit.Core.Git.GitRepository.OpenFailure("/x",
            new(128, "", "fatal: cannot change to '/x': Permission denied\n")).Message);
    }

    [AvaloniaFact]
    public async Task Malformed_entries_nested_source_folders_and_no_source_folders_are_handled()
    {
        using var repo = new TempRepo();
        repo.Commit("init", 0, ("a.txt", "1\n"));

        // A hand-edited entry without a path (or a null entry) is left out.
        Settings.Set(RepositoryList.EntriesKey, new List<RepoEntry?> { new(null!, "x"), null, new(""), new(repo.Path, "Alias") });
        Assert.Equal([new RepoEntry(repo.Path, "Alias")], RepositoryList.Entries);
        var main = new MainWindowViewModel { ShowDialog = _ => Task.FromResult(true) };
        Assert.Null(await main.OpenAsync(repo.Path)); // titles and "last opened" work
        Assert.Equal("Alias", main.Tabs[0].Title);

        // Nested source folders: removing the inner one keeps entries that are still below the outer one.
        var inner = RepositoryScanTests.Init(_root, "outer/inner/x");
        var lone = RepositoryScanTests.Init(_root, "lone/y");
        RepositoryList.Add([inner, lone]);
        RepositoryList.SetAlias(inner, "Kept");
        AppPreferences.SourceFolders = [Dir("outer") + "/"]; // "outer/inner/" and "lone/" were removed in Preferences
        RepositoryList.RemoveBelow(Dir("outer/inner") + "/");
        RepositoryList.RemoveBelow(Dir("lone") + "/");
        Assert.Equal("Kept", RepositoryList.Find(inner)!.Alias);
        Assert.Null(RepositoryList.Find(lone));

        // No source folders: no Rescan item, and a reset rescan cannot empty the list.
        AppPreferences.SourceFolders = [];
        main.NewTab();
        var m = main.Manager!;
        var repos = Section(m, RepositoryManagerViewModel.RepositoriesKey);
        Assert.Equal(["Open", "Open In File Explorer", "-", "Rename", "Remove..."],
            m.Menu([repos.Children.First(c => c.IsRepository)])!.Select(e => e.ToString()));
        Assert.Null(m.Menu([repos]));
        var count = RepositoryList.Entries.Count;
        await new RescanViewModel(m) { Reset = true }.SubmitAsync();
        Assert.Equal(count, RepositoryList.Entries.Count);
        Close(main);
    }
}
