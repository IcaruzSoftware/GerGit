using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GerGit.Core;
using GerGit.Core.Git;
using GerGit.Hosting;
using GerGit.ViewModels.Dialogs;
using GerGit.Views;
using GerGit.Views.Dialogs;

namespace GerGit.ViewModels;

/// <summary>One open repository tab: its data snapshot, refresh and file watching.</summary>
public sealed partial class RepoTabViewModel : ObservableObject, IDisposable
{
    readonly List<FileSystemWatcher> _watchers = [];
    // The first event starts it, events within 150 ms join the batch (not restarted: a steady stream of
    // events cannot hold the refresh back).
    readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(150) };
    readonly HashSet<string> _changedPaths = []; // working-tree paths of the current batch (UI thread)
    bool _refreshAll;     // the batch has a .git event or lost events: refresh without asking check-ignore
    bool _watchBusy;      // a batch is being filtered or refreshed; the next one waits for it (no refresh storm)
    // Watcher threads collect here and post one TakePending per batch, not one UI job per event.
    readonly Lock _pendingLock = new();
    HashSet<string> _pending = [];
    bool _pendingAll, _posted;
    // Batches dropped while a job runs that its refresh does not cover (UI thread); queued when the pause ends.
    readonly HashSet<string> _droppedPaths = [];
    bool _droppedAll;
    // Taken on the UI thread: watcher threads must not read Dispatcher.UIThread, which an unset UI dispatcher
    // (between headless tests) hands to the calling thread.
    readonly Dispatcher _dispatcher = Dispatcher.UIThread;
    CancellationTokenSource? _refreshCts;
    bool _lockFreeStatus; // set for a watcher refresh: the next status read must not write the index
    bool _disposed;
    bool _dataErrorShown; // a failing data refresh shows its error once, until a refresh succeeds again
    int _watchPaused;     // > 0 while a job of this tab runs: watcher events are ignored

    public RepoTabViewModel(GitRepository repo)
    {
        Repo = repo;
        Title = repo.Info.Name;
        CommitList = new CommitListViewModel(this);
        Details = new DetailsViewModel(this);
        Sidebar = new SidebarViewModel(this);
        LocalChanges = new LocalChangesViewModel(this);
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            _ = RefreshForWatchersAsync();
        };
        Watch();
        WorkTreeWatcher = new WorkTreeWatcher(repo.Info.WorkTree);
        WorkTreeWatcher.Changed += QueueWatchEvent;
        WorkTreeWatcher.Failed += _ => _dispatcher.Post(WorkTreeWatcher.Stop); // logged by the watcher; activation refreshes remain
        ShowError = (title, text) => title == "Git Error"
            ? ShowGitErrorDialogAsync(GitErrors.Classify(text), null)
            : MessageWindow.ShowAsync(title, text);
        ShowGitError = (result, actions) => ShowGitErrorDialogAsync(GitErrors.Classify(result), actions);
        ShowResolver = path => MergeResolverWindow.ShowAsync(this, path);
    }

    async Task ShowGitErrorDialogAsync(GitError error, GitErrorActions? actions)
    {
        var vm = GitErrorViewModel.Create(error, Repo.Info.GitDir, Path, RefreshAsync, actions, Confirm);
        await ShowDialog(vm);
        if (vm.ChosenAction is { } action) await action(); // the window is closed first
    }

    /// <summary>
    /// Runs one operation as a job of this tab. While it runs the file
    /// watcher is paused (until the refresh and afterRefresh are done); <paramref name="dialog"/> (a dialog that stays open) is busy and is closed when the
    /// work ends (unless the work set <see cref="DialogViewModel.KeepOpen"/>). Then: refresh, <paramref name="afterRefresh"/> (e.g. select a commit), and for a failure that was
    /// not cancelled the Git Error window (with <paramref name="actions"/>; none for a dialog kept open).
    /// The work reports progress/output into the job (GitRunner.RunJobAsync) and returns the failing GitResult (or the
    /// last one, or null). Unless the work finished the job itself: a failed result fails it with the short reason,
    /// anything else succeeds it; an exception fails it with its text. A failed job without a failed result shows
    /// its message in the window. "Could not resolve host" opens no window when actions.QuietHostError is set (fetch and
    /// pull); the job message is the short `Could not resolve host '<host>'` in every case.
    /// <paramref name="background"/> (automatic fetch): a background job that never shows an error window and
    /// refreshes only after a success, and only when this tab is active (other tabs refresh when they are selected).
    /// </summary>
    public async Task<Job> RunJobAsync(string name, Func<Job, Task<GitResult?>> work, DialogViewModel? dialog = null,
        Func<Task>? afterRefresh = null, GitErrorActions? actions = null, bool hidden = false, bool background = false)
    {
        var job = new Job(name, hidden) { Background = background };
        Jobs.Start(job);
        _watchPaused++; // until the job's own refresh is done: git's last writes must not start a second one
        bool paused = true;
        try
        {
            if (dialog is not null)
            {
                dialog.RunningJob = job; // the dialog's Cancel / Esc cancel the job
                dialog.IsBusy = true;
            }
            GitResult? result = null;
            string? exceptionText = null;
            try
            {
                result = await work(job);
            }
            catch (Exception e)
            {
                exceptionText = e is GitException ? e.Message : "[Internal]: " + e.Message;
                if (e is not GitException) Trace.WriteLine(e);
            }
            var error = result is { Success: false } ? GitErrors.Classify(result) : null;
            if (exceptionText is not null) job.Fail(exceptionText);
            else if (error is not null) job.Fail(error.ShortMessage);
            else job.Succeed();
            Jobs.End(job);
            bool shownInDialog = dialog?.KeepOpen == true; // the dialog shows the result: open, no error window
            if (dialog is not null)
            {
                dialog.IsBusy = false;
                dialog.RunningJob = null;
                if (!shownInDialog) dialog.Close(true); // also after a cancel: closed as success, no error
            }

            if (!background || (job.Outcome == JobOutcome.Succeeded && IsActive))
            {
                // Watcher batches dropped so far are covered by this refresh; .git events during it are its own index write.
                _droppedPaths.Clear();
                _droppedAll = false;
                await RefreshAsync();
                while (!Refreshing.IsCompleted) await Refreshing; // a newer refresh replaced ours: wait for the latest
                _droppedAll = false;
            }
            if (afterRefresh is not null) await afterRefresh();
            EndWatchPause();
            paused = false;
            if (job.Outcome == JobOutcome.Failed && !background && !shownInDialog)
            {
                if (error is not null && result is not null)
                {
                    if (error.Kind != GitErrorKind.CouldNotResolveHost || actions?.QuietHostError != true)
                        await ShowGitError(result, actions);
                }
                else await ShowError("Git Error", exceptionText ?? job.Message);
            }
            return job;
        }
        finally
        {
            if (paused) EndWatchPause(); // also when afterRefresh throws
        }
    }

    /// <summary>Shows an error window; replaceable in tests. "Git Error" goes to the Git Error window (classified text).</summary>
    internal Func<string, string, Task> ShowError { get; set; }

    /// <summary>Shows the Git Error window for a failed result with its action; replaceable in tests.</summary>
    internal Func<GitResult, GitErrorActions?, Task> ShowGitError { get; set; }

    /// <summary>Opens a dialog modally; true = closed by submit. Replaceable in tests (fill the view model, call
    /// <see cref="DialogViewModel.SubmitAsync"/>, return <c>vm.Result == true</c>).</summary>
    internal Func<DialogViewModel, Task<bool>> ShowDialog { get; set; } = vm => DialogWindow.ShowAsync(vm);

    /// <summary>A confirmation (title, text, confirm button, width) with "Cancel"; true = confirmed. Replaceable in tests.
    /// Limitation: MessageWindow has no warning icon.</summary>
    internal Func<string, string, string, double, Task<bool>> Confirm { get; set; } =
        (title, text, button, width) => MessageWindow.ShowAsync(title, text, button, "Cancel", width);

    /// <summary>Opens the three-pane resolver for the unmerged file at this path (conflict card "Merge"); its job
    /// refreshes the tab. Replaceable in tests.</summary>
    internal Func<string, Task> ShowResolver { get; set; }

    /// <summary>The jobs of this tab (status box, Activity Manager).</summary>
    public JobQueue Jobs { get; } = new();

    public GitRepository Repo { get; }
    public string Path => Repo.Info.WorkTree;
    public CommitListViewModel CommitList { get; }
    public DetailsViewModel Details { get; }
    public SidebarViewModel Sidebar { get; }
    public LocalChangesViewModel LocalChanges { get; }
    public StashOps StashOps => field ??= new(this);
    public CommitOps CommitOps => field ??= new(this);
    public RemoteOps RemoteOps => field ??= new(this);
    public RefOps RefOps => field ??= new(this);
    public WorktreeOps WorktreeOps => field ??= new(this);

    /// <summary>The window that owns this tab (set when it opens the tab): worktree tabs open and close through it.</summary>
    internal MainWindowViewModel? Main { get; set; }

    /// <summary>Tab title; set by the main window when names collide.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeaderToolTip))]
    public partial string Title { get; set; }

    public string HeaderToolTip => Title + "\n" + Path;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveBranchName), nameof(StatusDescription), nameof(PullBadge), nameof(PushBadge))]
    public partial RefSnapshot Refs { get; private set; } = new([], null, null);

    [ObservableProperty]
    public partial IReadOnlyList<StashEntry> Stashes { get; private set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<SubmoduleEntry> Submodules { get; private set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<RemoteEntry> Remotes { get; private set; } = [];

    /// <summary>Main (unless bare) + linked worktrees; empty when there is no linked worktree.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<WorktreeEntry> Worktrees { get; private set; } = [];

    /// <summary>Worktrees seen flag: set once any repository had worktrees.</summary>
    public const string ShowWorktreesKey = "views.showWorktrees";

    /// <summary>Worktrees: "Checkout as Worktree..." is offered when this repository has worktrees or the
    /// global flag is on.</summary>
    public bool WorktreesEnabled => Worktrees.Count > 0 || Settings.Get(ShowWorktreesKey, false);

    /// <summary>The worktree that has this branch (full ref) checked out, null when none (or no linked worktrees).</summary>
    public WorktreeEntry? WorktreeOf(string? branch) => branch is null ? null : Worktrees.FirstOrDefault(w => w.Branch == branch);

    /// <summary>Matching a remote URL to an account, one entry per remote in <see cref="Remotes"/> order. Part of
    /// the remote comparison: an account change (the app refreshes the active tab) updates icons and menus.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<HostedRemote> HostedRemotes { get; private set; } = [];

    /// <summary>The accounts used for <see cref="HostedRemotes"/> and pull requests; tests set their own.</summary>
    internal HostingService Hosting { get => field ?? HostingService.Current; set; }

    /// <summary>Hosting data of a remote, null when there is no such remote.</summary>
    public HostedRemote? Hosted(string remote) => HostedRemotes.FirstOrDefault(r => r.Name == remote);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LocalChangesText))]
    public partial StatusSnapshot Status { get; private set; } = new([], []);

    /// <summary>The conflict state read after every status read.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Banner), nameof(HasBanner))]
    public partial RepoState State { get; private set; } = RepoState.Clean;

    /// <summary>The notification bar; null = hidden.
    /// Not built yet: a .gitignore suggestion and bisect mode.</summary>
    public BannerContent? Banner => BannerContent.For(State);
    public bool HasBanner => Banner is not null;

    /// <summary>Banner `Resolve`: the Local Changes view (the button shows only while the commit list is shown).</summary>
    [RelayCommand]
    void Resolve() => ShowLocalChanges();

    /// <summary>A banner link or a conflict-card SHA button: the commit list with that commit selected.</summary>
    [RelayCommand]
    internal void OpenCommit(string sha)
    {
        IsAllCommitsView = true;
        CommitList.SelectCommit(sha);
    }

    /// <summary>
    /// Banner `Abort`: confirmation, then the
    /// abort as a job; the draft message is cleared (and Amend unticked for rebase) even when git fails.
    /// </summary>
    [RelayCommand]
    async Task Abort()
    {
        var state = State;
        if (state.Kind == RepoStateKind.None || LocalChanges.Busy) return; // the banner button is disabled while Busy
        var texts = BannerContent.AbortTexts(state.Kind);
        if (texts is { } t && !await Confirm(t.Title, t.Text, t.Button, 600)) return;
        // Through Local Changes: the lists and the commit area stay locked while the abort runs.
        await LocalChanges.RunGitJobAsync(texts?.Button ?? "Abort", async job =>
        {
            var r = await Repo.AbortAsync(job, state);
            LocalChanges.ResetMessage(uncheckAmend: state.Kind == RepoStateKind.Rebase);
            return r;
        }, focusList: false);
    }

    /// <summary>False until the first refresh finished (status box shows "loading...").</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusDescription))]
    public partial bool IsLoaded { get; private set; }

    /// <summary>The selected tab; only that one is watched.</summary>
    [ObservableProperty]
    public partial bool IsActive { get; set; }

    partial void OnIsActiveChanged(bool value)
    {
        if (!value)
        {
            _debounce.Stop(); // no watcher refresh after the tab lost the selection
            _changedPaths.Clear();
            _refreshAll = false;
            _droppedPaths.Clear();
            _droppedAll = false;
        }
        if (value) WorkTreeWatcher.Start();
        else WorkTreeWatcher.Stop(); // an inactive tab holds no inotify watches
        foreach (var w in _watchers)
        {
            try { w.EnableRaisingEvents = value; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // OS watch limit reached -> log and continue without watching.
                Trace.WriteLine($"Cannot watch {w.Path}: {e.Message}");
            }
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAllCommitsView))]
    public partial bool IsLocalChangesView { get; set; }

    public bool IsAllCommitsView
    {
        get => !IsLocalChangesView;
        set => IsLocalChangesView = !value;
    }

    /// <summary>Ctrl+1: already visible → focus the subject, else the list with the selection.</summary>
    public void ShowLocalChanges()
    {
        bool visible = IsLocalChangesView;
        IsLocalChangesView = true;
        LocalChanges.RequestFocus(visible ? LocalChangesFocus.Subject : LocalChangesFocus.List);
    }

    /// <summary>Local Changes / All Commits rows.</summary>
    public string LocalChangesText => Status.FileCount == 0 ? "Local Changes" : $"Local Changes ({Status.FileCount})";

    public string? ActiveBranchName => Refs.ActiveBranch?["refs/heads/".Length..];

    /// <summary>Second line of the status box.</summary>
    public string StatusDescription => !IsLoaded ? "loading..." : ActiveBranchName ?? "detached HEAD";

    GitRef? ActiveBranchRef => Refs.Refs.FirstOrDefault(r => r.FullName == Refs.ActiveBranch && r.UpstreamState == UpstreamState.Valid);

    /// <summary>Null hides the badge.</summary>
    public string? PullBadge => ActiveBranchRef is { Behind: > 0 } b ? b.Behind.ToString() : null;
    public string? PushBadge => ActiveBranchRef is { Ahead: > 0 } b ? b.Ahead.ToString() : null;

    /// <summary>
    /// Data first (refs, stashes, submodules, remotes), then status, as two independent steps; a newer call cancels a
    /// running one. The commit list reloads only when refs, HEAD or stashes changed.
    /// Limitation: always the "standard refresh" set, no per-domain invalidation and no per-path status yet.
    /// </summary>
    public Task RefreshAsync() => _disposed ? Task.CompletedTask : Refreshing = RunRefreshAsync();

    /// <summary>Stops a running refresh without an error; data read so far stays.</summary>
    public void CancelRefresh() => _refreshCts?.Cancel();

    /// <summary>The latest refresh; a newer one cancels it, so waiters loop until the latest is done. Never faults.</summary>
    public Task Refreshing { get; private set; } = Task.CompletedTask;

    async Task RunRefreshAsync()
    {
        _refreshCts?.Cancel();
        var cts = _refreshCts = new CancellationTokenSource();
        var ct = cts.Token;
        var job = new Job("Refresh Repository", hidden: true); // only shows the status-box spinner
        Jobs.Start(job);
        try
        {
            await RefreshDataAsync(ct);
            await RefreshStatusAsync(ct);
        }
        catch (Exception e) when (e is OperationCanceledException || ct.IsCancellationRequested) { }
        catch (Exception e)
        {
            Trace.WriteLine(e); // last resort: a refresh must never take the app down
        }
        finally
        {
            job.Succeed();
            Jobs.End(job);
        }
    }

    async Task RefreshDataAsync(CancellationToken ct)
    {
        IReadOnlyList<StashEntry> stashes;
        IReadOnlyList<SubmoduleEntry> subs;
        IReadOnlyList<RemoteEntry> remotes;
        IReadOnlyList<WorktreeEntry> worktrees;
        RefSnapshot refs;
        try
        {
            var refsTask = Repo.ReadRefsAsync(ct);
            var stashesTask = Repo.ReadStashesAsync(ct);
            var subsTask = Repo.ReadSubmodulesAsync(ct);
            var remotesTask = Repo.ReadRemotesAsync(ct);
            var worktreesTask = Repo.ReadWorktreesAsync(ct);
            await Task.WhenAll(refsTask, stashesTask, subsTask, remotesTask, worktreesTask);
            (refs, stashes, subs, remotes, worktrees) = (refsTask.Result, stashesTask.Result, subsTask.Result, remotesTask.Result, worktreesTask.Result);
        }
        catch (GitException e) when (!ct.IsCancellationRequested)
        {
            // A linked worktree whose folder or git data is gone closes silently, its main tab is selected.
            if (Repo.Info.Kind == RepoKind.Worktree && (!Directory.Exists(Path) || !Directory.Exists(Repo.Info.GitDir)))
            {
                Main?.CloseWorktreeTab(this);
                return;
            }
            // Data refresh error -> error window, previous data stays.
            // Only once: a deleted repo folder would otherwise show it on every window activation.
            if (_dataErrorShown) return;
            _dataErrorShown = true;
            await ShowError("Git Error", e.Message);
            return;
        }
        if (ct.IsCancellationRequested) return;
        _dataErrorShown = false;

        var accounts = Hosting.Accounts;
        var hosted = remotes.Select(r => RemoteUrl.Parse(r.Url) is { } u ? new HostedRemote(r.Name, u, u.MatchAccount(accounts))
            : new HostedRemote(r.Name, null, null)).ToList();
        bool hostingChanged = !HostedRemotes.SequenceEqual(hosted);
        bool historyChanged = !IsLoaded || !SameRefs(Refs, refs) || !Stashes.SequenceEqual(stashes);
        bool worktreesChanged = !Worktrees.SequenceEqual(worktrees);
        bool treeChanged = historyChanged || hostingChanged || !Submodules.SequenceEqual(subs) || !Remotes.SequenceEqual(remotes)
            || worktreesChanged;
        Refs = refs;
        Stashes = stashes;
        Submodules = subs;
        Remotes = remotes;
        if (worktreesChanged) Worktrees = worktrees; // only a real change: the commit list relayouts its labels on it
        if (worktrees.Count > 0 && !Settings.Get(ShowWorktreesKey, false)) Settings.Set(ShowWorktreesKey, true);
        if (hostingChanged) HostedRemotes = hosted;
        IsLoaded = true;
        if (treeChanged) Sidebar.Rebuild();
        if (historyChanged) await CommitList.LoadAsync(refs, stashes);
    }

    async Task RefreshStatusAsync(CancellationToken ct)
    {
        StatusSnapshot status;
        try
        {
            status = await Repo.ReadStatusAsync(_lockFreeStatus, ct);
        }
        catch (GitException e) when (!ct.IsCancellationRequested)
        {
            // Status errors are silent, except the unsafe-repository error.
            Trace.WriteLine($"Status refresh failed: {e.Message}");
            if (GitErrors.Classify(e.Message).Kind == GitErrorKind.UnsafeRepository)
                await ShowError("Git Error", e.Message);
            return;
        }
        if (ct.IsCancellationRequested) return;
        _lockFreeStatus = false;
        var state = State;
        try { state = await Repo.ReadRepoStateAsync(status, ct); }
        catch (GitException e) when (!ct.IsCancellationRequested)
        {
            Trace.WriteLine($"Repository state read failed: {e.Message}"); // silent like status errors; the old state stays
        }
        if (ct.IsCancellationRequested) return;
        if (!SameStatus(Status, status))
        {
            bool dirtySubmodulesChanged = !DirtySubmodules(Status).SetEquals(DirtySubmodules(status));
            Status = status;
            if (dirtySubmodulesChanged) Sidebar.Rebuild();
        }
        var oldState = State;
        State = state;
        LocalChanges.OnStateRead(oldState, state);
        // Told after every read: the selected file's diff reloads even when the snapshot is unchanged.
        await LocalChanges.OnStatusReadAsync(status, ct);
    }

    internal static HashSet<string> DirtySubmodules(StatusSnapshot s) =>
        s.Unstaged.Concat(s.Staged).Where(e => e.IsSubmodule).Select(e => e.Path).ToHashSet();

    static bool SameRefs(RefSnapshot a, RefSnapshot b) =>
        a.HeadSha == b.HeadSha && a.ActiveBranch == b.ActiveBranch && a.Refs.SequenceEqual(b.Refs);

    static bool SameStatus(StatusSnapshot a, StatusSnapshot b) =>
        a.Unstaged.SequenceEqual(b.Unstaged) && a.Staged.SequenceEqual(b.Staged);

    /// <summary>Started while the tab is active; replaceable parts for tests.</summary>
    internal WorkTreeWatcher WorkTreeWatcher { get; }

    /// <summary>A watcher event (any thread): a working-tree path, or null = refresh without asking check-ignore.</summary>
    void QueueWatchEvent(string? path)
    {
        lock (_pendingLock)
        {
            if (path is null) _pendingAll = true;
            else _pending.Add(path);
            if (_posted) return;
            _posted = true;
        }
        _dispatcher.Post(TakePending);
    }

    /// <summary>UI thread: moves what the watcher threads collected into the batch and starts the 150 ms window.</summary>
    void TakePending()
    {
        HashSet<string> paths;
        bool all;
        lock (_pendingLock)
        {
            (paths, all) = (_pending, _pendingAll);
            (_pending, _pendingAll, _posted) = ([], false, false);
        }
        if (_disposed || !IsActive) return; // an inactive tab refreshes when it is selected again
        if (_watchPaused > 0)
        {
            // Dropped while a job runs; kept in case its refresh does not cover them.
            _droppedPaths.UnionWith(paths);
            _droppedAll |= all;
            return;
        }
        _changedPaths.UnionWith(paths);
        _refreshAll |= all;
        if (!_debounce.IsEnabled && !_watchBusy) _debounce.Start(); // a running batch starts the window when it ends
    }

    /// <summary>
    /// A job's watcher pause ends. When no job runs any more, the batches dropped meanwhile that no job refresh
    /// covered become the next batch: a background job refreshes only after a success, and changes can come in while
    /// a job's refresh runs.
    /// </summary>
    void EndWatchPause()
    {
        if (--_watchPaused > 0 || (_droppedPaths.Count == 0 && !_droppedAll)) return;
        if (!_disposed && IsActive)
        {
            _changedPaths.UnionWith(_droppedPaths);
            _refreshAll |= _droppedAll;
            if (!_debounce.IsEnabled && !_watchBusy) _debounce.Start();
        }
        _droppedPaths.Clear();
        _droppedAll = false;
    }

    /// <summary>
    /// The end of a watcher batch: working-tree paths that git ignores cause nothing; otherwise one
    /// refresh, lock-free (git must not take index.lock or rewrite the index for it). Coalesced: one batch per tick; a
    /// running refresh is waited for, not cancelled and restarted; what arrives meanwhile is the next batch, 150 ms
    /// after this one ends. So at most one check-ignore or refresh per (150 ms + its run time). Leaving the tab drops the
    /// batch; a job start (also during the check-ignore or the wait) keeps it for <see cref="EndWatchPause"/>.
    /// Limitation: always the full standard refresh, no per-path status yet.
    /// </summary>
    async Task RefreshForWatchersAsync()
    {
        if (_watchBusy) return; // the running batch restarts the window when it ends
        _watchBusy = true;
        try
        {
            if (_disposed || !IsActive || _watchPaused > 0) return;
            bool all = _refreshAll;
            var paths = _changedPaths.ToList();
            _refreshAll = false;
            _changedPaths.Clear();
            if (!all && !await WorkTreeWatcher.AnyNotIgnoredAsync(paths)) return;
            while (!Refreshing.IsCompleted) await Refreshing;
            if (_disposed || !IsActive || _watchPaused > 0)
            {
                // A job started meanwhile: back into the batch, which finally keeps aside for EndWatchPause (the job may
                // refresh nothing, e.g. a failed background fetch).
                _changedPaths.UnionWith(paths);
                _refreshAll |= all;
                return;
            }
            _lockFreeStatus = true;
            await RefreshAsync();
        }
        catch (Exception e)
        {
            Trace.WriteLine(e); // a watcher batch must never take the app down
        }
        finally
        {
            _watchBusy = false;
            if (_disposed || !IsActive || _watchPaused > 0)
            {
                if (_watchPaused > 0 && !_disposed && IsActive)
                {
                    _droppedPaths.UnionWith(_changedPaths); // until the job's refresh covers them
                    _droppedAll |= _refreshAll;
                }
                _changedPaths.Clear(); // an inactive tab refreshes when selected
                _refreshAll = false;
            }
            else if ((_refreshAll || _changedPaths.Count > 0) && !_debounce.IsEnabled) _debounce.Start();
        }
    }

    /// <summary>
    /// Watched: HEAD, index, refs/**, packed-refs, logs/refs/stash and info/exclude (an ignore file),
    /// lock files ignored; the working tree is <see cref="WorkTreeWatcher"/>.
    /// Limitation: no config / operation-marker events; window activation and F5 cover them.
    /// </summary>
    void Watch()
    {
        var info = Repo.Info;
        Add(info.GitDir, false, n => n is "HEAD" or "index" or "packed-refs");
        if (info.CommonDir != info.GitDir) Add(info.CommonDir, false, n => n == "packed-refs");
        Add(System.IO.Path.Combine(info.CommonDir, "refs"), true, n => !n.EndsWith(".lock", StringComparison.Ordinal));
        Add(System.IO.Path.Combine(info.CommonDir, "logs", "refs"), false, n => n == "stash");
        Add(System.IO.Path.Combine(info.CommonDir, "info"), false, n => n == "exclude");

        void Add(string dir, bool recursive, Func<string, bool> accept)
        {
            if (!Directory.Exists(dir)) return;
            var w = new FileSystemWatcher(dir)
            {
                IncludeSubdirectories = recursive,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
            };
            void OnEvent(string? path)
            {
                if (accept(System.IO.Path.GetFileName(path ?? ""))) QueueWatchEvent(null);
            }
            w.Changed += (_, e) => OnEvent(e.Name);
            w.Created += (_, e) => OnEvent(e.Name);
            w.Deleted += (_, e) => OnEvent(e.Name);
            w.Renamed += (_, e) => OnEvent(e.Name); // git writes x.lock and renames it to x
            _watchers.Add(w);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        LocalChanges.CloseChecks(tabClosed: true);
        _refreshCts?.Cancel();
        CommitList.Cancel(); // stops the commit walk and its git process
        _debounce.Stop();
        foreach (var w in _watchers) w.Dispose();
        WorkTreeWatcher.Dispose();
    }
}

/// <summary>A remote with its matched account. Url null = not a
/// host URL (local path, file URL): a custom remote.</summary>
public sealed record HostedRemote(string Name, RemoteUrl? Url, Account? Account)
{
    /// <summary>The account's type when one matched, else the host-name type (icon, wording). Computed once: the
    /// commit list looks it up while drawing.</summary>
    public HostType Type { get; } = Url?.TypeFor(Account) ?? HostType.Custom;

    /// <summary>"View on &lt;service&gt;" name.</summary>
    public string? ServiceName => HostTypes.DisplayName(Type);

    /// <summary>Repository web page; null = no "View on" and no pull requests.</summary>
    public string? RepositoryPage => Url?.RepositoryPage(Account);

    /// <summary>GitLab says "Merge Request".</summary>
    public bool IsMergeRequest => Type is HostType.GitLab or HostType.GitLabServer;

    /// <summary>"Pull Request" or "Merge Request".</summary>
    public string RequestName => IsMergeRequest ? "Merge Request" : "Pull Request";
}
