using System.Text.Json.Nodes;

namespace GerGit.Hosting;

/// <summary>GitLab (gitlab.com) and GitLab Server REST v4: `Private-Token: &lt;T&gt;`. API base = server URL.
/// <paramref name="membershipOnly"/>: gitlab.com lists only the user's projects (`membership=true`), a server lists
/// everything visible to the token.</summary>
public sealed class GitLabClient(string serverUrl, string token, string? caFile = null, bool membershipOnly = false)
    : HostingClient(token, caFile)
{
    const int PageSize = 30;

    /// <summary>GitLab / GitLab Server: the "personal access tokens" link of the login window.</summary>
    public static string TokenPageUrl(string serverUrl) =>
        $"{serverUrl}/-/user_settings/personal_access_tokens?name=GerGit&scopes=api%2Cwrite_repository";

    string Url(string pathAndQuery) => serverUrl.TrimEnd('/') + "/api/v4" + pathAndQuery;

    string? Avatar(string? url) => AvatarUrl(url, serverUrl);

    protected override void Authorize(HttpRequestMessage request) =>
        request.Headers.TryAddWithoutValidation("Private-Token", Token);

    /// <summary>`GitLab error: ` + `error_description`, else `error`, else `message` (a string, list or field map).</summary>
    protected override string? ServiceError(JsonNode body)
    {
        var value = Str(body, "error_description") ?? Str(body, "error") ?? Text(Child(body, "message"));
        return string.IsNullOrEmpty(value) ? null : $"GitLab error: {value}";
    }

    // GitLab's `message` may be {"title":["can't be blank"]} or ["Another open merge request already exists ..."].
    static string? Text(JsonNode? node) => node switch
    {
        JsonValue v => v.TryGetValue(out string? s) ? s : v.ToJsonString(),
        JsonArray a => string.Join(", ", a.Select(Text).Where(t => !string.IsNullOrEmpty(t))),
        JsonObject o => string.Join("; ", o.Select(p => $"{p.Key} {Text(p.Value)}")),
        _ => null,
    };

    public override async Task<HostedUser> GetUserAsync(CancellationToken ct = default)
    {
        var json = await GetAsync(Url("/user"), ct);
        var username = Required(json, "username", "User json");
        var name = Str(json, "name");
        return new(username, string.IsNullOrEmpty(name) ? username : name, Avatar(Str(json, "avatar_url")), Str(json, "web_url"));
    }

    public override Task<IReadOnlyList<HostedRepository>> GetRepositoriesAsync(CancellationToken ct = default)
    {
        const string what = "repository array json";
        var membership = membershipOnly ? "&membership=true" : "";
        return GetPagesAsync(page => Url($"/projects?page={page}&per_page={PageSize}{membership}"), PageSize, what, item =>
        {
            var space = Child(item, "namespace");
            var owner = Child(item, "owner");
            return new HostedRepository(Required(item, "name", what), Required(item, "http_url_to_repo", what),
                Required(item, "ssh_url_to_repo", what), Str(space, "name") ?? Str(owner, "name") ?? "",
                Avatar(Str(item, "avatar_url") ?? Str(space, "avatar_url") ?? Str(owner, "avatar_url")));
        }, ct);
    }

    public override async Task<CreatedPullRequest> CreatePullRequestAsync(string slug, string sourceBranch, string targetBranch,
        string title, string description, CancellationToken ct = default)
    {
        const string what = "merge request json";
        // The project path is one URL-encoded path segment (`group%2Fsub%2Frepo`).
        var json = await PostJsonAsync(Url($"/projects/{Uri.EscapeDataString(slug)}/merge_requests"), new JsonObject
        {
            ["source_branch"] = sourceBranch,
            ["target_branch"] = targetBranch,
            ["title"] = title,
            ["description"] = description,
        }, ct);
        return new(RequiredNumber(json, "iid", what), Required(json, "web_url", what));
    }

    /// <summary>The pending to-do items:
    /// `GET /api/v4/todos?page=1&amp;per_page=70`, first page only. All are unread; merge requests count as pull requests.</summary>
    public override async Task<IReadOnlyList<HostedNotification>> GetNotificationsAsync(CancellationToken ct = default)
    {
        var json = await GetAsync(Url("/todos?page=1&per_page=70"), ct);
        return Entries(json, "todo array json", item =>
        {
            var target = Child(item, "target");
            var project = Child(item, "project");
            NotificationKind? kind = Str(item, "target_type") switch
            {
                "Commit" => NotificationKind.Commit,
                "Issue" => NotificationKind.Issue,
                "MergeRequest" => NotificationKind.PullRequest,
                _ => null,
            };
            var targetId = kind == NotificationKind.Commit ? Str(target, "id") : Number(target, "iid");
            if (kind is null || targetId is null || Number(item, "id") is not { } id
                || (Date(item, "updated_at") ?? Date(item, "created_at")) is not { } updated
                || (Str(target, "title") ?? Str(item, "body")) is not { } title
                || Str(item, "target_url") is not { Length: > 0 } url || Str(project, "path_with_namespace") is not { } repo)
                return null;
            return new HostedNotification(id, Str(item, "state") != "done", updated, kind.Value, title, targetId, url, repo,
                Avatar(Str(project, "avatar_url") ?? Str(Child(item, "author"), "avatar_url")));
        });
    }
}
