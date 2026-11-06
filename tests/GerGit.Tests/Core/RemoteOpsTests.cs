using GerGit.Core;
using GerGit.Core.Git;

namespace GerGit.Tests.Core;

/// <summary>A clone with a local bare remote "origin" and a second clone that plays "someone else" (no network).</summary>
sealed class RemoteSetup : IDisposable
{
    public readonly TempRepo Bare = new(init: false);
    public readonly TempRepo Local = new();
    public readonly TempRepo Other = new(init: false);

    public RemoteSetup()
    {
        Bare.Git("init", "--bare", "-q", "-b", "main");
        Local.Commit("first", 1, ("a.txt", "a\n"));
        Local.Git("remote", "add", "origin", Bare.Path);
        Local.Git("push", "-q", "-u", "origin", "main");
        Other.Git("clone", "-q", Bare.Path, ".");
        Other.Git("config", "user.name", "Other");
        Other.Git("config", "user.email", "other@example.com");
    }

    public Task<GitRepository> OpenAsync() => GitRepository.OpenAsync(Local.Path);

    /// <summary>A commit by "someone else", pushed to the bare remote.</summary>
    public string OtherPush(string message, int seconds, string branch = "main", params (string Path, string Content)[] files)
    {
        if (Other.Git("branch", "--show-current") != branch) Other.Git("checkout", "-q", "-B", branch);
        var sha = Other.Commit(message, seconds, files);
        Other.Git("push", "-q", "origin", branch);
        return sha;
    }

    public string BareSha(string branch) => Bare.Git("rev-parse", "refs/heads/" + branch);
    public bool BareHas(string fullRef) => Bare.Git("for-each-ref", "--format=%(refname)", fullRef).Length > 0;

    public void Dispose()
    {
        Bare.Dispose();
        Local.Dispose();
        Other.Dispose();
    }
}

/// <summary>the git commands and result parsing against local bare remotes.</summary>
public class RemoteOpsTests
{
    static Job NewJob(string name = "test") => new(name);

    [Fact]
    public void Fetch_lines_are_counted_except_up_to_date()
    {
        var stderr = "From /tmp/x\n * [new branch]      feat       -> origin/feat\n   abc1234..def5678  main       -> origin/main\n" +
                     " + 1111111...2222222 wip        -> origin/wip  (forced update)\n - [deleted]         (none)     -> origin/old\n" +
                     " = [up to date]      dev        -> origin/dev\n * [new tag]         v1         -> v1\nsomething else\n";
        Assert.Equal(5, GitRepository.CountFetched(stderr));
        Assert.Equal(0, GitRepository.CountFetched(""));
    }

    [Fact]
    public void Remote_of_a_ref_prefers_the_longest_remote_name()
    {
        Assert.Equal("team/a", GitRepository.RemoteOfRef("refs/remotes/team/a/main", ["team", "team/a"]));
        Assert.Equal("origin", GitRepository.RemoteOfRef("refs/remotes/origin/feat/x", ["origin"]));
        Assert.Equal("gone", GitRepository.RemoteOfRef("refs/remotes/gone/main", ["origin"]));
    }

    [Fact]
    public async Task Fetch_new_branch_then_nothing_new()
    {
        using var s = new RemoteSetup();
        var repo = await s.OpenAsync();
        s.OtherPush("feat", 2, "feat", ("f.txt", "f"));

        var job = NewJob();
        var r = await repo.FetchAsync(job, "origin", tags: false);
        Assert.True(r.Success, r.ReadableOutput);
        Assert.Equal("Fetched 1 branch", job.Message);
        Assert.DoesNotContain("$ git", job.Output); // fetch and push show only git's output
        Assert.Equal(s.BareSha("feat"), s.Local.Git("rev-parse", "refs/remotes/origin/feat"));

        var again = NewJob();
        await repo.FetchAsync(again, "origin", tags: false);
        Assert.Equal("Already up to date", again.Message);
        Assert.EndsWith("Already up to date\n", again.Output);
    }

    [Fact]
    public async Task Fetch_all_remotes_and_prune()
    {
        using var s = new RemoteSetup();
        using var second = new TempRepo(init: false);
        second.Git("init", "--bare", "-q", "-b", "main");
        s.Local.Git("remote", "add", "second", second.Path);
        s.Local.Git("push", "-q", "second", "main");
        s.OtherPush("feat", 2, "feat");
        s.Local.Git("fetch", "-q", "origin");
        s.Bare.Git("update-ref", "-d", "refs/heads/feat"); // gone on the remote: pruned locally
        s.OtherPush("main 2", 3, "main", ("b.txt", "b"));
        var repo = await s.OpenAsync();

        var job = NewJob();
        var r = await repo.FetchAsync(job, null, tags: false);
        Assert.True(r.Success, r.ReadableOutput);
        Assert.Equal("Fetched 2 branches", job.Message); // origin/main updated, origin/feat deleted
        Assert.Equal("", s.Local.Git("for-each-ref", "refs/remotes/origin/feat"));
        Assert.NotEqual("", s.Local.Git("for-each-ref", "refs/remotes/second/main"));
    }

    [Fact]
    public async Task Pull_merge_creates_a_merge_commit()
    {
        using var s = new RemoteSetup();
        s.OtherPush("theirs", 2, "main", ("b.txt", "b"));
        s.Local.Commit("mine", 3, ("c.txt", "c"));
        var repo = await s.OpenAsync();

        var job = NewJob();
        var r = await repo.PullAsync(job, "origin", null, rebase: false, autostash: false, tags: false);
        Assert.True(r.Success, r.ReadableOutput);
        Assert.Equal("Everything is up to date", job.Message);
        Assert.StartsWith("$ git pull --rebase=false origin --prune --no-recurse-submodules\n", job.Output);
        Assert.Equal(2, s.Local.Git("log", "-1", "--format=%P").Split(' ').Length);
    }

    [Fact]
    public async Task Pull_rebase_fast_forwards_with_the_branch_argument()
    {
        using var s = new RemoteSetup();
        var theirs = s.OtherPush("theirs", 2, "main", ("b.txt", "b"));
        var repo = await s.OpenAsync();

        var job = NewJob();
        var r = await repo.PullAsync(job, "origin", "main", rebase: true, autostash: false, tags: false);
        Assert.True(r.Success, r.ReadableOutput);
        Assert.Contains("$ git pull --rebase=true origin refs/heads/main --no-recurse-submodules\n", job.Output);
        Assert.Equal(theirs, s.Local.Git("rev-parse", "HEAD"));
    }

    [Fact]
    public async Task Pull_with_stash_and_reapply_on_a_dirty_tree()
    {
        using var s = new RemoteSetup();
        var theirs = s.OtherPush("theirs", 2, "main", ("b.txt", "b"));
        s.Local.Write("a.txt", "local edit\n");
        var repo = await s.OpenAsync();
        var stashesBefore = (await repo.ReadStashesAsync()).Count;

        var job = NewJob();
        var r = await repo.PullAsync(job, "origin", null, rebase: false, autostash: true, tags: false);
        Assert.True(r.Success, r.ReadableOutput);
        Assert.Contains("$ git stash push --message \"Pull autostash ", job.Output);
        Assert.Contains("$ git stash pop --index refs/stash@{0}", job.Output);
        Assert.Equal(theirs, s.Local.Git("rev-parse", "HEAD"));
        Assert.Equal("local edit\n", File.ReadAllText(Path.Combine(s.Local.Path, "a.txt")));
        Assert.Equal(stashesBefore, (await repo.ReadStashesAsync()).Count);
    }

    [Fact]
    public async Task Failed_pull_with_stash_and_reapply_keeps_the_stash_and_the_clean_tree()
    {
        using var s = new RemoteSetup();
        s.OtherPush("theirs", 2, "main", ("b.txt", "theirs\n"));
        s.Local.Commit("mine", 3, ("b.txt", "mine\n"));
        s.Local.Write("a.txt", "local edit\n");
        var repo = await s.OpenAsync();
        var stashesBefore = (await repo.ReadStashesAsync()).Count;

        var job = NewJob();
        var r = await repo.PullAsync(job, "origin", null, rebase: false, autostash: true, tags: false);
        Assert.False(r.Success);
        Assert.Equal("Pull failed", job.Message);
        Assert.Contains("CONFLICT", r.Stdout); // add/add conflict
        Assert.DoesNotContain("stash pop", job.Output);
        var stashes = await repo.ReadStashesAsync();
        Assert.Equal(stashesBefore + 1, stashes.Count);
        Assert.StartsWith("Pull autostash ", stashes[0].ShortSubject);
        Assert.Equal("a\n", File.ReadAllText(Path.Combine(s.Local.Path, "a.txt"))); // not re-applied
    }

    [Fact]
    public async Task Pull_conflict_is_a_merge_conflict_and_keeps_the_state()
    {
        using var s = new RemoteSetup();
        s.OtherPush("theirs", 2, "main", ("a.txt", "theirs\n"));
        s.Local.Commit("mine", 3, ("a.txt", "mine\n"));
        var repo = await s.OpenAsync();

        var job = NewJob();
        var r = await repo.PullAsync(job, "origin", null, rebase: false, autostash: false, tags: false);
        Assert.False(r.Success);
        Assert.Equal(JobOutcome.Failed, job.Outcome);
        Assert.Equal("Pull failed", job.Message);
        Assert.Equal(GitErrorKind.MergeConflict, GitErrors.Classify(r).Kind);
        Assert.True(File.Exists(Path.Combine(s.Local.Path, ".git", "MERGE_HEAD"))); // left for the conflict flow
    }

    [Fact]
    public async Task Push_new_branch_sets_the_upstream()
    {
        using var s = new RemoteSetup();
        s.Local.Git("checkout", "-q", "-b", "feat");
        var sha = s.Local.Commit("feat", 2);
        var repo = await s.OpenAsync();

        var job = NewJob();
        var r = await repo.PushAsync(job, "origin", "refs/heads/feat", force: false, tags: false, setUpstream: true);
        Assert.True(r.Success, r.ReadableOutput);
        Assert.Equal(sha, s.BareSha("feat"));
        Assert.Equal("refs/heads/feat", s.Local.Git("config", "branch.feat.merge"));
        Assert.Equal("Updated 'origin/feat'", job.Message);
        Assert.DoesNotContain("$ git", job.Output);

        var again = NewJob();
        await repo.PushAsync(again, "origin", "refs/heads/feat", false, false, false);
        Assert.Equal("Everything is up to date", again.Message);
    }

    [Fact]
    public async Task Push_bare_source_goes_to_the_configured_upstream()
    {
        using var s = new RemoteSetup();
        s.OtherPush("bar", 2, "bar");
        s.Local.Git("fetch", "-q", "origin");
        s.Local.Git("checkout", "-q", "-b", "foo", "--track", "origin/bar");
        var sha = s.Local.Commit("foo", 3);
        var repo = await s.OpenAsync();

        var r = await repo.PushAsync(NewJob(), "origin", "refs/heads/foo", false, false, false);
        Assert.True(r.Success, r.ReadableOutput);
        Assert.Equal(sha, s.BareSha("bar"));
        Assert.False(s.BareHas("refs/heads/foo"));

        // An existing remote branch as destination, and a custom refs/ destination.
        var main = s.Local.Git("rev-parse", "main");
        Assert.True((await repo.PushAsync(NewJob(), "origin", "refs/heads/main:refs/heads/bar", true, false, false)).Success);
        Assert.Equal(main, s.BareSha("bar"));
        Assert.True((await repo.PushAsync(NewJob(), "origin", "refs/heads/foo:refs/heads/custom/x", false, false, false)).Success);
        Assert.Equal(sha, s.BareSha("custom/x"));
    }

    [Fact]
    public async Task Push_rejected_then_force_with_lease_after_amend()
    {
        using var s = new RemoteSetup();
        s.Local.Git("commit", "-q", "--amend", "-m", "amended");
        var amended = s.Local.Git("rev-parse", "HEAD");
        var repo = await s.OpenAsync();

        var job = NewJob();
        var r = await repo.PushAsync(job, "origin", "refs/heads/main", force: false, tags: false, setUpstream: false);
        Assert.False(r.Success);
        Assert.Equal("'main' rejected", job.Message);
        Assert.Contains("[rejected]", r.ReadableOutput);

        var forced = NewJob();
        r = await repo.PushAsync(forced, "origin", "refs/heads/main", force: true, tags: false, setUpstream: false);
        Assert.True(r.Success, r.ReadableOutput);
        Assert.Equal(amended, s.BareSha("main"));
    }

    [Fact]
    public async Task Push_several_branches_atomic_and_tags()
    {
        using var s = new RemoteSetup();
        s.Local.Git("branch", "a");
        s.Local.Git("branch", "b");
        s.Local.Git("tag", "v1");
        s.Local.Git("tag", "-a", "v2", "-m", "two");
        s.Local.Git("tag", "v3");
        var repo = await s.OpenAsync();

        var job = NewJob();
        var r = await repo.PushRefsAsync(job, "origin", ["refs/heads/a", "refs/heads/b"], branches: true);
        Assert.True(r.Success, r.ReadableOutput);
        Assert.Equal("Everything is up to date", job.Message);
        Assert.True(s.BareHas("refs/heads/a") && s.BareHas("refs/heads/b"));
        Assert.Equal("origin", s.Local.Git("config", "branch.b.remote"));

        Assert.True((await repo.PushRefsAsync(NewJob(), "origin", ["refs/tags/v1"], branches: false)).Success);
        Assert.True(s.BareHas("refs/tags/v1"));
        Assert.False(s.BareHas("refs/tags/v2"));
        Assert.True((await repo.PushRefsAsync(NewJob(), "origin", ["refs/tags/v2", "refs/tags/v3"], branches: false)).Success);
        Assert.True(s.BareHas("refs/tags/v2") && s.BareHas("refs/tags/v3"));
    }

    [Fact]
    public async Task Fast_forward_local_active_and_other_branch_and_diverged_refused()
    {
        using var s = new RemoteSetup();
        s.Local.Git("branch", "dev", "--track", "origin/main");
        s.Local.Git("branch", "div", "--track", "origin/main");
        var theirs = s.OtherPush("theirs", 2, "main", ("b.txt", "b"));
        s.Local.Git("fetch", "-q", "origin");
        s.Local.Git("checkout", "-q", "div");
        s.Local.Commit("local only", 3);
        s.Local.Git("checkout", "-q", "main");
        var repo = await s.OpenAsync();

        var job = NewJob();
        var r = await repo.FastForwardAsync(job, "main", "refs/remotes/origin/main", active: true);
        Assert.True(r.Success, r.ReadableOutput);
        Assert.Equal("Everything is up to date", job.Message);
        Assert.Equal(theirs, s.Local.Git("rev-parse", "HEAD"));

        var devBefore = s.Local.Git("rev-parse", "dev");
        var dev = NewJob();
        Assert.True((await repo.FastForwardAsync(dev, "dev", "refs/remotes/origin/main", active: false)).Success);
        Assert.Equal(theirs, s.Local.Git("rev-parse", "dev"));
        Assert.Contains($"$ git update-ref refs/heads/dev {theirs} {devBefore}", dev.Output);

        var divBefore = s.Local.Git("rev-parse", "div");
        var refused = NewJob();
        r = await repo.FastForwardAsync(refused, "div", "refs/remotes/origin/main", active: false);
        Assert.False(r.Success);
        Assert.Equal("Fast forward failed", refused.Message);
        Assert.Contains("Cannot fast-forward 'div'", r.Stderr);
        Assert.Equal(divBefore, s.Local.Git("rev-parse", "div"));

        var missing = NewJob(); // branch deleted meanwhile: a readable error, not an empty one
        r = await repo.FastForwardAsync(missing, "nope", "refs/remotes/origin/main", active: false);
        Assert.Equal(JobOutcome.Failed, missing.Outcome);
        Assert.Equal("Fast forward failed", missing.Message);
        Assert.Equal("Branch 'nope' doesn't exist.", r.Stderr);
    }

    [Fact]
    public async Task Fast_forward_refuses_a_branch_checked_out_in_another_worktree()
    {
        using var s = new RemoteSetup();
        using var wt = new TempRepo(init: false);
        s.Local.Git("branch", "dev", "--track", "origin/main");
        s.Local.Git("worktree", "add", "-q", wt.Path, "dev"); // created outside GerGit
        s.OtherPush("theirs", 2, "main", ("b.txt", "b"));
        s.Local.Git("fetch", "-q", "origin");
        var devBefore = s.Local.Git("rev-parse", "dev");
        var repo = await s.OpenAsync();

        var job = NewJob();
        var r = await repo.FastForwardAsync(job, "dev", "refs/remotes/origin/main", active: false);
        Assert.Equal((JobOutcome.Failed, "Fast forward failed"), (job.Outcome, job.Message));
        Assert.StartsWith("'dev' is checked out in another worktree: ", r.Stderr);
        Assert.EndsWith(Path.GetFileName(wt.Path), r.Stderr);
        Assert.DoesNotContain("update-ref", job.Output);
        Assert.Equal(devBefore, s.Local.Git("rev-parse", "dev"));
    }

    [Fact]
    public async Task Fast_forward_pull_active_and_other_branch()
    {
        using var s = new RemoteSetup();
        s.Local.Git("branch", "dev", "--track", "origin/main");
        var theirs = s.OtherPush("theirs", 2, "main", ("b.txt", "b"));
        var repo = await s.OpenAsync();

        var job = NewJob();
        var r = await repo.FastForwardPullAsync(job, "dev", "origin", "main", active: false);
        Assert.True(r.Success, r.ReadableOutput);
        Assert.Contains("$ git fetch -- origin refs/heads/main:refs/heads/dev", job.Output);
        Assert.Equal(theirs, s.Local.Git("rev-parse", "dev"));

        var active = NewJob();
        r = await repo.FastForwardPullAsync(active, "main", "origin", "main", active: true);
        Assert.True(r.Success, r.ReadableOutput);
        Assert.Equal("Everything is up to date", active.Message);
        Assert.Equal(theirs, s.Local.Git("rev-parse", "HEAD"));
    }

    [Fact]
    public async Task Tracking_set_and_unset()
    {
        using var s = new RemoteSetup();
        s.Local.Git("branch", "dev");
        var repo = await s.OpenAsync();
        Assert.True((await repo.SetUpstreamAsync(NewJob(), "dev", "refs/remotes/origin/main")).Success);
        Assert.Equal("refs/heads/main", s.Local.Git("config", "branch.dev.merge"));
        Assert.True((await repo.SetUpstreamAsync(NewJob(), "dev", null)).Success);
        Assert.Equal("", s.Local.Git("config", "--default", "", "branch.dev.merge"));
    }

    [Fact]
    public async Task Remotes_add_edit_rename_delete_and_fetch_automatically()
    {
        using var s = new RemoteSetup();
        var repo = await s.OpenAsync();
        Assert.True((await repo.AddRemoteAsync(NewJob(), "up", "/tmp/one")).Success);
        Assert.False((await repo.AddRemoteAsync(NewJob(), "up", "/tmp/one")).Success);
        Assert.Contains(new RemoteEntry("up", "/tmp/one"), await repo.ReadRemotesAsync());

        var edit = NewJob();
        Assert.True((await repo.EditRemoteAsync(edit, "up", "/tmp/one", "upstream", "/tmp/two"))!.Success);
        Assert.Contains("$ git remote set-url up /tmp/two", edit.Output);
        Assert.Contains("$ git remote rename up upstream", edit.Output);
        Assert.Contains(new RemoteEntry("upstream", "/tmp/two"), await repo.ReadRemotesAsync());
        Assert.DoesNotContain(await repo.ReadRemotesAsync(), r => r.Name == "up");

        Assert.True(await repo.IsFetchAutomaticallyAsync("upstream"));
        Assert.True((await repo.SetFetchAutomaticallyAsync(NewJob(), "upstream", false)).Success);
        Assert.False(await repo.IsFetchAutomaticallyAsync("upstream"));
        Assert.True((await repo.SetFetchAutomaticallyAsync(NewJob(), "upstream", true)).Success);
        Assert.Equal("false", s.Local.Git("config", "remote.upstream.disableimplicitfetch"));
        Assert.True(await repo.IsFetchAutomaticallyAsync("upstream"));

        Assert.True((await repo.RemoveRemoteAsync(NewJob(), "upstream")).Success);
        Assert.DoesNotContain(await repo.ReadRemotesAsync(), r => r.Name == "upstream");
    }
}
