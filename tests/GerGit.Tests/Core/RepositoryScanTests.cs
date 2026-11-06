using System.Diagnostics;
using GerGit.Core;

namespace GerGit.Tests.Core;

/// <summary>Repository scan and the validity check, on a throwaway source folder (never the home folder).</summary>
public sealed class RepositoryScanTests : IDisposable
{
    readonly string _src = Directory.CreateTempSubdirectory("gergit-scan-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_src, recursive: true); }
        catch (IOException) { }
    }

    internal static void Git(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)}: {err}");
    }

    /// <summary>`git init` in a new folder below the source folder; returns its full path.</summary>
    internal static string Init(string root, string relative)
    {
        var dir = Path.Combine(root, relative);
        Directory.CreateDirectory(dir);
        Git(dir, "init", "-q", "-b", "main");
        return dir;
    }

    [Fact]
    public void Finds_repositories_up_to_four_levels_below_and_skips_nested_dollar_ignored_and_linked_ones()
    {
        var a = Init(_src, "a");
        Init(_src, "a/nested");                       // inside a found repository: not searched
        var b = Init(_src, "x/b");
        var deep = Init(_src, "l1/l2/l3/r4");          // depth 5: the source folder is level 1
        Init(_src, "l1/l2/l3/l4/r5");                  // one level too deep
        Init(_src, "$tmp/c");                          // "$" folders are skipped
        Init(_src, "ignored/d");
        Init(_src, "ignoredness/e");                  // only whole path components are ignored
        Directory.CreateDirectory(Path.Combine(_src, "plain"));
        Git(a, "-c", "user.name=t", "-c", "user.email=t@e", "commit", "-q", "--allow-empty", "-m", "x");
        Git(a, "worktree", "add", "-q", Path.Combine(_src, "wt"));  // a `.git` file: never found by scans

        var found = RepositoryScan.Scan([_src + "/"], 5, [Path.Combine(_src, "ignored") + "/"]);
        Assert.Equal([a, Path.Combine(_src, "ignoredness/e"), deep, b], found);

        Assert.Equal(RepoKind.Repository, RepositoryScan.Validate(a));
        Assert.Equal(RepoKind.Worktree, RepositoryScan.Validate(Path.Combine(_src, "wt")));
        Assert.Null(RepositoryScan.Validate(Path.Combine(_src, "plain")));
        Assert.Null(RepositoryScan.Validate(Path.Combine(_src, "missing")));
    }

    [Fact]
    public void The_source_folder_itself_can_be_a_repository_and_a_missing_folder_is_skipped()
    {
        Git(_src, "init", "-q");
        Init(_src, "inner");
        Assert.Equal([_src], RepositoryScan.Scan([_src, Path.Combine(_src, "gone")], 5, []));
    }

    [Fact]
    public void A_submodule_is_recognised()
    {
        var sub = Init(_src, "sub");
        Git(sub, "-c", "user.name=t", "-c", "user.email=t@e", "commit", "-q", "--allow-empty", "-m", "x");
        var super = Init(_src, "super");
        Git(super, "-c", "protocol.file.allow=always", "submodule", "add", "-q", sub, "mod");
        Assert.Equal(RepoKind.Submodule, RepositoryScan.Validate(Path.Combine(super, "mod")));
        Assert.Equal([sub, super], RepositoryScan.Scan([_src], 5, []));
    }

    [Fact]
    public void Symbolic_links_are_never_followed_and_a_linked_repository_is_found_by_its_link()
    {
        var outside = Directory.CreateTempSubdirectory("gergit-scan-out-").FullName;
        try
        {
            var src = Directory.CreateDirectory(Path.Combine(_src, "src")).FullName;
            var hidden = Init(src, ".hidden/repo");                                     // dot folders are walked
            Directory.CreateSymbolicLink(Path.Combine(src, "loop"), src);              // a self-link: no endless walk
            Init(outside, "far/away");
            var target = Init(outside, "linked");
            Directory.CreateSymbolicLink(Path.Combine(src, "out"), Path.Combine(outside, "far")); // leaves the source folder
            Directory.CreateSymbolicLink(Path.Combine(src, "repo-link"), target);      // is itself a repository

            Assert.Equal([hidden, Path.Combine(src, "repo-link")], RepositoryScan.Scan([src], 5, []));
            Assert.Equal(target, RepositoryScan.Normalize(Path.Combine(src, "repo-link") + "/"));
            Assert.Equal(Path.Combine(target, "not/yet"), RepositoryScan.Normalize(Path.Combine(src, "repo-link", "not", "yet")));
        }
        finally { Directory.Delete(outside, recursive: true); }
    }

    [Fact]
    public void Only_regular_files_count()
    {
        var file = Path.Combine(_src, "f.txt");
        File.WriteAllText(file, "x");
        File.CreateSymbolicLink(Path.Combine(_src, "link"), file);
        Assert.True(RepositoryScan.IsRegularFile(file));
        Assert.False(RepositoryScan.IsRegularFile(Path.Combine(_src, "link")));
        Assert.False(RepositoryScan.IsRegularFile("/dev/zero"));
        Assert.False(RepositoryScan.IsRegularFile(_src));
        Assert.False(RepositoryScan.IsRegularFile(Path.Combine(_src, "missing")));
    }

    [Fact]
    public void A_cancelled_scan_throws()
    {
        Init(_src, "a");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => RepositoryScan.Scan([_src], 5, [], cts.Token));
    }

    [Fact]
    public async Task A_FIFO_named_dot_git_or_commondir_is_never_opened()
    {
        static async Task MkFifo(string path) => await System.Diagnostics.Process.Start("mkfifo", path)!.WaitForExitAsync();
        var fake = Directory.CreateDirectory(Path.Combine(_src, "fake")).FullName;
        await MkFifo(Path.Combine(fake, ".git"));
        var check = Task.Run(() => RepositoryScan.Validate(fake));
        Assert.Same(check, await Task.WhenAny(check, Task.Delay(5000))); // returns at once, nothing blocks
        Assert.Null(await check);

        var a = Init(_src, "a");
        Git(a, "-c", "user.name=t", "-c", "user.email=t@e", "commit", "-q", "--allow-empty", "-m", "x");
        var wt = Path.Combine(_src, "wt");
        Git(a, "worktree", "add", "-q", wt);
        var commondir = Path.Combine(a, ".git", "worktrees", "wt", "commondir");
        File.Delete(commondir);
        await MkFifo(commondir);
        check = Task.Run(() => RepositoryScan.Validate(wt));
        Assert.Same(check, await Task.WhenAny(check, Task.Delay(5000)));
        Assert.Null(await check);
    }
}
