using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GerGit.Core;
using GerGit.Core.Git;
using GerGit.ViewModels.Dialogs;

namespace GerGit.ViewModels;

/// <summary>The blame entry of one document line of the blame view; Commit null = no commit (hunk header, service
/// line, a line git did not blame). Only the first row of a run with the same commit shows it.</summary>
public sealed record BlameRow(CommitHeader? Commit, bool IsGroupStart)
{
    public bool ShowsCommit => IsGroupStart && Commit is not null;
}

/// <summary>
/// Blame window: the file as of the selected commit R in the shared diff viewer (entire-file mode, R's own
/// changes highlighted) and, per document line, the commit that last touched it (added lines: R; context and deleted
/// lines: `git blame` of R~ by the old line number). Back/forward history of visited commits.
/// Not built yet: no Revision window (`Open in Separate Window...`, the row icon button) and no find bar (Ctrl+F / F3; the
/// diff viewer has no search yet).
/// </summary>
public sealed partial class BlameViewModel : ObservableObject
{
    public const string TextOnly = "Blame can only be used for text files";

    readonly string? _sha;
    readonly List<RevisionItem> _visited = [];
    int _position = -1;
    bool _stepping;
    CancellationTokenSource? _cts;
    IReadOnlyList<CommitHeader?>? _blame; // blame of the shown revision's parent; null while loading
    RevisionItem? _blamed;
    bool _closed;

    public BlameViewModel(RepoTabViewModel tab, string path, string? sha)
    {
        Tab = tab;
        Path = path;
        _sha = sha;
        Diff = new DiffViewModel(tab.Repo) { EntireFile = true, ShowContextControls = false };
        Diff.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(DiffViewModel.Document) or nameof(DiffViewModel.State)) Rebuild();
        };
    }

    public RepoTabViewModel Tab { get; }
    public string Path { get; }
    public DiffViewModel Diff { get; }
    public string Title => $"{System.IO.Path.GetFileName(Path)} - Blame";

    /// <summary>Delay before a combo box change reloads; tests set 0.</summary>
    internal TimeSpan Delay { get; set; } = TimeSpan.FromMilliseconds(150);

    /// <summary>The finished load of the current revision (tests await it).</summary>
    public Task Loading { get; private set; } = Task.CompletedTask;

    /// <summary>The file's history, newest first (combo box).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Dates))]
    public partial IReadOnlyList<RevisionItem> Revisions { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedDates))]
    public partial RevisionItem? SelectedRevision { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLoadingText))]
    public partial IReadOnlyList<BlameRow> Rows { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLoadingText))]
    public partial bool IsLoading { get; private set; }

    /// <summary>Right-side `Error` fallback (git error, `Blame can only be used for text files`); null = the diff viewer.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorText { get; private set; }

    public bool HasError => ErrorText is not null;
    public bool ShowLoadingText => IsLoading && Rows.Count == 0;
    public IReadOnlyList<DateTimeOffset> Dates => Revisions.Select(r => r.Commit.AuthorDate).ToList();
    public IReadOnlyList<DateTimeOffset> SelectedDates => SelectedRevision is { } r ? [r.Commit.AuthorDate] : [];

    /// <summary>Go To Line: the 1-based line of the displayed document to scroll to (the view listens).</summary>
    public event Action<int>? GoToLineRequested;

    internal Func<DialogViewModel, Task<bool>> ShowDialog { get; set; } = _ => Task.FromResult(false);
    internal Func<string, Task> Copy { get; set; } = _ => Task.CompletedTask;
    internal Func<string, string?, Task<string?>> PickSaveFile { get; set; } = (_, _) => Task.FromResult<string?>(null);
    /// <summary>Error message over this window (set by the window); null = the tab's, over the main window.</summary>
    internal Func<string, string, Task>? ShowError { get; set; }

    /// <summary>
    /// Opening: start = the newest commit at or before the given one (HEAD without one) that touched the
    /// file; the history is read from the best tip of that commit.
    /// </summary>
    public Task LoadAsync() => Loading = LoadCoreAsync();

    async Task LoadCoreAsync()
    {
        IsLoading = true;
        try
        {
            var start = await Tab.Repo.FindBlameStartAsync(_sha, Path);
            // A file without history (e.g. untracked) gets its own message.
            var noHistory = $"'{Path}' has no committed history";
            if (start is null)
            {
                ErrorText = noHistory;
                return;
            }
            var tip = await Tab.Repo.FindBestTipAsync(start, Tab.Refs);
            var history = await Tab.Repo.ReadFileHistoryAsync(tip?.Sha ?? start, Path);
            Revisions = await Task.Run(() => RevisionItem.List(history, Path));
            SelectedRevision = Revisions.FirstOrDefault(r => r.Sha == start) ?? Revisions.FirstOrDefault();
            // Loading is still this method's own task when no revision load started (empty history, window closed).
            if (SelectedRevision is null) ErrorText = noHistory;
            else if (!_closed) await Loading;
        }
        catch (GitException e) { ErrorText = e.Message; }
        finally { IsLoading = false; }
    }

    partial void OnSelectedRevisionChanged(RevisionItem? value)
    {
        if (value is null || _closed) return;
        if (!_stepping && (_position < 0 || _visited[_position] != value))
        {
            // A new commit drops the forward history; consecutive duplicates are not added.
            _visited.RemoveRange(_position + 1, _visited.Count - _position - 1);
            _visited.Add(value);
            _position = _visited.Count - 1;
        }
        GoBackCommand.NotifyCanExecuteChanged();
        GoForwardCommand.NotifyCanExecuteChanged();
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        Loading = LoadRevisionAsync(value, cts.Token);
    }

    async Task LoadRevisionAsync(RevisionItem rev, CancellationToken ct)
    {
        try
        {
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
            ct.ThrowIfCancellationRequested(); // Close() may run between the delay's end and this continuation
            IsLoading = true;
            ErrorText = null;
            _blame = null;
            Rebuild();
            Diff.Show(new DiffRequest(null, rev.Sha, rev.Path, rev.OldPath));
            // The parent's lines are blamed under the path they had there.
            var blame = Task.Run(() => Tab.Repo.ReadBlameParentAsync(rev.Sha, rev.OldPath ?? rev.Path, ct), ct);
            await Diff.Loading;
            var lines = await blame;
            ct.ThrowIfCancellationRequested();
            (_blame, _blamed) = (lines, rev);
            Rebuild();
        }
        catch (Exception e) when (e is OperationCanceledException || ct.IsCancellationRequested) { }
        catch (GitException e) { ErrorText = e.Message; }
        finally
        {
            if (!ct.IsCancellationRequested) IsLoading = false;
        }
    }

    /// <summary>The window closed: stops the running load and frees the diff viewer's bitmaps; no further loads.</summary>
    public void Close()
    {
        _closed = true;
        _cts?.Cancel();
        Diff.Show(null);
    }

    /// <summary>Rows for the shown document; `Blame can only be used for text files` when the diff has no hunks.</summary>
    void Rebuild()
    {
        if (_blame is null || _blamed is null || Diff.State is DiffState.None)
        {
            Rows = [];
            return;
        }
        bool text = Diff.State == DiffState.Text && Diff.FileDiff is { Hunks.Count: > 0 } && Diff.Document is not null;
        if (!text && Diff.State is DiffState.Text or DiffState.Binary or DiffState.Image) ErrorText = TextOnly;
        Rows = text ? BuildRows(Diff.Document!, _blame, _blamed.Commit) : [];
    }

    /// <summary>One row per document line: added lines belong to <paramref name="commit"/>, context and deleted lines to
    /// the parent blame entry of their old line number.</summary>
    internal static List<BlameRow> BuildRows(DiffDocument doc, IReadOnlyList<CommitHeader?> blame, CommitHeader commit)
    {
        var rows = new List<BlameRow>(doc.Lines.Count);
        string? previous = null;
        foreach (var line in doc.Lines)
        {
            var c = line.Kind switch
            {
                DocLineKind.Added => commit,
                DocLineKind.Context or DocLineKind.Removed when line.OldNumber is { } n && n >= 1 && n <= blame.Count => blame[n - 1],
                _ => null,
            };
            rows.Add(new BlameRow(c, c is not null && c.Sha != previous));
            previous = c?.Sha;
        }
        return rows;
    }

    /// <summary>The SHA button: selects that commit when it is in the file's history, else nothing happens.</summary>
    public void Navigate(string sha)
    {
        if (Revisions.FirstOrDefault(r => r.Sha == sha) is { } r) SelectedRevision = r;
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    void GoBack() => Step(-1);
    bool CanGoBack() => _position > 0;

    [RelayCommand(CanExecute = nameof(CanGoForward))]
    void GoForward() => Step(1);
    bool CanGoForward() => _position >= 0 && _position < _visited.Count - 1;

    void Step(int by)
    {
        _position += by;
        _stepping = true;
        try { SelectedRevision = _visited[_position]; }
        finally { _stepping = false; }
    }

    /// <summary>Ctrl+G: `Go To Line`; a valid integer scrolls to that document line, anything else does nothing.</summary>
    public async Task GoToLineAsync()
    {
        var dialog = new GoToLineViewModel();
        if (await ShowDialog(dialog) && dialog.Line is { } n) GoToLineRequested?.Invoke(n);
    }

    /// <summary>"Blame window menu" for a row with a commit. The file of `Save as...` is the path blamed for
    /// that row (Limitation: a file renamed before that commit is looked up under its later name and fails).</summary>
    public IReadOnlyList<MenuEntry> Menu(CommitHeader c)
    {
        var path = c.Sha == _blamed?.Sha || _blamed is null ? _blamed?.Path ?? Path : _blamed.OldPath ?? _blamed.Path;
        return Menus.Join(
            [Menus.Item("Reveal in GerGit", () => RevealAsync(c.Sha))],
            [Menus.Item("Save as...", () => RevisionActions.SaveAsAsync(Tab, PickSaveFile, c.Sha, path, ShowError))],
            [Menus.Item("Copy Commit SHA", () => Copy(c.Sha)), Menus.Item("Copy Commit Info", () => Copy(RevisionActions.Info(c)))]);
    }

    public Task RevealAsync(string sha)
    {
        RevisionActions.Reveal(Tab, sha);
        return Task.CompletedTask;
    }
}
