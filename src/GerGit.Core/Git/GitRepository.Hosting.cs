namespace GerGit.Core.Git;

public sealed partial class GitRepository
{
    /// <summary>The branch `refs/remotes/&lt;remote&gt;/HEAD` points to, as its name in the remote ("main"); null when the
    /// remote HEAD is not known locally (default target of a new pull / merge request).</summary>
    public async Task<string?> ReadRemoteHeadAsync(string remote, CancellationToken ct = default)
    {
        var r = await GitRunner.RunAsync(Info.WorkTree, ["symbolic-ref", "-q", $"refs/remotes/{remote}/HEAD"], ct: ct);
        var prefix = $"refs/remotes/{remote}/";
        var target = r.Stdout.Trim();
        return r.Success && target.StartsWith(prefix, StringComparison.Ordinal) ? target[prefix.Length..] : null;
    }
}
