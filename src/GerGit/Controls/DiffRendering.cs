using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using GerGit.Core;

namespace GerGit.Controls;

// Drawing parts of the diff editor; in the side-by-side layout each
// pane has its own set.
// Document line n (1-based) of the editor is DiffDocument.Lines[n - 1].

/// <summary>Full-width backgrounds of added/removed/alignment lines and the intra-line word ranges on top.</summary>
public sealed class DiffBackgroundRenderer(Func<DiffDocument?> document) : IBackgroundRenderer
{
    public KnownLayer Layer => KnownLayer.Background;

    public void Draw(TextView textView, DrawingContext dc)
    {
        if (document() is not { } doc || !textView.VisualLinesValid) return;
        foreach (var vl in textView.VisualLines)
        {
            int i = vl.FirstDocumentLine.LineNumber - 1;
            if (i >= doc.Lines.Count) continue;
            var line = doc.Lines[i];
            var (lineKey, wordKey) = line.Kind switch
            {
                DocLineKind.Added => ("Color.Diff.AddedLine", "Color.Diff.AddedWords"),
                DocLineKind.Removed => ("Color.Diff.RemovedLine", "Color.Diff.RemovedWords"),
                DocLineKind.Alignment => ("Color.Diff.Filler", null),
                _ => (null, null),
            };
            if (lineKey is null) continue;

            var y = vl.VisualTop - textView.ScrollOffset.Y;
            dc.FillRectangle(Brush(textView, lineKey), new Rect(0, y, textView.Bounds.Width, vl.Height));
            if (wordKey is null) continue;
            var word = Brush(textView, wordKey);
            foreach (var r in line.WordRanges)
            {
                int start = vl.GetVisualColumn(r.Start), end = vl.GetVisualColumn(r.Start + r.Length);
                foreach (var rect in BackgroundGeometryBuilder.GetRectsFromVisualSegment(textView, vl, start, end))
                    dc.FillRectangle(word, rect);
            }
        }
    }

    internal static IBrush Brush(StyledElement e, string key) =>
        e.TryFindResource(key, e.ActualThemeVariant, out var b) && b is IBrush brush ? brush : Brushes.Transparent;
}

/// <summary>Hunk headers and service lines ("changed file mode", "No newline at end of file") in the service colour.</summary>
public sealed class DiffServiceColorizer(Func<DiffDocument?> document) : DocumentColorizingTransformer
{
    protected override void ColorizeLine(AvaloniaEdit.Document.DocumentLine line)
    {
        if (document() is not { } doc || line.LineNumber > doc.Lines.Count || line.Length == 0) return;
        if (doc.Lines[line.LineNumber - 1].Kind is not (DocLineKind.HunkHeader or DocLineKind.Service)) return;
        var brush = DiffBackgroundRenderer.Brush(CurrentContext.TextView, "Color.Diff.ServiceLine");
        ChangeLinePart(line.Offset, line.EndOffset, e => e.TextRunProperties.SetForegroundBrush(brush));
    }
}

/// <summary>Which numbers a gutter shows: both (unified), or one side's (side-by-side panes).</summary>
public enum NumberColumns { Both, Old, New }

/// <summary>
/// Line numbers (gutter), unified: old number centred in the left half, new number right-aligned in the
/// right half, horizontal margin 7, 1 px separator at the right edge; digit width = max(2, digits of the largest number).
/// Side-by-side: one column (old numbers left, new numbers right); the new pane's gutter also has a 1 px line on its
/// left edge. The single number is right-aligned like the unified new column (unverified).
/// </summary>
public sealed class DiffLineNumberMargin(Func<DiffDocument?> document) : AbstractMargin
{
    NumberColumns _columns;

    public NumberColumns Columns
    {
        get => _columns;
        set
        {
            _columns = value;
            InvalidateMeasure();
        }
    }

    const double FontSize = 11, Pad = 7;

    Typeface Typeface => new(this.TryFindResource("Font.Mono", out var f) && f is FontFamily ff ? ff : FontFamily.Default);

    FormattedText Format(string s) => new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface, FontSize,
        DiffBackgroundRenderer.Brush(this, "Color.Diff.LineNumber"));

    double ColumnWidth()
    {
        int max = document()?.Lines.Select(l => Math.Max(l.OldNumber ?? 0, l.NewNumber ?? 0)).DefaultIfEmpty(0).Max() ?? 0;
        int digits = Math.Max(2, max.ToString(CultureInfo.InvariantCulture).Length);
        return Format(new string('9', digits)).WidthIncludingTrailingWhitespace + 2 * Pad;
    }

    double _col; // width of one number column, set on measure (the view re-measures when the document changes)

    protected override Size MeasureOverride(Size availableSize)
    {
        _col = ColumnWidth();
        double width = Columns switch { NumberColumns.Both => 2 * _col + 1, NumberColumns.Old => _col + 1, _ => _col + 2 };
        return document() is { Lines.Count: > 0 } ? new Size(width, 0) : new Size(0, 0);
    }

    protected override void OnTextViewChanged(TextView? oldTextView, TextView? newTextView)
    {
        if (oldTextView is not null) oldTextView.VisualLinesChanged -= OnVisualLinesChanged;
        base.OnTextViewChanged(oldTextView, newTextView);
        if (newTextView is not null) newTextView.VisualLinesChanged += OnVisualLinesChanged;
    }

    void OnVisualLinesChanged(object? sender, EventArgs e) => InvalidateVisual();

    public override void Render(DrawingContext dc)
    {
        if (document() is not { } doc || TextView is not { VisualLinesValid: true } tv) return;
        double col = _col;
        foreach (var vl in tv.VisualLines)
        {
            int i = vl.FirstDocumentLine.LineNumber - 1;
            if (i >= doc.Lines.Count) continue;
            var y = vl.VisualTop - tv.ScrollOffset.Y + (vl.Height - Format("0").Height) / 2;
            var (o, n) = (doc.Lines[i].OldNumber, doc.Lines[i].NewNumber);
            if (Columns == NumberColumns.Both)
            {
                if (o is not null) Draw(o.Value, w => (col - w) / 2);
                if (n is not null) Draw(n.Value, w => 2 * col - Pad - w);
            }
            else if ((Columns == NumberColumns.Old ? o : n) is { } number)
                Draw(number, w => Bounds.Width - 1 - Pad - w);

            // x from the text width
            void Draw(int number, Func<double, double> x)
            {
                var t = Format(number.ToString(CultureInfo.InvariantCulture));
                dc.DrawText(t, new Point(x(t.Width), y));
            }
        }
        var line = DiffBackgroundRenderer.Brush(this, "Color.Diff.GutterLine");
        dc.FillRectangle(line, new Rect(Bounds.Width - 1, 0, 1, Bounds.Height));
        if (Columns == NumberColumns.New) dc.FillRectangle(line, new Rect(0, 0, 1, Bounds.Height));
    }
}

/// <summary>
/// The group under the mouse: 1 px outline (radius 2) from x = 2 to the right edge plus a tint over the
/// hovered staging group (inclusive document line indices).
/// </summary>
public sealed class DiffHoverRenderer(Func<(int First, int Last)?> group) : IBackgroundRenderer
{
    public KnownLayer Layer => KnownLayer.Background;

    public void Draw(TextView textView, DrawingContext dc)
    {
        if (group() is not { } g || !textView.VisualLinesValid) return;
        double top = textView.GetVisualTopByDocumentLine(g.First + 1) - textView.ScrollOffset.Y;
        double bottom = textView.GetVisualTopByDocumentLine(g.Last + 1) + textView.DefaultLineHeight - textView.ScrollOffset.Y;
        var pen = new Pen(DiffBackgroundRenderer.Brush(textView, "Color.Diff.ChunkBorder"), 1);
        // Half-pixel inset: the 1 px pen sits on whole pixels.
        dc.DrawRectangle(DiffBackgroundRenderer.Brush(textView, "Color.Diff.ChunkFill"), pen,
            new Rect(2.5, top + 0.5, Math.Max(0, textView.Bounds.Width - 3), Math.Max(0, bottom - top - 1)), 2, 2);
    }
}
