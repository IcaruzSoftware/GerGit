using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GerGit.Core;
using GerGit.Core.Git;
using GerGit.ViewModels.Dialogs;
using GerGit.Views;
using GerGit.Views.Dialogs;

namespace GerGit.ViewModels;

/// <summary>Why opening a folder failed: dialog title, text and whether "Initialize git repository here" is offered.</summary>
public sealed record OpenFailure(string Title, string Text, bool CanInit = false);

/// <summary>Main window: repository tabs and the toolbar state.</summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    static Task<(Version? Version, string Error)>? s_gitVersion;
    bool _activatedOnce;
    bool _sessionRestored; // until then nothing is saved: a failed start must not wipe the stored session

    public ObservableCollection<RepoTabViewModel> Tabs { get; } = [];

    /// <summary>Null exactly when the selected tab is not a repository tab: the Repository Manager tab (welcome texts),
    /// or before the session restore.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedStripTab), nameof(IsManagerShown))]
    public partial RepoTabViewModel? SelectedTab { get; set; }

    /// <summary>Job display in the toolbar centre.</summary>
    public StatusBoxViewModel StatusBox { get; } = new();

    /// <summary>Activity Manager popup.</summary>
    public ActivityViewModel Activity { get; } = new();

    /// <summary>The bell and notifications popup: app-wide, from the current hosting service.</summary>
    public NotificationsViewModel Notifications => HostingService.Current.Notifications;

    /// <summary>Automatic background fetch over all tabs; its timer is started by the desktop app.</summary>
    public AutoFetch AutoFetch => field ??= new(this);

    /// <summary>Every 200 ms (main window timer): status box and, while open, the Activity Manager.</summary>
    public void Tick(DateTime utcNow)
    {
        StatusBox.Update(SelectedTab, utcNow);
        if (Activity.IsOpen) Activity.Refresh(SelectedTab?.Jobs);
    }

    /// <summary>Click on the status box title/description or the list button (only with a repository tab).</summary>
    public void OpenActivity()
    {
        if (SelectedTab is not null) Activity.Open(SelectedTab.Jobs);
    }

    partial void OnSelectedTabChanged(RepoTabViewModel? oldValue, RepoTabViewModel? newValue)
    {
        Tick(DateTime.UtcNow);
        // Watch only the new tab and refresh it.
        if (oldValue is not null) oldValue.IsActive = false;
        // A restored tab passed through before its first load stops loading; it loads again when selected.
        if (oldValue is { IsLoaded: false }) oldValue.CancelRefresh();
        SaveSession();
        if (newValue is null) return;
        newValue.IsActive = true;
        _ = newValue.RefreshAsync(); // also the first load of a restored tab; OpenAsync awaits it through Refreshing
    }

    /// <summary>Null when git is usable, else the message to show.</summary>
    public static async Task<OpenFailure?> CheckGitAsync()
    {
        var (version, error) = await (s_gitVersion ??= GitRunner.ProbeVersionAsync());
        return GitFailure(version, error);
    }

    /// <summary>The message of <see cref="CheckGitAsync"/>: git not found, or another start failure (a missing setsid or
    /// env) with its own text, or a git that is too old.</summary>
    internal static OpenFailure? GitFailure(Version? version, string error)
    {
        if (version is null)
            return new("Git Error", error.Length == 0 || error.StartsWith("Cannot find git instance", StringComparison.Ordinal)
                ? $"Cannot find git instance at: '{GitRunner.GitPath}'" : error);
        if (version < GitRunner.MinimumVersion)
            return new("Git Error", $"Git {version} is too old. GerGit needs git {GitRunner.MinimumVersion} or newer.");
        return null;
    }

    /// <summary>Opens the folder as a tab or selects its existing tab; a new
    /// tab goes directly after <paramref name="after"/> (worktrees opened from a repository tab), else at the end.</summary>
    public async Task<OpenFailure?> OpenAsync(string path, RepoTabViewModel? after = null)
    {
        if (await CheckGitAsync() is { } gitFailure) return gitFailure;
        path = Path.GetFullPath(path); // relative command-line paths resolve against the working directory
        GitRepository repo;
        try
        {
            repo = await GitRepository.OpenAsync(path);
        }
        catch (GitException e) when (e.Message == "[internal] Not found")
        {
            return new("Repository Not Found", $"It looks like '{path}' was deleted or moved.");
        }
        catch (GitException e) when (e.Message == $"The '{path}' directory is not a git repository")
        {
            return new("The directory is not under git source control", e.Message, CanInit: true);
        }
        catch (GitException e)
        {
            // Unsafe repository and other git errors: the Git Error window with git's text.
            return new("Git Error", e.Message);
        }

        var tab = TabOf(repo.Info.WorkTree);
        if (tab is null)
        {
            tab = new RepoTabViewModel(repo) { Main = this };
            int index = after is null ? -1 : Tabs.IndexOf(after);
            if (index < 0) Tabs.Add(tab);
            else Tabs.Insert(index + 1, tab);
            UpdateTitles();
            SelectedTab = tab;
            AfterOpen(repo);
            while (!tab.Refreshing.IsCompleted) await tab.Refreshing; // a newer refresh replaced ours: wait for the latest
        }
        else
        {
            SelectedTab = tab;
            AfterOpen(repo);
        }
        return null;
    }

    /// <summary>The Repository Manager tab goes, and an ordinary repository is recorded as opened.</summary>
    void AfterOpen(GitRepository repo)
    {
        RemoveManager();
        if (repo.Info.Kind == RepoKind.Repository) RepositoryList.RecordOpened(repo.Info.WorkTree, DateTimeOffset.UtcNow);
    }

    /// <summary>"Initialize git repository here" of the not-a-repository alert.</summary>
    public async Task<OpenFailure?> InitAsync(string path)
    {
        var r = await GitRunner.RunAsync(path, ["init"]);
        return r.Success ? await OpenAsync(path) : new("Git Error", r.ReadableOutput);
    }

    /// <summary>Opens a dialog that needs no repository tab (Clone); true = closed by submit. Replaceable in tests.</summary>
    internal Func<DialogViewModel, Task<bool>> ShowDialog { get; set; } = vm => DialogWindow.ShowAsync(vm);

    /// <summary>File > Clone...; the clone opens as a tab.</summary>
    public Task OpenCloneAsync() => ShowDialog(new CloneViewModel(this, HostingService.Current));

    /// <summary>Help > About GerGit.</summary>
    public Task OpenAboutAsync() => ShowDialog(new AboutViewModel());

    /// <summary>Closes the tab (default: the selected one, which may be the Repository Manager tab); selects its left
    /// neighbour, or the next one. Without tabs left the Repository Manager tab opens.</summary>
    [RelayCommand]
    public void CloseTab(RepoTabViewModel? tab)
    {
        tab ??= SelectedTab;
        if (tab is null)
        {
            CloseManager();
            return;
        }
        int index = Tabs.IndexOf(tab);
        bool wasSelected = tab == SelectedTab;
        Tabs.Remove(tab);
        tab.IsActive = false;
        tab.Dispose();
        if (wasSelected) SelectedTab = Tabs.Count == 0 ? null : Tabs[Math.Max(0, index - 1)];
        if (Tabs.Count == 0) NewTab();
        UpdateTitles();
        SaveSession();
    }

    /// <summary>The tab of a deleted worktree closes and its main repository's tab (if open) is selected.</summary>
    public void CloseWorktreeTab(RepoTabViewModel tab)
    {
        if (!Tabs.Contains(tab)) return;
        var main = TabOf(tab.Repo.MainWorkTree);
        CloseTab(tab);
        if (main is not null) SelectedTab = main;
    }

    /// <summary>The open tab of this folder (compared ignoring case, unless <paramref name="comparison"/> says
    /// otherwise), else null.</summary>
    public RepoTabViewModel? TabOf(string path, StringComparison comparison = StringComparison.OrdinalIgnoreCase) =>
        Tabs.FirstOrDefault(t => string.Equals(t.Path.TrimEnd('/'), path.TrimEnd('/'), comparison));

    [RelayCommand]
    Task Refresh() => SelectedTab?.RefreshAsync() ?? Task.CompletedTask;

    /// <summary>
    /// The first activation is ignored, and so is the one after a dialog closed, and
    /// any while Quick Launch is open or within <see cref="QuickLaunchGraceMs"/> after it closed;
    /// otherwise the active tab refreshes.
    /// </summary>
    /// <param name="nowMs">Monotonic milliseconds (<see cref="Environment.TickCount64"/>); tests pass their own.</param>
    public void OnWindowActivated(long? nowMs = null)
    {
        bool dialogClosed = MessageWindow.DialogClosed;
        MessageWindow.DialogClosed = false;
        if (!_activatedOnce) { _activatedOnce = true; return; }
        bool quickLaunch = IsQuickLaunchOpen
            || QuickLaunchClosedMs is { } closed && (nowMs ?? Environment.TickCount64) - closed < QuickLaunchGraceMs;
        if (!dialogClosed && !quickLaunch) _ = SelectedTab?.RefreshAsync();
    }

    /// <summary>The activation that follows a Quick Launch close comes at once; a later one refreshes again.</summary>
    internal const long QuickLaunchGraceMs = 500;

    /// <summary>Set by the main window while its Quick Launch window is open.</summary>
    internal bool IsQuickLaunchOpen { get; set; }

    /// <summary>When the Quick Launch window last closed (<see cref="Environment.TickCount64"/>), null = never.</summary>
    internal long? QuickLaunchClosedMs { get; set; }

    /// <summary>
    /// Tab titles: the alias from the repository list, else the folder name plus " (first differing path component)"
    /// when another tab has the same name. A worktree gets "&lt;main repository alias or folder name&gt;: " in front.
    /// Limitation: names are compared against open tabs only, not the whole repository list; no submodule prefix.
    /// </summary>
    internal void UpdateTitles()
    {
        var aliases = RepositoryList.Entries.Where(e => e.Alias is not null).DistinctBy(e => e.Path).ToDictionary(e => e.Path, e => e.Alias!);
        foreach (var t in Tabs)
        {
            if (t.Repo.Info.Kind == RepoKind.Repository && aliases.TryGetValue(t.Path, out var alias))
            {
                t.Title = alias;
                continue;
            }
            var name = t.Repo.Info.Name;
            var other = Tabs.FirstOrDefault(o => o != t && o.Repo.Info.Name == name);
            var title = other is null ? name : $"{name} ({FirstDifference(t.Path, other.Path)})";
            // Worktree prefix: the main repository's alias from the repository list, else its folder name.
            var parent = aliases.GetValueOrDefault(t.Repo.MainWorkTree) ?? Path.GetFileName(t.Repo.MainWorkTree);
            t.Title = t.Repo.Info.Kind == RepoKind.Worktree ? $"{parent}: {title}" : title;
        }
    }

    internal static string FirstDifference(string a, string b)
    {
        var pa = a.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var pb = b.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < pa.Length; i++)
            if (i >= pb.Length || pa[i] != pb[i]) return pa[i];
        return pa[^1];
    }
}
