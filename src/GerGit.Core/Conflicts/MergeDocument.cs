namespace GerGit.Core.Conflicts;

/// <summary>Resolver window commands classification. Non-text modes use the single-side resolve only.</summary>
public enum MergeMode { Text, Binary, Submodule, Lfs }

public enum MergeSide { Ours, Theirs }

public enum MergeChunkKind { Context, Change, Conflict }

/// <summary>Context = in both sides and the result; Added/Removed = change-chunk lines; Conflict = a conflict-chunk line.</summary>
public enum MergeLineKind { Context, Added, Removed, Conflict }

/// <summary>A line of a side pane. Text keeps its line ending (CRLF files keep the \r). Side is null for
/// context lines (shown in both panes). IsPlaceholder = the synthetic `--- empty ---` line of an empty conflict side.</summary>
public sealed class MergeLine(MergeSide? side, MergeLineKind kind, string text, bool isPlaceholder = false)
{
    public MergeSide? Side { get; } = side;
    public MergeLineKind Kind { get; } = kind;
    public string Text { get; } = text;
    public bool IsPlaceholder { get; } = isPlaceholder;
}

/// <summary>A line of the result pane. Source = the side line it came from (null = a line the user inserted by editing).
/// Text is editable; it is dropped with the line when the source line is deselected.</summary>
public sealed class ResultLine(MergeLine? source, string text)
{
    public MergeLine? Source { get; } = source;
    public string Text { get; set; } = text;

    /// <summary>Text without its line ending, as an editor row shows it.</summary>
    public string Body => MergeText.Body(Text);

    /// <summary>Replaces the text but keeps the current line ending (an edit inside one row). A line without one gets
    /// "\n": every result line ends with one, and BuildResult drops the file's last one when the file had none.</summary>
    public void Edit(string body)
    {
        var ending = Text[MergeText.Body(Text).Length..];
        Text = body + (ending.Length > 0 ? ending : "\n");
    }
}

/// <summary>Chunks. Context chunks have the same list as Ours and Theirs. Result is mutable for editing.</summary>
public sealed class MergeChunk
{
    /// <summary><paramref name="diffOrder"/> = a change chunk's lines in diff order (its result keeps that order).</summary>
    internal MergeChunk(MergeChunkKind kind, List<MergeLine> ours, List<MergeLine> theirs, string? oursLabel = null,
        string? theirsLabel = null, List<MergeLine>? diffOrder = null)
    {
        Kind = kind;
        Ours = ours;
        Theirs = theirs;
        OursLabel = oursLabel;
        TheirsLabel = theirsLabel;
        // Context: all lines; change: the added lines (pre-selected), in diff order; conflict: nothing yet.
        IEnumerable<MergeLine> initial = kind switch
        {
            MergeChunkKind.Context => ours,
            MergeChunkKind.Change => (diffOrder ?? ours.Concat(theirs)).Where(l => l.Kind == MergeLineKind.Added),
            _ => [],
        };
        Result = initial.Select(l => new ResultLine(l, l.Text)).ToList();
    }

    public MergeChunkKind Kind { get; }
    public IReadOnlyList<MergeLine> Ours { get; }
    public IReadOnlyList<MergeLine> Theirs { get; }
    public string? OursLabel { get; }
    public string? TheirsLabel { get; }
    public List<ResultLine> Result { get; }

    public IReadOnlyList<MergeLine> Lines(MergeSide side) => side == MergeSide.Ours ? Ours : Theirs;

    /// <summary>Result and numbering: a conflict chunk is resolved once it has a result line.</summary>
    public bool IsResolved => Kind != MergeChunkKind.Conflict || Result.Count > 0;

    public bool IsSelected(MergeLine line) => Result.Any(r => r.Source == line);
}

/// <summary>
/// The resolver's model: the chunks parsed from the combined diff plus the
/// selection. Failed = the combined diff could not be parsed (the window shows nothing). Chunks is empty in non-text
/// modes and when Failed.
/// </summary>
public sealed class MergeDocument
{
    public MergeDocument(string path, MergeMode mode, IReadOnlyList<MergeChunk>? chunks, bool noNewlineAtEnd)
    {
        Path = path;
        Mode = mode;
        Failed = mode == MergeMode.Text && chunks is null;
        Chunks = chunks ?? [];
        NoNewlineAtEnd = noNewlineAtEnd;
    }

    public string Path { get; }
    public MergeMode Mode { get; }
    public bool Failed { get; }
    public IReadOnlyList<MergeChunk> Chunks { get; }

    /// <summary>The working file does not end with \n: the saved result loses its one trailing newline.</summary>
    public bool NoNewlineAtEnd { get; }

    public IEnumerable<MergeChunk> Conflicts => Chunks.Where(c => c.Kind == MergeChunkKind.Conflict);
    public int ConflictCount => Conflicts.Count();
    public int ResolvedCount => Conflicts.Count(c => c.IsResolved);
    public bool AllResolved => Conflicts.All(c => c.IsResolved);

    /// <summary>Header checkbox state, recomputed on every read: every line of that side
    /// in every conflict chunk is selected and no line of the other side is.</summary>
    public bool TheirsChecked => SideChecked(MergeSide.Theirs);
    public bool OursChecked => SideChecked(MergeSide.Ours);

    bool SideChecked(MergeSide side) =>
        ConflictCount > 0 &&
        Conflicts.All(c => c.Lines(side).All(c.IsSelected)) &&
        !Conflicts.Any(c => c.Lines(Other(side)).Any(c.IsSelected));

    static MergeSide Other(MergeSide s) => s == MergeSide.Ours ? MergeSide.Theirs : MergeSide.Ours;

    /// <summary>Gutter tag click: select (append to the chunk's result) or deselect (drop it with any edited text).
    /// Conflict chunks only.</summary>
    public void ToggleLine(MergeChunk chunk, MergeLine line)
    {
        if (chunk.Kind != MergeChunkKind.Conflict) return;
        if (chunk.Result.RemoveAll(r => r.Source == line) == 0) chunk.Result.Add(new ResultLine(line, line.Text));
    }

    /// <summary>Per-chunk button text: `Remove Left/Right` when this is true, else `Select Left/Right`.</summary>
    public static bool IsSideSelected(MergeChunk chunk, MergeSide side) => chunk.Lines(side).All(chunk.IsSelected);

    /// <summary>Per-chunk button: deselect all of that side's lines and, if it was a select, select them all again in
    /// their original order. The other side's selection stays.</summary>
    public void ToggleChunkSide(MergeChunk chunk, MergeSide side)
    {
        if (chunk.Kind != MergeChunkKind.Conflict) return;
        bool select = !IsSideSelected(chunk, side);
        Deselect(chunk, side);
        if (select) chunk.Result.AddRange(chunk.Lines(side).Select(l => new ResultLine(l, l.Text)));
    }

    /// <summary>Header checkbox: check = select every conflict chunk's lines of that side (as the chunk button does);
    /// uncheck = deselect them. The other side is not touched.</summary>
    public void SetSide(MergeSide side, bool selected)
    {
        foreach (var c in Conflicts)
        {
            Deselect(c, side);
            if (selected) c.Result.AddRange(c.Lines(side).Select(l => new ResultLine(l, l.Text)));
        }
    }

    static void Deselect(MergeChunk chunk, MergeSide side) =>
        chunk.Result.RemoveAll(r => r.Source is { } s && chunk.Lines(side).Contains(s));

    /// <summary>The text to write: every chunk's result lines; without the one trailing newline when the working file had none.</summary>
    public string BuildResult()
    {
        var text = string.Concat(Chunks.SelectMany(c => c.Result).Select(r => r.Text));
        return NoNewlineAtEnd && text.EndsWith('\n') ? text[..^1] : text;
    }

    // ---------------------------------------------------------------- parsing

    /// <summary>Classification of the combined diff: LFS pointer, submodule, binary, else text.</summary>
    public static MergeMode Classify(string diff, bool isSubmodule)
    {
        if (ContentLines(diff).Any(l => l.Length > 2 && l[2..].StartsWith("version https://git-lfs.github.com/spec/v1", StringComparison.Ordinal)))
            return MergeMode.Lfs;
        if (isSubmodule) return MergeMode.Submodule;
        return diff.Split('\n').Any(l => l.StartsWith("Binary files ", StringComparison.Ordinal)) ? MergeMode.Binary : MergeMode.Text;
    }

    /// <summary>Body lines after the first `@@@` header (Line classification 1-2), each with its newline.</summary>
    static IEnumerable<string> ContentLines(string diff)
    {
        bool body = false;
        foreach (var line in SplitKeepEnds(diff))
        {
            if (line.StartsWith("@@@", StringComparison.Ordinal)) { body = true; continue; }
            if (body && !line.StartsWith('\\')) yield return line;
        }
    }

    static IEnumerable<string> SplitKeepEnds(string text)
    {
        int start = 0;
        while (start < text.Length)
        {
            int nl = text.IndexOf('\n', start);
            int end = nl < 0 ? text.Length : nl + 1;
            yield return text[start..end];
            start = end;
        }
    }

    static bool IsPrefix(char c) => c is ' ' or '+' or '-';

    /// <summary>Line classification and "Chunks". Null = parse error (also: no `@@@` header at all).</summary>
    public static IReadOnlyList<MergeChunk>? ParseChunks(string diff)
    {
        if (!SplitKeepEnds(diff).Any(l => l.StartsWith("@@@", StringComparison.Ordinal))) return null;

        var chunks = new List<MergeChunk>();
        List<MergeLine>? context = null;                 // open context chunk
        List<MergeLine>? change = null;                  // open change chunk, diff order
        List<MergeLine>? ours = null, theirs = null;     // open conflict chunk
        string? oursLabel = null;
        var section = Section.Ours;

        foreach (var line in ContentLines(diff))
        {
            if (line.Length < 2 || !IsPrefix(line[0]) || !IsPrefix(line[1])) continue;
            char c1 = line[0], c2 = line[1];
            var text = line[2..];

            // Markers only with the `++` prefix; tested first (`=======` after `|||||||` is the separator).
            if (c1 == '+' && c2 == '+' && MarkerOf(text) is { } marker)
            {
                if (line.Length < 10) return null;
                var label = line[10..].TrimEnd();
                switch (marker)
                {
                    case '<':
                        if (ours is not null) return null;
                        CloseOpen();
                        (ours, theirs, oursLabel, section) = ([], [], label, Section.Ours);
                        break;
                    case '|':
                        if (ours is null || section == Section.Theirs) return null;
                        section = Section.Base;
                        break;
                    case '=':
                        if (ours is null || section == Section.Theirs) return null;
                        section = Section.Theirs;
                        break;
                    default: // '>'
                        if (ours is null || theirs is null || section != Section.Theirs) return null;
                        if (ours.Count == 0) ours.Add(Placeholder(MergeSide.Ours));
                        if (theirs.Count == 0) theirs.Add(Placeholder(MergeSide.Theirs));
                        chunks.Add(new MergeChunk(MergeChunkKind.Conflict, ours, theirs, oursLabel, label));
                        (ours, theirs) = (null, null);
                        break;
                }
                continue;
            }

            if (ours is not null)
            {
                if (section == Section.Ours && c1 != '-') ours.Add(new MergeLine(MergeSide.Ours, MergeLineKind.Conflict, text));
                else if (section == Section.Theirs && c2 != '-') theirs!.Add(new MergeLine(MergeSide.Theirs, MergeLineKind.Conflict, text));
                continue; // base lines and dropped lines
            }

            if (c1 == ' ' && c2 == ' ')
            {
                if (change is not null) CloseOpen();
                (context ??= []).Add(new MergeLine(null, MergeLineKind.Context, text));
                continue;
            }

            if (context is not null) CloseOpen();
            // Checked in this order: a `+` in column 2 wins.
            (change ??= []).Add(
                c2 == '+' ? new MergeLine(MergeSide.Ours, MergeLineKind.Added, text)
                : c1 == '-' ? new MergeLine(MergeSide.Ours, MergeLineKind.Removed, text)
                : c2 == '-' ? new MergeLine(MergeSide.Theirs, MergeLineKind.Removed, text)
                : new MergeLine(MergeSide.Theirs, MergeLineKind.Added, text));
        }
        if (ours is not null) return null; // conflict not closed
        CloseOpen();
        return chunks;

        void CloseOpen()
        {
            if (context is not null) chunks.Add(new MergeChunk(MergeChunkKind.Context, context, context));
            if (change is not null)
                chunks.Add(new MergeChunk(MergeChunkKind.Change,
                    change.Where(l => l.Side == MergeSide.Ours).ToList(),
                    change.Where(l => l.Side == MergeSide.Theirs).ToList(), diffOrder: change));
            (context, change) = (null, null);
        }
    }

    enum Section { Ours, Base, Theirs }

    static MergeLine Placeholder(MergeSide side) => new(side, MergeLineKind.Conflict, "", isPlaceholder: true);

    static char? MarkerOf(string text) =>
        text.StartsWith("<<<<<<<", StringComparison.Ordinal) ? '<'
        : text.StartsWith("=======", StringComparison.Ordinal) ? '='
        : text.StartsWith("|||||||", StringComparison.Ordinal) ? '|'
        : text.StartsWith(">>>>>>>", StringComparison.Ordinal) ? '>'
        : null;
}

static class MergeText
{
    /// <summary>The text without its trailing \n or \r\n.</summary>
    public static string Body(string text) =>
        text.EndsWith("\r\n", StringComparison.Ordinal) ? text[..^2] : text.EndsWith('\n') ? text[..^1] : text;
}
