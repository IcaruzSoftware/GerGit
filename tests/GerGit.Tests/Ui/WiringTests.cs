using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GerGit.Controls;
using GerGit.Core;
using GerGit.Tests.Core;
using GerGit.ViewModels;
using GerGit.ViewModels.Dialogs;
using GerGit.ViewModels.Dialogs.Refs;
using GerGit.ViewModels.Dialogs.Remote;
using GerGit.ViewModels.Preferences;
using GerGit.Views;

namespace GerGit.Tests.Ui;

/// <summary>toolbar, Repository menu, shortcuts, sidebar selection / keys / menus, commit-list menus and the
/// Local Changes "Stash Files..." item, wired to the entry points of the tab's operations.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public class WiringTests
{
    /// <summary>A main window with a tab on <see cref="RemoteSetup.Local"/> and recording hooks. Dialogs are recorded
    /// and handed to <see cref="Fill"/> (fill + submit); without it they are cancelled.</summary>
    sealed class Fx : IDisposable
    {
        public readonly RemoteSetup S = new();
        public MainWindow Window = null!;
        public MainWindowViewModel Main = null!;
        public RepoTabViewModel Tab = null!;
        public readonly List<DialogViewModel> Dialogs = [];
        public readonly List<string> Errors = [];
        public readonly List<string> Copied = [];
        public Func<DialogViewModel, Task>? Fill;

        public async Task OpenAsync()
        {
            Main = new MainWindowViewModel();
            Window = new MainWindow { DataContext = Main };
            Window.Show();
            Window.StatusTimer.Stop();
            Assert.Null(await Main.OpenAsync(S.Local.Path));
            Tab = Main.SelectedTab!;
            Tab.IsActive = false; // no watcher refreshes in between; the tab view stays shown
            Settle();
            Window.GetVisualDescendants().OfType<RepoTabView>().Single().IsVisible = true;
            Settle();
            Tab.ShowError = (_, text) => { Errors.Add(text); return Task.CompletedTask; };
            Tab.ShowGitError = (r, _) => { Errors.Add(r.ReadableOutput); return Task.CompletedTask; };
            Tab.ShowDialog = async vm =>
            {
                Dialogs.Add(vm);
                if (Fill is not null) await Fill(vm);
                return vm.Result == true;
            };
            Tab.Sidebar.Copy = Tab.CommitList.Copy = text => { Copied.Add(text); return Task.CompletedTask; };
        }

        public async Task RefreshAsync()
        {
            await Tab.RefreshAsync();
            await Tab.Refreshing;
            Settle();
        }

        public void Settle()
        {
            Dispatcher.UIThread.RunJobs();
            Window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }

        public SidebarView SidebarView => Window.GetVisualDescendants().OfType<SidebarView>().Single();
        public TreeView Tree => SidebarView.FindControl<TreeView>("Tree")!;
        public CommitListPanel List => Window.GetVisualDescendants().OfType<CommitListPanel>().Single();

        public SidebarNode Node(string section, params string[] path)
        {
            var n = Tab.Sidebar.Roots.Single(r => r.Title == section);
            foreach (var p in path) n = n.Children.Single(c => c.Title == p);
            return n;
        }

        public Control Row(SidebarNode n) => Tree.TreeContainerFromItem(n)!;

        public void Dispose()
        {
            Dispatcher.UIThread.RunJobs(); // posted focus requests of the views run while they are still attached
            Main?.CloseTab(Tab);
            Window?.Close();
            S.Dispose();
        }
    }

    static void Click(Fx f, Control c, MouseButton button = MouseButton.Left, RawInputModifiers m = RawInputModifiers.None)
    {
        // Rows: click on the text, left of the centre (not on an expander or the ahead/behind text).
        var at = c.TranslatePoint(new Point(Math.Min(60, c.Bounds.Width / 2), Math.Min(11, c.Bounds.Height / 2)), f.Window)!.Value;
        f.Window.MouseMove(at, m);
        f.Window.MouseDown(at, button, m);
        f.Window.MouseUp(at, button, m);
        f.Settle();
    }

    static void Press(Fx f, Key key, RawInputModifiers m = RawInputModifiers.None)
    {
        f.Window.KeyPress(key, m, PhysicalKey.None, null);
        f.Window.KeyRelease(key, m, PhysicalKey.None, null);
        f.Settle();
    }

    const RawInputModifiers Ctrl = RawInputModifiers.Control, CtrlShift = Ctrl | RawInputModifiers.Shift,
        CtrlShiftAlt = CtrlShift | RawInputModifiers.Alt;

    /// <summary>Menu entries as "Header [Gesture] (disabled) (checked) {children}" joined by "|", separators "-".</summary>
    static string Flat(IEnumerable<MenuEntry>? entries) => entries is null ? "<none>" : string.Join("|", entries.Select(e =>
        e.Kind == MenuEntryKind.Separator ? "-"
        : e.Header + (e.Gesture is null ? "" : $" [{e.Gesture}]") + (e.IsEnabled || e.Kind == MenuEntryKind.Header ? "" : " (disabled)")
          + (e.IsChecked == true ? " (checked)" : "") + (e.Children is { } c ? " {" + Flat(c) + "}" : "")));

    static string[] Headers(ItemsControl menu) =>
        menu.Items.OfType<Control>().Where(c => c.IsVisible).Select(c => c is MenuItem m ? (string)m.Header! : "-").ToArray();

    /// <summary>Clicks a menu item (path through submenus) and returns its running action.</summary>
    static Task ClickItem(ItemsControl menu, params string[] path)
    {
        ItemsControl current = menu;
        foreach (var header in path)
            current = current.Items.OfType<MenuItem>().Single(m => (string?)m.Header == header);
        var item = (MenuItem)current;
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        return item.Tag is MenuEntry e ? e.Running : Task.CompletedTask;
    }

    static async Task WaitFor(Func<bool> condition)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException();
            await Task.Delay(20);
            Dispatcher.UIThread.RunJobs();
        }
    }

    // ------------------------------------------------------------------ toolbar, Repository menu, shortcuts

    [AvaloniaFact]
    public async Task Toolbar_buttons_menus_and_shortcuts_call_their_entry_points()
    {
        // Without a repository tab the repository buttons are disabled.
        var empty = new MainWindow { DataContext = new MainWindowViewModel() };
        empty.Show();
        foreach (var name in new[] { "FetchButton", "PullButton", "PushButton", "StashButton", "StashArrow", "BranchButton", "BranchArrow" })
            Assert.False(empty.FindControl<Button>(name)!.IsEnabled, name);
        empty.Close();

        using var f = new Fx();
        await f.OpenAsync();
        var w = f.Window;
        foreach (var name in new[] { "FetchButton", "PullButton", "PushButton", "StashButton", "StashArrow", "BranchButton", "BranchArrow" })
            Assert.True(w.FindControl<Button>(name)!.IsEnabled, name);

        // Plain clicks open the dialogs.
        foreach (var (name, type) in new[] { ("FetchButton", typeof(FetchViewModel)), ("PullButton", typeof(PullViewModel)),
                     ("PushButton", typeof(PushViewModel)), ("StashButton", typeof(SaveStashViewModel)), ("BranchButton", typeof(CreateBranchViewModel)) })
        {
            f.Dialogs.Clear();
            Click(f, w.FindControl<Button>(name)!);
            await w.LastCommand;
            Assert.IsType(type, Assert.Single(f.Dialogs));
        }
        // Stash and Branch do the same with Ctrl held.
        f.Dialogs.Clear();
        Click(f, w.FindControl<Button>("StashButton")!, m: Ctrl);
        await w.LastCommand;
        Assert.IsType<SaveStashViewModel>(Assert.Single(f.Dialogs));

        // Ctrl+click Fetch = Quick Fetch: the remote change arrives without a dialog.
        f.Dialogs.Clear();
        var remoteSha = f.S.OtherPush("remote change", 10, files: ("b.txt", "b\n"));
        Click(f, w.FindControl<Button>("FetchButton")!, m: Ctrl);
        await w.LastCommand;
        Assert.Empty(f.Dialogs);
        Assert.Equal("Fetch 'origin'", f.Tab.Jobs.History[0].Name);
        Assert.Equal(remoteSha, f.Tab.Refs.Refs.Single(r => r.FullName == "refs/remotes/origin/main").Sha);
        // Ctrl+click Pull = Quick Pull of the upstream; Ctrl+click Push = Quick Push to it.
        Click(f, w.FindControl<Button>("PullButton")!, m: Ctrl);
        await w.LastCommand;
        Assert.Equal(("Pull 'origin/main'", remoteSha), (f.Tab.Jobs.History[0].Name, f.Tab.Refs.HeadSha));
        f.S.Local.Commit("local change", 20);
        await f.RefreshAsync();
        Click(f, w.FindControl<Button>("PushButton")!, m: Ctrl);
        await w.LastCommand;
        Assert.Equal("Push 'main' to 'origin'", f.Tab.Jobs.History[0].Name);
        Assert.Equal(f.S.Local.Git("rev-parse", "HEAD"), f.S.BareSha("main"));
        Assert.Empty(f.Dialogs);
        Assert.Empty(f.Errors);

        // Stash arrow menu: header, recent stashes, Save Snapshot...; a stash item opens Apply Stash.
        f.S.Local.Write("a.txt", "dirty\n");
        f.S.Local.Git("stash", "push", "-m", "my work");
        await f.RefreshAsync();
        Click(f, w.FindControl<Button>("StashArrow")!);
        var menu = w.ToolbarMenu!;
        Assert.Equal(["Recent Stashes", "-", "my work", "-", "Save Snapshot..."], Headers(menu));
        await ClickItem(menu, "my work");
        Assert.Equal("stash@{0}", Assert.IsType<ApplyStashViewModel>(f.Dialogs[^1]).Stash.Name);
        await ClickItem(menu, "Save Snapshot...");
        Assert.True(Assert.IsType<SaveStashViewModel>(f.Dialogs[^1]).IsSnapshot);
        menu.Close();
        Click(f, w.FindControl<Button>("BranchArrow")!);
        Assert.Equal(["New Branch...", "New Worktree..."], Headers(w.ToolbarMenu!));
        Assert.Equal("Ctrl+Shift+B", w.ToolbarMenu!.Items.OfType<MenuItem>().First().InputGesture!.ToString("p", null));
        w.ToolbarMenu.Close();

        // Repository menu.
        var repoMenu = w.FindControl<MenuItem>("RepositoryMenu")!;
        Assert.True(repoMenu.IsVisible);
        Assert.Equal(["Refresh", "-", "Fetch...", "Pull...", "Push...", "-", "Save Stash...", "-", "New Branch...", "New Tag...", "New Worktree...",
            "-", "Open In File Explorer", "Open In Terminal", "-", "Settings for This Repository..."], Headers(repoMenu));
        f.Dialogs.Clear();
        foreach (var header in new[] { "Fetch...", "Pull...", "Push...", "Save Stash...", "New Branch...", "New Tag...", "New Worktree...",
                     "Settings for This Repository..." })
        {
            await ClickItem(repoMenu, header);
            await w.LastCommand;
        }
        Assert.Equal([typeof(FetchViewModel), typeof(PullViewModel), typeof(PushViewModel), typeof(SaveStashViewModel),
            typeof(CreateBranchViewModel), typeof(CreateTagViewModel), typeof(CreateWorktreeViewModel), typeof(RepositorySettingsViewModel)],
            f.Dialogs.Select(d => d.GetType()));

        // The sidebar header's settings button: its menu item opens the same window.
        f.Dialogs.Clear();
        var sidebar = w.GetVisualDescendants().OfType<SidebarView>().Single();
        var settings = ((MenuFlyout)sidebar.FindControl<Button>("SettingsButton")!.Flyout!).Items.OfType<MenuItem>()
            .Single(m => (string?)m.Header == "Settings for This Repository...");
        Assert.True(settings.IsEnabled);
        settings.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await sidebar.SettingsOpening;
        Assert.Equal(f.Tab.Title, Assert.IsType<RepositorySettingsViewModel>(Assert.Single(f.Dialogs)).RepositoryName);

        // Global shortcuts (nothing focused that handles them).
        w.Focus(); // nothing focused that would handle the keys
        f.Dialogs.Clear();
        foreach (var key in new[] { Key.F, Key.L, Key.P, Key.H, Key.B, Key.G })
        {
            Press(f, key, CtrlShift);
            await w.LastCommand;
        }
        Assert.Equal([typeof(FetchViewModel), typeof(PullViewModel), typeof(PushViewModel), typeof(SaveStashViewModel),
            typeof(CreateBranchViewModel), typeof(CreateTagViewModel)], f.Dialogs.Select(d => d.GetType()));

        // Quick variants: Ctrl+Alt+Shift+F fires on the key release only.
        f.Dialogs.Clear();
        f.S.Other.Git("pull", "-q", "--ff-only", "origin", "main");
        var other = f.S.OtherPush("second remote change", 30);
        int jobs = f.Tab.Jobs.History.Count;
        // Another key in between, or an F release without the modifiers, disarms it.
        w.KeyPress(Key.F, CtrlShiftAlt, PhysicalKey.None, null);
        w.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);
        w.KeyRelease(Key.F, CtrlShiftAlt, PhysicalKey.None, null);
        w.KeyPress(Key.F, CtrlShiftAlt, PhysicalKey.None, null);
        w.KeyRelease(Key.F, RawInputModifiers.None, PhysicalKey.None, null);
        w.KeyRelease(Key.F, CtrlShiftAlt, PhysicalKey.None, null);
        f.Settle();
        await w.LastCommand;
        Assert.Equal(jobs, f.Tab.Jobs.History.Count);
        w.KeyPress(Key.F, CtrlShiftAlt, PhysicalKey.None, null);
        f.Settle();
        Assert.Equal(jobs, f.Tab.Jobs.History.Count);
        w.KeyRelease(Key.F, CtrlShiftAlt, PhysicalKey.None, null);
        await w.LastCommand;
        Assert.Equal(other, f.Tab.Refs.Refs.Single(r => r.FullName == "refs/remotes/origin/main").Sha);
        Press(f, Key.L, CtrlShiftAlt);
        await w.LastCommand;
        Assert.Equal(("Pull 'origin/main'", other), (f.Tab.Jobs.History[0].Name, f.Tab.Refs.HeadSha));
        Press(f, Key.P, CtrlShiftAlt);
        await w.LastCommand;
        Assert.Equal("Push 'main' to 'origin'", f.Tab.Jobs.History[0].Name);
        Assert.Empty(f.Dialogs);

        // The focused commit list handles Ctrl+Shift+B itself: the new branch starts at the selected commit.
        var first = f.S.Local.Git("rev-list", "--max-parents=0", "HEAD");
        f.Tab.CommitList.SelectCommit(first);
        f.List.Focus();
        Press(f, Key.B, CtrlShift);
        await WaitFor(() => f.Dialogs.Count == 1);
        Assert.Equal(first, Assert.IsType<CreateBranchViewModel>(f.Dialogs[0]).Start.Sha);
        Assert.Empty(f.Errors);
    }

    // ------------------------------------------------------------------ sidebar

    [AvaloniaFact]
    public async Task Sidebar_selects_extended_and_maps_keys()
    {
        using var f = new Fx();
        f.S.Local.Git("branch", "b1");
        f.S.Local.Git("branch", "b2");
        f.S.Local.Git("tag", "t1");
        f.S.Local.Write("a.txt", "dirty\n");
        f.S.Local.Git("stash");
        await f.OpenAsync();
        var sidebar = f.Tab.Sidebar;
        f.Node("Tags").IsExpanded = true;
        f.Settle();
        SidebarNode b1 = f.Node("Branches", "b1"), b2 = f.Node("Branches", "b2"), main = f.Node("Branches", "main"),
            t1 = f.Node("Tags", "t1"), stash = f.Node("Stashes").Children.Single();
        string[] Selected() => sidebar.SelectedNodes.Select(n => n.Title).Order().ToArray();

        Click(f, f.Row(b1));
        Assert.Equal(["b1"], Selected());
        Click(f, f.Row(b2), m: Ctrl);
        Assert.Equal(["b1", "b2"], Selected());
        Click(f, f.Row(main), m: RawInputModifiers.Shift); // range from b1
        Assert.Equal(["b1", "b2", "main"], Selected());

        // Right-click does not change the selection; inside it the menu is the multi-selection menu...
        Click(f, f.Row(main), MouseButton.Right);
        Assert.Equal(["b1", "b2", "main"], Selected());
        await WaitFor(() => f.SidebarView.Menu is not null);
        Assert.Equal(["Fast-Forward 3 branches to their upstreams", "Push 3 branches to 'origin'...", "-", "Delete 3 Branches..."],
            Headers(f.SidebarView.Menu!));
        f.SidebarView.Menu!.Close();
        // ... outside it, the menu of the clicked row only.
        Assert.StartsWith("Checkout Commit...|", Flat(await sidebar.BuildMenuAsync(t1)));

        // Delete: all local branches → one delete dialog for them.
        Press(f, Key.Delete);
        await WaitFor(() => f.Dialogs.Count == 1);
        Assert.Equal(["b1", "b2", "main"], Assert.IsType<DeleteRefsViewModel>(f.Dialogs[0]).Rows.Select(r => r.Name));
        // A mix of reference kinds does nothing.
        Click(f, f.Row(t1), m: Ctrl);
        Assert.Equal(["b1", "b2", "main", "t1"], Selected());
        Assert.Null(await sidebar.BuildMenuAsync(t1)); // mixed kinds: no menu
        Press(f, Key.Delete);
        Assert.Single(f.Dialogs);
        // Stash rows mixed with branches are dropped; only stashes = delete stashes.
        Click(f, f.Row(stash));
        Click(f, f.Row(b2), m: Ctrl);
        Press(f, Key.Delete);
        await WaitFor(() => f.Dialogs.Count == 2);
        Assert.Equal(["b2"], Assert.IsType<DeleteRefsViewModel>(f.Dialogs[1]).Rows.Select(r => r.Name));
        Click(f, f.Row(stash));
        Press(f, Key.Delete);
        await WaitFor(() => f.Dialogs.Count == 3);
        Assert.IsType<DeleteStashViewModel>(f.Dialogs[2]);

        // F2 renames exactly one local branch; Ctrl+C copies the first reference's name.
        Click(f, f.Row(b1));
        Press(f, Key.F2);
        await WaitFor(() => f.Dialogs.Count == 4);
        Assert.IsType<RenameBranchViewModel>(f.Dialogs[3]);
        Click(f, f.Row(t1));
        Press(f, Key.C, Ctrl);
        Assert.Equal(["t1"], f.Copied);
        Click(f, f.Row(stash));
        Press(f, Key.F2);
        Assert.Equal(4, f.Dialogs.Count);

        // Ctrl+A selects all visible rows.
        Press(f, Key.A, Ctrl);
        Assert.Equal(SidebarViewModel.VisibleRows(sidebar.Roots).Count(n => !n.IsShowAll), sidebar.SelectedNodes.Count);

        // A refresh keeps the selected rows.
        Click(f, f.Row(b1));
        Click(f, f.Row(b2), m: Ctrl);
        await f.RefreshAsync();
        sidebar.Rebuild();
        Assert.Equal(["b1", "b2"], Selected());
        Assert.Empty(f.Errors);
    }

    /// <summary>The sidebar menus' item lists for representative states, built through the view model.</summary>
    [AvaloniaFact]
    public async Task Sidebar_menus_list_their_items()
    {
        using var f = new Fx();
        var s = f.S;
        s.Local.Commit("ahead", 5);                                   // main strictly ahead of origin/main
        s.Local.Git("branch", "feature");
        s.Local.Git("tag", "-a", "va", "-m", "annotated");
        s.Local.Git("tag", "vl");
        s.Local.Git("push", "-q", "origin", "HEAD:refs/heads/old/gone");
        s.Local.Git("branch", "--track", "old/gone", "origin/old/gone");
        s.Local.Git("push", "-q", "origin", "--delete", "old/gone");   // upstream gone: a stale branch
        s.Local.Write("a.txt", "one\n");
        s.Local.Git("stash");
        s.Local.Write("a.txt", "two\n");
        s.Local.Git("stash");
        await f.OpenAsync();
        var sb = f.Tab.Sidebar;
        async Task<string> Menu(SidebarNode n, params SidebarNode[] selection)
        {
            if (selection.Length > 0) sb.Select(selection);
            return Flat(await sb.BuildMenuAsync(n));
        }
        SidebarNode main = f.Node("Branches", "main"), feature = f.Node("Branches", "feature"), va = f.Node("Tags", "va"),
            vl = f.Node("Tags", "vl"), originMain = f.Node("Remotes", "origin", "main"), origin = f.Node("Remotes", "origin");
        var stashes = f.Node("Stashes").Children;

        Assert.Equal("Checkout...|-|Fast-Forward to 'origin/main'|Pull 'origin/main'...|Push to 'origin'...|-" +
                     "|New Branch... [Ctrl+Shift+B]|New Tag... [Ctrl+Shift+G]|-" +
                     "|Tracking {Remove tracking reference|-|origin/main (checked)}|Rename... [F2]|Delete... [Delete] (disabled)" +
                     "|-|Copy Branch Name [Ctrl+C]", await Menu(main, main));
        Assert.Equal("Checkout...|-|Push to 'origin'...|-|Merge into 'main'...|Rebase 'main' on 'feature'...|Interactively Rebase 'main' on 'feature...|-|New Branch... [Ctrl+Shift+B]|New Tag... [Ctrl+Shift+G]" +
                     "|-|Tracking {origin/main}|Rename... [F2]|Delete... [Delete]|-|Copy Branch Name [Ctrl+C]", await Menu(feature, feature));
        Assert.Equal("Checkout...|-|Pull into 'main'... [Ctrl+Shift+L]|-|Merge into 'main'...|Rebase 'main' on 'origin/main'..." +
                     "|Interactively Rebase 'main' on 'origin/main...|-|New Branch... [Ctrl+Shift+B]" +
                     "|New Tag... [Ctrl+Shift+G]|-|Delete... [Delete]|-|Copy Branch Name [Ctrl+C]", await Menu(originMain, originMain));
        Assert.Equal("Checkout Commit...|Show Tag Details...|-|Push to 'origin'...|-|Merge into 'main'... (disabled)" +
                     "|Rebase 'main' on 'va'... (disabled)|Interactively Rebase 'main' on 'va...|-" +
                     "|New Branch...|New Tag...|-|Delete... [Delete]|-|Copy Tag Name [Ctrl+C]", await Menu(va, va));
        Assert.StartsWith("Checkout Commit...|-|Push to 'origin'...|", await Menu(vl, vl));
        Assert.Equal("Fetch 'origin'... [Ctrl+Shift+F]|-|Edit 'origin'...|Delete 'origin'...|-|Fetch 'origin' Automatically (checked)" +
                     "|-|Add New Remote...|-|Copy Remote Address", await Menu(origin));
        Assert.Equal("Apply...|Rename...|-|Delete... [Delete]", await Menu(stashes[0], stashes[0]));

        // Multi-selections (the right-clicked row is part of the selection).
        Assert.Equal("Fast-Forward 2 branches to their upstreams|Push 2 branches to 'origin'...|-|Delete 2 Branches...",
            await Menu(main, feature, main));
        Assert.Equal("Fast-Forward 1 branches to their upstreams (disabled)|Push 1 branches to 'origin'...|-|Delete 1 Branches...",
            Flat(Menus.LocalBranches(f.Tab, [feature.Ref!]))); // no upstream: nothing to fast-forward
        Assert.Equal("Push 2 tags to 'origin'...|-|Delete 2 Tags...", await Menu(va, va, vl));
        Assert.Equal("Delete 2 Stashes...", await Menu(stashes[0], stashes[0], stashes[1]));
        Assert.Equal("<none>", await Menu(main, main, va)); // mixed reference kinds
        Assert.Equal("Checkout Commit...|Show Tag Details...", string.Join("|", (await Menu(va, main, feature)).Split('|').Take(2)));

        // Section headers, folders.
        Assert.Equal("New Branch... [Ctrl+Shift+B]|Select Stale Local Branches", await Menu(f.Node("Branches")));
        Assert.Equal("New Tag... [Ctrl+Shift+G]", await Menu(f.Node("Tags")));
        Assert.Equal("Add New Remote...", await Menu(f.Node("Remotes")));
        Assert.Equal("<none>", await Menu(f.Node("Stashes")));
        Assert.Equal("<none>", await Menu(f.Node("Branches", "old")));

        // Select Stale Local Branches: the gone branch, its folder expanded.
        Assert.False(f.Node("Branches", "old").IsExpanded);
        var entries = await sb.BuildMenuAsync(f.Node("Branches"));
        await entries!.Single(e => e.Header == "Select Stale Local Branches").Invoke();
        Assert.Equal(["old/gone"], sb.SelectedNodes.Select(n => n.Ref!.ShortName));
        Assert.True(f.Node("Branches", "old").IsExpanded);

        // A second remote turns the push items into submenus; Ctrl+C / Copy items use the names.
        s.Local.Git("remote", "add", "backup", s.Bare.Path);
        await f.RefreshAsync();
        main = f.Node("Branches", "main");
        feature = f.Node("Branches", "feature");
        Assert.Contains("|Push {Push to 'backup'...|Push to 'origin'...}|", await Menu(feature, feature));
        Assert.Equal("Fast-Forward 2 branches to their upstreams|Push 2 branches to {backup...|origin...}|-|Delete 2 Branches...",
            await Menu(main, feature, main));
        Assert.Contains("|Push Tag... {Push to 'backup'...|Push to 'origin'...}|", await Menu(f.Node("Tags", "va"), f.Node("Tags", "va")));
        var copy = (await sb.BuildMenuAsync(feature))!.Single(e => e.Header == "Copy Branch Name");
        await copy.Invoke();
        Assert.Equal(["feature"], f.Copied);
        Assert.Empty(f.Errors);
    }

    [AvaloniaFact]
    public async Task Sidebar_menu_without_remotes_and_double_click_actions()
    {
        using var f = new Fx();
        f.S.Local.Git("push", "-q", "origin", "HEAD:refs/heads/feature"); // origin/feature: nothing tracks it
        f.S.Local.Git("fetch", "-q");
        f.S.Local.Git("branch", "other");
        f.S.Local.Git("tag", "v1");
        f.S.Local.Write("a.txt", "stashed\n");
        f.S.Local.Git("stash");
        f.S.Local.Write("a.txt", "dirty\n"); // a dirty tree: local checkout opens its dialog
        await f.OpenAsync();
        var sb = f.Tab.Sidebar;

        await sb.DoubleClickAsync(f.Node("Branches", "other"));
        Assert.Equal("Checkout Branch", Assert.IsType<CheckoutViewModel>(f.Dialogs[^1]).Title);
        await sb.DoubleClickAsync(f.Node("Remotes", "origin", "feature"));
        Assert.IsType<TrackRemoteBranchViewModel>(f.Dialogs[^1]);
        await sb.DoubleClickAsync(f.Node("Tags", "v1"));
        Assert.Equal("Checkout Commit", Assert.IsType<CheckoutViewModel>(f.Dialogs[^1]).Title);
        await sb.DoubleClickAsync(f.Node("Stashes").Children.Single());
        Assert.IsType<ApplyStashViewModel>(f.Dialogs[^1]);
        Assert.Equal(4, f.Dialogs.Count);

        // No remote: a disabled "Push to 'origin'..."; tags have no push part.
        f.S.Local.Git("remote", "remove", "origin");
        await f.RefreshAsync();
        Assert.Contains("|Push to 'origin'... (disabled)|", Flat(await sb.BuildMenuAsync(f.Node("Branches", "other"))));
        Assert.Equal("Checkout Commit...|-|Merge into 'main'... (disabled)|Rebase 'main' on 'v1'... (disabled)|Interactively Rebase 'main' on 'v1...|-|New Branch...|New Tag...|-|Delete... [Delete]|-|Copy Tag Name [Ctrl+C]",
            Flat(await sb.BuildMenuAsync(f.Node("Tags", "v1"))));
        Assert.Equal("Fast-Forward 2 branches to their upstreams (disabled)|Delete 2 Branches...",
            Flat(Menus.LocalBranches(f.Tab, [f.Node("Branches", "other").Ref!, f.Node("Branches", "main").Ref!])));
    }

    // ------------------------------------------------------------------ commit list

    [AvaloniaFact]
    public async Task Commit_list_menus_list_their_items()
    {
        using var f = new Fx();
        var g = f.S.Local;
        var first = g.Git("rev-parse", "HEAD");
        g.Git("branch", "b");
        g.Git("branch", "--track", "a", "origin/main");
        g.Git("checkout", "-q", "-b", "side");
        var sideWork = g.Commit("side work", 2);
        g.Git("checkout", "-q", "main");
        var second = g.Commit("second", 3);
        g.Git("tag", "-a", "v1", "-m", "release");
        g.Git("merge", "-q", "--no-ff", "side", "-m", "merge side");
        var merge = g.Git("rev-parse", "HEAD");
        g.Git("branch", "-d", "side");
        g.Git("checkout", "-q", "-b", "other", first);
        var otherWork = g.Commit("other work", 5);
        g.Git("checkout", "-q", "main");
        g.Write("a.txt", "one\n");
        g.Git("stash");
        g.Write("a.txt", "two\n");
        g.Git("stash");
        await f.OpenAsync();
        var list = f.Tab.CommitList;
        string MenuFor(params string[] shas)
        {
            list.Click(list.IndexOf(shas[0]), false, false);
            foreach (var sha in shas.Skip(1)) list.Click(list.IndexOf(sha), true, false);
            return Flat(Menus.CommitList(list));
        }
        const string Tail = "|-|New Branch... [Ctrl+Shift+B]|New Tag... [Ctrl+Shift+G]";
        const string Picks = "|-|Checkout Commit...|Cherry-pick Commit...|Revert Commit...|-|Copy Commit SHA [Ctrl+C]|Copy Commit Info [Ctrl+Shift+C]";

        // HEAD (active branch with an upstream): no Checkout, no Merge, no Reset.
        Assert.Equal("main {main|-|Fast-Forward to 'origin/main'|Pull...|Push to 'origin'...|-|Rename... [F2]|-|Copy Branch Name [Ctrl+C]}"
                     + Tail + "|-|Interactively Rebase 'main' to Here..." + Picks, MenuFor(merge));
        // A commit that HEAD does not reach: Checkout, Merge (submenu and top-level entries), Reset.
        Assert.Equal("other {other|-|Checkout|-|Push to 'origin'...|-|Merge into 'main'...|-|Rename... [F2]|Delete... [Delete]|-|Copy Branch Name [Ctrl+C]}"
                     + Tail + "|-|Merge into 'main'...|Rebase 'main' to Here...|Interactively Rebase 'main' to Here...|-|Reset 'main' to Here..." + Picks,
                     MenuFor(otherWork));
        // A tag row (annotated) reachable from HEAD.
        Assert.Equal("v1 {v1|-|Show Tag Details...|Merge into 'main'...|-|Delete... [Delete]|-|Copy Tag Name [Ctrl+C]|-|Push to 'origin'...}"
                     + Tail + "|-|Interactively Rebase 'main' to Here...|-|Reset 'main' to Here..." + Picks, MenuFor(second));
        // Branch order: natural by full name, a local branch directly before its own upstream.
        Assert.Equal(["a", "origin/main", "b"], Menus.CommitList(SelectAndGet(first))!.Where(e => e.Children is not null).Select(e => e.Header));
        Assert.Equal("origin/main|-|Checkout|-|Delete... [Delete]|-|Copy Branch Name [Ctrl+C]",
            Flat(Menus.CommitList(list)!.Single(e => e.Header == "origin/main").Children));
        // No references: no submenus.
        Assert.Equal(Tail[3..] + "|-|Interactively Rebase 'main' to Here...|-|Reset 'main' to Here..." + Picks, MenuFor(sideWork));

        // Multi-selection: Cherry-pick… only when every commit has one parent.
        Assert.Equal("Cherry-pick… (disabled)|-|Copy Commit SHA [Ctrl+C]|Copy Commit Info [Ctrl+Shift+C]", MenuFor(merge, otherWork));
        Assert.Equal("Cherry-pick…|-|Copy Commit SHA [Ctrl+C]|Copy Commit Info [Ctrl+Shift+C]", MenuFor(second, otherWork));
        // Stashes.
        var stashes = f.Tab.Stashes.Select(st => st.Sha).ToArray();
        Assert.Equal("Apply...|Rename...|-|Delete... [Delete]|-|Copy Commit SHA [Ctrl+C]|Copy Commit Info [Ctrl+Shift+C]", MenuFor(stashes[0]));
        Assert.Equal("Delete 2 Stashes...", MenuFor(stashes));
        // Nothing selected: no menu.
        list.Click(list.IndexOf(merge), false, false);
        list.Click(list.IndexOf(merge), true, false);
        Assert.Empty(list.SelectedRows);
        Assert.Null(Menus.CommitList(list));

        // Copy keys (Ctrl+C / Ctrl+Shift+C) on the focused list: rows in row order.
        MenuFor(second, otherWork);
        f.List.Focus();
        Press(f, Key.C, Ctrl);
        Press(f, Key.C, CtrlShift);
        await WaitFor(() => f.Copied.Count == 2);
        var rows = list.SelectedShas;
        Assert.Equal(string.Join("\n", rows), f.Copied[0]);
        Assert.Equal(string.Join("\n", rows.Select(sha => sha[..7] + " - " + (sha == second ? "second" : "other work"))), f.Copied[1]);

        // Double-click: stash → Apply; a row without branches → Checkout Commit; the active branch's row → nothing.
        await list.DoubleClickAsync(list.IndexOf(stashes[0]), null);
        Assert.IsType<ApplyStashViewModel>(f.Dialogs[^1]);
        await list.DoubleClickAsync(list.IndexOf(sideWork), null);
        Assert.Equal("Checkout Commit", Assert.IsType<CheckoutViewModel>(f.Dialogs[^1]).Title);
        int count = f.Dialogs.Count;
        await list.DoubleClickAsync(list.IndexOf(merge), null);
        Assert.Equal(count, f.Dialogs.Count);
        // F2 / Delete on a row: the first local branch / the references of the row.
        MenuFor(otherWork);
        await list.RenameAsync();
        Assert.IsType<RenameBranchViewModel>(f.Dialogs[^1]);
        await list.DeleteAsync();
        Assert.Equal(["other"], Assert.IsType<DeleteRefsViewModel>(f.Dialogs[^1]).Rows.Select(r => r.Name));
        MenuFor(stashes);
        await list.DeleteAsync();
        Assert.IsType<DeleteStashViewModel>(f.Dialogs[^1]);
        // Local branches with a remote branch on the row: the local delete dialog (it offers the remote one).
        MenuFor(first);
        await list.DeleteAsync();
        Assert.Equal(["a", "b"], Assert.IsType<DeleteRefsViewModel>(f.Dialogs[^1]).Rows.Select(r => r.Name).Order()); // label order
        MenuFor(second);
        await list.DeleteAsync();
        Assert.Equal(["v1"], Assert.IsType<DeleteRefsViewModel>(f.Dialogs[^1]).Rows.Select(r => r.Name));
        // Only remote branches: their dialog. The active branch with its upstream: nothing.
        g.Git("push", "-q", "origin", "main", sideWork + ":refs/heads/side");
        await f.RefreshAsync();
        MenuFor(sideWork);
        await list.DeleteAsync();
        Assert.Equal(["origin/side"], Assert.IsType<DeleteRefsViewModel>(f.Dialogs[^1]).Rows.Select(r => r.Name));
        count = f.Dialogs.Count;
        MenuFor(merge);
        await list.DeleteAsync();
        Assert.Equal(count, f.Dialogs.Count);
        Assert.Empty(f.Errors);

        CommitListViewModel SelectAndGet(string sha)
        {
            list.Click(list.IndexOf(sha), false, false);
            return list;
        }
    }

    // ------------------------------------------------------------------ end to end through the real menu items

    [AvaloniaFact]
    public async Task Right_click_a_local_branch_and_push_it_to_origin()
    {
        using var f = new Fx();
        f.S.Local.Commit("feature work", 5);
        f.S.Local.Git("branch", "feature");
        f.S.Local.Git("reset", "-q", "--hard", "HEAD~1");
        await f.OpenAsync();
        f.Fill = vm => vm.SubmitAsync();
        var node = f.Node("Branches", "feature");
        Click(f, f.Row(f.Node("Branches", "main")));
        Click(f, f.Row(node), MouseButton.Right); // not selected: the menu is the clicked branch's
        Assert.Equal("main", f.Tab.Sidebar.SelectedNode!.Title);
        await WaitFor(() => f.SidebarView.Menu is not null);
        await ClickItem(f.SidebarView.Menu!, "Push to 'origin'...");
        Assert.Equal("feature", Assert.IsType<PushViewModel>(Assert.Single(f.Dialogs)).SelectedLocal!.ShortName);
        Assert.Equal(f.S.Local.Git("rev-parse", "feature"), f.S.BareSha("feature"));
        Assert.Empty(f.Errors);
    }

    [AvaloniaFact]
    public async Task Right_click_a_stash_in_the_commit_list_and_apply_it()
    {
        using var f = new Fx();
        f.S.Local.Write("a.txt", "stashed\n");
        f.S.Local.Git("stash");
        await f.OpenAsync();
        Assert.Equal("a\n", File.ReadAllText(Path.Combine(f.S.Local.Path, "a.txt")));
        f.Fill = vm => vm.SubmitAsync();
        f.Tab.CommitList.SelectCommit(f.Tab.Stashes.Single().Sha);
        var menu = f.List.OpenContextMenu()!;
        Assert.Equal(["Apply...", "Rename...", "-", "Delete...", "-", "Copy Commit SHA", "Copy Commit Info"], Headers(menu));
        await ClickItem(menu, "Apply...");
        menu.Close();
        Assert.IsType<ApplyStashViewModel>(Assert.Single(f.Dialogs));
        Assert.Equal("stashed\n", File.ReadAllText(Path.Combine(f.S.Local.Path, "a.txt")));
        Assert.Empty(f.Errors);
    }

    [AvaloniaFact]
    public async Task Stash_files_item_of_the_local_changes_menu_stashes_the_selected_file()
    {
        using var f = new Fx();
        f.S.Local.Commit("two files", 5, ("b.txt", "b\n"));
        f.S.Local.Write("a.txt", "changed a\n");
        f.S.Local.Write("b.txt", "changed b\n");
        await f.OpenAsync();
        f.Tab.ShowLocalChanges();
        await f.RefreshAsync();
        var view = f.Window.GetVisualDescendants().OfType<LocalChangesView>().Single();
        var unstaged = view.FindControl<ListBox>("UnstagedList")!;
        var index = f.Tab.LocalChanges.UnstagedRows.Select(r => r.Path).ToList().IndexOf("b.txt");
        unstaged.SelectedIndex = index;
        f.Settle();
        Click(f, unstaged.ContainerFromIndex(index)!, MouseButton.Right);
        var menu = unstaged.ContextMenu!;
        Assert.Contains("Stash 1 File...", Headers(menu));
        f.Fill = vm =>
        {
            var files = Assert.IsType<StashFilesViewModel>(vm).Files;
            Assert.Equal(["b.txt"], files.Where(r => r.IsChecked).Select(r => r.Path));
            return vm.SubmitAsync();
        };
        menu.Items.OfType<MenuItem>().Single(m => (string?)m.Header == "Stash 1 File...").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        menu.Close();
        await WaitFor(() => f.Tab.Stashes.Count == 1 && f.Tab.Jobs.IsIdle);
        Assert.Equal("b\n", File.ReadAllText(Path.Combine(f.S.Local.Path, "b.txt")));
        Assert.Equal("changed a\n", File.ReadAllText(Path.Combine(f.S.Local.Path, "a.txt")));
        Assert.Empty(f.Errors);
    }
}
