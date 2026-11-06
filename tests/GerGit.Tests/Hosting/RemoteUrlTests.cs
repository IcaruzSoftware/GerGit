using GerGit.Hosting;

namespace GerGit.Tests.Hosting;

/// <summary>Matching a remote URL to an account, web URL building, URL conversion, clone account selector.</summary>
public class RemoteUrlTests
{
    static readonly Account GitHubA = new(HostType.GitHub, AuthType.OAuth, Account.GitHubServer, "alice");
    static readonly Account GitHubB = new(HostType.GitHub, AuthType.AccessToken, Account.GitHubServer, "Bob");
    static readonly Account GitLabC = new(HostType.GitLab, AuthType.AccessToken, Account.GitLabServer, "carol");
    static readonly Account Server = new(HostType.GitLabServer, AuthType.AccessToken, "https://git.corp.example:8443", "dave");

    [Theory]
    [InlineData("https://github.com/owner/repo.git", "https", null, "github.com", null, "owner/repo", "repo")]
    [InlineData("http://git.example.com:8080/team/app", "http", null, "git.example.com", 8080, "team/app", "app")]
    [InlineData("https://GitHub.com:443/Owner/Repo/", "https", null, "github.com", null, "Owner/Repo", "Repo")]
    [InlineData("git@github.com:owner/repo.git", "ssh", null, "github.com", null, "owner/repo", "repo")]
    [InlineData("ssh://git@gitlab.com:2222/group/sub/proj.git", "ssh", null, "gitlab.com", 2222, "group/sub/proj", "proj")]
    [InlineData("https://bob:secret@github.com/o/r.git", "https", "bob", "github.com", null, "o/r", "r")]
    [InlineData("https://:pw@github.com/o/r", "https", null, "github.com", null, "o/r", "r")]
    [InlineData("https://gitlab.com/a/b/c/d.git", "https", null, "gitlab.com", null, "a/b/c/d", "d")]
    [InlineData("org@vs-ssh.visualstudio.com:v3/org/proj/repo", "ssh", null, "vs-ssh.visualstudio.com", null, "v3/org/proj/repo", "repo")]
    [InlineData("host.example:repo", "ssh", null, "host.example", null, "repo", "repo")]
    public void Parse(string url, string scheme, string? user, string host, int? port, string slug, string name)
    {
        var r = RemoteUrl.Parse(url);
        Assert.NotNull(r);
        Assert.Equal((scheme, user, host, port, slug, name), (r.Scheme, r.UserName, r.Host, r.Port, r.Slug, r.RepositoryName));
    }

    [Theory]
    [InlineData("")]
    [InlineData("/srv/git/repo.git")]
    [InlineData("../other")]
    [InlineData("https://github.com/")]
    [InlineData("file:///srv/git/repo.git")]
    public void Not_a_remote_URL(string url) => Assert.Null(RemoteUrl.Parse(url));

    [Fact]
    public void Account_matching()
    {
        Account[] accounts = [GitHubA, GitHubB, GitLabC, Server];
        // no user name: first account of the host; ssh never has one
        Assert.Same(GitHubA, RemoteUrl.Parse("https://github.com/o/r")!.MatchAccount(accounts));
        Assert.Same(GitHubA, RemoteUrl.Parse("git@github.com:o/r.git")!.MatchAccount(accounts));
        // user name: case-insensitive, and no fallback to another account
        Assert.Same(GitHubB, RemoteUrl.Parse("https://bob@github.com/o/r")!.MatchAccount(accounts));
        Assert.Null(RemoteUrl.Parse("https://eve@github.com/o/r")!.MatchAccount(accounts));
        // the port of the remote is ignored, the host must be equal
        Assert.Same(Server, RemoteUrl.Parse("ssh://git@git.corp.example:2222/g/r.git")!.MatchAccount(accounts));
        Assert.Same(Server, RemoteUrl.Parse("https://git.corp.example:8443/g/r.git")!.MatchAccount(accounts));
        Assert.Null(RemoteUrl.Parse("https://corp.example/g/r.git")!.MatchAccount(accounts));
    }

    [Theory]
    [InlineData("github.com", HostType.GitHub)]
    [InlineData("gist.github.com", HostType.GitHub)]
    [InlineData("gitlab.com", HostType.GitLab)]
    [InlineData("bitbucket.org", HostType.Bitbucket)]
    [InlineData("ssh.dev.azure.com", HostType.Azure)]
    [InlineData("org.visualstudio.com", HostType.VisualStudio)]
    [InlineData("git.corp.example", HostType.Custom)]
    public void Host_name_rule(string host, HostType type) => Assert.Equal(type, HostTypes.FromHost(host));

    [Fact]
    public void Remote_type_is_the_account_type_else_the_host_type()
    {
        var url = RemoteUrl.Parse("https://git.corp.example/g/r.git")!;
        Assert.Equal(HostType.Custom, url.TypeFor(null));
        Assert.Equal(HostType.GitLabServer, url.TypeFor(Server));
        Assert.Equal("GitLab Server", HostTypes.DisplayName(HostType.GitLabServer));
        Assert.Equal("Visual Studio", HostTypes.DisplayName(HostType.VisualStudio));
        Assert.Null(HostTypes.DisplayName(HostType.Custom));
    }

    [Fact]
    public void Web_URLs()
    {
        var gh = RemoteUrl.Parse("git@github.com:owner/repo.git")!;
        Assert.Equal("https://github.com/owner/repo", gh.RepositoryPage(null));
        Assert.Equal("https://github.com/owner/repo/compare/feature%2Fa+b%23c%26d?expand=1", gh.NewPullRequestPage(null, "feature/a b#c&d"));

        var gl = RemoteUrl.Parse("https://gitlab.com/group/sub/proj.git")!;
        Assert.Equal("https://gitlab.com/group/sub/proj/-/merge_requests/new?&merge_request%5Bsource_branch%5D=feat%2Fx%23y%26z%2B1",
            gl.NewPullRequestPage(null, "feat/x#y&z+1"));

        var server = RemoteUrl.Parse("ssh://git@git.corp.example:2222/g/r.git")!;
        Assert.Null(server.RepositoryPage(null)); // self-hosted without account = custom
        Assert.Equal("https://git.corp.example:8443/g/r", server.RepositoryPage(Server));
        Assert.Null(server.RepositoryPage(Server with { ServerUrl = "ftp://git.corp.example" })); // only http(s) pages
        Assert.Equal("https://git.corp.example:8443/g/r/-/merge_requests/new?&merge_request%5Bsource_branch%5D=main",
            server.NewPullRequestPage(Server, "main"));

        Assert.Null(RemoteUrl.Parse("https://github.com/onlyone")!.RepositoryPage(null)); // needs two segments
        Assert.Null(RemoteUrl.Parse("https://bitbucket.org/o/r")!.NewPullRequestPage(null, "b"));
    }

    [Theory]
    [InlineData("git@github.com:owner/repo.git", "https://github.com/owner/repo.git", "git@github.com:owner/repo.git")]
    [InlineData("https://bob:pw@github.com:443/owner/repo", "https://github.com/owner/repo.git", "git@github.com:owner/repo.git")]
    [InlineData("ssh://git@gitlab.com:2222/g/sub/p.git", "https://gitlab.com/g/sub/p.git", "git@gitlab.com:g/sub/p.git")]
    [InlineData("http://git.example.com:8080/team/app.git", "https://git.example.com:8080/team/app.git", "git@git.example.com:team/app.git")]
    [InlineData("git@git.example.com:team/app.git", "https://git.example.com/team/app.git", "git@git.example.com:team/app.git")]
    public void Clone_protocol_forms(string url, string https, string ssh)
    {
        var r = RemoteUrl.Parse(url)!;
        Assert.Equal(https, r.HttpsForm(null));
        Assert.Equal(ssh, r.SshForm(null));
    }

    [Fact]
    public void Clone_forms_use_the_account_server_for_GitLab_Server()
    {
        var r = RemoteUrl.Parse("git@git.corp.example:g/r.git")!;
        Assert.Equal("https://git.corp.example:8443/g/r.git", r.HttpsForm(Server));
        Assert.Null(RemoteUrl.Parse("https://dev.azure.com/org/proj/_git/repo")!.HttpsForm(null));
    }

    [Fact]
    public void Clone_account_selector_is_hidden_unless_https_with_two_accounts_of_the_host()
    {
        Account[] accounts = [GitHubA, GitHubB, GitLabC];
        Assert.Null(CloneAccounts.Choices("git@github.com:o/r.git", accounts)); // not https
        Assert.Null(CloneAccounts.Choices("https://gitlab.com/o/r.git", accounts)); // one account
        Assert.Null(CloneAccounts.Choices("https://example.com/o/r.git", accounts)); // none
        Assert.Null(CloneAccounts.Choices("not a url", accounts));
    }

    [Fact]
    public void Clone_account_selector_items_and_selection()
    {
        Account[] accounts = [GitHubA, GitLabC, GitHubB];

        var (items, selected) = CloneAccounts.Choices("https://github.com/o/r.git", accounts)!.Value;
        Assert.Equal(["default (alice)", "GitHub (alice)", "GitHub (Bob)"], items.Select(i => i.Text));
        Assert.Equal(0, selected);

        (items, selected) = CloneAccounts.Choices("https://BOB@github.com/o/r.git", accounts)!.Value;
        Assert.Equal(2, selected);

        (items, selected) = CloneAccounts.Choices("https://eve:pw@github.com/o/r.git", accounts)!.Value;
        Assert.Equal("custom (eve)", items[selected].Text);
        Assert.Equal(3, selected);
    }

    [Fact]
    public void Applying_a_choice_sets_or_removes_the_user_name_never_a_password()
    {
        Account[] accounts = [GitHubA, GitHubB];
        var url = "https://eve:pw@github.com/o/r.git";
        var (items, _) = CloneAccounts.Choices(url, accounts)!.Value;

        Assert.Equal("https://github.com/o/r.git", CloneAccounts.Apply(url, items[0]));
        Assert.Equal("https://alice@github.com/o/r.git", CloneAccounts.Apply(url, items[1]));
        Assert.Equal("https://Bob@github.com/o/r.git", CloneAccounts.Apply("https://github.com/o/r.git", items[2]));
        Assert.Equal("https://eve@github.com/o/r.git", CloneAccounts.Apply(url, items[3]));
    }

    [Fact]
    public void Selector_works_for_two_accounts_on_one_GitLab_server()
    {
        var other = Server with { Username = "erin" };
        var (items, _) = CloneAccounts.Choices("https://git.corp.example:8443/g/r.git", [Server, other])!.Value;
        Assert.Equal(["default (dave)", "GitLab Server (dave)", "GitLab Server (erin)"], items.Select(i => i.Text));
    }
}
