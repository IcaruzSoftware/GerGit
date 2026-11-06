using GerGit.Core.Rebase;

namespace GerGit.Core.Git;

/// <summary>Live conflict prediction of the Rebase dialog.</summary>
public enum RebasePrediction { Unknown, NoConflicts, Conflicts }

/// <summary>What the Rebase dialog shows on open: the prediction and the "other local branches in range" (short names).</summary>
public sealed record RebasePreview(RebasePrediction Prediction, IReadOnlyList<string> BranchesInRange);

/// <summary>Simple rebase, rebase continue and the quick-action targets.</summary>
public sealed partial class GitRepository
{
    /// <summary>
    /// Simple rebase prediction and "Other local branches in range" for rebasing <paramref name="sourceRef"/>
    /// (full ref) onto <paramref name="target"/> (full ref or SHA). Read-only. Throws GitException with the dialog's
    /// closing texts when the target has no commit or the merge base cannot be computed.
    /// A merge-base exit 1 without output (no common ancestor) counts as "unknown", not as that error.
    /// </summary>
    public async Task<RebasePreview> PreviewRebaseAsync(string sourceRef, string target, CancellationToken ct = default)
    {
        var tip = await CommitShaAsync(sourceRef, ct);
        var dest = await CommitShaAsync(target, ct) ?? throw new GitException("Cannot get destination sha for rebase");
        var mb = await GitRunner.RunAsync(Info.WorkTree, ["merge-base", sourceRef, target], ct: ct);
        ct.ThrowIfCancellationRequested();
        if (!mb.Success && mb.ReadableOutput.Trim().Length > 0)
            throw new GitException($"Cannot get merge base: '{tip}...{dest}':\n{mb.ReadableOutput.Trim()}");
        var mergeBase = mb.Success ? mb.Stdout.Trim() : null;

        List<string> args = ["for-each-ref", "--format=%(refname)", "--merged", sourceRef];
        if (mergeBase is not null) args.AddRange(["--no-merged", mergeBase]);
        args.Add("refs/heads/");
        var branches = (await Run(args, ct)).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(r => r != sourceRef)
            .Select(r => r["refs/heads/".Length..])
            .ToList();

        var prediction = RebasePrediction.Unknown;
        if (mergeBase is not null && tip is not null)
        {
            if (mergeBase == tip) prediction = RebasePrediction.NoConflicts; // nothing to replay
            else
            {
                // Needs git >= 2.44 (`git replay`); older git fails with stderr text = unknown.
                var r = await GitRunner.RunAsync(Info.WorkTree, ["replay", "--onto", target, $"{mergeBase}..{tip}"], ct: ct);
                prediction = r.Success ? RebasePrediction.NoConflicts
                    : !r.Cancelled && r.Stderr.Trim().Length == 0 ? RebasePrediction.Conflicts
                    : RebasePrediction.Unknown;
                if (prediction == RebasePrediction.Unknown) System.Diagnostics.Trace.WriteLine($"Rebase prediction failed: {r.ReadableOutput}");
            }
        }
        return new RebasePreview(prediction, branches);
    }

    async Task<string?> CommitShaAsync(string rev, CancellationToken ct)
    {
        var r = await GitRunner.RunAsync(Info.WorkTree, ["rev-parse", "--verify", "-q", rev + "^{commit}"], ct: ct);
        ct.ThrowIfCancellationRequested();
        return r.Success && r.Stdout.Trim().Length > 0 ? r.Stdout.Trim() : null;
    }

    /// <summary>
    /// Simple rebase execution, the body of the job: autostash (only when dirty) → checkout of
    /// <paramref name="branch"/> if needed → `rebase [--update-refs] &lt;target&gt;` → pop (not after a failure; the stash
    /// stays with a hint line; a cancelled checkout or rebase pops it unless a rebase is left in progress) → submodule update. Returns the first error in this order (stash error, then
    /// submodule error), else the rebase result. A conflict is a failed result with `CONFLICT (` in stdout.
    /// <paramref name="step"/> gets the footer texts.
    /// </summary>
    public async Task<GitResult> RebaseAsync(Job job, string branch, string target, bool updateRefs, bool autostash,
        Action<string>? step = null)
    {
        var submodules = await CaptureSubmodulesAsync();
        var message = $"Rebase autostash {StashTimestamp()}";
        string? stashed = null;
        if (autostash && IsDirty(await ReadStatusAsync(lockFree: true)))
        {
            step?.Invoke("Stashing...");
            (var s, stashed) = await StashPushAsync(job, message);
            if (!s.Success) return s;
        }

        var head = await GitRunner.RunAsync(Info.WorkTree, ["symbolic-ref", "-q", "HEAD"]);
        if (head.Stdout.Trim() != "refs/heads/" + branch)
        {
            step?.Invoke("Checkout...");
            var checkout = RefuseOptionLikeBranch(branch) ?? await GitRunner.RunJobAsync(job, Info.WorkTree, ["checkout", "--progress", branch]);
            if (!checkout.Success)
            {
                await KeepOrPopAsync(checkout);
                return checkout;
            }
        }

        step?.Invoke("Rebasing...");
        List<string> args = ["-c", "core.commentChar=^", "rebase"];
        if (updateRefs) args.Add("--update-refs");
        args.Add(target);
        var r = await GitRunner.RunJobAsync(job, Info.WorkTree, args);
        if (!r.Success)
        {
            await UpdateSubmodules();
            await KeepOrPopAsync(r);
            return r;
        }

        GitResult? pop = null;
        if (stashed is not null)
        {
            step?.Invoke("Applying stash...");
            pop = await PopAutostashAsync(job, stashed, message);
        }
        var sub = await UpdateSubmodules();
        return pop is { Success: false } ? pop : sub is { Success: false } ? sub : r;

        Task<GitResult?> UpdateSubmodules()
        {
            if (UpdateSubmodulesOnCheckout && submodules.Submodules.Count > 0 && !job.CancelRequested)
                step?.Invoke("Updating submodules...");
            return UpdateSubmodulesAsync(job, submodules);
        }

        // A git failure keeps the stash. A cancel is no failure: the changes come back, unless the cancel
        // left a rebase in progress (PopAutostashAsync keeps the stash then, with the hint line).
        async Task KeepOrPopAsync(GitResult failed)
        {
            if (stashed is null) return;
            if (!failed.Cancelled) AppendStashKept(job, message);
            else
            {
                step?.Invoke("Applying stash...");
                await PopAutostashAsync(job, stashed, message);
            }
        }
    }

    /// <summary>
    /// Continue for rebase: `rebase --skip` when `git diff-index HEAD --` is empty, else `--continue`, both
    /// with the editor helper so archived reword/squash messages still apply after a conflict. Then submodule update.
    /// Returns the rebase error, else a submodule error, else the rebase result.
    /// </summary>
    public async Task<GitResult> ContinueRebaseAsync(Job job)
    {
        var submodules = await CaptureSubmodulesAsync();
        var diff = await GitRunner.RunAsync(Info.WorkTree, ["diff-index", "HEAD", "--"]);
        var verb = diff.Success && diff.Stdout.Trim().Length == 0 ? "--skip" : "--continue";
        var editor = RebaseEditor.ConfigArgs(); // empty without the helper server: GIT_EDITOR=true keeps git's messages
        var r = await GitRunner.RunJobAsync(job, Info.WorkTree, ["-c", "core.commentChar=^", .. editor, "rebase", verb],
            editor: editor.Count > 0);
        var sub = await UpdateSubmodulesAsync(job, submodules);
        return !r.Success ? r : sub is { Success: false } ? sub : r;
    }

    /// <summary>
    /// Rebase target of a quick action (Quick actions table) for <paramref name="shasNewestFirst"/> (one commit,
    /// or several for squash/drop; the last is the oldest). Possible false = "nothing happens" (the needed parent is
    /// missing). Target null = `--root`.
    /// </summary>
    public async Task<(bool Possible, string? Target)> QuickActionTargetAsync(QuickAction action, IReadOnlyList<string> shasNewestFirst)
    {
        var oldest = shasNewestFirst[^1];
        var parent = await CommitShaAsync(oldest + "^", default);
        return action switch
        {
            QuickAction.Reword or QuickAction.Edit => (true, parent),
            QuickAction.Drop => (parent is not null, parent),
            _ => parent is null ? (false, null) : (true, await CommitShaAsync(oldest + "~2", default)),
        };
    }

    /// <summary>Commit menu: the range <paramref name="sha"/>..HEAD contains a merge commit.</summary>
    public async Task<bool> RangeHasMergeAsync(string sha, CancellationToken ct = default) =>
        (await Run(["rev-list", "--min-parents=2", "--max-count=1", $"{sha}..HEAD", "--"], ct)).Trim().Length > 0;
}
