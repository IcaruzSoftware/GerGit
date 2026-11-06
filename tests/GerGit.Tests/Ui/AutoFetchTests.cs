using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GerGit.Core;
using GerGit.Core.Git;
using GerGit.Tests.Core;
using GerGit.ViewModels;
using GerGit.Views;

namespace GerGit.Tests.Ui;

/// <summary>Automatic background fetch against a local bare remote, through the app's timer tick and the
/// sidebar menu. In the non-parallel "Askpass" collection: the askpass endpoint and the settings are process-wide.</summary>
[Collection("Askpass")]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class AutoFetchTests : IDisposable
{
    readonly RemoteSetup _s = new();
    readonly MainWindowViewModel _main = new();
    readonly List<string> _errors = [];
    RepoTabViewModel _tab = null!;

    async Task OpenAsync(string path)
    {
        Assert.Null(await _main.OpenAsync(path));
        _tab = _main.SelectedTab!;
        _tab.ShowError = (title, text) => { _errors.Add($"{title}: {text}"); return Task.CompletedTask; };
        _tab.ShowGitError = (r, _) => { _errors.Add(r.ReadableOutput); return Task.CompletedTask; };
        _tab.ShowDialog = vm => { _errors.Add("dialog " + vm.Title); return Task.FromResult(false); };
        await _tab.Refreshing;
    }

    /// <summary>Waits until no watcher-triggered refresh follows (the first status read may rewrite the index).</summary>
    static async Task SettleAsync(RepoTabViewModel tab)
    {
        for (int i = 0; i < 20; i++)
        {
            var r = tab.Refreshing;
            await r;
            await Task.Delay(300);
            Dispatcher.UIThread.RunJobs();
            if (r == tab.Refreshing) return;
        }
    }

    string? RemoteSha(string fullRef) => _tab.Refs.Refs.FirstOrDefault(r => r.FullName == fullRef)?.Sha;

    public void Dispose()
    {
        while (_main.Tabs.Count > 0) _main.CloseTab(_main.Tabs[0]);
        Settings.Set<bool?>(RepoTabViewModel.ShowWorktreesKey, null); // set by opening a repository with worktrees
        _s.Dispose();
    }

    [AvaloniaFact]
    public async Task Tick_fetches_a_pushed_commit_as_background_job_and_refreshes_the_active_tab()
    {
        await OpenAsync(_s.Local.Path);
        var sha = _s.OtherPush("second", 2);
        Assert.NotEqual(sha, RemoteSha("refs/remotes/origin/main"));

        // The first tick fetches even while the window is focused.
        var (next, round) = _main.AutoFetch.Tick(windowFocused: true);
        Assert.Equal(AutoFetch.Interval, next);
        await round;
        Assert.Equal(sha, RemoteSha("refs/remotes/origin/main"));
        var job = Assert.Single(_tab.Jobs.History);
        Assert.Equal("Fetch 'origin'", job.Name);
        Assert.True(job.Background);
        Assert.Equal((JobOutcome.Succeeded, "Fetched 1 branch"), (job.Outcome, job.Message));

        // Later ticks: a focused window postpones once by a minute, the tick after that fetches regardless.
        (next, round) = _main.AutoFetch.Tick(windowFocused: true);
        Assert.Equal(AutoFetch.Postpone, next);
        await round;
        Assert.Single(_tab.Jobs.History);
        (next, round) = _main.AutoFetch.Tick(windowFocused: true);
        Assert.Equal(AutoFetch.Interval, next);
        await round;
        Assert.Equal("Already up to date", _tab.Jobs.History[0].Message);
        (next, _) = _main.AutoFetch.Tick(windowFocused: false);
        Assert.Equal(AutoFetch.Interval, next);
        Assert.Empty(_errors);
    }

    [AvaloniaFact]
    public async Task Opted_out_remote_is_skipped_and_the_sidebar_item_toggles_the_config()
    {
        _s.Local.Git("remote", "add", "up2", _s.Bare.Path);
        _s.Local.Git("fetch", "-q", "up2");
        await OpenAsync(_s.Local.Path);
        var old = RemoteSha("refs/remotes/up2/main");

        async Task<MenuEntry> Item() => (await Menus.Remote(_tab, "up2", _ => Task.CompletedTask))
            .Single(e => e.Header == "Fetch 'up2' Automatically");
        var item = await Item();
        Assert.True(item.IsChecked);
        await item.Invoke();
        Assert.Equal("true", _s.Local.Git("config", "remote.up2.disableimplicitfetch"));
        Assert.False((await Item()).IsChecked);

        var sha = _s.OtherPush("second", 2);
        await _main.AutoFetch.RunAsync();
        Assert.Equal(["Fetch 'origin'"], _tab.Jobs.History.Select(j => j.Name));
        Assert.Equal(sha, RemoteSha("refs/remotes/origin/main"));
        Assert.Equal(old, RemoteSha("refs/remotes/up2/main"));

        await (await Item()).Invoke();
        Assert.Equal("false", _s.Local.Git("config", "remote.up2.disableimplicitfetch"));
        await _main.AutoFetch.RunAsync();
        Assert.Equal(sha, RemoteSha("refs/remotes/up2/main"));
        Assert.Empty(_errors);
    }

    [AvaloniaFact]
    public async Task Setting_off_fetches_nothing_and_a_running_job_of_the_same_name_skips_the_repository()
    {
        await OpenAsync(_s.Local.Path);
        var old = RemoteSha("refs/remotes/origin/main");
        _s.OtherPush("second", 2);
        Settings.Set(AutoFetch.EnabledKey, false);
        try
        {
            var (next, round) = _main.AutoFetch.Tick(windowFocused: false);
            Assert.Equal(AutoFetch.Interval, next); // the timer keeps running
            await round;
        }
        finally
        {
            Settings.Set(AutoFetch.EnabledKey, true);
        }
        Assert.Empty(_tab.Jobs.History);
        Assert.Equal(old, RemoteSha("refs/remotes/origin/main"));

        var gate = new TaskCompletionSource<GitResult?>();
        var user = _tab.RunJobAsync("Fetch 'origin'", _ => gate.Task);
        await _main.AutoFetch.RunAsync();
        Assert.Single(_tab.Jobs.History);
        gate.SetResult(null);
        await user;
    }

    [AvaloniaFact]
    public async Task Remote_that_needs_credentials_is_cancelled_quietly_without_any_dialog()
    {
        using var server = new GitHttpServer("alice", "tok-5e1d");
        server.AddRepo("repo", _s.Bare.Path);
        _s.Local.Git("remote", "add", "web", server.Url + "repo.git");
        _s.Local.Git("config", "remote.origin.disableimplicitfetch", "true");
        int prompts = 0;
        var (prompt, lookup) = (Askpass.PromptCredentials, Askpass.LookupCredential);
        Askpass.Start();
        Askpass.PromptCredentials = _ => { Interlocked.Increment(ref prompts); return Task.FromResult<string?>("x"); };
        Askpass.LookupCredential = _ => Task.FromResult<CredentialAnswer?>(null); // no account
        GitRunner.CredentialHelperEnabled = true;
        try
        {
            await OpenAsync(_s.Local.Path);
            await SettleAsync(_tab);
            var refresh = _tab.Refreshing;
            await _main.AutoFetch.RunAsync();
            Assert.Same(refresh, _tab.Refreshing); // only a successful fetch refreshes
        }
        finally
        {
            GitRunner.CredentialHelperEnabled = false;
            (Askpass.PromptCredentials, Askpass.LookupCredential) = (prompt, lookup);
            Askpass.Stop();
        }
        var job = Assert.Single(_tab.Jobs.History);
        Assert.Equal("Fetch 'web'", job.Name);
        Assert.Equal(JobOutcome.Canceled, job.Outcome);
        Assert.Contains("Cancel background fetch...", job.Output);
        Assert.True(server.Rejected > 0);
        Assert.Equal(0, prompts);
        Assert.Empty(_errors);
    }

    [AvaloniaFact]
    public async Task Failed_fetch_does_not_refresh_and_a_success_in_an_inactive_tab_waits_until_it_is_selected()
    {
        // All git changes first: no watcher refresh can run during the round.
        _s.Local.Git("remote", "add", "bad", "/nonexistent/gergit-remote");
        _s.Local.Git("config", "remote.origin.disableimplicitfetch", "true");
        _s.Local.Commit("second", 2, ("a.txt", "b\n"));
        _s.Local.Git("push", "-q", "origin", "main");
        var sha = _s.BareSha("main");
        await OpenAsync(_s.Other.Path); // Other: a clone of the bare remote, opened first so it ends up inactive
        var other = _tab;
        await OpenAsync(_s.Local.Path);
        Assert.False(other.IsActive);
        await SettleAsync(_tab);
        var (localRefresh, otherRefresh) = (_tab.Refreshing, other.Refreshing);
        var oldOther = other.Refs.Refs.Single(r => r.FullName == "refs/remotes/origin/main").Sha;

        await _main.AutoFetch.RunAsync();
        var failed = Assert.Single(_tab.Jobs.History);
        Assert.Equal(("Fetch 'bad'", JobOutcome.Failed), (failed.Name, failed.Outcome));
        Assert.Same(localRefresh, _tab.Refreshing); // failed: no refresh, no window
        var fetched = Assert.Single(other.Jobs.History);
        Assert.Equal(JobOutcome.Succeeded, fetched.Outcome);
        Assert.Same(otherRefresh, other.Refreshing); // inactive: refreshed only when selected
        Assert.Equal(oldOther, other.Refs.Refs.Single(r => r.FullName == "refs/remotes/origin/main").Sha);

        _main.SelectedTab = other;
        await other.Refreshing;
        Assert.Equal(sha, other.Refs.Refs.Single(r => r.FullName == "refs/remotes/origin/main").Sha);
        Assert.Empty(_errors);
    }

    [AvaloniaFact]
    public async Task Worktree_tabs_of_one_repository_fetch_once_in_the_active_tab()
    {
        var wt = Path.Combine(Path.GetTempPath(), "gergit-test-wt-" + Guid.NewGuid().ToString("N")[..12]);
        _s.Local.Git("worktree", "add", "-q", "-b", "wt", wt);
        try
        {
            await OpenAsync(_s.Local.Path);
            var mainTab = _tab;
            await OpenAsync(wt);
            Assert.True(_tab.IsActive);
            var sha = _s.OtherPush("second", 2);

            await _main.AutoFetch.RunAsync();
            Assert.Empty(mainTab.Jobs.History);
            Assert.Equal(["Fetch 'origin'"], _tab.Jobs.History.Select(j => j.Name));
            Assert.Equal(sha, RemoteSha("refs/remotes/origin/main"));

            // A user fetch running in the main tab skips the round for the whole repository.
            var gate = new TaskCompletionSource<GitResult?>();
            var user = mainTab.RunJobAsync("Fetch 'origin'", _ => gate.Task);
            await _main.AutoFetch.RunAsync();
            Assert.Single(_tab.Jobs.History);
            gate.SetResult(null);
            await user;
            Assert.Empty(_errors);
        }
        finally
        {
            while (_main.Tabs.Count > 0) _main.CloseTab(_main.Tabs[0]);
            Directory.Delete(wt, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task Tab_closed_while_its_remotes_are_read_gets_no_job()
    {
        await OpenAsync(_s.Local.Path);
        _s.OtherPush("second", 2);
        var tab = _tab;
        var round = _main.AutoFetch.RunAsync(); // runs up to the first git read of the tab
        _main.CloseTab(tab);
        await round;
        Assert.Empty(tab.Jobs.History);
    }

    [AvaloniaFact]
    public async Task Background_jobs_are_listed_under_Background_and_show_in_the_status_box_only_after_500ms()
    {
        await OpenAsync(_s.Local.Path);
        var window = new MainWindow { DataContext = _main };
        window.Show();
        window.StatusTimer.Stop();
        try
        {
            await _tab.RunJobAsync("Create branch 'x'", async j => await GitRunner.RunJobAsync(j, _tab.Path, ["branch", "x"]));
            _s.OtherPush("second", 2);
            await _main.AutoFetch.RunAsync();

            _main.OpenActivity();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var act = _main.Activity;
            var strip = window.GetVisualDescendants().OfType<TabStrip>().Single(t => t.Name == "Filter");
            Assert.Equal(["All", "User", "Background"], strip.Items.OfType<TabStripItem>().Select(i => (string)i.Content!));
            Assert.Equal(["Fetch 'origin'", "Create branch 'x'"], act.Rows.Select(r => r.Name));

            strip.SelectedIndex = 2;
            Assert.Equal(2, act.FilterIndex);
            Assert.Equal("background", Settings.Get(ActivityViewModel.FilterKey, ""));
            Assert.Equal(["Fetch 'origin'"], act.Rows.Select(r => r.Name));
            Assert.Same(act.Rows[0], act.Selected); // changing the tab selects the first row
            act.FilterIndex = 1;
            Assert.Equal(["Create branch 'x'"], act.Rows.Select(r => r.Name));
            act.FilterIndex = 0;
            Assert.Equal(2, act.Rows.Count);
        }
        finally
        {
            Settings.Set(ActivityViewModel.FilterKey, "all");
            window.Close();
        }

        // Status box: a background job counts only after 500 ms of running and not once it has finished.
        var queue = new JobQueue();
        var bg = new Job("Fetch 'origin'") { Background = true };
        queue.Start(bg);
        Assert.Null(queue.Primary(bg.Started.AddMilliseconds(400)));
        Assert.Same(bg, queue.Primary(bg.Started.AddMilliseconds(600)));
        bg.Succeed("Fetched 1 branch");
        queue.End(bg);
        Assert.Null(queue.Primary(bg.Finished!.Value.AddMilliseconds(100)));
        Assert.Empty(_errors);
    }
}
