using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GerGit.Hosting;
using GerGit.ViewModels.Dialogs;

namespace GerGit.ViewModels.Accounts;

/// <summary>
/// Login window of GitHub, GitLab and GitLab Server . Validates the token with the user-info call, stores it in the keyring and adds the
/// account (identity replace). With <c>existing</c> it is the `Update token...` window of that account.
/// GitHub OAuth is the device flow: Sign In requests a code, shows it and opens the verification page,
/// then polls. No OAuth `Username:` field: the device flow has no login hint.
/// While a login runs the inputs are disabled, but not the code panel (the user copies the code); Cancel closes the
/// window and stops the wait.
/// </summary>
public sealed partial class LoginViewModel : DialogViewModel
{
    /// <summary>Shown instead of the OAuth fields when no client id is built in.</summary>
    public const string OAuthNotConfigured = "OAuth login is not configured in this build. Please use a Personal Access Token.";

    /// <summary>Shown when the configured CA file does not exist.</summary>
    public const string CaFileMissing = "CA file not found";

    /// <summary>Shown under a GitLab Server URL that starts with `http://`.</summary>
    public const string PlainHttpWarning = "Plain http: the token is sent unencrypted.";

    readonly HostingService _hosting;
    readonly Account? _existing;
    CancellationTokenSource? _cts;

    public LoginViewModel(HostingService hosting, HostType type, Account? existing = null)
    {
        _hosting = hosting;
        _existing = existing;
        Type = type;
        Title = type == HostType.GitHub ? "Login to GitHub" : "Login to GitLab";
        Header = type == HostType.GitHub ? "Log in to GitHub" : "Log in to GitLab";
        SubmitText = "Sign In";
        Width = 450;
        PlainFrame = true;
        Server = existing?.ServerUrl ?? Account.GitLabServer;
        CaFile = existing?.CaFile ?? "";
        MethodIndex = existing is { AuthType: AuthType.AccessToken } ? 1 : 0;
        CloseRequested += _ => _cts?.Cancel(); // Cancel / Esc / the close button stop a running login
        Validate();
    }

    public HostType Type { get; }
    public string Header { get; }
    public bool IsGitHub => Type == HostType.GitHub;
    public bool ShowServer => Type == HostType.GitLabServer;

    public IReadOnlyList<string> Methods { get; } = ["Log in on GitHub.com (OAuth)", "Personal Access Token"];

    /// <summary>GitHub combo: 0 = OAuth (default for new accounts), 1 = Personal Access Token.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOAuth), nameof(ShowToken), nameof(ShowOAuthNotConfigured))]
    public partial int MethodIndex { get; set; }

    public bool IsOAuth => IsGitHub && MethodIndex == 0;
    public bool ShowToken => !IsOAuth;
    public bool OAuthConfigured => _hosting.GitHubClientId.Length > 0;
    public bool ShowOAuthNotConfigured => IsOAuth && !OAuthConfigured;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowHttpWarning))]
    public partial string Server { get; set; } = "";

    /// <summary>The server URL uses plain http, so the token travels in clear text; allowed, but warned about.</summary>
    public bool ShowHttpWarning => ShowServer && Uri.TryCreate(NormalizedServer, UriKind.Absolute, out var u) && u.Scheme == "http";

    /// <summary>GitLab Server: optional extra CA for the API calls; must exist when set.</summary>
    [ObservableProperty]
    public partial string CaFile { get; set; } = "";

    [ObservableProperty]
    public partial string Token { get; set; } = "";

    public string TokenLabel => IsGitHub ? "Access Token:" : "Personal Access Token:";
    public string TokenPlaceholder => IsGitHub ? "Token" : "Token with 'api write_repository' scopes";
    public string? TokenLinkTip => IsGitHub ? null : "Required scopes: read_user, read_api, read_repository, write_repository";

    /// <summary>The `Manage personal access tokens` hint: disabled until the server URL is valid.</summary>
    [ObservableProperty]
    public partial bool HintEnabled { get; private set; }

    /// <summary>A login runs: the inputs are disabled.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditable))]
    public partial bool IsRunning { get; private set; }

    public bool IsEditable => !IsRunning;

    /// <summary>Device flow: the code the user enters on GitHub; null = no code panel.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUserCode))]
    public partial string? UserCode { get; private set; }

    public bool HasUserCode => UserCode is not null;

    [ObservableProperty]
    public partial string? VerificationUri { get; private set; }

    /// <summary>The account created or updated by a successful login.</summary>
    public Account? Account { get; private set; }

    /// <summary>The server URL as stored: fixed for GitHub and GitLab, trimmed / lower-case for GitLab Server.</summary>
    public string NormalizedServer => Type switch
    {
        HostType.GitHub => Account.GitHubServer,
        HostType.GitLabServer => Account.NormalizeServerUrl(Server),
        _ => Account.GitLabServer,
    };

    bool ServerValid => Uri.TryCreate(NormalizedServer, UriKind.Absolute, out var u) && u.Scheme is "http" or "https";

    partial void OnMethodIndexChanged(int value) => Validate();
    partial void OnServerChanged(string value) => Validate();
    partial void OnCaFileChanged(string value) => Validate();
    partial void OnTokenChanged(string value) => Validate();

    void Validate()
    {
        if (IsRunning) return;
        HintEnabled = !ShowServer || ServerValid;
        if (IsOAuth)
        {
            SetValid(OAuthConfigured);
            return;
        }
        bool valid = Token.Trim().Length > 0 && HintEnabled;
        string? warning = null;
        if (ShowServer && CaFile.Trim() is { Length: > 0 } ca && !File.Exists(ca))
            (valid, warning) = (false, CaFileMissing);
        SetValid(valid, warning);
    }

    /// <summary>The hint's link: the service's new-token page.</summary>
    [RelayCommand]
    public void OpenTokenPage() =>
        _hosting.OpenBrowser(IsGitHub ? GitHubClient.TokenPageUrl : GitLabClient.TokenPageUrl(NormalizedServer));

    /// <summary>Device flow: opens the verification page again.</summary>
    [RelayCommand]
    public void OpenVerificationPage()
    {
        if (VerificationUri is { } uri) _hosting.OpenBrowser(uri);
    }

    protected override async Task OnSubmitAsync()
    {
        var server = NormalizedServer;
        var type = IsGitHub ? HostType.GitHub : Account.GitLabTypeFor(server);
        var ca = type == HostType.GitLabServer && CaFile.Trim() is { Length: > 0 } path ? path : null;
        var auth = IsOAuth ? AuthType.OAuth : AuthType.AccessToken;
        using var cts = _cts = new CancellationTokenSource();
        IsRunning = true;
        CanSubmit = false;
        SetStatus(DialogStatus.InProgress, $"Log in to {server}...");
        try
        {
            var token = IsOAuth ? await DeviceFlowAsync(cts.Token) : Token.Trim();
            if (IsOAuth) SetStatus(DialogStatus.InProgress, $"Log in to {server}...");
            var user = await _hosting.Client(type, server, token, ca).GetUserAsync(cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            var account = new Account(type, auth, server, user.Username, user.AvatarUrl, ca,
                EnableNotifications: type == HostType.GitHub); // True for new GitHub accounts
            // Busy while the keyring and the list are written: Cancel / close wait for the save, so a
            // finished save never ends as a cancelled window with a stale account list.
            IsBusy = true;
            try { account = await Task.Run(() => Save(account, token)); }
            finally { IsBusy = false; }
            Account = account;
            IsRunning = false;
            SetStatus(DialogStatus.None);
            Close(true);
        }
        catch (OperationCanceledException)
        {
            IsRunning = false;
            Close(false); // Cancel stops the wait and closes the window
        }
        catch (HostingException e)
        {
            IsRunning = false;
            Validate();
            SetStatus(DialogStatus.Error, e.Message); // friendly text, never the token
        }
        finally
        {
            _cts = null;
        }
    }

    async Task<string> DeviceFlowAsync(CancellationToken ct)
    {
        var flow = _hosting.DeviceFlow();
        var code = await flow.StartAsync(ct);
        (UserCode, VerificationUri) = (code.UserCode, code.VerificationUri);
        try
        {
            _hosting.OpenBrowser(code.VerificationUri);
            SetStatus(DialogStatus.InProgress, $"Waiting for response from {Account.GitHubServer}...");
            return await flow.PollAsync(code, ct);
        }
        finally
        {
            (UserCode, VerificationUri) = (null, null);
        }
    }

    /// <summary>
    /// Keyring first (a failure is `Cannot save authentication` and nothing changes, the old secret stays), then the
    /// account list. An updated account is logged out only after the validation succeeded. Same identity:
    /// the store replaces the item with the same attributes. Another identity: the new account is added before the old
    /// one is removed (no gap without accounts, one list change after the other).
    /// Limitation: same identity with another token kind (OAuth &lt;-&gt; PAT) leaves the old kind's secret in the keyring
    /// (unused; removed on log out): ISecretStore can only clear all kinds of an account.
    /// </summary>
    Account Save(Account account, string token)
    {
        // A replaced account keeps its notification switch and time.
        if (_hosting.Accounts.FirstOrDefault(a => a.SameIdentity(account)) is { } same)
            account = account with { EnableNotifications = same.EnableNotifications, NotificationsUpdatedAt = same.NotificationsUpdatedAt };
        var old = _existing ?? _hosting.Accounts.FirstOrDefault(a => a.SameIdentity(account));
        _hosting.Secrets.Store(account.ServerUrl, account.Username, account.SecretKind, token);
        _hosting.Store.Add(account);
        if (old is not null && !old.SameIdentity(account))
        {
            _hosting.Secrets.Clear(old.ServerUrl, old.Username);
            _hosting.Store.Remove(old);
        }
        return account;
    }
}
