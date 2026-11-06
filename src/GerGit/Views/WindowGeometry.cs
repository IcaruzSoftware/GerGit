using Avalonia;
using Avalonia.Controls;

namespace GerGit.Views;

/// <summary>Remembered position, size and maximised state of a resizable window (Settings: interactive rebase
/// and resolver windows; default 100,100, 1020 x 720). Saved when the window closes, and only if it was activated, so
/// the initial placement does not overwrite the stored values (one write instead of one per move event). The size is
/// stored unzoomed: <see cref="Zoom"/> scales it again when the window opens.</summary>
public static class WindowGeometry
{
    public sealed record Geometry(int X, int Y, double Width, double Height, bool Maximized);

    public static void Apply(Window window, string key)
    {
        var g = Settings.Get(key, new Geometry(100, 100, 1020, 720, false));
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Position = new PixelPoint(g.X, g.Y);
        window.Width = g.Width;
        window.Height = g.Height;
        if (g.Maximized) window.WindowState = WindowState.Maximized;

        bool active = false;
        var opened = default(Size);
        double scale = 1; // the zoom the window opened with: its size stays the user's on a later zoom change
        void Save()
        {
            if (!active) return;
            var max = window.WindowState == WindowState.Maximized;
            var old = Settings.Get(key, g);
            // A maximised window keeps the normal bounds stored before it. A width or height the user did not change
            // keeps the stored one: the opening size may be capped at the screen (Zoom), which must not shrink it for good.
            var w = window.Width == opened.Width ? old.Width : window.Width / scale;
            var h = window.Height == opened.Height ? old.Height : window.Height / scale;
            Settings.Set(key, max ? old with { Maximized = true } : new Geometry(window.Position.X, window.Position.Y, w, h, false));
        }
        window.Opened += (_, _) => (opened, scale) = (new Size(window.Width, window.Height), Zoom.Scale);
        window.Activated += (_, _) => active = true;
        window.Closing += (_, _) => Save();
    }
}
