using System.Diagnostics;
using System.Text.RegularExpressions;
using GerGit.Core;
using GerGit.Core.Git;

namespace GerGit.Tests.Core;

/// <summary>Issue tracker rule files and link matching (issue tracker links in commit messages), plain URL detection and the safe file writes, on real temporary repositories.</summary>
public sealed class IssueTrackerTests : IDisposable
{
    readonly TempRepo _repo = new();

    public void Dispose() => _repo.Dispose();

    async Task<RepoInfo> InfoAsync() => (await GitRepository.OpenAsync(_repo.Path)).Info;

    static List<(Regex, string)> Rules(params (string Regex, string Url)[] rules) =>
        rules.Select(r => (IssueTracker.Compile(r.Regex)!, r.Url)).ToList();

    static string[] Texts(string text, IEnumerable<TextLink> links) => links.Select(l => $"{text.Substring(l.Start, l.Length)}={l.Url}").ToArray();

    [Fact]
    public void Parse_reads_sections_unescapes_backslashes_and_skips_the_rest()
    {
        var text = """
            # Integration with Issue Tracker
            #
            [issuetracker "GitHub"]
              regex = "#(\\d+)"
              url = "https://github.com/o/r/issues/$1"

            [other "x"]
              regex = "ignored"
              url = "https://ignored"
            [issuetracker "no url"]
              regex = "X-(\\d+)"
            [issuetracker "Jira"]
              url = "https://jira/browse/$1"
              regex = "PROJ-(\\d+)"
            this line is broken
            [issuetracker "after the error"]
              regex = "a"
              url = "https://a"
            """;
        var rules = IssueTracker.Parse(text, IssueRuleLevel.Local);
        Assert.Equal([
            new IssueRule("GitHub", IssueRuleLevel.Local, @"#(\d+)", "https://github.com/o/r/issues/$1"),
            new IssueRule("Jira", IssueRuleLevel.Local, @"PROJ-(\d+)", "https://jira/browse/$1"),
        ], rules);
    }

    [Fact]
    public void Format_doubles_backslashes_and_round_trips_without_line_breaks_in_values()
    {
        var rules = new List<IssueRule>
        {
            new("Sample GitHub Rule", IssueRuleLevel.Shared, @"#(\d+)", "https://github.com/username/repository/issues/$1"),
            new("evil\n[issuetracker \"x\"]", IssueRuleLevel.Shared, "a\r\nb", "https://x/$1"),
        };
        var text = IssueTracker.Format(rules);
        Assert.StartsWith("# Integration with Issue Tracker\n#\n# (note that '\\' need to be escaped).\n\n[issuetracker \"Sample GitHub Rule\"]\n  regex = \"#(\\\\d+)\"\n", text);
        var back = IssueTracker.Parse(text, IssueRuleLevel.Shared);
        Assert.Equal(2, back.Count); // a line break in a value cannot start a new section
        Assert.Equal(rules[0], back[0]);
        Assert.Equal(("evil [issuetracker \"x\"]", "a  b"), (back[1].Name, back[1].Regex));
    }

    [Fact]
    public void Find_builds_urls_from_groups_and_resolves_overlaps()
    {
        var text = "PRJ1-28 Fix docs (PRJ2-45), see #7 and #12337";
        var links = IssueTracker.Find(text, Rules(
            (@"(PRJ1|PRJ2)-(\d+)", "https://jira.yourcompany.com/browse/$1-$2"),
            (@"#(\d+)", "https://github.com/username/repository/issues/$1"),
            (@"\d+", "https://numbers/$0")), plainUrls: false);
        Assert.Equal([
            "PRJ1-28=https://jira.yourcompany.com/browse/PRJ1-28",
            "PRJ2-45=https://jira.yourcompany.com/browse/PRJ2-45",
            "#7=https://github.com/username/repository/issues/7",
            "#12337=https://github.com/username/repository/issues/12337",
        ], Texts(text, links)); // every bare number of the third rule starts inside an accepted link
    }

    [Fact]
    public void At_the_same_start_the_longer_match_wins_and_unmatched_groups_stay_literal()
    {
        var text = "ABC-1 x";
        var links = IssueTracker.Find(text, Rules((@"ABC", "https://short"), (@"ABC-(\d+)(z)?", "https://long/$1/$2")), plainUrls: false);
        Assert.Equal(["ABC-1=https://long/1/$2"], Texts(text, links));
    }

    [Theory]
    [InlineData("javascript:alert($1)")]
    [InlineData("file:///etc/passwd#$1")]
    [InlineData("ftp://host/$1")]
    [InlineData("mailto:a@b?$1")]
    [InlineData("/relative/$1")]
    [InlineData("")]
    public void Templates_that_give_no_http_url_make_no_link(string template)
    {
        Assert.Empty(IssueTracker.Find("see #5", Rules((@"#(\d+)", template)), plainUrls: false));
    }

    [Fact]
    public void Invalid_patterns_do_not_compile_and_supported_ones_run_without_backtracking()
    {
        using var trace = new TraceCapture();
        Assert.Null(IssueTracker.Compile("(unclosed-marker-1"));
        Assert.Single(trace.Lines("unclosed-marker-1")); // logged
        Assert.Null(IssueTracker.Compile(""));
        Assert.Null(IssueTracker.Compile(new string('a', IssueTracker.MaxValueLength + 1)));
        Assert.True(IssueTracker.Compile("^a$")!.Options.HasFlag(RegexOptions.Multiline));

        // The classic catastrophic pattern runs on the linear engine: no timeout, the link is found at once.
        var linear = IssueTracker.Compile(@"(a+)+$")!;
        Assert.True(linear.Options.HasFlag(RegexOptions.NonBacktracking));
        var text = new string('a', 20000) + "!\naaa";
        var sw = Stopwatch.StartNew();
        Assert.Equal(["aaa=https://l/aaa"], Texts(text, IssueTracker.Find(text, [(linear, "https://l/$0")], plainUrls: false)));
        Assert.InRange(sw.Elapsed.TotalSeconds, 0, 1);

        // A lookahead needs the backtracking engine: it gets the match timeout.
        var backtracking = IssueTracker.Compile(@"(?=a)(a+)+b")!;
        Assert.False(backtracking.Options.HasFlag(RegexOptions.NonBacktracking));
        Assert.Equal(IssueTracker.MatchTimeout, backtracking.MatchTimeout);
    }

    [Fact]
    public void A_slow_backtracking_pattern_times_out_logged_and_the_other_rules_still_link()
    {
        using var trace = new TraceCapture();
        var text = new string('a', 40) + "! #1";
        var sw = Stopwatch.StartNew();
        var links = IssueTracker.Find(text, Rules((@"#(\d)", "https://fast/$1"), (@"(?=a)(a+)+b(?#timeout-marker)", "https://slow/$1")), plainUrls: false);
        Assert.InRange(sw.Elapsed.TotalSeconds, 0, 5);
        Assert.Equal(["#1=https://fast/1"], Texts(text, links));
        Assert.Contains(trace.Lines("timeout-marker"), l => l.Contains("timed out after 200 ms"));
    }

    [Fact]
    public void One_time_budget_covers_all_rules_of_a_text()
    {
        using var trace = new TraceCapture();
        var text = new string('a', 40) + "!";
        var slow = Enumerable.Range(0, 6).Select(i => ($"(?=a)(a+)+b(?#budget-marker-{i})", "https://x/$1")).ToArray();
        var sw = Stopwatch.StartNew();
        Assert.Empty(IssueTracker.Find(text, Rules(slow), plainUrls: false));
        Assert.InRange(sw.Elapsed.TotalMilliseconds, 0, 6 * 200 - 100); // not every rule ran to its timeout
        Assert.True(trace.Lines("budget-marker-").Length < 6);
        Assert.NotEmpty(trace.Lines("budget of a text is used up"));
    }

    [Fact]
    public void Links_text_length_url_length_and_values_are_limited()
    {
        var many = string.Concat(Enumerable.Repeat("#1 ", 2000));
        Assert.Equal(IssueTracker.MaxLinks, IssueTracker.Find(many, Rules((@"#(\d)", "https://i/$1")), plainUrls: false).Count);
        Assert.Equal(IssueTracker.MaxLinks, IssueTracker.FindUrls(string.Concat(Enumerable.Repeat("https://u.example/ ", 2000))).Count);

        var late = new string('x', IssueTracker.TextLimit) + " #9"; // only the first 64 KB are scanned
        Assert.Empty(IssueTracker.Find(late, Rules((@"#(\d)", "https://i/$1")), plainUrls: true));

        var longUrl = "https://h.example/" + new string('p', IssueTracker.MaxUrlLength);
        Assert.Empty(IssueTracker.FindUrls(longUrl));
        Assert.Null(IssueTracker.WebUrl(longUrl));
        var longGroup = "ID-" + new string('7', IssueTracker.MaxUrlLength + 1);
        Assert.Empty(IssueTracker.Find(longGroup, Rules((@"ID-(\d+)", "https://i/$1")), plainUrls: false));

        var tooLong = new string('r', IssueTracker.MaxValueLength + 1);
        var parsed = IssueTracker.Parse($"[issuetracker \"long\"]\n  regex = \"{tooLong}\"\n  url = \"https://x\"\n"
            + "[issuetracker \"ok\"]\n  regex = \"a\"\n  url = \"https://ok\"\n", IssueRuleLevel.Shared, out var complete);
        Assert.Equal(["ok"], parsed.Select(r => r.Name));
        Assert.False(complete); // the dropped rule: a rewrite must keep a backup
        IssueTracker.Parse("[issuetracker \"a\"]\n broken line\n", IssueRuleLevel.Shared, out complete);
        Assert.False(complete);
    }

    [Fact]
    public void The_link_cap_is_for_the_whole_text_and_stops_the_other_rules()
    {
        using var trace = new TraceCapture();
        var text = string.Concat(Enumerable.Repeat("#1 ", 600)) + new string('a', 40) + "!";
        var links = IssueTracker.Find(text, Rules((@"#(\d)", "https://i/$1"), (@"(?=a)(a+)+b(?#cap-marker)", "https://slow/$1")), plainUrls: false);
        Assert.Equal(IssueTracker.MaxLinks, links.Count);
        Assert.Empty(trace.Lines("cap-marker")); // the slow rule never ran (it would time out and log)
    }

    [Fact]
    public void A_template_full_of_groups_stops_at_the_url_limit()
    {
        var template = "https://x.example/" + string.Concat(Enumerable.Repeat("$1", 1000));
        var text = "ID-" + new string('9', 1000);
        Assert.Empty(IssueTracker.Find(text, Rules((@"ID-(\d+)", template)), plainUrls: false)); // 1,000 x 1,000 would be 1 MB
        Assert.Equal(["ID-12=https://x.example/1212"], Texts("ID-12", IssueTracker.Find("ID-12", Rules((@"ID-(\d+)", "https://x.example/$1$1")), plainUrls: false)));
    }

    [Fact]
    public async Task Only_the_local_rule_file_gets_a_backup()
    {
        var info = await InfoAsync();
        _repo.Write(".issuetracker", "broken");
        File.WriteAllText(IssueTracker.FileOf(info, IssueRuleLevel.Local), "broken");
        IssueTracker.Write(info, IssueRuleLevel.Shared, [new("S", IssueRuleLevel.Shared, "s", "https://s")], keepBackup: true);
        IssueTracker.Write(info, IssueRuleLevel.Local, [new("L", IssueRuleLevel.Local, "l", "https://l")], keepBackup: true);
        Assert.False(File.Exists(Path.Combine(_repo.Path, ".issuetracker.bak"))); // never an untracked file in the working tree
        Assert.Equal("broken", File.ReadAllText(IssueTracker.FileOf(info, IssueRuleLevel.Local) + ".bak"));
        Assert.Equal(["S", "L"], IssueTracker.Read(info).Select(r => r.Name));
    }

    [Fact]
    public void A_cancelled_search_stops()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => IssueTracker.Find("#1 #2", Rules((@"#(\d)", "https://i/$1")), plainUrls: false, cts.Token));
    }

    [Fact]
    public void Url_text_always_opens_itself_and_user_info_is_refused()
    {
        // A rule that matches inside a visible URL cannot send it elsewhere: the URL wins, the rule match is dropped.
        var text = "see https://github.com/o/r/issues/12 or #5";
        var links = IssueTracker.Find(text, Rules((@"github\.com/o/r/issues/(\d+)", "https://evil.example/$1"), (@"#(\d+)", "https://i/$1")),
            plainUrls: false);
        Assert.Equal(["#5=https://i/5"], Texts(text, links));
        Assert.Null(IssueTracker.WebUrl("https://github.com@evil.example/"));
        Assert.Null(IssueTracker.WebUrl("https://user:pw@h.example/"));
        Assert.Empty(IssueTracker.FindUrls("https://github.com@evil.example/x"));
        Assert.Empty(IssueTracker.Find("X-1", Rules((@"X-(\d)", "https://github.com@evil.example/$1")), plainUrls: false));
    }

    [Fact]
    public void Groups_are_url_escaped_and_the_checked_form_is_used()
    {
        var text = "REF:a?b/c#d REF:x&y";
        Assert.Equal(["REF:a?b/c#d=https://i.example/a%3Fb/c%23d", "REF:x&y=https://i.example/x%26y"],
            Texts(text, IssueTracker.Find(text, Rules((@"REF:(\S+)", "https://I.example/$1")), plainUrls: false)));
        Assert.Equal("https://h.example/", IssueTracker.WebUrl("HTTPS://H.EXAMPLE"));
    }

    [Fact]
    public void Url_detection_is_linear_on_bracket_runs()
    {
        var text = "https://x.example/" + new string(')', 50_000) + " https://y.example/(a)) " + new string('(', 50_000);
        var sw = Stopwatch.StartNew();
        var links = IssueTracker.FindUrls(text);
        Assert.InRange(sw.Elapsed.TotalSeconds, 0, 1);
        Assert.Equal(["https://y.example/(a)=https://y.example/(a)"], Texts(text, links)); // the 50,000-character one is too long
        var trimmed = "https://x.example/" + new string(')', 1000);
        sw.Restart();
        Assert.Equal(["https://x.example/=https://x.example/"], Texts(trimmed, IssueTracker.FindUrls(trimmed)));
        Assert.InRange(sw.Elapsed.TotalSeconds, 0, 1);
    }

    [Fact]
    public void Plain_urls_are_found_without_trailing_punctuation_and_only_for_http()
    {
        var text = "Docs: https://example.com/a_(b). Also (http://x.org/p?q=1), javascript:alert(1) file:///etc/passwd ftp://h/x HTTPS://UP.example/";
        Assert.Equal(["https://example.com/a_(b)=https://example.com/a_(b)", "http://x.org/p?q=1=http://x.org/p?q=1",
            "HTTPS://UP.example/=https://up.example/"], Texts(text, IssueTracker.FindUrls(text)));
        // URLs and issue links together: the URL starts earlier and wins over the "#1" inside it.
        var mixed = "see https://h.example/x#12 and #3";
        Assert.Equal(["https://h.example/x#12=https://h.example/x#12", "#3=https://i/3"],
            Texts(mixed, IssueTracker.Find(mixed, Rules((@"#(\d+)", "https://i/$1")), plainUrls: true)));
    }

    [Theory]
    [InlineData("https://example.com", true)]
    [InlineData("http://example.com/x", true)]
    [InlineData("file:///etc/passwd", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("/home/user", false)]
    [InlineData("data:text/html,x", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_absolute_http_urls_are_web_urls(string? url, bool expected) => Assert.Equal(expected, IssueTracker.IsWebUrl(url));

    [Fact]
    public async Task Rules_are_read_from_both_files_shared_first_and_written_back_safely()
    {
        var info = await InfoAsync();
        _repo.Write(".issuetracker", "[issuetracker \"S\"]\n  regex = \"S-(\\\\d+)\"\n  url = \"https://s/$1\"\n");
        File.WriteAllText(Path.Combine(info.GitDir, "issuetracker"), "[issuetracker \"L\"]\n  regex = \"L-(\\\\d+)\"\n  url = \"https://l/$1\"\n");
        Assert.Equal([
            new IssueRule("S", IssueRuleLevel.Shared, @"S-(\d+)", "https://s/$1"),
            new IssueRule("L", IssueRuleLevel.Local, @"L-(\d+)", "https://l/$1"),
        ], IssueTracker.Read(info));

        // A link planted at the target is replaced, its target never written.
        var outside = Path.Combine(Path.GetTempPath(), "gergit-outside-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(outside, "keep");
        try
        {
            var local = IssueTracker.FileOf(info, IssueRuleLevel.Local);
            File.Delete(local);
            File.CreateSymbolicLink(local, outside);
            Assert.DoesNotContain(IssueTracker.Read(info), r => r.Level == IssueRuleLevel.Local); // a link is not read
            IssueTracker.Write(info, IssueRuleLevel.Local, [new("N", IssueRuleLevel.Local, "x", "https://n")]);
            Assert.Equal("keep", File.ReadAllText(outside));
            Assert.Null(new FileInfo(local).LinkTarget);
            Assert.Equal("N", IssueTracker.Read(info).Single(r => r.Level == IssueRuleLevel.Local).Name);
            Assert.Empty(Directory.GetFiles(info.GitDir, "*.tmp"));

            IssueTracker.Write(info, IssueRuleLevel.Shared, []); // no rules: the file goes
            Assert.False(File.Exists(Path.Combine(_repo.Path, ".issuetracker")));
        }
        finally { File.Delete(outside); }
    }

    [Fact]
    public async Task A_fifo_rule_file_is_never_opened()
    {
        var info = await InfoAsync();
        var fifo = IssueTracker.FileOf(info, IssueRuleLevel.Shared);
        await Process.Start("mkfifo", fifo)!.WaitForExitAsync();
        var read = Task.Run(() => IssueTracker.Read(info));
        Assert.Same(read, await Task.WhenAny(read, Task.Delay(5000))); // returns at once, nothing blocks
        Assert.Empty(await read);
    }
}

/// <summary>Collects the Trace lines written while it lives (other tests may write too: filter by a marker).</summary>
internal sealed class TraceCapture : TraceListener
{
    readonly List<string> _lines = [];
    public TraceCapture() => Trace.Listeners.Add(this);
    public override void Write(string? message) { }
    public override void WriteLine(string? message) { lock (_lines) _lines.Add(message ?? ""); }
    public string[] Lines(string marker) { lock (_lines) return _lines.Where(l => l.Contains(marker)).ToArray(); }
    protected override void Dispose(bool disposing)
    {
        Trace.Listeners.Remove(this);
        base.Dispose(disposing);
    }
}
