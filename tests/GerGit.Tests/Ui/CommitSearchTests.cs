using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using GerGit.Controls;
using GerGit.ViewModels;
using GerGit.Views;
using static GerGit.Tests.Ui.CommitListTests;

namespace GerGit.Tests.Ui;

/// <summary>Search: the search bar of the commit list, against real temporary repositories.</summary>
public class CommitSearchTests(ITestOutputHelper output)
{
    const double Row = CommitListPanel.RowHeight;

    static List<string> Matches(CommitListViewModel vm) =>
        Enumerable.Range(0, vm.Rows.Count).Where(vm.IsMatch).Select(i => vm.Rows[i].Sha).ToList();

    static string Author(TempRepo repo, string message, int seconds, string name, string email)
    {
        var date = $"@{1577836800 + seconds} +0000";
        repo.GitEnv(new()
        {
            ["GIT_AUTHOR_DATE"] = date, ["GIT_COMMITTER_DATE"] = date, ["GIT_AUTHOR_NAME"] = name, ["GIT_AUTHOR_EMAIL"] = email,
        }, "commit", "-q", "--allow-empty", "-m", message);
        return repo.Git("rev-parse", "HEAD");
    }

    [AvaloniaFact]
    public async Task Matches_sha_author_email_message_and_ref_names_counts_and_draws_the_strip()
    {
        using var repo = new TempRepo();
        var c1 = repo.Commit("first", 10);
        repo.Git("tag", "rel-wombat");
        var c2 = Author(repo, "second", 20, "Zelda Quill", "zq@nowhere.example");
        var c3 = repo.Commit("third\n\nhidden body word quokka", 30);
        var c4 = repo.Commit("fourth", 40);
        repo.Git("branch", "feature/xyzzy");

        using var tab = await OpenTab(repo.Path, []);
        var vm = tab.CommitList;
        await Load(tab);
        Assert.Equal([c4, c3, c2, c1], vm.Rows.Select(r => r.Sha));

        Assert.Equal("", vm.MatchCountText);
        foreach (var (text, expected) in new[]
                 {
                     (c2[5..15].ToUpperInvariant(), c2), ("  zelda ", c2), ("NOWHERE.example", c2), ("QUOKKA", c3),
                     ("xyzzy", c4), ("Wombat", c1),
                 })
        {
            await vm.SearchAsync(text);
            Assert.Equal([expected], Matches(vm));
            Assert.Equal("1 match", vm.MatchCountText);
            Assert.Equal(expected, vm.SelectedSha); // the first match is selected
        }
        await vm.SearchAsync("test user");
        Assert.Equal([c4, c3, c1], Matches(vm));
        Assert.Equal("3 matches", vm.MatchCountText);
        await vm.SearchAsync("not-anywhere");
        Assert.Equal("0 matches", vm.MatchCountText);
        await vm.SearchAsync("   ");
        Assert.Equal("", vm.MatchCountText);
        Assert.Equal("", vm.SearchText);

        // The 4 px strip at the right edge of matching rows, and the match background behind the subject text.
        await vm.SearchAsync("third");
        var (window, panel) = Show(vm);
        await WaitFor(() => vm.GetHeader(3) is not null);
        uint match = Color("Color.Search.MatchBg");
        int stripX = (int)panel.Bounds.Width - 4 - 2;
        using (var frame = window.CaptureRenderedFrame()!)
        {
            Assert.Equal(match, Pixel(frame, stripX, (int)(1 * Row + 12)));
            Assert.NotEqual(match, Pixel(frame, stripX, (int)(2 * Row + 12)));
            Assert.Contains(match, Pixels(frame, 8, (int)Row, 600, (int)Row));
            Assert.DoesNotContain(match, Pixels(frame, 8, (int)(2 * Row), 600, (int)Row));
        }
        window.Close();
    }

    [AvaloniaFact]
    public async Task Next_and_previous_do_not_wrap_but_the_first_jump_wraps_once()
    {
        using var repo = new TempRepo();
        for (int i = 1; i <= 7; i++) repo.Commit(i % 2 == 0 ? $"fix {i}" : $"other {i}", i);
        using var tab = await OpenTab(repo.Path, []);
        var vm = tab.CommitList;
        await Load(tab); // rows 0..6 = commits 7..1: "fix" on rows 1, 3, 5

        vm.Click(4, false, false);
        await vm.SearchAsync("fix");
        Assert.Equal("3 matches", vm.MatchCountText);
        Assert.Equal([5], Selected(vm)); // first match at or after the selection
        await vm.NextMatchAsync();
        Assert.Equal([5], Selected(vm)); // no wrap
        await vm.PreviousMatchAsync();
        Assert.Equal([3], Selected(vm));
        await vm.PreviousMatchAsync();
        Assert.Equal([1], Selected(vm));
        await vm.PreviousMatchAsync();
        Assert.Equal([1], Selected(vm)); // no wrap
        await vm.NextMatchAsync();
        Assert.Equal([3], Selected(vm));

        vm.Click(6, false, false);
        await vm.SearchAsync("FIX ");
        Assert.Equal([1], Selected(vm)); // nothing at or after row 6: wraps once to the top
    }

    static List<int> Selected(CommitListViewModel vm) => vm.SelectedShas.Select(vm.IndexOf).ToList();

    [AvaloniaFact]
    public async Task Next_loads_pages_until_a_match_and_new_rows_are_searched_incrementally()
    {
        using var repo = new TempRepo(init: false);
        repo.Git("clone", "-q", Synthetic.Value, ".");
        var deep = repo.Git("log", "--all", "--format=%H", "--grep=^c5$");
        repo.Git("tag", "needle", deep); // c5 is in the last page, beyond the initial 50,000 rows

        using var tab = await OpenTab(repo.Path, []);
        var vm = tab.CommitList;
        await Load(tab);
        await WaitFor(() => vm.Rows.Count >= CommitListViewModel.InitialCount);
        await Settle();
        Assert.Equal(CommitListViewModel.InitialCount, vm.Rows.Count);

        await vm.SearchAsync("NEEDLE");
        Assert.Equal("0 matches", vm.MatchCountText);
        Assert.Equal(0, vm.IndexOf(vm.SelectedSha!)); // no match: the selection stays
        await vm.PreviousMatchAsync(); // previous never loads pages
        Assert.Equal(CommitListViewModel.InitialCount, vm.Rows.Count);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await vm.NextMatchAsync().WaitAsync(TimeSpan.FromSeconds(30));
        output.WriteLine($"Next over the last page: {sw.ElapsedMilliseconds} ms");
        Assert.Equal(deep, vm.SelectedSha);
        Assert.Equal("1 match", vm.MatchCountText);
        Assert.True(vm.Rows.Count > CommitListViewModel.InitialCount); // the match came from rows searched as they arrived

        await vm.NextMatchAsync().WaitAsync(TimeSpan.FromSeconds(30)); // the history is exhausted: nothing changes
        Assert.Equal(deep, vm.SelectedSha);
        Assert.Equal(SyntheticCount, vm.Rows.Count);
    }

    [AvaloniaFact]
    public async Task Keys_open_navigate_and_Esc_closes_and_clears_the_search()
    {
        using var repo = new TempRepo();
        for (int i = 1; i <= 7; i++) repo.Commit(i % 2 == 0 ? $"fix {i}" : $"other {i}", i);
        using var tab = await OpenTab(repo.Path, []);
        var vm = tab.CommitList;
        await Load(tab);
        var (window, panel) = Show(vm);
        var box = ((CommitListView)window.Content!).FindControl<TextBox>("SearchBox")!;

        window.MouseDown(new(300, 0 * Row + 11), MouseButton.Left);
        window.MouseUp(new(300, 0 * Row + 11), MouseButton.Left);
        Assert.True(panel.IsKeyboardFocusWithin);
        window.KeyPress(Key.F, RawInputModifiers.Control, PhysicalKey.F, "f");
        Assert.True(vm.IsSearchOpen);
        await Settle(50);
        Assert.True(box.IsKeyboardFocusWithin);

        window.KeyTextInput("fix");
        await WaitFor(() => vm.MatchCountText == "3 matches");
        await WaitFor(() => vm.SelectedSha == vm.Rows[1].Sha); // after the search finished
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await WaitFor(() => vm.SelectedSha == vm.Rows[3].Sha);
        window.KeyPress(Key.F3, RawInputModifiers.None, PhysicalKey.F3, null);
        await WaitFor(() => vm.SelectedSha == vm.Rows[5].Sha);
        window.KeyPress(Key.F3, RawInputModifiers.Shift, PhysicalKey.F3, null);
        Assert.Equal(vm.Rows[3].Sha, vm.SelectedSha);
        window.KeyPress(Key.Enter, RawInputModifiers.Shift, PhysicalKey.Enter, null);
        Assert.Equal(vm.Rows[1].Sha, vm.SelectedSha);
        Assert.True(box.IsKeyboardFocusWithin); // jumping keeps the focus in the box

        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        await Settle(300); // a late debounced search of the old text must not come back
        Assert.False(vm.IsSearchOpen);
        Assert.Equal("", box.Text ?? "");
        Assert.Equal("", vm.SearchText);
        Assert.Equal("", vm.MatchCountText);
        Assert.Empty(Matches(vm));
        Assert.True(panel.IsKeyboardFocusWithin); // back on the selected row
        Assert.Equal(vm.Rows[1].Sha, vm.SelectedSha);
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_new_text_cancels_the_running_search_and_typing_is_debounced()
    {
        using var repo = new TempRepo();
        for (int i = 1; i <= 3; i++) repo.Commit($"commit {i}", i);
        using var tab = await OpenTab(repo.Path, []);
        var vm = tab.CommitList;
        await Load(tab);

        var calls = new List<(string Text, CancellationToken Ct)>();
        vm.SearchCommits = async (shas, text, ct) =>
        {
            calls.Add((text, ct));
            if (text == "slow") await Task.Delay(Timeout.Infinite, ct);
            return [shas[^1]];
        };
        var slow = vm.SearchAsync("slow");
        Assert.True(vm.IsSearching);
        await vm.SearchAsync("fast");
        Assert.True(calls[0].Ct.IsCancellationRequested);
        await slow; // ends without touching the new result
        Assert.False(vm.IsSearching);
        Assert.Equal("fast", vm.SearchText);
        Assert.Equal("1 match", vm.MatchCountText);
        Assert.Equal([vm.Rows[^1].Sha], Matches(vm));

        calls.Clear();
        vm.SearchInput = "typed1";
        vm.SearchInput = "typed2 ";
        Assert.Empty(calls); // nothing before 100 ms
        await WaitFor(() => calls.Count > 0);
        await Settle(200);
        Assert.Equal(["typed2"], calls.Select(c => c.Text));
    }

    [AvaloniaFact]
    public async Task A_failed_search_shows_an_error_and_an_empty_result()
    {
        using var repo = new TempRepo();
        repo.Commit("first", 1);
        repo.Git("tag", "boom-tag");
        using var tab = await OpenTab(repo.Path, []);
        var errors = new List<string>();
        tab.ShowError = (_, text) =>
        {
            errors.Add(text);
            return Task.CompletedTask;
        };
        var vm = tab.CommitList;
        await Load(tab);
        vm.SearchCommits = (_, _, _) => throw new InvalidOperationException("git broke");

        await vm.SearchAsync("boom");
        Assert.Equal(["git broke"], errors);
        Assert.Empty(Matches(vm)); // not even the tag match stays
        Assert.Equal("0 matches", vm.MatchCountText);
        Assert.False(vm.IsSearching);
    }

    [AvaloniaFact]
    public async Task A_refresh_searches_the_rebuilt_list_again()
    {
        using var repo = new TempRepo();
        for (int i = 1; i <= 3; i++) repo.Commit($"fix {i}", i);
        using var tab = await OpenTab(repo.Path, []);
        var vm = tab.CommitList;
        await Load(tab);
        await vm.SearchAsync("fix");
        Assert.Equal("3 matches", vm.MatchCountText);

        var added = repo.Commit("another fix", 10);
        await Load(tab);
        await WaitFor(() => vm.MatchCountText == "4 matches" && !vm.IsSearching);
        Assert.True(vm.IsMatch(vm.IndexOf(added)));
        Assert.Equal(4, Matches(vm).Count);
    }

    [AvaloniaFact]
    public async Task Separator_characters_in_a_message_do_not_break_the_records()
    {
        using var repo = new TempRepo();
        var odd = repo.Commit("start\x1equux\x1fmore\n\nbody \x1e quux again", 1);
        var plain = repo.Commit("plain", 2);
        using var tab = await OpenTab(repo.Path, []);
        var vm = tab.CommitList;
        await Load(tab);

        await vm.SearchAsync("quux");
        Assert.Equal([odd], Matches(vm));
        Assert.Equal("1 match", vm.MatchCountText); // no stray record piece counted as a SHA
        var direct = await tab.Repo.SearchCommitsAsync([plain, odd], "more");
        Assert.Equal([odd], direct);
    }

    [AvaloniaFact]
    public async Task Highlights_of_matches_hidden_by_the_ellipsis_stay_in_their_column()
    {
        using var repo = new TempRepo();
        var date = "@1577836801 +0000";
        repo.GitEnv(new()
        {
            ["GIT_AUTHOR_DATE"] = date, ["GIT_COMMITTER_DATE"] = date,
            ["GIT_AUTHOR_NAME"] = "A very long author name that ends with zebra", ["GIT_AUTHOR_EMAIL"] = "a@example.com",
        }, "commit", "-q", "--allow-empty", "-m", "A subject " + string.Concat(Enumerable.Repeat("long ", 40)) + "zebra");
        using var tab = await OpenTab(repo.Path, []);
        var vm = tab.CommitList;
        await Load(tab);
        await vm.SearchAsync("zebra");
        Assert.Equal("1 match", vm.MatchCountText);

        var (window, panel) = Show(vm);
        await WaitFor(() => vm.GetHeader(0) is not null);
        uint match = Color("Color.Search.MatchBg");
        int width = (int)panel.Bounds.Width, col0End = width - 4 - 4 - 129 - 70 - 120 - 19;
        int authorEnd = col0End + 19 + 120;
        using (var frame = window.CaptureRenderedFrame()!)
        {
            Assert.Equal(match, Pixel(frame, width - 6, 12)); // the strip
            // Both matches are cut off, so none is drawn: nothing in the subject column, and nothing spills into the
            // avatar, author, SHA and date columns right of it (only the strip at the right edge).
            Assert.DoesNotContain(match, Pixels(frame, 8, 0, col0End - 8, (int)Row));
            Assert.DoesNotContain(match, Pixels(frame, col0End, 0, authorEnd - col0End, (int)Row));
            Assert.DoesNotContain(match, Pixels(frame, authorEnd, 0, width - 10 - authorEnd, (int)Row));
        }
        window.Close();
    }

    /// <summary>Repo for the Next tests: rows 0..6 = commits 7..1, "fix" on rows 1, 3, 5; HEAD (row 0) selected.</summary>
    static async Task<(TempRepo Repo, RepoTabViewModel Tab)> FixRepo()
    {
        var repo = new TempRepo();
        for (int i = 1; i <= 7; i++) repo.Commit(i % 2 == 0 ? $"fix {i}" : $"other {i}", i);
        var tab = await OpenTab(repo.Path, []);
        await Load(tab);
        return (repo, tab);
    }

    [AvaloniaFact]
    public async Task Next_pressed_while_the_search_runs_goes_to_the_second_match()
    {
        var (repo, tab) = await FixRepo();
        using var _ = repo;
        using var __ = tab;
        var vm = tab.CommitList;
        var real = vm.SearchCommits;
        var gate = new TaskCompletionSource();
        vm.SearchCommits = async (shas, text, ct) =>
        {
            await gate.Task;
            return await real(shas, text, ct);
        };

        var search = vm.SearchAsync("fix");
        var next = vm.NextMatchAsync();
        Assert.True(vm.IsSearching);
        gate.SetResult();
        await search.WaitAsync(TimeSpan.FromSeconds(10));
        await next.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(vm.Rows[3].Sha, vm.SelectedSha); // the search jumped to row 1, then Next to row 3

        var previous = vm.SearchAsync("fix"); // Previous waits for a running search too
        var back = vm.PreviousMatchAsync();
        await previous.WaitAsync(TimeSpan.FromSeconds(10));
        await back.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(vm.Rows[1].Sha, vm.SelectedSha); // the search kept row 3, then Previous went to row 1
    }

    [AvaloniaFact]
    public async Task Next_right_after_typing_searches_the_new_text_once()
    {
        var (repo, tab) = await FixRepo();
        using var _ = repo;
        using var __ = tab;
        var vm = tab.CommitList;
        var real = vm.SearchCommits;
        var calls = new List<string>();
        vm.SearchCommits = (shas, text, ct) =>
        {
            calls.Add(text);
            return real(shas, text, ct);
        };

        vm.SearchInput = "fix";
        await vm.NextMatchAsync().WaitAsync(TimeSpan.FromSeconds(10)); // within 100 ms: searches "fix" (jump to row 1), then Next
        Assert.Equal(["fix"], calls);
        Assert.Equal(vm.Rows[3].Sha, vm.SelectedSha);
        await Settle(200);
        Assert.Equal(["fix"], calls); // the debounced search of the same text did not run again
        Assert.Equal(vm.Rows[3].Sha, vm.SelectedSha);
    }

    [AvaloniaFact]
    public async Task A_waiting_Next_ends_when_its_search_is_replaced()
    {
        var (repo, tab) = await FixRepo();
        using var _ = repo;
        using var __ = tab;
        var vm = tab.CommitList;
        var real = vm.SearchCommits;
        var gate = new TaskCompletionSource();
        vm.SearchCommits = async (shas, text, ct) =>
        {
            await gate.Task;
            return await real(shas, text, ct);
        };

        var first = vm.SearchAsync("fix");
        var next = vm.NextMatchAsync(); // waits for the "fix" run
        var second = vm.SearchAsync("other"); // replaces it: "other" on rows 0, 2, 4, 6
        gate.SetResult();
        await next.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("other", vm.SearchText);
        Assert.Equal(vm.Rows[0].Sha, vm.SelectedSha); // only the new search's jump; the old Next did not move on to row 2
    }
}
