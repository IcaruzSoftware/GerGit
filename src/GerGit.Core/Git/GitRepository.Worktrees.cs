namespace GerGit.Core.Git;

/// <summary>One worktree of Branch = full ref ("refs/heads/x"), null when detached; Sha = its HEAD commit
/// (null on an unborn HEAD). IsCurrent = it is the repository this <see cref="GitRepository"/> was opened on.</summary>
public sealed record WorktreeEntry(string Path, string? Branch, string? Sha, bool IsMain, bool IsCurrent)
{
    /// <summary>Last folder name.</summary>
    public string Name => System.IO.Path.GetFileName(Path.TrimEnd('/'));

    /// <summary>"x" for refs/heads/x.</summary>
    public string? BranchName => Branch is { } b && b.StartsWith("refs/heads/", StringComparison.Ordinal) ? b["refs/heads/".Length..] : Branch;
}

public sealed partial class GitRepository
{
    /// <summary>The main worktree folder: the parent of the common git dir when that is named ".git", else the common
    /// git dir itself. For a normal repository this is <see cref="RepoInfo.WorkTree"/>.</summary>
    public string MainWorkTree => Path.GetFileName(Info.CommonDir.TrimEnd('/')) == ".git"
        ? Path.GetDirectoryName(Info.CommonDir.TrimEnd('/'))!
        : Info.CommonDir;

    /// <summary>through `git worktree list --porcelain -z`.</summary>
    public async Task<IReadOnlyList<WorktreeEntry>> ReadWorktreesAsync(CancellationToken ct = default) =>
        ParseWorktrees(await Run(["worktree", "list", "--porcelain", "-z"], ct), Info.WorkTree);

    /// <summary>
    /// Parses `worktree list --porcelain -z`: NUL-terminated "key value" fields, an empty field ends a record; the first
    /// record is the main worktree. Empty when there is no linked worktree. The main one is left out when the repository
    /// is bare; linked ones whose folder is gone are skipped (prunable, or <paramref name="exists"/> false: git never marks
    /// a locked worktree prunable); locked ones are listed like the others. Order: main first, then the linked ones by
    /// folder name (natural, case-insensitive).
    /// </summary>
    internal static IReadOnlyList<WorktreeEntry> ParseWorktrees(string output, string currentPath, Func<string, bool>? exists = null)
    {
        exists ??= Directory.Exists;
        var all = new List<(WorktreeEntry Entry, bool Bare, bool Prunable)>();
        string? path = null, sha = null, branch = null;
        bool bare = false, prunable = false;
        foreach (var field in output.Split('\0'))
        {
            if (field.Length == 0)
            {
                if (path is not null)
                    all.Add((new WorktreeEntry(path, branch, sha, IsMain: all.Count == 0, path == currentPath), bare, prunable));
                (path, sha, branch, bare, prunable) = (null, null, null, false, false);
                continue;
            }
            int space = field.IndexOf(' ');
            var (key, value) = space < 0 ? (field, "") : (field[..space], field[(space + 1)..]);
            switch (key)
            {
                case "worktree": path = value; break;
                case "HEAD": sha = value.Trim('0').Length == 0 ? null : value; break; // unborn: all zeros
                case "branch": branch = value; break;
                case "bare": bare = true; break;
                case "prunable": prunable = true; break;
            }
        }
        var linked = all.Where(e => !e.Entry.IsMain && !e.Prunable && exists(e.Entry.Path)).Select(e => e.Entry).OrderBy(e => e.Name, NaturalOrder).ToList();
        if (linked.Count == 0) return [];
        return all is [{ Bare: false } main, ..] ? [main.Entry, .. linked] : linked;
    }

    static readonly StringComparer NaturalOrder = StringComparer.Create(System.Globalization.CultureInfo.InvariantCulture,
        System.Globalization.CompareOptions.IgnoreCase | System.Globalization.CompareOptions.NumericOrdering);
}
