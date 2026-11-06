using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using GerGit.Views.Dialogs;

namespace GerGit.Views;

/// <summary>
/// The one app-wide layout scale for every GerGit window, dialog, popup, context
/// menu and tooltip. Nothing to do per window:
/// <list type="bullet">
/// <item>Content: <c>Themes/Zoom.axaml</c> gives every window a template whose content sits in a
/// <see cref="LayoutTransformControl"/> bound to the <see cref="TransformKey"/> resource (live on every change), and
/// makes every popup inherit the scale of its target.</item>
/// <item>Size: <see cref="Install"/> hooks every window just before it is shown (<see cref="Window.WindowOpenedEvent"/>)
/// and multiplies its design size and its minimum size by the scale, capped at the screen's working area.</item>
/// </list>
/// The main window binds its <c>appearance.zoomPercent</c> setting to <see cref="PercentProperty"/>.
/// Why not a whole-window render scale: Avalonia 12 takes a window's RenderScaling from the platform only (read-only on
/// TopLevel; X11 reads AVALONIA_GLOBAL_SCALE_FACTOR once at start), so it can neither change live nor per app.
/// </summary>
public static class Zoom
{
    public const string TransformKey = "Zoom.Transform";

    /// <summary>The open windows with their design (100 %) size and minimum size.</summary>
    static readonly Dictionary<Window, (Size Size, Size Min)> s_open = [];

    /// <summary>The current layout scale (1.0 to 2.0).</summary>
    public static double Scale { get; private set; } = 1;

    /// <summary>Sets the app-wide scale; on the main window, bound to its zoom setting.</summary>
    public static readonly AttachedProperty<int> PercentProperty =
        AvaloniaProperty.RegisterAttached<Window, int>("Percent", typeof(Zoom), 100);

    public static int GetPercent(Window w) => w.GetValue(PercentProperty);

    public static void SetPercent(Window w, int value) => w.SetValue(PercentProperty, value);

    static bool s_hooked;

    /// <summary>At start (<see cref="App.Initialize"/>; headless tests get a new App per test): 100 %.</summary>
    internal static void Install(Application app)
    {
        Scale = 1;
        s_open.Clear();
        app.Resources[TransformKey] = new ScaleTransform(1, 1);
        if (s_hooked) return;
        s_hooked = true;
        PercentProperty.Changed.AddClassHandler<Window>((_, e) => Set((int)e.NewValue!));
        Window.WindowOpenedEvent.AddClassHandler<Window>((w, _) => Opening(w));
    }

    /// <summary>
    /// Changes the scale of every open window live. The content scales through the resource; the minimum size follows
    /// so the content is not clipped. A window the user cannot resize also gets its size scaled; a resizable one keeps
    /// the size the user gave it (like the main window). Sizes are recomputed from the design sizes, so repeated
    /// steps do not drift.
    /// </summary>
    public static void Set(int percent)
    {
        double scale = percent / 100.0;
        if (scale == Scale) return;
        Scale = scale;
        Application.Current!.Resources[TransformKey] = new ScaleTransform(scale, scale);
        foreach (var (w, design) in s_open)
        {
            var area = Area(w.Screens?.ScreenFromWindow(w) ?? w.Screens?.Primary);
            w.MinWidth = Cap(design.Min.Width, area.Width);
            w.MinHeight = Cap(design.Min.Height, area.Height);
            if (w.CanResize || w.WindowState != WindowState.Normal) continue;
            if (!w.SizeToContent.HasFlag(SizeToContent.Width)) w.Width = Cap(design.Size.Width, area.Width);
            if (!w.SizeToContent.HasFlag(SizeToContent.Height)) w.Height = Cap(design.Size.Height, area.Height);
        }
    }

    /// <summary>Every window multiplies its design width and height by the scale when it is
    /// created, before its size is computed. The minimum size is scaled the same way.</summary>
    static void Opening(Window w)
    {
        if (s_open.ContainsKey(w)) return; // shown again after Hide
        var design = (Size: new Size(w.Width, w.Height), Min: new Size(w.MinWidth, w.MinHeight));
        s_open[w] = design;
        w.Closed += (_, _) => s_open.Remove(w);
        if (Scale == 1) return;
        var area = Area(OpeningScreen(w));
        w.MinWidth = Cap(design.Min.Width, area.Width);
        w.MinHeight = Cap(design.Min.Height, area.Height);
        w.Width = Cap(design.Size.Width, area.Width);
        w.Height = Cap(design.Size.Height, area.Height);
    }

    /// <summary>A scaled size (or minimum size) never exceeds the screen's working area, but is never made smaller than
    /// the design size. NaN (no size set) stays NaN.</summary>
    static double Cap(double design, double limit) => Math.Min(design * Scale, Math.Max(design, limit));

    /// <summary>The screen a window will open on, before it is shown: its own position for a manual start position with a
    /// set position, else the active GerGit window's screen (the owner of dialogs), else the primary screen. Manual is
    /// the default, and a window that never set its position is at (0,0), which is not always the primary screen.</summary>
    static Screen? OpeningScreen(Window w)
    {
        if (w.Screens is not { } screens) return null;
        if (w.WindowStartupLocation == WindowStartupLocation.Manual && w.Position != default)
            return screens.ScreenFromPoint(w.Position) ?? screens.Primary;
        return s_open.Keys.FirstOrDefault(o => o.IsActive) is { } active ? screens.ScreenFromWindow(active) ?? screens.Primary
            : screens.Primary;
    }

    /// <summary>The working area in layout pixels, less the window manager's title bar.</summary>
    static Size Area(Screen? s) => s is null ? Size.Infinity
        : new Size(s.WorkingArea.Width / s.Scaling, s.WorkingArea.Height / s.Scaling - DialogWindow.TitleBarAllowance);
}
