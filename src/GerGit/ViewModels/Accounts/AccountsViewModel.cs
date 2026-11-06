using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GerGit.Hosting;
using GerGit.ViewModels.Dialogs;

namespace GerGit.ViewModels.Accounts;

/// <summary>A row of the account list. A class: re-reading the list selects a new
/// instance, so the status call runs again even for an unchanged account.</summary>
public sealed class AccountItem(Account account)
{
    /// <summary>Replaced when the Enable Notifications check box changes the stored account.</summary>
    public Account Account { get; internal set; } = account;
    public HostType Type => Account.Type;

    /// <summary>The server URL for GitLab Server, else the type name.</summary>
    public string Name => Account.Type == HostType.GitLabServer ? Account.ServerUrl : HostTypes.DisplayName(Account.Type)!;

    public string Username => Account.Username;
}

/// <summary>
/// The Accounts window: account list with + / −,
/// details of the selected account with its online status. Opens with the first account selected.
/// The clone list lives in the Clone dialog.
/// </summary>
public sealed partial class AccountsViewModel : DialogViewModel
{
    readonly HostingService _hosting;
    CancellationTokenSource? _statusCts;

    public AccountsViewModel(HostingService hosting)
    {
        _hosting = hosting;
        Title = "Accounts";
        SubmitText = null;
        CancelText = "Close";
        Width = 700;
        Height = 500;
        CanResize = true;
        PlainFrame = true;
        CloseRequested += _ => _statusCts?.Cancel();
        Reload(null);
    }

    [ObservableProperty]
    public partial IReadOnlyList<AccountItem> Items { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(ServerTypeText), nameof(AuthTypeText))]
    public partial AccountItem? Selected { get; set; }

    public bool HasSelection => Selected is not null;
    public string ServerTypeText => Selected is null ? "" : HostTypes.DisplayName(Selected.Type)!;
    public string AuthTypeText => Selected?.Account.AuthType == AuthType.OAuth ? "OAuth" : "Access Token";

    /// <summary>Title of the empty details pane.</summary>
    public const string EmptyTitle = "No Accounts";
    public const string EmptyText = "Use + to log in to GitHub or GitLab.";

    // Account tab "Status:" line: spinner + `Updating...`, then a green dot + `Online` or a red dot + the error.
    [ObservableProperty]
    public partial bool IsUpdating { get; private set; }

    [ObservableProperty]
    public partial bool IsOnline { get; private set; }

    [ObservableProperty]
    public partial bool IsOffline { get; private set; }

    [ObservableProperty]
    public partial string AccountStatusText { get; private set; } = "";

    /// <summary>The latest status call (tests await it).</summary>
    public Task StatusTask { get; private set; } = Task.CompletedTask;

    partial void OnSelectedChanged(AccountItem? value)
    {
        NotificationsEnabled = value?.Account.EnableNotifications ?? false;
        StatusTask = RefreshStatusAsync(value);
    }

    /// <summary>Account tab `Enable Notifications`: stored and saved, then an immediate notification refresh.
    /// Shown for every account type GerGit has (GitLab to-dos are an extra).</summary>
    [ObservableProperty]
    public partial bool NotificationsEnabled { get; set; }

    partial void OnNotificationsEnabledChanged(bool value)
    {
        if (Selected is not { } item || item.Account.EnableNotifications == value) return;
        item.Account = item.Account with { EnableNotifications = value };
        _hosting.Store.Change(item.Account, a => a with { EnableNotifications = value });
        _ = _hosting.Notifications.RefreshAsync();
    }

    /// <summary>The user-info call runs on every selection, off the UI thread; a new selection
    /// or closing the window cancels the running one.</summary>
    async Task RefreshStatusAsync(AccountItem? item)
    {
        _statusCts?.Cancel();
        var cts = _statusCts = new CancellationTokenSource();
        IsOnline = IsOffline = false;
        IsUpdating = item is not null;
        AccountStatusText = item is null ? "" : "Updating...";
        if (item is null) return;
        string text;
        bool ok = false;
        try
        {
            var client = await _hosting.ClientAsync(item.Account);
            await client.GetUserAsync(cts.Token);
            (ok, text) = (true, "Online");
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { return; }
        catch (HostingException e) { text = e.Message; } // friendly text; never holds the token
        catch (Exception e)
        {
            Trace.WriteLine($"Account status of {item.Username}@{item.Account.ServerUrl}: {e.GetType().Name}: {e.Message}");
            text = "Error: " + e.Message;
        }
        if (cts.IsCancellationRequested) return;
        IsUpdating = false;
        IsOnline = ok;
        IsOffline = !ok;
        AccountStatusText = text;
    }

    /// <summary>Re-reads the list and selects <paramref name="select"/> (by identity), else the first account.</summary>
    void Reload(Account? select)
    {
        Items = [.. _hosting.Accounts.Select(a => new AccountItem(a))];
        Selected = (select is null ? null : Items.FirstOrDefault(i => i.Account.SameIdentity(select))) ?? Items.FirstOrDefault();
    }

    /// <summary>`+`: Add Account; after a login the list reloads with the new (last) account selected.</summary>
    [RelayCommand]
    public async Task AddAsync()
    {
        if (!await _hosting.ShowDialog(new AddAccountViewModel(_hosting))) return;
        Reload(_hosting.Accounts.LastOrDefault());
        _ = _hosting.Notifications.RefreshAsync(); // a new login is polled at once
    }

    /// <summary>`Update token...`: the login window of the account's type, pre-filled; the result is re-selected.</summary>
    [RelayCommand]
    public async Task UpdateTokenAsync()
    {
        if (Selected is not { } item) return;
        var login = new LoginViewModel(_hosting, item.Type, item.Account);
        if (!await _hosting.ShowDialog(login)) return;
        Reload(login.Account);
        _ = _hosting.Notifications.RefreshAsync();
    }

    /// <summary>`−`: log out confirmation, then secrets deleted, account removed, first account selected.</summary>
    [RelayCommand]
    public async Task LogOutAsync()
    {
        if (Selected is not { } item) return;
        var account = item.Account;
        var confirm = new DialogViewModel
        {
            Title = $"Log out of {account.ServerUrl}?",
            Description = "You can always log back in at any time",
            SubmitText = "Log out",
            Width = 500,
        };
        if (!await _hosting.ShowDialog(confirm)) return;
        await Task.Run(() =>
        {
            _hosting.Secrets.Clear(account.ServerUrl, account.Username);
            _hosting.Store.Remove(account);
        });
        Reload(null);
        _ = _hosting.Notifications.RefreshAsync(); // its rows and badge go away
    }

    /// <summary>Header hyperlink: the server URL in the browser.</summary>
    [RelayCommand]
    public void OpenServer()
    {
        if (Selected is { } item) _hosting.OpenBrowser(item.Account.ServerUrl);
    }
}

/// <summary>A tile of the Add Account window.</summary>
public sealed record AccountTile(HostType Type)
{
    public string Name => HostTypes.DisplayName(Type)!;
}

/// <summary>
/// Add Account: wrapping tiles, `Log in` enabled with a selected tile (double-click =
/// Log in); opens the login window and closes with OK when the login succeeded.
/// </summary>
public sealed partial class AddAccountViewModel : DialogViewModel
{
    readonly HostingService _hosting;

    public AddAccountViewModel(HostingService hosting)
    {
        _hosting = hosting;
        Title = "Add Account";
        SubmitText = "Log in";
        Width = 450;
        Height = 350;
        PlainFrame = true;
        CanSubmit = false;
    }

    /// <summary>Only the supported hosting services have tiles.</summary>
    public IReadOnlyList<AccountTile> Tiles { get; } = [new(HostType.GitHub), new(HostType.GitLab), new(HostType.GitLabServer)];

    [ObservableProperty]
    public partial AccountTile? SelectedTile { get; set; }

    partial void OnSelectedTileChanged(AccountTile? value) => CanSubmit = value is not null;

    /// <summary>The account the login created.</summary>
    public Account? Account { get; private set; }

    protected override async Task OnSubmitAsync()
    {
        var login = new LoginViewModel(_hosting, SelectedTile!.Type);
        if (!await _hosting.ShowDialog(login)) return;
        Account = login.Account;
        Close(true);
    }
}
