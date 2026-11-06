using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GerGit.Controls;
using GerGit.Core;
using GerGit.Core.Git;
using GerGit.Tests.Core;
using GerGit.ViewModels;
using GerGit.ViewModels.Dialogs;
using GerGit.ViewModels.Dialogs.Remote;
using GerGit.Views.Dialogs;

namespace GerGit.Tests.Ui;

/// <summary>Through the entry points (RemoteOps) and the dialog view models, plus headless smoke tests of the views.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public class RemoteDialogsTests
{
    /// <summary>A tab on <see cref="RemoteSetup.Local"/> with recording hooks. <see cref="Fill"/> handles every dialog
    /// (fill and submit, or leave it to cancel); without it dialogs are recorded and cancelled.</summary>
    sealed class Fx : IDisposable
    {
        public readonly RemoteSetup S = new();
        public RepoTabViewModel Tab = null!;
        public readonly List<DialogViewModel> Dialogs = [];
        public readonly List<string> Errors = [];
        public readonly List<GitResult> GitErrors = [];
        public Func<DialogViewModel, Task>? Fill;

        public RemoteOps Ops => Tab.RemoteOps;
        public Job LastJob => Tab.Jobs.History[0];

        public async Task OpenAsync()
        {
            Tab = new RepoTabViewModel(await GitRepository.OpenAsync(S.Local.Path));
            Tab.ShowError = (_, text) => { Errors.Add(text); return Task.CompletedTask; };
            Tab.ShowGitError = (r, _) => { GitErrors.Add(r); return Task.CompletedTask; };
            Tab.ShowDialog = async vm =>
            {
                Dialogs.Add(vm);
                if (Fill is not null) await Fill(vm);
                return vm.Result == true;
            };
            await RefreshAsync();
        }

        public async Task RefreshAsync()
        {
            await Tab.RefreshAsync();
            await Tab.Refreshing;
        }

        public GitRef Ref(string fullName) => Tab.Refs.Refs.Single(r => r.FullName == fullName);

        public void Dispose()
        {
            Tab?.Dispose();
            S.Dispose();
        }
    }

    static async Task<T> Open<T>(Fx f, Func<Task> open) where T : DialogViewModel
    {
        f.Fill = null;
        f.Dialogs.Clear();
        await open();
        return Assert.IsType<T>(Assert.Single(f.Dialogs));
    }

    static async Task WaitFor(Func<bool> condition)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException();
            await Task.Delay(20);
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>An `ext::` remote that runs <paramref name="script"/> instead of a git server (no network).</summary>
    static void AddExtRemote(Fx f, string name, string script)
    {
        var path = Path.Combine(f.S.Local.Path, ".git", name + ".sh");
        File.WriteAllText(path, "#!/bin/sh\n" + script);
        File.SetUnixFileMode(path, (UnixFileMode)0b111_101_101);
        f.S.Local.Git("config", "protocol.ext.allow", "always");
        f.S.Local.Git("remote", "add", name, "ext::" + path);
    }

    // ---------------------------------------------------------------- fetch

    [AvaloniaFact]
    public async Task Fetch_dialog_defaults_and_submit()
    {
        using var f = new Fx();
        f.S.OtherPush("feat", 2, "feat");
        await f.OpenAsync();

        var vm = await Open<FetchViewModel>(f, () => f.Ops.OpenFetchAsync());
        Assert.Equal(["origin"], vm.Remotes);
        Assert.Equal("origin", vm.SelectedRemote);
        Assert.True(vm.CanSubmit);
        Assert.Equal(Settings.Get(RemoteOps.AllRemotesKey, false), vm.AllRemotes);
        vm.AllRemotes = true;
        Assert.False(vm.RemoteEnabled);
        vm.AllRemotes = false;
        await vm.SubmitAsync();

        Assert.True(vm.Result);
        Assert.Equal("Fetch 'origin'", f.LastJob.Name);
        Assert.Equal("Fetched 1 branch", f.LastJob.Message);
        Assert.Contains(f.Tab.Refs.Refs, r => r.FullName == "refs/remotes/origin/feat");
        Assert.False(Settings.Get(RemoteOps.AllRemotesKey, true));
    }

    [AvaloniaFact]
    public async Task Quick_fetch_uses_origin_and_opens_the_dialog_without_remotes()
    {
        using var f = new Fx();
        f.S.Local.Git("remote", "add", "aaa", f.S.Bare.Path);
        f.S.OtherPush("feat", 2, "feat");
        await f.OpenAsync();
        Settings.Set(RemoteOps.AllRemotesKey, false);

        await f.Ops.QuickFetchAsync();
        Assert.Empty(f.Dialogs);
        Assert.Equal("Fetch 'origin'", f.LastJob.Name);
        Assert.Equal("Fetched 1 branch", f.LastJob.Message);

        f.S.Local.Git("remote", "remove", "origin");
        f.S.Local.Git("remote", "remove", "aaa");
        await f.RefreshAsync();
        var vm = await Open<FetchViewModel>(f, f.Ops.QuickFetchAsync);
        Assert.Empty(vm.Remotes);
        Assert.False(vm.CanSubmit);
    }

    [AvaloniaFact]
    public async Task Host_error_fails_quietly_for_fetch_but_shows_the_window_for_push()
    {
        using var f = new Fx();
        AddExtRemote(f, "nohost", "echo \"fatal: unable to access 'https://nohost.invalid/': Could not resolve host: nohost.invalid\" >&2\nexit 128\n");
        await f.OpenAsync();

        var fetch = await f.Ops.FetchAsync("nohost");
        Assert.Equal(JobOutcome.Failed, fetch.Outcome);
        Assert.Equal("Could not resolve host 'nohost.invalid'", fetch.Message);
        Assert.Empty(f.GitErrors);

        var push = await f.Ops.PushAsync("main", "nohost", "refs/heads/main", false, false, false);
        Assert.Equal("Could not resolve host 'nohost.invalid'", push.Message);
        Assert.Contains("Could not resolve host: nohost.invalid", Assert.Single(f.GitErrors).ReadableOutput);
    }

    [AvaloniaFact]
    public async Task Tag_clobber_offers_force_fetch()
    {
        using var f = new Fx();
        f.S.Local.Git("tag", "v1");
        var theirs = f.S.OtherPush("theirs", 2);
        f.S.Other.Git("tag", "v1");
        f.S.Other.Git("push", "-q", "origin", "v1");
        f.S.Local.Git("config", "--add", "remote.origin.fetch", "refs/tags/*:refs/tags/*"); // tags without "+"
        await f.OpenAsync();
        GitErrorActions? actions = null;
        f.Tab.ShowGitError = (r, a) => { f.GitErrors.Add(r); actions = a; return Task.CompletedTask; };

        var job = await f.Ops.FetchAsync("origin");
        Assert.Equal(JobOutcome.Failed, job.Outcome);
        Assert.Equal(GitErrorKind.TagClobber, GitErrors.Classify(Assert.Single(f.GitErrors)).Kind);
        Assert.True(actions!.QuietHostError);
        await actions.ForceFetch!();
        Assert.Equal("Force Fetch", f.LastJob.Name);
        Assert.Equal(JobOutcome.Succeeded, f.LastJob.Outcome);
        Assert.Equal(theirs, f.S.Local.Git("rev-parse", "v1^{}"));
    }

    [AvaloniaFact]
    public async Task Cancelled_fetch_ends_canceled_without_error_and_leaves_no_process()
    {
        using var f = new Fx();
        var pidFile = Path.Combine(f.S.Local.Path, ".git", "slow.pid");
        AddExtRemote(f, "slow", $"echo $$ > '{pidFile}'\nexec sleep 300\n");
        await f.OpenAsync();

        var run = f.Ops.FetchAsync("slow");
        await WaitFor(() => File.Exists(pidFile) && File.ReadAllText(pidFile).Trim().Length > 0);
        int pid = int.Parse(File.ReadAllText(pidFile).Trim());
        f.Tab.Jobs.Running.Single(j => j.Name == "Fetch 'slow'").Cancel();
        var job = await run;

        Assert.Equal(JobOutcome.Canceled, job.Outcome);
        Assert.Empty(f.GitErrors);
        Assert.Empty(f.Errors);
        await WaitFor(() => !Alive(pid));

        static bool Alive(int pid)
        {
            try
            {
                var stat = File.ReadAllText($"/proc/{pid}/stat");
                return stat[stat.LastIndexOf(')') + 2] != 'Z';
            }
            catch (IOException) { return false; }
        }
    }

    // ---------------------------------------------------------------- pull

    [AvaloniaFact]
    public async Task Pull_dialog_waits_for_the_reload_and_pulls_the_upstream_without_branch_argument()
    {
        using var f = new Fx();
        var theirs = f.S.OtherPush("theirs", 2, "main", ("b.txt", "b"));
        f.S.OtherPush("feat", 3, "feat");
        f.S.Local.Git("fetch", "-q", "origin");
        await f.OpenAsync();

        var vm = await Open<PullViewModel>(f, () => f.Ops.OpenPullAsync());
        Assert.False(vm.CanSubmit); // the reload is still running
        await vm.Loading;
        Assert.True(vm.CanSubmit);
        Assert.Equal("origin", vm.SelectedRemote);
        Assert.Equal("refs/remotes/origin/main", vm.SelectedBranch!.FullName);
        Assert.Equal(["origin/feat", "origin/main"], vm.Branches.Select(b => b.ShortName));
        Assert.Equal("main", vm.Into);
        vm.Rebase = false;
        vm.AutoStash = false;
        await vm.SubmitAsync();

        Assert.Equal("Pull 'origin/main'", f.LastJob.Name);
        Assert.Equal("Everything is up to date", f.LastJob.Message);
        Assert.StartsWith("$ git pull --rebase=false origin --prune --no-recurse-submodules\n", f.LastJob.Output);
        Assert.Equal(theirs, f.Tab.Refs.HeadSha);
        Assert.Equal(theirs, f.Tab.CommitList.SelectedSha);

        // A preselected other remote branch is passed by name.
        var other = await Open<PullViewModel>(f, () => f.Ops.OpenPullAsync("refs/remotes/origin/feat"));
        await other.Loading;
        Assert.Equal("refs/remotes/origin/feat", other.SelectedBranch!.FullName);
        other.Rebase = false;
        other.AutoStash = false;
        await other.SubmitAsync();
        Assert.Equal("Pull 'origin/feat'", f.LastJob.Name);
        Assert.StartsWith("$ git pull --rebase=false --no-ff origin refs/heads/feat --no-recurse-submodules\n", f.LastJob.Output); // another name: merge commit
    }

    /// <summary>Opens the Pull dialog on <paramref name="remoteBranch"/> and submits it as a merge pull.</summary>
    static async Task PullMergeAsync(Fx f, string? remoteBranch)
    {
        var vm = await Open<PullViewModel>(f, () => f.Ops.OpenPullAsync(remoteBranch));
        await vm.Loading;
        vm.Rebase = false;
        vm.AutoStash = false;
        await vm.SubmitAsync();
    }

    [AvaloniaFact]
    public async Task Pull_of_another_branch_is_no_ff_and_creates_a_merge_commit()
    {
        using var f = new Fx();
        var feature = f.S.OtherPush("feature", 2, "feature", ("b.txt", "b")); // main could fast-forward to it
        f.S.Local.Git("fetch", "-q", "origin");
        await f.OpenAsync();
        var main = f.Tab.Refs.HeadSha;
        await PullMergeAsync(f, "refs/remotes/origin/feature");
        Assert.StartsWith("$ git pull --rebase=false --no-ff origin refs/heads/feature --no-recurse-submodules\n", f.LastJob.Output);
        Assert.Equal($"{main} {feature}", f.S.Local.Git("log", "-1", "--format=%P"));
    }

    [AvaloniaFact]
    public async Task Pull_of_the_same_name_from_another_remote_fast_forwards()
    {
        using var f = new Fx();
        using var up = new TempRepo(init: false);
        up.Git("init", "--bare", "-q", "-b", "main");
        var theirs = f.S.OtherPush("theirs", 2, "main", ("b.txt", "b"));
        f.S.Other.Git("push", "-q", up.Path, "main");
        f.S.Local.Git("remote", "add", "upstream", up.Path);
        f.S.Local.Git("fetch", "-q", "upstream");
        await f.OpenAsync();
        await PullMergeAsync(f, "refs/remotes/upstream/main");
        Assert.StartsWith("$ git pull --rebase=false upstream refs/heads/main --no-recurse-submodules\n", f.LastJob.Output);
        Assert.Equal(theirs, f.Tab.Refs.HeadSha);
    }

    [AvaloniaFact]
    public async Task Pull_of_an_upstream_of_another_name_is_no_ff()
    {
        using var f = new Fx();
        var develop = f.S.OtherPush("develop", 2, "develop", ("b.txt", "b"));
        f.S.Local.Git("fetch", "-q", "origin");
        f.S.Local.Git("checkout", "-q", "-b", "feature/login");
        f.S.Local.Git("branch", "-q", "-u", "origin/develop");
        await f.OpenAsync();
        var head = f.Tab.Refs.HeadSha;
        Settings.Set(RemoteOps.RebaseKey, false);
        Settings.Set(RemoteOps.AutoStashKey, false);
        await f.Ops.QuickPullAsync(); // the plain Pull button: no branch argument
        Assert.Equal("Pull 'origin/develop'", f.LastJob.Name);
        Assert.StartsWith("$ git pull --rebase=false --no-ff origin --prune --no-recurse-submodules\n", f.LastJob.Output);
        Assert.Equal($"{head} {develop}", f.S.Local.Git("log", "-1", "--format=%P"));
    }

    [AvaloniaFact]
    public async Task Pull_dialog_reload_keeps_the_choice_unless_the_active_branch_changed()
    {
        using var f = new Fx();
        f.S.OtherPush("feat", 2, "feat");
        f.S.Local.Git("fetch", "-q", "origin");
        f.S.Local.Git("branch", "dev", "--track", "origin/feat");
        await f.OpenAsync();

        var vm = await Open<PullViewModel>(f, () => f.Ops.OpenPullAsync());
        vm.SelectedBranch = vm.Branches.Single(b => b.FullName == "refs/remotes/origin/feat");
        await vm.Loading;
        Assert.Equal("refs/remotes/origin/feat", vm.SelectedBranch!.FullName); // kept
        Assert.True(vm.CanSubmit);

        f.S.Local.Git("checkout", "-q", "dev"); // the tab has not seen it yet
        var changed = await Open<PullViewModel>(f, () => f.Ops.OpenPullAsync());
        Assert.Equal("main", changed.Into);
        Assert.Equal("refs/remotes/origin/main", changed.SelectedBranch!.FullName);
        await changed.Loading;
        Assert.Equal("dev", changed.Into); // rebuilt for the new active branch
        Assert.Equal("refs/remotes/origin/feat", changed.SelectedBranch!.FullName);
    }

    [AvaloniaFact]
    public async Task Pull_dialog_is_disabled_on_detached_head_and_quick_pull_falls_back_to_it()
    {
        using var f = new Fx();
        f.S.Local.Git("checkout", "-q", "--detach");
        await f.OpenAsync();
        var vm = await Open<PullViewModel>(f, f.Ops.QuickPullAsync);
        await vm.Loading;
        Assert.Null(vm.Into);
        Assert.False(vm.CanSubmit);
    }

    [AvaloniaFact]
    public async Task Quick_pull_runs_the_upstream_and_a_conflict_shows_the_merge_conflict_window()
    {
        using var f = new Fx();
        f.S.OtherPush("theirs", 2, "main", ("a.txt", "theirs\n"));
        f.S.Local.Commit("mine", 3, ("a.txt", "mine\n"));
        await f.OpenAsync();
        Settings.Set(RemoteOps.RebaseKey, false);
        Settings.Set(RemoteOps.AutoStashKey, false);

        await f.Ops.QuickPullAsync();
        Assert.Empty(f.Dialogs);
        Assert.Equal("Pull 'origin/main'", f.LastJob.Name);
        Assert.Equal("Pull failed", f.LastJob.Message);
        Assert.Equal(GitErrorKind.MergeConflict, GitErrors.Classify(Assert.Single(f.GitErrors)).Kind);
    }

    // ---------------------------------------------------------------- push

    [AvaloniaFact]
    public async Task Quick_push_rules()
    {
        using var f = new Fx();
        await f.OpenAsync();
        Settings.Set(RemoteOps.PushTagsKey, false);

        // Upstream exists: push there, no dialog.
        var sha = f.S.Local.Commit("second", 2);
        await f.RefreshAsync();
        await f.Ops.QuickPushAsync();
        Assert.Empty(f.Dialogs);
        Assert.Equal("Push 'main' to 'origin'", f.LastJob.Name);
        Assert.Equal(sha, f.S.BareSha("main"));

        // No upstream, one remote: push and set the upstream.
        f.S.Local.Git("checkout", "-q", "-b", "feat");
        await f.RefreshAsync();
        await f.Ops.QuickPushAsync();
        Assert.Equal("Push 'feat' to 'origin'", f.LastJob.Name);
        Assert.True(f.S.BareHas("refs/heads/feat"));
        Assert.Equal("refs/heads/feat", f.S.Local.Git("config", "branch.feat.merge"));

        // No upstream, several remotes: the Push dialog for the active branch.
        f.S.Local.Git("checkout", "-q", "-b", "other");
        f.S.Local.Git("remote", "add", "second", f.S.Bare.Path);
        await f.RefreshAsync();
        var vm = await Open<PushViewModel>(f, f.Ops.QuickPushAsync);
        Assert.Equal("other", vm.SelectedLocal!.ShortName);
        Assert.True(vm.ShowRemote);
    }

    [AvaloniaFact]
    public async Task Push_dialog_target_list_default_new_match_and_custom()
    {
        using var f = new Fx();
        f.S.OtherPush("bar", 2, "bar");
        f.S.Local.Git("fetch", "-q", "origin");
        f.S.Local.Git("branch", "foo", "--track", "origin/bar");
        f.S.Local.Git("branch", "bar");     // no upstream, origin/bar exists: matched item
        f.S.Local.Git("branch", "feat");    // no upstream, no remote branch: new item
        var fooSha = f.S.Local.Commit("on main", 3);
        f.S.Local.Git("branch", "-f", "foo", fooSha);
        await f.OpenAsync();
        Settings.Set(f.Ops.PushRefspecKey, null as string);

        var vm = await Open<PushViewModel>(f, () => f.Ops.OpenPushAsync("foo"));
        Assert.False(vm.ShowRemote); // exactly one remote
        Assert.Equal("origin", vm.SelectedRemote);
        Assert.Equal(["default (origin/bar)", "", "origin/bar", "origin/main", "", "Custom..."], vm.Targets.Select(t => t.Text));
        Assert.Equal("default (origin/bar)", vm.SelectedTarget!.Text);
        Assert.False(vm.ShowTracking);
        Assert.True(vm.CanSubmit);

        vm.SelectedLocal = vm.LocalBranches.Single(b => b.ShortName == "feat");
        Assert.Equal(["new (origin/feat)", "", "origin/bar", "origin/main", "", "Custom..."], vm.Targets.Select(t => t.Text));
        Assert.True(vm.ShowTracking && vm.CreateTracking);

        vm.SelectedLocal = vm.LocalBranches.Single(b => b.ShortName == "bar");
        Assert.Equal(["origin/bar", "origin/main", "", "Custom..."], vm.Targets.Select(t => t.Text));
        Assert.Equal("origin/bar", vm.SelectedTarget!.Text);

        // "Custom..." cancelled: the previous selection returns.
        vm.SelectedTarget = vm.Targets[^1];
        await vm.Pending;
        Assert.Equal("origin/bar", vm.SelectedTarget!.Text);
        var custom = Assert.IsType<CustomBranchNameViewModel>(f.Dialogs[^1]);
        Assert.Equal("origin/", custom.Prefix);
        Assert.Equal("bar", custom.Name);

        // Back to foo (default target): the bare source goes to the upstream bar.
        vm.SelectedLocal = vm.LocalBranches.Single(b => b.ShortName == "foo");
        f.Fill = async d =>
        {
            if (d is CustomBranchNameViewModel c) { c.Name = "refs/heads/custom/y"; await c.SubmitAsync(); }
        };
        vm.SelectedTarget = vm.Targets[^1];
        await vm.Pending;
        Assert.Equal("refs/heads/custom/y", vm.SelectedTarget!.Text);
        Assert.Equal(["default (origin/bar)", "", "origin/bar", "origin/main", "", "refs/heads/custom/y", "Custom..."],
            vm.Targets.Select(t => t.Text));
        Assert.Equal("refs/heads/custom/y", Settings.Get<string?>(f.Ops.PushRefspecKey, null));
        await vm.SubmitAsync();
        Assert.Equal("Push 'foo' to 'origin'", f.LastJob.Name);
        Assert.Equal(fooSha, f.S.BareSha("custom/y"));

        var again = await Open<PushViewModel>(f, () => f.Ops.OpenPushAsync("foo"));
        again.Force = true; // foo and bar diverged
        await again.SubmitAsync(); // default (origin/bar)
        Assert.Equal(fooSha, f.S.BareSha("bar"));
        Assert.False(f.S.BareHas("refs/heads/foo"));
    }

    [AvaloniaFact]
    public async Task Push_dialog_new_branch_with_tracking_and_rejected_push_shows_the_full_output()
    {
        using var f = new Fx();
        f.S.Local.Git("checkout", "-q", "-b", "feat");
        await f.OpenAsync();

        var vm = await Open<PushViewModel>(f, () => f.Ops.OpenPushAsync());
        Assert.Equal("feat", vm.SelectedLocal!.ShortName);
        Assert.Equal("new (origin/feat)", vm.SelectedTarget!.Text);
        await vm.SubmitAsync();
        Assert.Equal("refs/heads/feat", f.S.Local.Git("config", "branch.feat.merge"));
        Assert.Equal("feat", f.Tab.Refs.Refs.Single(r => r.FullName == "refs/heads/feat").Upstream?["refs/remotes/origin/".Length..]);

        f.S.Local.Git("commit", "-q", "--amend", "-m", "amended");
        await f.RefreshAsync();
        var rejected = await Open<PushViewModel>(f, () => f.Ops.OpenPushAsync());
        rejected.Force = false;
        await rejected.SubmitAsync();
        Assert.Equal("'feat' rejected", f.LastJob.Message);
        var output = Assert.Single(f.GitErrors).ReadableOutput;
        Assert.Contains("[rejected]", output);
        Assert.Contains("To ", output); // the full output, not only the first error line

        var forced = await Open<PushViewModel>(f, () => f.Ops.OpenPushAsync());
        forced.Force = true;
        await forced.SubmitAsync();
        Assert.Equal(f.S.Local.Git("rev-parse", "feat"), f.S.BareSha("feat"));
    }

    [AvaloniaFact]
    public async Task Push_dialog_without_remotes_offers_add_remote()
    {
        using var f = new Fx();
        f.S.Local.Git("remote", "remove", "origin");
        await f.OpenAsync();

        var vm = await Open<PushViewModel>(f, () => f.Ops.OpenPushAsync());
        Assert.Equal([PushViewModel.AddRemoteItem], vm.Remotes);
        Assert.Null(vm.SelectedRemote);
        Assert.True(vm.ShowRemote);
        Assert.False(vm.CanSubmit);

        vm.SelectedRemote = PushViewModel.AddRemoteItem; // cancelled: back to nothing
        await vm.Pending;
        Assert.Null(vm.SelectedRemote);
        Assert.IsType<RemoteViewModel>(f.Dialogs[^1]);

        f.Fill = async d =>
        {
            if (d is RemoteViewModel r) { r.Url = f.S.Bare.Path; await r.SubmitAsync(); }
        };
        vm.SelectedRemote = PushViewModel.AddRemoteItem;
        await vm.Pending;
        Assert.Equal(["origin"], vm.Remotes);
        Assert.Equal("origin", vm.SelectedRemote);
        Assert.False(vm.ShowRemote);
        Assert.True(vm.CanSubmit);
        Assert.Equal("new (origin/main)", vm.SelectedTarget!.Text); // nothing fetched from the new remote yet
    }

    [AvaloniaFact]
    public async Task Push_several_branches_and_tags_dialogs()
    {
        using var f = new Fx();
        f.S.Local.Git("branch", "a");
        f.S.Local.Git("branch", "b", "--track", "origin/main");
        f.S.Local.Git("tag", "v1");
        f.S.Local.Git("tag", "v2");
        f.S.Local.Git("tag", "v3");
        await f.OpenAsync();

        var vm = await Open<PushBranchesViewModel>(f, () => f.Ops.OpenPushBranchesAsync(["a", "b"], "origin"));
        Assert.Equal("Push 2 branches to remote repository", vm.Description);
        Assert.Equal("Push 2 branches", vm.SubmitText);
        Assert.Equal([new PushBranchRow("a", "origin/a (new)"), new PushBranchRow("b", "origin/main")], vm.Rows);
        await vm.SubmitAsync();
        Assert.Equal("Push 2 branches to 'origin'", f.LastJob.Name);
        Assert.Equal("Everything is up to date", f.LastJob.Message);
        Assert.True(f.S.BareHas("refs/heads/a") && f.S.BareHas("refs/heads/b"));
        Assert.Equal("refs/heads/b", f.S.Local.Git("config", "branch.b.merge")); // rewired to the same-named branch

        var one = await Open<PushTagsViewModel>(f, () => f.Ops.OpenPushTagAsync("v1"));
        Assert.Equal(("Push Tag", "Push tag to remote repository", "Push"), (one.Title, one.Description, one.SubmitText));
        Assert.Equal("origin", one.SelectedRemote);
        await one.SubmitAsync();
        Assert.Equal("Push 'v1' to 'origin'", f.LastJob.Name);
        Assert.True(f.S.BareHas("refs/tags/v1"));

        var many = await Open<PushTagsViewModel>(f, () => f.Ops.OpenPushTagsAsync(["v2", "v3"]));
        Assert.Equal(("Push", "Push 2 tags to remote repository", "Push 2 tags"), (many.Title, many.Description, many.SubmitText));
        await many.SubmitAsync();
        Assert.Equal("Push 2 tags to 'origin'", f.LastJob.Name);
        Assert.True(f.S.BareHas("refs/tags/v2") && f.S.BareHas("refs/tags/v3"));
    }

    // ---------------------------------------------------------------- fast-forward and tracking

    [AvaloniaFact]
    public async Task Fast_forward_entry_points()
    {
        using var f = new Fx();
        f.S.Local.Git("branch", "dev", "--track", "origin/main");
        f.S.Local.Git("branch", "div", "--track", "origin/main");
        f.S.Local.Git("branch", "g");
        f.S.Local.Git("config", "branch.g.remote", "origin");
        f.S.Local.Git("config", "branch.g.merge", "refs/heads/gone");
        var theirs = f.S.OtherPush("theirs", 2, "main", ("b.txt", "b"));
        f.S.Local.Git("fetch", "-q", "origin");
        f.S.Local.Git("checkout", "-q", "div");
        f.S.Local.Commit("local only", 3);
        f.S.Local.Git("checkout", "-q", "main");
        await f.OpenAsync();

        Assert.False(RemoteOps.IsStrictlyAhead(f.Ref("refs/heads/main")));
        await f.Ops.FastForwardAsync(["main", "dev", "g"]); // g has no existing upstream: skipped
        Assert.Equal(["Fast forward 'dev'", "Fast forward 'main'"], f.Tab.Jobs.History.Take(2).Select(j => j.Name));
        Assert.Equal(theirs, f.S.Local.Git("rev-parse", "main"));
        Assert.Equal(theirs, f.S.Local.Git("rev-parse", "dev"));
        Assert.Equal(theirs, f.Tab.CommitList.SelectedSha); // the upstream's commit
        Assert.Empty(f.Errors);

        await f.Ops.FastForwardAsync("g");
        Assert.Equal("Remote branch 'origin/gone' doesn't exist. It might be removed, but 'g' refers to it.", Assert.Single(f.Errors));

        await f.Ops.FastForwardAsync("div"); // diverged, not active: refused
        Assert.Equal("Fast forward failed", f.LastJob.Message);
        Assert.Contains("Cannot fast-forward 'div'", Assert.Single(f.GitErrors).Stderr);

        f.S.Local.Commit("ahead", 4);
        await f.RefreshAsync();
        Assert.True(RemoteOps.IsStrictlyAhead(f.Ref("refs/heads/main")));
        await f.Ops.FastForwardAsync("main"); // strictly ahead: the fast-forward pull
        Assert.Equal("Pull 'origin/main' into 'main'", f.LastJob.Name);
        Assert.Equal("Everything is up to date", f.LastJob.Message);
    }

    [AvaloniaFact]
    public async Task Tracking_submenu_and_change_tracking_dialog()
    {
        using var f = new Fx();
        f.S.OtherPush("feat", 2, "feat");
        f.S.Local.Git("fetch", "-q", "origin");
        f.S.Local.Git("branch", "dev");
        await f.OpenAsync();
        Assert.False(f.Ops.UseTrackingDialog);

        await f.Ops.SetUpstreamAsync("dev", "refs/remotes/origin/feat");
        Assert.Equal("Update tracking reference", f.LastJob.Name);
        Assert.Equal("refs/remotes/origin/feat", f.Ref("refs/heads/dev").Upstream);

        var vm = await Open<ChangeTrackingViewModel>(f, () => f.Ops.OpenChangeTrackingAsync("dev"));
        Assert.Equal(["No tracking", "", "origin/feat", "origin/main"], vm.Items.Select(i => i.Text));
        Assert.True(vm.Items[1].IsSeparator);
        Assert.Equal("refs/remotes/origin/feat", vm.Selected!.FullName);
        Assert.False(vm.CanSubmit); // equals the current state
        vm.Selected = vm.Items[0];
        Assert.True(vm.CanSubmit);
        await vm.SubmitAsync();
        Assert.True(vm.Result);
        Assert.Equal("Removing tracking reference...", vm.StatusText);
        Assert.Equal("Remove tracking reference", f.LastJob.Name);
        Assert.Null(f.Ref("refs/heads/dev").Upstream);

        // An upstream that is gone: "No tracking" preselected, Change enabled (removes it).
        f.S.Local.Git("config", "branch.dev.remote", "origin");
        f.S.Local.Git("config", "branch.dev.merge", "refs/heads/gone");
        await f.RefreshAsync();
        var gone = await Open<ChangeTrackingViewModel>(f, () => f.Ops.OpenChangeTrackingAsync("dev"));
        Assert.Null(gone.Selected!.FullName);
        Assert.True(gone.CanSubmit);
    }

    // ---------------------------------------------------------------- remotes

    [AvaloniaFact]
    public async Task Add_edit_delete_remote_and_fetch_automatically()
    {
        using var f = new Fx();
        await f.OpenAsync();

        var add = await Open<RemoteViewModel>(f, f.Ops.OpenAddRemoteAsync);
        Assert.Equal(("Add Remote", "Add New Remote", "origin"), (add.Title, add.SubmitText, add.Name));
        Assert.False(add.CanSubmit); // empty URL: no message
        add.Url = "/tmp/x";
        Assert.False(add.CanSubmit);
        Assert.Equal("Remote 'origin' already exists", add.StatusText);
        add.Name = "a..b";
        Assert.Equal("Name cannot contain '..'", add.StatusText);
        add.Name = "-v";
        Assert.Equal(("Name cannot begin with '-'", false), (add.StatusText, add.CanSubmit));
        add.Name = "up";
        Assert.True(add.CanSubmit);
        Assert.Equal(DialogStatus.None, add.Status);
        await add.SubmitAsync();
        Assert.True(add.Result);
        Assert.Equal("Add remote 'up'", f.LastJob.Name);
        Assert.Contains(new RemoteEntry("up", "/tmp/x"), f.Tab.Remotes);

        var edit = await Open<RemoteViewModel>(f, () => f.Ops.OpenEditRemoteAsync("up"));
        Assert.Equal(("Edit Remote", "Edit", "up", "/tmp/x"), (edit.Title, edit.SubmitText, edit.Name, edit.Url));
        Assert.False(edit.CanSubmit); // nothing changed
        edit.Name = "origin";
        Assert.Equal("Remote 'origin' already exists", edit.StatusText);
        edit.Name = "up2";
        edit.Url = "/tmp/y";
        await edit.SubmitAsync();
        Assert.Equal("Edit remote 'up'", f.LastJob.Name);
        Assert.Contains(new RemoteEntry("up2", "/tmp/y"), f.Tab.Remotes);
        Assert.DoesNotContain(f.Tab.Remotes, r => r.Name == "up");

        Assert.True(await f.Ops.IsFetchAutomaticallyAsync("up2"));
        await f.Ops.SetFetchAutomaticallyAsync("up2", false);
        Assert.False(await f.Ops.IsFetchAutomaticallyAsync("up2"));

        var confirm = await Open<DialogViewModel>(f, () => f.Ops.DeleteRemoteAsync("up2")); // cancelled
        Assert.Equal("Are you sure you want to delete reference to remote 'up2'?", confirm.Title);
        Assert.Equal("Do you want to delete 'up2'?", confirm.Description);
        Assert.Equal("Delete", confirm.SubmitText);
        Assert.Contains(f.Tab.Remotes, r => r.Name == "up2");

        f.Fill = d => d.SubmitAsync();
        await f.Ops.DeleteRemoteAsync("up2");
        Assert.Equal("Delete remote 'up2'", f.LastJob.Name);
        Assert.DoesNotContain(f.Tab.Remotes, r => r.Name == "up2");
    }

    // ---------------------------------------------------------------- headless smoke tests of the views

    static void Settle(params Window[] windows)
    {
        Dispatcher.UIThread.RunJobs();
        foreach (var w in windows) w.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    static T Find<T>(Window w, string name) where T : Control => w.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    static async Task<DialogWindow> Show(Window owner, DialogViewModel vm, Func<DialogWindow, Task> check)
    {
        var shown = DialogWindow.ShowAsync(vm, owner);
        Settle(owner);
        var d = (DialogWindow)owner.OwnedWindows.Single();
        Settle(d);
        await check(d);
        vm.Cancel();
        Assert.False(await shown);
        return d;
    }

    [AvaloniaFact]
    public async Task Dialog_views_open_with_their_defaults()
    {
        using var f = new Fx();
        f.S.Local.Git("branch", "feat");
        f.S.Local.Git("tag", "v1");
        await f.OpenAsync();
        var owner = new Window { Width = 900, Height = 700 };
        owner.Show();
        try
        {
            await Show(owner, new FetchViewModel(f.Ops, null), d =>
            {
                Assert.Equal("Fetch", d.Title);
                Assert.Equal("origin", Find<ComboBox>(d, "Remote").SelectedItem);
                Assert.Equal("Fetch all remotes", Find<CheckBox>(d, "AllRemotes").Content);
                Assert.True(Find<Button>(d, "SubmitButton").IsEffectivelyEnabled);
                return Task.CompletedTask;
            });

            var pull = new PullViewModel(f.Ops, null);
            await Show(owner, pull, async d =>
            {
                await pull.Loading;
                Settle(d);
                Assert.Equal(520, d.Width);
                Assert.Equal("main", Find<RefView>(d, "Into").Text);
                Assert.Same(pull.SelectedBranch, Find<ComboBox>(d, "Branch").SelectedItem);
                Assert.True(Find<Button>(d, "SubmitButton").IsEffectivelyEnabled);
            });

            var push = new PushViewModel(f.Ops, "feat", null);
            await Show(owner, push, d =>
            {
                Assert.False(Find<ComboBox>(d, "Remote").IsVisible); // one remote
                var target = Find<ComboBox>(d, "Target");
                Assert.Equal(5, target.ItemCount); // new (origin/feat), separator, origin/main, separator, Custom...
                Assert.Equal("new (origin/feat)", ((PushTarget)target.SelectedItem!).Text);
                Assert.True(Find<CheckBox>(d, "Tracking").IsVisible);
                Assert.False(Find<LucideIcon>(d, "ForceWarning").IsVisible);
                push.Force = true;
                Settle(d);
                Assert.True(Find<LucideIcon>(d, "ForceWarning").IsVisible);
                return Task.CompletedTask;
            });

            await Show(owner, new CustomBranchNameViewModel("origin", "feat"), d =>
            {
                Assert.Equal("origin/", Find<TextBlock>(d, "Prefix").Text);
                Assert.Equal("feat", Find<RefNameBox>(d, "BranchName").Text);
                return Task.CompletedTask;
            });

            await Show(owner, new PushBranchesViewModel(f.Ops, ["feat", "main"], "origin"), d =>
            {
                Assert.True(d.CanResize);
                Assert.Equal(2, Find<ItemsControl>(d, "Rows").ItemCount);
                return Task.CompletedTask;
            });

            await Show(owner, new PushTagsViewModel(f.Ops, ["v1"], null), d =>
            {
                Assert.Equal("Push Tag", d.Title);
                Assert.True(Find<RefView>(d, "SingleTag").IsVisible);
                return Task.CompletedTask;
            });

            await Show(owner, new PushTagsViewModel(f.Ops, ["v1", "v2"], null), d =>
            {
                Assert.Equal(2, Find<ItemsControl>(d, "Tags").ItemCount);
                Assert.Equal("origin", Find<ComboBox>(d, "Remote").SelectedItem);
                return Task.CompletedTask;
            });

            await Show(owner, new ChangeTrackingViewModel(f.Ops, f.Ref("refs/heads/main")), d =>
            {
                Assert.Equal("main", Find<RefView>(d, "Branch").Text);
                Assert.Equal("origin/main", ((TrackItem)Find<ComboBox>(d, "Track").SelectedItem!).Text);
                Assert.False(Find<Button>(d, "SubmitButton").IsEffectivelyEnabled);
                return Task.CompletedTask;
            });

            await Show(owner, new RemoteViewModel(f.Ops, null), d =>
            {
                Assert.Equal(630, d.Width);
                Assert.Equal("origin", Find<RefNameBox>(d, "RemoteName").Text);
                Assert.Equal("Git Repository Url", Find<TextBox>(d, "Url").PlaceholderText);
                Assert.False(Find<Button>(d, "SubmitButton").IsEffectivelyEnabled);
                return Task.CompletedTask;
            });

            await Show(owner, new RemoteViewModel(f.Ops, f.Tab.Remotes[0]), d =>
            {
                Assert.Equal("Edit Remote", d.Title);
                Assert.Equal(f.S.Bare.Path, Find<TextBox>(d, "Url").Text);
                return Task.CompletedTask;
            });
        }
        finally
        {
            owner.Close();
        }
    }
}
