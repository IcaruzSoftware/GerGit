using GerGit.Core;
using GerGit.Core.Git;
using GerGit.Core.Rebase;

namespace GerGit.Tests.Core;

/// <summary>Simple rebase, its prediction and the quick-action targets.</summary>
public sealed class RebaseTests : IDisposable
{
    readonly TempRepo _repo = new();

    public void Dispose() => _repo.Dispose();

    async Task<GitRepository> Open() => await GitRepository.OpenAsync(_repo.Path);

    /// <summary>main: base, m (file <paramref name="mainFile"/>). feature from base: f1 (file f), f2 (file g); `part` at f1.</summary>
    (string Base, string Main, string F1, string F2) Setup(string mainFile = "m")
    {
        var b = _repo.Commit("base", 1, ("f", "base\n"));
        _repo.Git("checkout", "-q", "-b", "feature");
        var f1 = _repo.Commit("f1", 2, ("f", "feature\n"));
        _repo.Git("branch", "part");
        var f2 = _repo.Commit("f2", 3, ("g", "g"));
        _repo.Git("checkout", "-q", "main");
        var m = _repo.Commit("main", 4, (mainFile, "main\n"));
        return (b, m, f1, f2);
    }

    [Fact]
    public async Task Rebase_ChecksOutTheBranchAndReplaysIt()
    {
        var (_, m, _, f2) = Setup();
        var repo = await Open();
        var steps = new List<string>();
        var r = await repo.RebaseAsync(new Job("rebase"), "feature", "refs/heads/main", updateRefs: false, autostash: true, steps.Add);
        Assert.True(r.Success, r.ReadableOutput);
        Assert.Equal(["Checkout...", "Rebasing..."], steps); // clean tree: no stash
        Assert.Equal("refs/heads/feature", _repo.Git("symbolic-ref", "HEAD"));
        Assert.Equal(m, _repo.Git("rev-parse", "HEAD~2"));
        Assert.NotEqual(f2, _repo.Git("rev-parse", "HEAD"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UpdateRefs_MovesTheDependentBranchOnlyWhenAsked(bool updateRefs)
    {
        var (_, m, f1, _) = Setup();
        _repo.Git("checkout", "-q", "feature");
        var repo = await Open();
        var r = await repo.RebaseAsync(new Job("rebase"), "feature", "refs/heads/main", updateRefs, autostash: false);
        Assert.True(r.Success, r.ReadableOutput);
        Assert.Equal(updateRefs ? _repo.Git("rev-parse", "HEAD~1") : f1, _repo.Git("rev-parse", "part"));
        Assert.Equal(m, _repo.Git("rev-parse", "HEAD~2"));
    }

    [Fact]
    public async Task Autostash_IsPoppedAfterSuccess()
    {
        Setup();
        _repo.Git("checkout", "-q", "feature");
        _repo.Write("g", "dirty");
        var repo = await Open();
        var steps = new List<string>();
        var r = await repo.RebaseAsync(new Job("rebase"), "feature", "refs/heads/main", false, autostash: true, steps.Add);
        Assert.True(r.Success, r.ReadableOutput);
        Assert.Equal(["Stashing...", "Rebasing...", "Applying stash..."], steps);
        Assert.Equal("dirty", File.ReadAllText(Path.Combine(_repo.Path, "g")));
        Assert.Equal("", _repo.Git("stash", "list"));
    }

    [Fact]
    public async Task CancelBeforeTheRebase_PopsTheAutostash()
    {
        var (_, _, _, f2) = Setup();
        _repo.Write("f", "dirty"); // on main
        var head = _repo.Git("rev-parse", "HEAD");
        var repo = await Open();
        var job = new Job("rebase");
        job.Cancel(); // the stash push still runs, the checkout does not
        var r = await repo.RebaseAsync(job, "feature", "refs/heads/main", false, autostash: true);
        Assert.True(r.Cancelled);
        Assert.Equal("dirty", File.ReadAllText(Path.Combine(_repo.Path, "f")));
        Assert.Equal("", _repo.Git("stash", "list"));
        Assert.Equal((head, "refs/heads/main"), (_repo.Git("rev-parse", "HEAD"), _repo.Git("symbolic-ref", "HEAD")));
        Assert.Equal(f2, _repo.Git("rev-parse", "feature"));
    }

    [Fact]
    public async Task Conflict_StopsAsMergeConflict_AndKeepsTheStash()
    {
        Setup(mainFile: "f");
        _repo.Git("checkout", "-q", "feature");
        _repo.Write("g", "dirty");
        var repo = await Open();
        var job = new Job("rebase");
        var r = await repo.RebaseAsync(job, "feature", "refs/heads/main", false, autostash: true);
        Assert.False(r.Success);
        Assert.Equal(GitErrorKind.MergeConflict, GitErrors.Classify(r).Kind);
        Assert.True(Directory.Exists(Path.Combine(_repo.Path, ".git", "rebase-merge")));
        Assert.Contains("Rebase autostash", _repo.Git("stash", "list"));
        Assert.Contains($"{GitErrors.HintPrefix} your local changes are kept in stash@{{0}}", job.Output);
    }

    [Fact]
    public async Task Preview_PredictsAndListsBranchesInRange()
    {
        var (b, _, _, _) = Setup();
        _repo.Git("branch", "at-base", b);
        _repo.Git("checkout", "-q", "feature");
        var repo = await Open();

        var clean = await repo.PreviewRebaseAsync("refs/heads/feature", "refs/heads/main");
        Assert.Equal(RebasePrediction.NoConflicts, clean.Prediction);
        Assert.Equal(["part"], clean.BranchesInRange); // not feature itself, not at-base (the merge base)

        _repo.Git("checkout", "-q", "main");
        _repo.Commit("main f", 5, ("f", "other\n"));
        _repo.Git("checkout", "-q", "feature");
        Assert.Equal(RebasePrediction.Conflicts, (await repo.PreviewRebaseAsync("refs/heads/feature", "refs/heads/main")).Prediction);

        // Merge base = source tip: nothing to replay.
        Assert.Equal(RebasePrediction.NoConflicts, (await repo.PreviewRebaseAsync("refs/heads/at-base", "refs/heads/main")).Prediction);

        _repo.Git("checkout", "-q", "--orphan", "lonely");
        _repo.Commit("lonely", 6, ("x", "x"));
        var unrelated = await repo.PreviewRebaseAsync("refs/heads/lonely", "refs/heads/main");
        Assert.Equal(RebasePrediction.Unknown, unrelated.Prediction);

        var e = await Assert.ThrowsAsync<GitException>(() => repo.PreviewRebaseAsync("refs/heads/feature", "refs/heads/nope"));
        Assert.Equal("Cannot get destination sha for rebase", e.Message);
    }

    [Fact]
    public async Task QuickActionTargets_FollowTheTable()
    {
        var c1 = _repo.Commit("one", 1, ("f", "1"));
        var c2 = _repo.Commit("two", 2, ("f", "2"));
        var c3 = _repo.Commit("three", 3, ("f", "3"));
        var c4 = _repo.Commit("four", 4, ("f", "4"));
        var repo = await Open();

        Assert.Equal((true, (string?)null), await repo.QuickActionTargetAsync(QuickAction.Reword, [c1]));
        Assert.Equal((true, (string?)c2), await repo.QuickActionTargetAsync(QuickAction.Edit, [c3]));
        Assert.Equal((true, (string?)c1), await repo.QuickActionTargetAsync(QuickAction.Squash, [c4, c3]));
        Assert.Equal((true, (string?)null), await repo.QuickActionTargetAsync(QuickAction.Fixup, [c2]));
        Assert.False((await repo.QuickActionTargetAsync(QuickAction.Squash, [c1])).Possible);
        Assert.False((await repo.QuickActionTargetAsync(QuickAction.Drop, [c1])).Possible);
        Assert.Equal((true, (string?)c2), await repo.QuickActionTargetAsync(QuickAction.Drop, [c4, c3]));

        Assert.False(await repo.RangeHasMergeAsync(c1));
        _repo.Git("checkout", "-q", "-b", "side", c2);
        _repo.Commit("side", 5, ("s", "s"));
        _repo.Git("checkout", "-q", "main");
        _repo.Git("merge", "-q", "--no-ff", "-m", "merge", "side");
        Assert.True(await repo.RangeHasMergeAsync(c1));
        Assert.False(await repo.RangeHasMergeAsync("HEAD"));
    }
}
