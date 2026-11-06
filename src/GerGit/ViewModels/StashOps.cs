using GerGit.Core;
using GerGit.ViewModels.Dialogs;

namespace GerGit.ViewModels;

/// <summary>Entry points of the stash operations; menus, toolbar and keys call these.
/// Not built yet: no "Save as Patch..." (stash and commits) and no "Compare to Local Changes" yet; patches come later.</summary>
public sealed class StashOps(RepoTabViewModel tab)
{
    /// <summary>Save Stash dialog (toolbar Stash, Repository menu, Ctrl+Shift+H).</summary>
    public Task SaveAsync() => tab.ShowDialog(new SaveStashViewModel(tab, snapshot: false));

    /// <summary>Save Snapshot dialog (toolbar Stash arrow menu).</summary>
    public Task SaveSnapshotAsync() => tab.ShowDialog(new SaveStashViewModel(tab, snapshot: true));

    /// <summary>Partial stash dialog (Local Changes "Stash N Files..."); <paramref name="paths"/> start checked.</summary>
    public Task StashFilesAsync(IReadOnlyList<string> paths) => tab.ShowDialog(new StashFilesViewModel(tab, paths));

    /// <summary>Apply Stash dialog (stash "Apply...", double-click, Recent Stashes menu item).</summary>
    public Task ApplyAsync(StashEntry stash) => tab.ShowDialog(new ApplyStashViewModel(tab, stash));

    public Task RenameAsync(StashEntry stash) => tab.ShowDialog(new RenameStashViewModel(tab, stash));

    /// <summary>Delete Stash / Delete N Stashes dialog; the first entry decides the selection afterwards.</summary>
    public Task DeleteAsync(IReadOnlyList<StashEntry> stashes) =>
        stashes.Count == 0 ? Task.CompletedTask : tab.ShowDialog(new DeleteStashViewModel(tab, stashes));

    /// <summary>Toolbar "Recent Stashes" menu: up to 15, newest first; item text = <see cref="StashEntry.ShortSubject"/>.</summary>
    public IReadOnlyList<StashEntry> RecentStashes => tab.Stashes.Take(15).ToList();
}

/// <summary>Entry points of cherry-pick and revert.</summary>
public sealed class CommitOps(RepoTabViewModel tab)
{
    /// <summary>Cherry Pick dialog; <paramref name="shas"/> in commit-list order (newest first).</summary>
    public Task CherryPickAsync(IReadOnlyList<string> shas) => OpenAsync(shas, revert: false);

    public Task RevertAsync(string sha) => OpenAsync([sha], revert: true);

    async Task OpenAsync(IReadOnlyList<string> shas, bool revert)
    {
        if (shas.Count == 0) return;
        IReadOnlyList<CommitHeader> commits, parents = [];
        try
        {
            commits = await tab.Repo.ReadHeadersAsync(shas);
            if (shas.Count == 1 && (await tab.Repo.ReadCommitDetailsAsync(shas[0])).Parents is { Length: > 1 } p)
                parents = await tab.Repo.ReadHeadersAsync(p);
        }
        catch (GitException e)
        {
            await tab.ShowError("Git Error", e.Message);
            return;
        }
        await tab.ShowDialog(new CommitPickViewModel(tab, commits.Select(Item).ToList(), parents.Select(Item).ToList(), revert));

        static CommitItem Item(CommitHeader h) => new(h.Sha, h.Subject);
    }
}
