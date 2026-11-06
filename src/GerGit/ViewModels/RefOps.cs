using System.Text.RegularExpressions;
using GerGit.Core;
using GerGit.Core.Git;
using GerGit.ViewModels.Dialogs;
using GerGit.ViewModels.Dialogs.Refs;

namespace GerGit.ViewModels;

/// <summary>
/// Branch, checkout, merge, reset and tag operations of one tab (including the remote-branch checkout decision tree). The public methods are the entry points the
/// menus, toolbar and double-clicks call. Dialogs stay open while their job runs.
/// There are no pinned or filtered references, so there is nothing to re-point or remove after rename/delete. Worktree dialogs are in <see cref="WorktreeOps"/>.
/// </summary>
public sealed partial class RefOps(RepoTabViewModel tab)
{
    /// <summary>Create Branch: the name of a failed attempt, shared by all repositories, for this app session.</summary>
    internal static string? FailedBranchName;

    GitRepository Repo => tab.Repo;
    RefSnapshot Refs => tab.Refs;
    GitRef? ActiveRef => Refs.Refs.FirstOrDefault(r => r.FullName == Refs.ActiveBranch);
    string RecentPrefixKey => Settings.RepoKey(tab.Path, "recent.branchPrefix");

    // ------------------------------------------------------------------ entry points

    /// <summary>"New Branch...": start = a branch / remote branch / tag (<paramref name="start"/>), a commit
    /// (<paramref name="commitSha"/>), or neither (active branch, else the HEAD commit).</summary>
    public async Task CreateBranchAsync(GitRef? start = null, string? commitSha = null)
    {
        if (await PointAsync(start, commitSha) is not { } point) return;
        var vm = new CreateBranchViewModel(point, FailedBranchName ?? Settings.Get(RecentPrefixKey, ""),
            Names(RefKind.LocalBranch), await IsDirtyAsync());
        vm.Run = () => CreateBranchJobAsync(vm);
        await tab.ShowDialog(vm);
    }

    /// <summary>Double-click / "Checkout..." on a local branch: its worktree's tab when another worktree has it checked
    /// out, else a quick checkout when clean, else the dialog.</summary>
    public async Task CheckoutBranchAsync(GitRef local)
    {
        if (tab.WorktreeOf(local.FullName) is { IsCurrent: false } wt)
        {
            await tab.WorktreeOps.OpenAsync([wt.Path]);
            return;
        }
        if (!await IsDirtyAsync())
        {
            await CheckoutLocalJobAsync(null, local, null, LocalChangesMode.DontChange);
            return;
        }
        var vm = new CheckoutViewModel([new("Switch to:", Point(local))], dirty: true)
        {
            Title = "Checkout Branch", Description = "Switch to another branch", SubmitText = "Checkout", Width = 500,
        };
        vm.Run = () => { vm.Local.Remember(); return CheckoutLocalJobAsync(vm, local, null, vm.Local.Mode); };
        await tab.ShowDialog(vm);
    }

    /// <summary>Double-click / "Checkout..." on a remote branch: the decision tree.</summary>
    public async Task CheckoutRemoteBranchAsync(GitRef remote)
    {
        var local = Refs.Refs.FirstOrDefault(r => r.Kind == RefKind.LocalBranch && r.Upstream == remote.FullName);
        if (local is null)
        {
            var branch = SplitRemoteRef(remote.FullName)?.Branch ?? remote.NameInRemote;
            var vm = new TrackRemoteBranchViewModel(Point(remote), branch, Names(RefKind.LocalBranch), await IsDirtyAsync());
            vm.Run = () => TrackJobAsync(vm, remote);
            await tab.ShowDialog(vm);
        }
        else if (local.Sha == remote.Sha || (local.Ahead > 0 && local.Behind == 0))
        {
            // Same commit or ahead only: just check out the local branch (nothing when it is active).
            if (local.FullName != Refs.ActiveBranch) await CheckoutBranchAsync(local);
        }
        else if (local.Ahead > 0 && local.Behind > 0)
        {
            var vm = new CheckoutSyncViewModel(Point(local), remote.ShortName);
            vm.Run = () => CheckoutSyncJobAsync(vm, local, remote);
            await tab.ShowDialog(vm);
        }
        else
        {
            // Behind only: "Checkout and Fast-Forward", shown even for a clean tree.
            var vm = new CheckoutViewModel([new("Switch to:", Point(local)), new("Fast-Forward to:", Point(remote))], await IsDirtyAsync())
            {
                Title = "Checkout and Fast-Forward", Description = "Checkout local branch and fast-forward it to remote branch",
                SubmitText = "Checkout and Fast-Forward", Width = 500,
            };
            vm.Run = () => { vm.Local.Remember(); return CheckoutLocalJobAsync(vm, local, remote, vm.Local.Mode); };
            await tab.ShowDialog(vm);
        }
    }

    /// <summary>"Checkout Commit..." on a commit, or on / double-click of a tag (<paramref name="tag"/>): always the dialog.</summary>
    public async Task CheckoutCommitAsync(string sha, GitRef? tag = null)
    {
        var point = tag is not null ? Point(tag) : await CommitPointAsync(sha);
        var vm = new CheckoutViewModel([new("Commit to checkout:", point)], await IsDirtyAsync())
        {
            Title = "Checkout Commit", Description = "Checkout a particular revision. Repository will be in detached HEAD state.",
            SubmitText = "Checkout Commit", Width = 590, MinHeight = 166,
        };
        vm.Run = () => { vm.Local.Remember(); return CheckoutCommitJobAsync(vm, point, vm.Local.Mode); };
        await tab.ShowDialog(vm);
    }

    /// <summary>"Rename..." / F2 on a local branch; <paramref name="newName"/> prefills the box.</summary>
    public async Task RenameBranchAsync(GitRef local, string? newName = null)
    {
        var upstream = local.Upstream is { } u ? SplitRemoteRef(u) : null;
        var remote = upstream is { } up && up.Branch == local.ShortName ? up.Remote : null;
        var remoteBranches = Refs.Refs.Where(r => r.Kind == RefKind.RemoteBranch).Select(r => r.ShortName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var vm = new RenameBranchViewModel(local.ShortName, remote, newName ?? local.ShortName, Names(RefKind.LocalBranch), remoteBranches);
        vm.Run = () => RenameJobAsync(vm, local);
        await tab.ShowDialog(vm);
    }

    /// <summary>"Delete..." / "Delete N Branches..." / Delete key on local branches.</summary>
    public async Task DeleteBranchesAsync(IReadOnlyList<GitRef> locals)
    {
        if (locals.Count == 0) return;
        // Computed once: the job deletes exactly the remote branches shown here, even if the refs refresh meanwhile.
        var corresponding = locals.Select(CorrespondingRemoteBranch).ToList();
        var targets = corresponding.OfType<(string Remote, string Branch)>().ToList();
        var rows = locals.Select((b, i) => new DeleteRow("git-branch", b.ShortName,
            corresponding[i] is { } c ? $"{c.Remote}/{c.Branch}" : null)).ToList();
        bool single = rows.Count == 1, anyRemote = rows.Any(r => r.RemoteBranch is not null);
        var worktree = single ? tab.WorktreeOf(locals[0].FullName) is { IsCurrent: false, IsMain: false } w ? w : null : null;
        var vm = new DeleteRefsViewModel(rows, single ? "Branch:" : "Branches:")
        {
            Title = single ? "Delete Branch" : "Delete Branches",
            Description = single ? "Delete local branch from your repository" : "Delete local branches from your repository",
            SubmitText = single ? "Delete" : $"Delete {rows.Count} branches",
            CheckText = !single ? "Also delete corresponding _remote branches"
                : anyRemote ? "Also delete _remote branch" : "Also delete corresponding _remote branch",
            CheckEnabled = anyRemote,
            CheckDetail = single ? rows[0].RemoteBranch : null,
            Width = 570, MinWidth = 500,
            Height = single ? double.NaN : 270, MinHeight = single ? 0 : 270, CanResize = !single,
            WorktreeName = worktree?.Name,
        };
        vm.Run = () => DeleteBranchesJobAsync(vm, locals, vm.IsChecked ? targets : [], vm.DeleteWorktree ? worktree : null);
        await tab.ShowDialog(vm);
    }

    /// <summary>"Delete..." / "Delete N Remote Branches..." on remote branches.</summary>
    public async Task DeleteRemoteBranchesAsync(IReadOnlyList<GitRef> remotes)
    {
        if (remotes.Count == 0) return;
        bool single = remotes.Count == 1;
        var vm = new DeleteRefsViewModel(remotes.Select(b => new DeleteRow("cloud", b.ShortName, null)).ToList(), single ? "Branch:" : "Branches:")
        {
            Title = single ? "Delete Branch" : "Delete Branches",
            Description = single ? "Delete branch from remote repository" : "Delete branches from remote repository",
            SubmitText = single ? "Delete" : $"Delete {remotes.Count} branches",
            Width = 520, ListHeight = 80,
        };
        vm.Run = () => DeleteRemoteBranchesJobAsync(vm, remotes);
        await tab.ShowDialog(vm);
    }

    /// <summary>"Reset '&lt;branch or HEAD&gt;' to Here..." on a commit.</summary>
    public async Task ResetAsync(string sha)
    {
        var name = tab.ActiveBranchName;
        var vm = new ResetViewModel(name ?? "HEAD", await CommitPointAsync(sha))
        {
            Title = name is null ? "Reset HEAD to Revision" : "Reset Current Branch to Revision",
            Description = name is null ? "Move HEAD to the selected revision" : $"Move the '{name}' branch HEAD to the selected revision",
            SubmitText = "Reset", Width = 590,
        };
        vm.Run = () => ResetJobAsync(vm);
        await tab.ShowDialog(vm);
    }

    /// <summary>"Merge into '&lt;active&gt;'..." on a branch, remote branch or tag; <paramref name="into"/> = the drop
    /// target of a drag ("Merge 'a' into b..."), default the active branch. Nothing happens without a destination or
    /// when source = destination.</summary>
    public Task MergeAsync(GitRef source, GitRef? into = null)
    {
        // The full ref when another reference has the same short name or the short name would read as an option (a tag
        // fetched from a remote may start with "-"), else the short name.
        bool ambiguous = Refs.Refs.Any(r => r.ShortName == source.ShortName && r.FullName != source.FullName)
            || source.ShortName.StartsWith('-');
        return MergeDialogAsync(Point(source), ambiguous ? source.FullName : source.ShortName, into, source.FullName);
    }

    /// <summary>"Merge into '&lt;active&gt;'..." on a commit.</summary>
    public async Task MergeCommitAsync(string sha, GitRef? into = null) =>
        await MergeDialogAsync(await CommitPointAsync(sha), sha, into, sha);

    /// <summary>"New Tag...": start = a branch / tag, a commit, or neither (active branch, else the HEAD commit).</summary>
    public async Task CreateTagAsync(GitRef? start = null, string? commitSha = null)
    {
        if (await PointAsync(start, commitSha) is not { } point) return;
        // A tag start point is resolved to its commit; branches stay full refs.
        if (start?.Kind == RefKind.Tag) point = point with { Arg = start.Sha };
        var vm = new CreateTagViewModel(point, Names(RefKind.Tag), tab.Remotes.Count);
        vm.Run = () => CreateTagJobAsync(vm);
        await tab.ShowDialog(vm);
    }

    /// <summary>"Delete..." / "Delete N Tags..." on tags.</summary>
    public async Task DeleteTagsAsync(IReadOnlyList<GitRef> tags)
    {
        if (tags.Count == 0) return;
        bool single = tags.Count == 1;
        var vm = new DeleteRefsViewModel(tags.Select(t => new DeleteRow("tag", t.ShortName, null)).ToList(), single ? "Tag:" : "Tags:")
        {
            Title = single ? "Delete Tag" : "Delete Tags",
            Description = single ? "Delete tag from your repository" : "Delete tags from your repository",
            SubmitText = single ? "Delete" : $"Delete {tags.Count} tags",
            CheckText = single ? "Delete tag from remote repositories" : "Delete tags from remote repositories",
            CheckWarns = false,
            Width = 520, ListHeight = 80,
        };
        vm.Run = () => DeleteTagsJobAsync(vm, tags);
        await tab.ShowDialog(vm);
    }

    /// <summary>"Show Tag Details..." (annotated tags only).</summary>
    public async Task ShowTagDetailsAsync(GitRef tag)
    {
        var details = await Repo.ReadTagDetailsAsync(tag);
        await tab.ShowDialog(new TagDetailsViewModel(tag.ShortName, details));
    }

    /// <summary>"Select Stale Local Branches": local branches whose configured upstream no longer exists.</summary>
    public IReadOnlyList<GitRef> StaleBranches() =>
        Refs.Refs.Where(r => r.Kind == RefKind.LocalBranch && r.UpstreamState == UpstreamState.Gone).ToList();

    // ------------------------------------------------------------------ jobs

    async Task CreateBranchJobAsync(CreateBranchViewModel vm)
    {
        string name = vm.Name;
        bool checkout = vm.CheckoutAfterCreate;
        var mode = vm.Local.Mode;
        Settings.Set(CreateBranchViewModel.CheckoutKey, checkout);
        if (checkout) vm.Local.Remember();
        await tab.RunJobAsync($"Create branch '{name}'", async job =>
        {
            GitResult r;
            bool created;
            if (!checkout)
            {
                Step(job, vm, "Creating branch...");
                r = await Git(job, ["branch", "--no-track", name, vm.Start.Arg]);
                created = r.Success;
            }
            else
            {
                created = false;
                r = await WithSubmodulesAsync(job, vm, async () =>
                {
                    (var a, created) = await AutostashAsync(job, vm, mode, StashMessage(name), () =>
                    {
                        Step(job, vm, "Creating branch...");
                        return Git(job, ["checkout", "--no-track", "-b", name, vm.Start.Arg, .. Force(mode)]);
                    });
                    return a;
                });
                if (r.Success) job.Succeed($"Switched to branch '{name}'");
            }
            // Only a failed create is offered again; a failing stash reapply or submodule update left the branch behind.
            if (created)
            {
                FailedBranchName = null;
                int slash = name.LastIndexOf('/');
                Settings.Set(RecentPrefixKey, slash < 0 ? null : name[..(slash + 1)]);
            }
            else FailedBranchName = name;
            return r;
        }, vm, () => Select(vm.Start.Sha));
    }

    /// <summary>Checkout of a local branch (quick when <paramref name="vm"/> is null), optionally followed by the
    /// fast-forward to <paramref name="ffTo"/>.</summary>
    Task CheckoutLocalJobAsync(CheckoutViewModel? vm, GitRef local, GitRef? ffTo, LocalChangesMode mode)
    {
        var name = local.ShortName;
        return tab.RunJobAsync($"Checkout branch '{name}'", async job =>
        {
            bool checkedOut = false;
            GitResult? checkout = null;
            var r = await WithSubmodulesAsync(job, vm, async () => (await AutostashAsync(job, vm, mode, StashMessage(name), async () =>
            {
                if (vm is null) job.SetMessage(name); // quick checkout: progress text = branch name
                else Step(job, vm, "Checkout...");
                checkout = GitRepository.RefuseOptionLikeBranch(name) ?? await Git(job, ["checkout", "--progress", .. Force(mode), name]);
                checkedOut = checkout.Success;
                if (!checkout.Success || ffTo is null) return checkout;
                return await Git(job, ["merge", "--ff-only", ffTo.FullName]);
            })).Result);
            if (!r.Success) job.Fail(ffTo is not null && checkedOut ? "Fast-forward failed" : "Checkout failed");
            else if (ffTo is not null) job.Succeed(r.Stdout.Contains("Already up to date", StringComparison.Ordinal) ? "Up to date" : "Fast-forwarded");
            else job.Succeed(CheckoutMessage(checkout ?? r, name));
            return r;
        }, vm, () => Select(Tip(ffTo ?? local)));
    }

    /// <summary>Checkout log line from git's "Your branch is ..." text, else "Switched to branch '&lt;name&gt;'".</summary>
    static string CheckoutMessage(GitResult r, string name)
    {
        var text = r.Stdout + "\n" + r.Stderr;
        if (UpToDateRegex().Match(text) is { Success: true } u) return $"Up to date with '{u.Groups[1].Value}'";
        if (BehindAheadRegex().Match(text) is { Success: true } m)
            return m.Groups[1].Value == "behind"
                ? $"Behind '{m.Groups[2].Value}' '{m.Groups[3].Value}'"
                : $"Ahead of '{m.Groups[2].Value}' '{m.Groups[3].Value}'";
        return $"Switched to branch '{name}'";
    }

    [GeneratedRegex(@"Your branch is up to date with '(.+)'\.")]
    private static partial Regex UpToDateRegex();

    [GeneratedRegex(@"Your branch is (behind|ahead of) '(.+)' (by \d+ commits?)")]
    private static partial Regex BehindAheadRegex();

    Task TrackJobAsync(TrackRemoteBranchViewModel vm, GitRef remote)
    {
        string name = vm.Name;
        var mode = vm.Local.Mode;
        vm.Local.Remember();
        return tab.RunJobAsync($"Track '{remote.ShortName}'", async job =>
        {
            var r = await WithSubmodulesAsync(job, vm, async () => (await AutostashAsync(job, vm, mode, StashMessage(name), () =>
            {
                Step(job, vm, $"Tracking '{remote.ShortName}'...");
                return Git(job, ["checkout", "--track", "-b", name, remote.ShortName, .. Force(mode)]);
            })).Result);
            if (r.Success) job.Succeed($"Switched to branch '{name}'");
            return r;
        }, vm, () => Select(Tip(remote)));
    }

    /// <summary>The Checkout and Sync dialog: stash local changes when asked, check out the local branch, bring it to the
    /// remote branch (rebase, merge or reset), reapply the stash and update submodules.</summary>
    Task CheckoutSyncJobAsync(CheckoutSyncViewModel vm, GitRef local, GitRef remote)
    {
        var action = vm.Action;
        bool stash = vm.Stash;
        Settings.Set(CheckoutSyncViewModel.StashKey, stash);
        var name = local.ShortName;
        return tab.RunJobAsync($"Checkout branch '{name}'", async job =>
        {
            if (GitRepository.RefuseOptionLikeBranch(name) is { } refused) return refused; // before anything is stashed
            string? stashed = null;
            bool popTried = false;
            var message = $"Checkout autostash {GitRepository.StashTimestamp()}";
            if (stash && await IsDirtyAsync())
            {
                Step(job, vm, "Stashing...");
                (var s, stashed) = await Repo.StashPushAsync(job, message);
                if (!s.Success) return s;
            }
            var result = await WithSubmodulesAsync(job, vm, async () =>
            {
                Step(job, vm, "Checkout...");
                var r = await Git(job, ["checkout", "--progress", name]);
                if (!r.Success && !r.Cancelled) return r;
                bool ambiguous = Refs.Refs.Any(x => x.ShortName == remote.ShortName && x.FullName != remote.FullName);
                if (r.Success)
                    r = action switch
                    {
                        SyncAction.Rebase => await Git(job, ["-c", "core.commentChar=^", "rebase", remote.Sha]),
                        SyncAction.Merge => await Git(job, ["merge", ambiguous ? remote.FullName : remote.ShortName]),
                        SyncAction.Reset => await Git(job, ["reset", "--hard", remote.Sha]),
                        _ => r,
                    };
                // A cancel reapplies too, unless it left a
                // rebase or merge in progress (PopAutostashAsync keeps the stash then).
                if ((!r.Success && !r.Cancelled) || stashed is null) return r;
                Step(job, vm, "Applying stash...");
                popTried = true;
                if (await Repo.PopAutostashAsync(job, stashed, message) is not { Success: false } pop) return r;
                job.Fail("Apply stash failed");
                return pop;
            });
            // A failed step ends the job and leaves the stash; say where the changes are.
            if (stashed is not null && !popTried) GitRepository.AppendStashKept(job, message);
            return result;
        }, vm, () => Select(Tip(local)));
    }

    Task CheckoutCommitJobAsync(CheckoutViewModel vm, RefPoint point, LocalChangesMode mode) =>
        tab.RunJobAsync($"Checkout '{point.Text}'", async job =>
        {
            var r = await WithSubmodulesAsync(job, vm, async () => (await AutostashAsync(job, vm, mode,
                StashMessage(point.Text, " (detached HEAD)"), () =>
                {
                    Step(job, vm, "Checkout...");
                    return Git(job, ["checkout", "--progress", .. Force(mode), point.Sha]);
                })).Result);
            if (r.Success) job.Succeed("Detached HEAD");
            return r;
        }, vm, () => Select(Refs.HeadSha));

    Task RenameJobAsync(RenameBranchViewModel vm, GitRef local)
    {
        string oldName = vm.OldName, newName = vm.NewName;
        var remote = vm.RenameRemote ? vm.Remote : null;
        return tab.RunJobAsync($"Rename branch '{oldName}'", async job =>
        {
            Step(job, vm, "Renaming branch...");
            var r = await Git(job, ["branch", "--move", "--", oldName, newName]);
            if (!r.Success || remote is null) return r;
            Step(job, vm, "Renaming remote branch...");
            r = await Git(job, ["push", remote, $"{local.Sha}:refs/heads/{newName}", $":refs/heads/{oldName}"]);
            if (!r.Success) return r;
            Step(job, vm, "Updating tracking reference...");
            return await Git(job, ["branch", "--set-upstream-to", $"refs/remotes/{remote}/{newName}", "--", newName]);
        }, vm, () => Select(local.Sha));
    }

    /// <summary><paramref name="targets"/> = the same-name remote branches the dialog showed (see
    /// CorrespondingRemoteBranch), never upstreams of another name; empty = local branches only.</summary>
    Task DeleteBranchesJobAsync(DeleteRefsViewModel vm, IReadOnlyList<GitRef> locals,
        IReadOnlyList<(string Remote, string Branch)> targets, WorktreeEntry? worktree)
    {
        var name = locals.Count == 1 ? $"Delete '{locals[0].ShortName}'" : $"Delete {locals.Count} branches";
        return tab.RunJobAsync(name, async job =>
        {
            if (worktree is not null)
            {
                Step(job, vm, "Deleting worktree...");
                if (await tab.WorktreeOps.RemoveAsync(job, worktree.Path) is { Success: false } removed) return removed;
            }
            Step(job, vm, "Deleting...");
            var r = await Git(job, ["branch", "--delete", "--force", "--", .. locals.Select(b => b.ShortName)]);
            if (!r.Success || targets.Count == 0) return r;
            // Grouped per remote; a failing group does not stop the others, the last error is shown.
            GitResult? failed = null;
            foreach (var g in targets.GroupBy(x => x.Remote, x => x.Branch))
            {
                var branches = g.ToList();
                Step(job, vm, branches.Count == 1 ? $"Deleting '{g.Key}/{branches[0]}'..." : $"Deleting {branches.Count} remote branches...");
                var p = await Git(job, ["push", g.Key, "--delete", .. branches.Select(b => "refs/heads/" + b)]);
                // Already gone on the remote (older git rejects the whole push): one push per branch, a gone one only
                // loses its stale remote-tracking ref, as in DeleteRemoteBranchesJobAsync.
                if (RemoteRefGone(p))
                    foreach (var b in branches)
                    {
                        p = await Git(job, ["push", g.Key, "--delete", "refs/heads/" + b]);
                        // update-ref -d also succeeds when the tracking ref is already gone.
                        if (RemoteRefGone(p)) p = await Git(job, ["update-ref", "-d", $"refs/remotes/{g.Key}/{b}"]);
                        if (!p.Success) failed = p;
                    }
                else if (!p.Success) failed = p;
            }
            return failed ?? r;
        }, vm, () => Select(locals[0].Sha));
    }

    Task DeleteRemoteBranchesJobAsync(DeleteRefsViewModel vm, IReadOnlyList<GitRef> remotes)
    {
        var name = remotes.Count == 1 ? $"Delete '{remotes[0].ShortName}'" : $"Delete {remotes.Count} branches";
        var locals = Refs.Refs.Where(r => r.Kind == RefKind.LocalBranch).ToList();
        return tab.RunJobAsync(name, async job =>
        {
            GitResult? last = null;
            foreach (var b in remotes)
            {
                Step(job, vm, $"Deleting '{b.ShortName}'...");
                var (remote, branch) = SplitRemoteRef(b.FullName) ?? (b.RemoteName!, b.NameInRemote);
                var r = await Git(job, ["push", remote, "--delete", "refs/heads/" + branch]);
                // Already gone on the remote: remove the stale remote-tracking ref instead.
                if (RemoteRefGone(r)) r = await Git(job, ["branch", "-rd", b.ShortName]);
                if (!r.Success) return r;
                foreach (var l in locals.Where(l => l.Upstream == b.FullName))
                {
                    r = await Git(job, ["branch", "--unset-upstream", "--", l.ShortName]);
                    if (!r.Success) return r;
                }
                last = r;
            }
            return last;
        }, vm, () => Select(remotes[0].Sha));
    }

    Task ResetJobAsync(ResetViewModel vm)
    {
        var kind = vm.Kind.ToString().ToLowerInvariant();
        var sha = vm.Target.Sha;
        return tab.RunJobAsync($"Reset '{vm.Branch}' ({kind})", async job =>
        {
            Step(job, vm, $"Resetting '{vm.Branch}'...");
            Task<GitResult> Reset() => Git(job, ["reset", "--" + kind, sha]);
            var r = vm.Kind == ResetKind.Hard ? await WithSubmodulesAsync(job, vm, Reset) : await Reset();
            if (r.Success) job.Succeed($"Reset to '{Short(sha)}'");
            else job.Fail("reset failed");
            return r;
        }, vm, () => Select(sha));
    }

    async Task MergeDialogAsync(RefPoint source, string sourceArg, GitRef? into, string sourceId)
    {
        var destination = into ?? ActiveRef;
        if (destination is null || destination.FullName == sourceId) return;
        var vm = new MergeViewModel(source, Point(destination));
        vm.Preview = PreviewMergeAsync(vm, sourceArg, destination.FullName);
        vm.Run = () => MergeJobAsync(vm, sourceArg, source.Text, destination, vm.Kind, unrelated: false);
        await tab.ShowDialog(vm);
        if (vm.FocusSubjectOnClose) tab.LocalChanges.RequestFocus(LocalChangesFocus.Subject);
    }

    /// <summary>Footer of the Merge dialog: conflict preview (shows nothing when it cannot tell).</summary>
    async Task PreviewMergeAsync(MergeViewModel vm, string source, string destination)
    {
        var conflicts = await Repo.MergeHasConflictsAsync(source, destination);
        if (conflicts is null || vm.IsBusy || vm.Result is not null || vm.KeepOpen) return;
        if (conflicts.Value) vm.SetStatus(DialogStatus.Warning, "Merge will cause conflicts");
        else vm.SetStatus(DialogStatus.Success, "Merge can be done without conflicts");
    }

    Task MergeJobAsync(MergeViewModel? vm, string sourceArg, string sourceName, GitRef destination, MergeKind kind, bool unrelated)
    {
        GitResult? last = null;
        string[] options = kind switch
        {
            MergeKind.NoFastForward => ["--no-ff"],
            MergeKind.Squash => ["--squash"],
            MergeKind.NoCommit => ["--no-ff", "--no-commit"],
            _ => [],
        };
        var name = unrelated ? $"Merge unrelated '{sourceName}'" : $"Merge '{sourceName}' into '{destination.ShortName}'";
        return tab.RunJobAsync(name, async job =>
        {
            GitResult? merged = null; // the `git merge` itself (last may be the submodule update after it)
            bool headMoved = false;   // a cancelled merge that committed anyway (cancel during e.g. a post-merge hook)
            last = await WithSubmodulesAsync(job, vm, async () =>
            {
                if (destination.FullName != Refs.ActiveBranch)
                {
                    Step(job, vm, "Checkout...");
                    var c = GitRepository.RefuseOptionLikeBranch(destination.ShortName) ?? await Git(job, ["checkout", "--progress", destination.ShortName]);
                    if (!c.Success) return c;
                }
                Step(job, vm, "Merging...");
                string[] args = ["merge", sourceArg, .. options, .. (unrelated ? new[] { "--allow-unrelated-histories" } : [])];
                if (vm is null) return merged = await Git(job, args);
                var head = await Repo.ResolveHeadAsync();
                merged = await MergeWithChecksAsync(job, vm, args);
                if (merged.Cancelled) headMoved = await Repo.ResolveHeadAsync() is { } now && now != head;
                return merged;
            });
            if (vm is not null && vm.Checks.HasHooks) ShowChecksResult(vm, kind, merged, last, headMoved);
            return last;
        }, vm, async () =>
        {
            await Select(Refs.HeadSha);
            if (last is null || last.Cancelled) return;
            if (!last.Success)
            {
                // Conflicts are resolved in Local Changes; "unrelated histories" offers its button instead.
                if (GitErrors.Classify(last).Kind != GitErrorKind.UnrelatedHistories) tab.IsLocalChangesView = true;
            }
            else if (kind is MergeKind.Squash or MergeKind.NoCommit)
            {
                tab.IsLocalChangesView = true;
                // A dialog kept open is modal: the subject gets the focus once it closes (MergeDialogAsync).
                if (vm?.KeepOpen == true) vm.FocusSubjectOnClose = true;
                else tab.LocalChanges.RequestFocus(LocalChangesFocus.Subject);
            }
        }, new GitErrorActions(MergeUnrelated: () => MergeJobAsync(null, sourceArg, sourceName, destination, kind, unrelated: true)));
    }

    /// <summary>`git merge` with the dialog's checks panel fed while it runs: the merge's own output (pre-commit's hook
    /// lines), its pending line and what the hooks write to the terminal (such as a test runner's progress), read from a
    /// pseudo-terminal git gets as its controlling terminal. PRE_COMMIT_COLOR=never keeps escape sequences out of the
    /// lines; GIT_MERGE_AUTOEDIT=no (with GIT_EDITOR=true and GIT_TERMINAL_PROMPT=0 from the runner) keeps the terminal
    /// from ever being used for input. The panel never fails the merge.</summary>
    async Task<GitResult> MergeWithChecksAsync(Job job, MergeViewModel vm, IReadOnlyList<string> args) =>
        await vm.Checks.RunAsync(job, () => GitRunner.RunJobAsync(job, tab.Path, args,
            env: new Dictionary<string, string> { ["PRE_COMMIT_COLOR"] = "never", ["GIT_MERGE_AUTOEDIT"] = "no" }, tty: true,
            hooksWait: GitRunner.HooksWait));

    /// <summary>
    /// After a merge whose hooks ran, decided by <paramref name="merged"/> =
    /// `git merge` itself. Committed, a red hook or a cancel: the dialog stays open with the checks as the result (no Git
    /// Error window), Merge gone, the body disabled, Cancel = Close. Any other failure (signing, a hook that is not
    /// pre-commit's, a failing submodule update after the commit) keeps the usual way: the dialog closes and the Git
    /// Error window opens with its actions.
    /// </summary>
    void ShowChecksResult(MergeViewModel vm, MergeKind kind, GitResult? merged, GitResult last, bool headMoved)
    {
        var failed = vm.Checks.Hooks.Where(h => h.IsFailed).Select(h => h.Name).ToList();
        // From `git merge` itself (last may be the submodule update): exit 0, or a cancel that killed git after its
        // commit (HEAD moved, e.g. during a post-merge hook).
        bool committed = merged is { ExitCode: 0 } || (merged is { Cancelled: true } && headMoved);
        if (merged is { Cancelled: true } && !committed)
            // The cancel interrupted the hooks before git's commit (git itself is not signalled while they run, so
            // pre-commit can restore its stash): git then stops like after a failed hook and leaves the merge in progress
            // (MERGE_HEAD), its result staged. Only if git itself had to be interrupted is there no MERGE_HEAD. Nothing is
            // reset for the user.
            vm.SetStatus(DialogStatus.Warning, File.Exists(Path.Combine(Repo.Info.GitDir, "MERGE_HEAD"))
                ? "Cancelled during checks — not merged; the merge is in progress, its result staged in Local Changes (commit it or Abort)"
                : "Cancelled during checks — not merged, but the merged changes are staged in Local Changes; unstage or discard the staged changes if unwanted");
        else if (committed)
        {
            // A failing submodule update afterwards keeps its usual way: the dialog closes, the Git Error window opens.
            // A cancelled one leaves the merge as it is: shown as done.
            if (last is { Success: false, Cancelled: false }) return;
            var done = kind switch
            {
                MergeKind.Squash => "Squash staged in Local Changes",
                MergeKind.NoCommit => "Merge staged in Local Changes",
                _ => "Merged",
            };
            if (failed.Count == 0) vm.SetStatus(DialogStatus.Success, done + ", checks passed");
            else vm.SetStatus(DialogStatus.Warning, $"{done}, but checks failed: {string.Join(", ", failed)}");
        }
        else if (failed.Count > 0)
            vm.SetStatus(DialogStatus.Error, $"Checks failed: {string.Join(", ", failed)} — merge not committed (see Local Changes)");
        else return;
        vm.KeepOpen = true;
        vm.CanSubmit = false;
        vm.SubmitText = null;
        vm.CancelText = "Close";
    }

    Task CreateTagJobAsync(CreateTagViewModel vm)
    {
        string name = vm.Name, message = vm.Message;
        bool push = vm.Push;
        Settings.Set(CreateTagViewModel.PushKey, push);
        var remotes = tab.Remotes.Select(r => r.Name).ToList();
        return tab.RunJobAsync(push ? $"Create and push tag '{name}'" : $"Create tag '{name}'", async job =>
        {
            Step(job, vm, $"Creating '{name}'...");
            var file = Path.GetTempFileName();
            GitResult r;
            try
            {
                await File.WriteAllTextAsync(file, message);
                r = await Git(job, ["tag", "--annotate", name, vm.Start.Arg, "-F", file]);
            }
            finally { File.Delete(file); }
            if (!r.Success || !push) return r;
            foreach (var remote in remotes)
            {
                Step(job, vm, $"Pushing '{name}' to '{remote}'...");
                r = await Git(job, ["push", remote, "refs/tags/" + name, "--verbose", "--progress"]);
                if (!r.Success) return r; // the tag stays local
            }
            job.Succeed("Everything is up to date");
            return r;
        }, vm, () => Select(vm.Start.Sha));
    }

    Task DeleteTagsJobAsync(DeleteRefsViewModel vm, IReadOnlyList<GitRef> tags)
    {
        bool fromRemotes = vm.IsChecked;
        var remotes = tab.Remotes.Select(r => r.Name).ToList();
        var names = tags.Select(t => t.ShortName).ToList();
        return tab.RunJobAsync(tags.Count == 1 ? $"Delete '{names[0]}'" : $"Delete {tags.Count} tags", async job =>
        {
            Step(job, vm, "Deleting...");
            if (fromRemotes)
                foreach (var remote in remotes)
                {
                    // A remote that lacks a tag fails the push: stop before anything is deleted locally.
                    var p = await Git(job, ["push", remote, "--delete", .. names.Select(n => "refs/tags/" + n)]);
                    if (!p.Success) return p;
                }
            return await Git(job, ["tag", "--delete", "--", .. names]); // a tag fetched from a remote may start with "-"
        }, vm, () => Select(tags[0].Sha));
    }

    // ------------------------------------------------------------------ helpers

    Task<GitResult> Git(Job job, IReadOnlyList<string> args) => GitRunner.RunJobAsync(job, tab.Path, args);

    static string[] Force(LocalChangesMode mode) => mode == LocalChangesMode.Discard ? ["--force"] : [];

    static string Short(string sha) => sha.Length > 7 ? sha[..7] : sha;

    /// <summary>Job message and dialog footer.</summary>
    static void Step(Job job, DialogViewModel? vm, string text)
    {
        job.SetMessage(text);
        vm?.SetStatus(DialogStatus.InProgress, text);
    }

    /// <summary>Autostash message: "Autostash. Switch from 'S0' to 'T' &lt;timestamp&gt;", S0 = active branch,
    /// else the short HEAD sha, else empty.</summary>
    string StashMessage(string target, string suffix = "") =>
        $"Autostash. Switch from '{tab.ActiveBranchName ?? (Refs.HeadSha is { } h ? Short(h) : "")}' to '{target}'{suffix} {GitRepository.StashTimestamp()}";

    /// <summary>The autostash runner with the dialog footer; OperationSucceeded = the last run of the operation itself
    /// worked, so a failure came from reapplying the stash (job message "Apply stash failed", not the step's).</summary>
    async Task<(GitResult Result, bool OperationSucceeded)> AutostashAsync(Job job, DialogViewModel? vm, LocalChangesMode mode,
        string stashMessage, Func<Task<GitResult>> operation)
    {
        bool ok = false;
        var r = await Repo.RunWithAutostashAsync(job, mode, stashMessage, async () =>
        {
            var x = await operation();
            ok = x.Success;
            return x;
        }, s => Step(job, vm, s));
        if (!r.Success && ok) job.Fail("Apply stash failed");
        return (r, ok);
    }

    /// <summary>Submodule refresh after switching commits around <paramref name="operation"/>: a failing
    /// update replaces an otherwise successful result.</summary>
    async Task<GitResult> WithSubmodulesAsync(Job job, DialogViewModel? vm, Func<Task<GitResult>> operation)
    {
        var before = await Repo.CaptureSubmodulesAsync();
        var r = await operation();
        if (GitRepository.UpdateSubmodulesOnCheckout && before.Submodules.Count > 0 && !job.CancelRequested)
            Step(job, vm, "Updating submodules...");
        var s = await Repo.UpdateSubmodulesAsync(job, before);
        if (!r.Success || s is not { Success: false }) return r;
        job.Fail("Update submodules failed"); // first finish wins over the caller's message
        return s;
    }

    /// <summary>Fresh dirty check (no working-tree watcher yet, so the snapshot may be old); the snapshot if git fails.</summary>
    async Task<bool> IsDirtyAsync()
    {
        try { return GitRepository.IsDirty(await Repo.ReadStatusAsync(lockFree: true)); }
        catch (GitException) { return GitRepository.IsDirty(tab.Status); }
    }

    IReadOnlySet<string> Names(RefKind kind) =>
        Refs.Refs.Where(r => r.Kind == kind).Select(r => r.ShortName).ToHashSet(StringComparer.OrdinalIgnoreCase);

    internal static RefPoint Point(GitRef r) => new(r.Kind switch
    {
        RefKind.RemoteBranch => "cloud",
        RefKind.Tag => "tag",
        _ => "git-branch",
    }, r.ShortName, null, r.FullName, r.Sha);

    internal async Task<RefPoint> CommitPointAsync(string sha)
    {
        string? subject = null;
        try { subject = (await Repo.ReadHeadersAsync([sha])).FirstOrDefault()?.Subject; }
        catch (GitException) { } // the row then shows the sha only
        return new RefPoint("git-commit-horizontal", Short(sha), subject, sha, sha);
    }

    /// <summary>The given ref, else the given commit, else the active branch, else the HEAD commit (null on an unborn HEAD).</summary>
    async Task<RefPoint?> PointAsync(GitRef? start, string? commitSha) =>
        start is not null ? Point(start)
        : commitSha is not null ? await CommitPointAsync(commitSha)
        : ActiveRef is { } a ? Point(a)
        : Refs.HeadSha is { } h ? await CommitPointAsync(h)
        : null;

    /// <summary>"refs/remotes/&lt;remote&gt;/&lt;branch&gt;" split with the configured remotes (names may contain '/').</summary>
    (string Remote, string Branch)? SplitRemoteRef(string fullRef)
    {
        const string prefix = "refs/remotes/";
        if (!fullRef.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var rest = fullRef[prefix.Length..];
        var remote = tab.Remotes.Select(r => r.Name).Where(n => rest.StartsWith(n + "/", StringComparison.Ordinal))
            .MaxBy(n => n.Length);
        if (remote is not null) return (remote, rest[(remote.Length + 1)..]);
        int slash = rest.IndexOf('/');
        return slash > 0 ? (rest[..slash], rest[(slash + 1)..]) : null;
    }

    /// <summary>The remote branch of the same name as <paramref name="local"/> that exists in the refs of a
    /// configured remote: with an upstream only on the upstream's remote; without one on origin, else on the only
    /// remote that has one; else null. An upstream of another name
    /// (feature/login created from origin/develop) is never a candidate. Deliberately never deletes the upstream branch
    /// (only the same-name remote branch).</summary>
    (string Remote, string Branch)? CorrespondingRemoteBranch(GitRef local)
    {
        var name = local.ShortName;
        // Stale tracking refs of removed remotes do not count.
        var remotes = Refs.Refs.Where(r => r.Kind == RefKind.RemoteBranch).Select(r => SplitRemoteRef(r.FullName))
            .Where(s => s?.Branch == name && tab.Remotes.Any(x => x.Name == s.Value.Remote)).Select(s => s!.Value.Remote).ToList();
        // With an upstream only its remote counts (as Rename); without one origin, else the only remote that has one.
        if (local.Upstream is { } u) return SplitRemoteRef(u)?.Remote is { } ur && remotes.Contains(ur) ? (ur, name) : null;
        var remote = remotes.Contains("origin") ? "origin" : remotes.Count == 1 ? remotes[0] : null;
        return remote is null ? null : (remote, name);
    }

    /// <summary>"push --delete" of a branch that is already gone on the remote (older git; newer git only warns).</summary>
    static bool RemoteRefGone(GitResult r) => !r.Success && r.Stderr.Contains("error: unable to delete", StringComparison.Ordinal)
        && r.Stderr.Contains("remote ref does not exist", StringComparison.Ordinal);

    /// <summary>The ref's current tip after the refresh (it may have moved), else the tip it had.</summary>
    string Tip(GitRef r) => Refs.Refs.FirstOrDefault(x => x.FullName == r.FullName)?.Sha ?? r.Sha;

    Task Select(string? sha)
    {
        if (sha is not null) tab.CommitList.SelectCommit(sha);
        return Task.CompletedTask;
    }
}
