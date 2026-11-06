using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using GerGit.Core;
using GerGit.Core.Git;
using GerGit.Hosting;
using GerGit.Views;

namespace GerGit.ViewModels;

/// <summary>The "fzy" scoring (Quick Launch lists, later the Keyboard Shortcuts search).</summary>
public static class Fuzzy
{
    const double GapLeading = -0.005, GapTrailing = -0.005, GapInner = -0.01, Consecutive = 1.0;
    const int MaxLength = 1024;

    /// <summary>The score of <paramref name="needle"/> (trimmed, lower-case) in <paramref name="haystack"/>, compared
    /// ignoring case; null when it is no subsequence or the haystack is longer than 1024. Same length = exact match =
    /// infinite. fzy's split: two rolling rows on the stack and no positions (those come from <see cref="Match"/> for
    /// the rows that are shown); same arithmetic as <see cref="Match"/>, so the same score.</summary>
    public static double? Score(string needle, string haystack)
    {
        int n = needle.Length, m = haystack.Length;
        if (n == 0 || m > MaxLength || !IsSubsequence(needle, haystack)) return null;
        if (n == m) return double.PositiveInfinity;

        Span<double> bonus = stackalloc double[m], d = stackalloc double[m], best = stackalloc double[m];
        Span<double> lastD = stackalloc double[m], lastBest = stackalloc double[m];
        Span<double> swap = lastD; // same stack scope as the rows, so the rows can be swapped through it
        for (int j = 0; j < m; j++) bonus[j] = Bonus(j == 0 ? '/' : haystack[j - 1], haystack[j]);
        for (int i = 0; i < n; i++)
        {
            double previous = double.NegativeInfinity, gap = i == n - 1 ? GapTrailing : GapInner;
            for (int j = 0; j < m; j++)
            {
                if (needle[i] == char.ToLowerInvariant(haystack[j]))
                {
                    double score = double.NegativeInfinity;
                    if (i == 0) score = j * GapLeading + bonus[j];
                    else if (j > 0) score = Math.Max(lastBest[j - 1] + bonus[j], lastD[j - 1] + Consecutive);
                    d[j] = score;
                    best[j] = previous = Math.Max(score, previous + gap);
                }
                else
                {
                    d[j] = double.NegativeInfinity;
                    best[j] = previous += gap;
                }
            }
            swap = d; d = lastD; lastD = swap;
            swap = best; best = lastBest; lastBest = swap;
        }
        return lastBest[m - 1];
    }

    /// <summary>Score and the matched positions of the best alignment of <paramref name="needle"/> (trimmed, lower-case)
    /// in <paramref name="haystack"/>, compared ignoring case; null when it is no subsequence or the haystack is longer than
    /// 1024. Same length = exact match = infinite score.</summary>
    public static (double Score, int[] Positions)? Match(string needle, string haystack)
    {
        int n = needle.Length, m = haystack.Length;
        if (n == 0 || m > MaxLength || !IsSubsequence(needle, haystack)) return null;
        if (n == m) return (double.PositiveInfinity, Enumerable.Range(0, n).ToArray());

        var bonus = new double[m];
        for (int j = 0; j < m; j++) bonus[j] = Bonus(j == 0 ? '/' : haystack[j - 1], haystack[j]);
        // D: best score ending with a match at j; M: best score up to j (match or gap).
        var d = new double[n, m];
        var best = new double[n, m];
        for (int i = 0; i < n; i++)
        {
            double previous = double.NegativeInfinity, gap = i == n - 1 ? GapTrailing : GapInner;
            for (int j = 0; j < m; j++)
            {
                if (needle[i] == char.ToLowerInvariant(haystack[j]))
                {
                    double score = double.NegativeInfinity;
                    if (i == 0) score = j * GapLeading + bonus[j];
                    else if (j > 0) score = Math.Max(best[i - 1, j - 1] + bonus[j], d[i - 1, j - 1] + Consecutive);
                    d[i, j] = score;
                    best[i, j] = previous = Math.Max(score, previous + gap);
                }
                else
                {
                    d[i, j] = double.NegativeInfinity;
                    best[i, j] = previous += gap;
                }
            }
        }

        // Backtrack the positions of that alignment (fzy match_positions).
        var positions = new int[n];
        bool required = false;
        for (int i = n - 1, j = m - 1; i >= 0; i--)
            for (; j >= 0; j--)
                if (d[i, j] != double.NegativeInfinity && (required || d[i, j] == best[i, j]))
                {
                    required = i > 0 && j > 0 && best[i, j] == d[i - 1, j - 1] + Consecutive;
                    positions[i] = j--;
                    break;
                }
        return (best[n - 1, m - 1], positions);
    }

    static bool IsSubsequence(string needle, string haystack)
    {
        int i = 0;
        foreach (var c in haystack)
            if (i < needle.Length && needle[i] == char.ToLowerInvariant(c)) i++;
        return i == needle.Length;
    }

    /// <summary>Bonus of a matched character by the one before it; only ASCII letters and digits get one.</summary>
    static double Bonus(char before, char c) => !char.IsAsciiLetterOrDigit(c) ? 0 : before switch
    {
        '/' => 0.9,
        '-' or '_' or ' ' => 0.8,
        '.' => 0.6,
        _ => char.IsAsciiLetterLower(before) && char.IsAsciiLetterUpper(c) ? 0.7 : 0,
    };
}

/// <summary>An unfiltered Quick Launch item: a command, a repository (value = path) or an argument value.
/// <paramref name="Secondary"/> is shown after a dash and matched too (repositories and files).</summary>
public sealed record QuickChoice(string Name, object Value, string Icon, string? Secondary = null, HostType? Host = null);

/// <summary>A command's argument: placeholder, list header (null = no header row) and its choices.</summary>
public sealed record QuickArg(string Placeholder, string? Header, Func<Task<List<QuickChoice>>> Choices);

/// <summary>A Quick Launch command; <see cref="Run"/> gets the chosen argument value (null without argument).</summary>
public sealed record QuickCommand(string Name, Func<object?, Task> Run, QuickArg? Arg = null);

/// <summary>A row of the list: a section header or an item. The matched character positions are computed when the row
/// is shown (the list is virtualized), not for every candidate.</summary>
public sealed partial class QuickRow : ObservableObject
{
    int[]? _nameHits, _secondaryHits;

    public string? Header { get; init; }
    public QuickChoice? Choice { get; init; }

    /// <summary>The filter text the row was matched with ("" = unfiltered, no highlight).</summary>
    public string Needle { get; init; } = "";

    public int[]? NameHits => Choice is null || Needle.Length == 0 ? null : _nameHits ??= Hits(Choice.Name);
    public int[]? SecondaryHits => Choice?.Secondary is not { } s || Needle.Length == 0 ? null : _secondaryHits ??= Hits(s);

    int[] Hits(string text) => Fuzzy.Match(Needle, text)?.Positions ?? [];

    public bool IsHeader => Header is not null;
    public bool IsRemote => Choice?.Host is not null;
    public bool HasSecondary => Choice?.Secondary is not null;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    public override string ToString() => Header ?? Choice!.Name;
}

/// <summary>
/// Quick Launch: the root list (recent repositories, commands, filtered repositories), the
/// argument flow with the command chip, and the command catalogue over the existing entry points.
/// Limitation: commands take at most one argument (every built command does), so the chip row holds one chip. Not built
/// yet: Apply Patch…, Bisect, Git Flow, Git LFS, repository statistics, custom commands, and a "Pinned" section of
/// branch lists.
/// </summary>
public sealed partial class QuickLaunchViewModel : ObservableObject
{
    /// <summary>The list refreshes 0.1 s after the last keystroke.</summary>
    static readonly TimeSpan FilterDelay = TimeSpan.FromMilliseconds(100);

    /// <summary>Lists with more candidates than this are built off the UI thread (repository files).</summary>
    internal static int BackgroundLimit { get; set; } = 5000;

    /// <summary>Runs a background filter; tests replace it to control the order in which filters finish.</summary>
    internal static Func<Func<List<QuickRow>>, Task<List<QuickRow>>> RunInBackground { get; set; } = work => Task.Run(work);

    readonly MainWindowViewModel _main;
    readonly Func<Task> _openRepository;
    readonly Func<string, Task> _openPath;
    readonly DispatcherTimer _filterTimer;
    QuickCommand? _command; // chosen, its argument is being asked
    List<QuickChoice> _choices = [];
    int _version; // of the last refresh: an older background filter result is dropped

    /// <param name="openRepository">File > Open Repository... (folder chooser).</param>
    /// <param name="openPath">Opens a folder as a tab with the main window's failure dialogs.</param>
    public QuickLaunchViewModel(MainWindowViewModel main, Func<Task> openRepository, Func<string, Task> openPath)
    {
        _main = main;
        _openRepository = openRepository;
        _openPath = openPath;
        Commands = BuildCommands();
        _filterTimer = new DispatcherTimer { Interval = FilterDelay };
        _filterTimer.Tick += (_, _) => Refresh();
        Refresh();
        Scanning = ScanAsync();
    }

    /// <summary>The commands offered for the active tab, sorted naturally by name.</summary>
    public IReadOnlyList<QuickCommand> Commands { get; }

    /// <summary>The name of the chosen command (the chip), null in the root list.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Placeholder))]
    public partial string? Chip { get; private set; }

    public string Placeholder => _command?.Arg?.Placeholder ?? "Command";

    [ObservableProperty]
    public partial string Text { get; set; } = "";

    partial void OnTextChanged(string value)
    {
        _filterTimer.Stop();
        _filterTimer.Start();
    }

    [ObservableProperty]
    public partial IReadOnlyList<QuickRow> Rows { get; private set; } = [];

    [ObservableProperty]
    public partial QuickRow? Selected { get; private set; }

    partial void OnSelectedChanged(QuickRow? oldValue, QuickRow? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
    }

    /// <summary>Set by the window: closes it and runs the command afterwards on the UI thread.</summary>
    public Action<Func<Task>>? Done { get; set; }

    /// <summary>The background scan of the source folders started on open (tests await it).</summary>
    public Task Scanning { get; }

    /// <summary>The last load of an argument's choices (tests await it).</summary>
    public Task Loading { get; private set; } = Task.CompletedTask;

    /// <summary>The last background filter of a long list (tests await it).</summary>
    public Task Filtering { get; private set; } = Task.CompletedTask;

    async Task ScanAsync()
    {
        try
        {
            if (!await _main.ScanSourcesAsync()) return;
        }
        catch (Exception e)
        {
            Trace.WriteLine(e);
            return;
        }
        if (_command is null) Refresh();
    }

    // ------------------------------------------------------------------ list

    sealed record Scored(QuickChoice Choice, double Score);

    /// <summary>Rebuilds the list for the typed text and selects the first item. Long lists (also unfiltered, e.g. all
    /// repository files) are built in the background; the rows shown until then stay, and only the newest refresh's
    /// result is shown.</summary>
    public void Refresh()
    {
        _filterTimer.Stop();
        int version = ++_version;
        var needle = Text.Trim().ToLowerInvariant();
        List<(string? Header, List<QuickChoice> Items)> sections = _command is { Arg: { } arg } ? [(arg.Header, _choices)]
            : needle.Length == 0 ? [("Recent Repositories", Recent()), ("Commands", CommandChoices())]
            : [("Commands", CommandChoices()), ("Repositories", Repositories())];
        if (sections.Sum(s => s.Items.Count) > BackgroundLimit)
            Filtering = FilterInBackgroundAsync(version, needle, sections);
        else Show(BuildRows(needle, sections));
    }

    async Task FilterInBackgroundAsync(int version, string needle, List<(string? Header, List<QuickChoice> Items)> sections)
    {
        var rows = await RunInBackground(() => BuildRows(needle, sections));
        if (version == _version) Show(rows);
    }

    void Show(List<QuickRow> rows)
    {
        Rows = rows;
        Selected = rows.FirstOrDefault(r => !r.IsHeader);
    }

    /// <summary>Empty sections go; the others by the score of their first item (stable: equal scores keep the order).</summary>
    static List<QuickRow> BuildRows(string needle, List<(string? Header, List<QuickChoice> Items)> sections)
    {
        var rows = new List<QuickRow>();
        foreach (var (header, items) in sections.Select(s => (s.Header, Items: Filter(needle, s.Items)))
                     .Where(s => s.Items.Count > 0).OrderByDescending(s => s.Items[0].Score))
        {
            if (header is not null) rows.Add(new QuickRow { Header = header });
            rows.AddRange(items.Select(s => new QuickRow { Choice = s.Choice, Needle = needle }));
        }
        return rows;
    }

    /// <summary>Fuzzy matching 6 and 7: name score plus secondary score; a total of 0 or less drops the item;
    /// sorted by total, highest first (stable). An empty needle keeps everything in order.</summary>
    static List<Scored> Filter(string needle, List<QuickChoice> choices)
    {
        if (needle.Length == 0) return choices.Select(c => new Scored(c, 0)).ToList();
        var result = new List<Scored>();
        foreach (var c in choices)
        {
            double total = (Fuzzy.Score(needle, c.Name) ?? 0) + (c.Secondary is null ? 0 : Fuzzy.Score(needle, c.Secondary) ?? 0);
            if (total > 0) result.Add(new(c, total));
        }
        return result.OrderByDescending(s => s.Score).ToList();
    }

    List<QuickChoice> CommandChoices() => Commands.Select(c => new QuickChoice(c.Name, c, "chevron-right")).ToList();

    static QuickChoice Repository(RepoEntry e) => new(e.Alias ?? Path.GetFileName(e.Path), e, "folder-git-2", e.Path);

    static List<QuickChoice> Repositories() => RepositoryList.Entries.Select(Repository).ToList();

    /// <summary>The 8 most recently opened repositories; never opened ones sort last.</summary>
    static List<QuickChoice> Recent() =>
        RepositoryList.Entries.OrderByDescending(e => e.LastOpened ?? long.MinValue).Take(8).Select(Repository).ToList();

    /// <summary>Up / Down: the previous / next item, wrapping around (headers are skipped).</summary>
    public void Move(int delta)
    {
        var items = Rows.Where(r => !r.IsHeader).ToList();
        if (items.Count == 0) return;
        int at = Selected is null ? -1 : items.IndexOf(Selected);
        Selected = items[at < 0 ? 0 : ((at + delta) % items.Count + items.Count) % items.Count];
    }

    /// <summary>Enter: the selected row, after a filter refresh that is still pending (or running in the background).
    /// Nothing when the list changed meanwhile (typing, going back); a closed window ignores the choice (Done).</summary>
    public async Task ActivateSelectedAsync()
    {
        if (_filterTimer.IsEnabled) Refresh();
        int version = _version;
        await Filtering;
        if (version == _version) Activate(Selected);
    }

    /// <summary>Enter or a click: a command without argument or a repository finishes; a command with an argument
    /// becomes the chip and its choices show; a choice runs the chosen command.</summary>
    public void Activate(QuickRow? row)
    {
        if (row?.Choice is not { } choice) return;
        if (_command is { } command) Finish(() => command.Run(choice.Value));
        else if (choice.Value is QuickCommand c) Choose(c);
        else if (choice.Value is RepoEntry e) Finish(() => RepositoryManagerViewModel.OpenListedAsync(_main, choice.Name, e.Path, _openPath));
    }

    /// <summary>Activates the command of that name if it is offered (Ctrl+B: "Checkout Branch"); false if not.</summary>
    public bool ActivateCommand(string name)
    {
        if (Commands.FirstOrDefault(c => c.Name == name) is not { } c) return false;
        Choose(c);
        return true;
    }

    void Choose(QuickCommand c)
    {
        if (c.Arg is not { } arg)
        {
            Finish(() => c.Run(null));
            return;
        }
        _command = c;
        _choices = [];
        Chip = c.Name;
        Text = "";
        Refresh();
        Loading = LoadAsync(c, arg);
    }

    async Task LoadAsync(QuickCommand c, QuickArg arg)
    {
        var choices = await arg.Choices();
        if (_command != c) return; // went back meanwhile
        _choices = choices;
        Refresh();
    }

    /// <summary>Backspace at caret position 0: the chip goes and the root list returns; false without a chip.</summary>
    public bool Back()
    {
        if (_command is null) return false;
        _command = null;
        _choices = [];
        Chip = null;
        Text = "";
        Refresh();
        return true;
    }

    void Finish(Func<Task> run) => Done?.Invoke(run);

    // ------------------------------------------------------------------ command catalogue

    List<QuickCommand> BuildCommands()
    {
        var list = new List<QuickCommand>
        {
            new("Switch Workspace", v => _main.SwitchWorkspaceAsync((string)v!), new("workspace", "Workspaces",
                () => Task.FromResult(MainWindowViewModel.LoadWorkspaces().Select(w => new QuickChoice(w.Name, w.Name, "layers")).ToList()))),
            Plain("About GerGit", _main.OpenAboutAsync),
        };
        // Every other command needs a repository tab.
        if (_main.SelectedTab is { } tab) list.AddRange(RepositoryCommands(tab));
        return list.OrderBy(c => c.Name, RemoteOps.Natural).ToList();
    }

    IEnumerable<QuickCommand> RepositoryCommands(RepoTabViewModel tab)
    {
        var refs = tab.Refs;
        var ops = tab.RemoteOps;
        List<GitRef> Of(RefKind kind) => refs.Refs.Where(r => r.Kind == kind).OrderBy(r => r.ShortName, RemoteOps.Natural).ToList();
        var locals = Of(RefKind.LocalBranch);
        var remotes = Of(RefKind.RemoteBranch);
        var tags = Of(RefKind.Tag);
        var active = refs.ActiveBranch;
        var notActive = (List<GitRef> l) => l.Where(r => r.FullName != active).ToList();

        var list = new List<QuickCommand>
        {
            Plain("Create Branch...", () => tab.RefOps.CreateBranchAsync()),
            Plain("Create Tag...", () => tab.RefOps.CreateTagAsync()),
            Plain("Create Worktree...", tab.WorktreeOps.CreateAsync),
            Plain("Fetch...", () => ops.OpenFetchAsync()),
            Plain("Pull...", () => ops.OpenPullAsync()),
            Plain("Push...", () => ops.OpenPushAsync()),
            Plain("Save Stash...", tab.StashOps.SaveAsync),
            Plain("Open In File Explorer", () => OpenIn.FileExplorerAsync(tab)),
            Plain($"Open In {AppPreferences.Current.TerminalName}", () => OpenIn.TerminalAsync(tab)),
            Plain("Open Repository...", _openRepository),
            Plain("Repository Manager", () => { _main.NewTab(); return Task.CompletedTask; }),
            Plain("Repository Settings...", () => Preferences.RepositorySettingsViewModel.OpenAsync(tab)),
            Plain("Zoom In", () => { _main.ZoomIn(); return Task.CompletedTask; }),
            Plain("Zoom Out", () => { _main.ZoomOut(); return Task.CompletedTask; }),
            Plain("Switch Theme", () => { AppTheme.Switch(); return Task.CompletedTask; }),
            new("Blame...", v => { BlameWindow.Open(tab, (string)v!, null); return Task.CompletedTask; }, Files(tab)),
            new("File History...", v => { FileHistoryWindow.Open(tab, (string)v!, false, null); return Task.CompletedTask; }, Files(tab)),
        };

        // A command whose argument list is empty would do nothing: it is not offered.
        void WithRef(string name, string placeholder, string header, List<GitRef> choices, Func<GitRef, Task> run)
        {
            if (choices.Count == 0) return;
            var items = choices.Select(r => new QuickChoice(r.ShortName, r, r.Kind == RefKind.Tag ? "tag" : "git-branch")).ToList();
            list.Add(new(name, v => run((GitRef)v!), new(placeholder, header, () => Task.FromResult(items))));
        }

        WithRef("Checkout Branch", "branch", "Branches", [.. locals, .. remotes],
            r => r.Kind == RefKind.LocalBranch ? tab.RefOps.CheckoutBranchAsync(r) : tab.RefOps.CheckoutRemoteBranchAsync(r));
        // The active branch is not offered: Delete is disabled for it in the sidebar, and merging or rebasing it
        // onto itself does nothing.
        WithRef("Delete Branch...", "branch to delete", "Branches", [.. notActive(locals), .. remotes],
            r => r.Kind == RefKind.LocalBranch ? tab.RefOps.DeleteBranchesAsync([r]) : tab.RefOps.DeleteRemoteBranchesAsync([r]));
        WithRef("Delete Tag...", "tag to delete", "Tags", tags, t => tab.RefOps.DeleteTagsAsync([t]));
        // Like the menus: only branches whose upstream exists, and not those checked out in another worktree.
        WithRef("Fast-Forward", "branch", "Local Branches",
            locals.Where(b => RemoteOps.HasExistingUpstream(b) && tab.WorktreeOf(b.FullName) is not { IsCurrent: false }).ToList(),
            b => ops.FastForwardAsync(b.ShortName));
        WithRef("Rename Branch...", "branch to rename", "Local Branches", locals, b => tab.RefOps.RenameBranchAsync(b));
        if (active is not null) // merge and rebase need an active branch
        {
            WithRef("Merge...", "branch or tag", "Branches and Tags", notActive([.. locals, .. remotes, .. tags]), r => tab.RefOps.MergeAsync(r));
            WithRef("Rebase...", "branch or tag", "Branches and Tags", notActive([.. locals, .. remotes, .. tags]), r => new RebaseOps(tab).RebaseOnAsync(r));
        }

        var remoteNames = ops.RemoteNames;
        if (remoteNames.Count > 0)
        {
            var items = remoteNames.Select(r => new QuickChoice(r, r, "server", Host: ops.HostOf(r))).ToList();
            list.Add(new("Fetch", v => ops.OpenFetchAsync((string)v!), new("remote", null, () => Task.FromResult(items))));
            WithRef("Push", "branch", "Local Branches", locals, b => ops.OpenPushAsync(b.ShortName));
        }

        // One command per remote that offers pull / merge requests; GitLab says "Merge Request".
        foreach (var h in remoteNames.Select(tab.Hosted).OfType<HostedRemote>().Where(h => h.RepositoryPage is not null))
        {
            var remote = h.Name;
            // "upstream begins with the remote's name" is read as "the upstream's remote is that remote".
            var branches = locals.Where(b => b.Upstream is null || ops.RemoteOf(b.Upstream) == remote).Concat(ops.RemoteBranchesOf(remote)
                .OrderBy(r => r.ShortName, RemoteOps.Natural)).ToList();
            WithRef($"Create {h.RequestName} on '{remote}'", "branch", "Branches", branches, b =>
                b.Kind == RefKind.RemoteBranch ? ops.CreatePullRequestAsync(remote, ops.NameInRemote(b.FullName), b.Sha)
                : ops.StartPullRequestAsync(b, remote));
        }
        return list;
    }

    static QuickCommand Plain(string name, Func<Task> run) => new(name, _ => run());

    /// <summary>The "file" argument: tracked files (`git ls-files --cached -z`), name with the path as secondary text,
    /// sorted naturally by file name.</summary>
    static QuickArg Files(RepoTabViewModel tab) => new("file", "Repository Files", async () =>
    {
        var r = await GitRunner.RunAsync(tab.Path, ["ls-files", "--cached", "-z"]);
        if (!r.Success) Trace.WriteLine($"Quick Launch: git ls-files failed: {r.ReadableOutput}");
        return await Task.Run(() => r.Stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries).Distinct()
            .Select(p => new QuickChoice(Path.GetFileName(p), p, "file", p))
            .OrderBy(c => c.Name, RemoteOps.Natural).ToList());
    });
}
