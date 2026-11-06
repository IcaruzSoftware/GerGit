using CommunityToolkit.Mvvm.ComponentModel;

namespace GerGit.ViewModels.Dialogs;

/// <summary>Rescan Repositories: rescans all source folders; with "Reset" the list is replaced by the result.
/// While it runs the window cannot be closed; "Done" shows for 1.5 seconds, then it closes.</summary>
public sealed partial class RescanViewModel : DialogViewModel
{
    readonly RepositoryManagerViewModel _manager;

    /// <summary>How long "Done" stays (tests shorten it).</summary>
    internal static TimeSpan DoneDelay { get; set; } = TimeSpan.FromSeconds(1.5);

    public RescanViewModel(RepositoryManagerViewModel manager)
    {
        _manager = manager;
        Title = "Rescan repositories in source folder";
        // The folders are quoted once each.
        Description = $"Do you want to rescan repositories in {string.Join(", ", AppPreferences.SourceFolders.Select(f => $"'{f}'"))}?";
        SubmitText = "Rescan";
        Width = 570;
    }

    [ObservableProperty]
    public partial bool Reset { get; set; }

    protected override async Task OnSubmitAsync()
    {
        IsBusy = true;
        SetStatus(DialogStatus.InProgress, "Scanning repositories...");
        try { await _manager.RescanAsync(Reset); }
        finally { IsBusy = false; }
        SetStatus(DialogStatus.Success, "Done");
        await Task.Delay(DoneDelay);
        Close(true);
    }
}
