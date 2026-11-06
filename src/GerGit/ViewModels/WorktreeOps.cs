using GerGit.Core;
using GerGit.Core.Git;
using GerGit.ViewModels.Dialogs;
using GerGit.ViewModels.Dialogs.Refs;

namespace GerGit.ViewModels;

/// <summary>
/// Worktrees of one tab: the three create dialogs, delete, open (as tabs right after this one) and copy path.
/// Not built yet: no submodule update in a new worktree and no automatic reopen after
/// "Mark repository as safe" on an open failure (open it again).
/// </summary>
public sealed class WorktreeOps(RepoTabViewModel tab)
{
    static readonly string[] MainBranchNames = ["develop", "main", "master"];

    /// <summary>"&lt;parent of the repository folder&gt;/&lt;repository folder name&gt;-worktrees"; the repository folder is the main worktree.</summary>
    string Container
    {
        get
        {
            var main = tab.Repo.MainWorkTree;
            return Path.Combine(Path.GetDirectoryName(main) ?? main, Path.GetFileName(main) + "-worktrees");
        }
    }

    /// <summary>The branch (full ref) is checked out somewhere: in a listed worktree or in this one.</summary>
    bool CheckedOut(string branch) => tab.WorktreeOf(branch) is not null || branch == tab.Refs.ActiveBranch;

    IReadOnlySet<string> LocalNames => tab.Refs.Refs.Where(r => r.Kind == RefKind.LocalBranch)
        .Select(r => r.ShortName).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>"New Worktree..." (branch arrow, Repository menu, Worktrees header): start = main branch
    /// ("develop", "main", "master", first match), else the first local branch.</summary>
    public Task CreateAsync()
    {
        var starts = tab.Refs.Refs.Where(r => r.Kind == RefKind.LocalBranch).Select(RefOps.Point).ToList();
        var start = MainBranchNames.Select(n => starts.FirstOrDefault(p => p.Text == n)).OfType<RefPoint>().FirstOrDefault() ?? starts.FirstOrDefault();
        var vm = new CreateWorktreeViewModel(Container, starts, start, null, "", "", LocalNames, n => CheckedOut("refs/heads/" + n))
        {
            Title = "Create Worktree", Description = "Create branch and checkout it in a separate worktree",
        };
        vm.Run = () => AddJobAsync(vm, "Create Worktree", vm.Name, ["worktree", "add", "-b", vm.Name, FullPath(vm), vm.Start!.Sha]);
        return tab.ShowDialog(vm);
    }

    /// <summary>"Checkout as Worktree..." on a local branch.</summary>
    public Task CheckoutAsWorktreeAsync(GitRef local)
    {
        var vm = new CreateWorktreeViewModel(Container, null, null, RefOps.Point(local), "Local Branch:", local.ShortName, LocalNames,
            n => CheckedOut("refs/heads/" + n))
        {
            Title = "Checkout Branch as Worktree", Description = "Checkout branch in separate worktree",
        };
        vm.Run = () => AddJobAsync(vm, "Checkout Branch As Worktree", local.ShortName, ["worktree", "add", FullPath(vm), local.ShortName]);
        return tab.ShowDialog(vm);
    }

    /// <summary>"Checkout as Worktree..." on a remote branch: its tracking local branch, else a new tracking branch
    /// named like the remote branch without the remote.</summary>
    public Task CheckoutRemoteAsWorktreeAsync(GitRef remote)
    {
        var name = tab.RemoteOps.NameInRemote(remote.FullName);
        var local = tab.Refs.Refs.FirstOrDefault(r => r.Kind == RefKind.LocalBranch && r.Upstream == remote.FullName);
        var vm = new CreateWorktreeViewModel(Container, null, null, RefOps.Point(remote), "Remote Branch:", name, LocalNames,
            n => CheckedOut("refs/heads/" + n))
        {
            Title = "Checkout Remote Branch as Worktree", Description = "Checkout remote branch in separate worktree",
        };
        vm.Run = () => AddJobAsync(vm, "Checkout Remote Branch As Worktree", local?.ShortName ?? name, local is not null
            ? ["worktree", "add", FullPath(vm), local.ShortName]
            : ["worktree", "add", "--track", "-b", name, FullPath(vm), remote.ShortName]);
        return tab.ShowDialog(vm);
    }

    string FullPath(CreateWorktreeViewModel vm) => Path.GetFullPath(vm.Location.Trim(), tab.Path).TrimEnd('/');

    /// <summary>The git step, then the new worktree opens in a new tab.</summary>
    Task AddJobAsync(CreateWorktreeViewModel vm, string jobName, string branch, List<string> args)
    {
        var path = FullPath(vm);
        bool created = false;
        return tab.RunJobAsync(jobName, async job =>
        {
            job.SetMessage("Creating worktree...");
            vm.SetStatus(DialogStatus.InProgress, "Creating worktree...");
            var r = GitRepository.RefuseOptionLikeBranch(branch) ?? await GitRunner.RunJobAsync(job, tab.Path, args);
            if (created = r.Success) job.Succeed($"Created '{branch}' worktree");
            return r;
        }, vm, () => created ? OpenAsync([path]) : Task.CompletedTask);
    }

    /// <summary>"Delete '&lt;name&gt;'..." on a worktree row.</summary>
    public async Task DeleteAsync(WorktreeEntry wt)
    {
        var vm = new DeleteWorktreeViewModel
        {
            Title = $"Are you sure you want to delete worktree {wt.Name}?", Description = $"Do you want to delete worktree {wt.Path}?",
            SubmitText = "Delete", Width = 590,
        };
        vm.Run = () => tab.RunJobAsync($"Delete worktree '{wt.Name}'", job =>
        {
            vm.SetStatus(DialogStatus.InProgress, "Deleting worktree...");
            return RemoveAsync(job, wt.Path);
        }, vm);
        await tab.ShowDialog(vm);
    }

    /// <summary>`git worktree remove --force` (local changes in it are lost); on success its tab closes. Also the
    /// "Also delete worktree" step of Delete Branch.</summary>
    internal async Task<GitResult?> RemoveAsync(Job job, string path)
    {
        job.SetMessage("Deleting worktree...");
        var r = await GitRunner.RunJobAsync(job, tab.Path, ["worktree", "remove", "--force", path]);
        if (r.Success && tab.Main is { } main && main.TabOf(path, StringComparison.Ordinal) is { } open) main.CloseTab(open); // Linux: /x/Foo is not /x/foo
        return r;
    }

    /// <summary>Opens each folder as a tab right after this one (an open one is selected); the first failure shows its error.</summary>
    public async Task OpenAsync(IReadOnlyList<string> paths)
    {
        if (tab.Main is not { } main) return;
        var after = tab;
        foreach (var p in paths)
        {
            if (await main.OpenAsync(p, after) is { } failure)
            {
                await tab.ShowError(failure.Title, failure.Text);
                return;
            }
            after = main.SelectedTab ?? after;
        }
    }

    /// <summary>"Show in File Explorer": the folder in the desktop's file manager (xdg-open); nothing when it is gone.</summary>
    public Task ShowInFileManagerAsync(WorktreeEntry wt) =>
        Directory.Exists(wt.Path) ? tab.Hosting.OpenPath(wt.Path) : Task.CompletedTask;
}
