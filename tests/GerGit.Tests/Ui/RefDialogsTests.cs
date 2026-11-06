using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GerGit.Controls;
using GerGit.Core.Git;
using GerGit.Tests.Core;
using GerGit.ViewModels;
using GerGit.ViewModels.Dialogs;
using GerGit.ViewModels.Dialogs.Refs;
using GerGit.Views.Dialogs;
using GerGit.Views.Dialogs.Refs;

namespace GerGit.Tests.Ui;

/// <summary>Headless smoke tests: each dialog opens through its entry point with its defaults,
/// validity messages and dynamic submit texts; the local-changes block and its Shift relabel.</summary>
[Collection("RefOps")]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public class RefDialogsTests
{
    static void Settle(Window w)
    {
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    static T Find<T>(Window w, string name) where T : Control => w.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    sealed class Shown<T>(DialogWindow window, T vm, Task done) where T : DialogViewModel
    {
        public DialogWindow Window { get; } = window;
        public T Vm { get; } = vm;

        public async Task CloseAsync()
        {
            Vm.Cancel();
            Settle(Window);
            await done;
        }
    }

    /// <summary>Runs the entry point with a real dialog window over <paramref name="owner"/> and waits until it is open.</summary>
    static async Task<Shown<T>> OpenAsync<T>(RefFixture f, Window owner, Func<Task> entry) where T : DialogViewModel
    {
        DialogViewModel? vm = null;
        f.Tab.ShowDialog = d => { vm = d; return DialogWindow.ShowAsync(d, owner); };
        var done = entry();
        // The entry point reads git state first; wait for the window (condition, not a fixed delay).
        for (int i = 0; i < 1000 && owner.OwnedWindows.Count == 0; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
        var w = (DialogWindow)owner.OwnedWindows.Single();
        Settle(w);
        return new Shown<T>(w, (T)vm!, done);
    }

    static Window Owner()
    {
        var owner = new Window { Width = 900, Height = 700 };
        owner.Show();
        return owner;
    }

    static async Task<RefFixture> RepoAsync(bool origin = false)
    {
        var f = new RefFixture();
        f.Repo.Commit("init", 0, ("a.txt", "1\n"));
        f.Repo.Git("branch", "b");
        if (origin) f.AddOrigin();
        await f.OpenAsync();
        return f;
    }

    [AvaloniaFact]
    public async Task Create_branch_dialog_defaults_validity_and_submit_text()
    {
        using var f = await RepoAsync();
        var owner = Owner();
        var s = await OpenAsync<CreateBranchViewModel>(f, owner, () => f.Ops.CreateBranchAsync());
        var (w, vm) = (s.Window, s.Vm);
        Assert.Equal(("Create Branch", 600), (w.Title, w.Width));
        Assert.Equal("Use '/' as a path separator to create folders", Find<TextBlock>(w, "DialogDescription").Text);
        Assert.Equal("main", Find<RefView>(w, "Start").Text);
        var box = Find<RefNameBox>(w, "NameBox");
        Assert.Equal("Enter branch name", box.PlaceholderText);
        Assert.True(box.IsFocused);
        Assert.Equal(("Create", false, ""), (Find<Button>(w, "SubmitButton").Content, vm.SubmitEnabled, vm.StatusText));

        box.Text = "a..b";
        Settle(w);
        Assert.Equal("Name cannot contain '..'", Find<TextBlock>(w, "FooterText").Text);
        box.Text = "-m"; // would be an option of `git branch`
        Settle(w);
        Assert.Equal(("Name cannot begin with '-'", false), (vm.StatusText, vm.SubmitEnabled));
        box.Text = "MAIN";
        Settle(w);
        Assert.Equal("Branch 'MAIN' already exists", vm.StatusText);
        Assert.False(Find<Button>(w, "SubmitButton").IsEffectivelyEnabled);
        box.Text = "new";
        Settle(w);
        Assert.True(Find<Button>(w, "SubmitButton").IsEffectivelyEnabled);

        Find<CheckBox>(w, "CheckoutBox").IsChecked = true;
        Settle(w);
        Assert.Equal("Create and Checkout", Find<Button>(w, "SubmitButton").Content);
        Assert.False(Find<RadioButton>(w, "Stash").IsEffectivelyVisible); // clean tree: no local-changes block
        await s.CloseAsync();

        // Dirty tree: the block follows the check box.
        f.Repo.Write("a.txt", "dirty\n");
        s = await OpenAsync<CreateBranchViewModel>(f, owner, () => f.Ops.CreateBranchAsync());
        w = s.Window;
        Assert.False(Find<CheckBox>(w, "CheckoutBox").IsChecked); // the last dialog was not submitted: still the default
        Assert.False(Find<RadioButton>(w, "Stash").IsEffectivelyVisible);
        Find<CheckBox>(w, "CheckoutBox").IsChecked = true;
        Settle(w);
        Assert.True(Find<RadioButton>(w, "Stash").IsEffectivelyVisible);
        await s.CloseAsync();
        owner.Close();
    }

    [AvaloniaFact]
    public async Task Local_changes_block_shows_when_dirty_and_shift_relabels()
    {
        using var f = await RepoAsync();
        var owner = Owner();
        var s = await OpenAsync<CheckoutViewModel>(f, owner, async () =>
        {
            f.Repo.Write("a.txt", "dirty\n");
            await f.Ops.CheckoutCommitAsync(f.Ref("refs/heads/b").Sha);
        });
        var w = s.Window;
        Assert.Equal(("Checkout Commit", 590, "Checkout Commit"), (w.Title, w.Width, Find<Button>(w, "SubmitButton").Content));
        var stash = Find<RadioButton>(w, "Stash");
        Assert.True(stash.IsEffectivelyVisible);
        Assert.True(Find<RadioButton>(w, "DontChange").IsChecked);
        Assert.Equal("Stash and reapply", stash.Content);
        Assert.Equal("Hold Shift to not reapply", ToolTip.GetTip(stash));

        w.KeyPress(Key.LeftShift, RawInputModifiers.Shift, PhysicalKey.ShiftLeft, null);
        Settle(w);
        Assert.Equal("Leave as stash", stash.Content);
        w.KeyRelease(Key.LeftShift, RawInputModifiers.None, PhysicalKey.ShiftLeft, null);
        Settle(w);
        Assert.Equal("Stash and reapply", stash.Content);

        Assert.False(Find<LucideIcon>(w, "DiscardWarning").IsVisible);
        Find<RadioButton>(w, "Discard").IsChecked = true;
        Settle(w);
        Assert.True(Find<LucideIcon>(w, "DiscardWarning").IsVisible);
        Assert.Equal(GerGit.Core.Git.LocalChangesMode.Discard, s.Vm.Local.Mode);
        await s.CloseAsync();

        // Clean: the block and its label are hidden.
        f.Repo.Git("checkout", "--", "a.txt");
        s = await OpenAsync<CheckoutViewModel>(f, owner, () => f.Ops.CheckoutCommitAsync(f.Ref("refs/heads/b").Sha));
        Assert.False(Find<RadioButton>(s.Window, "Stash").IsEffectivelyVisible);
        await s.CloseAsync();
        owner.Close();
    }

    [AvaloniaFact]
    public async Task Track_and_checkout_sync_dialogs()
    {
        using var f = await RepoAsync(origin: true);
        f.Repo.Git("push", "-q", "origin", "main:refs/heads/feat");
        await f.RefreshAsync();
        var owner = Owner();
        var s = await OpenAsync<TrackRemoteBranchViewModel>(f, owner, () => f.Ops.CheckoutRemoteBranchAsync(f.Ref("refs/remotes/origin/feat")));
        var w = s.Window;
        Assert.Equal(("Track Remote Branch", 540, "Track"), (w.Title, w.Width, Find<Button>(w, "SubmitButton").Content));
        Assert.Equal("origin/feat", Find<RefView>(w, "Remote").Text);
        Assert.Equal("feat", Find<RefNameBox>(w, "NameBox").Text);
        Find<RefNameBox>(w, "NameBox").Text = "b";
        Settle(w);
        Assert.Equal("Branch 'b' already exists", Find<TextBlock>(w, "FooterText").Text);
        Find<RefNameBox>(w, "NameBox").Text = "-b";
        Settle(w);
        Assert.Equal("Name cannot begin with '-'", Find<TextBlock>(w, "FooterText").Text);
        await s.CloseAsync();

        // Diverged main vs origin/main → Checkout and sync.
        f.Repo.Commit("remote", 1, ("a.txt", "r\n"));
        f.Repo.Git("push", "-q", "origin", "main");
        f.Repo.Git("reset", "-q", "--hard", "HEAD~1");
        f.Repo.Commit("local", 2, ("l.txt", "l\n"));
        f.Repo.Git("checkout", "-q", "b");
        await f.RefreshAsync();
        var sync = await OpenAsync<CheckoutSyncViewModel>(f, owner, () => f.Ops.CheckoutRemoteBranchAsync(f.Ref("refs/remotes/origin/main")));
        w = sync.Window;
        Assert.Equal(("Checkout Branch", 590), (w.Title, w.Width));
        Assert.Equal("Switch to 'main' branch", Find<TextBlock>(w, "DialogDescription").Text);
        var combo = Find<ComboBox>(w, "ActionBox");
        Assert.Equal((0, "Checkout"), (combo.SelectedIndex, Find<Button>(w, "SubmitButton").Content));
        combo.SelectedIndex = 3;
        Settle(w);
        Assert.Equal("Checkout and Merge", Find<Button>(w, "SubmitButton").Content);
        combo.SelectedIndex = 5;
        Settle(w);
        Assert.Equal("Checkout and Reset", Find<Button>(w, "SubmitButton").Content);
        Assert.Equal("Reset (--hard) 'main' to 'origin/main'", sync.Vm.ResetText);
        Assert.False(Find<CheckBox>(w, "StashBox").IsChecked);
        await sync.CloseAsync();
        owner.Close();
    }

    [AvaloniaFact]
    public async Task Rename_dialog_validation_order()
    {
        using var f = await RepoAsync(origin: true);
        f.Repo.Git("push", "-q", "origin", "main:refs/heads/taken");
        await f.RefreshAsync();
        var owner = Owner();
        var s = await OpenAsync<RenameBranchViewModel>(f, owner, () => f.Ops.RenameBranchAsync(f.Ref("refs/heads/main")));
        var (w, vm) = (s.Window, s.Vm);
        Assert.Equal(("Rename Local Branch", 500, "Rename"), (w.Title, w.Width, Find<Button>(w, "SubmitButton").Content));
        var box = Find<RefNameBox>(w, "NameBox");
        Assert.Equal(("main", "Enter new branch name", true), (box.Text, box.PlaceholderText, box.IsFocused));
        Assert.Equal((0, 4), (box.SelectionStart, box.SelectionEnd)); // the name is selected, typing replaces it
        Assert.False(vm.SubmitEnabled);
        Assert.Equal("", vm.StatusText);
        var remote = Find<CheckBox>(w, "RenameRemoteBox");
        Assert.True(remote.IsVisible);
        Assert.Equal("Also rename origin/main", remote.Content);

        remote.IsChecked = true;
        Settle(w);
        Assert.Equal("Branch origin/main already exists", vm.StatusText); // the taken remote branch is reported before "same name"
        Assert.False(vm.SubmitEnabled);
        box.Text = "taken";
        Settle(w);
        Assert.Equal(("Branch origin/taken already exists", false), (vm.StatusText, vm.SubmitEnabled));
        box.Text = "B";
        Settle(w);
        Assert.Equal("Branch 'B' already exists", vm.StatusText);
        box.Text = "-f";
        Settle(w);
        Assert.Equal(("Name cannot begin with '-'", false), (vm.StatusText, vm.SubmitEnabled));
        box.Text = "fresh";
        Settle(w);
        Assert.Equal(("Renaming can break tracking references for other users", true), (vm.StatusText, vm.SubmitEnabled));
        await s.CloseAsync();

        // A branch without an upstream of the same name: no check box.
        s = await OpenAsync<RenameBranchViewModel>(f, owner, () => f.Ops.RenameBranchAsync(f.Ref("refs/heads/b")));
        Assert.False(Find<CheckBox>(s.Window, "RenameRemoteBox").IsVisible);
        await s.CloseAsync();
        owner.Close();
    }

    [AvaloniaFact]
    public async Task Delete_dialogs_single_multiple_and_options()
    {
        using var f = await RepoAsync(origin: true);
        var owner = Owner();
        var s = await OpenAsync<DeleteRefsViewModel>(f, owner, () => f.Ops.DeleteBranchesAsync([f.Ref("refs/heads/main")]));
        var w = s.Window;
        Assert.Equal(("Delete Branch", 570, "Delete"), (w.Title, w.Width, Find<Button>(w, "SubmitButton").Content));
        Assert.Equal("Delete local branch from your repository", Find<TextBlock>(w, "DialogDescription").Text);
        Assert.Equal("main", Find<RefView>(w, "Single").Text);
        var check = Find<CheckBox>(w, "OptionCheck");
        Assert.Equal(("Also delete _remote branch", true, false), (check.Content, check.IsEffectivelyEnabled, check.IsChecked));
        Assert.Equal("Alt+R", ToolTip.GetTip(check));
        Assert.Equal("origin/main", Find<RefView>(w, "CheckDetail").Text);
        Assert.False(Find<LucideIcon>(w, "CheckWarning").IsVisible);
        check.IsChecked = true;
        Settle(w);
        Assert.True(Find<LucideIcon>(w, "CheckWarning").IsVisible);
        await s.CloseAsync();

        s = await OpenAsync<DeleteRefsViewModel>(f, owner, () => f.Ops.DeleteBranchesAsync([f.Ref("refs/heads/b")]));
        check = Find<CheckBox>(s.Window, "OptionCheck");
        Assert.Equal(("Also delete corresponding _remote branch", false), (check.Content, check.IsEffectivelyEnabled));
        await s.CloseAsync();

        s = await OpenAsync<DeleteRefsViewModel>(f, owner, () => f.Ops.DeleteBranchesAsync([f.Ref("refs/heads/b"), f.Ref("refs/heads/main")]));
        w = s.Window;
        Assert.Equal(("Delete Branches", "Delete 2 branches", 270.0), (w.Title, Find<Button>(w, "SubmitButton").Content, w.Height));
        Assert.True(w.CanResize);
        Assert.Equal(2, Find<ListBox>(w, "List").ItemCount);
        await s.CloseAsync();

        f.Repo.Git("tag", "-a", "v1", "-m", "one");
        await f.RefreshAsync();
        s = await OpenAsync<DeleteRefsViewModel>(f, owner, () => f.Ops.DeleteTagsAsync([f.Ref("refs/tags/v1")]));
        w = s.Window;
        Assert.Equal(("Delete Tag", 520), (w.Title, w.Width));
        Assert.Equal("Delete tag from remote repositories", Find<CheckBox>(w, "OptionCheck").Content);
        Assert.Null(ToolTip.GetTip(Find<CheckBox>(w, "OptionCheck"))); // no access key, no "Alt+R"
        await s.CloseAsync();

        s = await OpenAsync<DeleteRefsViewModel>(f, owner, () => f.Ops.DeleteRemoteBranchesAsync([f.Ref("refs/remotes/origin/main")]));
        w = s.Window;
        Assert.Equal("Delete branch from remote repository", Find<TextBlock>(w, "DialogDescription").Text);
        Assert.False(Find<CheckBox>(w, "OptionCheck").IsEffectivelyVisible);
        await s.CloseAsync();
        owner.Close();
    }

    [AvaloniaFact]
    public async Task Reset_dialog_keys_pick_the_type()
    {
        using var f = await RepoAsync();
        var owner = Owner();
        var s = await OpenAsync<ResetViewModel>(f, owner, () => f.Ops.ResetAsync(f.Ref("refs/heads/main").Sha));
        var w = s.Window;
        Assert.Equal(("Reset Current Branch to Revision", 590, "Reset"), (w.Title, w.Width, Find<Button>(w, "SubmitButton").Content));
        Assert.Equal("main", Find<RefView>(w, "Branch").Text);
        Assert.Equal("init", Find<RefView>(w, "Target").Detail);
        var combo = Find<ComboBox>(w, "KindBox");
        Assert.Equal(1, combo.SelectedIndex);
        w.KeyPress(Key.H, RawInputModifiers.None, PhysicalKey.H, "h");
        Settle(w);
        Assert.Equal((2, ResetKind.Hard), (combo.SelectedIndex, s.Vm.Kind));
        w.KeyPress(Key.S, RawInputModifiers.None, PhysicalKey.S, "s");
        Settle(w);
        Assert.Equal(ResetKind.Soft, s.Vm.Kind);
        await s.CloseAsync();

        f.Repo.Git("checkout", "-q", "--detach");
        await f.RefreshAsync();
        s = await OpenAsync<ResetViewModel>(f, owner, () => f.Ops.ResetAsync(f.Ref("refs/heads/main").Sha));
        Assert.Equal("Reset HEAD to Revision", s.Window.Title);
        Assert.Equal("Move HEAD to the selected revision", s.Vm.Description);
        Assert.Equal("HEAD", Find<RefView>(s.Window, "Branch").Text);
        await s.CloseAsync();
        owner.Close();
    }

    [AvaloniaFact]
    public async Task Merge_dialog_option_and_preview()
    {
        using var f = await RepoAsync();
        f.Repo.Git("checkout", "-q", "b");
        f.Repo.Commit("b1", 1, ("b.txt", "b\n"));
        f.Repo.Git("checkout", "-q", "main");
        await f.RefreshAsync();
        GerGit.Settings.Set("lastUsed.merge.kind", "Squash"); // stored by older versions: ignored
        var owner = Owner();
        var s = await OpenAsync<MergeViewModel>(f, owner, () => f.Ops.MergeAsync(f.Ref("refs/heads/b")));
        GerGit.Settings.Set<string?>("lastUsed.merge.kind", null);
        var w = s.Window;
        Assert.Equal(("Merge Branch", 560, "Merge"), (w.Title, w.Width, Find<Button>(w, "SubmitButton").Content));
        Assert.Equal(("b", "main"), (Find<RefView>(w, "Source").Text, Find<RefView>(w, "Destination").Text));
        Assert.Equal(1, Find<ComboBox>(w, "OptionBox").SelectedIndex); // always "No Fast-Forward"
        // The closed box shows the selected row from its left edge: "No Fast-Forward" is whole (was cut to ":-Forward").
        Assert.Equal(0, Find<ComboBox>(w, "OptionBox").GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Rectangle>().Single().Bounds.X);
        await s.Vm.Preview;
        Settle(w);
        Assert.Equal("Merge can be done without conflicts", Find<TextBlock>(w, "FooterText").Text);
        Assert.Equal("circle-check", Find<LucideIcon>(w, "FooterIcon").Icon);
        await s.CloseAsync();
        owner.Close();
    }

    static string Line(string name, string status, string note = "") => HookChecksTests.Line(name, status, note);

    /// <summary>A pre-merge-commit hook with this sh body.</summary>
    static void WriteHook(RefFixture f, string body)
    {
        var hooks = Path.Combine(f.Repo.Path, ".git", "hooks");
        Directory.CreateDirectory(hooks);
        var hook = Path.Combine(hooks, "pre-merge-commit");
        File.WriteAllText(hook, "#!/bin/sh\n" + body);
        File.SetUnixFileMode(hook, (UnixFileMode)0b111_101_101);
        f.Repo.Git("config", "core.hooksPath", hooks); // a global hooksPath must not hide it
    }

    /// <summary>A hook that prints pre-commit's lines (the running one without a line break) and, like a test runner
    /// under pre-commit, writes its per-item progress to /dev/tty while "running", then the status;
    /// <paramref name="red"/> = test_b.py fails and pytest's summary follows.</summary>
    static void WriteMergeHook(RefFixture f, bool red)
    {
        var end = red
            ? """
              printf 'Failed\n- hook id: pytest\n- exit code: 1\n\n'
              printf '=================== 1 failed, 1 passed in 1.30s ===================\n'
              exit 1
              """
            : "printf 'Passed\\n'\n";
        WriteHook(f, $$"""
            printf '{{Line("ruff format", "Passed")}}\n'
            printf '{{Line("isort", "Skipped", "(no files to check)")}}\n'
            printf '{{HookChecksTests.Dots("pytest")}}'
            printf 'test_a.py ' > /dev/tty
            sleep 0.3
            printf '✓ test_b.py ' > /dev/tty
            sleep 1
            printf '{{(red ? "✗" : "✓")}} ' > /dev/tty
            {{end}}
            """);
    }

    static void BranchCommit(RefFixture f)
    {
        f.Repo.Git("checkout", "-q", "b");
        f.Repo.Commit("b1", 1, ("b.txt", "b\n"));
        f.Repo.Git("checkout", "-q", "main");
    }

    static HookRow? Pytest(Shown<MergeViewModel> s) => s.Vm.Checks.Hooks.FirstOrDefault(h => h.Name == "pytest");

    /// <summary>Submits the Merge dialog; waits until <paramref name="ready"/> (if given) and then for the job's end.</summary>
    static async Task SubmitAsync(Shown<MergeViewModel> s, Func<bool>? ready = null, Action? whenReady = null)
    {
        var run = s.Vm.SubmitAsync();
        if (ready is not null)
        {
            for (int i = 0; i < 2000 && !ready(); i++)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(5);
            }
            Assert.True(ready());
            Settle(s.Window);
            Assert.True(s.Window.GetVisualDescendants().OfType<ChecksView>().Single().IsEffectivelyVisible);
            whenReady?.Invoke();
        }
        while (!run.IsCompleted)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
        await run;
        Settle(s.Window);
    }

    /// <summary>Waits until pytest shows as running (the pending "pytest....." line: what runs now) with test_b.py running.</summary>
    static Task MergeWithHookAsync(Shown<MergeViewModel> s) =>
        SubmitAsync(s, () => Pytest(s) is { IsRunning: true } m && m.Items.Any(x => x is { Name: "test_b.py", IsRunning: true }));

    static bool Alive(int pid)
    {
        try
        {
            var stat = File.ReadAllText($"/proc/{pid}/stat");
            return stat[stat.LastIndexOf(')') + 2] != 'Z';
        }
        catch (IOException) { return false; }
    }

    static List<string> Texts(Window w) =>
        Find<ItemsControl>(w, "HookList").GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible)
            .Select(t => t.Text ?? "").Where(t => t.Length > 0).ToList();

    [AvaloniaFact]
    public async Task Merge_checks_panel_shows_the_hooks_and_items_and_stays_open_after_success()
    {
        using var f = await RepoAsync();
        BranchCommit(f); // --no-ff (preselected): the merge makes a commit, so the hook runs
        WriteMergeHook(f, red: false);
        await f.RefreshAsync();
        var owner = Owner();
        var s = await OpenAsync<MergeViewModel>(f, owner, () => f.Ops.MergeAsync(f.Ref("refs/heads/b")));
        var w = s.Window;
        Assert.False(w.GetVisualDescendants().OfType<ChecksView>().Single().IsVisible); // no hook ran yet: no panel
        await MergeWithHookAsync(s);

        Assert.Single(owner.OwnedWindows); // stays open on success
        Assert.Null(s.Vm.Result);
        Assert.Empty(f.Errors);
        Assert.Equal([("ruff format", CheckState.Passed), ("isort", CheckState.Skipped), ("pytest", CheckState.Passed)],
            s.Vm.Checks.Hooks.Select(h => (h.Name, h.State)));
        Assert.Equal([("test_a.py", CheckState.Passed), ("test_b.py", CheckState.Passed)], s.Vm.Checks.Hooks[2].Items.Select(m => (m.Name, m.State)));
        Assert.Equal(["ruff format", "isort", "pytest", "test_a.py", "test_b.py"], Texts(w));
        Assert.Equal((DialogStatus.Success, "Merged, checks passed"), (s.Vm.Status, s.Vm.StatusText));
        Assert.False(Find<Button>(w, "SubmitButton").IsVisible); // Merge is gone, Cancel is Close
        Assert.False(s.Vm.CanSubmit);
        Assert.False(Find<ComboBox>(w, "OptionBox").IsEffectivelyEnabled); // the body shows a finished merge
        Assert.Equal("Close", Find<Button>(w, "CancelButton").Content);
        Assert.Contains("pytest.....", s.Vm.Checks.Output);
        Assert.Equal(2, f.Repo.Git("log", "-1", "--format=%P").Split(' ').Length); // merged
        await s.CloseAsync();
        Assert.Empty(owner.OwnedWindows);
        owner.Close();
    }

    [AvaloniaFact]
    public async Task Merge_checks_panel_stays_red_after_a_failing_hook_without_an_error_window()
    {
        using var f = await RepoAsync();
        BranchCommit(f);
        WriteMergeHook(f, red: true);
        await f.RefreshAsync();
        var owner = Owner();
        var s = await OpenAsync<MergeViewModel>(f, owner, () => f.Ops.MergeAsync(f.Ref("refs/heads/b")));
        var w = s.Window;
        await MergeWithHookAsync(s);

        Assert.Single(owner.OwnedWindows);
        Assert.Empty(f.Errors); // the panel shows the failure: no second window
        var pytest = s.Vm.Checks.Hooks[2];
        Assert.Equal(("pytest", CheckState.Failed), (pytest.Name, pytest.State));
        Assert.Equal([("test_a.py", CheckState.Passed), ("test_b.py", CheckState.Failed)], pytest.Items.Select(m => (m.Name, m.State)));
        Assert.Equal("1 failed, 1 passed in 1.30s", pytest.Note);
        Assert.Equal(["ruff format", "isort", "pytest", "test_a.py", "test_b.py", "1 failed, 1 passed in 1.30s"], Texts(w));
        Assert.Contains(Find<ItemsControl>(w, "HookList").GetVisualDescendants().OfType<LucideIcon>(),
            i => i.Classes.Contains("failed") && i.Icon == "circle-x" && i.IsEffectivelyVisible);
        Assert.Equal((DialogStatus.Error, "Checks failed: pytest — merge not committed (see Local Changes)"), (s.Vm.Status, s.Vm.StatusText));
        Assert.Equal("Close", Find<Button>(w, "CancelButton").Content);
        Assert.True(File.Exists(Path.Combine(f.Repo.Path, ".git", "MERGE_HEAD"))); // git stopped before the merge commit
        Assert.True(f.Tab.IsLocalChangesView);
        await s.CloseAsync();
        owner.Close();
    }

    [AvaloniaFact]
    public async Task Merge_with_a_terminal_never_waits_for_an_editor_or_input()
    {
        using var f = await RepoAsync();
        BranchCommit(f);
        var marker = Path.Combine(f.Repo.Path, ".git", "editor-ran");
        var editor = Path.Combine(f.Repo.Path, ".git", "slow-editor");
        File.WriteAllText(editor, $"#!/bin/sh\ntouch '{marker}'\nsleep 30\n");
        File.SetUnixFileMode(editor, (UnixFileMode)0b111_101_101);
        f.Repo.Git("config", "core.editor", editor);
        WriteHook(f, $"printf '{Line("x", "Passed")}\\n'\n" +
                     "if read -r line; then echo hook-got-input; else echo hook-stdin-eof; fi\n[ -t 0 ] || echo hook-stdin-no-tty\n");
        await f.RefreshAsync();
        var autoEdit = Environment.GetEnvironmentVariable("GIT_MERGE_AUTOEDIT");
        Environment.SetEnvironmentVariable("GIT_MERGE_AUTOEDIT", "yes"); // would ask for the message where git may
        try
        {
            var owner = Owner();
            var s = await OpenAsync<MergeViewModel>(f, owner, () => f.Ops.MergeAsync(f.Ref("refs/heads/b")));
            var run = s.Vm.SubmitAsync();
            for (int i = 0; i < 2000 && !run.IsCompleted; i++)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(5);
            }
            Assert.True(run.IsCompleted, "the merge hangs");
            await run;
            Assert.False(File.Exists(marker));
            Assert.Equal("Merge branch 'b'", f.Repo.Git("log", "-1", "--format=%s"));
            Assert.Contains("hook-stdin-eof\nhook-stdin-no-tty\n", s.Vm.Checks.Output); // git gives hooks no terminal input
            Assert.Equal((DialogStatus.Success, "Merged, checks passed"), (s.Vm.Status, s.Vm.StatusText));
            await s.CloseAsync();
            owner.Close();
        }
        finally { Environment.SetEnvironmentVariable("GIT_MERGE_AUTOEDIT", autoEdit); }
    }

    [AvaloniaFact]
    public async Task A_hook_reading_the_terminal_gets_eof_at_once()
    {
        using var f = await RepoAsync();
        BranchCommit(f);
        WriteHook(f, $"printf '{Line("x", "Passed")}\\n'\nif read -r line </dev/tty; then echo tty-input; else echo tty-eof; fi\n");
        await f.RefreshAsync();
        var owner = Owner();
        var s = await OpenAsync<MergeViewModel>(f, owner, () => f.Ops.MergeAsync(f.Ref("refs/heads/b")));
        var run = s.Vm.SubmitAsync();
        for (int i = 0; i < 2000 && !run.IsCompleted; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
        Assert.True(run.IsCompleted, "the merge waits for terminal input"); // ended without Cancel
        await run;
        Assert.Contains("tty-eof", s.Vm.Checks.Output);
        Assert.Equal((DialogStatus.Success, "Merged, checks passed"), (s.Vm.Status, s.Vm.StatusText));
        await s.CloseAsync();
        owner.Close();
    }

    /// <summary>With the terminal, git is the session leader: when it ends, the kernel sends SIGHUP to what is left of its
    /// group, as in a terminal. Without one nothing does; with one a job that ignores SIGHUP (nohup, once it
    /// has started) survives.</summary>
    [AvaloniaTheory]
    [InlineData(false, "", true)]
    [InlineData(true, "", false)]
    [InlineData(true, "nohup ", true)]
    public async Task A_background_job_of_a_post_merge_hook_and_the_hangup(bool terminal, string prefix, bool survives)
    {
        using var f = await RepoAsync();
        BranchCommit(f);
        WriteHook(f, $"printf '{Line("x", "Passed")}\\n'\n");
        var git = Path.Combine(f.Repo.Path, ".git");
        var postMerge = Path.Combine(git, "hooks", "post-merge");
        // Detached from the pipes, so the merge does not wait for it; it outlives git by a second.
        File.WriteAllText(postMerge, $"#!/bin/sh\n{prefix}sh -c 'sleep 1; echo alive > .git/bg-alive' </dev/null >/dev/null 2>&1 &\nsleep 0.3\n");
        File.SetUnixFileMode(postMerge, (UnixFileMode)0b111_101_101);
        await f.RefreshAsync();
        GitRunner.PtyDisabled = !terminal;
        try
        {
            var owner = Owner();
            var s = await OpenAsync<MergeViewModel>(f, owner, () => f.Ops.MergeAsync(f.Ref("refs/heads/b")));
            await SubmitAsync(s);
            for (int i = 0; i < 50 && !File.Exists(Path.Combine(git, "bg-alive")); i++) await Task.Delay(50);
            Assert.Equal(survives, File.Exists(Path.Combine(git, "bg-alive")));
            await s.CloseAsync();
            owner.Close();
        }
        finally { GitRunner.PtyDisabled = false; }
    }

    [AvaloniaFact]
    public async Task Merge_without_a_pseudo_terminal_runs_as_before()
    {
        using var f = await RepoAsync();
        BranchCommit(f);
        WriteHook(f, $"printf '{Line("pytest", "Passed")}\\n'\nprintf 'test_a.py ' 2>/dev/null > /dev/tty || echo no-terminal\n");
        await f.RefreshAsync();
        GitRunner.PtyDisabled = true;
        try
        {
            var owner = Owner();
            var s = await OpenAsync<MergeViewModel>(f, owner, () => f.Ops.MergeAsync(f.Ref("refs/heads/b")));
            await SubmitAsync(s);
            Assert.Contains("no-terminal", s.Vm.Checks.Output);
            Assert.Empty(Pytest(s)!.Items);
            Assert.Equal((DialogStatus.Success, "Merged, checks passed"), (s.Vm.Status, s.Vm.StatusText));
            Assert.Equal(2, f.Repo.Git("log", "-1", "--format=%P").Split(' ').Length);
            await s.CloseAsync();
            owner.Close();
        }
        finally { GitRunner.PtyDisabled = false; }
    }

    [AvaloniaFact]
    public async Task Terminal_text_that_is_no_item_progress_is_shown_raw()
    {
        using var f = await RepoAsync();
        BranchCommit(f);
        WriteHook(f, $"printf '{Line("pytest", "Passed")}\\n'\nprintf '\\033[1mhello\\033[0m\\r\\nsomething else\\n' > /dev/tty\n");
        await f.RefreshAsync();
        var owner = Owner();
        var s = await OpenAsync<MergeViewModel>(f, owner, () => f.Ops.MergeAsync(f.Ref("refs/heads/b")));
        await SubmitAsync(s);
        Assert.Empty(Pytest(s)!.Items); // no invented rows
        Assert.EndsWith("[terminal]\nhello\nsomething else\n", s.Vm.Checks.Output);
        await s.CloseAsync();
        owner.Close();
    }

    [AvaloniaFact]
    public async Task Merge_failing_after_green_hooks_closes_and_shows_the_git_error_window()
    {
        using var f = await RepoAsync();
        BranchCommit(f);
        WriteHook(f, $"printf '{Line("x", "Passed")}\\n'\n");
        f.Repo.Git("config", "commit.gpgsign", "true"); // the commit fails after the hook: signing
        f.Repo.Git("config", "gpg.program", "false");
        await f.RefreshAsync();
        var owner = Owner();
        var s = await OpenAsync<MergeViewModel>(f, owner, () => f.Ops.MergeAsync(f.Ref("refs/heads/b")));
        await SubmitAsync(s);

        Assert.Equal(("x", CheckState.Passed), (s.Vm.Checks.Hooks.Single().Name, s.Vm.Checks.Hooks.Single().State));
        Assert.False(s.Vm.KeepOpen);
        Assert.True(s.Vm.Result); // closed as before
        Assert.Empty(owner.OwnedWindows);
        Assert.Single(f.Errors); // the Git Error window, once
        owner.Close();
    }

    [AvaloniaFact]
    public async Task Merge_hook_ending_without_a_status_line_turns_red_instead_of_spinning()
    {
        using var f = await RepoAsync();
        BranchCommit(f);
        // pre-commit died inside a hook: no status, git's message follows the dots on the same line.
        WriteHook(f, $"printf '{Line("ruff", "Passed")}\\n{HookChecksTests.Dots("pytest")}'\nsleep 1\nexit 1\n");
        await f.RefreshAsync();
        var owner = Owner();
        var s = await OpenAsync<MergeViewModel>(f, owner, () => f.Ops.MergeAsync(f.Ref("refs/heads/b")));
        await SubmitAsync(s, () => Pytest(s) is { IsRunning: true });

        Assert.Equal([("ruff", CheckState.Passed), ("pytest", CheckState.Failed)], s.Vm.Checks.Hooks.Select(h => (h.Name, h.State)));
        Assert.DoesNotContain(s.Window.GetVisualDescendants().OfType<BusySpinner>(), b => b.IsEffectivelyVisible);
        Assert.Equal((DialogStatus.Error, "Checks failed: pytest — merge not committed (see Local Changes)"), (s.Vm.Status, s.Vm.StatusText));
        Assert.Single(owner.OwnedWindows);
        Assert.Empty(f.Errors);
        await s.CloseAsync();
        owner.Close();
    }

    [AvaloniaFact]
    public async Task Merge_cancelled_during_the_hooks_stays_open_and_says_what_git_left()
    {
        using var f = await RepoAsync();
        BranchCommit(f);
        // sleep in the background (SIGINT ignored there): it ends with the group after git (SIGHUP or the final SIGKILL).
        WriteHook(f, $"printf '{Line("ruff", "Passed")}\\n{HookChecksTests.Dots("pytest")}'\nprintf 'test_a.py ' > /dev/tty\n" +
                     "echo $$ > .git/hook-pid\nsleep 30 &\necho $! > .git/sleep-pid\nwait\n");
        await f.RefreshAsync();
        var owner = Owner();
        var s = await OpenAsync<MergeViewModel>(f, owner, () => f.Ops.MergeAsync(f.Ref("refs/heads/b")));
        var git = Path.Combine(f.Repo.Path, ".git");
        await SubmitAsync(s, () => Pytest(s) is { IsRunning: true } && File.Exists(Path.Combine(git, "sleep-pid")), () => s.Vm.Cancel());

        // What git leaves: only the hook was interrupted; git stopped like after a failed hook. The merge is in progress
        // (MERGE_HEAD), HEAD unchanged, its result staged.
        Assert.True(File.Exists(Path.Combine(git, "MERGE_HEAD")));
        Assert.Equal("init", f.Repo.Git("log", "-1", "--format=%s"));
        Assert.Equal("A  b.txt", f.Repo.Git("status", "--porcelain"));
        Assert.Single(owner.OwnedWindows);
        Assert.Empty(f.Errors);
        Assert.Equal(DialogStatus.Warning, s.Vm.Status);
        Assert.Equal(MergeInProgressText, s.Vm.StatusText);
        Assert.Equal(CheckState.Failed, Pytest(s)!.State);
        Assert.Equal(("test_a.py", CheckState.Failed), (Pytest(s)!.Items.Single().Name, Pytest(s)!.Items.Single().State)); // via the pty
        Assert.Equal("Close", Find<Button>(s.Window, "CancelButton").Content);
        int hookPid = int.Parse(File.ReadAllText(Path.Combine(git, "hook-pid"))), sleepPid = int.Parse(File.ReadAllText(Path.Combine(git, "sleep-pid")));
        for (int i = 0; i < 100 && (Alive(hookPid) || Alive(sleepPid)); i++) await Task.Delay(50);
        Assert.False(Alive(hookPid)); // the process group is gone
        Assert.False(Alive(sleepPid));
        await s.CloseAsync();
        owner.Close();
    }

    const string MergeInProgressText =
        "Cancelled during checks — not merged; the merge is in progress, its result staged in Local Changes (commit it or Abort)";

    [AvaloniaFact]
    public async Task Merge_cancelled_during_a_hook_lets_it_restore_the_unstaged_changes()
    {
        using var f = await RepoAsync();
        BranchCommit(f);
        f.Repo.Commit("c", 2, ("c.txt", "c\n"));
        await f.RefreshAsync();
        f.Repo.Write("c.txt", "c unstaged\n"); // not touched by the merge
        // Like pre-commit: keep the unstaged change as a patch, check the file out, and apply the patch back on SIGINT.
        WriteHook(f, $"git diff -- c.txt > .git/stash.patch\ngit checkout -- c.txt\n" +
                     "trap 'sleep 1; git apply .git/stash.patch; exit 130' INT\n" +
                     $"printf '{HookChecksTests.Dots("pytest")}'\necho ready > .git/hook-ready\nsleep 30\n");
        var owner = Owner();
        var s = await OpenAsync<MergeViewModel>(f, owner, () => f.Ops.MergeAsync(f.Ref("refs/heads/b")));
        var git = Path.Combine(f.Repo.Path, ".git");
        await SubmitAsync(s, () => Pytest(s) is { IsRunning: true } && File.Exists(Path.Combine(git, "hook-ready")), () => s.Vm.Cancel());

        Assert.Equal("c unstaged\n", File.ReadAllText(Path.Combine(f.Repo.Path, "c.txt"))); // restored, not lost
        Assert.Equal("c", f.Repo.Git("log", "-1", "--format=%s")); // not merged
        Assert.True(File.Exists(Path.Combine(git, "MERGE_HEAD")));
        Assert.Equal((DialogStatus.Warning, MergeInProgressText), (s.Vm.Status, s.Vm.StatusText));
        await s.CloseAsync();
        owner.Close();
    }

    [AvaloniaFact]
    public async Task Merge_cancelled_in_a_post_merge_hook_counts_as_merged()
    {
        using var f = await RepoAsync();
        BranchCommit(f);
        WriteHook(f, $"printf '{Line("x", "Passed")}\\n'\n");
        var git = Path.Combine(f.Repo.Path, ".git");
        var postMerge = Path.Combine(git, "hooks", "post-merge"); // runs after the commit; blocks until the cancel
        File.WriteAllText(postMerge, "#!/bin/sh\nsleep 30 &\necho $! > .git/post-merge-pid\ntrap 'kill $! 2>/dev/null; exit 130' INT\nwait\n");
        File.SetUnixFileMode(postMerge, (UnixFileMode)0b111_101_101);
        await f.RefreshAsync();
        var main = f.Head;
        var owner = Owner();
        var s = await OpenAsync<MergeViewModel>(f, owner, () => f.Ops.MergeAsync(f.Ref("refs/heads/b")));
        await SubmitAsync(s, () => s.Vm.Checks.HasHooks && File.Exists(Path.Combine(git, "post-merge-pid")), () => s.Vm.Cancel());

        Assert.Equal(main, f.Repo.Git("log", "-1", "--format=%P").Split(' ')[0]); // the merge commit exists
        Assert.Equal(2, f.Repo.Git("log", "-1", "--format=%P").Split(' ').Length);
        Assert.DoesNotContain("not merged", s.Vm.StatusText);
        Assert.Equal((DialogStatus.Success, "Merged, checks passed"), (s.Vm.Status, s.Vm.StatusText));
        Assert.Single(owner.OwnedWindows);
        Assert.Empty(f.Errors);
        await s.CloseAsync();
        owner.Close();
    }

    [Theory]
    [InlineData("Passed", CheckState.Skipped)]
    [InlineData("Failed", CheckState.Failed)]
    public void Item_still_running_at_the_end_is_skipped_under_a_passed_hook_else_red(string status, CheckState expected)
    {
        var checks = new ChecksViewModel();
        const string progress = "test_a.py ";
        checks.Update("", 0, HookChecksTests.Dots("pytest"), progress);
        Assert.Equal(CheckState.Running, checks.Hooks.Single().Items.Single().State);
        checks.Update(Line("pytest", status) + "\n", 0, "", progress, final: true);
        Assert.Equal(expected, checks.Hooks.Single().Items.Single().State);
    }

    [Fact]
    public void Item_progress_of_a_hook_never_seen_running_goes_to_the_last_hook()
    {
        var checks = new ChecksViewModel();
        checks.Update(Line("ruff", "Passed") + "\n" + Line("pytest", "Failed") + "\n", 0, "", "test_a.py ✓ test_b.py ✗ ", final: true);
        Assert.Empty(checks.Hooks[0].Items);
        Assert.Equal([("test_a.py", CheckState.Passed), ("test_b.py", CheckState.Failed)], checks.Hooks[1].Items.Select(m => (m.Name, m.State)));
    }

    /// <summary>Superproject with a submodule: main records s1, b records s2, so the submodule update after the merge
    /// checks out s2 and runs the submodule's own post-checkout hook (<paramref name="subHook"/>).</summary>
    static async Task<RefFixture> SubmoduleRepoAsync(TempRepo sub, string subHook)
    {
        var f = new RefFixture();
        var s1 = sub.Commit("s1", 0, ("s.txt", "1\n"));
        var s2 = sub.Commit("s2", 1, ("s.txt", "2\n"));
        f.Repo.Git("-c", "protocol.file.allow=always", "submodule", "add", "-q", sub.Path, "sub");
        f.Repo.GitEnv([], "-C", "sub", "checkout", "-q", s1);
        f.Repo.Commit("p1", 0);
        f.Repo.Git("checkout", "-q", "-b", "b");
        f.Repo.GitEnv([], "-C", "sub", "checkout", "-q", s2);
        f.Repo.Commit("p2", 1);
        f.Repo.Git("checkout", "-q", "main");
        f.Repo.GitEnv([], "-C", "sub", "checkout", "-q", s1);
        var hook = Path.Combine(f.Repo.Path, ".git", "modules", "sub", "hooks", "post-checkout");
        File.WriteAllText(hook, "#!/bin/sh\n" + subHook);
        File.SetUnixFileMode(hook, (UnixFileMode)0b111_101_101);
        await f.OpenAsync();
        return f;
    }

    [AvaloniaFact]
    public async Task Merge_cancelled_during_the_submodule_update_after_the_commit_shows_merged()
    {
        using var sub = new TempRepo();
        // The submodule update stalls: the cancel lands after `git merge` ended with 0.
        using var f = await SubmoduleRepoAsync(sub, "sleep 30 &\necho $! > \"$GERGIT_REPO_PATH/.git/sub-sleep-pid\"\nwait\n");
        var git = Path.Combine(f.Repo.Path, ".git");
        WriteHook(f, $"printf '{Line("x", "Passed")}\\n'\n");
        var owner = Owner();
        var s = await OpenAsync<MergeViewModel>(f, owner, () => f.Ops.MergeAsync(f.Ref("refs/heads/b")));
        await SubmitAsync(s, () => s.Vm.Checks.HasHooks && File.Exists(Path.Combine(git, "sub-sleep-pid")), () => s.Vm.Cancel());

        Assert.Equal(2, f.Repo.Git("log", "-1", "--format=%P").Split(' ').Length); // git committed the merge
        Assert.Equal((DialogStatus.Success, "Merged, checks passed"), (s.Vm.Status, s.Vm.StatusText));
        Assert.Single(owner.OwnedWindows);
        Assert.Empty(f.Errors);
        await s.CloseAsync();
        owner.Close();
    }

    [AvaloniaFact]
    public async Task Merge_committed_with_a_red_post_merge_hook_then_a_failing_submodule_update_shows_the_git_error()
    {
        using var sub = new TempRepo();
        // post-checkout's exit status is checkout's: the submodule update fails.
        using var f = await SubmoduleRepoAsync(sub, "echo submodule says no\nexit 1\n");
        WriteHook(f, $"printf '{Line("x", "Passed")}\\n'\n");
        var postMerge = Path.Combine(f.Repo.Path, ".git", "hooks", "post-merge"); // red line, but exit 0: git merge succeeds
        File.WriteAllText(postMerge, $"#!/bin/sh\nprintf '{Line("late", "Failed")}\\n'\n");
        File.SetUnixFileMode(postMerge, (UnixFileMode)0b111_101_101);
        var owner = Owner();
        var s = await OpenAsync<MergeViewModel>(f, owner, () => f.Ops.MergeAsync(f.Ref("refs/heads/b")));
        await SubmitAsync(s);

        Assert.Equal(2, f.Repo.Git("log", "-1", "--format=%P").Split(' ').Length); // merged
        Assert.Equal([("x", CheckState.Passed), ("late", CheckState.Failed)], s.Vm.Checks.Hooks.Select(h => (h.Name, h.State)));
        Assert.False(s.Vm.KeepOpen); // the submodule failure keeps its usual way
        Assert.Empty(owner.OwnedWindows);
        Assert.Contains("submodule says no", Assert.Single(f.Errors).Text);
        owner.Close();
    }

    [AvaloniaFact]
    public async Task Squash_with_hook_lines_says_staged_and_focuses_the_subject_after_the_dialog_closed()
    {
        using var f = await RepoAsync();
        BranchCommit(f);
        WriteHook(f, ""); // sets core.hooksPath; squash makes no commit, so pre-merge-commit does not run
        var postMerge = Path.Combine(f.Repo.Path, ".git", "hooks", "post-merge");
        File.WriteAllText(postMerge, $"#!/bin/sh\nprintf '{Line("x", "Passed")}\\n'\n");
        File.SetUnixFileMode(postMerge, (UnixFileMode)0b111_101_101);
        await f.RefreshAsync();
        var focus = new List<LocalChangesFocus>();
        f.Tab.LocalChanges.FocusRequested += focus.Add;
        var owner = Owner();
        var s = await OpenAsync<MergeViewModel>(f, owner, () => f.Ops.MergeAsync(f.Ref("refs/heads/b")));
        s.Vm.Kind = MergeKind.Squash;
        await SubmitAsync(s);

        Assert.Equal((DialogStatus.Success, "Squash staged in Local Changes, checks passed"), (s.Vm.Status, s.Vm.StatusText));
        Assert.Equal("A  b.txt", f.Repo.Git("status", "--porcelain"));
        Assert.True(f.Tab.IsLocalChangesView);
        Assert.DoesNotContain(LocalChangesFocus.Subject, focus); // not while the modal dialog is open
        await s.CloseAsync();
        Assert.Equal(LocalChangesFocus.Subject, focus[^1]);
        owner.Close();
    }

    [AvaloniaFact]
    public async Task A_long_checks_panel_stays_within_the_height_cap_and_scrolls()
    {
        using var f = await RepoAsync();
        BranchCommit(f);
        var hooks = string.Concat(Enumerable.Range(1, 80).Select(i => Line($"hook {i}", "Passed") + "\\n"));
        WriteHook(f, $"printf '{hooks}{Line("pytest", "Failed")}\\n'\nexit 1\n");
        await f.RefreshAsync();
        var owner = Owner();
        var s = await OpenAsync<MergeViewModel>(f, owner, () => f.Ops.MergeAsync(f.Ref("refs/heads/b")));
        var w = s.Window;
        Assert.False(double.IsInfinity(w.MaxHeight)); // capped to the screen's working area (headless has one)
        await SubmitAsync(s);
        w.GetVisualDescendants().OfType<Expander>().Single().IsExpanded = true;
        Settle(w);
        Settle(w);

        Assert.True(w.Bounds.Height <= w.MaxHeight, $"{w.Bounds.Height} > {w.MaxHeight}");
        var close = Find<Button>(w, "CancelButton");
        Assert.True(close.TranslatePoint(new Point(0, close.Bounds.Height), w)!.Value.Y <= w.Bounds.Height); // still reachable
        var scroll = Find<ItemsControl>(w, "HookList").GetVisualAncestors().OfType<ScrollViewer>().First();
        Assert.True(scroll.Extent.Height > scroll.Viewport.Height); // the rows scroll
        await s.CloseAsync();
        owner.Close();
    }

    [AvaloniaFact]
    public async Task Create_tag_dialog_and_tag_details_window()
    {
        using var f = await RepoAsync(origin: true);
        f.Repo.Git("tag", "-a", "v1", "-m", "Release one");
        await f.RefreshAsync();
        var owner = Owner();
        var s = await OpenAsync<CreateTagViewModel>(f, owner, () => f.Ops.CreateTagAsync());
        var w = s.Window;
        Assert.Equal(("Create Tag", 590, "Create"), (w.Title, w.Width, Find<Button>(w, "SubmitButton").Content));
        Assert.Equal(("Enter tag name", "Enter tag message (optional)"),
            (Find<RefNameBox>(w, "NameBox").PlaceholderText, Find<TextBox>(w, "MessageBox").PlaceholderText));
        Assert.Equal("tag", Find<RefNameBox>(w, "NameBox").Icon);
        Assert.Equal("Push", Find<CheckBox>(w, "PushBox").Content);
        Find<RefNameBox>(w, "NameBox").Text = "-d";
        Settle(w);
        Assert.Equal("Name cannot begin with '-'", Find<TextBlock>(w, "FooterText").Text);
        Find<RefNameBox>(w, "NameBox").Text = "V1";
        Settle(w);
        Assert.Equal("Tag 'V1' already exists", Find<TextBlock>(w, "FooterText").Text);
        Find<CheckBox>(w, "PushBox").IsChecked = true;
        Settle(w);
        Assert.Equal("Create and Push", Find<Button>(w, "SubmitButton").Content);
        await s.CloseAsync();

        f.Repo.Git("remote", "add", "second", f.Bare!.Path);
        await f.RefreshAsync();
        s = await OpenAsync<CreateTagViewModel>(f, owner, () => f.Ops.CreateTagAsync());
        Assert.Equal("Push to all remotes", Find<CheckBox>(s.Window, "PushBox").Content);
        await s.CloseAsync();

        var d = await OpenAsync<TagDetailsViewModel>(f, owner, () => f.Ops.ShowTagDetailsAsync(f.Ref("refs/tags/v1")));
        w = d.Window;
        Assert.Equal(("Tag Details", 650.0, 350.0, true), (w.Title, w.Width, w.Height, w.CanResize));
        Assert.False(Find<Button>(w, "SubmitButton").IsVisible);
        Assert.Equal("Close", Find<Button>(w, "CancelButton").Content);
        Assert.Equal(("Test User", "test@example.com"), (Find<SelectableTextBlock>(w, "Tagger").Text, Find<SelectableTextBlock>(w, "Email").Text));
        Assert.Equal("Release one", Find<SelectableTextBlock>(w, "Message").Text);
        Assert.Equal("v1", Find<RefView>(w, "TagRow").Text);
        await d.CloseAsync();
        owner.Close();
    }
}
