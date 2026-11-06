using System.Diagnostics;
using GerGit.Core;
using GerGit.Core.Git;

namespace GerGit.Tests.Core;

/// <summary>A smudge filter on one path whose first run (the first time git writes that file) waits until
/// <see cref="CancelWhenStartedAsync"/> releases it. `git stash push` stores the stash first and then resets the tree,
/// which runs the filter: a cancel at that point is the one that used to leave the changes only in the stash (or a
/// half-reset tree). A filter on a file that only a rebase pick writes holds the rebase with `rebase-merge/` present.</summary>
sealed class StashBlock
{
    readonly string _started, _release;

    public StashBlock(TempRepo repo, string path)
    {
        var dir = Path.Combine(repo.Path, ".git");
        (_started, _release) = (Path.Combine(dir, "smudge.started"), Path.Combine(dir, "smudge.release"));
        var script = Path.Combine(dir, "smudge.sh");
        File.WriteAllText(script,
            $"if [ ! -e '{_started}' ]; then\n  touch '{_started}'\n  while [ ! -e '{_release}' ]; do sleep 0.05; done\nfi\nexec cat\n");
        repo.Git("config", "filter.block.smudge", $"sh '{script}'");
        Directory.CreateDirectory(Path.Combine(dir, "info"));
        File.WriteAllText(Path.Combine(dir, "info", "attributes"), path + " filter=block\n");
    }

    /// <summary>Waits until the filter runs, cancels <paramref name="job"/>, then lets the filter go on.
    /// <paramref name="pump"/> runs the UI jobs between polls (headless tests).</summary>
    public async Task CancelWhenStartedAsync(Func<Job> job, Action? pump = null)
    {
        var sw = Stopwatch.StartNew();
        while (!File.Exists(_started))
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException();
            await Task.Delay(20);
            pump?.Invoke();
        }
        job().Cancel();
        File.WriteAllText(_release, "");
    }
}

/// <summary>Shared building blocks of the ref operations: the dirty check, stashing and reapplying local changes (also
/// after a cancel), and updating submodules after an operation.</summary>
public class OpsHelperTests
{
    static StatusEntry E(ChangeKind kind, bool staged = false, bool sub = false) => new("p", null, kind, staged, sub);

    [Fact]
    public void IsDirty_PerLocalChangesBlock()
    {
        Assert.False(GitRepository.IsDirty(new([], [])));
        Assert.False(GitRepository.IsDirty(new([E(ChangeKind.Untracked), E(ChangeKind.Ignored)], [])));
        Assert.False(GitRepository.IsDirty(new([E(ChangeKind.Modified, sub: true)], [E(ChangeKind.Modified, true, true)])));
        Assert.True(GitRepository.IsDirty(new([E(ChangeKind.Modified)], [])));
        Assert.True(GitRepository.IsDirty(new([E(ChangeKind.Conflicted)], [])));
        Assert.True(GitRepository.IsDirty(new([], [E(ChangeKind.Added, true)])));
    }

    [Fact]
    public void StashTimestamp_IsIsoLocal() =>
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}$", GitRepository.StashTimestamp());

    [Fact]
    public async Task StashPush_ThenPop_RoundTrip()
    {
        using var repo = new TempRepo();
        repo.Commit("init", 0, ("a.txt", "1\n"));
        repo.Write("a.txt", "2\n");
        repo.Write("new.txt", "untracked\n");
        var git = await GitRepository.OpenAsync(repo.Path);
        var job = new Job("Pull 'origin/main'");

        var (r, sha) = await git.StashPushAsync(job, "Pull autostash 2026-10-30 12:00:00");
        Assert.True(r.Success);
        Assert.Equal(repo.Git("rev-parse", "refs/stash"), sha);
        Assert.Equal("1\n", File.ReadAllText(Path.Combine(repo.Path, "a.txt")));
        Assert.True(File.Exists(Path.Combine(repo.Path, "new.txt"))); // tracked changes only
        Assert.Equal("Pull autostash 2026-10-30 12:00:00", Assert.Single(await git.ReadStashesAsync()).ShortSubject);

        var applied = await git.ApplyStashAsync(job, "stash@{0}", sha!, pop: true);
        Assert.True(applied.Result.Success);
        Assert.False(applied.Conflict);
        Assert.Equal("2\n", File.ReadAllText(Path.Combine(repo.Path, "a.txt")));
        Assert.Empty(await git.ReadStashesAsync());
        Assert.Contains("$ git stash push --message \"Pull autostash 2026-10-30 12:00:00\"\n", job.Output);
        Assert.Contains("$ git stash pop --index refs/stash@{0}\n", job.Output);
    }

    [Fact]
    public async Task Cancel_during_the_pull_autostash_push_still_reapplies_the_changes()
    {
        using var s = new RemoteSetup();
        s.Local.Write("a.txt", "local edit\n");
        var block = new StashBlock(s.Local, "a.txt");
        var git = await s.OpenAsync();
        var job = new Job("Pull 'origin/main'");

        var run = git.PullAsync(job, "origin", null, rebase: false, autostash: true, tags: false);
        await block.CancelWhenStartedAsync(() => job);
        var r = await run;

        Assert.True(r.Cancelled);
        Assert.Equal("local edit\n", File.ReadAllText(Path.Combine(s.Local.Path, "a.txt")));
        Assert.Empty(await git.ReadStashesAsync());
        Assert.Contains("$ git stash pop --index refs/stash@{0}\n", job.Output);
        Assert.DoesNotContain("$ git pull", job.Output);
    }

    [Fact]
    public async Task Cancel_during_the_pull_rebase_keeps_the_autostash_with_the_hint()
    {
        using var s = new RemoteSetup();
        s.OtherPush("theirs", 2, "main", ("b.txt", "theirs\n"));
        s.Local.Commit("mine", 3, ("c.txt", "mine\n")); // the pick of "mine" writes c.txt: held there
        s.Local.Write("a.txt", "local edit\n");
        var block = new StashBlock(s.Local, "c.txt");
        var git = await s.OpenAsync();
        var job = new Job("Pull 'origin/main'");

        var run = git.PullAsync(job, "origin", null, rebase: true, autostash: true, tags: false);
        await block.CancelWhenStartedAsync(() => job);
        var r = await run;

        Assert.True(r.Cancelled);
        Assert.True(Directory.Exists(Path.Combine(s.Local.Path, ".git", "rebase-merge"))); // the cancel left the rebase
        Assert.DoesNotContain("stash pop", job.Output);
        Assert.Matches(@"gergit: your local changes are kept in stash@\{0\} 'Pull autostash [^']+'\n", job.Output);
        Assert.StartsWith("Pull autostash ", Assert.Single(await git.ReadStashesAsync()).ShortSubject);
        Assert.Equal("a\n", File.ReadAllText(Path.Combine(s.Local.Path, "a.txt"))); // not popped onto the half rebase
    }

    [Fact]
    public async Task Cancel_during_the_leave_as_stash_push_keeps_the_stash_with_the_hint()
    {
        using var repo = new TempRepo();
        repo.Commit("init", 0, ("a.txt", "1\n"));
        repo.Git("branch", "other");
        repo.Write("a.txt", "2\n");
        var block = new StashBlock(repo, "a.txt");
        var git = await GitRepository.OpenAsync(repo.Path);
        var job = new Job("Checkout 'other'");

        var run = git.RunWithAutostashAsync(job, LocalChangesMode.LeaveAsStash, "Autostash x",
            () => GitRunner.RunJobAsync(job, repo.Path, ["checkout", "other"]));
        await block.CancelWhenStartedAsync(() => job);
        var r = await run;

        Assert.True(r.Cancelled);
        Assert.Equal("main", repo.Git("branch", "--show-current"));
        Assert.Equal("1\n", File.ReadAllText(Path.Combine(repo.Path, "a.txt"))); // fully reset, not half
        Assert.Equal("Autostash x", Assert.Single(await git.ReadStashesAsync()).ShortSubject);
        Assert.Contains("gergit: your local changes are kept in stash@{0} 'Autostash x'\n", job.Output);
    }

    [Fact]
    public async Task ApplyStash_of_a_shifted_index_does_nothing()
    {
        using var repo = new TempRepo();
        repo.Commit("init", 0, ("a.txt", "1\n"));
        repo.Write("a.txt", "mine\n");
        repo.Git("stash", "push", "-m", "mine");
        var git = await GitRepository.OpenAsync(repo.Path);
        var shown = Assert.Single(await git.ReadStashesAsync());
        repo.Write("a.txt", "other job\n");
        repo.Git("stash", "push", "-m", "other job"); // e.g. a quick pull's autostash: "mine" is stash@{1} now

        var job = new Job("Apply stash");
        var applied = await git.ApplyStashAsync(job, shown.Name, shown.Sha, pop: true);
        Assert.False(applied.Result.Success);
        Assert.Equal(GitRepository.StashListChanged, applied.Result.Stderr);
        Assert.DoesNotContain("stash pop", job.Output);
        Assert.Equal("1\n", File.ReadAllText(Path.Combine(repo.Path, "a.txt")));
        Assert.Equal(2, (await git.ReadStashesAsync()).Count);
    }

    [Fact]
    public async Task ApplyStash_IndexConflict_RetriesWithoutIndex_AndReportsMergeConflict()
    {
        using var repo = new TempRepo();
        repo.Commit("init", 0, ("a.txt", "1\n"));
        repo.Write("a.txt", "2\n");
        repo.Git("add", "a.txt");
        repo.Git("stash", "push");
        repo.Commit("three", 1, ("a.txt", "3\n"));
        var git = await GitRepository.OpenAsync(repo.Path);
        var job = new Job("Apply stash 'stash@{0}'");
        job.Cancel(); // applying a stash runs even after a cancel: a half-applied stash is worse

        var applied = await git.ApplyStashAsync(job, "stash@{0}", repo.Git("rev-parse", "refs/stash"), pop: true);
        Assert.False(applied.Result.Success);
        Assert.True(applied.Conflict);
        Assert.Contains("$ git stash pop --index refs/stash@{0}\n", job.Output);
        Assert.Contains("$ git stash pop refs/stash@{0}\n", job.Output);
        Assert.Single(await git.ReadStashesAsync()); // git keeps the stash on conflict
    }

    /// <summary>Parent at p2 (submodule "sub" at s2); p1 records s1. Returns (parent, sub repo, s1).</summary>
    static (TempRepo Parent, TempRepo Sub, string S1) SubmoduleRepos()
    {
        var sub = new TempRepo();
        var s1 = sub.Commit("s1", 0, ("s.txt", "1\n"));
        var s2 = sub.Commit("s2", 1, ("s.txt", "2\n"));
        var parent = new TempRepo();
        parent.Git("-c", "protocol.file.allow=always", "submodule", "add", "-q", sub.Path, "sub");
        parent.GitEnv([], "-C", "sub", "checkout", "-q", s1);
        parent.Commit("p1", 0);
        parent.GitEnv([], "-C", "sub", "checkout", "-q", s2);
        parent.Commit("p2", 1);
        return (parent, sub, s1);
    }

    [Fact]
    public async Task Submodules_UpdatedAfterTheOperation()
    {
        var (parent, sub, s1) = SubmoduleRepos();
        using var _p = parent;
        using var _s = sub;
        var git = await GitRepository.OpenAsync(parent.Path);
        var before = await git.CaptureSubmodulesAsync();
        Assert.Empty(before.Changed);

        parent.Git("checkout", "-q", "HEAD~1"); // moves the gitlink to s1, the submodule stays at s2
        var job = new Job("Checkout");
        var r = await git.UpdateSubmodulesAsync(job, before);

        Assert.NotNull(r);
        Assert.True(r.Success, r.ReadableOutput);
        Assert.Equal(s1, parent.GitEnv([], "-C", "sub", "rev-parse", "HEAD"));
        Assert.Equal("Updating submodules...", job.Message);
        Assert.Contains("$ git --literal-pathspecs submodule update -- sub\n", job.Output);
    }

    [Fact]
    public async Task Submodules_ChangedBefore_OrNone_NothingRuns()
    {
        var (parent, sub, s1) = SubmoduleRepos();
        using var _p = parent;
        using var _s = sub;
        var git = await GitRepository.OpenAsync(parent.Path);

        parent.GitEnv([], "-C", "sub", "checkout", "-q", s1); // shown as changed before the operation
        var dirty = await git.CaptureSubmodulesAsync();
        Assert.Equal(["sub"], dirty.Changed);
        Assert.Null(await git.UpdateSubmodulesAsync(new Job("x"), dirty));

        using var plain = new TempRepo();
        plain.Commit("init", 0);
        var plainGit = await GitRepository.OpenAsync(plain.Path);
        Assert.Null(await plainGit.UpdateSubmodulesAsync(new Job("x"), await plainGit.CaptureSubmodulesAsync()));
    }
}
