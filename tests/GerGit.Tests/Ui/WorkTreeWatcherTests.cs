using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using GerGit.Core;
using GerGit.Core.Git;
using GerGit.ViewModels;

namespace GerGit.Tests.Ui;

/// <summary>Working-tree file changed: the live working-tree watcher of the active tab.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class WorkTreeWatcherTests : IDisposable
{
    readonly TempRepo _repo = new();

    public WorkTreeWatcherTests()
    {
        _repo.Commit("init", 1, ("a.txt", "a\n"), ("b.txt", "b\n"), (".gitignore", "node_modules/\n*.log\n"));
        _repo.Write("node_modules/pkg/index.js", "x"); // exists before the watcher starts
        // The developer's global ignore rules (core.excludesFile, ~/.config/git/ignore) must not change check-ignore.
        _repo.Git("config", "core.excludesFile", "/dev/null");
    }

    public void Dispose() => _repo.Dispose();

    /// <summary>An active tab, loaded and quiet (the first status read may rewrite the index: one more refresh).</summary>
    async Task<RepoTabViewModel> OpenAsync(Func<string, FileSystemWatcher>? create = null)
    {
        var tab = new RepoTabViewModel(await GitRepository.OpenAsync(_repo.Path));
        tab.ShowError = (title, text) => throw new Xunit.Sdk.XunitException($"{title}: {text}");
        if (create is not null) tab.WorkTreeWatcher.Create = create;
        tab.IsActive = true;
        await tab.RefreshAsync();
        await QuietAsync(tab);
        return tab;
    }

    static async Task QuietAsync(RepoTabViewModel tab)
    {
        for (int i = 0; i < 30; i++)
        {
            var r = tab.Refreshing;
            await r;
            await Task.Delay(400);
            Dispatcher.UIThread.RunJobs();
            if (r == tab.Refreshing && r.IsCompleted) return;
        }
        Assert.Fail("never quiet");
    }

    static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && sw.ElapsedMilliseconds < timeoutMs) await Task.Delay(20);
        Assert.True(condition(), "condition not met within " + timeoutMs + " ms");
    }

    /// <summary>Counts the refreshes started while it runs (polls <see cref="RepoTabViewModel.Refreshing"/>).</summary>
    sealed class RefreshCounter
    {
        public int Count;
        bool _stop;
        readonly Task _loop;

        public RefreshCounter(RepoTabViewModel tab) => _loop = Loop(tab);

        async Task Loop(RepoTabViewModel tab)
        {
            var last = tab.Refreshing;
            while (!_stop)
            {
                if (tab.Refreshing != last) { Count++; last = tab.Refreshing; }
                await Task.Delay(5);
            }
        }

        public async Task<int> StopAsync()
        {
            _stop = true;
            await _loop;
            return Count;
        }
    }

    static string[] Unstaged(RepoTabViewModel tab) => tab.Status.Unstaged.Select(e => e.Path).Order(StringComparer.Ordinal).ToArray();

    /// <summary>A watcher whose dispose and raw events the test can see, and which can report an error.</summary>
    sealed class ProbeWatcher(string path) : FileSystemWatcher(path)
    {
        public int RawEvents;
        public volatile bool WasDisposed;

        public void Probe()
        {
            Created += (_, _) => Interlocked.Increment(ref RawEvents);
            Changed += (_, _) => Interlocked.Increment(ref RawEvents);
        }

        public void RaiseError(Exception e) => OnError(new ErrorEventArgs(e));

        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
        }
    }

    [AvaloniaFact]
    public async Task Editing_creating_and_removing_a_file_updates_local_changes_by_itself()
    {
        using var tab = await OpenAsync();
        Assert.Empty(Unstaged(tab));
        Assert.True(tab.WorkTreeWatcher.IsRunning);

        _repo.Write("a.txt", "edited\n");
        await WaitUntilAsync(() => Unstaged(tab).SequenceEqual(["a.txt"]));

        _repo.Write("dir/new.txt", "new\n"); // also in a folder created after the watcher started
        await WaitUntilAsync(() => Unstaged(tab).SequenceEqual(["a.txt", "dir/new.txt"]));

        File.Delete(Path.Combine(_repo.Path, "b.txt"));
        await WaitUntilAsync(() => Unstaged(tab).SequenceEqual(["a.txt", "b.txt", "dir/new.txt"]));
        Assert.Equal(ChangeKind.Deleted, tab.Status.Unstaged.Single(e => e.Path == "b.txt").Kind);
        Assert.Equal("Local Changes (3)", tab.LocalChangesText);
    }

    [AvaloniaFact]
    public async Task Changes_in_ignored_paths_and_inside_git_cause_no_refresh()
    {
        using var tab = await OpenAsync();
        var counter = new RefreshCounter(tab);
        _repo.Write("node_modules/pkg/index.js", "changed");
        _repo.Write("node_modules/other/new.js", "new"); // a new ignored folder
        _repo.Write("debug.log", "log");
        File.WriteAllText(Path.Combine(_repo.Path, ".git", "description"), "not a work-tree change");
        await Task.Delay(1000);
        Assert.Equal(0, await counter.StopAsync());

        // The watcher is live: a file that is not ignored refreshes.
        _repo.Write("visible.txt", "v");
        await WaitUntilAsync(() => Unstaged(tab).SequenceEqual(["visible.txt"]));
    }

    [AvaloniaFact]
    public async Task No_refresh_storm_while_a_gergit_job_writes_the_working_tree()
    {
        using var tab = await OpenAsync();
        var counter = new RefreshCounter(tab);
        int checkIgnores = 0;
        tab.WorkTreeWatcher.CheckIgnoreProbe = start =>
        {
            if (start) checkIgnores++;
            return Task.CompletedTask;
        };
        int duringWork = -1;
        await tab.RunJobAsync("Write files", async _ =>
        {
            for (int i = 0; i < 60; i++)
            {
                _repo.Write($"gen/f{i}.txt", "x");
                await Task.Delay(20);
            }
            duringWork = counter.Count;
            return null;
        }, hidden: true);
        Assert.Equal(0, duringWork); // paused: no refresh while the job runs
        Assert.Equal(60, tab.Status.Unstaged.Count); // the job's own refresh
        await Task.Delay(1000);
        // The job's refresh, at most one more for git's index rewrite; the 60 writes start none: its refresh covered
        // them, so they are not handed on after the job either.
        Assert.InRange(await counter.StopAsync(), 1, 2);
        Assert.Equal(0, checkIgnores);
    }

    [AvaloniaFact]
    public async Task A_closed_tab_disposes_its_watcher_and_gets_no_events()
    {
        ProbeWatcher? probe = null;
        var tab = await OpenAsync(root => probe = new ProbeWatcher(root));
        probe!.Probe();
        _repo.Write("a.txt", "edited\n");
        await WaitUntilAsync(() => probe.RawEvents > 0 && Unstaged(tab).Length == 1);

        tab.IsActive = false; // as the main window does on close
        tab.Dispose();
        Assert.False(tab.WorkTreeWatcher.IsRunning);
        await WaitUntilAsync(() => probe.WasDisposed); // released on the thread pool
        var events = probe.RawEvents;
        var refreshing = tab.Refreshing;
        _repo.Write("b.txt", "edited\n");
        _repo.Write("c.txt", "new\n");
        await Task.Delay(500);
        Assert.Equal(events, probe.RawEvents);
        Assert.Same(refreshing, tab.Refreshing);
    }

    [AvaloniaFact]
    public async Task An_inactive_tab_releases_its_watcher_and_takes_a_new_one_when_selected()
    {
        var probes = new List<ProbeWatcher>();
        using var tab = await OpenAsync(root => { var p = new ProbeWatcher(root); probes.Add(p); return p; });
        tab.IsActive = false;
        await WaitUntilAsync(() => Assert.Single(probes).WasDisposed); // released on the thread pool
        var refreshing = tab.Refreshing;
        _repo.Write("a.txt", "edited\n");
        await Task.Delay(500);
        Assert.Same(refreshing, tab.Refreshing); // an inactive tab refreshes when it is selected again

        tab.IsActive = true;
        Assert.Equal(2, probes.Count);
        _repo.Write("b.txt", "edited\n");
        await WaitUntilAsync(() => Unstaged(tab).SequenceEqual(["a.txt", "b.txt"]));
    }

    [AvaloniaFact]
    public async Task Without_a_working_tree_watcher_the_tab_keeps_working_and_refreshes_on_activation()
    {
        // : the OS watch limit is reached -> log it and continue without watching.
        int created = 0;
        using var tab = await OpenAsync(_ =>
        {
            created++;
            throw new IOException("The configured user limit (8192) on the number of inotify watches has been reached.");
        });
        Assert.True(tab.IsLoaded);
        Assert.True(tab.WorkTreeWatcher.IsFailed);
        Assert.False(tab.WorkTreeWatcher.IsRunning);

        var counter = new RefreshCounter(tab);
        _repo.Write("a.txt", "edited\n");
        await Task.Delay(800);
        Assert.Equal(0, await counter.StopAsync());
        Assert.Empty(Unstaged(tab));

        // The .git watcher still works.
        _repo.Git("branch", "external");
        await WaitUntilAsync(() => tab.Refs.Refs.Any(r => r.FullName == "refs/heads/external"));

        // Selecting the tab again refreshes it; no new attempt, no spinning.
        tab.IsActive = false;
        tab.IsActive = true;
        await tab.RefreshAsync();
        Assert.Equal(["a.txt"], Unstaged(tab));
        Assert.Equal(1, created);
    }

    [AvaloniaFact]
    public async Task A_watcher_error_stops_working_tree_watching_and_an_overflow_refreshes()
    {
        ProbeWatcher? probe = null;
        using var tab = await OpenAsync(root => probe = new ProbeWatcher(root));

        // Lost events (inotify queue overflow): one refresh, the watcher stays.
        _repo.Write("debug.log", "ignored, but the overflow does not tell which paths changed");
        await QuietAsync(tab);
        var counter = new RefreshCounter(tab);
        probe!.RaiseError(new InternalBufferOverflowException());
        await WaitUntilAsync(() => counter.Count > 0);
        await counter.StopAsync();
        Assert.True(tab.WorkTreeWatcher.IsRunning);
        await QuietAsync(tab);

        // A folder that cannot be watched (no permission): logged, the rest stays watched.
        probe.RaiseError(new UnauthorizedAccessException("Access to the path 'x' is denied."));
        Assert.True(tab.WorkTreeWatcher.IsRunning);
        Assert.False(tab.WorkTreeWatcher.IsFailed);

        // The watch limit (ENOSPC): working-tree watching ends.
        probe.RaiseError(new IOException("The configured user limit (8192) on the number of inotify watches has been reached.", 28));
        await WaitUntilAsync(() => probe.WasDisposed);
        Assert.True(tab.WorkTreeWatcher.IsFailed);
        Assert.False(tab.WorkTreeWatcher.IsRunning);
        counter = new RefreshCounter(tab);
        _repo.Write("a.txt", "edited\n");
        await Task.Delay(800);
        Assert.Equal(0, await counter.StopAsync());
    }

    [Fact]
    public async Task Check_ignore_answers_for_a_whole_batch()
    {
        using var sub = new TempRepo();
        sub.Commit("s", 1, ("s.txt", "s"));
        _repo.Git("-c", "protocol.file.allow=always", "submodule", "add", "-q", sub.Path, "sub");
        _repo.Write("kept.log", "tracked although ignored");
        _repo.Git("add", "-f", "kept.log");
        var w = new WorkTreeWatcher(_repo.Path);

        Assert.False(await w.AnyNotIgnoredAsync(["node_modules/pkg/index.js", "node_modules", "x.log", "node_modules/gone/f.js"]));
        Assert.True(await w.AnyNotIgnoredAsync(["x.log", "src/new.txt"]));
        Assert.True(await w.AnyNotIgnoredAsync(["kept.log"])); // tracked files are never ignored
        Assert.True(await w.AnyNotIgnoredAsync(["x.log", "sub/s.txt"])); // git fails inside a submodule: refresh to be safe
        Assert.False(await w.AnyNotIgnoredAsync([]));
    }

    [AvaloniaFact]
    public async Task A_watcher_refresh_does_not_write_the_index()
    {
        _repo.Write("a.txt", "edited\n");
        using var tab = await OpenAsync();
        tab.LocalChanges.UnstagedSelection.Select(0); // its diff reloads after every status read
        await tab.LocalChanges.Diff.Loading;
        await QuietAsync(tab);
        var index = Path.Combine(_repo.Path, ".git", "index");
        var before = File.ReadAllBytes(index);
        var counter = new RefreshCounter(tab);
        // Only the time stamp changes: a status that may write would store the new stat data in the index.
        File.SetLastWriteTimeUtc(Path.Combine(_repo.Path, "b.txt"), DateTime.UtcNow.AddMinutes(-5));
        await WaitUntilAsync(() => counter.Count > 0);
        await QuietAsync(tab);
        await tab.LocalChanges.Diff.Loading;
        Assert.Equal(1, await counter.StopAsync()); // no second refresh from an index event
        Assert.Equal(before, File.ReadAllBytes(index));
    }

    [Fact]
    public async Task The_working_diffs_never_write_the_index()
    {
        var repo = await GitRepository.OpenAsync(_repo.Path);
        var index = Path.Combine(_repo.Path, ".git", "index");
        // Stat-only changes: `git diff` would store the new stat data (and take index.lock) unless told not to.
        File.SetLastWriteTimeUtc(Path.Combine(_repo.Path, "a.txt"), DateTime.UtcNow.AddMinutes(-7));
        File.SetLastWriteTimeUtc(Path.Combine(_repo.Path, "b.txt"), DateTime.UtcNow.AddMinutes(-7));
        var before = File.ReadAllBytes(index);
        var d = await repo.ReadFileDiffAsync(new DiffRequest(null, "", "b.txt", null, Working: WorkingDiff.Unstaged));
        Assert.Empty(d.Hunks); // a stat-only change still shows no diff
        Assert.Equal(before, File.ReadAllBytes(index));
    }

    [AvaloniaFact]
    public async Task A_change_during_a_failed_background_job_still_shows_up_afterwards()
    {
        using var tab = await OpenAsync();
        var job = await tab.RunJobAsync("Fetch 'origin'", async _ =>
        {
            _repo.Write("a.txt", "edited during the job\n");
            await Task.Delay(400); // the watcher batch comes in while the job runs
            return new GitResult(1, "", "fatal: Could not read from remote repository.");
        }, background: true);
        Assert.Equal(JobOutcome.Failed, job.Outcome); // a failed background job refreshes nothing itself
        await WaitUntilAsync(() => Unstaged(tab).SequenceEqual(["a.txt"]));
    }

    [AvaloniaFact]
    public async Task A_change_during_a_running_refresh_leads_to_exactly_one_more()
    {
        // A slow fsmonitor hook makes every status take 2 s more (it fails, so git scans normally afterwards): the
        // 300 ms below land inside the first refresh even under load.
        var hook = Path.Combine(_repo.Path, ".git", "slow-fsmonitor");
        File.WriteAllText(hook, "#!/bin/sh\nsleep 2\nexit 1\n");
        File.SetUnixFileMode(hook, (UnixFileMode)0b111_101_101);
        _repo.Git("config", "core.fsmonitor", hook);
        using var tab = await OpenAsync();
        var counter = new RefreshCounter(tab);
        _repo.Write("a.txt", "edited\n");
        await WaitUntilAsync(() => counter.Count == 1);
        var first = tab.Refreshing;
        _repo.Write("b.txt", "edited\n");
        await Task.Delay(300);
        Assert.False(first.IsCompleted); // the second change came while the first refresh ran
        Assert.Same(first, tab.Refreshing); // it is not cancelled and restarted
        await WaitUntilAsync(() => Unstaged(tab).SequenceEqual(["a.txt", "b.txt"]), 20000);
        // One more refresh for the second batch (with this slow git, later than QuietAsync's window).
        await WaitUntilAsync(() => counter.Count == 2, 20000);
        await tab.Refreshing;
        await Task.Delay(2000);
        Assert.Equal(2, await counter.StopAsync());
    }

    [AvaloniaFact]
    public async Task A_steady_stream_of_ignored_writes_runs_few_check_ignores_and_no_refresh()
    {
        using var tab = await OpenAsync();
        var counter = new RefreshCounter(tab);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var runs = new List<(long Start, long End)>();
        tab.WorkTreeWatcher.CheckIgnoreProbe = start =>
        {
            if (!start)
            {
                runs[^1] = (runs[^1].Start, sw.ElapsedMilliseconds);
                return Task.CompletedTask;
            }
            runs.Add((sw.ElapsedMilliseconds, -1));
            return Task.Delay(100); // a slow git: events keep coming while it runs
        };
        for (int i = 0; sw.ElapsedMilliseconds < 1000; i++)
        {
            _repo.Write($"node_modules/gen/f{i % 50}.js", i.ToString());
            await Task.Delay(5);
        }
        await Task.Delay(600);
        Assert.Equal(0, await counter.StopAsync());
        // One batch per (150 ms + check-ignore): the next window starts when the running batch ends.
        Assert.InRange(runs.Count, 1, 7);
        for (int i = 1; i < runs.Count; i++)
            Assert.True(runs[i].Start - runs[i - 1].End >= 130, $"check-ignore {i} started {runs[i].Start - runs[i - 1].End} ms after the previous one");
    }

    [AvaloniaFact]
    public async Task Editing_info_exclude_refreshes()
    {
        _repo.Write("x.dat", "x");
        using var tab = await OpenAsync();
        Assert.Equal(["x.dat"], Unstaged(tab));
        File.AppendAllText(Path.Combine(_repo.Path, ".git", "info", "exclude"), "\nx.dat\n");
        await WaitUntilAsync(() => Unstaged(tab).Length == 0);
    }

    [AvaloniaFact]
    public async Task Symlinked_folders_are_not_followed()
    {
        var outside = Path.Combine(Path.GetTempPath(), "gergit-outside-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Path.Combine(outside, "deep"));
        try
        {
            File.CreateSymbolicLink(Path.Combine(_repo.Path, "outside"), outside);
            File.CreateSymbolicLink(Path.Combine(_repo.Path, "loop"), "."); // a loop back to the work tree
            _repo.Commit("links", 2);
            using var tab = await OpenAsync();
            _repo.Write("started.txt", "s");
            await WaitUntilAsync(() => Unstaged(tab).SequenceEqual(["started.txt"]));
            Assert.True(tab.WorkTreeWatcher.IsRunning);
            Assert.False(tab.WorkTreeWatcher.IsFailed);
            await QuietAsync(tab);

            var counter = new RefreshCounter(tab);
            File.WriteAllText(Path.Combine(outside, "x.txt"), "x");
            File.WriteAllText(Path.Combine(outside, "deep", "y.txt"), "y");
            await Task.Delay(800);
            Assert.Equal(0, await counter.StopAsync());
        }
        finally
        {
            Directory.Delete(outside, true);
        }
    }

    [AvaloniaFact]
    public async Task An_unreadable_folder_is_skipped_and_the_rest_stays_watched()
    {
        if (Environment.UserName == "root") return; // root reads every folder
        var locked = Path.Combine(_repo.Path, "node_modules", "locked");
        Directory.CreateDirectory(Path.Combine(locked, "inner"));
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            int errors = 0;
            using var tab = await OpenAsync(root =>
            {
                var w = new FileSystemWatcher(root);
                w.Error += (_, _) => Interlocked.Increment(ref errors);
                return w;
            });
            _repo.Write("a.txt", "edited\n");
            await WaitUntilAsync(() => Unstaged(tab).SequenceEqual(["a.txt"]));
            Assert.True(errors > 0); // .NET reported the folder it could not watch
            Assert.True(tab.WorkTreeWatcher.IsRunning);
            Assert.False(tab.WorkTreeWatcher.IsFailed);
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [AvaloniaFact]
    public async Task A_job_starting_during_the_check_ignore_does_not_lose_the_batch()
    {
        using var tab = await OpenAsync();
        var inCheck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var checkDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tab.WorkTreeWatcher.CheckIgnoreProbe = start =>
        {
            if (!start)
            {
                checkDone.TrySetResult();
                return Task.CompletedTask;
            }
            inCheck.TrySetResult();
            return release.Task; // held until the job runs
        };
        _repo.Write("a.txt", "edited\n");
        await inCheck.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var job = await tab.RunJobAsync("Fetch 'origin'", async _ =>
        {
            release.SetResult(); // the batch goes on while the job runs and finds the watcher paused
            await checkDone.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(200);
            return new GitResult(1, "", "fatal: Could not read from remote repository.");
        }, background: true);
        Assert.Equal(JobOutcome.Failed, job.Outcome); // refreshes nothing itself
        await WaitUntilAsync(() => Unstaged(tab).SequenceEqual(["a.txt"]));
    }
}
