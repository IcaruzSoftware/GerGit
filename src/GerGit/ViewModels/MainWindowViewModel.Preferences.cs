using GerGit.ViewModels.Preferences;

namespace GerGit.ViewModels;

public sealed partial class MainWindowViewModel
{
    /// <summary>File > Preferences... and Ctrl+,: afterwards the commit boxes show the new length limits
    /// and the active repository reloads (sort order and labels may have changed); the Repository Manager reloads and
    /// scans (the source folders may have changed).
    /// Limitation: an open interactive rebase window keeps the old limits until its next keystroke.</summary>
    public async Task OpenPreferencesAsync()
    {
        await ShowDialog(new PreferencesViewModel());
        foreach (var t in Tabs) t.LocalChanges.OnSubjectLimitsChanged();
        Manager?.Refresh();
        if (SelectedTab is { } tab) await tab.RefreshAsync();
    }
}
