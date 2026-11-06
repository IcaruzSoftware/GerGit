using GerGit.Hosting;

namespace GerGit.Tests.Hosting;

/// <summary>Notification calls (GitHub; GitLab to-dos) and the
/// account's notification fields, against the fake server.</summary>
public sealed class NotificationsTests : IDisposable
{
    const string Token = "ghp_SECRET0123456789";

    readonly FakeHostingServer _server = new();

    public void Dispose() => _server.Dispose();

    static object GitHubItem(string id, string type, string url, bool unread = true, string updated = "2026-10-03T12:05:00Z") => new
    {
        id,
        unread,
        updated_at = updated,
        subject = new { title = "Title " + id, url, type },
        repository = new { full_name = "octo/repo", owner = new { avatar_url = "https://avatars/octo" } },
    };

    [Fact]
    public async Task GitHub_reads_the_first_page_keeps_commits_issues_and_pull_requests_and_builds_web_urls()
    {
        _server.Map("GET", "/notifications", FakeResponse.Json(new object[]
        {
            GitHubItem("1", "PullRequest", "https://api.github.com/repos/octo/repo/pulls/42"),
            GitHubItem("2", "Issue", "https://api.github.com/repos/octo/repo/issues/7", unread: false),
            GitHubItem("3", "Commit", "https://api.github.com/repos/octo/repo/commits/0123456789abcdef"),
            GitHubItem("4", "Release", "https://api.github.com/repos/octo/repo/releases/1"),
            new { id = "5", unread = true, updated_at = "2026-10-03T12:05:00Z", subject = new { title = "no url", type = "Issue" },
                  repository = new { full_name = "octo/repo" } },
            GitHubItem("6", "Issue", "https://api.github.com/repos/octo/repo/issues/8", updated: "not a date"),
        }));

        var list = await new GitHubClient(Token) { ApiBase = _server.BaseUri }.GetNotificationsAsync();

        var r = Assert.Single(_server.Requests);
        Assert.Equal(("1", "70", "true"), (r.QueryValue("page"), r.QueryValue("per_page"), r.QueryValue("all")));
        Assert.Equal("token " + Token, r.Header("Authorization"));
        Assert.Equal(
        [
            new HostedNotification("1", true, new DateTimeOffset(2026, 10, 3, 12, 5, 0, TimeSpan.Zero), NotificationKind.PullRequest,
                "Title 1", "42", "https://github.com/octo/repo/pull/42", "octo/repo", "https://avatars/octo"),
            new HostedNotification("2", false, new DateTimeOffset(2026, 10, 3, 12, 5, 0, TimeSpan.Zero), NotificationKind.Issue,
                "Title 2", "7", "https://github.com/octo/repo/issues/7", "octo/repo", "https://avatars/octo"),
            new HostedNotification("3", true, new DateTimeOffset(2026, 10, 3, 12, 5, 0, TimeSpan.Zero), NotificationKind.Commit,
                "Title 3", "0123456789abcdef", "https://github.com/octo/repo/commit/0123456789abcdef", "octo/repo", "https://avatars/octo"),
        ], list);
    }

    [Fact]
    public async Task GitLab_reads_pending_todos_with_Private_Token()
    {
        _server.Map("GET", "/api/v4/todos", FakeResponse.Json(new object[]
        {
            new
            {
                id = 101, state = "pending", target_type = "MergeRequest", updated_at = "2026-10-04T08:00:00.000Z",
                target = new { iid = 5, id = 9999, title = "Fix it" }, target_url = "https://gl/g/p/-/merge_requests/5",
                project = new { path_with_namespace = "g/p" }, author = new { avatar_url = "https://gl/a.png" },
            },
            new
            {
                id = 102, state = "pending", target_type = "Commit", created_at = "2026-10-02T08:00:00Z",
                target = new { id = "abcdef0123", title = "Commit title" }, target_url = "https://gl/g/p/-/commit/abcdef0123",
                project = new { path_with_namespace = "g/p", avatar_url = "https://gl/p.png" }, author = new { avatar_url = "https://gl/a.png" },
            },
            new
            {
                id = 103, state = "pending", target_type = "Epic", updated_at = "2026-10-04T08:00:00Z",
                target = new { iid = 1, title = "Epic" }, target_url = "https://gl/groups/g/-/epics/1",
                project = new { path_with_namespace = "g/p" },
            },
        }));

        var list = await new GitLabClient(_server.Url, Token).GetNotificationsAsync();

        var r = Assert.Single(_server.Requests);
        Assert.Equal(("/api/v4/todos", "1", "70"), (r.Path, r.QueryValue("page"), r.QueryValue("per_page")));
        Assert.Equal(Token, r.Header("Private-Token"));
        Assert.Equal(
        [
            new HostedNotification("101", true, new DateTimeOffset(2026, 10, 4, 8, 0, 0, TimeSpan.Zero), NotificationKind.PullRequest,
                "Fix it", "5", "https://gl/g/p/-/merge_requests/5", "g/p", "https://gl/a.png"),
            new HostedNotification("102", true, new DateTimeOffset(2026, 10, 2, 8, 0, 0, TimeSpan.Zero), NotificationKind.Commit,
                "Commit title", "abcdef0123", "https://gl/g/p/-/commit/abcdef0123", "g/p", "https://gl/p.png"),
        ], list);
    }

    [Theory]
    [InlineData("https://api.github.com/repos/o/pulls/pulls/3", "https://github.com/o/pulls/pull/3")]
    [InlineData("https://api.github.com/repos/commits/repos/commits/abc", "https://github.com/commits/repos/commit/abc")]
    [InlineData("https://api.github.com/repos/o/r/issues/9", "https://github.com/o/r/issues/9")]
    [InlineData("http://127.0.0.1:8080/repos/o/r/pulls/1", "http://127.0.0.1:8080/o/r/pull/1")]
    public void GitHub_web_url_rewrites_only_the_prefix_and_the_kind_segment(string api, string web) =>
        Assert.Equal(web, GitHubClient.WebUrl(api));

    [Fact]
    public async Task A_server_error_is_a_friendly_text_without_the_echoed_token()
    {
        _server.Map("GET", "/notifications", FakeResponse.Json(new { message = "Bad token " + Token }, 401));
        var e = await Assert.ThrowsAsync<HostingException>(() => new GitHubClient(Token) { ApiBase = _server.BaseUri }.GetNotificationsAsync());
        Assert.Equal("GitHub Error: Bad token ***", e.Message);
    }

    [Fact]
    public void Change_replaces_in_place_and_can_skip_the_Changed_event()
    {
        var dir = Directory.CreateTempSubdirectory("gergit-notif-").FullName;
        try
        {
            var path = Path.Combine(dir, "accounts.json");
            var store = new AccountStore(path);
            var a = new Account(HostType.GitHub, AuthType.AccessToken, Account.GitHubServer, "alice", EnableNotifications: true);
            var b = new Account(HostType.GitLab, AuthType.AccessToken, Account.GitLabServer, "bob");
            store.Add(a);
            store.Add(b);
            int changed = 0;
            store.Changed += () => changed++;
            var at = new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.Zero);

            store.Change(a, x => x with { NotificationsUpdatedAt = at }, raiseChanged: false);
            store.Change(b, x => x with { EnableNotifications = true });

            Assert.Equal(1, changed);
            Assert.Equal([a with { NotificationsUpdatedAt = at }, b with { EnableNotifications = true }], new AccountStore(path).Accounts);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
