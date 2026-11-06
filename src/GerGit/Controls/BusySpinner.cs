using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Threading;

namespace GerGit.Controls;

/// <summary>
/// Busy spinner: 12 short rounded bars on a circle, idle opacity 0.3, a bright head moving clockwise one
/// bar every 0.05 s with three fading bars behind it. Size = <see cref="Control.Width"/> (default 20). Colour = Foreground.
/// </summary>
public sealed class BusySpinner : Control
{
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<BusySpinner>();

    static readonly double[] Trail = [1, 0.85, 0.6, 0.3];
    readonly DispatcherTimer _timer;
    int _head;

    public BusySpinner()
    {
        Width = Height = 20;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Background, (_, _) =>
        {
            if (!IsEffectivelyVisible) return;
            _head = (_head + 1) % 12;
            InvalidateVisual();
        });
        AttachedToVisualTree += (_, _) => _timer.Start();
        DetachedFromVisualTree += (_, _) => _timer.Stop();
    }

    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    public override void Render(DrawingContext context)
    {
        if (Foreground is not ISolidColorBrush brush) return;
        double size = Math.Min(Bounds.Width, Bounds.Height), scale = size / 20;
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        for (int i = 0; i < 12; i++)
        {
            int behind = (_head - i + 12) % 12;
            double opacity = behind < Trail.Length ? Trail[behind] : 0.3;
            var pen = new Pen(new SolidColorBrush(brush.Color, opacity), Math.Max(1, 1.7 * scale), lineCap: PenLineCap.Round);
            double angle = i * Math.PI / 6;
            var dir = new Vector(Math.Sin(angle), -Math.Cos(angle));
            context.DrawLine(pen, center + dir * 5 * scale, center + dir * 9 * scale);
        }
    }
}
