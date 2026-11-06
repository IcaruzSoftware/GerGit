using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using GerGit.Hosting;

namespace GerGit.Tests.Hosting;

/// <summary>GitHub / GitLab REST round trips against the fake server.</summary>
public sealed class HostingClientTests : IDisposable
{
    const string Token = "ghp_SECRET0123456789";
    const string Json = "application/json; charset=utf-8";

    readonly FakeHostingServer _server = new();

    public void Dispose() => _server.Dispose();

    GitHubClient GitHub() => new(Token) { ApiBase = _server.BaseUri };
    GitLabClient GitLab(bool cloud = false) => new(_server.Url, Token, membershipOnly: cloud);

    static object GitHubRepo(int i) => new
    {
        name = $"repo{i}",
        clone_url = $"https://github.com/o/repo{i}.git",
        ssh_url = $"git@github.com:o/repo{i}.git",
        owner = new { login = i % 2 == 0 ? "even" : "odd", avatar_url = "https://avatars/o" },
    };

    static object GitLabRepo(int i) => new
    {
        name = $"p{i}",
        http_url_to_repo = $"https://gl/g/p{i}.git",
        ssh_url_to_repo = $"git@gl:g/p{i}.git",
        @namespace = new { name = "Group", avatar_url = (string?)null },
        owner = new { name = "Owner", avatar_url = "https://gl/owner.png" },
    };

    static FakeResponse Page(FakeRequest r, int total, int pageSize, Func<int, object> item)
    {
        var page = int.Parse(r.QueryValue("page")!);
        var first = (page - 1) * pageSize;
        return FakeResponse.Json(Enumerable.Range(first, Math.Clamp(total - first, 0, pageSize)).Select(item).ToArray());
    }

    [Fact]
    public async Task GitHub_user_info_sends_the_token_header_and_reads_the_fields()
    {
        _server.Map("GET", "/user", FakeResponse.Json(new
        {
            login = "octo", name = "Octo Cat", avatar_url = "https://a/octo.png", html_url = "https://github.com/octo",
        }));

        var user = await GitHub().GetUserAsync();

        Assert.Equal(new HostedUser("octo", "Octo Cat", "https://a/octo.png", "https://github.com/octo"), user);
        var r = Assert.Single(_server.Requests);
        Assert.Equal("token " + Token, r.Header("Authorization"));
        Assert.Null(r.Header("Private-Token"));
        Assert.StartsWith("GerGit ", r.Header("User-Agent"));
        Assert.Equal("application/json; charset=utf-8", r.Header("Accept"));
        Assert.Null(r.Header("Cookie"));
    }

    [Fact]
    public async Task GitHub_display_name_falls_back_to_the_login()
    {
        _server.Map("GET", "/user", FakeResponse.Json(new { login = "octo", name = (string?)null, avatar_url = "a", html_url = "h" }));
        Assert.Equal("octo", (await GitHub().GetUserAsync()).DisplayName);
    }

    [Fact]
    public async Task GitLab_user_info_sends_Private_Token()
    {
        _server.Map("GET", "/api/v4/user", FakeResponse.Json(new
        {
            username = "jdoe", name = "Jane Doe", avatar_url = "https://gl/a.png", web_url = "https://gl/jdoe",
        }));

        var user = await GitLab().GetUserAsync();

        Assert.Equal(new HostedUser("jdoe", "Jane Doe", "https://gl/a.png", "https://gl/jdoe"), user);
        var r = Assert.Single(_server.Requests);
        Assert.Equal(Token, r.Header("Private-Token"));
        Assert.Null(r.Header("Authorization"));
    }

    [Fact]
    public async Task HostingClient_For_picks_the_client_by_type()
    {
        Assert.IsType<GitHubClient>(HostingClient.For(new Account(HostType.GitHub, AuthType.OAuth, Account.GitHubServer, "u"), Token));
        var server = new Account(HostType.GitLabServer, AuthType.AccessToken, _server.Url, "u", CaFile: "/ca.pem");
        Assert.Equal("/ca.pem", Assert.IsType<GitLabClient>(HostingClient.For(server, Token)).CaFile);

        _server.Map("GET", "/api/v4/projects", r => Page(r, 0, 30, GitLabRepo));
        await HostingClient.For(HostType.GitLab, _server.Url, Token).GetRepositoriesAsync();
        Assert.Equal("true", Assert.Single(_server.Requests).QueryValue("membership")); // type GitLab = cloud
    }

    [Fact]
    public async Task GitHub_repositories_page_by_70_until_a_short_page()
    {
        _server.Map("GET", "/user/repos", r => Page(r, 150, 70, GitHubRepo));

        var repos = await GitHub().GetRepositoriesAsync();

        Assert.Equal(150, repos.Count);
        Assert.Equal(["1", "2", "3"], _server.Requests.Select(r => r.QueryValue("page")!));
        Assert.All(_server.Requests, r => Assert.Equal("70", r.QueryValue("per_page")));
        Assert.Equal(new HostedRepository("repo1", "https://github.com/o/repo1.git", "git@github.com:o/repo1.git", "odd",
            "https://avatars/o"), repos[1]);
    }

    [Fact]
    public async Task Repository_lists_stop_after_20_pages()
    {
        _server.Map("GET", "/user/repos", r => Page(r, int.MaxValue / 2, 70, GitHubRepo));

        var repos = await GitHub().GetRepositoriesAsync();

        Assert.Equal(20 * 70, repos.Count);
        Assert.Equal(20, _server.Requests.Count);
    }

    [Fact]
    public async Task GitLab_cloud_lists_member_projects_by_30()
    {
        _server.Map("GET", "/api/v4/projects", r => Page(r, 45, 30, GitLabRepo));

        var repos = await GitLab(cloud: true).GetRepositoriesAsync();

        Assert.Equal(45, repos.Count);
        Assert.Equal(2, _server.Requests.Count);
        Assert.All(_server.Requests, r => Assert.Equal(("30", "true"), (r.QueryValue("per_page"), r.QueryValue("membership"))));
        // group = namespace.name; avatar = avatar_url, else namespace's, else owner's
        Assert.Equal(new HostedRepository("p0", "https://gl/g/p0.git", "git@gl:g/p0.git", "Group", "https://gl/owner.png"), repos[0]);
    }

    [Fact]
    public async Task GitLab_server_lists_all_visible_projects()
    {
        _server.Map("GET", "/api/v4/projects", FakeResponse.Json(new object[]
        {
            new { name = "x", http_url_to_repo = "h", ssh_url_to_repo = "s", owner = new { name = "Owner" } },
            new { name = "y", http_url_to_repo = "h", ssh_url_to_repo = "s" },
        }));

        var repos = await GitLab().GetRepositoriesAsync();

        Assert.Null(Assert.Single(_server.Requests).QueryValue("membership"));
        Assert.Equal(["Owner", ""], repos.Select(r => r.Group));
    }

    [Fact]
    public async Task A_missing_required_field_fails_the_whole_list()
    {
        _server.Map("GET", "/user/repos", FakeResponse.Json(new object[]
        {
            GitHubRepo(1),
            new { name = "broken", ssh_url = "s", owner = new { login = "o" } }, // no clone_url
        }));

        var e = await Assert.ThrowsAsync<HostingException>(() => GitHub().GetRepositoriesAsync());
        Assert.Equal("Parse Error: repository array json. Contact support.", e.Message);
    }

    public static TheoryData<bool, int, string, string, string> ErrorRows => new()
    {
        { true, 404, Json, """{"message":"Not Found"}""", "Not found (404)" },
        { false, 404, "text/html", "<html/>", "Not found (404)" },
        { true, 401, Json, """{"message":"Bad credentials"}""", "GitHub Error: Bad credentials" },
        { true, 422, Json, """{"foo":1}""", "Error: raw json error" },
        { true, 500, "text/plain", "oops", "HTTP Error 500 (Internal Server Error)" },
        { true, 401, "text/html", "<html/>", "HTTP Error 401 (Unauthorized)" },
        { false, 401, Json, """{"message":"401 Unauthorized"}""", "GitLab error: 401 Unauthorized" },
        { false, 401, Json, """{"error":"invalid_token","error_description":"Token was revoked.","message":"m"}""", "GitLab error: Token was revoked." },
        { false, 403, Json, """{"error":"insufficient_scope"}""", "GitLab error: insufficient_scope" },
        { false, 400, Json, """{"message":{"title":["can't be blank"]}}""", "GitLab error: title can't be blank" },
        { false, 502, "text/html", "<html/>", "HTTP Error 502 (Bad Gateway)" },
    };

    [Theory]
    [MemberData(nameof(ErrorRows))]
    public async Task Error_mapping(bool github, int status, string contentType, string body, string expected)
    {
        var path = github ? "/user" : "/api/v4/user";
        _server.Map("GET", path, new FakeResponse(status, body, contentType));
        HostingClient client = github ? GitHub() : GitLab();

        var e = await Assert.ThrowsAsync<HostingException>(() => client.GetUserAsync());
        Assert.Equal(expected, e.Message);
    }

    [Fact]
    public async Task Rate_limit_shows_the_GitHub_message()
    {
        _server.Map("GET", "/user", FakeResponse.Json(new { message = "API rate limit exceeded for 10.0.0.1." }, 403,
            new Dictionary<string, string> { ["X-RateLimit-Remaining"] = "0" }));

        var e = await Assert.ThrowsAsync<HostingException>(() => GitHub().GetUserAsync());
        Assert.Equal("GitHub Error: API rate limit exceeded for 10.0.0.1.", e.Message);
    }

    [Fact]
    public async Task Network_failure_shows_the_innermost_message()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop(); // nothing listens there now

        var e = await Assert.ThrowsAsync<HostingException>(() =>
            new GitHubClient(Token) { ApiBase = new Uri($"http://127.0.0.1:{port}") }.GetUserAsync());
        Assert.StartsWith("Error: ", e.Message);
        Assert.DoesNotContain("HttpRequestException", e.Message);
    }

    [Fact]
    public async Task Body_that_is_not_JSON()
    {
        _server.Map("GET", "/user", new FakeResponse(200, "<html>login</html>", "text/html"));
        var e = await Assert.ThrowsAsync<HostingException>(() => GitHub().GetUserAsync());
        Assert.Equal("Cannot parse JSON in server response", e.Message);
    }

    [Fact]
    public async Task Wrong_JSON_shape_is_a_parse_error()
    {
        _server.Map("GET", "/user", FakeResponse.Json(new[] { 1, 2 }));
        var e = await Assert.ThrowsAsync<HostingException>(() => GitHub().GetUserAsync());
        Assert.Equal("Parse Error: User json. Contact support.", e.Message);

        _server.Map("GET", "/api/v4/user", FakeResponse.Json(new { name = "no username" }));
        e = await Assert.ThrowsAsync<HostingException>(() => GitLab().GetUserAsync());
        Assert.Equal("Parse Error: User json. Contact support.", e.Message);

        _server.Map("GET", "/user/repos", FakeResponse.Json(new { message = "not a list" }));
        e = await Assert.ThrowsAsync<HostingException>(() => GitHub().GetRepositoriesAsync());
        Assert.Equal("Parse Error: repository array json. Contact support.", e.Message);
    }

    [Fact]
    public async Task GitHub_creates_a_pull_request()
    {
        _server.Map("POST", "/repos/o/r/pulls", FakeResponse.Json(new { number = 42, html_url = "https://github.com/o/r/pull/42" }, 201));

        var pr = await GitHub().CreatePullRequestAsync("o/r", "feature/x", "main", "Title", "Body\nline 2");

        Assert.Equal(new CreatedPullRequest(42, "https://github.com/o/r/pull/42"), pr);
        var r = Assert.Single(_server.Requests);
        Assert.Equal("token " + Token, r.Header("Authorization"));
        Assert.StartsWith("application/json", r.Header("Content-Type"));
        var body = JsonNode.Parse(r.Body)!;
        Assert.Equal(("Title", "Body\nline 2", "feature/x", "main"),
            ((string)body["title"]!, (string)body["body"]!, (string)body["head"]!, (string)body["base"]!));
    }

    [Fact]
    public async Task GitHub_pull_request_error_includes_the_first_detail()
    {
        _server.Map("POST", "/repos/o/r/pulls", FakeResponse.Json(new
        {
            message = "Validation Failed",
            errors = new object[] { new { resource = "PullRequest", code = "custom", message = "A pull request already exists for o:x." } },
        }, 422));

        var e = await Assert.ThrowsAsync<HostingException>(() => GitHub().CreatePullRequestAsync("o/r", "x", "main", "t", ""));
        Assert.Equal("GitHub Error: Validation Failed: A pull request already exists for o:x.", e.Message);
    }

    [Fact]
    public async Task GitLab_creates_a_merge_request_with_the_encoded_project_path()
    {
        _server.Map("POST", "/api/v4/projects/group%2Fsub%2Frepo/merge_requests",
            FakeResponse.Json(new { iid = 7, web_url = "https://gl/group/sub/repo/-/merge_requests/7" }, 201));

        var mr = await GitLab().CreatePullRequestAsync("group/sub/repo", "feature", "develop", "T", "D");

        Assert.Equal(new CreatedPullRequest(7, "https://gl/group/sub/repo/-/merge_requests/7"), mr);
        var body = JsonNode.Parse(Assert.Single(_server.Requests).Body)!;
        Assert.Equal(("feature", "develop", "T", "D"),
            ((string)body["source_branch"]!, (string)body["target_branch"]!, (string)body["title"]!, (string)body["description"]!));
    }

    [Fact]
    public async Task GitLab_merge_request_conflict_message()
    {
        _server.Map("POST", "/api/v4/projects/g%2Fr/merge_requests", FakeResponse.Json(new
        {
            message = new[] { "Another open merge request already exists for this source branch: !1" },
        }, 409));

        var e = await Assert.ThrowsAsync<HostingException>(() => GitLab().CreatePullRequestAsync("g/r", "f", "main", "t", ""));
        Assert.Equal("GitLab error: Another open merge request already exists for this source branch: !1", e.Message);
    }

    [Fact]
    public async Task The_token_never_leaks_into_errors_or_the_trace()
    {
        var log = new StringWriter();
        var listener = new TextWriterTraceListener(log);
        Trace.Listeners.Add(listener);
        try
        {
            _server.Map("GET", "/user", FakeResponse.Json(new { message = "Bad credentials" }, 401));
            _server.Map("GET", "/user/repos", FakeResponse.Json(new { message = "API rate limit exceeded" }, 403,
                new Dictionary<string, string> { ["X-RateLimit-Remaining"] = "0" }));
            // A server that echoes the token back in its error text.
            _server.Map("GET", "/api/v4/user", FakeResponse.Json(new { message = $"token {Token} is revoked" }, 401));

            var errors = new List<string>
            {
                (await Assert.ThrowsAsync<HostingException>(() => GitHub().GetUserAsync())).Message,
                (await Assert.ThrowsAsync<HostingException>(() => GitHub().GetRepositoriesAsync())).Message,
                (await Assert.ThrowsAsync<HostingException>(() => GitLab().GetUserAsync())).Message,
            };

            listener.Flush();
            var trace = log.ToString();
            Assert.Contains("GET http://127.0.0.1:", trace);
            Assert.Contains("/user -> 401", trace);
            Assert.DoesNotContain(Token, trace);
            Assert.All(errors, m => Assert.DoesNotContain(Token, m));
            Assert.Equal("GitLab error: token *** is revoked", errors[2]);
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }
    }

    [Fact]
    public async Task Account_CA_file_is_trusted_and_the_system_store_alone_is_not()
    {
        using var tls = new FakeHostingServer(TestCertificates.Server);
        tls.Map("GET", "/api/v4/user", FakeResponse.Json(new { username = "jdoe" }));
        var dir = Directory.CreateTempSubdirectory("gergit-ca-").FullName;
        try
        {
            var e = await Assert.ThrowsAsync<HostingException>(() => new GitLabClient(tls.Url, Token).GetUserAsync());
            Assert.StartsWith("Error: ", e.Message);

            var withCa = new GitLabClient(tls.Url, Token, TestCertificates.WriteCaFile(dir));
            Assert.Equal("jdoe", (await withCa.GetUserAsync()).Username);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    public static TheoryData<string> RejectedCertificates => ["other CA", "wrong host"];

    [Theory]
    [MemberData(nameof(RejectedCertificates))]
    public async Task Account_CA_file_does_not_accept_a_foreign_CA_or_a_wrong_host(string kind)
    {
        var certificate = kind == "other CA"
            ? TestCertificates.CreateServer(TestCertificates.CreateCa("Other Test CA"), "localhost", includeLoopback: true)
            : TestCertificates.CreateServer(TestCertificates.Ca, "wrong.example", includeLoopback: false);
        using var tls = new FakeHostingServer(certificate);
        tls.Map("GET", "/api/v4/user", FakeResponse.Json(new { username = "jdoe" }));
        var dir = Directory.CreateTempSubdirectory("gergit-ca-").FullName;
        try
        {
            var e = await Assert.ThrowsAsync<HostingException>(() =>
                new GitLabClient(tls.Url, Token, TestCertificates.WriteCaFile(dir)).GetUserAsync());
            Assert.StartsWith("Error: ", e.Message);
            Assert.Empty(tls.Requests); // no request (and no token) went over the rejected connection
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Authorization_headers_are_dropped_on_a_redirect_to_another_host()
    {
        using var other = new FakeHostingServer();
        other.Map("GET", "/api/v4/user", FakeResponse.Json(new { username = "jdoe" }));
        _server.Map("GET", "/api/v4/user", new FakeResponse(302, Headers: new Dictionary<string, string>
        {
            ["Location"] = other.Url + "/api/v4/user",
        }));
        _server.Map("GET", "/user", new FakeResponse(301, Headers: new Dictionary<string, string> { ["Location"] = "/moved/user" }));
        _server.Map("GET", "/moved/user", FakeResponse.Json(new { login = "octo", avatar_url = "a", html_url = "h" }));

        Assert.Equal("jdoe", (await GitLab().GetUserAsync()).Username);
        var r = Assert.Single(other.Requests);
        Assert.Null(r.Header("Private-Token"));
        Assert.Null(r.Header("Authorization"));
        Assert.StartsWith("GerGit ", r.Header("User-Agent"));

        // same scheme + host + port: the header stays
        Assert.Equal("octo", (await GitHub().GetUserAsync()).Username);
        Assert.Equal("token " + Token, _server.Requests.Last().Header("Authorization"));
        Assert.Equal("/moved/user", _server.Requests.Last().Path);
    }

    [Fact]
    public async Task Unreadable_CA_file_is_an_error_message()
    {
        var e = await Assert.ThrowsAsync<HostingException>(() =>
            new GitLabClient(_server.Url, Token, "/nonexistent/gergit-ca.pem").GetUserAsync());
        Assert.StartsWith("Error: ", e.Message);
    }

    [Fact]
    public async Task Response_larger_than_20_MB_fails()
    {
        _server.Map("GET", "/user", new FakeResponse(200, $"{{\"login\":\"{new string('x', HostingClient.MaxBodyBytes)}\"}}"));
        var e = await Assert.ThrowsAsync<HostingException>(() => GitHub().GetUserAsync());
        Assert.Equal("Error: the server response is larger than 20 MB", e.Message);
    }

    [Theory]
    [InlineData("https://a/octo.png", null, "https://a/octo.png")]
    [InlineData("http://a/octo.png", null, null)]
    [InlineData("http://gitlab.corp:8080/u/1.png", "http://GitLab.corp:8080", "http://gitlab.corp:8080/u/1.png")]
    [InlineData("http://gitlab.corp/u/1.png", "http://gitlab.corp:8080", null)]
    [InlineData("http://other.corp:8080/u/1.png", "http://gitlab.corp:8080", null)]
    [InlineData("http://gitlab.corp/u/1.png", "https://gitlab.corp", null)]
    [InlineData("file:///etc/passwd", null, null)]
    [InlineData("/uploads/1.png", "http://gitlab.corp", null)]
    [InlineData(null, null, null)]
    public void Avatar_URLs_are_https_or_the_plain_http_GitLab_server_itself(string? url, string? server, string? expected) =>
        Assert.Equal(expected, HostingClient.AvatarUrl(url, server));

    [Fact]
    public async Task Clients_drop_avatar_URLs_that_are_not_allowed()
    {
        _server.Map("GET", "/user", FakeResponse.Json(new { login = "octo", avatar_url = "http://a/octo.png", html_url = "h" }));
        Assert.Null((await GitHub().GetUserAsync()).AvatarUrl);

        var own = _server.Url + "/uploads/jdoe.png";
        _server.Map("GET", "/api/v4/user", FakeResponse.Json(new { username = "jdoe", avatar_url = own }));
        Assert.Equal(own, (await GitLab().GetUserAsync()).AvatarUrl); // the plain http server's own origin
        _server.Map("GET", "/api/v4/projects", FakeResponse.Json(new object[]
        {
            new { name = "x", http_url_to_repo = "h", ssh_url_to_repo = "s", avatar_url = "http://elsewhere.test/x.png" },
        }));
        Assert.Null(Assert.Single(await GitLab().GetRepositoriesAsync()).AvatarUrl);
    }
}
