namespace GerGit.Core.Diff;

/// <summary>Unified ("Split") layout document + intra-line word highlighting, and the side-by-side pair made from it.</summary>
public static class DiffDocumentBuilder
{
    public const string NoNewlineText = "No newline at end of file";

    public static DiffDocument Build(FileDiff diff, bool entireFile)
    {
        var lines = new List<DocLine>();
        var groups = new List<DocChangeGroup>();

        if (diff.OldMode is { } om && diff.NewMode is { } nm && om != nm)
            lines.Add(Service($" changed file mode {om:D6} → {nm:D6}"));

        for (int h = 0; h < diff.Hunks.Count; h++)
        {
            var hunk = diff.Hunks[h];
            if (!entireFile)
                lines.Add(new DocLine(DocLineKind.HunkHeader,
                    $"@@ -{hunk.OldStart},{hunk.OldLength} +{hunk.NewStart},{hunk.NewLength} @@" + (hunk.Context is { } c ? " " + c : ""),
                    null, null, []));

            // Change group: leading context, deleted lines, added lines, trailing context.
            // A change after trailing context starts the next group. The lists hold indices into hunk.Lines (DocLine.Line).
            var lead = new List<int>();
            var removed = new List<int>();
            var added = new List<int>();
            var trail = new List<int>();
            for (int i = 0; i < hunk.Lines.Count; i++)
            {
                var kind = hunk.Lines[i].Kind;
                if (kind == DiffLineKind.Context)
                    (removed.Count + added.Count == 0 ? lead : trail).Add(i);
                else
                {
                    if (trail.Count > 0) Flush(lines, groups, h, hunk.Lines, lead, removed, added, trail);
                    (kind == DiffLineKind.Removed ? removed : added).Add(i);
                }
            }
            Flush(lines, groups, h, hunk.Lines, lead, removed, added, trail);
        }
        return new DiffDocument(lines, groups);
    }

    /// <summary>
    /// Side-by-side layout, made from the unified document: each run of deleted and added lines (one change
    /// group, with the "No newline" lines that follow them) goes to its side, and the shorter side is padded with
    /// alignment lines. Everything else (headers, context, mode line) is in both. Word ranges carry over: line i of
    /// the deleted block sits next to line i of the added block.
    /// </summary>
    public static SideBySideDocument SideBySide(DiffDocument unified)
    {
        List<DocLine> left = [], right = [], removed = [], added = [];
        var groups = new List<DocChangeGroup>();
        var last = DocLineKind.Context;
        foreach (var line in unified.Lines)
        {
            switch (line.Kind)
            {
                case DocLineKind.Removed:
                case DocLineKind.Service when last == DocLineKind.Removed:
                    removed.Add(line);
                    break;
                case DocLineKind.Added:
                case DocLineKind.Service when last == DocLineKind.Added:
                    added.Add(line);
                    break;
                default:
                    FlushRun();
                    left.Add(line);
                    right.Add(line);
                    break;
            }
            if (line.Kind != DocLineKind.Service) last = line.Kind;
        }
        FlushRun();
        return new(new DiffDocument(left, groups), new DiffDocument(right, groups));

        void FlushRun()
        {
            if (removed.Count + added.Count == 0) return;
            groups.Add(new DocChangeGroup(left.Count, removed.Count(l => l.Kind == DocLineKind.Removed),
                added.Count(l => l.Kind == DocLineKind.Added)));
            int rows = Math.Max(removed.Count, added.Count);
            left.AddRange(removed);
            right.AddRange(added);
            left.AddRange(Enumerable.Repeat(Alignment, rows - removed.Count));
            right.AddRange(Enumerable.Repeat(Alignment, rows - added.Count));
            removed.Clear();
            added.Clear();
        }
    }

    static readonly DocLine Alignment = new(DocLineKind.Alignment, "", null, null, []);

    static void Flush(List<DocLine> doc, List<DocChangeGroup> groups, int h, IReadOnlyList<DiffLine> src,
        List<int> lead, List<int> removed, List<int> added, List<int> trail)
    {
        foreach (var l in lead) Emit(doc, h, l, src[l], []);

        // Word highlighting only when both blocks have the same non-zero line count; line i pairs with line i.
        var words = removed.Count > 0 && removed.Count == added.Count
            ? removed.Zip(added, (r, a) => WordDiff.Compute(Text(src[r]), Text(src[a]))).ToList()
            : null;

        if (removed.Count + added.Count > 0) groups.Add(new DocChangeGroup(doc.Count, removed.Count, added.Count));
        for (int i = 0; i < removed.Count; i++) Emit(doc, h, removed[i], src[removed[i]], words?[i].Old ?? []);
        for (int i = 0; i < added.Count; i++) Emit(doc, h, added[i], src[added[i]], words?[i].New ?? []);
        foreach (var l in trail) Emit(doc, h, l, src[l], []);

        lead.Clear(); removed.Clear(); added.Clear(); trail.Clear();
    }

    static void Emit(List<DocLine> doc, int h, int index, DiffLine l, IReadOnlyList<TextRange> ranges)
    {
        var kind = l.Kind switch
        {
            DiffLineKind.Added => DocLineKind.Added,
            DiffLineKind.Removed => DocLineKind.Removed,
            _ => DocLineKind.Context,
        };
        doc.Add(new DocLine(kind, Text(l).Replace('\r', '\u240D'), l.OldNumber, l.NewNumber, ranges, h, index));
        if (l.NoNewlineAtEnd) doc.Add(Service(NoNewlineText)); // also after context lines
    }

    // Encoding and line endings: the trailing \r of CRLF files is not shown (it stays in DiffLine).
    // A lone \r inside the line would start a new line in the editor (AvaloniaEdit splits on \r and \n only) and shift
    // backgrounds and numbers, so Emit shows it as ␍. One char for one: the word ranges, computed on the real text, stay valid.
    static string Text(DiffLine l) => l.Text.EndsWith('\r') ? l.Text[..^1] : l.Text;

    static DocLine Service(string text) => new(DocLineKind.Service, text, null, null, []);
}
