using GerGit.Core;
using GerGit.Core.Git;

namespace GerGit.Tests.Core;

/// <summary>Conflict state detection on real repositories.</summary>
public class ConflictStateTests
{
    /// <summary>A repo whose `main` and `side` change line 2 of f.txt differently (base "a b c").</summary>
    internal static TempRepo Diverged()
    {
        var repo = new TempRepo();
        repo.Commit("base", 0, ("f.txt", "a\nb\nc\n"));
        repo.Git("checkout", "-q", "-b", "side");
        repo.Commit("side change", 10, ("f.txt", "a\nT\nc\n"));
        repo.Git("checkout", "-q", "main");
        repo.Commit("main change", 20, ("f.txt", "a\nO\nc\n"));
        return repo;
    }

    /// <summary>Runs git that is expected to fail (a conflict stop).</summary>
    internal static void GitFails(TempRepo repo, params string[] args) =>
        Assert.Throws<InvalidOperationException>(() => repo.Git(args));

    internal static async Task<RepoState> StateAsync(GitRepository git) =>
        await git.ReadRepoStateAsync(await git.ReadStatusAsync());

    [Fact]
    public async Task Clean()
    {
        using var repo = Diverged();
        var git = await GitRepository.OpenAsync(repo.Path);
        Assert.Equal(RepoState.Clean, await StateAsync(git));
    }

    [Fact]
    public async Task Merge_content_conflict()
    {
        using var repo = Diverged();
        GitFails(repo, "merge", "side");
        var git = await GitRepository.OpenAsync(repo.Path);
        var s = await StateAsync(git);
        Assert.Equal(RepoStateKind.Merge, s.Kind);
        Assert.Equal("main", s.OursName);
        Assert.Equal("side", s.TheirsName);
        Assert.Equal(repo.Git("rev-parse", "HEAD"), s.OursSha);
        Assert.Equal(repo.Git("rev-parse", "side"), s.TheirsSha);
        var f = Assert.Single(s.Unmerged);
        Assert.Equal(("f.txt", 'U', 'U', false), (f.Path, f.Ours, f.Theirs, f.IsSubmodule));
        Assert.True(f.MergeAllowed);
    }

    [Fact]
    public async Task Merge_of_a_commit_without_ref_uses_the_abbreviated_sha_and_skips_backup_refs()
    {
        using var repo = Diverged();
        var side = repo.Git("rev-parse", "side");
        repo.Git("branch", "backup/side", "side");
        repo.Git("branch", "-D", "side");
        GitFails(repo, "merge", side);
        var git = await GitRepository.OpenAsync(repo.Path);
        Assert.Equal(side[..7], (await StateAsync(git)).TheirsName);
    }

    [Fact]
    public async Task Add_add_and_modify_delete_side_letters()
    {
        using var repo = new TempRepo();
        repo.Commit("base", 0, ("del-ours.txt", "x\n"), ("del-theirs.txt", "y\n"));
        repo.Git("checkout", "-q", "-b", "side");
        repo.Git("rm", "-q", "del-theirs.txt");
        repo.Commit("side", 10, ("aa.txt", "theirs\n"), ("del-ours.txt", "theirs-mod\n"));
        repo.Git("checkout", "-q", "main");
        repo.Git("rm", "-q", "del-ours.txt");
        repo.Commit("main", 20, ("aa.txt", "ours\n"), ("del-theirs.txt", "ours-mod\n"));
        GitFails(repo, "merge", "side");

        var git = await GitRepository.OpenAsync(repo.Path);
        var s = await StateAsync(git);
        Assert.Equal(RepoStateKind.Merge, s.Kind);
        Assert.Equal([("aa.txt", 'A', 'A'), ("del-ours.txt", 'D', 'U'), ("del-theirs.txt", 'U', 'D')],
            s.Unmerged.Select(f => (f.Path, f.Ours, f.Theirs)));
        Assert.False(s.Unmerged[1].MergeAllowed);
        Assert.Equal("deleted", UnmergedFile.SideWord(s.Unmerged[1].Ours));
        Assert.Equal("modified", UnmergedFile.SideWord(s.Unmerged[1].Theirs));
        Assert.Equal("added", UnmergedFile.SideWord(s.Unmerged[0].Ours));
    }

    [Fact]
    public async Task Cherry_pick_conflict()
    {
        using var repo = Diverged();
        var side = repo.Git("rev-parse", "side");
        GitFails(repo, "cherry-pick", "side");
        var git = await GitRepository.OpenAsync(repo.Path);
        var s = await StateAsync(git);
        Assert.Equal(RepoStateKind.CherryPick, s.Kind);
        Assert.Equal("main", s.OursName);
        Assert.Equal(side[..7], s.TheirsName);
        Assert.Equal(side, s.TheirsSha);
        Assert.Single(s.Unmerged);
    }

    [Fact]
    public async Task Multi_pick_sequence_stopped_after_the_conflict_was_committed()
    {
        using var repo = Diverged();
        repo.Git("checkout", "-q", "side");
        repo.Commit("side 2", 30, ("g.txt", "g\n"));
        repo.Git("checkout", "-q", "main");
        GitFails(repo, "cherry-pick", "main..side");
        var git = await GitRepository.OpenAsync(repo.Path);
        Assert.Equal(RepoStateKind.CherryPick, (await StateAsync(git)).Kind); // CHERRY_PICK_HEAD comes first

        repo.Write("f.txt", "a\nresolved\nc\n");
        repo.Git("add", "f.txt");
        repo.Git("commit", "-q", "--no-edit");
        var s = await StateAsync(git);
        Assert.Equal(RepoStateKind.CherryPickSequence, s.Kind);
        Assert.Empty(s.Unmerged);
    }

    [Fact]
    public async Task Revert_conflict()
    {
        using var repo = new TempRepo();
        repo.Commit("base", 0, ("f.txt", "a\n"));
        var change = repo.Commit("change", 10, ("f.txt", "b\n"));
        repo.Commit("again", 20, ("f.txt", "c\n"));
        GitFails(repo, "revert", "--no-edit", change);
        var git = await GitRepository.OpenAsync(repo.Path);
        var s = await StateAsync(git);
        Assert.Equal(RepoStateKind.Revert, s.Kind);
        Assert.Equal(("local", "remote"), (s.OursName, s.TheirsName));
        Assert.Equal(change, s.TheirsSha);
        Assert.Single(s.Unmerged);
    }

    [Fact]
    public async Task Rebase_conflict_counters_and_sides()
    {
        using var repo = Diverged();
        repo.Git("checkout", "-q", "side");
        repo.Commit("side 2", 30, ("g.txt", "g\n"));
        var first = repo.Git("rev-parse", "side~1");
        var orig = repo.Git("rev-parse", "side");
        GitFails(repo, "rebase", "main");
        var git = await GitRepository.OpenAsync(repo.Path);
        var s = await StateAsync(git);
        Assert.Equal(RepoStateKind.Rebase, s.Kind);
        Assert.Equal("main", s.OursName);  // the onto reference
        Assert.Equal("side", s.TheirsName); // the branch being rebased
        Assert.Equal(repo.Git("rev-parse", "main"), s.OursSha);
        Assert.Equal(orig, s.TheirsSha);
        Assert.Equal((0, 2), (s.Done, s.Total)); // first of two commits stopped
        Assert.Equal(first, s.ActiveSha);
        Assert.Null(s.AmendSha);
        Assert.Single(s.Unmerged);
    }

    [Fact]
    public async Task Rebase_apply_backend_conflict()
    {
        using var repo = Diverged();
        repo.Git("checkout", "-q", "side");
        var first = repo.Git("rev-parse", "side");
        GitFails(repo, "rebase", "--apply", "main");
        var git = await GitRepository.OpenAsync(repo.Path);
        var s = await StateAsync(git);
        Assert.Equal(RepoStateKind.Rebase, s.Kind);
        Assert.Equal(("main", "side"), (s.OursName, s.TheirsName));
        Assert.Equal((0, 1), (s.Done, s.Total));
        Assert.Equal(first, s.ActiveSha);
    }

    [Fact]
    public async Task Rebase_stopped_at_edit_has_the_amend_sha()
    {
        using var repo = new TempRepo();
        repo.Commit("base", 0, ("f.txt", "a\n"));
        var edited = repo.Commit("to edit", 10, ("f.txt", "b\n"));
        repo.Commit("after", 20, ("f.txt", "c\n"));
        repo.GitEnv(new() { ["GIT_SEQUENCE_EDITOR"] = "sed -i '1s/^pick/edit/'" }, "rebase", "-i", "HEAD~2");
        var git = await GitRepository.OpenAsync(repo.Path);
        var s = await StateAsync(git);
        Assert.Equal(RepoStateKind.Rebase, s.Kind);
        Assert.Equal(edited, s.AmendSha);
        Assert.Equal(edited, s.ActiveSha);
        Assert.Equal((0, 2), (s.Done, s.Total));
        Assert.Empty(s.Unmerged);

        File.WriteAllText(Path.Combine(repo.Path, ".git", "rebase-merge", "current-fixups"), "fixup x\n");
        Assert.Null((await StateAsync(git)).AmendSha);
    }

    [Fact]
    public async Task Rebase_merge_without_onto_falls_through()
    {
        using var repo = Diverged();
        Directory.CreateDirectory(Path.Combine(repo.Path, ".git", "rebase-merge"));
        File.WriteAllText(Path.Combine(repo.Path, ".git", "rebase-merge", "orig-head"), repo.Git("rev-parse", "HEAD") + "\n");
        var git = await GitRepository.OpenAsync(repo.Path);
        Assert.Equal(RepoStateKind.None, (await StateAsync(git)).Kind);

        File.WriteAllText(Path.Combine(repo.Path, ".git", "rebase-merge", "onto"), "garbage\n");
        var e = await Assert.ThrowsAsync<GitException>(() => StateAsync(git));
        Assert.Equal("Failed to parse SHA in 'garbage'", e.Message);
    }

    [Fact]
    public async Task Apply_patches_am_conflict()
    {
        using var repo = Diverged();
        var patchDir = Path.Combine(repo.Path, ".git", "patches");
        repo.Git("format-patch", "-q", "-1", "side", "-o", patchDir);
        GitFails(repo, "am", "-3", Directory.GetFiles(patchDir).Single());
        var git = await GitRepository.OpenAsync(repo.Path);
        var s = await StateAsync(git);
        Assert.Equal(RepoStateKind.ApplyPatches, s.Kind);
        Assert.Equal(("main", "patch"), (s.OursName, s.TheirsName));
        Assert.Equal((0, 1), (s.Done, s.Total));
        Assert.Single(s.Unmerged);
    }

    [Fact]
    public async Task Merge_squash_conflict()
    {
        using var repo = Diverged();
        GitFails(repo, "merge", "--squash", "side");
        var git = await GitRepository.OpenAsync(repo.Path);
        var s = await StateAsync(git);
        Assert.Equal(RepoStateKind.Squash, s.Kind);
        Assert.Equal(("main", "remote"), (s.OursName, s.TheirsName));
        Assert.Single(s.Unmerged);
    }

    [Fact]
    public async Task Stash_pop_conflict_is_an_unmerged_index()
    {
        using var repo = new TempRepo();
        repo.Commit("base", 0, ("f.txt", "a\n"));
        repo.Write("f.txt", "stashed\n");
        repo.Git("stash", "push", "-q");
        repo.Commit("change", 10, ("f.txt", "committed\n"));
        GitFails(repo, "stash", "pop");
        var git = await GitRepository.OpenAsync(repo.Path);
        var s = await StateAsync(git);
        Assert.Equal(RepoStateKind.Unmerged, s.Kind);
        Assert.Equal(("main", "stash/patch"), (s.OursName, s.TheirsName));
        Assert.Equal("f.txt", Assert.Single(s.Unmerged).Path);
    }

    [Fact]
    public async Task Bisect_is_detected()
    {
        using var repo = Diverged();
        repo.Git("bisect", "start");
        var git = await GitRepository.OpenAsync(repo.Path);
        Assert.Equal(RepoStateKind.Bisect, (await StateAsync(git)).Kind);
    }

    [Fact]
    public async Task Detached_head_merge_names_the_ref_of_the_commit()
    {
        using var repo = Diverged();
        repo.Git("checkout", "-q", "--detach", "main");
        GitFails(repo, "merge", "side");
        var git = await GitRepository.OpenAsync(repo.Path);
        Assert.Equal("main", (await StateAsync(git)).OursName);
    }
}
