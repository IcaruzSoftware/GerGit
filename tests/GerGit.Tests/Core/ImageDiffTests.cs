using GerGit.Core;
using GerGit.Core.Diff;
using GerGit.Core.Git;

namespace GerGit.Tests.Core;

/// <summary>Image diff: extension rule, difference image, binary data of both sides.</summary>
public sealed class ImageDiffTests : IDisposable
{
    readonly TempRepo _repo = new();
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _repo.Dispose();

    [Theory]
    [InlineData("a.png", true), InlineData("dir/b.JPG", true), InlineData("c.jpeg", true), InlineData("d.bmp", true),
     InlineData("e.gif", true), InlineData("f.ico", true), InlineData("g.TGA", true), InlineData("h.webp", true),
     InlineData("i.svg", false), InlineData("j.bin", false), InlineData("png", false), InlineData("k.png.txt", false)]
    public void Image_extensions(string path, bool image) => Assert.Equal(image, ImageDiff.IsImagePath(path));

    [Fact]
    public void Difference_marks_channel_changes_of_5_or_more()
    {
        byte[] old = [10, 10, 10, 255, /**/ 10, 10, 10, 255, /**/ 0, 0, 0, 0, /**/ 100, 100, 100, 100, /**/ 200, 200, 200, 255];
        byte[] @new = [14, 6, 10, 255, /**/ 10, 10, 15, 255, /**/ 0, 0, 0, 5, /**/ 100, 100, 100, 100, /**/ 200, 195, 200, 255];
        //             B and G off by 4      R off by 5             A off by 5         identical             G off by 5
        byte[] magenta = [0xFF, 0, 0xFF, 0xFF], none = [0, 0, 0, 0];
        Assert.Equal([.. none, .. magenta, .. magenta, .. none, .. magenta], ImageDiff.Difference(old, @new));
    }

    [Fact]
    public void Difference_needs_same_size_buffers() =>
        Assert.Throws<ArgumentException>(() => ImageDiff.Difference(new byte[8], new byte[4]));

    [Fact]
    public async Task ReadBytes_cancel_kills_git()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            GitRunner.ReadBytesAsync(_repo.Path, ["-c", "alias.slow=!sleep 20", "slow"], cts.Token));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"took {sw.Elapsed}");
    }

    [Fact]
    public async Task ReadBytes_unknown_blob_is_a_git_error() =>
        await Assert.ThrowsAsync<GitException>(() =>
            GitRunner.ReadBytesAsync(_repo.Path, ["cat-file", "blob", "1234567890123456789012345678901234567890"], Ct));

    Task<GitRepository> Open() => GitRepository.OpenAsync(_repo.Path);

    void WritePng(string path, byte[] png) => File.WriteAllBytes(Path.Combine(_repo.Path, path), png);

    async Task<(BinarySide? Old, BinarySide? New)> Sides(GitRepository repo, DiffRequest q, bool bytes = true)
    {
        var d = await repo.ReadFileDiffAsync(q, Ct);
        Assert.True(d.IsBinary);
        return (await repo.ReadBinarySideAsync(q, d, false, bytes, Ct), await repo.ReadBinarySideAsync(q, d, true, bytes, Ct));
    }

    [Fact]
    public async Task Sides_of_commit_staged_unstaged_and_untracked_diffs()
    {
        byte[] red = TestPng.Solid(3, 2, 0xFF0000FF), green = TestPng.Solid(3, 2, 0x00FF00FF),
               blue = TestPng.Solid(4, 4, 0x0000FFFF), white = TestPng.Solid(1, 1, 0xFFFFFFFF);
        WritePng("a.png", red);
        var c1 = _repo.Commit("one", 0);
        WritePng("a.png", green);
        var c2 = _repo.Commit("two", 1);
        var repo = await Open();

        // Commit: both blobs by their IDs from the index line.
        var (o, n) = await Sides(repo, new DiffRequest(null, c2, "a.png", null));
        Assert.Equal(red, o!.Bytes);
        Assert.Equal(green, n!.Bytes);
        // Root commit: the file was created, no old side.
        (o, n) = await Sides(repo, new DiffRequest(null, c1, "a.png", null));
        Assert.Null(o);
        Assert.Equal(red, n!.Bytes);
        Assert.Equal(red.Length, n.Size);

        // Staged (HEAD → index) and unstaged (index → working tree file).
        WritePng("a.png", blue);
        _repo.Git("add", "a.png");
        WritePng("a.png", white);
        (o, n) = await Sides(repo, new DiffRequest(null, "", "a.png", null, Working: WorkingDiff.Staged));
        Assert.Equal(green, o!.Bytes);
        Assert.Equal(blue, n!.Bytes);
        (o, n) = await Sides(repo, new DiffRequest(null, "", "a.png", null, Working: WorkingDiff.Unstaged));
        Assert.Equal(blue, o!.Bytes);
        Assert.Equal(white, n!.Bytes);

        // Size only (unknown-binary cards): `cat-file -s` and the file length.
        (o, n) = await Sides(repo, new DiffRequest(null, "", "a.png", null, Working: WorkingDiff.Unstaged), bytes: false);
        Assert.Equal(new BinarySide(blue.Length, null), o);
        Assert.Equal(new BinarySide(white.Length, null), n);

        // Untracked: only the new side, from disk; a working-tree deletion: only the old side.
        WritePng("u.png", red);
        (o, n) = await Sides(repo, new DiffRequest(null, "", "u.png", null, Working: WorkingDiff.Untracked));
        Assert.Null(o);
        Assert.Equal(red, n!.Bytes);
        File.Delete(Path.Combine(_repo.Path, "a.png"));
        (o, n) = await Sides(repo, new DiffRequest(null, "", "a.png", null, Working: WorkingDiff.Unstaged));
        Assert.Equal(blue, o!.Bytes);
        Assert.Null(n);
    }
}
