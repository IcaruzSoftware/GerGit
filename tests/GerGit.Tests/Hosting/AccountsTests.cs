using System.Text.Json.Nodes;
using GerGit.Hosting;

namespace GerGit.Tests.Hosting;

/// <summary>Account record, token storage and HTTPS git operations with account credentials.</summary>
public sealed class AccountsTests : IDisposable
{
    const string Token = "glpat-SECRET-abcdef";

    readonly string _dir = Directory.CreateTempSubdirectory("gergit-accounts-").FullName;
    string FilePath => Path.Combine(_dir, "sub", "accounts.json");

    static readonly Account GitHub = new(HostType.GitHub, AuthType.OAuth, Account.GitHubServer, "alice",
        "https://avatars/alice", EnableNotifications: true);
    static readonly Account Corp = new(HostType.GitLabServer, AuthType.AccessToken, "https://git.corp.example:8443", "dave",
        CaFile: "/etc/corp-ca.pem");
    static readonly Account Corp2 = Corp with { Username = "erin" };

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Store_round_trip_keeps_order_and_fields()
    {
        var store = new AccountStore(FilePath);
        int changes = 0;
        store.Changed += () => changes++;
        store.Add(GitHub);
        store.Add(Corp);

        Assert.Equal(2, changes);
        Assert.Equal([GitHub, Corp], new AccountStore(FilePath).Accounts);
        Assert.False(File.Exists(FilePath + ".tmp"));

        var json = JsonNode.Parse(File.ReadAllText(FilePath))!.AsArray();
        Assert.Equal("GitLabServer", (string)json[1]!["type"]!);
        Assert.Equal("AccessToken", (string)json[1]!["authType"]!);
        Assert.False((bool)json[1]!["enableNotifications"]!); // written explicitly, also when false
        Assert.Null(json[0]!["host"]); // computed properties are not stored
    }

    [Fact]
    public void Same_identity_replaces_and_moves_to_the_end()
    {
        var store = new AccountStore(FilePath);
        store.Add(Corp);
        store.Add(GitHub);
        store.Add(Corp2);
        var updated = Corp with { Username = "DAVE", AvatarUrl = "new" }; // identity: server + username, case-insensitive
        store.Add(updated);

        Assert.Equal([GitHub, Corp2, updated], store.Accounts);
        store.Remove(Corp2);
        Assert.Equal([GitHub, updated], new AccountStore(FilePath).Accounts);
    }

    [Fact]
    public void Broken_or_unknown_entries_are_skipped()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, """
            [ {"type":"GitHub","serverUrl":"https://github.com","username":"a"},
              {"type":"Bitbucket","serverUrl":"https://bitbucket.org","username":"b"},
              {"type":"Nope","serverUrl":"x","username":"c"},
              {"type":"GitLab","serverUrl":"https://gitlab.com"} ]
            """);
        var account = Assert.Single(new AccountStore(FilePath).Accounts);
        Assert.Equal(AuthType.AccessToken, account.AuthType); // default when missing

        File.WriteAllText(FilePath, "{ not json");
        Assert.Empty(new AccountStore(FilePath).Accounts);
        Assert.Equal("{ not json", File.ReadAllText(FilePath + ".bak"));

        File.WriteAllText(FilePath, """{"accounts":[]}"""); // valid JSON, but not the array: kept like broken JSON
        Assert.Empty(new AccountStore(FilePath).Accounts);
        Assert.Equal("""{"accounts":[]}""", File.ReadAllText(FilePath + ".bak"));
    }

    [Fact]
    public void No_secret_in_the_file()
    {
        var secrets = new FakeSecretStore();
        secrets.Store(Corp.ServerUrl, Corp.Username, Corp.SecretKind, Token);
        new AccountStore(FilePath).Add(Corp);
        Assert.DoesNotContain(Token, File.ReadAllText(FilePath));
    }

    [Fact]
    public void Default_path_follows_the_settings_rule()
    {
        var dir = Environment.GetEnvironmentVariable("GERGIT_CONFIG_DIR"); // set for every test (TestSettings)
        Assert.Equal(Path.Combine(dir!, "accounts.json"), AccountStore.DefaultFilePath);
    }

    [Fact]
    public void Server_URL_rules()
    {
        Assert.Equal("https://git.corp.example", Account.NormalizeServerUrl(" HTTPS://Git.Corp.Example/ "));
        Assert.Equal(HostType.GitLab, Account.GitLabTypeFor("https://gitlab.com"));
        Assert.Equal(HostType.GitLabServer, Account.GitLabTypeFor("https://gitlab.corp"));
        Assert.Equal("git.corp.example", Corp.Host);
    }

    [Fact]
    public void Credential_lookup_compares_host_and_port()
    {
        var secrets = new FakeSecretStore();
        secrets.Store(Corp.ServerUrl, "dave", SecretKind.AccessToken, "t-dave");
        secrets.Store(Corp2.ServerUrl, "erin", SecretKind.AccessToken, "t-erin");
        secrets.Store(GitHub.ServerUrl, "alice", SecretKind.OAuthToken, "t-alice");
        Account[] accounts = [GitHub, Corp, Corp2];

        Assert.Equal(("dave", "t-dave"), Credentials.Lookup(accounts, secrets, "https", "git.corp.example:8443", null));
        Assert.Equal(("erin", "t-erin"), Credentials.Lookup(accounts, secrets, "https", "git.corp.example:8443", "ERIN"));
        Assert.Equal(("alice", "t-alice"), Credentials.Lookup(accounts, secrets, "https", "github.com", null)); // default port 443
        Assert.Equal(("alice", "t-alice"), Credentials.Lookup(accounts, secrets, "https", "github.com:443", "Alice"));

        Assert.Null(Credentials.Lookup(accounts, secrets, "https", "git.corp.example", null)); // wrong port (443)
        Assert.Null(Credentials.Lookup(accounts, secrets, "https", "git.corp.example:9443", null));
        Assert.Null(Credentials.Lookup(accounts, secrets, "http", "github.com:443", null)); // other scheme
        Assert.Null(Credentials.Lookup(accounts, secrets, "https", "github.com", "mallory")); // wrong user
        Assert.Null(Credentials.Lookup([], secrets, "https", "github.com", null)); // no account
        Assert.Null(Credentials.Lookup([GitLabCloud], secrets, "https", "gitlab.com", null)); // no token stored

        secrets.Fail = true;
        Assert.Null(Credentials.Lookup(accounts, secrets, "https", "github.com", null)); // keyring failure = not found
    }

    static readonly Account GitLabCloud = new(HostType.GitLab, AuthType.AccessToken, Account.GitLabServer, "carol");

    [Fact]
    public void TokenFor_reports_a_missing_token_with_the_keyring_text()
    {
        var secrets = new FakeSecretStore();
        var e = Assert.Throws<SecretStoreException>(() => secrets.TokenFor(GitLabCloud));
        Assert.Equal("Cannot read authorization from the system keyring", e.Message);
        Assert.IsAssignableFrom<HostingException>(e);

        secrets.Store(GitLabCloud.ServerUrl, "carol", SecretKind.AccessToken, Token);
        Assert.Equal(Token, secrets.TokenFor(GitLabCloud));
        Assert.Throws<SecretStoreException>(() => secrets.TokenFor(GitLabCloud with { AuthType = AuthType.OAuth })); // other kind
    }

    /// <summary>Real Secret Service round trip; runs only with GERGIT_KEYRING_TEST=1.</summary>
    [Fact]
    public void LibSecret_round_trip()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("GERGIT_KEYRING_TEST") == "1", "set GERGIT_KEYRING_TEST=1 to use the real keyring");
        var server = "https://gergit-test.invalid";
        var user = "test-" + Guid.NewGuid().ToString("N");
        var store = new LibSecretStore();
        try
        {
            store.Store(server, user, SecretKind.AccessToken, "access-1");
            store.Store(server, user, SecretKind.OAuthToken, "oauth-1");
            Assert.Equal("access-1", new LibSecretStore().Lookup(server, user, SecretKind.AccessToken)); // fresh cache
            Assert.Equal("oauth-1", new LibSecretStore().Lookup(server, user, SecretKind.OAuthToken));
            Assert.Null(new LibSecretStore().Lookup(server, user + "x", SecretKind.AccessToken));

            store.Clear(server, user);
            Assert.Null(store.Lookup(server, user, SecretKind.AccessToken));
            Assert.Null(new LibSecretStore().Lookup(server, user, SecretKind.OAuthToken));
        }
        finally
        {
            store.Clear(server, user);
        }
    }
}
