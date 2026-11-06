using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using GerGit.Controls;
using GerGit.Core;
using GerGit.Core.Git;
using GerGit.ViewModels;
using GerGit.Views;
using Tmds.DBus.Protocol;
using static GerGit.Tests.Ui.CommitListTests;

namespace GerGit.Tests.Ui;

/// <summary>light theme: the token sets, Switch Theme, following the OS portal, and live re-colouring
/// of controls that read colours in code. The theme is app-wide, so these run alone (with the other appearance tests)
/// and put the dark test default (TestSettings) back.</summary>
[Collection("Appearance")]
public sealed class ThemeTests : IDisposable
{
    readonly Func<Action<uint>, Task<uint?>> _portal = AppTheme.Portal;
    readonly Func<bool?> _gtk = AppTheme.GtkPrefersDark;
    readonly TimeSpan _timeout = AppTheme.PortalTimeout;

    public void Dispose()
    {
        (AppTheme.Portal, AppTheme.GtkPrefersDark, AppTheme.PortalTimeout) = (_portal, _gtk, _timeout);
        Settings.Set<string?>(AppTheme.ThemeKey, null);
        Settings.Set<bool?>(AppTheme.FollowOsKey, null);
        Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
    }

    static ResourceDictionary ThemeDictionary(ResourceDictionary colors, ThemeVariant variant) =>
        (ResourceDictionary)colors.ThemeDictionaries[variant];

    [AvaloniaFact]
    public void Dark_and_light_have_the_same_tokens_with_the_expected_values()
    {
        var colors = (ResourceDictionary)AvaloniaXamlLoader.Load(new Uri("avares://GerGit/Themes/Colors.axaml"));
        var dark = ThemeDictionary(colors, ThemeVariant.Dark);
        var light = ThemeDictionary(colors, ThemeVariant.Light);
        Assert.True(dark.Count > 300, $"{dark.Count} dark keys");
        Assert.Equal(dark.Keys.Select(k => (string)k).Order(), light.Keys.Select(k => (string)k).Order());
        foreach (var key in dark.Keys)
            Assert.True(dark[key]!.GetType() == light[key]!.GetType(), $"{key}: {dark[key]!.GetType().Name} vs {light[key]!.GetType().Name}");

        // A few values straight from the colour tables.
        string Of(ResourceDictionary d, string key) => ((ISolidColorBrush)d[key]!).Color.ToString().ToUpperInvariant();
        Assert.Equal(("#FF1A1A1A", "#FFF3F3F3"), (Of(dark, "Color.Window.Bg"), Of(light, "Color.Window.Bg")));
        Assert.Equal(("#FFEAE8E8", "#FF222222"), (Of(dark, "Color.Text.Primary"), Of(light, "Color.Text.Primary")));
        Assert.Equal("#59EBEBEB", Of(light, "Color.Revlist.CodeBgSelected")); // "#EBEBEB @35% alpha"
        Assert.Equal("#FFE2FDE3", Of(light, "Color.Diff.AddedLine"));
        Assert.Equal("#FFFF9502", Of(light, "Color.RefLabel.Border.0")); // light ref borders are the lane colours
        Assert.Equal(BoxShadows.Parse("0 4 10 0 #CC000000"), (BoxShadows)dark["Color.Repotab.Shadow"]!); // repotab.shadow-opacity 0.8
        Assert.Equal(BoxShadows.Parse("0 4 10 0 #4D000000"), (BoxShadows)light["Color.Repotab.Shadow"]!); // 0.30
    }

    [AvaloniaFact]
    public void Portal_color_scheme_1_is_dark_0_and_2_are_light_and_without_portal_gtk_decides()
    {
        Assert.False(AppTheme.FromOs(0, true)); // the portal wins over GTK
        Assert.True(AppTheme.FromOs(1, null));
        Assert.False(AppTheme.FromOs(2, null));
        Assert.True(AppTheme.FromOs(null, true));
        Assert.False(AppTheme.FromOs(null, false));
        Assert.False(AppTheme.FromOs(null, null));

        // The portal's Read wraps the value in one more variant than ReadOne and the SettingChanged signal.
        Assert.Equal(1u, AppTheme.ColorScheme(VariantValue.UInt32(1)));
        Assert.Equal(2u, AppTheme.ColorScheme(VariantValue.Variant(VariantValue.Variant(VariantValue.UInt32(2)))));
        Assert.Null(AppTheme.ColorScheme(VariantValue.String("dark")));
    }

    [AvaloniaFact]
    public async Task A_portal_answer_after_the_timeout_still_applies_unless_the_user_chose_meanwhile()
    {
        var app = Application.Current!;
        AppTheme.PortalTimeout = TimeSpan.FromMilliseconds(20);
        var late = new TaskCompletionSource<uint?>();
        AppTheme.Portal = _ => late.Task;
        await AppTheme.Install(app); // times out: the fallback (no GTK setting) gives light for now
        Assert.False(AppTheme.IsDark);
        late.SetResult(1); // the portal finally answers "dark"
        Dispatcher.UIThread.RunJobs();
        Assert.Equal((true, "dark"), (AppTheme.IsDark, Settings.Get(AppTheme.ThemeKey, "")));

        // Again, but the user switches before the late answer: it is ignored.
        late = new TaskCompletionSource<uint?>();
        AppTheme.Portal = _ => late.Task;
        AppTheme.GtkPrefersDark = () => true;
        await AppTheme.Install(app);
        Assert.True(AppTheme.IsDark);
        AppTheme.Switch(); // light, no longer following
        late.SetResult(1);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal((false, "light"), (AppTheme.IsDark, Settings.Get(AppTheme.ThemeKey, "")));
    }

    [AvaloniaFact]
    public async Task Following_the_os_reads_the_portal_follows_changes_and_stops_after_a_manual_choice()
    {
        var app = Application.Current!;
        Action<uint>? changed = null;
        AppTheme.Portal = c => { changed = c; return Task.FromResult<uint?>(2); };
        await AppTheme.Install(app);
        Assert.Equal((ThemeVariant.Light, "light"), (app.ActualThemeVariant, Settings.Get(AppTheme.ThemeKey, "")));

        changed!(1); // live: the OS turned dark
        Assert.Equal((ThemeVariant.Dark, "dark"), (app.ActualThemeVariant, Settings.Get(AppTheme.ThemeKey, "")));
        changed(0); // no preference = light
        Assert.False(AppTheme.IsDark);

        // Without a portal (null or failing) the GTK setting decides.
        AppTheme.GtkPrefersDark = () => true;
        AppTheme.Portal = _ => Task.FromResult<uint?>(null);
        await AppTheme.Install(app);
        Assert.True(AppTheme.IsDark);
        AppTheme.GtkPrefersDark = () => null;
        AppTheme.Portal = _ => Task.FromException<uint?>(new InvalidOperationException("no session bus"));
        await AppTheme.Install(app);
        Assert.False(AppTheme.IsDark);

        // Switch Theme: toggles, stores the theme and stops following the OS.
        AppTheme.Switch();
        Assert.Equal((true, "dark", false), (AppTheme.IsDark, Settings.Get(AppTheme.ThemeKey, ""), Settings.Get(AppTheme.FollowOsKey, true)));
        changed(2); // ignored now
        Assert.True(AppTheme.IsDark);
        AppTheme.Portal = _ => Task.FromResult<uint?>(2);
        await AppTheme.Install(app); // a later start keeps the stored theme
        Assert.True(AppTheme.IsDark);
        AppTheme.Switch();
        Assert.Equal((false, "light"), (AppTheme.IsDark, Settings.Get(AppTheme.ThemeKey, "")));
    }

    [AvaloniaFact]
    public async Task Appearance_menu_chooses_the_theme_and_use_system_setting_follows_the_os_again()
    {
        var vm = new MainWindowViewModel();
        string[] ThemeItems() => vm.AppearanceMenu().Skip(1).Take(3).Select(e => e.Header + (e.IsChecked == true ? " (checked)" : "")).ToArray();
        Assert.Equal(["Light", "Dark (checked)", "Use System Setting (checked)"], ThemeItems()); // the test OS is dark
        await vm.AppearanceMenu().Single(e => e.Header == "Light").Invoke();
        Assert.Equal((false, false), (AppTheme.IsDark, Settings.Get(AppTheme.FollowOsKey, true)));
        Assert.Equal(["Light (checked)", "Dark", "Use System Setting"], ThemeItems());
        await vm.AppearanceMenu().Single(e => e.Header == "Dark").Invoke();
        Assert.True(AppTheme.IsDark);

        // After Switch Theme, "Use System Setting" follows the OS again and applies its value at once.
        AppTheme.Switch();
        Assert.Equal((false, false), (AppTheme.IsDark, Settings.Get(AppTheme.FollowOsKey, true)));
        AppTheme.Portal = _ => Task.FromResult<uint?>(1);
        await vm.AppearanceMenu().Single(e => e.Header == "Use System Setting").Invoke();
        Assert.Equal((true, true, "dark"), (AppTheme.IsDark, Settings.Get(AppTheme.FollowOsKey, false), Settings.Get(AppTheme.ThemeKey, "")));
        Assert.Equal(["Light", "Dark (checked)", "Use System Setting (checked)"], ThemeItems());
    }

    static uint Of(string k, ThemeVariant v) =>
        Application.Current!.TryGetResource(k, v, out var b) && b is ISolidColorBrush s ? s.Color.ToUInt32() : throw new KeyNotFoundException(k);

    [AvaloniaFact]
    public void A_live_switch_recolours_search_matches()
    {
        var block = new TextBlock { Text = "find the needle here" };
        SearchHighlight.SetIsEnabled(block, true);
        SearchHighlight.SetText(block, "needle");
        var window = new Window { Width = 300, Height = 100, Content = block };
        try
        {
            window.Show();
            Settle(window);
            uint Match() => ((ISolidColorBrush)block.Inlines!.OfType<Run>().Single(r => r.Background is not null).Background!).Color.ToUInt32();
            Assert.Equal(Of("Color.Search.MatchBg", ThemeVariant.Dark), Match());
            AppTheme.Switch();
            Settle(window);
            Assert.Equal(Of("Color.Search.MatchBg", ThemeVariant.Light), Match());
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task A_live_switch_recolours_the_merge_resolver_panes()
    {
        using var repo = new TempRepo();
        repo.Commit("base", 10, ("f.txt", "a\nb\nc\n"));
        repo.Git("checkout", "-q", "-b", "x");
        repo.Commit("theirs", 20, ("f.txt", "a\nTHEIRS\nc\n"));
        repo.Git("checkout", "-q", "-");
        repo.Commit("ours", 30, ("f.txt", "a\nOURS\nc\n"));
        Assert.ThrowsAny<Exception>(() => repo.Git("merge", "-q", "x")); // conflict
        using var tab = new RepoTabViewModel(await GitRepository.OpenAsync(repo.Path));
        await tab.RefreshAsync();
        var vm = new MergeResolverViewModel(tab, "f.txt");
        await vm.LoadAsync();
        var window = new MergeResolverWindow(vm) { Width = 1020, Height = 600 };
        try
        {
            window.Show();
            Settle(window);

            // Row 2 ("THEIRS") of the theirs pane is a conflict line; sample its background away from the text.
            var tv = window.FindControl<TextEditor>("TheirsEditor")!.TextArea.TextView;
            var at = tv.TranslatePoint(new Point(tv.Bounds.Width / 2, tv.GetVisualTopByDocumentLine(2) - tv.ScrollOffset.Y + tv.DefaultLineHeight / 2), window)!.Value;
            uint Shown()
            {
                using var frame = window.CaptureRenderedFrame()!;
                return Pixel(frame, (int)at.X, (int)at.Y);
            }
            Assert.Equal(Of("Color.Merge.Theirs", ThemeVariant.Dark), Shown());
            AppTheme.Switch();
            Settle(window);
            Assert.Equal(Of("Color.Merge.Theirs", ThemeVariant.Light), Shown());
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task A_live_switch_recolours_an_open_diff_and_its_links()
    {
        using var repo = new TempRepo();
        repo.Commit("init", 0, ("f.txt", "one\ntwo\n"));
        repo.Write("f.txt", "one\nTWO\n");
        var key = "test.diffLayout." + Guid.NewGuid().ToString("N");
        Settings.Set(key, "unified");
        Window? window = null;
        try
        {
            var vm = new DiffViewModel(await GitRepository.OpenAsync(repo.Path)) { LayoutKey = key };
            var view = new DiffView { DataContext = vm };
            var link = new SelectableTextBlock();
            Hyperlinks.SetContent(link, new LinkedText("see https://example.com", [new TextLink(4, 19, "https://example.com")]));
            window = new Window { Width = 800, Height = 400, Content = new DockPanel { Children = { link, view } } };
            DockPanel.SetDock(link, Dock.Top);
            window.Show();
            vm.Show(new DiffRequest(null, "", "f.txt", null, Working: WorkingDiff.Unstaged));
            await vm.Loading;
            Settle(window);

            var tv = view.GetVisualDescendants().OfType<TextEditor>().Single(e => e.Name == "Editor").TextArea.TextView;
            int added = vm.Document!.Lines.ToList().FindIndex(l => l.Kind == DocLineKind.Added);
            Assert.True(added >= 0);
            var at = tv.TranslatePoint(new Point(tv.Bounds.Width - 4,
                tv.GetVisualTopByDocumentLine(added + 1) - tv.ScrollOffset.Y + tv.DefaultLineHeight / 2), window)!.Value;
            uint Shown()
            {
                using var frame = window.CaptureRenderedFrame()!;
                return Pixel(frame, (int)at.X, (int)at.Y);
            }
            IBrush? LinkRun() => link.Inlines!.OfType<Run>().Single(r => r.TextDecorations == TextDecorations.Underline).Foreground;

            Assert.Equal(Of("Color.Diff.AddedLine", ThemeVariant.Dark), Shown());
            Assert.Equal(Of("Color.Text.Link", ThemeVariant.Dark), ((ISolidColorBrush)LinkRun()!).Color.ToUInt32());

            AppTheme.Switch();
            Settle(window);
            Assert.Equal(Of("Color.Diff.AddedLine", ThemeVariant.Light), Shown());
            Assert.Equal(Of("Color.Text.Link", ThemeVariant.Light), ((ISolidColorBrush)LinkRun()!).Color.ToUInt32());
        }
        finally
        {
            window?.Close();
            Settings.Set<string?>(key, null);
        }
    }

    static void Settle(Window w)
    {
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }
}
