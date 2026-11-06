using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GerGit.Core;
using GerGit.Core.Git;
using GerGit.Hosting;

namespace GerGit.ViewModels.Dialogs;

/// <summary>An item of the Account selector; Choice null = the separator.</summary>
public sealed record CloneAccountItem(AccountChoice? Choice, HostType Type)
{
    public bool IsSeparator => Choice is null;
    public bool IsSelectable => Choice is not null;
    public string Text => Choice?.Text ?? "";
    public override string ToString() => Text;
}

// Rows of the project list.

/// <summary>Group header of one account: service icon + `&lt;type name&gt; (&lt;username&gt;)`.</summary>
public sealed class CloneAccountHeader(Account account)
{
    public Account Account { get; } = account;
    public HostType Type => Account.Type;
    public string Text => $"{HostTypes.DisplayName(Account.Type)} ({Account.Username})";
}

/// <summary>Owner sub-header.</summary>
public sealed class CloneOwnerHeader(string owner)
{
    public string Owner { get; } = owner;
}

/// <summary>An account's fallback: `Loading repositories...`, or `Unable to load repositories` + the friendly error.</summary>
public sealed class CloneListStatus(string title, string? message = null)
{
    public string Title { get; } = title;
    public string? Message { get; } = message;
    public bool HasMessage => Message is not null;
}

/// <summary>One repository of an account; a click puts its HTTPS clone URL into the dialog.</summary>
public sealed class CloneRepoRow(Account account, HostedRepository repository)
{
    public Account Account { get; } = account;
    public HostedRepository Repository { get; } = repository;
    public HostType Type => Account.Type;
    public string Name => Repository.Name;
}

/// <summary>
/// "Clone (Ctrl+N, File > Clone...)" with the account project list of "Clone from account" below the
/// form. With a repository tab active the clone is a job of that tab and the dialog closes at once;
/// without one the dialog stays busy with the live progress and closes after success. Success opens the clone as a tab.
/// </summary>
public sealed partial class CloneViewModel : DialogViewModel
{
    public const string LoadingText = "Loading repositories...", LoadErrorTitle = "Unable to load repositories";

    readonly MainWindowViewModel _main;
    readonly IReadOnlyList<Account> _accounts;
    readonly List<AccountRepos> _lists;
    readonly CancellationTokenSource _closed = new();
    CancellationTokenSource? _filterDelay;
    bool _settingChoices; // the selector is rebuilt from the URL: its selection must not rewrite the URL again
    bool _sshDenied;      // Test Connection said "Permission denied (publickey)": the Test button stays hidden

    sealed class AccountRepos(Account account)
    {
        public Account Account { get; } = account;
        public IReadOnlyList<HostedRepository>? Repos;
        public string? Error;
    }

    public CloneViewModel(MainWindowViewModel main, HostingService hosting)
    {
        _main = main;
        _accounts = hosting.Accounts;
        Title = "Clone";
        Description = "Clone a remote repository into a local folder";
        SubmitText = "Clone";
        Width = 590;
        MinHeight = 245;
        // Not built yet: a source-folder setting; the default is always the home folder.
        ParentFolder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        CloseRequested += _ => _closed.Cancel();
        _lists = [.. _accounts.Select(a => new AccountRepos(a))];
        Rebuild();
        Loading = Task.WhenAll(_lists.Select(l => LoadAsync(hosting, l)));
        Validate();
    }

    /// <summary>"Recurse submodules" (default off): `clone --recurse-submodules`, which also clones every submodule from
    /// the URLs in the repository's .gitmodules. Later updates never initialize new submodules by themselves.</summary>
    [ObservableProperty]
    public partial bool RecurseSubmodules { get; set; }

    [ObservableProperty]
    public partial string Url { get; set; } = "";

    [ObservableProperty]
    public partial string ParentFolder { get; set; } = "";

    [ObservableProperty]
    public partial string Name { get; set; } = "";

    /// <summary>Protocol drop-down text (`HTTPS` / `SSH`), null = hidden (the URL is no HTTPS or SSH git URL).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProtocol))]
    public partial string? ProtocolText { get; private set; }

    public bool HasProtocol => ProtocolText is not null;

    /// <summary>The menu of the protocol drop-down: the HTTPS and SSH forms that can be built; choosing one replaces the URL.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<string> ProtocolForms { get; private set; } = [];

    /// <summary>Empty = the Account row is hidden.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAccountSelector))]
    public partial IReadOnlyList<CloneAccountItem> AccountItems { get; private set; } = [];

    public bool HasAccountSelector => AccountItems.Count > 0;

    [ObservableProperty]
    public partial CloneAccountItem? SelectedAccount { get; set; }

    /// <summary>"Test Connection" row: URL non-empty and all fields valid.</summary>
    [ObservableProperty]
    public partial bool ShowTestConnection { get; private set; }

    /// <summary>The project list is shown only with at least one account.</summary>
    public bool HasAccounts => _accounts.Count > 0;

    [ObservableProperty]
    public partial string Filter { get; set; } = "";

    /// <summary>Account headers, owner headers, status lines and repository rows, filtered.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<object> Rows { get; private set; } = [];

    /// <summary>All account lists loaded or failed (tests await it).</summary>
    public Task Loading { get; }

    /// <summary>The pending 100 ms filter delay (tests await it).</summary>
    public Task FilterTask { get; private set; } = Task.CompletedTask;

    /// <summary>Reads the clipboard text; set by the view (tests set their own).</summary>
    public Func<Task<string?>>? ReadClipboard { get; set; }

    partial void OnUrlChanged(string value)
    {
        value ??= "";
        Name = NameFromUrl(value);
        _sshDenied = false;
        SetStatus(DialogStatus.None); // any URL change clears the Test Connection result

        var remote = RemoteUrl.Parse(value);
        var account = remote?.MatchAccount(_accounts);
        ProtocolText = remote?.Scheme switch { "https" => "HTTPS", "ssh" => "SSH", _ => null };
        ProtocolForms = ProtocolText is null ? [] : new[] { remote!.HttpsForm(account), remote.SshForm(account) }.OfType<string>().ToList();

        _settingChoices = true;
        if (CloneAccounts.Choices(value, _accounts) is { } choices)
        {
            // default / custom show the icon of the host's accounts
            var type = _accounts.First(a => a.Host == remote!.Host).Type;
            AccountItems = [new(choices.Items[0], type), new(null, type), .. choices.Items.Skip(1).Select(c => new CloneAccountItem(c, c.Account?.Type ?? type))];
            SelectedAccount = AccountItems[choices.Selected == 0 ? 0 : choices.Selected + 1];
        }
        else
        {
            AccountItems = [];
            SelectedAccount = null;
        }
        _settingChoices = false;
        Validate();
    }

    /// <summary>Choosing an account rewrites the URL's user name (`default` removes it).</summary>
    partial void OnSelectedAccountChanged(CloneAccountItem? value)
    {
        if (!_settingChoices && value?.Choice is { } choice) Url = CloneAccounts.Apply(Url.Trim(), choice);
    }

    partial void OnParentFolderChanged(string value) => Validate();
    partial void OnNameChanged(string value) => Validate();

    partial void OnFilterChanged(string value) => FilterTask = FilterAfterDelayAsync();

    /// <summary>A click on a repository row: its HTTPS clone URL, and that account in the selector (when it is shown,
    /// the URL gets the account's user name).</summary>
    public void ChooseRepository(CloneRepoRow row)
    {
        Url = row.Repository.CloneUrl;
        if (AccountItems.FirstOrDefault(i => i.Choice?.Account?.SameIdentity(row.Account) == true) is { } item) SelectedAccount = item;
    }

    /// <summary>Submit rule: URL, parent folder and name non-blank after trimming.</summary>
    void Validate()
    {
        CanSubmit = (Url ?? "").Trim().Length > 0 && (ParentFolder ?? "").Trim().Length > 0 && (Name ?? "").Trim().Length > 0;
        ShowTestConnection = CanSubmit && !_sshDenied;
    }

    /// <summary>Repository name without ".git": the last path segment of the URL.</summary>
    internal static string NameFromUrl(string url)
    {
        var s = url.Trim().TrimEnd('/');
        s = s[(s.LastIndexOfAny(['/', ':', '\\']) + 1)..];
        return s.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? s[..^4] : s;
    }

    /// <summary>Clipboard pre-fill: a leading "git clone " removed, trimmed, surrounding double quotes removed;
    /// null unless the result is a git URL. "valid git URL" = no white space, no leading `-`, and an http(s),
    /// ssh or git URL with host and path, or the scp-like form with `@`, `/` or `.git` (so `12:30` or `TODO:fix` are not).</summary>
    internal static string? UrlFromClipboard(string? text)
    {
        if (text is null) return null;
        text = text.TrimStart();
        if (text.StartsWith("git clone ", StringComparison.Ordinal)) text = text["git clone ".Length..];
        text = text.Trim();
        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"') text = text[1..^1];
        if (text.StartsWith('-') || text.Any(char.IsWhiteSpace)) return null;
        bool scpLike = !text.Contains("://", StringComparison.Ordinal);
        if (scpLike && !(text.Contains('@') || text.Contains('/') || text.EndsWith(".git", StringComparison.Ordinal))) return null;
        return RemoteUrl.Parse(text) is { Scheme: "http" or "https" or "ssh" or "git" } ? text : null;
    }

    /// <summary>On open and on every activation: an empty URL takes a git URL from the clipboard.</summary>
    public async Task PrefillFromClipboardAsync()
    {
        if ((Url ?? "").Trim().Length > 0 || IsBusy || ReadClipboard is null) return;
        string? text;
        try { text = await ReadClipboard(); }
        catch (Exception e)
        {
            Trace.WriteLine(e); // clipboard owner gone: nothing entered
            return;
        }
        if (UrlFromClipboard(text) is { } url && (Url ?? "").Trim().Length == 0) Url = url;
    }

    // ---------------------------------------------------------------- project list

    async Task LoadAsync(HostingService hosting, AccountRepos list)
    {
        try
        {
            var client = await hosting.ClientAsync(list.Account);
            list.Repos = await client.GetRepositoriesAsync(_closed.Token);
        }
        catch (OperationCanceledException) { return; } // dialog closed
        catch (HostingException e) { list.Error = e.Message; } // friendly text; never holds the token
        catch (Exception e)
        {
            Trace.WriteLine($"Repositories of {list.Account.Username}@{list.Account.ServerUrl}: {e.GetType().Name}: {e.Message}");
            list.Error = "Error: " + e.Message;
        }
        Rebuild();
    }

    async Task FilterAfterDelayAsync()
    {
        _filterDelay?.Cancel();
        var delay = _filterDelay = new CancellationTokenSource();
        try { await Task.Delay(100, delay.Token); }
        catch (OperationCanceledException) { return; } // a newer keystroke
        Rebuild();
    }

    /// <summary>Per account: header, then its fallback or its repositories grouped by owner in first-appearance order of
    /// the full result, filtered inside each group by a case-insensitive "contains" on the name only.</summary>
    void Rebuild()
    {
        var filter = (Filter ?? "").Trim();
        var rows = new List<object>();
        foreach (var list in _lists)
        {
            rows.Add(new CloneAccountHeader(list.Account));
            if (list.Error is { } error) rows.Add(new CloneListStatus(LoadErrorTitle, error));
            else if (list.Repos is null) rows.Add(new CloneListStatus(LoadingText));
            else
                foreach (var owner in list.Repos.GroupBy(r => r.Group))
                {
                    var matches = owner.Where(r => r.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (matches.Count == 0) continue;
                    rows.Add(new CloneOwnerHeader(owner.Key));
                    rows.AddRange(matches.Select(r => new CloneRepoRow(list.Account, r)));
                }
        }
        Rows = rows;
    }

    // ---------------------------------------------------------------- Test Connection and Clone

    /// <summary>Test Connection: `git ls-remote -- "&lt;url&gt;" HEAD` (the `--` keeps a URL starting with `-` from becoming an option) with the fields disabled.
    /// Not built yet: no "Configure SSH Keys" button after "Permission denied (publickey)" (SSH key settings are not built).</summary>
    [RelayCommand]
    public async Task TestConnectionAsync()
    {
        if (!SubmitEnabled) return;
        var job = new Job("Test Connection", hidden: true);
        RunningJob = job; // Cancel / Esc / closing the window stop it
        IsBusy = true;
        SetStatus(DialogStatus.InProgress, "Connecting...");
        var r = await GitRunner.RunAsync(null, ["ls-remote", "--", Url.Trim(), "HEAD"], ct: job.Token);
        IsBusy = false;
        RunningJob = null;
        if (r.Cancelled)
        {
            Close(false);
            return;
        }
        var output = r.Stdout + "\n" + r.Stderr;
        if (r.Success) SetStatus(DialogStatus.Success, "Connection succeeded");
        else if (output.Contains("Permission denied (publickey)", StringComparison.Ordinal))
        {
            _sshDenied = true;
            SetStatus(DialogStatus.Warning, "Permission denied (publickey)");
            Validate();
        }
        else if (output.Contains("not found", StringComparison.Ordinal)) SetStatus(DialogStatus.Warning, "Repository not found");
        else
        {
            SetStatus(DialogStatus.None);
            await ShowGitErrorAsync(GitErrors.Classify(r));
        }
    }

    protected override async Task OnSubmitAsync()
    {
        string url = Url.Trim(), name = Name.Trim();
        var destination = Path.Combine(ParentFolder.Trim(), name);
        var title = $"Cloning {name}...";
        if (_main.SelectedTab is { } tab)
        {
            Close(true);
            var done = await tab.RunJobAsync(title, async j => await GitRepository.CloneAsync(j, url, destination, RecurseSubmodules));
            if (done.Outcome == JobOutcome.Succeeded) await OpenAsync(destination);
            return;
        }

        // No repository tab: the dialog stays with "Cloning..." and git's progress line.
        var job = new Job(title);
        RunningJob = job;
        IsBusy = true;
        SetStatus(DialogStatus.InProgress, "Cloning...");
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) =>
        {
            if (job.Message.Length > 0) StatusText = job.Message;
        });
        timer.Start();
        GitResult? result = null;
        string? exception = null;
        try { result = await GitRepository.CloneAsync(job, url, destination, RecurseSubmodules); }
        catch (GitException e) { exception = e.Message; }
        finally
        {
            timer.Stop();
            IsBusy = false;
            RunningJob = null;
        }
        if (job.CancelRequested)
        {
            Close(false);
            return;
        }
        if (result is { Success: true })
        {
            Close(true);
            await OpenAsync(destination);
            return;
        }
        SetStatus(DialogStatus.None);
        await ShowGitErrorAsync(exception is null ? GitErrors.Classify(result!) : GitErrors.Classify(exception));
    }

    async Task OpenAsync(string destination)
    {
        if (await _main.OpenAsync(destination) is { } failure) await ShowGitErrorAsync(GitErrors.Classify(failure.Text));
    }

    async Task ShowGitErrorAsync(GitError error)
    {
        var vm = GitErrorViewModel.Create(error, null, null, null);
        await _main.ShowDialog(vm);
        if (vm.ChosenAction is { } action) await action();
    }
}
