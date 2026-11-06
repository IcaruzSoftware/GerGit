using GerGit.Core;
using GerGit.Core.Git;

namespace GerGit.Tests.Core;

/// <summary>Blame porcelain and history log parsing, blame/history/tip against real repositories.</summary>
public sealed class BlameHistoryTests
{
    static readonly string A = new('a', 40), B = new('b', 40), Zero = new('0', 40);

    [Fact]
    public void Blame_porcelain_with_repeated_commits_boundary_uncommitted_lines_and_blocks()
    {
        var porcelain = string.Join("\n",
            $"{A} 1 1 2", "author Ann", "author-mail <ann@x.org>", "author-time 1577836800", "author-tz +0000",
            "committer Ann", "committer-mail <ann@x.org>", "committer-time 1577836800", "committer-tz +0000",
            "summary first", "boundary", "filename f.txt", "\tline 1",
            $"{A} 2 2", "filename f.txt", "\tline 2",
            $"{B} 3 3 1", "author Bob", "author-mail <bob@x.org>", "author-time 1577923200", "author-tz +0000",
            "summary second", "previous " + A + " f.txt", "filename f.txt", "\t", // an empty file line
            $"{A} 3 4 1", "filename f.txt", "\tline 4",
            $"{Zero} 5 5 1", "author Not Committed Yet", "author-mail <not.committed.yet>", "author-time 1577923300",
            "author-tz +0000", "summary Version of f.txt from f.txt", "filename f.txt", "\tdirty", "");

        var lines = GitRepository.ParseBlame(porcelain);

        Assert.Equal([A, A, B, A, Zero], lines.Select(l => l!.Sha));
        Assert.Equal("Ann", lines[0]!.AuthorName);
        Assert.Equal("ann@x.org", lines[0]!.AuthorEmail);
        Assert.Equal("first", lines[1]!.Subject);
        Assert.Same(lines[0], lines[3]); // one record per commit
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1577923200), lines[2]!.AuthorDate);
        Assert.Equal("Not Committed Yet", lines[4]!.AuthorName);
    }

    [Fact]
    public void Blame_parse_errors()
    {
        Assert.Equal("Can't parse blame header line: 'nonsense'", Assert.Throws<GitException>(() => GitRepository.ParseBlame("nonsense\n")).Message);
        Assert.Equal("Can't parse blame", Assert.Throws<GitException>(() => GitRepository.ParseBlame($"{A} 1 1 1\nauthor Ann\n")).Message);
        Assert.Empty(GitRepository.ParseBlame(""));
    }

    [Fact]
    public void History_log_with_rename_merge_and_several_files()
    {
        var log = string.Join("\n",
            A, "Ann", "ann@x.org", "1577836800", "merge without files",
            B, "Bob", "bob@x.org", "1577923200", "rename",
            "R080\told.txt\tnew.txt", "M\tdir/a.txt", "",
            Zero, "Ann", "ann@x.org", "1577836700", "add", "A\told.txt");

        var revs = GitRepository.ParseHistory(log);

        Assert.Equal([A, B, Zero], revs.Select(r => r.Commit.Sha));
        Assert.Empty(revs[0].Files);
        Assert.Equal("merge without files", revs[0].Commit.Subject);
        Assert.Equal([new FileChange(ChangeKind.Renamed, "new.txt", "old.txt"), new FileChange(ChangeKind.Modified, "dir/a.txt", null)], revs[1].Files);
        Assert.Equal([new FileChange(ChangeKind.Added, "old.txt", null)], revs[2].Files);
        Assert.Equal("Bob", revs[1].Commit.AuthorName);
        // C-quoted paths (quote, backslash, TAB) are unquoted; UTF-8 stays as it is with core.quotePath=false.
        var quoted = GitRepository.ParseHistory(string.Join("\n", A, "Ann", "ann@x.org", "1577836800", "q",
            "M\t\"a\\\"b\\tc\\\\d.txt\"", "R100\tä.txt\t\"ö\\\"x.txt\""));
        Assert.Equal([new FileChange(ChangeKind.Modified, "a\"b\tc\\d.txt", null), new FileChange(ChangeKind.Renamed, "ö\"x.txt", "ä.txt")],
            quoted[0].Files);
        Assert.Equal("Cannot parse revision", Assert.Throws<GitException>(() => GitRepository.ParseHistory("abc\nx\n")).Message);
    }

    [Fact]
    public async Task Blame_of_a_file_over_three_commits_and_history_of_a_renamed_file()
    {
        using var t = new TempRepo();
        var c1 = Commit(t, "one", "Ann", 0, ("f.txt", "a\nb\nc\n"));
        var c2 = Commit(t, "two", "Bob", 10, ("f.txt", "a\nB\nc\nd\n"));
        t.Git("mv", "f.txt", "g.txt");
        var c3 = Commit(t, "three", "Cid", 20, ("g.txt", "a\nB\nc\nd\ne\n"));
        var repo = await GitRepository.OpenAsync(t.Path);

        // Blame of c3's parent under the old name: line 2 and 4 by Bob, the rest by Ann.
        var blame = await repo.ReadBlameParentAsync(c3, "f.txt");
        Assert.Equal(["Ann", "Bob", "Ann", "Bob"], blame.Select(c => c!.AuthorName));
        Assert.Equal(c2, blame[1]!.Sha);
        Assert.Empty(await repo.ReadBlameParentAsync(c3, "g.txt")); // no such path in the parent
        Assert.Empty(await repo.ReadBlameParentAsync(c1, "f.txt")); // root commit: bad revision

        var history = await repo.ReadFileHistoryAsync(null, "g.txt");
        Assert.Equal([c3, c2, c1], history.Select(r => r.Commit.Sha));
        Assert.Equal(new FileChange(ChangeKind.Renamed, "g.txt", "f.txt"), history[0].Files.Single());
        Assert.Equal(new FileChange(ChangeKind.Modified, "f.txt", null), history[1].Files.Single());
        Assert.Equal("Cid", history[0].Commit.AuthorName);

        Assert.Equal(c3, await repo.FindBlameStartAsync(null, "g.txt"));
        Assert.Equal(c2, await repo.FindBlameStartAsync(c2, "f.txt"));
        Assert.Null(await repo.FindBlameStartAsync(null, "missing.txt"));

        // A name git C-quotes even with core.quotePath=false.
        var odd = Commit(t, "odd", "Ann", 30, ("q\"u ä.txt", "x\n"));
        Assert.Equal(new FileChange(ChangeKind.Added, "q\"u ä.txt", null), (await repo.ReadFileHistoryAsync(null, "q\"u ä.txt")).Single().Files.Single());

        var bytes = await repo.ReadFileAtAsync(c2, "f.txt");
        Assert.Equal("a\nB\nc\nd\n", System.Text.Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public async Task Best_tip_is_the_farthest_branch_containing_the_commit()
    {
        using var t = new TempRepo();
        var c1 = Commit(t, "one", "Ann", 0, ("f.txt", "1\n"));
        t.Git("branch", "short");
        var c2 = Commit(t, "two", "Ann", 10, ("f.txt", "2\n"));
        t.Git("checkout", "-q", "-b", "long");
        var c3 = Commit(t, "three", "Ann", 20, ("f.txt", "3\n"));
        t.Git("checkout", "-q", "main");
        var repo = await GitRepository.OpenAsync(t.Path);
        var refs = await repo.ReadRefsAsync();

        Assert.Equal(new HistoryTip(c3, "long"), await repo.FindBestTipAsync(c1, refs));
        Assert.Equal(new HistoryTip(c3, "long"), await repo.FindBestTipAsync(c2, refs));
        t.Git("checkout", "-q", "--detach");
        var lost = Commit(t, "detached", "Ann", 30, ("f.txt", "4\n"));
        t.Git("checkout", "-q", "long");
        refs = await repo.ReadRefsAsync();
        Assert.Equal(new HistoryTip(c3, "long"), await repo.FindBestTipAsync(c3, refs));
        Assert.Null(await repo.FindBestTipAsync(lost, refs)); // on no branch and not in HEAD
    }

    internal static string Commit(TempRepo t, string message, string author, int seconds, params (string Path, string Content)[] files)
    {
        foreach (var (p, c) in files) t.Write(p, c);
        t.Git("add", "-A");
        var date = $"@{1577836800 + seconds} +0000";
        t.GitEnv(new()
        {
            ["GIT_AUTHOR_DATE"] = date, ["GIT_COMMITTER_DATE"] = date,
            ["GIT_AUTHOR_NAME"] = author, ["GIT_AUTHOR_EMAIL"] = author.ToLowerInvariant() + "@example.com",
        }, "commit", "-q", "-m", message);
        return t.Git("rev-parse", "HEAD");
    }
}
