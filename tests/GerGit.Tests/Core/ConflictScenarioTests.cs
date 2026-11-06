using GerGit.Core;
using GerGit.Core.Conflicts;
using GerGit.Core.Git;
using static GerGit.Tests.Core.ConflictStateTests;

namespace GerGit.Tests.Core;

/// <summary>abort, continue, resolve, reset to unmerged, stage gate and the resolver round trip on real conflicts.</summary>
public class ConflictScenarioTests
{
    static string Read(TempRepo r, string path) => File.ReadAllText(Path.Combine(r.Path, path));

    // ---------------------------------------------------------------- abort

    public static TheoryData<string> AbortCases => ["merge", "rebase", "cherry-pick", "multi-pick", "sequence", "revert", "am", "squash", "unmerged"];

    [Theory]
    [MemberData(nameof(AbortCases))]
    public async Task Abort_restores_head_branch_and_working_tree(string kind)
    {
        using var repo = Diverged();
        repo.Git("checkout", "-q", "side");
        repo.Commit("side 2", 30, ("g.txt", "g\n"));
        var startBranch = kind == "rebase" ? "side" : "main";
        repo.Git("checkout", "-q", startBranch);
        var head = repo.Git("rev-parse", "HEAD");
        var tree = Read(repo, "f.txt");
        string? stash = null;
        switch (kind)
        {
            case "merge": GitFails(repo, "merge", "side"); break;
            case "rebase": GitFails(repo, "rebase", "main"); break;
            case "cherry-pick": GitFails(repo, "cherry-pick", "side~1"); break;
            case "multi-pick": GitFails(repo, "cherry-pick", "main..side"); break; // CHERRY_PICK_HEAD plus sequencer/
            case "sequence":
                GitFails(repo, "cherry-pick", "main..side");
                repo.Git("checkout", "-q", "--theirs", "f.txt");
                repo.Git("add", "f.txt");
                repo.Git("commit", "-q", "--no-edit"); // only sequencer/ is left
                // git does not rewind a commit made outside the sequencer ("You seem to have moved HEAD"): it stays.
                head = repo.Git("rev-parse", "HEAD");
                tree = Read(repo, "f.txt");
                break;
            case "revert":
                repo.Commit("again", 40, ("f.txt", "a\nO2\nc\n"));
                head = repo.Git("rev-parse", "HEAD");
                tree = Read(repo, "f.txt");
                GitFails(repo, "revert", "--no-edit", "HEAD~1");
                break;
            case "am":
                var dir = Path.Combine(repo.Path, ".git", "patches");
                repo.Git("format-patch", "-q", "-1", "side~1", "-o", dir);
                GitFails(repo, "am", "-3", Directory.GetFiles(dir).Single());
                break;
            case "squash": GitFails(repo, "merge", "--squash", "side"); break;
            case "unmerged":
                repo.Write("f.txt", "a\nstashed\nc\n");
                repo.Git("stash", "push", "-q");
                repo.Commit("change", 40, ("f.txt", "a\ncommitted\nc\n"));
                head = repo.Git("rev-parse", "HEAD");
                tree = Read(repo, "f.txt");
                GitFails(repo, "stash", "pop");
                stash = "kept";
                break;
        }

        var git = await GitRepository.OpenAsync(repo.Path);
        var state = await StateAsync(git);
        Assert.NotEqual(RepoStateKind.None, state.Kind);
        var r = await git.AbortAsync(new Job("Abort"), state);
        Assert.True(r.Success, r.ReadableOutput);

        Assert.Equal(RepoState.Clean, await StateAsync(git));
        Assert.Equal(head, repo.Git("rev-parse", "HEAD"));
        Assert.Equal(startBranch, repo.Git("rev-parse", "--abbrev-ref", "HEAD"));
        Assert.Equal(tree, Read(repo, "f.txt"));
        Assert.Equal("", repo.Git("status", "--porcelain", "--untracked-files=no"));
        if (stash is not null) Assert.Single(await git.ReadStashesAsync()); // pop kept the stash on its conflict
    }

    [Fact]
    public async Task Abort_failure_returns_the_result()
    {
        using var repo = Diverged();
        var git = await GitRepository.OpenAsync(repo.Path);
        var r = await git.AbortAsync(new Job("Abort"), new RepoState(RepoStateKind.Revert, []));
        Assert.False(r.Success); // no revert in progress
        Assert.Equal(RepoState.Clean, await StateAsync(git));
    }

    // ---------------------------------------------------------------- resolve with one side

    [Theory]
    [InlineData(true, "a\nO\nc\n")]
    [InlineData(false, "a\nT\nc\n")]
    public async Task Resolve_using_one_side(bool ours, string expected)
    {
        using var repo = Diverged();
        GitFails(repo, "merge", "side");
        repo.Write("other.txt", "untouched\n");
        var git = await GitRepository.OpenAsync(repo.Path);
        var state = await StateAsync(git);
        // Paths that are not unmerged are skipped.
        var r = await git.ResolveWithSideAsync(new Job("Resolve"), state, ["other.txt", "f.txt"], ours);
        Assert.True(r.Success, r.ReadableOutput);
        Assert.Equal(expected, Read(repo, "f.txt"));
        Assert.Equal(expected, repo.Git("show", ":f.txt") + "\n");
        var s = await StateAsync(git);
        Assert.Equal(RepoStateKind.Merge, s.Kind);
        Assert.Empty(s.Unmerged);
        Assert.Equal("?? other.txt", repo.Git("status", "--porcelain", "--", "other.txt"));
    }

    [Fact]
    public async Task Resolve_using_the_deleting_side_removes_the_file()
    {
        using var repo = new TempRepo();
        repo.Commit("base", 0, ("d.txt", "x\n"), ("keep.txt", "k\n"));
        repo.Git("checkout", "-q", "-b", "side");
        repo.Commit("side", 10, ("d.txt", "modified\n"));
        repo.Git("checkout", "-q", "main");
        repo.Git("rm", "-q", "d.txt");
        repo.Commit("main deletes", 20);
        GitFails(repo, "merge", "side");
        var git = await GitRepository.OpenAsync(repo.Path);
        var state = await StateAsync(git);
        Assert.True(Assert.Single(state.Unmerged).OursDeleted);
        Assert.False(await git.PassesStageGateAsync(["d.txt"])); // "* Unmerged path"

        var r = await git.ResolveWithSideAsync(new Job("Resolve"), state, ["d.txt"], ours: true);
        Assert.True(r.Success, r.ReadableOutput);
        Assert.False(File.Exists(Path.Combine(repo.Path, "d.txt")));
        Assert.Empty((await StateAsync(git)).Unmerged);
    }

    [Fact]
    public async Task Resolve_stops_at_the_first_error()
    {
        using var repo = Diverged();
        GitFails(repo, "merge", "side");
        var git = await GitRepository.OpenAsync(repo.Path);
        var state = await StateAsync(git);
        // A stale state: the file claims a theirs stage git does not have.
        var bogus = state with { Unmerged = [new UnmergedFile("missing.txt", 'U', 'U', false), state.Unmerged[0]] };
        var r = await git.ResolveWithSideAsync(new Job("Resolve"), bogus, ["missing.txt", "f.txt"], ours: false);
        Assert.False(r.Success);
        Assert.Single((await StateAsync(git)).Unmerged); // f.txt untouched
    }

    [Fact]
    public async Task Resolve_submodule_conflict_checks_out_the_chosen_commit()
    {
        using var sub = new TempRepo();
        sub.Commit("s1", 0);
        sub.Git("checkout", "-q", "-b", "x");
        var x = sub.Commit("x1", 10);
        sub.Git("checkout", "-q", "main");
        var m = sub.Commit("m1", 20);

        using var repo = new TempRepo();
        repo.Git("-c", "protocol.file.allow=always", "submodule", "add", "-q", sub.Path, "sm");
        string Sm(params string[] args) => repo.Git(["-C", "sm", .. args]);
        Sm("checkout", "-q", "main~1");
        repo.Commit("init", 0);
        repo.Git("checkout", "-q", "-b", "side");
        Sm("checkout", "-q", x);
        repo.Commit("side", 10);
        repo.Git("checkout", "-q", "main");
        Sm("checkout", "-q", m);
        repo.Commit("main", 20);
        GitFails(repo, "merge", "side");

        var git = await GitRepository.OpenAsync(repo.Path);
        var state = await StateAsync(git);
        var f = Assert.Single(state.Unmerged);
        Assert.True(f.IsSubmodule);
        Assert.Equal(MergeMode.Submodule, (await git.LoadMergeDocumentAsync("sm")).Mode);
        Assert.False(await git.PassesStageGateAsync(["sm"]));

        // Uninitialised submodule folder: no checkout in the superproject, a clear failure instead.
        var gitFile = Path.Combine(repo.Path, "sm", ".git");
        File.Move(gitFile, gitFile + ".off");
        var head = repo.Git("rev-parse", "HEAD");
        var off = await git.ResolveWithSideAsync(new Job("Resolve"), state, ["sm"], ours: false);
        Assert.False(off.Success);
        Assert.Equal("Submodule 'sm' is not initialized", off.Stderr);
        Assert.Equal(head, repo.Git("rev-parse", "HEAD"));
        File.Move(gitFile + ".off", gitFile);

        var r = await git.ResolveWithSideAsync(new Job("Resolve"), state, ["sm"], ours: false);
        Assert.True(r.Success, r.ReadableOutput);
        Assert.Equal(x, Sm("rev-parse", "HEAD"));
        Assert.Empty((await StateAsync(git)).Unmerged);
        Assert.Contains(x, repo.Git("ls-files", "-s", "sm"));
    }

    // ---------------------------------------------------------------- reset to unmerged, stage gate

    [Fact]
    public async Task Reset_file_to_unmerged_state()
    {
        using var repo = Diverged();
        GitFails(repo, "merge", "side");
        var git = await GitRepository.OpenAsync(repo.Path);
        await git.ResolveWithSideAsync(new Job("Resolve"), await StateAsync(git), ["f.txt"], ours: true);
        Assert.Empty((await StateAsync(git)).Unmerged);

        var r = await git.ResetToUnmergedAsync(new Job("Reset"), "f.txt");
        Assert.True(r.Success, r.ReadableOutput);
        Assert.Single((await StateAsync(git)).Unmerged);
        Assert.Contains("<<<<<<<", Read(repo, "f.txt"));
    }

    [Fact]
    public async Task Reset_file_to_unmerged_state_with_a_locked_index()
    {
        using var repo = Diverged();
        GitFails(repo, "merge", "side");
        var git = await GitRepository.OpenAsync(repo.Path);
        await git.ResolveWithSideAsync(new Job("Resolve"), await StateAsync(git), ["f.txt"], ours: true);
        File.WriteAllText(Path.Combine(repo.Path, ".git", "index.lock"), "");
        var r = await git.ResetToUnmergedAsync(new Job("Reset"), "f.txt");
        Assert.False(r.Success);
        Assert.Equal(GitErrorKind.IndexLocked, GitErrors.Classify(r).Kind);
    }

    [Fact]
    public async Task Stage_gate_refuses_markers_and_accepts_a_resolved_file()
    {
        using var repo = Diverged();
        repo.Git("checkout", "-q", "side");
        repo.Commit("side 2", 30, ("g.txt", "a\nT\nc\n"));
        repo.Git("checkout", "-q", "main");
        repo.Commit("main 2", 40, ("g.txt", "a\nO\nc\n"));
        GitFails(repo, "merge", "side");
        repo.Write("new.txt", "n\n");
        var git = await GitRepository.OpenAsync(repo.Path);

        // g.txt is resolved by hand, f.txt still has markers: nothing at all is staged.
        repo.Write("g.txt", "a\nboth\nc\n");
        Assert.False(await git.StageAsync(["new.txt", "g.txt", "f.txt"]));
        Assert.Equal(2, (await StateAsync(git)).Unmerged.Count);
        Assert.Contains("?? new.txt", repo.Git("status", "--porcelain"));

        Assert.True(await git.StageAsync(["new.txt", "g.txt"]));
        var s = await StateAsync(git);
        Assert.Equal("f.txt", Assert.Single(s.Unmerged).Path);
        Assert.False(await git.IsConflictResolvedAsync(s.Unmerged[0]));
        Assert.Equal("a\nboth\nc\n", repo.Git("show", ":g.txt") + "\n");
    }

    [Fact]
    public async Task Stage_gate_refuses_binary_conflicts()
    {
        using var repo = new TempRepo();
        repo.Commit("base", 0, ("b.bin", "\0\u0001base"));
        repo.Git("checkout", "-q", "-b", "side");
        repo.Commit("side", 10, ("b.bin", "\0\u0002theirs"));
        repo.Git("checkout", "-q", "main");
        repo.Commit("main", 20, ("b.bin", "\0\u0003ours"));
        GitFails(repo, "merge", "side");
        var git = await GitRepository.OpenAsync(repo.Path);
        Assert.Equal(MergeMode.Binary, (await git.LoadMergeDocumentAsync("b.bin")).Mode);
        Assert.False(await git.StageAsync(["b.bin"]));

        var r = await git.ResolveWithSideAsync(new Job("Resolve"), await StateAsync(git), ["b.bin"], ours: false);
        Assert.True(r.Success, r.ReadableOutput);
        Assert.Equal("\0\u0002theirs", Read(repo, "b.bin"));
    }

    // ---------------------------------------------------------------- resolver round trip, continue

    [Fact]
    public async Task Merge_resolved_through_the_model_then_committed()
    {
        using var repo = new TempRepo();
        repo.Commit("base", 0, ("f.txt", "top\na\nb\nc\nend\n"));
        repo.Git("checkout", "-q", "-b", "side");
        repo.Commit("side", 10, ("f.txt", "top\na\nT1\nT2\nc\nend\nside-tail\n"));
        repo.Git("checkout", "-q", "main");
        repo.Commit("main", 20, ("f.txt", "main-head\ntop\na\nO\nc\nend\n"));
        GitFails(repo, "merge", "side");
        var git = await GitRepository.OpenAsync(repo.Path);

        var doc = await git.LoadMergeDocumentAsync("f.txt");
        Assert.Equal(MergeMode.Text, doc.Mode);
        Assert.False(doc.Failed);
        Assert.False(doc.NoNewlineAtEnd);
        var c = doc.Conflicts.Single();
        Assert.Equal(("HEAD", "side"), (c.OursLabel, c.TheirsLabel));
        doc.ToggleLine(c, c.Theirs[1]);
        doc.ToggleLine(c, c.Ours[0]);
        c.Result[1].Edit("O edited");
        Assert.True(doc.AllResolved);
        var text = doc.BuildResult();
        Assert.Equal("main-head\ntop\na\nT2\nO edited\nc\nend\nside-tail\n", text);

        var r = await git.WriteResolvedAsync(new Job("Resolve"), "f.txt", text);
        Assert.True(r.Success, r.ReadableOutput);
        Assert.Equal(text, Read(repo, "f.txt"));
        Assert.Equal(text, repo.Git("show", ":f.txt") + "\n");
        var state = await StateAsync(git);
        Assert.Equal(RepoStateKind.Merge, state.Kind);
        Assert.Empty(state.Unmerged);

        // Continue for a merge = the normal commit flow.
        Assert.True((await git.CommitAsync("Merge side", amend: false, noVerify: false)).Success);
        Assert.Equal(RepoState.Clean, await StateAsync(git));
        Assert.Equal(2, repo.Git("rev-list", "--parents", "-n1", "HEAD").Split(' ').Length - 1);
    }

    [Fact]
    public async Task No_newline_at_end_is_kept()
    {
        using var repo = new TempRepo();
        repo.Commit("base", 0, ("f.txt", "k\nlast"));
        repo.Git("checkout", "-q", "-b", "side");
        repo.Commit("side", 10, ("f.txt", "k\nlastT"));
        repo.Git("checkout", "-q", "main");
        repo.Commit("main", 20, ("f.txt", "k\nlastO"));
        GitFails(repo, "merge", "side");
        // git's conflict file ends with the marker line; resolve by hand-editing the markers out without a newline.
        var git = await GitRepository.OpenAsync(repo.Path);
        var doc = await git.LoadMergeDocumentAsync("f.txt");
        Assert.False(doc.NoNewlineAtEnd); // the conflict file ends with ">>>>>>> side\n"
        repo.Write("f.txt", Read(repo, "f.txt").TrimEnd('\n'));
        doc = await git.LoadMergeDocumentAsync("f.txt");
        Assert.True(doc.NoNewlineAtEnd);
        doc.SetSide(MergeSide.Theirs, true);
        Assert.False(doc.BuildResult().EndsWith('\n'));
    }

    [Fact]
    public async Task Cherry_pick_sequence_continue_skips_an_empty_pick()
    {
        using var repo = Diverged();
        repo.Git("checkout", "-q", "side");
        repo.Commit("side 2", 30, ("g.txt", "g\n"));
        repo.Git("checkout", "-q", "main");
        var head = repo.Git("rev-parse", "HEAD");
        GitFails(repo, "cherry-pick", "main..side");
        var git = await GitRepository.OpenAsync(repo.Path);

        // Taking ours makes the first pick empty: continue must skip it and apply "side 2".
        Assert.True((await git.ResolveWithSideAsync(new Job("Resolve"), await StateAsync(git), ["f.txt"], ours: true)).Success);
        var job = new Job("Continue");
        var r = await git.ContinueCherryPickAsync(job);
        Assert.True(r.Success, job.Output);
        Assert.Contains("cherry-pick --skip", job.Output);
        Assert.Equal(RepoState.Clean, await StateAsync(git));
        Assert.Equal(head, repo.Git("rev-parse", "HEAD~1"));
        Assert.Equal("side 2", repo.Git("log", "-1", "--format=%s"));
    }

    [Fact]
    public async Task Am_continue_after_resolving()
    {
        using var repo = Diverged();
        var dir = Path.Combine(repo.Path, ".git", "patches");
        repo.Git("format-patch", "-q", "-1", "side", "-o", dir);
        GitFails(repo, "am", "-3", Directory.GetFiles(dir).Single());
        var git = await GitRepository.OpenAsync(repo.Path);
        Assert.True((await git.ResolveWithSideAsync(new Job("Resolve"), await StateAsync(git), ["f.txt"], ours: false)).Success);
        var r = await git.ContinueAmAsync(new Job("Continue"));
        Assert.True(r.Success, r.ReadableOutput);
        Assert.Equal(RepoState.Clean, await StateAsync(git));
        Assert.Equal("side change", repo.Git("log", "-1", "--format=%s"));
    }

    [Fact]
    public async Task Am_continue_with_nothing_left_skips()
    {
        using var repo = Diverged();
        var dir = Path.Combine(repo.Path, ".git", "patches");
        repo.Git("format-patch", "-q", "-1", "side", "-o", dir);
        var head = repo.Git("rev-parse", "HEAD");
        GitFails(repo, "am", "-3", Directory.GetFiles(dir).Single());
        var git = await GitRepository.OpenAsync(repo.Path);
        Assert.True((await git.ResolveWithSideAsync(new Job("Resolve"), await StateAsync(git), ["f.txt"], ours: true)).Success);
        var job = new Job("Continue");
        var r = await git.ContinueAmAsync(job);
        Assert.True(r.Success, job.Output);
        Assert.Contains("am --skip", job.Output);
        Assert.Equal(RepoState.Clean, await StateAsync(git));
        Assert.Equal(head, repo.Git("rev-parse", "HEAD"));
    }

    // ---------------------------------------------------------------- card commit lists

    [Fact]
    public async Task Conflict_card_commit_lists()
    {
        using var repo = Diverged();
        repo.Git("checkout", "-q", "side");
        repo.Commit("side unrelated", 30, ("u.txt", "u\n"));
        repo.Git("checkout", "-q", "main");
        GitFails(repo, "merge", "side");
        var git = await GitRepository.OpenAsync(repo.Path);
        var state = await StateAsync(git);
        var lists = await git.ReadConflictCommitsAsync(state, "f.txt");
        Assert.Equal(["side change"], lists.Theirs.Select(c => c.Subject));
        Assert.Equal(["main change"], lists.Ours.Select(c => c.Subject));
        Assert.Equal("Test User", lists.Ours[0].AuthorName);

        var none = await git.ReadConflictCommitsAsync(state with { Kind = RepoStateKind.Squash }, "f.txt");
        Assert.Empty(none.Theirs);
        Assert.Empty(none.Ours);
    }

    [Fact]
    public async Task Conflict_card_commit_lists_for_cherry_pick_show_only_the_picked_commit()
    {
        using var repo = Diverged();
        repo.Git("checkout", "-q", "side");
        repo.Commit("side 2", 30, ("f.txt", "a\nT2\nc\n"));
        repo.Git("checkout", "-q", "main");
        GitFails(repo, "cherry-pick", "side");
        var git = await GitRepository.OpenAsync(repo.Path);
        var lists = await git.ReadConflictCommitsAsync(await StateAsync(git), "f.txt");
        Assert.Equal(["side 2"], lists.Theirs.Select(c => c.Subject));
        Assert.Equal(["main change"], lists.Ours.Select(c => c.Subject));
    }
}
