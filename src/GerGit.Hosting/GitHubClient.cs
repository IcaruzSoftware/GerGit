using System.Diagnostics;
using System.Text.Json.Nodes;

namespace GerGit.Hosting;

/// <summary>GitHub (github.com) REST: `Authorization: token &lt;T&gt;` for token and OAuth.</summary>
public sealed class GitHubClient(string token) : HostingClient(token, null)
{
    const int PageSize = 70;

    /// <summary>GitHub (github.com): the "personal access tokens" link of the login window.</summary>
    public const string TokenPageUrl = "https://github.com/settings/tokens/new?description=GerGit&scopes=repo,user,notifications,workflow";

    /// <summary>API base of new clients; tests point it at a local server.</summary>
    public static Uri DefaultApiBase { get; set; } = new("https://api.github.com");

    public Uri ApiBase { get; init; } = DefaultApiBase;

    string Url(string pathAndQuery) => ApiBase.AbsoluteUri.TrimEnd('/') + pathAndQuery;

    protected override void Authorize(HttpRequestMessage request) =>
        request.Headers.TryAddWithoutValidation("Authorization", "token " + Token);

    /// <summary>`GitHub Error: &lt;message&gt;`, plus the first `errors[].message` (e.g. "A pull request already exists").</summary>
    protected override string? ServiceError(JsonNode body)
    {
        if (Str(body, "message") is not { } message) return null;
        var detail = (Child(body, "errors") as JsonArray)?.Select(e => Str(e, "message")).FirstOrDefault(m => !string.IsNullOrEmpty(m));
        return detail is null ? $"GitHub Error: {message}" : $"GitHub Error: {message}: {detail}";
    }

    public override async Task<HostedUser> GetUserAsync(CancellationToken ct = default)
    {
        const string what = "User json";
        var json = await GetAsync(Url("/user"), ct);
        var login = Required(json, "login", what);
        var name = Str(json, "name");
        return new(login, string.IsNullOrEmpty(name) ? login : name, AvatarUrl(Required(json, "avatar_url", what)), Required(json, "html_url", what));
    }

    public override Task<IReadOnlyList<HostedRepository>> GetRepositoriesAsync(CancellationToken ct = default)
    {
        const string what = "repository array json";
        return GetPagesAsync(page => Url($"/user/repos?page={page}&per_page={PageSize}"), PageSize, what, item =>
        {
            var owner = Child(item, "owner");
            return new HostedRepository(Required(item, "name", what), Required(item, "clone_url", what),
                Required(item, "ssh_url", what), Required(owner, "login", what), AvatarUrl(Str(owner, "avatar_url")));
        }, ct);
    }

    public override async Task<CreatedPullRequest> CreatePullRequestAsync(string slug, string sourceBranch, string targetBranch,
        string title, string description, CancellationToken ct = default)
    {
        const string what = "pull request json";
        var json = await PostJsonAsync(Url($"/repos/{EscapePath(slug)}/pulls"), new JsonObject
        {
            ["title"] = title,
            ["body"] = description,
            ["head"] = sourceBranch,
            ["base"] = targetBranch,
        }, ct);
        return new(RequiredNumber(json, "number", what), Required(json, "html_url", what));
    }

    /// <summary>Notifications: `GET /notifications?page=1&amp;per_page=70&amp;all=true` (read ones too), first page only.</summary>
    public override async Task<IReadOnlyList<HostedNotification>> GetNotificationsAsync(CancellationToken ct = default)
    {
        var json = await GetAsync(Url($"/notifications?page=1&per_page={PageSize}&all=true"), ct);
        return Entries(json, "notification array json", item =>
        {
            var subject = Child(item, "subject");
            var repository = Child(item, "repository");
            NotificationKind? kind = Str(subject, "type") switch
            {
                "Commit" => NotificationKind.Commit,
                "Issue" => NotificationKind.Issue,
                "PullRequest" => NotificationKind.PullRequest,
                _ => null,
            };
            if (kind is null || Str(item, "id") is not { } id || Child(item, "unread") is not JsonValue unreadNode
                || !unreadNode.TryGetValue(out bool unread) || Date(item, "updated_at") is not { } updated
                || Str(subject, "title") is not { } title || Str(subject, "url") is not { Length: > 0 } url
                || Str(repository, "full_name") is not { } repo)
                return null;
            return new HostedNotification(id, unread, updated, kind.Value, title, url.TrimEnd('/').Split('/')[^1], WebUrl(url),
                repo, AvatarUrl(Str(Child(repository, "owner"), "avatar_url")));
        });
    }

    /// <summary>The API URL of the subject as a web page: host without `api.`, path `/repos/` prefix removed,
    /// `commits` / `pulls` → `commit` / `pull` only in the segment after `&lt;owner&gt;/&lt;repo&gt;` (repositories may
    /// be named `pulls`).</summary>
    internal static string WebUrl(string apiUrl)
    {
        if (!Uri.TryCreate(apiUrl, UriKind.Absolute, out var uri)) return apiUrl;
        var host = uri.Host.StartsWith("api.", StringComparison.Ordinal) ? uri.Host[4..] : uri.Host;
        var parts = uri.AbsolutePath.Trim('/').Split('/');
        if (parts is ["repos", _, _, var kind, ..])
        {
            parts[3] = kind switch { "commits" => "commit", "pulls" => "pull", _ => kind };
            parts = parts[1..];
        }
        return $"{uri.Scheme}://{host}{(uri.IsDefaultPort ? "" : ":" + uri.Port)}/{string.Join('/', parts)}";
    }
}

/// <summary>GitHub OAuth device flow (used instead of a loopback flow).</summary>
public sealed class GitHubDeviceFlow(string clientId)
{
    /// <summary>Client ID of GerGit's own GitHub OAuth app. Empty = OAuth is not configured in this build: the login
    /// window shows the OAuth entry with a hint to use a Personal Access Token and disables Sign In.
    /// Fill in after registering an OAuth app with "Enable Device Flow" on github.com (no client secret is needed).</summary>
    public const string ClientId = "";

    public static bool IsConfigured => ClientId.Length > 0;

    /// <summary>Web base of new flows; tests point it at a local server.</summary>
    public static Uri DefaultWebBase { get; set; } = new("https://github.com");

    public Uri WebBase { get; init; } = DefaultWebBase;

    /// <summary>The wait between polls; tests replace it so no real seconds pass.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    public sealed record DeviceCode(string Code, string UserCode, string VerificationUri, int ExpiresIn, int Interval);

    string Url(string path) => WebBase.AbsoluteUri.TrimEnd('/') + path;

    /// <summary>Step 1: `POST /login/device/code`. The user enters <see cref="DeviceCode.UserCode"/> at
    /// <see cref="DeviceCode.VerificationUri"/>.</summary>
    public async Task<DeviceCode> StartAsync(CancellationToken ct = default)
    {
        const string what = "device code json";
        var json = await PostFormAsync("/login/device/code", new()
        {
            ["client_id"] = clientId,
            ["scope"] = "repo user notifications workflow",
        }, ct);
        return new(Required(json, "device_code", what), Required(json, "user_code", what),
            Required(json, "verification_uri", what), (int)HostingClient.RequiredNumber(json, "expires_in", what),
            (int)HostingClient.RequiredNumber(json, "interval", what));
    }

    /// <summary>Step 2: polls `POST /login/oauth/access_token` every interval until the user authorized (returns the
    /// token), denied or the code expired (<see cref="HostingException"/>). Cancel → <see cref="OperationCanceledException"/>.</summary>
    public async Task<string> PollAsync(DeviceCode code, CancellationToken ct = default)
    {
        var interval = code.Interval;
        while (true)
        {
            await Delay(TimeSpan.FromSeconds(interval), ct);
            ct.ThrowIfCancellationRequested();
            var json = await PostFormAsync("/login/oauth/access_token", new()
            {
                ["client_id"] = clientId,
                ["device_code"] = code.Code,
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
            }, ct);
            if (Str(json, "access_token") is { Length: > 0 } token) return token;
            var error = Str(json, "error");
            var description = Str(json, "error_description");
            switch (error)
            {
                case "authorization_pending":
                    continue;
                case "slow_down":
                    interval = (HostingClient.Child(json, "interval") as JsonValue)?.TryGetValue(out int next) == true ? next : interval + 5;
                    continue;
                case "expired_token":
                    throw Fail(description ?? "The login code has expired. Please sign in again.");
                case "access_denied":
                    throw Fail(description ?? "The login was denied on GitHub.");
                case null:
                    throw HostingClient.ParseError("access token json");
                default:
                    throw Fail(description ?? $"GitHub Error: {error}");
            }
        }
    }

    static HostingException Fail(string text)
    {
        Trace.WriteLine($"GitHub device flow: {text}");
        return new HostingException(text);
    }

    static string? Str(JsonNode? node, string field) => HostingClient.Str(node, field);
    static string Required(JsonNode? node, string field, string what) => HostingClient.Required(node, field, what);

    Task<JsonNode> PostFormAsync(string path, Dictionary<string, string> form, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Url(path)) { Content = new FormUrlEncodedContent(form) };
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        return HostingClient.SendAsync(request, null,
            body => (Str(body, "error_description") ?? Str(body, "error") ?? Str(body, "message")) is { } m ? $"GitHub Error: {m}" : null,
            null, ct);
    }
}
