using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia;
using Avalonia.VisualTree;
using GerGit.Controls;
using GerGit.ViewModels;
using GerGit.Views;

namespace GerGit.Tests.Ui;

public class ShellSmokeTests
{
    [AvaloniaFact]
    public async Task Opening_a_repository_shows_tab_sidebar_and_local_changes()
    {
        using var repo = new TempRepo();
        repo.Commit("first", 1, ("a.txt", "a"));
        repo.Git("tag", "v1.0");
        repo.Git("branch", "feature/login");
        repo.Write("b.txt", "untracked");
        repo.Write("a.txt", "modified"); // a racily clean, modified file made git status rewrite the index

        var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm };
        window.Show();
        Assert.Null(await vm.OpenAsync(repo.Path));

        var tab = Assert.Single(vm.Tabs);
        Assert.Equal(Path.GetFileName(repo.Path), tab.Title);
        tab.Sidebar.Roots.Single(n => n.Title == "Tags").IsExpanded = true; // Tags start collapsed
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        var texts = window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).ToList();
        var tabStrip = window.FindControl<ListBox>("TabStrip")!;
        Assert.Contains(tabStrip.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == tab.Title);

        var sidebar = window.GetVisualDescendants().OfType<SidebarView>().Single();
        var branchRow = Assert.Single(sidebar.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "main");
        Assert.Equal(FontWeight.Bold, branchRow.FontWeight);
        var rowIcons = branchRow.GetVisualParent()!.GetVisualDescendants().OfType<LucideIcon>();
        Assert.Contains(rowIcons, i => i.Icon == "check");
        Assert.Contains(texts, t => t.Text == "feature"); // feature/login becomes a folder row
        var folder = tab.Sidebar.Roots.Single(n => n.Title == "Branches").Children[0];
        Assert.Equal(("feature", "login"), (folder.Title, folder.Children.Single().Title)); // folders before leaves
        Assert.Contains(texts, t => t.Text == "v1.0");
        Assert.Contains(texts, t => t.Text == "Local Changes (2)");
        Assert.Equal("main", window.FindControl<TextBlock>("StatusDescription")!.Text);

        // Selecting the tag hands its commit to the commit list.
        tab.Sidebar.SelectedNode = tab.Sidebar.Roots.Single(n => n.Title == "Tags").Children.Single();
        Assert.Equal(repo.Git("rev-parse", "HEAD"), tab.CommitList.SelectedSha);

        // An idle repository must settle: the first status may rewrite the index (one lock-free refresh follows),
        // but that refresh must not write the index again.
        int refreshes = 0;
        tab.PropertyChanged += (_, e) => refreshes += e.PropertyName == nameof(tab.Refs) ? 1 : 0;
        var settleEnd = DateTime.UtcNow.AddSeconds(5); // until no Refs change for 500 ms and no refresh is running
        for (int seen = -1; (seen != refreshes || !tab.Refreshing.IsCompleted) && DateTime.UtcNow < settleEnd;)
        {
            seen = refreshes;
            await Task.Delay(500);
        }
        refreshes = 0;
        await Task.Delay(1500);
        Assert.Equal(0, refreshes);

        // At the default 1000 x 600 no toolbar button is clipped or hidden under the status box.
        var statusBox = window.FindControl<TextBlock>("StatusTitle")!.FindAncestorOfType<Border>()!;
        double boxLeft = statusBox.TranslatePoint(default, window)!.Value.X, boxRight = boxLeft + statusBox.Bounds.Width;
        foreach (var b in window.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("toolbar") || b.Classes.Contains("toolbar-arrow")))
        {
            double left = b.TranslatePoint(default, window)!.Value.X, right = left + b.Bounds.Width;
            Assert.True(left >= 0 && right <= window.Bounds.Width, $"toolbar button at {left}-{right} outside the window");
            Assert.True(right <= boxLeft || left >= boxRight, $"toolbar button at {left}-{right} under the status box");
        }
        var ws = window.FindControl<Button>("WorkspacesButton")!;
        Assert.True(ws.TranslatePoint(new(ws.Bounds.Width, 0), window)!.Value.X <= 1000);

        vm.CloseTab(tab);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Welcome to GerGit!", window.FindControl<TextBlock>("StatusTitle")!.Text);
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_long_branch_name_is_trimmed_inside_the_status_box_and_a_short_one_stays_centred()
    {
        using var shortRepo = new TempRepo();
        shortRepo.Commit("first", 1, ("a.txt", "a"));
        using var longRepo = new TempRepo();
        longRepo.Commit("first", 1, ("a.txt", "a"));
        var longName = "feature/" + new string('x', 120);
        longRepo.Git("checkout", "-q", "-b", longName);

        var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm };
        window.Show();
        var box = window.FindControl<Border>("StatusBox")!;
        var description = window.FindControl<TextBlock>("StatusDescription")!;
        double Centre(Visual v) => v.TranslatePoint(new(v.Bounds.Width / 2, 0), window)!.Value.X;

        Assert.Null(await vm.OpenAsync(shortRepo.Path));
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Assert.Equal("main", description.Text);
        Assert.False(TrimmedToolTip.IsTrimmed(description));
        Assert.InRange(Centre(description.GetVisualParent()!) - Centre(box), -1, 1); // icon and name centred

        Assert.Null(await vm.OpenAsync(longRepo.Path));
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Assert.Equal(longName, description.Text);
        double right = description.TranslatePoint(new(description.Bounds.Width, 0), box)!.Value.X;
        Assert.True(right <= box.Bounds.Width, $"the branch name ends at {right}, the box at {box.Bounds.Width}");
        Assert.True(TrimmedToolTip.IsTrimmed(description));
        Assert.Equal(longName, ToolTip.GetTip(description)); // the full name in the tooltip
        window.Close();
    }

    [AvaloniaFact]
    public async Task Sidebar_truncates_tags_shows_remotes_and_stashes_and_filters()
    {
        using var repo = new TempRepo();
        repo.Commit("first", 1, ("a.txt", "a"));
        for (int i = 1; i <= 25; i++) repo.Git("tag", $"v{i}");
        repo.Git("branch", "feature/login");
        repo.Git("remote", "add", "origin", "https://example.com/x.git");
        repo.Git("update-ref", "refs/remotes/origin/main", "HEAD");
        repo.Git("branch", "--set-upstream-to=origin/main", "main");
        repo.Git("branch", "local-up");
        repo.Git("branch", "--set-upstream-to=main", "local-up"); // upstream is a local branch
        repo.Commit("second", 2, ("a.txt", "b"));
        repo.Write("c.txt", "x");
        repo.Git("stash", "-u");

        var vm = new MainWindowViewModel();
        Assert.Null(await vm.OpenAsync(repo.Path));
        using var tab = vm.SelectedTab!; // disposed before the repo: no watcher stays open (each one holds an inotify instance)
        var sidebar = tab.Sidebar;
        SidebarNode Section(string title) => sidebar.Roots.Single(n => n.Title == title);

        Assert.Equal("1↑", Section("Branches").Children.Single(n => n.Title == "main").AheadBehind);
        Assert.StartsWith("Local branch:\tlocal-up", Section("Branches").Children.Single(n => n.Title == "local-up").ToolTip);
        var origin = Section("Remotes").Children.Single();
        Assert.Equal(("origin", "https://example.com/x.git", "main"), (origin.Title, origin.ToolTip, origin.Children.Single().Title));
        Assert.StartsWith("WIP on main:", Section("Stashes").Children.Single().Title);

        // 25 tags with equal dates: 20 shown in natural order, then the "Show all" row.
        var tags = Section("Tags").Children;
        Assert.Equal(21, tags.Count);
        Assert.Equal(["v1", "v2", "v3"], tags.Take(3).Select(t => t.Title));
        Assert.Equal("Show all tags (25)", tags[^1].Title);
        sidebar.ToggleShowAll(tags[^1]);
        Assert.Equal(26, Section("Tags").Children.Count);
        Assert.Equal("Show less", Section("Tags").Children[^1].Title);

        // Filter: case-insensitive substring on the full name; folders kept, everything expanded, empty sections hidden.
        sidebar.FilterText = "LOG";
        await Task.Delay(400); // 100 ms debounce
        var branches = Assert.Single(sidebar.Roots);
        var folder = branches.Children.Single();
        Assert.True(folder.IsExpanded);
        Assert.Equal("login", folder.Children.Single().Title);
        sidebar.FilterText = "";
        await Task.Delay(400);
        Assert.False(Section("Branches").Children.Single(n => n.Title == "feature").IsExpanded);

        // File watching: a branch created outside the app shows up after the debounce.
        repo.Git("branch", "external");
        for (int i = 0; i < 50 && !Section("Branches").Children.Any(n => n.Title == "external"); i++) await Task.Delay(100);
        Assert.Contains(Section("Branches").Children, n => n.Title == "external");
    }

    [AvaloniaFact]
    public async Task Status_failure_is_silent_and_does_not_block_the_sidebar()
    {
        using var repo = new TempRepo();
        repo.Commit("first", 1, ("a.txt", "a"));
        File.WriteAllText(Path.Combine(repo.Path, ".git", "index"), "garbage"); // git status fails, refs still read

        var vm = new MainWindowViewModel();
        Assert.Null(await vm.OpenAsync(repo.Path));
        using var tab = vm.SelectedTab!; // disposed before the repo: no watcher stays open (each one holds an inotify instance)
        Assert.True(tab.IsLoaded);
        Assert.Equal("main", tab.StatusDescription);
        Assert.Contains(tab.Sidebar.Roots.Single(n => n.Title == "Branches").Children, n => n.Title == "main");
        Assert.Equal("Local Changes", tab.LocalChangesText);
    }

    [AvaloniaFact]
    public async Task Rebuild_moves_selection_to_the_row_above_a_removed_one()
    {
        using var repo = new TempRepo();
        repo.Commit("first", 1, ("a.txt", "a"));
        repo.Git("branch", "b1");
        repo.Git("branch", "b2");
        var vm = new MainWindowViewModel();
        Assert.Null(await vm.OpenAsync(repo.Path));
        using var tab = vm.SelectedTab!; // disposed before the repo: no watcher stays open (each one holds an inotify instance)
        var sidebar = tab.Sidebar;
        sidebar.SelectedNode = sidebar.Roots[0].Children.Single(n => n.Title == "b2");

        repo.Git("branch", "-D", "b2");
        await tab.RefreshAsync();
        Assert.Equal("b1", sidebar.SelectedNode?.Title);
    }

    [AvaloniaFact]
    public async Task Keyboard_passes_the_show_all_row_by_and_a_rebuild_keeps_the_tree_focused()
    {
        using var repo = new TempRepo();
        repo.Commit("first", 1, ("a.txt", "a"));
        for (int i = 1; i <= 25; i++) repo.Git("tag", $"v{i}");
        repo.Write("a.txt", "dirty");
        repo.Git("stash", "-q");
        var vm = new MainWindowViewModel();
        Assert.Null(await vm.OpenAsync(repo.Path));
        vm.SelectedTab!.IsActive = false; // no watcher refreshes in between
        var sidebar = vm.SelectedTab.Sidebar;
        SidebarNode Tags() => sidebar.Roots.Single(n => n.Title == "Tags");
        Tags().IsExpanded = true;
        var view = new SidebarView { DataContext = sidebar };
        var window = new Window { Width = 300, Height = 1000, Content = view };
        window.Show();
        var tree = view.FindControl<TreeView>("Tree")!;
        sidebar.SelectedNode = Tags().Children[19];
        window.UpdateLayout();
        tree.TreeContainerFromItem(sidebar.SelectedNode)!.Focus();

        // Down jumps over the "Show all" row to the next section, Up comes back the same way.
        window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        Assert.Equal("Stashes", sidebar.SelectedNode!.Title);
        Assert.Equal(21, Tags().Children.Count); // not toggled
        Assert.True(tree.IsKeyboardFocusWithin);
        window.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
        Assert.Equal("v20", sidebar.SelectedNode!.Title);
        Assert.True(tree.IsKeyboardFocusWithin);

        // A rebuild (e.g. a watcher refresh) replaces the rows; the focus goes back to the selected one.
        var selected = sidebar.SelectedNode;
        sidebar.Rebuild();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(selected.Key, sidebar.SelectedNode?.Key);
        Assert.Same(tree.TreeContainerFromItem(sidebar.SelectedNode!), TopLevel.GetTopLevel(tree)!.FocusManager!.GetFocusedElement());

        // A click toggles the row without selecting it.
        var showAll = (Control)tree.TreeContainerFromItem(Tags().Children[^1])!;
        var at = showAll.TranslatePoint(new Point(40, 11), window)!.Value;
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Assert.Equal(26, Tags().Children.Count);
        Assert.Equal(selected.Key, sidebar.SelectedNode?.Key);
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_moved_repo_folder_reports_not_found_once_until_a_refresh_succeeds()
    {
        using var repo = new TempRepo();
        repo.Commit("first", 1, ("a.txt", "a"));
        var vm = new MainWindowViewModel();
        Assert.Null(await vm.OpenAsync(repo.Path));
        var tab = vm.SelectedTab!;
        tab.IsActive = false; // no watcher refreshes in between
        var errors = new List<string>();
        tab.ShowError = (_, text) => { errors.Add(text); return Task.CompletedTask; };

        Directory.Move(repo.Path, repo.Path + "-moved");
        await tab.RefreshAsync();
        await tab.RefreshAsync(); // e.g. the next window activation
        Assert.Equal([$"It looks like '{tab.Path}' was deleted or moved."], errors);

        Directory.Move(repo.Path + "-moved", repo.Path);
        await tab.RefreshAsync();
        Directory.Move(repo.Path, repo.Path + "-moved");
        await tab.RefreshAsync();
        Directory.Move(repo.Path + "-moved", repo.Path);
        Assert.Equal(2, errors.Count); // shown again after a successful refresh
    }
}
