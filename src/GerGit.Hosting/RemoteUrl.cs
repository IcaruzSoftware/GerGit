using System.Net;
using System.Text.RegularExpressions;

namespace GerGit.Hosting;

/// <summary>A parsed remote URL. Scheme lower-case (`ssh` for the
/// scp-like form); UserName = the HTTP(S) user name (never for ssh); Host lower-case without port; Port null when absent
/// or the scheme's default; Slug = path without leading `/` and trailing `.git` (`owner/repo`, `group/sub/repo`).</summary>
public sealed partial record RemoteUrl(string Scheme, string? UserName, string Host, int? Port, string Slug)
{
    /// <summary>Last slug segment.</summary>
    public string RepositoryName => Slug[(Slug.LastIndexOf('/') + 1)..];

    /// <summary>`scheme://[user[:pw]@]host[:port]/path` or scp-like `[user@]host:path` (read as `ssh://host/path`);
    /// null when it is neither or has no path.</summary>
    public static RemoteUrl? Parse(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        url = url.Trim();
        if (!url.Contains("://", StringComparison.Ordinal) && ScpRegex().Match(url) is { Success: true } scp)
            url = $"ssh://{scp.Groups[1].Value}{scp.Groups[2].Value}/{scp.Groups[3].Value.TrimStart('/')}";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Host.Length == 0) return null;

        string? user = null;
        if (uri.Scheme != "ssh" && uri.UserInfo.Length > 0)
        {
            var name = Uri.UnescapeDataString(uri.UserInfo.Split(':')[0]);
            if (name.Length > 0) user = name;
        }
        var slug = Uri.UnescapeDataString(uri.AbsolutePath).Trim('/');
        if (slug.EndsWith(".git", StringComparison.Ordinal)) slug = slug[..^4].TrimEnd('/');
        if (slug.Length == 0) return null;
        return new(uri.Scheme, user, uri.Host, uri.IsDefaultPort || uri.Port < 0 ? null : uri.Port, slug);
    }

    [GeneratedRegex(@"^([^@/:]+@)?([^@/:]+):(.+)$")]
    private static partial Regex ScpRegex();

    /// <summary>An account of this host; with a URL user name only one with that username (case-insensitive),
    /// else the first in list order. The port is ignored.</summary>
    public Account? MatchAccount(IEnumerable<Account> accounts) => accounts.FirstOrDefault(a =>
        a.Host == Host && (UserName is null || string.Equals(a.Username, UserName, StringComparison.OrdinalIgnoreCase)));

    /// <summary>The account's type when one matched, else the host-name type.</summary>
    public HostType TypeFor(Account? account) => account?.Type ?? HostTypes.FromHost(Host);

    string EscapedSlug => HostingClient.EscapePath(Slug);

    /// <summary>Web URL building: `&lt;server&gt;/&lt;slug&gt;` for GitHub / GitLab (server from the URL host) and
    /// GitLab Server (the account's server URL); needs two slug segments. Null = no web URL, also when the page would not
    /// be http(s) (a stored server URL is opened in the browser, so nothing else may get through).
    /// Limitation: no Bitbucket / Azure / Visual Studio web URLs (no such accounts).</summary>
    public string? RepositoryPage(Account? account)
    {
        if (!Slug.Contains('/')) return null;
        var page = TypeFor(account) switch
        {
            HostType.GitHub or HostType.GitLab => $"https://{Host}/{EscapedSlug}",
            // Limitation: a GitLab Server under a sub-path (https://host/gitlab) keeps that prefix in the slug, so web URLs and
            // the MR project path are wrong there; strip the server path from the slug if such an instance shows up.
            HostType.GitLabServer when account is not null => $"{account.ServerUrl.ToLowerInvariant().TrimEnd('/')}/{EscapedSlug}",
            _ => null,
        };
        return Uri.TryCreate(page, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp)
            ? page : null;
    }

    /// <summary>The host's "new pull request for branch" page, null when none.</summary>
    public string? NewPullRequestPage(Account? account, string branch)
    {
        if (RepositoryPage(account) is not { } page) return null;
        return TypeFor(account) == HostType.GitHub
            ? $"{page}/compare/{WebUtility.UrlEncode(branch)}?expand=1"
            : $"{page}/-/merge_requests/new?&merge_request%5Bsource_branch%5D={GitLabEncode(branch)}";
    }

    // `#`, `/`, `&` are encoded, and so are `%` and `+` (a query decodes `+` as a space).
    static string GitLabEncode(string branch) => branch.Replace("%", "%25").Replace("#", "%23").Replace("/", "%2F")
        .Replace("&", "%26").Replace("+", "%2B");

    /// <summary>Clone dialog protocol switch: repository page URL (for a custom host
    /// `https://host[:http port]/slug`) with scheme https, no user info, no port 443, plus `.git`. Null for Azure /
    /// Visual Studio (not built yet).</summary>
    public string? HttpsForm(Account? account)
    {
        var type = TypeFor(account);
        if (type is HostType.Azure or HostType.VisualStudio) return null;
        var page = RepositoryPage(account) ??
                   $"https://{Host}{(Scheme is "http" or "https" && Port is { } p ? $":{p}" : "")}/{EscapedSlug}";
        var u = new Uri(page);
        var port = u.IsDefaultPort || u.Port == 443 ? "" : $":{u.Port}";
        return $"https://{u.Host}{port}{u.AbsolutePath}.git";
    }

    /// <summary>`git@&lt;host&gt;:&lt;slug&gt;.git`; null for Azure / Visual Studio.</summary>
    public string? SshForm(Account? account) =>
        TypeFor(account) is HostType.Azure or HostType.VisualStudio ? null : $"git@{Host}:{Slug}.git";
}

/// <summary>One entry of the clone dialog's Account selector: an account, `default` (Account and UserName null) or
/// `custom` (UserName = the URL's user name that matches no account).</summary>
public sealed record AccountChoice(string Text, Account? Account, string? UserName);

/// <summary>Clone dialog account selector (also used by the Edit Remote dialog).</summary>
public static partial class CloneAccounts
{
    /// <summary>The selector for a typed URL, or null when it is hidden. Shown for HTTPS URLs whose host has two or more
    /// accounts (deliberately counting accounts of any type, also custom host types, so
    /// self-hosted GitLab Server never got it; the host is what git's credential lookup compares). Items: `default
    /// (&lt;first account&gt;)`, one `&lt;type name&gt; (&lt;username&gt;)` per account (the UI puts a separator after `default`),
    /// `custom (&lt;name&gt;)` when the URL user name matches no account. Selected = the matching account, else custom,
    /// else default.</summary>
    public static (IReadOnlyList<AccountChoice> Items, int Selected)? Choices(string url, IReadOnlyList<Account> accounts)
    {
        if (RemoteUrl.Parse(url) is not { Scheme: "https" } remote) return null;
        var same = accounts.Where(a => a.Host == remote.Host).ToList();
        if (same.Count < 2) return null;

        var items = new List<AccountChoice> { new($"default ({same[0].Username})", null, null) };
        items.AddRange(same.Select(a => new AccountChoice($"{HostTypes.DisplayName(a.Type)} ({a.Username})", a, null)));
        if (remote.UserName is not { } user) return (items, 0);
        var match = same.FindIndex(a => string.Equals(a.Username, user, StringComparison.OrdinalIgnoreCase));
        if (match >= 0) return (items, match + 1);
        items.Add(new($"custom ({user})", null, user));
        return (items, items.Count - 1);
    }

    /// <summary>The URL with the choice's user name (`https://user@host/...`; default removes it). Any password in the
    /// URL is dropped.</summary>
    public static string Apply(string url, AccountChoice choice)
    {
        var m = UserInfoRegex().Match(url);
        if (!m.Success) return url;
        var user = choice.Account?.Username ?? choice.UserName;
        return m.Groups[1].Value + (user is null ? "" : Uri.EscapeDataString(user) + "@") + m.Groups[2].Value;
    }

    [GeneratedRegex(@"^([a-zA-Z][a-zA-Z0-9+.-]*://)(?:[^@/]*@)?(.*)$", RegexOptions.Singleline)]
    private static partial Regex UserInfoRegex();
}
