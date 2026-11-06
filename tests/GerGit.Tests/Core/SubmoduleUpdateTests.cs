using GerGit.Core;
using GerGit.Core.Git;

namespace GerGit.Tests.Core;

/// <summary>Tests that switch the global "update submodules" preference off run alone.</summary>
[CollectionDefinition("SubmodulePreference", DisableParallelization = true)]
public sealed class SubmodulePreferenceCollection;

/// <summary>
/// The automatic submodule update after pull and fast-forward: only submodules the user initialized are updated, never
/// one that the upstream just added to .gitmodules. The clone has `submodule.active=.` (what `clone --recurse-submodules`
/// writes), which makes plain git treat every listed submodule as active. All needed commits are already in the local
/// submodule clones. `protocol.file.allow=always` is set for every git process of these tests through GIT_CONFIG_COUNT
/// (a repository's own config does not reach the clone git starts for a submodule), so a submodule git tried to clone
/// would really be cloned and "not cloned" means something.
/// </summary>
[Collection("SubmodulePreference")]
public sealed class SubmoduleUpdateTests : IDisposable
{
    static readonly string[] ConfigEnv = ["GIT_CONFIG_COUNT", "GIT_CONFIG_KEY_0", "GIT_CONFIG_VALUE_0"];
    readonly List<TempRepo> _repos = [];
    readonly string?[] _savedEnv = ConfigEnv.Select(Environment.GetEnvironmentVariable).ToArray();

    public SubmoduleUpdateTests()
    {
        Environment.SetEnvironmentVariable("GIT_CONFIG_COUNT", "1");
        Environment.SetEnvironmentVariable("GIT_CONFIG_KEY_0", "protocol.file.allow");
        Environment.SetEnvironmentVariable("GIT_CONFIG_VALUE_0", "always");
    }

    public void Dispose()
    {
        for (int i = 0; i < ConfigEnv.Length; i++) Environment.SetEnvironmentVariable(ConfigEnv[i], _savedEnv[i]);
        foreach (var r in _repos) r.Dispose();
    }

    TempRepo New(bool init = true)
    {
        var r = new TempRepo(init);
        _repos.Add(r);
        return r;
    }

    static void AddSubmodule(TempRepo repo, string url, string path) => repo.Git("submodule", "add", "-q", url, path);

    /// <summary>Upstream "up": p1 records sub at s1; p2 moves sub to s2, adds a new submodule "evil" and then whatever
    /// <paramref name="p2"/> changes. The clone "down" is at p1 with "sub" initialized and checked out, main tracking
    /// origin/main (p2). Returns (down, s2).</summary>
    (TempRepo Down, string S2) Setup(Action<TempRepo>? p2 = null)
    {
        var sub = New();
        sub.Commit("s1", 0, ("s.txt", "1\n"));
        var s1 = sub.Git("rev-parse", "HEAD");
        var s2 = sub.Commit("s2", 1, ("s.txt", "2\n"));
        var evil = New();
        evil.Commit("evil", 0, ("e.txt", "e\n"));

        var up = New();
        AddSubmodule(up, sub.Path, "sub");
        up.GitEnv([], "-C", "sub", "checkout", "-q", s1);
        var p1 = up.Commit("p1", 0);
        up.GitEnv([], "-C", "sub", "checkout", "-q", s2);
        AddSubmodule(up, evil.Path, "evil");
        p2?.Invoke(up);
        up.Commit("p2", 1);

        var down = New(init: false);
        down.Git("clone", "-q", up.Path, ".");
        down.Git("reset", "-q", "--hard", p1);
        down.Git("submodule", "update", "--init", "-q");
        down.Git("config", "submodule.active", ".");
        down.Git("config", "protocol.file.allow", "always");
        return (down, s2);
    }

    static void AssertNotCloned(TempRepo repo, string path)
    {
        Assert.False(Path.Exists(Path.Combine(repo.Path, path, ".git")), $"'{path}' was cloned");
        Assert.DoesNotContain($"submodule.{path}.url", repo.Git("config", "--local", "--list"));
    }

    [Fact]
    public async Task Pull_updates_the_initialized_submodule_but_not_a_new_one()
    {
        var (down, s2) = Setup();
        var git = await GitRepository.OpenAsync(down.Path);

        var job = new Job("Pull");
        var r = await git.PullAsync(job, "origin", null, rebase: false, autostash: false, tags: false);

        Assert.True(r.Success, r.ReadableOutput);
        Assert.Equal(s2, down.GitEnv([], "-C", "sub", "rev-parse", "HEAD"));
        AssertNotCloned(down, "evil");
        Assert.Contains("$ git --literal-pathspecs submodule update -- sub\n", job.Output);
        Assert.DoesNotContain("--init", job.Output);
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("/etc")]
    public async Task A_gitmodules_path_outside_the_work_tree_does_not_stop_the_other_updates(string path)
    {
        var (down, s2) = Setup(up => up.Git("config", "-f", ".gitmodules", "submodule.bad.path", path));
        down.Git("config", "submodule.bad.url", down.Path); // even "initialized"
        var git = await GitRepository.OpenAsync(down.Path);

        var job = new Job("Pull");
        var r = await git.PullAsync(job, "origin", null, rebase: false, autostash: false, tags: false);

        Assert.True(r.Success, r.ReadableOutput);
        Assert.Equal(s2, down.GitEnv([], "-C", "sub", "rev-parse", "HEAD"));
        Assert.Contains("$ git --literal-pathspecs submodule update -- sub\n", job.Output);
    }

    [Fact]
    public async Task Pull_with_submodule_recurse_on_does_not_clone_a_new_submodule()
    {
        var (down, s2) = Setup();
        down.Git("config", "submodule.recurse", "true");
        var git = await GitRepository.OpenAsync(down.Path);

        var job = new Job("Pull");
        var r = await git.PullAsync(job, "origin", null, rebase: false, autostash: false, tags: false);

        Assert.True(r.Success, r.ReadableOutput);
        Assert.Equal(s2, down.GitEnv([], "-C", "sub", "rev-parse", "HEAD"));
        AssertNotCloned(down, "evil");
    }

    /// <summary>.gitmodules entries that point the name of the initialized "sub" somewhere else; none of them may lead
    /// to the new "evil" submodule being cloned or to a folder that is not a submodule being updated.</summary>
    [Theory]
    [InlineData("pathspec")]  // path = "." would match every submodule as a pathspec
    [InlineData("duplicate")] // a second `path = evil`: git takes the first value
    [InlineData("symlink")]   // path = a tracked symlink to "."
    [InlineData("folder")]    // path = a plain tracked folder with its own .gitmodules
    public async Task Misleading_gitmodules_entries_update_nothing(string variant)
    {
        var (down, _) = Setup(up =>
        {
            void SubPath(string path) => up.Git("config", "-f", ".gitmodules", "submodule.sub.path", path);
            switch (variant)
            {
                case "pathspec": SubPath("."); break;
                case "duplicate": up.Git("config", "-f", ".gitmodules", "--add", "submodule.sub.path", "evil"); break;
                case "symlink":
                    File.CreateSymbolicLink(Path.Combine(up.Path, "link"), ".");
                    SubPath("link");
                    break;
                case "folder":
                    up.Write("plain/.gitmodules", "[submodule \"sub\"]\n\tpath = evil\n");
                    SubPath("plain");
                    break;
            }
        });
        var git = await GitRepository.OpenAsync(down.Path);

        var job = new Job("Pull");
        var r = await git.PullAsync(job, "origin", null, rebase: false, autostash: false, tags: false);

        Assert.True(r.Success, r.ReadableOutput);
        AssertNotCloned(down, "evil");
        Assert.DoesNotContain("submodule update", job.Output);
    }

    [Fact]
    public async Task Fast_forward_updates_the_initialized_submodule_but_not_a_new_one()
    {
        var (down, s2) = Setup();
        down.Git("fetch", "-q");
        var git = await GitRepository.OpenAsync(down.Path);

        var job = new Job("Fast forward");
        var r = await git.FastForwardAsync(job, "main", "refs/remotes/origin/main", active: true);

        Assert.True(r.Success, r.ReadableOutput);
        Assert.Equal(s2, down.GitEnv([], "-C", "sub", "rev-parse", "HEAD"));
        AssertNotCloned(down, "evil");
    }

    [Fact]
    public async Task Preference_off_updates_nothing()
    {
        var (down, _) = Setup();
        var s1 = down.GitEnv([], "-C", "sub", "rev-parse", "HEAD");
        var git = await GitRepository.OpenAsync(down.Path);
        GitRepository.UpdateSubmodulesOnCheckout = false;
        try
        {
            var job = new Job("Pull");
            var r = await git.PullAsync(job, "origin", null, rebase: false, autostash: false, tags: false);

            Assert.True(r.Success, r.ReadableOutput);
            Assert.Equal(s1, down.GitEnv([], "-C", "sub", "rev-parse", "HEAD"));
            AssertNotCloned(down, "evil");
            Assert.DoesNotContain("submodule update", job.Output);
        }
        finally
        {
            GitRepository.UpdateSubmodulesOnCheckout = true;
        }
    }

    [Fact]
    public async Task Nested_initialized_submodules_are_updated_but_new_nested_ones_are_not_cloned()
    {
        var leaf = New();
        leaf.Commit("l1", 0, ("l.txt", "1\n"));
        var l1 = leaf.Git("rev-parse", "HEAD");
        var l2 = leaf.Commit("l2", 1, ("l.txt", "2\n"));
        var evil = New();
        evil.Commit("evil", 0, ("e.txt", "e\n"));

        // mid: m1 records leaf at l1; m2 moves leaf to l2 and adds "evil".
        var mid = New();
        AddSubmodule(mid, leaf.Path, "leaf");
        mid.GitEnv([], "-C", "leaf", "checkout", "-q", l1);
        var m1 = mid.Commit("m1", 0);
        mid.GitEnv([], "-C", "leaf", "checkout", "-q", l2);
        AddSubmodule(mid, evil.Path, "evil");
        var m2 = mid.Commit("m2", 1);

        // top: p1 records mid at m1, p2 at m2.
        var top = New();
        AddSubmodule(top, mid.Path, "mid");
        top.GitEnv([], "-C", "mid", "checkout", "-q", m1);
        var p1 = top.Commit("p1", 0);
        top.GitEnv([], "-C", "mid", "checkout", "-q", m2);
        top.Commit("p2", 1);

        var down = New(init: false);
        down.Git("clone", "-q", top.Path, ".");
        down.Git("reset", "-q", "--hard", p1);
        down.Git("submodule", "update", "--init", "--recursive", "-q");
        down.Git("config", "submodule.active", ".");
        down.GitEnv([], "-C", "mid", "config", "submodule.active", ".");
        var git = await GitRepository.OpenAsync(down.Path);

        var job = new Job("Pull");
        var r = await git.PullAsync(job, "origin", null, rebase: false, autostash: false, tags: false);

        Assert.True(r.Success, r.ReadableOutput);
        Assert.Equal(m2, down.GitEnv([], "-C", "mid", "rev-parse", "HEAD"));
        Assert.Equal(l2, down.GitEnv([], "-C", "mid/leaf", "rev-parse", "HEAD"));
        Assert.False(Path.Exists(Path.Combine(down.Path, "mid", "evil", ".git")), "nested 'evil' was cloned");
        Assert.DoesNotContain("submodule.evil.url", down.GitEnv([], "-C", "mid", "config", "--local", "--list"));
        Assert.Contains("$ git --literal-pathspecs -C mid submodule update -- leaf\n", job.Output);
    }
}
