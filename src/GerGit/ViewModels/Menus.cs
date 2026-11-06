using GerGit.Core;
using GerGit.Core.Git;
using GerGit.Core.Rebase;

namespace GerGit.ViewModels;

public enum MenuEntryKind { Item, Separator, Header }

/// <summary>
/// One entry of a menu that is built when it opens (toolbar arrow menus, sidebar and commit-list context menus).
/// The view turns entries into MenuItems (Views/MenuControls.cs); tests read the labels, order and enabled states.
/// </summary>
public sealed class MenuEntry
{
    public MenuEntryKind Kind { get; init; }
    public string Header { get; init; } = "";
    /// <summary>Shortcut text shown right-aligned ("Ctrl+Shift+B", "Delete", "F2").</summary>
    public string? Gesture { get; init; }
    public bool IsEnabled { get; init; } = true;
    /// <summary>Non-null for a checkable item.</summary>
    public bool? IsChecked { get; init; }
    public IReadOnlyList<MenuEntry>? Children { get; init; }
    public Func<Task>? Action { get; init; }

    /// <summary>The action started by the last click (tests await it).</summary>
    public Task Running { get; private set; } = Task.CompletedTask;

    public Task Invoke() => Running = Action?.Invoke() ?? Task.CompletedTask;

    public override string ToString() => Kind == MenuEntryKind.Separator ? "-" : Header;
}

/// <summary>
/// The context menus of the sidebar and the commit list over the entry points. Built from the current
/// data each time a menu opens. Not built yet: Git Flow, AI, custom commands,
/// Save as Patch, Compare to Local Changes, pin/filter/hide toggles and the sidebar drag-and-drop menus.
/// </summary>
public static class Menus
{
    public static readonly MenuEntry Separator = new() { Kind = MenuEntryKind.Separator };

    public static MenuEntry Item(string header, Func<Task>? action, string? gesture = null, bool enabled = true) =>
        new() { Header = header, Action = action, Gesture = gesture, IsEnabled = enabled };

    public static MenuEntry Sub(string header, IReadOnlyList<MenuEntry> children, bool enabled = true) =>
        new() { Header = header, Children = children, IsEnabled = enabled };

    public static MenuEntry Heading(string header) => new() { Kind = MenuEntryKind.Header, Header = header, IsEnabled = false };

    /// <summary>The groups with one separator between non-empty neighbours.</summary>
    public static List<MenuEntry> Join(params IEnumerable<MenuEntry>[] groups)
    {
        var result = new List<MenuEntry>();
        foreach (var g in groups.Select(g => g.ToList()).Where(g => g.Count > 0))
        {
            if (result.Count > 0) result.Add(Separator);
            result.AddRange(g);
        }
        return result;
    }

    static string? Active(RepoTabViewModel tab) => tab.ActiveBranchName;
    static string? ActiveTip(RepoTabViewModel tab) => tab.RemoteOps.ActiveBranch?.Sha;

    static MenuEntry Merge(RepoTabViewModel tab, GitRef source, bool enabled = true) =>
        Item($"Merge into '{Active(tab)}'...", () => tab.RefOps.MergeAsync(source), enabled: enabled);

    /// <summary>Sidebar: "Rebase ... on ..." (disabled like Merge) and the interactive item (never disabled;
    /// the missing closing quote in the label is kept as is).</summary>
    static IEnumerable<MenuEntry> RebaseOn(RepoTabViewModel tab, GitRef target, bool enabled = true)
    {
        yield return Item($"Rebase '{Active(tab)}' on '{target.ShortName}'...", () => new RebaseOps(tab).RebaseOnAsync(target), enabled: enabled);
        yield return Item($"Interactively Rebase '{Active(tab)}' on '{target.ShortName}...", () => new RebaseOps(tab).InteractiveOnAsync(target));
    }

    /// <summary>Push part of a local branch: one remote = an item, several = a submenu, none = the disabled
    /// "Push to 'origin'..." (sidebar) or nothing (commit list). Then the pull request part (create pull request flow): one remote = its item, several = the submenu "Create Pull Request" of the remotes that offer one.</summary>
    static IEnumerable<MenuEntry> PushBranch(RemoteOps ops, GitRef b, string submenu, bool disabledWhenNone)
    {
        var branch = b.ShortName;
        var remotes = ops.RemoteNames;
        if (remotes.Count == 0)
        {
            if (disabledWhenNone) yield return Item("Push to 'origin'...", null, enabled: false);
        }
        else if (remotes.Count == 1) yield return Item($"Push to '{remotes[0]}'...", () => ops.OpenPushAsync(branch, remotes[0]));
        else yield return Sub(submenu, remotes.Select(r => Item($"Push to '{r}'...", () => ops.OpenPushAsync(branch, r))).ToList());

        var requests = PullRequestRemotes(ops).Select(h => CreatePullRequest(ops, b, h)).ToList();
        if (remotes.Count == 1) foreach (var r in requests) yield return r;
        // The submenu keeps the title "Create Pull Request" also when every remote is a GitLab one.
        else if (requests.Count > 0) yield return Sub("Create Pull Request", requests);
    }

    /// <summary>Remotes (drop-down order) for which a pull / merge request can be made: a repository page can be built
    /// (matched account, or github.com / gitlab.com by host name).</summary>
    static IEnumerable<HostedRemote> PullRequestRemotes(RemoteOps ops) =>
        ops.RemoteNames.Select(ops.Tab.Hosted).OfType<HostedRemote>().Where(h => h.RepositoryPage is not null);

    /// <summary>"Push and Create ..." when the branch has no (existing) upstream or is strictly ahead, else
    /// "Create ..."; GitLab remotes say "Merge Request".</summary>
    static MenuEntry CreatePullRequest(RemoteOps ops, GitRef b, HostedRemote h) =>
        Item($"{(RemoteOps.PushesBeforePullRequest(b) ? "Push and Create" : "Create")} {h.RequestName} on '{h.Name}'",
            () => ops.StartPullRequestAsync(b, h.Name));

    /// <summary>Remote branch menus: "Create Pull Request on '&lt;remote&gt;'" when its remote offers one.</summary>
    static IEnumerable<MenuEntry> CreatePullRequest(RepoTabViewModel tab, GitRef r)
    {
        var ops = tab.RemoteOps;
        var remote = ops.RemoteOf(r.FullName);
        if (tab.Hosted(remote) is { RepositoryPage: not null } h)
            yield return Item($"Create {h.RequestName} on '{remote}'", () => ops.CreatePullRequestAsync(remote, ops.NameInRemote(r.FullName), r.Sha));
    }

    static IEnumerable<MenuEntry> PushTag(RemoteOps ops, string tag)
    {
        var remotes = ops.RemoteNames;
        if (remotes.Count == 1) yield return Item($"Push to '{remotes[0]}'...", () => ops.OpenPushTagAsync(tag, remotes[0]));
        else if (remotes.Count > 1)
            yield return Sub("Push Tag...", remotes.Select(r => Item($"Push to '{r}'...", () => ops.OpenPushTagAsync(tag, r))).ToList());
    }

    /// <summary>"Push N ... to '&lt;remote&gt;'..." or the submenu "Push N ... to" &gt; "&lt;remote&gt;..." (multi-selections).</summary>
    static IEnumerable<MenuEntry> PushMany(RemoteOps ops, string what, Func<string, Task> push)
    {
        var remotes = ops.RemoteNames;
        if (remotes.Count == 1) yield return Item($"Push {what} to '{remotes[0]}'...", () => push(remotes[0]));
        else if (remotes.Count > 1) yield return Sub($"Push {what} to", remotes.Select(r => Item($"{r}...", () => push(r))).ToList());
    }

    // ------------------------------------------------------------------ toolbar arrow menus

    /// <summary>Header, up to 15 stashes (click = Apply Stash), "Save Snapshot...".</summary>
    public static List<MenuEntry> StashArrow(RepoTabViewModel tab) =>
        Join(
            [Heading("Recent Stashes")],
            tab.StashOps.RecentStashes.Select(s => Item(s.ShortSubject, () => tab.StashOps.ApplyAsync(s))),
            [Item("Save Snapshot...", tab.StashOps.SaveSnapshotAsync)]);

    /// <summary>Branch arrow menu. Not built yet: Git Flow.</summary>
    public static List<MenuEntry> BranchArrow(RepoTabViewModel tab) =>
        [Item("New Branch...", () => tab.RefOps.CreateBranchAsync(), "Ctrl+Shift+B"), Item("New Worktree...", tab.WorktreeOps.CreateAsync)];

    // ------------------------------------------------------------------ worktrees

    static MenuEntry OpenWorktree(RepoTabViewModel tab, WorktreeEntry wt) =>
        Item($"Open '{wt.Name}' Worktree", () => tab.WorktreeOps.OpenAsync([wt.Path]));

    /// <summary>Checkout part of a local branch: checked out in a linked worktree = "Open '&lt;name&gt;'
    /// Worktree", in the main one = nothing; else <paramref name="checkout"/> and, when worktrees are enabled and the
    /// branch is not the active one, "Checkout as Worktree...".</summary>
    static IEnumerable<MenuEntry> CheckoutPart(RepoTabViewModel tab, GitRef local, IEnumerable<MenuEntry> checkout, Func<Task> asWorktree)
    {
        if (tab.WorktreeOf(local.FullName) is { } wt)
            return wt.IsMain ? [] : [OpenWorktree(tab, wt)];
        return local.FullName != tab.Refs.ActiveBranch && tab.WorktreesEnabled
            ? [.. checkout, Item("Checkout as Worktree...", asWorktree)] : checkout;
    }

    /// <summary>Worktree or main worktree (one selected).</summary>
    public static List<MenuEntry> Worktree(RepoTabViewModel tab, WorktreeEntry wt, Func<string, Task> copy) =>
        Join(
            [
                Item($"Open '{wt.Name}'...", () => tab.WorktreeOps.OpenAsync([wt.Path])),
                Item("Show in File Explorer", () => tab.WorktreeOps.ShowInFileManagerAsync(wt)),
            ],
            [Item($"Delete '{wt.Name}'...", () => tab.WorktreeOps.DeleteAsync(wt), enabled: !wt.IsCurrent)],
            [Item("Copy Path", () => copy(wt.Path))]);

    /// <summary>Worktrees (two or more selected).</summary>
    public static List<MenuEntry> Worktrees(RepoTabViewModel tab, IReadOnlyList<WorktreeEntry> wts, Func<string, Task> copy) =>
        Join(
            [Item($"Open {wts.Count} worktrees...", () => tab.WorktreeOps.OpenAsync(wts.Select(w => w.Path).ToList()))],
            [Item("Copy Path", () => copy(string.Join('\n', wts.Select(w => w.Path))))]);

    // ------------------------------------------------------------------ sidebar

    /// <summary>Local branch (one selected). Not built yet: Git Flow, AI review, custom commands.</summary>
    public static List<MenuEntry> LocalBranch(RepoTabViewModel tab, GitRef b, Func<string, Task> copy)
    {
        var ops = tab.RemoteOps;
        bool active = b.FullName == tab.Refs.ActiveBranch;
        var upstream = new List<MenuEntry>();
        if (RemoteOps.HasExistingUpstream(b))
        {
            var up = RemoteOps.ShortRef(b.Upstream!);
            upstream.Add(Item($"Fast-Forward to '{up}'", () => ops.FastForwardAsync(b.ShortName), enabled: !InOtherWorktree(tab, b)));
            if (active) upstream.Add(Item($"Pull '{up}'...", () => ops.OpenPullAsync(b.Upstream)));
        }
        var merge = Active(tab) is not null && !active ? [Merge(tab, b), .. RebaseOn(tab, b)] : new List<MenuEntry>();
        return Join(
            CheckoutPart(tab, b, [Item("Checkout...", () => tab.RefOps.CheckoutBranchAsync(b))], () => tab.WorktreeOps.CheckoutAsWorktreeAsync(b)),
            upstream.Concat(PushBranch(ops, b, "Push", disabledWhenNone: true)),
            merge,
            [
                Item("New Branch...", () => tab.RefOps.CreateBranchAsync(start: b), "Ctrl+Shift+B"),
                Item("New Tag...", () => tab.RefOps.CreateTagAsync(start: b), "Ctrl+Shift+G"),
            ],
            [
                Tracking(ops, b),
                Item("Rename...", () => tab.RefOps.RenameBranchAsync(b), "F2"),
                Item("Delete...", () => tab.RefOps.DeleteBranchesAsync([b]), "Delete", enabled: !active),
            ],
            [Item("Copy Branch Name", () => copy(b.ShortName), "Ctrl+C")]);
    }

    /// <summary>Tracking submenu, or "Change Tracking Reference..." with 150 or more remote branches.</summary>
    static MenuEntry Tracking(RemoteOps ops, GitRef b)
    {
        if (ops.UseTrackingDialog) return Item("Change Tracking Reference...", () => ops.OpenChangeTrackingAsync(b.ShortName));
        var items = Join(
            b.Upstream is null ? [] : [Item("Remove tracking reference", () => ops.SetUpstreamAsync(b.ShortName, null))],
            ops.RemoteBranches.Select(r => new MenuEntry
            {
                Header = r.ShortName, IsChecked = r.FullName == b.Upstream,
                Action = () => ops.SetUpstreamAsync(b.ShortName, r.FullName),
            }));
        return Sub("Tracking", items, enabled: items.Count > 0);
    }

    /// <summary>Local branches (two or more selected). The fast-forward item is disabled when no
    /// selected branch has an existing upstream.</summary>
    public static List<MenuEntry> LocalBranches(RepoTabViewModel tab, IReadOnlyList<GitRef> bs)
    {
        var ops = tab.RemoteOps;
        var names = bs.Select(b => b.ShortName).ToList();
        var push = PushMany(ops, $"{bs.Count} branches", r => ops.OpenPushBranchesAsync(names, r)).ToList();
        var ff = Item($"Fast-Forward {bs.Count} branches to their upstreams", () => ops.FastForwardAsync(names),
            enabled: bs.Any(RemoteOps.HasExistingUpstream));
        var delete = Item($"Delete {bs.Count} Branches...", () => tab.RefOps.DeleteBranchesAsync(bs));
        // The separator belongs to the push part ("no remote: nothing").
        return push.Count == 0 ? [ff, delete] : Join([ff, .. push], [delete]);
    }

    /// <summary>A local branch checked out in a worktree other than this tab's.</summary>
    static bool InOtherWorktree(RepoTabViewModel tab, GitRef b) => tab.WorktreeOf(b.FullName) is { IsCurrent: false };

    /// <summary>Checkout part of a remote branch: by its downstream local branch when that one is checked
    /// out in a worktree, else <paramref name="checkout"/> and (enabled) "Checkout as Worktree...".</summary>
    static IEnumerable<MenuEntry> RemoteCheckoutPart(RepoTabViewModel tab, GitRef r, MenuEntry checkout)
    {
        var down = tab.Refs.Refs.FirstOrDefault(l => l.Kind == RefKind.LocalBranch && l.Upstream == r.FullName);
        if (tab.WorktreeOf(down?.FullName) is { } wt)
            return wt.IsMain ? [] : [OpenWorktree(tab, wt)];
        return tab.WorktreesEnabled ? [checkout, Item("Checkout as Worktree...", () => tab.WorktreeOps.CheckoutRemoteAsWorktreeAsync(r))] : [checkout];
    }

    /// <summary>Remote branch (one selected).</summary>
    public static List<MenuEntry> RemoteBranch(RepoTabViewModel tab, GitRef r, Func<string, Task> copy)
    {
        var active = Active(tab);
        return Join(
            RemoteCheckoutPart(tab, r, Item("Checkout...", () => tab.RefOps.CheckoutRemoteBranchAsync(r))),
            active is null ? [] : [Item($"Pull into '{active}'...", () => tab.RemoteOps.OpenPullAsync(r.FullName), "Ctrl+Shift+L")],
            active is null ? [] : [Merge(tab, r, enabled: r.Sha != ActiveTip(tab)), .. RebaseOn(tab, r, enabled: r.Sha != ActiveTip(tab))],
            [
                .. CreatePullRequest(tab, r),
                Item("New Branch...", () => tab.RefOps.CreateBranchAsync(start: r), "Ctrl+Shift+B"),
                Item("New Tag...", () => tab.RefOps.CreateTagAsync(start: r), "Ctrl+Shift+G"),
            ],
            [Item("Delete...", () => tab.RefOps.DeleteRemoteBranchesAsync([r]), "Delete")],
            [Item("Copy Branch Name", () => copy(r.ShortName), "Ctrl+C")]);
    }

    public static List<MenuEntry> RemoteBranches(RepoTabViewModel tab, IReadOnlyList<GitRef> rs) =>
        [Item($"Delete {rs.Count} Remote Branches...", () => tab.RefOps.DeleteRemoteBranchesAsync(rs))];

    /// <summary>Remote node.</summary>
    public static async Task<List<MenuEntry>> Remote(RepoTabViewModel tab, string remote, Func<string, Task> copy)
    {
        var ops = tab.RemoteOps;
        bool automatic = await ops.IsFetchAutomaticallyAsync(remote);
        var url = tab.Remotes.FirstOrDefault(r => r.Name == remote)?.Url;
        // "View on <service>" opens the repository page.
        var viewOn = tab.Hosted(remote) is { RepositoryPage: { } page, ServiceName: { } service }
            ? [Item($"View on {service}", () => { tab.Hosting.OpenBrowser(page); return Task.CompletedTask; })] : new List<MenuEntry>();
        return Join(
            [Item($"Fetch '{remote}'...", () => ops.OpenFetchAsync(remote), "Ctrl+Shift+F")],
            [Item($"Edit '{remote}'...", () => ops.OpenEditRemoteAsync(remote)), Item($"Delete '{remote}'...", () => ops.DeleteRemoteAsync(remote))],
            [new MenuEntry
            {
                Header = $"Fetch '{remote}' Automatically", IsChecked = automatic,
                Action = () => ops.SetFetchAutomaticallyAsync(remote, !automatic),
            }],
            viewOn,
            [Item("Add New Remote...", () => ops.OpenAddRemoteAsync())],
            [Item("Copy Remote Address", () => copy(url ?? ""), enabled: url is not null)]);
    }

    /// <summary>Tag (one selected).</summary>
    public static List<MenuEntry> Tag(RepoTabViewModel tab, GitRef t, Func<string, Task> copy)
    {
        var first = new List<MenuEntry> { Item("Checkout Commit...", () => tab.RefOps.CheckoutCommitAsync(t.Sha, t)) };
        if (t.TagObjectSha is not null) first.Add(Item("Show Tag Details...", () => tab.RefOps.ShowTagDetailsAsync(t)));
        return Join(
            first,
            PushTag(tab.RemoteOps, t.ShortName),
            Active(tab) is null ? [] : [Merge(tab, t, enabled: t.Sha != ActiveTip(tab)), .. RebaseOn(tab, t, enabled: t.Sha != ActiveTip(tab))],
            [Item("New Branch...", () => tab.RefOps.CreateBranchAsync(start: t)), Item("New Tag...", () => tab.RefOps.CreateTagAsync(start: t))],
            [Item("Delete...", () => tab.RefOps.DeleteTagsAsync([t]), "Delete")],
            [Item("Copy Tag Name", () => copy(t.ShortName), "Ctrl+C")]);
    }

    public static List<MenuEntry> Tags(RepoTabViewModel tab, IReadOnlyList<GitRef> ts)
    {
        var names = ts.Select(t => t.ShortName).ToList();
        return Join(PushMany(tab.RemoteOps, $"{ts.Count} tags", r => tab.RemoteOps.OpenPushTagsAsync(names, r)),
            [Item($"Delete {ts.Count} Tags...", () => tab.RefOps.DeleteTagsAsync(ts))]);
    }

    /// <summary>Stash (one selected), with the copy items.</summary>
    public static List<MenuEntry> Stash(RepoTabViewModel tab, StashEntry s, IEnumerable<MenuEntry>? copyItems = null) =>
        Join(
            [Item("Apply...", () => tab.StashOps.ApplyAsync(s)), Item("Rename...", () => tab.StashOps.RenameAsync(s))],
            [Item("Delete...", () => tab.StashOps.DeleteAsync([s]), "Delete")],
            copyItems ?? []);

    public static List<MenuEntry> Stashes(RepoTabViewModel tab, IReadOnlyList<StashEntry> ss) =>
        [Item($"Delete {ss.Count} Stashes...", () => tab.StashOps.DeleteAsync(ss))];

    // ------------------------------------------------------------------ commit list

    /// <summary>Stash menu, commit menu, multi-selection menu, or null (nothing selected).
    /// Not built yet: the merge menu (graph cell of a merge); it needs merge collapsing.</summary>
    public static List<MenuEntry>? CommitList(CommitListViewModel list)
    {
        var rows = list.SelectedRows;
        var tab = list.Owner;
        if (rows.Count == 0) return null;
        List<MenuEntry> copy =
        [
            Item("Copy Commit SHA", list.CopyShasAsync, "Ctrl+C"),
            Item("Copy Commit Info", list.CopyInfoAsync, "Ctrl+Shift+C"),
        ];
        if (rows.All(r => r.Stash is not null))
            return rows.Count == 1 ? Stash(tab, rows[0].Stash!, copy) : Stashes(tab, rows.Select(r => r.Stash!).ToList());
        if (rows.Count == 1) return Commit(list, rows[0], copy);
        // Multi-selection. Not built yet: "Save Commit Range as Patch…" (patches).
        bool singleParents = rows.All(r => r.Stash is null && r.VisualParents.Length == 1);
        var shas = rows.Select(r => r.Sha).ToList();
        var rebase = new List<MenuEntry>();
        if (Active(tab) is not null && rows.All(r => r.Stash is null && r.IsReachable))
        {
            // Multi-selection: "Squash into Parent…" only for an unbroken chain of single-parent commits.
            if (singleParents && RebaseTodo.IsParentChain(rows.Select(r => (r.Sha, (IReadOnlyList<string>)r.VisualParents)).ToList()))
                rebase.Add(Item("Squash into Parent…", () => new RebaseOps(tab).QuickActionAsync(QuickAction.Squash, shas)));
            rebase.Add(Item("Drop…", () => new RebaseOps(tab).QuickActionAsync(QuickAction.Drop, shas)));
        }
        return Join(
            [Item("Cherry-pick…", () => tab.CommitOps.CherryPickAsync(shas), enabled: singleParents)],
            rebase,
            copy);
    }

    /// <summary>Commit menu (one commit selected).</summary>
    static List<MenuEntry> Commit(CommitListViewModel list, CommitRow row, List<MenuEntry> copy)
    {
        var tab = list.Owner;
        var refs = RowRefs(tab, row);
        var merge = new List<MenuEntry>();
        if (Active(tab) is { } active)
        {
            // Merge, rebase and reset entries (no branch filters yet): only for commits not reachable from HEAD, merge target = the first
            // local branch on the row, else the first tag, else the first reference.
            if (!row.IsReachable)
            {
                if (row.Sha != ActiveTip(tab)
                    && (refs.FirstOrDefault(r => r.Kind == RefKind.LocalBranch) ?? refs.FirstOrDefault(r => r.Kind == RefKind.Tag)
                        ?? refs.FirstOrDefault()) is { } target)
                    merge.Add(Merge(tab, target));
                merge.Add(Item($"Rebase '{active}' to Here...", () => new RebaseOps(tab).RebaseToCommitAsync(row.Sha)));
            }
            merge.Add(InteractiveRebase(list, row, active));
        }
        return Join(
            BranchOrder(tab, refs).Select(b => Sub(b.ShortName, b.Kind == RefKind.LocalBranch ? LocalBranchSub(tab, b, row) : RemoteBranchSub(tab, b, row))),
            refs.Where(r => r.Kind == RefKind.Tag).Select(t => Sub(t.ShortName, TagSub(tab, t))),
            [
                Item("New Branch...", () => tab.RefOps.CreateBranchAsync(commitSha: row.Sha), "Ctrl+Shift+B"),
                Item("New Tag...", () => tab.RefOps.CreateTagAsync(commitSha: row.Sha), "Ctrl+Shift+G"),
            ],
            merge,
            row.IsHead ? [] : [Item($"Reset '{Active(tab) ?? "HEAD"}' to Here...", () => tab.RefOps.ResetAsync(row.Sha))],
            [
                Item("Checkout Commit...", () => tab.RefOps.CheckoutCommitAsync(row.Sha)),
                Item("Cherry-pick Commit...", () => tab.CommitOps.CherryPickAsync([row.Sha])),
                Item("Revert Commit...", () => tab.CommitOps.RevertAsync(row.Sha)),
            ],
            copy);
    }

    /// <summary>Interactive rebase items: the single item for unreachable
    /// commits or a merge on the path from HEAD (the commit itself counts), else the submenu with the quick actions.
    /// The dots: "Reword...", "Edit...", "Squash into Parent..." three dots, "Fixup into Parent…", "Drop…" the ellipsis.</summary>
    static MenuEntry InteractiveRebase(CommitListViewModel list, CommitRow row, string active)
    {
        var tab = list.Owner;
        var item = Item($"Interactively Rebase '{active}' to Here...", () => new RebaseOps(tab).InteractiveToCommitAsync(row.Sha));
        if (!row.IsReachable || MergeOnPath(list, row)) return item;
        bool root = row.VisualParents.Length == 0;
        Task Quick(QuickAction a) => new RebaseOps(tab).QuickActionAsync(a, [row.Sha]);
        return Sub("Interactive Rebase", Join(
            [item],
            [Heading("Quick Actions")],
            [
                Item("Reword...", () => Quick(QuickAction.Reword)),
                Item("Edit...", () => Quick(QuickAction.Edit)),
                Item("Squash into Parent...", () => Quick(QuickAction.Squash), enabled: !root),
                Item("Fixup into Parent…", () => Quick(QuickAction.Fixup), enabled: !root),
                Item("Drop…", () => Quick(QuickAction.Drop), enabled: !root),
            ]));
    }

    /// <summary>A merge commit lies on the path from HEAD down to <paramref name="row"/> (both included). Walks the
    /// loaded rows along single parents: below a linear stretch the row must come next, so the first merge met is in
    /// the range. A commit that is not loaded counts as a merge (the single item is offered).</summary>
    static bool MergeOnPath(CommitListViewModel list, CommitRow row)
    {
        for (var sha = list.Owner.Refs.HeadSha; sha is not null;)
        {
            int i = list.IndexOf(sha);
            if (i < 0) return true;
            var r = list.Rows[i];
            if (r.VisualParents.Length > 1) return true;
            if (sha == row.Sha) return false;
            sha = r.VisualParents.FirstOrDefault();
        }
        return true;
    }

    /// <summary>The references of a row in label order.</summary>
    internal static List<GitRef> RowRefs(RepoTabViewModel tab, CommitRow row) =>
        row.Labels.Select(l => tab.Refs.Refs.FirstOrDefault(r => r.FullName == l.FullName)).OfType<GitRef>().ToList();

    /// <summary>Natural order by full name, a local branch directly before its own upstream.</summary>
    static List<GitRef> BranchOrder(RepoTabViewModel tab, List<GitRef> refs)
    {
        var branches = refs.Where(r => r.Kind is RefKind.LocalBranch or RefKind.RemoteBranch)
            .OrderBy(r => r.FullName, RemoteOps.Natural).ToList();
        var result = new List<GitRef>();
        foreach (var b in branches)
        {
            if (result.Contains(b)) continue;
            result.Add(b);
            if (b.Kind == RefKind.LocalBranch && branches.FirstOrDefault(r => r.FullName == b.Upstream) is { } up && !result.Contains(up))
                result.Add(up);
        }
        return result;
    }

    /// <summary>Branch submenu, local branch (without the Pin/Filter/Hide toggle row).</summary>
    static List<MenuEntry> LocalBranchSub(RepoTabViewModel tab, GitRef b, CommitRow row)
    {
        var ops = tab.RemoteOps;
        bool active = b.FullName == tab.Refs.ActiveBranch;
        var middle = new List<MenuEntry>();
        if (RemoteOps.HasExistingUpstream(b))
        {
            middle.Add(Item($"Fast-Forward to '{RemoteOps.ShortRef(b.Upstream!)}'", () => ops.FastForwardAsync(b.ShortName),
                enabled: !InOtherWorktree(tab, b)));
            if (active) middle.Add(Item("Pull...", () => ops.OpenPullAsync(b.Upstream)));
        }
        middle.AddRange(PushBranch(ops, b, "Push...", disabledWhenNone: false));
        var rename = new List<MenuEntry> { Item("Rename...", () => tab.RefOps.RenameBranchAsync(b), "F2") };
        if (!active) rename.Add(Item("Delete...", () => tab.RefOps.DeleteBranchesAsync([b]), "Delete"));
        return Join(
            [Heading(b.ShortName)],
            !active
                ? CheckoutPart(tab, b, [Item("Checkout", () => tab.RefOps.CheckoutBranchAsync(b))], () => tab.WorktreeOps.CheckoutAsWorktreeAsync(b))
                : [],
            middle,
            HasCommitsActiveLacks(tab, b, row) ? [Merge(tab, b)] : [],
            rename,
            [Item("Copy Branch Name", () => CopyName(tab, b), "Ctrl+C")]);
    }

    /// <summary>Branch submenu, remote branch.</summary>
    static List<MenuEntry> RemoteBranchSub(RepoTabViewModel tab, GitRef r, CommitRow row) =>
        Join(
            [Heading(r.ShortName)],
            [.. RemoteCheckoutPart(tab, r, Item("Checkout", () => tab.RefOps.CheckoutRemoteBranchAsync(r))), .. CreatePullRequest(tab, r)],
            HasCommitsActiveLacks(tab, r, row) ? [Merge(tab, r)] : [],
            [Item("Delete...", () => tab.RefOps.DeleteRemoteBranchesAsync([r]), "Delete")],
            [Item("Copy Branch Name", () => CopyName(tab, r), "Ctrl+C")]);

    /// <summary>Tag submenu.</summary>
    static List<MenuEntry> TagSub(RepoTabViewModel tab, GitRef t) =>
        Join(
            [Heading(t.ShortName)],
            // The only separator in this part follows "Merge into..."; without it Details and Delete are one group.
            new[]
            {
                t.TagObjectSha is null ? null : Item("Show Tag Details...", () => tab.RefOps.ShowTagDetailsAsync(t)),
                Active(tab) is null ? null : Merge(tab, t),
                Active(tab) is null ? Item("Delete...", () => tab.RefOps.DeleteTagsAsync([t]), "Delete") : null,
            }.OfType<MenuEntry>(),
            Active(tab) is null ? [] : [Item("Delete...", () => tab.RefOps.DeleteTagsAsync([t]), "Delete")],
            [Item("Copy Tag Name", () => CopyName(tab, t), "Ctrl+C")],
            PushTag(tab.RemoteOps, t.ShortName));

    /// <summary>An active branch exists, the tips differ and the row (the tip) is not an ancestor of HEAD.</summary>
    static bool HasCommitsActiveLacks(RepoTabViewModel tab, GitRef b, CommitRow row) =>
        Active(tab) is not null && b.Sha != ActiveTip(tab) && !row.IsReachable;

    static Task CopyName(RepoTabViewModel tab, GitRef r) => tab.CommitList.Copy(r.ShortName);
}
