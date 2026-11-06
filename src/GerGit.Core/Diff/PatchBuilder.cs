using System.Text;

namespace GerGit.Core.Diff;

/// <summary>Building the patch from a set of lines: a patch for git apply from selected diff lines.</summary>
public static class PatchBuilder
{
    const string NoNewline = "\\ No newline at end of file\n";

    /// <summary>Null when there is nothing to apply (no remaining +/- line, binary or submodule diff).</summary>
    // Limitation: the patch is built from UTF-8 decoded text (DiffLine.Text) and sent as a UTF-8 string, so bytes of a
    // non-UTF-8 file are lost (U+FFFD); the UI hides the line buttons then. Byte-exact patches come later.
    public static string? Build(FileDiff diff, IReadOnlyCollection<HunkLine> selected, PatchAction action)
    {
        if (diff.IsBinary || diff.IsSubmodule) return null;
        var sel = selected as IReadOnlySet<HunkLine> ?? selected.ToHashSet();

        var body = new StringBuilder();
        // A line that loses its "no newline" state gets the file's line ending.
        // Limitation: a diff without any line ending (one-line "o0" → "n0", both without EOL) cannot show CRLF, so such a
        // line gets "\n"; reading the file's other lines would fix it.
        bool crlf = diff.Hunks.Any(hunk => hunk.Lines.Any(l => l.Text.EndsWith('\r')));
        for (int h = 0; h < diff.Hunks.Count; h++) AppendHunk(body, diff.Hunks[h], h, sel, action, crlf);
        if (body.Length == 0) return null;

        return Header(diff, sel, action) + body;
    }

    // Design choice: no `index` line (the blob ids do not describe a partial result) and no mail-style trailer.
    static string Header(FileDiff diff, IReadOnlySet<HunkLine> sel, PatchAction action)
    {
        string path = diff.NewPath ?? diff.OldPath!;
        bool created = diff.OldPath is null, deleted = diff.NewPath is null;
        bool allDeletedSelected = diff.Hunks.SelectMany((hunk, h) => hunk.Lines.Select((l, i) => (l, i, h)))
            .All(x => x.l.Kind != DiffLineKind.Removed || sel.Contains(new HunkLine(x.h, x.i)));

        // The header table: which side of the result exists for the target.
        (string? Old, string? New, string? ModeLine) side = action switch
        {
            PatchAction.Stage when created => (null, path, $"new file mode {diff.NewMode ?? 100644}"),
            PatchAction.Stage when deleted && allDeletedSelected => (path, null, $"deleted file mode {diff.OldMode ?? 100644}"),
            PatchAction.Unstage when deleted => (path, null, $"deleted file mode {diff.OldMode ?? 100644}"),
            PatchAction.Discard when deleted => (null, path, $"new file mode {diff.OldMode ?? 100644}"),
            _ => (path, path, null), // modification of the new path (also new-in-index unstage, untracked discard)
        };

        var sb = new StringBuilder($"diff --git {Quote("a/" + path)} {Quote("b/" + path)}\n");
        if (side.ModeLine is not null) sb.Append(side.ModeLine).Append('\n');
        sb.Append("--- ").Append(side.Old is null ? "/dev/null" : Quote("a/" + side.Old)).Append('\n');
        sb.Append("+++ ").Append(side.New is null ? "/dev/null" : Quote("b/" + side.New)).Append('\n');
        return sb.ToString();
    }

    enum Phase { Leading, Deleted, Added, Trailing }

    /// <summary>Splits the hunk into blocks (leading context, deleted run, added run, trailing context) and maps every
    /// line to A/B/C/D by the header table; writes nothing when no +/- line remains.</summary>
    static void AppendHunk(StringBuilder body, DiffHunk hunk, int h, IReadOnlySet<HunkLine> sel, PatchAction action, bool crlf)
    {
        var output = new List<(char Prefix, DiffLine Line)>();
        bool anyChange = false;
        List<(char Prefix, DiffLine Line)> a = [], b = [], c = [], d = [];
        var phase = Phase.Leading;

        for (int i = 0; i < hunk.Lines.Count; i++)
        {
            var line = hunk.Lines[i];
            bool isSel = sel.Contains(new HunkLine(h, i));
            switch (line.Kind)
            {
                case DiffLineKind.Context:
                    if (phase == Phase.Leading) a.Add((' ', line));
                    else
                    {
                        phase = Phase.Trailing;
                        (b.Count > 0 || c.Count > 0 ? d : a).Add((' ', line));
                    }
                    break;
                case DiffLineKind.Removed:
                    if (phase is Phase.Added or Phase.Trailing) Flush();
                    phase = Phase.Deleted;
                    if (isSel) Change(action == PatchAction.Discard ? c : b, action == PatchAction.Discard ? '+' : '-', line);
                    else if (action == PatchAction.Stage) AddContext(line);
                    break;
                case DiffLineKind.Added:
                    if (phase == Phase.Trailing) Flush();
                    phase = Phase.Added;
                    if (isSel) Change(action == PatchAction.Discard ? b : c, action == PatchAction.Discard ? '-' : '+', line);
                    else if (action != PatchAction.Stage) AddContext(line);
                    break;
            }
        }
        Flush();
        if (!anyChange) return;

        // "No newline at end of file" : a line lacks its newline on
        // a patch side only when it is the last line of that side and its source line had the marker. A marker anywhere
        // else makes git join two lines. A context line whose sides differ is written as a -/+ pair.
        int lastOld = output.FindLastIndex(x => x.Prefix != '+'), lastNew = output.FindLastIndex(x => x.Prefix != '-');
        var lines = new StringBuilder();
        int fromLen = 0, toLen = 0;
        for (int i = 0; i < output.Count; i++)
        {
            var (prefix, line) = output[i];
            bool oldNoEol = prefix != '+' && i == lastOld && line.NoNewlineAtEnd;
            bool newNoEol = prefix != '-' && i == lastNew && line.NoNewlineAtEnd;
            if (prefix == ' ' && oldNoEol != newNoEol)
            {
                Write('-', line, oldNoEol);
                Write('+', line, newNoEol);
            }
            else Write(prefix, line, oldNoEol || newNoEol);
        }

        // Start numbers copied from the original header, lengths recomputed, no function context.
        // A start of 0 (empty side) with a now non-empty side becomes 1; git rejects "-0,5" ("patch failed: x:0").
        int oldStart = hunk.OldStart == 0 && fromLen > 0 ? 1 : hunk.OldStart;
        int newStart = hunk.NewStart == 0 && toLen > 0 ? 1 : hunk.NewStart;
        body.Append($"@@ -{oldStart},{fromLen} +{newStart},{toLen} @@\n").Append(lines);

        void AddContext(DiffLine line) => (b.Count == 0 && c.Count == 0 ? a : d).Add((' ', line));

        // Extension of the header table for non-contiguous selections: the side that must match the target (old side for
        // stage/discard, new side for the reversed unstage) keeps source order only if a line for B (C) that comes after
        // a line already in D starts a new block. A contiguous selection never gets here, so the common case is unchanged.
        void Change(List<(char, DiffLine)> list, char prefix, DiffLine line)
        {
            if (d.Count > 0 && list == (action == PatchAction.Unstage ? c : b)) Flush();
            list.Add((prefix, line));
        }

        void Flush()
        {
            anyChange |= b.Count > 0 || c.Count > 0;
            output.AddRange(a.Concat(b).Concat(c).Concat(d));
            a.Clear(); b.Clear(); c.Clear(); d.Clear();
        }

        void Write(char prefix, DiffLine line, bool noEol)
        {
            if (prefix != '+') fromLen++;
            if (prefix != '-') toLen++;
            // Text keeps a trailing \r of CRLF files, so the bytes match the target. A line that gains a newline is
            // always on the result side only (its target side keeps the marker), so the added \r cannot break matching.
            lines.Append(prefix).Append(line.Text);
            if (crlf && !noEol && line.NoNewlineAtEnd && !line.Text.EndsWith('\r')) lines.Append('\r');
            lines.Append('\n');
            if (noEol) lines.Append(NoNewline);
        }
    }

    /// <summary>C-style quoting as git writes it, only when the name has a space, quote, backslash or control character.</summary>
    static string Quote(string name)
    {
        if (!name.Any(ch => ch is < ' ' or '\x7f' or '"' or '\\' or ' ')) return name;
        var sb = new StringBuilder("\"");
        foreach (var ch in name)
            sb.Append(ch switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\t' => "\\t",
                '\n' => "\\n",
                < ' ' or '\x7f' => "\\" + Convert.ToString(ch, 8).PadLeft(3, '0'),
                _ => ch.ToString(),
            });
        return sb.Append('"').ToString();
    }
}
