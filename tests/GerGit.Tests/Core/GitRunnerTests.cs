using System.Diagnostics;
using GerGit.Core;
using GerGit.Core.Git;

namespace GerGit.Tests.Core;

public class GitRunnerTests
{
    [Theory]
    [InlineData("git version 2.53.0\n", "2.53.0")]
    [InlineData("git version 2.39.2.windows.1", "2.39.2")]
    [InlineData("git version 2.39.2 (Apple Git-143)", "2.39.2")]
    [InlineData("git version 2.40", "2.40.0")]
    public void ParseVersion(string output, string expected) =>
        Assert.Equal(Version.Parse(expected), GitRunner.ParseVersion(output));

    [Theory]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("git version abc")]
    public void ParseVersion_Invalid(string output) => Assert.Null(GitRunner.ParseVersion(output));

    [Fact]
    public async Task GetVersion_SystemGit()
    {
        var v = await GitRunner.GetVersionAsync();
        Assert.NotNull(v);
        Assert.True(v >= GitRunner.MinimumVersion);
    }

    [Fact]
    public async Task NonZeroExit_IsAResultNotAnException()
    {
        using var repo = new TempRepo();
        var r = await GitRunner.RunAsync(repo.Path, ["rev-parse", "--verify", "no-such-ref"]);
        Assert.False(r.Success);
        Assert.NotEqual(0, r.ExitCode);
    }

    [Fact]
    public async Task Stdin_IsPassed()
    {
        using var repo = new TempRepo();
        var r = await GitRunner.RunAsync(repo.Path, ["hash-object", "--stdin"], "hello\n");
        Assert.Equal("ce013625030ba8dba906f756967f9e9ca394464a", r.Stdout.Trim());
    }

    [Fact]
    public async Task Environment_IsSetUpForGit()
    {
        using var repo = new TempRepo();
        var r = await GitRunner.RunAsync(repo.Path,
            ["-c", "alias.envtest=!echo \"$LC_MESSAGES|${LC_ALL-unset}|${LANGUAGE-unset}|$GIT_TERMINAL_PROMPT|$GIT_EDITOR\"", "envtest"]);
        Assert.Equal("C|unset|unset|0|true", r.Stdout.Trim());
    }

    [Fact]
    public async Task InvalidUtf8_BecomesReplacementChar()
    {
        using var repo = new TempRepo();
        File.WriteAllBytes(Path.Combine(repo.Path, "bad.txt"), [0x61, 0xff, 0x62]);
        var sha = repo.Git("hash-object", "-w", "bad.txt");
        var r = await GitRunner.RunAsync(repo.Path, ["cat-file", "blob", sha]);
        Assert.Equal("a�b", r.Stdout);
    }

    [Fact]
    public async Task Cancel_KillsProcess()
    {
        using var repo = new TempRepo();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var sw = Stopwatch.StartNew();
        var r = await GitRunner.RunAsync(repo.Path, ["-c", "alias.slow=!sleep 20", "slow"], ct: cts.Token);
        Assert.True(r.Cancelled);
        Assert.False(r.Success);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Run_FloodsStdoutAndStderrAtOnce_DoesNotDeadlock()
    {
        using var repo = new TempRepo();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60)); // a pipe deadlock shows up as Cancelled
        var r = await GitRunner.RunAsync(repo.Path,
            ["-c", "alias.flood=!yes out | head -c 5000000 & yes err | head -c 5000000 >&2; wait", "flood"], ct: cts.Token);
        Assert.False(r.Cancelled);
        Assert.True(r.Success);
        Assert.Equal(5_000_000, r.Stdout.Length);
        Assert.Equal(5_000_000, r.Stderr.Length);
    }

    [Fact]
    public async Task ReadLines_StreamsAndThrowsOnFailure()
    {
        using var repo = new TempRepo();
        var lines = new List<string>();
        await foreach (var l in GitRunner.ReadLinesAsync(repo.Path, ["-c", "alias.lines=!printf 'a\\nb\\r\\nc'", "lines"]))
            lines.Add(l);
        Assert.Equal(["a", "b", "c"], lines);

        await Assert.ThrowsAsync<GitException>(async () =>
        {
            await foreach (var _ in GitRunner.ReadLinesAsync(repo.Path, ["log", "no-such-ref"])) { }
        });
    }

    [Fact]
    public async Task ReadLines_EarlyBreakDoesNotHang()
    {
        using var repo = new TempRepo();
        // Plenty of output so git would block on a full pipe if it were not killed.
        await foreach (var _ in GitRunner.ReadLinesAsync(repo.Path, ["-c", "alias.many=!yes", "many"]))
            break;
    }

    [Theory]
    [InlineData("https://user:secret@example.com/a.git", "https://user:***@example.com/a.git")]
    [InlineData("https://user:p@ss@example.com:8443/a.git", "https://user:***@example.com:8443/a.git")]
    [InlineData("https://:token@example.com", "https://:***@example.com")]
    [InlineData("https://user@example.com/a.git", "https://***@example.com/a.git")] // a plain user is masked too
    [InlineData("https://ghp_abc123@github.com/o/r.git", "https://***@github.com/o/r.git")]
    [InlineData("HTTP://tok@example.com", "HTTP://***@example.com")]
    [InlineData("ssh://git@example.com/a.git", "ssh://git@example.com/a.git")]
    [InlineData("ssh://me:secret@example.com/a.git", "ssh://me:***@example.com/a.git")]
    [InlineData("https://example.com:8443/a@b.git", "https://example.com:8443/a@b.git")]
    [InlineData("git@example.com:a.git", "git@example.com:a.git")]
    [InlineData("--upload-pack=x", "--upload-pack=x")]
    public void MaskPassword(string arg, string shown) => Assert.Equal(shown, GitRunner.MaskPassword(arg));

    [Fact]
    public async Task Job_header_masks_the_password_of_a_url_but_git_gets_it()
    {
        using var repo = new TempRepo();
        var job = new Job("Add remote");
        var r = await GitRunner.RunJobAsync(job, repo.Path, ["remote", "add", "origin", "https://me:secret@example.com/a.git"]);
        Assert.True(r.Success, r.ReadableOutput);
        Assert.Contains("$ git remote add origin https://me:***@example.com/a.git\n", job.Output);
        Assert.DoesNotContain("secret", job.Output);
        Assert.Equal("https://me:secret@example.com/a.git", repo.Git("config", "remote.origin.url"));
    }
}
