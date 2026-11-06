using Avalonia.Headless.XUnit;
using GerGit.Core;
using GerGit.ViewModels.Dialogs.Refs;

namespace GerGit.Tests.Core;

/// <summary>Branch and tag names that come from a remote and look like git options. Over a local or file:// remote,
/// "--upload-pack=&lt;cmd&gt;" read as an option would run the command; the name used here would create a file PWNED in
/// the folder git runs in.</summary>
public sealed class RemoteNameInjectionTests
{
    /// <summary>A valid ref name (no spaces, no path) that, read as an option, runs `touch PWNED` through the shell.</summary>
    internal const string Payload = "--upload-pack=touch${IFS}PWNED;git-upload-pack";

    /// <summary>origin gets a branch named <see cref="Payload"/> at main's commit; Local has fetched it.</summary>
    static RemoteSetup Setup()
    {
        var s = new RemoteSetup();
        try
        {
            s.Other.Git("push", "-q", "origin", "main:refs/heads/" + Payload);
            s.Local.Git("fetch", "-q", "origin");
            return s;
        }
        catch
        {
            s.Dispose();
            throw;
        }
    }

    static void AssertNoMarker(RemoteSetup s)
    {
        foreach (var dir in new[] { s.Local.Path, s.Bare.Path, s.Other.Path })
            Assert.False(File.Exists(Path.Combine(dir, "PWNED")), $"PWNED created in {dir}");
    }

    [Fact]
    public async Task Pull_of_a_remote_branch_named_like_an_option()
    {
        using var s = Setup();
        const string name = Payload;
        var repo = await s.OpenAsync();

        var job = new Job("Pull");
        var r = await repo.PullAsync(job, "origin", name, rebase: false, autostash: false, tags: false);

        Assert.True(r.Success, r.ReadableOutput);
        AssertNoMarker(s);
        Assert.Contains($"$ git pull --rebase=false origin refs/heads/{name} --no-recurse-submodules\n", job.Output);
    }

    [Fact]
    public async Task Fast_forward_pull_of_a_remote_branch_named_like_an_option()
    {
        using var s = Setup();
        const string name = Payload;
        s.Local.Git("branch", "dev");
        var repo = await s.OpenAsync();

        var other = new Job("Fast forward");
        var r = await repo.FastForwardPullAsync(other, "dev", "origin", name, active: false);
        Assert.True(r.Success, r.ReadableOutput);
        Assert.Contains($"$ git fetch -- origin refs/heads/{name}:refs/heads/dev\n", other.Output);

        var active = new Job("Fast forward");
        r = await repo.FastForwardPullAsync(active, "main", "origin", name, active: true);
        Assert.True(r.Success, r.ReadableOutput);
        Assert.Contains($"$ git pull --ff-only origin refs/heads/{name} --no-recurse-submodules\n", active.Output);
        AssertNoMarker(s);
    }
}

/// <summary>Tags named like options, as a fetch from a remote can bring them, through the ref operations.</summary>
[Collection("RefOps")]
public sealed class TagNameInjectionTests
{
    [AvaloniaFact]
    public async Task Delete_and_merge_a_tag_named_like_an_option()
    {
        using var f = new RefFixture();
        f.Repo.Commit("init", 0, ("a.txt", "1\n"));
        f.Repo.Git("checkout", "-q", "-b", "b");
        var b = f.Repo.Commit("b1", 1, ("a.txt", "2\n"));
        f.Repo.Git("checkout", "-q", "main");
        var marker = Path.Combine(f.Repo.Path, "PWNED");
        const string name = RemoteNameInjectionTests.Payload;
        f.Repo.Git("update-ref", "refs/tags/" + name, b);
        f.Repo.Git("update-ref", "refs/tags/-l", b);
        await f.OpenAsync();

        await f.Ops.MergeAsync(f.Ref("refs/tags/" + name));
        Assert.Empty(f.Errors);
        Assert.Contains("refs/tags/" + name, f.LastJob.Output);
        Assert.Equal(b, f.Repo.Git("rev-parse", "HEAD^2"));

        await f.Ops.DeleteTagsAsync([f.Ref("refs/tags/" + name), f.Ref("refs/tags/-l")]);
        Assert.Empty(f.Errors);
        Assert.Contains("$ git tag --delete -- ", f.LastJob.Output);
        Assert.Equal("", f.Repo.Git("tag"));
        Assert.False(File.Exists(marker));
    }

    [AvaloniaFact]
    public async Task Local_branch_named_like_an_option_is_refused_by_checkout_but_can_be_renamed_and_deleted()
    {
        using var f = new RefFixture();
        var c = f.Repo.Commit("init", 0, ("a.txt", "1\n"));
        f.Repo.Git("update-ref", "refs/heads/--orphan=x", c);
        f.Repo.Git("update-ref", "refs/heads/-d", c);
        await f.OpenAsync();

        await f.Ops.CheckoutBranchAsync(f.Ref("refs/heads/--orphan=x"));
        Assert.Contains(f.Errors, e => e.Text.Contains("The branch name '--orphan=x' starts with '-'", StringComparison.Ordinal));
        Assert.Equal("main", f.Branch);
        Assert.DoesNotContain("$ git checkout", f.LastJob.Output);

        f.Errors.Clear();
        f.Fill = vm => { ((RenameBranchViewModel)vm).NewName = "fixed"; return true; };
        await f.Ops.RenameBranchAsync(f.Ref("refs/heads/--orphan=x"));
        Assert.Empty(f.Errors);
        Assert.Contains("$ git branch --move -- --orphan=x fixed", f.LastJob.Output);
        Assert.Equal(c, f.Repo.Git("rev-parse", "refs/heads/fixed"));

        f.Fill = _ => true;
        await f.Ops.DeleteBranchesAsync([f.Ref("refs/heads/-d")]);
        Assert.Empty(f.Errors);
        Assert.Equal("", f.Repo.Git("for-each-ref", "refs/heads/-d"));
    }
}
