using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using GerGit.Core;
using GerGit.Core.Git;
using GerGit.ViewModels;
using GerGit.Views;

namespace GerGit.Tests.Ui;

public sealed class DetailsTests : IDisposable
{
    const int Noon = 43200; // commit dates at noon UTC, so the local date is 1 Jan 2020 in every time zone

    readonly TempRepo _repo = new();
    readonly string _c1, _c2, _c3;

    public DetailsTests()
    {
        var renamed = string.Join("\n", Enumerable.Range(1, 20).Select(i => $"line {i}")) + "\n";
        _c1 = _repo.Commit("first", Noon,
            ("a.txt", "one\nfoo(a, b)\nthree\n"), ("d.txt", "gone\n"), ("r.txt", renamed));
        File.Delete(Path.Combine(_repo.Path, "d.txt"));
        File.Move(Path.Combine(_repo.Path, "r.txt"), Path.Combine(_repo.Path, "r2.txt"));
        _c2 = _repo.Commit("second\n\nbody line", Noon + 1,
            ("a.txt", "one\nfoo(a, c)\nthree\n"), ("n.txt", "new\n"), ("bin.dat", "x\0\0\0\0\0\0y"));
        _c3 = _repo.Commit("third", Noon + 2, ("n.txt", "new\nmore\n"));
    }

    public void Dispose() => _repo.Dispose();

    async Task<(RepoTabViewModel Tab, Window Window, DetailsView View)> OpenAsync()
    {
        var tab = new RepoTabViewModel(await GitRepository.OpenAsync(_repo.Path));
        var view = new DetailsView { DataContext = tab.Details };
        var window = new Window { Width = 1000, Height = 600, Content = view };
        window.Show();
        return (tab, window, view);
    }

    static void Settle(Window w)
    {
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    static IEnumerable<string?> VisibleTexts(Visual root) =>
        root.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text);

    [AvaloniaFact]
    public async Task Single_commit_shows_commit_tab_texts_and_changed_files()
    {
        var (tab, window, view) = await OpenAsync();
        var d = tab.Details;
        await d.ShowAsync([_c2]);
        Settle(window);

        Assert.Equal(DetailsViewModel.CommitTab, d.SelectedTab);
        Assert.False(d.ShowSingleHeader); // compact header hidden on the Commit tab
        Assert.Equal("second", view.FindControl<SelectableTextBlock>("SubjectText")!.Text);
        Assert.Equal("body line", view.FindControl<SelectableTextBlock>("DescriptionText")!.Text);
        Assert.Equal(_c2, view.FindControl<SelectableTextBlock>("ShaText")!.Text);
        Assert.False(d.ShowCommitter);
        Assert.StartsWith("1 Jan 2020 ", d.AuthorDateText);
        var texts = view.GetVisualDescendants().OfType<Visual>().Where(v => v.IsEffectivelyVisible).ToList();
        Assert.Contains(texts.OfType<SelectableTextBlock>(), t => t.Text == "Test User");
        Assert.Contains(texts.OfType<Button>(), b => b.Content as string == _c1[..7]); // PARENTS

        var files = VisibleTexts(view.FindControl<ItemsControl>("CommitFiles")!).ToList();
        Assert.Equal(["a.txt", "bin.dat", "d.txt", "n.txt", "r.txt → r2.txt"], files);
        Assert.Equal(
            [ChangeKind.Modified, ChangeKind.Added, ChangeKind.Deleted, ChangeKind.Added, ChangeKind.Renamed],
            d.Files.Select(f => f.Kind));

        // Changes tab: one-row compact header, first file selected and its diff loaded.
        d.SelectedTab = DetailsViewModel.ChangesTab;
        await d.Diff.Loading;
        Settle(window);
        Assert.True(d.ShowSingleHeader);
        Assert.Contains(VisibleTexts(view), t => t == _c2[..7]);
        Assert.Equal("a.txt", d.SelectedFile?.Path);
        window.Close();
        tab.Dispose();
    }

    [AvaloniaFact]
    public async Task Selected_file_shows_diff_lines_with_backgrounds_and_word_ranges()
    {
        var (tab, window, view) = await OpenAsync();
        var d = tab.Details;
        await d.ShowAsync([_c2]);
        d.SelectedTab = DetailsViewModel.ChangesTab;
        await d.Diff.Loading;
        Settle(window);

        var doc = d.Diff.Document!;
        Assert.Equal(DiffState.Text, d.Diff.State);
        Assert.Equal(
            [(DocLineKind.HunkHeader, "@@ -1,3 +1,3 @@"), (DocLineKind.Context, "one"), (DocLineKind.Removed, "foo(a, b)"),
             (DocLineKind.Added, "foo(a, c)"), (DocLineKind.Context, "three")],
            doc.Lines.Select(l => (l.Kind, l.Text)));
        Assert.Equal([new TextRange(7, 1)], doc.Lines[2].WordRanges);
        Assert.Equal([new TextRange(7, 1)], doc.Lines[3].WordRanges);
        Assert.Equal(((int?)2, (int?)null, (int?)null, (int?)2), (doc.Lines[2].OldNumber, doc.Lines[2].NewNumber, doc.Lines[3].OldNumber, doc.Lines[3].NewNumber));

        var editor = view.GetVisualDescendants().OfType<TextEditor>().Single(e => e.Name == "Editor");
        Assert.Equal("@@ -1,3 +1,3 @@\none\nfoo(a, b)\nfoo(a, c)\nthree", editor.Text);

        // Rendered frame: line backgrounds right of the text, word colour on the changed character.
        using var frame = window.CaptureRenderedFrame()!;
        var tv = editor.TextArea.TextView;
        Assert.Equal(Color.Parse("#633F3E"), PixelAtLine(frame, window, tv, 3, tv.Bounds.Width - 5));
        Assert.Equal(Color.Parse("#3A5C3F"), PixelAtLine(frame, window, tv, 4, tv.Bounds.Width - 5));
        Assert.Equal(Color.Parse("#282828"), PixelAtLine(frame, window, tv, 2, tv.Bounds.Width - 5));
        var vl = tv.GetVisualLine(3)!;
        var wordRect = AvaloniaEdit.Rendering.BackgroundGeometryBuilder.GetRectsFromVisualSegment(tv, vl, 7, 8).Single();
        Assert.Equal(Color.Parse("#9F4247"), PixelAtLine(frame, window, tv, 3, wordRect.X + 1));

        // Up/Down in the file list changes the diff: bin.dat is a binary file.
        var list = view.FindControl<ListBox>("FileList")!;
        list.ContainerFromIndex(0)!.Focus();
        window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        await d.Diff.Loading;
        Settle(window);
        Assert.Equal("bin.dat", d.SelectedFile?.Path);
        Assert.Equal(DiffState.Binary, d.Diff.State);
        Assert.Contains(VisibleTexts(view), t => t == ".dat"); // unknown-binary card

        // Deleted file: removed lines only; renamed file without content changes: empty document.
        window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        await d.Diff.Loading;
        Assert.Equal([(DocLineKind.Removed, (int?)1, (int?)null)],
            d.Diff.Document!.Lines.Where(l => l.Kind != DocLineKind.HunkHeader).Select(l => (l.Kind, l.OldNumber, l.NewNumber)));
        d.SelectedFile = d.Files.Single(f => f.Kind == ChangeKind.Renamed);
        await d.Diff.Loading;
        Settle(window); // an empty document must lay out (line-number margin of no lines)
        Assert.Equal(DiffState.Text, d.Diff.State);
        Assert.Empty(d.Diff.Document!.Lines);
        Assert.Equal(("r.txt", " → ", "r2.txt"), (d.Diff.OldName, d.Diff.Arrow, d.Diff.Name));
        window.Close();
        tab.Dispose();
    }

    static Color PixelAtLine(WriteableBitmap frame, Window window, AvaloniaEdit.Rendering.TextView tv, int line, double x)
    {
        var vl = tv.GetVisualLine(line)!;
        var p = tv.TranslatePoint(new Point(x, vl.VisualTop - tv.ScrollOffset.Y + 1), window)!.Value;
        using var fb = frame.Lock();
        var at = (int)p.Y * fb.RowBytes + (int)p.X * 4;
        byte B(int i) => Marshal.ReadByte(fb.Address, at + i);
        return fb.Format == PixelFormat.Rgba8888 ? Color.FromArgb(B(3), B(0), B(1), B(2)) : Color.FromArgb(B(3), B(2), B(1), B(0));
    }

    [AvaloniaFact]
    public async Task Two_commits_show_range_older_to_newer_and_more_show_message()
    {
        var (tab, window, view) = await OpenAsync();
        var d = tab.Details;
        await d.ShowAsync([_c3, _c1]); // row order: newer first
        Settle(window);

        Assert.True(d.IsRange);
        Assert.Equal(DetailsViewModel.ChangesTab, d.SelectedTab);
        Assert.False(d.IsCommitTabEnabled);
        Assert.Equal((_c1[..7], _c3[..7]), (d.SourceHeader!.Sha7, d.DestinationHeader!.Sha7));
        Assert.Equal(["a.txt", "bin.dat", "d.txt", "n.txt", "r2.txt"], d.Files.Select(f => f.Path));
        d.SelectedFile = d.Files.Single(f => f.Path == "n.txt");
        await d.Diff.Loading;
        Assert.Equal(["new", "more"], d.Diff.Document!.Lines.Where(l => l.Kind == DocLineKind.Added).Select(l => l.Text));

        await d.ReverseCommitsCommand.ExecuteAsync(null);
        Assert.Equal(_c3[..7], d.SourceHeader!.Sha7);
        await d.Diff.Loading;
        Assert.Equal(["new", "more"], d.Diff.Document!.Lines.Where(l => l.Kind == DocLineKind.Removed).Select(l => l.Text));

        await d.ShowAsync([_c3, _c2, _c1]);
        Settle(window);
        Assert.Equal("Select two commits to see difference between them", view.FindControl<TextBlock>("OverlayTitle")!.Text);
        Assert.Equal("3 commits selected", view.FindControl<SelectableTextBlock>("OverlayText")!.Text);
        Assert.True(view.FindControl<TextBlock>("OverlayTitle")!.IsEffectivelyVisible);
        Assert.False(d.IsChangesTabEnabled);

        await d.ShowAsync([_c2]);
        Assert.False(d.HasOverlay);
        Assert.Equal(DetailsViewModel.ChangesTab, d.SelectedTab); // the active tab is kept
        window.Close();
        tab.Dispose();
    }

    [AvaloniaFact]
    public async Task Root_commit_shows_all_files_added()
    {
        var (tab, window, _) = await OpenAsync();
        var d = tab.Details;
        await d.ShowAsync([_c1]);
        Assert.Empty(d.Commit!.Parents);
        Assert.All(d.Files, f => Assert.Equal(ChangeKind.Added, f.Kind));
        await d.Diff.Loading;
        Assert.Equal("a.txt", d.SelectedFile?.Path);
        Assert.Equal([(1, "one"), (2, "foo(a, b)"), (3, "three")],
            d.Diff.Document!.Lines.Where(l => l.Kind == DocLineKind.Added).Select(l => (l.NewNumber ?? 0, l.Text)));

        d.Diff.EntireFile = true; // reloads; no hunk header in entire-file mode
        await d.Diff.Loading;
        Assert.DoesNotContain(d.Diff.Document!.Lines, l => l.Kind == DocLineKind.HunkHeader);
        window.Close();
        tab.Dispose();
    }

    [AvaloniaFact]
    public async Task Unknown_commit_shows_error_overlay()
    {
        var (tab, window, _) = await OpenAsync();
        await tab.Details.ShowAsync(["0123456789012345678901234567890123456789"]);
        Assert.Equal("Error", tab.Details.OverlayTitle);
        Assert.StartsWith("Commit not found", tab.Details.OverlayText);
        window.Close();
        tab.Dispose();
    }

    [AvaloniaFact]
    public async Task Entire_file_starts_at_first_change_and_navigation_puts_groups_at_the_top()
    {
        var lines = Enumerable.Range(1, 200).Select(i => $"line {i}").ToList();
        _repo.Commit("long", Noon + 3, ("long.txt", string.Join("\n", lines) + "\n"));
        lines[99] = "changed 100";
        lines[149] = "changed 150";
        var c5 = _repo.Commit("edit", Noon + 4, ("long.txt", string.Join("\n", lines) + "\n"));
        var (tab, window, view) = await OpenAsync();
        var d = tab.Details;
        d.SelectedTab = DetailsViewModel.ChangesTab;
        d.Diff.EntireFile = true;
        await d.ShowAsync([c5]);
        await d.Diff.Loading;
        Settle(window);

        var editor = view.GetVisualDescendants().OfType<TextEditor>().Single(e => e.Name == "Editor");
        var tv = editor.TextArea.TextView;
        int FirstVisible() { Settle(window); return tv.GetDocumentLineByVisualTop(tv.ScrollOffset.Y + 1)!.LineNumber - 1; }
        var sv = editor.FindDescendantOfType<ScrollViewer>()!;
        Assert.True(sv.Offset.Y > 0);
        Assert.Equal(99, FirstVisible()); // the first changed line (entire file: no hunk header)

        view.FindControl<DiffView>("Diff")!.GoToChange(true);
        Settle(window);
        Assert.True(Math.Abs(sv.Offset.Y - tv.GetVisualTopByDocumentLine(149)) < 1, $"{sv.Offset.Y} {tv.ScrollOffset.Y} {tv.GetVisualTopByDocumentLine(149)}");
        Assert.Equal(148, FirstVisible()); // second group, 2 context lines above the removed line 150
        view.FindControl<DiffView>("Diff")!.GoToChange(false);
        Assert.Equal(97, FirstVisible());
        window.Close();
        tab.Dispose();
    }

    [AvaloniaFact]
    public async Task Too_large_diff_shows_fallback_and_commit_tab_limits_files_to_100()
    {
        var files = Enumerable.Range(0, 100).Select(i => ($"f{i:D3}.txt", "x")).Append(("wide.txt", new string('x', 2048))).ToArray();
        var c4 = _repo.Commit("many", Noon + 3, files);
        var (tab, window, _) = await OpenAsync();
        var d = tab.Details;
        await d.ShowAsync([c4]);
        Assert.Equal(100, d.CommitTabFiles.Count);
        Assert.True(d.HasFileLimitNote);
        Assert.Equal("See full list of changed files (101) in the Changes tab", d.FileLimitNote);

        d.SelectedFile = d.Files.Single(f => f.Path == "wide.txt");
        await d.Diff.Loading;
        Assert.Equal(DiffState.TooLarge, d.Diff.State);
        Assert.False(d.Diff.ShowHeader);
        d.Diff.LoadDiffCommand.Execute(null);
        await d.Diff.Loading;
        Assert.Equal(DiffState.Text, d.Diff.State);
        window.Close();
        tab.Dispose();
    }

    [AvaloniaFact]
    public async Task Newest_selection_wins_and_the_diff_does_not_blank_between_commits()
    {
        var (tab, window, _) = await OpenAsync();
        var d = tab.Details;
        await d.ShowAsync([_c2]);
        await d.Diff.Loading;
        var states = new List<DiffState>();
        d.Diff.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(DiffViewModel.State)) states.Add(d.Diff.State); };

        var first = d.ShowAsync([_c1]);
        var second = d.ShowAsync([_c3]);
        await Task.WhenAll(first, second);
        await d.Diff.Loading;
        Assert.Equal(_c3, d.Commit!.Sha);
        Assert.Equal("n.txt", d.SelectedFile?.Path); // a.txt is not in c3: first file
        Assert.DoesNotContain(DiffState.None, states);
        window.Close();
        tab.Dispose();
    }

    [AvaloniaFact]
    public async Task Stash_hides_the_sha_row()
    {
        _repo.Write("a.txt", "stashed\n");
        _repo.Git("stash");
        var stash = _repo.Git("rev-parse", "stash@{0}");
        var (tab, window, view) = await OpenAsync();
        await tab.RefreshAsync(); // reads the stash list
        await tab.Details.ShowAsync([stash]);
        Settle(window);
        Assert.True(tab.Details.IsStash);
        Assert.False(view.FindControl<SelectableTextBlock>("ShaText")!.IsEffectivelyVisible);
        window.Close();
        tab.Dispose();
    }

    [Fact]
    public void Navigation_targets_merge_close_runs_and_include_two_context_lines()
    {
        DocLine L(DocLineKind k) => new(k, "", null, null, []);
        var c = DocLineKind.Context; var a = DocLineKind.Added; var r = DocLineKind.Removed; var h = DocLineKind.HunkHeader;
        var doc = new DiffDocument(new[] { h, c, c, c, r, a, c, c, c, a, c, c, c, c, a, h, a }.Select(L).ToList(), []);
        // runs at 4-5, 9 (3 lines gap: merged), 14 (4 lines gap: new group, starts 2 lines earlier), 16 (after a header: 1 line gap: merged)
        Assert.Equal([2, 12], DiffViewModel.NavigationTargets(doc));
    }
}
