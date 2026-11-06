using System.Text;
using GerGit.Core.Conflicts;

namespace GerGit.Core.Git;

/// <summary>Conflict state, abort / continue, single-side resolve, stage gate and the resolver's data.</summary>
public sealed partial class GitRepository
{
    // ---------------------------------------------------------------- state detection

    /// <summary>
    /// Conflict state detection, first match wins. Runs no git process in the clean state.
    /// Throws GitException when the state files are broken ("the state read fails").
    /// </summary>
    public async Task<RepoState> ReadRepoStateAsync(StatusSnapshot status, CancellationToken ct = default)
    {
        string dir = Info.GitDir;
        bool IsFile(string name) => File.Exists(Path.Combine(dir, name));
        bool IsDir(string name) => Directory.Exists(Path.Combine(dir, name));
        string? ReadFile(string name)
        {
            try { return File.ReadAllText(Path.Combine(dir, name)); }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        var conflicted = status.Unstaged.Where(e => e.Kind == ChangeKind.Conflicted).ToList();
        async Task<IReadOnlyList<UnmergedFile>> UnmergedAsync()
        {
            if (conflicted.Count == 0) return [];
            var stages = await ReadUnmergedStagesAsync(null, ct);
            return conflicted.Select(e => ToUnmergedFile(e.Path, stages.GetValueOrDefault(e.Path), e.IsSubmodule)).ToList();
        }

        List<(string Name, string Sha)>? refs = null;
        async Task<string> NameOf(string sha, bool skipBackup)
        {
            refs ??= await ReadNamedRefsAsync(ct);
            foreach (var (name, refSha) in refs)
                if (refSha == sha && !(skipBackup && name.StartsWith("backup/", StringComparison.Ordinal))) return name;
            return Abbrev(sha);
        }

        // Root ref of HEAD: the checked-out branch, else a named ref of its commit, else the abbreviated id.
        async Task<(string Name, string Sha)> HeadAsync()
        {
            var sha = await RevParseAsync("HEAD", ct) ?? throw new GitException("'HEAD' not found in references");
            var sym = await GitRunner.RunAsync(Info.WorkTree, ["symbolic-ref", "-q", "HEAD"], ct: ct);
            ct.ThrowIfCancellationRequested();
            var target = sym.Success ? sym.Stdout.Trim() : "";
            return (target.StartsWith("refs/heads/", StringComparison.Ordinal) ? target["refs/heads/".Length..] : await NameOf(sha, false), sha);
        }

        // 1. merge
        if (IsFile("MERGE_HEAD"))
        {
            var theirs = ParseSha(ReadFile("MERGE_HEAD"));
            var head = await HeadAsync();
            return new RepoState(RepoStateKind.Merge, await UnmergedAsync(), head.Name, await NameOf(theirs, true), head.Sha, theirs);
        }

        // 2. rebase-apply: rebase (apply backend) or am; never falls through.
        if (IsDir("rebase-apply"))
        {
            var orig = await RevParseAsync("ORIG_HEAD", ct) ?? throw new GitException("'ORIG_HEAD' not found in references");
            int done = Math.Max(0, ReadInt("rebase-apply/next") - 1), total = ReadInt("rebase-apply/last");
            var onto = ReadFile("rebase-apply/onto")?.Trim();
            if (onto is null || !IsSha(onto) || await RevParseAsync(onto + "^{commit}", ct) is null)
            {
                var head = await HeadAsync();
                return new RepoState(RepoStateKind.ApplyPatches, await UnmergedAsync(), head.Name, "patch", head.Sha, null, done, total);
            }
            var active = ReadFile("rebase-apply/original-commit")?.Trim();
            if (active is null || !IsSha(active)) throw new GitException("Cannot read active SHA in 'original-commit'");
            return new RepoState(RepoStateKind.Rebase, await UnmergedAsync(), await NameOf(onto, true), await NameOf(orig, false),
                onto, orig, done, total, active);
        }

        // 3. rebase-merge: the only rule that falls through (missing orig-head or onto).
        if (IsDir("rebase-merge") && ReadFile("rebase-merge/orig-head") is { } origText && ReadFile("rebase-merge/onto") is { } ontoText)
        {
            var orig = ParseSha(origText);
            var onto = ParseSha(ontoText);
            // Remaining todo lines: non-blank, not starting with the app's comment char `^` (`#` lines count).
            var todo = ReadFile("rebase-merge/git-rebase-todo");
            int remaining = todo is null ? 1 : todo.Split('\n').Count(l => l.Trim().Length > 0 && !l.StartsWith('^'));
            // Done lines: non-blank, not `#`, at least 45 characters; the id follows the first space.
            var doneLines = (ReadFile("rebase-merge/done") ?? "").Split('\n')
                .Select(l => l.TrimEnd('\r'))
                .Where(l => l.Trim().Length > 0 && !l.StartsWith('#') && l.Length >= 45)
                .ToList();
            string? active = null;
            if (doneLines.Count > 0)
            {
                var rest = doneLines[^1][(doneLines[^1].IndexOf(' ') + 1)..];
                var id = rest.Split(' ')[0];
                active = IsSha(id) ? id : null; // an abbreviated id gives no active commit
            }
            string? amend = IsFile("rebase-merge/current-fixups") ? null : ReadFile("rebase-merge/amend")?.Trim();
            return new RepoState(RepoStateKind.Rebase, await UnmergedAsync(), await NameOf(onto, true), await NameOf(orig, true),
                onto, orig, Math.Max(0, doneLines.Count - 1), doneLines.Count + remaining, active,
                amend is { Length: > 0 } ? amend : null);
        }

        // 4. single cherry-pick: theirs = the picked commit's abbreviated id.
        if (IsFile("CHERRY_PICK_HEAD"))
        {
            var picked = ParseSha(ReadFile("CHERRY_PICK_HEAD"));
            var head = await HeadAsync();
            return new RepoState(RepoStateKind.CherryPick, await UnmergedAsync(), head.Name, Abbrev(picked), head.Sha, picked)
            {
                InSequence = IsDir("sequencer"),
            };
        }

        // 5. revert: Side names keeps `local` / `remote`.
        if (IsFile("REVERT_HEAD"))
        {
            var reverted = ParseSha(ReadFile("REVERT_HEAD"));
            return new RepoState(RepoStateKind.Revert, await UnmergedAsync(), OursSha: await RevParseAsync("HEAD", ct), TheirsSha: reverted);
        }

        // 6. cherry-pick sequence: no unmerged list.
        if (IsDir("sequencer")) return new RepoState(RepoStateKind.CherryPickSequence, []);

        // 7. merge --squash
        if (IsFile("SQUASH_MSG"))
        {
            var head = await HeadAsync();
            return new RepoState(RepoStateKind.Squash, await UnmergedAsync(), head.Name, "remote", head.Sha);
        }

        // 8. unmerged index without an operation (e.g. after `stash pop`)
        if (conflicted.Count > 0)
        {
            var head = await HeadAsync();
            return new RepoState(RepoStateKind.Unmerged, await UnmergedAsync(), head.Name, "stash/patch", head.Sha);
        }

        // 9. bisect (detected only; its UI is not built yet)
        if (IsFile("BISECT_START")) return new RepoState(RepoStateKind.Bisect, []);

        return RepoState.Clean;

        int ReadInt(string name) => int.TryParse(ReadFile(name)?.Trim(), out var n) ? n : 0;
    }

    static string Abbrev(string sha) => sha.Length > 7 ? sha[..7] : sha;

    /// <summary>A full object id (SHA-1 or SHA-256).</summary>
    static bool IsSha(string s) => s.Length is 40 or 64 && s.All(char.IsAsciiHexDigit);

    /// <summary>Reads a commit id file: the first line, trimmed, must be an id.</summary>
    static string ParseSha(string? text)
    {
        var sha = (text ?? "").Trim().Split('\n')[0].Trim(); // octopus MERGE_HEAD: the first head
        return IsSha(sha) ? sha : throw new GitException($"Failed to parse SHA in '{(text ?? "").Trim()}'");
    }

    async Task<string?> RevParseAsync(string rev, CancellationToken ct)
    {
        var r = await GitRunner.RunAsync(Info.WorkTree, ["rev-parse", "--verify", "-q", rev], ct: ct);
        ct.ThrowIfCancellationRequested();
        var sha = r.Stdout.Trim();
        return r.Success && sha.Length > 0 ? sha : null;
    }

    /// <summary>Branches, remote branches and tags (short name, commit), sorted by full name; symbolic refs skipped.
    /// Limitation: other refs/ namespaces (notes, stash, bisect) are not used as side names.</summary>
    async Task<List<(string Name, string Sha)>> ReadNamedRefsAsync(CancellationToken ct)
    {
        var output = await Run(["for-each-ref", "--format=%(refname)%00%(objectname)%00%(*objectname)%00%(symref)",
            "refs/heads", "refs/remotes", "refs/tags"], ct);
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split('\0'))
            .Where(f => f.Length == 4 && f[3].Length == 0)
            .OrderBy(f => f[0], StringComparer.Ordinal)
            .Select(f => (f[0][(f[0].IndexOf('/', 5) + 1)..], f[2].Length > 0 ? f[2] : f[1]))
            .ToList();
    }

    /// <summary>One unmerged path's index stages 1-3 (null = no entry for that stage) and whether it is a gitlink.</summary>
    sealed record UnmergedStages(string?[] Shas, bool Gitlink);

    /// <summary>`git ls-files -u -z` for <paramref name="path"/> (null = every unmerged path).</summary>
    async Task<Dictionary<string, UnmergedStages>> ReadUnmergedStagesAsync(string? path, CancellationToken ct)
    {
        List<string> args = ["--literal-pathspecs", "ls-files", "-u", "-z"];
        if (path is not null) args.AddRange(["--", path]);
        var result = new Dictionary<string, UnmergedStages>(StringComparer.Ordinal);
        // Records: "<mode> <sha> <stage>\t<path>".
        foreach (var rec in (await Run(args, ct)).Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            int tab = rec.IndexOf('\t');
            var f = tab < 0 ? [] : rec[..tab].Split(' ');
            if (f.Length != 3 || !int.TryParse(f[2], out var stage) || stage is < 1 or > 3) continue;
            var p = rec[(tab + 1)..];
            if (!result.TryGetValue(p, out var s)) result[p] = s = new UnmergedStages(new string?[3], false);
            s.Shas[stage - 1] = f[1];
            if (f[0] == "160000") result[p] = s with { Gitlink = true };
        }
        return result;
    }

    /// <summary>Which index stages 1-3 (base, ours, theirs) the unmerged <paramref name="path"/> has (`ls-files -u`);
    /// all false when it is not unmerged.</summary>
    public async Task<bool[]> ReadStagesPresentAsync(string path, CancellationToken ct = default) =>
        (await ReadUnmergedStagesAsync(path, ct)).TryGetValue(path, out var s) ? s.Shas.Select(x => x is not null).ToArray() : new bool[3];

    /// <summary>The status letters git derives from the stages present (base, ours, theirs): DD, AU, UD, UA, DU, AA, UU.</summary>
    static UnmergedFile ToUnmergedFile(string path, UnmergedStages? s, bool isSubmodule)
    {
        if (s is null) return new UnmergedFile(path, 'U', 'U', isSubmodule); // index changed since the status was read
        int mask = (s.Shas[0] is null ? 0 : 1) | (s.Shas[1] is null ? 0 : 2) | (s.Shas[2] is null ? 0 : 4);
        var (ours, theirs) = mask switch
        {
            1 => ('D', 'D'),
            2 => ('A', 'U'),
            3 => ('U', 'D'),
            4 => ('U', 'A'),
            5 => ('D', 'U'),
            6 => ('A', 'A'),
            _ => ('U', 'U'),
        };
        return new UnmergedFile(path, ours, theirs, isSubmodule || s.Gitlink);
    }

    // ---------------------------------------------------------------- abort / continue

    /// <summary>
    /// Abort command ... per state, then the submodule update; the update also runs after a failure,
    /// and the caller refreshes either way. Clearing the draft message / Amend box is the caller's part.
    /// </summary>
    public Task<GitResult> AbortAsync(Job job, RepoState state)
    {
        // A multi-commit pick stopped at a conflict has CHERRY_PICK_HEAD and sequencer/; the
        // plain `reset --merge` left the sequencer behind ("Cherry-pick in progress" after Abort), so it gets
        // `cherry-pick --abort` like the sequencer state.
        bool sequencer = Directory.Exists(Path.Combine(Info.GitDir, "sequencer"));
        string[]? args = state.Kind switch
        {
            RepoStateKind.None => null,
            RepoStateKind.Rebase => ["rebase", "--abort"],
            RepoStateKind.CherryPickSequence => ["cherry-pick", "--abort"],
            RepoStateKind.CherryPick when sequencer => ["cherry-pick", "--abort"],
            RepoStateKind.Revert => ["revert", "--abort"],
            RepoStateKind.ApplyPatches => ["am", "--abort"],
            RepoStateKind.Bisect => ["bisect", "reset"], // notification bar Abort
            _ => ["reset", "--merge"], // merge, single cherry-pick, squash, unmerged index
        };
        if (args is null) return Task.FromResult(new GitResult(0, "", ""));
        return WithSubmoduleUpdateAsync(job, () => GitRunner.RunJobAsync(job, Info.WorkTree, args));
    }

    /// <summary>Continue for the cherry-pick sequence: `cherry-pick --continue`, then `--skip` while git reports an
    /// empty pick (same texts as the cherry-pick operation); then the submodule update.</summary>
    public Task<GitResult> ContinueCherryPickAsync(Job job) =>
        WithSubmoduleUpdateAsync(job, async () =>
        {
            var r = await GitRunner.RunJobAsync(job, Info.WorkTree, ["cherry-pick", "--continue"]);
            while (!r.Success && !r.Cancelled && IsEmptyPick(r))
                r = await GitRunner.RunJobAsync(job, Info.WorkTree, ["cherry-pick", "--skip"]);
            return r;
        });

    /// <summary>Continue for am: `am --continue`; when it fails with "No changes - did you forget to use 'git add'?"
    /// `am --skip` instead; then the submodule update.</summary>
    public Task<GitResult> ContinueAmAsync(Job job) =>
        WithSubmoduleUpdateAsync(job, async () =>
        {
            var r = await GitRunner.RunJobAsync(job, Info.WorkTree, ["am", "--continue"]);
            if (!r.Success && !r.Cancelled && r.ReadableOutput.Contains("No changes - did you forget to use 'git add'?", StringComparison.Ordinal))
                r = await GitRunner.RunJobAsync(job, Info.WorkTree, ["am", "--skip"]);
            return r;
        });

    // ---------------------------------------------------------------- resolve

    /// <summary>
    /// Resolve with one side: for each of <paramref name="paths"/> that is unmerged in
    /// <paramref name="state"/> (other paths are skipped), in order, stopping at the first failure.
    /// Chosen side deleted: `git rm`; submodule: check out the chosen commit in the submodule, then `git add`;
    /// otherwise `checkout-index -f --stage=2|3` + `git add`. Returns the failing or the last result.
    /// </summary>
    public async Task<GitResult> ResolveWithSideAsync(Job job, RepoState state, IReadOnlyList<string> paths, bool ours)
    {
        var byPath = state.Unmerged.ToDictionary(f => f.Path, StringComparer.Ordinal);
        var result = new GitResult(0, "", "");
        foreach (var path in paths.Distinct())
        {
            if (!byPath.TryGetValue(path, out var file)) continue;
            result = await ResolveOneAsync(job, file, ours);
            if (!result.Success) break;
        }
        return result;
    }

    async Task<GitResult> ResolveOneAsync(Job job, UnmergedFile file, bool ours)
    {
        if (ours ? file.OursDeleted : file.TheirsDeleted)
            return await GitRunner.RunJobAsync(job, Info.WorkTree, ["--literal-pathspecs", "rm", "--", file.Path]);

        if (file.IsSubmodule)
        {
            // Both ids could be read from the diff's index line; the index stages hold the same ids.
            var stages = await ReadUnmergedStagesAsync(file.Path, job.Token);
            var sha = stages.GetValueOrDefault(file.Path)?.Shas[ours ? 1 : 2];
            if (sha is null) return new GitResult(1, "", $"Cannot find the {(ours ? "local" : "remote")} commit of submodule '{file.Path}'");
            var subDir = Path.Combine(Info.WorkTree, file.Path);
            // An uninitialised submodule has no .git: git would walk up and check out in the superproject.
            if (!Path.Exists(Path.Combine(subDir, ".git")))
                return new GitResult(1, "", $"Submodule '{file.Path}' is not initialized");
            var r = await GitRunner.RunJobAsync(job, subDir, ["checkout", "--detach", sha]);
            if (!r.Success) return r;
        }
        else
        {
            var r = await GitRunner.RunJobAsync(job, Info.WorkTree, ["checkout-index", "-f", $"--stage={(ours ? 2 : 3)}", "--", file.Path]);
            if (!r.Success) return r;
        }
        return await GitRunner.RunJobAsync(job, Info.WorkTree, ["--literal-pathspecs", "add", "--", file.Path]);
    }

    /// <summary>Reset File to Unmerged State: `git checkout -m`. With `--literal-pathspecs` and `--`,
    /// so names starting with "-" or holding glob characters work. An index.lock failure classifies as IndexLocked.</summary>
    public Task<GitResult> ResetToUnmergedAsync(Job job, string path) =>
        GitRunner.RunJobAsync(job, Info.WorkTree, ["--literal-pathspecs", "checkout", "-m", "--", path]);

    // ---------------------------------------------------------------- stage gate

    /// <summary>
    /// Stage gate for staging files: false when any of <paramref name="paths"/> is an unmerged file that is not
    /// resolved (<see cref="IsResolvedConflictDiff"/>). Paths that are not unmerged always pass.
    /// </summary>
    public async Task<bool> PassesStageGateAsync(IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        var unmerged = await ReadUnmergedStagesAsync(null, ct);
        if (unmerged.Count == 0) return true;
        var subs = await SubmodulePathsAsync(ct);
        foreach (var path in paths.Distinct())
            if (unmerged.TryGetValue(path, out var s) &&
                !IsResolvedConflictDiff(await ReadConflictDiffAsync(path, ct), s.Gitlink || subs.Contains(path)))
                return false;
        return true;
    }

    /// <summary>The conflict card's "resolved" state: the same test as the stage gate.</summary>
    public async Task<bool> IsConflictResolvedAsync(UnmergedFile file, CancellationToken ct = default) =>
        IsResolvedConflictDiff(await ReadConflictDiffAsync(file.Path, ct), file.IsSubmodule);

    /// <summary>Stage gate: unresolved while the combined diff still holds `<<<<<<<` or starts with
    /// `* Unmerged path` (deleted side). Binary, LFS and submodule conflicts are resolved only by the card actions.</summary>
    public static bool IsResolvedConflictDiff(string diff, bool isSubmodule) =>
        !diff.StartsWith("* Unmerged path", StringComparison.Ordinal) &&
        MergeDocument.Classify(diff, isSubmodule) == MergeMode.Text &&
        !diff.Contains("<<<<<<<", StringComparison.Ordinal);

    /// <summary>Resolver window commands: the whole file as one combined-diff hunk. Never index.lock (the
    /// conflict card reloads it after watcher refreshes; see ReadWorkingDiffAsync).</summary>
    Task<string> ReadConflictDiffAsync(string path, CancellationToken ct) =>
        Run(["--no-optional-locks", "-c", "diff.autoRefreshIndex=false", "-c", "core.quotepath=false", "--literal-pathspecs", "--no-pager", "diff", "--find-renames", "--no-ext-diff",
            "--no-color", "--src-prefix=a/", "--dst-prefix=b/", "--full-index", "--submodule=short",
            "--inter-hunk-context=1000000", "--unified=1000000", "--", path], ct);

    // ---------------------------------------------------------------- resolver data

    /// <summary>Resolver window commands: runs the combined diff, classifies it and (text mode) parses it and
    /// reads the working file's last byte for the "no newline at end of file" rule.</summary>
    public async Task<MergeDocument> LoadMergeDocumentAsync(string path, CancellationToken ct = default)
    {
        var diffTask = ReadConflictDiffAsync(path, ct);
        var stages = await ReadUnmergedStagesAsync(path, ct);
        var subs = await SubmodulePathsAsync(ct);
        var diff = await diffTask;
        bool submodule = stages.GetValueOrDefault(path)?.Gitlink == true || subs.Contains(path);
        var mode = MergeDocument.Classify(diff, submodule);
        if (mode != MergeMode.Text) return new MergeDocument(path, mode, null, false);
        return new MergeDocument(path, mode, MergeDocument.ParseChunks(diff), EndsWithoutNewline(Path.Combine(Info.WorkTree, path)));
    }

    static bool EndsWithoutNewline(string file)
    {
        try
        {
            using var s = File.OpenRead(file);
            if (s.Length == 0) return false;
            s.Seek(-1, SeekOrigin.End);
            return s.ReadByte() != '\n';
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>On Resolve (text mode): writes the text (UTF-8, no BOM) over the working file, then `git add`.</summary>
    public async Task<GitResult> WriteResolvedAsync(Job job, string path, string text)
    {
        await File.WriteAllTextAsync(Path.Combine(Info.WorkTree, path), text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return await GitRunner.RunJobAsync(job, Info.WorkTree, ["--literal-pathspecs", "add", "--", path]);
    }

    /// <summary>
    /// Conflict card actions: the commits per side that touched <paramref name="path"/>, from the merge base of
    /// the replayed/merged commit and HEAD. Merge: base..MERGE_HEAD and base..HEAD; rebase, cherry-pick, revert: only the
    /// applied commit (sha~..sha) for theirs. Other states (and any git failure) list nothing.
    /// </summary>
    public async Task<ConflictCommits> ReadConflictCommitsAsync(RepoState state, string path, CancellationToken ct = default)
    {
        var src = state.Kind switch
        {
            RepoStateKind.Merge or RepoStateKind.CherryPick or RepoStateKind.Revert => state.TheirsSha,
            RepoStateKind.Rebase => state.ActiveSha,
            _ => null,
        };
        if (src is null) return new ConflictCommits([], []);
        var mb = await GitRunner.RunAsync(Info.WorkTree, ["merge-base", src, "HEAD"], ct: ct);
        ct.ThrowIfCancellationRequested();
        var mergeBase = mb.Success ? mb.Stdout.Trim() : "";
        if (mergeBase.Length == 0) return new ConflictCommits([], []);

        var theirs = LogAsync(state.Kind == RepoStateKind.Merge ? $"{mergeBase}..{src}" : $"{src}~..{src}");
        var ours = LogAsync($"{mergeBase}..HEAD");
        return new ConflictCommits(await theirs, await ours);

        async Task<IReadOnlyList<CommitHeader>> LogAsync(string range)
        {
            var r = await GitRunner.RunAsync(Info.WorkTree,
                ["--literal-pathspecs", "log", "--no-show-signature", "--date-order", "--format=%H", range, "--", path], ct: ct);
            ct.ThrowIfCancellationRequested();
            if (!r.Success) return [];
            var shas = r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            return await ReadHeadersAsync(shas, ct);
        }
    }
}
