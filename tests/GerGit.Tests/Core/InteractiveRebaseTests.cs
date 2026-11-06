using GerGit.Core;
using GerGit.Core.Git;
using GerGit.Core.Rebase;

namespace GerGit.Tests.Core;

/// <summary>
/// Interactive rebase end to end through real git and the real editor helper (`GerGit --rebase-editor` over the askpass
/// socket of this test process). Shares the askpass server, so it runs in that collection.
/// </summary>
[Collection("Askpass")]
public sealed class InteractiveRebaseTests : IDisposable
{
    static readonly TimeSpan Limit = TimeSpan.FromSeconds(60);
    readonly TempRepo _repo = new();

    public InteractiveRebaseTests()
    {
        Askpass.Start();
        Assert.NotNull(Askpass.EditorPath);
    }

    public void Dispose()
    {
        Askpass.Stop();
        _repo.Dispose();
    }

    async Task<GitRepository> Open() => await GitRepository.OpenAsync(_repo.Path);

    /// <summary>Full messages, newest first.</summary>
    string[] Messages(string rev = "HEAD") =>
        _repo.Git("log", "--format=%B%x1e", rev).Split('\x1e', StringSplitOptions.RemoveEmptyEntries)
            .Select(m => m.Trim()).Where(m => m.Length > 0).ToArray();

    static async Task<(InteractiveRebaseSession Session, RebaseTodo? Todo)> Start(GitRepository repo, string branch, string? target,
        Job job, TimeSpan? timeout = null)
    {
        var session = new InteractiveRebaseSession(repo, branch, target) { Timeout = timeout ?? TimeSpan.FromMinutes(15) };
        var todo = await session.StartAsync(job).WaitAsync(Limit);
        return (session, todo);
    }

    int Index(RebaseTodo todo, string sha) => todo.Rows.Select(r => r.Sha).ToList().IndexOf(sha);

    [Fact]
    public async Task ReorderRewordSquashFixupDrop_GivesTheExactHistory_UpdateRefsMovesTopic_BackupCreated()
    {
        var c1 = _repo.Commit("one", 1, ("f1", "1"));
        var c2 = _repo.Commit("two", 2, ("f2", "2"));
        var c3 = _repo.Commit("three", 3, ("f3", "3"));
        _repo.Git("branch", "topic");
        var c4 = _repo.Commit("four", 4, ("f4", "4"));
        var c5 = _repo.Commit("five\n\nbody five", 5, ("f5", "5"));
        var c6 = _repo.Commit("six", 6, ("f6", "6"));
        var c7 = _repo.Commit("seven", 7, ("f7", "7"));
        var repo = await Open();
        var job = new Job("rebase");

        var (session, todo) = await Start(repo, "main", c1, job);
        Assert.NotNull(todo);
        Assert.True(session.Waiting);
        Assert.Equal([c7, c6, c5, c4, c3, c2], todo.Rows.Select(r => r.Sha));
        Assert.Equal(["refs/heads/topic"], todo.Rows[Index(todo, c3)].Refs);
        Assert.Equal(("body five", "Test User"), (todo.Rows[Index(todo, c5)].Description, todo.Rows[0].AuthorName));

        todo.Reword(Index(todo, c7), "seven reworded\n\nnew body");
        todo.SetAction([Index(todo, c6)], RebaseAction.Drop);
        todo.SetAction([Index(todo, c5)], RebaseAction.Squash);
        todo.SetAction([Index(todo, c3)], RebaseAction.Fixup);
        todo.MoveTo([Index(todo, c7)], todo.Rows.Count - 1, after: true); // c7 becomes the oldest
        Assert.Equal([c6, c5, c4, c3, c2, c7], todo.Rows.Select(r => r.Sha));
        Assert.True(todo.Changed);

        await session.ApplyAsync(todo.Rows, updateRefs: true, backup: true);
        var outcome = await session.Completion.WaitAsync(Limit);

        Assert.Equal(new RebaseOutcome(true, null), outcome);
        Assert.Equal(["four\n\nfive\n\nbody five", "two", "seven reworded\n\nnew body", "one"], Messages());
        Assert.Equal("f1\nf7", _repo.Git("ls-tree", "--name-only", "HEAD~2"));
        Assert.Equal("f1\nf2\nf3\nf7", _repo.Git("ls-tree", "--name-only", "HEAD~1"));
        Assert.Equal("f1\nf2\nf3\nf4\nf5\nf7", _repo.Git("ls-tree", "--name-only", "HEAD"));
        Assert.Equal(_repo.Git("rev-parse", "HEAD~1"), _repo.Git("rev-parse", "topic")); // after the fixup line
        var backup = _repo.Git("for-each-ref", "--format=%(refname:short) %(objectname)", "refs/heads/backup/");
        Assert.Matches($@"^backup/main-\d\d-\d\d-\d\d {c7}$", backup);
        Assert.Equal("refs/heads/main", _repo.Git("symbolic-ref", "HEAD"));
        Assert.Equal("", _repo.Git("status", "--porcelain"));
        Assert.True(job.IsRunning); // the caller finishes its job
    }

    [Fact]
    public async Task WithoutUpdateRefs_TheDependentBranchStays()
    {
        var c1 = _repo.Commit("one", 1, ("f", "1"));
        var c2 = _repo.Commit("two", 2, ("g", "2"));
        _repo.Git("branch", "topic");
        _repo.Commit("three", 3, ("h", "3"));
        var repo = await Open();

        var (session, todo) = await Start(repo, "main", c1, new Job("rebase"));
        todo!.Reword(Index(todo, c2), "two!");
        await session.ApplyAsync(todo.Rows, updateRefs: false, backup: false);
        Assert.True((await session.Completion.WaitAsync(Limit)).Success);

        Assert.Equal(["three", "two!", "one"], Messages());
        Assert.Equal(c2, _repo.Git("rev-parse", "topic"));
        Assert.Equal("", _repo.Git("for-each-ref", "refs/heads/backup/"));
    }

    [Fact]
    public async Task SquashTargetWithCustomMessage_GetsOnlyThatMessage()
    {
        // git's own squash text would hold both messages and "# This is a combination of 2 commits.":
        // the result proves the helper wrote the archived message.
        var c1 = _repo.Commit("one", 1, ("f", "1"));
        var c2 = _repo.Commit("two", 2, ("g", "2"));
        var c3 = _repo.Commit("three", 3, ("h", "3"));
        var repo = await Open();

        var (session, todo) = await Start(repo, "main", c1, new Job("rebase"));
        todo!.SetAction([Index(todo, c3)], RebaseAction.Squash);
        Assert.Equal("two\n\nthree", todo.Rows[Index(todo, c2)].GroupMessage);
        todo.Reword(Index(todo, c2), "custom only");
        await session.ApplyAsync(todo.Rows, updateRefs: false, backup: false);
        Assert.Equal(new RebaseOutcome(true, null), await session.Completion.WaitAsync(Limit));

        Assert.Equal(["custom only", "one"], Messages());
        Assert.Equal("f\ng\nh", _repo.Git("ls-tree", "--name-only", "HEAD"));
    }

    [Fact]
    public async Task Root_RewritesTheFirstCommit()
    {
        var c1 = _repo.Commit("one", 1, ("f", "1"));
        _repo.Commit("two", 2, ("g", "2"));
        var repo = await Open();

        var (session, todo) = await Start(repo, "main", null, new Job("rebase"));
        Assert.Equal(c1, todo!.Rows[^1].Sha);
        todo.Reword(todo.Rows.Count - 1, "first");
        await session.ApplyAsync(todo.Rows, updateRefs: false, backup: false);
        Assert.True((await session.Completion.WaitAsync(Limit)).Success);
        Assert.Equal(["two", "first"], Messages());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancel_LeavesHeadAndTheRepositoryUntouched(bool changed)
    {
        var c1 = _repo.Commit("one", 1, ("f", "1"));
        _repo.Commit("two", 2, ("f", "2"));
        var head = _repo.Commit("three", 3, ("f", "3"));
        var repo = await Open();

        var (session, todo) = await Start(repo, "main", c1, new Job("rebase"));
        if (changed) todo!.SetAction([0], RebaseAction.Drop);
        Assert.Equal(changed, todo!.Changed);
        session.Cancel();
        await session.ApplyAsync(todo.Rows, true, true); // too late: nothing happens

        Assert.Equal(new RebaseOutcome(true, null), await session.Completion.WaitAsync(Limit));
        Assert.Equal(head, _repo.Git("rev-parse", "HEAD"));
        Assert.Equal("refs/heads/main", _repo.Git("symbolic-ref", "HEAD"));
        Assert.False(Directory.Exists(Path.Combine(_repo.Path, ".git", "rebase-merge")));
        Assert.Equal("", _repo.Git("status", "--porcelain"));
        Assert.Equal("", _repo.Git("for-each-ref", "refs/heads/backup/"));
    }

    [Fact]
    public async Task NothingToRebase_GivesAnEmptyList()
    {
        var head = _repo.Commit("one", 1, ("f", "1"));
        var repo = await Open();
        var (session, todo) = await Start(repo, "main", head, new Job("rebase"));
        Assert.Empty(todo!.Rows);
        session.Cancel();
        Assert.True((await session.Completion.WaitAsync(Limit)).Success);
        Assert.Equal(head, _repo.Git("rev-parse", "HEAD"));
    }

    [Fact]
    public async Task UnstagedChanges_FailBeforeAnyList()
    {
        var c1 = _repo.Commit("one", 1, ("f", "1"));
        _repo.Commit("two", 2, ("f", "2"));
        _repo.Write("f", "dirty");
        var repo = await Open();

        var (session, todo) = await Start(repo, "main", c1, new Job("rebase"));
        Assert.Null(todo);
        var outcome = await session.Completion.WaitAsync(Limit);
        Assert.False(outcome.Success);
        Assert.Contains("You have unstaged changes", outcome.Error!.Stderr);
    }

    [Fact]
    public async Task FailureBeforeGitStarts_EndsCompletion()
    {
        // A submodule whose .git points nowhere: `git status` (submodule capture) fails before the rebase starts.
        var c1 = _repo.Commit("one", 1, ("f", "1"));
        var head = _repo.Commit("two", 2, ("f", "2"));
        _repo.Write(".gitmodules", "[submodule \"sub\"]\n\tpath = sub\n\turl = ./x\n");
        _repo.Git("update-index", "--add", "--cacheinfo", $"160000,{head},sub");
        _repo.Git("add", ".gitmodules"); // not `add -A`: that would stage the missing gitlink's removal
        _repo.Git("commit", "-q", "-m", "sub");
        head = _repo.Git("rev-parse", "HEAD");
        _repo.Write("sub/.git", "gitdir: /nonexistent/gergit\n");
        var repo = await Open();

        var session = new InteractiveRebaseSession(repo, "main", c1);
        var e = await Assert.ThrowsAsync<GitException>(() => session.StartAsync(new Job("rebase")).WaitAsync(Limit));
        // git's own error, whose wording differs between versions ("not a git repository: ..." / "gitfile does not point
        // to a valid repository...").
        Assert.Matches("^fatal: .+", e.Message);
        Assert.Same(e, await Assert.ThrowsAsync<GitException>(() => session.Completion.WaitAsync(Limit)));
        Assert.False(session.Waiting);
        Assert.False(Directory.Exists(Path.Combine(_repo.Path, ".git", "rebase-merge")));
        Assert.Equal(head, _repo.Git("rev-parse", "HEAD"));

        // The registration was not left behind: a later session of the same repository is not refused.
        File.Delete(Path.Combine(_repo.Path, "sub", ".git"));
        var (next, todo) = await Start(repo, "main", c1, new Job("rebase"));
        Assert.NotNull(todo);
        next.Cancel();
        Assert.True((await next.Completion.WaitAsync(Limit)).Success);
    }

    [Fact]
    public async Task Timeout_AndJobCancel_AnswerCancel()
    {
        var c1 = _repo.Commit("one", 1, ("f", "1"));
        var head = _repo.Commit("two", 2, ("f", "2"));
        var repo = await Open();

        var (session, todo) = await Start(repo, "main", c1, new Job("rebase"), TimeSpan.FromSeconds(1));
        Assert.NotNull(todo);
        Assert.Equal(new RebaseOutcome(true, null), await session.Completion.WaitAsync(Limit));
        Assert.Equal(head, _repo.Git("rev-parse", "HEAD"));

        var job = new Job("rebase");
        (session, todo) = await Start(repo, "main", c1, job);
        Assert.NotNull(todo);
        job.Cancel();
        Assert.True((await session.Completion.WaitAsync(Limit)).Success);
        Assert.Equal(head, _repo.Git("rev-parse", "HEAD"));
        Assert.False(Directory.Exists(Path.Combine(_repo.Path, ".git", "rebase-merge")));
    }

    /// <summary>main: base → main change of f. feature (from base): f change (conflicts), g, h, i.</summary>
    (string Base, string F2, string G, string H, string I) ConflictSetup()
    {
        var b = _repo.Commit("base", 1, ("f", "base\n"));
        _repo.Git("checkout", "-q", "-b", "feature");
        var f2 = _repo.Commit("two", 2, ("f", "feature\n"));
        var g = _repo.Commit("three", 3, ("g", "g"));
        var h = _repo.Commit("four", 4, ("h", "h"));
        var i = _repo.Commit("five", 5, ("i", "i"));
        _repo.Git("checkout", "-q", "main");
        _repo.Commit("main change", 6, ("f", "main\n"));
        return (b, f2, g, h, i);
    }

    [Fact]
    public async Task ConflictStop_ThenContinue_StillAppliesArchivedRewordAndSquashMessages()
    {
        var (_, f2, g, h, i) = ConflictSetup();
        var repo = await Open();

        // Starts on main: the session checks out feature first.
        var (session, todo) = await Start(repo, "feature", "refs/heads/main", new Job("rebase"));
        Assert.Equal("refs/heads/feature", _repo.Git("symbolic-ref", "HEAD"));
        Assert.Equal([i, h, g, f2], todo!.Rows.Select(r => r.Sha));
        todo.SetAction([Index(todo, i)], RebaseAction.Squash);
        todo.Reword(Index(todo, g), "three reworded");
        await session.ApplyAsync(todo.Rows, updateRefs: false, backup: false);
        var outcome = await session.Completion.WaitAsync(Limit);
        Assert.Equal(new RebaseOutcome(true, null), outcome); // stopped for the conflict = success
        Assert.True(Directory.Exists(Path.Combine(_repo.Path, ".git", "rebase-merge")));

        _repo.Write("f", "resolved\n");
        _repo.Git("add", "f");
        var r = await repo.ContinueRebaseAsync(new Job("continue")).WaitAsync(Limit);
        Assert.True(r.Success, r.ReadableOutput);

        Assert.False(Directory.Exists(Path.Combine(_repo.Path, ".git", "rebase-merge")));
        Assert.Equal(["four\n\nfive", "three reworded", "two", "main change", "base"], Messages());
        Assert.Equal("resolved", _repo.Git("show", "HEAD~2:f"));
        Assert.Equal("refs/heads/feature", _repo.Git("symbolic-ref", "HEAD"));
    }

    [Fact]
    public async Task ConflictStop_ThenSkipWhenNothingIsLeftToCommit()
    {
        var (_, f2, _, _, _) = ConflictSetup();
        var repo = await Open();
        var (session, todo) = await Start(repo, "feature", "refs/heads/main", new Job("rebase"));
        await session.ApplyAsync(todo!.Rows, false, false);
        Assert.True((await session.Completion.WaitAsync(Limit)).Success);

        _repo.Git("checkout", "--ours", "f"); // take main's side: the commit becomes empty
        _repo.Git("add", "f");
        var r = await repo.ContinueRebaseAsync(new Job("continue")).WaitAsync(Limit);
        Assert.True(r.Success, r.ReadableOutput);
        Assert.Equal(["five", "four", "three", "main change", "base"], Messages());
        Assert.DoesNotContain(f2, _repo.Git("log", "--format=%H"));
    }

    [Fact]
    public async Task ConflictStop_ThenAbort_RestoresTheOriginalBranch()
    {
        ConflictSetup();
        var tip = _repo.Git("rev-parse", "feature");
        var repo = await Open();
        var (session, todo) = await Start(repo, "feature", "refs/heads/main", new Job("rebase"));
        await session.ApplyAsync(todo!.Rows, false, false);
        Assert.True((await session.Completion.WaitAsync(Limit)).Success);

        _repo.Git("rebase", "--abort"); // the Abort command itself is tested elsewhere
        Assert.Equal("refs/heads/feature", _repo.Git("symbolic-ref", "HEAD"));
        Assert.Equal(tip, _repo.Git("rev-parse", "HEAD"));
        Assert.Equal("", _repo.Git("status", "--porcelain"));
    }

    [Fact]
    public async Task EditorCommands_DropInheritedEditorVariables()
    {
        // Process-wide: this collection runs alone.
        var (editor, sequence) = (Environment.GetEnvironmentVariable("GIT_EDITOR"), Environment.GetEnvironmentVariable("GIT_SEQUENCE_EDITOR"));
        Environment.SetEnvironmentVariable("GIT_EDITOR", "inherited");
        Environment.SetEnvironmentVariable("GIT_SEQUENCE_EDITOR", "inherited");
        try
        {
            string[] args = ["-c", "core.editor=mine", "var", "GIT_EDITOR"];
            Assert.Equal("mine", (await GitRunner.RunJobAsync(new Job("x"), _repo.Path, args, editor: true)).Stdout.Trim());
            Assert.Equal("true", (await GitRunner.RunJobAsync(new Job("x"), _repo.Path, args)).Stdout.Trim());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GIT_EDITOR", editor);
            Environment.SetEnvironmentVariable("GIT_SEQUENCE_EDITOR", sequence);
        }
    }
}
