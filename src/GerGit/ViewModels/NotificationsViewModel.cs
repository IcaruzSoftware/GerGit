using System.Diagnostics;
using System.Globalization;
using System.Security;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GerGit.Hosting;

namespace GerGit.ViewModels;

/// <summary>A row of the notifications popup. Unread = the server's state, until the
/// row is opened (read locally only).</summary>
public sealed record NotificationRow(HostedNotification Item, bool Unread)
{
    public string Repository => Item.Repository;
    public string Title => Item.Title;
    public string? AvatarUrl => Item.AvatarUrl;

    /// <summary>`#&lt;number&gt;` for issues and pull requests, the first 7 characters of the hash for commits.</summary>
    public string TargetText => Item.Kind == NotificationKind.Commit ? Item.TargetId[..Math.Min(7, Item.TargetId.Length)] : "#" + Item.TargetId;

    /// <summary>`d MMM yyyy HH:mm` in local time, e.g. `3 Oct 2026 14:05`.</summary>
    public string DateText => Item.UpdatedAt.ToLocalTime().ToString("d MMM yyyy HH:mm", CultureInfo.InvariantCulture);

    public string Icon => Item.Kind switch
    {
        NotificationKind.Commit => "git-commit-horizontal",
        NotificationKind.Issue => "circle-dot",
        _ => "git-pull-request",
    };
}

/// <summary>
/// One app-wide list over every account with Enable Notifications on (GitHub notifications;
/// GitLab to-dos as an extra), refreshed 5 s after start, every 15 minutes, by the popup's refresh button and
/// the account check box; every refresh restarts the timer and cancels a running one. Errors are only logged.
/// The timer is <see cref="RestartTimer"/>: the desktop app installs a dispatcher timer with <see cref="Start"/>, tests
/// record the delays and call <see cref="RefreshAsync"/> as the tick.
/// Not built yet: a `Refresh Notifications` job (there is no app-wide job queue, as for AutoFetch) and a repository avatar in
/// the desktop notification; each shown desktop notification keeps one waiting `notify-send` process until it is
/// clicked or closed (a D-Bus client would need none).
/// </summary>
public sealed partial class NotificationsViewModel : ObservableObject
{
    public static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    readonly HostingService _hosting;
    CancellationTokenSource? _cts;

    public NotificationsViewModel(HostingService hosting)
    {
        _hosting = hosting;
        hosting.Store.Changed += () => Dispatcher.UIThread.Post(UpdateAvailable);
        UpdateAvailable();
    }

    /// <summary>The bell is shown only while at least one account has notifications on.</summary>
    [ObservableProperty]
    public partial bool IsAvailable { get; private set; }

    [ObservableProperty]
    public partial bool IsOpen { get; set; }

    [ObservableProperty]
    public partial bool IsUpdating { get; private set; }

    /// <summary>All accounts' notifications, newest first.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Badge))]
    public partial IReadOnlyList<NotificationRow> Items { get; private set; } = [];

    /// <summary>The number of unread notifications on the bell (null = no badge).</summary>
    public string? Badge => Items.Count(r => r.Unread) switch { 0 => null, > 99 => "99+", var n => n.ToString(CultureInfo.InvariantCulture) };

    /// <summary>Selecting a row opens it (see <see cref="OpenRow"/>); the selection is cleared again.</summary>
    [ObservableProperty]
    public partial NotificationRow? Selected { get; set; }

    partial void OnSelectedChanged(NotificationRow? value)
    {
        if (value is null) return;
        OpenRow(value);
        Selected = null;
    }

    /// <summary>(Re)starts the poll timer with the given delay. Default: no timer (tests, until <see cref="Start"/>).</summary>
    public Action<TimeSpan> RestartTimer { get; set; } = _ => { };

    /// <summary>Shows a desktop notification (title, text); true = the user clicked it. Tests record the calls.</summary>
    public Func<string, string, Task<bool>> ShowToast { get; set; } = NotifySendAsync;

    /// <summary>Brings the main window to the front (a click on the "several new" desktop notification).</summary>
    public Action? BringToFront { get; set; }

    /// <summary>The latest refresh (tests await it).</summary>
    public Task LastRefresh { get; private set; } = Task.CompletedTask;

    /// <summary>The desktop app's poll timer (null until <see cref="Start"/>; tests read it).</summary>
    internal DispatcherTimer? Timer { get; private set; }

    /// <summary>Desktop app: a dispatcher timer, first tick after <see cref="FirstDelay"/>.</summary>
    public void Start()
    {
        var timer = Timer = new DispatcherTimer();
        timer.Tick += (_, _) => RefreshCommand.Execute(null);
        RestartTimer = delay =>
        {
            timer.Stop();
            timer.Interval = delay;
            timer.Start();
        };
        RestartTimer(FirstDelay);
    }

    [RelayCommand]
    public void Open() => IsOpen = true;

    void UpdateAvailable() => IsAvailable = _hosting.Accounts.Any(a => a.EnableNotifications);

    /// <summary>One refresh: the first page of every qualifying account, merged newest first. Each successful account
    /// gets "updated at" = now (saved); unread items newer than its previous time are new (none on the very first fetch)
    /// and raise one desktop notification.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    public Task RefreshAsync()
    {
        // Cancelled before dispose: late users of the replaced token see it as cancelled.
        var old = _cts;
        var cts = _cts = new CancellationTokenSource();
        old?.Cancel();
        old?.Dispose();
        RestartTimer(Interval);
        return LastRefresh = RunAsync(cts);
    }

    async Task RunAsync(CancellationTokenSource cts)
    {
        IsUpdating = true;
        try
        {
            var now = DateTimeOffset.UtcNow; // before the fetch: nothing newer is missed by the next round
            var accounts = _hosting.Accounts.Where(a => a.EnableNotifications).ToList();
            var results = await Task.WhenAll(accounts.Select(a => FetchAsync(a, cts.Token)));
            if (cts.IsCancellationRequested) return;
            var fresh = new List<HostedNotification>();
            for (int i = 0; i < accounts.Count; i++)
            {
                if (results[i] is not { } list) continue;
                if (accounts[i].NotificationsUpdatedAt is { } since) fresh.AddRange(list.Where(n => n.Unread && n.UpdatedAt > since));
                _hosting.Store.Change(accounts[i], a => a with { NotificationsUpdatedAt = now }, raiseChanged: false);
            }
            Items = [.. results.OfType<IReadOnlyList<HostedNotification>>().SelectMany(l => l)
                .OrderByDescending(n => n.UpdatedAt).Select(n => new NotificationRow(n, n.Unread))];
            if (fresh.Count > 0) _ = ToastAsync(fresh);
        }
        finally
        {
            if (_cts == cts) IsUpdating = false;
        }
    }

    async Task<IReadOnlyList<HostedNotification>?> FetchAsync(Account account, CancellationToken ct)
    {
        try
        {
            var client = await _hosting.ClientAsync(account);
            return await client.GetNotificationsAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return null; }
        catch (Exception e)
        {
            // Background polling stays quiet: logged only. HostingException texts never hold the token.
            Trace.WriteLine($"Refresh Notifications for {account.Username}@{account.ServerUrl}: {e.GetType().Name}: {e.Message}");
            return null;
        }
    }

    /// <summary>One new item: `&lt;owner/repo&gt; #&lt;id&gt;` / its title, a click opens it. Several: `New Notifications`,
    /// a click brings the window to the front and opens the popup.</summary>
    async Task ToastAsync(List<HostedNotification> fresh)
    {
        if (fresh is [var one])
        {
            if (await ShowToast($"{one.Repository} #{one.TargetId}", one.Title))
            {
                // notify-send may wait for hours: the row may be gone by now, the page is opened anyway.
                OpenInBrowser(one);
                MarkRead(one);
            }
        }
        else if (await ShowToast("New Notifications", $"You've got {fresh.Count} new notifications"))
        {
            BringToFront?.Invoke();
            IsOpen = true;
        }
    }

    /// <summary>Marks the row read (locally only: the next refresh brings back the server state), opens its page in the
    /// browser (http/https only) and closes the popup.</summary>
    void OpenRow(NotificationRow row)
    {
        MarkRead(row.Item);
        OpenInBrowser(row.Item);
        IsOpen = false;
    }

    void MarkRead(HostedNotification item) => Items = [.. Items.Select(r => r.Item == item ? r with { Unread = false } : r)];

    /// <summary>Server URLs reach the browser only when they are http/https.</summary>
    void OpenInBrowser(HostedNotification item)
    {
        if (Uri.TryCreate(item.WebUrl, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
            _hosting.OpenBrowser(item.WebUrl);
        else
            Trace.WriteLine($"Notification {item.Id}: not opened, no http(s) URL");
    }

    /// <summary>libnotify's `notify-send` (no sound, a default action, body markup escaped). An old `notify-send`
    /// without `--action` (libnotify before 0.7.10) fails at once: shown again without the action (no click then).
    /// Missing or failing = skipped, logged.</summary>
    static async Task<bool> NotifySendAsync(string title, string text)
    {
        string[] args = ["--app-name=GerGit", "--hint=boolean:suppress-sound:true", "--", title, SecurityElement.Escape(text)];
        var watch = Stopwatch.StartNew();
        var (exit, chosen) = await RunNotifySendAsync(["--action=default=Open", .. args]);
        if (exit > 0 && watch.Elapsed < TimeSpan.FromSeconds(2)) await RunNotifySendAsync(args);
        return exit == 0 && chosen.Trim() == "default";
    }

    static async Task<(int Exit, string Output)> RunNotifySendAsync(string[] args)
    {
        try
        {
            // Absolute path (ProgramPath): a bare name would be looked up in the current directory first.
            var notifySend = GerGit.Core.ProgramPath.Of("notify-send") ?? throw new FileNotFoundException("notify-send is not on PATH");
            using var process = Process.Start(new ProcessStartInfo(notifySend, args)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (process is null) return (-1, "");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0) Trace.WriteLine($"notify-send failed ({process.ExitCode}): {error.Trim()}");
            return (process.ExitCode, await output);
        }
        catch (Exception e)
        {
            Trace.WriteLine($"Desktop notification skipped: {e.Message}");
            return (-1, "");
        }
    }
}
