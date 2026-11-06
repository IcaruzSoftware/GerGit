using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GerGit.Hosting;
using GerGit.Tests.Hosting;
using GerGit.ViewModels;
using GerGit.ViewModels.Accounts;
using GerGit.Views;

namespace GerGit.Tests.Ui;

/// <summary>Notifications (bell, popup, polling, desktop notification, account check box) against a
/// fake GitHub + GitLab server and an in-memory keyring. In the Askpass collection because it captures Trace.</summary>
[Collection("Askpass")]
public sealed class NotificationsUiTests : IDisposable
{
    const string OctoToken = "ghp_octo-token-1a2b3c", MuteToken = "ghp_mute-token-4d5e6f", GitLabToken = "glpat-lab-token-7a8b9c";

    readonly FakeHostingServer _server = new();
    readonly FakeSecretStore _secrets = new();
    readonly string _dir = Directory.CreateTempSubdirectory("gergit-notifications-").FullName;
    readonly HostingService _hosting;
    readonly List<string> _opened = [];
    readonly List<(string Title, string Text)> _toasts = [];
    readonly List<TimeSpan> _timer = [];
    bool _clickToast;

    public NotificationsUiTests()
    {
        _hosting = new HostingService(new AccountStore(Path.Combine(_dir, "accounts.json")), _secrets) { GitHubApiBase = _server.BaseUri };
        _hosting.OpenBrowser = url => { lock (_opened) _opened.Add(url); };
        _hosting.ShowDialog = _ => Task.FromResult(false);
        N.RestartTimer = _timer.Add;
        N.ShowToast = (title, text) => { _toasts.Add((title, text)); return Task.FromResult(_clickToast); };
    }

    public void Dispose()
    {
        _server.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    NotificationsViewModel N => _hosting.Notifications;

    Account Add(HostType type, string user, string token, bool enabled)
    {
        var server = type == HostType.GitHub ? Account.GitHubServer : _server.Url;
        var account = new Account(type, AuthType.AccessToken, server, user, EnableNotifications: enabled);
        _secrets.Store(server, user, account.SecretKind, token);
        _hosting.Store.Add(account);
        return account;
    }

    static object GitHubItem(string id, int number, DateTimeOffset updated, bool unread = true, string url = "") => new
    {
        id,
        unread,
        updated_at = updated.ToString("o"),
        subject = new { title = "Issue " + number, url = url.Length > 0 ? url : $"https://api.github.com/repos/octo/repo/issues/{number}", type = "Issue" },
        repository = new { full_name = "octo/repo", owner = new { avatar_url = "https://avatars/octo" } },
    };

    void GitHubReturns(params object[] items) => _server.Map("GET", "/notifications", r =>
        r.Header("Authorization") == "token " + OctoToken ? FakeResponse.Json(items) : FakeResponse.Json(new { message = "Bad credentials" }, 401));

    void GitLabReturns(DateTimeOffset updated) => _server.Map("GET", "/api/v4/todos", r =>
        r.Header("Private-Token") == GitLabToken
            ? FakeResponse.Json(new object[]
            {
                new
                {
                    id = 7, state = "pending", target_type = "MergeRequest", updated_at = updated.ToString("o"),
                    target = new { iid = 3, title = "Review me" }, target_url = $"{_server.Url}/g/p/-/merge_requests/3",
                    project = new { path_with_namespace = "g/p" },
                },
            })
            : new FakeResponse(401, "denied", "text/plain"));

    [Fact]
    public async Task Refresh_polls_enabled_accounts_merges_newest_first_and_stores_the_time_quietly()
    {
        var now = DateTimeOffset.UtcNow;
        Add(HostType.GitHub, "octo", OctoToken, enabled: true);
        Add(HostType.GitHub, "mute", MuteToken, enabled: false);
        Add(HostType.GitLabServer, "lab", GitLabToken, enabled: true);
        GitHubReturns(GitHubItem("a", 1, now.AddHours(-2)), GitHubItem("b", 2, now.AddHours(-1), unread: false));
        GitLabReturns(now.AddMinutes(-90));
        int changed = 0;
        _hosting.Store.Changed += () => changed++;

        await N.RefreshAsync();

        Assert.Equal(["Issue 2", "Review me", "Issue 1"], N.Items.Select(r => r.Title));
        Assert.Equal([false, true, true], N.Items.Select(r => r.Unread));
        Assert.Equal(["#2", "#3", "#1"], N.Items.Select(r => r.TargetText));
        Assert.Equal("2", N.Badge);
        Assert.False(N.IsUpdating);
        Assert.DoesNotContain(_server.Requests, r => r.Header("Authorization") == "token " + MuteToken); // disabled: not polled
        Assert.Equal(2, _server.Requests.Count);
        Assert.Equal([NotificationsViewModel.Interval], _timer); // every refresh restarts the 15-minute timer
        Assert.Empty(_toasts); // the very first fetch has no "new" items
        Assert.Equal(0, changed); // the stored time does not refresh repository tabs
        var stored = new AccountStore(_hosting.Store.FilePath).Accounts;
        Assert.All(stored.Where(a => a.EnableNotifications), a => Assert.InRange(a.NotificationsUpdatedAt!.Value, now.AddMinutes(-1), DateTimeOffset.UtcNow));
        Assert.Null(stored.Single(a => a.Username == "mute").NotificationsUpdatedAt);
    }

    [Fact]
    public async Task Timer_tick_raises_one_desktop_notification_per_round_and_its_click_opens_the_item()
    {
        var now = DateTimeOffset.UtcNow;
        Add(HostType.GitHub, "octo", OctoToken, enabled: true);
        GitHubReturns(GitHubItem("a", 1, now.AddHours(-2)));
        await N.RefreshAsync(); // first fetch: stores the time, no toast

        GitHubReturns(GitHubItem("b", 5, now.AddHours(1)), GitHubItem("a", 1, now.AddHours(-2)));
        _clickToast = true;
        await N.RefreshAsync(); // the timer's tick
        await WaitFor(() => _opened.Count > 0);

        Assert.Equal(("octo/repo #5", "Issue 5"), Assert.Single(_toasts));
        Assert.Equal(["https://github.com/octo/repo/issues/5"], _opened);
        Assert.Equal([false, true], N.Items.Select(r => r.Unread)); // read locally
        Assert.Equal([NotificationsViewModel.Interval, NotificationsViewModel.Interval], _timer);

        // Several new ones: one summary; its click brings the window up and opens the popup.
        bool fronted = false;
        N.BringToFront = () => fronted = true;
        GitHubReturns(GitHubItem("c", 6, now.AddHours(2)), GitHubItem("d", 7, now.AddHours(3)));
        await N.RefreshAsync();
        await WaitFor(() => N.IsOpen);
        Assert.Equal(("New Notifications", "You've got 2 new notifications"), _toasts[^1]);
        Assert.True(fronted);
    }

    [Fact]
    public async Task A_server_error_stays_silent_and_no_token_reaches_the_log()
    {
        Add(HostType.GitHub, "octo", OctoToken, enabled: true);
        Add(HostType.GitLabServer, "lab", GitLabToken, enabled: true);
        _server.Map("GET", "/notifications", FakeResponse.Json(new { message = "token " + OctoToken + " exploded" }, 500));
        GitLabReturns(DateTimeOffset.UtcNow);
        var trace = new StringWriter();
        var listener = new TextWriterTraceListener(trace);
        Trace.Listeners.Add(listener);
        try
        {
            await N.RefreshAsync(); // no exception, no dialog (ShowDialog would return false and is never needed)
            Trace.Flush();
            Assert.Equal(["Review me"], N.Items.Select(r => r.Title));
            Assert.Contains("Refresh Notifications for octo@https://github.com", trace.ToString());
            Assert.Contains("GitHub Error: token *** exploded", trace.ToString());
            foreach (var token in new[] { OctoToken, GitLabToken }) Assert.DoesNotContain(token, trace.ToString());
            // Only the GitLab account got its time; the failed one stays unset.
            Assert.Equal([null, true], _hosting.Accounts.Select(a => a.NotificationsUpdatedAt is null ? (bool?)null : true));
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }
    }

    [Fact]
    public async Task Selecting_a_row_marks_it_read_opens_only_http_urls_and_closes_the_popup()
    {
        var now = DateTimeOffset.UtcNow;
        Add(HostType.GitHub, "octo", OctoToken, enabled: true);
        GitHubReturns(GitHubItem("a", 1, now), GitHubItem("b", 2, now.AddMinutes(-1), url: "javascript:alert(1)/2"));
        await N.RefreshAsync();
        N.IsOpen = true;

        N.Selected = N.Items[0];
        Assert.Equal(["https://github.com/octo/repo/issues/1"], _opened);
        Assert.Equal((false, false, null), (N.Items[0].Unread, N.IsOpen, N.Selected));
        Assert.Equal("1", N.Badge);

        N.Selected = N.Items[1];
        Assert.Single(_opened); // not http(s): not handed to the browser
        Assert.Null(N.Badge);
    }

    [AvaloniaFact]
    public async Task Enable_Notifications_check_box_stores_the_flag_and_refreshes_at_once()
    {
        GitHubReturns(GitHubItem("a", 1, DateTimeOffset.UtcNow));
        _server.Map("GET", "/user", FakeResponse.Json(new { login = "octo", avatar_url = "a", html_url = "h" }));
        Add(HostType.GitHub, "octo", OctoToken, enabled: false);
        var accounts = new AccountsViewModel(_hosting);
        await accounts.StatusTask;
        Assert.False(accounts.NotificationsEnabled);
        Assert.False(N.IsAvailable);

        accounts.NotificationsEnabled = true;
        await N.LastRefresh;

        Assert.True(Assert.Single(new AccountStore(_hosting.Store.FilePath).Accounts).EnableNotifications);
        Assert.Contains(_server.Requests, r => r.Path == "/notifications");
        Assert.Equal(["Issue 1"], N.Items.Select(r => r.Title));
        await WaitFor(() => { Dispatcher.UIThread.RunJobs(); return N.IsAvailable; });

        accounts.Selected = null;
        accounts.Selected = accounts.Items[0]; // the row keeps the new value
        Assert.True(accounts.NotificationsEnabled);
    }

    [Fact]
    public async Task A_new_refresh_cancels_the_running_one_and_its_stale_result_is_dropped()
    {
        Add(HostType.GitHub, "octo", OctoToken, enabled: true);
        using var gate = new ManualResetEventSlim();
        int calls = 0;
        _server.Map("GET", "/notifications", _ =>
        {
            if (Interlocked.Increment(ref calls) == 1) gate.Wait(TimeSpan.FromSeconds(20)); // the first answer is held
            return FakeResponse.Json(new[] { GitHubItem(calls == 1 ? "stale" : "fresh", calls, DateTimeOffset.UtcNow) });
        });
        try
        {
            var first = N.RefreshAsync();
            await WaitFor(() => Volatile.Read(ref calls) == 1);
            var between = DateTimeOffset.UtcNow;
            await N.RefreshAsync();
            gate.Set();
            await first;

            Assert.Equal(["Issue 2"], N.Items.Select(r => r.Title));
            Assert.False(N.IsUpdating);
            Assert.True(Assert.Single(_hosting.Accounts).NotificationsUpdatedAt >= between); // the second round's time
            Assert.Equal([NotificationsViewModel.Interval, NotificationsViewModel.Interval], _timer);
        }
        finally { gate.Set(); }
    }

    [AvaloniaFact]
    public async Task Start_schedules_the_first_refresh_after_5_seconds_and_each_refresh_15_minutes()
    {
        var n = new NotificationsViewModel(_hosting);
        n.Start();
        try
        {
            Assert.Equal((true, TimeSpan.FromSeconds(5)), (n.Timer!.IsEnabled, n.Timer.Interval));
            await n.RefreshAsync();
            Assert.Equal((true, TimeSpan.FromMinutes(15)), (n.Timer.IsEnabled, n.Timer.Interval));
        }
        finally { n.Timer!.Stop(); }
    }

    [Fact]
    public async Task Update_token_keeps_the_notification_switch_and_time()
    {
        const string NewToken = "ghp_new-token-9z8y7x";
        _server.Map("GET", "/user", FakeResponse.Json(new { login = "octo", avatar_url = "a", html_url = "h" }));
        var at = new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);
        var account = Add(HostType.GitHub, "octo", OctoToken, enabled: false);
        _hosting.Store.Change(account, a => a with { NotificationsUpdatedAt = at });

        var login = new LoginViewModel(_hosting, HostType.GitHub, _hosting.Accounts[0]) { MethodIndex = 1, Token = NewToken };
        await login.SubmitAsync();

        Assert.True(login.Result);
        var stored = Assert.Single(new AccountStore(_hosting.Store.FilePath).Accounts);
        Assert.Equal((false, at), (stored.EnableNotifications, stored.NotificationsUpdatedAt));
        Assert.Equal(stored, login.Account);
        Assert.Equal(NewToken, _secrets.Lookup(Account.GitHubServer, "octo", SecretKind.AccessToken));
    }

    [AvaloniaFact]
    public async Task Log_out_removes_the_account_rows_and_badge()
    {
        _server.Map("GET", "/user", FakeResponse.Json(new { login = "octo", avatar_url = "a", html_url = "h" }));
        Add(HostType.GitHub, "octo", OctoToken, enabled: true);
        GitHubReturns(GitHubItem("a", 1, DateTimeOffset.UtcNow));
        await N.RefreshAsync();
        Assert.Equal("1", N.Badge);

        _hosting.ShowDialog = _ => Task.FromResult(true); // the log out confirmation
        var accounts = new AccountsViewModel(_hosting);
        await accounts.LogOutAsync();
        await N.LastRefresh;

        Assert.Empty(_hosting.Accounts);
        Assert.Empty(N.Items);
        Assert.Null(N.Badge);
    }

    [AvaloniaFact]
    public async Task Bell_shows_with_an_enabled_account_with_badge_and_popup_rows()
    {
        var saved = HostingService.Current;
        HostingService.Current = _hosting;
        var window = new MainWindow { DataContext = new MainWindowViewModel() };
        try
        {
            window.Show();
            window.StatusTimer.Stop();
            Settle(window);
            var bell = Find<Panel>(window, "NotificationsBell");
            Assert.False(bell.IsVisible); // no account with notifications on

            var now = DateTimeOffset.UtcNow;
            Add(HostType.GitHub, "octo", OctoToken, enabled: true);
            GitHubReturns(GitHubItem("a", 1, now), GitHubItem("b", 2, now.AddMinutes(-5)), GitHubItem("c", 3, now.AddMinutes(-9), unread: false));
            Settle(window);
            Assert.True(bell.IsVisible);
            Assert.False(Find<Canvas>(window, "NotificationsBadge").IsVisible);

            await N.RefreshAsync();
            Settle(window);
            Assert.True(Find<Canvas>(window, "NotificationsBadge").IsVisible);
            Assert.Contains(Find<Canvas>(window, "NotificationsBadge").GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "2");

            var button = Find<Button>(window, "NotificationsButton");
            Assert.Equal("Notifications", ToolTip.GetTip(button));
            button.Command!.Execute(null);
            Settle(window);
            Assert.True(N.IsOpen);
            Assert.False(button.IsEffectivelyEnabled); // disabled while open
            var popup = (NotificationsPopup)Find<Popup>(window, "NotificationsPopup").Child!;
            Settle(window);
            var list = popup.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "NotificationList");
            Assert.Equal(3, list.ItemCount);
            var texts = popup.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Inlines?.Text ?? t.Text).ToList();
            Assert.Contains("Notifications", texts);
            Assert.Contains("Issue 1", texts);
            Assert.Contains(texts, t => t is not null && t.Contains("octo/repo") && t.Contains("#1"));
            Assert.Contains(N.Items[0].DateText, texts);
            Assert.Matches(@"^\d{1,2} [A-Z][a-z]{2} \d{4} \d{2}:\d{2}$", N.Items[0].DateText);

            list.SelectedIndex = 1;
            Settle(window);
            Assert.Equal(["https://github.com/octo/repo/issues/2"], _opened);
            Assert.False(N.IsOpen);
            Assert.Null(list.SelectedItem);
            Assert.Contains(Find<Canvas>(window, "NotificationsBadge").GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "1");
        }
        finally
        {
            HostingService.Current = saved;
            window.Close();
        }
    }

    static void Settle(Window w)
    {
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    static T Find<T>(Window w, string name) where T : Control => w.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    static async Task WaitFor(Func<bool> condition)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException();
            await Task.Delay(10);
        }
    }
}
