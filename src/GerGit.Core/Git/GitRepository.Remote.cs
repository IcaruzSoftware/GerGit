using System.Text.RegularExpressions;

namespace GerGit.Core.Git;

/// <summary>
/// Fetch, pull, push, fast-forward, tracking and remotes (git commands, result parsing and job messages).
/// Every method is the work of one job: it runs its git steps into <paramref name="job"/>, sets the end message itself
/// where the operation defines one, and returns the failing result (or the last one) for the caller's error window.
/// The credential override ([CRED]) comes from GitRunner for every git process. Limitation: no LFS progress file.
/// </summary>
public sealed partial class GitRepository
{
    /// <summary>The remote of a remote-tracking ref ("refs/remotes/origin/main" → "origin"): the longest remote name that
    /// prefixes it (remote names may contain '/'), else the first path segment.</summary>
    public static string RemoteOfRef(string fullRef, IEnumerable<string> remotes)
    {
        var rest = fullRef.StartsWith("refs/remotes/", StringComparison.Ordinal) ? fullRef["refs/remotes/".Length..] : fullRef;
        return remotes.Where(r => rest.StartsWith(r + "/", StringComparison.Ordinal)).MaxBy(r => r.Length)
            ?? (rest.Contains('/') ? rest[..rest.IndexOf('/')] : rest);
    }

    /// <summary>Fetch one remote, or all (<paramref name="remote"/> null). <paramref name="force"/> = the error window's
    /// "Force Fetch" (`--force ... --tags`). Success message `Fetched N branch(es)` / "Already up to date".
    /// <paramref name="silent"/> = the automatic background fetch: no prompts (GERGIT_NO_PROMPT=1), and a
    /// failure because git could not prompt cancels the job quietly ("Cancel background fetch...").</summary>
    public async Task<GitResult> FetchAsync(Job job, string? remote, bool tags, bool force = false, bool silent = false)
    {
        List<string> args = ["fetch", "--prune"];
        if (force) args.Add("--force");
        args.Add(remote ?? "--all");
        if (tags || force) args.Add("--tags");
        args.Add("--progress");
        job.SetMessage("Fetching...");
        var r = await GitRunner.RunJobAsync(job, Info.WorkTree, args, silent: silent, header: false); // fetch and push show only git's output
        if (!r.Success)
        {
            // git exits by itself on Linux, so the cancel follows the failure
            // (no 100 ms timer); a cancel requested here makes the job end as canceled, never as an error.
            if (silent && NoPromptRegex().IsMatch(r.Stderr))
            {
                job.AppendOutput("Cancel background fetch...\n");
                job.Cancel();
            }
            return r;
        }
        int n = CountFetched(r.Stderr);
        if (n == 0)
        {
            job.AppendOutput("Already up to date\n");
            job.Succeed("Already up to date");
        }
        else job.Succeed(n == 1 ? "Fetched 1 branch" : $"Fetched {n} branches");
        return r;
    }

    /// <summary>Result parsing: ` &lt;flag&gt; &lt;summary&gt; &lt;ref&gt; -> &lt;ref&gt;` lines whose summary is not "[up to date]".</summary>
    internal static int CountFetched(string stderr) =>
        stderr.Split('\n').Count(l => FetchLineRegex().Match(l) is { Success: true } m && !m.Groups[1].Value.StartsWith("[up to date]", StringComparison.Ordinal));

    /// <summary>GerGit's pulls keep git from touching submodules: with `submodule.recurse=true` git's pull would run
    /// `submodule update`, which clones every submodule a `submodule.active` pathspec marks as active, also new ones from
    /// the upstream. GerGit's own filtered update runs afterwards (<see cref="UpdateSubmodulesAsync"/>).</summary>
    const string NoRecurseSubmodules = "--no-recurse-submodules";

    /// <summary>
    /// Pull: stash local changes when asked, pull, reapply the stash, update submodules. <paramref name="branch"/> = short name in the remote, or null to pull the active branch's
    /// configured upstream (then `--prune`). <paramref name="noFastForward"/> adds `--no-ff` to a merge pull. Messages: "Stashing...", "Pulling...", "Applying stash...", end "Everything is
    /// up to date" or "Pull failed" / "Apply stash failed" / "Update submodules failed". A failed pull keeps the stash.
    /// </summary>
    public async Task<GitResult> PullAsync(Job job, string remote, string? branch, bool rebase, bool autostash, bool tags,
        bool noFastForward = false)
    {
        var subs = await CaptureSubmodulesAsync();
        string? stashed = null;
        var stashMessage = "Pull autostash " + StashTimestamp();
        if (autostash && IsDirty(await ReadStatusAsync(lockFree: true)))
        {
            job.SetMessage("Stashing...");
            (var s, stashed) = await StashPushAsync(job, stashMessage);
            if (!s.Success) return s;
        }

        // The `/dev/tty` / "terminal prompts disabled" self-cancel belongs to the silent
        // background fetch only (FetchAsync); a pull is never silent, so on Linux it is a normal failure.
        // The full ref name: a remote branch named like an option ("--upload-pack=...") must never become one. `--`
        // does not help here, pull hands its arguments on to fetch without it.
        List<string> args = ["pull", rebase ? "--rebase=true" : "--rebase=false", .. (noFastForward ? new[] { "--no-ff" } : []),
            remote, branch is null ? "--prune" : "refs/heads/" + branch];
        if (tags) args.Add("--tags");
        args.AddRange([NoRecurseSubmodules, "--progress"]);
        job.SetMessage("Pulling...");
        var pull = await GitRunner.RunJobAsync(job, Info.WorkTree, args);
        if (!pull.Success && !pull.Cancelled)
        {
            await UpdateSubmodulesAsync(job, subs);
            job.Fail(PullFailure(pull));
            return pull;
        }

        GitResult? failed = null;
        string failure = "";
        if (stashed is not null) // also after a cancelled pull, unless it left a rebase or merge in progress
        {
            job.SetMessage("Applying stash...");
            if (await PopAutostashAsync(job, stashed, stashMessage) is { Success: false } a) (failed, failure) = (a, "Apply stash failed");
        }
        if (await UpdateSubmodulesAsync(job, subs) is { Success: false } sub && failed is null)
            (failed, failure) = (sub, "Update submodules failed");
        if (failed is not null)
        {
            job.Fail(failure);
            return failed;
        }
        job.Succeed("Everything is up to date");
        return pull;
    }

    /// <summary>"Could not resolve host '&lt;h&gt;'" or "Pull failed".</summary>
    static string PullFailure(GitResult r) =>
        GitErrors.Classify(r) is { Kind: GitErrorKind.CouldNotResolveHost } e ? e.ShortMessage : "Pull failed";

    /// <summary>
    /// Push one branch. <paramref name="refspec"/> = `refs/heads/&lt;local&gt;[:&lt;dst&gt;]`; a bare
    /// source goes to the branch's upstream through `-c push.default=upstream`. Messages: `'&lt;src&gt;' rejected`,
    /// "Push failed", "Everything is up to date", `Updated '&lt;name&gt;'`.
    /// <paramref name="checks"/> (a pre-push hook exists; Quick push): git runs like the commit
    /// with checks (pseudo-terminal, PRE_COMMIT_COLOR=never, the hooks' SIGINT wait on cancel).
    /// </summary>
    public async Task<GitResult> PushAsync(Job job, string remote, string refspec, bool force, bool tags, bool setUpstream,
        bool checks = false)
    {
        List<string> args = ["-c", "push.default=upstream", "push", remote, refspec];
        if (force) args.Add("--force-with-lease");
        if (tags) args.Add("--tags");
        if (setUpstream) args.Add("--set-upstream");
        args.AddRange(["--verbose", "--progress"]);
        job.SetMessage("Pushing...");
        var r = checks
            ? await GitRunner.RunJobAsync(job, Info.WorkTree, args, header: false,
                env: new Dictionary<string, string> { ["PRE_COMMIT_COLOR"] = "never" }, tty: true, hooksWait: GitRunner.HooksWait)
            : await GitRunner.RunJobAsync(job, Info.WorkTree, args, header: false);
        var text = r.Stdout + "\n" + r.Stderr;
        if (r.Success)
        {
            if (text.Contains("Everything up-to-date", StringComparison.Ordinal)) job.Succeed("Everything is up to date");
            else if (TrackingRefRegex().Match(text) is { Success: true } m) job.Succeed($"Updated '{m.Groups[1].Value}'");
        }
        else if (!r.Cancelled)
        {
            var e = GitErrors.Classify(r);
            job.Fail(RejectedRegex().Match(text) is { Success: true } m ? $"'{m.Groups[1].Value}' rejected"
                : e.Kind == GitErrorKind.CouldNotResolveHost ? e.ShortMessage : "Push failed");
        }
        return r;
    }

    /// <summary>Push several branches (`--set-upstream --atomic`, <paramref name="branches"/> true) or tags: full ref names.
    /// Failure message = git's text (the caller's default), success "Everything is up to date".</summary>
    public async Task<GitResult> PushRefsAsync(Job job, string remote, IReadOnlyList<string> refs, bool branches)
    {
        List<string> args = ["push", remote, .. refs];
        if (branches) args.AddRange(["--set-upstream", "--atomic"]);
        args.AddRange(["--verbose", "--progress"]);
        job.SetMessage("Pushing...");
        var r = await GitRunner.RunJobAsync(job, Info.WorkTree, args, header: false);
        if (r.Success) job.Succeed("Everything is up to date");
        return r;
    }

    /// <summary>
    /// Fast-forward (local, no network): `merge --ff-only` for the active branch, else `update-ref`.
    /// A non-active branch whose tip is not an ancestor of the upstream is refused, so no
    /// local commits are dropped. Messages "Everything is up to date" / "Fast forward failed" / "Update submodules failed".
    /// </summary>
    public async Task<GitResult> FastForwardAsync(Job job, string branch, string upstream, bool active)
    {
        GitResult r;
        if (active)
        {
            var subs = await CaptureSubmodulesAsync();
            r = await GitRunner.RunJobAsync(job, Info.WorkTree, ["merge", "--ff-only", upstream]);
            if (r.Success && await UpdateSubmodulesAsync(job, subs) is { Success: false } sub)
            {
                job.Fail("Update submodules failed");
                return sub;
            }
        }
        else
        {
            var local = "refs/heads/" + branch;
            var shortUp = upstream.StartsWith("refs/remotes/", StringComparison.Ordinal) ? upstream["refs/remotes/".Length..] : upstream;
            // update-ref would silently move a branch checked out in another worktree (the menu item is disabled then).
            var wt = await GitRunner.RunAsync(Info.WorkTree, ["worktree", "list", "--porcelain", "-z"], ct: job.Token);
            if (wt.Cancelled) return wt;
            if (ParseWorktrees(wt.Stdout, Info.WorkTree).FirstOrDefault(w => !w.IsCurrent && w.Branch == local) is { } other)
            {
                job.Fail("Fast forward failed");
                return new GitResult(1, "", $"'{branch}' is checked out in another worktree: {other.Path}");
            }
            // Both tips resolved once: what is checked is what is written (a forced fetch in between changes nothing).
            var tip = await GitRunner.RunAsync(Info.WorkTree, ["rev-parse", "--verify", "-q", local + "^{commit}"], ct: job.Token);
            var up = await GitRunner.RunAsync(Info.WorkTree, ["rev-parse", "--verify", "-q", upstream + "^{commit}"], ct: job.Token);
            if (tip.Cancelled || up.Cancelled) return tip.Cancelled ? tip : up;
            if (!tip.Success || !up.Success)
            {
                job.Fail("Fast forward failed");
                return new GitResult(1, "", !tip.Success ? $"Branch '{branch}' doesn't exist." : $"Remote branch '{shortUp}' doesn't exist.");
            }
            string sha = tip.Stdout.Trim(), upSha = up.Stdout.Trim();
            var check = await GitRunner.RunAsync(Info.WorkTree, ["merge-base", "--is-ancestor", sha, upSha], ct: job.Token);
            if (check.Cancelled) return check;
            if (!check.Success)
            {
                job.Fail("Fast forward failed");
                return new GitResult(1, "", check.ExitCode == 1
                    ? $"Cannot fast-forward '{branch}': it has commits that '{shortUp}' does not have."
                    : check.ReadableOutput);
            }
            // The checked tip as <oldvalue>: a branch moved meanwhile makes update-ref fail instead of going backwards.
            r = await GitRunner.RunJobAsync(job, Info.WorkTree, ["update-ref", local, upSha, sha]);
        }
        if (!r.Success) job.Fail("Fast forward failed");
        else job.Succeed("Everything is up to date");
        return r;
    }

    /// <summary>Fast-forward pull (network): `pull --ff-only` (active) or `fetch &lt;remote&gt; &lt;name&gt;:&lt;local&gt;`
    /// (git refuses non-fast-forward updates). Submodules are updated afterwards in every case. The remote branch goes
    /// in as a full ref name, so a name like "--upload-pack=..." is never read as an option.</summary>
    public async Task<GitResult> FastForwardPullAsync(Job job, string branch, string remote, string nameInRemote, bool active)
    {
        var subs = await CaptureSubmodulesAsync();
        job.SetMessage("Pulling...");
        var r = await GitRunner.RunJobAsync(job, Info.WorkTree, active
            ? ["pull", "--ff-only", remote, "refs/heads/" + nameInRemote, NoRecurseSubmodules, "--progress"]
            : ["fetch", "--progress", "--", remote, $"refs/heads/{nameInRemote}:refs/heads/{branch}"]);
        var sub = await UpdateSubmodulesAsync(job, subs);
        if (!r.Success)
        {
            job.Fail(PullFailure(r));
            return r;
        }
        if (sub is { Success: false })
        {
            job.Fail("Update submodules failed");
            return sub;
        }
        job.Succeed("Everything is up to date");
        return r;
    }

    /// <summary>`branch --set-upstream-to &lt;upstream&gt; &lt;branch&gt;`, or `--unset-upstream` when <paramref name="upstream"/> is null.</summary>
    public Task<GitResult> SetUpstreamAsync(Job job, string branch, string? upstream) =>
        GitRunner.RunJobAsync(job, Info.WorkTree, upstream is null
            ? ["branch", "--unset-upstream", "--", branch]
            : ["branch", "--set-upstream-to", upstream, "--", branch]);

    public Task<GitResult> AddRemoteAsync(Job job, string name, string url) =>
        GitRunner.RunJobAsync(job, Info.WorkTree, ["remote", "add", name, url]);

    /// <summary>OK, Edit: set-url when the URL changed, then rename when the name changed; stops at the first failure.</summary>
    public async Task<GitResult?> EditRemoteAsync(Job job, string oldName, string oldUrl, string newName, string newUrl)
    {
        GitResult? r = null;
        if (newUrl != oldUrl)
        {
            r = await GitRunner.RunJobAsync(job, Info.WorkTree, ["remote", "set-url", oldName, newUrl]);
            if (!r.Success) return r;
        }
        if (newName != oldName) r = await GitRunner.RunJobAsync(job, Info.WorkTree, ["remote", "rename", oldName, newName]);
        return r;
    }

    public Task<GitResult> RemoveRemoteAsync(Job job, string name) =>
        GitRunner.RunJobAsync(job, Info.WorkTree, ["remote", "remove", name]);

    /// <summary>"Fetch '&lt;remote&gt;' Automatically": checked = `remote.&lt;name&gt;.disableimplicitfetch false`.</summary>
    public Task<GitResult> SetFetchAutomaticallyAsync(Job job, string remote, bool enabled) =>
        GitRunner.RunJobAsync(job, Info.WorkTree, ["config", $"remote.{remote}.disableimplicitfetch", enabled ? "false" : "true"]);

    /// <summary>False only when `remote.&lt;name&gt;.disableimplicitfetch` is true.</summary>
    public async Task<bool> IsFetchAutomaticallyAsync(string remote, CancellationToken ct = default)
    {
        var r = await GitRunner.RunAsync(Info.WorkTree, ["config", "--type=bool", "--get", $"remote.{remote}.disableimplicitfetch"], ct: ct);
        return !(r.Success && r.Stdout.Trim() == "true");
    }

    /// <summary>Git wanted to prompt but could not (Credentials; Windows text, then the Linux ones).</summary>
    [GeneratedRegex(@"/dev/tty: No such device or address|terminal prompts disabled|could not read Username")]
    private static partial Regex NoPromptRegex();

    [GeneratedRegex(@"^ [ =*+\-] (\[[^\]]*\]|\S+)\s+\S+\s+->\s+\S+")]
    private static partial Regex FetchLineRegex();

    [GeneratedRegex(@"^\s*!\s+\[rejected\]\s+(\S+)\s+->\s+\S+", RegexOptions.Multiline)]
    private static partial Regex RejectedRegex();

    [GeneratedRegex(@"updating local tracking ref 'refs/remotes/([^']+)'")]
    private static partial Regex TrackingRefRegex();
}
