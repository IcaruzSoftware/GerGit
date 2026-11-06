using GerGit.Core;
using GerGit.Core.Git;

namespace GerGit.Tests.Core;

public class GitRepositoryTests
{
    static readonly DateTimeOffset Base = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

    static Task<GitRepository> Open(TempRepo r) => GitRepository.OpenAsync(r.Path);

    // ---- open ----

    [Fact]
    public async Task Open_Repository_AndSubfolderOpensRoot()
    {
        using var r = new TempRepo();
        r.Write("sub/x.txt", "x");
        var repo = await GitRepository.OpenAsync(Path.Combine(r.Path, "sub"));
        Assert.Equal(r.Path, repo.Info.WorkTree);
        Assert.Equal(Path.Combine(r.Path, ".git"), repo.Info.GitDir);
        Assert.Equal(repo.Info.GitDir, repo.Info.CommonDir);
        Assert.Equal(RepoKind.Repository, repo.Info.Kind);
    }

    [Fact]
    public async Task Open_NonRepository()
    {
        using var r = new TempRepo(init: false);
        var ex = await Assert.ThrowsAsync<GitException>(() => Open(r));
        Assert.Equal($"The '{r.Path}' directory is not a git repository", ex.Message);
    }

    [Fact]
    public async Task Open_BareRepository_IsNotARepository()
    {
        using var r = new TempRepo(init: false);
        r.Git("init", "-q", "--bare");
        await Assert.ThrowsAsync<GitException>(() => Open(r));
    }

    [Fact]
    public async Task Open_MissingFolder() =>
        await Assert.ThrowsAsync<GitException>(() => GitRepository.OpenAsync("/nonexistent/gergit-" + Guid.NewGuid()));

    [Fact]
    public async Task Open_LinkedWorktree()
    {
        using var r = new TempRepo();
        r.Commit("c1", 1);
        var wt = r.Path + "-wt";
        try
        {
            r.Git("worktree", "add", "-q", wt);
            var repo = await GitRepository.OpenAsync(wt);
            Assert.Equal(RepoKind.Worktree, repo.Info.Kind);
            Assert.Equal(wt, repo.Info.WorkTree);
            Assert.Equal(Path.Combine(r.Path, ".git"), repo.Info.CommonDir);
        }
        finally { Directory.Delete(wt, true); }
    }

    // ---- refs ----

    [Fact]
    public async Task Refs_BranchesTagsUpstreamAheadBehind()
    {
        using var origin = new TempRepo();
        origin.Commit("o1", 1, ("a.txt", "1"));
        origin.Commit("o2", 2, ("a.txt", "2"));

        using var r = new TempRepo(init: false);
        r.Git("clone", "-q", origin.Path, ".");
        r.Git("config", "user.name", "T");
        r.Git("config", "user.email", "t@example.com");
        var local = r.Commit("local", 3, ("b.txt", "b"));        // ahead 1
        var o3 = origin.Commit("o3", 4, ("a.txt", "3"));          // behind 1 after fetch
        r.Git("fetch", "-q", "origin");
        r.Git("tag", "v1", "HEAD~1");
        r.Git("tag", "-a", "v2", "-m", "annotated");
        r.Git("branch", "feat");
        r.Git("config", "branch.feat.remote", "origin");
        r.Git("config", "branch.feat.merge", "refs/heads/feat"); // no origin/feat → gone

        var snap = await (await Open(r)).ReadRefsAsync();

        Assert.Equal(["refs/heads/feat", "refs/heads/main", "refs/remotes/origin/main", "refs/tags/v1", "refs/tags/v2"],
            snap.Refs.Select(x => x.FullName));
        Assert.Equal("refs/heads/main", snap.ActiveBranch);
        Assert.Equal(local, snap.HeadSha);

        var main = snap.Refs.Single(x => x.FullName == "refs/heads/main");
        Assert.Equal((RefKind.LocalBranch, "refs/remotes/origin/main", UpstreamState.Valid, 1, 1),
            (main.Kind, main.Upstream, main.UpstreamState, main.Ahead, main.Behind));
        Assert.Equal(Base.AddSeconds(3), main.CommitterDate);

        var feat = snap.Refs.Single(x => x.FullName == "refs/heads/feat");
        Assert.Equal(UpstreamState.Gone, feat.UpstreamState);

        var remote = snap.Refs.Single(x => x.Kind == RefKind.RemoteBranch);
        Assert.Equal((o3, "origin", "main", UpstreamState.None), (remote.Sha, remote.RemoteName, remote.NameInRemote, remote.UpstreamState));

        var v1 = snap.Refs.Single(x => x.ShortName == "v1");
        Assert.Null(v1.TagObjectSha);
        var v2 = snap.Refs.Single(x => x.ShortName == "v2");
        Assert.Equal(local, v2.Sha);                                 // peeled to the commit
        Assert.Equal(r.Git("rev-parse", "v2"), v2.TagObjectSha);     // the tag object
        Assert.Equal(Base.AddSeconds(3), v2.CommitterDate);          // from the peeled commit
    }

    [Fact]
    public async Task Refs_DetachedHead()
    {
        using var r = new TempRepo();
        var c1 = r.Commit("c1", 1);
        r.Commit("c2", 2);
        r.Git("checkout", "-q", "--detach", c1);
        var snap = await (await Open(r)).ReadRefsAsync();
        Assert.True(snap.IsDetached);
        Assert.Equal(c1, snap.HeadSha);
    }

    [Fact]
    public async Task Refs_UnbornHead()
    {
        using var r = new TempRepo();
        var snap = await (await Open(r)).ReadRefsAsync();
        Assert.Empty(snap.Refs);
        Assert.Null(snap.HeadSha);
        Assert.Null(snap.ActiveBranch);

        r.Commit("c1", 1);
        r.Git("checkout", "-q", "--orphan", "fresh");
        snap = await (await Open(r)).ReadRefsAsync();
        Assert.Single(snap.Refs);
        Assert.Null(snap.HeadSha);
        Assert.Null(snap.ActiveBranch);
    }

    [Fact]
    public async Task Remotes()
    {
        using var r = new TempRepo();
        r.Git("remote", "add", "zeta", "https://example.com/z.git");
        r.Git("remote", "add", "Alpha", "/srv/a.git");
        var remotes = await (await Open(r)).ReadRemotesAsync();
        Assert.Equal([new RemoteEntry("Alpha", "/srv/a.git"), new RemoteEntry("zeta", "https://example.com/z.git")], remotes);
    }

    // ---- stashes ----

    [Fact]
    public async Task Stashes()
    {
        using var r = new TempRepo();
        var head = r.Commit("base commit", 1, ("a.txt", "1"));
        var repo = await Open(r);
        Assert.Empty(await repo.ReadStashesAsync());

        r.Write("a.txt", "2");
        r.Git("stash", "push", "-q", "-m", "my note");
        r.Write("a.txt", "3");
        r.Git("stash", "push", "-q");

        var stashes = await repo.ReadStashesAsync();
        Assert.Equal(2, stashes.Count);
        Assert.Equal("stash@{0}", stashes[0].Name);
        Assert.StartsWith("WIP on main: ", stashes[0].Subject);
        Assert.Equal(stashes[0].Subject, stashes[0].ShortSubject);
        Assert.Equal(("stash@{1}", "On main: my note", "my note"), (stashes[1].Name, stashes[1].Subject, stashes[1].ShortSubject));
        Assert.All(stashes, s => Assert.Equal(head, s.ParentSha));
        Assert.Equal("Test User", stashes[0].AuthorName);
    }

    [Theory]
    [InlineData("On main: my: note", "my: note")]
    [InlineData("WIP on main: abc subj", "WIP on main: abc subj")]
    [InlineData("no colon", "no colon")]
    public void StashSubject(string subject, string expected) => Assert.Equal(expected, GitRepository.ShortenStashSubject(subject));

    // ---- submodules ----

    [Fact]
    public async Task Submodules_FromGitmodules()
    {
        using var r = new TempRepo();
        r.Write(".gitmodules", """
            [submodule "zeta"]
            	path = a/zeta
            	url = https://example.com/zeta.git
            [submodule "alpha"]
            	path = b/alpha
            	url = ../alpha
            	active = false
            [submodule "nopath"]
            	url = x
            """);
        var subs = await (await Open(r)).ReadSubmodulesAsync();
        Assert.Equal([new SubmoduleEntry("zeta", "a/zeta", true), new SubmoduleEntry("alpha", "b/alpha", false)], subs);
    }

    [Fact]
    public async Task MalformedGitmodules_StatusAndChangedFilesStillWork()
    {
        using var r = new TempRepo();
        var c1 = r.Commit("c1", 1, ("a.txt", "1"));
        r.Write(".gitmodules", "[submodule \"x\"\n\tpath = x\n"); // no closing bracket: `git config -f` fails
        r.Write("a.txt", "2");
        var repo = await Open(r);

        Assert.Empty(await repo.ReadSubmodulesAsync());
        var s = await repo.ReadStatusAsync();
        Assert.Equal([
            new StatusEntry(".gitmodules", null, ChangeKind.Untracked, false),
            new StatusEntry("a.txt", null, ChangeKind.Modified, false)], s.Unstaged);
        Assert.Equal([new ChangedFile("a.txt", null, ChangeKind.Added)], await repo.ReadChangedFilesAsync(null, c1));
    }

    // ---- status ----

    [Fact]
    public void Status_MappingTable()
    {
        var output = string.Join('\0',
            "?? u.txt", "MM m.txt", "R  new.txt", "old.txt", "UU c1.txt", "AA c2.txt", "DD c3.txt", " M sub", "A  added.txt", " D gone.txt", "!! ign") + "\0";
        var s = GitRepository.ParseStatus(output, new HashSet<string> { "sub" });

        Assert.Equal([
            new StatusEntry("c1.txt", null, ChangeKind.Conflicted, false),
            new StatusEntry("c2.txt", null, ChangeKind.Conflicted, false),
            new StatusEntry("c3.txt", null, ChangeKind.Conflicted, false),
            new StatusEntry("gone.txt", null, ChangeKind.Deleted, false),
            new StatusEntry("ign", null, ChangeKind.Ignored, false),
            new StatusEntry("m.txt", null, ChangeKind.Modified, false),
            new StatusEntry("sub", null, ChangeKind.Modified, false, IsSubmodule: true),
            new StatusEntry("u.txt", null, ChangeKind.Untracked, false)], s.Unstaged);
        Assert.Equal([
            new StatusEntry("added.txt", null, ChangeKind.Added, true),
            new StatusEntry("m.txt", null, ChangeKind.Modified, true),
            new StatusEntry("new.txt", "old.txt", ChangeKind.Renamed, true)], s.Staged);
        Assert.Equal(10, s.FileCount);
    }

    [Fact]
    public void Status_IntentToAddRename_ConsumesSourceToken()
    {
        // `git add -N` on a moved file prints " R new\0old\0"; the source is the old path (here oddly named like a
        // status record), not an entry of its own. New = Added, old = Deleted, both unstaged.
        var s = GitRepository.ParseStatus(" R new name.txt\0MM old name.txt\0?? u.txt\0", new HashSet<string>());
        Assert.Equal([new StatusEntry("MM old name.txt", null, ChangeKind.Deleted, false),
            new StatusEntry("new name.txt", null, ChangeKind.Added, false),
            new StatusEntry("u.txt", null, ChangeKind.Untracked, false)], s.Unstaged);
        Assert.Empty(s.Staged);
    }

    [Fact]
    public async Task Status_RealRepository()
    {
        using var r = new TempRepo();
        r.Commit("c1", 1, ("m.txt", "1"), ("b.txt", "same content\n"));
        r.Write("m.txt", "2");
        r.Git("add", "m.txt");
        r.Write("m.txt", "3");
        r.Git("mv", "b.txt", "b2.txt");
        r.Write("dir/u.txt", "u");

        var s = await (await Open(r)).ReadStatusAsync();
        Assert.Equal([
            new StatusEntry("dir/u.txt", null, ChangeKind.Untracked, false),
            new StatusEntry("m.txt", null, ChangeKind.Modified, false)], s.Unstaged);
        Assert.Equal([
            new StatusEntry("b2.txt", "b.txt", ChangeKind.Renamed, true),
            new StatusEntry("m.txt", null, ChangeKind.Modified, true)], s.Staged);
    }

    [Fact]
    public async Task Status_LockFree_DoesNotRewriteIndex()
    {
        using var r = new TempRepo();
        r.Commit("c1", 1, ("a.txt", "1"));
        File.SetLastWriteTimeUtc(Path.Combine(r.Path, "a.txt"), DateTime.UtcNow.AddHours(1)); // stale stat data in the index
        var index = Path.Combine(r.Path, ".git", "index");
        var before = File.ReadAllBytes(index);
        var repo = await Open(r);

        await repo.ReadStatusAsync(lockFree: true);
        Assert.Equal(before, File.ReadAllBytes(index));
        await repo.ReadStatusAsync(); // a normal status refreshes the stat data and writes the index
        Assert.NotEqual(before, File.ReadAllBytes(index));
    }

    // ---- walk, headers, details ----

    [Fact]
    public async Task Walk_DateOrderAndParents()
    {
        using var r = new TempRepo();
        var c1 = r.Commit("c1", 1, ("a.txt", "1"));
        r.Git("checkout", "-q", "-b", "side");
        var c2 = r.Commit("c2", 2, ("b.txt", "2"));
        r.Git("checkout", "-q", "main");
        var c3 = r.Commit("c3", 3, ("c.txt", "3"));
        var date = "@1577836804 +0000";
        r.GitEnv(new() { ["GIT_AUTHOR_DATE"] = date, ["GIT_COMMITTER_DATE"] = date }, "merge", "-q", "--no-ff", "-m", "merge", "side");
        var m = r.Git("rev-parse", "HEAD");

        var walked = new List<WalkedCommit>();
        await foreach (var c in (await Open(r)).WalkAsync()) walked.Add(c);

        Assert.Equal([m, c3, c2, c1], walked.Select(w => w.Sha));
        Assert.Equal([c3, c2], walked[0].Parents);
        Assert.Equal([c1], walked[1].Parents);
        Assert.Empty(walked[3].Parents);
    }

    [Fact]
    public async Task Walk_IncludesCommitReachableOnlyFromTag()
    {
        using var r = new TempRepo();
        var c1 = r.Commit("c1", 1);
        r.Git("checkout", "-q", "-b", "tmp");
        var c2 = r.Commit("c2", 2);
        r.Git("tag", "-a", "-m", "t", "t");
        r.Git("checkout", "-q", "main");
        r.Git("branch", "-q", "-D", "tmp");

        var walked = new List<string>();
        await foreach (var c in (await Open(r)).WalkAsync()) walked.Add(c.Sha);
        Assert.Equal([c2, c1], walked);
    }

    [Fact]
    public async Task Walk_UnbornHeadIsEmpty()
    {
        using var r = new TempRepo();
        await foreach (var _ in (await Open(r)).WalkAsync()) Assert.Fail("no commits expected");
    }

    [Fact]
    public async Task Headers_InRequestedOrder()
    {
        using var r = new TempRepo();
        var c1 = r.Commit("first", 1);
        var c2 = r.Commit("second\n\nwith body", 2);
        var c3 = r.Commit("third", 3);

        var h = await (await Open(r)).ReadHeadersAsync([c2, c1, c3]);
        Assert.Equal([c2, c1, c3], h.Select(x => x.Sha));
        Assert.Equal(new CommitHeader(c2, "Test User", "test@example.com", Base.AddSeconds(2), "second", true), h[0]);
        Assert.False(h[1].HasBody);
    }

    [Fact]
    public async Task CommitDetails()
    {
        using var r = new TempRepo();
        var c1 = r.Commit("c1", 1);
        var c2 = r.Commit("subject ±. x\n\nbody line", 2);
        var repo = await Open(r);

        var d = await repo.ReadCommitDetailsAsync(c2);
        Assert.Equal([c1], d.Parents);
        Assert.Equal("subject ±. x", d.Subject);
        Assert.Equal("body line", d.Description);
        Assert.Equal(Base.AddSeconds(2), d.CommitterDate);

        var ex = await Assert.ThrowsAsync<GitException>(() => repo.ReadCommitDetailsAsync("0123456789012345678901234567890123456789"));
        Assert.Equal("Commit not found: 0123456789012345678901234567890123456789", ex.Message);
    }

    // ---- changed files and file diff ----

    [Fact]
    public async Task ChangedFiles_RootCommitRenameAndRange()
    {
        using var r = new TempRepo();
        var c1 = r.Commit("root", 1, ("a.txt", "a\n"), ("b.txt", "some longer content\nline 2\nline 3\n"));
        r.Git("mv", "b.txt", "c.txt");
        r.Write("a.txt", "a2\n");
        var c2 = r.Commit("rename", 2);
        var repo = await Open(r);

        var root = await repo.ReadChangedFilesAsync(null, c1);
        Assert.Equal([new ChangedFile("a.txt", null, ChangeKind.Added), new ChangedFile("b.txt", null, ChangeKind.Added)],
            root.OrderBy(f => f.Path, StringComparer.Ordinal));

        var changed = await repo.ReadChangedFilesAsync(null, c2);
        Assert.Equal([new ChangedFile("a.txt", null, ChangeKind.Modified), new ChangedFile("c.txt", "b.txt", ChangeKind.Renamed)],
            changed.OrderBy(f => f.Path, StringComparer.Ordinal));

        Assert.Equal(changed.OrderBy(f => f.Path), (await repo.ReadChangedFilesAsync(c1, c2)).OrderBy(f => f.Path));
    }

    [Fact]
    public void NameStatus_Parse()
    {
        var files = GitRepository.ParseNameStatus("M\0a\0R087\0old\0new\0X\0weird\0C100\0s\0t\0A\0lib\0",
            new HashSet<string> { "lib" });
        Assert.Equal([
            new ChangedFile("a", null, ChangeKind.Modified),
            new ChangedFile("new", "old", ChangeKind.Renamed),
            new ChangedFile("t", "s", ChangeKind.Copied),
            new ChangedFile("lib", null, ChangeKind.Added, IsSubmodule: true)], files);
    }

    [Fact]
    public async Task FileDiff_Options()
    {
        using var r = new TempRepo();
        var lines = string.Concat(Enumerable.Range(1, 20).Select(i => $"line {i}\n"));
        var c1 = r.Commit("root", 1, ("ä.txt", lines));
        var c2 = r.Commit("edit", 2, ("ä.txt", lines.Replace("line 10\n", "line ten\n")));
        var c3 = r.Commit("ws", 3, ("ä.txt", lines.Replace("line 10\n", "line  ten\n")));
        var repo = await Open(r);

        var root = await repo.ReadFileDiffAsync(new DiffRequest(null, c1, "ä.txt", null));
        Assert.Null(root.OldPath);
        Assert.Equal("ä.txt", root.NewPath);
        Assert.Equal(20, Assert.Single(root.Hunks).Lines.Count);

        var edit = await repo.ReadFileDiffAsync(new DiffRequest(null, c2, "ä.txt", null, Context: 1));
        var hunk = Assert.Single(edit.Hunks);
        Assert.Equal(["line 9", "line 10", "line ten", "line 11"], hunk.Lines.Select(l => l.Text));
        Assert.Equal((9, 3, 9, 3), (hunk.OldStart, hunk.OldLength, hunk.NewStart, hunk.NewLength));

        var whole = await repo.ReadFileDiffAsync(new DiffRequest(null, c2, "ä.txt", null, EntireFile: true));
        Assert.Equal(21, Assert.Single(whole.Hunks).Lines.Count);

        var ws = await repo.ReadFileDiffAsync(new DiffRequest(c2, c3, "ä.txt", null, IgnoreWhitespace: true));
        Assert.True(ws.IsEmpty);
    }

    [Fact]
    public async Task FileDiff_Rename()
    {
        using var r = new TempRepo();
        r.Commit("root", 1, ("old.txt", "one\ntwo\nthree\nfour\n"));
        r.Git("mv", "old.txt", "new.txt");
        r.Write("new.txt", "one\ntwo\nthree\nfour!\n");
        var c2 = r.Commit("rename", 2);

        var d = await (await Open(r)).ReadFileDiffAsync(new DiffRequest(null, c2, "new.txt", "old.txt"));
        Assert.Equal(("old.txt", "new.txt"), (d.OldPath, d.NewPath));
        Assert.Equal(["four"], d.Hunks.SelectMany(h => h.Lines).Where(l => l.Kind == DiffLineKind.Removed).Select(l => l.Text));
    }
}
