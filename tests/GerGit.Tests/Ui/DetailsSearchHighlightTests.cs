using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GerGit.Controls;
using GerGit.Core;
using GerGit.Core.Git;
using GerGit.ViewModels;
using GerGit.Views;

namespace GerGit.Tests.Ui;

/// <summary>Search highlighting: the commit list's search text in the
/// details pane.</summary>
public sealed class DetailsSearchHighlightTests : IDisposable
{
    static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);
    readonly TempRepo _repo = new();

    public void Dispose() => _repo.Dispose();

    static IBrush MatchBrush => Avalonia.Application.Current is { } app
        && app.TryFindResource("Color.Search.MatchBg", app.ActualThemeVariant, out var b) ? (IBrush)b! : throw new InvalidOperationException();

    /// <summary>The highlighted pieces of a text block (empty for plain text).</summary>
    static List<string> Marked(TextBlock b) =>
        b.Inlines?.OfType<Run>().Where(r => r.Background == MatchBrush).Select(r => r.Text ?? "").ToList() ?? [];

    /// <summary>The text the block draws: its runs when it has any, else Text.</summary>
    static string Shown(TextBlock b) => b.Inlines is { Count: > 0 } runs ? runs.Text ?? "" : b.Text ?? "";

    static void Settle(Window w)
    {
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    async Task<(RepoTabViewModel Tab, Window Window, DetailsView View)> OpenAsync()
    {
        var tab = new RepoTabViewModel(await GitRepository.OpenAsync(_repo.Path).WaitAsync(Limit));
        var view = new DetailsView { DataContext = tab.Details };
        var window = new Window { Width = 1000, Height = 700, Content = view };
        // As RepoTabView does for its details pane (the rebase editor's preview gets no search text).
        window.Bind(SearchHighlight.TextProperty, new Avalonia.Data.Binding(nameof(CommitListViewModel.SearchText)) { Source = tab.CommitList });
        window.Show();
        return (tab, window, view);
    }

    static async Task ShowCommitAsync(RepoTabViewModel tab, DetailsView view, Window window, string sha)
    {
        await tab.Details.ShowAsync([sha]).WaitAsync(Limit);
        await view.MessageShown.WaitAsync(Limit);
        Settle(window);
    }

    static TextBlock FileRow(DetailsView view, string path) =>
        view.FindControl<ItemsControl>("CommitFiles")!.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == path);

    [AvaloniaFact]
    public async Task Search_text_marks_sha_author_message_header_and_file_paths_until_the_search_is_cleared()
    {
        _repo.Commit("init", 1, ("a.txt", "a\n"));
        var sha = _repo.Commit("Find the Wombat\n\nbody: wombat and WOMBAT", 2, ("wombat.txt", "w\n"));
        var (tab, window, view) = await OpenAsync();
        try
        {
            await ShowCommitAsync(tab, view, window, sha);
            var subject = view.FindControl<SelectableTextBlock>("SubjectText")!;
            var description = view.FindControl<SelectableTextBlock>("DescriptionText")!;
            Assert.Empty(Marked(subject)); // no search: plain text

            await tab.CommitList.SearchAsync("  wombat ").WaitAsync(Limit);
            Settle(window);
            Assert.Equal(["Wombat"], Marked(subject));
            Assert.Equal("Find the Wombat", Shown(subject)); // the whole text is still shown
            Assert.Equal(["wombat", "WOMBAT"], Marked(description));
            Assert.Equal(["wombat"], Marked(FileRow(view, "wombat.txt")));

            // SHA and author name too.
            await tab.CommitList.SearchAsync(sha[3..10].ToUpperInvariant()).WaitAsync(Limit);
            Settle(window);
            var shaText = view.FindControl<SelectableTextBlock>("ShaText")!;
            Assert.Equal([sha[3..10]], Marked(shaText));
            Assert.Equal(sha, shaText.Text); // the bound text is untouched
            Assert.Empty(Marked(subject));
            await tab.CommitList.SearchAsync("user").WaitAsync(Limit);
            Settle(window);
            Assert.Equal(["User"], Marked(view.FindControl<SelectableTextBlock>("AuthorNameText")!)); // "Test User"

            // The compact header on the Changes tab.
            await tab.CommitList.SearchAsync("wombat").WaitAsync(Limit);
            tab.Details.SelectedTab = DetailsViewModel.ChangesTab;
            Settle(window);
            var header = view.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("header-subject"));
            Assert.Equal(["Wombat"], Marked(header));
            Assert.Contains(view.FindControl<ListBox>("FileList")!.GetVisualDescendants().OfType<TextBlock>(),
                t => Marked(t).SequenceEqual(["wombat"]));

            // Closing the search clears every mark.
            tab.CommitList.CloseSearch();
            Settle(window);
            Assert.Empty(Marked(header));
            Assert.Empty(Marked(subject));
            Assert.Empty(Marked(description));
        }
        finally
        {
            window.Close();
            tab.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task Another_commit_under_an_active_search_shows_its_own_text_and_marks()
    {
        _repo.Commit("init", 1, ("a.txt", "a\n"));
        var first = _repo.Commit("Find the Wombat", 2, ("wombat.txt", "w\n"));
        var second = _repo.Commit("A second wombat here", 3, ("other-wombat.md", "o\n"));
        var (tab, window, view) = await OpenAsync();
        try
        {
            await tab.CommitList.SearchAsync("wombat").WaitAsync(Limit);
            await ShowCommitAsync(tab, view, window, first);
            var subject = view.FindControl<SelectableTextBlock>("SubjectText")!;
            Assert.Equal(["Wombat"], Marked(subject));

            await ShowCommitAsync(tab, view, window, second);
            Assert.Equal("A second wombat here", Shown(subject));
            Assert.Equal(["wombat"], Marked(subject));
            Assert.Equal("other-wombat.md", Shown(FileRow(view, "other-wombat.md")));
            Assert.Equal(["wombat"], Marked(FileRow(view, "other-wombat.md")));
            Assert.DoesNotContain(view.FindControl<ItemsControl>("CommitFiles")!.GetVisualDescendants().OfType<TextBlock>(),
                t => t.IsEffectivelyVisible && Shown(t) == "wombat.txt");

            // The SHA: marked for the commit it matches, plain (and new) for the next one.
            await tab.CommitList.SearchAsync(second[3..10]).WaitAsync(Limit);
            Settle(window);
            var shaText = view.FindControl<SelectableTextBlock>("ShaText")!;
            Assert.Equal([second[3..10]], Marked(shaText));
            await ShowCommitAsync(tab, view, window, first);
            Assert.Equal(first, Shown(shaText));
            Assert.Empty(Marked(shaText));
            Assert.Equal("Find the Wombat", Shown(subject));
        }
        finally
        {
            window.Close();
            tab.Dispose();
        }
    }

    [AvaloniaFact]
    public void Runs_made_by_others_are_split_at_the_matches_and_keep_their_look()
    {
        // As Hyperlinks builds them: plain text, then an underlined link run in the link colour.
        var block = new SelectableTextBlock();
        SearchHighlight.SetIsEnabled(block, true);
        SearchHighlight.SetText(block, "ex");
        var window = new Window { Content = block };
        window.Show();
        try
        {
            block.Inlines = [new Run("see "), new Run("https://example.com/") { TextDecorations = TextDecorations.Underline, Foreground = Brushes.Blue }];

            var runs = block.Inlines!.OfType<Run>().ToList();
            Assert.Equal("see https://example.com/", block.Inlines.Text);
            Assert.Equal(["see ", "https://", "ex", "ample.com/"], runs.Select(r => r.Text));
            Assert.Equal(MatchBrush, runs[2].Background);
            Assert.All(runs.Skip(1), r => Assert.Equal(TextDecorations.Underline, r.TextDecorations));
            Assert.All(runs.Skip(1), r => Assert.Equal(Brushes.Blue, r.Foreground));
            Assert.Null(runs[1].Background);

            // A match across two runs is marked in both pieces.
            SearchHighlight.SetText(block, "e h");
            Assert.Equal(["e ", "h"], Marked(block));
        }
        finally
        {
            window.Close();
        }
    }

    [Fact]
    public void Matches_are_case_insensitive_do_not_overlap_and_are_capped()
    {
        Assert.Equal([(0, 2), (2, 2)], TextSearch.Matches("aAaa", "aa"));
        Assert.Equal([(4, 3)], TextSearch.Matches("the WOMbat", "wom"));
        Assert.Empty(TextSearch.Matches("text", ""));
        Assert.Empty(TextSearch.Matches("text", null));
        Assert.Equal(TextSearch.MaxMatches, TextSearch.Matches(new string('x', 5000), "x").Count);
    }
    [AvaloniaFact]
    public async Task The_repository_tab_passes_its_commit_list_search_to_its_details_pane()
    {
        _repo.Commit("init", 1, ("a.txt", "a\n"));
        var sha = _repo.Commit("Find the Wombat", 2, ("b.txt", "b\n"));
        using var tab = new RepoTabViewModel(await GitRepository.OpenAsync(_repo.Path).WaitAsync(Limit));
        var view = new RepoTabView { DataContext = tab };
        var window = new Window { Width = 1200, Height = 800, Content = view };
        window.Show();
        try
        {
            var details = view.FindControl<DetailsView>("DetailsPane")!;
            await ShowCommitAsync(tab, details, window, sha);
            await tab.CommitList.SearchAsync("wombat").WaitAsync(Limit);
            Settle(window);
            Assert.Equal(["Wombat"], Marked(details.FindControl<SelectableTextBlock>("SubjectText")!));
        }
        finally
        {
            window.Close();
        }
    }
}
