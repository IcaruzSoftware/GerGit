using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using GerGit.ViewModels.Dialogs.Refs;

namespace GerGit.Views.Dialogs.Refs;

public partial class CreateWorktreeView : UserControl
{
    public CreateWorktreeView() => InitializeComponent();

    /// <summary>Folder dialog "Select location", starting in the container (or its parent when it does not
    /// exist yet); the chosen folder becomes the container.</summary>
    async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage || DataContext is not CreateWorktreeViewModel vm) return;
            var dir = Directory.Exists(vm.Container) ? vm.Container : Path.GetDirectoryName(vm.Container) ?? vm.Container;
            var start = await storage.TryGetFolderFromPathAsync(dir);
            var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Select location", SuggestedStartLocation = start });
            if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path) vm.Container = path;
        }
        catch (Exception ex) { Trace.WriteLine(ex); }
    }
}
