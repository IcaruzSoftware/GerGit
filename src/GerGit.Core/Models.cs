namespace GerGit.Core;

// Shared data model between Core and the app. Owned by the orchestrator:
// implementers may ADD members, but must not rename or remove existing ones.

public sealed class GitException(string message) : Exception(message);

/// <summary>One finished git process. Success = exit code 0 and not cancelled.</summary>
public sealed record GitResult(int ExitCode, string Stdout, string Stderr, bool Cancelled = false)
{
    public bool Success => ExitCode == 0 && !Cancelled;

    /// <summary>Full readable output: stdout+stderr joined by \n when both are non-blank, else the non-blank one, else stderr.</summary>
    public string ReadableOutput =>
        !string.IsNullOrWhiteSpace(Stdout) && !string.IsNullOrWhiteSpace(Stderr) ? Stdout + "\n" + Stderr
        : !string.IsNullOrWhiteSpace(Stdout) ? Stdout
        : Stderr;
}

public enum RepoKind { Repository, Submodule, Worktree }

/// <summary>Result of opening a folder.</summary>
public sealed record RepoInfo(string WorkTree, string GitDir, string CommonDir, RepoKind Kind)
{
    public string Name => Path.GetFileName(WorkTree.TrimEnd('/'));
}

public enum RefKind { LocalBranch, RemoteBranch, Tag, BisectGood, BisectBad, BisectSkip }

public enum UpstreamState { None, Valid, Gone }

/// <summary>A displayable reference. Sha is always the commit (annotated tags peeled).</summary>
public sealed record GitRef(
    string FullName,
    string Sha,
    RefKind Kind,
    DateTimeOffset CommitterDate,
    string? TagObjectSha = null,
    string? Upstream = null,                 // full ref name, local branches only
    UpstreamState UpstreamState = UpstreamState.None,
    int Ahead = 0,
    int Behind = 0)
{
    /// <summary>Name without refs/heads/, refs/remotes/, refs/tags/, refs/bisect/ ("origin/main" for remote branches).</summary>
    public string ShortName => FullName[(FullName.IndexOf('/', FullName.IndexOf('/') + 1) + 1)..];

    /// <summary>Remote name for remote branches ("origin"), else null.</summary>
    public string? RemoteName => Kind == RefKind.RemoteBranch ? ShortName[..ShortName.IndexOf('/')] : null;

    /// <summary>Branch name below the remote ("main" for origin/main); same as ShortName for other kinds.</summary>
    public string NameInRemote => Kind == RefKind.RemoteBranch ? ShortName[(ShortName.IndexOf('/') + 1)..] : ShortName;
}

/// <summary>All references plus HEAD. Refs sorted by FullName, ordinal.</summary>
public sealed record RefSnapshot(
    IReadOnlyList<GitRef> Refs,
    string? HeadSha,                 // null on an unborn HEAD
    string? ActiveBranch)            // full ref name of the checked-out branch, null when detached/unborn
{
    public bool IsDetached => ActiveBranch is null;
}

public sealed record RemoteEntry(string Name, string Url);

/// <summary>Subject is git's raw subject; ShortSubject has the "On main: " part removed.</summary>
public sealed record StashEntry(
    string Sha, string ParentSha, string AuthorName, string AuthorEmail, DateTimeOffset Date,
    string Name, string Subject, string ShortSubject);

public sealed record SubmoduleEntry(string Name, string Path, bool Active);

public enum ChangeKind { Added, Modified, Deleted, Renamed, Copied, TypeChanged, Untracked, Ignored, Conflicted }

/// <summary>Mode/SourceBlob: only set on amend-mode Staged entries (Amend: source mode and source blob id from
/// diff-index raw output; restoring needs the source mode, not the destination mode).</summary>
public sealed record StatusEntry(string Path, string? OldPath, ChangeKind Kind, bool Staged, bool IsSubmodule = false,
    string? Mode = null, string? SourceBlob = null);

/// <summary>FileCount = number of distinct paths across both lists.</summary>
public sealed record StatusSnapshot(IReadOnlyList<StatusEntry> Unstaged, IReadOnlyList<StatusEntry> Staged)
{
    public int FileCount => Unstaged.Select(e => e.Path).Concat(Staged.Select(e => e.Path)).Distinct().Count();
}

/// <summary>One commit from the revision walk.</summary>
public sealed record WalkedCommit(string Sha, string[] Parents);

/// <summary>Lazily loaded row text.</summary>
public sealed record CommitHeader(
    string Sha, string AuthorName, string AuthorEmail, DateTimeOffset AuthorDate, string Subject, bool HasBody);

/// <summary>Commit details (git show).</summary>
public sealed record CommitDetails(
    string Sha,
    string AuthorName, string AuthorEmail, DateTimeOffset AuthorDate,
    string CommitterName, string CommitterEmail, DateTimeOffset CommitterDate,
    string[] Parents,
    string Message)
{
    /// <summary>No line break = whole message trimmed; else text before the first line break.</summary>
    public string Subject => Message.Contains('\n') ? Message[..Message.IndexOf('\n')].TrimEnd('\r') : Message.Trim();

    /// <summary>Rest after the first line break, leading/trailing line breaks removed.</summary>
    public string Description => Message.Contains('\n') ? Message[(Message.IndexOf('\n') + 1)..].Trim('\r', '\n') : "";
}

/// <summary>A file in the changed-files list (name-status parsing).</summary>
public sealed record ChangedFile(string Path, string? OldPath, ChangeKind Kind, bool IsSubmodule = false);

/// <summary>Working-tree diffs of Local Changes (Hunk and line staging diff commands). None = commit diff.</summary>
/// Ours = the working file against the ours (stage 2) version of an unmerged path (resolved conflict card).
public enum WorkingDiff { None, Unstaged, Untracked, Staged, StagedAmend, Ours }

/// <summary>What to diff. FromSha null = against the first parent of ToSha (empty tree for a root commit).
/// With Working != None, FromSha/ToSha are ignored and the index / working tree is diffed instead.</summary>
public sealed record DiffRequest(
    string? FromSha, string ToSha, string Path, string? OldPath,
    int Context = 3, bool EntireFile = false, bool IgnoreWhitespace = false, WorkingDiff Working = WorkingDiff.None);

public enum DiffLineKind { Context, Added, Removed }

/// <summary>One body line of a hunk, marker removed. Line numbers follow the diff gutter rules.</summary>
public sealed record DiffLine(DiffLineKind Kind, string Text, int? OldNumber, int? NewNumber, bool NoNewlineAtEnd = false);

public sealed record DiffHunk(int OldStart, int OldLength, int NewStart, int NewLength, string? Context, IReadOnlyList<DiffLine> Lines);

/// <summary>Parsed result of one file diff.</summary>
public sealed record FileDiff(
    string? OldPath, string? NewPath,   // null = side absent (added / deleted file)
    int? OldMode, int? NewMode,
    string? OldBlob, string? NewBlob,   // from the index line (--full-index)
    bool IsBinary, bool IsSubmodule, bool IsMinified,
    int TotalLength,                    // sum of all body line lengths (too-large check)
    IReadOnlyList<DiffHunk> Hunks)
{
    public bool IsEmpty => Hunks.Count == 0 && !IsBinary && OldMode == NewMode;
}

/// <summary>One side of a binary diff; Bytes null when only the size was read.</summary>
public sealed record BinarySide(long Size, byte[]? Bytes);

// ---- Diff document (Text diff: how the document is built, unified layout) ----

/// <summary>Alignment: an empty side-by-side row that keeps the next context line level with the other pane.</summary>
public enum DocLineKind { Context, Added, Removed, HunkHeader, Service, Alignment }

/// <summary>A highlighted intra-line range (character offsets into Text).</summary>
public readonly record struct TextRange(int Start, int Length);

/// <summary>Hunk/Line: the source line in FileDiff.Hunks[Hunk].Lines[Line]; -1 for headers and service lines.</summary>
public sealed record DocLine(DocLineKind Kind, string Text, int? OldNumber, int? NewNumber, IReadOnlyList<TextRange> WordRanges,
    int Hunk = -1, int Line = -1);

/// <summary>A change group (deleted + added block) by document line index, for navigation and the change map.</summary>
public sealed record DocChangeGroup(int FirstLine, int RemovedCount, int AddedCount);

public sealed record DiffDocument(IReadOnlyList<DocLine> Lines, IReadOnlyList<DocChangeGroup> Groups)
{
    /// <summary>The editor text: one document line per line.</summary>
    public string Text => string.Join("\n", Lines.Select(l => l.Text));
}

/// <summary>Side-by-side layout: two documents with equal line counts (old = context + deleted lines,
/// new = context + added lines); both share the change groups (by row). The editor texts are joined once, where the
/// pair is made (off the UI thread).</summary>
public sealed record SideBySideDocument(DiffDocument Old, DiffDocument New)
{
    public string OldText { get; } = Old.Text;
    public string NewText { get; } = New.Text;
}

// ---- Line staging ----

public enum PatchAction { Stage, Unstage, Discard }

/// <summary>One body line of a parsed diff: FileDiff.Hunks[Hunk].Lines[Line].</summary>
public readonly record struct HunkLine(int Hunk, int Line);

// ---- Commit list ----

/// <summary>One graph line in a row. Top/Bottom = column or -1 for none.</summary>
public readonly record struct GraphLine(int LaneId, int Top, int Bottom);

/// <summary>Layout of one row. DotColumn is used for ref label colours (index = DotColumn mod 13).</summary>
public sealed record GraphRow(int DotColumn, int DotLaneId, bool IsMerge, IReadOnlyList<GraphLine> Lines);

public enum UpstreamMark { None, Ahead, Behind }

public enum RefLabelKind { LocalBranch, RemoteBranch, Tag, BisectGood, BisectBad, BisectSkip, Stash }

/// <summary>A label on a row. IconOnly = compacted remote label (tooltip = remote/branch).</summary>
public sealed record RefLabel(RefLabelKind Kind, string Text, string FullName, bool IsActive = false, bool IconOnly = false, string? Tooltip = null);

/// <summary>A row in the commit list: a commit or a stash.</summary>
public sealed record CommitRow(
    string Sha,
    string[] VisualParents,
    GraphRow Graph,
    IReadOnlyList<RefLabel> Labels,
    bool IsHead,
    bool IsReachable,
    UpstreamMark Upstream,
    StashEntry? Stash = null);

// ---- Conflict state (detection) ----

public enum RepoStateKind { None, Merge, Rebase, ApplyPatches, CherryPick, CherryPickSequence, Revert, Squash, Unmerged, Bisect }

/// <summary>An unmerged file. Ours/Theirs = first/second letter of git's status pair (DD AU UD UA DU AA UU):
/// 'A', 'D' or 'U'. IsSubmodule = a gitlink conflict (mode 160000) or a registered submodule.</summary>
public sealed record UnmergedFile(string Path, char Ours, char Theirs, bool IsSubmodule)
{
    public bool OursDeleted => Ours == 'D';
    public bool TheirsDeleted => Theirs == 'D';

    /// <summary>Merge allowed: neither side deleted the file.</summary>
    public bool MergeAllowed => !OursDeleted && !TheirsDeleted;

    /// <summary>Conflict card change-type word for a side letter ("modified" also for 'U').</summary>
    public static string SideWord(char letter) => letter switch { 'A' => "added", 'D' => "deleted", _ => "modified" };
}

/// <summary>
/// The repository state. Names = the side names of Side names; Shas = the commits
/// behind them (null when there is none). Done/Total = rebase and am counters. ActiveSha = the commit being applied
/// (rebase). AmendSha = rebase stopped for amending. Unmerged = the unmerged files (empty for the sequencer state).
/// </summary>
public sealed record RepoState(
    RepoStateKind Kind,
    IReadOnlyList<UnmergedFile> Unmerged,
    string OursName = "local", string TheirsName = "remote",
    string? OursSha = null, string? TheirsSha = null,
    int Done = 0, int Total = 0,
    string? ActiveSha = null, string? AmendSha = null)
{
    public static readonly RepoState Clean = new(RepoStateKind.None, []);

    /// <summary>A cherry-pick stopped inside a multi-commit sequence (CHERRY_PICK_HEAD together with sequencer/).</summary>
    public bool InSequence { get; init; }
}

/// <summary>Conflict card actions: per side the commits that touched the file.</summary>
public sealed record ConflictCommits(IReadOnlyList<CommitHeader> Theirs, IReadOnlyList<CommitHeader> Ours);
