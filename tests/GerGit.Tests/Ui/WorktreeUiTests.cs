using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GerGit.Controls;
using GerGit.Core;
using GerGit.ViewModels;
using GerGit.ViewModels.Dialogs;
using GerGit.ViewModels.Dialogs.Refs;
using GerGit.Views.Dialogs;

namespace GerGit.Tests.Ui;

/// <summary>Worktrees through the view models (sidebar section and menus, dialogs,
/// worktree tabs). In the non-parallel "RefOps" collection: the global "worktrees seen" flag is reset afterwards.</summary>
[Collection("RefOps")]
public sealed class WorktreeUiTests
{
    sealed class Fx : IDisposable
    {
        public readonly TempRepo Repo = new();
        public readonly MainWindowViewModel Main = new();
        public RepoTabViewModel Tab = null!;
        public readonly List<DialogViewModel> Dialogs = [];
        public readonly List<string> Errors = [];
        public readonly List<string> Copied = [];
        public Func<DialogViewModel, bool> Fill = _ => true;

        public Fx()
        {
            Settings.Set<string?>(RepoTabViewModel.ShowWorktreesKey, null);
            Repo.Commit("init", 0, ("a.txt", "1\n"));
        }

        public string Container => Repo.Path + "-worktrees";
        public string Wt(string name) => Path.Combine(Container, name);

        public async Task OpenAsync()
        {
            Assert.Null(await Main.OpenAsync(Repo.Path));
            Tab = Main.SelectedTab!;
            Hook(Tab);
        }

        /// <summary>Records errors and copies of <paramref name="t"/>; its dialogs go through <see cref="Fill"/>.</summary>
        public void Hook(RepoTabViewModel t)
        {
            t.ShowError = (_, text) => { Errors.Add(text); return Task.CompletedTask; };
            t.ShowGitError = (r, _) => { Errors.Add(r.ReadableOutput); return Task.CompletedTask; };
            t.ShowDialog = async vm =>
            {
                Dialogs.Add(vm);
                if (!Fill(vm)) { vm.Cancel(); return false; }
                await vm.SubmitAsync();
                return vm.Result == true;
            };
            t.Sidebar.Copy = text => { Copied.Add(text); return Task.CompletedTask; };
        }

        public async Task RefreshAsync(RepoTabViewModel? tab = null)
        {
            tab ??= Tab;
            await tab.RefreshAsync();
            while (!tab.Refreshing.IsCompleted) await tab.Refreshing;
        }

        public Job LastJob => Tab.Jobs.History[0];
        public GitRef Ref(string fullName) => Tab.Refs.Refs.Single(r => r.FullName == fullName);
        public SidebarNode Section(RepoTabViewModel tab, string title) => tab.Sidebar.Roots.Single(r => r.Title == title);

        public void Dispose()
        {
            foreach (var t in Main.Tabs.ToList()) Main.CloseTab(t);
            Settings.Set<string?>(RepoTabViewModel.ShowWorktreesKey, null);
            if (Directory.Exists(Container)) Directory.Delete(Container, recursive: true);
            Repo.Dispose();
        }
    }

    static string Flat(IEnumerable<MenuEntry>? entries) =>
        entries is null ? "<none>" : string.Join("|", entries.Select(e => e.Kind == MenuEntryKind.Separator ? "-" : e.IsEnabled ? e.Header : e.Header + " (disabled)"));

    static Task Click(IEnumerable<MenuEntry>? menu, string header) => menu!.Single(e => e.Header == header).Invoke();

    [AvaloniaFact]
    public async Task Sidebar_section_menus_double_click_and_worktree_tab()
    {
        using var f = new Fx();
        var sha = f.Repo.Git("rev-parse", "HEAD");
        f.Repo.Git("branch", "feat");
        f.Repo.Git("branch", "free");
        f.Repo.Git("worktree", "add", "-q", f.Wt("feat"), "feat");
        f.Repo.Git("worktree", "add", "-q", "--detach", f.Wt("det"));
        await f.OpenAsync();
        var sb = f.Tab.Sidebar;

        // Main worktree's tab: the section on top, expanded, the linked worktrees directly below it.
        var section = sb.Roots[0];
        Assert.Equal(("Worktrees", true), (section.Title, section.IsExpanded));
        Assert.Equal(["det", "feat"], section.Children.Select(n => n.Title));
        var (det, feat) = (section.Children[0], section.Children[1]);
        Assert.Equal(("folder-git-2", false), (feat.Icon, feat.IsBold));
        Assert.Equal($"Worktree:\tfeat\nLocation:\t\t{f.Wt("feat")}\nBranch:\t\tfeat", feat.ToolTip);
        Assert.EndsWith($"\nHEAD:\t\t{sha[..7]}", det.ToolTip);
        Assert.True(Settings.Get(RepoTabViewModel.ShowWorktreesKey, false)); // Worktrees seen

        // Selecting a worktree selects its HEAD commit.
        sb.Select([feat]);
        Assert.Equal(sha, f.Tab.CommitList.SelectedSha);

        Assert.Equal("New Worktree...", Flat(await sb.BuildMenuAsync(section)));
        Assert.Equal("Open 'feat'...|Show in File Explorer|-|Delete 'feat'...|-|Copy Path", Flat(await sb.BuildMenuAsync(feat)));
        sb.Select([det, feat]);
        var multi = await sb.BuildMenuAsync(feat);
        Assert.Equal("Open 2 worktrees...|-|Copy Path", Flat(multi));
        await Click(multi, "Copy Path");
        Assert.Equal($"{f.Wt("det")}\n{f.Wt("feat")}", f.Copied[^1]);

        // Branch menus: checked out in a linked worktree → open it; in the main one → nothing; free → both checkouts.
        Assert.StartsWith("Open 'feat' Worktree|-|", Flat(await sb.BuildMenuAsync(sb.Roots.Single(r => r.Title == "Branches").Children.Single(n => n.Title == "feat"))));
        Assert.StartsWith("Checkout...|Checkout as Worktree...|-|", Flat(Menus.LocalBranch(f.Tab, f.Ref("refs/heads/free"), _ => Task.CompletedTask)));
        Assert.DoesNotContain("Checkout", Flat(Menus.LocalBranch(f.Tab, f.Ref("refs/heads/main"), _ => Task.CompletedTask)));

        // Double-click on a linked worktree opens it as a tab right after this one, titled "<main>: <name>".
        await sb.DoubleClickAsync(feat);
        Assert.Equal(2, f.Main.Tabs.Count);
        var wtTab = f.Main.SelectedTab!;
        Assert.Equal((f.Wt("feat"), 1), (wtTab.Path, f.Main.Tabs.IndexOf(wtTab)));
        Assert.Equal($"{Path.GetFileName(f.Repo.Path)}: feat", wtTab.Title);

        // The linked worktree's tab: a node for the main worktree with all linked ones below, the current one bold;
        // deleting the current one is not offered.
        await f.RefreshAsync(wtTab);
        var mainNode = Assert.Single(f.Section(wtTab, "Worktrees").Children);
        Assert.Equal((Path.GetFileName(f.Repo.Path), false, true), (mainNode.Title, mainNode.IsBold, mainNode.IsExpanded));
        Assert.Equal([("det", false), ("feat", true)], mainNode.Children.Select(n => (n.Title, n.IsBold)));
        Assert.Contains("Delete 'feat'... (disabled)", Flat(await wtTab.Sidebar.BuildMenuAsync(mainNode.Children[1])));
        Assert.Contains($"Delete '{mainNode.Title}'...|", Flat(await wtTab.Sidebar.BuildMenuAsync(mainNode)));

        // Checkout of a branch that another worktree has: that worktree's tab is selected, no git step.
        int jobs = f.Tab.Jobs.History.Count;
        f.Main.SelectedTab = f.Tab;
        await f.Tab.RefOps.CheckoutBranchAsync(f.Ref("refs/heads/feat"));
        Assert.Same(wtTab, f.Main.SelectedTab);
        Assert.Equal(jobs, f.Tab.Jobs.History.Count);
        Assert.Empty(f.Errors);
    }

    [AvaloniaFact]
    public async Task New_worktree_dialog_new_branch_existing_branch_and_remote_branch()
    {
        using var f = new Fx();
        var sha = f.Repo.Git("rev-parse", "HEAD");
        f.Repo.Git("branch", "dev");
        using var bare = new TempRepo(init: false);
        bare.Git("init", "-q", "--bare", "-b", "main");
        f.Repo.Git("remote", "add", "origin", bare.Path);
        f.Repo.Git("push", "-q", "origin", "main:rb");
        f.Repo.Git("fetch", "-q", "origin");
        await f.OpenAsync();
        Assert.Empty(f.Tab.Worktrees);
        Assert.False(f.Tab.WorktreesEnabled);
        Assert.DoesNotContain("Checkout as Worktree...", Flat(Menus.LocalBranch(f.Tab, f.Ref("refs/heads/dev"), _ => Task.CompletedTask)));

        f.Fill = d =>
        {
            var vm = (CreateWorktreeViewModel)d;
            Assert.Equal(("Create Worktree", "Create branch and checkout it in a separate worktree", "Create", 590.0),
                (vm.Title, vm.Description, vm.SubmitText, vm.Width));
            Assert.Equal(["dev", "main"], vm.Starts.Select(s => s.Text));
            Assert.Equal(("main", f.Container, false), (vm.Start!.Text, vm.Location, vm.SubmitEnabled));
            vm.Name = "dev";
            Assert.Equal(("Branch 'dev' already exists", false), (vm.StatusText, vm.SubmitEnabled));
            vm.Name = "main";
            Assert.Equal("Worktree 'main' already exists", vm.StatusText);
            vm.Name = "a..b";
            Assert.Equal("Name cannot contain '..'", vm.StatusText);
            vm.Name = "feature/a";
            Assert.Equal((f.Wt("feature-a"), true, ""), (vm.Location, vm.SubmitEnabled, vm.StatusText));
            vm.Container = "/elsewhere";
            Assert.Equal("/elsewhere/feature-a", vm.Location);
            vm.Container = f.Container;
            return true;
        };
        await Click(Menus.BranchArrow(f.Tab), "New Worktree...");
        Assert.Empty(f.Errors);
        Assert.Equal(("Create Worktree", "Created 'feature/a' worktree"), (f.LastJob.Name, f.LastJob.Message));
        Assert.Contains($"$ git worktree add -b feature/a {f.Wt("feature-a")} {sha}", f.LastJob.Output);
        Assert.Equal("feature/a", f.Repo.Git("-C", f.Wt("feature-a"), "rev-parse", "--abbrev-ref", "HEAD"));
        Assert.Equal((2, f.Wt("feature-a")), (f.Main.Tabs.Count, f.Main.SelectedTab!.Path));
        Assert.Equal(2, f.Tab.Worktrees.Count);
        Assert.True(f.Tab.WorktreesEnabled);

        // Existing local branch: the sidebar menu now offers "Checkout as Worktree...".
        f.Fill = d =>
        {
            var vm = (CreateWorktreeViewModel)d;
            Assert.Equal(("Checkout Branch as Worktree", "Local Branch:", "dev", f.Wt("dev"), true),
                (vm.Title, vm.BranchLabel, vm.Branch!.Text, vm.Location, vm.SubmitEnabled));
            return true;
        };
        await Click(Menus.LocalBranch(f.Tab, f.Ref("refs/heads/dev"), _ => Task.CompletedTask), "Checkout as Worktree...");
        Assert.Empty(f.Errors);
        Assert.Equal(("Checkout Branch As Worktree", "Created 'dev' worktree"), (f.LastJob.Name, f.LastJob.Message));
        Assert.Contains($"$ git worktree add {f.Wt("dev")} dev", f.LastJob.Output);
        Assert.Equal([f.Repo.Path, f.Wt("dev"), f.Wt("feature-a")], f.Main.Tabs.Select(t => t.Path)); // right after the origin tab

        // Remote branch without a tracking branch: a new tracking branch named like it.
        f.Fill = d =>
        {
            var vm = (CreateWorktreeViewModel)d;
            Assert.Equal(("Checkout Remote Branch as Worktree", "Remote Branch:", "origin/rb", f.Wt("rb")),
                (vm.Title, vm.BranchLabel, vm.Branch!.Text, vm.Location));
            return true;
        };
        await Click(Menus.RemoteBranch(f.Tab, f.Ref("refs/remotes/origin/rb"), _ => Task.CompletedTask), "Checkout as Worktree...");
        Assert.Empty(f.Errors);
        Assert.Contains($"$ git worktree add --track -b rb {f.Wt("rb")} origin/rb", f.LastJob.Output);
        Assert.Equal("refs/remotes/origin/rb", f.Ref("refs/heads/rb").Upstream);
        Assert.Equal(4, f.Tab.Worktrees.Count);
        // Now checked out in a linked worktree: the remote branch menu opens that worktree instead.
        Assert.StartsWith("Open 'rb' Worktree|-|", Flat(Menus.RemoteBranch(f.Tab, f.Ref("refs/remotes/origin/rb"), _ => Task.CompletedTask)));
    }

    [AvaloniaFact]
    public async Task Delete_worktree_and_branch_with_also_delete_worktree()
    {
        using var f = new Fx();
        f.Repo.Git("worktree", "add", "-q", "-b", "x", f.Wt("x"));
        f.Repo.Git("worktree", "add", "-q", "-b", "y", f.Wt("y"));
        f.Repo.Git("worktree", "add", "-q", "-b", "upper", f.Wt("X"));
        await f.OpenAsync();
        await f.Tab.WorktreeOps.OpenAsync([f.Wt("x")]);
        Assert.Equal(2, f.Main.Tabs.Count);
        f.Main.SelectedTab = f.Tab;

        // Removing X leaves the open tab of x alone: paths differ by case only (Linux).
        await Click(await f.Tab.Sidebar.BuildMenuAsync(f.Section(f.Tab, "Worktrees").Children.Single(n => n.Title == "X")), "Delete 'X'...");
        Assert.Empty(f.Errors);
        Assert.False(Directory.Exists(f.Wt("X")));
        Assert.Equal(2, f.Main.Tabs.Count);

        var section = f.Section(f.Tab, "Worktrees");
        f.Fill = d =>
        {
            Assert.IsType<DeleteWorktreeViewModel>(d);
            Assert.Equal(("Are you sure you want to delete worktree x?", $"Do you want to delete worktree {f.Wt("x")}?", "Delete", 590.0),
                (d.Title, d.Description, d.SubmitText, d.Width));
            return true;
        };
        await Click(await f.Tab.Sidebar.BuildMenuAsync(section.Children.Single(n => n.Title == "x")), "Delete 'x'...");
        Assert.Empty(f.Errors);
        Assert.Equal("Delete worktree 'x'", f.LastJob.Name);
        Assert.Contains($"$ git worktree remove --force {f.Wt("x")}", f.LastJob.Output);
        Assert.False(Directory.Exists(f.Wt("x")));
        Assert.Equal([f.Tab], f.Main.Tabs); // its tab is closed

        // Delete Branch on a branch checked out in another worktree offers "Also delete worktree".
        f.Fill = d =>
        {
            var vm = (DeleteRefsViewModel)d;
            Assert.Equal(("y", true), (vm.WorktreeName, vm.HasWorktreeCheck));
            vm.DeleteWorktree = true;
            return true;
        };
        await f.Tab.RefOps.DeleteBranchesAsync([f.Ref("refs/heads/y")]);
        Assert.Empty(f.Errors);
        Assert.Contains($"$ git worktree remove --force {f.Wt("y")}", f.LastJob.Output);
        Assert.False(Directory.Exists(f.Wt("y")));
        Assert.DoesNotContain(f.Tab.Refs.Refs, r => r.FullName == "refs/heads/y");
        Assert.Empty(f.Tab.Worktrees);
        Assert.DoesNotContain(f.Tab.Sidebar.Roots, r => r.Title == "Worktrees"); // the section goes with the last worktree

        // Without a worktree the option is not there.
        f.Repo.Git("branch", "z");
        await f.RefreshAsync();
        f.Fill = d => { Assert.False(((DeleteRefsViewModel)d).HasWorktreeCheck); return false; };
        await f.Tab.RefOps.DeleteBranchesAsync([f.Ref("refs/heads/z")]);
    }

    /// <summary>The dialogs in real windows: Create Worktree focuses the name box; the checkout variant shows the branch
    /// row; Delete Branch shows the worktree check box with the folder name.</summary>
    [AvaloniaFact]
    public async Task Dialog_views_render()
    {
        using var f = new Fx();
        f.Repo.Git("worktree", "add", "-q", "-b", "x", f.Wt("x"));
        f.Repo.Git("branch", "free");
        await f.OpenAsync();
        var owner = new Window { Width = 900, Height = 700 };
        owner.Show();

        async Task<DialogWindow> Show(Func<Task> entry)
        {
            f.Tab.ShowDialog = vm => DialogWindow.ShowAsync(vm, owner);
            _ = entry();
            for (int i = 0; i < 1000 && owner.OwnedWindows.Count == 0; i++)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(5);
            }
            var w = (DialogWindow)owner.OwnedWindows.Single();
            Dispatcher.UIThread.RunJobs();
            w.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            return w;
        }
        T Find<T>(Window w, string name) where T : Control => w.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);
        void Close(DialogWindow w)
        {
            ((DialogViewModel)w.DataContext!).Cancel();
            Dispatcher.UIThread.RunJobs();
        }

        var w = await Show(f.Tab.WorktreeOps.CreateAsync);
        Assert.Equal(("Create Worktree", 590.0), (w.Title, w.Width));
        Assert.True(Find<RefNameBox>(w, "NameBox").IsFocused);
        Assert.True(Find<ComboBox>(w, "StartBox").IsEffectivelyVisible);
        Assert.False(Find<RefView>(w, "BranchView").IsEffectivelyVisible);
        Find<RefNameBox>(w, "NameBox").Text = "n/m";
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(f.Wt("n-m"), Find<TextBox>(w, "LocationBox").Text);
        Assert.Equal("...", Find<Button>(w, "BrowseButton").Content);
        Close(w);

        w = await Show(() => f.Tab.WorktreeOps.CheckoutAsWorktreeAsync(f.Ref("refs/heads/free")));
        Assert.Equal(("free", false), (Find<RefView>(w, "BranchView").Text, Find<ComboBox>(w, "StartBox").IsEffectivelyVisible));
        Assert.True(Find<TextBox>(w, "LocationBox").IsFocused);
        Close(w);

        w = await Show(() => f.Tab.RefOps.DeleteBranchesAsync([f.Ref("refs/heads/x")]));
        Assert.True(Find<CheckBox>(w, "WorktreeCheck").IsEffectivelyVisible);
        Assert.Equal("x", Find<RefView>(w, "WorktreeDetail").Text);
        Assert.False(Find<LucideIcon>(w, "WorktreeWarning").IsVisible);
        Find<CheckBox>(w, "WorktreeCheck").IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        Assert.True(Find<LucideIcon>(w, "WorktreeWarning").IsVisible);
        Close(w);
        owner.Close();
    }

    /// <summary>Review round 1: no "Also delete worktree" for a branch the main worktree has (git cannot remove the
    /// main worktree); a path with a space and non-ASCII through create, open and delete; delete with local changes.</summary>
    [AvaloniaFact]
    public async Task Linked_tab_branch_of_main_and_odd_paths_with_local_changes()
    {
        using var f = new Fx();
        f.Repo.Git("worktree", "add", "-q", "-b", "x", f.Wt("x"));
        f.Repo.Git("branch", "other");
        await f.OpenAsync();
        await f.Tab.WorktreeOps.OpenAsync([f.Wt("x")]);
        var wtTab = f.Main.SelectedTab!;
        f.Hook(wtTab);
        f.Fill = d => { Assert.False(((DeleteRefsViewModel)d).HasWorktreeCheck); return false; };
        await wtTab.RefOps.DeleteBranchesAsync([wtTab.Refs.Refs.Single(r => r.FullName == "refs/heads/main")]);
        Assert.Single(f.Dialogs);

        // Create with a location below "with space ü", from the main tab.
        var odd = Path.Combine(f.Container, "with space ü");
        f.Fill = d => { var vm = (CreateWorktreeViewModel)d; vm.Name = "feat"; vm.Container = odd; return true; };
        f.Main.SelectedTab = f.Tab;
        await f.Tab.WorktreeOps.CreateAsync();
        Assert.Empty(f.Errors);
        var path = Path.Combine(odd, "feat");
        Assert.True(Directory.Exists(path));
        Assert.Equal(path, f.Main.SelectedTab!.Path);
        Assert.Contains(f.Tab.Worktrees, w => w.Path == path && w.Branch == "refs/heads/feat");
        Assert.Contains(f.Section(f.Tab, "Worktrees").Children, n => n.Title == "feat");

        // Uncommitted and untracked changes in it: Delete still removes it (--force).
        File.WriteAllText(Path.Combine(path, "a.txt"), "changed\n");
        File.WriteAllText(Path.Combine(path, "new ü.txt"), "new\n");
        f.Fill = _ => true;
        var node = f.Section(f.Tab, "Worktrees").Children.Single(n => n.Title == "feat");
        await Click(await f.Tab.Sidebar.BuildMenuAsync(node), "Delete 'feat'...");
        Assert.Empty(f.Errors);
        Assert.False(Directory.Exists(path));
        Assert.DoesNotContain(f.Main.Tabs, t => t.Path == path);
        Assert.DoesNotContain(f.Tab.Worktrees, w => w.Path == path);
    }

    /// <summary>Ref labels: the label of a branch checked out in another worktree gets the worktree cell; a
    /// change of the worktrees alone redraws the list.</summary>
    [AvaloniaFact]
    public async Task Commit_list_label_of_a_branch_in_another_worktree()
    {
        using var f = new Fx();
        f.Repo.Git("branch", "feat");
        await f.OpenAsync();
        var list = new GerGit.Views.CommitListView { DataContext = f.Tab.CommitList };
        var window = new Window { Width = 1000, Height = 300, Content = list };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var panel = list.FindControl<CommitListPanel>("List")!;
        RefLabel Label(string name) => f.Tab.CommitList.Rows[0].Labels.Single(l => l.FullName == name);
        Assert.False(panel.InOtherWorktree(Label("refs/heads/feat")));
        var before = Frame(window);

        f.Repo.Git("worktree", "add", "-q", f.Wt("feat"), "feat"); // refs and HEAD unchanged: only the worktrees differ
        await f.RefreshAsync();
        Dispatcher.UIThread.RunJobs();
        Assert.True(panel.InOtherWorktree(Label("refs/heads/feat")));
        Assert.False(panel.InOtherWorktree(Label("refs/heads/main"))); // the active branch keeps its check mark
        Assert.NotEqual(before, Frame(window));
        window.Close();

        static byte[] Frame(Window w)
        {
            using var frame = w.CaptureRenderedFrame()!;
            using var stream = new MemoryStream();
            frame.Save(stream, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            return stream.ToArray();
        }
    }

    /// <summary>Only a vanished linked worktree closes silently: a data error with the folder still there shows the
    /// error window and keeps the tab; the main repository's tab is never closed.</summary>
    [AvaloniaFact]
    public async Task Data_refresh_errors_keep_the_tab_unless_the_linked_worktree_is_gone()
    {
        using var f = new Fx();
        f.Repo.Git("worktree", "add", "-q", "-b", "x", f.Wt("x"));
        await f.OpenAsync();
        await f.Tab.WorktreeOps.OpenAsync([f.Wt("x")]);
        var wtTab = f.Main.SelectedTab!;
        f.Hook(wtTab);
        var gitFile = Path.Combine(f.Wt("x"), ".git");
        var gitText = File.ReadAllText(gitFile);
        File.WriteAllText(gitFile, "broken\n");
        await f.RefreshAsync(wtTab);
        Assert.Single(f.Errors);
        Assert.Contains(wtTab, f.Main.Tabs);
        File.WriteAllText(gitFile, gitText);

        // The main repository's folder goes away: error window, the tab stays.
        var moved = f.Repo.Path + "-moved";
        Directory.Move(f.Repo.Path, moved);
        try
        {
            f.Errors.Clear();
            await f.RefreshAsync(f.Tab);
            Assert.Single(f.Errors);
            Assert.Contains(f.Tab, f.Main.Tabs);
        }
        finally { Directory.Move(moved, f.Repo.Path); }
    }

    /// <summary>"a/b" and "a-b" give the same location: the name alone must re-validate.</summary>
    [Fact]
    public void Name_change_with_the_same_location_revalidates()
    {
        var start = new RefPoint("git-branch", "main", null, "refs/heads/main", "1111111");
        var vm = new CreateWorktreeViewModel("/c", [start], start, null, "", "", new HashSet<string>(["a-b"], StringComparer.OrdinalIgnoreCase), _ => false);
        vm.Name = "a-b";
        Assert.Equal(("Branch 'a-b' already exists", false), (vm.StatusText, vm.SubmitEnabled));
        vm.Name = "a/b";
        Assert.Equal(("/c/a-b", "", true), (vm.Location, vm.StatusText, vm.SubmitEnabled));
    }

    [AvaloniaFact]
    public async Task A_deleted_worktree_closes_its_tab_and_selects_the_main_tab()
    {
        using var f = new Fx();
        f.Repo.Git("worktree", "add", "-q", "-b", "x", f.Wt("x"));
        await f.OpenAsync();
        await f.Tab.WorktreeOps.OpenAsync([f.Wt("x")]);
        var wtTab = f.Main.SelectedTab!;
        Assert.NotSame(f.Tab, wtTab);
        var errors = new List<string>();
        wtTab.ShowError = (_, text) => { errors.Add(text); return Task.CompletedTask; };

        f.Repo.Git("worktree", "remove", "--force", f.Wt("x")); // outside GerGit
        await f.RefreshAsync(wtTab);
        Assert.Empty(errors);
        Assert.Equal([f.Tab], f.Main.Tabs);
        Assert.Same(f.Tab, f.Main.SelectedTab);
    }
}
