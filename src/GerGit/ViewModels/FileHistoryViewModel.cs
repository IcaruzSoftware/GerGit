using System.Globalization;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Selection;
using CommunityToolkit.Mvvm.ComponentModel;
using GerGit.Core;
using GerGit.Core.Git;

namespace GerGit.ViewModels;

/// <summary>One commit of a file history (Blame combo box, File History rows). The file entry is the commit's change
/// to the file (file mode: exactly one; folder mode: the first). A commit without one (a merge: git prints no files)
/// gets a Modified entry at <paramref name="path"/>, the path the next newer entry had before it.</summary>
public sealed class RevisionItem(FileRevision revision, string path)
{
    public FileRevision Revision { get; } = revision;
    public CommitHeader Commit => Revision.Commit;
    public string Sha => Commit.Sha;
    public string Sha7 => Sha[..Math.Min(7, Sha.Length)];
    public string Subject => Commit.Subject;
    public string AuthorName => Commit.AuthorName;
    public string AuthorEmail => Commit.AuthorEmail;
    public string Date => RevisionActions.ShortDate(Commit.AuthorDate);
    public string DateTip => Commit.AuthorDate.ToLocalTime().ToString("d MMM yyyy HH:mm", CultureInfo.InvariantCulture);
    public FileChange File { get; } = revision.Files.FirstOrDefault() ?? new FileChange(ChangeKind.Modified, path, null);
    /// <summary>The file's path as of this commit; OldPath = its path before (renames and copies only).</summary>
    public string Path => File.Path;
    public string? OldPath => File.OldPath;
    public ChangeKind Kind => File.Kind;
    public string PathText => OldPath is { } o ? $"{o} → {Path}" : Path;

    /// <summary>Items for a history (newest first) of <paramref name="path"/>: each entry without files takes the
    /// path the next newer entry had before its commit.</summary>
    public static List<RevisionItem> List(IReadOnlyList<FileRevision> revisions, string path)
    {
        var items = new List<RevisionItem>(revisions.Count);
        foreach (var r in revisions)
        {
            var item = new RevisionItem(r, path);
            items.Add(item);
            path = item.OldPath ?? item.Path;
        }
        return items;
    }
}

public enum HistoryRowKind { Revision, Commit, File }

/// <summary>A File History row: Revision = file mode (one row per commit); folder mode = a Commit row followed by
/// its File rows.</summary>
public sealed class HistoryRow(RevisionItem revision, HistoryRowKind kind, FileChange? file = null)
{
    public RevisionItem Revision { get; } = revision;
    public HistoryRowKind Kind { get; } = kind;
    public FileChange? File { get; } = kind == HistoryRowKind.Revision ? revision.File : file;
    public bool IsRevision => Kind == HistoryRowKind.Revision;
    public bool IsCommit => Kind == HistoryRowKind.Commit;
    public bool IsFile => Kind == HistoryRowKind.File;
    public ChangeKind FileKind => File?.Kind ?? ChangeKind.Modified;
    public string FilePath => File is { OldPath: { } o } f ? $"{o} → {f.Path}" : File?.Path ?? "";
}

/// <summary>Actions shared by the Blame and File History windows.</summary>
public static class RevisionActions
{
    // English month names (invariant culture), local time, as in the details pane.
    public static string ShortDate(DateTimeOffset d) => d.ToLocalTime().ToString("d MMM yyyy", CultureInfo.InvariantCulture);

    /// <summary>`Copy Commit Info` line: "&lt;sha7&gt; - &lt;subject&gt;" (as in the commit list).</summary>
    public static string Info(CommitHeader c) => $"{c.Sha[..Math.Min(7, c.Sha.Length)]} - {c.Subject}";

    /// <summary>`Reveal in GerGit` / double-click: the tab and the commit in the main window, which is activated.</summary>
    public static void Reveal(RepoTabViewModel tab, string sha)
    {
        if (tab.Main is { } main && main.Tabs.Contains(tab)) main.SelectedTab = tab;
        tab.OpenCommit(sha);
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow?.Activate();
    }

    /// <summary>`Save as...`: the file as of <paramref name="rev"/> to a chosen location; errors as
    /// error dialog. <paramref name="pick"/> = the save dialog (suggested name, start folder) → path or null.</summary>
    public static async Task SaveAsAsync(RepoTabViewModel tab, Func<string, string?, Task<string?>> pick, string rev, string path,
        Func<string, string, Task>? showError = null)
    {
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.Combine(tab.Path, path));
        var target = await pick(System.IO.Path.GetFileName(path), Directory.Exists(dir) ? dir : null);
        if (target is null) return;
        try { await System.IO.File.WriteAllBytesAsync(target, await tab.Repo.ReadFileAtAsync(rev, path)); }
        catch (Exception e) when (e is GitException or IOException or UnauthorizedAccessException)
        {
            await (showError ?? tab.ShowError)("Error", e.Message);
        }
    }

    /// <summary>`Open`: the working-tree file with the default application; nothing when it does not exist.</summary>
    public static Task OpenAsync(RepoTabViewModel tab, string path)
    {
        var full = System.IO.Path.Combine(tab.Path, path);
        return System.IO.File.Exists(full) ? tab.Hosting.OpenPath(full) : Task.CompletedTask;
    }

    /// <summary>`Show in File Explorer`: the file's folder in the working tree (the repository root when it is gone).
    /// Limitation: xdg-open on the folder, the file is not selected.</summary>
    public static Task ShowInFileManagerAsync(RepoTabViewModel tab, string path)
    {
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.Combine(tab.Path, path));
        return tab.Hosting.OpenPath(Directory.Exists(dir) ? dir : tab.Path);
    }

    /// <summary>The reset file confirmation and its job (named like the button): each file to
    /// <paramref name="target"/> (`&lt;sha&gt;` or `&lt;sha&gt;~`), stopping at the first failure; then the Local Changes view.
    /// <paramref name="before"/> picks the "before the commit" texts; <paramref name="files"/> = the number of files for
    /// the texts when <paramref name="paths"/> also holds old paths of renames. <paramref name="confirm"/> = the
    /// confirmation over the calling window (default: the tab's, over the main window).</summary>
    public static async Task ResetFilesAsync(RepoTabViewModel tab, string target, IReadOnlyList<string> paths, bool before, int? files = null,
        Func<string, string, string, double, Task<bool>>? confirm = null)
    {
        int count = files ?? paths.Count;
        bool one = count == 1;
        var title = (one, before) switch
        {
            (true, false) => "Do you want to reset the file to the state it is at the commit?",
            (false, false) => $"Do you want to reset {count} files to the state they are at the commit?",
            (true, true) => "Do you want to reset the file to the state it was before the commit?",
            (false, true) => $"Do you want to reset {count} files to the state they were before the commit?",
        };
        var button = one ? "Reset File" : "Reset Files";
        if (!await (confirm ?? tab.Confirm)(title, "The current file changes will be discarded.", button, 490)) return;
        await tab.RunJobAsync(button, async job =>
        {
            GitResult? last = null;
            foreach (var path in paths)
                if (!(last = await tab.Repo.ResetFileToAsync(job, target, path)).Success) break;
            return last;
        }, afterRefresh: () =>
        {
            tab.ShowLocalChanges();
            return Task.CompletedTask;
        });
    }

    /// <summary>Ctrl+Alt+Shift+O: the file as of the commit through a temp copy, opened with the default application
    /// (or its folder shown, see <see cref="HostingService.SafeToOpen"/>). The copy lives in a run folder of the external
    /// tools, which is removed when GerGit exits. Limitation: a viewer that still has the copy open then loses the file
    /// (an unsaved edit in it is lost as well).</summary>
    public static async Task OpenAtAsync(RepoTabViewModel tab, string rev, string path, Func<string, string, Task>? showError = null)
    {
        try
        {
            // A fresh private folder per open (0700, unpredictable name): no planted links, no overwrites.
            var file = System.IO.Path.Combine(ExternalTools.NewRunFolder(tab.Path), System.IO.Path.GetFileName(path));
            await System.IO.File.WriteAllBytesAsync(file, await tab.Repo.ReadFileAtAsync(rev, path));
            await tab.Hosting.OpenPath(file);
        }
        catch (Exception e) when (e is GitException or IOException or UnauthorizedAccessException)
        {
            await (showError ?? tab.ShowError)("Error", e.Message);
        }
    }
}

/// <summary>
/// File History window: the commits that changed a file (or a folder), the timeline and the diff of the
/// selected row(s) in the shared diff viewer. Not built yet: no `Selected lines` mode (the diff editor has no context
/// menu yet), no Revision window (`Open in Separate Window...` and the row icon button) and no custom commands. Entry points: the Local Changes and details-pane file menus; the sidebar submodule menu
/// (`History...`) and the File Tree tab do not exist yet.
/// </summary>
public sealed partial class FileHistoryViewModel : ObservableObject
{
    readonly string? _sha;
    bool _closed;

    public FileHistoryViewModel(RepoTabViewModel tab, string path, bool isFolder, string? sha)
    {
        Tab = tab;
        Path = path;
        IsFolder = isFolder;
        _sha = sha;
        Diff = new DiffViewModel(tab.Repo) { LayoutKey = DiffViewModel.HistoryLayoutKey };
        Selection = new SelectionModel<HistoryRow> { SingleSelect = isFolder };
        Selection.SelectionChanged += (_, _) => ShowSelection();
    }

    public RepoTabViewModel Tab { get; }
    public string Path { get; }
    public bool IsFolder { get; }
    public DiffViewModel Diff { get; }
    public SelectionModel<HistoryRow> Selection { get; }
    /// <summary>File mode: multi-select (two rows = the diff between them); folder mode: single.</summary>
    public Avalonia.Controls.SelectionMode SelectionMode =>
        IsFolder ? Avalonia.Controls.SelectionMode.Single : Avalonia.Controls.SelectionMode.Multiple;
    public string Name => System.IO.Path.GetFileName(Path.TrimEnd('/'));
    public string Title => $"{Name} - History";
    public string Icon => IsFolder ? "folder" : Tab.Submodules.Any(s => s.Path == Path) ? "package" : "file";

    /// <summary>The finished first load (tests await it).</summary>
    public Task Loading { get; private set; } = Task.CompletedTask;

    [ObservableProperty] public partial IReadOnlyList<HistoryRow> Rows { get; private set; } = [];
    [ObservableProperty] public partial bool IsLoading { get; private set; }

    /// <summary>The branch the history starts from (ref label in the top row); null = none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTip))]
    public partial string? TipName { get; private set; }

    public bool HasTip => TipName is not null;

    /// <summary>Overlay over the diff side; null = none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOverlay))]
    public partial string? OverlayTitle { get; private set; }

    [ObservableProperty] public partial string? OverlayText { get; private set; }
    public bool HasOverlay => OverlayTitle is not null;

    [ObservableProperty] public partial IReadOnlyList<DateTimeOffset> Dates { get; private set; } = [];
    [ObservableProperty] public partial IReadOnlyList<DateTimeOffset> SelectedDates { get; private set; } = [];

    internal Func<string, Task> Copy { get; set; } = _ => Task.CompletedTask;
    internal Func<string, string?, Task<string?>> PickSaveFile { get; set; } = (_, _) => Task.FromResult<string?>(null);
    /// <summary>Confirmation and error message over this window (set by the window); null = the tab's, over the main window.</summary>
    internal Func<string, string, string, double, Task<bool>>? Confirm { get; set; }
    internal Func<string, string, Task>? ShowError { get; set; }

    /// <summary>Reads the history once; selects the commit the window was opened from, else the first file entry.</summary>
    public Task LoadAsync() => Loading = LoadCoreAsync();

    async Task LoadCoreAsync()
    {
        IsLoading = true;
        try
        {
            var start = _sha ?? Tab.Refs.HeadSha;
            var tip = start is null ? null : await Tab.Repo.FindBestTipAsync(start, Tab.Refs);
            TipName = tip?.RefName;
            var history = await Tab.Repo.ReadFileHistoryAsync(tip?.Sha ?? start, Path);
            var (revisions, rows) = await Task.Run(() =>
            {
                var items = RevisionItem.List(history, Path);
                return (items, IsFolder
                    ? items.SelectMany(r => r.Revision.Files.Select(f => new HistoryRow(r, HistoryRowKind.File, f)).Prepend(new HistoryRow(r, HistoryRowKind.Commit))).ToList()
                    : items.Select(r => new HistoryRow(r, HistoryRowKind.Revision)).ToList());
            });
            Rows = rows;
            Dates = revisions.Select(r => r.Commit.AuthorDate).ToList();
            Selection.Source = Rows;
            int index = Rows.ToList().FindIndex(r => r.Revision.Sha == _sha && !r.IsCommit);
            if (index < 0) index = Rows.ToList().FindIndex(r => !r.IsCommit);
            if (index >= 0) Selection.Select(index);
        }
        catch (GitException e) { SetOverlay("Error", e.Message); }
        finally { IsLoading = false; }
    }

    /// <summary>Selected rows in list order (newest first).</summary>
    public IReadOnlyList<HistoryRow> SelectedRows => Selection.SelectedIndexes.Order().Select(i => Rows[i]).ToList();

    void ShowSelection()
    {
        if (_closed) return;
        var rows = SelectedRows;
        SetOverlay(null, null);
        SelectedDates = rows.Select(r => r.Revision.Commit.AuthorDate).Distinct().ToList();
        switch (rows)
        {
            case [{ File: { } f } row]:
                Diff.Show(new DiffRequest(null, row.Revision.Sha, f.Path, f.OldPath));
                break;
            case [{ File: { } newer } n, { File: { } older } o] when !IsFolder:
                // Older commit as source, newer as destination.
                Diff.Show(new DiffRequest(o.Revision.Sha, n.Revision.Sha, newer.Path, older.Path != newer.Path ? older.Path : null));
                break;
            case { Count: > 2 }:
                Diff.Show(null);
                SetOverlay("Select two commits to see difference between them", $"{rows.Count} commits selected");
                break;
            default:
                Diff.Show(null);
                break;
        }
    }

    void SetOverlay(string? title, string? text)
    {
        OverlayTitle = title;
        OverlayText = text;
    }

    /// <summary>The window closed: stops a running diff load and frees its bitmaps; a later selection loads nothing.</summary>
    public void Close()
    {
        _closed = true;
        Diff.Show(null);
    }

    /// <summary>The File History window menu for the current selection; empty = no menu.</summary>
    public IReadOnlyList<MenuEntry> Menu()
    {
        // Two rows: only the external diff items for the pair (none configured: no menu).
        if (SelectedRows is [_, _]) return ExternalTools.Entries(ToolKind.Diff, ExternalDiffAsync, enabled: !IsFolder);
        if (SelectedRows is not [var row]) return [];
        var rev = row.Revision;
        List<MenuEntry> revision = [Menus.Item("Copy Commit SHA", CopyShasAsync, "Ctrl+C"), Menus.Item("Copy Commit Info", CopyInfoAsync, "Ctrl+Shift+C")];
        if (row.File is not { } f) return Menus.Join([Menus.Item("Reveal in GerGit", () => RevealAsync(rev.Sha))], revision);
        var file = Menus.Join(
            [Menus.Item("Open", () => RevisionActions.OpenAsync(Tab, f.Path)), .. ExternalTools.Entries(ToolKind.Diff, ExternalDiffAsync),
                Menus.Item("Show in File Explorer", () => RevisionActions.ShowInFileManagerAsync(Tab, f.Path))],
            [Menus.Item("Reset File to State at Commit...", () => RevisionActions.ResetFilesAsync(Tab, rev.Sha, [f.Path], before: false, confirm: Confirm))],
            [Menus.Item("Save as...", () => RevisionActions.SaveAsAsync(Tab, PickSaveFile, RevOf(rev.Sha, f), f.Path, ShowError))]);
        return Menus.Join([Menus.Item("Reveal in GerGit", () => RevealAsync(rev.Sha))], file, row.IsRevision ? revision : []);
    }

    /// <summary>"External diff" with <paramref name="tool"/>: one row = the commit's change to the file, two rows
    /// = the older row's file (left) against the newer row's (right). Other selections do nothing.</summary>
    public Task ExternalDiffAsync(ExternalTool tool)
    {
        switch (SelectedRows)
        {
            case [{ File: { } f } row]:
            {
                var (left, right) = ExternalTools.CommitSides(null, row.Revision.Sha, f.Path, f.OldPath, f.Kind);
                return ExternalTools.DiffAsync(Tab, tool, left, right, ShowError);
            }
            case [{ File: { } newer } n, { File: { } older } o] when !IsFolder:
                return ExternalTools.DiffAsync(Tab, tool, new(o.Revision.Sha, older.Path, older.Kind == ChangeKind.Deleted),
                    new(n.Revision.Sha, newer.Path, newer.Kind == ChangeKind.Deleted), ShowError);
            default:
                return Task.CompletedTask;
        }
    }

    /// <summary>Ctrl+D: the primary diff tool for the selection (1 or 2 rows).</summary>
    public Task ExternalDiffShortcutAsync() => ExternalTools.RunPrimaryAsync(ToolKind.Diff, ExternalDiffAsync);

    /// <summary>A file deleted in the commit is read from the parent.</summary>
    static string RevOf(string sha, FileChange f) => f.Kind == ChangeKind.Deleted ? sha + "^" : sha;

    public Task RevealAsync(string sha)
    {
        RevisionActions.Reveal(Tab, sha);
        return Task.CompletedTask;
    }

    /// <summary>Ctrl+C: the selected commits' SHAs, one per line.</summary>
    public Task CopyShasAsync() =>
        SelectedRows.Count == 0 ? Task.CompletedTask : Copy(string.Join("\n", SelectedRows.Select(r => r.Revision.Sha).Distinct()));

    /// <summary>Ctrl+Shift+C: "&lt;sha7&gt; - &lt;subject&gt;" per selected commit.</summary>
    public Task CopyInfoAsync() =>
        SelectedRows.Count == 0 ? Task.CompletedTask
            : Copy(string.Join("\n", SelectedRows.Select(r => r.Revision.Commit).Distinct().Select(RevisionActions.Info)));

    /// <summary>Ctrl+Alt+Shift+O: the selected file as of its commit.</summary>
    public Task OpenAtCommitAsync() =>
        SelectedRows is [{ File: { } f } row] ? RevisionActions.OpenAtAsync(Tab, RevOf(row.Revision.Sha, f), f.Path, ShowError) : Task.CompletedTask;
}
