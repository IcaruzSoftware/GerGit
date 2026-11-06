using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace GerGit.Controls;

/// <summary>
/// Timeline strip (Blame and File History): top 16 px = one thin line per commit at its author date, the
/// selected commit(s) in the accent colour with a small triangle and a full-height line; below a 30 px month/year
/// ruler whose detail depends on the zoom. Drawn only with 2 or more commits. Not interactive.
/// </summary>
public sealed class Timeline : Control
{
    public static readonly StyledProperty<IReadOnlyList<DateTimeOffset>?> DatesProperty =
        AvaloniaProperty.Register<Timeline, IReadOnlyList<DateTimeOffset>?>(nameof(Dates));
    public static readonly StyledProperty<IReadOnlyList<DateTimeOffset>?> SelectedProperty =
        AvaloniaProperty.Register<Timeline, IReadOnlyList<DateTimeOffset>?>(nameof(Selected));

    const double Bars = 16;

    static Timeline() => AffectsRender<Timeline>(DatesProperty, SelectedProperty);

    public Timeline() => ActualThemeVariantChanged += (_, _) => InvalidateVisual(); // colours are read while drawing

    public IReadOnlyList<DateTimeOffset>? Dates { get => GetValue(DatesProperty); set => SetValue(DatesProperty, value); }
    public IReadOnlyList<DateTimeOffset>? Selected { get => GetValue(SelectedProperty); set => SetValue(SelectedProperty, value); }

    /// <summary>The axis: first day of the oldest month to the first day of the month after the newest (local time).</summary>
    public static (DateTime Start, DateTime End) Range(IReadOnlyList<DateTimeOffset> dates)
    {
        var min = dates.Min().LocalDateTime;
        var max = dates.Max().LocalDateTime;
        return (new DateTime(min.Year, min.Month, 1), new DateTime(max.Year, max.Month, 1).AddMonths(1));
    }

    IBrush B(string key) => this.TryFindResource(key, ActualThemeVariant, out var v) && v is IBrush b ? b : Brushes.Gray;

    public override void Render(DrawingContext ctx)
    {
        double w = Bounds.Width, h = Bounds.Height;
        ctx.FillRectangle(B("Color.List.Bg"), new Rect(0, 0, w, Bars));
        if (Dates is not { Count: >= 2 } dates || w <= 0) return;

        var (start, end) = Range(dates);
        double hours = (end - start).TotalHours;
        double X(DateTime d) => (d - start).TotalHours / hours * w;
        double perPixel = hours / w;

        var face = new Typeface(this.TryFindResource("Font.UI", ActualThemeVariant, out var f) && f is FontFamily ff ? ff : FontFamily.Default);
        var label = B("Color.Timeline.Label");
        var tick = new Pen(B("Color.Timeline.Tick"), 1);
        FormattedText Text(string s) => new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, 9, label);

        // Ruler (below the bars) by zoom: months with M/YYYY, year bands with month numbers, or year labels only.
        for (var m = start; m < end; m = m.AddMonths(1))
        {
            double x = Math.Round(X(m)) + 0.5, next = X(m.AddMonths(1));
            if (perPixel < 10)
            {
                ctx.DrawLine(tick, new Point(x, Bars), new Point(x, h));
                ctx.DrawText(Text($"{m.Month}/{m.Year}"), new Point(x + 2, Bars + 2));
            }
            else if (perPixel < 60)
            {
                if (m.Month == 1 || m == start)
                {
                    var yearEnd = new DateTime(m.Year + 1, 1, 1);
                    double right = X(yearEnd < end ? yearEnd : end);
                    if (m.Year % 2 == 0) ctx.FillRectangle(B("Color.Timeline.AltBand"), new Rect(X(m), Bars, right - X(m), 14));
                    ctx.DrawText(Text(m.Year.ToString(CultureInfo.InvariantCulture)), new Point(x + 2, Bars + 1));
                }
                ctx.DrawLine(tick, new Point(x, Bars + 14), new Point(x, h));
                var month = Text(m.Month.ToString(CultureInfo.InvariantCulture));
                if (next - x > month.Width + 2) ctx.DrawText(month, new Point(x + 2, Bars + 16));
            }
            else
            {
                bool year = m.Month == 1;
                ctx.DrawLine(tick, new Point(x, year ? Bars : h - 6), new Point(x, h));
                if (year) ctx.DrawText(Text(m.Year.ToString(CultureInfo.InvariantCulture)), new Point(x + 2, Bars + 2));
            }
        }

        var commit = new Pen(B("Color.Timeline.Commit"), 1);
        foreach (var d in dates)
        {
            double x = Math.Round(X(d.LocalDateTime)) + 0.5;
            ctx.DrawLine(commit, new Point(x, 0), new Point(x, Bars));
        }

        var accent = B("Color.Accent.Base");
        var accentPen = new Pen(accent, 1);
        foreach (var d in Selected ?? [])
        {
            double x = Math.Round(X(d.LocalDateTime)) + 0.5;
            ctx.DrawLine(accentPen, new Point(x, 0), new Point(x, h));
            var triangle = new StreamGeometry();
            using (var g = triangle.Open())
            {
                g.BeginFigure(new Point(x - 4, 0), true);
                g.LineTo(new Point(x + 4, 0));
                g.LineTo(new Point(x, 5));
                g.EndFigure(true);
            }
            ctx.DrawGeometry(accent, null, triangle);
        }
    }
}
