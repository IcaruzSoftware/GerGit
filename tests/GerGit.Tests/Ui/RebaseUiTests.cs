using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GerGit.Controls;
using GerGit.Core;
using GerGit.Core.Git;
using GerGit.Core.Rebase;
using GerGit.Tests.Core;
using GerGit.ViewModels;
using GerGit.ViewModels.Dialogs;
using GerGit.ViewModels.Dialogs.Refs;
using GerGit.Views;
using GerGit.Views.Dialogs;

namespace GerGit.Tests.Ui;

/// <summary>
/// the Rebase dialog, the interactive rebase window (driven through its view model and headless), the quick
/// actions and the menu items, against real repositories. The interactive rebase uses the askpass socket server
/// (editor helper), so these tests run in that collection.
/// </summary>
[Collection("Askpass")]
public sealed class RebaseUiTests : IDisposable
{
    static readonly TimeSpan Limit = TimeSpan.FromSeconds(60);
    static readonly string[] Keys =
    [
        RebaseViewModel.UpdateRefsKey, RebaseViewModel.AutostashKey, InteractiveRebaseViewModel.UpdateRefsKey,
        InteractiveRebaseViewModel.BackupKey,
    ];

    readonly Func<InteractiveRebaseViewModel, Task> _show = InteractiveRebaseViewModel.Show;
    readonly RefFixture _f = new();
    readonly List<DialogViewModel> _dialogs = [];

    /// <summary>Answers every dialog of the tab (Rebase dialog, cancel confirmation); default: cancel.</summary>
    Func<DialogViewModel, Task<bool>> _answer = _ => Task.FromResult(false);

    public RebaseUiTests()
    {
        Askpass.Start();
        foreach (var k in Keys) Settings.Set<string?>(k, null);
    }

    public void Dispose()
    {
        InteractiveRebaseViewModel.Show = _show;
        Askpass.Stop();
        _f.Dispose();
        foreach (var k in Keys) Settings.Set<string?>(k, null);
    }

    TempRepo Repo => _f.Repo;
    RebaseOps Ops => new(_f.Tab);

    async Task OpenAsync()
    {
        await _f.OpenAsync();
        _f.Tab.ShowDialog = d =>
        {
            _dialogs.Add(d);
            return _answer(d);
        };
    }

    /// <summary>The window is replaced by <paramref name="drive"/>, which gets the loaded view model.</summary>
    static void Drive(Func<InteractiveRebaseViewModel, Task> drive) =>
        InteractiveRebaseViewModel.Show = async vm =>
        {
            await vm.Loaded.WaitAsync(Limit);
            await drive(vm);
        };

    static void Drive(Action<InteractiveRebaseViewModel> drive) => Drive(vm =>
    {
        drive(vm);
        return Task.CompletedTask;
    });

    /// <summary>Full messages, newest first.</summary>
    string[] Messages() =>
        Repo.Git("log", "--format=%B%x1e").Split('\x1e', StringSplitOptions.RemoveEmptyEntries)
            .Select(m => m.Trim()).Where(m => m.Length > 0).ToArray();

    string Files(string rev = "HEAD") => Repo.Git("ls-tree", "--name-only", rev).Replace('\n', ' ');
    bool InRebase => Directory.Exists(Path.Combine(Repo.Path, ".git", "rebase-merge"));
    static string[] Shas(InteractiveRebaseViewModel vm) => vm.Items.Select(i => i.Row.Sha).ToArray();
    static string Actions(InteractiveRebaseViewModel vm) => string.Join(" ", vm.Items.Select(i => i.Choice.Title));

    /// <summary>one (f1) .. four (f4) on main; returns the SHAs oldest first.</summary>
    string[] Four() => Enumerable.Range(1, 4).Select(i => Repo.Commit(new[] { "one", "two", "three", "four" }[i - 1], i, ($"f{i}", $"{i}\n"))).ToArray();

    static MenuEntry Find(IEnumerable<MenuEntry> menu, string header) =>
        menu.SelectMany(e => e.Children is { } c ? c.Prepend(e) : [e]).First(e => e.Header == header);

    static bool Has(IEnumerable<MenuEntry> menu, string header) =>
        menu.SelectMany(e => e.Children is { } c ? c.Prepend(e) : [e]).Any(e => e.Header == header);

    List<MenuEntry> CommitMenu(params string[] shas)
    {
        _f.Tab.CommitList.SelectCommits(shas);
        return Menus.CommitList(_f.Tab.CommitList)!;
    }

    async Task Invoke(MenuEntry e) => await e.Invoke().WaitAsync(Limit);

    // ---------------------------------------------------------------- interactive rebase window (view model)

    [AvaloniaFact]
    public async Task Window_reorder_reword_squash_drop_then_rebase_gives_the_exact_history()
    {
        var c = Enumerable.Range(1, 5).Select(i => Repo.Commit(new[] { "one", "two", "three", "four", "five" }[i - 1], i, ($"f{i}", $"{i}\n"))).ToArray();
        await OpenAsync();
        InteractiveRebaseViewModel? seen = null;
        Drive(vm =>
        {
            seen = vm;
            Assert.Equal([c[4], c[3], c[2], c[1]], Shas(vm));
            Assert.Equal(("main", c[0][..7]), (vm.Branch, vm.TargetText));
            Assert.Equal([0], vm.Selection);
            Assert.False(vm.Changed);
            Assert.True(vm.CanRebase);

            vm.MoveDown(); // Ctrl+Down: five below four
            Assert.Equal([c[3], c[4], c[2], c[1]], Shas(vm));
            Assert.Equal([1], vm.Selection);
            vm.SelectRows([3]);
            vm.Drop(0, after: false); // drag two above four
            Assert.Equal([c[1], c[3], c[4], c[2]], Shas(vm));
            Assert.Equal([0], vm.Selection);
            vm.Drop(0, after: true); // onto a dragged row: nothing
            Assert.Equal([c[1], c[3], c[4], c[2]], Shas(vm));

            vm.SelectRows([3]);
            vm.RewordKey(); // R: the popup with the current message
            Assert.Equal((3, "three", ""), (vm.Reword!.Row, vm.Reword.Subject, vm.Reword.Description));
            vm.Reword.Subject = "three!";
            vm.Reword.Description = "body";
            vm.CloseReword(ok: true);
            vm.SelectRows([1]);
            vm.SetAction(RebaseAction.Squash); // four melds into five (the row below)
            vm.SelectRows([0]);
            vm.SetAction(RebaseAction.Drop);
            Assert.Equal("Drop Squash Reword Reword", Actions(vm));
            Assert.Equal([RebaseGlyph.None, RebaseGlyph.Start, RebaseGlyph.Target, RebaseGlyph.None], vm.Items.Select(i => i.Glyph));
            Assert.Equal((true, true, false), (vm.Items[0].IsDropped, vm.Items[1].IsMuted, vm.Items[2].IsMuted));
            Assert.True(vm.Changed);
            vm.Backup = true;
            return vm.RebaseAsync();
        });

        await Ops.InteractiveToCommitAsync(c[0]).WaitAsync(Limit);

        Assert.Equal(["five\n\nfour", "three!\n\nbody", "one"], Messages());
        Assert.Equal("f1 f3 f4 f5", Files());
        Assert.Equal("", Repo.Git("status", "--porcelain"));
        Assert.True(seen!.IsClosed);
        Assert.Equal($"Interactive rebase 'main' onto '{c[0][..7]}'", _f.LastJob.Name);
        Assert.Equal(JobOutcome.Succeeded, _f.LastJob.Outcome);
        Assert.Empty(_f.Errors);
        Assert.Equal(Repo.Git("rev-parse", "HEAD"), _f.Tab.CommitList.SelectedSha);
        Assert.Matches(@"^backup/main-\d\d-\d\d-\d\d$", Repo.Git("for-each-ref", "--format=%(refname:short)", "refs/heads/backup/"));
        Assert.True(Settings.Get(InteractiveRebaseViewModel.BackupKey, false));
    }

    [AvaloniaFact]
    public async Task Cancel_asks_only_when_the_list_changed_and_leaves_the_repository_unchanged()
    {
        var c = Four();
        await OpenAsync();

        Drive(vm => vm.CancelAsync());
        await Ops.InteractiveToCommitAsync(c[0]).WaitAsync(Limit);
        Assert.Empty(_dialogs);
        Assert.Equal(c[3], _f.Head);
        Assert.False(InRebase);

        var answers = new Queue<bool>([false, true]);
        _answer = _ => Task.FromResult(answers.Dequeue());
        Drive(async vm =>
        {
            vm.SetAction(RebaseAction.Drop);
            await vm.CancelAsync(); // "No": stays
            Assert.False(vm.IsFinishing);
            await vm.CancelAsync(); // "Yes"
            Assert.True(vm.IsFinishing);
        });
        await Ops.InteractiveToCommitAsync(c[0]).WaitAsync(Limit);

        Assert.Equal(2, _dialogs.Count);
        Assert.Equal(("Do you really want to cancel Interactive Rebase?", "All your changes will be discarded.", "Yes", "No", 550.0),
            (_dialogs[0].Title, _dialogs[0].Description, _dialogs[0].SubmitText, _dialogs[0].CancelText, _dialogs[0].Width));
        Assert.Equal(c[3], _f.Head);
        Assert.Equal(["four", "three", "two", "one"], Messages());
        Assert.False(InRebase);
        Assert.Empty(_f.Errors);
        Assert.False(Settings.Get(InteractiveRebaseViewModel.BackupKey, false)); // only saved by Rebase
    }

    [AvaloniaFact]
    public async Task A_failure_in_the_window_code_answers_git_before_the_window_closes()
    {
        var c = Four();
        await OpenAsync();
        InteractiveRebaseViewModel.Show = vm =>
        {
            vm.SelectionChanged += () => throw new InvalidOperationException("boom"); // fails while the list loads
            return vm.Loaded;
        };
        await Ops.InteractiveToCommitAsync(c[0]).WaitAsync(Limit);
        Assert.False(InRebase);
        Assert.Equal(c[3], _f.Head);
        Assert.Contains("boom", Assert.Single(_f.Errors).Text);
    }

    [AvaloniaFact]
    public async Task Quick_reword_of_the_root_commit_rebases_from_the_root_with_an_empty_on_chip()
    {
        var c = Four();
        await OpenAsync();
        Drive(vm =>
        {
            Assert.Equal(("", null), (vm.TargetText, vm.TargetIcon));
            Assert.Equal(c.Reverse(), Shas(vm));
            Assert.Equal(3, vm.Reword!.Row);
            vm.Reword.Subject = "first";
            vm.CloseReword(true);
            return vm.RebaseAsync();
        });
        await Invoke(Find(CommitMenu(c[0]), "Reword..."));
        Assert.Equal(["four", "three", "two", "first"], Messages());
        Assert.Equal("Interactive rebase 'main'", _f.LastJob.Name);
    }

    // ---------------------------------------------------------------- quick actions from the commit menu

    async Task QuickAsync(string sha, string item) => await Invoke(Find(CommitMenu(sha), item));

    [AvaloniaFact]
    public async Task Quick_reword_opens_the_popup_on_the_commit()
    {
        var c = Four();
        await OpenAsync();
        Drive(vm =>
        {
            Assert.Equal([c[3], c[2]], Shas(vm));
            Assert.Equal("Pick Pick", Actions(vm));
            Assert.Equal([1], vm.Selection);
            Assert.Equal((1, "three"), (vm.Reword!.Row, vm.Reword.Subject));
            Assert.False(vm.Changed);
            vm.Reword.Subject = "THREE";
            vm.CloseReword(true);
            Assert.Equal("Pick Reword", Actions(vm));
            return vm.RebaseAsync();
        });
        await QuickAsync(c[2], "Reword...");
        Assert.Equal(["four", "THREE", "two", "one"], Messages());
    }

    [AvaloniaFact]
    public async Task Quick_edit_stops_the_rebase_at_the_commit()
    {
        var c = Four();
        await OpenAsync();
        Drive(vm =>
        {
            Assert.Equal("Pick Edit", Actions(vm));
            Assert.False(vm.Changed); // the initial action counts as loaded
            return vm.RebaseAsync();
        });
        await QuickAsync(c[2], "Edit...");
        Assert.True(InRebase);
        Assert.Equal(c[2], _f.Head);
        Assert.Empty(_f.Errors);
        Repo.Git("rebase", "--abort");
    }

    [AvaloniaFact]
    public async Task Quick_squash_into_parent()
    {
        var c = Four();
        await OpenAsync();
        Drive(vm =>
        {
            Assert.Equal([c[3], c[2], c[1]], Shas(vm));
            Assert.Equal("Pick Squash Reword", Actions(vm));
            Assert.Equal([1], vm.Selection);
            return vm.RebaseAsync();
        });
        await QuickAsync(c[2], "Squash into Parent...");
        Assert.Equal(["four", "two\n\nthree", "one"], Messages());
    }

    [AvaloniaFact]
    public async Task Quick_fixup_into_parent()
    {
        var c = Four();
        await OpenAsync();
        Drive(vm =>
        {
            Assert.Equal("Pick Fixup Pick", Actions(vm));
            return vm.RebaseAsync();
        });
        await QuickAsync(c[2], "Fixup into Parent…");
        Assert.Equal(["four", "two", "one"], Messages());
        Assert.Equal("f1 f2 f3", Files("HEAD~1"));
    }

    [AvaloniaFact]
    public async Task Quick_drop()
    {
        var c = Four();
        await OpenAsync();
        Drive(vm =>
        {
            Assert.Equal([c[3], c[2]], Shas(vm));
            Assert.Equal("Pick Drop", Actions(vm));
            Assert.True(vm.Items[1].IsDropped);
            return vm.RebaseAsync();
        });
        await QuickAsync(c[2], "Drop…");
        Assert.Equal(["four", "two", "one"], Messages());
        Assert.Equal("f1 f2 f4", Files());
    }

    [AvaloniaFact]
    public async Task Multi_selection_squash_and_drop()
    {
        var c = Four();
        await OpenAsync();
        Drive(vm =>
        {
            Assert.Equal([c[3], c[2], c[1]], Shas(vm)); // target = the parent of the parent of the oldest
            Assert.Equal("Squash Squash Reword", Actions(vm));
            Assert.Equal([1], vm.Selection); // the oldest of them
            return vm.RebaseAsync();
        });
        await Invoke(Find(CommitMenu(c[3], c[2]), "Squash into Parent…"));
        Assert.Equal(["two\n\nthree\n\nfour", "one"], Messages());

        var d = Repo.Commit("five", 5, ("f5", "5\n"));
        var e = Repo.Commit("six", 6, ("f6", "6\n"));
        await _f.RefreshAsync();
        Drive(vm =>
        {
            Assert.Equal([e, d], Shas(vm)); // target = the parent of the oldest (five)
            Assert.Equal("Drop Drop", Actions(vm));
            return vm.RebaseAsync();
        });
        await Invoke(Find(CommitMenu(e, d), "Drop…"));
        Assert.Equal(["two\n\nthree\n\nfour", "one"], Messages());
    }

    // ---------------------------------------------------------------- rebase dialog

    /// <summary>main: init, m (file m); feature from init: x, y (files x, y), "dep" at x. Checked out: feature.</summary>
    async Task<(string Init, string X, string Y)> FeatureAsync(bool conflict = false)
    {
        var init = Repo.Commit("init", 0, ("a.txt", "1\n"));
        Repo.Git("checkout", "-q", "-b", "feature");
        var x = Repo.Commit("x", 1, ("x", "x\n"));
        Repo.Git("branch", "dep");
        var y = Repo.Commit("y", 2, conflict ? ("a.txt", "feature\n") : ("y", "y\n"));
        Repo.Git("checkout", "-q", "main");
        Repo.Commit("m", 3, conflict ? ("a.txt", "main\n") : ("m", "m\n"));
        Repo.Git("checkout", "-q", "feature");
        await OpenAsync();
        return (init, x, y);
    }

    MenuEntry SidebarItem(string branch, string header) =>
        Find(Menus.LocalBranch(_f.Tab, _f.Ref("refs/heads/" + branch), _ => Task.CompletedTask), header);

    [AvaloniaFact]
    public async Task Rebase_dialog_predicts_no_conflicts_shows_dependent_branches_and_updates_them()
    {
        var (_, x, _) = await FeatureAsync();
        RebaseViewModel? seen = null;
        (DialogStatus, string, bool, bool)? opened = null;
        _answer = async d =>
        {
            var vm = seen = (RebaseViewModel)d;
            await vm.Preview;
            opened = (vm.Status, vm.StatusText, vm.ShowUpdateRefs, vm.ShowBranchList);
            vm.UpdateRefs = true;
            Assert.True(vm.ShowBranchList);
            await vm.SubmitAsync();
            return vm.Result == true;
        };
        await Invoke(SidebarItem("main", "Rebase 'feature' on 'main'..."));

        Assert.Equal((DialogStatus.Success, "Rebase can be done without conflicts", true, false), opened);
        Assert.Equal(("Rebase", "Copy commits from one branch to another", "Rebase", 570.0), (seen!.Title, seen.Description, seen.SubmitText, seen.Width));
        Assert.Equal(["dep"], seen.Branches);
        Assert.Equal(("feature", "refs/heads/main"), (seen.Source.Text, seen.Target.Arg));
        Assert.Equal("Rebase 'feature' onto 'main'", _f.LastJob.Name);
        Assert.Equal(["y", "x", "m", "init"], Messages());
        Assert.Equal(Repo.Git("rev-parse", "feature~1"), Repo.Git("rev-parse", "dep")); // --update-refs moved it
        Assert.NotEqual(x, Repo.Git("rev-parse", "dep"));
        Assert.True(Settings.Get(RebaseViewModel.UpdateRefsKey, false));
        Assert.Equal(_f.Head, _f.Tab.CommitList.SelectedSha);
        Assert.Empty(_f.Errors);
    }

    [AvaloniaFact]
    public async Task Rebase_dialog_conflict_opens_the_merge_conflict_window_and_leaves_the_rebase_state()
    {
        await FeatureAsync(conflict: true);
        Repo.Git("branch", "-D", "dep"); // no dependent branch: the box stays hidden
        await _f.RefreshAsync();
        (DialogStatus, string, bool)? opened = null;
        _answer = async d =>
        {
            var vm = (RebaseViewModel)d;
            await vm.Preview;
            opened = (vm.Status, vm.StatusText, vm.ShowUpdateRefs);
            await vm.SubmitAsync();
            return vm.Result == true;
        };
        await Invoke(SidebarItem("main", "Rebase 'feature' on 'main'..."));

        Assert.Equal((DialogStatus.Warning, "Rebase will cause conflicts", false), opened);
        Assert.Equal(GitErrorKind.MergeConflict, Assert.Single(_f.Errors).Kind);
        Assert.True(InRebase);
    }

    [AvaloniaFact]
    public async Task Rebase_dialog_autostash_reapplies_local_changes()
    {
        await FeatureAsync();
        Repo.Write("a.txt", "dirty\n");
        _answer = async d =>
        {
            var vm = (RebaseViewModel)d;
            await vm.Preview;
            Assert.False(vm.Autostash); // default off
            vm.Autostash = true;
            await vm.SubmitAsync();
            return vm.Result == true;
        };
        await Invoke(SidebarItem("main", "Rebase 'feature' on 'main'..."));

        Assert.Empty(_f.Errors);
        Assert.Equal(["y", "x", "m", "init"], Messages());
        Assert.Equal("dirty\n", _f.Read("a.txt"));
        Assert.Empty(_f.Stashes);
        Assert.True(Settings.Get(RebaseViewModel.AutostashKey, false));
    }

    [AvaloniaFact]
    public async Task Rebase_dialog_closes_with_the_error_when_the_target_has_no_commit()
    {
        await FeatureAsync();
        _answer = async d =>
        {
            await ((RebaseViewModel)d).Preview;
            return d.Result == true;
        };
        await Ops.RebaseToCommitAsync(new string('1', 40)).WaitAsync(Limit);
        Assert.False(_dialogs.Single().Result);
        Assert.Equal("Cannot get destination sha for rebase", Assert.Single(_f.Errors).Text);
    }

    // ---------------------------------------------------------------- menus

    [AvaloniaFact]
    public async Task Commit_menu_items_texts_and_enabled_rules()
    {
        var c = Enumerable.Range(1, 3).Select(i => Repo.Commit($"c{i}", i, ($"f{i}", $"{i}\n"))).ToArray();
        Repo.Git("checkout", "-q", "-b", "side", c[1]);
        var s1 = Repo.Commit("s1", 4, ("s", "s\n"));
        Repo.Git("checkout", "-q", "main");
        await OpenAsync();

        var sub = Find(CommitMenu(c[1]), "Interactive Rebase");
        Assert.Equal("Interactively Rebase 'main' to Here...|-|Quick Actions|-|Reword...|Edit...|Squash into Parent...|Fixup into Parent…|Drop…",
            string.Join("|", sub.Children!));
        Assert.All(sub.Children!.Where(e => e.Kind == MenuEntryKind.Item), e => Assert.True(e.IsEnabled));
        Assert.False(Has(CommitMenu(c[1]), "Rebase 'main' to Here..."));
        var root = Find(CommitMenu(c[0]), "Interactive Rebase").Children!;
        Assert.Equal([true, true, true, false, false, false],
            root.Where(e => e.Kind == MenuEntryKind.Item).Select(e => e.IsEnabled));

        // Not reachable from HEAD: Merge, plain Rebase and the single interactive item, then a separator.
        var side = CommitMenu(s1).Select(e => e.ToString()).ToList();
        int merge = side.IndexOf("Merge into 'main'...");
        Assert.Equal(["Merge into 'main'...", "Rebase 'main' to Here...", "Interactively Rebase 'main' to Here...", "-"], side.Skip(merge).Take(4));
        Assert.False(Has(CommitMenu(s1), "Interactive Rebase"));

        // Multi-selection: an unbroken chain offers both; otherwise only Drop…; an unreachable commit neither.
        var multi = CommitMenu(c[2], c[1]).Select(e => e.ToString()).ToList();
        Assert.Equal(["Cherry-pick…", "-", "Squash into Parent…", "Drop…", "-"], multi.Take(5));
        Assert.False(Has(CommitMenu(c[2], c[0]), "Squash into Parent…"));
        Assert.True(Has(CommitMenu(c[2], c[0]), "Drop…"));
        Assert.False(Has(CommitMenu(s1, c[1]), "Drop…"));
        Assert.False(Has(CommitMenu(c[1], c[0]), "Squash into Parent…")); // the root commit has no parent
        Assert.True(Has(CommitMenu(c[1], c[0]), "Drop…"));

        // A merge commit between HEAD and the commit (the commit itself counts): the single item.
        Repo.Git("merge", "-q", "--no-ff", "-m", "merge side", "side");
        await _f.RefreshAsync();
        Assert.True(Has(CommitMenu(c[2]), "Interactively Rebase 'main' to Here..."));
        Assert.False(Has(CommitMenu(c[2]), "Interactive Rebase"));
        Assert.False(Has(CommitMenu(_f.Head), "Interactive Rebase"));

        // Detached HEAD: no block at all.
        Repo.Git("checkout", "-q", "--detach");
        await _f.RefreshAsync();
        Assert.False(Has(CommitMenu(c[1]), "Interactively Rebase 'main' to Here..."));
    }

    [AvaloniaFact]
    public async Task Sidebar_items_texts_and_enabled_rules()
    {
        var c1 = Repo.Commit("c1", 1, ("f1", "1\n"));
        Repo.Git("tag", "t1");
        Repo.Git("branch", "b");
        _f.AddOrigin();
        Repo.Commit("c2", 2, ("f2", "2\n"));
        Repo.Git("tag", "t2");
        await OpenAsync();
        Task Copy(string _) => Task.CompletedTask;

        var local = Menus.LocalBranch(_f.Tab, _f.Ref("refs/heads/b"), Copy).Select(e => e.ToString()).ToList();
        int i = local.IndexOf("Merge into 'main'...");
        Assert.Equal(["Merge into 'main'...", "Rebase 'main' on 'b'...", "Interactively Rebase 'main' on 'b...", "-"], local.Skip(i).Take(4));
        Assert.False(Has(Menus.LocalBranch(_f.Tab, _f.Ref("refs/heads/main"), Copy), "Rebase 'main' on 'main'..."));

        // origin/main points at c1 (pushed before c2): both enabled; a tag on the active tip disables the plain item only.
        var remote = Menus.RemoteBranch(_f.Tab, _f.Ref("refs/remotes/origin/main"), Copy);
        Assert.True(Find(remote, "Rebase 'main' on 'origin/main'...").IsEnabled);
        var tip = Menus.Tag(_f.Tab, _f.Ref("refs/tags/t2"), Copy);
        Assert.False(Find(tip, "Rebase 'main' on 't2'...").IsEnabled);
        Assert.True(Find(tip, "Interactively Rebase 'main' on 't2...").IsEnabled);
        Assert.True(Find(Menus.Tag(_f.Tab, _f.Ref("refs/tags/t1"), Copy), "Rebase 'main' on 't1'...").IsEnabled);

        // The interactive item from the sidebar rebases onto the full ref.
        Drive(vm =>
        {
            Assert.Equal(("t1", "tag"), (vm.TargetText, vm.TargetIcon));
            Assert.Single(vm.Items);
            return vm.CancelAsync();
        });
        await Invoke(Find(Menus.Tag(_f.Tab, _f.Ref("refs/tags/t1"), Copy), "Interactively Rebase 'main' on 't1..."));
        Assert.Equal(c1, Repo.Git("rev-parse", "HEAD~1"));
    }

    // ---------------------------------------------------------------- headless views

    static void Settle(Window w)
    {
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    static T FindIn<T>(Window w, string name) where T : Control => w.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    /// <summary>A control in the row at <paramref name="index"/> (containers are not in row order after a change).</summary>
    static T Row<T>(ListBox list, int index, string? name = null) where T : Control =>
        list.ContainerFromIndex(index)!.GetVisualDescendants().OfType<T>().First(c => name is null || c.Name == name);

    static async Task UntilAsync(Func<bool> done)
    {
        var end = DateTime.UtcNow + Limit;
        while (!done() && DateTime.UtcNow < end)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
        Assert.True(done());
    }

    [AvaloniaFact]
    public async Task Window_renders_rows_glyphs_selection_blocks_keys_and_the_reword_popup()
    {
        var c = Four();
        await OpenAsync();
        var owner = new Window { Width = 1200, Height = 900 };
        owner.Show();
        InteractiveRebaseViewModel? vm = null;
        InteractiveRebaseViewModel.Show = v => InteractiveRebaseWindow.ShowAsync(vm = v, owner);
        var op = Ops.InteractiveToCommitAsync(c[0]);
        await UntilAsync(() => owner.OwnedWindows.Count == 1 && vm!.Loaded.IsCompleted);
        var w = (InteractiveRebaseWindow)owner.OwnedWindows.Single();
        Settle(w);

        Assert.Equal(("Interactive Rebase", "main", c[0][..7]),
            (FindIn<TextBlock>(w, "Heading").Text, FindIn<RefView>(w, "BranchChip").Text, FindIn<RefView>(w, "TargetChip").Text));
        Assert.False(FindIn<Border>(w, "LoadingCover").IsVisible);
        var list = FindIn<ListBox>(w, "TodoList");
        Assert.Equal(3, list.GetRealizedContainers().Count());
        Assert.Equal(22, list.ContainerFromIndex(0)!.Bounds.Height);
        Assert.Equal(["four", "three", "two"], Enumerable.Range(0, 3).Select(i => Row<TextBlock>(list, i, "SubjectText").Text));
        Assert.True(FindIn<Button>(w, "RebaseButton").IsEffectivelyEnabled);

        // S on the selected second row: squash into the row below (glyphs Start / Target, grey text).
        list.SelectedIndex = 1;
        Settle(w);
        Assert.Equal([1], vm!.Selection);
        list.ContainerFromIndex(1)!.Focus();
        w.KeyPress(Key.S, RawInputModifiers.None, PhysicalKey.S, "s");
        Settle(w);
        Assert.Equal("Pick Squash Reword", Actions(vm));
        Assert.Equal([RebaseGlyph.None, RebaseGlyph.Start, RebaseGlyph.Target],
            Enumerable.Range(0, 3).Select(i => Row<RebaseGlyphView>(list, i).Glyph));
        Assert.Contains("muted", Row<TextBlock>(list, 1, "SubjectText").Classes);

        // Ctrl+Down moves the row; Ctrl+Up back.
        w.KeyPress(Key.Down, RawInputModifiers.Control, PhysicalKey.ArrowDown, null);
        Settle(w);
        Assert.Equal([c[3], c[1], c[2]], Shas(vm));
        Assert.Equal([2], vm.Selection);
        w.KeyPress(Key.Up, RawInputModifiers.Control, PhysicalKey.ArrowUp, null);
        Settle(w);
        Assert.Equal([c[3], c[2], c[1]], Shas(vm));

        // Two selected rows form one block: top corners on the first, bottom corners on the last.
        list.Selection.Select(0);
        Settle(w);
        Assert.Equal([0, 1], vm.Selection);
        Assert.Equal((new CornerRadius(4, 4, 0, 0), new CornerRadius(0, 0, 4, 4)),
            (((ListBoxItem)list.ContainerFromIndex(0)!).CornerRadius, ((ListBoxItem)list.ContainerFromIndex(1)!).CornerRadius));

        // Reword popup: opens below the row, Enter moves to the description, Backspace at 0 back, Esc cancels.
        vm.DoubleClick(0);
        Settle(w);
        var subject = FindIn<TextBox>(w, "RewordSubject");
        Assert.True(FindIn<Panel>(w, "RewordHost").IsVisible);
        Assert.Equal(("four", true, 4), (subject.Text, subject.IsFocused, subject.CaretIndex));
        Assert.Equal(130, Canvas.GetLeft(FindIn<Panel>(w, "RewordHost")));
        Assert.Equal("46", FindIn<TextBlock>(w, "RewordCounter").Text);
        w.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Settle(w);
        Assert.True(FindIn<TextBox>(w, "RewordDescription").IsFocused);
        w.KeyPress(Key.Back, RawInputModifiers.None, PhysicalKey.Backspace, null);
        Settle(w);
        Assert.True(subject.IsFocused);
        using (var frame = w.CaptureRenderedFrame()) Assert.NotNull(frame);
        w.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Settle(w);
        Assert.Null(vm.Reword);
        Assert.False(FindIn<Panel>(w, "RewordHost").IsVisible);

        // Drag the third row (pressed while unselected) onto the upper half of the first: it moves above it.
        Assert.Equal([c[3], c[2], c[1]], Shas(vm));
        Point At(int row, double y) => list.ContainerFromIndex(row)!.TranslatePoint(new Point(400, y), w)!.Value;
        w.MouseDown(At(2, 11), MouseButton.Left);
        w.MouseMove(At(1, 11), RawInputModifiers.LeftMouseButton);
        w.MouseMove(At(0, 5), RawInputModifiers.LeftMouseButton);
        Settle(w);
        Assert.True(FindIn<Border>(w, "DropLine").IsVisible);
        w.MouseUp(At(0, 5), MouseButton.Left);
        Settle(w);
        Assert.Equal([c[1], c[3], c[2]], Shas(vm));
        Assert.Equal([0], vm.Selection);
        Assert.False(FindIn<Border>(w, "DropLine").IsVisible);

        // The row dropdown: an action on an unselected row applies to that row only; Move Down moves it.
        var combo = Row<ComboBox>(list, 2);
        combo.SelectedIndex = (int)RebaseAction.Drop;
        Settle(w);
        Assert.Equal("Pick Pick Drop", Actions(vm));
        Assert.Equal([2], vm.Selection);
        Row<ComboBox>(list, 0).SelectedIndex = ActionChoice.All.Count - 1; // Move Down
        Settle(w);
        Assert.Equal([c[3], c[1], c[2]], Shas(vm));
        Assert.Equal("Pick Pick Drop", Actions(vm));
        Assert.Equal((int)RebaseAction.Pick, Row<ComboBox>(list, 1).SelectedIndex); // the dropdown returned to the action
        list.ContainerFromIndex(1)!.Focus();
        w.KeyPress(Key.D, RawInputModifiers.None, PhysicalKey.D, "d");
        Settle(w);
        Assert.Equal(("Pick Drop Drop", (int)RebaseAction.Drop), (Actions(vm), Row<ComboBox>(list, 1).SelectedIndex));

        // P with the row's dropdown open sets the action of the selection.
        var open = Row<ComboBox>(list, 1);
        open.IsDropDownOpen = true;
        Settle(w);
        w.KeyPress(Key.P, RawInputModifiers.None, PhysicalKey.P, "p");
        Settle(w);
        open.IsDropDownOpen = false;
        Settle(w);
        Assert.Equal("Pick Pick Drop", Actions(vm));

        // Esc with a changed list asks once (a second request while it is open adds none); "Yes" cancels and the
        // window closes when git has ended.
        var asked = new List<DialogViewModel>();
        var answer = new TaskCompletionSource<bool>();
        vm.Confirm = d => { asked.Add(d); return answer.Task; };
        w.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        w.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        answer.SetResult(true);
        await op.WaitAsync(Limit);
        Settle(owner);
        Assert.Single(asked);
        Assert.False(w.IsVisible);
        Assert.Equal(c[3], _f.Head);
        Assert.False(InRebase);
        owner.Close();
    }

    [AvaloniaFact]
    public async Task Rebase_dialog_renders_prediction_and_the_dependent_branch_list()
    {
        await FeatureAsync();
        var owner = new Window { Width = 900, Height = 700 };
        owner.Show();
        RebaseViewModel? vm = null;
        _f.Tab.ShowDialog = d => DialogWindow.ShowAsync(vm = (RebaseViewModel)d, owner);
        var op = Invoke(SidebarItem("main", "Rebase 'feature' on 'main'..."));
        await UntilAsync(() => owner.OwnedWindows.Count == 1 && vm!.Preview.IsCompleted);
        var w = (DialogWindow)owner.OwnedWindows.Single();
        Settle(w);

        Assert.Equal(("Rebase", 570.0), (w.Title, w.Width));
        Assert.Equal(("feature", "main"), (FindIn<RefView>(w, "Source").Text, FindIn<RefView>(w, "Target").Text));
        Assert.Equal("Rebase can be done without conflicts", FindIn<TextBlock>(w, "FooterText").Text);
        Assert.True(FindIn<CheckBox>(w, "UpdateRefsBox").IsEffectivelyVisible);
        Assert.Equal("--update-refs", ToolTip.GetTip(FindIn<CheckBox>(w, "UpdateRefsBox")));
        Assert.False(FindIn<ScrollViewer>(w, "BranchList").IsEffectivelyVisible);
        FindIn<CheckBox>(w, "UpdateRefsBox").IsChecked = true;
        Settle(w);
        Assert.True(FindIn<ScrollViewer>(w, "BranchList").IsEffectivelyVisible);
        Assert.Contains(w.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "dep");
        Assert.Equal("Stash and reapply local changes", FindIn<CheckBox>(w, "AutostashBox").Content);
        using (var frame = w.CaptureRenderedFrame()) Assert.NotNull(frame);

        vm!.Cancel();
        Settle(w);
        await op;
        owner.Close();
    }
}
