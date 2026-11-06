using System.Diagnostics;
using System.Text;
using GerGit.Core;
using GerGit.Core.Diff;
using GerGit.Core.Git;

namespace GerGit.Tests.Core;

/// <summary>Local Changes against real temp repos: working diffs, line patches, file operations, commit.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public class LocalChangesTests
{
    const string Six = "one\ntwo\nthree\nfour\nfive\nsix\n";
    const string SixChanged = "one\nTWO\nTHREE\nfour\nfive\nsix\n";

    static Task<GitRepository> Open(TempRepo r) => GitRepository.OpenAsync(r.Path);

    /// <summary>Raw bytes of `git cat-file blob <rev>` (TempRepo.Git trims its output).</summary>
    static string Blob(TempRepo r, string rev)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = r.Path, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in (string[])["cat-file", "blob", rev]) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        using var ms = new MemoryStream();
        p.StandardOutput.BaseStream.CopyTo(ms);
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"cat-file {rev} failed: {p.StandardError.ReadToEnd()}");
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    static string Index(TempRepo r, string path) => Blob(r, ":" + path);
    static string Work(TempRepo r, string path) => File.ReadAllText(Path.Combine(r.Path, path));

    static HunkLine[] Pick(FileDiff d, Func<DiffLine, bool> pick) =>
        d.Hunks.SelectMany((h, hi) => h.Lines.Select((l, li) => (l, hi, li)))
            .Where(x => x.l.Kind != DiffLineKind.Context && pick(x.l)).Select(x => new HunkLine(x.hi, x.li)).ToArray();

    static Func<DiffLine, bool> Texts(params string[] texts) => l => texts.Contains(l.Text);

    /// <summary>Reads the diff, builds the patch from the picked +/- lines and applies it.</summary>
    static async Task Apply(GitRepository repo, WorkingDiff kind, string path, PatchAction action, Func<DiffLine, bool> pick, string? oldPath = null)
    {
        var diff = await repo.ReadFileDiffAsync(new DiffRequest(null, "", path, oldPath, Working: kind), ct: TestContext.Current.CancellationToken);
        var patch = PatchBuilder.Build(diff, Pick(diff, pick), action);
        Assert.NotNull(patch);
        await repo.ApplyPatchAsync(patch, action, ct: TestContext.Current.CancellationToken);
    }

    // ---- working diffs ----

    [Fact]
    public async Task WorkingDiffs_AllKinds()
    {
        using var r = new TempRepo();
        r.Commit("c1", 1, ("a.txt", Six));
        r.Write("a.txt", SixChanged);
        r.Git("add", "a.txt");
        r.Write("a.txt", SixChanged + "seven\n");
        r.Write("dir/n ä.txt", "x\ny\n");
        var repo = await Open(r);

        var unstaged = await repo.ReadFileDiffAsync(new DiffRequest(null, "", "a.txt", null, Working: WorkingDiff.Unstaged), ct: TestContext.Current.CancellationToken);
        Assert.Equal(["seven"], unstaged.Hunks.SelectMany(h => h.Lines).Where(l => l.Kind == DiffLineKind.Added).Select(l => l.Text));

        var staged = await repo.ReadFileDiffAsync(new DiffRequest(null, "", "a.txt", null, Working: WorkingDiff.Staged), ct: TestContext.Current.CancellationToken);
        Assert.Equal(["TWO", "THREE"], staged.Hunks.SelectMany(h => h.Lines).Where(l => l.Kind == DiffLineKind.Added).Select(l => l.Text));

        var untracked = await repo.ReadFileDiffAsync(new DiffRequest(null, "", "dir/n ä.txt", null, Working: WorkingDiff.Untracked), ct: TestContext.Current.CancellationToken);
        Assert.Null(untracked.OldPath);
        Assert.Equal("dir/n ä.txt", untracked.NewPath);
        Assert.Equal(["x", "y"], untracked.Hunks.Single().Lines.Select(l => l.Text));

        await Assert.ThrowsAsync<GitException>(() =>
            repo.ReadFileDiffAsync(new DiffRequest(null, "", "missing.txt", null, Working: WorkingDiff.Untracked), ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StagedDiff_OnUnbornHead()
    {
        using var r = new TempRepo();
        r.Write("a.txt", "1\n");
        r.Git("add", "a.txt");
        var d = await (await Open(r)).ReadFileDiffAsync(new DiffRequest(null, "", "a.txt", null, Working: WorkingDiff.Staged), ct: TestContext.Current.CancellationToken);
        Assert.Null(d.OldPath);
        Assert.Equal("1", d.Hunks.Single().Lines.Single().Text);
    }

    // ---- line patches against git ----

    [Fact]
    public async Task PartialStage_Modified()
    {
        using var r = new TempRepo();
        r.Commit("c1", 1, ("a.txt", Six));
        r.Write("a.txt", SixChanged);
        await Apply(await Open(r), WorkingDiff.Unstaged, "a.txt", PatchAction.Stage, Texts("two", "TWO"));
        Assert.Equal("one\nTWO\nthree\nfour\nfive\nsix\n", Index(r, "a.txt"));
        Assert.Equal(SixChanged, Work(r, "a.txt"));
    }

    [Fact]
    public async Task PartialUnstage()
    {
        using var r = new TempRepo();
        r.Commit("c1", 1, ("a.txt", Six));
        r.Write("a.txt", SixChanged);
        r.Git("add", "a.txt");
        await Apply(await Open(r), WorkingDiff.Staged, "a.txt", PatchAction.Unstage, Texts("two", "TWO"));
        Assert.Equal("one\ntwo\nTHREE\nfour\nfive\nsix\n", Index(r, "a.txt"));
        Assert.Equal(SixChanged, Work(r, "a.txt"));
    }

    [Fact]
    public async Task PartialDiscard()
    {
        using var r = new TempRepo();
        r.Commit("c1", 1, ("a.txt", Six));
        r.Write("a.txt", SixChanged);
        await Apply(await Open(r), WorkingDiff.Unstaged, "a.txt", PatchAction.Discard, Texts("two", "TWO"));
        Assert.Equal("one\ntwo\nTHREE\nfour\nfive\nsix\n", Work(r, "a.txt"));
        Assert.Equal(Six, Index(r, "a.txt"));
    }

    [Fact]
    public async Task Crlf_BytesKeepCrLf()
    {
        using var r = new TempRepo();
        r.Commit("c1", 1, ("a.txt", "a\r\nb\r\nc\r\n"));
        r.Write("a.txt", "a\r\nB\r\nc\r\nD\r\n");
        await Apply(await Open(r), WorkingDiff.Unstaged, "a.txt", PatchAction.Stage, l => l.Text.StartsWith('b') || l.Text.StartsWith('B'));
        Assert.Equal("a\r\nB\r\nc\r\n", Index(r, "a.txt"));
    }

    [Fact]
    public async Task NoNewlineAtEnd_LastLineSelected()
    {
        using var r = new TempRepo();
        r.Commit("c1", 1, ("a.txt", "1\n2\n3"));
        r.Write("a.txt", "X\n2\nY");
        await Apply(await Open(r), WorkingDiff.Unstaged, "a.txt", PatchAction.Stage, Texts("3", "Y"));
        Assert.Equal("1\n2\nY", Index(r, "a.txt"));
    }

    [Fact]
    public async Task NoNewlineAtEnd_LastLineNotSelected()
    {
        using var r = new TempRepo();
        r.Commit("c1", 1, ("a.txt", "1\n2\n3"));
        r.Write("a.txt", "X\n2\nY");
        var repo = await Open(r);
        await Apply(repo, WorkingDiff.Unstaged, "a.txt", PatchAction.Stage, Texts("1", "X"));
        Assert.Equal("X\n2\n3", Index(r, "a.txt"));
        // Discard swaps the roles: the markers follow "3" (now +) and "Y" (now -).
        await Apply(repo, WorkingDiff.Unstaged, "a.txt", PatchAction.Discard, Texts("3", "Y"));
        Assert.Equal("X\n2\n3", Work(r, "a.txt"));
    }

    // Review regressions: a "No newline" marker is only valid on the last line of its side.
    [Fact]
    public async Task NoNewline_StageAfterLineWithoutNewline()
    {
        using var r = new TempRepo();
        r.Commit("c1", 1, ("f.txt", "o0"));
        r.Write("f.txt", "n0\r\n");
        await Apply(await Open(r), WorkingDiff.Unstaged, "f.txt", PatchAction.Stage, Texts("n0\r"));
        Assert.Equal("o0\r\nn0\r\n", Index(r, "f.txt")); // o0 gains the file's CRLF
    }

    [Fact]
    public async Task NoNewline_DiscardPutsBackFormerLastLine()
    {
        using var r = new TempRepo();
        r.Commit("c1", 1, ("f.txt", "o0\no1"));
        r.Write("f.txt", "o0\nn0");
        await Apply(await Open(r), WorkingDiff.Unstaged, "f.txt", PatchAction.Discard, Texts("o1"));
        Assert.Equal("o0\no1\nn0", Work(r, "f.txt"));
    }

    [Fact]
    public async Task NoNewline_UnstageFormerLastLine()
    {
        using var r = new TempRepo();
        r.Commit("c1", 1, ("f.txt", "o0\no1\no2"));
        r.Write("f.txt", "o0\no1\nn0\no2\n");
        r.Git("add", "f.txt");
        var repo = await Open(r);
        var diff = await repo.ReadFileDiffAsync(new DiffRequest(null, "", "f.txt", null, Working: WorkingDiff.Staged), ct: TestContext.Current.CancellationToken);
        var pick = Pick(diff, l => l.Kind == DiffLineKind.Removed && l.Text == "o2" || l.Text == "n0");
        await repo.ApplyPatchAsync(PatchBuilder.Build(diff, pick, PatchAction.Unstage)!, PatchAction.Unstage, ct: TestContext.Current.CancellationToken);
        Assert.Equal("o0\no1\no2\no2\n", Index(r, "f.txt"));
    }

    [Fact]
    public async Task Untracked_StageTwoOfFive_AndDiscardLines()
    {
        using var r = new TempRepo();
        r.Commit("c1", 1, ("keep.txt", "k\n"));
        r.Write("n.txt", "1\n2\n3\n4\n5\n");
        var repo = await Open(r);
        await Apply(repo, WorkingDiff.Untracked, "n.txt", PatchAction.Stage, Texts("2", "4"));
        Assert.Equal("2\n4\n", Index(r, "n.txt"));
        Assert.Equal("1\n2\n3\n4\n5\n", Work(r, "n.txt"));
        var status = await repo.ReadStatusAsync(ct: TestContext.Current.CancellationToken);
        Assert.Contains(status.Staged, e => e.Path == "n.txt" && e.Kind == ChangeKind.Added);
        Assert.Contains(status.Unstaged, e => e.Path == "n.txt" && e.Kind == ChangeKind.Modified);

        r.Write("u.txt", "1\n2\n3\n4\n5\n");
        await Apply(repo, WorkingDiff.Untracked, "u.txt", PatchAction.Discard, Texts("2", "4"));
        Assert.Equal("1\n3\n5\n", Work(r, "u.txt"));
    }

    [Fact]
    public async Task NewInIndex_UnstageSomeLines_NonContiguous()
    {
        using var r = new TempRepo();
        r.Commit("c1", 1, ("keep.txt", "k\n"));
        r.Write("n.txt", "1\n2\n3\n4\n5\n");
        r.Git("add", "n.txt");
        var repo = await Open(r);
        await Apply(repo, WorkingDiff.Staged, "n.txt", PatchAction.Unstage, Texts("2", "4"));
        Assert.Equal("1\n3\n5\n", Index(r, "n.txt"));

        // Stage of non-contiguous deleted lines of one run: "3" stays between them.
        r.Write("keep.txt", "1\n2\n3\n4\n5\n");
        r.Git("add", "keep.txt");
        r.Write("keep.txt", "");
        await Apply(repo, WorkingDiff.Unstaged, "keep.txt", PatchAction.Stage, Texts("2", "4"));
        Assert.Equal("1\n3\n5\n", Index(r, "keep.txt"));
    }

    [Fact]
    public async Task Deleted_FullAndPartialStage()
    {
        using var r = new TempRepo();
        r.Commit("c1", 1, ("d.txt", "1\n2\n3\n"), ("e.txt", "1\n2\n3\n"));
        File.Delete(Path.Combine(r.Path, "d.txt"));
        File.Delete(Path.Combine(r.Path, "e.txt"));
        var repo = await Open(r);

        await Apply(repo, WorkingDiff.Unstaged, "d.txt", PatchAction.Stage, _ => true);
        Assert.Equal("", r.Git("ls-files", "d.txt"));

        await Apply(repo, WorkingDiff.Unstaged, "e.txt", PatchAction.Stage, Texts("2"));
        Assert.Equal("1\n3\n", Index(r, "e.txt"));
    }

    [Fact]
    public async Task Deleted_UnstagePartOfStagedDeletion()
    {
        using var r = new TempRepo();
        r.Commit("c1", 1, ("d.txt", "1\n2\n3\n"));
        r.Git("rm", "-q", "d.txt");
        await Apply(await Open(r), WorkingDiff.Staged, "d.txt", PatchAction.Unstage, Texts("2"));
        Assert.Equal("2\n", Index(r, "d.txt"));
        Assert.False(File.Exists(Path.Combine(r.Path, "d.txt")));
    }

    [Fact]
    public async Task Deleted_DiscardLinesOfWorkingTreeDeletion()
    {
        using var r = new TempRepo();
        r.Commit("c1", 1, ("d.txt", "1\n2\n3\n"));
        File.Delete(Path.Combine(r.Path, "d.txt"));
        await Apply(await Open(r), WorkingDiff.Unstaged, "d.txt", PatchAction.Discard, Texts("1", "3"));
        Assert.Equal("1\n3\n", Work(r, "d.txt"));
        Assert.Equal("1\n2\n3\n", Index(r, "d.txt"));
    }

    [Fact]
    public async Task StagedRenameWithChange_UnstageSomeLines_RenameStays()
    {
        using var r = new TempRepo();
        const string Ten = "1\n2\n3\n4\n5\n6\n7\n8\n9\n10\n";
        r.Commit("c1", 1, ("old name.txt", Ten));
        r.Git("mv", "old name.txt", "new name.txt");
        r.Write("new name.txt", Ten.Replace("2\n", "TWO\n").Replace("9\n", "NINE\n"));
        r.Git("add", "new name.txt");
        await Apply(await Open(r), WorkingDiff.Staged, "new name.txt", PatchAction.Unstage, Texts("2", "TWO"), "old name.txt");
        Assert.Equal(Ten.Replace("9\n", "NINE\n"), Index(r, "new name.txt"));
        Assert.StartsWith("R", r.Git("diff", "--cached", "--name-status", "-M"));
    }

    [Fact]
    public async Task SelectionSpanningTwoHunks()
    {
        using var r = new TempRepo();
        var lines = Enumerable.Range(1, 20).Select(i => i.ToString()).ToList();
        r.Commit("c1", 1, ("a.txt", string.Join('\n', lines) + "\n"));
        var changed = lines.Select(l => l is "2" or "10" or "18" ? "X" + l : l);
        r.Write("a.txt", string.Join('\n', changed) + "\n");
        var repo = await Open(r);
        var diff = await repo.ReadFileDiffAsync(new DiffRequest(null, "", "a.txt", null, Context: 1, Working: WorkingDiff.Unstaged), ct: TestContext.Current.CancellationToken);
        Assert.Equal(3, diff.Hunks.Count);
        await Apply(repo, WorkingDiff.Unstaged, "a.txt", PatchAction.Stage, Texts("2", "X2", "18", "X18"));
        Assert.Equal(string.Join('\n', lines.Select(l => l is "2" or "18" ? "X" + l : l)) + "\n", Index(r, "a.txt"));
    }

    [Fact]
    public async Task SpecialPath_SpacesAndUmlauts()
    {
        using var r = new TempRepo();
        r.Commit("c1", 1, ("dir ä/x \"q\".txt", Six));
        r.Write("dir ä/x \"q\".txt", SixChanged);
        await Apply(await Open(r), WorkingDiff.Unstaged, "dir ä/x \"q\".txt", PatchAction.Stage, Texts("two", "TWO"));
        Assert.Equal("one\nTWO\nthree\nfour\nfive\nsix\n", Index(r, "dir ä/x \"q\".txt"));
    }

    [Fact]
    public async Task Binary_NoPatch_WholeFileOpsWork()
    {
        using var r = new TempRepo();
        r.Commit("c1", 1, ("b.bin", "a\0b"));
        r.Write("b.bin", "a\0c");
        var repo = await Open(r);
        var diff = await repo.ReadFileDiffAsync(new DiffRequest(null, "", "b.bin", null, Working: WorkingDiff.Unstaged), ct: TestContext.Current.CancellationToken);
        Assert.True(diff.IsBinary);
        Assert.Null(PatchBuilder.Build(diff, [], PatchAction.Stage));

        await repo.StageAsync(["b.bin"], ct: TestContext.Current.CancellationToken);
        Assert.Equal("a\0c", Index(r, "b.bin"));
        await repo.UnstageAsync((await repo.ReadStatusAsync(ct: TestContext.Current.CancellationToken)).Staged, amend: false, ct: TestContext.Current.CancellationToken);
        Assert.Equal("a\0b", Index(r, "b.bin"));
        await repo.DiscardAsync((await repo.ReadStatusAsync(ct: TestContext.Current.CancellationToken)).Unstaged, ct: TestContext.Current.CancellationToken);
        Assert.Equal("a\0b", Work(r, "b.bin"));
    }

    [Fact]
    public async Task PatchDoesNotApply_GetsWhitespaceHint()
    {
        using var r = new TempRepo();
        r.Commit("c1", 1, ("a.txt", Six));
        r.Write("a.txt", SixChanged);
        var repo = await Open(r);
        var diff = await repo.ReadFileDiffAsync(new DiffRequest(null, "", "a.txt", null, Working: WorkingDiff.Unstaged), ct: TestContext.Current.CancellationToken);
        var patch = PatchBuilder.Build(diff, Pick(diff, Texts("two", "TWO")), PatchAction.Stage)!;
        r.Write("a.txt", "totally\ndifferent\n");
        var ex = await Assert.ThrowsAsync<GitException>(() => repo.ApplyPatchAsync(patch, PatchAction.Discard, ct: TestContext.Current.CancellationToken));
        Assert.EndsWith("\ngergit: try to disable 'ignore whitespaces'", ex.Message);
    }

    [Fact]
    public async Task EndToEnd_PartialCommit()
    {
        using var r = new TempRepo();
        const string Ten = "1\n2\n3\n4\n5\n6\n7\n8\n9\n10\n";
        r.Commit("c1", 1, ("a.txt", Ten));
        var both = Ten.Replace("2\n", "X2\n").Replace("9\n", "X9\n");
        r.Write("a.txt", both);
        var repo = await Open(r);
        await Apply(repo, WorkingDiff.Unstaged, "a.txt", PatchAction.Stage, Texts("2", "X2"));
        var result = await repo.CommitAsync("Partial", amend: false, noVerify: false, ct: TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.ReadableOutput);
        Assert.Equal(Ten.Replace("2\n", "X2\n"), Blob(r, "HEAD:a.txt"));
        Assert.Equal(both, Work(r, "a.txt"));
    }

    // ---- file operations ----

    [Fact]
    public async Task StageAndUnstage_ModifiedAddedRenamed()
    {
        using var r = new TempRepo();
        r.Commit("c1", 1, ("a.txt", "a\n"), ("old.txt", "same content\n"));
        r.Write("a.txt", "A\n");
        r.Write("n ä.txt", "new\n");
        r.Git("mv", "old.txt", "renamed.txt");
        var repo = await Open(r);

        await repo.StageAsync(["a.txt", "n ä.txt"], ct: TestContext.Current.CancellationToken);
        var staged = (await repo.ReadStatusAsync(ct: TestContext.Current.CancellationToken)).Staged;
        Assert.Equal(["a.txt:Modified", "n ä.txt:Added", "renamed.txt:Renamed"], staged.Select(e => $"{e.Path}:{e.Kind}"));

        await repo.UnstageAsync(staged, amend: false, ct: TestContext.Current.CancellationToken);
        var status = await repo.ReadStatusAsync(ct: TestContext.Current.CancellationToken);
        Assert.Empty(status.Staged);
        Assert.Equal(["a.txt:Modified", "n ä.txt:Untracked", "old.txt:Deleted", "renamed.txt:Untracked"],
            status.Unstaged.Select(e => $"{e.Path}:{e.Kind}"));
    }

    [Fact]
    public async Task Discard_TrackedUntrackedAndFolder()
    {
        using var r = new TempRepo();
        r.Commit("c1", 1, ("a.txt", "a\n"));
        r.Write("a.txt", "staged\n");
        r.Git("add", "a.txt");
        r.Write("a.txt", "work\n");
        r.Write("u.txt", "u\n");
        r.Write("dir/x.txt", "x\n");
        r.Write("dir/sub/y.txt", "y\n");
        var ro = Path.Combine(r.Path, "dir", "sub");
        File.SetUnixFileMode(ro, UnixFileMode.UserRead | UnixFileMode.UserExecute); // read-only folder: cleared and retried
        var repo = await Open(r);

        await repo.DiscardAsync([
            new StatusEntry("a.txt", null, ChangeKind.Modified, false),
            new StatusEntry("u.txt", null, ChangeKind.Untracked, false),
            new StatusEntry("dir", null, ChangeKind.Untracked, false), // a folder; "dir/" would be a nested repository
        ], ct: TestContext.Current.CancellationToken);
        Assert.Equal("staged\n", Work(r, "a.txt")); // restored from the index, staged change survives
        Assert.False(File.Exists(Path.Combine(r.Path, "u.txt")));
        Assert.False(Directory.Exists(Path.Combine(r.Path, "dir")));
    }

    [Fact]
    public async Task Discard_SymlinkToFolder_KeepsTarget()
    {
        using var r = new TempRepo();
        r.Commit("c1", 1, ("keep/k.txt", "k\n"));
        File.CreateSymbolicLink(Path.Combine(r.Path, "link"), Path.Combine(r.Path, "keep"));
        await (await Open(r)).DiscardAsync([new StatusEntry("link", null, ChangeKind.Untracked, false)], ct: TestContext.Current.CancellationToken);
        Assert.False(Path.Exists(Path.Combine(r.Path, "link")));
        Assert.True(File.Exists(Path.Combine(r.Path, "keep/k.txt")));
    }

    [Fact]
    public async Task Discard_ReadOnlyFolderWithSymlinksOutside_OutsideUnchanged()
    {
        using var r = new TempRepo();
        using var outside = new TempRepo(init: false);
        r.Commit("c1", 1, ("keep.txt", "k\n"));
        const UnixFileMode RX = UnixFileMode.UserRead | UnixFileMode.UserExecute, R = UnixFileMode.UserRead;
        outside.Write("dir/o.txt", "o\n");
        outside.Write("file.txt", "f\n");
        string oDir = Path.Combine(outside.Path, "dir"), oFile = Path.Combine(outside.Path, "file.txt");
        string oInner = Path.Combine(oDir, "o.txt"); // behind the folder link: the walk must not recurse into it
        r.Write("ro/sub/y.txt", "y\n");
        r.Write("rofile/f.txt", "f\n");
        File.CreateSymbolicLink(Path.Combine(r.Path, "ro/sub/dirlink"), oDir);
        File.CreateSymbolicLink(Path.Combine(r.Path, "ro/filelink"), oFile);
        foreach (var p in (string[])[oInner, oDir, oFile, Path.Combine(r.Path, "ro/sub"), Path.Combine(r.Path, "ro"), Path.Combine(r.Path, "rofile")])
            File.SetUnixFileMode(p, p == oFile || p == oInner ? R : RX);
        try
        {
            await (await Open(r)).DiscardAsync([
                new StatusEntry("ro", null, ChangeKind.Untracked, false), // a folder; "ro/" would be a nested repository
                new StatusEntry("rofile/f.txt", null, ChangeKind.Untracked, false), // file in a read-only folder
            ], ct: TestContext.Current.CancellationToken);
            Assert.False(Directory.Exists(Path.Combine(r.Path, "ro")));
            Assert.False(File.Exists(Path.Combine(r.Path, "rofile/f.txt")));
            Assert.Equal(RX, File.GetUnixFileMode(oDir));
            Assert.Equal(R, File.GetUnixFileMode(oFile));
            Assert.Equal(R, File.GetUnixFileMode(oInner));
        }
        finally
        {
            File.SetUnixFileMode(oDir, RX | UnixFileMode.UserWrite); // let TempRepo clean up
        }
    }

    [Fact]
    public async Task Discard_IntentToAdd_RemovesFileAndIndexEntry()
    {
        using var r = new TempRepo();
        r.Commit("c1", 1, ("keep.txt", "k\n"));
        r.Write("n.txt", "n\n");
        r.Git("add", "-N", "n.txt");
        var repo = await Open(r);
        var entry = (await repo.ReadStatusAsync(ct: TestContext.Current.CancellationToken)).Unstaged.Single();
        Assert.Equal(ChangeKind.Added, entry.Kind);
        await repo.DiscardAsync([entry], ct: TestContext.Current.CancellationToken);
        Assert.False(File.Exists(Path.Combine(r.Path, "n.txt")));
        var status = await repo.ReadStatusAsync(ct: TestContext.Current.CancellationToken);
        Assert.Empty(status.Unstaged);
        Assert.Empty(status.Staged);
    }

    [Fact]
    public async Task GlobLikeNames_AreLiteralPaths()
    {
        using var r = new TempRepo();
        r.Commit("c1", 1, ("a.txt", "a\n"), ("b.txt", "b\n"), ("[ab].txt", "x\n"), (".gitignore", "*.log\n"));
        r.Write("a.txt", "A\n");
        r.Write("b.txt", "B\n");
        r.Write("[ab].txt", "X\n");
        r.Write("*.log", "star\n");
        r.Write("x.log", "ignored\n");
        var repo = await Open(r);

        var diff = await repo.ReadFileDiffAsync(new DiffRequest(null, "", "[ab].txt", null, Working: WorkingDiff.Unstaged), ct: TestContext.Current.CancellationToken);
        Assert.Equal("[ab].txt", diff.NewPath);

        await repo.StageAsync(["[ab].txt", "*.log"], ct: TestContext.Current.CancellationToken);
        Assert.Equal(["*.log", "[ab].txt"], (await repo.ReadStatusAsync(ct: TestContext.Current.CancellationToken)).Staged.Select(e => e.Path));

        await repo.StageAsync(["a.txt"], ct: TestContext.Current.CancellationToken);
        await repo.UnstageAsync([new StatusEntry("[ab].txt", null, ChangeKind.Modified, true)], amend: false, ct: TestContext.Current.CancellationToken);
        Assert.Equal(["*.log", "a.txt"], (await repo.ReadStatusAsync(ct: TestContext.Current.CancellationToken)).Staged.Select(e => e.Path));
    }

    [Fact]
    public async Task Stage_RetriesWhileIndexIsLocked()
    {
        using var r = new TempRepo();
        r.Commit("c1", 1, ("a.txt", "a\n"));
        r.Write("a.txt", "A\n");
        var lockFile = Path.Combine(r.Path, ".git", "index.lock");
        File.WriteAllText(lockFile, "");
        var repo = await Open(r);
        var stage = repo.StageAsync(["a.txt"], ct: TestContext.Current.CancellationToken);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        File.Delete(lockFile);
        await stage;
        Assert.Equal("A\n", Index(r, "a.txt"));
    }

    [Fact]
    public async Task Amend_ListAndUnstage()
    {
        using var r = new TempRepo();
        const string Long = "line 1\nline 2\nline 3\nline 4\nline 5\n";
        var c1 = r.Commit("c1", 1, ("m.txt", "m\n"), ("d.txt", "d\n"), ("r.txt", Long));
        File.Delete(Path.Combine(r.Path, "d.txt"));
        File.Delete(Path.Combine(r.Path, "r.txt"));
        r.Commit("c2", 2, ("m.txt", "M\n"), ("n.txt", "n\n"), ("r2.txt", Long + "line 6\n"));
        var repo = await Open(r);

        var list = await repo.ReadAmendStagedAsync(ct: TestContext.Current.CancellationToken);
        Assert.Equal(["d.txt:Deleted:", "m.txt:Modified:", "n.txt:Added:", "r2.txt:Renamed:r.txt"],
            list.Select(e => $"{e.Path}:{e.Kind}:{e.OldPath}"));
        Assert.All(list, e => Assert.True(e.Staged));
        var m = list.Single(e => e.Path == "m.txt");
        Assert.Equal("100644", m.Mode);
        Assert.Equal(r.Git("rev-parse", c1 + ":m.txt"), m.SourceBlob);
        Assert.Equal("100644", list.Single(e => e.Path == "d.txt").Mode); // source mode

        var diff = await repo.ReadFileDiffAsync(new DiffRequest(null, "", "m.txt", null, Working: WorkingDiff.StagedAmend), ct: TestContext.Current.CancellationToken);
        Assert.Equal(["-m", "+M"], diff.Hunks.Single().Lines.Select(l => (l.Kind == DiffLineKind.Added ? "+" : "-") + l.Text));

        await repo.UnstageAsync(list, amend: true, ct: TestContext.Current.CancellationToken);
        Assert.Equal(r.Git("rev-parse", c1 + "^{tree}"), r.Git("write-tree"));
    }

    [Fact]
    public async Task Amend_Unstage_RestoresModesAndTypes()
    {
        using var r = new TempRepo();
        const string Long = "line 1\nline 2\nline 3\nline 4\nline 5\n";
        var exec = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        r.Write("x.sh", "x\n");
        r.Write("d.sh", "d\n");
        r.Write("r.sh", Long);
        r.Write("m.txt", "m\n");
        foreach (var f in (string[])["x.sh", "d.sh", "r.sh"]) File.SetUnixFileMode(Path.Combine(r.Path, f), exec);
        var c1 = r.Commit("c1", 1);

        File.SetUnixFileMode(Path.Combine(r.Path, "x.sh"), UnixFileMode.UserRead | UnixFileMode.UserWrite); // mode change
        File.Delete(Path.Combine(r.Path, "d.sh"));                                                         // exec file deleted
        r.Git("mv", "r.sh", "r2.sh");                                                                       // exec file renamed
        File.Delete(Path.Combine(r.Path, "m.txt"));
        File.CreateSymbolicLink(Path.Combine(r.Path, "m.txt"), "x.sh");                                     // file → symlink
        r.Commit("c2", 2);
        var repo = await Open(r);

        var list = await repo.ReadAmendStagedAsync(ct: TestContext.Current.CancellationToken);
        Assert.Equal(["d.sh:Deleted:100755", "m.txt:TypeChanged:100644", "r2.sh:Renamed:100755", "x.sh:Modified:100755"],
            list.Select(e => $"{e.Path}:{e.Kind}:{e.Mode}"));
        await repo.UnstageAsync(list, amend: true, ct: TestContext.Current.CancellationToken);
        Assert.Equal(r.Git("rev-parse", c1 + "^{tree}"), r.Git("write-tree"));
    }

    [Fact]
    public async Task Amend_RootCommit_UsesEmptyTree()
    {
        using var r = new TempRepo();
        r.Commit("c1", 1, ("a.txt", "a\n"));
        var repo = await Open(r);
        var list = await repo.ReadAmendStagedAsync(ct: TestContext.Current.CancellationToken);
        Assert.Equal("a.txt:Added", $"{list.Single().Path}:{list.Single().Kind}");
        var diff = await repo.ReadFileDiffAsync(new DiffRequest(null, "", "a.txt", null, Working: WorkingDiff.StagedAmend), ct: TestContext.Current.CancellationToken);
        Assert.Null(diff.OldPath);
        await repo.UnstageAsync(list, amend: true, ct: TestContext.Current.CancellationToken);
        Assert.Equal("", r.Git("ls-files"));
    }

    [Fact]
    public async Task Amend_RootCommit_Sha256Repository()
    {
        using var r = new TempRepo(init: false);
        r.Git("init", "-q", "-b", "main", "--object-format=sha256");
        r.Git("config", "user.name", "Test User");
        r.Git("config", "user.email", "test@example.com");
        r.Commit("c1", 1, ("a.txt", "a\n"));
        var repo = await Open(r);
        var ct = TestContext.Current.CancellationToken;
        var list = await repo.ReadAmendStagedAsync(ct);
        Assert.Equal("a.txt:Added", $"{list.Single().Path}:{list.Single().Kind}");
        Assert.Equal(64, list.Single().SourceBlob!.Length); // SHA-256 ids
        // The root commit's changed files and diff use the same empty tree (root retry).
        var sha = r.Git("rev-parse", "HEAD");
        Assert.Equal("a.txt", (await repo.ReadChangedFilesAsync(null, sha, ct)).Single().Path);
        Assert.Single((await repo.ReadFileDiffAsync(new DiffRequest(null, sha, "a.txt", null), ct)).Hunks);
        await repo.UnstageAsync(list, amend: true, ct);
        Assert.Equal("", r.Git("ls-files"));
    }

    [Fact]
    public async Task Status_IntentToAddMatchingADeletedFile_GivesAddedAndDeletedRows()
    {
        using var r = new TempRepo();
        var content = string.Join("\n", Enumerable.Range(1, 20).Select(i => $"line {i}")) + "\n";
        r.Commit("c1", 1, ("old.txt", content));
        File.Delete(Path.Combine(r.Path, "old.txt"));
        r.Write("new.txt", content);
        r.Git("add", "-N", "new.txt");
        Assert.StartsWith("R new.txt\0old.txt", r.Git("status", "--porcelain", "-z", "--untracked-files=all")); // " R", trimmed
        var status = await (await Open(r)).ReadStatusAsync(ct: TestContext.Current.CancellationToken);
        Assert.Equal(["new.txt:Added", "old.txt:Deleted"], status.Unstaged.Select(e => $"{e.Path}:{e.Kind}"));
        Assert.Empty(status.Staged);
    }

    [Fact]
    public async Task Discard_NeverDeletesANestedRepository()
    {
        using var r = new TempRepo();
        r.Commit("c1", 1, ("a.txt", "a\n"));
        r.Write("outer/nested/n.txt", "n\n");
        r.Write("outer/x.txt", "x\n");
        r.Git("-C", "outer/nested", "init", "-q");
        r.Git("-C", "outer/nested", "add", "n.txt");
        r.Git("-C", "outer/nested", "-c", "user.name=T", "-c", "user.email=t@e", "commit", "-q", "-m", "nested");
        var repo = await Open(r);
        var ct = TestContext.Current.CancellationToken;
        var status = await repo.ReadStatusAsync(ct: ct);
        Assert.Equal(["outer/nested/:Untracked", "outer/x.txt:Untracked"], status.Unstaged.Select(e => $"{e.Path}:{e.Kind}"));
        await repo.DiscardAsync(status.Unstaged, ct);
        Assert.False(File.Exists(Path.Combine(r.Path, "outer", "x.txt")));
        Assert.Equal("nested", r.Git("-C", "outer/nested", "log", "-1", "--format=%s"));
    }

    // ---- commit ----

    [Fact]
    public async Task Commit_AndAmend()
    {
        using var r = new TempRepo();
        r.Commit("c1", 1, ("a.txt", "a\n"));
        r.Write("a.txt", "b\n");
        r.Git("add", "a.txt");
        var repo = await Open(r);

        var result = await repo.CommitAsync("Subject Ä\n\nBody", amend: false, noVerify: false, ct: TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.ReadableOutput);
        Assert.Equal("Subject Ä\n\nBody", await repo.ReadHeadMessageAsync(ct: TestContext.Current.CancellationToken));
        var file = File.ReadAllBytes(Path.Combine(r.Path, ".git", "GERGIT_COMMIT_EDITMSG"));
        Assert.Equal(Encoding.UTF8.GetBytes("Subject Ä\n\nBody"), file); // UTF-8, no BOM

        result = await repo.CommitAsync("Amended", amend: true, noVerify: false, ct: TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.ReadableOutput);
        Assert.Equal("Amended", await repo.ReadHeadMessageAsync(ct: TestContext.Current.CancellationToken));
        Assert.Equal("2", r.Git("rev-list", "--count", "HEAD"));
    }

    [Fact]
    public async Task UnbornRepo_StageUnstageAndFirstCommit()
    {
        using var r = new TempRepo();
        r.Write("a.txt", "a\n");
        var repo = await Open(r);
        await repo.StageAsync(["a.txt"], ct: TestContext.Current.CancellationToken);
        var staged = (await repo.ReadStatusAsync(ct: TestContext.Current.CancellationToken)).Staged;
        Assert.Equal(ChangeKind.Added, staged.Single().Kind);

        await repo.UnstageAsync(staged, amend: false, ct: TestContext.Current.CancellationToken);
        Assert.Empty((await repo.ReadStatusAsync(ct: TestContext.Current.CancellationToken)).Staged);

        await repo.StageAsync(["a.txt"], ct: TestContext.Current.CancellationToken);
        var result = await repo.CommitAsync("First", amend: false, noVerify: false, ct: TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.ReadableOutput);
        Assert.Equal("First", await repo.ReadHeadMessageAsync(ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Hooks_DetectionAndNoVerify()
    {
        using var r = new TempRepo();
        r.Commit("c1", 1, ("a.txt", "a\n"));
        var repo = await Open(r);
        Assert.False(await repo.HasCommitHooksAsync(ct: TestContext.Current.CancellationToken)); // only *.sample files

        var hook = Path.Combine(r.Path, ".git", "hooks", "pre-commit");
        Directory.CreateDirectory(Path.GetDirectoryName(hook)!);
        File.WriteAllText(hook, "#!/bin/sh\necho hook says no >&2\nexit 1\n");
        File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Assert.True(await repo.HasCommitHooksAsync(ct: TestContext.Current.CancellationToken));

        r.Write("a.txt", "b\n");
        r.Git("add", "a.txt");
        var failed = await repo.CommitAsync("msg", amend: false, noVerify: false, ct: TestContext.Current.CancellationToken);
        Assert.False(failed.Success);
        Assert.Contains("hook says no", failed.ReadableOutput);
        Assert.True((await repo.CommitAsync("msg", amend: false, noVerify: true, ct: TestContext.Current.CancellationToken)).Success);

        r.Git("config", "core.hooksPath", "myhooks"); // relative to the work tree, folder without hooks
        Assert.False(await repo.HasCommitHooksAsync(ct: TestContext.Current.CancellationToken));
        r.Write("myhooks/commit-msg", "");
        Assert.True(await repo.HasCommitHooksAsync(ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PreparedMessage()
    {
        using var r = new TempRepo();
        var repo = await Open(r);
        Assert.Null(repo.ReadPreparedMessage());
        File.WriteAllText(Path.Combine(r.Path, ".git", "SQUASH_MSG"), "");
        File.WriteAllText(Path.Combine(r.Path, ".git", "MERGE_MSG"), "Merge x\n");
        Assert.Equal("Merge x\n", repo.ReadPreparedMessage()); // empty SQUASH_MSG is skipped
        File.WriteAllText(Path.Combine(r.Path, ".git", "SQUASH_MSG"), "Squash\n");
        Assert.Equal("Squash\n", repo.ReadPreparedMessage());
    }

    // ---- message fields ----

    [Theory]
    [InlineData("\r\nSubject\n", "", "Subject")]
    [InlineData(" Subject ", "Body", " Subject \n\nBody")]
    [InlineData("Subject", " ", "Subject\n\n ")]
    public void CommitMessage_Build(string subject, string description, string expected) =>
        Assert.Equal(expected, CommitMessage.Build(subject, description));

    [Theory]
    [InlineData("Subject\n\nBody\n\nMore", "Subject", "Body\n\nMore")]
    [InlineData("Subject\r\n\r\nBody", "Subject", "Body")]
    [InlineData("Line 1\nLine 2\r\n\r\nBody\n\nx", "Line 1\nLine 2", "Body\n\nx")]
    [InlineData("\nSubject only\r\n", "Subject only", "")]
    [InlineData("\n\nBody", "", "Body")]
    public void CommitMessage_Split(string message, string subject, string description) =>
        Assert.Equal((subject, description), CommitMessage.Split(message));
}
