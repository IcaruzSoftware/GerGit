using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GerGit.Core;
using GerGit.Core.Git;
using GerGit.Hosting;

namespace GerGit.ViewModels;

public enum SidebarNodeKind { Section, Folder, Remote, LocalBranch, RemoteBranch, Tag, Stash, Submodule, ShowAll, Worktree }

/// <summary>One row of the sidebar tree.</summary>
public sealed partial class SidebarNode(SidebarViewModel owner, SidebarNodeKind kind, string key, string title) : ObservableObject
{
    public SidebarNodeKind Kind { get; } = kind;
    /// <summary>Unique path of titles ("Branches/feature/login"), used to keep expansion and selection over a rebuild.</summary>
    public string Key { get; } = key;
    public string Title { get; } = title;
    public string? Icon { get; init; }
    /// <summary>Remote rows: the host icon.</summary>
    public HostType HostType { get; init; }
    public bool IsRemote => Kind == SidebarNodeKind.Remote;
    public string? Sha { get; init; }
    public string? ToolTip { get; init; }
    public DateTimeOffset Date { get; init; }
    public bool IsBold { get; init; }
    public bool HasWarning { get; init; }
    public double IconOpacity { get; init; } = 1;
    public string? AheadBehind { get; init; }
    /// <summary>The reference of a local branch, remote branch or tag row.</summary>
    public GitRef? Ref { get; init; }
    public StashEntry? Stash { get; init; }
    public WorktreeEntry? Worktree { get; init; }
    public List<SidebarNode> Children { get; } = [];
    public bool HasChildren => Children.Count > 0;
    public bool IsSection => Kind == SidebarNodeKind.Section;
    public bool IsShowAll => Kind == SidebarNodeKind.ShowAll;

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    partial void OnIsExpandedChanged(bool value) => owner.OnExpandedChanged(this, value);
}

/// <summary>Sidebar of one repository tab in Branches mode.</summary>
public sealed partial class SidebarViewModel : ObservableObject
{
    // Natural, case-insensitive (v2 < v10).
    static readonly StringComparer Natural =
        StringComparer.Create(CultureInfo.InvariantCulture, CompareOptions.IgnoreCase | CompareOptions.NumericOrdering);
    const int TruncateAt = 20;

    // Limitation: expansion and "Show all" state live in memory only; per-repository persistence comes with settings.
    readonly HashSet<string> _expanded = ["Branches", "Remotes", "Stashes"];
    readonly HashSet<string> _showAll = [];
    readonly DispatcherTimer _filterTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    string _filter = "";
    List<string> _selectedKeys = [];
    bool _building;
    bool _worktreesShown; // The Worktrees section is expanded the first time it appears

    public SidebarViewModel(RepoTabViewModel tab)
    {
        Tab = tab;
        SelectedNodes.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(SelectedNode));
            if (!_building) Activate();
        };
        _filterTimer.Tick += (_, _) =>
        {
            _filterTimer.Stop();
            _filter = FilterText.Trim();
            Rebuild();
        };
    }

    public RepoTabViewModel Tab { get; }

    [ObservableProperty]
    public partial IReadOnlyList<SidebarNode> Roots { get; private set; } = [];

    /// <summary>The selected rows in selection order. The tree binds its SelectedItems to it.</summary>
    public ObservableCollection<SidebarNode> SelectedNodes { get; } = [];

    /// <summary>The first selected row; setting it selects only that row.</summary>
    public SidebarNode? SelectedNode
    {
        get => SelectedNodes.Count > 0 ? SelectedNodes[0] : null;
        set => Select(value is null ? [] : [value]);
    }

    /// <summary>Raised to focus a row (Select Stale Local Branches).</summary>
    public event Action<SidebarNode>? FocusRequested;

    /// <summary>Puts text on the clipboard; set by the view, replaceable by tests.</summary>
    internal Func<string, Task> Copy { get; set; } = _ => Task.CompletedTask;

    /// <summary>Replaces the selection; activates it once (unless the tree is rebuilding).</summary>
    public void Select(IReadOnlyList<SidebarNode> nodes)
    {
        if (SelectedNodes.SequenceEqual(nodes)) return;
        bool building = _building;
        _building = true; // one activation for the whole change, not one per Clear/Add
        try
        {
            SelectedNodes.Clear();
            foreach (var n in nodes) SelectedNodes.Add(n);
        }
        finally { _building = building; }
        if (!_building) Activate();
        else _selectedKeys = nodes.Select(n => n.Key).ToList();
    }

    [ObservableProperty]
    public partial string FilterText { get; set; } = "";

    partial void OnFilterTextChanged(string value)
    {
        _filterTimer.Stop();
        _filterTimer.Start();
    }

    [RelayCommand]
    void ClearFilter() => FilterText = "";

    internal void OnExpandedChanged(SidebarNode node, bool expanded)
    {
        // No saving while the sidebar rebuilds itself or while a filter is active.
        if (_building || _filter.Length > 0) return;
        if (expanded) _expanded.Add(node.Key);
        else _expanded.Remove(node.Key);
    }

    /// <summary>Clicking the already selected row runs the selection action again.</summary>
    public void Reselect() => Activate();

    /// <summary>A click on a "Show all …" / "Show less" row toggles truncation of its section; the row is never selected.</summary>
    public void ToggleShowAll(SidebarNode node)
    {
        var section = node.Key[..node.Key.IndexOf('/')];
        if (!_showAll.Remove(section)) _showAll.Add(section);
        Rebuild();
    }

    /// <summary>By the first selected row, a stash selects its commit, references select the
    /// commits of all selected references.</summary>
    void Activate()
    {
        _selectedKeys = SelectedNodes.Select(n => n.Key).ToList();
        if (SelectedNodes.Count > 0) Tab.IsLocalChangesView = false; // Any selected row leaves the Local Changes view
        if (SelectedNode is not { Sha: { } sha } first) return;
        Tab.CommitList.SelectCommits(first.Ref is null ? [sha]
            : SelectedNodes.Where(n => n.Ref is not null).Select(n => n.Sha!).Distinct().ToList());
    }

    /// <summary>
    /// Rebuilds the tree from the tab's data, keeping expansion and selection by key. A selected row that is gone
    /// hands the selection to the nearest remaining row above it.
    /// </summary>
    public void Rebuild()
    {
        _building = true;
        try
        {
            var oldRows = VisibleRows(Roots).Select(n => n.Key).ToList();
            var keys = _selectedKeys;
            Roots = Build();
            var selected = keys.Select(k => Find(Roots, k)).OfType<SidebarNode>().ToList();
            for (int i = keys.Count == 0 ? -1 : oldRows.IndexOf(keys[0]) - 1; selected.Count == 0 && i >= 0; i--)
                if (Find(Roots, oldRows[i]) is { IsShowAll: false } above) selected.Add(above);
            SelectedNodes.Clear(); // the tree cleared it already when the rows were replaced
            foreach (var n in selected) SelectedNodes.Add(n);
            _selectedKeys = selected.Select(n => n.Key).ToList();
        }
        finally { _building = false; }
    }

    internal static IEnumerable<SidebarNode> VisibleRows(IEnumerable<SidebarNode> nodes) =>
        nodes.SelectMany(n => n.IsExpanded ? VisibleRows(n.Children).Prepend(n) : [n]);

    static SidebarNode? Find(IEnumerable<SidebarNode> nodes, string key)
    {
        foreach (var n in nodes)
        {
            if (n.Key == key) return n;
            if (key.StartsWith(n.Key + "/", StringComparison.Ordinal) && Find(n.Children, key) is { } hit) return hit;
        }
        return null;
    }

    bool Match(string text) => text.Contains(_filter, StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ menus, keys and double-click

    static IEnumerable<SidebarNode> AllNodes(IEnumerable<SidebarNode> nodes) => nodes.SelectMany(n => AllNodes(n.Children).Prepend(n));

    List<SidebarNode> InDisplayOrder(IEnumerable<SidebarNode> nodes)
    {
        var set = nodes.ToHashSet();
        return AllNodes(Roots).Where(set.Contains).ToList();
    }

    /// <summary>
    /// The menu acts on the selection when the right-clicked row is part of it, else on that
    /// row only; its kind decides the menu (the kind of the clicked row).
    /// References of mixed kinds open no menu; folders, Stashes / Submodules headers and "Show all" rows have none.
    /// </summary>
    public async Task<IReadOnlyList<MenuEntry>?> BuildMenuAsync(SidebarNode clicked)
    {
        var targets = InDisplayOrder(SelectedNodes.Contains(clicked) ? SelectedNodes : [clicked]);
        switch (clicked.Kind)
        {
            case SidebarNodeKind.LocalBranch or SidebarNodeKind.RemoteBranch or SidebarNodeKind.Tag:
                var refNodes = targets.Where(n => n.Ref is not null).ToList();
                if (refNodes.Any(n => n.Kind != clicked.Kind)) return null;
                var refs = refNodes.Select(n => n.Ref!).ToList();
                return (clicked.Kind, refs.Count) switch
                {
                    (SidebarNodeKind.LocalBranch, 1) => Menus.LocalBranch(Tab, refs[0], Copy),
                    (SidebarNodeKind.LocalBranch, _) => Menus.LocalBranches(Tab, refs),
                    (SidebarNodeKind.RemoteBranch, 1) => Menus.RemoteBranch(Tab, refs[0], Copy),
                    (SidebarNodeKind.RemoteBranch, _) => Menus.RemoteBranches(Tab, refs),
                    (_, 1) => Menus.Tag(Tab, refs[0], Copy),
                    _ => Menus.Tags(Tab, refs),
                };
            case SidebarNodeKind.Stash:
                // Not built yet: "Save as Patch..." comes with patches.
                var stashes = targets.Select(n => n.Stash).OfType<StashEntry>().ToList();
                return stashes.Count == 1 ? Menus.Stash(Tab, stashes[0]) : Menus.Stashes(Tab, stashes);
            case SidebarNodeKind.Remote:
                return await Menus.Remote(Tab, clicked.Title, Copy);
            case SidebarNodeKind.Worktree:
                var worktrees = targets.Select(n => n.Worktree).OfType<WorktreeEntry>().ToList();
                return worktrees.Count == 1 ? Menus.Worktree(Tab, worktrees[0], Copy) : Menus.Worktrees(Tab, worktrees, Copy);
            case SidebarNodeKind.Section:
                // Not built yet: the "Sort ...:" blocks come with sidebar sorting.
                return clicked.Title switch
                {
                    "Submodules" => [Menus.Item("Initialize Submodules...", InitializeSubmodulesAsync)],
                    "Worktrees" => [Menus.Item("New Worktree...", Tab.WorktreeOps.CreateAsync)],
                    "Branches" =>
                    [
                        Menus.Item("New Branch...", () => Tab.RefOps.CreateBranchAsync(), "Ctrl+Shift+B"),
                        Menus.Item("Select Stale Local Branches", () => { SelectStaleBranches(); return Task.CompletedTask; }),
                    ],
                    "Tags" => [Menus.Item("New Tag...", () => Tab.RefOps.CreateTagAsync(), "Ctrl+Shift+G")],
                    "Remotes" => [Menus.Item("Add New Remote...", () => Tab.RemoteOps.OpenAddRemoteAsync())],
                    _ => null,
                };
            default:
                return null;
        }
    }

    /// <summary>Submodules header "Initialize Submodules...": the submodules that are not checked out, with the URLs they
    /// will be cloned from, need a confirmation first (the URLs come from the repository, not from the user); then
    /// `submodule update --init --recursive` for exactly those paths. Updates after checkout or pull never do this.</summary>
    public async Task InitializeSubmodulesAsync()
    {
        var subs = await Tab.Repo.ReadUninitializedSubmodulesAsync();
        if (subs.Count == 0)
        {
            await Tab.ShowError("Initialize Submodules", "There are no submodules to initialize.");
            return;
        }
        var list = string.Join("\n", subs.Select(s =>
            $"{Printable(s.Path)}: {DisplayUrl(s.Url)}{(s.RelativeTo is { } to ? $" (relative to {DisplayUrl(to)})" : "")}"));
        var text = "These submodules, and the submodules inside them, will be cloned from the URLs listed in the repository. "
            + "Only continue if you trust them.\n\n" + list;
        if (!await Tab.Confirm("Initialize Submodules", text, "Initialize", 600)) return;
        var paths = subs.Select(s => s.Path).ToList();
        await Tab.RunJobAsync(paths.Count == 1 ? $"Initialize submodule '{paths[0]}'" : $"Initialize {paths.Count} submodules", async job =>
        {
            job.SetMessage("Initializing submodules...");
            return await Tab.Repo.InitializeSubmodulesAsync(job, paths);
        });
    }

    /// <summary>A URL from the repository as the confirmation shows it: a non-ASCII host in its ASCII (punycode) form and
    /// every invisible or text-direction character escaped (<see cref="Printable"/>), so no look-alike host or
    /// right-to-left trick can hide where the clone goes.</summary>
    internal static string DisplayUrl(string url)
    {
        // scheme://[user@]host or the scp form [user@]host:path
        var m = UrlHostRegex().Match(url);
        if (m.Success && m.Groups["host"].Value is var host && host.Any(c => c > 127))
        {
            try { url = url[..m.Groups["host"].Index] + new IdnMapping().GetAscii(host) + url[(m.Groups["host"].Index + host.Length)..]; }
            catch (ArgumentException) { } // not a valid host name: shown escaped as it is
        }
        return Printable(url);
    }

    /// <summary>Control, format (bidi overrides, zero-width), line and paragraph separator characters as `\uXXXX`.</summary>
    internal static string Printable(string text) => string.Concat(text.Select(c =>
        char.IsControl(c) || char.GetUnicodeCategory(c) is UnicodeCategory.Format or UnicodeCategory.LineSeparator
            or UnicodeCategory.ParagraphSeparator
            ? $"\\u{(int)c:X4}" : c.ToString()));

    [GeneratedRegex(@"^(?:[A-Za-z][A-Za-z0-9+.-]*://(?:[^/@]*@)?|(?:[^/@:]*@)?(?=[^/:]+:(?!//)))(?<host>[^/:?#@]+)")]
    private static partial Regex UrlHostRegex();

    /// <summary>Select every local branch whose upstream is gone, expand their
    /// folders and focus the first; nothing found = empty selection.</summary>
    public void SelectStaleBranches()
    {
        var stale = Tab.RefOps.StaleBranches().Select(r => r.FullName).ToHashSet();
        var nodes = AllNodes(Roots).Where(n => n.Kind == SidebarNodeKind.LocalBranch && stale.Contains(n.Ref!.FullName)).ToList();
        foreach (var a in AllNodes(Roots).Where(a => nodes.Any(n => n.Key.StartsWith(a.Key + "/", StringComparison.Ordinal))).ToList())
            a.IsExpanded = true;
        Select(nodes);
        if (nodes.Count > 0) FocusRequested?.Invoke(nodes[0]);
    }

    /// <summary>Local branch → checkout, remote branch → remote checkout, tag → Checkout Commit,
    /// stash → Apply Stash, linked worktree → its tab. Other rows: nothing (rows with an expander toggle in the tree).</summary>
    public Task DoubleClickAsync(SidebarNode node) => node switch
    {
        { Worktree: { IsMain: false } w } => Tab.WorktreeOps.OpenAsync([w.Path]),
        { Kind: SidebarNodeKind.LocalBranch, Ref: { } r } => Tab.RefOps.CheckoutBranchAsync(r),
        { Kind: SidebarNodeKind.RemoteBranch, Ref: { } r } => Tab.RefOps.CheckoutRemoteBranchAsync(r),
        { Kind: SidebarNodeKind.Tag, Ref: { } r } => Tab.RefOps.CheckoutCommitAsync(r.Sha, r),
        { Stash: { } s } => Tab.StashOps.ApplyAsync(s),
        _ => Task.CompletedTask,
    };

    /// <summary>F2: rename when exactly one local branch is selected.</summary>
    public Task RenameAsync() =>
        SelectedNodes is [{ Kind: SidebarNodeKind.LocalBranch, Ref: { } r }] ? Tab.RefOps.RenameBranchAsync(r) : Task.CompletedTask;

    /// <summary>
    /// Delete key: references of one kind → their delete dialog; a mix of reference kinds → nothing; other
    /// rows are ignored (stashes too when references are selected); only stashes → delete stashes.
    /// </summary>
    public Task DeleteAsync()
    {
        var selected = InDisplayOrder(SelectedNodes);
        var refs = selected.Where(n => n.Ref is not null).ToList();
        if (refs.Count == 0)
            return Tab.StashOps.DeleteAsync(selected.Select(n => n.Stash).OfType<StashEntry>().ToList());
        if (refs.Select(n => n.Kind).Distinct().Count() > 1) return Task.CompletedTask;
        var list = refs.Select(n => n.Ref!).ToList();
        return refs[0].Kind switch
        {
            SidebarNodeKind.LocalBranch => Tab.RefOps.DeleteBranchesAsync(list),
            SidebarNodeKind.RemoteBranch => Tab.RefOps.DeleteRemoteBranchesAsync(list),
            _ => Tab.RefOps.DeleteTagsAsync(list),
        };
    }

    /// <summary>Ctrl+C: the name of the first selected reference. Not built yet: submodule paths come with submodule menus.</summary>
    public Task CopyNameAsync() =>
        InDisplayOrder(SelectedNodes).FirstOrDefault(n => n.Ref is not null) is { Ref: { } r } ? Copy(r.ShortName) : Task.CompletedTask;

    List<SidebarNode> Build()
    {
        bool filtering = _filter.Length > 0;
        var refs = Tab.Refs.Refs;
        var roots = new List<SidebarNode>();

        void AddSection(string title, Action<SidebarNode> fill)
        {
            var section = Node(SidebarNodeKind.Section, title, title);
            fill(section);
            if (!filtering || section.HasChildren || Match(title)) roots.Add(section);
        }

        // At the top, only with linked worktrees. In the main worktree's tab the linked ones
        // are direct children; in a linked worktree's tab they sit below a node for the main worktree.
        var worktrees = Tab.Worktrees;
        if (worktrees.Count > 0)
        {
            if (!_worktreesShown) _expanded.Add("Worktrees");
            AddSection("Worktrees", s =>
            {
                var linked = worktrees.Where(w => !w.IsMain).ToList();
                var parent = s;
                if (worktrees is [{ IsMain: true, IsCurrent: false } main, ..])
                {
                    parent = WorktreeNode(s.Key, main, worktrees);
                    if (!_worktreesShown) _expanded.Add(parent.Key);
                }
                foreach (var w in linked.Where(w => !filtering || Match(WorktreeTitle(w, worktrees))))
                    parent.Children.Add(WorktreeNode(parent.Key, w, worktrees));
                if (parent != s && (!filtering || parent.HasChildren || Match(parent.Title))) s.Children.Add(parent);
            });
            _worktreesShown = true;
        }

        AddSection("Branches", s =>
        {
            var items = refs.Where(r => r.Kind == RefKind.LocalBranch && (!filtering || Match(r.ShortName)));
            AddPaths(s, items, r => r.ShortName, LocalBranch, byDate: false);
        });

        AddSection("Remotes", s =>
        {
            var remoteBranches = refs.Where(r => r.Kind == RefKind.RemoteBranch).ToList();
            var urls = Tab.Remotes.ToDictionary(r => r.Name, r => r.Url);
            var names = urls.Keys.Concat(remoteBranches.Select(r => r.RemoteName!)).Distinct().Order(Natural);
            foreach (var name in names)
            {
                var remote = new SidebarNode(this, SidebarNodeKind.Remote, s.Key + "/" + name, name)
                {
                    Icon = "server", HostType = Tab.Hosted(name)?.Type ?? HostType.Custom, ToolTip = urls.GetValueOrDefault(name),
                };
                var items = remoteBranches.Where(r => r.RemoteName == name && (!filtering || Match(r.ShortName)));
                AddPaths(remote, items, r => r.NameInRemote, RemoteBranch, byDate: false);
                if (!filtering || remote.HasChildren || Match(name)) s.Children.Add(remote);
            }
        });

        AddSection("Tags", s =>
        {
            // Sort order "Recently used" is the default for tags.
            var items = refs.Where(r => r.Kind == RefKind.Tag && (!filtering || Match(r.ShortName)))
                .OrderByDescending(r => r.CommitterDate).ThenBy(r => r.ShortName, Natural).ToList();
            AddPaths(s, Truncate(s, items), r => r.ShortName, Tag, byDate: true);
            AddShowAllRow(s, items.Count, "tags");
        });

        AddSection("Stashes", s =>
        {
            var items = Tab.Stashes.Where(st => !filtering || Match(st.ShortSubject)).ToList();
            foreach (var st in Truncate(s, items))
                s.Children.Add(new SidebarNode(this, SidebarNodeKind.Stash, s.Key + "/" + st.Name, st.ShortSubject)
                    { Icon = "archive", ToolTip = st.ShortSubject, Sha = st.Sha, Stash = st });
            AddShowAllRow(s, items.Count, "stashes");
        });

        AddSection("Submodules", s =>
        {
            var dirty = RepoTabViewModel.DirtySubmodules(Tab.Status);
            var items = Tab.Submodules.Where(m => !filtering || Match(m.Path[(m.Path.LastIndexOf('/') + 1)..]));
            AddPaths(s, items, m => m.Path,
                (m, key, title) => Node(SidebarNodeKind.Submodule, key, dirty.Contains(m.Path) ? title + "*" : title, "package"),
                byDate: false);
        });

        // While filtering every node is expanded; afterwards the remembered state applies again.
        void Expand(IEnumerable<SidebarNode> nodes)
        {
            foreach (var n in nodes.Where(n => n.HasChildren))
            {
                n.IsExpanded = filtering || _expanded.Contains(n.Key);
                Expand(n.Children);
            }
        }
        Expand(roots);
        return roots;
    }

    IEnumerable<T> Truncate<T>(SidebarNode section, List<T> items) =>
        _filter.Length == 0 && items.Count > TruncateAt && !_showAll.Contains(section.Key) ? items.Take(TruncateAt) : items;

    void AddShowAllRow(SidebarNode section, int count, string what)
    {
        if (_filter.Length > 0 || count <= TruncateAt) return;
        var text = _showAll.Contains(section.Key) ? "Show less" : $"Show all {what} ({count})";
        section.Children.Add(Node(SidebarNodeKind.ShowAll, section.Key + "/\u0001show-all", text));
    }

    /// <summary>Splits each path at "/" into folder rows and sorts every level.</summary>
    void AddPaths<T>(SidebarNode parent, IEnumerable<T> items, Func<T, string> path, Func<T, string, string, SidebarNode> leaf, bool byDate)
    {
        foreach (var item in items)
        {
            var parts = path(item).Split('/');
            var node = parent;
            foreach (var part in parts[..^1])
            {
                var key = node.Key + "/" + part;
                var folder = node.Children.FirstOrDefault(c => c.Key == key && c.Kind == SidebarNodeKind.Folder);
                if (folder is null) node.Children.Add(folder = Node(SidebarNodeKind.Folder, key, part, "folder"));
                node = folder;
            }
            node.Children.Add(leaf(item, node.Key + "/" + parts[^1], parts[^1]));
        }
        Sort(parent, byDate);
    }

    static void Sort(SidebarNode node, bool byDate)
    {
        // Folders first (alphabetically), then leaves alphabetically or newest first. The "Show all" row is added afterwards.
        var folders = node.Children.Where(c => c.HasChildren).OrderBy(c => c.Title, Natural);
        var leaves = node.Children.Where(c => !c.HasChildren);
        leaves = byDate ? leaves.OrderByDescending(c => c.Date).ThenBy(c => c.Title, Natural) : leaves.OrderBy(c => c.Title, Natural);
        var sorted = folders.Concat(leaves).ToList();
        node.Children.Clear();
        node.Children.AddRange(sorted);
        foreach (var f in node.Children.Where(c => c.HasChildren)) Sort(f, byDate);
    }

    /// <summary>Last folder name, "name (first differing parent folder)" when another worktree has the same name.</summary>
    static string WorktreeTitle(WorktreeEntry w, IReadOnlyList<WorktreeEntry> all) =>
        all.FirstOrDefault(o => o != w && o.Name == w.Name) is { } twin
            ? $"{w.Name} ({MainWindowViewModel.FirstDifference(Path.GetDirectoryName(w.Path)!, Path.GetDirectoryName(twin.Path)!)})"
            : w.Name;

    SidebarNode WorktreeNode(string parentKey, WorktreeEntry w, IReadOnlyList<WorktreeEntry> all)
    {
        var title = WorktreeTitle(w, all);
        var last = w.BranchName is { } b ? $"Branch:\t\t{b}" : $"HEAD:\t\t{(w.Sha is { Length: > 7 } sha ? sha[..7] : w.Sha)}";
        return new SidebarNode(this, SidebarNodeKind.Worktree, parentKey + "/" + title, title)
        {
            Icon = "folder-git-2", IsBold = w.IsCurrent, Sha = w.Sha, Worktree = w,
            ToolTip = $"Worktree:\t{title}\nLocation:\t\t{w.Path}\n{last}",
        };
    }

    SidebarNode Node(SidebarNodeKind kind, string key, string title, string? icon = null, string? toolTip = null) =>
        new(this, kind, key, title) { Icon = icon, ToolTip = toolTip };

    SidebarNode LocalBranch(GitRef r, string key, string title)
    {
        // Local branch icon states and "Ahead / behind text".
        bool active = r.FullName == Tab.Refs.ActiveBranch;
        var tip = $"Local branch:\t{r.ShortName}";
        if (r.Upstream is not null)
            tip += $"\nTracked branch:\t{ShortRefName(r.Upstream)}" + (r.UpstreamState == UpstreamState.Gone ? " [removed]" : "");
        string? aheadBehind = null;
        if (r.UpstreamState == UpstreamState.Valid)
            aheadBehind = string.Join(' ', new[] { r.Ahead > 0 ? $"{r.Ahead}↑" : null, r.Behind > 0 ? $"{r.Behind}↓" : null }.OfType<string>());
        return new SidebarNode(this, SidebarNodeKind.LocalBranch, key, title)
        {
            Icon = active ? "check" : "git-branch",
            IsBold = active,
            IconOpacity = r.Upstream is null ? 0.5 : 1,
            HasWarning = r.UpstreamState == UpstreamState.Gone,
            AheadBehind = aheadBehind is { Length: > 0 } ? aheadBehind : null,
            ToolTip = tip,
            Sha = r.Sha,
            Ref = r,
        };
    }

    static string ShortRefName(string fullName) =>
        fullName.StartsWith("refs/remotes/", StringComparison.Ordinal) ? fullName["refs/remotes/".Length..]
        : fullName.StartsWith("refs/heads/", StringComparison.Ordinal) ? fullName["refs/heads/".Length..]
        : fullName;

    SidebarNode RemoteBranch(GitRef r, string key, string title) =>
        new(this, SidebarNodeKind.RemoteBranch, key, title)
        {
            Icon = "git-branch", Sha = r.Sha, ToolTip = $"Remote branch '{r.NameInRemote}'", Ref = r,
        };

    SidebarNode Tag(GitRef r, string key, string title) =>
        new(this, SidebarNodeKind.Tag, key, title)
        {
            Icon = "tag", Sha = r.Sha, Date = r.CommitterDate, Ref = r,
            ToolTip = $"Tag '{r.ShortName}'\n{r.CommitterDate.ToLocalTime():g}",
        };
}
