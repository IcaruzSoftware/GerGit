using System.Diagnostics;
using GerGit.Core;
using GerGit.Core.Git;
using GerGit.Core.Rebase;
using GerGit.ViewModels.Dialogs;
using GerGit.ViewModels.Dialogs.Refs;

namespace GerGit.ViewModels;

/// <summary>
/// The rebase entry points of one tab: the Rebase dialog, the interactive rebase window and the
/// quick actions. Always rebases the active branch. Stateless, so menus create one per click.
/// Not built yet: the sidebar drag-and-drop menu and the commit-list drag-and-drop quick actions (no drag infrastructure
/// yet) are not built.
/// </summary>
public sealed class RebaseOps(RepoTabViewModel tab)
{
    GitRepository Repo => tab.Repo;
    GitRef? ActiveRef => tab.Refs.Refs.FirstOrDefault(r => r.FullName == tab.Refs.ActiveBranch);

    /// <summary>"Rebase '&lt;active&gt;' on '&lt;name&gt;'..." (sidebar).</summary>
    public Task RebaseOnAsync(GitRef target) => RebaseAsync(RefOps.Point(target));

    /// <summary>"Rebase '&lt;active&gt;' to Here..." (commit menu).</summary>
    public async Task RebaseToCommitAsync(string sha) => await RebaseAsync(await tab.RefOps.CommitPointAsync(sha));

    /// <summary>"Interactively Rebase '&lt;active&gt;' on '&lt;name&gt;..." (sidebar).</summary>
    public Task InteractiveOnAsync(GitRef target) => InteractiveAsync(RefOps.Point(target));

    /// <summary>"Interactively Rebase '&lt;active&gt;' to Here..." (commit menu).</summary>
    public async Task InteractiveToCommitAsync(string sha) => await InteractiveAsync(await tab.RefOps.CommitPointAsync(sha));

    /// <summary>
    /// The quick actions: <paramref name="shasNewestFirst"/> = the selected commits in row order.
    /// Opens the interactive window with the initial action; nothing happens when the needed parent is missing.
    /// </summary>
    public async Task QuickActionAsync(QuickAction action, IReadOnlyList<string> shasNewestFirst)
    {
        if (shasNewestFirst.Count == 0) return;
        var (possible, target) = await Repo.QuickActionTargetAsync(action, shasNewestFirst);
        if (!possible)
        {
            Trace.WriteLine($"Quick action {action}: the commit has no parent, nothing to do");
            return;
        }
        await InteractiveAsync(target is null ? null : await tab.RefOps.CommitPointAsync(target), action, shasNewestFirst);
    }

    // ------------------------------------------------------------------ rebase dialog

    async Task RebaseAsync(RefPoint target)
    {
        if (ActiveRef is not { } active) return;
        var vm = new RebaseViewModel(RefOps.Point(active), target);
        vm.Preview = PreviewAsync(vm, active.FullName, target.Arg);
        vm.Run = () => RebaseJobAsync(vm, active.ShortName);
        await tab.ShowDialog(vm);
    }

    /// <summary>Footer prediction and the dependent branches; a failing merge base / target closes the dialog with
    /// the error.</summary>
    async Task PreviewAsync(RebaseViewModel vm, string source, string target)
    {
        RebasePreview preview;
        try
        {
            preview = await Repo.PreviewRebaseAsync(source, target);
        }
        catch (GitException e)
        {
            if (vm.IsBusy || vm.Result is not null) return;
            vm.Close(false);
            await tab.ShowError("Git Error", e.Message);
            return;
        }
        if (vm.IsBusy || vm.Result is not null) return;
        vm.Branches = preview.BranchesInRange;
        if (preview.Prediction == RebasePrediction.NoConflicts) vm.SetStatus(DialogStatus.Success, "Rebase can be done without conflicts");
        else if (preview.Prediction == RebasePrediction.Conflicts) vm.SetStatus(DialogStatus.Warning, "Rebase will cause conflicts");
    }

    Task RebaseJobAsync(RebaseViewModel vm, string branch)
    {
        bool updateRefs = vm.EffectiveUpdateRefs, autostash = vm.Autostash;
        Settings.Set(RebaseViewModel.UpdateRefsKey, vm.UpdateRefs);
        Settings.Set(RebaseViewModel.AutostashKey, autostash);
        return tab.RunJobAsync($"Rebase '{vm.Source.Text}' onto '{vm.Target.Text}'", async job =>
            await Repo.RebaseAsync(job, branch, vm.Target.Arg, updateRefs, autostash, text =>
            {
                job.SetMessage(text);
                vm.SetStatus(DialogStatus.InProgress, text);
            }), vm, SelectHead);
    }

    // ------------------------------------------------------------------ interactive rebase window

    async Task InteractiveAsync(RefPoint? target, QuickAction? quick = null, IReadOnlyList<string>? shas = null)
    {
        if (ActiveRef is not { } active) return;
        var vm = new InteractiveRebaseViewModel(tab, active.ShortName, target, quick, shas);
        var run = vm.RunAsync(SelectHead);
        try { await InteractiveRebaseViewModel.Show(vm); }
        finally
        {
            vm.Abort(); // git must never stay blocked in the helper without a window
            await run;
        }
    }

    Task SelectHead()
    {
        if (tab.Refs.HeadSha is { } head) tab.CommitList.SelectCommit(head);
        return Task.CompletedTask;
    }
}
