using GerGit.Core;
using GerGit.Views;
using static GerGit.ViewModels.Menus;

namespace GerGit.ViewModels;

/// <summary>
/// The file menu of the Changes tab and of the rows in the Commit tab for the commit (or range) shown in
/// the details pane. The Changes list is flat, so there are no folder rows.
/// Not built yet: no LFS or custom-command items; no `Show in File Tree` (there is no File Tree tab yet);
/// `Open` is always enabled (no check for a default application).
/// </summary>
public static class CommitFileMenu
{
    /// <summary>To = the shown commit (destination of a range); From = the range's source, null for one commit.</summary>
    static (string? From, string To)? Target(DetailsViewModel d) =>
        d.Commit is { } c ? (null, c.Sha) : d.Source is { } s && d.Destination is { } t ? (s.Sha, t.Sha) : null;

    /// <summary>The revision holding the file's content: a file deleted by the commit is read from before it.</summary>
    static string RevOf(string? from, string to, ChangedFile f) => f.Kind == ChangeKind.Deleted ? from ?? to + "^" : to;

    static string Short(string sha) => sha[..Math.Min(7, sha.Length)];

    /// <param name="commitTab">Commit tab rows: no shortcut texts, `Reset File To`, Blame/History always enabled.</param>
    /// <param name="copy">Puts text on the clipboard.</param>
    /// <param name="pickSaveFile">The `Select location` dialog (suggested name, start folder) → path or null.</param>
    public static IReadOnlyList<MenuEntry> Build(DetailsViewModel d, IReadOnlyList<ChangedFile> files, bool commitTab,
        Func<string, Task> copy, Func<string, string?, Task<string?>> pickSaveFile)
    {
        if (files.Count == 0 || Target(d) is not var (from, to)) return [];
        var tab = d.Owner;
        var f = files[0];
        bool single = files.Count == 1, sub = single && f.IsSubmodule;
        string? Gesture(string g) => commitTab ? null : g;
        var paths = files.Select(x => x.Path).ToList();
        // The older side of a rename has the file under its old path: reset that one too (the new path then goes away).
        var oldSide = files.SelectMany(x => x.OldPath is { } o && o != x.Path ? [o, x.Path] : new[] { x.Path }).ToList();
        int n = files.Count;

        List<MenuEntry> reset = from is null
            ? [Item("State At Commit...", () => RevisionActions.ResetFilesAsync(tab, to, paths, before: false)),
               Item("State Before Commit...", () => RevisionActions.ResetFilesAsync(tab, to + "~", oldSide, before: true, n))]
            : [Item($"State at Commit '{Short(to)}'...", () => RevisionActions.ResetFilesAsync(tab, to, paths, before: false)),
               Item($"State at Commit '{Short(from)}'...", () => RevisionActions.ResetFilesAsync(tab, from, oldSide, before: false, n))];
        var resetTitle = commitTab ? "Reset File To" : single ? "Reset File to" : $"Reset {files.Count} Files to";

        return Join(
            sub ? [Item("Open Submodule In New Tab", () => tab.WorktreeOps.OpenAsync([Path.Combine(tab.Path, f.Path)]))] : [],
            [.. sub ? [] : (MenuEntry[])[Item("Open", () => RevisionActions.OpenAsync(tab, f.Path), Gesture("Ctrl+Alt+Shift+O"))],
                .. sub ? [] : ExternalTools.Entries(ToolKind.Diff, t => DiffAsync(tab, from, to, f, t), enabled: single, gestures: !commitTab),
                Item("Show in File Explorer", () => RevisionActions.ShowInFileManagerAsync(tab, f.Path))],
            [Sub(resetTitle, reset)],
            [.. sub ? [] : (MenuEntry[])[Item("Blame/Timeline...", () => OpenBlame(tab, f.Path, to), enabled: commitTab || single)],
                Item("History...", () => OpenHistory(tab, f.Path, to), enabled: commitTab || single)],
            sub ? [] : [Item("Save as...", () => RevisionActions.SaveAsAsync(tab, pickSaveFile, RevOf(from, to, f), f.Path), enabled: single)],
            [Item("Copy Path", () => CopyPathsAsync(d, files, false, copy), Gesture("Ctrl+C")),
             Item("Copy Full Path", () => CopyPathsAsync(d, files, true, copy), Gesture("Ctrl+Shift+C"))]);
    }

    static Task DiffAsync(RepoTabViewModel tab, string? from, string to, ChangedFile f, ExternalTool tool)
    {
        var (left, right) = ExternalTools.CommitSides(from, to, f.Path, f.OldPath, f.Kind);
        return ExternalTools.DiffAsync(tab, tool, left, right);
    }

    /// <summary>Ctrl+D in the Changes list: the primary diff tool for the one selected file (not a submodule).</summary>
    public static Task ExternalDiffAsync(DetailsViewModel d, IReadOnlyList<ChangedFile> files) =>
        files is [{ IsSubmodule: false } f] && Target(d) is var (from, to)
            ? ExternalTools.RunPrimaryAsync(ToolKind.Diff, t => DiffAsync(d.Owner, from, to, f, t))
            : Task.CompletedTask;

    static Task OpenBlame(RepoTabViewModel tab, string path, string sha)
    {
        BlameWindow.Open(tab, path, sha);
        return Task.CompletedTask;
    }

    static Task OpenHistory(RepoTabViewModel tab, string path, string sha)
    {
        FileHistoryWindow.Open(tab, path, false, sha);
        return Task.CompletedTask;
    }

    /// <summary>Copy Path / Copy Full Path (Ctrl+C / Ctrl+Shift+C): relative or absolute paths, one per line.</summary>
    public static Task CopyPathsAsync(DetailsViewModel d, IReadOnlyList<ChangedFile> files, bool full, Func<string, Task> copy) =>
        files.Count == 0 ? Task.CompletedTask
            : copy(string.Join("\n", files.Select(x => full ? Path.Combine(d.Owner.Path, x.Path) : x.Path)));

    /// <summary>Ctrl+Alt+Shift+O: the first selected file as of the commit, through a temp copy.</summary>
    public static Task OpenAtCommitAsync(DetailsViewModel d, IReadOnlyList<ChangedFile> files) =>
        files is [var f, ..] && !f.IsSubmodule && Target(d) is var (from, to)
            ? RevisionActions.OpenAtAsync(d.Owner, RevOf(from, to, f), f.Path)
            : Task.CompletedTask;
}
