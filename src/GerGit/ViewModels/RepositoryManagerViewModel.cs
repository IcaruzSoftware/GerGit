using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using GerGit.Core;
using GerGit.Core.Git;
using GerGit.Hosting;
using GerGit.ViewModels.Dialogs;

namespace GerGit.ViewModels;

public enum RepoNodeKind { Section, Source, Folder, Repository }

/// <summary>A row of the Repository Manager tree.</summary>
public sealed partial class RepoNode(RepositoryManagerViewModel owner, RepoNodeKind kind, string name, string path, RepoNode? parent)
    : ObservableObject
{
    public RepoNodeKind Kind { get; } = kind;

    /// <summary>Section title, source folder title, folder name, or the repository's alias / folder name.</summary>
    public string Name { get; } = name;

    /// <summary>Full path; sections use <see cref="RepositoryManagerViewModel.RecentKey"/> /
    /// <see cref="RepositoryManagerViewModel.RepositoriesKey"/>. Also the key of the remembered expanded rows.</summary>
    public string Path { get; } = path;

    public RepoNode? Parent { get; } = parent;
    public ObservableCollection<RepoNode> Children { get; } = [];

    public bool IsSection => Kind == RepoNodeKind.Section;
    public bool IsRepository => Kind == RepoNodeKind.Repository;
    public bool IsFolder => Kind is RepoNodeKind.Source or RepoNodeKind.Folder;
    public string? ToolTip => Kind == RepoNodeKind.Source ? Path : null;

    /// <summary>The repositories in this row and below it.</summary>
    public IEnumerable<RepoNode> Repositories => IsRepository ? [this] : Children.SelectMany(c => c.Repositories);

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    partial void OnIsExpandedChanged(bool value) => owner.SaveExpanded();

    /// <summary>False when the background check found no valid repository at the path (warning icon).</summary>
    [ObservableProperty]
    public partial bool IsValid { get; set; } = true;

    /// <summary>F2 rename in place.</summary>
    [ObservableProperty]
    public partial bool IsEditing { get; set; }

    [ObservableProperty]
    public partial string EditText { get; set; } = "";

    public override string ToString() => Name;
}

/// <summary>
/// The Repository Manager tab (start screen): the tree of known repositories grouped by
/// source folder and folder, the details panel of the selected repository, and the background scan of the source folders.
/// Not built yet: a colour row in the menu (repository colours) and a statistics tab.
/// </summary>
public sealed partial class RepositoryManagerViewModel : ObservableObject, IDisposable
{
    public const string RecentKey = "#Recent", RepositoriesKey = "#Repositories";
    public const string TreeWidthKey = "layout.repoManagerTreeWidth", ExpandedKey = "layout.repoManagerExpanded";

    /// <summary>Short debounce before the details are read.</summary>
    internal static TimeSpan DetailsDelay { get; set; } = TimeSpan.FromMilliseconds(200);

    readonly MainWindowViewModel _main;
    readonly CancellationTokenSource _cts = new();
    CancellationTokenSource? _detailsCts; // the running details read, cancelled when the selection changes
    bool _building;

    public RepositoryManagerViewModel(MainWindowViewModel main)
    {
        _main = main;
        OpenPath = async path =>
        {
            if (await main.OpenAsync(path) is { } failure) Trace.WriteLine($"{failure.Title}: {failure.Text}");
        };
        SelectedNodes.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(SelectedNode));
            if (!_building) DetailsLoading = LoadDetailsAsync();
        };
        Reload();
        Scanning = BackgroundScanAsync();
    }

    public ObservableCollection<RepoNode> Roots { get; } = [];
    public ObservableCollection<RepoNode> SelectedNodes { get; } = [];
    public RepoNode? SelectedNode => SelectedNodes.Count > 0 ? SelectedNodes[0] : null;

    /// <summary>The list is empty: the tree is replaced by "Drop repository here to add".</summary>
    [ObservableProperty]
    public partial bool IsEmpty { get; private set; }

    /// <summary>Details of the selected repository; null = placeholder (no single repository selected).</summary>
    [ObservableProperty]
    public partial RepoDetails? Details { get; private set; }

    /// <summary>The last background scan: started with the tab and by <see cref="Refresh"/> (tests await it).</summary>
    public Task Scanning { get; private set; }

    /// <summary>The last details read (tests await it).</summary>
    public Task DetailsLoading { get; private set; } = Task.CompletedTask;

    /// <summary>The last validity check of the rows (tests await it).</summary>
    public Task Validating { get; private set; } = Task.CompletedTask;

    /// <summary>Opens a repository as a tab; the main window shows the failure dialogs (default: only logged).</summary>
    internal Func<string, Task> OpenPath { get; set; }

    public void Dispose() => _cts.Cancel();

    IEnumerable<RepoNode> All() => Roots.SelectMany(Flatten);

    static IEnumerable<RepoNode> Flatten(RepoNode n) => n.Children.SelectMany(Flatten).Prepend(n);

    /// <summary>The rows a user can see (children of expanded rows), top to bottom.</summary>
    public IEnumerable<RepoNode> VisibleRows() => Roots.SelectMany(Visible);

    static IEnumerable<RepoNode> Visible(RepoNode n) => (n.IsExpanded ? n.Children.SelectMany(Visible) : []).Prepend(n);

    /// <summary>Rebuilds the tree from the list. Selects the row of <paramref name="select"/>; else the first repository
    /// with <paramref name="firstRepository"/>; else the first "Recent" entry, otherwise the first repository.</summary>
    public void Reload(string? select = null, bool firstRepository = false)
    {
        var entries = RepositoryList.Entries;
        var expanded = Settings.Get<List<string>?>(ExpandedKey, null) ?? [RecentKey, RepositoriesKey];
        var sources = RepositoryList.SourceFolders.Order(RemoteOps.Natural).ToList();
        RepoNode recent, repos;
        _building = true;
        try
        {
            SelectedNodes.Clear();
            Roots.Clear();
            recent = new RepoNode(this, RepoNodeKind.Section, "Recent", RecentKey, null);
            repos = new RepoNode(this, RepoNodeKind.Section, "Repositories", RepositoriesKey, null);
            foreach (var e in entries.Where(e => e.LastOpened is not null).OrderByDescending(e => e.LastOpened).Take(5))
                recent.Children.Add(Repository(e, recent));

            var sourceNodes = sources.Count > 1
                ? sources.ToDictionary(s => s, s => new RepoNode(this, RepoNodeKind.Source, SourceTitle(s, sources), s, repos))
                : null;
            if (sourceNodes is not null) foreach (var s in sourceNodes.Values) repos.Children.Add(s);
            foreach (var e in entries)
            {
                var source = sources.Where(s => RepositoryList.IsBelow(e.Path, s)).MaxBy(s => s.Length);
                var parent = source is not null && sourceNodes is not null ? sourceNodes[source] : repos;
                // A repository outside every source folder sits directly under "Repositories", without folders.
                if (source is not null && e.Path != source)
                {
                    var folder = source;
                    foreach (var part in System.IO.Path.GetRelativePath(source, e.Path).Split('/')[..^1])
                    {
                        folder = System.IO.Path.Combine(folder, part);
                        var next = parent.Children.FirstOrDefault(c => c.Kind == RepoNodeKind.Folder && c.Name == part);
                        if (next is null) parent.Children.Add(next = new RepoNode(this, RepoNodeKind.Folder, part, folder, parent));
                        parent = next;
                    }
                }
                var row = Repository(e, parent);
                if (parent.Children.Any(c => c.IsRepository && c.Name == row.Name))
                {
                    Trace.WriteLine($"Repository Manager: '{e.Path}' has the same name as another repository in its folder");
                    continue;
                }
                parent.Children.Add(row);
            }
            Sort(repos);
            Roots.Add(recent);
            Roots.Add(repos);
            foreach (var n in All()) n.IsExpanded = !n.IsRepository && expanded.Contains(n.Path);
            IsEmpty = entries.Count == 0;
        }
        finally { _building = false; }

        var first = repos.Repositories.FirstOrDefault();
        var target = (select is null ? null : All().FirstOrDefault(n => !n.IsSection && n.Path == select))
                     ?? (firstRepository ? first : recent.Children.FirstOrDefault() ?? first);
        if (target is null) DetailsLoading = LoadDetailsAsync();
        else SelectedNodes.Add(target);
        Validating = ValidateAsync();
    }

    RepoNode Repository(RepoEntry e, RepoNode parent) =>
        new(this, RepoNodeKind.Repository, e.Alias ?? System.IO.Path.GetFileName(e.Path), e.Path, parent);

    /// <summary>The source folder path without the prefix all source folders share, joined by "/"; the last component
    /// when nothing is left.</summary>
    static string SourceTitle(string source, List<string> sources)
    {
        var split = sources.Select(s => s.Split('/', StringSplitOptions.RemoveEmptyEntries)).ToList();
        int shared = 0;
        while (split.All(p => p.Length > shared && p[shared] == split[0][shared])) shared++;
        var parts = source.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > shared ? string.Join('/', parts[shared..]) : parts.LastOrDefault() ?? source;
    }

    /// <summary>Source folders, then folders, then repositories; each group in natural, case-insensitive order.</summary>
    static void Sort(RepoNode node)
    {
        var sorted = node.Children.OrderBy(c => c.Kind switch { RepoNodeKind.Source => 0, RepoNodeKind.Folder => 1, _ => 2 })
            .ThenBy(c => c.Name, RemoteOps.Natural).ToList();
        node.Children.Clear();
        foreach (var c in sorted)
        {
            node.Children.Add(c);
            Sort(c);
        }
    }

    /// <summary>Remembers the expanded rows.</summary>
    internal void SaveExpanded()
    {
        if (_building) return;
        Settings.Set(ExpandedKey, All().Where(n => n.IsExpanded).Select(n => n.Path).Distinct().ToList());
    }

    /// <summary>Selects exactly this row.</summary>
    public void Select(RepoNode node)
    {
        _building = true;
        SelectedNodes.Clear();
        _building = false;
        SelectedNodes.Add(node);
    }

    /// <summary>Typing a letter: the next visible row whose name starts with it (wraps).</summary>
    public void JumpTo(string text)
    {
        var rows = VisibleRows().ToList();
        int start = SelectedNode is { } s ? rows.IndexOf(s) : -1;
        for (int k = 1; k <= rows.Count; k++)
        {
            var row = rows[(start + k + rows.Count) % rows.Count];
            if (row.Name.StartsWith(text, StringComparison.OrdinalIgnoreCase))
            {
                Select(row);
                return;
            }
        }
    }

    // "Validating repository path..." as one hidden background check over all rows.
    async Task ValidateAsync()
    {
        var rows = All().Where(n => n.IsRepository).ToList();
        var paths = rows.Select(n => n.Path).Distinct().ToList();
        var invalid = await Task.Run(() => paths.Where(p => RepositoryScan.Validate(p) is null).ToHashSet());
        foreach (var n in rows) n.IsValid = !invalid.Contains(n.Path);
    }

    /// <summary>After Preferences closed (source folders may have changed): reload, then scan in the background.</summary>
    public void Refresh()
    {
        Reload(SelectedNode?.Path);
        Scanning = BackgroundScanAsync();
    }

    async Task BackgroundScanAsync()
    {
        if (await ScanAsync(reset: false) > 0) Reload(SelectedNode?.Path);
    }

    /// <summary>Repository scan over all source folders in the background: adds what is new (reset: replaces the
    /// list, ignoring nothing). Returns the number of added entries, -1 when cancelled (the tab closed).</summary>
    internal Task<int> ScanAsync(bool reset) => ScanSourcesAsync(reset, _cts.Token);

    /// <summary><see cref="ScanAsync"/> without a Repository Manager tab (Quick Launch opening).</summary>
    internal static async Task<int> ScanSourcesAsync(bool reset, CancellationToken ct)
    {
        var depth = RepositoryList.ScanDepth;
        List<string> storedIgnored = reset ? [] : RepositoryList.Ignored;
        List<(string Walked, string Real)> found;
        try
        {
            // All file-system work, normalising included, off the UI thread.
            found = await Task.Run(() =>
            {
                var sources = RepositoryList.RefreshSourceFolders();
                var ignored = storedIgnored.Select(RepositoryList.SafeNormalize).ToList();
                return RepositoryScan.Scan(sources, depth, ignored, ct).Select(p => (p, RepositoryList.SafeNormalize(p))).ToList();
            }, ct);
        }
        catch (OperationCanceledException) { return -1; }
        if (ct.IsCancellationRequested) return -1;
        if (!reset) return RepositoryList.AddScanned(found);
        RepositoryList.Replace(found);
        return found.Count;
    }

    /// <summary>The Rescan dialog's work: scan, then reload without keeping the selection; reset mode also forgets the
    /// expanded rows.</summary>
    internal async Task RescanAsync(bool reset)
    {
        if (AppPreferences.SourceFolders.Count == 0) return; // nothing to scan; a reset would empty the whole list
        if (await ScanAsync(reset) < 0) return;
        if (reset) Settings.Set<List<string>?>(ExpandedKey, null);
        Reload();
    }

    async Task LoadDetailsAsync()
    {
        _detailsCts?.Cancel();
        var node = SelectedNodes is [{ IsRepository: true } n] ? n : null;
        if (Details?.Path != node?.Path) Details = null; // the panel never shows another repository than the selection
        if (node is null) return;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        _detailsCts = cts;
        try
        {
            await Task.Delay(DetailsDelay, cts.Token);
            // Off the UI thread: the README check and read and the folder listing touch the file system.
            var details = await Task.Run(() => RepoDetails.LoadAsync(node.Name, node.Path, DateTimeOffset.UtcNow, cts.Token), cts.Token);
            if (!cts.IsCancellationRequested) Details = details;
        }
        catch (OperationCanceledException) { } // the selection moved on, or the tab closed
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Win32Exception)
        {
            Trace.WriteLine($"Repository Manager: cannot read {node.Path}: {e.Message}");
            if (!cts.IsCancellationRequested) Details = null;
        }
        finally
        {
            if (_detailsCts == cts) _detailsCts = null;
            cts.Dispose();
        }
    }

    // ------------------------------------------------------------------ Commands

    /// <summary>Double-click / Enter / Open: a missing folder offers to remove the entry, else the tab opens.</summary>
    public Task OpenAsync(RepoNode node) =>
        node.IsRepository ? OpenListedAsync(_main, node.Name, node.Path, OpenPath) : Task.CompletedTask;

    /// <summary>Opens a listed repository (manager tree, Quick Launch): a missing folder offers to remove the entry
    /// (<paramref name="name"/> in the text), else <paramref name="open"/> opens the tab.</summary>
    internal static async Task OpenListedAsync(MainWindowViewModel main, string name, string path, Func<string, Task> open)
    {
        // Also a folder that is no repository any more: opening it would open an enclosing repository instead.
        if (!Directory.Exists(path) || RepositoryScan.Validate(path) is null)
        {
            var gone = new DialogViewModel
            {
                Title = "Repository Not Found",
                Description = $"It looks like '{name}' was deleted or moved. Do you want to remove the reference from GerGit?",
                SubmitText = "Remove",
                CancelText = "Close",
            };
            if (!await main.ShowDialog(gone)) return;
            if (main.Manager is { } manager) manager.RemoveMissing(path);
            else RepositoryList.Remove([path], ignore: false);
            return;
        }
        await open(path);
    }

    /// <summary>The "Remove" of a repository that is gone: the entry goes, the first repository is selected.</summary>
    public void RemoveMissing(string path)
    {
        RepositoryList.Remove([path], ignore: false);
        Reload(firstRepository: true);
    }

    /// <summary>Remove... needs rows of one kind (repositories or folders) that share one parent.</summary>
    public static bool CanRemove(IReadOnlyList<RepoNode> nodes) =>
        nodes.Count > 0 && nodes[0].Kind is RepoNodeKind.Repository or RepoNodeKind.Folder
                        && nodes.All(n => n.Kind == nodes[0].Kind && n.Parent == nodes[0].Parent);

    /// <summary>Remove... after the confirmation: repository entries go and are ignored by later scans; folders are
    /// ignored below every source folder with all repositories in them. Nothing on disk is touched.</summary>
    public async Task RemoveAsync(IReadOnlyList<RepoNode> nodes)
    {
        if (!CanRemove(nodes)) return;
        bool repos = nodes[0].IsRepository, one = nodes.Count == 1;
        var confirm = new DialogViewModel
        {
            Title = !repos ? "Do you want to remove the selected folders from GerGit?"
                : one ? "Do you want to remove the selected repository from GerGit?"
                : $"Do you want to remove {nodes.Count} selected repositories from GerGit?",
            Description = !repos ? "Included repositories will be removed from GerGit."
                : one ? "The repository will remain on disk" : "The repositories will remain on disk",
            SubmitText = "Remove",
            Width = 560,
        };
        if (!await _main.ShowDialog(confirm)) return;
        var paths = nodes.Select(n => n.Path).ToList();
        if (repos) RepositoryList.Remove(paths, ignore: true);
        else RepositoryList.RemoveFolders(paths);
        Reload(firstRepository: true);
    }

    /// <summary>F2: only with exactly one repository row.</summary>
    public void BeginRename(RepoNode node)
    {
        if (!node.IsRepository) return;
        node.EditText = node.Name;
        node.IsEditing = true;
    }

    /// <summary>Enter: stores the alias (the automatic name or a blank text clears it), refreshes tree and tab titles.</summary>
    public void CommitRename(RepoNode node)
    {
        if (!node.IsEditing) return;
        node.IsEditing = false;
        RepositoryList.SetAlias(node.Path, node.EditText);
        Reload(node.Path);
        _main.UpdateTitles();
    }

    public void CancelRename(RepoNode node) => node.IsEditing = false;

    /// <summary>Folders dropped from the file manager: on a source folder or folder row each opens as a tab (any kind,
    /// recorded as opened); elsewhere only ordinary repositories are added, nothing opens, and a drop on "Repositories"
    /// expands it.</summary>
    public async Task DropAsync(IReadOnlyList<string> folders, RepoNode? target)
    {
        if (target is { IsFolder: true })
        {
            foreach (var f in folders) await OpenPath(f);
            return;
        }
        var valid = folders.Where(f => RepositoryScan.Validate(f) == RepoKind.Repository).ToList();
        if (valid.Count == 0) return;
        RepositoryList.Add(valid);
        if (target?.Path == RepositoriesKey) target.IsExpanded = true;
        Reload(SelectedNode?.Path);
    }

    /// <summary>Opens the folder in the desktop's file manager.</summary>
    public static Task ShowInFileManagerAsync(string path) => HostingService.Current.OpenPath(path);

    /// <summary>Ctrl+Alt+O on the manager tab: the selected repository's folder.</summary>
    public Task ShowSelectedInFileManagerAsync() =>
        SelectedNode is { IsRepository: true } node ? ShowInFileManagerAsync(node.Path) : Task.CompletedTask;

    /// <summary>Rescan Repositories....</summary>
    public Task RescanDialogAsync() => _main.ShowDialog(new RescanViewModel(this));

    /// <summary>Repository Manager tree context menu for the rows it acts on; null = no menu (the "Recent"
    /// section row is part of it).</summary>
    public List<MenuEntry>? Menu(IReadOnlyList<RepoNode> nodes)
    {
        if (nodes.Count == 0 || nodes.Any(n => n.Path == RecentKey)) return null;
        // Without source folders there is nothing to rescan (and a reset would empty the list): no Rescan item.
        IEnumerable<MenuEntry> rescan = AppPreferences.SourceFolders.Count > 0
            ? [Menus.Item("Rescan Repositories...", RescanDialogAsync)] : [];
        var remove = Menus.Item("Remove...", () => RemoveAsync(nodes), "Delete");
        if (nodes is [{ IsRepository: true } repo])
            return Menus.Join(
                [
                    Menus.Item("Open", () => OpenAsync(repo), "Enter"),
                    Menus.Item("Open In File Explorer", () => ShowInFileManagerAsync(repo.Path), "Ctrl+Alt+O"),
                ],
                [
                    Menus.Item("Rename", () => { BeginRename(repo); return Task.CompletedTask; }, "F2"),
                    remove,
                ],
                rescan);
        if (nodes is [{ Kind: RepoNodeKind.Folder } folder])
        {
            var below = folder.Repositories.ToList();
            return Menus.Join(
                below.Count > 0 ? [Menus.Item($"Open All ({below.Count})", () => OpenAllAsync(below))] : [],
                [remove],
                rescan);
        }
        var menu = CanRemove(nodes) ? Menus.Join([remove], rescan) : rescan.ToList();
        return menu.Count > 0 ? menu : null;
    }

    async Task OpenAllAsync(List<RepoNode> repos)
    {
        foreach (var r in repos) await OpenAsync(r);
    }
}
