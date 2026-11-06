namespace GerGit.Core.Git;

/// <summary>Stash, cherry-pick and revert commands.</summary>
public sealed partial class GitRepository
{
    /// <summary>Stage every untracked file first (the "Stage new files" option of Save Stash / Snapshot).</summary>
    public async Task StageUntrackedAsync(Job job)
    {
        var paths = (await ReadStatusAsync(ct: job.Token)).Unstaged
            .Where(e => e.Kind == ChangeKind.Untracked).Select(e => e.Path).ToList();
        if (paths.Count == 0) return;
        job.SetMessage($"Staging {paths.Count} files...");
        await StageAsync(paths, job.Token);
    }

    /// <summary>`git stash push [--message m] [paths]` (`push`). Blank message = none.
    /// "No local changes to save" is exit 0: nothing is created. Paths go through stdin (no command-line length limit).</summary>
    public Task<GitResult> SaveStashAsync(Job job, string? message, IReadOnlyList<string>? paths = null)
    {
        List<string> args = ["--literal-pathspecs", "stash", "push"];
        if (!string.IsNullOrWhiteSpace(message)) args.AddRange(["--message", message]);
        if (paths is not { Count: > 0 }) return GitRunner.RunJobAsync(job, Info.WorkTree, args);
        return GitRunner.RunJobAsync(job, Info.WorkTree, [.. args, .. PathspecFromStdin], PathspecInput(paths));
    }

    static readonly string[] PathspecFromStdin = ["--pathspec-from-file=-", "--pathspec-file-nul"];

    static string PathspecInput(IEnumerable<string> paths) => string.Join('\0', paths);

    /// <summary>Save Snapshot: `stash create`, then `stash store` of the printed sha; the working tree stays.
    /// No changes (empty output) = nothing stored, a hint line in the job output, success.</summary>
    public async Task<GitResult> SaveSnapshotAsync(Job job, string message)
    {
        var r = await GitRunner.RunJobAsync(job, Info.WorkTree, ["stash", "create", message]);
        var sha = r.Stdout.Trim();
        if (!r.Success) return r;
        if (sha.Length == 0)
        {
            job.AppendOutput($"{GitErrors.HintPrefix} No local changes to save\n");
            return r;
        }
        // --message: a plain `stash store`, which leaves "Created via ..." in `git stash list`.
        return await GitRunner.RunJobAsync(job, Info.WorkTree, ["stash", "store", "--message", message, sha]);
    }

    /// <summary>Stash Files: add -f the untracked ones, reset staged deletions and old paths of renames, then
    /// `stash push` of the paths. <paramref name="entries"/> = the status rows (staged and unstaged) of the checked paths.</summary>
    public async Task<GitResult> StashFilesAsync(Job job, string? message, IReadOnlyList<StatusEntry> entries)
    {
        var untracked = entries.Where(e => e.Kind == ChangeKind.Untracked).Select(e => e.Path).Distinct().ToList();
        if (untracked.Count > 0)
        {
            var r = await GitRunner.RunJobAsync(job, Info.WorkTree, ["--literal-pathspecs", "add", "-f", .. PathspecFromStdin], PathspecInput(untracked));
            if (!r.Success) return r;
        }
        // Only staged deletions: an unstaged one is still in the index, and resetting it would drop a staged version
        // ("MD": the staged content is lost; "AD": the path leaves the index and the push fails).
        var reset = entries.Where(e => e.Staged && e.Kind == ChangeKind.Deleted).Select(e => e.Path)
            .Concat(entries.Where(e => e.Kind == ChangeKind.Renamed && e.OldPath is not null).Select(e => e.OldPath!))
            .Distinct().ToList();
        if (reset.Count > 0)
        {
            var r = await GitRunner.RunJobAsync(job, Info.WorkTree, ["--literal-pathspecs", "reset", "HEAD", .. PathspecFromStdin], PathspecInput(reset));
            if (!r.Success) return r;
        }
        var paths = entries.Select(e => e.Path)
            .Concat(entries.Where(e => e.Kind == ChangeKind.Renamed && e.OldPath is not null).Select(e => e.OldPath!))
            .Distinct().ToList();
        return await SaveStashAsync(job, message, paths);
    }

    /// <summary>Rename Stash: a copy of the stash commit with the new message is stored, the old entry dropped.
    /// Returns the last result and the new stash commit (null on failure). Nothing happens when <paramref name="stash"/>
    /// is no longer at its index (<see cref="StashListChanged"/>).</summary>
    public async Task<(GitResult Result, string? NewSha)> RenameStashAsync(Job job, StashEntry stash, string message)
    {
        const string sep = "\x1f";
        var reflogName = stash.Name;
        var show = await GitRunner.RunJobAsync(job, Info.WorkTree, ["show", "--no-patch", "--no-show-signature", "--pretty=format:%H%x1f%T%x1f%P", reflogName]);
        var f = show.Stdout.Trim().Split(sep);
        if (!show.Success || f.Length != 3) return (show, null);
        var (oldSha, tree) = (f[0], f[1]);
        if (oldSha != stash.Sha) return (new GitResult(1, "", StashListChanged), null);

        List<string> args = ["commit-tree", tree];
        foreach (var p in f[2].Split(' ', StringSplitOptions.RemoveEmptyEntries)) args.AddRange(["-p", p]);
        args.AddRange(["-m", message]);
        var commit = await GitRunner.RunJobAsync(job, Info.WorkTree, args);
        var newSha = commit.Stdout.Trim();
        if (!commit.Success || newSha.Length == 0) return (commit, null);

        var store = await GitRunner.RunJobAsync(job, Info.WorkTree, ["stash", "store", "--message", message, newSha]);
        if (!store.Success) return (store, null);

        // The store pushed the old entry one down: drop exactly stash@{n+1} (another entry may have the same sha).
        var oldName = $"stash@{{{StashIndex(reflogName) + 1}}}";
        var reflog = await GitRunner.RunJobAsync(job, Info.WorkTree, ["reflog", "--format=%H%x1f%gD", "stash"]);
        if (!reflog.Success) return (reflog, newSha);
        var old = reflog.Stdout.Split('\n').Select(l => l.Split(sep)).FirstOrDefault(l => l.Length == 2 && l[0] == oldSha && l[1] == oldName);
        if (old is null) return (reflog, newSha);
        return (await GitRunner.RunJobAsync(job, Info.WorkTree, ["stash", "drop", old[1]]), newSha);
    }

    /// <summary>Delete Stash(es): highest index first so the others keep their index; stops at the first failure,
    /// also before a stash that is no longer at its index (<see cref="StashListChanged"/>).</summary>
    public async Task<GitResult?> DropStashesAsync(Job job, IReadOnlyList<StashEntry> stashes)
    {
        GitResult? last = null;
        foreach (var stash in stashes.OrderByDescending(s => StashIndex(s.Name)))
        {
            var stashRef = "refs/" + stash.Name;
            last = await CheckStashAsync(stashRef, stash.Sha)
                ?? await GitRunner.RunJobAsync(job, Info.WorkTree, ["stash", "drop", stashRef]);
            if (!last.Success) break;
        }
        return last;
    }

    /// <summary>n of "stash@{n}".</summary>
    static int StashIndex(string reflogName) =>
        int.TryParse(reflogName.AsSpan(reflogName.IndexOf('{') + 1).TrimEnd('}'), out var n) ? n : 0;

    /// <summary>
    /// Cherry-pick: <paramref name="shas"/> oldest first; <paramref name="mainline"/> = 1-based parent of a merge
    /// commit (-m N). Empty picks are skipped with `--skip` while git reports them; then the submodule update.
    /// A conflict is a failed result whose stdout has git's CONFLICT line (the "Merge Conflict" window).
    /// </summary>
    public Task<GitResult> CherryPickAsync(Job job, IReadOnlyList<string> shas, int? mainline, bool commit, bool recordOrigin) =>
        WithSubmoduleUpdateAsync(job, async () =>
        {
            List<string> args = ["cherry-pick"];
            if (mainline is { } m) args.AddRange(["-m", m.ToString()]);
            if (!commit) args.Add("--no-commit");
            if (recordOrigin) args.Add("-x");
            var r = await GitRunner.RunJobAsync(job, Info.WorkTree, [.. args, .. shas]);
            while (!r.Success && !r.Cancelled && IsEmptyPick(r))
                r = await GitRunner.RunJobAsync(job, Info.WorkTree, ["cherry-pick", "--skip"]);
            return r;
        });

    static bool IsEmptyPick(GitResult r) =>
        r.ReadableOutput.Contains("nothing to commit, working tree clean", StringComparison.Ordinal) ||
        r.ReadableOutput.Contains("The previous cherry-pick is now empty, possibly due to conflict resolution.", StringComparison.Ordinal);

    /// <summary>Revert: `revert [-m N] --no-edit|--no-commit sha`, then the submodule update.</summary>
    public Task<GitResult> RevertAsync(Job job, string sha, int? mainline, bool commit) =>
        WithSubmoduleUpdateAsync(job, () =>
        {
            List<string> args = ["revert"];
            if (mainline is { } m) args.AddRange(["-m", m.ToString()]);
            args.AddRange([commit ? "--no-edit" : "--no-commit", sha]);
            return GitRunner.RunJobAsync(job, Info.WorkTree, args);
        });

    /// <summary>Submodule refresh after switching commits: its failure replaces an otherwise successful result.</summary>
    async Task<GitResult> WithSubmoduleUpdateAsync(Job job, Func<Task<GitResult>> operation)
    {
        var before = await CaptureSubmodulesAsync(job.Token);
        var r = await operation();
        var sub = await UpdateSubmodulesAsync(job, before);
        return r.Success && sub is { Success: false } ? sub : r;
    }

    /// <summary>Cherry-pick conflict preview: merge-tree per commit, oldest first, chained through the printed
    /// tree. True = clean, false = conflicts (exit 1), null = could not tell.</summary>
    public async Task<bool?> PreviewCherryPickAsync(IReadOnlyList<string> shas, int parent, CancellationToken ct = default)
    {
        var current = "HEAD";
        for (int i = 0; i < shas.Count; i++)
        {
            var r = await GitRunner.RunAsync(Info.WorkTree,
                ["merge-tree", "--write-tree", $"--merge-base={shas[i]}^{(i == 0 ? parent : 1)}", current, shas[i]], ct: ct);
            if (r.ExitCode == 1 && !r.Cancelled) return false;
            var tree = r.Stdout.Split('\n')[0].Trim();
            if (!r.Success || tree.Length == 0) return null;
            current = tree;
        }
        return true;
    }

    /// <summary>Revert conflict preview: `merge-tree --write-tree --merge-base=sha HEAD sha^n`.</summary>
    public async Task<bool?> PreviewRevertAsync(string sha, int parent, CancellationToken ct = default)
    {
        var r = await GitRunner.RunAsync(Info.WorkTree, ["merge-tree", "--write-tree", $"--merge-base={sha}", "HEAD", $"{sha}^{parent}"], ct: ct);
        return r.Cancelled ? null : r.ExitCode switch { 0 => true, 1 => false, _ => null };
    }
}
