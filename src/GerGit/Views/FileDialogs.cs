using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace GerGit.Views;

/// <summary>Shared file dialogs.</summary>
internal static class FileDialogs
{
    /// <summary>Save as: the file-save dialog `Select location` with the suggested name, starting in
    /// <paramref name="startFolder"/> (null: the home folder; there is no default source folder).
    /// Returns the chosen local path, or null when cancelled.</summary>
    public static async Task<string?> SaveFileAsync(TopLevel top, string suggestedName, string? startFolder = null)
    {
        var storage = top.StorageProvider;
        var start = await storage.TryGetFolderFromPathAsync(startFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            { Title = "Select location", SuggestedFileName = suggestedName, SuggestedStartLocation = start });
        return file?.TryGetLocalPath();
    }

    /// <summary>Closes <paramref name="window"/> when <paramref name="tab"/> leaves its main window's tabs (Blame and
    /// File History must not act on a closed repository).</summary>
    public static void CloseWithTab(Window window, ViewModels.RepoTabViewModel tab)
    {
        if (tab.Main is not { } main) return;
        void OnTabs(object? s, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (!main.Tabs.Contains(tab)) window.Close();
        }
        main.Tabs.CollectionChanged += OnTabs;
        window.Closed += (_, _) => main.Tabs.CollectionChanged -= OnTabs;
    }
}
