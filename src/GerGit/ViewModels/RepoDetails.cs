using GerGit.Core;
using GerGit.Core.Git;
using GerGit.Hosting;

namespace GerGit.ViewModels;

/// <summary>A row of the Branches, Tags or Commits tile: name (or subject), relative date, person, subject.</summary>
public sealed record RepoSummaryRow(string Name, string Date, string Person, string Email, string Subject);

/// <summary>A row of "Remotes:": host link (the repository page) and the Issues / Pull Requests pages when the host has them.</summary>
public sealed record RepoRemoteRow(string Name, HostType Type, string? Host, string? Page, string? IssuesPage, string? PullRequestsPage)
{
    public bool HasPage => Page is not null;
    public bool HasIssues => IssuesPage is not null;
    public bool HasPullRequests => PullRequestsPage is not null;
}

/// <summary>
/// The details panel of the Repository Manager, read in the background.
/// Not built yet: a "Statistics" tab; only "Summary" is shown.
/// </summary>
public sealed class RepoDetails
{
    public required string Name { get; init; }
    public required string Path { get; init; }

    /// <summary>The path no longer opens as a repository: the "Repository Not Found" panel.</summary>
    public bool NotFound { get; init; }

    public int Uncommitted { get; init; }
    public string Commits { get; init; } = "";
    public string InitialCommit { get; init; } = "";
    public string LastCommit { get; init; } = "";
    public IReadOnlyList<RepoRemoteRow> Remotes { get; init; } = [];
    public IReadOnlyList<RepoSummaryRow> Branches { get; init; } = [];
    public IReadOnlyList<RepoSummaryRow> Tags { get; init; } = [];
    public IReadOnlyList<RepoSummaryRow> RecentCommits { get; init; } = [];

    /// <summary>Text of `readme.md` in the root (any case), null = no README tile.</summary>
    public string? Readme { get; init; }

    public bool Found => !NotFound;

    /// <summary>git could not read the repository for another reason than "not found" (e.g. dubious ownership): the
    /// header with Open stays, the error text replaces the tiles.</summary>
    public string? Error { get; init; }

    public bool HasError => Error is not null;
    public bool HasData => Found && !HasError;
    public bool HasReadme => Readme is not null;

    public static async Task<RepoDetails> LoadAsync(string name, string path, DateTimeOffset now, CancellationToken ct)
    {
        // A folder whose `.git` is gone may sit inside another repository: git would describe that one.
        if (RepositoryScan.Validate(path) is null) return new() { Name = name, Path = path, NotFound = true };
        GitRepository repo;
        try { repo = await GitRepository.OpenAsync(path, ct); }
        catch (GitException e) when (e.Message == "[internal] Not found" || e.Message.EndsWith("is not a git repository", StringComparison.Ordinal))
        {
            return new() { Name = name, Path = path, NotFound = true };
        }
        catch (GitException e)
        {
            // E.g. dubious ownership: the text shows, and Open leads to the Git Error window ("Mark repository as safe").
            return new() { Name = name, Path = path, Error = e.Message };
        }

        Task<GitResult> Git(params string[] args) => GitRunner.RunAsync(path, args, ct: ct);
        var status = Git("-c", "core.fsmonitor=false", "--no-optional-locks", "status", "--porcelain", "-uno", "-z");
        var count = Git("rev-list", "--all", "--count");
        var initial = Git("log", "--no-show-signature", "--max-parents=0", "HEAD", "--pretty=format:%at", "--");
        var last = Git("log", "--no-show-signature", "-n", "1", "--pretty=format:%at", "--");
        var refs = Git("for-each-ref", "--format=%(objectname)%09%(refname)%09%(HEAD)%09%(upstream)%09%(*objectname)%09" +
            "%(committerdate:raw)%09%(*committerdate:raw)%09%(committername)%09%(*committername)%09%(committeremail)%09" +
            "%(*committeremail)%09%(contents:subject)");
        var log = Git("log", "--no-show-signature", "--all", "-n", "5", "--date-order", "--pretty=format:%H%n%an%n%ae%n%at%n%s", "--");
        var remotes = repo.ReadRemotesAsync(ct);
        await Task.WhenAll(status, count, initial, last, refs, log, remotes);
        ct.ThrowIfCancellationRequested();

        string Date(GitResult r, bool oldest)
        {
            var stamps = r.Success ? r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => long.TryParse(l.Trim(), out var s) ? s : (long?)null).OfType<long>().ToList() : [];
            return stamps.Count == 0 ? "" : Ago(oldest ? stamps.Min() : stamps.Max(), now);
        }

        // Remote branches and tags: committer date, name and e-mail fall back to the dereferenced values (annotated tags).
        var refRows = (await refs).Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split('\t'))
            .Where(f => f.Length >= 12 && !f[1].EndsWith("/HEAD", StringComparison.Ordinal))
            .Select(f => (Ref: f[1], Date: RawDate(f[5]) ?? RawDate(f[6]) ?? 0, Name: Or(f[7], f[8]), Email: Or(f[9], f[10]).Trim('<', '>'),
                Subject: string.Join('\t', f[11..])))
            .OrderByDescending(r => r.Date).ToList();
        List<RepoSummaryRow> Rows(string prefix) => refRows.Where(r => r.Ref.StartsWith(prefix, StringComparison.Ordinal)).Take(5)
            .Select(r => new RepoSummaryRow(r.Ref[prefix.Length..], Ago(r.Date, now), r.Name, r.Email, r.Subject)).ToList();

        var commits = new List<RepoSummaryRow>();
        var lines = (await log).Stdout.Split('\n');
        for (int i = 0; i + 4 < lines.Length; i += 5)
            commits.Add(new(lines[i + 4], Ago(long.TryParse(lines[i + 3], out var at) ? at : 0, now), lines[i + 1], lines[i + 2], lines[i + 4]));

        var accounts = HostingService.Current.Accounts;
        var remoteRows = (await remotes).Select(r =>
        {
            var url = RemoteUrl.Parse(r.Url);
            var hosted = new HostedRemote(r.Name, url, url?.MatchAccount(accounts));
            var page = hosted.RepositoryPage;
            var (issues, pulls) = page is null ? (null, null) : hosted.Type switch
            {
                HostType.GitHub => (page + "/issues", page + "/pulls"),
                HostType.GitLab or HostType.GitLabServer => (page + "/-/issues/", page + "/-/merge_requests"),
                _ => ((string?)null, (string?)null),
            };
            return new RepoRemoteRow(r.Name, hosted.Type, url?.Host, page, issues, pulls);
        }).ToList();


        return new()
        {
            Name = name,
            Path = path,
            Uncommitted = CountStatus((await status).Stdout),
            Commits = (await count).Success ? (await count).Stdout.Trim() : "0",
            InitialCommit = Date(await initial, oldest: true),
            LastCommit = Date(await last, oldest: false),
            Remotes = remoteRows,
            Branches = Rows("refs/remotes/"),
            Tags = Rows("refs/tags/"),
            RecentCommits = commits,
            Readme = await ReadReadmeAsync(path, ct),
        };
    }

    /// <summary>The README tile shows at most this many characters, then "…".</summary>
    internal const int ReadmeLimit = 64 * 1024;

    /// <summary>`readme.md` in the root (any case), only a regular file (no link, FIFO or device), at most
    /// <see cref="ReadmeLimit"/> characters; null when there is none or it cannot be read.
    /// Limitation: the type is checked before the open (`statx` on the path), so a file swapped for a FIFO in between could
    /// still block the thread-pool thread of this load (never the UI thread: the load runs in Task.Run); opening with O_NOFOLLOW | O_NONBLOCK and checking the handle needs per-architecture flag
    /// values.</summary>
    static async Task<string?> ReadReadmeAsync(string folder, CancellationToken ct)
    {
        try
        {
            var file = Directory.EnumerateFiles(folder)
                .FirstOrDefault(f => string.Equals(System.IO.Path.GetFileName(f), "readme.md", StringComparison.OrdinalIgnoreCase));
            if (file is null || !RepositoryScan.IsRegularFile(file)) return null;
            await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, useAsync: true);
            using var reader = new StreamReader(stream);
            var buffer = new char[ReadmeLimit];
            int n = await reader.ReadBlockAsync(buffer, ct);
            var text = new string(buffer, 0, n);
            return reader.Peek() >= 0 ? text + "…" : text;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    static string Or(string a, string b) => a.Length > 0 ? a : b;

    static long? RawDate(string raw) => long.TryParse(raw.Split(' ')[0], out var s) ? s : null;

    /// <summary>Entries of `status --porcelain -z`: a rename or copy carries its source path as an extra field.</summary>
    internal static int CountStatus(string z)
    {
        var fields = z.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        int n = 0;
        for (int i = 0; i < fields.Length; i++, n++)
            if (fields[i].Length > 0 && fields[i][0] is 'R' or 'C') i++;
        return n;
    }

    /// <summary>Wording of relative dates.</summary>
    internal static string Ago(long unixSeconds, DateTimeOffset now)
    {
        var span = now - DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        static string N(double v, string unit) => (int)v == 1 ? $"1 {unit} ago" : $"{(int)v} {unit}s ago";
        return span.TotalMinutes < 1 ? "just now"
            : span.TotalHours < 1 ? N(span.TotalMinutes, "minute")
            : span.TotalDays < 1 ? N(span.TotalHours, "hour")
            : span.TotalDays < 30 ? N(span.TotalDays, "day")
            : span.TotalDays < 365 ? N(span.TotalDays / 30, "month")
            : N(span.TotalDays / 365, "year");
    }
}
