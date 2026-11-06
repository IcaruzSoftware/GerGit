using System.Diagnostics;
using Avalonia.Threading;
using GerGit.Core;
using GerGit.Core.Git;

namespace GerGit.ViewModels;

/// <summary>
/// Automatic background fetch: one timer for the whole app. Each round walks all open tabs and queues a
/// silent background `Fetch '&lt;remote&gt;'` job per remote that is not opted out (`remote.&lt;name&gt;.disableimplicitfetch`).
/// Tests drive <see cref="Tick"/> / <see cref="RunAsync"/> directly; only the desktop app calls <see cref="Start"/>.
/// Limitation: no `Automatic fetch` wrapper job in an app-wide queue (the status box and Activity Manager show the
/// active tab's queue only); the per-remote jobs carry the visible state.
/// </summary>
public sealed class AutoFetch(MainWindowViewModel main)
{
    /// <summary>Setting key of "Fetch remotes automatically" (Preferences > General), re-read at every
    /// round.</summary>
    public const string EnabledKey = "general.autoFetch";

    public static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan Postpone = TimeSpan.FromMinutes(1);
    static readonly TimeSpan RepoPause = TimeSpan.FromMilliseconds(200);

    bool _first = true, _postponed;

    /// <summary>Starts the timer: first tick <see cref="FirstDelay"/> after start.</summary>
    public void Start(Func<bool> windowFocused)
    {
        var timer = new DispatcherTimer { Interval = FirstDelay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            timer.Interval = Tick(windowFocused()).Next;
            timer.Start();
        };
        timer.Start();
    }

    /// <summary>One timer tick: the delay until the next tick and the round it started (not awaited by the timer).
    /// A tick other than the first that finds the main window focused is postponed once by <see cref="Postpone"/>.</summary>
    public (TimeSpan Next, Task Round) Tick(bool windowFocused)
    {
        bool first = _first;
        _first = false;
        if (!first && windowFocused && !_postponed)
        {
            _postponed = true;
            return (Postpone, Task.CompletedTask);
        }
        _postponed = false;
        return (Interval, RunAsync());
    }

    /// <summary>One round over all open tabs, sequentially with a 200 ms pause between them; completes when the queued
    /// fetch jobs have ended. Tabs that share a git common dir (a repository and its worktrees) share their remotes and
    /// refs, so only the first of them fetches; the active tab goes first, so the tab that refreshes is the one that
    /// fetched. A tab whose remotes cannot be read is skipped; a still running job of the same name (in any tab of that
    /// repository) skips the rest of its remotes.</summary>
    public async Task RunAsync()
    {
        if (!Settings.Get(EnabledKey, true)) return;
        List<Task<Job>> jobs = [];
        HashSet<string> done = [];
        foreach (var tab in main.Tabs.OrderBy(t => t != main.SelectedTab).ToList())
        {
            if (!main.Tabs.Contains(tab) || !done.Add(CommonDir(tab))) continue;
            if (done.Count > 1) await Task.Delay(RepoPause);
            try { await QueueFetchesAsync(tab, jobs); }
            catch (Exception e) { Trace.WriteLine(e); } // one tab's failure must not end the fire-and-forget round
        }
        await Task.WhenAll(jobs);
    }

    async Task QueueFetchesAsync(RepoTabViewModel tab, List<Task<Job>> jobs)
    {
        if (!main.Tabs.Contains(tab)) return; // closed during the pause
        IReadOnlyList<RemoteEntry> remotes;
        try { remotes = await tab.Repo.ReadRemotesAsync(); }
        catch (GitException) { return; }
        foreach (var remote in remotes)
        {
            if (!await tab.Repo.IsFetchAutomaticallyAsync(remote.Name)) continue;
            if (!main.Tabs.Contains(tab)) return; // closed during the reads
            var name = $"Fetch '{remote.Name}'";
            if (main.Tabs.Any(t => CommonDir(t) == CommonDir(tab) && t.Jobs.Running.Any(j => j.Name == name))) break;
            jobs.Add(tab.RemoteOps.BackgroundFetchAsync(remote.Name));
        }
    }

    static string CommonDir(RepoTabViewModel tab) => tab.Repo.Info.CommonDir.TrimEnd('/');
}
