using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using GerGit.Hosting;

namespace GerGit.Controls;

/// <summary>
/// Host / account icon: the filled brand logo of the host family from Assets/BrandIcons.axaml
/// (GitHub; GitLab and GitLab Server; Bitbucket), else the stroked Lucide `server` glyph (custom hosts, Azure,
/// Visual Studio). Scaled from 24 px to <see cref="Size"/>; Foreground inherits like text.
/// </summary>
public sealed class HostIcon : Control
{
    public static readonly StyledProperty<HostType> TypeProperty =
        AvaloniaProperty.Register<HostIcon, HostType>(nameof(Type));

    public static readonly StyledProperty<double> SizeProperty =
        AvaloniaProperty.Register<HostIcon, double>(nameof(Size), 16);

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<HostIcon>();

    static HostIcon()
    {
        AffectsRender<HostIcon>(TypeProperty, ForegroundProperty);
        AffectsMeasure<HostIcon>(SizeProperty);
    }

    public HostType Type { get => GetValue(TypeProperty); set => SetValue(TypeProperty, value); }

    public double Size { get => GetValue(SizeProperty); set => SetValue(SizeProperty, value); }

    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    /// <summary>Resource key of the glyph: `Brand.*` (filled) or `Icon.server` (Lucide, stroked).</summary>
    public static string ResourceKey(HostType type) => type switch
    {
        HostType.GitHub => "Brand.GitHub",
        HostType.GitLab or HostType.GitLabServer => "Brand.GitLab",
        HostType.Bitbucket => "Brand.Bitbucket",
        _ => "Icon.server",
    };

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    public override void Render(DrawingContext context)
    {
        var key = ResourceKey(Type);
        if (Foreground is null || !this.TryFindResource(key, ActualThemeVariant, out var res) || res is not Geometry geometry)
            return;
        var scale = Size / 24;
        using (context.PushTransform(Matrix.CreateScale(scale, scale)))
        {
            if (key.StartsWith("Brand.", StringComparison.Ordinal)) context.DrawGeometry(Foreground, null, geometry);
            else context.DrawGeometry(null, new Pen(Foreground, 2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), geometry);
        }
    }
}
