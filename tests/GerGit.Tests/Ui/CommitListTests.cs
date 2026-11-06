using System.Diagnostics;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using GerGit.Controls;
using GerGit.Core;
using GerGit.Core.Git;
using GerGit.ViewModels;
using GerGit.Views;

namespace GerGit.Tests.Ui;

public class CommitListTests(ITestOutputHelper output)
{
    const double Row = CommitListPanel.RowHeight;
    internal const int SyntheticCount = 60_000, SideBranches = 4;

    /// <summary>A 60,000-commit history made with git fast-import (a few seconds), shared by the paging and smoothness tests; read-only.
    /// Four long-running side branches commit in turn and are merged into main one after another, so rows have several lanes.
    /// Main's tip is c60000 (a merge), the newest commit.</summary>
    internal static readonly Lazy<string> Synthetic = new(() =>
    {
        var repo = new TempRepo();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => repo.Dispose();
        var stream = new StringBuilder();
        int mainTip = 0;
        var sideTips = new int[SideBranches + 1];
        for (int i = 1; i <= SyntheticCount; i++)
        {
            int r = i % 10;
            string branch = r is >= 1 and <= SideBranches && i > 10 ? $"side{r}" : "main";
            stream.Append($"commit refs/heads/{branch}\nmark :{i}\ncommitter T <t@example.com> {1577836800 + i} +0000\ndata {$"c{i}".Length}\nc{i}\n");
            if (branch == "main")
            {
                if (mainTip > 0) stream.Append($"from :{mainTip}\n");
                int side = i / 10 % SideBranches + 1;
                if (r == 0 && sideTips[side] > 0) stream.Append($"merge :{sideTips[side]}\n");
                mainTip = i;
            }
            else
            {
                stream.Append($"from :{(sideTips[r] > 0 ? sideTips[r] : mainTip)}\n");
                sideTips[r] = i;
            }
            stream.Append('\n');
        }
        var psi = new ProcessStartInfo("git", ["fast-import", "--quiet"]) { WorkingDirectory = repo.Path, RedirectStandardInput = true };
        psi.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        using var p = Process.Start(psi)!;
        p.StandardInput.Write(stream.ToString());
        p.StandardInput.Close();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException("git fast-import failed");
        return repo.Path;
    });

    internal static async Task<RepoTabViewModel> OpenTab(string path, List<IReadOnlyList<string>> shown)
    {
        var tab = new RepoTabViewModel(await GitRepository.OpenAsync(path));
        tab.CommitList.ShowDetails = shas =>
        {
            shown.Add(shas);
            return Task.CompletedTask;
        };
        return tab;
    }

    internal static async Task Load(RepoTabViewModel tab) =>
        await tab.CommitList.LoadAsync(await tab.Repo.ReadRefsAsync(), await tab.Repo.ReadStashesAsync());

    internal static async Task WaitFor(Func<bool> condition, int timeoutMs = 15_000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("condition not met");
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(); // renders a frame, which requests headers and pages
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
    }

    /// <summary>Lets queued UI work and background pages run for a while.</summary>
    internal static async Task Settle(int ms = 400)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
    }

    internal static (Window Window, CommitListPanel Panel) Show(CommitListViewModel vm, double width = 1000, double height = 400)
    {
        var view = new CommitListView { DataContext = vm };
        var window = new Window { Width = width, Height = height, Content = view };
        window.Show();
        window.UpdateLayout();
        return (window, view.FindControl<CommitListPanel>("List")!);
    }

    [AvaloniaFact]
    public async Task Rows_labels_marks_colours_headers_and_selection()
    {
        using var repo = new TempRepo();
        var c1 = repo.Commit("first", 10, ("a.txt", "1"));
        repo.Git("tag", "v1.0");
        repo.Git("remote", "add", "origin", "https://example.com/x.git");
        repo.Git("update-ref", "refs/remotes/origin/main", c1);
        repo.Git("branch", "--set-upstream-to=origin/main", "main");
        var c2 = repo.Commit("feat: second", 20, ("a.txt", "2"));
        repo.Git("checkout", "-q", "-b", "side", c1);
        var s1 = repo.Commit("side start", 15, ("s.txt", "1"));
        repo.Git("branch", "old-side");
        var c3 = repo.Commit("side work", 30, ("s.txt", "s"));
        repo.Git("checkout", "-q", "-b", "topic", c2);
        var c5 = repo.Commit("topic work\n\nwith a body", 40, ("t.txt", "t"));
        repo.Git("checkout", "-q", "main");
        var date = "@1577836850 +0000";
        repo.GitEnv(new() { ["GIT_AUTHOR_DATE"] = date, ["GIT_COMMITTER_DATE"] = date }, "merge", "-q", "--no-ff", "-m", "Merge topic", "topic");
        var m = repo.Git("rev-parse", "HEAD");
        repo.Write("a.txt", "dirty");
        repo.Git("stash", "-q");
        var stash = repo.Git("rev-parse", "refs/stash");

        var shown = new List<IReadOnlyList<string>>();
        using var tab = await OpenTab(repo.Path, shown);
        var vm = tab.CommitList;
        await Load(tab);

        Assert.Equal([stash, m, c5, c3, c2, s1, c1], vm.Rows.Select(r => r.Sha));
        Assert.Equal("stash@{0}", Assert.Single(vm.Rows[0].Labels).Text);
        Assert.True(vm.Rows[1].IsHead && vm.Rows[1].Graph.IsMerge);
        Assert.Contains(vm.Rows[1].Labels, l => l is { Kind: RefLabelKind.LocalBranch, Text: "main", IsActive: true });
        Assert.False(vm.Rows[3].IsReachable); // side branch: dimmed
        Assert.Equal(UpstreamMark.Ahead, vm.Rows[1].Upstream);
        Assert.Equal(UpstreamMark.None, vm.Rows[6].Upstream);
        Assert.Contains(vm.Rows[6].Labels, l => l is { Kind: RefLabelKind.Tag, Text: "v1.0" });
        Assert.Contains(vm.Rows[6].Labels, l => l is { Kind: RefLabelKind.RemoteBranch, Text: "origin/main" });

        // Nothing was selected before: HEAD is selected and handed to the details pane.
        Assert.Equal(m, vm.SelectedSha);
        Assert.Equal([m], shown[^1]);

        var (window, panel) = Show(vm);
        await WaitFor(() => vm.GetHeader(5) is not null);
        Assert.Equal("topic work", vm.GetHeader(2)!.Subject);
        Assert.True(vm.GetHeader(2)!.HasBody);
        Assert.Equal("Test User", vm.GetHeader(1)!.AuthorName);
        Assert.NotNull(vm.GetHeader(0)); // stash rows take their text from the stash itself

        using (var frame = window.CaptureRenderedFrame()!)
        {
            // The selected (unfocused) row is painted in the inactive selection colour.
            Assert.Equal(Color("Color.Row.BgSelectedInactive"), Pixel(frame, (int)panel.Bounds.Width - 6, (int)(1 * Row + 12)));

            // Row s1: its lane moved left, so the dot colour (lane id) and the label colour (dot column) differ.
            var g = vm.Rows[5].Graph;
            Assert.NotEqual(g.DotLaneId % 13, g.DotColumn % 13);
            AssertNear(Color($"Color.Lane.{g.DotLaneId % 13}"), Pixel(frame, 8 + 12 * g.DotColumn, (int)(5 * Row + 11)));
            int labelLeft = 8 + 12 * Math.Max(1, g.Lines.Count) + 3; // graph cell, then the label's 3 px margin
            AssertNear(Color($"Color.RefLabel.Border.{g.DotColumn % 13}"), Pixel(frame, labelLeft, (int)(5 * Row + 11)));

            // Text colours: dimmed SHA text on the unreachable side commit, normal text on c2.
            int shaX = (int)panel.Bounds.Width - 4 - 4 - 129 - 70 + 5;
            Assert.Contains(Color("Color.Revlist.UnreachableFg"), Pixels(frame, shaX, (int)(5 * Row), 60, (int)Row));
            Assert.DoesNotContain(Color("Color.Text.Primary"), Pixels(frame, shaX, (int)(5 * Row), 60, (int)Row));
            Assert.Contains(Color("Color.Text.Primary"), Pixels(frame, shaX, (int)(4 * Row), 60, (int)Row));
        }

        // Sidebar selection leaves the focus alone; a parent link in the details pane (focus: true) moves it to the list.
        vm.SelectCommit(c3);
        Assert.Equal([c3], shown[^1]);
        Assert.False(panel.IsKeyboardFocusWithin);
        vm.SelectCommit(c2, focus: true);
        Assert.Equal([c2], shown[^1]);
        Assert.True(panel.IsKeyboardFocusWithin);

        Point At(int row) => new(300, row * Row + 11);
        void Click(int row, RawInputModifiers mods = RawInputModifiers.None)
        {
            window.MouseDown(At(row), MouseButton.Left, mods);
            window.MouseUp(At(row), MouseButton.Left, mods);
        }

        Click(2);
        Assert.Equal([c5], shown[^1]);
        Click(4, RawInputModifiers.Control);
        Assert.Equal([c5, c2], shown[^1]);
        Click(5, RawInputModifiers.Shift);
        Assert.Equal([c2, s1], shown[^1]);
        Assert.True(panel.IsKeyboardFocusWithin);
        using (var frame = window.CaptureRenderedFrame()!)
            Assert.Equal(Color("Color.Row.BgSelected"), Pixel(frame, (int)panel.Bounds.Width - 6, (int)(4 * Row + 12)));

        window.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
        Assert.Equal([c2], shown[^1]);
        window.KeyPress(Key.Down, RawInputModifiers.Shift, PhysicalKey.ArrowDown, null);
        Assert.Equal([c2, s1], shown[^1]);
        window.KeyPress(Key.Home, RawInputModifiers.None, PhysicalKey.Home, null);
        Assert.Equal([stash], shown[^1]);
        window.KeyPress(Key.End, RawInputModifiers.None, PhysicalKey.End, null);
        Assert.Equal([c1], shown[^1]);
        window.KeyPress(Key.PageUp, RawInputModifiers.None, PhysicalKey.PageUp, null);
        Assert.Equal([stash], shown[^1]);

        // Clicking empty space below the rows changes nothing.
        int calls = shown.Count;
        window.MouseDown(new Point(300, 10 * Row), MouseButton.Left);
        window.MouseUp(new Point(300, 10 * Row), MouseButton.Left);
        Assert.Equal(calls, shown.Count);

        vm.SelectCommit(c3);

        // A reload (new commit) keeps the selection by SHA.
        var c6 = repo.Commit("sixth", 60, ("a.txt", "6"));
        await Load(tab);
        Assert.Equal([c6, stash, m], vm.Rows.Take(3).Select(r => r.Sha));
        Assert.Equal(c3, vm.SelectedSha);
        Assert.Equal([c3], shown[^1]);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Empty_repository_shows_an_empty_list()
    {
        using var repo = new TempRepo();
        var shown = new List<IReadOnlyList<string>>();
        using var tab = await OpenTab(repo.Path, shown);
        await Load(tab);
        Assert.Empty(tab.CommitList.Rows);
        Assert.Null(tab.CommitList.SelectedSha);
        var (window, _) = Show(tab.CommitList);
        window.CaptureRenderedFrame()?.Dispose();
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_failed_header_block_is_logged_once_and_not_retried_every_frame()
    {
        using var repo = new TempRepo();
        repo.Commit("first", 1, ("a.txt", "1"));
        var shown = new List<IReadOnlyList<string>>();
        using var tab = await OpenTab(repo.Path, shown);
        int calls = 0;
        tab.CommitList.ReadHeaders = _ =>
        {
            calls++;
            throw new IOException("git could not start");
        };
        await Load(tab);
        for (int i = 0; i < 5; i++)
        {
            tab.CommitList.OnRowsShown(0, 0);
            await Settle(50);
        }
        Assert.Equal(1, calls);
        Assert.Null(tab.CommitList.GetHeader(0));
    }

    [AvaloniaFact]
    public async Task Paging_loads_the_initial_count_then_more_when_a_row_near_the_end_is_shown()
    {
        var shown = new List<IReadOnlyList<string>>();
        using var tab = await OpenTab(Synthetic.Value, shown);
        var vm = tab.CommitList;
        await Load(tab);
        Assert.Equal(CommitListViewModel.PageSize, vm.Rows.Count); // LoadAsync returns after the first page
        Assert.Equal(0, vm.IndexOf(vm.SelectedSha!));               // HEAD selected

        await WaitFor(() => vm.Rows.Count >= CommitListViewModel.InitialCount);
        vm.OnRowsShown(100, 130); // far from the end
        await Settle();
        Assert.Equal(CommitListViewModel.InitialCount, vm.Rows.Count);

        vm.OnRowsShown(45_100, 45_130); // within 5,000 rows of the end
        await WaitFor(() => vm.Rows.Count == SyntheticCount);
        Assert.Equal(SyntheticCount, vm.Rows.Select(r => r.Sha).Distinct().Count());
    }

    [AvaloniaFact]
    public async Task A_new_load_cancels_the_old_walk_and_drops_its_pages()
    {
        var shown = new List<IReadOnlyList<string>>();
        using var tab = await OpenTab(Synthetic.Value, shown);
        var vm = tab.CommitList;
        await Load(tab); // the rest of the initial load is now running in the background
        var first = vm.WalkToken;
        await Load(tab);
        Assert.True(first.IsCancellationRequested);

        await WaitFor(() => vm.Rows.Count >= CommitListViewModel.InitialCount);
        await Settle();
        Assert.Equal(CommitListViewModel.InitialCount, vm.Rows.Count); // no page of the old walk was appended
        Assert.Equal(vm.Rows.Count, vm.Rows.Select(r => r.Sha).Distinct().Count());
        Assert.Equal("c60000", (await tab.Repo.ReadHeadersAsync([vm.Rows[0].Sha]))[0].Subject);
    }

    [AvaloniaFact]
    public async Task Closing_the_tab_stops_the_walk()
    {
        var shown = new List<IReadOnlyList<string>>();
        var tab = await OpenTab(Synthetic.Value, shown);
        await Load(tab);
        tab.Dispose();
        Assert.True(tab.CommitList.WalkToken.IsCancellationRequested);
        await Settle(800);
        Assert.Equal(CommitListViewModel.PageSize, tab.CommitList.Rows.Count);
    }

    [Theory]
    [InlineData("[core] fix", 6)]
    [InlineData("fix: crash on start", 3)]
    [InlineData(":x y", 0)]
    [InlineData("plain subject", 0)]
    public void Bold_prefix_follows_the_subject_rules(string subject, int expected) =>
        Assert.Equal(expected, CommitListPanel.BoldPrefixLength(subject));

    [Fact]
    public void Code_spans_and_initials()
    {
        Assert.Equal([(4, 5), (14, 3)], CommitListPanel.CodeSpans("use `foo` and `x` `"));
        Assert.Equal("JD", GerGit.Hosting.Avatars.Initials("John Mid Doe"));
        Assert.Equal("jm", GerGit.Hosting.Avatars.Initials("john mid doe"));
        Assert.Equal("?", GerGit.Hosting.Avatars.Initials(""));
    }

    /// <summary>Smoothness check on the synthetic 60k-commit history with several lanes; always runs.</summary>
    [AvaloniaFact]
    public Task Large_history_shows_first_page_fast_and_renders_deep_rows_quickly() =>
        AssertSmooth(Synthetic.Value);

    /// <summary>The same check on a real big history (e.g. a git/git clone, 85k commits) named by GERGIT_BIG_REPO.</summary>
    [AvaloniaFact]
    public Task Real_big_history_shows_first_page_fast_and_renders_deep_rows_quickly()
    {
        var path = Environment.GetEnvironmentVariable("GERGIT_BIG_REPO");
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) Assert.Skip("Set GERGIT_BIG_REPO to a clone with >= 50,000 commits to run this check.");
        return AssertSmooth(path);
    }

    async Task AssertSmooth(string path)
    {
        var shown = new List<IReadOnlyList<string>>();
        using var tab = await OpenTab(path, shown);
        var vm = tab.CommitList;
        var refs = await tab.Repo.ReadRefsAsync();
        var stashes = await tab.Repo.ReadStashesAsync();

        var sw = Stopwatch.StartNew();
        await vm.LoadAsync(refs, stashes);
        long firstPage = sw.ElapsedMilliseconds;
        Assert.True(vm.Rows.Count > 0);
        await WaitFor(() => vm.Rows.Count >= CommitListViewModel.InitialCount);
        output.WriteLine($"first page after {firstPage} ms, initial load ({vm.Rows.Count} rows) after {sw.ElapsedMilliseconds} ms");
        Assert.InRange(firstPage, 0, 1500); // git itself needs ~0.6 s to sort 85k commits by date without a commit-graph file

        var (window, panel) = Show(vm, 1200, 800);
        // Deep in the history and within 5,000 rows of the end: headers of two blocks arrive and the next page loads.
        panel.Offset = 48_040 * Row;
        await WaitFor(() => vm.GetHeader(48_150) is not null && vm.Rows.Count > CommitListViewModel.InitialCount);
        panel.Offset = 48_100 * Row;
        await WaitFor(() => vm.GetHeader(48_130) is not null);

        // Scroll frames: each frame moves by 3 rows, so new rows are laid out like during real scrolling.
        // "UI thread" = the control's Render (what blocks input); "full" adds CPU rasterisation (the render thread's job in the app).
        // Wall-clock times also count the time other processes had the CPU (parallel builds and test runs): over a limit,
        // the frames are measured again, up to three rounds, and the best median counts; a real slowdown fails every round.
        const double UiLimit = 10, FullLimit = 32; // full: CPU raster on a shared test machine; the UI-thread limit is the real guard
        using var bitmap = new RenderTargetBitmap(new PixelSize(1200, 800));
        bitmap.Render(panel); // warm-up
        double uiMedian = double.MaxValue, fullMedian = double.MaxValue;
        for (int round = 1; round <= 3 && (uiMedian > UiLimit || fullMedian > FullLimit); round++)
        {
            var ui = new List<double>();
            var full = new List<double>();
            for (int i = 0; i < 21; i++)
            {
                panel.Offset = (48_000 + 3 * i) * Row;
                var t = Stopwatch.StartNew();
                using (var ctx = new DrawingGroup().Open()) panel.Render(ctx);
                ui.Add(t.Elapsed.TotalMilliseconds);
            }
            for (int i = 0; i < 21; i++)
            {
                panel.Offset = (48_100 + 3 * i) * Row;
                var t = Stopwatch.StartNew();
                bitmap.Render(panel);
                full.Add(t.Elapsed.TotalMilliseconds);
            }
            ui.Sort();
            full.Sort();
            uiMedian = Math.Min(uiMedian, ui[10]);
            fullMedian = Math.Min(fullMedian, full[10]);
            output.WriteLine($"scroll frames at row 48,000 ({vm.Rows[48_000].Graph.Lines.Count} graph lines per row), round {round}: " +
                             $"UI thread median {ui[10]:F2} ms (max {ui[^1]:F2}), full frame median {full[10]:F2} ms (max {full[^1]:F2})");
        }
        Assert.InRange(uiMedian, 0, UiLimit);
        Assert.InRange(fullMedian, 0, FullLimit);
        window.Close();
    }

    internal static uint Color(string key) =>
        Application.Current!.TryGetResource(key, ThemeVariant.Dark, out var v) && v is ISolidColorBrush b
            ? b.Color.ToUInt32()
            : throw new KeyNotFoundException(key);

    /// <summary>Equal up to blending rounding (each channel within 3).</summary>
    static void AssertNear(uint expected, uint actual) =>
        Assert.True(Enumerable.Range(0, 4).All(i => Math.Abs((int)(expected >> (8 * i) & 0xFF) - (int)(actual >> (8 * i) & 0xFF)) <= 3),
            $"expected #{expected:X8}, got #{actual:X8}");

    internal static uint Pixel(WriteableBitmap bitmap, int x, int y) => Pixels(bitmap, x, y, 1, 1).Single();

    /// <summary>ARGB values of a rectangle of the frame.</summary>
    internal static List<uint> Pixels(WriteableBitmap bitmap, int x, int y, int width, int height)
    {
        using var fb = bitmap.Lock();
        var result = new List<uint>();
        for (int j = y; j < y + height; j++)
            for (int i = x; i < x + width; i++)
            {
                var p = (uint)System.Runtime.InteropServices.Marshal.ReadInt32(fb.Address, j * fb.RowBytes + i * 4);
                // Little-endian: Bgra8888 reads as 0xAARRGGBB, Rgba8888 as 0xAABBGGRR.
                result.Add(fb.Format == Avalonia.Platform.PixelFormat.Rgba8888 ? (p & 0xFF00FF00) | ((p & 0xFF) << 16) | ((p >> 16) & 0xFF) : p);
            }
        return result;
    }
}
