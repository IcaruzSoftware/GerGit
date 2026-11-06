using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace GerGit.ViewModels;

/// <summary>The Repository Manager tab kind.</summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>The Repository Manager tab, null when there is none (at most one). It is always the last tab: it is
    /// removed whenever a repository opens, and repository tabs are only added by opening. It is the selected tab exactly
    /// when <see cref="SelectedTab"/> is null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedStripTab), nameof(IsManagerShown))]
    public partial RepositoryManagerViewModel? Manager { get; private set; }

    partial void OnManagerChanged(RepositoryManagerViewModel? value) => SyncStrip(StripTabs);

    public bool IsManagerShown => SelectedTab is null && Manager is not null;

    /// <summary>The tab strip: the repository tabs, then the Repository Manager tab.</summary>
    public ObservableCollection<object> StripTabs => field ??= CreateStrip();

    ObservableCollection<object> CreateStrip()
    {
        var strip = new ObservableCollection<object>();
        Tabs.CollectionChanged += (_, _) => SyncStrip(strip);
        SyncStrip(strip);
        return strip;
    }

    void SyncStrip(ObservableCollection<object> strip)
    {
        var want = Tabs.Cast<object>().Concat(Manager is null ? [] : [Manager]).ToList();
        for (int i = 0; i < want.Count; i++)
        {
            int at = strip.IndexOf(want[i]);
            if (at == i) continue;
            if (at >= 0) strip.Move(at, i);
            else strip.Insert(i, want[i]);
        }
        while (strip.Count > want.Count) strip.RemoveAt(strip.Count - 1);
        OnPropertyChanged(nameof(SelectedStripTab)); // a move may have cleared the list box selection
    }

    /// <summary>The tab strip's selection: the repository tab, or the Repository Manager tab.</summary>
    public object? SelectedStripTab
    {
        get => SelectedTab ?? (object?)Manager;
        set
        {
            if (value is RepoTabViewModel tab) SelectedTab = tab;
            else if (value is RepositoryManagerViewModel) SelectedTab = null;
        }
    }

    /// <summary>New Tab ("+", Ctrl+T, File > New Tab): selects the Repository Manager tab, created at the end when there
    /// is none.</summary>
    public void NewTab()
    {
        Manager ??= new RepositoryManagerViewModel(this);
        SelectedTab = null;
    }

    /// <summary>Closes the Repository Manager tab; the tab to its left is selected. The only tab stays (a fresh one would
    /// replace it).</summary>
    public void CloseManager()
    {
        if (Manager is null || Tabs.Count == 0) return;
        bool wasSelected = SelectedTab is null;
        RemoveManager();
        if (wasSelected) SelectedTab = Tabs[^1];
    }

    void RemoveManager()
    {
        Manager?.Dispose();
        Manager = null;
    }

    readonly CancellationTokenSource _scanCts = new();
    Task<bool>? _sourceScan;

    /// <summary>Repository scan each time Quick Launch opens: new repositories are added and the Repository
    /// Manager tree (if any) reloads; true when something was added. One scan at a time: a running one is shared.</summary>
    public Task<bool> ScanSourcesAsync() =>
        _sourceScan is { IsCompleted: false } running ? running : _sourceScan = RunSourceScanAsync();

    async Task<bool> RunSourceScanAsync()
    {
        if (await RepositoryManagerViewModel.ScanSourcesAsync(reset: false, _scanCts.Token) <= 0) return false;
        Manager?.Reload(Manager.SelectedNode?.Path);
        return true;
    }

    /// <summary>The main window closed: a running source scan stops.</summary>
    public void StopSourceScan() => _scanCts.Cancel();
}
