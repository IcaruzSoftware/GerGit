using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GerGit.Core;
using GerGit.Tests.Core;
using GerGit.ViewModels;
using GerGit.ViewModels.Dialogs;
using GerGit.Views;
using GerGit.Views.Dialogs;

namespace GerGit.Tests.Ui;

/// <summary>Commit and Push (Commit button text, Commit Options menu, Quick push), the
/// pre-push hook in the checks window, and the push after "Skip pre-commit hooks and commit". Against a
/// local bare remote; the setting is restored before the test ends. Not parallel with PreferencesTests, which writes the
/// same setting.</summary>
[Collection("OpenIn")]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class CommitAndPushTests : IDisposable
{
    readonly RemoteSetup _s = new();
    readonly List<string> _errors = [];
    readonly List<GitResult> _gitErrors = [];
    readonly List<(string Title, string Primary)> _asked = [];

    public CommitAndPushTests()
    {
        // The app's git reads the user's global config: a global hooksPath must not add or hide hooks here.
        Directory.CreateDirectory(HooksDir);
        _s.Local.Git("config", "core.hooksPath", HooksDir);
    }

    public void Dispose()
    {
        Settings.Set<bool?>(LocalChangesViewModel.PushAfterCommitKey, null);
        _s.Dispose();
    }

    string HooksDir => Path.Combine(_s.Local.Path, ".git", "hooks");
    string Head => _s.Local.Git("rev-parse", "HEAD");

    sealed record Ui(MainWindow Window, RepoTabViewModel Tab, LocalChangesViewModel Vm, LocalChangesView View)
    {
        public T Find<T>(string name) where T : Control => View.FindControl<T>(name)!;
    }

    /// <summary>Opens the main window on the repository (a.txt changed and staged unless <paramref name="stage"/> is
    /// false, the subject typed; dialogs over the main window), runs <paramref name="body"/> and always closes it.</summary>
    async Task WithUiAsync(Func<Ui, Task> body, string subject = "change", bool stage = true)
    {
        if (stage)
        {
            _s.Local.Write("a.txt", subject + "\n");
            _s.Local.Git("add", "a.txt");
        }
        var main = new MainWindowViewModel();
        var window = new MainWindow { DataContext = main };
        window.Show();
        try
        {
            Assert.Null(await main.OpenAsync(_s.Local.Path));
            var tab = main.SelectedTab!;
            tab.IsActive = false; // no watcher refreshes in between
            Settle(window);
            window.GetVisualDescendants().OfType<RepoTabView>().Single().IsVisible = true;
            tab.ShowError = (_, text) => { _errors.Add(text); return Task.CompletedTask; };
            tab.ShowGitError = (r, _) => { _gitErrors.Add(r); return Task.CompletedTask; };
            tab.ShowDialog = d => DialogWindow.ShowAsync(d, window);
            var vm = tab.LocalChanges;
            vm.Ask = (title, _, primary, _) => { _asked.Add((title, primary)); return Task.FromResult(true); };
            tab.ShowLocalChanges();
            await UntilAsync(window, () => tab.IsLoaded && tab.Refreshing.IsCompleted && vm.StagedRows.Count > 0, "Local Changes loaded");
            await vm.Diff.Loading;
            Settle(window);
            vm.Subject = subject;
            Settle(window);
            await body(new Ui(window, tab, vm, window.GetVisualDescendants().OfType<LocalChangesView>().Single()));
        }
        finally { window.Close(); } // also closes the owned checks windows
    }

    static void Settle(Window w)
    {
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    static Task Until(Ui ui, Func<bool> condition, string what) => UntilAsync(ui.Window, condition, what);

    static async Task UntilAsync(Window window, Func<bool> condition, string what)
    {
        for (int i = 0; i < 2000 && !condition(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
        Assert.True(condition(), what);
        Settle(window);
    }

    /// <summary>A git command that is expected to fail (a conflict).</summary>
    void GitFails(params string[] args) => Assert.Throws<InvalidOperationException>(() => _s.Local.Git(args));

    /// <summary>The setting is on, the commit text has no " and Push" and no arrow, and Ctrl+Enter commits without a push.</summary>
    async Task CommitsWithoutPushAsync(Ui ui, RepoStateKind state, string before, string subject = "resolved")
    {
        ui.Vm.Subject = subject;
        SetPushAfterCommit(ui, true);
        Assert.Equal((state, "Commit 1 File", false), (ui.Tab.State.Kind, ui.Vm.CommitText, ui.Vm.HasCommitMenu));
        Assert.False(ui.Find<Button>("CommitMenuButton").IsVisible);
        Press(ui, shift: false);
        await DoneAsync(ui);
        Assert.Equal(subject, _s.Local.Git("log", "-1", "--format=%s"));
        Assert.Equal(before, _s.BareSha("main"));
        Assert.False(PushJobRan(ui));
    }

    /// <summary>The commit (from a key or click) and its push are over.</summary>
    static async Task DoneAsync(Ui ui)
    {
        await Until(ui, () => !ui.Vm.Busy, "the commit ends");
        await ui.Vm.Pushing;
        Settle(ui.Window);
    }

    /// <summary>Ctrl(+Shift)+Enter while typing in the subject.</summary>
    static void Press(Ui ui, bool shift)
    {
        ui.Find<TextBox>("SubjectBox").Focus();
        Settle(ui.Window);
        ui.Window.KeyPress(Key.Enter, RawInputModifiers.Control | (shift ? RawInputModifiers.Shift : 0), PhysicalKey.Enter, "\r");
    }

    static bool PushJobRan(Ui ui) => ui.Tab.Jobs.History.Any(j => j.Name.StartsWith("Push ", StringComparison.Ordinal));

    static DialogWindow? ChecksWindow(Ui ui) => ui.Window.OwnedWindows.OfType<DialogWindow>().SingleOrDefault();
    static ChecksDialogViewModel Checks(Ui ui) => (ChecksDialogViewModel)ChecksWindow(ui)!.DataContext!;

    void Hook(string name, string body)
    {
        var path = Path.Combine(HooksDir, name);
        File.WriteAllText(path, "#!/bin/sh\n" + body);
        File.SetUnixFileMode(path, (UnixFileMode)0b111_101_101);
    }

    static void SetPushAfterCommit(Ui ui, bool on)
    {
        Settings.Set(LocalChangesViewModel.PushAfterCommitKey, on);
        ui.Vm.OnSubjectLimitsChanged(); // what closing Preferences does
        Settle(ui.Window);
    }

    [AvaloniaFact]
    public Task Button_text_and_drop_down_follow_the_setting_and_shift() => WithUiAsync(async ui =>
    {
        var vm = ui.Vm;
        var button = ui.Find<Button>("CommitButton");
        var arrow = ui.Find<Button>("CommitMenuButton");

        Assert.Equal(("Commit 1 File", true, "Commit Settings"), (button.Content, arrow.IsVisible, ToolTip.GetTip(arrow)));
        Assert.True(arrow.IsEffectivelyEnabled);
        vm.ShiftHeld = true;
        Settle(ui.Window);
        Assert.Equal("Commit 1 File and Push", button.Content);
        vm.ShiftHeld = false;

        SetPushAfterCommit(ui, true);
        Assert.Equal(("Commit 1 File and Push", "Commit"), (button.Content, vm.CommitMenuText));
        vm.ShiftHeld = true;
        Settle(ui.Window);
        Assert.Equal("Commit 1 File", button.Content);
        vm.ShiftHeld = false;

        vm.Amend = true; // no push and no drop-down
        await vm.Loading;
        Settle(ui.Window);
        Assert.Equal(("Amend Last Commit", false), (button.Content, arrow.IsVisible));
    });

    [AvaloniaFact]
    public Task The_commit_options_item_commits_and_pushes_through_its_bindings() => WithUiAsync(async ui =>
    {
        var arrow = ui.Find<Button>("CommitMenuButton");
        var flyout = (MenuFlyout)arrow.Flyout!;
        flyout.ShowAt(arrow);
        Settle(ui.Window);
        var items = flyout.Items.OfType<MenuItem>().ToList();
        Assert.Equal(("Commit Options", false), (items[0].Header, items[0].IsEnabled));
        var item = items[^1];
        Assert.Equal(("Commit and Push", KeyGesture.Parse("Ctrl+Shift+Enter"), true), (item.Header, item.InputGesture, item.CommandParameter));
        Assert.Same(ui.Vm.CommitCommand, item.Command);
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); // runs Command with CommandParameter
        await DoneAsync(ui);
        Assert.Equal("change", _s.Local.Git("log", "-1", "--format=%s"));
        Assert.Equal(Head, _s.BareSha("main"));
    });

    [AvaloniaFact]
    public Task Commit_and_push_sends_the_commit_and_sets_the_upstream_of_a_new_branch() => WithUiAsync(async ui =>
    {
        // Setting off: Ctrl+Shift+Enter commits and pushes main to its upstream.
        Press(ui, shift: true);
        await DoneAsync(ui);
        Assert.Equal("first", _s.Local.Git("log", "-1", "--format=%s"));
        Assert.Equal(Head, _s.BareSha("main"));
        Assert.Equal("Push 'main' to 'origin'", ui.Tab.Jobs.History[0].Name);

        // Ctrl+Enter with the setting off: no push.
        _s.Local.Write("a.txt", "second\n");
        _s.Local.Git("add", "a.txt");
        await ui.Tab.RefreshAsync();
        ui.Vm.Subject = "second";
        Settle(ui.Window);
        Press(ui, shift: false);
        await DoneAsync(ui);
        Assert.Equal("second", _s.Local.Git("log", "-1", "--format=%s"));
        Assert.NotEqual(Head, _s.BareSha("main"));

        // Setting on, a new branch without upstream and one remote: Ctrl+Enter pushes it and sets the upstream.
        _s.Local.Git("checkout", "-q", "-b", "feat");
        _s.Local.Write("b.txt", "b\n");
        _s.Local.Git("add", "b.txt");
        await ui.Tab.RefreshAsync();
        SetPushAfterCommit(ui, true);
        ui.Vm.Subject = "on feat";
        Settle(ui.Window);
        Assert.Equal("Commit 1 File and Push", ui.Vm.CommitText);
        Press(ui, shift: false);
        await DoneAsync(ui);
        Assert.Equal(Head, _s.BareSha("feat"));
        Assert.Equal("origin/feat", _s.Local.Git("rev-parse", "--abbrev-ref", "feat@{upstream}"));
        Assert.Empty(_gitErrors);
        Assert.Empty(_errors);
    }, "first");

    [AvaloniaFact]
    public Task The_keys_never_push_after_an_amend() => WithUiAsync(async ui =>
    {
        var before = _s.BareSha("main");
        foreach (var (setting, shift) in new[] { (true, false), (false, true) })
        {
            SetPushAfterCommit(ui, setting);
            ui.Vm.Amend = true;
            await ui.Vm.Loading;
            ui.Vm.Subject = $"amended {setting}";
            Settle(ui.Window);
            Assert.True(ui.Vm.CanCommit);
            Press(ui, shift);
            await DoneAsync(ui);
            Assert.Equal($"amended {setting}", _s.Local.Git("log", "-1", "--format=%s")); // amended
            Assert.Equal(before, _s.BareSha("main"));
            Assert.False(PushJobRan(ui));
        }
        Assert.Empty(_errors);
    });

    [AvaloniaFact]
    public async Task The_keys_never_push_after_a_squash_commit()
    {
        _s.Local.Git("checkout", "-q", "-b", "side");
        _s.Local.Commit("side", 3, ("s.txt", "s\n"));
        _s.Local.Git("checkout", "-q", "main");
        _s.Local.Git("merge", "-q", "--squash", "side");
        var before = _s.BareSha("main");
        await WithUiAsync(async ui =>
        {
            Assert.Equal(RepoStateKind.Squash, ui.Tab.State.Kind);
            SetPushAfterCommit(ui, true);
            Assert.Equal(("Squash", false), (ui.Vm.CommitText, ui.Vm.HasCommitMenu));
            Press(ui, shift: false);
            await DoneAsync(ui);
            Assert.Equal("squashed", _s.Local.Git("log", "-1", "--format=%s"));
            Assert.Equal(before, _s.BareSha("main"));
            Assert.False(PushJobRan(ui));
        }, "squashed", stage: false);
    }

    [AvaloniaFact]
    public async Task A_rejected_push_after_the_commit_shows_the_git_error_and_never_forces()
    {
        var theirs = _s.OtherPush("theirs", 5, "main", ("o.txt", "o\n"));
        await WithUiAsync(async ui =>
        {
            await ui.Vm.CommitCommand.ExecuteAsync(true);
            await DoneAsync(ui);

            Assert.Equal("change", _s.Local.Git("log", "-1", "--format=%s")); // committed
            Assert.Equal(theirs, _s.BareSha("main"));                         // not overwritten
            Assert.Equal("'main' rejected", ui.Tab.Jobs.History[0].Message);
            Assert.Contains("[rejected]", Assert.Single(_gitErrors).ReadableOutput);
            Assert.DoesNotContain("--force", ui.Tab.Jobs.History[0].Output);
        });
    }

    [AvaloniaFact]
    public async Task A_green_pre_push_hook_shows_live_checks_then_pushes()
    {
        Hook("pre-push", $"printf '{HookChecksTests.Line("ruff", "Passed")}\\n{HookChecksTests.Dots("pytest")}'\nsleep 0.7\nprintf 'Passed\\n'\n");
        var delay = LocalChangesViewModel.ChecksCloseDelay;
        LocalChangesViewModel.ChecksCloseDelay = TimeSpan.FromMinutes(1);
        try
        {
            await WithUiAsync(async ui =>
            {
                await ui.Vm.CommitCommand.ExecuteAsync(true);
                await Until(ui, () => ChecksWindow(ui) is not null && Checks(ui).Checks.Hooks.Any(h => h is { Name: "pytest", IsRunning: true }), "live checks");
                var w = ChecksWindow(ui)!;
                Assert.EndsWith(" — Push — Checks", w.Title);
                Assert.False(w.IsDialog);
                Assert.Equal("Cancel", Checks(ui).CancelText);
                await DoneAsync(ui);

                var vm = Checks(ui);
                Assert.Equal((DialogStatus.Success, "Pushed, checks passed"), (vm.Status, vm.StatusText));
                Assert.Equal([("ruff", true), ("pytest", true)], vm.Checks.Hooks.Select(h => (h.Name, h.IsPassed)));
                Assert.Equal(Head, _s.BareSha("main"));
                LocalChangesViewModel.ChecksCloseDelay = TimeSpan.FromMilliseconds(200);
                await Until(ui, () => ChecksWindow(ui) is null, "closes by itself");
                Assert.Empty(_gitErrors);
            });
        }
        finally { LocalChangesViewModel.ChecksCloseDelay = delay; }
    }

    [AvaloniaFact]
    public async Task A_red_pre_push_hook_leaves_the_remote_unchanged_and_shows_red_without_a_skip()
    {
        Hook("pre-push", $"printf '{HookChecksTests.Line("pytest", "Failed")}\\n- hook id: pytest\\n'\nexit 1\n");
        var before = _s.BareSha("main");
        await WithUiAsync(async ui =>
        {
            await ui.Vm.CommitCommand.ExecuteAsync(true);
            await DoneAsync(ui);

            Assert.Equal("change", _s.Local.Git("log", "-1", "--format=%s")); // the commit stays
            Assert.Equal(before, _s.BareSha("main"));                         // nothing pushed
            var vm = Checks(ui);
            Assert.Equal((DialogStatus.Error, "Checks failed: pytest — not pushed"), (vm.Status, vm.StatusText));
            Assert.True(vm.Checks.Hooks.Single().IsFailed);
            Assert.Equal(((string?)null, "Close"), (vm.SubmitText, vm.CancelText)); // hooks are never bypassed: no skip
            Assert.Empty(_gitErrors); // the window shows it
            Assert.Equal(JobOutcome.Failed, ui.Tab.Jobs.History[0].Outcome);
            vm.Cancel();
            await Until(ui, () => ChecksWindow(ui) is null, "Close");
        });
    }

    [AvaloniaFact]
    public async Task Cancel_interrupts_the_pre_push_hook_through_the_ladder()
    {
        var marker = Path.Combine(_s.Local.Path, ".git", "hook-int");
        Hook("pre-push", $"trap 'echo int > \"{marker}\"; exit 130' INT\nprintf '{HookChecksTests.Dots("pytest")}'\n" +
                         "echo ready > .git/hook-ready\nsleep 30 & wait\n");
        var before = _s.BareSha("main");
        await WithUiAsync(async ui =>
        {
            await ui.Vm.CommitCommand.ExecuteAsync(true);
            await Until(ui, () => ChecksWindow(ui) is not null && File.Exists(Path.Combine(_s.Local.Path, ".git", "hook-ready")), "the running hook");
            Checks(ui).Cancel();
            await DoneAsync(ui);

            Assert.True(File.Exists(marker)); // the hook got its SIGINT
            Assert.Equal(before, _s.BareSha("main"));
            Assert.Equal("Cancelled during checks — not pushed", Checks(ui).StatusText);
            Assert.Empty(_gitErrors);
        });
    }

    /// <summary>The pty is non-canonical with MIN=0 and TIME=0: a prompt on /dev/tty (ssh's host key or passphrase
    /// question without askpass) reads EOF at once instead of waiting for input no one can type.</summary>
    [AvaloniaFact]
    public async Task A_pre_push_hook_reading_the_terminal_does_not_hang()
    {
        Hook("pre-push", "read x </dev/tty\necho tty-read-returned\n");
        await WithUiAsync(async ui =>
        {
            var sw = Stopwatch.StartNew();
            await ui.Vm.CommitCommand.ExecuteAsync(true);
            await DoneAsync(ui);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(8), $"took {sw.Elapsed}");
            Assert.Equal(Head, _s.BareSha("main"));
            Assert.Contains("tty-read-returned", ui.Tab.Jobs.History[0].Output);
        });
    }

    [AvaloniaFact]
    public async Task Skip_after_a_failed_commit_hook_pushes_only_when_requested()
    {
        Hook("pre-commit", "echo hook says no\nexit 1\n");
        var before = _s.BareSha("main");
        await WithUiAsync(async ui =>
        {
            await ui.Vm.CommitCommand.ExecuteAsync(false); // "Skip pre-commit hooks and commit" is answered
            await DoneAsync(ui);
            Assert.Equal(("Git Error", "Skip pre-commit hooks and commit"), _asked.Single());
            Assert.Equal("no push", _s.Local.Git("log", "-1", "--format=%s"));
            Assert.Equal(before, _s.BareSha("main"));

            _s.Local.Write("a.txt", "push\n");
            _s.Local.Git("add", "a.txt");
            await ui.Tab.RefreshAsync();
            ui.Vm.Subject = "push";
            await ui.Vm.CommitCommand.ExecuteAsync(true);
            await DoneAsync(ui);
            Assert.Equal(2, _asked.Count);
            Assert.Equal("push", _s.Local.Git("log", "-1", "--format=%s"));
            Assert.Equal(Head, _s.BareSha("main"));
        }, "no push");
    }

    [AvaloniaFact]
    public async Task Skip_in_the_commit_checks_window_pushes_when_requested()
    {
        Hook("pre-commit", $"printf '{HookChecksTests.Line("pytest", "Failed")}\\n'\nexit 1\n");
        await WithUiAsync(async ui =>
        {
            await ui.Vm.CommitCommand.ExecuteAsync(true);
            await Until(ui, () => ChecksWindow(ui) is not null && Checks(ui).Skip is not null, "the red commit checks");
            await Checks(ui).SubmitAsync(); // "Skip pre-commit hooks and commit"
            await DoneAsync(ui);

            Assert.Equal("change", _s.Local.Git("log", "-1", "--format=%s"));
            Assert.Equal(Head, _s.BareSha("main"));
            Assert.Empty(_gitErrors);
        });
    }

    [AvaloniaFact]
    public async Task Cancel_during_post_commit_keeps_the_commit_but_drops_the_push()
    {
        Hook("post-commit", $"printf '{HookChecksTests.Dots("pytest")}'\necho ready > .git/hook-ready\nsleep 30\n");
        var before = _s.BareSha("main");
        await WithUiAsync(async ui =>
        {
            var run = ui.Vm.CommitCommand.ExecuteAsync(true);
            await Until(ui, () => ChecksWindow(ui) is not null && File.Exists(Path.Combine(_s.Local.Path, ".git", "hook-ready")), "the running hook");
            Checks(ui).Cancel();
            await Until(ui, () => run.IsCompleted, "the commit ends");
            await run;
            await DoneAsync(ui);

            Assert.Equal("change", _s.Local.Git("log", "-1", "--format=%s")); // committed before post-commit
            Assert.Equal(before, _s.BareSha("main"));
            Assert.False(PushJobRan(ui));
        });
    }

    [AvaloniaFact]
    public async Task No_push_during_a_cherry_pick_sequence()
    {
        var before = _s.BareSha("main");
        _s.Local.Git("checkout", "-q", "-b", "side");
        var c1 = _s.Local.Commit("c1", 3, ("a.txt", "side\n"));
        var c2 = _s.Local.Commit("c2", 4, ("s.txt", "s\n"));
        _s.Local.Git("checkout", "-q", "main");
        _s.Local.Commit("main2", 5, ("a.txt", "main\n"));
        GitFails("cherry-pick", c1, c2);
        _s.Local.Write("a.txt", "resolved\n");
        _s.Local.Git("add", "a.txt");
        await WithUiAsync(async ui =>
        {
            // Stopped at the conflict of the first pick (CHERRY_PICK_HEAD and sequencer/), then the sequence with changes.
            await CommitsWithoutPushAsync(ui, RepoStateKind.CherryPick, before);
            Assert.Equal((RepoStateKind.CherryPickSequence, "Continue Cherry-Pick"), (ui.Tab.State.Kind, ui.Vm.CommitText));
            _s.Local.Write("a.txt", "more\n");
            _s.Local.Git("add", "a.txt");
            await ui.Tab.RefreshAsync();
            await ui.Tab.Refreshing;
            await CommitsWithoutPushAsync(ui, RepoStateKind.CherryPickSequence, before, "more");
        }, "resolved", stage: false);
    }

    [AvaloniaFact]
    public async Task No_push_during_a_revert()
    {
        var before = _s.BareSha("main");
        var r1 = _s.Local.Commit("r1", 3, ("a.txt", "r1\n"));
        _s.Local.Commit("r2", 4, ("a.txt", "r2\n"));
        GitFails("revert", "--no-edit", r1);
        _s.Local.Write("a.txt", "resolved\n");
        _s.Local.Git("add", "a.txt");
        await WithUiAsync(ui => CommitsWithoutPushAsync(ui, RepoStateKind.Revert, before), "resolved", stage: false);
    }

    [AvaloniaFact]
    public async Task A_red_post_commit_hook_keeps_the_commit_but_does_not_push()
    {
        Hook("post-commit", $"printf '{HookChecksTests.Line("pytest", "Failed")}\\n'\nexit 1\n");
        var before = _s.BareSha("main");
        await WithUiAsync(async ui =>
        {
            await ui.Vm.CommitCommand.ExecuteAsync(true);
            await DoneAsync(ui);
            Assert.Equal("change", _s.Local.Git("log", "-1", "--format=%s"));
            Assert.Equal((DialogStatus.Warning, "Committed, but checks failed: pytest"), (Checks(ui).Status, Checks(ui).StatusText));
            Assert.Equal(before, _s.BareSha("main"));
            Assert.False(PushJobRan(ui));
        });
    }

    [AvaloniaFact]
    public Task The_push_goes_only_to_the_committed_branch() => WithUiAsync(async ui =>
    {
        var before = _s.BareSha("main");
        _s.Local.Git("commit", "-q", "-m", "local");
        await ui.Tab.RefreshAsync();
        await ui.Tab.Refreshing;
        await ui.Tab.RemoteOps.QuickPushAsync("refs/heads/other"); // HEAD is on main now: dropped
        Assert.Equal(before, _s.BareSha("main"));
        Assert.False(PushJobRan(ui));
        // git's HEAD moved behind the tab's back (the tab still shows main): dropped as well.
        _s.Local.Git("checkout", "-q", "-b", "behind");
        await ui.Tab.RemoteOps.QuickPushAsync("refs/heads/main");
        Assert.Equal(before, _s.BareSha("main"));
        Assert.False(PushJobRan(ui));
        _s.Local.Git("checkout", "-q", "main");
        await ui.Tab.RemoteOps.QuickPushAsync("refs/heads/main");
        Assert.Equal(Head, _s.BareSha("main"));
    });

    /// <summary>The setting is on, the text reads " and Push", and Ctrl+Enter commits and pushes main.</summary>
    async Task CommitsAndPushesAsync(Ui ui, RepoStateKind state)
    {
        SetPushAfterCommit(ui, true);
        Assert.Equal((state, "Commit 1 File and Push", true), (ui.Tab.State.Kind, ui.Vm.CommitText, ui.Vm.HasCommitMenu));
        Press(ui, shift: false);
        await DoneAsync(ui);
        Assert.Equal("resolved", _s.Local.Git("log", "-1", "--format=%s"));
        Assert.Equal(Head, _s.BareSha("main"));
        Assert.Empty(_gitErrors);
    }

    /// <summary>side changes a.txt (c1), main changes it too: picking or merging c1 conflicts.</summary>
    string Diverged()
    {
        _s.Local.Git("checkout", "-q", "-b", "side");
        var c1 = _s.Local.Commit("c1", 3, ("a.txt", "side\n"));
        _s.Local.Git("checkout", "-q", "main");
        _s.Local.Commit("main2", 5, ("a.txt", "main\n"));
        return c1;
    }

    void Resolve()
    {
        _s.Local.Write("a.txt", "resolved\n");
        _s.Local.Git("add", "a.txt");
    }

    [AvaloniaFact]
    public async Task A_single_cherry_pick_still_pushes()
    {
        GitFails("cherry-pick", Diverged());
        Resolve();
        Assert.False(Directory.Exists(Path.Combine(_s.Local.Path, ".git", "sequencer")));
        await WithUiAsync(ui => CommitsAndPushesAsync(ui, RepoStateKind.CherryPick), "resolved", stage: false);
    }

    [AvaloniaFact]
    public async Task A_merge_commit_still_pushes()
    {
        Diverged();
        GitFails("merge", "side");
        Resolve();
        await WithUiAsync(async ui =>
        {
            await CommitsAndPushesAsync(ui, RepoStateKind.Merge);
            Assert.Equal(3, _s.Local.Git("rev-list", "--parents", "-n", "1", "HEAD").Split(' ').Length); // a merge commit
        }, "resolved", stage: false);
    }

    [AvaloniaFact]
    public async Task No_push_on_a_detached_head()
    {
        _s.Local.Git("checkout", "-q", "--detach");
        var before = _s.BareSha("main");
        await WithUiAsync(async ui =>
        {
            SetPushAfterCommit(ui, true);
            Assert.Equal(("Commit 1 File", false), (ui.Vm.CommitText, ui.Vm.HasCommitMenu));
            Press(ui, shift: false);
            await DoneAsync(ui);
            Assert.Equal("change", _s.Local.Git("log", "-1", "--format=%s"));
            Assert.Equal(before, _s.BareSha("main"));
            Assert.False(PushJobRan(ui));
        });
    }

    /// <summary>post-commit still runs after a skip commit; its red pre-commit line must drop the push.</summary>
    void RedPostCommit() => Hook("post-commit", $"printf '{HookChecksTests.Line("post", "Failed")}\\n'\nexit 1\n");

    [AvaloniaFact]
    public async Task A_red_post_commit_hook_after_the_skip_question_does_not_push()
    {
        Hook("pre-commit", "echo hook says no\nexit 1\n");
        RedPostCommit();
        var before = _s.BareSha("main");
        await WithUiAsync(async ui =>
        {
            await ui.Vm.CommitCommand.ExecuteAsync(true); // "Skip pre-commit hooks and commit" is answered
            await DoneAsync(ui);
            Assert.Single(_asked);
            Assert.Equal("change", _s.Local.Git("log", "-1", "--format=%s"));
            Assert.Equal(before, _s.BareSha("main"));
            Assert.False(PushJobRan(ui));
        });
    }

    [AvaloniaFact]
    public async Task A_red_post_commit_hook_after_the_skip_in_the_checks_window_does_not_push()
    {
        Hook("pre-commit", $"printf '{HookChecksTests.Line("pytest", "Failed")}\\n'\nexit 1\n");
        RedPostCommit();
        var before = _s.BareSha("main");
        await WithUiAsync(async ui =>
        {
            await ui.Vm.CommitCommand.ExecuteAsync(true);
            await Until(ui, () => ChecksWindow(ui) is not null && Checks(ui).Skip is not null, "the red commit checks");
            await Checks(ui).SubmitAsync();
            await DoneAsync(ui);
            Assert.Equal("change", _s.Local.Git("log", "-1", "--format=%s"));
            Assert.Equal(before, _s.BareSha("main"));
            Assert.False(PushJobRan(ui));
        });
    }
}
