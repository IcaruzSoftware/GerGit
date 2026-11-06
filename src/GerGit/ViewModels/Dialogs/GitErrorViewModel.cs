using System.Diagnostics;
using CommunityToolkit.Mvvm.Input;
using GerGit.Core;
using GerGit.Core.Git;
using GerGit.Views;

namespace GerGit.ViewModels.Dialogs;

/// <summary>
/// Error-window options owned by the caller's operation: the actions are shown only for their error kind.
/// QuietHostError: a "Could not resolve host" failure only fails the job, no window (fetch and pull).
/// </summary>
public sealed record GitErrorActions(Func<Task>? ForceFetch = null, Func<Task>? MergeUnrelated = null, bool QuietHostError = false);

/// <summary>
/// The "Git Error" window: the error text in the coloured output viewer, one optional
/// action button, Close (Esc). The action closes the window first; the opener then runs <see cref="ChosenAction"/>.
/// </summary>
public sealed partial class GitErrorViewModel : DialogViewModel
{
    Func<Task>? _action;

    /// <summary>Plain form (also for failures that are not git errors): title "Git Error", the text as is.</summary>
    public GitErrorViewModel(string text, GitErrorKind kind = GitErrorKind.Generic)
    {
        Kind = kind;
        Text = text;
        bool conflict = kind == GitErrorKind.MergeConflict;
        Title = conflict ? "Merge Conflict" : "Git Error";
        Description = conflict
            ? "Automatic merge failed. Fix conflicts and then commit the result"
            : "An unexpected error occurred while performing the git request";
        WarningBadge = !conflict;
        SubmitText = null;
        CancelText = "Close";
    }

    public GitErrorKind Kind { get; }
    public string Text { get; }

    /// <summary>Text of the action button; null hides it.</summary>
    public string? ActionText { get; private set; }

    /// <summary>The action the user picked; the window is closed by then.</summary>
    public Func<Task>? ChosenAction { get; private set; }

    public void SetAction(string text, Func<Task> run)
    {
        ActionText = text;
        _action = run;
    }

    [RelayCommand]
    void RunAction()
    {
        ChosenAction = _action;
        Close(true);
    }

    /// <summary>
    /// The window for a classified error with its action: Remove .git/index.lock (no refresh), Mark
    /// repository as safe (after <paramref name="confirm"/>, see <see cref="SafeConfirmation"/>; then
    /// <paramref name="afterSafe"/>, e.g. a refresh or reopening the repository), and the caller's Force Fetch / Merge
    /// unrelated history. <paramref name="confirm"/> (title, text, confirm button, width; true = confirmed) defaults to a
    /// message window over the main window. A path that git would read as a pattern (<see cref="TrustsMore"/>) gets no
    /// Mark-as-safe action but <see cref="WiderThanRepository"/> under the error text.
    /// </summary>
    public static GitErrorViewModel Create(GitError error, string? gitDir, string? repoPath, Func<Task>? afterSafe,
        GitErrorActions? actions = null, Func<string, string, string, double, Task<bool>>? confirm = null)
    {
        confirm ??= (title, text, button, width) => MessageWindow.ShowAsync(title, text, button, "Cancel", width);
        var unsafePath = error.Kind == GitErrorKind.UnsafeRepository ? error.Path ?? repoPath : null;
        bool tooWide = unsafePath is not null && TrustsMore(unsafePath);
        var text = tooWide ? error.Text.TrimEnd('\n') + "\n\n" + WiderThanRepository : error.Text;
        var vm = new GitErrorViewModel(text, error.Kind) { Width = 740, Height = 330, CanResize = true, MinWidth = 420, MinHeight = 220 };
        switch (error.Kind)
        {
            case GitErrorKind.IndexLocked when gitDir is not null:
                vm.SetAction("Remove .git/index.lock", () => { RemoveIndexLock(gitDir); return Task.CompletedTask; });
                break;
            case GitErrorKind.UnsafeRepository when unsafePath is { } path && !tooWide:
                vm.SetAction("Mark repository as safe", async () =>
                {
                    var (title, text) = await Task.Run(() => SafeConfirmation(path)); // user lookups can block (NSS)
                    if (await confirm(title, text, "Mark as Safe", 520) && await MarkSafeAsync(path) && afterSafe is not null) await afterSafe();
                });
                break;
            case GitErrorKind.TagClobber when actions?.ForceFetch is { } forceFetch:
                vm.SetAction("Force Fetch", forceFetch);
                break;
            case GitErrorKind.UnrelatedHistories when actions?.MergeUnrelated is { } merge:
                vm.SetAction("Merge unrelated history", merge);
                break;
        }
        return vm;
    }

    /// <summary>Shown instead of the Mark-as-safe action for a path that git would read as a pattern.</summary>
    public const string WiderThanRepository =
        "This folder name would trust more than this repository; mark it as safe from a terminal if you really want to.";

    /// <summary>A `safe.directory` value with this path would trust more than this one repository: `*` trusts every
    /// repository, a trailing `/*` every one below that folder (git 2.46 and newer), and a leading `%(prefix)/` is
    /// replaced by git's install prefix.</summary>
    internal static bool TrustsMore(string path) =>
        path == "*" || path.EndsWith("/*", StringComparison.Ordinal) || path.StartsWith("%(prefix)/", StringComparison.Ordinal);

    /// <summary>Deletes &lt;gitdir&gt;/index.lock; a failure is only logged.</summary>
    public static void RemoveIndexLock(string gitDir)
    {
        try { File.Delete(Path.Combine(gitDir, "index.lock")); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Trace.WriteLine(e.Message); }
    }

    /// <summary>The question before a repository is marked as safe: who owns the folder and what trusting it means.
    /// Of the folder, its `.git` and, when `.git` is a file (`gitdir: &lt;path&gt;`), the git dir it points to, the first
    /// one that belongs to another user names the owner (git also refuses a repository whose git dir alone belongs to
    /// someone else, and reports the work-tree path); when all belong to the current user the text says "you", when no
    /// owner is known "unknown". Reads files and looks up users: call it off the UI thread.</summary>
    internal static (string Title, string Text) SafeConfirmation(string path) =>
        SafeConfirmation(path, RepositoryScan.OwnerUid, RepositoryScan.CurrentUid());

    internal static (string Title, string Text) SafeConfirmation(string path, Func<string, uint?> ownerOf, uint me)
    {
        var dotGit = Path.Combine(path, ".git");
        string? gitDir;
        try { gitDir = RepositoryScan.GitFileTarget(dotGit); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { gitDir = null; } // also a malformed gitdir line
        uint?[] owners = [ownerOf(path), ownerOf(dotGit), .. gitDir is null ? [] : new[] { ownerOf(gitDir) }];
        var uid = owners.FirstOrDefault(u => u is not null && u != me) ?? owners.FirstOrDefault(u => u is not null);
        const string Consequence = "Git will then run this repository's hooks and settings as you.";
        var name = uid is { } id && id != me ? RepositoryScan.UserName(id) : null;
        var text = uid is null ? $"The owner of '{path}' is unknown.\n\n{Consequence} Only do this if you trust its owner."
            : name is null ? $"'{path}' is owned by you.\n\n{Consequence} Only do this if you trust its content."
            : $"'{path}' is owned by {name}.\n\n{Consequence} Only do this if you trust {name}.";
        return ("Mark repository as safe?", text);
    }

    /// <summary>`git config --global --add safe.directory &lt;path&gt;`; false (logged) when git fails or the path would be
    /// read as a pattern (<see cref="TrustsMore"/>).</summary>
    public static async Task<bool> MarkSafeAsync(string path)
    {
        if (TrustsMore(path))
        {
            Trace.WriteLine($"Not marked as safe (git would read '{path}' as a pattern)");
            return false;
        }
        var r = await GitRunner.RunAsync(null, ["config", "--global", "--add", "safe.directory", path]);
        if (!r.Success) Trace.WriteLine($"Mark repository as safe failed: {r.ReadableOutput}");
        return r.Success;
    }
}
