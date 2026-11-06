using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using GerGit.Core.Conflicts;

namespace GerGit.Controls;

// Drawing parts of the resolver panes. Document line n (1-based) of a pane is rows[n - 1].
// Colours are the theme's Color.Merge.* / Color.Diff.* resources, read while drawing.

/// <summary>Line colours: conflict lines per side, auto-merged added/removed lines, alignment filler rows and
/// the unresolved conflict rows of the result pane.</summary>
public sealed class MergeBackgroundRenderer(Func<IReadOnlyList<MergeRow>> rows) : IBackgroundRenderer
{
    public KnownLayer Layer => KnownLayer.Background;

    /// <summary>The colour resource of a row's background; null = none.</summary>
    public static string? BrushKeyOf(MergeRow row) => row.Kind switch
    {
        MergeRowKind.Filler => "Color.Diff.Filler",
        MergeRowKind.Unresolved => "Color.Merge.Unresolved",
        _ => (row.Line ?? row.Result?.Source) switch
        {
            { Kind: MergeLineKind.Conflict, Side: MergeSide.Theirs } => "Color.Merge.Theirs",
            { Kind: MergeLineKind.Conflict } => "Color.Merge.Ours",
            { Kind: MergeLineKind.Added } => "Color.Merge.Added",
            { Kind: MergeLineKind.Removed } => "Color.Merge.Removed",
            _ => null, // context and inserted lines
        },
    };

    public void Draw(TextView textView, DrawingContext dc)
    {
        var list = rows();
        if (!textView.VisualLinesValid) return;
        foreach (var vl in textView.VisualLines)
        {
            int i = vl.FirstDocumentLine.LineNumber - 1;
            if (i >= list.Count || BrushKeyOf(list[i]) is not { } key) continue;
            dc.FillRectangle(DiffBackgroundRenderer.Brush(textView, key), new Rect(0, vl.VisualTop - textView.ScrollOffset.Y, textView.Bounds.Width, vl.Height));
        }
    }
}

/// <summary>
/// Line-number gutter: monospace 11 px, Color.Merge.LineNumber, width = '9' x max(2, digits) + 20, 1 px separator
/// at the right edge; numbers only on numbered rows. Side panes (<paramref name="toggle"/> set) also draw the tag over a
/// conflict row that is hovered (Color.Merge.HoverTag) or selected (Color.Merge.SelectedTag), with a white number, and
/// toggle the row on click.
/// </summary>
public sealed class MergeGutter(Func<IReadOnlyList<MergeRow>> rows, Func<int?> hoverRow, Func<MergeRow, bool> isSelected,
    Action<MergeRow>? toggle) : AbstractMargin
{
    const double FontSize = 11, Pad = 10;

    IBrush B(string key) => DiffBackgroundRenderer.Brush(this, key);

    /// <summary>A row the gutter tag applies to: a conflict line or the `--- empty ---` placeholder of a side pane.</summary>
    public static bool IsTaggable(MergeRow row) => row.Line is { Kind: MergeLineKind.Conflict };

    Typeface Typeface => new(this.TryFindResource("Font.Mono", out var f) && f is FontFamily ff ? ff : FontFamily.Default);

    FormattedText Format(string s, IBrush brush) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface, FontSize, brush);

    protected override Size MeasureOverride(Size availableSize)
    {
        int max = rows().Select(r => r.Number ?? 0).DefaultIfEmpty(0).Max();
        int digits = Math.Max(2, max.ToString(CultureInfo.InvariantCulture).Length);
        return new Size(Format(new string('9', digits), Brushes.White).WidthIncludingTrailingWhitespace + 2 * Pad, 0);
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
        var list = rows();
        if (TextView is not { VisualLinesValid: true } tv) return;
        double w = Bounds.Width;
        int? hover = hoverRow();
        var number = B("Color.Merge.LineNumber");
        foreach (var vl in tv.VisualLines)
        {
            int i = vl.FirstDocumentLine.LineNumber - 1;
            if (i >= list.Count) continue;
            var row = list[i];
            double top = vl.VisualTop - tv.ScrollOffset.Y;
            IBrush? tag = toggle is null || !IsTaggable(row) ? null
                : isSelected(row) ? B("Color.Merge.SelectedTag")
                : hover == i ? B("Color.Merge.HoverTag") : null;
            if (tag is not null)
            {
                // Right-pointing tag: 16 px high, width gutter - 3, 5 px point.
                double y0 = top + (vl.Height - 16) / 2, right = w - 3;
                var g = new StreamGeometry();
                using (var c = g.Open())
                {
                    c.BeginFigure(new Point(0, y0), true);
                    c.LineTo(new Point(right - 5, y0));
                    c.LineTo(new Point(right, y0 + 8));
                    c.LineTo(new Point(right - 5, y0 + 16));
                    c.LineTo(new Point(0, y0 + 16));
                    c.EndFigure(true);
                }
                dc.DrawGeometry(tag, null, g);
            }
            if (row.Number is { } n)
            {
                var t = Format(n.ToString(CultureInfo.InvariantCulture), tag is null ? number : Brushes.White);
                dc.DrawText(t, new Point(w - Pad - t.Width, top + (vl.Height - t.Height) / 2));
            }
        }
        dc.FillRectangle(DiffBackgroundRenderer.Brush(this, "Color.Diff.GutterLine"), new Rect(w - 1, 0, 1, Bounds.Height));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (toggle is null || TextView is not { } tv || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var list = rows();
        tv.EnsureVisualLines();
        var line = tv.GetDocumentLineByVisualTop(e.GetPosition(this).Y + tv.ScrollOffset.Y);
        if (line is null || line.LineNumber > list.Count || !IsTaggable(list[line.LineNumber - 1])) return;
        e.Handled = true; // no caret move or selection
        toggle(list[line.LineNumber - 1]);
    }
}
