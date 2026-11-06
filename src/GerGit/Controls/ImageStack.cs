using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace GerGit.Controls;

/// <summary>
/// Image diff "Swipe" and "Onion Skin": old and new image laid on top of each other, centred, each scaled down
/// to fit (aspect kept, floored to whole pixels), on the checkerboard. Swipe: a draggable vertical divider, old image
/// left of it, new image right of it, starting in the middle. Onion Skin: the new image drawn over the old one with
/// <see cref="NewOpacity"/>. <see cref="Overlay"/> (Highlight pixels) is drawn over the new image.
/// </summary>
public sealed class ImageStack : Control
{
    public static readonly StyledProperty<Bitmap?> OldProperty = AvaloniaProperty.Register<ImageStack, Bitmap?>(nameof(Old));
    public static readonly StyledProperty<Bitmap?> NewProperty = AvaloniaProperty.Register<ImageStack, Bitmap?>(nameof(New));
    public static readonly StyledProperty<Bitmap?> OverlayProperty = AvaloniaProperty.Register<ImageStack, Bitmap?>(nameof(Overlay));
    public static readonly StyledProperty<bool> SwipeProperty = AvaloniaProperty.Register<ImageStack, bool>(nameof(Swipe));
    public static readonly StyledProperty<double> NewOpacityProperty = AvaloniaProperty.Register<ImageStack, double>(nameof(NewOpacity), 1);
    public static readonly StyledProperty<double> SplitProperty =
        AvaloniaProperty.Register<ImageStack, double>(nameof(Split), 0.5, defaultBindingMode: BindingMode.TwoWay);

    bool _dragging;

    static ImageStack()
    {
        AffectsRender<ImageStack>(OldProperty, NewProperty, OverlayProperty, SwipeProperty, NewOpacityProperty, SplitProperty);
    }

    public ImageStack() => ActualThemeVariantChanged += (_, _) => InvalidateVisual(); // checkerboard and divider colours

    public Bitmap? Old { get => GetValue(OldProperty); set => SetValue(OldProperty, value); }
    public Bitmap? New { get => GetValue(NewProperty); set => SetValue(NewProperty, value); }
    public Bitmap? Overlay { get => GetValue(OverlayProperty); set => SetValue(OverlayProperty, value); }
    public bool Swipe { get => GetValue(SwipeProperty); set => SetValue(SwipeProperty, value); }
    public double NewOpacity { get => GetValue(NewOpacityProperty); set => SetValue(NewOpacityProperty, value); }

    /// <summary>Divider position within the images' common width (0..1).</summary>
    public double Split { get => GetValue(SplitProperty); set => SetValue(SplitProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SwipeProperty) Cursor = Swipe ? new Cursor(StandardCursorType.SizeWestEast) : null;
    }

    Rect Fit(Bitmap b)
    {
        var p = b.PixelSize;
        double scale = Math.Min(1, Math.Min(Bounds.Width / p.Width, Bounds.Height / p.Height));
        double w = Math.Floor(p.Width * scale), h = Math.Floor(p.Height * scale);
        return new Rect(Math.Floor((Bounds.Width - w) / 2), Math.Floor((Bounds.Height - h) / 2), w, h);
    }

    /// <summary>The divider's x position; null outside Swipe mode.</summary>
    internal double? DividerX => Swipe && Old is { } o && New is { } n && Fit(o).Union(Fit(n)) is var u ? u.X + u.Width * Split : null;

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size)); // the whole area takes the drag
        if (Old is not { } oldImage || New is not { } newImage) return;
        Rect o = Fit(oldImage), n = Fit(newImage);
        var checker = this.TryFindResource("ImageDiff.Checker", ActualThemeVariant, out var res) ? res as IBrush : null;
        if (DividerX is { } x)
        {
            using (context.PushClip(new Rect(0, 0, x, Bounds.Height))) { Checker(o); Draw(oldImage, o); }
            using (context.PushClip(new Rect(x, 0, Math.Max(0, Bounds.Width - x), Bounds.Height))) { Checker(n); Draw(newImage, n); DrawOverlay(n); }
            // Divider: 2 px line with round dots at the ends, accent-secondary colour.
            var brush = this.TryFindResource("Color.Accent.Secondary", ActualThemeVariant, out var a) && a is IBrush b ? b : Brushes.DodgerBlue;
            var u = o.Union(n);
            context.FillRectangle(brush, new Rect(x - 1, u.Top, 2, u.Height));
            context.DrawEllipse(brush, null, new Point(x, u.Top), 3, 3);
            context.DrawEllipse(brush, null, new Point(x, u.Bottom), 3, 3);
            return;
        }
        Checker(o);
        Checker(n);
        Draw(oldImage, o);
        using (context.PushOpacity(NewOpacity)) Draw(newImage, n);
        DrawOverlay(n);

        void Checker(Rect r)
        {
            if (checker is not null) context.FillRectangle(checker, r);
        }

        void Draw(Bitmap b, Rect r) => context.DrawImage(b, new Rect(b.Size), r);

        void DrawOverlay(Rect r)
        {
            if (Overlay is { } overlay) context.DrawImage(overlay, new Rect(overlay.Size), r);
        }
    }

    // Swipe: press anywhere and drag moves the divider.
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (DividerX is null) return;
        _dragging = true;
        e.Pointer.Capture(this);
        MoveDivider(e.GetPosition(this).X);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging) MoveDivider(e.GetPosition(this).X);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragging = false;
        e.Pointer.Capture(null);
    }

    void MoveDivider(double x)
    {
        if (Old is not { } o || New is not { } n) return;
        var u = Fit(o).Union(Fit(n));
        Split = u.Width > 0 ? Math.Clamp((x - u.X) / u.Width, 0, 1) : 0.5;
    }
}
