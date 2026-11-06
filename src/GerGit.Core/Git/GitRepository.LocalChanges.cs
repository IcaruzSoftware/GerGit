using System.Text;

namespace GerGit.Core.Git;

/// <summary>Local Changes: working diffs, file stage/unstage/discard, patch apply, commit.</summary>
public sealed partial class GitRepository
{
    /// <summary>Hunk and line staging, unstaging, discarding: diff commands of the Local Changes view.</summary>
    async Task<string> ReadWorkingDiffAsync(DiffRequest q, CancellationToken ct)
    {
        // --literal-pathspecs: a file named "[ab].txt" or "*.log" must not match other files.
        // Never index.lock (these run after every watcher refresh, a terminal `git commit` must not fail):
        // `git diff` refreshes and writes the index for stat-only changes even with --no-optional-locks (checked with
        // git 2.53); diff.autoRefreshIndex=false turns that off, and stat-only changes still show no diff.
        List<string> a = ["--no-optional-locks", "-c", "diff.autoRefreshIndex=false", "-c", "core.quotepath=false", "--literal-pathspecs", "--no-pager"];
        switch (q.Working)
        {
            case WorkingDiff.Unstaged:
                a.AddRange(["diff", .. DiffOptionsAndPaths(q)]);
                break;
            case WorkingDiff.Staged:
                a.AddRange(["diff", "--staged", .. DiffOptionsAndPaths(q)]);
                break;
            case WorkingDiff.Ours: // `git diff` of an unmerged path is a combined diff; --ours is a plain two-way one
                a.AddRange(["diff", "--ours", .. DiffOptionsAndPaths(q)]);
                break;
            case WorkingDiff.StagedAmend:
                a.AddRange(["diff-index", "--patch", await AmendBaseAsync(ct), "--cached", .. DiffOptionsAndPaths(q)]);
                break;
            case WorkingDiff.Untracked:
                var opts = DiffOptionsAndPaths(q with { OldPath = null });
                opts.Insert(opts.IndexOf("--") + 1, "/dev/null");
                a.AddRange(["diff", "--no-index", .. opts]);
                var r = await GitRunner.RunAsync(Info.WorkTree, a, ct: ct);
                // --no-index exits 1 when there are differences; a missing file also exits 1, but prints nothing on stdout.
                if (r.ExitCode == 1 && r.Stdout.Length > 0) r = r with { ExitCode = 0 };
                return Check(r, ct);
        }
        return await Run(a, ct);
    }

    /// <summary>Whether HEAD has a parent (false for a root commit or an unborn HEAD): the left side of an amend diff.</summary>
    public async Task<bool> HeadHasParentAsync(CancellationToken ct = default) =>
        (await GitRunner.RunAsync(Info.WorkTree, ["rev-parse", "--verify", "-q", "HEAD^"], ct: ct)).Success;

    /// <summary>HEAD's parent, or the empty tree when HEAD is a root commit (amend mode).</summary>
    async Task<string> AmendBaseAsync(CancellationToken ct)
    {
        var r = await GitRunner.RunAsync(Info.WorkTree, ["rev-parse", "--verify", "-q", "HEAD^"], ct: ct);
        ct.ThrowIfCancellationRequested();
        var sha = r.Stdout.Trim();
        return r.Success && sha.Length > 0 ? sha : await EmptyTreeAsync(ct);
    }

    /// <summary>The Staged list in amend mode (HEAD's parent → index).</summary>
    public async Task<IReadOnlyList<StatusEntry>> ReadAmendStagedAsync(CancellationToken ct = default)
    {
        var subsTask = SubmodulePathsAsync(ct);
        var output = await Run(["diff-index", "-z", "-M", "--cached", await AmendBaseAsync(ct), "--"], ct);
        var subs = await subsTask;

        // Raw records: ":<srcmode> <dstmode> <srcsha> <dstsha> <status>[score]" NUL path [NUL path2 for R/C].
        var entries = new List<StatusEntry>();
        var t = output.Split('\0');
        for (int i = 0; i + 1 < t.Length; i++)
        {
            var f = t[i].TrimStart(':').Split(' ');
            if (f.Length != 5 || f[4].Length == 0) continue;
            char status = f[4][0];
            string path = t[++i];
            string? old = null;
            if (status is 'R' or 'C')
            {
                if (i + 1 >= t.Length) break;
                (old, path) = (path, t[++i]);
            }
            if (status is not ('A' or 'D' or 'M' or 'T' or 'R' or 'C')) continue; // U (unmerged), X: no staged row
            // Source mode, not the destination mode: a restore record must pair the parent's blob with the
            // parent's mode (type change, exec bit).
            entries.Add(new StatusEntry(path, old, KindOf(status), true, subs.Contains(path), Mode: f[0], SourceBlob: f[2]));
        }
        return entries.OrderBy(e => e.Path, StringComparer.Ordinal).ThenBy(e => e.Kind).ToList();
    }

    /// <summary>Stage files. False = refused by the stage gate (an unresolved unmerged file among the
    /// paths): nothing is staged and the caller beeps.</summary>
    public async Task<bool> StageAsync(IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        // Not built yet: "Staging n/total..." progress.
        if (paths.Count == 0) return true;
        if (!await PassesStageGateAsync(paths, ct)) return false;
        Check(await RunLockedAsync(["--literal-pathspecs", "add", "--force", "--verbose", "--pathspec-from-file=-", "--pathspec-file-nul", "--"],
            string.Join('\0', paths), 3, ct), ct);
        return true;
    }

    /// <summary>Unstage files; with <paramref name="amend"/> the amend procedure (entries from ReadAmendStagedAsync).</summary>
    public async Task UnstageAsync(IReadOnlyList<StatusEntry> entries, bool amend, CancellationToken ct = default)
    {
        if (amend)
        {
            var records = new StringBuilder();
            foreach (var e in entries)
            {
                if (e.Mode is null || e.SourceBlob is null) continue;
                var zero = new string('0', e.SourceBlob.Length); // the all-zero id of this object format (SHA-1 or SHA-256)
                switch (e.Kind)
                {
                    // A fixed 100644 for renames and deletions would drop the exec bit; Mode is the source mode.
                    case ChangeKind.Renamed when e.OldPath is not null:
                        records.Append($"0 {zero}\t{e.Path}\0{e.Mode} {e.SourceBlob}\t{e.OldPath}\0");
                        break;
                    case ChangeKind.Added:
                        records.Append($"0 {zero}\t{e.Path}\0");
                        break;
                    default:
                        records.Append($"{e.Mode} {e.SourceBlob}\t{e.Path}\0");
                        break;
                }
            }
            if (records.Length > 0)
                Check(await RunLockedAsync(["update-index", "-z", "--index-info"], records.ToString(), 4, ct), ct);
            return;
        }

        var added = entries.Where(e => e.Kind == ChangeKind.Added).Select(e => e.Path).ToList();
        var others = entries.Where(e => e.Kind != ChangeKind.Added)
            .SelectMany(e => e.OldPath is null ? [e.Path] : new[] { e.Path, e.OldPath }).ToList();
        if (added.Count > 0)
            Check(await RunLockedAsync(["update-index", "--force-remove", "-z", "--stdin"],
                string.Concat(added.Select(p => p + "\0")), 4, ct), ct);
        if (others.Count > 0)
            Check(await RunLockedAsync(["--literal-pathspecs", "reset", "HEAD", "--pathspec-from-file=-", "--pathspec-file-nul", "--"],
                string.Join('\0', others), 3, ct), ct);
    }

    /// <summary>Discard files (Unstaged list): new files are deleted from disk, the rest is restored from the index.</summary>
    public async Task DiscardAsync(IReadOnlyList<StatusEntry> entries, CancellationToken ct = default)
    {
        // Not built yet: submodule entries are skipped; the per-submodule reset/clean/update discard.
        // Limitation: an untracked/ignored path ending in "/" is a nested repository (with -uall git lists nothing else
        // that way); discard never deletes it, like `git clean` without -ff.
        var files = entries.Where(e => !e.IsSubmodule && !IsNestedRepository(e)).ToList();
        // Unstaged Added = intent-to-add, which counts as new.
        foreach (var e in files.Where(e => e.Kind is ChangeKind.Untracked or ChangeKind.Ignored or ChangeKind.Added))
            DeleteFromDisk(Path.Combine(Info.WorkTree, e.Path).TrimEnd('/'));
        // An intent-to-add entry stays in the index after the delete (status " D"), so remove it too.
        var intentToAdd = files.Where(e => e.Kind == ChangeKind.Added).Select(e => e.Path + "\0").ToList();
        if (intentToAdd.Count > 0)
            Check(await RunLockedAsync(["update-index", "--force-remove", "-z", "--stdin"], string.Concat(intentToAdd), 4, ct), ct);

        var restore = files.Where(e => e.Kind is not (ChangeKind.Untracked or ChangeKind.Ignored or ChangeKind.Added))
            .Select(e => e.Path + "\0").ToList();
        if (restore.Count > 0)
            Check(await RunLockedAsync(["checkout-index", "--index", "--force", "--stdin", "-z"], string.Concat(restore), 3, ct), ct);
    }

    /// <summary>An untracked or ignored folder entry: a nested git repository that is not a submodule.</summary>
    public static bool IsNestedRepository(StatusEntry e) =>
        e.Kind is ChangeKind.Untracked or ChangeKind.Ignored && e.Path.EndsWith('/');

    /// <summary>Permanent delete (no trash). A symlink is removed itself, never its target. On a permission error the
    /// read-only flag is cleared (on Linux: the folders need write permission) and the delete is retried.</summary>
    void DeleteFromDisk(string full)
    {
        var dir = new DirectoryInfo(full);
        bool isDir = dir.Exists && dir.LinkTarget is null;
        try { Delete(); }
        catch (UnauthorizedAccessException)
        {
            // Never through a symlink (that would change files outside the work tree), never above the work tree.
            var parent = Directory.GetParent(full);
            if (parent is not null && parent.FullName.StartsWith(Info.WorkTree + "/", StringComparison.Ordinal))
                MakeWritable(parent);
            if (isDir)
                // Skipping reparse points (symlinks on Linux) keeps the walk from recursing into linked folders.
                foreach (var info in dir.EnumerateFileSystemInfos("*",
                             new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }).Prepend(dir))
                    MakeWritable(info);
            Delete();
        }

        static void MakeWritable(FileSystemInfo info)
        {
            if (info.LinkTarget is null) info.Attributes &= ~FileAttributes.ReadOnly;
        }

        void Delete()
        {
            if (isDir) Directory.Delete(full, recursive: true);
            else File.Delete(full); // a missing file is no error
        }
    }

    /// <summary>Hunk and line staging, unstaging, discarding: apply a patch from PatchBuilder.</summary>
    public async Task ApplyPatchAsync(string patch, PatchAction action, CancellationToken ct = default)
    {
        List<string> args = action switch
        {
            PatchAction.Stage => ["apply", "--cached", "--whitespace=nowarn", "--ignore-space-change", "-C1"],
            PatchAction.Unstage => ["apply", "--cached", "--whitespace=nowarn", "--ignore-space-change", "-C1", "--reverse"],
            _ => ["apply", "--whitespace=nowarn", "--ignore-whitespace"],
        };
        var r = await RunLockedAsync(args, patch, 4, ct);
        ct.ThrowIfCancellationRequested();
        if (r.Cancelled) throw new OperationCanceledException(ct);
        if (r.Success) return;
        // "patch does not apply".
        var hint = r.Stderr.StartsWith("error: patch failed", StringComparison.Ordinal) && r.Stderr.Contains("patch does not apply")
            ? "\ngergit: try to disable 'ignore whitespaces'" : "";
        throw new GitException(r.ReadableOutput + hint);
    }

    /// <summary>lock retry: rerun while the index is locked; waits 1000, 1500, 2000 ms before attempts 2, 3, 4.</summary>
    async Task<GitResult> RunLockedAsync(List<string> args, string stdin, int attempts, CancellationToken ct)
    {
        // Not built yet: a "Repository is locked. Retrying..." job-output line.
        for (int attempt = 1; ; attempt++)
        {
            var r = await GitRunner.RunAsync(Info.WorkTree, args, stdin, ct);
            if (attempt == attempts || r.Cancelled ||
                !(r.Stderr.StartsWith("fatal: Unable to create", StringComparison.Ordinal) && r.Stderr.Contains("index.lock': File exists.")))
                return r;
            await Task.Delay((attempt + 1) * 500, ct);
        }
    }

    /// <summary>HEAD's message, trimmed.</summary>
    public async Task<string> ReadHeadMessageAsync(CancellationToken ct = default) =>
        (await Run(["log", "--no-show-signature", "-1", "--pretty=%B", "--"], ct)).Trim();

    /// <summary>Message prepared by git: first non-empty of SQUASH_MSG, MERGE_MSG, rebase-apply/final-commit.</summary>
    public string? ReadPreparedMessage()
    {
        foreach (var name in (string[])["SQUASH_MSG", "MERGE_MSG", "rebase-apply/final-commit"])
        {
            var file = Path.Combine(Info.GitDir, name);
            if (File.Exists(file) && File.ReadAllText(file) is { Length: > 0 } text) return text;
        }
        return null;
    }

    /// <summary>Commit failed: does a pre-commit, commit-msg or post-commit hook exist?</summary>
    public Task<bool> HasCommitHooksAsync(CancellationToken ct = default) => HasHookAsync(ct, "pre-commit", "commit-msg", "post-commit");

    /// <summary>Does one of the hooks exist in the hooks folder (`core.hooksPath` when set, resolved relative to the work
    /// tree if not absolute, else `&lt;common git dir&gt;/hooks`)?</summary>
    public async Task<bool> HasHookAsync(CancellationToken ct, params string[] names)
    {
        var r = await GitRunner.RunAsync(Info.WorkTree, ["config", "--type=path", "core.hooksPath"], ct: ct);
        ct.ThrowIfCancellationRequested();
        var configured = r.Success ? r.Stdout.Trim() : "";
        // Path.Combine keeps an absolute hooksPath as is; a relative one is resolved against the work tree.
        var dir = configured.Length > 0 ? Path.Combine(Info.WorkTree, configured) : Path.Combine(Info.CommonDir, "hooks");
        return names.Any(h => File.Exists(Path.Combine(dir, h)));
    }

    /// <summary>Commit. The message goes through a file in the git dir; the caller decides how to show a failure.
    /// With <paramref name="job"/> (the checks window): git runs as a step of it with a
    /// pseudo-terminal (hooks' /dev/tty output in <see cref="Job.TtyText"/>); the cancel then comes only from
    /// <see cref="Job.Token"/> (with the hooks' SIGINT wait) and <paramref name="ct"/> is ignored.</summary>
    public async Task<GitResult> CommitAsync(string message, bool amend, bool noVerify, CancellationToken ct = default, Job? job = null)
    {
        // Not built yet: --signoff.
        var file = Path.Combine(Info.GitDir, "GERGIT_COMMIT_EDITMSG");
        await File.WriteAllTextAsync(file, message, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), ct);
        List<string> args = ["commit"];
        if (amend) args.Add("--amend");
        if (noVerify) args.Add("--no-verify");
        args.AddRange(["--file", file]);
        return job is null ? await GitRunner.RunAsync(Info.WorkTree, args, ct: ct)
            : await GitRunner.RunJobAsync(job, Info.WorkTree, args, env: new Dictionary<string, string> { ["PRE_COMMIT_COLOR"] = "never" }, tty: true,
                hooksWait: GitRunner.HooksWait);
    }
}
