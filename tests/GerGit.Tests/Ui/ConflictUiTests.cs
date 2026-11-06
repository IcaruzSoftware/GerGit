using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GerGit.Core;
using GerGit.ViewModels;
using GerGit.Views;
using static GerGit.Tests.Core.ConflictStateTests;

namespace GerGit.Tests.Ui;

/// <summary>conflict banner, abort, continue, conflict card, Resolve Using, Reset to Unmerged, stage gate.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class ConflictUiTests
{
    /// <summary>A main window with a tab on <paramref name="repo"/>; dialogs, errors, beeps and the resolver are recorded.</summary>
    sealed class Fx : IDisposable
    {
        public readonly TempRepo Repo;
        public MainWindow Window = null!;
        public MainWindowViewModel Main = null!;
        public RepoTabViewModel Tab = null!;
        public readonly List<string> Errors = [];
        public readonly List<(string Title, string Text, string Button, double Width)> Confirms = [];
        public readonly List<string> Resolver = [];
        public int Beeps;
        public bool Answer = true;

        Fx(TempRepo repo) => Repo = repo;

        public static async Task<Fx> OpenAsync(TempRepo repo)
        {
            var f = new Fx(repo);
            f.Main = new MainWindowViewModel();
            f.Window = new MainWindow { DataContext = f.Main };
            f.Window.Show();
            f.Window.StatusTimer.Stop();
            Assert.Null(await f.Main.OpenAsync(repo.Path));
            f.Tab = f.Main.SelectedTab!;
            f.Tab.IsActive = false; // no watcher refreshes in between; the tab view stays shown
            f.Settle();
            f.Window.GetVisualDescendants().OfType<RepoTabView>().Single().IsVisible = true;
            f.Tab.ShowError = (_, text) => { f.Errors.Add(text); return Task.CompletedTask; };
            f.Tab.ShowGitError = (r, _) => { f.Errors.Add(r.ReadableOutput); return Task.CompletedTask; };
            f.Tab.Confirm = (title, text, button, width) => { f.Confirms.Add((title, text, button, width)); return Task.FromResult(f.Answer); };
            f.Tab.ShowResolver = path => { f.Resolver.Add(path); return Task.CompletedTask; };
            f.Vm.Beep = () => f.Beeps++;
            await f.RefreshAsync();
            return f;
        }

        public LocalChangesViewModel Vm => Tab.LocalChanges;

        public async Task RefreshAsync()
        {
            await Tab.RefreshAsync();
            while (!Tab.Refreshing.IsCompleted) await Tab.Refreshing;
            await Vm.CardLoading;
            Settle();
        }

        public void Settle()
        {
            Dispatcher.UIThread.RunJobs();
            Window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }

        public T Find<T>(string name) where T : Control =>
            Window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

        /// <summary>Selects the Unstaged (or Staged) row of that path, as a click does.</summary>
        public async Task SelectAsync(string path, bool staged = false)
        {
            var (sel, rows) = staged ? (Vm.StagedSelection, Vm.StagedRows) : (Vm.UnstagedSelection, Vm.UnstagedRows);
            sel.Clear();
            sel.Select(rows.Select((r, i) => (r, i)).Single(x => x.r.Path == path).i);
            await Vm.CardLoading;
            Settle();
        }

        public string Git(params string[] args) => Repo.Git(args);
        public string Work(string path) => File.ReadAllText(Path.Combine(Repo.Path, path));

        public void Dispose()
        {
            Dispatcher.UIThread.RunJobs();
            Main?.CloseTab(Tab);
            Window?.Close();
            Repo.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task Merge_conflict_banner_resolve_and_abort()
    {
        using var f = await Fx.OpenAsync(Diverged());
        var head = f.Git("rev-parse", "HEAD");
        GitFails(f.Repo, "merge", "side");
        await f.RefreshAsync();

        Assert.Equal(RepoStateKind.Merge, f.Tab.State.Kind);
        Assert.Equal("Merging branch 'side' into 'main'. Fix 1 conflict and then continue.", f.Tab.Banner!.Text);
        Assert.Equal(["side", "main"], f.Tab.Banner.Left.Where(p => p.IsLink).Select(p => p.Text));
        Assert.True(f.Window.GetVisualDescendants().OfType<ConflictBanner>().Single().IsEffectivelyVisible);
        var resolve = f.Find<Button>("BannerResolveButton");
        Assert.True(resolve.IsEffectivelyVisible); // the commit list is shown
        Assert.True(f.Find<Button>("BannerAbortButton").IsEffectivelyVisible);

        // A link selects its commit in the commit list.
        f.Tab.OpenCommitCommand.Execute(f.Tab.Banner.Left[1].Sha);
        f.Settle();
        Assert.Equal(f.Git("rev-parse", "side"), f.Tab.CommitList.SelectedSha);

        resolve.Command!.Execute(null);
        f.Settle();
        Assert.True(f.Tab.IsLocalChangesView);
        Assert.False(resolve.IsEffectivelyVisible);
        // Merge state: the normal button text, disabled while an unmerged file is left; the prepared message is loaded.
        Assert.Equal("Commit", f.Vm.CommitText);
        Assert.False(f.Vm.CanCommit);
        Assert.StartsWith("Merge branch 'side'", f.Vm.Subject);
        Assert.Equal(138, f.Vm.DescriptionMaxHeight);

        f.Answer = false;
        await f.Tab.AbortCommand.ExecuteAsync(null);
        Assert.Equal(RepoStateKind.Merge, f.Tab.State.Kind); // cancelled
        f.Answer = true;
        bool lockedDuringAbort = false;
        f.Vm.PropertyChanged += (_, e) => lockedDuringAbort |= e.PropertyName == nameof(f.Vm.Busy) && f.Vm.Busy && !f.Vm.CanCommit;
        await f.Tab.AbortCommand.ExecuteAsync(null);
        Assert.True(lockedDuringAbort);
        Assert.False(f.Vm.Busy);
        f.Settle();
        Assert.Equal(("Do you want to abort the merge?", "All uncommitted changes will be lost.", "Abort merge", 600.0), f.Confirms[^1]);
        Assert.Equal(head, f.Git("rev-parse", "HEAD"));
        Assert.Equal("a\nO\nc\n", f.Work("f.txt"));
        Assert.Equal(RepoStateKind.None, f.Tab.State.Kind);
        Assert.False(f.Tab.HasBanner);
        Assert.False(f.Window.GetVisualDescendants().OfType<ConflictBanner>().Single().IsVisible);
        Assert.Equal("", f.Vm.Subject); // the draft (merge message) is cleared
        Assert.Empty(f.Errors);
    }

    [AvaloniaFact]
    public async Task Rebase_conflict_resolve_using_and_continue_rebase()
    {
        var repo = Diverged();
        repo.Git("checkout", "-q", "side");
        var sideSubject = "side change";
        GitFails(repo, "rebase", "main");
        var active = repo.Git("rev-parse", "side");
        using var f = await Fx.OpenAsync(repo);
        f.Tab.ShowLocalChanges();
        f.Settle();

        Assert.Equal(RepoStateKind.Rebase, f.Tab.State.Kind);
        var banner = f.Tab.Banner!;
        Assert.Equal("Rebasing 'side' → 'main' (rebased 0/1 commits)", string.Concat(banner.Left.Select(p => p.Text)));
        Assert.Equal($"Fix 1 conflict with '{active[..7]}' and then continue.", string.Concat(banner.Right.Select(p => p.Text)));
        Assert.True(banner.HasProgress);
        Assert.Equal((1, 1), (banner.Value, banner.Maximum));
        Assert.Equal(13, banner.FontSize);
        Assert.True(f.Find<ProgressBar>("BannerProgress").IsEffectivelyVisible);
        Assert.False(f.Find<Button>("BannerResolveButton").IsEffectivelyVisible); // Local Changes is shown

        Assert.Equal("Continue Rebase", f.Vm.CommitText);
        Assert.False(f.Vm.CanCommit);
        Assert.False(f.Vm.MessageEnabled); // a conflict stop: git owns the message
        Assert.False(f.Find<TextBox>("SubjectBox").IsEffectivelyEnabled);

        await f.SelectAsync("f.txt");
        Assert.True(f.Vm.UnmergedSelected);
        Assert.Equal("Resolve Using 'main'", f.Vm.ResolveOursText);
        Assert.Equal("Resolve Using 'side'", f.Vm.ResolveTheirsText);
        await f.Vm.ResolveTheirsCommand.ExecuteAsync(null);
        Assert.Equal("a\nT\nc\n", f.Work("f.txt"));
        Assert.Equal("Continue Rebase", f.Vm.CommitText);
        Assert.True(f.Vm.CanCommit);

        await f.Vm.CommitCommand.ExecuteAsync(null);
        f.Settle();
        Assert.Empty(f.Errors);
        Assert.Equal(RepoStateKind.None, f.Tab.State.Kind);
        Assert.Equal("side", f.Git("rev-parse", "--abbrev-ref", "HEAD"));
        Assert.Equal(f.Git("rev-parse", "main"), f.Git("rev-parse", "HEAD~1"));
        Assert.Equal(sideSubject, f.Git("log", "-1", "--format=%s"));
        Assert.Equal("a\nT\nc\n", f.Work("f.txt"));
        Assert.Equal("Commit", f.Vm.CommitText);
        Assert.Equal(235, f.Vm.DescriptionMaxHeight);
    }

    [AvaloniaFact]
    public async Task Rebase_edit_stop_checks_amend_and_continue_does_what_it_says()
    {
        var repo = new TempRepo();
        repo.Commit("base", 0, ("f.txt", "a\n"));
        repo.Commit("second", 10, ("f.txt", "b\n"));
        repo.GitEnv(new() { ["GIT_SEQUENCE_EDITOR"] = "sed -i -e 's/^pick/edit/'" }, "rebase", "-i", "HEAD~1");
        using var f = await Fx.OpenAsync(repo);
        f.Tab.ShowLocalChanges();
        await f.Vm.Loading;
        f.Settle();

        Assert.NotNull(f.Tab.State.AmendSha);
        Assert.EndsWith($"Amending '{f.Tab.State.AmendSha![..7]}'.", f.Tab.Banner!.Text);
        Assert.True(f.Vm.Amend); // checked when the edit stop is first seen
        Assert.True(f.Vm.MessageEnabled);
        Assert.Equal("Amend Last Commit", f.Vm.CommitText);
        Assert.Equal("second", f.Vm.Subject); // HEAD's message

        f.Vm.Amend = false;
        await f.Vm.Loading;
        Assert.Equal("Continue Rebase", f.Vm.CommitText); // amend pending, Amend off, clean working directory
        await f.RefreshAsync();
        Assert.False(f.Vm.Amend); // not checked again for the same stop

        // A tracked change while an amend is pending: the normal commit text (the click commits).
        f.Repo.Write("f.txt", "c\n");
        await f.RefreshAsync();
        Assert.Equal("Commit", f.Vm.CommitText);
        f.Repo.Write("f.txt", "b\n");
        await f.RefreshAsync();

        await f.Vm.CommitCommand.ExecuteAsync(null);
        Assert.Empty(f.Errors);
        Assert.Equal(RepoStateKind.None, f.Tab.State.Kind);
        Assert.False(f.Vm.Amend);
        Assert.Equal("", f.Vm.Subject);
        Assert.Equal("second", f.Git("log", "-1", "--format=%s"));
    }

    [AvaloniaFact]
    public async Task Amend_ticked_before_a_conflicting_rebase_is_unticked()
    {
        var repo = Diverged();
        repo.Git("checkout", "-q", "side");
        using var f = await Fx.OpenAsync(repo);
        f.Tab.ShowLocalChanges();
        f.Vm.Subject = "my draft";
        f.Vm.Amend = true;
        await f.Vm.Loading;
        Assert.Equal("side change", f.Vm.Subject);

        GitFails(f.Repo, "rebase", "main");
        await f.RefreshAsync();
        Assert.Equal(RepoStateKind.Rebase, f.Tab.State.Kind);
        Assert.False(f.Vm.Amend); // locked state: it could not be unticked, and "Amend Last Commit" would rewrite the onto commit
        Assert.False(f.Vm.CanAmend);
        Assert.Equal("Continue Rebase", f.Vm.CommitText);
        Assert.Equal("side change", f.Vm.Subject); // git's prepared message, not HEAD's
    }

    [AvaloniaFact]
    public async Task Kind_change_with_amend_on_restores_the_prepared_message_on_untick()
    {
        var repo = Diverged();
        using var f = await Fx.OpenAsync(repo);
        f.Tab.ShowLocalChanges();
        f.Vm.Subject = "my draft";
        f.Vm.Amend = true;
        await f.Vm.Loading;
        GitFails(f.Repo, "merge", "side");
        await f.RefreshAsync();
        Assert.True(f.Vm.Amend); // merge state does not lock the message
        Assert.Equal("main change", f.Vm.Subject);
        f.Vm.Amend = false;
        await f.Vm.Loading;
        Assert.StartsWith("Merge branch 'side'", f.Vm.Subject); // the draft was replaced by the prepared message
    }

    [AvaloniaFact]
    public async Task Multi_commit_cherry_pick_commit_then_continue_cherry_pick()
    {
        var repo = Diverged();
        repo.Git("checkout", "-q", "side");
        repo.Commit("side 2", 30, ("g.txt", "g\n"));
        repo.Git("checkout", "-q", "main");
        var mainHead = repo.Git("rev-parse", "HEAD");
        GitFails(repo, "cherry-pick", "main..side");
        using var f = await Fx.OpenAsync(repo);
        f.Tab.ShowLocalChanges();
        Assert.Equal(RepoStateKind.CherryPick, f.Tab.State.Kind);

        await f.SelectAsync("f.txt");
        await f.Vm.ResolveTheirsCommand.ExecuteAsync(null);
        Assert.Equal("Commit 1 File", f.Vm.CommitText);
        Assert.Equal("side change", f.Vm.Subject);
        await f.Vm.CommitCommand.ExecuteAsync(null); // the text says commit: a normal commit, no continue
        f.Settle();
        Assert.Empty(f.Errors);
        Assert.Equal("side change", f.Git("log", "-1", "--format=%s"));
        Assert.Equal(RepoStateKind.CherryPickSequence, f.Tab.State.Kind);
        Assert.Equal("Cherry-pick in progress", f.Tab.Banner!.Text);
        Assert.Equal("Continue Cherry-Pick", f.Vm.CommitText);
        Assert.True(f.Vm.CanCommit);

        await f.Vm.CommitCommand.ExecuteAsync(null);
        f.Settle();
        Assert.Empty(f.Errors);
        Assert.False(Directory.Exists(Path.Combine(f.Repo.Path, ".git", "sequencer")));
        Assert.Equal(["side 2", "side change", "main change"], f.Git("log", "-3", "--format=%s").Split('\n'));
        Assert.Equal(mainHead, f.Git("rev-parse", "HEAD~2"));
        Assert.Equal("g\n", f.Work("g.txt"));
        Assert.Equal(RepoStateKind.None, f.Tab.State.Kind);
        Assert.Equal("Commit", f.Vm.CommitText);
    }

    [AvaloniaFact]
    public async Task Squash_merge_conflict_squash_button_commits()
    {
        var repo = Diverged();
        var mainHead = repo.Git("rev-parse", "HEAD");
        GitFails(repo, "merge", "--squash", "side");
        using var f = await Fx.OpenAsync(repo);
        f.Tab.ShowLocalChanges();
        Assert.Equal(RepoStateKind.Squash, f.Tab.State.Kind);
        Assert.Equal("Squash", f.Vm.CommitText);
        Assert.False(f.Vm.CanCommit);

        await f.SelectAsync("f.txt");
        await f.Vm.ResolveTheirsCommand.ExecuteAsync(null);
        Assert.Equal("Squashing commits", f.Tab.Banner!.Text);
        Assert.Equal("Squash", f.Vm.CommitText);
        Assert.StartsWith("Squashed commit", f.Vm.Subject);
        Assert.True(f.Vm.CanCommit);
        await f.Vm.CommitCommand.ExecuteAsync(null);
        f.Settle();
        Assert.Empty(f.Errors);
        Assert.Equal(mainHead, f.Git("rev-parse", "HEAD~1"));
        Assert.Equal("1", f.Git("rev-list", "--count", "--min-parents=1", "--max-parents=1", "HEAD~1..HEAD"));
        Assert.Equal("a\nT\nc\n", f.Work("f.txt"));
        Assert.Equal(RepoStateKind.None, f.Tab.State.Kind);
    }

    [AvaloniaFact]
    public async Task Cherry_pick_conflict_card_choose_theirs_then_commit()
    {
        var repo = Diverged();
        var picked = repo.Git("rev-parse", "side");
        var mainHead = repo.Git("rev-parse", "HEAD");
        GitFails(repo, "cherry-pick", "side");
        using var f = await Fx.OpenAsync(repo);
        f.Tab.ShowLocalChanges();
        await f.SelectAsync("f.txt");

        var card = f.Vm.Card!;
        Assert.True(f.Vm.CardUnresolved);
        Assert.False(f.Vm.DiffVisible);
        Assert.True(f.Find<ConflictCard>("ConflictCardPane").IsEffectivelyVisible);
        Assert.Equal(picked[..7], card.Theirs.Name);
        Assert.Equal("main", card.Ours.Name);
        Assert.Equal(("modified", "modified"), (card.Theirs.Word, card.Ours.Word));
        Assert.True(card.TheirsChecked && card.OursChecked);
        Assert.Equal("Merge", card.PrimaryText);
        await card.CommitsLoading;
        Assert.Equal(["side change"], card.Theirs.Commits.Select(c => c.Subject));
        Assert.Equal(["main change"], card.Ours.Commits.Select(c => c.Subject));
        Assert.Equal("Cherry-picking commit '" + picked[..7] + "'. Fix 1 conflict and then continue.", f.Tab.Banner!.Text);

        // Merge opens the resolver; a refresh keeps the card and its check boxes.
        await card.PrimaryCommand.ExecuteAsync(null);
        Assert.Equal(["f.txt"], f.Resolver);
        card.OursChecked = false;
        await f.RefreshAsync();
        Assert.Same(card, f.Vm.Card);
        Assert.Equal($"Choose {picked[..7]}", card.PrimaryText);

        await card.PrimaryCommand.ExecuteAsync(null);
        f.Settle();
        Assert.Empty(f.Errors);
        Assert.Equal("a\nT\nc\n", f.Work("f.txt"));
        Assert.Null(f.Vm.Card); // the file left the Unstaged list
        Assert.Equal("Cherry-picking commit '" + picked[..7] + "'. All conflicts fixed.", f.Tab.Banner!.Text);
        Assert.Equal("Commit 1 File", f.Vm.CommitText);
        Assert.Equal("side change", f.Vm.Subject); // prepared message
        Assert.True(f.Vm.CanCommit);

        await f.Vm.CommitCommand.ExecuteAsync(null);
        f.Settle();
        Assert.Empty(f.Errors);
        Assert.False(File.Exists(Path.Combine(f.Repo.Path, ".git", "CHERRY_PICK_HEAD")));
        Assert.Equal("side change", f.Git("log", "-1", "--format=%s"));
        Assert.Equal(mainHead, f.Git("rev-parse", "HEAD~1"));
        Assert.Equal(RepoStateKind.None, f.Tab.State.Kind);
        Assert.False(f.Tab.HasBanner);
    }

    [AvaloniaFact]
    public async Task Modify_delete_conflict_delete_file()
    {
        var repo = new TempRepo();
        repo.Commit("base", 0, ("f.txt", "a\nb\nc\n"), ("g.txt", "g\n"));
        repo.Git("checkout", "-q", "-b", "side");
        repo.Git("rm", "-q", "f.txt");
        repo.Commit("delete f", 10);
        repo.Git("checkout", "-q", "main");
        repo.Commit("modify f", 20, ("f.txt", "a\nO\nc\n"));
        GitFails(repo, "merge", "side");
        using var f = await Fx.OpenAsync(repo);
        f.Tab.ShowLocalChanges();
        await f.SelectAsync("f.txt");

        var card = f.Vm.Card!;
        Assert.Equal(("deleted", "modified"), (card.Theirs.Word, card.Ours.Word));
        Assert.False(card.TheirsChecked || card.OursChecked); // merge not possible: both start unchecked
        Assert.Equal("Merge", card.PrimaryText);
        Assert.False(card.PrimaryEnabled);
        card.TheirsChecked = card.OursChecked = true;
        Assert.Equal("Select version to resolve with", card.PrimaryText);
        Assert.False(card.PrimaryEnabled);
        card.OursChecked = false;
        Assert.Equal("Delete file", card.PrimaryText);
        Assert.True(card.PrimaryEnabled);
        f.Settle();
        Assert.Equal("Delete file", f.Find<Button>("PrimaryButton").Content);

        await card.PrimaryCommand.ExecuteAsync(null);
        Assert.Empty(f.Errors);
        Assert.False(File.Exists(Path.Combine(f.Repo.Path, "f.txt")));
        Assert.Equal("D  f.txt", f.Git("status", "--porcelain"));
        Assert.Equal("Merging branch 'side' into 'main'. All conflicts fixed.", f.Tab.Banner!.Text);
        Assert.True(f.Vm.CanCommit);
    }

    [AvaloniaFact]
    public async Task Stage_gate_beeps_until_the_markers_are_gone()
    {
        var repo = Diverged();
        GitFails(repo, "merge", "side");
        using var f = await Fx.OpenAsync(repo);
        f.Tab.ShowLocalChanges();
        await f.SelectAsync("f.txt");
        Assert.True(f.Vm.CardUnresolved);

        await f.Vm.StageCommand.ExecuteAsync(null);
        Assert.Equal(1, f.Beeps);
        Assert.Equal("UU f.txt", f.Git("status", "--porcelain"));
        await f.Vm.StageAllCommand.ExecuteAsync(null);
        Assert.Equal(2, f.Beeps);

        f.Repo.Write("f.txt", "a\nmerged\nc\n");
        await f.RefreshAsync();
        Assert.True(f.Vm.CardResolved);
        Assert.True(f.Find<Border>("ResolvedStrip").IsEffectivelyVisible);
        Assert.False(f.Find<ConflictCard>("ConflictCardPane").IsEffectivelyVisible);
        // Below the strip: the working file against the ours version.
        await f.Vm.Diff.Loading;
        f.Settle();
        Assert.Equal([("O", DiffLineKind.Removed), ("merged", DiffLineKind.Added)],
            f.Vm.Diff.FileDiff!.Hunks.Single().Lines.Where(l => l.Kind != DiffLineKind.Context).Select(l => (l.Text, l.Kind)));
        Assert.True(f.Find<DiffView>("DiffPane").IsEffectivelyVisible);

        Assert.Same(f.Vm.StageCommand, f.Find<Button>("CardStageButton").Command);
        await f.Vm.StageCommand.ExecuteAsync(null);
        Assert.Equal(2, f.Beeps);
        Assert.Equal("M  f.txt", f.Git("status", "--porcelain"));
        Assert.Empty(f.Errors);
    }

    [AvaloniaFact]
    public async Task Reset_file_to_unmerged_state_brings_the_conflict_back()
    {
        var repo = Diverged();
        GitFails(repo, "merge", "side");
        using var f = await Fx.OpenAsync(repo);
        f.Tab.ShowLocalChanges();
        await f.SelectAsync("f.txt");
        await f.Vm.ResolveTheirsCommand.ExecuteAsync(null); // ours would equal HEAD: nothing left to stage
        Assert.Equal("M  f.txt", f.Git("status", "--porcelain"));

        await f.SelectAsync("f.txt", staged: true);
        Assert.True(f.Vm.ResetToUnmergedVisible);
        Assert.True(f.Vm.ResetToUnmergedEnabled);
        await f.Vm.ResetToUnmergedCommand.ExecuteAsync(null);
        Assert.Equal(("Do you want reset file to unmerged state?",
            "This will reset conflict resolution and return the file to the unmerged state.", "Unmerge", 500.0), f.Confirms.Single());
        Assert.Empty(f.Errors);
        Assert.Equal("UU f.txt", f.Git("status", "--porcelain"));
        Assert.Contains("<<<<<<<", f.Work("f.txt"));
        Assert.Equal("Merging branch 'side' into 'main'. Fix 1 conflict and then continue.", f.Tab.Banner!.Text);
    }

    [AvaloniaFact]
    public async Task Renders_banner_and_card()
    {
        var repo = Diverged();
        GitFails(repo, "merge", "side");
        using var f = await Fx.OpenAsync(repo);
        f.Tab.ShowLocalChanges();
        await f.SelectAsync("f.txt");
        await f.Vm.Card!.CommitsLoading;
        f.Settle();
        using (var frame = f.Window.CaptureRenderedFrame()) Assert.NotNull(frame);

        var banner = f.Window.GetVisualDescendants().OfType<ConflictBanner>().Single();
        Assert.Equal(28, banner.Bounds.Height);
        var theirs = f.Find<ToggleButton>("TheirsCard");
        var ours = f.Find<ToggleButton>("OursCard");
        Assert.True(theirs.Bounds.Width >= 150 && ours.Bounds.Width >= 150);
        Assert.True(theirs.TranslatePoint(default, f.Window)!.Value.X < ours.TranslatePoint(default, f.Window)!.Value.X);
        Assert.Equal("Merge", f.Find<Button>("PrimaryButton").Content);
        Assert.Equal("f.txt", f.Find<TextBlock>("CardPath").Text);

        // Clicking a card toggles it.
        var at = theirs.TranslatePoint(new Point(theirs.Bounds.Width / 2, 4), f.Window)!.Value;
        f.Window.MouseDown(at, Avalonia.Input.MouseButton.Left);
        f.Window.MouseUp(at, Avalonia.Input.MouseButton.Left);
        f.Settle();
        Assert.False(f.Vm.Card.TheirsChecked);
        Assert.Equal("Choose main", f.Find<Button>("PrimaryButton").Content);
    }

    [Fact]
    public void Banner_texts()
    {
        UnmergedFile U(string p) => new(p, 'U', 'U', false);
        Assert.Equal("Squashing commits. Fix 2 conflicts and continue.",
            BannerContent.For(new RepoState(RepoStateKind.Squash, [U("a"), U("b")]))!.Text);
        Assert.Equal("Squashing commits", BannerContent.For(new RepoState(RepoStateKind.Squash, []))!.Text);
        Assert.Equal("Working directory contains 1 unmerged file.", BannerContent.For(new RepoState(RepoStateKind.Unmerged, [U("a")]))!.Text);
        Assert.Equal("Working directory contains 2 unmerged files.", BannerContent.For(new RepoState(RepoStateKind.Unmerged, [U("a"), U("b")]))!.Text);
        Assert.Equal("Cherry-pick in progress", BannerContent.For(new RepoState(RepoStateKind.CherryPickSequence, []))!.Text);
        var am = BannerContent.For(new RepoState(RepoStateKind.ApplyPatches, [U("a")], Done: 1, Total: 3))!;
        Assert.Equal("Applying a series of patches (AM) in progress (applied 1/3 commits)", string.Concat(am.Left.Select(p => p.Text)));
        Assert.Equal("Fix 1 conflict and then continue.", string.Concat(am.Right.Select(p => p.Text)));
        Assert.Equal((2, 3, 13.0), (am.Value, am.Maximum, am.FontSize));
        var sha = new string('a', 40);
        var amend = BannerContent.For(new RepoState(RepoStateKind.Rebase, [], "main", "topic", sha, sha, 2, 4, sha, new string('b', 40)))!;
        Assert.Equal("Amending 'bbbbbbb'.", string.Concat(amend.Right.Select(p => p.Text)));
        Assert.Equal("Reverting commit 'aaaaaaa'. All conflicts fixed.", BannerContent.For(new RepoState(RepoStateKind.Revert, [], TheirsSha: sha))!.Text);
        Assert.Null(BannerContent.For(RepoState.Clean));
        Assert.Null(BannerContent.For(new RepoState(RepoStateKind.Bisect, [])));
    }
}
