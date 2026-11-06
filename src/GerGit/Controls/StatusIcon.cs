using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using GerGit.Core;

namespace GerGit.Controls;

/// <summary>
/// Status icons: a 14 x 14 rounded square in the change colour with a white glyph; conflicts are an amber
/// warning triangle with a black "!". Untracked and ignored files use the "added" icon.
/// </summary>
public sealed class StatusIcon : Control
{
    public static readonly StyledProperty<ChangeKind> KindProperty = AvaloniaProperty.Register<StatusIcon, ChangeKind>(nameof(Kind));

    static readonly Geometry Triangle = Geometry.Parse("M 7,0.5 L 13.8,13.5 L 0.2,13.5 Z");

    static StatusIcon() => AffectsRender<StatusIcon>(KindProperty);

    public ChangeKind Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }

    public static string Glyph(ChangeKind k) => k switch
    {
        ChangeKind.Added or ChangeKind.Untracked or ChangeKind.Ignored => "+",
        ChangeKind.Deleted => "−",
        ChangeKind.Renamed => "→",
        ChangeKind.Copied => "++",
        ChangeKind.Conflicted => "!",
        _ => "M", // modified, type changed
    };

    /// <summary>Theme resource of the colour (Colors.axaml "Color.FileStatus.*").</summary>
    public static string BrushKey(ChangeKind k) => "Color.FileStatus." + k switch
    {
        ChangeKind.Added or ChangeKind.Untracked or ChangeKind.Ignored => "Added",
        ChangeKind.Deleted => "Deleted",
        ChangeKind.Renamed => "Renamed",
        ChangeKind.Copied => "Copied",
        ChangeKind.Conflicted => "Conflict",
        _ => "Modified",
    };

    protected override Size MeasureOverride(Size availableSize) => new(14, 14);

    public override void Render(DrawingContext context)
    {
        if (!this.TryFindResource(BrushKey(Kind), ActualThemeVariant, out var res) || res is not IBrush fill) return;
        bool conflict = Kind == ChangeKind.Conflicted;
        if (conflict) context.DrawGeometry(fill, null, Triangle);
        else context.DrawRectangle(fill, null, new Rect(0, 0, 14, 14), 3, 3);
        var text = new FormattedText(Glyph(Kind), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Bold), 10, conflict ? Brushes.Black : Brushes.White);
        context.DrawText(text, new Point((14 - text.Width) / 2, (14 - text.Height) / 2 + (conflict ? 1.5 : 0)));
    }
}
