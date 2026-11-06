using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GerGit.Controls;
using GerGit.Core;
using GerGit.Core.Git;
using GerGit.ViewModels;
using GerGit.ViewModels.Dialogs;
using GerGit.Views.Dialogs;

namespace GerGit.Tests.Ui;

/// <summary>Through the entry points (StashOps / CommitOps) and their dialogs.</summary>
public class StashDialogsTests
{
    /// <summary>A tab on a temp repo whose dialogs are answered by <see cref="Fill"/> and submitted.</summary>
    sealed class Fixture : IDisposable
    {
        public readonly TempRepo Repo = new();
        public RepoTabViewModel Tab = null!;
        public readonly List<DialogViewModel> Dialogs = [];
        public readonly List<GitErrorKind> Errors = [];
        public Func<DialogViewModel, Task> Fill = _ => Task.CompletedTask;
        public bool Submit = true;

        public async Task OpenAsync()
        {
            Tab = new RepoTabViewModel(await GitRepository.OpenAsync(Repo.Path));
            await Tab.RefreshAsync();
            Tab.ShowDialog = async vm =>
            {
                Dialogs.Add(vm);
                await Fill(vm);
                if (Submit) await vm.SubmitAsync();
                return vm.Result == true;
            };
            Tab.ShowGitError = (r, _) => { Errors.Add(GitErrors.Classify(r).Kind); return Task.CompletedTask; };
            Tab.ShowError = (_, text) => { Errors.Add(GitErrors.Classify(text).Kind); return Task.CompletedTask; };
        }

        public string Read(string path) => File.ReadAllText(Path.Combine(Repo.Path, path));
        public bool Exists(string path) => File.Exists(Path.Combine(Repo.Path, path));
        public Task Refresh() => Tab.RefreshAsync();

        public void Dispose()
        {
            Tab?.Dispose();
            Repo.Dispose();
        }
    }

    static async Task<Fixture> OpenAsync(Action<TempRepo> setup)
    {
        var f = new Fixture();
        f.Repo.Commit("init", 0, ("a.txt", "1\n"));
        setup(f.Repo);
        await f.OpenAsync();
        return f;
    }

    // ---------------------------------------------------------------- stash

    [AvaloniaFact]
    public async Task Save_stash_with_message_and_stage_new_files()
    {
        using var f = await OpenAsync(r => { r.Write("a.txt", "2\n"); r.Write("new.txt", "n\n"); });
        f.Fill = vm =>
        {
            var s = Assert.IsType<SaveStashViewModel>(vm);
            Assert.Equal(("Save stash", "Save Stash", true), (s.Title, s.SubmitText, s.CanSubmit));
            s.Message = "my note";
            s.StageNewFiles = true;
            return Task.CompletedTask;
        };
        await f.Tab.StashOps.SaveAsync();

        Assert.Empty(f.Errors);
        Assert.False(f.Exists("new.txt"));
        Assert.Equal("1\n", f.Read("a.txt"));
        var stash = Assert.Single(f.Tab.Stashes);
        Assert.Equal("my note", stash.ShortSubject);
        Assert.Contains("new.txt", f.Repo.Git("stash", "show", "--name-only", "stash@{0}"));
        Assert.Equal(0, f.Tab.Status.FileCount);
        Assert.True(Settings.Get(SaveStashViewModel.StageNewFilesKey, false)); // remembered
        Assert.Equal("Stash local changes", f.Tab.Jobs.History[0].Name);

        // Remembered value is the next default; nothing to save → no error, no stash.
        f.Fill = vm => { Assert.True(((SaveStashViewModel)vm).StageNewFiles); ((SaveStashViewModel)vm).StageNewFiles = false; return Task.CompletedTask; };
        await f.Tab.StashOps.SaveAsync();
        Assert.Empty(f.Errors);
        Assert.Single(f.Tab.Stashes);
        Assert.Equal(JobOutcome.Succeeded, f.Tab.Jobs.History[0].Outcome);
        Assert.False(Settings.Get(SaveStashViewModel.StageNewFilesKey, true));
    }

    [AvaloniaFact]
    public async Task Save_snapshot_keeps_the_working_tree_with_the_default_message()
    {
        using var f = await OpenAsync(r => r.Write("a.txt", "2\n"));
        f.Fill = vm => { Assert.Equal("Save Snapshot", vm.SubmitText); return Task.CompletedTask; };
        await f.Tab.StashOps.SaveSnapshotAsync();

        Assert.Empty(f.Errors);
        Assert.Equal("2\n", f.Read("a.txt"));
        Assert.Matches(@"^Snapshot on 'main' \d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}$", Assert.Single(f.Tab.Stashes).ShortSubject);
        Assert.Equal("Stash snapshot", f.Tab.Jobs.History[0].Name);
    }

    [AvaloniaFact]
    public async Task Stash_files_preselects_validates_and_stashes_the_checked_files()
    {
        using var f = await OpenAsync(r =>
        {
            r.Commit("more", 1, ("b.txt", "b\n"), ("file10.txt", "x\n"), ("file9.txt", "y\n"));
            r.Write("a.txt", "2\n");
            r.Write("b.txt", "b2\n");
            r.Write("file10.txt", "x2\n");
            r.Write("file9.txt", "y2\n");
            r.Write("new.txt", "n\n");
        });
        f.Fill = vm =>
        {
            var s = Assert.IsType<StashFilesViewModel>(vm);
            Assert.Equal(["a.txt", "b.txt", "file9.txt", "file10.txt", "new.txt"], s.Files.Select(x => x.Path)); // natural order
            Assert.Equal(["a.txt", "new.txt"], s.Files.Where(x => x.IsChecked).Select(x => x.Path));
            Assert.True(s.CanSubmit);
            foreach (var x in s.Files) x.IsChecked = false;
            Assert.False(s.CanSubmit);
            s.Files[0].IsChecked = s.Files[4].IsChecked = true;
            Assert.True(s.CanSubmit);
            s.Message = "part";
            return Task.CompletedTask;
        };
        f.Tab.IsLocalChangesView = false;
        await f.Tab.StashOps.StashFilesAsync(["a.txt", "new.txt"]);

        Assert.Empty(f.Errors);
        Assert.Equal("1\n", f.Read("a.txt"));
        Assert.False(f.Exists("new.txt"));
        Assert.Equal("b2\n", f.Read("b.txt"));
        Assert.Equal("part", Assert.Single(f.Tab.Stashes).ShortSubject);
        Assert.True(f.Tab.IsLocalChangesView);
        Assert.Equal("Partial stash", f.Tab.Jobs.History[0].Name);
    }

    [AvaloniaFact]
    public async Task Apply_keeps_the_stash_pop_removes_it_and_a_conflict_keeps_it()
    {
        using var f = await OpenAsync(r => { r.Write("a.txt", "2\n"); r.Git("stash", "push", "-m", "s"); });
        f.Fill = vm =>
        {
            var a = Assert.IsType<ApplyStashViewModel>(vm);
            Assert.Equal(("Apply Stash", "Apply", false), (a.Title, a.SubmitText, a.DeleteAfterApply));
            return Task.CompletedTask;
        };
        await f.Tab.StashOps.ApplyAsync(f.Tab.Stashes[0]);
        Assert.Equal("2\n", f.Read("a.txt"));
        Assert.Single(f.Tab.Stashes);
        Assert.Equal("Apply stash 'stash@{0}'", f.Tab.Jobs.History[0].Name);
        Assert.Equal(f.Tab.Refs.HeadSha, f.Tab.CommitList.SelectedSha); // select HEAD

        // Conflict: the working tree changed the same line; the stash stays even with pop.
        f.Repo.Git("checkout", "--", "a.txt");
        f.Repo.Commit("three", 2, ("a.txt", "3\n"));
        await f.Refresh();
        f.Fill = vm => { ((ApplyStashViewModel)vm).DeleteAfterApply = true; return Task.CompletedTask; };
        await f.Tab.StashOps.ApplyAsync(f.Tab.Stashes[0]);
        Assert.Equal([GitErrorKind.MergeConflict], f.Errors);
        Assert.Single(f.Tab.Stashes);
        Assert.True(Settings.Get(ApplyStashViewModel.DropKey, false));

        // Pop without conflict removes it.
        f.Errors.Clear();
        f.Repo.Git("reset", "-q", "--hard", "HEAD~1");
        await f.Refresh();
        f.Fill = vm => { Assert.True(((ApplyStashViewModel)vm).DeleteAfterApply); return Task.CompletedTask; };
        await f.Tab.StashOps.ApplyAsync(f.Tab.Stashes[0]);
        Assert.Empty(f.Errors);
        Assert.Empty(f.Tab.Stashes);
        Assert.Equal("2\n", f.Read("a.txt"));
        Settings.Set<bool?>(ApplyStashViewModel.DropKey, null);
    }

    [AvaloniaFact]
    public async Task Apply_retries_without_index_when_the_index_part_conflicts()
    {
        // The staged part no longer applies as a patch (context changed), the 3-way merge of the retry does.
        using var f = await OpenAsync(r =>
        {
            r.Commit("lines", 1, ("a.txt", "1\n2\n3\n4\n5\n6\n7\n8\n"));
            r.Write("a.txt", "X\n2\n3\n4\n5\n6\n7\n8\n");
            r.Git("add", "a.txt");
            r.Git("stash", "push");
            r.Commit("y", 2, ("a.txt", "1\n2\n3\nY\n5\n6\n7\n8\n"));
        });
        await f.Tab.StashOps.ApplyAsync(f.Tab.Stashes[0]);
        var output = f.Tab.Jobs.History[0].Output;
        Assert.Contains("$ git stash apply --index refs/stash@{0}\n", output);
        Assert.Contains("$ git stash apply refs/stash@{0}\n", output);
        Assert.Empty(f.Errors);
        Assert.Equal("X\n2\n3\nY\n5\n6\n7\n8\n", f.Read("a.txt"));
    }

    [AvaloniaFact]
    public async Task Rename_validates_and_selects_the_new_stash()
    {
        using var f = await OpenAsync(r =>
        {
            r.Write("a.txt", "2\n");
            r.Git("stash", "push", "-m", "old");
        });
        f.Fill = vm =>
        {
            var s = Assert.IsType<RenameStashViewModel>(vm);
            Assert.Equal(("old", false), (s.Message, s.CanSubmit));
            s.Message = "   ";
            Assert.False(s.CanSubmit);
            s.Message = "new name";
            Assert.True(s.CanSubmit);
            return Task.CompletedTask;
        };
        var oldSha = f.Tab.Stashes[0].Sha;
        await f.Tab.StashOps.RenameAsync(f.Tab.Stashes[0]);

        Assert.Empty(f.Errors);
        var s = Assert.Single(f.Tab.Stashes);
        Assert.Equal("new name", s.ShortSubject);
        Assert.NotEqual(oldSha, s.Sha);
        Assert.Equal(s.Sha, f.Tab.CommitList.SelectedSha);
        Assert.Equal("Rename stash 'old'", f.Tab.Jobs.History[0].Name);
    }

    [AvaloniaFact]
    public async Task Delete_several_stashes_and_select_the_parent_of_the_first()
    {
        using var f = await OpenAsync(r =>
        {
            foreach (var n in new[] { "s0", "s1", "s2" })
            {
                r.Write("a.txt", n + "\n");
                r.Git("stash", "push", "-m", n);
            }
        });
        f.Fill = vm =>
        {
            var d = Assert.IsType<DeleteStashViewModel>(vm);
            Assert.Equal(("Delete Stashes", "Delete 2 stashes", "Stashes:"), (d.Title, d.SubmitText, d.Label));
            return Task.CompletedTask;
        };
        var chosen = new[] { f.Tab.Stashes[0], f.Tab.Stashes[2] };
        await f.Tab.StashOps.DeleteAsync(chosen);
        Assert.Equal(["s1"], f.Tab.Stashes.Select(s => s.ShortSubject));
        Assert.Equal(chosen[0].ParentSha, f.Tab.CommitList.SelectedSha);
        Assert.Equal("Delete 2 stashes", f.Tab.Jobs.History[0].Name);

        f.Fill = vm => { Assert.Equal(("Delete Stash", "Delete", "Stash:"), (vm.Title, vm.SubmitText, ((DeleteStashViewModel)vm).Label)); return Task.CompletedTask; };
        await f.Tab.StashOps.DeleteAsync([f.Tab.Stashes[0]]);
        Assert.Empty(f.Tab.Stashes);
        Assert.Equal("Delete 'stash@{0}'", f.Tab.Jobs.History[0].Name);
    }

    [AvaloniaFact]
    public async Task Recent_stashes_are_the_15_newest()
    {
        using var f = await OpenAsync(r =>
        {
            for (int i = 0; i < 17; i++)
            {
                r.Write("a.txt", i + "\n");
                r.Git("stash", "push", "-m", "s" + i);
            }
        });
        var recent = f.Tab.StashOps.RecentStashes;
        Assert.Equal(15, recent.Count);
        Assert.Equal("s16", recent[0].ShortSubject);
        Assert.Equal("s2", recent[14].ShortSubject);
    }

    // ---------------------------------------------------------------- cherry-pick / revert

    /// <summary>The previews need `merge-tree --merge-base` (git 2.40); older git shows no footer text.</summary>
    static async Task<bool> CanPreviewAsync() => await GitRunner.GetVersionAsync() >= new Version(2, 40);

    [AvaloniaFact]
    public async Task Cherry_pick_several_commits_oldest_first_with_preview_and_options()
    {
        string b = "", c = "";
        using var f = await OpenAsync(r =>
        {
            r.Git("checkout", "-q", "-b", "side");
            b = r.Commit("b", 1, ("b.txt", "b\n"));
            c = r.Commit("c", 2, ("c.txt", "c\n"));
            r.Git("checkout", "-q", "main");
        });
        Settings.Set(CommitPickViewModel.RecordOriginKey, true);
        f.Fill = async vm =>
        {
            var p = Assert.IsType<CommitPickViewModel>(vm);
            Assert.Equal(("Cherry Pick", "Cherry Pick 2 commits", "Commits to apply:"), (p.Title, p.SubmitText, p.CommitsLabel));
            Assert.Equal([c, b], p.Commits.Select(x => x.Sha));
            Assert.False(p.HasParents);
            Assert.True(p.CommitChanges);
            Assert.True(p.AppendOrigin); // remembered
            await p.Preview;
            if (await CanPreviewAsync())
                Assert.Equal((DialogStatus.Success, "Cherry pick can be done without conflicts"), (p.Status, p.StatusText));
            p.CommitChanges = false;
            Assert.False(p.AppendOrigin);
            p.CommitChanges = true;
            p.AppendOrigin = true;
        };
        await f.Tab.CommitOps.CherryPickAsync([c, b]); // list order, newest first

        Assert.Empty(f.Errors);
        Assert.Equal(["c", "b", "init"], f.Repo.Git("log", "--format=%s").Split('\n'));
        Assert.Contains($"(cherry picked from commit {c})", f.Repo.Git("log", "-1", "--format=%B"));
        Assert.Equal(c, f.Tab.CommitList.SelectedSha); // the newest picked commit of the selection
        Assert.Contains($"$ git cherry-pick -x {b} {c}\n", f.Tab.Jobs.History[0].Output);
        Settings.Set<bool?>(CommitPickViewModel.RecordOriginKey, null);
    }

    [AvaloniaFact]
    public async Task Cherry_pick_merge_commit_parent_choice_and_conflict()
    {
        string merge = "", x = "";
        using var f = await OpenAsync(r =>
        {
            r.Git("checkout", "-q", "-b", "topic");
            r.Commit("t", 1, ("t.txt", "t\n"));
            r.Git("checkout", "-q", "main");
            r.Commit("m", 2, ("m.txt", "m\n"));
            r.Git("merge", "-q", "--no-ff", "-m", "merge topic", "topic");
            merge = r.Git("rev-parse", "HEAD");
            x = r.Commit("x", 3, ("a.txt", "x\n"));
            r.Git("checkout", "-q", "-b", "target", "topic");
            r.Commit("y", 4, ("a.txt", "y\n"));
        });
        f.Fill = async vm =>
        {
            var p = (CommitPickViewModel)vm;
            Assert.Equal(("Cherry Pick", "Commit to apply:"), (p.SubmitText, p.CommitsLabel));
            Assert.Equal(["m", "t"], p.Parents.Select(x => x.Subject));
            Assert.Same(p.Parents[0], p.SelectedParent);
            await p.Preview;
            p.SelectedParent = p.Parents[1];
            await p.Preview;
            if (await CanPreviewAsync()) Assert.Equal(DialogStatus.Success, p.Status);
        };
        await f.Tab.CommitOps.CherryPickAsync([merge]);
        Assert.Empty(f.Errors);
        Assert.True(f.Exists("m.txt"));
        Assert.Contains($"$ git cherry-pick -m 2 {merge}\n", f.Tab.Jobs.History[0].Output);

        f.Fill = async vm =>
        {
            await ((CommitPickViewModel)vm).Preview;
            if (await CanPreviewAsync())
                Assert.Equal((DialogStatus.Warning, "Cherry pick will cause conflicts"), (vm.Status, vm.StatusText));
        };
        await f.Tab.CommitOps.CherryPickAsync([x]);
        Assert.Equal([GitErrorKind.MergeConflict], f.Errors);
    }

    [AvaloniaFact]
    public async Task Revert_with_preview_no_commit_and_conflict()
    {
        string b = "", a2 = "";
        using var f = await OpenAsync(r =>
        {
            b = r.Commit("b", 1, ("b.txt", "b\n"));
            a2 = r.Commit("a2", 2, ("a.txt", "2\n"));
            r.Commit("a3", 3, ("a.txt", "3\n"));
        });
        f.Fill = async vm =>
        {
            var p = Assert.IsType<CommitPickViewModel>(vm);
            Assert.Equal(("Revert", "Revert", "Commit to apply:", false), (p.Title, p.SubmitText, p.CommitsLabel, p.ShowAppendOrigin));
            await p.Preview;
            if (await CanPreviewAsync())
                Assert.Equal((DialogStatus.Success, "Revert can be done without conflicts"), (p.Status, p.StatusText));
            p.CommitChanges = false;
        };
        await f.Tab.CommitOps.RevertAsync(b);
        Assert.Empty(f.Errors);
        Assert.Equal("D  b.txt", f.Repo.Git("status", "--porcelain"));
        Assert.Equal(b, f.Tab.CommitList.SelectedSha);
        Assert.Equal($"Revert '{b[..7]}'", f.Tab.Jobs.History[0].Name);

        f.Repo.Git("reset", "-q", "--hard");
        await f.Refresh();
        f.Fill = async vm =>
        {
            await ((CommitPickViewModel)vm).Preview;
            if (await CanPreviewAsync())
                Assert.Equal((DialogStatus.Warning, "Revert will cause conflicts"), (vm.Status, vm.StatusText));
        };
        await f.Tab.CommitOps.RevertAsync(a2);
        Assert.Equal([GitErrorKind.MergeConflict], f.Errors);
    }

    // ---------------------------------------------------------------- headless smoke: the real dialog windows

    [AvaloniaFact]
    public async Task Each_dialog_opens_with_its_body_and_defaults()
    {
        using var f = await OpenAsync(r =>
        {
            r.Write("a.txt", "2\n");
            r.Git("stash", "push", "-m", "s");
            r.Write("a.txt", "3\n");
        });
        await f.Refresh();
        var owner = new Window { Width = 900, Height = 700 };
        owner.Show();
        var stash = f.Tab.Stashes[0];
        var head = f.Tab.Refs.HeadSha!;
        var commits = new List<CommitItem> { new(head, "init"), new(head, "init") };

        (DialogViewModel Vm, Type Body, string Submit)[] cases =
        [
            (new SaveStashViewModel(f.Tab, false), typeof(SaveStashView), "Save Stash"),
            (new SaveStashViewModel(f.Tab, true), typeof(SaveStashView), "Save Snapshot"),
            (new StashFilesViewModel(f.Tab, ["a.txt"]), typeof(StashFilesView), "Save Stash"),
            (new ApplyStashViewModel(f.Tab, stash), typeof(ApplyStashView), "Apply"),
            (new RenameStashViewModel(f.Tab, stash), typeof(RenameStashView), "Rename"),
            (new DeleteStashViewModel(f.Tab, [stash]), typeof(DeleteStashView), "Delete"),
            (new CommitPickViewModel(f.Tab, commits, [], revert: false), typeof(CommitPickView), "Cherry Pick 2 commits"),
            (new CommitPickViewModel(f.Tab, [commits[0]], commits, revert: true), typeof(CommitPickView), "Revert"),
        ];
        foreach (var (vm, body, submit) in cases)
        {
            var shown = DialogWindow.ShowAsync(vm, owner);
            Dispatcher.UIThread.RunJobs();
            var d = (DialogWindow)owner.OwnedWindows.Single();
            d.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Assert.Single(d.GetVisualDescendants(), v => v.GetType() == body);
            var submitButton = d.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "SubmitButton");
            Assert.Equal(submit, submitButton.Content);
            if (vm is RenameStashViewModel)
            {
                Assert.False(submitButton.IsEffectivelyEnabled); // unchanged message
                var box = d.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "MessageBox");
                Assert.Equal("s", box.Text);
            }
            if (vm is CommitPickViewModel { IsRevert: true })
            {
                Assert.True(d.GetVisualDescendants().OfType<ComboBox>().Single().IsVisible); // parent drop-down for a merge
                Assert.False(d.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Name == "OriginBox").IsVisible);
            }
            if (vm is CommitPickViewModel { IsRevert: false } pick)
            {
                pick.CommitChanges = false;
                Dispatcher.UIThread.RunJobs();
                Assert.False(d.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Name == "OriginBox").IsEffectivelyEnabled);
            }
            if (vm is ApplyStashViewModel apply)
            {
                Assert.Contains(d.GetVisualDescendants().OfType<RefView>(), r => r.Text == "stash@{0}");
                apply.DeleteAfterApply = true;
                Dispatcher.UIThread.RunJobs();
                Assert.True(d.GetVisualDescendants().OfType<LucideIcon>().Single(i => i.Name == "PopWarning").IsVisible);
            }
            vm.Cancel();
            Dispatcher.UIThread.RunJobs();
            Assert.False(await shown);
        }
        owner.Close();
    }
}
