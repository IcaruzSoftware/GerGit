using CommunityToolkit.Mvvm.ComponentModel;
using GerGit.Views;

namespace GerGit.ViewModels;

/// <summary>Appearance menu, commit list layout and layout zoom . Both settings are app-wide: every repository tab follows them at once.</summary>
public sealed partial class MainWindowViewModel
{
    public const string CommitListLayoutKey = "views.commitListLayout", ZoomKey = "appearance.zoomPercent";

    /// <summary>Commit list left, details right ("Vertical"); else list on top ("Horizontal", the default). Stored as
    /// "leftRight" / "topBottom". Horizontal = "topBottom" (list on top).</summary>
    [ObservableProperty]
    public partial bool IsVerticalLayout { get; set; } = Settings.Get(CommitListLayoutKey, "topBottom") == "leftRight";

    /// <summary>Switching the layout re-activates the revision view of the active repository. Read as
    /// "the active tab shows All Commits", also when it showed Local Changes.</summary>
    partial void OnIsVerticalLayoutChanged(bool value)
    {
        Settings.Set(CommitListLayoutKey, value ? "leftRight" : "topBottom");
        if (SelectedTab is { } tab) tab.IsAllCommitsView = true;
    }

    /// <summary>Layout scale in percent: 100 to 200 in steps of 10, written on every step.</summary>
    [ObservableProperty]
    public partial int ZoomPercent { get; set; } = Math.Clamp(Settings.Get(ZoomKey, 100) / 10 * 10, 100, 200);

    partial void OnZoomPercentChanged(int value) => Settings.Set(ZoomKey, value);

    public void ZoomIn() => ZoomPercent = Math.Min(ZoomPercent + 10, 200);

    public void ZoomOut() => ZoomPercent = Math.Max(ZoomPercent - 10, 100);

    /// <summary>Appearance menu, built each time it opens. Choosing a theme switches every window and stops
    /// following the OS theme (<see cref="AppTheme.Choose"/>); "Use System Setting" follows it again.</summary>
    public List<MenuEntry> AppearanceMenu()
    {
        bool repo = SelectedTab is not null, dark = AppTheme.IsDark;
        Func<Task> Layout(bool vertical) => () =>
        {
            IsVerticalLayout = vertical;
            return Task.CompletedTask;
        };
        Func<Task> ThemeOf(bool d) => () =>
        {
            AppTheme.Choose(d);
            return Task.CompletedTask;
        };
        return
        [
            Menus.Heading("Theme"),
            new() { Header = "Light", IsChecked = !dark, Action = ThemeOf(false) },
            new() { Header = "Dark", IsChecked = dark, Action = ThemeOf(true) },
            new() { Header = "Use System Setting", IsChecked = Settings.Get(AppTheme.FollowOsKey, true), Action = AppTheme.FollowOs },
            Menus.Separator,
            Menus.Heading("Commit List Layout"),
            new() { Header = "Horizontal", IsChecked = !IsVerticalLayout, IsEnabled = repo, Action = Layout(false) },
            new() { Header = "Vertical", IsChecked = IsVerticalLayout, IsEnabled = repo, Action = Layout(true) },
        ];
    }
}
