using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using GerGit.Core.Rebase;

namespace GerGit.Controls;

/// <summary>
/// The graph glyph of an interactive-rebase row, drawn in a 22 x 22 cell (centre x = 11,
/// line 1.4): Start / Middle dots on a vertical line, Through = line only, Target = a bent arrow whose head may reach
/// past the cell (tip at x 28). Colour = <see cref="Foreground"/> (the grey muted text colour).
/// </summary>
public sealed class RebaseGlyphView : Control
{
    public static readonly StyledProperty<RebaseGlyph> GlyphProperty = AvaloniaProperty.Register<RebaseGlyphView, RebaseGlyph>(nameof(Glyph));
    public static readonly StyledProperty<IBrush?> ForegroundProperty = AvaloniaProperty.Register<RebaseGlyphView, IBrush?>(nameof(Foreground));

    // Arc radius 7.75 so the arc ends at the head's base (x 18.5, y 10.8).
    static readonly Geometry Arrow = Geometry.Parse("M 10.75 0 L 10.75 3.05 A 7.75 7.75 0 0 0 18.5 10.8");
    static readonly Geometry Head = Geometry.Parse("M 18.5 7.6 L 28 10.8 L 18.5 14 Z");

    static RebaseGlyphView() => AffectsRender<RebaseGlyphView>(GlyphProperty, ForegroundProperty);

    public RebaseGlyphView()
    {
        Width = 22;
        Height = 22;
        ClipToBounds = false;
    }

    public RebaseGlyph Glyph { get => GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    public override void Render(DrawingContext ctx)
    {
        var brush = Foreground ?? Brushes.Gray;
        var pen = new ImmutablePen(brush.ToImmutable(), 1.4);
        switch (Glyph)
        {
            case RebaseGlyph.Start:
                ctx.DrawLine(pen, new Point(11, 11), new Point(11, 22));
                ctx.DrawEllipse(brush, null, new Point(11, 11), 3, 3);
                break;
            case RebaseGlyph.Middle:
                ctx.DrawLine(pen, new Point(11, 0), new Point(11, 22));
                ctx.DrawEllipse(brush, null, new Point(11, 11), 3, 3);
                break;
            case RebaseGlyph.Through:
                ctx.DrawLine(pen, new Point(11, 0), new Point(11, 22));
                break;
            case RebaseGlyph.Target:
                ctx.DrawGeometry(null, new ImmutablePen(brush.ToImmutable(), 1.5), Arrow);
                ctx.DrawGeometry(brush, null, Head);
                break;
        }
    }
}
