using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using GerGit.Core;
using GerGit.Core.Git;
using GerGit.ViewModels.Dialogs;

namespace GerGit.ViewModels;

/// <summary>A named set of open tabs: the repository paths in tab order and the active one.</summary>
public sealed record Workspace(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("repositories")] string[] Repositories,
    [property: JsonPropertyName("active")] string? Active = null);

/// <summary>Workspaces and the per-workspace session. Not built yet: a "Workspaces" submenu in the tab menu (the tab menu does not exist yet) and a
/// dirty-marker update on switch (no dirty markers yet).</summary>
public sealed partial class MainWindowViewModel
{
    public const string WorkspacesKey = "workspaces.list", ActiveWorkspaceKey = "workspaces.active",
        ShowWorkspaceInTitleKey = "workspaces.showInTitle";

    /// <summary>The single session of GerGit before workspaces; moved into "Work" on the first load, then removed.</summary>
    public const string SessionTabsKey = "session.tabs", SessionActiveKey = "session.active";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WorkspaceCaption), nameof(WindowTitle))]
    public partial string ActiveWorkspace { get; private set; } = Settings.Get(ActiveWorkspaceKey, "Work");

    /// <summary>"Show workspace name in title bar" (Configure Workspaces), stored at once.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    public partial bool ShowWorkspaceInTitle { get; set; } = Settings.Get(ShowWorkspaceInTitleKey, false);

    partial void OnShowWorkspaceInTitleChanged(bool value) => Settings.Set(ShowWorkspaceInTitleKey, value);

    /// <summary>Toolbar caption: the last "/" part of the active workspace's name.</summary>
    public string WorkspaceCaption => ActiveWorkspace[(ActiveWorkspace.LastIndexOf('/') + 1)..];

    public string WindowTitle => ShowWorkspaceInTitle ? $"GerGit - {ActiveWorkspace}" : "GerGit";

    /// <summary>The stored workspaces. The first load creates "Home" (empty) and "Work" (active, with the tabs of the
    /// old single session, so nobody loses the open tabs); at least two always exist.</summary>
    public static List<Workspace> LoadWorkspaces()
    {
        var stored = Settings.Get<List<Workspace>?>(WorkspacesKey, null);
        // A hand-edited file may lack names or lists: nameless entries go, a missing list is empty.
        var list = stored?.Where(w => !string.IsNullOrWhiteSpace(w.Name))
            .Select(w => w with { Repositories = w.Repositories ?? [] })
            .OrderBy(w => w.Name, RemoteOps.Natural).ToList();
        if (list is { Count: >= 2 }) return list;
        if (stored is null) Settings.Set(ActiveWorkspaceKey, "Work");
        list ??= [];
        Workspace[] defaults =
        [
            new("Home", []),
            new("Work", Settings.Get<string[]>(SessionTabsKey, []), Settings.Get<string?>(SessionActiveKey, null)),
        ];
        list.AddRange(defaults.Where(d => !list.Any(w => w.Name == d.Name)));
        StoreWorkspaces(list);
        Settings.Set<string[]?>(SessionTabsKey, null);
        Settings.Set<string?>(SessionActiveKey, null);
        return list.OrderBy(w => w.Name, RemoteOps.Natural).ToList();
    }

    /// <summary>The list is kept sorted by name.</summary>
    static void StoreWorkspaces(IEnumerable<Workspace> list) =>
        Settings.Set(WorkspacesKey, list.OrderBy(w => w.Name, RemoteOps.Natural).ToList());

    /// <summary>Paths of the active workspace whose folder exists but did not open at the restore for another reason than
    /// "not a git repository" (an unsafe repository, an I/O error, git missing): kept in the session after the open tabs
    /// until they open again.</summary>
    List<string> _unopened = [];

    /// <summary>The open repository paths in tab order and the active one, into the active
    /// workspace. The Repository Manager tab is not saved: while it is selected the previous active repository stays
    /// when it is still open.</summary>
    public void SaveSession()
    {
        if (!_sessionRestored) return;
        var list = LoadWorkspaces();
        int i = list.FindIndex(w => w.Name == ActiveWorkspace);
        if (i < 0) return;
        _unopened.RemoveAll(p => TabOf(p) is not null);
        var active = SelectedTab?.Path ?? (list[i].Active is { } previous && TabOf(previous) is not null ? previous : null);
        list[i] = list[i] with { Repositories = [.. Tabs.Select(t => t.Path), .. _unopened], Active = active };
        StoreWorkspaces(list);
    }

    /// <summary>Reopens the active workspace's tabs in order (a path that is gone or no git repository
    /// any more is dropped; one that fails otherwise is kept for later) and selects the tab of <paramref name="prefer"/>
    /// (the command-line argument), else the stored active one, else the first; only that one is refreshed. Nothing
    /// opened: the Repository Manager tab.
    /// Limitation: Open, Clone or a drop while the restore awaits git adds its tab before the restored ones and loses the
    /// selection to the restore; awaiting the running restore at the start of OpenAsync would fix that.</summary>
    public async Task RestoreSessionAsync(string? prefer = null)
    {
        var list = LoadWorkspaces();
        var workspace = list.FirstOrDefault(w => w.Name == ActiveWorkspace) ?? list[0];
        ActiveWorkspace = workspace.Name;
        _unopened = [];
        foreach (var path in workspace.Repositories)
        {
            GitRepository repo;
            try { repo = await GitRepository.OpenAsync(path); }
            catch (Exception e) when (e is GitException or IOException or UnauthorizedAccessException or ArgumentException or Win32Exception)
            {
                // Dropped: a folder that is gone, or one that is no git repository any more.
                bool notRepository = e is GitException && e.Message.EndsWith("is not a git repository", StringComparison.Ordinal);
                if (!notRepository && Directory.Exists(path)) _unopened.Add(path);
                continue;
            }
            if (TabOf(repo.Info.WorkTree) is null) Tabs.Add(new RepoTabViewModel(repo) { Main = this });
        }
        UpdateTitles();
        _sessionRestored = true;
        var active = workspace.Active;
        SelectedTab = (prefer is null ? null : TabOf(prefer)) ?? (active is null ? null : TabOf(active)) ?? Tabs.FirstOrDefault();
        if (Tabs.Count == 0) NewTab();
        else RemoveManager();
        SaveSession(); // drops the paths that are gone or no repository
        _ = NormalizeListAsync();
    }

    /// <summary>Once per session, in the background: stored repository paths get their resolved key (see
    /// <see cref="RepositoryList.NormalizeStoredAsync"/>); then tab titles and the manager follow.</summary>
    async Task NormalizeListAsync()
    {
        try
        {
            if (!await RepositoryList.NormalizeStoredOnceAsync()) return;
        }
        catch (Exception e)
        {
            Trace.WriteLine(e);
            return;
        }
        UpdateTitles();
        Manager?.Reload(Manager.SelectedNode?.Path);
    }

    /// <summary>Workspaces menu item: saves the current tabs, closes them all and restores the chosen workspace (the
    /// Repository Manager when it is empty). The active one, or a switch before the first restore, does nothing.</summary>
    public async Task SwitchWorkspaceAsync(string name)
    {
        if (name == ActiveWorkspace || !_sessionRestored) return;
        SaveSession();
        await ReopenAsync(name);
    }

    async Task ReopenAsync(string name)
    {
        _sessionRestored = false; // closing the tabs must not overwrite any workspace
        SelectedTab = null;
        foreach (var t in Tabs.ToList()) t.Dispose();
        Tabs.Clear();
        UpdateTitles();
        ActiveWorkspace = name;
        Settings.Set(ActiveWorkspaceKey, name);
        await RestoreSessionAsync();
    }

    /// <summary>Ctrl+click on the toolbar button: the next workspace in name order (wraps).</summary>
    public Task NextWorkspaceAsync()
    {
        var names = LoadWorkspaces().Select(w => w.Name).ToList();
        if (names.Count < 2) return Task.CompletedTask;
        return SwitchWorkspaceAsync(names[(names.IndexOf(ActiveWorkspace) + 1) % names.Count]);
    }

    /// <summary>Workspaces menu, built each time it opens: names split at "/" into submenus (folders matched
    /// ignoring case), a check mark on the active one, then "Configure...".</summary>
    public List<MenuEntry> WorkspacesMenu()
    {
        var names = LoadWorkspaces().Select(w => (w.Name, w.Name.Split('/'))).ToList();
        return [Menus.Heading("Workspaces"), .. WorkspaceLevel(names, 0), Menus.Separator, Menus.Item("Configure...", ConfigureWorkspacesAsync)];
    }

    List<MenuEntry> WorkspaceLevel(List<(string Full, string[] Parts)> items, int depth)
    {
        var result = new List<MenuEntry>();
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (full, parts) in items)
        {
            if (parts.Length == depth + 1)
                result.Add(new() { Header = parts[depth], IsChecked = full == ActiveWorkspace, Action = () => SwitchWorkspaceAsync(full) });
            else if (folders.Add(parts[depth]))
                result.Add(Menus.Sub(parts[depth], WorkspaceLevel(items.Where(i => i.Parts.Length > depth + 1
                    && string.Equals(i.Parts[depth], parts[depth], StringComparison.OrdinalIgnoreCase)).ToList(), depth + 1)));
        }
        return result;
    }

    /// <summary>"Configure...". On close the list is stored sorted by name and the
    /// active workspace is kept by name (renamed with it). Design choice: the active workspace's
    /// session is not restored on every close; that only happens when the active workspace was deleted (then the first one
    /// opens), so closing the window keeps the open tabs as they are.</summary>
    public async Task ConfigureWorkspacesAsync()
    {
        SaveSession();
        var dialog = new ConfigureWorkspacesViewModel(this, LoadWorkspaces());
        await ShowDialog(dialog);
        var (list, active) = dialog.Outcome(ActiveWorkspace);
        StoreWorkspaces(list);
        if (active is null) await ReopenAsync(LoadWorkspaces()[0].Name);
        else
        {
            ActiveWorkspace = active;
            Settings.Set(ActiveWorkspaceKey, active);
            SaveSession(); // a repository opened while the window was open (a forwarded start) is not lost
        }
    }
}
