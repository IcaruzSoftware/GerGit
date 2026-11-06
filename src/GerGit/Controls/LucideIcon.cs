using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace GerGit.Controls;

/// <summary>
/// Draws a Lucide icon from the resource "Icon.&lt;Icon&gt;" (Assets/Icons.axaml): the 24x24 geometry is
/// stroked with width 2 and round caps/joins, scaled to <see cref="Size"/>. Foreground inherits like text.
/// </summary>
public sealed class LucideIcon : Control
{
    public static readonly StyledProperty<string?> IconProperty =
        AvaloniaProperty.Register<LucideIcon, string?>(nameof(Icon));

    public static readonly StyledProperty<double> SizeProperty =
        AvaloniaProperty.Register<LucideIcon, double>(nameof(Size), 16);

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<LucideIcon>();

    static LucideIcon()
    {
        AffectsRender<LucideIcon>(IconProperty, ForegroundProperty);
        AffectsMeasure<LucideIcon>(SizeProperty);
    }

    /// <summary>Lucide icon name, e.g. "git-branch".</summary>
    public string? Icon { get => GetValue(IconProperty); set => SetValue(IconProperty, value); }

    public double Size { get => GetValue(SizeProperty); set => SetValue(SizeProperty, value); }

    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    public override void Render(DrawingContext context)
    {
        if (Icon is null || Foreground is null
            || !this.TryFindResource("Icon." + Icon, ActualThemeVariant, out var res) || res is not Geometry geometry)
            return;
        var scale = Size / 24;
        using (context.PushTransform(Matrix.CreateScale(scale, scale)))
            context.DrawGeometry(null, new Pen(Foreground, 2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), geometry);
    }
}
