namespace GerGit.Core.Conflicts;

/// <summary>Line = a real line (numbered); Empty = the `--- empty ---` placeholder (side panes: selectable, not numbered;
/// result pane: a selected placeholder, text "", not numbered, not editable);
/// Filler = an alignment row; Unresolved = a result row of a conflict chunk without result lines (the first one
/// reads `--- Merge Conflict ---`).</summary>
public enum MergeRowKind { Line, Empty, Filler, Unresolved }

/// <summary>One row of a pane. Text has no line ending. Number = 1-based line number in that pane (null if not
/// numbered). Line = the side line (theirs/ours panes, also context lines); Result = the result line (result pane).</summary>
public sealed record MergeRow(int Chunk, MergeRowKind Kind, string Text, int? Number, MergeLine? Line = null, ResultLine? Result = null);

/// <summary>Result and numbering: the three panes, every chunk with the same row count in each.</summary>
public sealed record MergeLayout(IReadOnlyList<MergeRow> Theirs, IReadOnlyList<MergeRow> Result, IReadOnlyList<MergeRow> Ours)
{
    public const string EmptyText = "--- empty ---";
    public const string ConflictText = "--- Merge Conflict ---";

    public static MergeLayout Build(MergeDocument doc)
    {
        List<MergeRow> theirs = [], result = [], ours = [];
        int nTheirs = 0, nResult = 0, nOurs = 0;
        for (int i = 0; i < doc.Chunks.Count; i++)
        {
            var c = doc.Chunks[i];
            int rows = Math.Max(Math.Max(c.Theirs.Count, c.Ours.Count), Math.Max(c.Result.Count, 1));
            Side(c.Theirs, theirs, ref nTheirs);
            Side(c.Ours, ours, ref nOurs);
            // A selected `--- empty ---` placeholder adds nothing to the file: an unnumbered, protected row.
            foreach (var r in c.Result)
                result.Add(r.Source?.IsPlaceholder == true
                    ? new MergeRow(i, MergeRowKind.Empty, "", null, Result: r)
                    : new MergeRow(i, MergeRowKind.Line, r.Body, ++nResult, Result: r));
            bool unresolved = !c.IsResolved;
            for (int k = c.Result.Count; k < rows; k++)
                result.Add(unresolved
                    ? new MergeRow(i, MergeRowKind.Unresolved, k == 0 ? ConflictText : "", null)
                    : new MergeRow(i, MergeRowKind.Filler, "", null));

            void Side(IReadOnlyList<MergeLine> lines, List<MergeRow> pane, ref int n)
            {
                foreach (var l in lines)
                    pane.Add(l.IsPlaceholder
                        ? new MergeRow(i, MergeRowKind.Empty, EmptyText, null, l)
                        : new MergeRow(i, MergeRowKind.Line, MergeText.Body(l.Text), ++n, l));
                for (int k = lines.Count; k < rows; k++) pane.Add(new MergeRow(i, MergeRowKind.Filler, "", null));
            }
        }
        return new MergeLayout(theirs, result, ours);
    }
}
