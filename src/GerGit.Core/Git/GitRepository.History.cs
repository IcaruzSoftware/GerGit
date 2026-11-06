using System.Globalization;
using GerGit.Core.Diff;
using GerGit.Core.Graph;

namespace GerGit.Core.Git;

/// <summary>One file entry of a history commit; OldPath only for renames/copies.</summary>
public sealed record FileChange(ChangeKind Kind, string Path, string? OldPath);

/// <summary>One commit of a file or folder history with the files it changed below the path.</summary>
public sealed record FileRevision(CommitHeader Commit, IReadOnlyList<FileChange> Files);

/// <summary>Where a blame/history log starts: the tip commit and the reference shown.</summary>
public sealed record HistoryTip(string Sha, string? RefName);

/// <summary>Blame window and File History window: blame, history log, tip choice, file at a revision.</summary>
public sealed partial class GitRepository
{
    /// <summary>First commit for blame: the newest commit at or before <paramref name="sha"/> (HEAD when null)
    /// that touched the file; null = NotFound (no such file in that history, unborn HEAD).</summary>
    public async Task<string?> FindBlameStartAsync(string? sha, string path, CancellationToken ct = default)
    {
        var r = await GitRunner.RunAsync(Info.WorkTree,
            ["--literal-pathspecs", "log", "--no-show-signature", "--follow", "-n", "1", "--pretty=format:%H", sha ?? "HEAD", "--", path], ct: ct);
        ct.ThrowIfCancellationRequested();
        var s = r.Stdout.Trim();
        return r.Success && s.Length >= 40 && s.All(Uri.IsHexDigit) ? s : null;
    }

    /// <summary>File history (`--follow`; no effect for folders), starting at <paramref name="tip"/> (HEAD when null).</summary>
    public async Task<IReadOnlyList<FileRevision>> ReadFileHistoryAsync(string? tip, string path, CancellationToken ct = default)
    {
        List<string> args = ["-c", "core.quotePath=false", "--literal-pathspecs", "log", "--no-show-signature", "--oneline",
            "--name-status", "--follow", "--pretty=format:%H%n%aN%n%aE%n%ct%n%s"];
        if (tip is not null) args.Add(tip);
        args.AddRange(["--", path]);
        var output = await Run(args, ct);
        return await Task.Run(() => ParseHistory(output), ct); // long folder histories: off the UI thread
    }

    /// <summary>
    /// Parsing the history log: 5 header lines, then `&lt;status&gt;\t&lt;path&gt;[\t&lt;new path&gt;]` lines and a
    /// blank line. A merge commit has no name-status lines and no blank line, so a line without a TAB after
    /// the file lines starts the next commit (skipping it would misread every following commit).
    /// </summary>
    internal static List<FileRevision> ParseHistory(string output)
    {
        var lines = output.Split('\n');
        var result = new List<FileRevision>();
        int i = 0;
        while (i < lines.Length)
        {
            if (lines[i].Length == 0) { i++; continue; }
            if (i + 4 >= lines.Length || lines[i].Length < 40 || !lines[i].All(Uri.IsHexDigit)
                || !long.TryParse(lines[i + 3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var time))
                throw new GitException("Cannot parse revision");
            var commit = new CommitHeader(lines[i], lines[i + 1], lines[i + 2], DateTimeOffset.FromUnixTimeSeconds(time), lines[i + 4], false);
            i += 5;
            var files = new List<FileChange>();
            for (; i < lines.Length && lines[i].Contains('\t'); i++)
            {
                // core.quotePath=false keeps UTF-8, but `"`, `\`, TAB and control characters are still C-quoted.
                var f = lines[i].Split('\t').Select(DiffParser.Unquote).ToArray();
                if (f[0].Length == 0) continue;
                var kind = KindOf(f[0][0]);
                files.Add(f.Length >= 3 && f[0][0] is 'R' or 'C' ? new FileChange(kind, f[2], f[1]) : new FileChange(kind, f[1], null));
            }
            result.Add(new FileRevision(commit, files));
        }
        return result;
    }

    /// <summary>Blame: `git blame --porcelain &lt;sha&gt;~ -- &lt;path&gt;`. Index = final line - 1; null = a line
    /// without a commit. Empty for `no such path` / `bad revision` (root commit, file added or renamed in sha).</summary>
    public async Task<IReadOnlyList<CommitHeader?>> ReadBlameParentAsync(string sha, string path, CancellationToken ct = default)
    {
        var r = await GitRunner.RunAsync(Info.WorkTree, ["blame", "--porcelain", sha + "~", "--", path], ct: ct);
        ct.ThrowIfCancellationRequested();
        if (!r.Success && !r.Cancelled && (r.Stderr.Contains("fatal: no such path") || r.Stderr.Contains("fatal: bad revision")))
            return [];
        return ParseBlame(Check(r, ct));
    }

    /// <summary>Parsing `git blame --porcelain`.</summary>
    internal static List<CommitHeader?> ParseBlame(string output)
    {
        var commits = new Dictionary<string, CommitHeader>(StringComparer.Ordinal);
        var lines = new List<CommitHeader?>();
        var text = output.Split('\n');
        for (int i = 0; i < text.Length; i++)
        {
            var header = text[i].TrimEnd('\r');
            if (header.Length == 0) continue;
            var p = header.Split(' ');
            if (p.Length is < 3 or > 4 || p[0].Length < 40 || !p[0].All(Uri.IsHexDigit)
                || !int.TryParse(p[2], CultureInfo.InvariantCulture, out var final) || final < 1
                || !int.TryParse(p[1], CultureInfo.InvariantCulture, out _))
                throw new GitException($"Can't parse blame header line: '{header}'");
            int count = 1;
            if (p.Length == 4 && !int.TryParse(p[3], CultureInfo.InvariantCulture, out count))
                throw new GitException($"Can't parse blame header line: '{header}'");

            // Key lines up to the TAB content line: metadata for a new SHA, at most `filename` for a known one.
            string? name = null, mail = null, summary = null;
            long? time = null;
            for (i++; i < text.Length && !text[i].StartsWith('\t'); i++)
            {
                var l = text[i].TrimEnd('\r');
                int sp = l.IndexOf(' ');
                var (key, value) = sp < 0 ? (l, "") : (l[..sp], l[(sp + 1)..]);
                if (key == "author") name = value;
                else if (key == "author-mail") mail = value.Trim('<', '>');
                else if (key == "author-time" && long.TryParse(value, CultureInfo.InvariantCulture, out var t)) time = t;
                else if (key == "summary") summary = value;
            }
            if (i >= text.Length) throw new GitException("Can't parse blame");

            if (!commits.TryGetValue(p[0], out var c))
            {
                if (name is null || mail is null || time is null || summary is null) throw new GitException("Can't parse blame");
                commits[p[0]] = c = new CommitHeader(p[0], name, mail, DateTimeOffset.FromUnixTimeSeconds(time.Value), summary, false);
            }
            // A 4th number opens a block of lines; git still sends one header per line, which lands on the same slots.
            for (int line = final; line < final + count; line++)
            {
                while (lines.Count < line) lines.Add(null);
                lines[line - 1] = c;
            }
        }
        return lines;
    }

    /// <summary>
    /// How tips are chosen (git substitute): among HEAD and the local and remote branches that contain
    /// <paramref name="sha"/>, the farthest tip; ties prefer HEAD, then for-each-ref order. Reference = the active
    /// branch on that tip, else the first branch there in label order. Null when no candidate contains the commit.
    /// Limitation: one `rev-list --count` per distinct tip; label order is the plain natural sort.
    /// </summary>
    public async Task<HistoryTip?> FindBestTipAsync(string sha, RefSnapshot refs, CancellationToken ct = default)
    {
        var branches = (await Run(["for-each-ref", "--contains", sha, "--format=%(objectname) %(refname)", "refs/heads", "refs/remotes"], ct))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split(' ', 2)).Where(p => p.Length == 2 && !p[1].EndsWith("/HEAD", StringComparison.Ordinal))
            .Select(p => (Sha: p[0], Ref: p[1])).ToList();
        var tips = new List<string>();
        if (refs.HeadSha is { } head && (await GitRunner.RunAsync(Info.WorkTree, ["merge-base", "--is-ancestor", sha, head], ct: ct)).Success)
            tips.Add(head);
        tips.AddRange(branches.Select(b => b.Sha));

        string? best = null;
        int bestDistance = -1;
        foreach (var tip in tips.Distinct())
        {
            int d = int.Parse((await Run(["rev-list", "--count", $"{sha}..{tip}"], ct)).Trim(), CultureInfo.InvariantCulture);
            if (d > bestDistance) (best, bestDistance) = (tip, d);
        }
        if (best is null) return null;

        var atTip = branches.Where(b => b.Sha == best).Select(b => b.Ref).ToList();
        var name = refs.ActiveBranch is { } active && atTip.Contains(active) ? active
            : atTip.Order(Comparer<string>.Create(CommitListBuilder.NaturalCompare)).FirstOrDefault();
        return new HistoryTip(best, name is null ? null : ShortRefName(name));
    }

    static string ShortRefName(string full) =>
        full.StartsWith("refs/heads/", StringComparison.Ordinal) ? full["refs/heads/".Length..]
        : full.StartsWith("refs/remotes/", StringComparison.Ordinal) ? full["refs/remotes/".Length..] : full;

    /// <summary>The index (stage 0) as a revision for <see cref="ReadFileAtAsync"/>; <see cref="Stage"/> for conflicts.</summary>
    public const string IndexRev = ":0";

    /// <summary>Stage 1 (base), 2 (ours) or 3 (theirs) of an unmerged path as a revision for <see cref="ReadFileAtAsync"/>.</summary>
    public static string Stage(int n) => n is >= 0 and <= 3 ? $":{n}" : throw new ArgumentOutOfRangeException(nameof(n));

    /// <summary>The object name `&lt;rev&gt;:&lt;path&gt;`. An empty rev is refused: `:&lt;path&gt;` lets a file named `0:x` pick
    /// stage 0 of `x` (or `:/text` search commit messages); the index is always the explicit stage `:0:&lt;path&gt;`.</summary>
    public static string ObjectName(string rev, string path) =>
        rev.Length > 0 ? $"{rev}:{path}" : throw new ArgumentException($"Use {nameof(IndexRev)} for the index.", nameof(rev));

    /// <summary>File at revision: the bytes of `&lt;rev&gt;:&lt;path&gt;` with filters (line endings) applied.
    /// <paramref name="rev"/> = a revision, <see cref="IndexRev"/> or <see cref="Stage"/>.
    /// Limitation: no LFS smudge of pointer files (LFS is not built).</summary>
    public Task<byte[]> ReadFileAtAsync(string rev, string path, CancellationToken ct = default) =>
        GitRunner.ReadBytesAsync(Info.WorkTree, ["cat-file", "--filters", ObjectName(rev, path)], ct);

    /// <summary>Reset file: `git reset -q &lt;target&gt; -- &lt;path&gt;` then `git checkout &lt;target&gt; -- &lt;path&gt;`; when
    /// the file does not exist at the target ("did not match any file(s) known to git") the working-tree file is deleted.
    /// Returns the failing result, else the last one.</summary>
    public async Task<GitResult> ResetFileToAsync(Job job, string target, string path)
    {
        var reset = await GitRunner.RunJobAsync(job, Info.WorkTree, ["--literal-pathspecs", "reset", "-q", target, "--", path]);
        if (!reset.Success) return reset;
        var checkout = await GitRunner.RunJobAsync(job, Info.WorkTree, ["--literal-pathspecs", "checkout", target, "--", path]);
        if (checkout.Success || !checkout.Stderr.Contains("did not match any file(s) known to git")) return checkout;
        // A folder (e.g. a submodule checkout) is not deleted: git's error is shown instead.
        var full = System.IO.Path.Combine(Info.WorkTree, path);
        if (Directory.Exists(full)) return checkout;
        File.Delete(full);
        return new GitResult(0, "", "");
    }
}
