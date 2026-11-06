using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using GerGit.ViewModels;
using GerGit.ViewModels.Preferences;

namespace GerGit.Views.Preferences;

/// <summary>Body of the Preferences window; hosted by DialogWindow. The folder and file choosers live here.</summary>
public partial class PreferencesView : UserControl
{
    public PreferencesView() => InitializeComponent();

    PreferencesViewModel Vm => (PreferencesViewModel)DataContext!;

    static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    async Task<string?> PickFolderAsync(string start)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return null;
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select location",
            SuggestedStartLocation = await storage.TryGetFolderFromPathAsync(Directory.Exists(start) ? start : Home),
        });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    async void OnAddFolder(object? sender, RoutedEventArgs e) => await Safe(async () =>
    {
        if (await PickFolderAsync(Home) is { } path) Vm.AddFolder(path);
    });

    async void OnReplaceFolder(object? sender, RoutedEventArgs e) => await Safe(async () =>
    {
        if ((sender as Control)?.DataContext is SourceFolderItem item && await PickFolderAsync(item.Path) is { } path)
            Vm.ReplaceFolder(item, path);
    });

    async void OnRemoveFolder(object? sender, RoutedEventArgs e) => await Safe(async () =>
    {
        if (Vm.SelectedFolder is { } item) await Vm.RemoveFolderAsync(item);
    });

    async void OnRemoveFolderRow(object? sender, RoutedEventArgs e) => await Safe(async () =>
    {
        if ((sender as Control)?.DataContext is SourceFolderItem item) await Vm.RemoveFolderAsync(item);
    });

    async void OnIdentityLostFocus(object? sender, RoutedEventArgs e) => await Safe(Vm.SaveIdentityAsync);

    void OnGravatar(object? sender, RoutedEventArgs e) => HostingService.Current.OpenBrowser("https://gravatar.com");

    async void OnBrowseShell(object? sender, RoutedEventArgs e) => await Safe(async () =>
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Select shell", AllowMultiple = false });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) Vm.SetShellPath(path);
    });

    static async Task Safe(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { Trace.WriteLine(ex); } // a failing chooser or dialog must not end the process
    }
}
