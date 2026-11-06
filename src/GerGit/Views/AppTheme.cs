using System.Diagnostics;
using Avalonia;
using Avalonia.Styling;
using Avalonia.Threading;
using Tmds.DBus.Protocol;

namespace GerGit.Views;

/// <summary>
/// The app theme: dark or light for every window, dialog and popup at once, through
/// <see cref="Application.RequestedThemeVariant"/> and the "Dark"/"Light" dictionaries of <c>Themes/Colors.axaml</c>.
/// <c>appearance.theme</c> holds the last theme; while <c>appearance.followOs</c> (default true) is on, the OS
/// preference decides, read from the XDG desktop portal at start and followed live. Choosing a theme by hand (Window
/// menu Switch Theme, Appearance menu, Quick Launch) turns following off; Appearance > "Use System Setting" (GerGit
/// addition) turns it back on.
/// Controls that look colours up in code read them again when their ActualThemeVariant changes.
/// </summary>
public static class AppTheme
{
    public const string ThemeKey = "appearance.theme", FollowOsKey = "appearance.followOs";

    const string PortalName = "org.freedesktop.portal.Desktop", PortalPath = "/org/freedesktop/portal/desktop",
        PortalSettings = "org.freedesktop.portal.Settings", Appearance = "org.freedesktop.appearance", ColorSchemeKey = "color-scheme";

    /// <summary>The OS source: reads the portal's color-scheme and from then on calls the action (on the UI thread) with
    /// every change; null when there is no portal or it has no such setting. Tests replace it, as the portal is not
    /// reachable there.</summary>
    internal static Func<Action<uint>, Task<uint?>> Portal { get; set; } = ReadPortalAsync;

    /// <summary>The fallback without a portal: GTK's gtk-application-prefer-dark-theme, null when not set. Tests replace it.</summary>
    internal static Func<bool?> GtkPrefersDark { get; set; } = ReadGtkSetting;

    public static bool IsDark => Application.Current?.ActualThemeVariant == ThemeVariant.Dark;

    /// <summary>At start (<see cref="App.Initialize"/>, before any window): the stored theme at once, so the usual start
    /// does not flash; then, when following the OS, its preference (synchronously when the source answers at once).</summary>
    internal static Task Install(Application app)
    {
        Apply(app, Settings.Get(ThemeKey, "light") == "dark");
        return Settings.Get(FollowOsKey, true) ? FollowOsAsync() : Task.CompletedTask;
    }

    /// <summary>Appearance menu "Use System Setting": follow the OS again and apply its preference now.</summary>
    public static Task FollowOs()
    {
        Settings.Set(FollowOsKey, true);
        return FollowOsAsync();
    }

    /// <summary>How long the start waits for the portal before the fallback decides (tests shorten it).</summary>
    internal static TimeSpan PortalTimeout { get; set; } = TimeSpan.FromSeconds(3);

    static async Task FollowOsAsync()
    {
        uint? scheme = null;
        Task<uint?>? read = null;
        try { scheme = await (read = Portal(OnOsChanged)).WaitAsync(PortalTimeout); }
        catch (TimeoutException) when (read is not null)
        {
            // At login D-Bus may still be starting the portal: the fallback decides now, its answer still counts later.
            Trace.WriteLine("Theme portal: no answer yet");
            _ = LateAnswerAsync(read);
        }
        catch (Exception e) // any other failure means no portal: the fallback decides
        {
            Trace.WriteLine($"Theme portal: {e.Message}");
        }
        await OnUiThread(() =>
        {
            if (Settings.Get(FollowOsKey, true)) Set(FromOs(scheme, scheme is null ? GtkPrefersDark() : null));
        });
    }

    /// <summary>A portal answer after the timeout: applied if the OS is still followed (no manual choice meanwhile).</summary>
    static async Task LateAnswerAsync(Task<uint?> read)
    {
        uint? scheme;
        try { scheme = await read; }
        catch (Exception e)
        {
            Trace.WriteLine($"Theme portal: {e.Message}");
            return;
        }
        if (scheme is { } v) await OnUiThread(() => OnOsChanged(v));
    }

    static async Task OnUiThread(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else await Dispatcher.UIThread.InvokeAsync(action);
    }

    /// <summary>A live OS change switches the app while it follows the OS.</summary>
    static void OnOsChanged(uint scheme)
    {
        if (Settings.Get(FollowOsKey, true)) Set(FromOs(scheme, null));
    }

    /// <summary>Color-scheme 1 = dark, 2 = light, 0 = no preference (light); without a portal the GTK
    /// setting decides, without that too light.</summary>
    internal static bool FromOs(uint? colorScheme, bool? gtkPrefersDark) => colorScheme is { } v ? v == 1 : gtkPrefersDark == true;

    /// <summary>Window menu / Quick Launch "Switch Theme" and Ctrl+click on Appearance: dark &lt;-&gt; light.</summary>
    public static void Switch() => Choose(!IsDark);

    /// <summary>A theme chosen by hand: stored, and the OS is no longer followed.</summary>
    public static void Choose(bool dark)
    {
        Settings.Set(FollowOsKey, false);
        Set(dark);
    }

    static void Set(bool dark)
    {
        var value = dark ? "dark" : "light";
        if (Settings.Get(ThemeKey, "light") != value) Settings.Set(ThemeKey, value);
        if (Application.Current is { } app) Apply(app, dark);
    }

    static void Apply(Application app, bool dark) => app.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;

    /// <summary>The value of a color-scheme variant: the portal's Read wraps it once more than ReadOne and the signal.</summary>
    internal static uint? ColorScheme(VariantValue value)
    {
        while (value.Type == VariantValueType.Variant) value = value.GetVariantValue();
        return value.Type == VariantValueType.UInt32 ? value.GetUInt32() : null;
    }

    /// <summary>On Linux: the portal's Settings interface on the session bus (Tmds.DBus.Protocol, an explicit reference
    /// at the version Avalonia.FreeDesktop uses). Read (not ReadOne) works with portal versions 1 and 2. The SettingChanged watch is
    /// installed first, so no change between the two is lost, and only once per process (a later read, e.g. "Use
    /// System Setting", reuses it); a failed install is tried again next time. Called on the UI thread.</summary>
    static async Task<uint?> ReadPortalAsync(Action<uint> changed)
    {
        var bus = DBusConnection.Session;
        s_watch ??= bus.WatchSignalAsync(PortalName, PortalPath, PortalSettings, "SettingChanged", ReadSetting,
            (Notification<(string Namespace, string Key, VariantValue Value)> n) =>
            {
                if (n.HasValue && n.Value is { Namespace: Appearance, Key: ColorSchemeKey } s && ColorScheme(s.Value) is { } v)
                    Dispatcher.UIThread.Post(() => changed(v));
            }, ObserverFlags.None, emitOnCapturedContext: false).AsTask();
        try { await s_watch; }
        catch
        {
            s_watch = null;
            throw;
        }
        return await bus.CallMethodAsync(ReadMessage(bus), (m, _) => ColorScheme(m.GetBodyReader().ReadVariantValue()));
    }

    /// <summary>The one SettingChanged watch, alive as long as the app runs.</summary>
    static Task<IDisposable>? s_watch;

    static (string, string, VariantValue) ReadSetting(Message m, object? _)
    {
        var r = m.GetBodyReader();
        return (r.ReadString(), r.ReadString(), r.ReadVariantValue());
    }

    static MessageBuffer ReadMessage(DBusConnection bus)
    {
        var w = bus.GetMessageWriter();
        w.WriteMethodCallHeader(PortalName, PortalPath, PortalSettings, "Read", "ss");
        w.WriteString(Appearance);
        w.WriteString(ColorSchemeKey);
        return w.CreateMessage();
    }

    /// <summary>gtk-application-prefer-dark-theme from $XDG_CONFIG_HOME/gtk-4.0 or gtk-3.0 settings.ini (first one that
    /// sets it). Limitation: no system-wide /etc/gtk-*/settings.ini and no gsettings; add if a desktop needs it.</summary>
    static bool? ReadGtkSetting()
    {
        var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x ? x
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        foreach (var dir in (string[])["gtk-4.0", "gtk-3.0"])
        {
            try
            {
                foreach (var line in File.ReadLines(Path.Combine(config, dir, "settings.ini")))
                    if (line.Split('=', 2) is [var k, var v] && k.Trim() == "gtk-application-prefer-dark-theme")
                        return v.Trim() is "1" or "true" or "TRUE" or "True";
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // no file: next one
        }
        return null;
    }
}
