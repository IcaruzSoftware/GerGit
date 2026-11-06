using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GerGit.Controls;
using GerGit.Core;
using GerGit.ViewModels;
using GerGit.ViewModels.Dialogs;
using GerGit.Views;
using GerGit.Views.Dialogs;

namespace GerGit.Tests.Ui;

/// <summary>dialog frame, reference-name box, Git Error window, jobs in the status box, Activity Manager,
/// credentials and host-key dialogs, the RunJob helper and the settings store.</summary>
public class AppInfraTests
{
    static void Settle(params Window[] windows)
    {
        Dispatcher.UIThread.RunJobs();
        foreach (var w in windows) w.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    static Window ShowOwner()
    {
        var owner = new Window { Width = 800, Height = 600 };
        owner.Show();
        return owner;
    }

    static DialogWindow Dialog(Window owner)
    {
        Settle(owner);
        var d = (DialogWindow)owner.OwnedWindows.Single();
        Settle(d);
        return d;
    }

    static void Press(Window w, Key key, PhysicalKey physical)
    {
        w.KeyPress(key, RawInputModifiers.None, physical, null);
        Dispatcher.UIThread.RunJobs(); // the key may close the window
    }

    static void Click(Button b)
    {
        if (b.Command is { } c) c.Execute(b.CommandParameter);
        else b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    static T Find<T>(Window w, string name) where T : Control => w.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    // ---------------------------------------------------------------- dialog frame

    [AvaloniaFact]
    public async Task Dialog_frame_enter_submits_only_when_enabled_and_esc_cancels()
    {
        var owner = ShowOwner();
        var vm = new DialogViewModel { Title = "Delete Branch", Description = "Really?", SubmitText = "Delete", CanSubmit = false };
        var shown = DialogWindow.ShowAsync(vm, owner);
        var d = Dialog(owner);
        Assert.Equal("Delete Branch", d.Title);
        Assert.Equal("Really?", Find<TextBlock>(d, "DialogDescription").Text);
        Assert.Equal(490, d.Width);

        Press(d, Key.Enter, PhysicalKey.Enter);
        Assert.Null(vm.Result); // Submit disabled: Enter does nothing
        Assert.False(Find<Button>(d, "SubmitButton").IsEffectivelyEnabled);

        vm.CanSubmit = true;
        Settle(d);
        Press(d, Key.Enter, PhysicalKey.Enter);
        Assert.True(await shown);
        Assert.True(vm.Result);

        var vm2 = new DialogViewModel { Title = "Second" };
        var shown2 = DialogWindow.ShowAsync(vm2, owner);
        var d2 = Dialog(owner);
        Press(d2, Key.Escape, PhysicalKey.Escape);
        Assert.False(await shown2);
        Assert.False(vm2.Result);
        owner.Close();
    }

    [AvaloniaFact]
    public async Task Dialog_frame_busy_disables_inputs_and_shows_the_footer_status()
    {
        var owner = ShowOwner();
        var vm = new CredentialsViewModel(new CredentialPrompt("", PromptKind.UserName, "Username for 'https://h': ", null));
        var shown = DialogWindow.ShowAsync(vm, owner);
        var d = Dialog(owner);
        var input = Find<TextBox>(d, "Input");
        Assert.True(input.IsEffectivelyEnabled);
        Assert.False(Find<BusySpinner>(d, "FooterSpinner").IsVisible);

        vm.IsBusy = true;
        vm.SetStatus(DialogStatus.InProgress, "Creating branch...");
        Settle(d);
        Assert.False(input.IsEffectivelyEnabled);
        Assert.False(Find<Button>(d, "SubmitButton").IsEffectivelyEnabled);
        Assert.True(Find<BusySpinner>(d, "FooterSpinner").IsVisible);
        Assert.Equal("Creating branch...", Find<TextBlock>(d, "FooterText").Text);
        // While busy, Esc and the title-bar close cancel the running job instead of closing (the job end closes).
        var job = new Job("Create branch");
        vm.RunningJob = job;
        Assert.True(Find<Button>(d, "CancelButton").IsEffectivelyEnabled);
        Press(d, Key.Escape, PhysicalKey.Escape);
        Assert.True(job.CancelRequested);
        var job2 = new Job("Create branch");
        vm.RunningJob = job2;
        d.Close();
        Settle(d);
        Assert.True(job2.CancelRequested);
        Assert.Null(vm.Result);
        Assert.True(d.IsVisible);
        vm.RunningJob = null;

        vm.IsBusy = false;
        vm.SetValid(false, "Name cannot end with '.'");
        Settle(d);
        Assert.False(Find<BusySpinner>(d, "FooterSpinner").IsVisible);
        var icon = Find<LucideIcon>(d, "FooterIcon");
        Assert.True(icon.IsVisible);
        Assert.Equal("triangle-alert", icon.Icon);
        Assert.Equal("Name cannot end with '.'", Find<TextBlock>(d, "FooterText").Text);
        Assert.False(Find<Button>(d, "SubmitButton").IsEffectivelyEnabled);

        vm.Cancel();
        Assert.False(await shown);
        owner.Close();
    }

    [AvaloniaFact]
    public async Task Dialog_selects_the_prefilled_text_of_the_first_box_so_typing_replaces_it()
    {
        var owner = ShowOwner();
        var vm = new CredentialsViewModel(new CredentialPrompt("", PromptKind.UserName, "Username for 'https://h': ", null)) { Text = "main" };
        var shown = DialogWindow.ShowAsync(vm, owner);
        var d = Dialog(owner);
        var input = Find<TextBox>(d, "Input");
        Assert.Equal((true, 0, 4), (input.IsFocused, input.SelectionStart, input.SelectionEnd));
        d.KeyTextInput("dev");
        Assert.Equal("dev", vm.Text);
        vm.Cancel();
        await shown;

        var keep = new CredentialsViewModel(new CredentialPrompt("", PromptKind.UserName, "u", null)) { Text = "main", SelectTextOnOpen = false };
        shown = DialogWindow.ShowAsync(keep, owner);
        d = Dialog(owner);
        input = Find<TextBox>(d, "Input");
        Assert.True(input.IsFocused);
        Assert.Equal(input.SelectionStart, input.SelectionEnd); // opted out: nothing selected
        keep.Cancel();
        await shown;
        owner.Close();
    }

    [AvaloniaFact]
    public void Dialog_without_owner_returns_false_at_once()
    {
        var shown = DialogWindow.ShowAsync(new DialogViewModel { Title = "x" });
        Assert.True(shown.IsCompleted);
        Assert.False(shown.Result);
    }

    // ---------------------------------------------------------------- reference-name box

    [AvaloniaFact]
    public async Task RefNameBox_types_the_replacement_for_space_and_cleans_pastes()
    {
        var box = new RefNameBox { Replacement = "-" };
        var w = new Window { Content = box };
        w.Show();
        box.Focus();
        w.KeyTextInput("my branch");
        Assert.Equal("my-branch", box.Text);
        Assert.Equal("git-branch", box.Icon);

        box.Text = "";
        box.InsertPasted("fix: a..b");
        Assert.Equal("fix--a-b", box.Text);
        box.InsertPasted(null); // non-text clipboard: nothing happens
        Assert.Equal("fix--a-b", box.Text);
        box.SelectAll();
        box.InsertPasted("@");
        Assert.Equal("-", box.Text);

        // Through the real paste path (Ctrl+V → clipboard).
        var clipboard = w.Clipboard;
        Assert.NotNull(clipboard);
        box.Text = "";
        await clipboard.SetTextAsync("x y");
        box.Paste();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (box.Text != "x-y" && DateTime.UtcNow < deadline) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
        Assert.Equal("x-y", box.Text);
        w.Close();
    }

    [AvaloniaFact]
    public void Settings_store_round_trips_through_the_file()
    {
        Assert.StartsWith(Path.GetTempPath(), Settings.FilePath); // never the user's real file
        var key = Settings.RepoKey("/tmp/some repo", "test.value." + Guid.NewGuid().ToString("N"));
        Assert.False(Settings.Get(key, false));
        Settings.Set(key, true);
        Settings.Set(key + ".list", new[] { "a", "b" });
        Settings.Reload();
        Assert.True(Settings.Get(key, false));
        Assert.Equal(["a", "b"], Settings.Get<string[]>(key + ".list", []));
        Assert.Equal("fallback", Settings.Get(key, "fallback")); // wrong type → default
        Settings.Set<string?>(key, null);
        Assert.False(Settings.Get(key, false));
        Assert.Equal("-", Settings.Get(Settings.SpaceCharKey, "-"));
    }

    // ---------------------------------------------------------------- Git Error window

    [AvaloniaFact]
    public async Task Error_window_shows_the_text_and_the_index_lock_action()
    {
        var gitDir = Path.Combine(Path.GetTempPath(), "gergit-test-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(gitDir);
        File.WriteAllText(Path.Combine(gitDir, "index.lock"), "");
        try
        {
            var owner = ShowOwner();
            var text = $"fatal: Unable to create '{gitDir}/index.lock': File exists.\n\nhint: remove the file";
            var vm = GitErrorViewModel.Create(GitErrors.Classify(text), gitDir, "/repo", null);
            var shown = DialogWindow.ShowAsync(vm, owner);
            var d = Dialog(owner);
            Assert.Equal("Git Error", d.Title);
            Assert.Equal("An unexpected error occurred while performing the git request", Find<TextBlock>(d, "DialogDescription").Text);
            Assert.True(Find<LucideIcon>(d, "Badge").IsVisible);
            Assert.False(Find<Button>(d, "SubmitButton").IsVisible);
            Assert.Equal("Close", Find<Button>(d, "CancelButton").Content);
            Assert.True(d.CanResize);
            Assert.Equal((740, 330, 420, 220), (d.Width, d.Height, d.MinWidth, d.MinHeight));
            var output = Find<OutputView>(d, "Output");
            Assert.Equal(text, output.Text);
            var runs = output.TextBlock.Inlines!.OfType<Avalonia.Controls.Documents.Run>().ToList();
            Assert.Equal(Avalonia.Media.FontWeight.Bold, runs[0].FontWeight); // "fatal: " line: bold error colour
            var action = Find<Button>(d, "ActionButton");
            Assert.True(action.IsVisible);
            Assert.Equal("Remove .git/index.lock", action.Content);

            Click(action);
            Assert.True(await shown);
            await vm.ChosenAction!();
            Assert.False(File.Exists(Path.Combine(gitDir, "index.lock")));
            owner.Close();
        }
        finally { Directory.Delete(gitDir, true); }
    }

    [AvaloniaFact]
    public async Task Merge_conflict_variant_and_plain_errors_without_action()
    {
        var owner = ShowOwner();
        var vm = GitErrorViewModel.Create(GitErrors.Classify(new GitResult(1, "CONFLICT (content): Merge conflict in a.txt\n", "")), "/g", "/r", null);
        var shown = DialogWindow.ShowAsync(vm, owner);
        var d = Dialog(owner);
        Assert.Equal("Merge Conflict", d.Title);
        Assert.Equal("Automatic merge failed. Fix conflicts and then commit the result", Find<TextBlock>(d, "DialogDescription").Text);
        Assert.False(Find<LucideIcon>(d, "Badge").IsVisible);
        Assert.False(Find<Button>(d, "ActionButton").IsVisible);
        Press(d, Key.Escape, PhysicalKey.Escape);
        Assert.False(await shown);

        // Caller-owned actions appear only for their error kind.
        bool forced = false;
        var actions = new GitErrorActions(ForceFetch: () => { forced = true; return Task.CompletedTask; });
        var clobber = GitErrorViewModel.Create(GitErrors.Classify(new GitResult(1, "", " ! [rejected] v1 -> v1  (would clobber existing tag)")), null, null, null, actions);
        Assert.Equal("Force Fetch", clobber.ActionText);
        Assert.Null(GitErrorViewModel.Create(GitErrors.Classify("fatal: other"), null, null, null, actions).ActionText);
        clobber.RunActionCommand.Execute(null);
        await clobber.ChosenAction!();
        Assert.True(forced);
        var unsafeRepo = GitErrorViewModel.Create(GitErrors.Classify("fatal: detected dubious ownership in repository at '/x'"), null, "/r", null);
        Assert.Equal("Mark repository as safe", unsafeRepo.ActionText);
        owner.Close();
    }

    [AvaloniaFact]
    public async Task Mark_repository_as_safe_asks_first_and_names_the_owner()
    {
        var dir = Directory.CreateTempSubdirectory("gergit-safe-").FullName;
        try
        {
            var asked = new List<(string Title, string Text, string Button)>();
            bool marked = false;
            var vm = GitErrorViewModel.Create(GitErrors.Classify($"fatal: detected dubious ownership in repository at '{dir}'"), null, "/r",
                () => { marked = true; return Task.CompletedTask; },
                confirm: (title, text, button, _) => { asked.Add((title, text, button)); return Task.FromResult(false); });
            vm.RunActionCommand.Execute(null);
            await vm.ChosenAction!();
            var q = Assert.Single(asked);
            Assert.Equal(("Mark repository as safe?", "Mark as Safe"), (q.Title, q.Button));
            uint myUid = RepositoryScan.CurrentUid();
            Assert.Equal(myUid, RepositoryScan.OwnerUid(dir)); // a folder of our own (and no .git): "you"
            Assert.Equal($"'{dir}' is owned by you.\n\nGit will then run this repository's hooks and settings as you. " +
                         "Only do this if you trust its content.", q.Text);
            Assert.False(marked); // declined: no safe.directory entry, no reopening

            // The foreign one of folder and .git is named, also when only .git belongs to someone else; the folder first.
            uint other = myUid + 4242, third = myUid + 4343;
            var name = RepositoryScan.UserName(other);
            Assert.Equal($"'/r' is owned by {name}.\n\nGit will then run this repository's hooks and settings as you. " +
                         $"Only do this if you trust {name}.",
                GitErrorViewModel.SafeConfirmation("/r", p => p == "/r/.git" ? other : myUid, myUid).Text);
            Assert.StartsWith($"'/r' is owned by {name}.", GitErrorViewModel.SafeConfirmation("/r", p => p == "/r" ? other : null, myUid).Text);
            Assert.StartsWith($"'/r' is owned by {name}.", GitErrorViewModel.SafeConfirmation("/r", p => p == "/r" ? other : third, myUid).Text);
            Assert.StartsWith("'/r' is owned by you.", GitErrorViewModel.SafeConfirmation("/r", _ => myUid, myUid).Text);
            Assert.Equal($"uid {uint.MaxValue - 1}", RepositoryScan.UserName(uint.MaxValue - 1)); // no such user

            // A worktree or submodule: .git is a file, and the git dir it points to belongs to someone else.
            var gitDir = Path.Combine(dir, "elsewhere");
            File.WriteAllText(Path.Combine(dir, ".git"), "gitdir: elsewhere\n");
            Assert.StartsWith($"'{dir}' is owned by {name}.",
                GitErrorViewModel.SafeConfirmation(dir, p => p == gitDir ? other : myUid, myUid).Text);
            File.Delete(Path.Combine(dir, ".git"));

            Assert.NotNull(RepositoryScan.OwnerUid("/"));
            Assert.Null(RepositoryScan.OwnerUid(Path.Combine(dir, "gone")));
            Assert.StartsWith("The owner of '/gone/x' is unknown.\n\n", GitErrorViewModel.SafeConfirmation("/gone/x").Text);
            Assert.EndsWith("Only do this if you trust its owner.", GitErrorViewModel.SafeConfirmation("/gone/x").Text);
        }
        finally { Directory.Delete(dir); }
    }

    [Fact]
    public async Task Mark_repository_as_safe_is_not_offered_for_a_path_git_reads_as_a_pattern()
    {
        foreach (var path in new[] { "*", "/srv/share/*", "%(prefix)/srv/repo" })
        {
            var text = $"fatal: detected dubious ownership in repository at '{path}'\n";
            var vm = GitErrorViewModel.Create(GitErrors.Classify(text), null, "/r", null,
                confirm: (_, _, _, _) => throw new InvalidOperationException("must not ask"));
            Assert.Null(vm.ActionText);
            Assert.EndsWith("\n\n" + GitErrorViewModel.WiderThanRepository, vm.Text);
            Assert.False(await GitErrorViewModel.MarkSafeAsync(path)); // refused before git runs
        }
        Assert.Equal("Mark repository as safe",
            GitErrorViewModel.Create(GitErrors.Classify("fatal: detected dubious ownership in repository at '/srv/a*b'"), null, "/r", null).ActionText);
    }

    // ---------------------------------------------------------------- jobs: RunJob helper, status box, activity

    sealed class TabFixture : IDisposable
    {
        public readonly TempRepo Repo = new();
        public MainWindowViewModel Main = null!;
        public MainWindow Window = null!;
        public RepoTabViewModel Tab = null!;
        public readonly List<string> Events = [];

        public async Task OpenAsync()
        {
            Repo.Commit("first", 1, ("a.txt", "a"));
            Main = new MainWindowViewModel();
            Window = new MainWindow { DataContext = Main };
            Window.Show();
            Window.StatusTimer.Stop(); // the tests drive Tick with their own clock
            Assert.Null(await Main.OpenAsync(Repo.Path));
            Tab = Main.SelectedTab!;
            Tab.IsActive = false; // no watcher refreshes in between
            await Tab.Refreshing;
            Tab.ShowError = (title, text) => { Events.Add($"error {title}: {text}"); return Task.CompletedTask; };
            Tab.ShowGitError = (r, _) =>
            {
                Events.Add($"git-error {r.Stderr} refs={string.Join(",", Tab.Refs.Refs.Select(x => x.FullName))}");
                return Task.CompletedTask;
            };
            Settle(Window);
        }

        public void Dispose()
        {
            Window?.Close();
            Repo.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task RunJob_failure_shows_the_error_after_refresh_and_select()
    {
        using var f = new TabFixture();
        await f.OpenAsync();
        var job = await f.Tab.RunJobAsync("Create branch 'x'", async j =>
        {
            await GerGit.Core.Git.GitRunner.RunJobAsync(j, f.Repo.Path, ["branch", "x"]);
            return new GitResult(1, "", "fatal: boom");
        }, afterRefresh: () => { f.Events.Add("select"); return Task.CompletedTask; });

        Assert.Equal(JobOutcome.Failed, job.Outcome);
        Assert.Equal("fatal: boom", job.Message);
        Assert.Contains("$ git branch x", job.Output);
        // Order of Result handling: refresh (the new branch is visible), select, then the error window.
        Assert.Equal(["select", "git-error fatal: boom refs=refs/heads/main,refs/heads/x"], f.Events);
        Assert.Same(job, f.Tab.Jobs.History[0]);
        Assert.True(f.Tab.Jobs.IsIdle);
    }

    [AvaloniaFact]
    public async Task RunJob_cancel_success_exception_and_keep_open_dialog()
    {
        using var f = new TabFixture();
        await f.OpenAsync();

        var cancelled = await f.Tab.RunJobAsync("Fetch 'origin'", j =>
        {
            j.Cancel();
            return Task.FromResult<GitResult?>(new GitResult(130, "", "", Cancelled: true));
        });
        Assert.Equal(JobOutcome.Canceled, cancelled.Outcome);
        Assert.Empty(f.Events); // a cancelled job never shows an error

        var ok = await f.Tab.RunJobAsync("Stage", _ => Task.FromResult<GitResult?>(null));
        Assert.Equal(JobOutcome.Succeeded, ok.Outcome);

        var thrown = await f.Tab.RunJobAsync("Bad", _ => throw new InvalidOperationException("oops"));
        Assert.Equal((JobOutcome.Failed, "[Internal]: oops"), (thrown.Outcome, thrown.Message));
        Assert.Equal(["error Git Error: [Internal]: oops"], f.Events);
        f.Events.Clear();

        var hostError = new GitResult(128, "", "fatal: unable to access 'https://x.invalid/': Could not resolve host: x.invalid\n");
        var noHost = await f.Tab.RunJobAsync("Fetch", _ => Task.FromResult<GitResult?>(hostError), actions: new(QuietHostError: true));
        Assert.Equal((JobOutcome.Failed, "Could not resolve host 'x.invalid'"), (noHost.Outcome, noHost.Message));
        Assert.Empty(f.Events); // fetch / pull: only the job message
        var push = await f.Tab.RunJobAsync("Push", _ => Task.FromResult<GitResult?>(hostError));
        Assert.Equal("Could not resolve host 'x.invalid'", push.Message);
        Assert.Single(f.Events); // other operations still show the window
        f.Events.Clear();

        // A dialog that stays open: busy while the job runs, closed as success when it ends.
        var dialog = new DialogViewModel { Title = "Create Branch" };
        bool busyDuringWork = false;
        await f.Tab.RunJobAsync("Create", _ => { busyDuringWork = dialog.IsBusy; return Task.FromResult<GitResult?>(null); }, dialog);
        Assert.True(busyDuringWork);
        Assert.Equal((false, true), (dialog.IsBusy, dialog.Result));

        // Its Cancel cancels the job; the cancelled job closes the dialog as success without an error.
        var dialog2 = new DialogViewModel { Title = "Checkout" };
        var gate = new TaskCompletionSource<GitResult?>();
        var running = f.Tab.RunJobAsync("Checkout", j => { j.Token.Register(() => gate.TrySetResult(new GitResult(130, "", "", Cancelled: true))); return gate.Task; }, dialog2);
        Assert.Same(f.Tab.Jobs.Running.Single(j => !j.Hidden), dialog2.RunningJob);
        dialog2.Cancel();
        var checkout = await running;
        Assert.Equal((JobOutcome.Canceled, true, (Job?)null), (checkout.Outcome, dialog2.Result, dialog2.RunningJob));
        Assert.Empty(f.Events);
        Assert.Equal(["Checkout", "Create", "Push", "Fetch", "Bad", "Stage", "Fetch 'origin'"], f.Tab.Jobs.History.Select(j => j.Name));

        // Hidden jobs (refresh) do not change the history version (the Activity Manager rows stay as they are).
        int version = f.Tab.Jobs.Version;
        await f.Tab.RefreshAsync();
        Assert.Equal(version, f.Tab.Jobs.Version);
    }

    [AvaloniaFact]
    public async Task Default_error_hooks_route_git_errors_to_the_error_dialog()
    {
        using var f = new TabFixture();
        await f.OpenAsync();
        var tab = new RepoTabViewModel(f.Tab.Repo);
        try
        {
            var shown = new List<DialogViewModel>();
            tab.ShowDialog = vm => { shown.Add(vm); return Task.FromResult(false); };
            await tab.ShowError("Git Error", "fatal: Unable to create '/x/index.lock': File exists.");
            await tab.ShowGitError(new GitResult(1, "", "fatal: refusing to merge unrelated histories"), new(MergeUnrelated: () => Task.CompletedTask));
            var e1 = Assert.IsType<GitErrorViewModel>(shown[0]);
            Assert.Equal(("Git Error", "Remove .git/index.lock"), (e1.Title, e1.ActionText));
            var e2 = Assert.IsType<GitErrorViewModel>(shown[1]);
            Assert.Equal("Merge unrelated history", e2.ActionText);
        }
        finally { tab.Dispose(); }
    }

    [AvaloniaFact]
    public async Task Status_box_shows_the_running_job_its_progress_cancel_and_the_linger()
    {
        using var f = new TabFixture();
        await f.OpenAsync();
        var w = f.Window;
        var gate = new TaskCompletionSource<GitResult?>();
        Job? job = null;
        var run = f.Tab.RunJobAsync("Fetch 'origin'", j => { job = j; j.Report(40, "Receiving objects: 40%"); return gate.Task; });

        f.Main.Tick(DateTime.UtcNow);
        Settle(w);
        Assert.True(Find<TextBlock>(w, "JobTitle").IsVisible);
        Assert.False(Find<TextBlock>(w, "StatusTitle").IsVisible);
        Assert.Equal("Fetch 'origin'", Find<TextBlock>(w, "JobTitle").Text);
        Assert.Equal("Receiving objects: 40%", Find<TextBlock>(w, "JobDescription").Text);
        var bar = Find<ProgressBar>(w, "JobProgress");
        Assert.True(bar.IsVisible);
        Assert.Equal(5 + 40 * 0.95, bar.Value, 3);
        Assert.True(Find<BusySpinner>(w, "StatusSpinner").IsVisible);
        var cancel = Find<Button>(w, "JobCancelButton");
        Assert.True(cancel.IsVisible);

        Click(cancel);
        Assert.True(job!.CancelRequested);
        f.Main.Tick(DateTime.UtcNow);
        Settle(w);
        Assert.Equal("Canceling...", Find<TextBlock>(w, "JobDescription").Text);
        Assert.False(cancel.IsVisible);

        gate.SetResult(new GitResult(130, "", "", Cancelled: true));
        await run;
        var finished = job.Finished!.Value;
        f.Main.Tick(finished.AddSeconds(1.9));
        Settle(w);
        Assert.Equal("Canceled", Find<TextBlock>(w, "JobDescription").Text);
        Assert.False(Find<BusySpinner>(w, "StatusSpinner").IsVisible);
        f.Main.Tick(finished.AddSeconds(2.1)); // the 2 s linger is over: back to the repository title
        Settle(w);
        Assert.False(Find<TextBlock>(w, "JobTitle").IsVisible);
        Assert.Equal(f.Tab.Title, Find<TextBlock>(w, "StatusTitle").Text);
        Assert.Empty(f.Events);

        // A finished job without a message does not linger.
        var queue = new JobQueue();
        var quiet = new Job("Quiet");
        queue.Start(quiet);
        Assert.Same(quiet, queue.Primary(DateTime.UtcNow));
        quiet.Succeed();
        queue.End(quiet);
        Assert.Null(queue.Primary(quiet.Finished!.Value.AddSeconds(0.5)));
    }

    [AvaloniaFact]
    public async Task Activity_manager_lists_jobs_and_shows_the_selected_output()
    {
        using var f = new TabFixture();
        await f.OpenAsync();
        var w = f.Window;
        f.Main.OpenActivity();
        Settle(w);
        Assert.True(f.Main.Activity.IsOpen);
        Assert.True(f.Main.Activity.IsEmpty);
        Assert.False(Find<Button>(w, "ActivityButton").IsEffectivelyEnabled); // disabled while open
        f.Main.Activity.IsOpen = false;

        await f.Tab.RunJobAsync("Create branch 'y'", async j => await GerGit.Core.Git.GitRunner.RunJobAsync(j, f.Repo.Path, ["branch", "y"]));
        await f.Tab.RunJobAsync("Delete", j => { j.Fail("failed for a reason"); return Task.FromResult<GitResult?>(null); });
        f.Events.Clear();
        f.Main.OpenActivity();
        Settle(w);
        var popup = (ActivityPopup)Find<Avalonia.Controls.Primitives.Popup>(w, "ActivityPopup").Child!;
        Settle(w);
        var act = f.Main.Activity;
        Assert.Equal(["Delete", "Create branch 'y'"], act.Rows.Select(r => r.Name));
        Assert.Same(act.Rows[0], act.Selected); // opening selects the first row
        Assert.True(act.Rows[0].ShowWarning);
        Assert.Equal("failed for a reason", act.Rows[0].Message);
        Assert.StartsWith("failed ", act.StateText);

        act.Selected = act.Rows[1];
        Assert.Matches(@"^succeeded \d{1,2} [A-Z][a-z]{2} \d{4} \d{2}:\d{2}:\d{2}$", act.StateText);
        Assert.False(act.Rows[1].ShowWarning);
        Assert.Matches(@"^\d{2}:\d{2}$", act.Rows[1].FinishTime!);
        Settle(w);
        var output = popup.GetVisualDescendants().OfType<OutputView>().Single();
        Assert.Contains("$ git branch y", output.Text);
        Assert.Equal("Create branch 'y'", popup.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "JobName").Text);

        // A running job appears on the next 200 ms tick with its cancel button.
        var gate = new TaskCompletionSource<GitResult?>();
        var run = f.Tab.RunJobAsync("Push", _ => gate.Task);
        f.Main.Tick(DateTime.UtcNow);
        Assert.Equal("Push", act.Rows[0].Name);
        Assert.True(act.Rows[0].CanCancel);
        act.Rows[0].CancelCommand.Execute(null);
        f.Main.Tick(DateTime.UtcNow);
        Assert.False(act.Rows[0].CanCancel);
        gate.SetResult(null);
        await run;
        f.Main.Tick(DateTime.UtcNow);
        act.Selected = act.Rows[0];
        Assert.StartsWith("canceled ", act.StateText);
    }

    [AvaloniaFact]
    public void Output_lines_are_coloured_by_line_kind()
    {
        Assert.Equal(OutputLineKind.Command, OutputView.Classify("$ git fetch origin"));
        Assert.Equal(OutputLineKind.CommandBold, OutputView.Classify(" 2 files changed, 3 insertions(+)"));
        Assert.Equal(OutputLineKind.CommandBold, OutputView.Classify("Switched to branch 'x'"));
        Assert.Equal(OutputLineKind.CommandNormal, OutputView.Classify(" * [new branch]      main -> origin/main"));
        Assert.Equal(OutputLineKind.Hint, OutputView.Classify("hint: Updates were rejected"));
        Assert.Equal(OutputLineKind.AppHint, OutputView.Classify("gergit: try to disable 'ignore whitespaces'"));
        Assert.Equal(OutputLineKind.Noise, OutputView.Classify("Enumerating objects: 5, done."));
        Assert.Equal(OutputLineKind.Warning, OutputView.Classify("warning: redirecting"));
        Assert.Equal(OutputLineKind.Error, OutputView.Classify("fatal: Authentication failed"));
        Assert.Equal(OutputLineKind.Default, OutputView.Classify("From /tmp/remote"));
    }

    // ---------------------------------------------------------------- askpass dialogs

    [AvaloniaFact]
    public async Task Credentials_dialog_returns_the_typed_text_or_null()
    {
        var owner = ShowOwner();
        var prompt = new CredentialPrompt("/home/u/src/myrepo", PromptKind.SshPassphrase, "Enter passphrase for key '/k/id': ", "/k/id");
        var ask = CredentialsViewModel.AskAsync(prompt, owner);
        var d = Dialog(owner);
        Assert.Equal("myrepo", d.Title);
        Assert.Equal("Passphrase for SSH key '/k/id'", Find<TextBlock>(d, "DialogDescription").Text);
        var input = Find<TextBox>(d, "Input");
        Assert.NotEqual(default, input.PasswordChar); // masked
        Assert.True(input.IsFocused);
        d.KeyTextInput("s3cret");
        Press(d, Key.Enter, PhysicalKey.Enter); // OK is always enabled
        Assert.Equal("s3cret", await ask);

        var user = CredentialsViewModel.AskAsync(new CredentialPrompt("", PromptKind.UserName, "Username for 'https://h': ", null), owner);
        d = Dialog(owner);
        Assert.Equal("Credentials Required", d.Title);
        Assert.Equal("Username for 'https://h':", Find<TextBlock>(d, "DialogDescription").Text);
        Assert.Equal(default, Find<TextBox>(d, "Input").PasswordChar);
        Press(d, Key.Escape, PhysicalKey.Escape);
        Assert.Null(await user);

        var pw = new CredentialsViewModel(new CredentialPrompt("", PromptKind.SshPassword, "git@host's password: ", "git@host"));
        Assert.Equal(("Password for 'git@host'", "Password:"), (pw.Description, pw.Label));
        owner.Close();
    }

    [AvaloniaFact]
    public async Task Host_key_dialog_answers_once_always_or_cancel()
    {
        var owner = ShowOwner();
        var text = "The authenticity of host 'h (1.2.3.4)' can't be established.\nED25519 key fingerprint is SHA256:abc.\n" +
                   "Are you sure you want to continue connecting (yes/no/[fingerprint])?";
        var prompt = new HostKeyPrompt("/r", text);

        var ask = HostKeyViewModel.AskAsync(prompt, owner);
        var d = Dialog(owner);
        Assert.Equal("Unknown SSH Host", d.Title);
        Assert.Contains("SHA256:abc", Find<SelectableTextBlock>(d, "KeyText").Text);
        Assert.Equal(["Accept Once", "Accept Always", "Cancel"],
            new[] { "SecondaryButton", "SubmitButton", "CancelButton" }.Select(n => (string)Find<Button>(d, n).Content!));
        Press(d, Key.Enter, PhysicalKey.Enter); // Enter = Cancel: nothing is accepted by Enter
        Assert.Equal(HostKeyAnswer.Cancel, await ask);

        ask = HostKeyViewModel.AskAsync(prompt, owner);
        d = Dialog(owner);
        Click(Find<Button>(d, "SecondaryButton"));
        Assert.Equal(HostKeyAnswer.Once, await ask);

        ask = HostKeyViewModel.AskAsync(prompt, owner);
        d = Dialog(owner);
        Click(Find<Button>(d, "SubmitButton"));
        Assert.Equal(HostKeyAnswer.Always, await ask);
        owner.Close();
    }

    // ---------------------------------------------------------------- window icon

    sealed class SubWindow : Window;

    [AvaloniaFact]
    public void Every_window_gets_the_app_icon()
    {
        Window plain = new(), sub = new SubWindow();
        plain.Show();
        sub.Show();
        Settle(plain, sub);
        Assert.NotNull(plain.Icon);
        Assert.NotNull(sub.Icon);
        plain.Close();
        sub.Close();
    }
}
