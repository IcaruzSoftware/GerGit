using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GerGit.Controls;
using GerGit.Core;
using GerGit.Core.Git;
using GerGit.Hosting;
using GerGit.Tests.Hosting;
using GerGit.ViewModels;
using GerGit.ViewModels.Accounts;
using GerGit.ViewModels.Dialogs;
using GerGit.Views.Dialogs;

namespace GerGit.Tests.Ui;

/// <summary>Accounts window, Add Account, login windows (token + device flow) and the app-level hooks,
/// against a fake GitHub / GitLab (local listener) and an in-memory keyring. In the Askpass collection because some
/// tests set the process-wide git hooks (and capture Trace).</summary>
[Collection("Askpass")]
public class AccountsUiTests
{
    const string Token = "ghp_secret-7f3e9a1b", Token2 = "ghp_other-55aa01", BadToken = "ghp_wrong-0c0ffee";

    /// <summary>Own service per test: fake server as GitHub API/web base and GitLab server, fake keyring, temp
    /// accounts.json. <see cref="Fill"/> drives every dialog (fill and submit, or leave it to cancel).</summary>
    sealed class Fx : IDisposable
    {
        public readonly FakeHostingServer Server;
        public readonly FakeSecretStore Secrets = new();
        public readonly string Dir = Directory.CreateTempSubdirectory("gergit-accounts-").FullName;
        public readonly HostingService Hosting;
        public readonly List<string> Opened = [];
        public readonly List<DialogViewModel> Dialogs = [];
        public Func<DialogViewModel, Task>? Fill;

        public Fx(string clientId = "", FakeHostingServer? server = null)
        {
            Server = server ?? new FakeHostingServer();
            Hosting = new HostingService(new AccountStore(Path.Combine(Dir, "accounts.json")), Secrets)
            {
                GitHubApiBase = Server.BaseUri,
                GitHubWebBase = Server.BaseUri,
                GitHubClientId = clientId,
            };
            Hosting.ShowDialog = async vm =>
            {
                Dialogs.Add(vm);
                if (Fill is not null) await Fill(vm);
                return vm.Result == true;
            };
            Hosting.OpenBrowser = url => { lock (Opened) Opened.Add(url); };
        }

        public string AccountsJson => File.ReadAllText(Hosting.Store.FilePath);

        /// <summary>GitHub `GET /user`: token → login; anything else 401 with GitHub's JSON error.</summary>
        public void GitHubUsers(params (string Token, string Login)[] users) => Server.Map("GET", "/user", r =>
            users.FirstOrDefault(u => r.Header("Authorization") == "token " + u.Token) is { Login: { } login }
                ? FakeResponse.Json(new { login, name = "The " + login, avatar_url = $"https://avatars.example/{login}", html_url = $"https://github.com/{login}" })
                : FakeResponse.Json(new { message = "Bad credentials" }, 401));

        /// <summary>GitLab `GET /api/v4/user` with `Private-Token`; anything else a plain 401.</summary>
        public void GitLabUser(string token, string username) => Server.Map("GET", "/api/v4/user", r =>
            r.Header("Private-Token") == token
                ? FakeResponse.Json(new { username, name = "GL " + username, avatar_url = (string?)null, web_url = $"{Server.Url}/{username}" })
                : new FakeResponse(401, "denied", "text/plain"));

        public Account AddGitHub(string login, string token, AuthType auth = AuthType.AccessToken)
        {
            var account = new Account(HostType.GitHub, auth, Account.GitHubServer, login, EnableNotifications: true);
            Secrets.Store(account.ServerUrl, login, account.SecretKind, token);
            Hosting.Store.Add(account);
            return account;
        }

        public void Dispose()
        {
            Server.Dispose();
            try { Directory.Delete(Dir, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>Fill for Add Account → the tile → the login window with a token.</summary>
    static Func<DialogViewModel, Task> AddWithToken(HostType type, string token, string? server = null) => async vm =>
    {
        switch (vm)
        {
            case AddAccountViewModel add:
                add.SelectedTile = add.Tiles.Single(t => t.Type == type);
                await add.SubmitAsync();
                break;
            case LoginViewModel login:
                login.MethodIndex = 1;
                if (server is not null) login.Server = server;
                login.Token = token;
                await login.SubmitAsync();
                break;
        }
    };

    static async Task WaitFor(Func<bool> condition)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException();
            await Task.Delay(10);
        }
    }

    // ---------------------------------------------------------------- Accounts window + Add Account + token login

    [Fact]
    public async Task Add_GitHub_token_account_selects_it_online_with_the_token_only_in_the_keyring()
    {
        using var f = new Fx();
        f.GitHubUsers((Token, "octo"));
        var accounts = new AccountsViewModel(f.Hosting);
        Assert.Empty(accounts.Items);
        Assert.False(accounts.HasSelection);
        Assert.Equal(("Accounts", "Close", null, 700d, 500d, true), (accounts.Title, accounts.CancelText, accounts.SubmitText, accounts.Width, accounts.Height, accounts.CanResize));

        f.Fill = AddWithToken(HostType.GitHub, Token);
        await accounts.AddAsync();

        var add = Assert.IsType<AddAccountViewModel>(f.Dialogs[0]);
        Assert.Equal(["GitHub", "GitLab", "GitLab Server"], add.Tiles.Select(t => t.Name));
        Assert.True(add.Result);
        var login = Assert.IsType<LoginViewModel>(f.Dialogs[1]);
        Assert.Equal(("Login to GitHub", "Log in to GitHub", "Sign In"), (login.Title, login.Header, login.SubmitText));

        var account = Assert.Single(f.Hosting.Accounts);
        Assert.Equal(new Account(HostType.GitHub, AuthType.AccessToken, "https://github.com", "octo", "https://avatars.example/octo",
            EnableNotifications: true), account);
        Assert.Same(accounts.Items[^1], accounts.Selected);
        Assert.Equal(("GitHub", "Access Token", "octo"), (accounts.ServerTypeText, accounts.AuthTypeText, accounts.Selected!.Username));
        await accounts.StatusTask;
        Assert.Equal(("Online", true, false, false), (accounts.AccountStatusText, accounts.IsOnline, accounts.IsOffline, accounts.IsUpdating));

        Assert.Equal(Token, f.Secrets.Items[("https://github.com", "octo", SecretKind.AccessToken)]);
        Assert.Single(f.Secrets.Items);
        Assert.Contains("\"octo\"", f.AccountsJson);
        Assert.DoesNotContain(Token, f.AccountsJson);
        Assert.Contains("\"enableNotifications\": true", f.AccountsJson);

        accounts.OpenServer();
        Assert.Equal(["https://github.com/"], f.Opened); // the checked form (OpenBrowser passes Uri.AbsoluteUri)
    }

    [Fact]
    public async Task Add_Account_needs_a_tile_and_cancelled_login_keeps_it_open()
    {
        using var f = new Fx();
        var add = new AddAccountViewModel(f.Hosting);
        Assert.False(add.SubmitEnabled);
        Assert.Equal(("Add Account", "Log in", 450d, 350d), (add.Title, add.SubmitText, add.Width, add.Height));
        add.SelectedTile = add.Tiles[2];
        Assert.True(add.SubmitEnabled);
        await add.SubmitAsync(); // the login window is cancelled (no Fill)
        Assert.Null(add.Result);
        var login = Assert.IsType<LoginViewModel>(Assert.Single(f.Dialogs));
        Assert.Equal(HostType.GitLabServer, login.Type);
    }

    [Fact]
    public async Task GitLab_Server_login_normalizes_the_server_and_validates_the_CA_file_row()
    {
        using var f = new Fx();
        f.GitLabUser(Token, "gl-user");
        var login = new LoginViewModel(f.Hosting, HostType.GitLabServer);
        Assert.Equal(("Login to GitLab", "Log in to GitLab", "https://gitlab.com"), (login.Title, login.Header, login.Server));
        Assert.Equal(("Personal Access Token:", "Token with 'api write_repository' scopes"), (login.TokenLabel, login.TokenPlaceholder));
        Assert.Equal("Required scopes: read_user, read_api, read_repository, write_repository", login.TokenLinkTip);
        Assert.True(login.ShowServer);
        Assert.False(login.IsGitHub);
        Assert.False(login.SubmitEnabled); // no token yet

        login.Token = Token;
        login.Server = "not a url";
        Assert.False(login.HintEnabled);
        Assert.False(login.SubmitEnabled);

        login.Server = "  " + f.Server.Url.ToUpperInvariant() + "/ ";
        Assert.True(login.HintEnabled);
        Assert.True(login.SubmitEnabled);
        Assert.Equal(f.Server.Url, login.NormalizedServer);
        Assert.True(login.ShowHttpWarning); // the fake server is plain http

        login.CaFile = Path.Combine(f.Dir, "missing.pem");
        Assert.False(login.SubmitEnabled);
        Assert.Equal((DialogStatus.Warning, LoginViewModel.CaFileMissing), (login.Status, login.StatusText));
        var ca = TestCertificates.WriteCaFile(f.Dir);
        login.CaFile = ca;
        Assert.True(login.SubmitEnabled);
        Assert.Equal(DialogStatus.None, login.Status);

        login.OpenTokenPage();
        Assert.Equal([$"{f.Server.Url}/-/user_settings/personal_access_tokens?name=GerGit&scopes=api%2Cwrite_repository"], f.Opened);

        await login.SubmitAsync();
        Assert.True(login.Result);
        Assert.Equal(new Account(HostType.GitLabServer, AuthType.AccessToken, f.Server.Url, "gl-user", null, ca), login.Account);
        Assert.Equal(login.Account, Assert.Single(f.Hosting.Accounts));
        Assert.Equal(Token, f.Secrets.Items[(f.Server.Url, "gl-user", SecretKind.AccessToken)]);
        Assert.Contains("\"caFile\"", f.AccountsJson);
        Assert.Contains("\"enableNotifications\": false", f.AccountsJson);
    }

    [Fact]
    public async Task GitLab_Server_over_https_needs_the_CA_file()
    {
        using var f = new Fx(server: new FakeHostingServer(TestCertificates.Server));
        f.GitLabUser(Token, "tls-user");
        var login = new LoginViewModel(f.Hosting, HostType.GitLabServer) { Server = f.Server.Url, Token = Token };
        await login.SubmitAsync();
        Assert.Null(login.Result); // the test CA is not in the system store
        Assert.Equal(DialogStatus.Error, login.Status);
        Assert.StartsWith("Error: ", login.StatusText);

        login.CaFile = TestCertificates.WriteCaFile(f.Dir);
        await login.SubmitAsync();
        Assert.True(login.Result);
        Assert.Equal("tls-user", login.Account!.Username);
    }

    [Fact]
    public async Task Rejected_token_shows_the_error_and_leaks_the_token_nowhere()
    {
        using var f = new Fx();
        f.GitHubUsers((Token, "octo"));
        var trace = new StringWriter();
        var listener = new TextWriterTraceListener(trace);
        Trace.Listeners.Add(listener);
        try
        {
            var login = new LoginViewModel(f.Hosting, HostType.GitHub) { MethodIndex = 1, Token = BadToken };
            await login.SubmitAsync();
            Assert.Null(login.Result);
            Assert.False(login.IsRunning);
            Assert.True(login.SubmitEnabled); // controls enabled again
            Assert.Equal((DialogStatus.Error, "GitHub Error: Bad credentials"), (login.Status, login.StatusText));
            Assert.Empty(f.Hosting.Accounts);
            Assert.Empty(f.Secrets.Items);

            // The same in the Accounts window status line: a stored token the server no longer accepts.
            f.AddGitHub("octo", BadToken);
            var accounts = new AccountsViewModel(f.Hosting);
            await accounts.StatusTask;
            Assert.Equal(("GitHub Error: Bad credentials", false, true), (accounts.AccountStatusText, accounts.IsOnline, accounts.IsOffline));

            Trace.Flush();
            string[] texts = [login.StatusText, login.Title, login.Description, accounts.AccountStatusText, accounts.StatusText, trace.ToString()];
            Assert.All(texts, t => Assert.DoesNotContain(BadToken, t));
            Assert.Contains("/user", trace.ToString()); // the request was logged, without the token
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }
    }

    [Fact]
    public async Task Plain_401_and_missing_keyring_secret_show_friendly_texts()
    {
        using var f = new Fx();
        f.GitLabUser(Token, "gl");
        var login = new LoginViewModel(f.Hosting, HostType.GitLabServer) { Server = f.Server.Url, Token = BadToken };
        await login.SubmitAsync();
        Assert.Equal("HTTP Error 401 (Unauthorized)", login.StatusText);

        f.Hosting.Store.Add(new Account(HostType.GitHub, AuthType.AccessToken, Account.GitHubServer, "nosecret"));
        var accounts = new AccountsViewModel(f.Hosting);
        await accounts.StatusTask;
        Assert.Equal((SecretStoreException.ReadFailed, true), (accounts.AccountStatusText, accounts.IsOffline));
    }

    [Fact]
    public async Task Keyring_failure_is_Cannot_save_authentication_and_creates_no_account()
    {
        using var f = new Fx();
        f.GitHubUsers((Token, "octo"));
        f.Secrets.Fail = true;
        var login = new LoginViewModel(f.Hosting, HostType.GitHub) { MethodIndex = 1, Token = Token };
        await login.SubmitAsync();
        Assert.Null(login.Result);
        Assert.Equal((DialogStatus.Error, "Cannot save authentication"), (login.Status, login.StatusText));
        Assert.Empty(f.Hosting.Accounts);
        Assert.False(File.Exists(f.Hosting.Store.FilePath));
    }

    [Fact]
    public async Task Update_token_replaces_the_secret_or_the_account_and_reselects_it()
    {
        using var f = new Fx();
        f.GitHubUsers((Token2, "octo"), ("ghp_third", "renamed"));
        var other = f.AddGitHub("first", "t-first");
        f.AddGitHub("octo", Token, AuthType.OAuth);
        var accounts = new AccountsViewModel(f.Hosting);
        accounts.Selected = accounts.Items[1];

        // Same identity, OAuth → token: the new token is stored (the old OAuth item stays unused until log out).
        f.Fill = vm =>
        {
            var login = Assert.IsType<LoginViewModel>(vm);
            Assert.Equal(0, login.MethodIndex); // preselects the account's type
            login.MethodIndex = 1;
            login.Token = Token2;
            return login.SubmitAsync();
        };
        await accounts.UpdateTokenAsync();
        Assert.Equal(["first", "octo"], f.Hosting.Accounts.Select(a => a.Username));
        Assert.Equal(AuthType.AccessToken, f.Hosting.Accounts[1].AuthType);
        Assert.Equal(Token2, f.Secrets.Items[("https://github.com", "octo", SecretKind.AccessToken)]);
        Assert.Equal("octo", accounts.Selected!.Username);

        // Another user behind the new token: the old account is logged out after the validation.
        f.Fill = vm =>
        {
            var login = Assert.IsType<LoginViewModel>(vm);
            Assert.Equal(1, login.MethodIndex);
            login.Token = "ghp_third";
            return login.SubmitAsync();
        };
        await accounts.UpdateTokenAsync();
        Assert.Equal(["first", "renamed"], f.Hosting.Accounts.Select(a => a.Username));
        Assert.DoesNotContain(f.Secrets.Items.Keys, k => k.User == "octo");
        Assert.Equal("renamed", accounts.Selected!.Username);
        Assert.Equal(other, f.Hosting.Accounts[0]);
    }

    [Fact]
    public async Task Same_identity_update_with_a_failing_keyring_keeps_the_old_secret()
    {
        using var f = new Fx();
        f.GitHubUsers((Token2, "octo"));
        var old = f.AddGitHub("octo", Token);
        var login = new LoginViewModel(f.Hosting, HostType.GitHub, old) { Token = Token2 };
        f.Secrets.Fail = true;
        await login.SubmitAsync();
        f.Secrets.Fail = false;
        Assert.Null(login.Result);
        Assert.Equal((DialogStatus.Error, SecretStoreException.SaveFailed), (login.Status, login.StatusText));
        Assert.Equal(Token, f.Secrets.Items[("https://github.com", "octo", SecretKind.AccessToken)]);
        Assert.Equal(old, Assert.Single(f.Hosting.Accounts));
    }

    /// <summary>Lets a test hold the keyring save.</summary>
    sealed class GatedSecretStore(ISecretStore inner) : ISecretStore
    {
        public readonly ManualResetEventSlim Entered = new(), Release = new();

        public void Store(string server, string user, SecretKind kind, string secret)
        {
            Entered.Set();
            Release.Wait(TimeSpan.FromSeconds(20));
            inner.Store(server, user, kind, secret);
        }

        public string? Lookup(string server, string user, SecretKind kind) => inner.Lookup(server, user, kind);
        public void Clear(string server, string user) => inner.Clear(server, user);
    }

    [Fact]
    public async Task Cancel_during_the_keyring_save_waits_for_it_and_closes_with_the_account()
    {
        using var f = new Fx();
        f.GitHubUsers((Token, "octo"));
        var gate = new GatedSecretStore(f.Secrets);
        var hosting = new HostingService(f.Hosting.Store, gate) { GitHubApiBase = f.Server.BaseUri };
        var login = new LoginViewModel(hosting, HostType.GitHub) { MethodIndex = 1, Token = Token };
        var running = login.SubmitAsync();
        await WaitFor(() => gate.Entered.IsSet);
        Assert.True(login.IsBusy);
        login.Cancel(); // ignored while the save runs
        Assert.Null(login.Result);
        gate.Release.Set();
        await running.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(login.Result);
        Assert.Equal("octo", Assert.Single(f.Hosting.Accounts).Username);
        Assert.Equal(Token, f.Secrets.Items[("https://github.com", "octo", SecretKind.AccessToken)]);
    }

    [Fact]
    public async Task Log_out_asks_then_clears_the_secrets_and_selects_the_first_account()
    {
        using var f = new Fx();
        f.GitHubUsers();
        f.AddGitHub("one", "t1");
        f.AddGitHub("two", "t2");
        var accounts = new AccountsViewModel(f.Hosting);
        Assert.Equal("one", accounts.Selected!.Username); // opens with the first account
        accounts.Selected = accounts.Items[1];

        await accounts.LogOutAsync(); // no Fill: cancelled
        var confirm = Assert.Single(f.Dialogs);
        Assert.Equal(("Log out of https://github.com?", "You can always log back in at any time", "Log out", 500d),
            (confirm.Title, confirm.Description, confirm.SubmitText, confirm.Width));
        Assert.Equal(2, f.Hosting.Accounts.Count);

        f.Fill = vm => vm.SubmitAsync();
        await accounts.LogOutAsync();
        Assert.Equal(["one"], f.Hosting.Accounts.Select(a => a.Username));
        Assert.DoesNotContain(f.Secrets.Items.Keys, k => k.User == "two");
        Assert.Equal("one", accounts.Selected!.Username);
        Assert.DoesNotContain("\"two\"", f.AccountsJson);

        accounts.Selected = accounts.Items[0];
        await accounts.LogOutAsync();
        Assert.Empty(accounts.Items);
        Assert.False(accounts.HasSelection);
        Assert.Empty(f.Secrets.Items);
    }

    // ---------------------------------------------------------------- GitHub OAuth device flow

    static void MapDeviceCode(Fx f, int interval = 0) => f.Server.Map("POST", "/login/device/code", FakeResponse.Json(new
    {
        device_code = "dev-1", user_code = "ABCD-1234", verification_uri = f.Server.Url + "/login/device", expires_in = 900, interval,
    }));

    [Fact]
    public async Task Device_flow_shows_the_code_opens_the_page_and_saves_an_OAuth_account()
    {
        using var f = new Fx(clientId: "Iv1.test");
        f.GitHubUsers(("gho_device", "octo"));
        MapDeviceCode(f);
        LoginViewModel? login = null;
        string? codeWhilePolling = null;
        int polls = 0;
        f.Server.Map("POST", "/login/oauth/access_token", _ =>
        {
            codeWhilePolling = login!.UserCode;
            return Interlocked.Increment(ref polls) < 2
                ? FakeResponse.Json(new { error = "authorization_pending" })
                : FakeResponse.Json(new { access_token = "gho_device", token_type = "bearer" });
        });
        login = new LoginViewModel(f.Hosting, HostType.GitHub);
        Assert.True(login.IsOAuth);
        Assert.False(login.ShowToken);
        Assert.False(login.ShowOAuthNotConfigured);
        Assert.True(login.SubmitEnabled);

        await login.SubmitAsync();
        Assert.True(login.Result);
        Assert.Equal("ABCD-1234", codeWhilePolling);
        Assert.Null(login.UserCode);
        Assert.Equal([f.Server.Url + "/login/device"], f.Opened);
        Assert.Equal(new Account(HostType.GitHub, AuthType.OAuth, Account.GitHubServer, "octo", "https://avatars.example/octo",
            EnableNotifications: true), Assert.Single(f.Hosting.Accounts));
        Assert.Equal("gho_device", f.Secrets.Items[("https://github.com", "octo", SecretKind.OAuthToken)]);
        Assert.Contains(f.Server.Requests, r => r.Path == "/login/device/code" && r.Body.Contains("client_id=Iv1.test"));
    }

    [Fact]
    public async Task Device_flow_denied_shows_the_error_and_keeps_the_window()
    {
        using var f = new Fx(clientId: "Iv1.test");
        MapDeviceCode(f);
        f.Server.Map("POST", "/login/oauth/access_token",
            FakeResponse.Json(new { error = "access_denied", error_description = "The user has denied your application access." }));
        var login = new LoginViewModel(f.Hosting, HostType.GitHub);
        await login.SubmitAsync();
        Assert.Null(login.Result);
        Assert.Equal((DialogStatus.Error, "The user has denied your application access."), (login.Status, login.StatusText));
        Assert.Null(login.UserCode);
        Assert.False(login.IsRunning);
        Assert.Empty(f.Hosting.Accounts);
    }

    [Fact]
    public async Task Device_flow_cancel_stops_polling_and_closes()
    {
        using var f = new Fx(clientId: "Iv1.test");
        MapDeviceCode(f);
        int polls = 0;
        f.Server.Map("POST", "/login/oauth/access_token", _ =>
        {
            Interlocked.Increment(ref polls);
            return FakeResponse.Json(new { error = "authorization_pending" });
        });
        var login = new LoginViewModel(f.Hosting, HostType.GitHub);
        var running = login.SubmitAsync();
        await WaitFor(() => Volatile.Read(ref polls) > 0);
        Assert.True(login.IsRunning);
        Assert.False(login.SubmitEnabled);
        Assert.Equal("ABCD-1234", login.UserCode);
        Assert.Equal("Waiting for response from https://github.com...", login.StatusText);

        login.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.False(login.Result);
        int after = Volatile.Read(ref polls);
        await Task.Delay(100);
        Assert.InRange(Volatile.Read(ref polls), after, after + 1); // at most the request that was in flight
        Assert.Empty(f.Hosting.Accounts);
        Assert.Empty(f.Secrets.Items);
    }

    [Fact]
    public void Without_a_client_id_OAuth_shows_the_hint_and_Sign_In_is_disabled()
    {
        using var f = new Fx(clientId: "");
        var login = new LoginViewModel(f.Hosting, HostType.GitHub);
        Assert.Equal(["Log in on GitHub.com (OAuth)", "Personal Access Token"], login.Methods);
        Assert.True(login.IsOAuth);
        Assert.True(login.ShowOAuthNotConfigured);
        Assert.False(login.SubmitEnabled);

        login.MethodIndex = 1; // the token entry works fully
        Assert.False(login.ShowOAuthNotConfigured);
        Assert.Equal(("Access Token:", "Token", null), (login.TokenLabel, login.TokenPlaceholder, login.TokenLinkTip));
        login.Token = "x";
        Assert.True(login.SubmitEnabled);
        login.OpenTokenPage();
        Assert.Equal([GitHubClient.TokenPageUrl], f.Opened);
    }

    // ---------------------------------------------------------------- process-wide hooks

    [Fact]
    public async Task Install_follows_the_account_count_and_answers_git_from_the_keyring()
    {
        var saved = (GitRunner.CredentialHelperEnabled, Askpass.LookupCredential, GitErrors.GitHubAccountCount, GitErrors.GitHubAccessUrl);
        try
        {
            using var f = new Fx(clientId: "Iv1.abc");
            f.Hosting.Install();
            Assert.False(GitRunner.CredentialHelperEnabled);
            Assert.Equal("https://github.com/settings/connections/applications/Iv1.abc", GitErrors.GitHubAccessUrl);

            var server = new Account(HostType.GitLabServer, AuthType.AccessToken, "http://127.0.0.1:8080", "alice");
            f.Secrets.Store(server.ServerUrl, "alice", SecretKind.AccessToken, Token);
            f.Hosting.Store.Add(server);
            Assert.True(GitRunner.CredentialHelperEnabled);
            Assert.Equal(0, GitErrors.GitHubAccountCount());

            async Task<string> Get(string request) => (await Askpass.AnswerAsync("credential", true, "", request)).Answer;
            Assert.Equal($"protocol=http\nhost=127.0.0.1:8080\nusername=alice\npassword={Token}\n\n",
                await Get("protocol=http\nhost=127.0.0.1:8080\n"));
            Assert.Equal($"protocol=http\nhost=127.0.0.1:8080\nusername=alice\npassword={Token}\n\n",
                await Get("protocol=http\nhost=127.0.0.1:8080\nusername=ALICE\n"));
            Assert.Equal("", await Get("protocol=http\nhost=127.0.0.1:8080\nusername=bob\n")); // URL user name of another account
            Assert.Equal("", await Get("protocol=http\nhost=127.0.0.1:8081\n"));   // other port
            Assert.Equal("", await Get("protocol=http\nhost=127.0.0.1\n"));        // default port 80
            Assert.Equal("", await Get("protocol=https\nhost=127.0.0.1:8080\n"));  // other scheme
            f.Secrets.Fail = true;
            Assert.Equal("", await Get("protocol=http\nhost=127.0.0.1:8080\n"));   // keyring failure: git goes on
            f.Secrets.Fail = false;

            f.AddGitHub("octo", Token2);
            Assert.Equal(1, GitErrors.GitHubAccountCount());

            f.Hosting.Store.Remove(server);
            Assert.True(GitRunner.CredentialHelperEnabled);
            f.Hosting.Store.Remove(f.Hosting.Accounts[0]);
            Assert.False(GitRunner.CredentialHelperEnabled);
        }
        finally
        {
            (GitRunner.CredentialHelperEnabled, Askpass.LookupCredential, GitErrors.GitHubAccountCount, GitErrors.GitHubAccessUrl) = saved;
        }
    }

    // ---------------------------------------------------------------- views (headless)

    static void Settle(params Window[] windows)
    {
        Dispatcher.UIThread.RunJobs();
        foreach (var w in windows) w.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    static T Find<T>(Window w, string name) where T : Control => w.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    [AvaloniaFact]
    public async Task Views_show_the_plain_frame_list_details_and_login_fields()
    {
        using var f = new Fx();
        f.GitHubUsers((Token, "octo"));
        f.AddGitHub("octo", Token);
        var owner = new Window { Width = 900, Height = 700 };
        owner.Show();

        var accounts = new AccountsViewModel(f.Hosting);
        await accounts.StatusTask;
        var shown = DialogWindow.ShowAsync(accounts, owner);
        Settle(owner);
        var d = (DialogWindow)owner.OwnedWindows.Single();
        Settle(d);
        Assert.Equal(("Accounts", 700d, 500d), (d.Title, d.Width, d.Height));
        Assert.False(Find<Panel>(d, "Logo").IsVisible);
        Assert.False(Find<StackPanel>(d, "Header").IsVisible);
        Assert.False(Find<Button>(d, "SubmitButton").IsVisible);
        Assert.Equal("Close", Find<Button>(d, "CancelButton").Content);
        Assert.Single(Find<ListBox>(d, "AccountList").GetRealizedContainers());
        Assert.Equal("octo", Find<TextBlock>(d, "HeaderUser").Text);
        Assert.Equal("https://github.com", Find<Button>(d, "ServerLink").Content);
        Assert.Equal(("GitHub", "Access Token", "Online"),
            (Find<TextBlock>(d, "ServerType").Text, Find<TextBlock>(d, "AuthType").Text, Find<TextBlock>(d, "StatusLine").Text));
        Assert.True(Find<Avalonia.Controls.Shapes.Ellipse>(d, "OnlineDot").IsVisible);
        Assert.False(Find<StackPanel>(d, "Empty").IsVisible);
        accounts.Cancel();
        Dispatcher.UIThread.RunJobs();
        Assert.False(await shown);

        var login = new LoginViewModel(f.Hosting, HostType.GitLabServer);
        shown = DialogWindow.ShowAsync(login, owner);
        Settle(owner);
        d = (DialogWindow)owner.OwnedWindows.Single();
        Settle(d);
        Assert.Equal(("Login to GitLab", "Log in to GitLab"), (d.Title, Find<TextBlock>(d, "Heading").Text));
        Assert.True(Find<TextBox>(d, "ServerBox").IsVisible);
        Assert.True(Find<TextBox>(d, "CaFileBox").IsVisible);
        Assert.False(Find<ComboBox>(d, "Method").IsVisible);
        Assert.Equal('•', Find<TextBox>(d, "TokenBox").PasswordChar);
        Assert.False(Find<Button>(d, "SubmitButton").IsEffectivelyEnabled);
        login.Server = "nope";
        Settle(d);
        Assert.False(Find<Button>(d, "TokenLink").IsEffectivelyEnabled);
        Assert.False(Find<StackPanel>(d, "HttpWarning").IsVisible);

        // Plain http: a visible warning, but the login stays possible (internal servers).
        login.Server = "HTTP://gitlab.internal";
        login.Token = Token;
        Settle(d);
        Assert.True(Find<StackPanel>(d, "HttpWarning").IsEffectivelyVisible);
        Assert.Contains(LoginViewModel.PlainHttpWarning, Find<StackPanel>(d, "HttpWarning").GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
        Assert.True(Find<Button>(d, "SubmitButton").IsEffectivelyEnabled);
        login.Server = "https://gitlab.internal";
        Settle(d);
        Assert.False(Find<StackPanel>(d, "HttpWarning").IsVisible);
        login.Cancel();
        Dispatcher.UIThread.RunJobs();
        Assert.False(await shown);
        owner.Close();
    }

    [AvaloniaFact]
    public async Task File_menu_Accounts_opens_the_Accounts_window_of_the_app_service()
    {
        using var f = new Fx();
        var saved = HostingService.Current;
        HostingService.Current = f.Hosting;
        var window = new GerGit.Views.MainWindow { DataContext = new MainWindowViewModel() };
        try
        {
            window.Show();
            window.StatusTimer.Stop();
            Settle(window);
            var file = window.GetVisualDescendants().OfType<Menu>().Single().Items.OfType<MenuItem>().Single(m => (string?)m.Header == "_File");
            // The last entries of the File menu, without SSH keys.
            Assert.Equal(["Close Tab", "-", "Accounts...", "-", "Preferences...", "-", "Exit"],
                file.Items.OfType<Control>().Select(c => c is MenuItem m ? (string)m.Header! : "-").TakeLast(7));
            file.Items.OfType<MenuItem>().Single(m => (string?)m.Header == "Accounts...").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
            await WaitFor(() => f.Dialogs.Count > 0);
            Assert.IsType<AccountsViewModel>(Assert.Single(f.Dialogs));
        }
        finally
        {
            HostingService.Current = saved;
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Brand_icons_are_filled_geometries_and_custom_hosts_use_the_server_glyph()
    {
        foreach (var type in new[] { HostType.GitHub, HostType.GitLab, HostType.Bitbucket })
        {
            Assert.True(Application.Current!.TryFindResource(HostIcon.ResourceKey(type), out var g));
            Assert.InRange(Assert.IsAssignableFrom<Geometry>(g).Bounds.Width, 20, 24.1);
        }
        Assert.Equal("Brand.GitLab", HostIcon.ResourceKey(HostType.GitLabServer));
        Assert.Equal("Icon.server", HostIcon.ResourceKey(HostType.Custom));
        Assert.Equal("Icon.server", HostIcon.ResourceKey(HostType.Azure));
    }
}
