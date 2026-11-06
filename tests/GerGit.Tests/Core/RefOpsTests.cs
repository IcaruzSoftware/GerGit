using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using GerGit.Core;
using GerGit.Core.Git;
using GerGit.ViewModels;
using GerGit.ViewModels.Dialogs;
using GerGit.ViewModels.Dialogs.Refs;

namespace GerGit.Tests.Core;

/// <summary>Shared state of the branch, tag and ref dialog tests: the settings keys and the session-wide failed branch name.</summary>
[CollectionDefinition("RefOps", DisableParallelization = true)]
public sealed class RefOpsCollection;

/// <summary>A tab on a temp repo (optionally with a local bare "origin") whose dialogs are answered by <see cref="Fill"/>
/// and submitted; error windows are recorded.</summary>
sealed class RefFixture : IDisposable
{
    static readonly string[] Keys =
    [
        LocalChangesBlockViewModel.RememberKey, CreateBranchViewModel.CheckoutKey,
        CreateTagViewModel.PushKey, CheckoutSyncViewModel.StashKey,
    ];

    public readonly TempRepo Repo = new();
    public TempRepo? Bare;
    public RepoTabViewModel Tab = null!;
    public readonly List<DialogViewModel> Dialogs = [];
    public readonly List<(GitErrorKind Kind, string Text, GitErrorActions? Actions)> Errors = [];

    /// <summary>Fills the dialog before it is submitted; false = cancel it, null = leave it open (not submitted).</summary>
    public Func<DialogViewModel, bool?> Fill = _ => true;

    public RefFixture()
    {
        foreach (var k in Keys) Settings.Set<string?>(k, null);
        RefOps.FailedBranchName = null;
        Repo.Git("config", "tag.gpgsign", "false");
    }

    public RefOps Ops => Tab.RefOps;
    public Job LastJob => Tab.Jobs.History[0];

    public async Task OpenAsync()
    {
        Tab = new RepoTabViewModel(await GitRepository.OpenAsync(Repo.Path));
        await Tab.RefreshAsync();
        Tab.ShowError = (_, text) => { Errors.Add((GitErrors.Classify(text).Kind, text, null)); return Task.CompletedTask; };
        Tab.ShowGitError = (r, a) => { Errors.Add((GitErrors.Classify(r).Kind, r.ReadableOutput, a)); return Task.CompletedTask; };
        Tab.ShowDialog = async vm =>
        {
            Dialogs.Add(vm);
            if (vm.SubmitText is null) return false; // Close-only windows
            switch (Fill(vm))
            {
                case null: return false;
                case false: vm.Cancel(); return false;
            }
            await vm.SubmitAsync();
            return vm.Result == true;
        };
    }

    /// <summary>A bare "origin" with main pushed and tracked.</summary>
    public void AddOrigin()
    {
        Bare = new TempRepo(init: false);
        Bare.Git("init", "-q", "--bare", "-b", "main");
        Repo.Git("remote", "add", "origin", Bare.Path);
        Repo.Git("push", "-q", "-u", "origin", "main");
    }

    public async Task RefreshAsync() => await Tab.RefreshAsync();

    public GitRef Ref(string fullName) => Tab.Refs.Refs.Single(r => r.FullName == fullName);
    public string Head => Repo.Git("rev-parse", "HEAD");
    public string Branch => Repo.Git("rev-parse", "--abbrev-ref", "HEAD");
    public string Read(string path) => File.ReadAllText(Path.Combine(Repo.Path, path));
    public string[] Stashes => Repo.Git("stash", "list", "--format=%gs").Split('\n', StringSplitOptions.RemoveEmptyEntries);

    public void Dispose()
    {
        Tab?.Dispose();
        Repo.Dispose();
        Bare?.Dispose();
        foreach (var k in Keys) Settings.Set<string?>(k, null);
        RefOps.FailedBranchName = null;
    }
}

/// <summary>Round trips through the RefOps entry points and their dialog view models and remote-branch checkout.</summary>
[Collection("RefOps")]
public class RefOpsTests
{
    const string Five = "l1\nl2\nl3\nl4\nl5\n";

    /// <summary>main: a.txt = 5 lines; branch b changes line 1. Returns the fixture on main.</summary>
    static async Task<RefFixture> TwoBranchesAsync()
    {
        var f = new RefFixture();
        f.Repo.Commit("init", 0, ("a.txt", Five), ("other.txt", "o\n"));
        f.Repo.Git("checkout", "-q", "-b", "b");
        f.Repo.Commit("b1", 1, ("a.txt", Five.Replace("l1", "B1")));
        f.Repo.Git("checkout", "-q", "main");
        await f.OpenAsync();
        return f;
    }

    // ---------------------------------------------------------------- create branch

    [AvaloniaFact]
    public async Task CreateBranch_without_and_with_checkout_prefix_and_failed_name()
    {
        using var f = new RefFixture();
        var c1 = f.Repo.Commit("one", 1, ("a.txt", "1\n"));
        await f.OpenAsync();

        f.Fill = vm => { ((CreateBranchViewModel)vm).Name = "feature/x"; return true; };
        await f.Ops.CreateBranchAsync();
        var create = (CreateBranchViewModel)f.Dialogs[^1];
        Assert.Equal(("main", "refs/heads/main", "Create"), (create.Start.Text, create.Start.Arg, create.SubmitText));
        Assert.Equal("", create.Start.Detail ?? "");
        Assert.Equal(c1, f.Ref("refs/heads/feature/x").Sha);
        Assert.Equal("main", f.Branch);
        Assert.Equal("Create branch 'feature/x'", f.LastJob.Name);
        Assert.Contains("$ git branch --no-track feature/x refs/heads/main", f.LastJob.Output);

        // Recent prefix; the checkbox is remembered and switches the submit text.
        f.Fill = vm =>
        {
            var c = (CreateBranchViewModel)vm;
            Assert.Equal("feature/", c.Name);
            c.Name = "feature/y";
            c.CheckoutAfterCreate = true;
            Assert.Equal("Create and Checkout", c.SubmitText);
            return true;
        };
        await f.Ops.CreateBranchAsync(commitSha: c1);
        Assert.Equal("feature/y", f.Branch);
        Assert.Equal("Switched to branch 'feature/y'", f.LastJob.Message);
        Assert.Contains($"$ git checkout --no-track -b feature/y {c1}", f.LastJob.Output);
        Assert.True(Settings.Get(CreateBranchViewModel.CheckoutKey, false));

        // git rejects a name the rules allow: the name is offered again next time, also in another repository.
        f.Fill = vm => { var c = (CreateBranchViewModel)vm; c.Name = "HEAD"; c.CheckoutAfterCreate = false; return true; };
        await f.Ops.CreateBranchAsync();
        Assert.Equal(GitErrorKind.Generic, Assert.Single(f.Errors).Kind);
        Assert.Equal("HEAD", RefOps.FailedBranchName);
        f.Fill = vm => { Assert.Equal("HEAD", ((CreateBranchViewModel)vm).Name); ((CreateBranchViewModel)vm).Name = "plain"; return true; };
        await f.Ops.CreateBranchAsync();
        Assert.Null(RefOps.FailedBranchName);
        Assert.Null(Settings.Get<string?>(Settings.RepoKey(f.Repo.Path, "recent.branchPrefix"), null)); // no "/" clears it
    }

    // ---------------------------------------------------------------- checkout local branch

    [AvaloniaFact]
    public async Task Checkout_clean_is_quick_without_dialog()
    {
        using var f = await TwoBranchesAsync();
        await f.Ops.CheckoutBranchAsync(f.Ref("refs/heads/b"));
        Assert.Empty(f.Dialogs);
        Assert.Equal("b", f.Branch);
        Assert.Equal(("Checkout branch 'b'", "Switched to branch 'b'"), (f.LastJob.Name, f.LastJob.Message));
        Assert.Contains("$ git checkout b\n", f.LastJob.Output); // --progress is hidden in the header
        Assert.Equal(f.Head, f.Tab.CommitList.SelectedSha);
    }

    static Func<DialogViewModel, bool?> Mode(LocalChangesMode mode) => vm =>
    {
        var local = ((CheckoutViewModel)vm).Local;
        Assert.True(local.IsVisible);
        local.Choice = mode == LocalChangesMode.LeaveAsStash ? LocalChangesMode.StashAndReapply : mode;
        local.ShiftHeld = mode == LocalChangesMode.LeaveAsStash;
        return true;
    };

    [AvaloniaFact]
    public async Task Checkout_dirty_dont_change_shows_gits_error()
    {
        using var f = await TwoBranchesAsync();
        f.Repo.Write("a.txt", Five.Replace("l5", "L5"));
        f.Fill = Mode(LocalChangesMode.DontChange);
        await f.Ops.CheckoutBranchAsync(f.Ref("refs/heads/b"));
        Assert.IsType<CheckoutViewModel>(Assert.Single(f.Dialogs));
        Assert.Equal("main", f.Branch);
        Assert.Contains(GitRepository.OverwrittenByCheckout, Assert.Single(f.Errors).Text);
        Assert.Equal("Checkout failed", f.LastJob.Message);
        Assert.False(Settings.Get(LocalChangesBlockViewModel.RememberKey, true));
    }

    [AvaloniaFact]
    public async Task Checkout_dirty_stash_and_reapply_only_after_failure()
    {
        using var f = await TwoBranchesAsync();
        f.Repo.Write("a.txt", Five.Replace("l5", "L5"));
        f.Fill = Mode(LocalChangesMode.StashAndReapply);
        await f.Ops.CheckoutBranchAsync(f.Ref("refs/heads/b"));
        Assert.Empty(f.Errors);
        Assert.Equal("b", f.Branch);
        Assert.Equal(Five.Replace("l1", "B1").Replace("l5", "L5"), f.Read("a.txt"));
        Assert.Empty(f.Stashes);
        var output = f.LastJob.Output;
        Assert.Contains("gergit: failed to checkout without overwriting local changes. Trying again with stash and reapply...", output);
        Assert.Contains("$ git stash push --message \"Autostash. Switch from 'main' to 'b' ", output);
        Assert.Contains("$ git stash pop --index refs/stash@{0}", output);
        Assert.True(Settings.Get(LocalChangesBlockViewModel.RememberKey, false));

        // A change that does not block the checkout: no stash at all.
        f.Repo.Git("checkout", "--", "a.txt");
        f.Repo.Write("other.txt", "changed\n");
        f.Dialogs.Clear();
        await f.Ops.CheckoutBranchAsync(f.Ref("refs/heads/main"));
        Assert.Equal("main", f.Branch);
        Assert.DoesNotContain("stash", f.LastJob.Output);
        Assert.True(((CheckoutViewModel)f.Dialogs[0]).Local.IsStash); // remembered choice
    }

    [AvaloniaFact]
    public async Task Checkout_dirty_leave_as_stash_and_discard()
    {
        using var f = await TwoBranchesAsync();
        f.Repo.Write("a.txt", Five.Replace("l5", "L5"));
        f.Fill = Mode(LocalChangesMode.LeaveAsStash);
        await f.Ops.CheckoutBranchAsync(f.Ref("refs/heads/b"));
        Assert.Equal("b", f.Branch);
        Assert.Equal(Five.Replace("l1", "B1"), f.Read("a.txt"));
        Assert.StartsWith("On main: Autostash. Switch from 'main' to 'b' ", Assert.Single(f.Stashes));
        Assert.False(Settings.Get(LocalChangesBlockViewModel.RememberKey, true)); // Leave as stash counts as "don't change"

        f.Repo.Write("a.txt", "local\n");
        f.Fill = Mode(LocalChangesMode.Discard);
        await f.Ops.CheckoutBranchAsync(f.Ref("refs/heads/main"));
        Assert.Equal("main", f.Branch);
        Assert.Equal(Five, f.Read("a.txt"));
        Assert.Single(f.Stashes);
        Assert.Contains("$ git checkout --force main", f.LastJob.Output);
    }

    [AvaloniaFact]
    public async Task Checkout_conflict_on_reapply_keeps_the_stash()
    {
        using var f = await TwoBranchesAsync();
        f.Repo.Write("a.txt", Five.Replace("l1", "LOCAL"));
        f.Fill = Mode(LocalChangesMode.StashAndReapply);
        await f.Ops.CheckoutBranchAsync(f.Ref("refs/heads/b"));
        Assert.Equal("b", f.Branch);
        Assert.Equal(GitErrorKind.MergeConflict, Assert.Single(f.Errors).Kind);
        var stash = Assert.Single(f.Stashes);
        Assert.Equal("Apply stash failed", f.LastJob.Message); // the checkout itself worked
        Assert.Contains($"gergit: your local changes are kept in stash@{{0}} '{stash["On main: ".Length..]}'", f.LastJob.Output);
    }

    [AvaloniaFact]
    public async Task Create_branch_with_checkout_and_a_reapply_conflict_is_not_a_failed_name()
    {
        using var f = await TwoBranchesAsync();
        f.Repo.Write("a.txt", Five.Replace("l1", "LOCAL"));
        f.Fill = vm =>
        {
            var c = (CreateBranchViewModel)vm;
            c.Name = "x/nb";
            c.CheckoutAfterCreate = true;
            c.Local.Choice = LocalChangesMode.StashAndReapply;
            return true;
        };
        await f.Ops.CreateBranchAsync(f.Ref("refs/heads/b"));
        Assert.Equal("x/nb", f.Branch);
        Assert.Equal(GitErrorKind.MergeConflict, Assert.Single(f.Errors).Kind);
        Assert.Equal("Apply stash failed", f.LastJob.Message);
        Assert.Null(RefOps.FailedBranchName);
        Assert.Equal("x/", Settings.Get(Settings.RepoKey(f.Repo.Path, "recent.branchPrefix"), ""));
        Assert.Single(f.Stashes);
    }

    // ---------------------------------------------------------------- remote branch decision tree

    /// <summary>origin/feat changes line 1 of a.txt; main is checked out with line 5 changed locally.</summary>
    static async Task<RefFixture> DirtyWithRemoteFeatAsync()
    {
        var f = new RefFixture();
        f.Repo.Commit("init", 0, ("a.txt", Five));
        f.AddOrigin();
        f.Repo.Git("checkout", "-q", "-b", "tmp");
        f.Repo.Commit("feat", 1, ("a.txt", Five.Replace("l1", "F1")));
        f.Repo.Git("push", "-q", "origin", "tmp:refs/heads/feat");
        f.Repo.Git("checkout", "-q", "main");
        f.Repo.Git("branch", "-q", "-D", "tmp");
        f.Repo.Write("a.txt", Five.Replace("l5", "L5"));
        await f.OpenAsync();
        return f;
    }

    [AvaloniaTheory]
    [InlineData(LocalChangesMode.DontChange)]
    [InlineData(LocalChangesMode.StashAndReapply)]
    [InlineData(LocalChangesMode.LeaveAsStash)]
    [InlineData(LocalChangesMode.Discard)]
    public async Task Track_with_a_dirty_tree_in_each_mode(LocalChangesMode mode)
    {
        using var f = await DirtyWithRemoteFeatAsync();
        f.Fill = vm =>
        {
            var local = ((TrackRemoteBranchViewModel)vm).Local;
            Assert.True(local.IsVisible);
            local.Choice = mode == LocalChangesMode.LeaveAsStash ? LocalChangesMode.StashAndReapply : mode;
            local.ShiftHeld = mode == LocalChangesMode.LeaveAsStash;
            return true;
        };
        await f.Ops.CheckoutRemoteBranchAsync(f.Ref("refs/remotes/origin/feat"));
        var feat = Five.Replace("l1", "F1");
        switch (mode)
        {
            case LocalChangesMode.DontChange:
                Assert.Equal("main", f.Branch);
                Assert.Contains(GitRepository.OverwrittenByCheckout, Assert.Single(f.Errors).Text);
                Assert.DoesNotContain(f.Tab.Refs.Refs, r => r.FullName == "refs/heads/feat");
                return;
            case LocalChangesMode.StashAndReapply:
                Assert.Equal(feat.Replace("l5", "L5"), f.Read("a.txt"));
                Assert.Empty(f.Stashes);
                Assert.Contains("gergit: failed to checkout without overwriting local changes", f.LastJob.Output);
                break;
            case LocalChangesMode.LeaveAsStash:
                Assert.Equal(feat, f.Read("a.txt"));
                Assert.StartsWith("On main: Autostash. Switch from 'main' to 'feat' ", Assert.Single(f.Stashes));
                break;
            case LocalChangesMode.Discard:
                Assert.Equal(feat, f.Read("a.txt"));
                Assert.Empty(f.Stashes);
                Assert.Contains("$ git checkout --track -b feat origin/feat --force", f.LastJob.Output);
                break;
        }
        Assert.Empty(f.Errors);
        Assert.Equal("feat", f.Branch);
        Assert.Equal(("Track 'origin/feat'", "Switched to branch 'feat'"), (f.LastJob.Name, f.LastJob.Message));
    }

    [AvaloniaFact]
    public async Task Remote_checkout_without_local_branch_tracks_it()
    {
        using var f = new RefFixture();
        f.Repo.Commit("init", 0, ("a.txt", "1\n"));
        f.AddOrigin();
        f.Repo.Git("push", "-q", "origin", "main:refs/heads/feat");
        await f.OpenAsync();

        f.Fill = vm =>
        {
            var t = (TrackRemoteBranchViewModel)vm;
            Assert.Equal(("feat", "origin/feat", "Track"), (t.Name, t.Remote.Text, t.SubmitText));
            return true;
        };
        await f.Ops.CheckoutRemoteBranchAsync(f.Ref("refs/remotes/origin/feat"));
        Assert.Equal("feat", f.Branch);
        Assert.Equal("refs/remotes/origin/feat", f.Ref("refs/heads/feat").Upstream);
        Assert.Equal(("Track 'origin/feat'", "Switched to branch 'feat'"), (f.LastJob.Name, f.LastJob.Message));
        Assert.Contains("$ git checkout --track -b feat origin/feat", f.LastJob.Output);

        // Same commit as its local branch: active → nothing; else a plain checkout.
        int jobs = f.Tab.Jobs.History.Count;
        await f.Ops.CheckoutRemoteBranchAsync(f.Ref("refs/remotes/origin/feat"));
        Assert.Equal(jobs, f.Tab.Jobs.History.Count);
        await f.Ops.CheckoutRemoteBranchAsync(f.Ref("refs/remotes/origin/main"));
        Assert.Equal("main", f.Branch);
        Assert.Single(f.Dialogs); // only the Track dialog so far
    }

    /// <summary>origin/main one commit ahead of main (behind only), or diverged when <paramref name="diverge"/>; "other" active.</summary>
    static async Task<RefFixture> BehindAsync(bool diverge)
    {
        var f = new RefFixture();
        f.Repo.Commit("init", 0, ("a.txt", Five));
        f.AddOrigin();
        f.Repo.Commit("remote", 1, ("a.txt", Five.Replace("l1", "R1")));
        f.Repo.Git("push", "-q", "origin", "main");
        f.Repo.Git("reset", "-q", "--hard", "HEAD~1");
        if (diverge) f.Repo.Commit("local", 2, ("b.txt", "local\n"));
        f.Repo.Git("checkout", "-q", "-b", "other");
        await f.OpenAsync();
        return f;
    }

    [AvaloniaFact]
    public async Task Remote_checkout_behind_only_fast_forwards()
    {
        using var f = await BehindAsync(diverge: false);
        var remote = f.Ref("refs/remotes/origin/main");
        await f.Ops.CheckoutRemoteBranchAsync(remote);
        var vm = (CheckoutViewModel)Assert.Single(f.Dialogs);
        Assert.Equal(("Checkout and Fast-Forward", "Checkout and Fast-Forward"), (vm.Title, vm.SubmitText));
        Assert.Equal(["Switch to:", "Fast-Forward to:"], vm.Rows.Select(r => r.Label));
        Assert.False(vm.Local.IsVisible);
        Assert.Equal(("main", remote.Sha), (f.Branch, f.Head));
        Assert.Equal(("Checkout branch 'main'", "Fast-forwarded"), (f.LastJob.Name, f.LastJob.Message));
        Assert.Contains("$ git merge --ff-only refs/remotes/origin/main", f.LastJob.Output);
        Assert.Equal(remote.Sha, f.Tab.CommitList.SelectedSha);
    }

    [AvaloniaFact]
    public async Task Checkout_and_fast_forward_retries_with_stash_when_the_merge_would_overwrite()
    {
        using var f = await BehindAsync(diverge: false);
        f.Repo.Write("a.txt", Five.Replace("l5", "L5")); // "other" = main: the checkout keeps it, the ff-merge refuses
        f.Fill = Mode(LocalChangesMode.StashAndReapply);
        var remote = f.Ref("refs/remotes/origin/main");
        await f.Ops.CheckoutRemoteBranchAsync(remote);
        Assert.Empty(f.Errors);
        Assert.Equal(("main", remote.Sha), (f.Branch, f.Head));
        Assert.Equal(Five.Replace("l1", "R1").Replace("l5", "L5"), f.Read("a.txt"));
        Assert.Empty(f.Stashes);
        Assert.Contains("gergit: failed to fast-forward overwriting touching local changes. Trying again with stash and reapply...", f.LastJob.Output);
        Assert.Equal("Fast-forwarded", f.LastJob.Message);
    }

    [AvaloniaFact]
    public async Task Remote_checkout_ahead_only_checks_out_the_local_branch()
    {
        using var f = new RefFixture();
        f.Repo.Commit("init", 0, ("a.txt", "1\n"));
        f.AddOrigin();
        f.Repo.Commit("local", 1, ("a.txt", "2\n"));
        f.Repo.Git("checkout", "-q", "-b", "other");
        await f.OpenAsync();
        await f.Ops.CheckoutRemoteBranchAsync(f.Ref("refs/remotes/origin/main"));
        Assert.Empty(f.Dialogs);
        Assert.Equal("main", f.Branch);
    }

    [AvaloniaTheory]
    [InlineData(SyncAction.Checkout)]
    [InlineData(SyncAction.Merge)]
    [InlineData(SyncAction.Rebase)]
    [InlineData(SyncAction.Reset)]
    public async Task Remote_checkout_diverged_opens_checkout_and_sync(SyncAction action)
    {
        using var f = await BehindAsync(diverge: true);
        var remote = f.Ref("refs/remotes/origin/main").Sha;
        var local = f.Ref("refs/heads/main").Sha;
        f.Repo.Write("a.txt", Five.Replace("l5", "L5")); // dirty: stashed and popped
        f.Fill = vm =>
        {
            var s = (CheckoutSyncViewModel)vm;
            Assert.Equal(("Checkout Branch", "Switch to 'main' branch", "Checkout"), (s.Title, s.Description, s.SubmitText));
            Assert.False(s.Stash);
            s.Action = action;
            s.Stash = true;
            return true;
        };
        await f.Ops.CheckoutRemoteBranchAsync(f.Ref("refs/remotes/origin/main"));
        Assert.Empty(f.Errors);
        Assert.Equal("main", f.Branch);
        Assert.Equal("Checkout branch 'main'", f.LastJob.Name);
        Assert.Contains("$ git stash push --message \"Checkout autostash ", f.LastJob.Output);
        Assert.Empty(f.Stashes);
        Assert.EndsWith("L5\n", f.Read("a.txt"));
        var parents = f.Repo.Git("log", "-1", "--format=%P").Split(' ');
        switch (action)
        {
            case SyncAction.Checkout: Assert.Equal(local, f.Head); break;
            case SyncAction.Merge: Assert.Equal([local, remote], parents); break;
            case SyncAction.Rebase: Assert.Equal([remote], parents); Assert.Equal("local", f.Repo.Git("log", "-1", "--format=%s")); break;
            case SyncAction.Reset: Assert.Equal(remote, f.Head); break;
        }
        Assert.True(Settings.Get(CheckoutSyncViewModel.StashKey, false));
    }

    [AvaloniaFact]
    public async Task Checkout_and_sync_cancelled_while_stashing_reapplies_the_changes()
    {
        using var f = await BehindAsync(diverge: true);
        f.Repo.Write("a.txt", Five.Replace("l5", "L5"));
        var block = new StashBlock(f.Repo, "a.txt");
        f.Fill = vm =>
        {
            var s = (CheckoutSyncViewModel)vm;
            s.Action = SyncAction.Merge;
            s.Stash = true;
            return true;
        };
        var run = f.Ops.CheckoutRemoteBranchAsync(f.Ref("refs/remotes/origin/main"));
        await block.CancelWhenStartedAsync(() => f.Tab.Jobs.Running.Single(j => j.Name == "Checkout branch 'main'"),
            () => Dispatcher.UIThread.RunJobs());
        await run;

        Assert.Empty(f.Errors);
        Assert.Equal(JobOutcome.Canceled, f.LastJob.Outcome);
        Assert.Equal("other", f.Branch); // the checkout never ran
        Assert.Equal(Five.Replace("l5", "L5"), f.Read("a.txt"));
        Assert.Empty(f.Stashes);
        Assert.Contains("$ git stash pop --index refs/stash@{0}\n", f.LastJob.Output);
    }

    [AvaloniaFact]
    public async Task Checkout_and_sync_cancelled_mid_rebase_keeps_the_stash_with_the_hint()
    {
        using var f = await BehindAsync(diverge: true);
        f.Repo.Write("a.txt", Five.Replace("l5", "L5"));
        var block = new StashBlock(f.Repo, "b.txt"); // only the pick of "local" writes b.txt: the rebase is held there
        f.Fill = vm =>
        {
            var s = (CheckoutSyncViewModel)vm;
            s.Action = SyncAction.Rebase;
            s.Stash = true;
            return true;
        };
        var run = f.Ops.CheckoutRemoteBranchAsync(f.Ref("refs/remotes/origin/main"));
        await block.CancelWhenStartedAsync(() => f.Tab.Jobs.Running.Single(j => j.Name == "Checkout branch 'main'"),
            () => Dispatcher.UIThread.RunJobs());
        await run;

        Assert.Empty(f.Errors);
        Assert.Equal(JobOutcome.Canceled, f.LastJob.Outcome);
        Assert.True(Directory.Exists(Path.Combine(f.Repo.Path, ".git", "rebase-merge"))); // the cancel left the rebase
        Assert.DoesNotContain("stash pop", f.LastJob.Output);
        Assert.Matches(@"gergit: your local changes are kept in stash@\{0\} 'Checkout autostash [^']+'\n", f.LastJob.Output);
        Assert.Single(f.Stashes);
        Assert.DoesNotContain("L5", f.Read("a.txt")); // not popped onto the half rebase
    }

    // ---------------------------------------------------------------- checkout commit

    [AvaloniaFact]
    public async Task Checkout_commit_detaches_head_and_names_the_stash()
    {
        using var f = await TwoBranchesAsync();
        var target = f.Ref("refs/heads/b").Sha;
        f.Repo.Write("other.txt", "x\n");
        f.Fill = Mode(LocalChangesMode.LeaveAsStash);
        await f.Ops.CheckoutCommitAsync(target);
        var vm = (CheckoutViewModel)Assert.Single(f.Dialogs);
        Assert.Equal(("Checkout Commit", "Commit to checkout:", "b1"), (vm.Title, vm.Rows[0].Label, vm.Rows[0].Point.Detail));
        Assert.Equal("HEAD", f.Branch);
        Assert.Equal(target, f.Head);
        Assert.True(f.Tab.Refs.IsDetached);
        Assert.Equal(($"Checkout '{target[..7]}'", "Detached HEAD"), (f.LastJob.Name, f.LastJob.Message));
        Assert.Matches($@"^On main: Autostash\. Switch from 'main' to '{target[..7]}' \(detached HEAD\) \d{{4}}-", Assert.Single(f.Stashes));
    }

    // ---------------------------------------------------------------- rename / delete

    [AvaloniaFact]
    public async Task Rename_branch_locally_and_on_the_remote()
    {
        using var f = new RefFixture();
        f.Repo.Commit("init", 0, ("a.txt", "1\n"));
        f.AddOrigin();
        f.Repo.Git("checkout", "-q", "-b", "feat");
        f.Repo.Git("push", "-q", "-u", "origin", "feat");
        f.Repo.Git("branch", "solo");
        await f.OpenAsync();

        f.Fill = vm => { ((RenameBranchViewModel)vm).NewName = "solo2"; return true; };
        await f.Ops.RenameBranchAsync(f.Ref("refs/heads/solo"));
        Assert.False(((RenameBranchViewModel)f.Dialogs[^1]).ShowRenameRemote);
        Assert.NotNull(f.Ref("refs/heads/solo2"));

        f.Fill = vm =>
        {
            var r = (RenameBranchViewModel)vm;
            Assert.Equal("Also rename origin/feat", r.RenameRemoteText);
            r.NewName = "feat2";
            r.RenameRemote = true;
            return true;
        };
        await f.Ops.RenameBranchAsync(f.Ref("refs/heads/feat"));
        Assert.Empty(f.Errors);
        Assert.Equal("feat2", f.Branch);
        Assert.Equal("refs/remotes/origin/feat2", f.Ref("refs/heads/feat2").Upstream);
        Assert.Equal("main\nfeat2", f.Bare!.Git("for-each-ref", "--format=%(refname:short)", "--sort=-refname", "refs/heads"));
        Assert.Equal("Rename branch 'feat'", f.LastJob.Name);
    }

    [AvaloniaFact]
    public async Task Rename_with_a_failing_remote_push_keeps_the_local_rename()
    {
        using var f = new RefFixture();
        f.Repo.Commit("init", 0, ("a.txt", "1\n"));
        f.AddOrigin();
        f.Repo.Git("remote", "set-url", "origin", Path.Combine(f.Bare!.Path, "missing"));
        await f.OpenAsync();
        f.Fill = vm => { var r = (RenameBranchViewModel)vm; r.NewName = "trunk"; r.RenameRemote = true; return true; };
        await f.Ops.RenameBranchAsync(f.Ref("refs/heads/main"));
        Assert.Equal("trunk", f.Branch);
        Assert.Single(f.Errors);
        Assert.Equal(JobOutcome.Failed, f.LastJob.Outcome);
        Assert.DoesNotContain("--set-upstream-to", f.LastJob.Output);
        Assert.Equal("main", f.Bare.Git("for-each-ref", "--format=%(refname:short)", "refs/heads"));
    }

    [AvaloniaFact]
    public async Task Delete_local_branches_with_their_remote_branches()
    {
        using var f = new RefFixture();
        f.Repo.Commit("init", 0, ("a.txt", "1\n"));
        f.AddOrigin();
        foreach (var b in new[] { "x", "y" })
        {
            f.Repo.Git("branch", b);
            f.Repo.Git("push", "-q", "-u", "origin", b);
        }
        f.Repo.Git("branch", "z");
        await f.OpenAsync();

        f.Fill = vm => { ((DeleteRefsViewModel)vm).IsChecked = true; return true; };
        await f.Ops.DeleteBranchesAsync([f.Ref("refs/heads/x"), f.Ref("refs/heads/y"), f.Ref("refs/heads/z")]);
        var vm = (DeleteRefsViewModel)f.Dialogs[^1];
        Assert.Equal(("Delete Branches", "Delete 3 branches", "Also delete corresponding _remote branches"), (vm.Title, vm.SubmitText, vm.CheckText));
        Assert.Equal(new string?[] { "origin/x", "origin/y", null }, vm.Rows.Select(r => r.RemoteBranch));
        Assert.Empty(f.Errors);
        Assert.DoesNotContain(f.Tab.Refs.Refs, r => r.ShortName is "x" or "y" or "z");
        Assert.Equal("main", f.Bare!.Git("for-each-ref", "--format=%(refname:short)", "refs/heads"));
        Assert.Equal("Delete 3 branches", f.LastJob.Name);
        Assert.Contains("$ git branch --delete --force -- x y z", f.LastJob.Output);
        Assert.Contains("$ git push origin --delete refs/heads/x refs/heads/y", f.LastJob.Output);
    }

    /// <summary>feature/login created from origin/develop: its upstream "develop" has another name.</summary>
    static RefFixture OtherNameUpstream(params string[] branches)
    {
        var f = new RefFixture();
        f.Repo.Commit("init", 0, ("a.txt", "1\n"));
        f.AddOrigin();
        f.Repo.Git("push", "-q", "origin", "main:refs/heads/develop");
        f.Repo.Git("fetch", "-q", "origin");
        foreach (var b in branches) f.Repo.Git("branch", "--track", b, "origin/develop");
        return f;
    }

    static string BareBranches(RefFixture f) => f.Bare!.Git("for-each-ref", "--format=%(refname:short)", "refs/heads");

    [AvaloniaFact]
    public async Task Delete_branch_never_deletes_an_upstream_of_another_name()
    {
        using var f = OtherNameUpstream("feature/login");
        await f.OpenAsync();
        f.Fill = vm => { ((DeleteRefsViewModel)vm).IsChecked = true; return true; }; // forced, although disabled
        await f.Ops.DeleteBranchesAsync([f.Ref("refs/heads/feature/login")]);
        var vm = (DeleteRefsViewModel)f.Dialogs[^1];
        Assert.Equal(("Also delete corresponding _remote branch", false, null), (vm.CheckText, vm.CheckEnabled, vm.CheckDetail));
        Assert.Null(vm.First.RemoteBranch);
        Assert.Empty(f.Errors);
        Assert.DoesNotContain(f.Tab.Refs.Refs, r => r.FullName == "refs/heads/feature/login");
        Assert.DoesNotContain("$ git push", f.LastJob.Output);
        Assert.Equal("develop\nmain", BareBranches(f));
        Assert.NotNull(f.Ref("refs/remotes/origin/develop"));
    }

    [AvaloniaFact]
    public async Task Delete_branch_deletes_the_same_name_remote_branch_not_the_upstream()
    {
        using var f = OtherNameUpstream("feature/login");
        f.Repo.Git("push", "-q", "origin", "main:refs/heads/feature/login");
        await f.OpenAsync();
        f.Fill = vm => { ((DeleteRefsViewModel)vm).IsChecked = true; return true; };
        await f.Ops.DeleteBranchesAsync([f.Ref("refs/heads/feature/login")]);
        var vm = (DeleteRefsViewModel)f.Dialogs[^1];
        Assert.Equal(("Also delete _remote branch", true, "origin/feature/login"), (vm.CheckText, vm.CheckEnabled, vm.CheckDetail));
        Assert.Empty(f.Errors);
        Assert.Contains("$ git push origin --delete refs/heads/feature/login", f.LastJob.Output);
        Assert.DoesNotContain("refs/heads/develop", f.LastJob.Output);
        Assert.Equal("develop\nmain", BareBranches(f));
        Assert.DoesNotContain(f.Tab.Refs.Refs, r => r.ShortName is "feature/login" or "origin/feature/login");
    }

    [AvaloniaFact]
    public async Task Delete_branches_mixed_deletes_only_same_name_remote_branches()
    {
        using var f = OtherNameUpstream("feature/login", "feature/signup");
        f.Repo.Git("push", "-q", "origin", "main:refs/heads/feature/login");
        f.Repo.Git("branch", "a");
        f.Repo.Git("push", "-q", "-u", "origin", "a"); // same-name upstream
        await f.OpenAsync();
        f.Fill = vm => { ((DeleteRefsViewModel)vm).IsChecked = true; return true; };
        await f.Ops.DeleteBranchesAsync([f.Ref("refs/heads/a"), f.Ref("refs/heads/feature/login"), f.Ref("refs/heads/feature/signup")]);
        var vm = (DeleteRefsViewModel)f.Dialogs[^1];
        Assert.True(vm.CheckEnabled);
        Assert.Equal(new string?[] { "origin/a", "origin/feature/login", null }, vm.Rows.Select(r => r.RemoteBranch));
        Assert.Empty(f.Errors);
        Assert.Contains("$ git push origin --delete refs/heads/a refs/heads/feature/login\n", f.LastJob.Output);
        Assert.DoesNotContain("refs/heads/develop", f.LastJob.Output);
        Assert.DoesNotContain("refs/heads/feature/signup", f.LastJob.Output);
        Assert.Equal("develop\nmain", BareBranches(f));
    }

    /// <summary>A second bare remote <paramref name="name"/> with the given branches (all at main), fetched.</summary>
    static TempRepo AddBareRemote(RefFixture f, string name, params string[] branches)
    {
        var bare = new TempRepo(init: false);
        bare.Git("init", "-q", "--bare", "-b", "main");
        f.Repo.Git("remote", "add", name, bare.Path);
        foreach (var b in branches) f.Repo.Git("push", "-q", name, $"main:refs/heads/{b}");
        f.Repo.Git("fetch", "-q", name);
        return bare;
    }

    [AvaloniaFact]
    public async Task Delete_branch_with_an_upstream_ignores_same_name_branches_on_other_remotes()
    {
        using var f = OtherNameUpstream("feature/login");
        using var other = AddBareRemote(f, "other", "feature/login");
        await f.OpenAsync();
        f.Fill = vm => { ((DeleteRefsViewModel)vm).IsChecked = true; return true; }; // forced, although disabled
        await f.Ops.DeleteBranchesAsync([f.Ref("refs/heads/feature/login")]);
        var vm = (DeleteRefsViewModel)f.Dialogs[^1];
        Assert.Equal((false, null), (vm.CheckEnabled, vm.CheckDetail));
        Assert.DoesNotContain("$ git push", f.LastJob.Output);
        Assert.Equal("feature/login", other.Git("for-each-ref", "--format=%(refname:short)", "refs/heads"));
        Assert.Equal("develop\nmain", BareBranches(f));
    }

    [AvaloniaFact]
    public async Task Delete_branch_prefers_the_upstreams_remote()
    {
        using var f = new RefFixture();
        f.Repo.Commit("init", 0, ("a.txt", "1\n"));
        f.AddOrigin();
        f.Repo.Git("push", "-q", "origin", "main:refs/heads/feature/login");
        using var other = AddBareRemote(f, "other", "develop", "feature/login");
        f.Repo.Git("fetch", "-q", "origin");
        f.Repo.Git("branch", "--track", "feature/login", "other/develop");
        await f.OpenAsync();
        f.Fill = vm => { ((DeleteRefsViewModel)vm).IsChecked = true; return true; };
        await f.Ops.DeleteBranchesAsync([f.Ref("refs/heads/feature/login")]);
        Assert.Equal("other/feature/login", ((DeleteRefsViewModel)f.Dialogs[^1]).CheckDetail);
        Assert.Empty(f.Errors);
        Assert.Contains("$ git push other --delete refs/heads/feature/login", f.LastJob.Output);
        Assert.Equal("develop", other.Git("for-each-ref", "--format=%(refname:short)", "refs/heads"));
        Assert.Equal("feature/login\nmain", BareBranches(f));
    }

    [AvaloniaFact]
    public async Task Delete_branch_ignores_tracking_refs_of_removed_remotes()
    {
        using var f = new RefFixture();
        f.Repo.Commit("init", 0, ("a.txt", "1\n"));
        f.AddOrigin();
        f.Repo.Git("branch", "solo");
        f.Repo.Git("update-ref", "refs/remotes/old/solo", "main"); // stale: there is no remote "old"
        await f.OpenAsync();
        f.Fill = _ => null;
        await f.Ops.DeleteBranchesAsync([f.Ref("refs/heads/solo")]);
        var vm = (DeleteRefsViewModel)f.Dialogs[^1];
        Assert.Equal((false, null), (vm.CheckEnabled, vm.CheckDetail));
    }

    /// <summary>"solo" without an upstream; <paramref name="have"/> = the remotes that have solo (origin always exists).</summary>
    [AvaloniaTheory]
    [InlineData("origin", "origin")]
    [InlineData("origin,other", "origin")]
    [InlineData("other", "other")]
    [InlineData("other,third", null)]
    public async Task Delete_branch_without_upstream_prefers_origin_else_the_only_remote(string have, string? target)
    {
        using var f = new RefFixture();
        f.Repo.Commit("init", 0, ("a.txt", "1\n"));
        f.AddOrigin();
        var bares = new Dictionary<string, TempRepo> { ["origin"] = f.Bare! };
        foreach (var r in have.Split(','))
            if (r == "origin") f.Repo.Git("push", "-q", "origin", "main:refs/heads/solo");
            else bares[r] = AddBareRemote(f, r, "solo");
        f.Repo.Git("fetch", "-q", "origin");
        f.Repo.Git("branch", "solo");
        try
        {
            await f.OpenAsync();
            Assert.Null(f.Ref("refs/heads/solo").Upstream);
            f.Fill = vm => { ((DeleteRefsViewModel)vm).IsChecked = true; return true; }; // forced, also when disabled
            await f.Ops.DeleteBranchesAsync([f.Ref("refs/heads/solo")]);
            var vm = (DeleteRefsViewModel)f.Dialogs[^1];
            Assert.Equal((target is not null, target is null ? null : $"{target}/solo"), (vm.CheckEnabled, vm.CheckDetail));
            Assert.Empty(f.Errors);
            Assert.DoesNotContain(f.Tab.Refs.Refs, r => r.FullName == "refs/heads/solo");
            if (target is null) Assert.DoesNotContain("$ git push", f.LastJob.Output);
            else Assert.Contains($"$ git push {target} --delete refs/heads/solo\n", f.LastJob.Output);
            foreach (var (name, bare) in bares)
                Assert.Equal((name, name != target && have.Split(',').Contains(name)),
                    (name, bare.Git("for-each-ref", "--format=%(refname:short)", "refs/heads").Split('\n').Contains("solo")));
        }
        finally
        {
            foreach (var (name, bare) in bares) if (name != "origin") bare.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task Delete_branches_job_uses_the_targets_the_dialog_showed()
    {
        using var f = new RefFixture();
        f.Repo.Commit("init", 0, ("a.txt", "1\n"));
        f.AddOrigin();
        f.Repo.Git("branch", "a");
        f.Repo.Git("push", "-q", "-u", "origin", "a");
        f.Repo.Git("branch", "b"); // no upstream, no origin/b yet
        await f.OpenAsync();
        f.Tab.ShowDialog = async dialog =>
        {
            var vm = (DeleteRefsViewModel)dialog;
            f.Dialogs.Add(vm);
            // Meanwhile (auto-fetch, watcher) origin/b appears; the dialog did not show it.
            f.Repo.Git("push", "-q", "origin", "b");
            f.Repo.Git("fetch", "-q", "origin");
            await f.RefreshAsync();
            vm.IsChecked = true;
            await vm.SubmitAsync();
            return vm.Result == true;
        };
        await f.Ops.DeleteBranchesAsync([f.Ref("refs/heads/a"), f.Ref("refs/heads/b")]);
        var shown = (DeleteRefsViewModel)f.Dialogs[^1];
        Assert.Equal(new string?[] { "origin/a", null }, shown.Rows.Select(r => r.RemoteBranch));
        Assert.Empty(f.Errors);
        Assert.Contains("$ git push origin --delete refs/heads/a\n", f.LastJob.Output);
        Assert.Equal("b\nmain", BareBranches(f));
    }

    [AvaloniaFact]
    public async Task Delete_remote_branches_fallback_and_unset_upstream()
    {
        using var f = new RefFixture();
        f.Repo.Commit("init", 0, ("a.txt", "1\n"));
        f.AddOrigin();
        f.Repo.Git("branch", "y");
        f.Repo.Git("push", "-q", "-u", "origin", "y");
        f.Repo.Git("push", "-q", "origin", "main:refs/heads/gone");
        f.Bare!.Git("branch", "-D", "gone"); // already deleted on the remote, still known locally
        await f.OpenAsync();

        await f.Ops.DeleteRemoteBranchesAsync([f.Ref("refs/remotes/origin/y"), f.Ref("refs/remotes/origin/gone")]);
        Assert.Empty(f.Errors);
        Assert.DoesNotContain(f.Tab.Refs.Refs, r => r.Kind == RefKind.RemoteBranch && r.ShortName != "origin/main");
        Assert.Equal("main", f.Bare.Git("for-each-ref", "--format=%(refname:short)", "refs/heads"));
        Assert.Null(f.Ref("refs/heads/y").Upstream); // tracking removed
        Assert.Contains("$ git branch --unset-upstream -- y", f.LastJob.Output);
        // git ≥ 2.4x only warns about deleting a non-existent remote ref, so the `branch -rd` fallback for older
        // git's "unable to delete ... remote ref does not exist" is not reached here; the outcome is the same.
        Assert.Equal("Delete 2 branches", f.LastJob.Name);
    }

    [AvaloniaFact]
    public async Task Stale_branches_are_those_with_a_gone_upstream()
    {
        using var f = new RefFixture();
        f.Repo.Commit("init", 0, ("a.txt", "1\n"));
        f.AddOrigin();
        f.Repo.Git("branch", "s");
        f.Repo.Git("push", "-q", "-u", "origin", "s");
        f.Repo.Git("push", "-q", "origin", "--delete", "s");
        await f.OpenAsync();
        Assert.Equal(["refs/heads/s"], f.Ops.StaleBranches().Select(r => r.FullName));
    }

    // ---------------------------------------------------------------- reset

    [AvaloniaTheory]
    [InlineData(ResetKind.Soft, "a.txt|")]
    [InlineData(ResetKind.Mixed, "|a.txt")]
    [InlineData(ResetKind.Hard, "|")]
    public async Task Reset_soft_mixed_hard(ResetKind kind, string status)
    {
        using var f = new RefFixture();
        var c1 = f.Repo.Commit("one", 0, ("a.txt", "1\n"));
        f.Repo.Commit("two", 1, ("a.txt", "2\n"));
        await f.OpenAsync();
        f.Fill = vm =>
        {
            var r = (ResetViewModel)vm;
            Assert.Equal(("Reset Current Branch to Revision", "Move the 'main' branch HEAD to the selected revision"), (r.Title, r.Description));
            Assert.Equal(ResetKind.Mixed, r.Kind);
            r.Kind = kind;
            return true;
        };
        await f.Ops.ResetAsync(c1);
        Assert.Equal(c1, f.Head);
        Assert.Equal(status, $"{f.Repo.Git("diff", "--cached", "--name-only")}|{f.Repo.Git("diff", "--name-only")}");
        var k = kind.ToString().ToLowerInvariant();
        Assert.Equal(($"Reset 'main' ({k})", $"Reset to '{c1[..7]}'"), (f.LastJob.Name, f.LastJob.Message));
        Assert.Contains($"$ git reset --{k} {c1}", f.LastJob.Output);
        Assert.Equal(c1, f.Tab.CommitList.SelectedSha);
    }

    // ---------------------------------------------------------------- merge

    [AvaloniaTheory]
    [InlineData(MergeKind.Default)]
    [InlineData(MergeKind.NoFastForward)]
    [InlineData(MergeKind.Squash)]
    [InlineData(MergeKind.NoCommit)]
    public async Task Merge_options(MergeKind kind)
    {
        using var f = await TwoBranchesAsync();
        var main = f.Head;
        var b = f.Ref("refs/heads/b").Sha;
        f.Fill = vm => { ((MergeViewModel)vm).Kind = kind; return true; };
        await f.Ops.MergeAsync(f.Ref("refs/heads/b"));
        var vm = (MergeViewModel)Assert.Single(f.Dialogs);
        Assert.Equal(("b", "main"), (vm.Source.Text, vm.Destination.Text));
        Assert.Empty(f.Errors);
        Assert.Equal("Merge 'b' into 'main'", f.LastJob.Name);
        bool mergeHead = File.Exists(Path.Combine(f.Repo.Path, ".git", "MERGE_HEAD"));
        switch (kind)
        {
            case MergeKind.Default: Assert.Equal(b, f.Head); break;
            case MergeKind.NoFastForward: Assert.Equal([main, b], f.Repo.Git("log", "-1", "--format=%P").Split(' ')); break;
            case MergeKind.Squash:
                Assert.Equal((main, false), (f.Head, mergeHead));
                Assert.Equal("M  a.txt", f.Repo.Git("status", "--porcelain"));
                break;
            case MergeKind.NoCommit:
                Assert.Equal((main, true), (f.Head, mergeHead));
                Assert.Contains("--no-ff --no-commit", f.LastJob.Output);
                break;
        }
        Assert.Equal(kind is MergeKind.Squash or MergeKind.NoCommit, f.Tab.IsLocalChangesView);
        Assert.Equal(f.Head, f.Tab.CommitList.SelectedSha);
    }

    [AvaloniaFact]
    public async Task Merge_dialog_submitted_unchanged_creates_a_merge_commit_where_fast_forward_was_possible()
    {
        using var f = await TwoBranchesAsync();
        var main = f.Head;
        var b = f.Ref("refs/heads/b").Sha;
        await f.Ops.MergeAsync(f.Ref("refs/heads/b"));
        Assert.Empty(f.Errors);
        Assert.Equal($"{main} {b}", f.Repo.Git("log", "-1", "--format=%P"));
    }

    [AvaloniaFact]
    public async Task Merge_dialog_always_preselects_no_fast_forward()
    {
        using var f = await TwoBranchesAsync();
        var preselected = new List<MergeKind>();
        f.Fill = vm => { preselected.Add(((MergeViewModel)vm).Kind); ((MergeViewModel)vm).Kind = MergeKind.Squash; return true; };
        await f.Ops.MergeAsync(f.Ref("refs/heads/b"));
        Assert.Contains("--squash", f.LastJob.Output);
        f.Fill = vm => { preselected.Add(((MergeViewModel)vm).Kind); return null; }; // the second dialog stays open
        await f.Ops.MergeAsync(f.Ref("refs/heads/b"));
        Assert.Equal([MergeKind.NoFastForward, MergeKind.NoFastForward], preselected);
    }

    [AvaloniaFact]
    public async Task Merge_preview_conflict_and_the_merge_conflict_state()
    {
        using var f = new RefFixture();
        f.Repo.Commit("init", 0, ("a.txt", Five), ("other.txt", "o\n"));
        f.Repo.Git("checkout", "-q", "-b", "b");
        f.Repo.Commit("b1", 1, ("a.txt", Five.Replace("l1", "B1")));
        f.Repo.Git("checkout", "-q", "-b", "clean", "main");
        f.Repo.Commit("clean", 2, ("other.txt", "c\n"));
        f.Repo.Git("checkout", "-q", "main");
        f.Repo.Commit("main1", 3, ("a.txt", Five.Replace("l1", "M1")));
        await f.OpenAsync();

        MergeViewModel? vm = null;
        f.Fill = d => { vm = (MergeViewModel)d; return null; }; // left open: the preview shows in its footer
        await f.Ops.MergeAsync(f.Ref("refs/heads/b"));
        await vm!.Preview;
        Assert.Equal((DialogStatus.Warning, "Merge will cause conflicts"), (vm.Status, vm.StatusText));
        await f.Ops.MergeAsync(f.Ref("refs/heads/b"), f.Ref("refs/heads/clean"));
        await vm.Preview;
        Assert.Equal((DialogStatus.Success, "Merge can be done without conflicts"), (vm.Status, vm.StatusText));

        f.Fill = _ => true;
        await f.Ops.MergeAsync(f.Ref("refs/heads/b"));
        Assert.Equal(GitErrorKind.MergeConflict, Assert.Single(f.Errors).Kind);
        Assert.True(File.Exists(Path.Combine(f.Repo.Path, ".git", "MERGE_HEAD"))); // left for the conflict flow
        Assert.True(f.Tab.IsLocalChangesView);
    }

    [AvaloniaFact]
    public async Task Merge_into_another_branch_checks_it_out_first_and_commits_can_be_merged()
    {
        using var f = await TwoBranchesAsync();
        f.Repo.Git("branch", "dest");
        var b = f.Ref("refs/heads/b").Sha;
        var dest = f.Head;
        await f.RefreshAsync();
        await f.Ops.MergeCommitAsync(b, f.Ref("refs/heads/dest"));
        Assert.Equal(("dest", $"{dest} {b}"), (f.Branch, f.Repo.Git("log", "-1", "--format=%P"))); // --no-ff preselected
        Assert.Equal($"Merge '{b[..7]}' into 'dest'", f.LastJob.Name);
        Assert.Contains("$ git checkout dest", f.LastJob.Output);
        Assert.Contains($"$ git merge {b}", f.LastJob.Output);

        // Same name as a tag: the full ref is used.
        f.Repo.Git("tag", "b", "main");
        await f.RefreshAsync();
        await f.Ops.MergeAsync(f.Ref("refs/heads/b"));
        Assert.Contains("$ git merge refs/heads/b", f.LastJob.Output);
    }

    [AvaloniaFact]
    public async Task Merge_unrelated_histories_offers_the_rerun()
    {
        using var f = new RefFixture();
        f.Repo.Commit("init", 0, ("a.txt", "1\n"));
        f.Repo.Git("checkout", "-q", "--orphan", "orphan");
        f.Repo.Git("rm", "-q", "-rf", ".");
        f.Repo.Commit("orphan", 1, ("o.txt", "o\n"));
        f.Repo.Git("checkout", "-q", "main");
        await f.OpenAsync();

        MergeViewModel? vm = null;
        f.Fill = d => { vm = (MergeViewModel)d; return null; };
        await f.Ops.MergeAsync(f.Ref("refs/heads/orphan"));
        await vm!.Preview;
        Assert.Equal((DialogStatus.None, ""), (vm.Status, vm.StatusText)); // no common base: no preview

        f.Fill = _ => true;
        await f.Ops.MergeAsync(f.Ref("refs/heads/orphan"));
        var error = Assert.Single(f.Errors);
        Assert.Equal(GitErrorKind.UnrelatedHistories, error.Kind);
        Assert.False(f.Tab.IsLocalChangesView);

        await error.Actions!.MergeUnrelated!();
        Assert.Equal("Merge unrelated 'orphan'", f.LastJob.Name);
        Assert.Contains("--allow-unrelated-histories", f.LastJob.Output);
        Assert.Equal(2, f.Repo.Git("log", "-1", "--format=%P").Split(' ').Length);
        Assert.Single(f.Errors);
    }

    // ---------------------------------------------------------------- tags

    [AvaloniaFact]
    public async Task Create_tag_push_details_and_delete_with_remote()
    {
        using var f = new RefFixture();
        var c = f.Repo.Commit("init", 0, ("a.txt", "1\n"));
        f.AddOrigin();
        await f.OpenAsync();

        f.Fill = vm =>
        {
            var t = (CreateTagViewModel)vm;
            Assert.Equal(("Push", "Create"), (t.PushText, t.SubmitText));
            t.Name = "v1";
            t.Message = "Release 1\n\nNotes";
            t.Push = true;
            Assert.Equal("Create and Push", t.SubmitText);
            return true;
        };
        await f.Ops.CreateTagAsync();
        Assert.Empty(f.Errors);
        Assert.Equal(("Create and push tag 'v1'", "Everything is up to date"), (f.LastJob.Name, f.LastJob.Message));
        Assert.Equal("tag", f.Repo.Git("cat-file", "-t", "v1"));
        Assert.Equal(c, f.Bare!.Git("rev-parse", "v1^{commit}"));
        Assert.Contains("$ git tag --annotate v1 refs/heads/main -F ", f.LastJob.Output);

        var details = await f.Tab.Repo.ReadTagDetailsAsync(f.Ref("refs/tags/v1"));
        Assert.Equal(("Test User", "test@example.com", "Release 1\n\nNotes"), (details.TaggerName, details.TaggerEmail, details.Message));
        await f.Ops.ShowTagDetailsAsync(f.Ref("refs/tags/v1"));
        var dvm = (TagDetailsViewModel)f.Dialogs[^1];
        Assert.Equal(("v1", "Release 1\n\nNotes"), (dvm.TagName, dvm.Message));
        Assert.Matches(@"^\d{1,2} [A-Z][a-z]{2} \d{4} \d{2}:\d{2}:\d{2} [+-]\d{2}:\d{2}$", dvm.Date);

        // Tag a tag start point: the commit, not the tag object.
        f.Fill = vm => { ((CreateTagViewModel)vm).Name = "v1b"; ((CreateTagViewModel)vm).Push = false; return true; };
        await f.Ops.CreateTagAsync(f.Ref("refs/tags/v1"));
        Assert.Contains($"$ git tag --annotate v1b {c} -F ", f.LastJob.Output);
        Assert.Equal("Create tag 'v1b'", f.LastJob.Name);

        // Deleting from remotes first: a remote that fails stops the job before anything is deleted locally.
        f.Repo.Git("remote", "add", "broken", Path.Combine(f.Bare.Path, "missing"));
        await f.RefreshAsync();
        f.Fill = vm => { ((DeleteRefsViewModel)vm).IsChecked = true; return true; };
        await f.Ops.DeleteTagsAsync([f.Ref("refs/tags/v1b")]);
        Assert.Single(f.Errors);
        Assert.NotNull(f.Ref("refs/tags/v1b"));
        Assert.DoesNotContain("$ git tag --delete", f.LastJob.Output);

        f.Errors.Clear();
        f.Repo.Git("remote", "remove", "broken");
        await f.RefreshAsync();
        await f.Ops.DeleteTagsAsync([f.Ref("refs/tags/v1")]);
        Assert.Empty(f.Errors);
        Assert.Equal("Delete 'v1'", f.LastJob.Name);
        Assert.DoesNotContain(f.Tab.Refs.Refs, r => r.FullName == "refs/tags/v1");
        Assert.Equal("", f.Bare.Git("tag"));
    }

    [Fact]
    public void Tag_object_parsing()
    {
        var d = GitRepository.ParseTagObject("object abc\ntype commit\ntag v1\ntagger A B <a@b> 1741179789 +0100\n\nmsg\nline 2\n")!;
        Assert.Equal(("A B", "a@b", "msg\nline 2"), (d.TaggerName, d.TaggerEmail, d.Message));
        Assert.Equal("5 Mar 2025 14:03:09 +01:00", new TagDetailsViewModel("v1", d).Date);
        Assert.Null(GitRepository.ParseTagObject("garbage"));
    }
}
