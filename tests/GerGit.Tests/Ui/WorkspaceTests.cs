using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GerGit.ViewModels;
using GerGit.ViewModels.Dialogs;
using GerGit.Views;

namespace GerGit.Tests.Ui;

/// <summary>Workspaces, the Workspaces menu, the Configure Workspaces dialog and the per-workspace session. In the
/// non-parallel "RefOps" collection with the session tests: both write the workspace keys.</summary>
[Collection("RefOps")]
public sealed class WorkspaceTests : IDisposable
{
    public WorkspaceTests() => ClearKeys();

    public void Dispose() => ClearKeys();

    internal static void ClearKeys()
    {
        Settings.Set<List<Workspace>?>(MainWindowViewModel.WorkspacesKey, null);
        Settings.Set<string?>(MainWindowViewModel.ActiveWorkspaceKey, null);
        Settings.Set<bool?>(MainWindowViewModel.ShowWorkspaceInTitleKey, null);
        Settings.Set<string[]?>(MainWindowViewModel.SessionTabsKey, null);
        Settings.Set<string?>(MainWindowViewModel.SessionActiveKey, null);
    }

    internal static void Seed(List<Workspace> list, string active)
    {
        Settings.Set(MainWindowViewModel.WorkspacesKey, list);
        Settings.Set(MainWindowViewModel.ActiveWorkspaceKey, active);
    }

    static List<Workspace> Stored => Settings.Get<List<Workspace>>(MainWindowViewModel.WorkspacesKey, []);

    static TempRepo NewRepo()
    {
        var r = new TempRepo();
        r.Commit("init", 0, ("a.txt", "1\n"));
        return r;
    }

    static async Task<MainWindowViewModel> StartAsync()
    {
        var vm = new MainWindowViewModel();
        await vm.RestoreSessionAsync();
        return vm;
    }

    static void Close(MainWindowViewModel vm)
    {
        foreach (var t in vm.Tabs.ToList()) t.Dispose();
        vm.Manager?.Dispose();
    }

    [AvaloniaFact]
    public async Task The_old_single_session_moves_into_Work_without_losing_the_tabs()
    {
        using var a = NewRepo();
        using var b = NewRepo();
        Settings.Set(MainWindowViewModel.SessionTabsKey, new[] { a.Path, b.Path });
        Settings.Set(MainWindowViewModel.SessionActiveKey, b.Path);

        var vm = await StartAsync();
        Assert.Equal([a.Path, b.Path], vm.Tabs.Select(t => t.Path));
        Assert.Equal(b.Path, vm.SelectedTab!.Path);
        Assert.Equal(["Home", "Work"], Stored.Select(w => w.Name));
        Assert.Empty(Stored[0].Repositories);
        Assert.Equal([a.Path, b.Path], Stored[1].Repositories);
        Assert.Equal(b.Path, Stored[1].Active);
        Assert.Equal("Work", Settings.Get<string?>(MainWindowViewModel.ActiveWorkspaceKey, null));
        Assert.Null(Settings.Get<string[]?>(MainWindowViewModel.SessionTabsKey, null)); // migrated once, then gone
        Assert.Null(Settings.Get<string?>(MainWindowViewModel.SessionActiveKey, null));
        Assert.Equal(("Work", "GerGit"), (vm.WorkspaceCaption, vm.WindowTitle));
        Close(vm);
    }

    [AvaloniaFact]
    public async Task Switching_saves_the_tabs_and_restores_the_chosen_workspace_or_the_manager()
    {
        using var a = NewRepo();
        using var b = NewRepo();
        using var c = NewRepo();
        using var d = NewRepo();
        Seed([new("Home", []), new("Work", [a.Path]), new("Clients/Acme", [b.Path, c.Path], c.Path)], "Work");

        var vm = await StartAsync();
        Assert.Equal([a.Path], vm.Tabs.Select(t => t.Path));
        Assert.Null(await vm.OpenAsync(d.Path));

        await vm.SwitchWorkspaceAsync("Home");
        Assert.Empty(vm.Tabs);
        Assert.True(vm.IsManagerShown); // an empty workspace shows the Repository Manager
        Assert.Equal(("Home", "Home"), (vm.ActiveWorkspace, vm.WorkspaceCaption));
        Assert.Equal("Home", Settings.Get<string?>(MainWindowViewModel.ActiveWorkspaceKey, null));
        var work = Stored.Single(w => w.Name == "Work");
        Assert.Equal([a.Path, d.Path], work.Repositories);
        Assert.Equal(d.Path, work.Active);

        await vm.SwitchWorkspaceAsync("Clients/Acme");
        Assert.Equal([b.Path, c.Path], vm.Tabs.Select(t => t.Path));
        Assert.Equal(c.Path, vm.SelectedTab!.Path);
        Assert.Null(vm.Manager);
        Assert.Equal("Acme", vm.WorkspaceCaption); // the last "/" part
        Assert.Empty(Stored.Single(w => w.Name == "Home").Repositories);

        vm.SelectedTab = vm.Tabs[0];
        await vm.NextWorkspaceAsync(); // name order: Clients/Acme, Home, Work
        Assert.Equal("Home", vm.ActiveWorkspace);
        Assert.Equal(b.Path, Stored.Single(w => w.Name == "Clients/Acme").Active);
        await vm.NextWorkspaceAsync();
        Assert.Equal([a.Path, d.Path], vm.Tabs.Select(t => t.Path));
        Assert.Equal(d.Path, vm.SelectedTab!.Path);
        await vm.NextWorkspaceAsync(); // wraps
        Assert.Equal("Clients/Acme", vm.ActiveWorkspace);
        Assert.Equal(b.Path, vm.SelectedTab!.Path);
        Close(vm);
    }

    [AvaloniaFact]
    public async Task The_menu_nests_folders_ignoring_case_and_checks_the_active_workspace()
    {
        Seed([new("Home", []), new("Work", []), new("clients/Acme", []), new("Clients/Beta", [])], "Clients/Beta");
        var vm = await StartAsync();

        var menu = vm.WorkspacesMenu();
        Assert.Equal(["Workspaces", "clients", "Home", "Work", "-", "Configure..."], menu.Select(m => m.ToString()));
        Assert.Equal(MenuEntryKind.Header, menu[0].Kind);
        var folder = menu[1].Children!;
        Assert.Equal(["Acme", "Beta"], folder.Select(m => m.Header));
        Assert.Equal([false, true], folder.Select(m => m.IsChecked));
        Assert.Equal([false, false], menu.Skip(2).Take(2).Select(m => m.IsChecked));

        await folder[0].Invoke();
        Assert.Equal(("clients/Acme", "Acme"), (vm.ActiveWorkspace, vm.WorkspaceCaption));
        Close(vm);
    }

    [AvaloniaFact]
    public async Task Configure_rejects_blank_and_duplicate_names_and_keeps_two_workspaces()
    {
        var vm = await StartAsync(); // first start: Home and Work
        var answers = new List<DialogViewModel>();
        var dialog = new ConfigureWorkspacesViewModel(vm, MainWindowViewModel.LoadWorkspaces())
        {
            ShowDialog = q => { answers.Add(q); return Task.FromResult(true); },
        };
        Assert.Equal(["Home", "Work"], dialog.Items.Select(i => i.Name));
        Assert.Equal(dialog.Items[0], dialog.SelectedItem);
        Assert.Equal(("Workspaces", "Use '/' as path separator to create folders", null, "Close"),
            (dialog.Title, dialog.Description, dialog.SubmitText, dialog.CancelText));
        Assert.False(dialog.CanRemove);
        await dialog.RemoveAsync(dialog.Items[0]); // never fewer than two
        Assert.Equal(2, dialog.Items.Count);
        Assert.Empty(answers);

        dialog.Add();
        var added = dialog.Items[2];
        Assert.Equal(("New Workspace", true), (added.Name, added.IsEditing));
        Assert.Equal(added, dialog.SelectedItem);
        added.EditText = "   ";
        dialog.CommitRename(added); // blank: the previous name stays
        Assert.Equal("New Workspace", added.Name);
        dialog.BeginRename(added);
        added.EditText = "home";
        dialog.CommitRename(added); // duplicates (ignoring case) are rejected
        Assert.Equal("New Workspace", added.Name);
        dialog.Add();
        Assert.Equal("New Workspace 2", dialog.Items[3].Name);
        dialog.CancelRename(dialog.Items[3]);
        dialog.BeginRename(added);
        added.EditText = " Clients/Acme ";
        dialog.CommitRename(added);
        Assert.Equal("Clients/Acme", added.Name);

        Assert.True(dialog.CanRemove);
        await dialog.RemoveAsync(dialog.Items[1]);
        Assert.Equal(("Do you want to delete the workspace 'Work'?", "You can't undo this action", "Delete"),
            (answers[0].Title, answers[0].Description, answers[0].SubmitText));
        Assert.Equal(["Home", "Clients/Acme", "New Workspace 2"], dialog.Items.Select(i => i.Name));
        Assert.Equal(added, dialog.SelectedItem); // the row at the same index

        var (list, active) = dialog.Outcome("Work");
        Assert.Null(active); // the active workspace was deleted
        Assert.Equal(["Home", "Clients/Acme", "New Workspace 2"], list.Select(w => w.Name));
        Close(vm);
    }

    [AvaloniaFact]
    public async Task Closing_Configure_stores_the_sorted_list_keeps_the_renamed_active_one_and_shows_the_caption()
    {
        using var a = NewRepo();
        Seed([new("Home", []), new("Work", [a.Path], a.Path)], "Work");
        var vm = await StartAsync();
        var window = new MainWindow { DataContext = vm };
        window.Show();
        TextBlock Caption() => window.FindControl<Button>("WorkspacesButton")!.GetVisualDescendants().OfType<TextBlock>().Single();
        Assert.True(window.FindControl<Button>("WorkspacesButton")!.IsEnabled);
        Assert.Equal("Work", Caption().Text);
        Assert.Equal("GerGit", window.Title);

        vm.ShowDialog = q =>
        {
            var d = (ConfigureWorkspacesViewModel)q;
            d.BeginRename(d.Items[1]);
            d.Items[1].EditText = "Office/Work";
            d.ShowInTitle = true; // applies at once
            d.Add();
            d.Items[2].EditText = "Archive";
            return Task.FromResult(false); // "Close"; the open rename is committed
        };
        await vm.ConfigureWorkspacesAsync();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(["Archive", "Home", "Office/Work"], Stored.Select(w => w.Name));
        Assert.Equal([a.Path], Stored[2].Repositories); // renamed, tabs kept
        Assert.Equal("Office/Work", vm.ActiveWorkspace);
        Assert.Equal([a.Path], vm.Tabs.Select(t => t.Path)); // the tabs stay open
        Assert.Equal("Work", Caption().Text);
        Assert.Equal("GerGit - Office/Work", window.Title);
        Assert.True(Settings.Get(MainWindowViewModel.ShowWorkspaceInTitleKey, false));

        // The active workspace deleted: the first one (sorted) is restored.
        vm.ShowDialog = q =>
        {
            var d = (ConfigureWorkspacesViewModel)q;
            d.ShowDialog = _ => Task.FromResult(true);
            return d.RemoveAsync(d.Items[2]).ContinueWith(_ => false, TaskScheduler.FromCurrentSynchronizationContext());
        };
        await vm.ConfigureWorkspacesAsync();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(["Archive", "Home"], Stored.Select(w => w.Name));
        Assert.Equal("Archive", vm.ActiveWorkspace);
        Assert.Empty(vm.Tabs);
        Assert.True(vm.IsManagerShown);
        Assert.Equal("Archive", Caption().Text);
        window.Close();
        Close(vm);
    }

    [AvaloniaFact]
    public async Task A_stored_list_with_fewer_than_two_workspaces_gets_the_defaults_and_keeps_its_own()
    {
        using var a = NewRepo();
        Settings.Set(MainWindowViewModel.WorkspacesKey, new List<Workspace> { new("Solo", [a.Path], a.Path) });
        Settings.Set(MainWindowViewModel.ActiveWorkspaceKey, "Solo");
        var vm = await StartAsync();
        Assert.Equal(["Home", "Solo", "Work"], Stored.Select(w => w.Name));
        Assert.Equal("Solo", vm.ActiveWorkspace);
        Assert.Equal([a.Path], vm.Tabs.Select(t => t.Path));
        Close(vm);
    }

    [AvaloniaFact]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task The_restore_keeps_folders_that_exist_but_did_not_open_and_the_manager_keeps_the_active_one()
    {
        using var a = NewRepo();
        using var b = NewRepo();
        using var locked = NewRepo(); // exists, but git cannot read it (like an unmounted or broken repository): kept
        using var plain = new TempRepo(init: false); // exists, but is no git repository: dropped
        var gone = Path.Combine(Path.GetTempPath(), "gergit-test-gone-" + Guid.NewGuid().ToString("N")[..12]);
        Seed([new("Home", []), new("Work", [a.Path, locked.Path, plain.Path, gone, b.Path], b.Path)], "Work");
        var mode = File.GetUnixFileMode(locked.Path);
        File.SetUnixFileMode(locked.Path, UnixFileMode.None);
        try
        {
            var vm = await StartAsync();
            Assert.Equal([a.Path, b.Path], vm.Tabs.Select(t => t.Path));
            Assert.Equal([a.Path, b.Path, locked.Path], Stored.Single(w => w.Name == "Work").Repositories);

            vm.NewTab(); // the Repository Manager tab selected: the stored active repository stays
            Assert.Equal(b.Path, Stored.Single(w => w.Name == "Work").Active);
            Close(vm);
        }
        finally { File.SetUnixFileMode(locked.Path, mode); }
    }

    [AvaloniaFact]
    public async Task Stored_repository_paths_are_normalised_in_the_background_and_never_block_the_restore()
    {
        using var a = NewRepo();
        Seed([new("Home", []), new("Work", [a.Path], a.Path)], "Work");
        Settings.Set(RepositoryList.EntriesKey, new List<RepoEntry> { new(a.Path + "/", "Alias"), new("bad\0path", "Kept") });
        using var gate = new ManualResetEventSlim();
        RepositoryList.Normalizer = p => { gate.Wait(); return GerGit.Core.RepositoryScan.Normalize(p); };
        RepositoryList.ResetNormalizeOnce();
        try
        {
            Assert.Equal(2, RepositoryList.Entries.Count); // the stored list, without touching the file system
            var vm = new MainWindowViewModel();
            var restore = vm.RestoreSessionAsync();
            Assert.Same(restore, await Task.WhenAny(restore, Task.Delay(10000))); // a hung mount does not block the start
            Assert.Equal([a.Path], vm.Tabs.Select(t => t.Path));
            Assert.Equal(Path.GetFileName(a.Path), vm.Tabs[0].Title); // the stored key is not resolved yet

            gate.Set();
            Assert.True(await RepositoryList.NormalizeStoredOnceAsync());
            Dispatcher.UIThread.RunJobs();
            // The slash is gone, the malformed path is kept as it is, and the alias now reaches the tab title.
            Assert.Equal([a.Path, "bad\0path"], RepositoryList.Entries.Select(e => e.Path));
            Assert.Equal("Alias", vm.Tabs[0].Title);
            Close(vm);
        }
        finally
        {
            gate.Set();
            RepositoryList.Normalizer = GerGit.Core.RepositoryScan.Normalize;
            RepositoryList.ResetNormalizeOnce();
            Settings.Set<List<RepoEntry>?>(RepositoryList.EntriesKey, null);
        }
    }

    [AvaloniaFact]
    public async Task Names_with_an_empty_slash_part_are_rejected()
    {
        var vm = await StartAsync();
        var dialog = new ConfigureWorkspacesViewModel(vm, MainWindowViewModel.LoadWorkspaces());
        var item = dialog.Items[0];
        foreach (var bad in new[] { "a/", "a//b", "/a", "a/ /b" })
        {
            dialog.BeginRename(item);
            item.EditText = bad;
            dialog.CommitRename(item);
            Assert.Equal("Home", item.Name);
        }
        dialog.BeginRename(item);
        item.EditText = "a/b";
        dialog.CommitRename(item);
        Assert.Equal("a/b", item.Name);
        Close(vm);
    }
}
