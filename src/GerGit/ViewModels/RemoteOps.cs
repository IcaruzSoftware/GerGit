using System.Diagnostics;
using System.Globalization;
using GerGit.Core;
using GerGit.Core.Git;
using GerGit.Hosting;
using GerGit.ViewModels.Dialogs;
using GerGit.ViewModels.Dialogs.Hosting;
using GerGit.ViewModels.Dialogs.Remote;

namespace GerGit.ViewModels;

/// <summary>
/// Entry points of the remote operations of one tab: dialogs, quick variants, fast-forward, tracking and
/// remotes. Menus, toolbar and shortcuts call these. Local branches and tags are passed by short name
/// ("main", "v1"), remote branches by full ref ("refs/remotes/origin/main"), remotes by name.
/// Not built yet: no SSH keys dialog or LFS progress.
/// </summary>
public sealed class RemoteOps(RepoTabViewModel tab)
{
    /// <summary>Tracking submenu: with this many remote branches or more the dialog replaces the submenu.</summary>
    public const int TrackingMenuLimit = 150;

    internal const string AllRemotesKey = "lastUsed.fetch.allRemotes";
    internal const string RebaseKey = "lastUsed.pull.rebase";
    internal const string AutoStashKey = "lastUsed.pull.autoStash";
    internal const string PushTagsKey = "lastUsed.push.includeTags";
    internal const string FetchTagsKey = "general.fetchWithTags"; // Preferences > General

    // Drop-downs sort case-insensitively with numbers compared numerically.
    internal static readonly StringComparer Natural =
        StringComparer.Create(CultureInfo.InvariantCulture, CompareOptions.IgnoreCase | CompareOptions.NumericOrdering);

    internal RepoTabViewModel Tab => tab;
    GitRepository Repo => tab.Repo;
    static bool FetchTags => Settings.Get(FetchTagsKey, false);
    internal string RecentRemoteKey => Settings.RepoKey(tab.Path, "recent.remote");
    internal string PushRefspecKey => Settings.RepoKey(tab.Path, "recent.pushRefspec");

    // ---------------------------------------------------------------- data helpers (also for menu labels)

    /// <summary>Remote names in drop-down order.</summary>
    public IReadOnlyList<string> RemoteNames => tab.Remotes.Select(r => r.Name).Order(Natural).ToList();

    /// <summary>Icon type of a remote; unknown names are custom.</summary>
    public HostType HostOf(string remote) => tab.Hosted(remote)?.Type ?? HostType.Custom;

    /// <summary>All remote branches (ordinal by full name), e.g. for the "Tracking" submenu.</summary>
    public IReadOnlyList<GitRef> RemoteBranches => tab.Refs.Refs.Where(r => r.Kind == RefKind.RemoteBranch).ToList();

    /// <summary>"Change Tracking Reference..." dialog instead of the "Tracking" submenu.</summary>
    public bool UseTrackingDialog => RemoteBranches.Count >= TrackingMenuLimit;

    public IReadOnlyList<GitRef> LocalBranches =>
        tab.Refs.Refs.Where(r => r.Kind == RefKind.LocalBranch).OrderBy(r => r.ShortName, Natural).ToList();

    public GitRef? LocalBranch(string name) =>
        tab.Refs.Refs.FirstOrDefault(r => r.Kind == RefKind.LocalBranch && r.FullName == "refs/heads/" + name);

    public GitRef? ActiveBranch => tab.Refs.ActiveBranch is { } a ? tab.Refs.Refs.FirstOrDefault(r => r.FullName == a) : null;

    /// <summary>The branch has an upstream that exists ("Fast-Forward to '&lt;upstream&gt;'" is shown).</summary>
    public static bool HasExistingUpstream(GitRef b) => b.UpstreamState == UpstreamState.Valid;

    /// <summary>Ahead &gt; 0 and behind = 0 (picks the fast-forward pull).</summary>
    public static bool IsStrictlyAhead(GitRef b) => HasExistingUpstream(b) && b.Ahead > 0 && b.Behind == 0;

    /// <summary>"refs/remotes/origin/main" → "origin/main" (also refs/heads/, refs/tags/).</summary>
    public static string ShortRef(string fullRef) =>
        fullRef.StartsWith("refs/remotes/", StringComparison.Ordinal) ? fullRef["refs/remotes/".Length..]
        : fullRef.StartsWith("refs/heads/", StringComparison.Ordinal) ? fullRef["refs/heads/".Length..]
        : fullRef.StartsWith("refs/tags/", StringComparison.Ordinal) ? fullRef["refs/tags/".Length..]
        : fullRef;

    /// <summary>The remote of a remote-tracking ref.</summary>
    public string RemoteOf(string remoteBranch) => GitRepository.RemoteOfRef(remoteBranch, tab.Remotes.Select(r => r.Name));

    /// <summary>The branch name inside its remote ("refs/remotes/origin/feat/x" → "feat/x").</summary>
    public string NameInRemote(string remoteBranch) => remoteBranch[("refs/remotes/" + RemoteOf(remoteBranch) + "/").Length..];

    /// <summary>The remote of the branch's upstream when that remote exists.</summary>
    public string? UpstreamRemote(GitRef? b) =>
        b?.Upstream is { } up && RemoteOf(up) is var r && tab.Remotes.Any(x => x.Name == r) ? r : null;

    public IReadOnlyList<GitRef> RemoteBranchesOf(string remote) =>
        RemoteBranches.Where(r => r.FullName.StartsWith($"refs/remotes/{remote}/", StringComparison.Ordinal)).ToList();

    /// <summary>The first existing candidate, else "origin", else the first remote in drop-down order, else null.</summary>
    internal string? PickRemote(params string?[] candidates)
    {
        var names = RemoteNames;
        return candidates.FirstOrDefault(c => c is not null && names.Contains(c))
            ?? (names.Contains("origin") ? "origin" : names.FirstOrDefault());
    }

    // ---------------------------------------------------------------- fetch

    /// <summary>Fetch dialog; <paramref name="remote"/> preselected (sidebar remote item).</summary>
    public Task OpenFetchAsync(string? remote = null) => tab.ShowDialog(new FetchViewModel(this, remote));

    /// <summary>Quick fetch: "origin", else the first remote (ordinal); no remote → the Fetch dialog.</summary>
    public Task QuickFetchAsync()
    {
        if (tab.Remotes.Count == 0) return OpenFetchAsync();
        var remote = tab.Remotes.Any(r => r.Name == "origin") ? "origin" : tab.Remotes.Select(r => r.Name).Order(StringComparer.Ordinal).First();
        return FetchAsync(Settings.Get(AllRemotesKey, false) ? null : remote);
    }

    /// <summary>Job `Fetch all` (null) or `Fetch '&lt;remote&gt;'`; a tag clobber offers "Force Fetch".</summary>
    internal Task<Job> FetchAsync(string? remote) =>
        tab.RunJobAsync(remote is null ? "Fetch all" : $"Fetch '{remote}'",
            async j => await Repo.FetchAsync(j, remote, FetchTags), actions: ForceFetch(remote));

    /// <summary>Job `Fetch '&lt;remote&gt;'` as a silent background job (no prompts,
    /// no error window, refresh only after a success).</summary>
    internal Task<Job> BackgroundFetchAsync(string remote) =>
        tab.RunJobAsync($"Fetch '{remote}'", async j => await Repo.FetchAsync(j, remote, FetchTags, silent: true), background: true);

    /// <summary>Error-window actions of fetch and pull: "Force Fetch" (after "Fetch all" it
    /// force-fetches all remotes with `--all`) and no window for "Could not resolve host".</summary>
    static readonly GitErrorActions QuietHost = new(QuietHostError: true);

    GitErrorActions ForceFetch(string? remote) => new(ForceFetch: () =>
        tab.RunJobAsync("Force Fetch", async j => await Repo.FetchAsync(j, remote, tags: true, force: true), actions: QuietHost),
        QuietHostError: true);

    // ---------------------------------------------------------------- pull

    /// <summary>Pull dialog; <paramref name="remoteBranch"/> (full ref) preselected ("Pull into ..." / "Pull '&lt;upstream&gt;'...").</summary>
    public Task OpenPullAsync(string? remoteBranch = null) => tab.ShowDialog(new PullViewModel(this, remoteBranch));

    /// <summary>The active branch's existing upstream with the remembered options, else the dialog.</summary>
    public Task QuickPullAsync()
    {
        if (ActiveBranch is not { UpstreamState: UpstreamState.Valid, Upstream: { } up }) return OpenPullAsync();
        return PullAsync(RemoteOf(up), null, up, Settings.Get(RebaseKey, false), Settings.Get(AutoStashKey, false));
    }

    /// <summary>Job `Pull '&lt;remote branch&gt;'`; <paramref name="branch"/> null = the configured upstream. Selects HEAD.
    /// A merge pull of a remote branch named unlike the active branch is `--no-ff` (deliberate design choice).</summary>
    internal Task<Job> PullAsync(string remote, string? branch, string remoteBranch, bool rebase, bool autostash)
    {
        bool noFastForward = !rebase && ActiveBranch is { } a && (branch ?? NameInRemote(remoteBranch)) != a.ShortName;
        return tab.RunJobAsync($"Pull '{ShortRef(remoteBranch)}'",
            async j => await Repo.PullAsync(j, remote, branch, rebase, autostash, FetchTags, noFastForward),
            afterRefresh: SelectHead, actions: ForceFetch(remote));
    }

    // ---------------------------------------------------------------- push

    /// <summary>Push dialog; <paramref name="branch"/> (default: active) and <paramref name="remote"/> preselected.</summary>
    public Task OpenPushAsync(string? branch = null, string? remote = null) => tab.ShowDialog(new PushViewModel(this, branch, remote));

    /// <summary>Quick push.</summary>
    public Task QuickPushAsync() => QuickPushAsync(null);

    /// <summary>Quick push; for commit and push <paramref name="committedBranch"/> (full ref) is the branch that
    /// was committed: when HEAD is on another branch by now, nothing is pushed (logged).</summary>
    public async Task QuickPushAsync(string? committedBranch)
    {
        if (!tab.IsLoaded || ActiveBranch is not { } b) return;
        if (committedBranch is not null && (b.FullName != committedBranch || await Repo.ResolveBranchAsync() != committedBranch))
        {
            Trace.WriteLine($"Commit and push: '{ShortRef(committedBranch)}' was committed, but HEAD is on another branch now; not pushed");
            return;
        }
        bool tags = Settings.Get(PushTagsKey, false);
        var refspec = "refs/heads/" + b.ShortName;
        if (HasExistingUpstream(b) && UpstreamRemote(b) is { } remote) await PushAsync(b.ShortName, remote, refspec, false, tags, false);
        else if (tab.Remotes.Count == 1) await PushAsync(b.ShortName, tab.Remotes[0].Name, refspec, false, tags, true);
        else await OpenPushAsync(b.ShortName);
    }

    /// <summary>
    /// Job `Push '&lt;branch&gt;' to '&lt;remote&gt;'`. With a pre-push hook git runs
    /// like the commit with checks, and once a pre-commit hook line shows the checks window "&lt;repository&gt; — Push —
    /// Checks" opens (not modal; Cancel = the hooks' cancel ladder). A red hook stops the push and the window shows it;
    /// there is no skip (hooks are never bypassed).
    /// </summary>
    internal async Task<Job> PushAsync(string branch, string remote, string refspec, bool force, bool tags, bool setUpstream,
        Func<Task>? afterRefresh = null)
    {
        var checks = await Repo.HasHookAsync(CancellationToken.None, "pre-push") ? new ChecksDialogViewModel($"{tab.Title} — Push — Checks") : null;
        return await tab.RunJobAsync($"Push '{branch}' to '{remote}'", async j =>
        {
            if (checks is null) return await Repo.PushAsync(j, remote, refspec, force, tags, setUpstream);
            bool shown = false;
            var r = await checks.Checks.RunAsync(j, () => Repo.PushAsync(j, remote, refspec, force, tags, setUpstream, checks: true),
                () => shown = !tab.ShowDialog(checks).IsCompleted); // not shown (no main window): as without hooks
            if (shown) ShowChecksResult(checks, r);
            return r;
        }, checks, afterRefresh);
    }

    /// <summary>The push's result in its checks window. Pushed and green: closes by itself after
    /// <see cref="LocalChangesViewModel.ChecksCloseDelay"/>. A red hook (not pushed) or a cancel: stays open with the result
    /// (KeepOpen: no Git Error window). Any other failure (e.g. rejected): the job's end closes it and the Git Error window
    /// shows git's output as without hooks.</summary>
    static void ShowChecksResult(ChecksDialogViewModel vm, GitResult r)
    {
        var failed = vm.Checks.Hooks.Where(h => h.IsFailed).Select(h => h.Name).ToList();
        var names = string.Join(", ", failed);
        if (r.ExitCode == 0 && failed.Count == 0)
        {
            vm.SetStatus(DialogStatus.Success, "Pushed, checks passed");
            _ = vm.CloseLaterAsync(() => LocalChangesViewModel.ChecksCloseDelay);
        }
        else if (r.ExitCode == 0) vm.SetStatus(DialogStatus.Warning, $"Pushed, but checks failed: {names}");
        else if (r.Cancelled) vm.SetStatus(DialogStatus.Warning, failed.Count > 0 ? "Cancelled during checks — not pushed" : "Push cancelled");
        else if (failed.Count > 0) vm.SetStatus(DialogStatus.Error, $"Checks failed: {names} — not pushed");
        else return;
        vm.KeepOpen = true;
        vm.CancelText = "Close";
    }

    /// <summary>"Push N branches to '&lt;remote&gt;'..." (multi-selection of local branches).</summary>
    public Task OpenPushBranchesAsync(IReadOnlyList<string> branches, string remote) =>
        tab.ShowDialog(new PushBranchesViewModel(this, branches, remote));

    internal Task<Job> PushBranchesAsync(IReadOnlyList<string> branches, string remote) =>
        tab.RunJobAsync($"Push {branches.Count} branches to '{remote}'",
            async j => await Repo.PushRefsAsync(j, remote, branches.Select(b => "refs/heads/" + b).ToList(), branches: true),
            afterRefresh: SelectHead);

    /// <summary>Tag menu "Push to '&lt;remote&gt;'...".</summary>
    public Task OpenPushTagAsync(string tag, string? remote = null) => OpenPushTagsAsync([tag], remote);

    /// <summary>"Push N tags to '&lt;remote&gt;'..." (one tag = the Push Tag dialog).</summary>
    public Task OpenPushTagsAsync(IReadOnlyList<string> tags, string? remote = null) =>
        tab.ShowDialog(new PushTagsViewModel(this, tags, remote));

    internal Task<Job> PushTagsAsync(IReadOnlyList<string> tags, string remote) =>
        tab.RunJobAsync(tags.Count == 1 ? $"Push '{tags[0]}' to '{remote}'" : $"Push {tags.Count} tags to '{remote}'",
            async j => await Repo.PushRefsAsync(j, remote, tags.Select(t => "refs/tags/" + t).ToList(), branches: false));

    // ---------------------------------------------------------------- pull / merge requests

    /// <summary>"Push and Create Pull Request on '&lt;remote&gt;'": the normal branch push
    /// (no force, track when the branch has no upstream; there is no "push all tags" preference, so tags are never pushed),
    /// a failure shows the error window and stops; on success the refresh selects HEAD and the create flow follows.</summary>
    public async Task PushAndCreatePullRequestAsync(string branch, string remote)
    {
        if (LocalBranch(branch) is not { } b) return;
        var job = await PushAsync(branch, remote, "refs/heads/" + branch, force: false, tags: false,
            setUpstream: b.Upstream is null, afterRefresh: SelectHead);
        if (job.Outcome == JobOutcome.Succeeded && LocalBranch(branch) is { } pushed) await CreatePullRequestAsync(pushed, remote);
    }

    /// <summary>A local branch without an existing upstream, or strictly ahead of it, is pushed before the pull
    /// request is created ("Push and Create ...").</summary>
    public static bool PushesBeforePullRequest(GitRef b) => !HasExistingUpstream(b) || IsStrictlyAhead(b);

    /// <summary>The pull request of a local branch: push and create, or only create (<see cref="PushesBeforePullRequest"/>).</summary>
    public Task StartPullRequestAsync(GitRef b, string remote) =>
        PushesBeforePullRequest(b) ? PushAndCreatePullRequestAsync(b.ShortName, remote) : CreatePullRequestAsync(b, remote);

    /// <summary>"Create Pull Request on '&lt;remote&gt;'" for a local branch; source = the default source branch
    /// (the upstream's name in its remote, else the local name).</summary>
    public Task CreatePullRequestAsync(GitRef b, string remote) =>
        CreatePullRequestAsync(remote, HasExistingUpstream(b) ? NameInRemote(b.Upstream!) : b.ShortName, b.Sha);

    /// <summary>With a matched account the Create Pull / Merge Request dialog (REST), else the host's new-request page.</summary>
    public Task CreatePullRequestAsync(string remote, string source, string tipSha)
    {
        if (tab.Hosted(remote) is not { Url: { } url } h) return Task.CompletedTask;
        if (h.Account is not null) return tab.ShowDialog(new CreatePullRequestViewModel(this, h, source, tipSha));
        if (url.NewPullRequestPage(null, source) is { } page) tab.Hosting.OpenBrowser(page);
        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------- fast-forward

    /// <summary>
    /// "Fast-Forward to '&lt;upstream&gt;'": the local fast-forward, or the fast-forward pull when the branch is strictly ahead
    ///. An upstream that no longer exists shows the "doesn't exist" error. Selects the upstream's commit.
    /// </summary>
    public async Task FastForwardAsync(string branch)
    {
        if (LocalBranch(branch) is not { Upstream: { } up } b) return;
        if (!HasExistingUpstream(b))
        {
            await tab.ShowError("Git Error", $"Remote branch '{ShortRef(up)}' doesn't exist. It might be removed, but '{branch}' refers to it.");
            return;
        }
        bool active = tab.Refs.ActiveBranch == b.FullName;
        if (IsStrictlyAhead(b))
            await tab.RunJobAsync($"Pull '{ShortRef(up)}' into '{branch}'",
                async j => await Repo.FastForwardPullAsync(j, branch, RemoteOf(up), NameInRemote(up), active),
                afterRefresh: () => Select(up), actions: QuietHost);
        else
            await tab.RunJobAsync($"Fast forward '{branch}'",
                async j => await Repo.FastForwardAsync(j, branch, up, active), afterRefresh: () => Select(up));
    }

    /// <summary>"Fast-Forward N branches to their upstreams": every branch with an existing upstream, one job each.</summary>
    public async Task FastForwardAsync(IReadOnlyList<string> branches)
    {
        foreach (var b in branches)
            if (LocalBranch(b) is { } r && HasExistingUpstream(r)) await FastForwardAsync(b);
    }

    // ---------------------------------------------------------------- tracking

    /// <summary>"Tracking" submenu: set the upstream (full remote ref) or remove it (null). Selects the branch's commit.</summary>
    public Task SetUpstreamAsync(string branch, string? remoteBranch) => SetUpstreamAsync(branch, remoteBranch, null);

    internal Task<Job> SetUpstreamAsync(string branch, string? remoteBranch, DialogViewModel? dialog) =>
        tab.RunJobAsync(remoteBranch is null ? "Remove tracking reference" : "Update tracking reference",
            async j => await Repo.SetUpstreamAsync(j, branch, remoteBranch), dialog,
            afterRefresh: () => Select("refs/heads/" + branch));

    /// <summary>"Change Tracking Reference..." dialog (repositories with ≥ <see cref="TrackingMenuLimit"/> remote branches).</summary>
    public Task OpenChangeTrackingAsync(string branch) =>
        LocalBranch(branch) is { } b ? tab.ShowDialog(new ChangeTrackingViewModel(this, b)) : Task.CompletedTask;

    // ---------------------------------------------------------------- remotes

    /// <summary>"Add New Remote..."; true when submitted.</summary>
    public Task<bool> OpenAddRemoteAsync() => tab.ShowDialog(new RemoteViewModel(this, null));

    /// <summary>"Edit '&lt;remote&gt;'..." (name and URL).</summary>
    public Task<bool> OpenEditRemoteAsync(string remote) =>
        tab.Remotes.FirstOrDefault(r => r.Name == remote) is { } e ? tab.ShowDialog(new RemoteViewModel(this, e)) : Task.FromResult(false);

    /// <summary>"Delete '&lt;remote&gt;'..." with its confirmation.</summary>
    public async Task DeleteRemoteAsync(string remote)
    {
        var confirm = new DialogViewModel
        {
            Title = $"Are you sure you want to delete reference to remote '{remote}'?",
            Description = $"Do you want to delete '{remote}'?",
            SubmitText = "Delete",
            WarningBadge = true,
            Width = 550,
        };
        if (!await tab.ShowDialog(confirm)) return;
        await tab.RunJobAsync($"Delete remote '{remote}'", async j => await Repo.RemoveRemoteAsync(j, remote));
    }

    /// <summary>State of the checkable "Fetch '&lt;remote&gt;' Automatically" item.</summary>
    public Task<bool> IsFetchAutomaticallyAsync(string remote) => Repo.IsFetchAutomaticallyAsync(remote);

    /// <summary>Toggles "Fetch '&lt;remote&gt;' Automatically" (hidden job: refresh, errors in the error window).</summary>
    public Task SetFetchAutomaticallyAsync(string remote, bool enabled) =>
        tab.RunJobAsync($"Fetch '{remote}' Automatically",
            async j => await Repo.SetFetchAutomaticallyAsync(j, remote, enabled), hidden: true);

    // ---------------------------------------------------------------- selection after a job

    Task SelectHead()
    {
        if (tab.Refs.HeadSha is { } sha) tab.CommitList.SelectCommit(sha);
        return Task.CompletedTask;
    }

    Task Select(string fullRef)
    {
        if (tab.Refs.Refs.FirstOrDefault(r => r.FullName == fullRef) is { } r) tab.CommitList.SelectCommit(r.Sha);
        return Task.CompletedTask;
    }
}
