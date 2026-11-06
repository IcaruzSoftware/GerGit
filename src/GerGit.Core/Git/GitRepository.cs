using System.Runtime.CompilerServices;
using GerGit.Core.Diff;

namespace GerGit.Core.Git;

/// <summary>Read-only access to one repository.</summary>
public sealed partial class GitRepository(RepoInfo info)
{
    string? _emptyTree;

    public RepoInfo Info { get; } = info;

    /// <summary>The empty tree's id in this repository's object format (SHA-1 or SHA-256), resolved once.</summary>
    async Task<string> EmptyTreeAsync(CancellationToken ct) =>
        _emptyTree ??= (await Run(["hash-object", "-t", "tree", "/dev/null"], ct)).Trim();

    /// <summary>Throws GitException with a user-readable message when the folder is not a work-tree repository.</summary>
    public static async Task<GitRepository> OpenAsync(string path, CancellationToken ct = default)
    {
        if (!Directory.Exists(path)) throw new GitException("[internal] Not found");

        var r = await GitRunner.RunAsync(path, ["rev-parse", "--show-toplevel", "--absolute-git-dir"], ct: ct);
        ct.ThrowIfCancellationRequested();
        var lines = r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (!r.Success || lines.Length != 2) throw OpenFailure(path, r);

        string workTree = lines[0], gitDir = lines[1], commonDir = gitDir;
        var kind = RepoKind.Repository;
        var commonFile = Path.Combine(gitDir, "commondir");
        if (File.Exists(commonFile))
        {
            var first = File.ReadLines(commonFile).FirstOrDefault()?.Trim() ?? "";
            commonDir = Path.GetFullPath(Path.Combine(gitDir, first));
            kind = RepoKind.Worktree;
        }
        // Limitation: submodule = git dir inside a .git/modules/ folder; the .gitmodules cross-check is skipped.
        else if (gitDir.Contains("/.git/modules/", StringComparison.Ordinal))
            kind = RepoKind.Submodule;

        return new GitRepository(new RepoInfo(workTree, gitDir, commonDir, kind));
    }

    /// <summary>Why `rev-parse` did not describe a work tree. Only git's own "no repository found here or above" answers
    /// ("not a git repository (or any ...") become "The '...' directory is not a git repository". Every other error is
    /// passed on with git's text, or the exit status when stderr is empty (git killed, out of memory): a folder git cannot
    /// enter, a broken config, a `.git` file pointing nowhere (older git: "not a git repository: &lt;path&gt;", newer
    /// git: "gitfile does not point to a valid repository"; offering "Initialize" there would be wrong), an unsafe
    /// repository (verbatim, so the caller can offer "Mark repository as safe"). The session restore keeps such paths.</summary>
    internal static GitException OpenFailure(string path, GitResult r)
    {
        if (r.Stderr.Contains("not a git repository (or any", StringComparison.Ordinal)
            || r.Stderr.Contains("must be run in a work tree", StringComparison.Ordinal))
            return new GitException($"The '{path}' directory is not a git repository");
        return new GitException(r.ReadableOutput.Trim().Length > 0 ? r.ReadableOutput
            : $"git rev-parse failed in '{path}' (exit status {r.ExitCode})");
    }

    /// <summary>Reads branches, remote branches and tags (portable variant, works with git ≥ 2.38), including ahead/behind.</summary>
    public async Task<RefSnapshot> ReadRefsAsync(CancellationToken ct = default)
    {
        // The 8 base fields plus %(upstream:track,nobracket) as a 9th, so ahead/behind needs no extra process.
        var refsTask = Run(["for-each-ref",
            "--format=%(objectname)%09%(refname)%09%(HEAD)%09%(upstream)%09%(*objectname)%09%(committerdate:raw)%09%(*committerdate:raw)%09%(symref)%09%(upstream:track,nobracket)"], ct);
        var symTask = GitRunner.RunAsync(Info.WorkTree, ["symbolic-ref", "-q", "HEAD"], ct: ct);
        var headTask = ResolveHeadAsync(ct);
        await Task.WhenAll(refsTask, symTask, headTask);

        var parsed = new List<(string Name, string Sha, RefKind Kind, DateTimeOffset Date, string? TagObj, string? Upstream, string Track)>();
        foreach (var line in (await refsTask).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = line.Split('\t');
            if (f.Length != 9 || f[7].Length > 0) continue; // malformed or symbolic ref (origin/HEAD)
            var name = f[1];
            RefKind kind;
            if (name.StartsWith("refs/heads/", StringComparison.Ordinal)) kind = RefKind.LocalBranch;
            else if (name.StartsWith("refs/remotes/", StringComparison.Ordinal))
            {
                var rest = name["refs/remotes/".Length..];
                int slash = rest.IndexOf('/');
                if (slash < 0 || rest[(slash + 1)..] == "HEAD") continue;
                kind = RefKind.RemoteBranch;
            }
            else if (name.StartsWith("refs/tags/", StringComparison.Ordinal)) kind = RefKind.Tag;
            else if (name.StartsWith("refs/bisect/", StringComparison.Ordinal))
            {
                var b = name["refs/bisect/".Length..];
                kind = b.StartsWith("good", StringComparison.Ordinal) || b.StartsWith("old", StringComparison.Ordinal) ? RefKind.BisectGood
                    : b.StartsWith("skip", StringComparison.Ordinal) ? RefKind.BisectSkip : RefKind.BisectBad;
            }
            else continue; // refs/stash, notes, ...

            bool annotated = f[4].Length > 0;
            var date = ParseRawDate(f[5]) ?? ParseRawDate(f[6]) ?? DateTimeOffset.UnixEpoch;
            var upstream = kind == RefKind.LocalBranch && f[3].StartsWith("refs/remotes/", StringComparison.Ordinal) ? f[3] : null;
            parsed.Add((name, annotated ? f[4] : f[0], kind, date, annotated ? f[0] : null, upstream, f[8]));
        }

        var names = parsed.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var refs = parsed
            .Select(p =>
            {
                if (p.Upstream is null) return new GitRef(p.Name, p.Sha, p.Kind, p.Date, p.TagObj);
                if (p.Track == "gone" || !names.Contains(p.Upstream))
                    return new GitRef(p.Name, p.Sha, p.Kind, p.Date, p.TagObj, p.Upstream, UpstreamState.Gone);
                var (ahead, behind) = ParseTrack(p.Track);
                return new GitRef(p.Name, p.Sha, p.Kind, p.Date, p.TagObj, p.Upstream, UpstreamState.Valid, ahead, behind);
            })
            .OrderBy(r => r.FullName, StringComparer.Ordinal)
            .ToList();

        // Active branch only when HEAD points to an existing local branch (unborn HEAD → none).
        var sym = await symTask;
        var target = sym.Success ? sym.Stdout.Trim() : null;
        var active = target is not null && refs.Any(r => r.Kind == RefKind.LocalBranch && r.FullName == target) ? target : null;
        return new RefSnapshot(refs, await headTask, active);
    }

    /// <summary>"ahead N", "behind M", "ahead N, behind M" or empty (in sync).</summary>
    static (int Ahead, int Behind) ParseTrack(string track)
    {
        int ahead = 0, behind = 0;
        foreach (var part in track.Split(", ", StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split(' ');
            if (kv.Length != 2 || !int.TryParse(kv[1], out var n)) continue;
            if (kv[0] == "ahead") ahead = n;
            else if (kv[0] == "behind") behind = n;
        }
        return (ahead, behind);
    }

    static DateTimeOffset? ParseRawDate(string raw) =>
        long.TryParse(raw.Split(' ')[0], out var s) ? DateTimeOffset.FromUnixTimeSeconds(s) : null;

    /// <summary>The checked-out branch (full ref name), null when HEAD is detached or git fails.</summary>
    public async Task<string?> ResolveBranchAsync(CancellationToken ct = default)
    {
        var r = await GitRunner.RunAsync(Info.WorkTree, ["symbolic-ref", "-q", "HEAD"], ct: ct);
        return r.Success ? r.Stdout.Trim() : null;
    }

    /// <summary>HEAD's commit, null when there is none (unborn) or git fails.</summary>
    public async Task<string?> ResolveHeadAsync(CancellationToken ct = default)
    {
        var r = await GitRunner.RunAsync(Info.WorkTree, ["rev-parse", "--verify", "-q", "HEAD"], ct: ct);
        ct.ThrowIfCancellationRequested();
        var sha = r.Stdout.Trim();
        return r.Success && sha.Length > 0 ? sha : null;
    }

    /// <summary>Reads the remotes, sorted by name.</summary>
    public async Task<IReadOnlyList<RemoteEntry>> ReadRemotesAsync(CancellationToken ct = default)
    {
        var remotes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (section, sub, key, value) in ParseConfig(await Run(["config", "--local", "--null", "--list"], ct)))
            if (section == "remote" && key == "url" && sub is { Length: > 0 } && value is not null)
                remotes.TryAdd(sub, value);
        return remotes.Select(kv => new RemoteEntry(kv.Key, kv.Value)).ToList();
    }

    /// <summary>`git config --null --list` output: `key\nvalue\0`; key = section[.subsection].variable.</summary>
    static IEnumerable<(string Section, string? Subsection, string Key, string? Value)> ParseConfig(string output)
    {
        foreach (var entry in output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            int nl = entry.IndexOf('\n');
            var fullKey = nl < 0 ? entry : entry[..nl];
            var value = nl < 0 ? null : entry[(nl + 1)..];
            int first = fullKey.IndexOf('.'), last = fullKey.LastIndexOf('.');
            if (first < 0) continue;
            yield return (fullKey[..first].ToLowerInvariant(),
                last > first ? fullKey[(first + 1)..last] : null,
                fullKey[(last + 1)..].ToLowerInvariant(),
                value);
        }
    }

    /// <summary>The stash list in git's order (stash@{0} first).</summary>
    public async Task<IReadOnlyList<StashEntry>> ReadStashesAsync(CancellationToken ct = default)
    {
        var r = await GitRunner.RunAsync(Info.WorkTree,
            ["log", "--no-show-signature", "--format=%H%n%P%n%aN%n%aE%n%at%n%gd%n%s", "-g", "--first-parent", "-m", "refs/stash", "--"], ct: ct);
        ct.ThrowIfCancellationRequested();
        if (!r.Success) return []; // "bad revision" = no stashes; other failures are not shown either

        var lines = r.Stdout.Split('\n');
        var stashes = new List<StashEntry>();
        for (int i = 0; i + 6 < lines.Length; i += 7)
        {
            var parent = lines[i + 1].Length >= 40 ? lines[i + 1][..40] : "";
            if (parent.Length == 0) continue;
            var subject = lines[i + 6];
            stashes.Add(new StashEntry(lines[i], parent, lines[i + 2], lines[i + 3],
                DateTimeOffset.FromUnixTimeSeconds(long.Parse(lines[i + 4])), lines[i + 5], subject, ShortenStashSubject(subject)));
        }
        return stashes;
    }

    /// <summary>"On main: my note" → "my note"; subjects starting with "WIP" stay whole.</summary>
    internal static string ShortenStashSubject(string subject)
    {
        if (subject.StartsWith("WIP", StringComparison.Ordinal)) return subject;
        int i = subject.IndexOf(": ", StringComparison.Ordinal);
        return i < 0 ? subject : subject[(i + 2)..];
    }

    /// <summary>The submodules listed in .gitmodules, sorted by path.</summary>
    public Task<IReadOnlyList<SubmoduleEntry>> ReadSubmodulesAsync(CancellationToken ct = default) =>
        ReadSubmodulesAsync(Info.WorkTree, ct);

    /// <summary>The entries of <paramref name="workTree"/>/.gitmodules. Active only reflects an `active` key there:
    /// a listed submodule is not necessarily initialized (see <see cref="UpdateSubmodulesAsync"/>).</summary>
    static async Task<IReadOnlyList<SubmoduleEntry>> ReadSubmodulesAsync(string workTree, CancellationToken ct)
    {
        var file = Path.Combine(workTree, ".gitmodules");
        if (!File.Exists(file)) return [];
        var r = await GitRunner.RunAsync(workTree, ["config", "-f", file, "--null", "--list"], ct: ct);
        ct.ThrowIfCancellationRequested();
        if (!r.Success) return []; // malformed .gitmodules must not break status or changed files
        var subs = new Dictionary<string, (string? Path, bool Active)>(StringComparer.Ordinal);
        foreach (var (section, name, key, value) in ParseConfig(r.Stdout))
        {
            if (section != "submodule" || name is null) continue;
            var s = subs.GetValueOrDefault(name, (null, true));
            if (key == "path") s.Path ??= value; // the first value counts, as in git
            else if (key == "active") s.Active = value?.ToLowerInvariant() is not ("false" or "no" or "off" or "0");
            subs[name] = s;
        }
        return subs.Where(kv => kv.Value.Path is { Length: > 0 })
            .Select(kv => new SubmoduleEntry(kv.Key, kv.Value.Path!, kv.Value.Active))
            .OrderBy(s => s.Path, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The status of the work tree and index, including every untracked file. <paramref name="lockFree"/> = lock-free read: git must not rewrite the index.</summary>
    public async Task<StatusSnapshot> ReadStatusAsync(bool lockFree = false, CancellationToken ct = default)
    {
        var subsTask = SubmodulePathsAsync(ct);
        List<string> args = ["status", "--porcelain", "-z", "--untracked-files=all"];
        if (lockFree) args.Insert(0, "--no-optional-locks");
        var output = await Run(args, ct);
        return ParseStatus(output, await subsTask);
    }

    internal static StatusSnapshot ParseStatus(string output, IReadOnlySet<string> submodules)
    {
        var unstaged = new List<StatusEntry>();
        var staged = new List<StatusEntry>();
        var tokens = output.Split('\0');
        for (int i = 0; i < tokens.Length; i++)
        {
            var t = tokens[i];
            if (t.Length < 4 || t[2] != ' ') continue; // malformed (or the trailing empty token)
            char x = t[0], y = t[1];
            var path = t[3..];
            // Porcelain v1 puts the destination first, then the source of a rename/copy.
            string? orig = (x is 'R' or 'C' || y is 'R' or 'C') && i + 1 < tokens.Length ? tokens[++i] : null;
            bool sub = submodules.Contains(path);

            if (x == '?' && y == '?') unstaged.Add(new StatusEntry(path, null, ChangeKind.Untracked, false, sub));
            else if (x == '!' && y == '!') unstaged.Add(new StatusEntry(path, null, ChangeKind.Ignored, false, sub));
            else if (x == 'U' || y == 'U' || (x == 'A' && y == 'A') || (x == 'D' && y == 'D'))
                unstaged.Add(new StatusEntry(path, null, ChangeKind.Conflicted, false, sub));
            else
            {
                if (y is 'M' or 'D' or 'T' or 'A') unstaged.Add(new StatusEntry(path, null, KindOf(y), false, sub));
                // Y = R/C (git >= 2.18): an intent-to-add file that matches a deleted (R) or kept (C) file. The new
                // path is an Added row, the old path of a rename a Deleted row; otherwise both files would vanish.
                else if (y is 'R' or 'C' && x is not ('R' or 'C'))
                {
                    unstaged.Add(new StatusEntry(path, null, ChangeKind.Added, false, sub));
                    if (y == 'R' && orig is not null)
                        unstaged.Add(new StatusEntry(orig, null, ChangeKind.Deleted, false, submodules.Contains(orig)));
                }
                if (x is 'M' or 'D' or 'A' or 'T' or 'R' or 'C')
                    staged.Add(new StatusEntry(path, x is 'R' or 'C' ? orig : null, KindOf(x), true, sub));
            }
        }
        return new StatusSnapshot(Sorted(unstaged), Sorted(staged));

        static List<StatusEntry> Sorted(List<StatusEntry> l) =>
            l.OrderBy(e => e.Path, StringComparer.Ordinal).ThenBy(e => e.Staged).ThenBy(e => e.Kind).ToList();
    }

    static ChangeKind KindOf(char c) => c switch
    {
        'A' => ChangeKind.Added,
        'D' => ChangeKind.Deleted,
        'R' => ChangeKind.Renamed,
        'C' => ChangeKind.Copied,
        'T' => ChangeKind.TypeChanged,
        _ => ChangeKind.Modified,
    };

    /// <summary>Revision walk: streamed, date order, tips = branches, remotes (no */HEAD), tags, HEAD if it resolves.</summary>
    public async IAsyncEnumerable<WalkedCommit> WalkAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        List<string> args = ["log", "--date-order", "--no-show-signature", "--parents", "--format=%H%x20%P",
            "--exclude=*/HEAD", "--remotes", "--branches", "--tags"];
        if (await ResolveHeadAsync(ct) is not null) args.Add("HEAD"); // unborn HEAD would make git fail
        args.Add("--");

        await foreach (var line in GitRunner.ReadLinesAsync(Info.WorkTree, args, ct: ct))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0) yield return new WalkedCommit(parts[0], parts[1..]);
        }
    }

    /// <summary>Commit headers (lazy): one batch (≤ 100 SHAs) via --stdin, same order as requested.</summary>
    public async Task<IReadOnlyList<CommitHeader>> ReadHeadersAsync(IReadOnlyList<string> shas, CancellationToken ct = default)
    {
        if (shas.Count == 0) return [];
        var output = await Run(["log", "--no-walk=unsorted", "--no-show-signature",
            "--format=%H%x1f%aN%x1f%aE%x1f%at%x1f%s%x1f%b%x1e", "--stdin"], ct, string.Join('\n', shas) + "\n");

        var bySha = new Dictionary<string, CommitHeader>(StringComparer.Ordinal);
        foreach (var record in output.Split('\x1e'))
        {
            var f = record.TrimStart('\n').Split('\x1f');
            if (f.Length != 6 || !long.TryParse(f[3], out var time)) continue;
            bySha.TryAdd(f[0], new CommitHeader(f[0], f[1], f[2], DateTimeOffset.FromUnixTimeSeconds(time), f[4],
                f[5].Trim().Length > 0));
        }
        return shas.Where(bySha.ContainsKey).Select(s => bySha[s]).ToList();
    }

    /// <summary>
    /// Matching: the commits among <paramref name="shas"/> whose full SHA, author name, author e-mail or message
    /// contains <paramref name="text"/> (case-insensitive plain substring), in the requested order. One git call per batch.
    /// </summary>
    public async Task<IReadOnlyList<string>> SearchCommitsAsync(IReadOnlyList<string> shas, string text, CancellationToken ct = default)
    {
        if (shas.Count == 0) return [];
        // -z: records end with NUL, which a message cannot contain; the message is the last field, so a \x1f in it stays.
        // The parse runs off the UI thread (ConfigureAwait(false)); a batch has thousands of messages.
        var output = await Run(["log", "--no-walk=unsorted", "--no-show-signature", "-z", "--format=%H%x1f%aN%x1f%aE%x1f%B", "--stdin"],
            ct, string.Join('\n', shas) + "\n").ConfigureAwait(false);
        var requested = shas.ToHashSet(StringComparer.Ordinal);
        return output.Split('\0')
            .Select(record => record.Split('\x1f', 4))
            .Where(f => f.Length == 4 && requested.Contains(f[0]) && f.Any(field => field.Contains(text, StringComparison.OrdinalIgnoreCase)))
            .Select(f => f[0]).ToList();
    }

    /// <summary>Commit details. Throws GitException("Commit not found: <sha>") / ("Cannot parse revision details").</summary>
    public async Task<CommitDetails> ReadCommitDetailsAsync(string sha, CancellationToken ct = default)
    {
        var r = await GitRunner.RunAsync(Info.WorkTree, ["show", "--no-patch", "--no-show-signature",
            "--pretty=format:%H±.%aN±.%aE±.%at±.%cN±.%cE±.%ct±.%P±.%B", sha, "--"], ct: ct);
        ct.ThrowIfCancellationRequested();
        if (!r.Success) throw new GitException($"Commit not found: {sha}");

        var p = r.Stdout.Split("±.", 9); // the message itself may contain the separator
        if (p.Length < 9 || !long.TryParse(p[3], out var at) || !long.TryParse(p[6], out var cTime))
            throw new GitException("Cannot parse revision details");
        return new CommitDetails(p[0], p[1], p[2], DateTimeOffset.FromUnixTimeSeconds(at),
            p[4], p[5], DateTimeOffset.FromUnixTimeSeconds(cTime),
            p[7].Split(' ', StringSplitOptions.RemoveEmptyEntries), p[8].TrimEnd('\r', '\n'));
    }

    /// <summary>Changed files. fromSha null = against first parent (empty tree for root commits).</summary>
    public async Task<IReadOnlyList<ChangedFile>> ReadChangedFilesAsync(string? fromSha, string toSha, CancellationToken ct = default)
    {
        var subsTask = SubmodulePathsAsync(ct);
        var r = await RunDiffWithRootRetry(fromSha, toSha,
            from => ["--no-pager", "diff", "--name-status", "--find-renames", "-z", from, toSha], ct);
        return ParseNameStatus(r, await subsTask);
    }

    internal static List<ChangedFile> ParseNameStatus(string output, IReadOnlySet<string> submodules)
    {
        var files = new List<ChangedFile>();
        var t = output.Split('\0');
        for (int i = 0; i < t.Length; i++)
        {
            if (t[i].Length == 0) continue;
            char status = t[i][0];
            if (status is 'R' or 'C')
            {
                if (i + 2 >= t.Length) break;
                string old = t[++i], path = t[++i];
                files.Add(new ChangedFile(path, old, status == 'R' ? ChangeKind.Renamed : ChangeKind.Copied, submodules.Contains(path)));
            }
            else
            {
                if (i + 1 >= t.Length) break;
                var path = t[++i];
                if (status is 'A' or 'D' or 'M' or 'T')
                    files.Add(new ChangedFile(path, null, KindOf(status), submodules.Contains(path)));
                // other letters: skipped
            }
        }
        return files;
    }

    /// <summary>File diff, parsed from the diff text.</summary>
    public async Task<FileDiff> ReadFileDiffAsync(DiffRequest q, CancellationToken ct = default)
    {
        if (q.Working != WorkingDiff.None) return DiffParser.Parse(await ReadWorkingDiffAsync(q, ct));
        var output = await RunDiffWithRootRetry(q.FromSha, q.ToSha, from =>
            ["-c", "core.quotepath=false", "--literal-pathspecs", "--no-pager", "diff", from, q.ToSha, .. DiffOptionsAndPaths(q)], ct);
        return DiffParser.Parse(output);
    }

    /// <summary>Largest binary side read into memory for a preview (64 MiB); bigger ones show the card without preview.</summary>
    public const long MaxBinaryBytes = 64L * 1024 * 1024;

    /// <summary>Binary data of a changed file and its blob size: the old or new side of a binary diff of
    /// <paramref name="q"/>, null when that side is absent. Blob IDs come from the diff's `index` line (--full-index; a
    /// zero ID = absent); the working-tree side of Local Changes is read from disk. <paramref name="withBytes"/> false =
    /// only the size (unknown-binary cards). Above <see cref="MaxBinaryBytes"/> only the size is read either way
    /// (card without preview).</summary>
    public async Task<BinarySide?> ReadBinarySideAsync(DiffRequest q, FileDiff d, bool newSide, bool withBytes, CancellationToken ct = default)
    {
        if ((newSide ? d.NewPath : d.OldPath) is null) return null;
        if (newSide && q.Working is WorkingDiff.Unstaged or WorkingDiff.Untracked or WorkingDiff.Ours)
        {
            var file = new FileInfo(Path.Combine(Info.WorkTree, q.Path));
            if (!file.Exists) return null;
            return new(file.Length, withBytes && file.Length <= MaxBinaryBytes ? await File.ReadAllBytesAsync(file.FullName, ct) : null);
        }
        var blob = newSide ? d.NewBlob : d.OldBlob;
        if (blob is null || blob.All(c => c == '0')) return null;
        var size = long.Parse((await Run(["cat-file", "-s", blob], ct)).Trim(), System.Globalization.CultureInfo.InvariantCulture);
        if (!withBytes || size > MaxBinaryBytes) return new(size, null);
        var bytes = await GitRunner.ReadBytesAsync(Info.WorkTree, ["cat-file", "blob", blob], ct);
        return new(bytes.Length, bytes);
    }

    /// <summary>The shared diff options, then `-- path [old path]`.</summary>
    static List<string> DiffOptionsAndPaths(DiffRequest q)
    {
        bool renamed = q.OldPath is not null && q.OldPath != q.Path;
        List<string> a = ["--no-ext-diff", "--no-color",
            "--src-prefix=" + DiffParser.SrcPrefix, "--dst-prefix=" + DiffParser.DstPrefix,
            "--full-index", "--submodule=short"];
        if (renamed) a.Add("--find-renames");
        if (q.EntireFile) a.AddRange(["--inter-hunk-context=100000", "--unified=100000"]);
        else a.Add($"--unified={q.Context}");
        if (q.IgnoreWhitespace) a.Add("--ignore-all-space");
        a.AddRange(["--", q.Path]);
        if (renamed) a.Add(q.OldPath!);
        return a;
    }

    /// <summary>Runs a diff against `fromSha` or `toSha~1`; a root commit has no parent, so retry with the empty tree.</summary>
    async Task<string> RunDiffWithRootRetry(string? fromSha, string toSha, Func<string, List<string>> args, CancellationToken ct)
    {
        var r = await GitRunner.RunAsync(Info.WorkTree, args(fromSha ?? toSha + "~1"), ct: ct);
        if (fromSha is null && !r.Success && !r.Cancelled &&
            (r.Stderr.Contains("unknown revision or path not in the working tree.") || r.Stderr.Contains("fatal: bad revision")))
            r = await GitRunner.RunAsync(Info.WorkTree, args(await EmptyTreeAsync(ct)), ct: ct);
        return Check(r, ct);
    }

    async Task<IReadOnlySet<string>> SubmodulePathsAsync(CancellationToken ct) =>
        (await ReadSubmodulesAsync(ct)).Select(s => s.Path).ToHashSet(StringComparer.Ordinal);

    async Task<string> Run(List<string> args, CancellationToken ct, string? stdin = null) =>
        Check(await GitRunner.RunAsync(Info.WorkTree, args, stdin, ct), ct);

    static string Check(GitResult r, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (r.Cancelled) throw new OperationCanceledException(ct);
        if (!r.Success) throw new GitException(r.ReadableOutput);
        return r.Stdout;
    }
}
