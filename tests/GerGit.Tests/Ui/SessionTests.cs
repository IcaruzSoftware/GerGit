using Avalonia.Headless.XUnit;
using GerGit.ViewModels;

namespace GerGit.Tests.Ui;

/// <summary>Session: the open tabs and the active one survive a restart, stored in the active workspace
/// ("Work"). Only these and the workspace tests restore a session, so only they write the workspace keys (a view model
/// saves nothing before its restore). In the non-parallel "RefOps" collection: the global "worktrees seen" flag is reset
/// afterwards.</summary>
[Collection("RefOps")]
public sealed class SessionTests : IDisposable
{
    /// <summary>After every test (xunit's finally): the session and workspace keys are gone again.</summary>
    public void Dispose() => WorkspaceTests.ClearKeys();

    static Workspace Work => Settings.Get<List<Workspace>>(MainWindowViewModel.WorkspacesKey, []).Single(w => w.Name == "Work");
    static string[] StoredTabs => Work.Repositories;
    static string? StoredActive => Work.Active;

    /// <summary>The stored session of the active workspace "Work".</summary>
    static void Seed(string[] tabs, string? active = null) => WorkspaceTests.Seed([new("Home", []), new("Work", tabs, active)], "Work");

    static async Task<MainWindowViewModel> StartAsync(string? prefer = null)
    {
        var vm = new MainWindowViewModel();
        await vm.RestoreSessionAsync(prefer);
        return vm;
    }

    static TempRepo NewRepo()
    {
        var r = new TempRepo();
        r.Commit("init", 0, ("a.txt", "1\n"));
        return r;
    }

    [AvaloniaFact]
    public async Task Two_tabs_and_a_worktree_come_back_in_order_with_the_active_one()
    {
        using var a = NewRepo();
        using var b = NewRepo();
        var wt = a.Path + "-wt";
        a.Git("worktree", "add", "-q", "-b", "wt", wt);
        try
        {
            var first = await StartAsync();
            Assert.Empty(first.Tabs); // empty session: the Repository Manager tab
            Assert.Null(first.SelectedTab);
            Assert.True(first.IsManagerShown);
            Assert.Null(await first.OpenAsync(a.Path));
            Assert.Null(await first.OpenAsync(b.Path));
            await first.SelectedTab!.WorktreeOps.OpenAsync([wt]); // inserted after b, the tab it was opened from
            first.SelectedTab = first.Tabs[1];
            Assert.Equal([a.Path, b.Path, wt], StoredTabs);
            Assert.Equal(b.Path, StoredActive);

            var second = await StartAsync();
            Assert.Equal([a.Path, b.Path, wt], second.Tabs.Select(t => t.Path));
            Assert.Equal(second.Tabs[1], second.SelectedTab);
            Assert.Equal($"{Path.GetFileName(a.Path)}: {Path.GetFileName(wt)}", second.Tabs[2].Title); // worktree title prefix
            await second.Tabs[1].Refreshing;
            Assert.True(second.Tabs[1].IsLoaded);
            Assert.False(second.Tabs[0].IsLoaded); // only the selected tab is refreshed

            second.SelectedTab = second.Tabs[0]; // a restored tab loads when it is selected
            await second.Tabs[0].Refreshing;
            Assert.True(second.Tabs[0].IsLoaded);
            Assert.Equal(a.Path, StoredActive);
            foreach (var t in first.Tabs.Concat(second.Tabs).ToList()) t.Dispose();
        }
        finally
        {
            Settings.Set<string?>(RepoTabViewModel.ShowWorktreesKey, null);
            try { Directory.Delete(wt, recursive: true); } catch (IOException) { }
        }
    }

    [AvaloniaFact]
    public async Task A_path_that_no_longer_opens_is_skipped()
    {
        using var a = NewRepo();
        using var b = NewRepo();
        var gone = Path.Combine(Path.GetTempPath(), "gergit-test-gone-" + Guid.NewGuid().ToString("N")[..12]);
        Seed(new[] { a.Path, gone, b.Path }, gone);

        var vm = await StartAsync();
        Assert.Equal([a.Path, b.Path], vm.Tabs.Select(t => t.Path));
        Assert.Equal(vm.Tabs[0], vm.SelectedTab); // the stored active one is gone: the first tab
        Assert.Equal([a.Path, b.Path], StoredTabs);
        Assert.Equal(a.Path, StoredActive);
        foreach (var t in vm.Tabs.ToList()) t.Dispose();

        Seed(new[] { gone });
        var empty = await StartAsync();
        Assert.Empty(empty.Tabs);
        Assert.Null(empty.SelectedTab);
        Assert.True(empty.IsManagerShown);
        Assert.Empty(StoredTabs);
    }

    [AvaloniaFact]
    public async Task The_command_line_argument_is_selected_or_added_after_the_restore()
    {
        using var a = NewRepo();
        using var b = NewRepo();
        using var c = NewRepo();
        Seed(new[] { a.Path, b.Path }, b.Path);

        // App.OnFrameworkInitializationCompleted: restore preferring the argument, then open the argument.
        var vm = await StartAsync(a.Path);
        Assert.Null(await vm.OpenAsync(a.Path)); // already open: the restore selected it, nothing is added
        Assert.Equal([a.Path, b.Path], vm.Tabs.Select(t => t.Path));
        Assert.Equal(vm.Tabs[0], vm.SelectedTab);
        await vm.Tabs[0].Refreshing;
        Assert.True(vm.Tabs[0].IsLoaded);
        Assert.False(vm.Tabs[1].IsLoaded); // the stored active tab never loaded: only the selected one refreshes
        foreach (var t in vm.Tabs.ToList()) t.Dispose();

        var next = await StartAsync(c.Path); // a.Path is the stored active one now
        Assert.Null(await next.OpenAsync(c.Path)); // not open: added at the end and selected
        Assert.Equal([a.Path, b.Path, c.Path], next.Tabs.Select(t => t.Path));
        Assert.Equal(next.Tabs[2], next.SelectedTab);
        Assert.Equal([a.Path, b.Path, c.Path], StoredTabs);
        Assert.Equal(c.Path, StoredActive);
        foreach (var t in next.Tabs.ToList()) t.Dispose();
    }

    [AvaloniaFact]
    public async Task Closing_a_tab_updates_the_stored_session()
    {
        using var a = NewRepo();
        using var b = NewRepo();
        using var c = NewRepo();
        Seed(new[] { a.Path, b.Path, c.Path }, c.Path);
        var vm = await StartAsync();

        vm.CloseTab(vm.Tabs[0]); // not the selected one
        Assert.Equal([b.Path, c.Path], StoredTabs);
        Assert.Equal(c.Path, StoredActive);
        vm.CloseTab(vm.SelectedTab); // the selected one: its left neighbour becomes active
        Assert.Equal([b.Path], StoredTabs);
        Assert.Equal(b.Path, StoredActive);
        vm.CloseTab(vm.SelectedTab);
        Assert.Empty(StoredTabs);
        Assert.Null(StoredActive);
    }

    [AvaloniaFact]
    public async Task Passing_through_a_tab_that_never_loaded_cancels_its_first_load()
    {
        using var a = NewRepo();
        using var b = NewRepo();
        using var c = NewRepo();
        Seed(new[] { a.Path, b.Path, c.Path }, a.Path);
        var vm = await StartAsync();
        var errors = new List<string>();
        foreach (var t in vm.Tabs) t.ShowError = (_, text) => { errors.Add(text); return Task.CompletedTask; };

        vm.SelectedTab = vm.Tabs[1]; // Ctrl+Tab twice
        vm.SelectedTab = vm.Tabs[2];
        await vm.Tabs[1].Refreshing;
        Assert.False(vm.Tabs[1].IsLoaded); // cancelled, not half-loaded
        Assert.Empty(vm.Tabs[1].Refs.Refs);
        Assert.Empty(errors);

        vm.SelectedTab = vm.Tabs[1]; // loads again when selected
        while (!vm.Tabs[1].Refreshing.IsCompleted) await vm.Tabs[1].Refreshing;
        Assert.True(vm.Tabs[1].IsLoaded);
        Assert.NotEmpty(vm.Tabs[1].Refs.Refs);
        Assert.Empty(errors);
        foreach (var t in vm.Tabs.ToList()) t.Dispose();
    }

    [AvaloniaFact]
    public async Task Nothing_is_saved_before_the_restore()
    {
        using var a = NewRepo();
        Seed(new[] { a.Path });
        var vm = new MainWindowViewModel(); // e.g. git missing: the restore never runs
        vm.SaveSession();
        Assert.Equal([a.Path], StoredTabs);
    }
}
