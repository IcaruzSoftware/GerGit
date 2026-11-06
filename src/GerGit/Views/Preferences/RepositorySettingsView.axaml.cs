using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using GerGit.ViewModels;
using GerGit.ViewModels.Preferences;

namespace GerGit.Views.Preferences;

/// <summary>Body of the Repository Settings window; hosted by DialogWindow.</summary>
public partial class RepositorySettingsView : UserControl
{
    public RepositorySettingsView() => InitializeComponent();

    RepositorySettingsViewModel Vm => (RepositorySettingsViewModel)DataContext!;

    /// <summary>The `+` drop-down.</summary>
    internal IReadOnlyList<MenuEntry> AddMenu() =>
    [
        Menus.Item("Add New Rule", () => Add("new")),
        Menus.Separator,
        Menus.Item("Add Sample Github Rule", () => Add("github")),
        Menus.Item("Add Sample Jira Rule", () => Add("jira")),
        Menus.Item("Add Sample Jira Multiproject Rule", () => Add("jiraMulti")),
    ];

    Task Add(string kind)
    {
        Vm.AddRule(kind);
        return Task.CompletedTask;
    }

    void OnAddRule(object? sender, RoutedEventArgs e) => MenuControls.Open((Control)sender!, AddMenu(), below: true);

    async void OnRemoveRule(object? sender, RoutedEventArgs e)
    {
        try { await Vm.RemoveRuleAsync(); }
        catch (Exception ex) { Trace.WriteLine(ex); } // a failing dialog must not end the process
    }

    void OnGravatar(object? sender, RoutedEventArgs e) => HostingService.Current.OpenBrowser("https://gravatar.com");
}
