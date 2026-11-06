namespace GerGit.Core.Git;

public sealed partial class GitRepository
{
    /// <summary>
    /// Git commands Clone: `git clone [--recurse-submodules] --progress --verbose -- "&lt;url&gt;" "&lt;destination&gt;"`
    /// (order chosen so `--` can end the options)
    /// as the work of <paramref name="job"/> (progress from stderr; the credential override comes from GitRunner).
    /// git asking for a password without a terminal (`bash: /dev/tty: ...`) cancels the job. A clone that opens with an
    /// interrupted rebase counts as failed. A non-empty destination is left to git (no own check).
    /// </summary>
    public static async Task<GitResult> CloneAsync(Job job, string url, string destination, bool recurseSubmodules)
    {
        List<string> args = ["clone"];
        if (recurseSubmodules) args.Add("--recurse-submodules");
        // Options before `--`: a URL or destination starting with `-` is never read as an option (e.g. --upload-pack=).
        args.AddRange(["--progress", "--verbose", "--", url, destination]);
        job.SetMessage("Cloning...");
        var r = await GitRunner.RunJobAsync(job, null, args);
        // Limitation: checked after git ended, not live; git fails at once without a terminal, so the outcome is the same.
        if (r.Stderr.Contains("bash: /dev/tty: No such device or address", StringComparison.Ordinal))
        {
            job.Cancel();
            return r with { Cancelled = true };
        }
        if (!r.Success) return r;
        var gitDir = (await OpenAsync(destination)).Info.GitDir;
        if (Directory.Exists(Path.Combine(gitDir, "rebase-merge")) || Directory.Exists(Path.Combine(gitDir, "rebase-apply")))
            return new GitResult(1, "", $"error: Cloning into '{destination}' failed");
        return r;
    }
}
