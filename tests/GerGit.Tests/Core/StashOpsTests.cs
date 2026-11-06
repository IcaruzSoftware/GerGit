using GerGit.Core;
using GerGit.Core.Git;

namespace GerGit.Tests.Core;

/// <summary>Core commands: stash save / snapshot / partial / rename / drop, cherry-pick, revert, previews.</summary>
public class StashOpsTests
{
    static string Read(TempRepo r, string path) => File.ReadAllText(Path.Combine(r.Path, path));
    static bool Exists(TempRepo r, string path) => File.Exists(Path.Combine(r.Path, path));

    static async Task<(TempRepo Repo, GitRepository Git)> RepoAsync(params (string, string)[] files)
    {
        var repo = new TempRepo();
        repo.Commit("init", 0, files.Length > 0 ? files : [("a.txt", "1\n")]);
        return (repo, await GitRepository.OpenAsync(repo.Path));
    }

    // ---------------------------------------------------------------- stash

    [Fact]
    public async Task SaveStash_WithAndWithoutMessage()
    {
        var (repo, git) = await RepoAsync();
        using var _ = repo;
        repo.Write("a.txt", "2\n");
        var job = new Job("Stash local changes");
        Assert.True((await git.SaveStashAsync(job, "my note")).Success);
        Assert.Equal("1\n", Read(repo, "a.txt"));
        repo.Write("a.txt", "3\n");
        Assert.True((await git.SaveStashAsync(job, "  ")).Success); // blank = no message

        var stashes = await git.ReadStashesAsync();
        Assert.Equal(2, stashes.Count);
        Assert.StartsWith("WIP on main:", stashes[0].ShortSubject);
        Assert.Equal("my note", stashes[1].ShortSubject);
        Assert.Contains("$ git --literal-pathspecs stash push --message \"my note\"\n", job.Output);
        Assert.Contains("$ git --literal-pathspecs stash push\n", job.Output);
    }

    [Fact]
    public async Task SaveStash_NothingToSave_IsNoError()
    {
        var (repo, git) = await RepoAsync();
        using var _ = repo;
        var r = await git.SaveStashAsync(new Job("Stash local changes"), null);
        Assert.True(r.Success);
        Assert.Contains("No local changes to save", r.ReadableOutput);
        Assert.Empty(await git.ReadStashesAsync());
    }

    [Fact]
    public async Task StageUntracked_PutsNewFilesInTheStash()
    {
        var (repo, git) = await RepoAsync();
        using var _ = repo;
        repo.Write("new.txt", "n\n");
        var job = new Job("Stash local changes");
        await git.StageUntrackedAsync(job);
        Assert.Equal("Staging 1 files...", job.Message);
        Assert.True((await git.SaveStashAsync(job, "with new")).Success);
        Assert.False(Exists(repo, "new.txt"));
        Assert.Contains("new.txt", repo.Git("stash", "show", "--name-only", "stash@{0}"));
    }

    [Fact]
    public async Task Snapshot_KeepsTheWorkingTree_AndAddsAStash()
    {
        var (repo, git) = await RepoAsync();
        using var _ = repo;
        repo.Write("a.txt", "2\n");
        var job = new Job("Stash snapshot");
        Assert.True((await git.SaveSnapshotAsync(job, "Snapshot on 'main' 2026-10-30 12:00:00")).Success);
        Assert.Equal("2\n", Read(repo, "a.txt"));
        Assert.Equal(("a.txt", ""), (repo.Git("diff", "--name-only"), repo.Git("diff", "--cached", "--name-only")));
        var s = Assert.Single(await git.ReadStashesAsync());
        Assert.Equal("Snapshot on 'main' 2026-10-30 12:00:00", s.ShortSubject);
        Assert.Contains("$ git stash store ", job.Output);
        Assert.Equal("stash@{0}: Snapshot on 'main' 2026-10-30 12:00:00", repo.Git("stash", "list")); // also on the command line

        // No changes: nothing is stored, no failure.
        repo.Git("checkout", "--", "a.txt");
        var empty = new Job("Stash snapshot");
        Assert.True((await git.SaveSnapshotAsync(empty, "x")).Success);
        Assert.Single(await git.ReadStashesAsync());
        Assert.Contains("gergit: No local changes to save", empty.Output);
        Assert.DoesNotContain("stash store", empty.Output);
    }

    [Fact]
    public async Task StashFiles_Subset_WithUntrackedDeletedAndRenamed()
    {
        var (repo, git) = await RepoAsync(("a.txt", "a\n"), ("b.txt", "b\n"), ("del.txt", "d\n"), ("old.txt", "o\n"), ("[x].txt", "x\n"));
        using var _ = repo;
        repo.Write("a.txt", "a2\n");     // stashed
        repo.Write("b.txt", "b2\n");     // stays
        repo.Write("new.txt", "n\n");    // untracked, stashed
        repo.Write("keep.txt", "k\n");   // untracked, stays
        repo.Write("[x].txt", "x2\n");   // glob-like name, stashed
        repo.Git("rm", "-q", "del.txt"); // staged deletion, stashed
        repo.Git("mv", "old.txt", "renamed.txt"); // staged rename, stashed

        var status = await git.ReadStatusAsync();
        string[] chosen = ["a.txt", "new.txt", "del.txt", "renamed.txt", "[x].txt"];
        var entries = status.Unstaged.Concat(status.Staged).Where(e => chosen.Contains(e.Path)).ToList();
        var job = new Job("Partial stash");
        var r = await git.StashFilesAsync(job, "part", entries);
        Assert.True(r.Success, r.ReadableOutput);

        Assert.Equal("a\n", Read(repo, "a.txt"));
        Assert.Equal("x\n", Read(repo, "[x].txt"));
        Assert.False(Exists(repo, "new.txt"));
        Assert.True(Exists(repo, "del.txt"));
        Assert.True(Exists(repo, "old.txt"));
        Assert.False(Exists(repo, "renamed.txt"));
        Assert.Equal("b2\n", Read(repo, "b.txt"));
        Assert.True(Exists(repo, "keep.txt"));
        Assert.Equal(["M b.txt", "?? keep.txt"], repo.Git("status", "--porcelain").Split('\n').Select(l => l.Trim()));
        Assert.Contains("$ git --literal-pathspecs add -f --pathspec-from-file=- --pathspec-file-nul\n", job.Output);
        Assert.Contains("$ git --literal-pathspecs reset HEAD --pathspec-from-file=- --pathspec-file-nul\n", job.Output);

        // Applying it brings all of them back.
        Assert.True((await git.ApplyStashAsync(job, "stash@{0}", repo.Git("rev-parse", "refs/stash"), pop: true)).Result.Success);
        Assert.Equal("a2\n", Read(repo, "a.txt"));
        Assert.True(Exists(repo, "new.txt"));
        Assert.False(Exists(repo, "del.txt"));
        Assert.True(Exists(repo, "renamed.txt"));
    }

    [Fact]
    public async Task StashFiles_UnstagedDeletionOfAStagedFile_KeepsTheStagedContent()
    {
        var (repo, git) = await RepoAsync(("a.txt", "v1\n"), ("keep.txt", "k\n"));
        using var _ = repo;
        repo.Write("a.txt", "v2\n");
        repo.Git("add", "a.txt");
        File.Delete(Path.Combine(repo.Path, "a.txt")); // "MD"
        repo.Write("n.txt", "n\n");
        repo.Git("add", "n.txt");
        File.Delete(Path.Combine(repo.Path, "n.txt")); // "AD"
        repo.Write("keep.txt", "k2\n");

        var status = await git.ReadStatusAsync();
        var entries = status.Unstaged.Concat(status.Staged).Where(e => e.Path is "a.txt" or "n.txt").ToList();
        var job = new Job("Partial stash");
        var r = await git.StashFilesAsync(job, null, entries);
        Assert.True(r.Success, r.ReadableOutput);
        Assert.DoesNotContain("reset", job.Output);
        Assert.Equal(["M keep.txt"], repo.Git("status", "--porcelain").Split('\n').Select(l => l.Trim()));

        Assert.True((await git.ApplyStashAsync(job, "stash@{0}", repo.Git("rev-parse", "refs/stash"), pop: true)).Result.Success);
        Assert.Equal("v2", repo.Git("show", ":a.txt"));
        Assert.Equal("n", repo.Git("show", ":n.txt"));
    }

    [Fact]
    public async Task Rename_KeepsContentParentsAndTheOtherStashesOrder()
    {
        var (repo, git) = await RepoAsync();
        using var _ = repo;
        foreach (var n in new[] { "one", "two", "three" })
        {
            repo.Write("a.txt", n + "\n");
            repo.Write(n + ".txt", n); // untracked: also kept by the copy (third parent)
            repo.Git("stash", "push", "-u", "-m", n);
        }
        var before = await git.ReadStashesAsync();
        var job = new Job("Rename stash 'two'");
        var (r, newSha) = await git.RenameStashAsync(job, before[1], "second");
        Assert.True(r.Success, r.ReadableOutput);

        var after = await git.ReadStashesAsync();
        // The renamed copy is stored as stash@{0}; the others keep their order.
        Assert.Equal(["second", "three", "one"], after.Select(s => s.ShortSubject));
        Assert.Equal(newSha, after[0].Sha);
        Assert.Equal(before[1].ParentSha, after[0].ParentSha);
        Assert.Equal(repo.Git("rev-parse", before[1].Sha + "^{tree}"), repo.Git("rev-parse", newSha + "^{tree}"));
        Assert.Equal(repo.Git("rev-parse", before[1].Sha + "^3^{tree}"), repo.Git("rev-parse", newSha + "^3^{tree}"));
        Assert.Contains("$ git stash drop stash@{2}\n", job.Output);
    }

    [Fact]
    public async Task DropStashes_HighestIndexFirst()
    {
        var (repo, git) = await RepoAsync();
        using var _ = repo;
        foreach (var n in new[] { "s0", "s1", "s2", "s3" })
        {
            repo.Write("a.txt", n + "\n");
            repo.Git("stash", "push", "-m", n);
        }
        var job = new Job("Delete 2 stashes");
        var all = await git.ReadStashesAsync();
        var r = await git.DropStashesAsync(job, [all[0], all[2]]); // newest "s3" and "s1"
        Assert.True(r!.Success);
        Assert.Equal(["s2", "s0"], (await git.ReadStashesAsync()).Select(s => s.ShortSubject));
        Assert.True(job.Output.IndexOf("refs/stash@{2}", StringComparison.Ordinal) < job.Output.IndexOf("refs/stash@{0}", StringComparison.Ordinal));

        var left = await git.ReadStashesAsync();
        var fail = await git.DropStashesAsync(new Job("x"), [left[0] with { Name = "stash@{7}" }, left[0]]); // stops at the first failure
        Assert.False(fail!.Success);
        Assert.Equal(2, (await git.ReadStashesAsync()).Count);
    }

    [Fact]
    public async Task Drop_and_rename_of_a_shifted_index_do_nothing()
    {
        var (repo, git) = await RepoAsync();
        using var _ = repo;
        repo.Write("a.txt", "mine\n");
        repo.Git("stash", "push", "-m", "mine");
        var shown = Assert.Single(await git.ReadStashesAsync());
        repo.Write("a.txt", "autostash\n");
        repo.Git("stash", "push", "-m", "Pull autostash"); // another job pushed meanwhile: "mine" is stash@{1} now

        var drop = new Job("Delete 'stash@{0}'");
        var r = await git.DropStashesAsync(drop, [shown]);
        Assert.Equal(GitRepository.StashListChanged, r!.Stderr);
        Assert.DoesNotContain("stash drop", drop.Output);

        var rename = new Job("Rename stash 'mine'");
        var (rr, newSha) = await git.RenameStashAsync(rename, shown, "renamed");
        Assert.Equal(GitRepository.StashListChanged, rr.Stderr);
        Assert.Null(newSha);
        Assert.DoesNotContain("stash store", rename.Output);

        Assert.Equal(["Pull autostash", "mine"], (await git.ReadStashesAsync()).Select(x => x.ShortSubject));
    }

    // ---------------------------------------------------------------- cherry-pick

    /// <summary>main: init; side: b, c, d (one file each).</summary>
    static async Task<(TempRepo Repo, GitRepository Git, string B, string C, string D)> SideAsync()
    {
        var (repo, git) = await RepoAsync();
        repo.Git("checkout", "-q", "-b", "side");
        var b = repo.Commit("b", 1, ("b.txt", "b\n"));
        var c = repo.Commit("c", 2, ("c.txt", "c\n"));
        var d = repo.Commit("d", 3, ("d.txt", "d\n"));
        repo.Git("checkout", "-q", "main");
        return (repo, git, b, c, d);
    }

    static string[] Log(TempRepo r, string range = "HEAD") => r.Git("log", "--format=%s", range).Split('\n');

    [Fact]
    public async Task CherryPick_OneAndSeveral_OldestFirst()
    {
        var (repo, git, b, c, d) = await SideAsync();
        using var _ = repo;
        Assert.True((await git.CherryPickAsync(new Job("Cherry-pick"), [c], null, true, false)).Success);
        Assert.Equal(["c", "init"], Log(repo));

        var job = new Job("Cherry-pick");
        Assert.True((await git.CherryPickAsync(job, [b, d], null, true, false)).Success);
        Assert.Equal(["d", "b", "c", "init"], Log(repo));
        Assert.Contains($"$ git cherry-pick {b} {d}\n", job.Output);
    }

    [Fact]
    public async Task CherryPick_NoCommit_And_RecordOrigin()
    {
        var (repo, git, b, c, _) = await SideAsync();
        using var _r = repo;
        Assert.True((await git.CherryPickAsync(new Job("Cherry-pick"), [b], null, commit: false, recordOrigin: false)).Success);
        Assert.Equal(["init"], Log(repo));
        Assert.Equal("A  b.txt", repo.Git("status", "--porcelain"));
        repo.Git("commit", "-q", "-m", "b by hand");

        Assert.True((await git.CherryPickAsync(new Job("Cherry-pick"), [c], null, commit: true, recordOrigin: true)).Success);
        Assert.Contains($"(cherry picked from commit {c})", repo.Git("log", "-1", "--format=%B"));
    }

    [Fact]
    public async Task CherryPick_MergeCommit_WithMainline2()
    {
        var (repo, git) = await RepoAsync();
        using var _ = repo;
        repo.Git("checkout", "-q", "-b", "topic");
        repo.Commit("t", 1, ("t.txt", "t\n"));
        repo.Git("checkout", "-q", "main");
        repo.Commit("m", 2, ("m.txt", "m\n"));
        repo.Git("merge", "-q", "--no-ff", "-m", "merge topic", "topic");
        var merge = repo.Git("rev-parse", "HEAD");
        repo.Git("checkout", "-q", "-b", "target", "topic");

        // -m 2: the changes relative to the second parent (topic) = the main side ("m.txt").
        var job = new Job("Cherry-pick");
        var r = await git.CherryPickAsync(job, [merge], 2, true, false);
        Assert.True(r.Success, r.ReadableOutput);
        Assert.True(Exists(repo, "m.txt"));
        Assert.Contains($"$ git cherry-pick -m 2 {merge}\n", job.Output);
    }

    [Fact]
    public async Task CherryPick_EmptyPickIsSkipped()
    {
        var (repo, git, b, c, _) = await SideAsync();
        using var _r = repo;
        repo.Commit("b again", 5, ("b.txt", "b\n")); // makes the pick of b empty
        var job = new Job("Cherry-pick");
        var r = await git.CherryPickAsync(job, [b, c], null, true, false);
        Assert.True(r.Success, r.ReadableOutput);
        Assert.Equal(["c", "b again", "init"], Log(repo));
        Assert.Contains("$ git cherry-pick --skip\n", job.Output);
        Assert.False(Directory.Exists(Path.Combine(repo.Path, ".git", "sequencer")));
    }

    [Fact]
    public async Task CherryPick_Conflict_IsAMergeConflict()
    {
        var (repo, git) = await RepoAsync();
        using var _ = repo;
        repo.Git("checkout", "-q", "-b", "side");
        var x = repo.Commit("x", 1, ("a.txt", "side\n"));
        repo.Git("checkout", "-q", "main");
        repo.Commit("y", 2, ("a.txt", "main\n"));

        var r = await git.CherryPickAsync(new Job("Cherry-pick"), [x], null, true, false);
        Assert.False(r.Success);
        Assert.Equal(GitErrorKind.MergeConflict, GitErrors.Classify(r).Kind);
        Assert.True(File.Exists(Path.Combine(repo.Path, ".git", "CHERRY_PICK_HEAD"))); // state left for the conflict flow
    }

    // ---------------------------------------------------------------- revert

    [Fact]
    public async Task Revert_CommitAndNoCommit()
    {
        var (repo, git) = await RepoAsync();
        using var _ = repo;
        var b = repo.Commit("b", 1, ("b.txt", "b\n"));
        var job = new Job("Revert");
        Assert.True((await git.RevertAsync(job, b, null, commit: true)).Success);
        Assert.Equal("Revert \"b\"", Log(repo)[0]);
        Assert.False(Exists(repo, "b.txt"));
        Assert.Contains($"$ git revert --no-edit {b}\n", job.Output);

        var c = repo.Commit("c", 2, ("c.txt", "c\n"));
        Assert.True((await git.RevertAsync(new Job("Revert"), c, null, commit: false)).Success);
        Assert.Equal("c", Log(repo)[0]);
        Assert.Equal("D  c.txt", repo.Git("status", "--porcelain"));
    }

    [Fact]
    public async Task Revert_MergeWithParent_AndConflict()
    {
        var (repo, git) = await RepoAsync();
        using var _ = repo;
        repo.Git("checkout", "-q", "-b", "topic");
        repo.Commit("t", 1, ("t.txt", "t\n"));
        repo.Git("checkout", "-q", "main");
        repo.Commit("m", 2, ("m.txt", "m\n"));
        repo.Git("merge", "-q", "--no-ff", "-m", "merge topic", "topic");
        var merge = repo.Git("rev-parse", "HEAD");

        var job = new Job("Revert");
        Assert.True((await git.RevertAsync(job, merge, 1, true)).Success);
        Assert.False(Exists(repo, "t.txt")); // mainline 1 keeps main, drops topic
        Assert.True(Exists(repo, "m.txt"));
        Assert.Contains($"$ git revert -m 1 --no-edit {merge}\n", job.Output);

        var a2 = repo.Commit("a2", 3, ("a.txt", "2\n"));
        repo.Commit("a3", 4, ("a.txt", "3\n"));
        var r = await git.RevertAsync(new Job("Revert"), a2, null, true);
        Assert.False(r.Success);
        Assert.Equal(GitErrorKind.MergeConflict, GitErrors.Classify(r).Kind);
    }

    // ---------------------------------------------------------------- previews

    [Fact]
    public async Task Previews_CleanAndConflicting()
    {
        Assert.SkipWhen(!(await GitRunner.GetVersionAsync() >= new Version(2, 40)), "merge-tree --merge-base needs git 2.40");
        var (repo, git) = await RepoAsync();
        using var _ = repo;
        repo.Git("checkout", "-q", "-b", "side");
        var s1 = repo.Commit("s1", 1, ("a.txt", "side\n"));
        var s2 = repo.Commit("s2", 2, ("a.txt", "side2\n")); // clean only on top of s1: proves the chaining
        var other = repo.Commit("other", 3, ("o.txt", "o\n"));
        repo.Git("checkout", "-q", "main");

        Assert.True(await git.PreviewCherryPickAsync([s1, s2], 1));
        Assert.True(await git.PreviewCherryPickAsync([other], 1));
        Assert.False(await git.PreviewCherryPickAsync([s2], 1));
        Assert.Null(await git.PreviewCherryPickAsync([repo.Git("rev-parse", "main")], 1)); // root: no parent

        repo.Git("checkout", "-q", "side");
        Assert.True(await git.PreviewRevertAsync(s2, 1));
        Assert.False(await git.PreviewRevertAsync(s1, 1)); // s2 changed the same line afterwards
        Assert.Null(await git.PreviewRevertAsync(s1, 2));  // no second parent
    }
}
