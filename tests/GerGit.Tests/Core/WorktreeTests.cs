using GerGit.Core.Git;

namespace GerGit.Tests.Core;

/// <summary>The worktree list from `git worktree list --porcelain -z`.</summary>
public class WorktreeTests
{
    const string A = "1111111111111111111111111111111111111111", B = "2222222222222222222222222222222222222222";

    static bool Yes(string path) => true;

    static string Porcelain(params string[][] records) => string.Concat(records.Select(r => string.Concat(r.Select(f => f + "\0")) + "\0"));

    [Fact]
    public void Parse_main_linked_detached_locked_and_prunable()
    {
        var output = Porcelain(
            ["worktree /r/repo", $"HEAD {A}", "branch refs/heads/main"],
            ["worktree /r/wt/zeta", $"HEAD {B}", "detached"],
            ["worktree /r/wt/Alpha 10", $"HEAD {A}", "branch refs/heads/feature/x", "locked reason with spaces"],
            ["worktree /r/wt/alpha 9", $"HEAD {A}", "branch refs/heads/y", "locked"],
            ["worktree /r/wt/gone", $"HEAD {A}", "branch refs/heads/g", "prunable gitdir file points to non-existent location"],
            ["worktree /r/wt/missing", $"HEAD {A}", "branch refs/heads/m", "locked"]); // locked: never prunable, but its folder is gone
        var list = GitRepository.ParseWorktrees(output, "/r/wt/zeta", p => p != "/r/wt/missing");
        Assert.Equal(["/r/repo", "/r/wt/alpha 9", "/r/wt/Alpha 10", "/r/wt/zeta"], list.Select(w => w.Path)); // main first, then natural order
        Assert.Equal(new WorktreeEntry("/r/repo", "refs/heads/main", A, IsMain: true, IsCurrent: false), list[0]);
        Assert.Equal(new WorktreeEntry("/r/wt/zeta", null, B, IsMain: false, IsCurrent: true), list[3]);
        Assert.Equal(("Alpha 10", "feature/x"), (list[2].Name, list[2].BranchName));
    }

    [Fact]
    public void Parse_empty_without_linked_worktrees_and_no_main_when_bare()
    {
        Assert.Empty(GitRepository.ParseWorktrees(Porcelain(["worktree /r/repo", $"HEAD {A}", "branch refs/heads/main"]), "/r/repo", Yes));
        // Only a prunable linked one: no linked worktree left.
        Assert.Empty(GitRepository.ParseWorktrees(Porcelain(["worktree /r/repo", $"HEAD {A}", "branch refs/heads/main"],
            ["worktree /r/x", $"HEAD {A}", "detached", "prunable"]), "/r/repo", Yes));
        var bare = GitRepository.ParseWorktrees(Porcelain(["worktree /r/repo.git", "bare"],
            ["worktree /r/x", $"HEAD {A}", "branch refs/heads/x"]), "/r/x", Yes);
        Assert.Equal(new WorktreeEntry("/r/x", "refs/heads/x", A, false, true), Assert.Single(bare));
        // Unborn HEAD (all zeros) has no commit.
        var unborn = GitRepository.ParseWorktrees(Porcelain(["worktree /r/repo", $"HEAD {new string('0', 40)}", "branch refs/heads/main"],
            ["worktree /r/x", $"HEAD {A}", "branch refs/heads/x"]), "/r/repo", Yes);
        Assert.Null(unborn[0].Sha);
    }

    [Fact]
    public async Task Read_against_a_real_repository()
    {
        using var r = new TempRepo();
        var c = r.Commit("init", 0, ("a.txt", "1\n"));
        var repo = await GitRepository.OpenAsync(r.Path);
        Assert.Empty(await repo.ReadWorktreesAsync());
        Assert.Equal(r.Path, repo.MainWorkTree);

        string wt1 = r.Path + "-worktrees/one", wt2 = r.Path + "-worktrees/two";
        r.Git("worktree", "add", "-q", "-b", "one", wt1);
        r.Git("worktree", "add", "-q", "--detach", wt2, c);
        r.Git("worktree", "lock", wt2);
        try
        {
            var list = await repo.ReadWorktreesAsync();
            Assert.Equal([(r.Path, "refs/heads/main", true, true), (wt1, "refs/heads/one", false, false), (wt2, null, false, false)],
                list.Select(w => (w.Path, w.Branch, w.IsMain, w.IsCurrent)));
            Assert.All(list, w => Assert.Equal(c, w.Sha));

            // Seen from the linked worktree: it is the current one, the main worktree is found through the common dir.
            var linked = await GitRepository.OpenAsync(wt1);
            Assert.Equal(r.Path, linked.MainWorkTree);
            Assert.Equal([false, true, false], (await linked.ReadWorktreesAsync()).Select(w => w.IsCurrent));

            // A worktree whose folder is gone is skipped (prunable).
            Directory.Delete(wt1, recursive: true);
            Assert.Equal([r.Path, wt2], (await repo.ReadWorktreesAsync()).Select(w => w.Path));
        }
        finally
        {
            Directory.Delete(r.Path + "-worktrees", recursive: true);
        }
    }
}
