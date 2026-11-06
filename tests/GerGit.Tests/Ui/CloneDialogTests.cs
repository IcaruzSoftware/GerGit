using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GerGit.Core;
using GerGit.Core.Git;
using GerGit.Hosting;
using GerGit.Tests.Hosting;
using GerGit.ViewModels;
using GerGit.ViewModels.Dialogs;
using GerGit.Views.Dialogs;

namespace GerGit.Tests.Ui;

/// <summary>The Clone dialog with the account project list (Clone from account) against a local smart-HTTP git server, a fake GitHub / GitLab and an in-memory keyring. In the Askpass
/// collection: the clone tests install the process-wide credential hooks.</summary>
[Collection("Askpass")]
public sealed class CloneDialogTests : IDisposable
{
    const string User = "alice", Token = "glpat-clone-9c41d2e7", GitHubToken = "ghp_clone-31a7", GitHubToken2 = "ghp_clone-77b0";

    readonly FakeHostingServer _api = new();
    readonly FakeSecretStore _secrets = new();
    readonly string _dir = Directory.CreateTempSubdirectory("gergit-clone-").FullName;
    readonly HostingService _hosting;
    readonly MainWindowViewModel _main = new();
    readonly List<DialogViewModel> _dialogs = [];
    readonly List<TempRepo> _repos = [];
    readonly List<IDisposable> _disposables = [];
    readonly Func<CredentialPrompt, Task<string?>> _prompt = Askpass.PromptCredentials;
    readonly Func<CredentialQuery, Task<CredentialAnswer?>> _lookup = Askpass.LookupCredential;
    readonly Func<int> _gitHubCount = GitErrors.GitHubAccountCount;
    bool _askpass;
    int _prompts;

    public CloneDialogTests()
    {
        _hosting = new HostingService(new AccountStore(Path.Combine(_dir, "accounts.json")), _secrets) { GitHubApiBase = _api.BaseUri };
        _main.ShowDialog = vm =>
        {
            _dialogs.Add(vm);
            return Task.FromResult(false);
        };
    }

    public void Dispose()
    {
        Dispatcher.UIThread.RunJobs();
        foreach (var tab in _main.Tabs.ToList()) _main.CloseTab(tab);
        if (_askpass)
        {
            GitRunner.CredentialHelperEnabled = false;
            (Askpass.PromptCredentials, Askpass.LookupCredential, GitErrors.GitHubAccountCount) = (_prompt, _lookup, _gitHubCount);
            Askpass.Stop();
        }
        foreach (var d in _disposables) d.Dispose();
        foreach (var r in _repos) r.Dispose();
        _api.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    Account Add(Account account, string? token)
    {
        if (token is not null) _secrets.Store(account.ServerUrl, account.Username, account.SecretKind, token);
        _hosting.Store.Add(account);
        return account;
    }

    Account AddGitHub(string login, string token) =>
        Add(new Account(HostType.GitHub, AuthType.AccessToken, Account.GitHubServer, login, EnableNotifications: true), token);

    /// <summary>A git server with repository `proj` (one commit) and its account (GitLab Server on http://127.0.0.1:port)
    /// with the token in the fake keyring; the app's credential hooks installed, prompts counted.</summary>
    GitHttpServer GitServer()
    {
        var source = new TempRepo();
        _repos.Add(source);
        source.Commit("first", 1, ("a.txt", "a"));
        var server = new GitHttpServer(User, Token);
        _disposables.Add(server);
        server.AddRepo("proj", source.Path);
        Add(new Account(HostType.GitLabServer, AuthType.AccessToken, server.Url.TrimEnd('/'), User), Token);
        Askpass.Start();
        _askpass = true;
        Askpass.PromptCredentials = _ => { Interlocked.Increment(ref _prompts); return Task.FromResult<string?>(null); };
        _hosting.Install();
        return server;
    }

    static object GitHubRepo(int i) => new
    {
        name = $"repo{i}",
        clone_url = $"https://github.com/{(i % 2 == 0 ? "even" : "odd")}/repo{i}.git",
        ssh_url = $"git@github.com:o/repo{i}.git",
        owner = new { login = i % 2 == 0 ? "even" : "odd", avatar_url = "https://avatars/o" },
    };

    static object GitLabRepo(int i) => new
    {
        name = $"Proj{i}",
        http_url_to_repo = $"https://gl.example/g/proj{i}.git",
        ssh_url_to_repo = $"git@gl.example:g/proj{i}.git",
        @namespace = new { name = "Group" },
    };

    static FakeResponse Page(FakeRequest r, int total, int pageSize, Func<int, object> item)
    {
        var first = (int.Parse(r.QueryValue("page")!) - 1) * pageSize;
        return FakeResponse.Json(Enumerable.Range(first, Math.Clamp(total - first, 0, pageSize)).Select(item).ToArray());
    }

    CloneViewModel Open() => new(_main, _hosting);

    static List<CloneRepoRow> Repos(CloneViewModel vm) => vm.Rows.OfType<CloneRepoRow>().ToList();

    static string Text(object row) => row switch
    {
        CloneAccountHeader h => "# " + h.Text,
        CloneOwnerHeader o => "## " + o.Owner,
        CloneListStatus s => s.HasMessage ? $"! {s.Title}: {s.Message}" : "! " + s.Title,
        CloneRepoRow r => r.Name,
        _ => "?",
    };

    // ---------------------------------------------------------------- clone

    [AvaloniaFact]
    public async Task Clone_without_a_tab_uses_the_account_opens_the_tab_and_keeps_the_token_out_of_the_config()
    {
        var server = GitServer();
        var vm = Open();
        vm.Url = server.Url + "proj.git";
        vm.ParentFolder = _dir;
        Assert.Equal("proj", vm.Name);
        Assert.False(vm.HasAccountSelector); // one account of the host: the URL stays as is

        await vm.SubmitAsync();

        Assert.True(vm.Result);
        var dest = Path.Combine(_dir, "proj");
        var tab = Assert.Single(_main.Tabs);
        Assert.Same(tab, _main.SelectedTab);
        Assert.Equal(dest, tab.Path);
        Assert.True(File.Exists(Path.Combine(dest, "a.txt")));
        Assert.Equal(0, _prompts);
        Assert.Empty(_dialogs);
        var config = await File.ReadAllTextAsync(Path.Combine(dest, ".git", "config"));
        Assert.DoesNotContain(Token, config);
        Assert.Contains($"url = {server.Url}proj.git\n", config); // no user name either
        await vm.Loading;
    }

    [AvaloniaFact]
    public async Task Recurse_submodules_clones_the_submodules_and_is_off_by_default()
    {
        // git refuses local paths for submodule clones unless allowed; GIT_CONFIG_COUNT reaches the clone of the submodule.
        string[] keys = ["GIT_CONFIG_COUNT", "GIT_CONFIG_KEY_0", "GIT_CONFIG_VALUE_0"];
        var saved = keys.Select(Environment.GetEnvironmentVariable).ToArray();
        (string, string)[] values = [("GIT_CONFIG_COUNT", "1"), ("GIT_CONFIG_KEY_0", "protocol.file.allow"), ("GIT_CONFIG_VALUE_0", "always")];
        foreach (var (k, v) in values) Environment.SetEnvironmentVariable(k, v);
        try
        {
            var sub = new TempRepo();
            var super = new TempRepo();
            _repos.AddRange([sub, super]);
            sub.Commit("s", 0, ("s.txt", "s\n"));
            super.Git("submodule", "add", "-q", sub.Path, "sub");
            super.Commit("p", 0);

            var plain = Open();
            Assert.False(plain.RecurseSubmodules);
            (plain.Url, plain.ParentFolder, plain.Name) = (super.Path, _dir, "plain");
            await plain.SubmitAsync();
            Assert.True(plain.Result);
            Assert.False(Path.Exists(Path.Combine(_dir, "plain", "sub", ".git")));

            var vm = Open();
            (vm.Url, vm.ParentFolder, vm.Name, vm.RecurseSubmodules) = (super.Path, _dir, "deep", true);
            await vm.SubmitAsync();
            Assert.True(vm.Result);
            Assert.True(File.Exists(Path.Combine(_dir, "deep", "sub", "s.txt")));
            Assert.Contains("$ git clone --recurse-submodules ", _main.Tabs[0].Jobs.History.Single(j => j.Name.Contains("deep")).Output);
        }
        finally
        {
            for (int i = 0; i < keys.Length; i++) Environment.SetEnvironmentVariable(keys[i], saved[i]);
        }
    }

    [AvaloniaFact]
    public async Task Clone_with_a_tab_runs_as_a_job_of_that_tab_and_a_failure_shows_the_error_window()
    {
        var server = GitServer();
        var open = new TempRepo();
        _repos.Add(open);
        open.Commit("first", 1, ("b.txt", "b"));
        Assert.Null(await _main.OpenAsync(open.Path));
        var first = _main.SelectedTab!;
        var errors = new List<string>();
        first.ShowGitError = (r, _) => { errors.Add(r.ReadableOutput); return Task.CompletedTask; };

        var vm = Open();
        vm.Url = server.Url + "proj.git";
        vm.ParentFolder = _dir;
        vm.Name = "copy";
        await vm.SubmitAsync();

        Assert.True(vm.Result);
        Assert.Equal(2, _main.Tabs.Count);
        Assert.Equal(Path.Combine(_dir, "copy"), _main.SelectedTab!.Path);
        var job = Assert.Single(first.Jobs.History, j => j.Name == "Cloning copy...");
        Assert.Equal(JobOutcome.Succeeded, job.Outcome);
        Assert.Contains("$ git clone", job.Output);
        Assert.DoesNotContain(Token, job.Output);
        Assert.Empty(errors);

        _main.SelectedTab = first;
        var missing = Open();
        missing.Url = server.Url + "nope.git";
        missing.ParentFolder = _dir;
        await missing.SubmitAsync();
        Assert.True(missing.Result); // closed at once
        Assert.Equal(2, _main.Tabs.Count);
        Assert.Contains("not found", Assert.Single(errors));
        Assert.Equal(0, _prompts);
        await Task.WhenAll(vm.Loading, missing.Loading);
    }

    [AvaloniaFact]
    public async Task Clone_failure_without_a_tab_keeps_the_dialog_open_and_shows_the_Git_Error_window()
    {
        var server = GitServer();
        var vm = Open();
        vm.Url = server.Url + "nope.git";
        vm.ParentFolder = _dir;

        await vm.SubmitAsync();

        Assert.Null(vm.Result);
        Assert.False(vm.IsBusy);
        Assert.Empty(_main.Tabs);
        var error = Assert.IsType<GitErrorViewModel>(Assert.Single(_dialogs));
        Assert.Contains("not found", error.Text);
        await vm.Loading;
    }

    [AvaloniaFact]
    public async Task Test_Connection_reports_success_missing_repository_and_opens_the_error_window_otherwise()
    {
        var server = GitServer();
        var vm = Open();
        vm.ParentFolder = _dir;
        vm.Url = server.Url + "proj.git";
        Assert.True(vm.ShowTestConnection);

        await vm.TestConnectionAsync();
        Assert.Equal((DialogStatus.Success, "Connection succeeded"), (vm.Status, vm.StatusText));
        Assert.False(vm.IsBusy);
        Assert.Null(vm.Result);

        vm.Url = server.Url + "nope.git";
        Assert.Equal(DialogStatus.None, vm.Status); // any URL change clears the result
        await vm.TestConnectionAsync();
        Assert.Equal((DialogStatus.Warning, "Repository not found"), (vm.Status, vm.StatusText));

        var closed = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        closed.Start();
        var port = ((System.Net.IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();
        vm.Url = $"http://127.0.0.1:{port}/x.git";
        await vm.TestConnectionAsync();
        Assert.Equal(DialogStatus.None, vm.Status);
        Assert.IsType<GitErrorViewModel>(Assert.Single(_dialogs));
        Assert.Equal(0, _prompts);
        await vm.Loading;
    }

    [AvaloniaFact]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task A_URL_starting_with_a_dash_never_reaches_git_as_an_option()
    {
        var marker = Path.Combine(_dir, "ran");
        var script = Path.Combine(_dir, "evil.sh");
        await File.WriteAllTextAsync(script, $"#!/bin/sh\ntouch '{marker}'\nexit 1\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var vm = Open();
        vm.ParentFolder = _dir;
        vm.Url = "--upload-pack=" + script;
        vm.Name = "evil";

        await vm.TestConnectionAsync();
        await vm.SubmitAsync();

        Assert.False(File.Exists(marker));
        Assert.Equal(2, _dialogs.OfType<GitErrorViewModel>().Count()); // git read it as a repository path and failed
        Assert.Empty(_main.Tabs);
    }

    // ---------------------------------------------------------------- form rules

    [AvaloniaFact]
    public void Submit_rule_name_from_URL_and_Test_Connection_visibility()
    {
        var vm = Open();
        Assert.Equal(("Clone", "Clone a remote repository into a local folder", "Clone", 590d, 245d),
            (vm.Title, vm.Description, vm.SubmitText, vm.Width, vm.MinHeight));
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), vm.ParentFolder);
        Assert.False(vm.HasAccounts);
        Assert.False(vm.CanSubmit);
        Assert.False(vm.ShowTestConnection);

        vm.Url = "  https://example.com/team/My.Repo.git/ ";
        Assert.Equal("My.Repo", vm.Name);
        Assert.True(vm.CanSubmit);
        Assert.True(vm.ShowTestConnection);
        vm.Url = "git@example.com:team/tool.git";
        Assert.Equal("tool", vm.Name);
        vm.Name = "  ";
        Assert.False(vm.CanSubmit);
        Assert.False(vm.ShowTestConnection);
        vm.Name = "x";
        vm.ParentFolder = " ";
        Assert.False(vm.CanSubmit);
        vm.ParentFolder = "/tmp";
        Assert.True(vm.SubmitEnabled);
    }

    [AvaloniaFact]
    public void Protocol_switch_offers_the_HTTPS_and_SSH_forms_of_valid_URLs_only()
    {
        var vm = Open();
        vm.Url = "https://user@github.com/o/r.git";
        Assert.Equal("HTTPS", vm.ProtocolText);
        Assert.Equal(["https://github.com/o/r.git", "git@github.com:o/r.git"], vm.ProtocolForms);
        vm.Url = vm.ProtocolForms[1]; // what the menu item does
        Assert.Equal("SSH", vm.ProtocolText);
        Assert.Equal(["https://github.com/o/r.git", "git@github.com:o/r.git"], vm.ProtocolForms);
        vm.Url = "http://example.com/o/r.git";
        Assert.False(vm.HasProtocol);
        vm.Url = "not a url";
        Assert.False(vm.HasProtocol);
        Assert.Empty(vm.ProtocolForms);
    }

    [AvaloniaFact]
    public async Task Clipboard_prefill_strips_git_clone_and_quotes_and_only_fills_an_empty_URL()
    {
        string? clipboard = "  git clone \"https://github.com/o/r.git\"  ";
        var vm = Open();
        vm.ReadClipboard = () => Task.FromResult<string?>(clipboard);
        await vm.PrefillFromClipboardAsync();
        Assert.Equal(("https://github.com/o/r.git", "r"), (vm.Url, vm.Name));

        clipboard = "git@github.com:o/other.git";
        await vm.PrefillFromClipboardAsync(); // URL not empty: unchanged
        Assert.Equal("https://github.com/o/r.git", vm.Url);
        vm.Url = "";
        await vm.PrefillFromClipboardAsync();
        Assert.Equal("git@github.com:o/other.git", vm.Url);

        foreach (var text in new[] { "hello world", "some text: more", "/home/me/repo", "12:30", "TODO:fix", "--upload-pack=x:y/z",
                     "-oProxyCommand=a@b:c", "\"--upload-pack=evil\"", "", null })
        {
            vm.Url = "";
            clipboard = text;
            await vm.PrefillFromClipboardAsync();
            Assert.Equal("", vm.Url);
        }
        vm.ReadClipboard = () => throw new InvalidOperationException("owner gone");
        await vm.PrefillFromClipboardAsync();
        Assert.Equal("", vm.Url);
    }

    // ---------------------------------------------------------------- account selector

    [AvaloniaFact]
    public async Task Account_selector_with_two_accounts_of_the_host_rewrites_the_user_name()
    {
        AddGitHub("octo", GitHubToken);
        AddGitHub("mona", GitHubToken2);
        _api.Map("GET", "/user/repos", r => Page(r, 1, 70, GitHubRepo));
        var vm = Open();

        vm.Url = "http://github.com/o/r.git";
        Assert.False(vm.HasAccountSelector); // HTTPS only
        vm.Url = "https://github.com/o/r.git";
        Assert.True(vm.HasAccountSelector);
        Assert.Equal(["default (octo)", "", "GitHub (octo)", "GitHub (mona)"], vm.AccountItems.Select(i => i.Text));
        Assert.True(vm.AccountItems[1].IsSeparator);
        Assert.All(vm.AccountItems, i => Assert.Equal(HostType.GitHub, i.Type));
        Assert.Same(vm.AccountItems[0], vm.SelectedAccount);

        vm.SelectedAccount = vm.AccountItems[3];
        Assert.Equal("https://mona@github.com/o/r.git", vm.Url);
        Assert.Equal("GitHub (mona)", vm.SelectedAccount!.Text);
        vm.SelectedAccount = vm.AccountItems[0];
        Assert.Equal("https://github.com/o/r.git", vm.Url);

        vm.Url = "https://OCTO@github.com/o/r.git";
        Assert.Equal("GitHub (octo)", vm.SelectedAccount!.Text);
        vm.Url = "https://someone@github.com/o/r.git";
        Assert.Equal("custom (someone)", vm.SelectedAccount!.Text);
        Assert.Equal("custom (someone)", vm.AccountItems[^1].Text);

        // A row of the second account: its clone URL with that account's user name.
        await vm.Loading;
        var mona = Repos(vm).Single(r => r.Account.Username == "mona");
        vm.ChooseRepository(mona);
        Assert.Equal("https://mona@github.com/even/repo0.git", vm.Url);
        Assert.Equal("GitHub (mona)", vm.SelectedAccount!.Text);
    }

    // ---------------------------------------------------------------- project list

    [AvaloniaFact]
    public async Task Project_list_loads_every_account_with_pagination_groups_by_owner_filters_and_reports_errors()
    {
        AddGitHub("octo", GitHubToken);
        Add(new Account(HostType.GitLabServer, AuthType.AccessToken, _api.Url, "jdoe"), Token);
        Add(new Account(HostType.GitLabServer, AuthType.AccessToken, "https://gitlab.invalid", "lost"), token: null);
        _api.Map("GET", "/user/repos", r => Page(r, 75, 70, GitHubRepo));
        _api.Map("GET", "/api/v4/projects", r => Page(r, 35, 30, GitLabRepo));

        var vm = Open();
        Assert.True(vm.HasAccounts);
        Assert.Equal(["# GitHub (octo)", "! " + CloneViewModel.LoadingText, "# GitLab Server (jdoe)", "! " + CloneViewModel.LoadingText,
            "# GitLab Server (lost)", "! " + CloneViewModel.LoadingText], vm.Rows.Select(Text));

        await vm.Loading;
        var rows = vm.Rows.Select(Text).ToList();
        Assert.Equal(75 + 35, Repos(vm).Count);
        Assert.Equal(["# GitHub (octo)", "## even", "repo0", "repo2"], rows.Take(4));
        Assert.Equal(rows.IndexOf("## even") + 38 + 1, rows.IndexOf("## odd")); // owners in first-appearance order
        Assert.Equal(["# GitLab Server (jdoe)", "## Group", "Proj0"], rows.Skip(rows.IndexOf("# GitLab Server (jdoe)")).Take(3));
        Assert.Equal(["# GitLab Server (lost)", $"! {CloneViewModel.LoadErrorTitle}: {SecretStoreException.ReadFailed}"], rows.TakeLast(2));
        Assert.Equal(["1", "1", "2", "2"], _api.Requests.Select(r => r.QueryValue("page")!).Order());
        Assert.DoesNotContain(rows, r => r.Contains(Token) || r.Contains(GitHubToken));

        vm.Filter = "x";
        vm.Filter = "REPO7"; // the first keystroke's delay is superseded
        await vm.FilterTask;
        Assert.Equal(["repo70", "repo72", "repo74", "repo7", "repo71", "repo73"], Repos(vm).Select(r => r.Name)); // owners as first seen unfiltered
        Assert.Contains("# GitLab Server (jdoe)", vm.Rows.Select(Text));
        Assert.DoesNotContain("## Group", vm.Rows.Select(Text)); // no match, no owner header
        vm.Filter = "proj3";
        await vm.FilterTask;
        Assert.Equal(["Proj3", "Proj30", "Proj31", "Proj32", "Proj33", "Proj34"], Repos(vm).Select(r => r.Name));

        // A click on a row: its HTTPS clone URL; one account of the host, so no selector and no user name.
        var row = Repos(vm)[0];
        vm.ChooseRepository(row);
        Assert.Equal(("https://gl.example/g/proj3.git", "proj3"), (vm.Url, vm.Name));
        Assert.False(vm.HasAccountSelector);
        vm.Url = vm.ProtocolForms[1]; // switched to SSH, then the same row again
        Assert.Equal("SSH", vm.ProtocolText);
        vm.ChooseRepository(row);
        Assert.Equal("https://gl.example/g/proj3.git", vm.Url);
    }

    // ---------------------------------------------------------------- view and menu

    [AvaloniaFact]
    public async Task View_shows_the_form_and_the_project_list_in_the_dialog_frame()
    {
        AddGitHub("octo", GitHubToken);
        _api.Map("GET", "/user/repos", r => Page(r, 3, 70, GitHubRepo));
        var owner = new Window { Width = 900, Height = 700 };
        owner.Show();
        var vm = Open();
        await vm.Loading;
        DialogWindow d;
        Task<bool> shown;
        await owner.Clipboard!.SetTextAsync("git clone https://github.com/o/from-clipboard.git");
        try
        {
            shown = DialogWindow.ShowAsync(vm, owner);
            Settle(owner);
            d = (DialogWindow)owner.OwnedWindows.Single();
            Settle(d);
            Assert.Equal("https://github.com/o/from-clipboard.git", vm.Url); // read when the dialog opened
        }
        finally { await owner.Clipboard!.ClearAsync(); }
        vm.Url = "";
        Settle(d);
        Assert.Equal(("Clone", 590d), (d.Title, d.Width));
        Assert.True(Find<TextBox>(d, "UrlBox").IsFocused);
        Assert.False(Find<Button>(d, "ProtocolButton").IsVisible);
        Assert.False(Find<Button>(d, "TestButton").IsVisible);
        Assert.False(Find<ComboBox>(d, "AccountBox").IsVisible);
        Assert.True(Find<DockPanel>(d, "Projects").IsVisible);
        var recurse = Find<CheckBox>(d, "RecurseSubmodules");
        Assert.Equal((false, "Recurse submodules"), (recurse.IsChecked, recurse.Content));
        recurse.IsChecked = true;
        Assert.True(vm.RecurseSubmodules);
        Assert.Equal(6, Find<ItemsControl>(d, "ProjectList").GetRealizedContainers().Count()); // header, 2 owners, 3 repos

        // Clicking the same repository row twice, with a URL change in between, fills the URL both times.
        var repo0 = d.GetVisualDescendants().OfType<Button>().First(b => b.Name == "RepoButton");
        repo0.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("https://github.com/even/repo0.git", vm.Url);
        vm.Url = "https://github.com/edited/x.git";
        repo0.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("https://github.com/even/repo0.git", vm.Url);

        vm.Url = "https://github.com/o/r.git";
        Settle(d);
        Assert.True(Find<Button>(d, "ProtocolButton").IsVisible);
        Assert.True(Find<Button>(d, "TestButton").IsVisible);
        vm.Cancel();
        Dispatcher.UIThread.RunJobs();
        Assert.False(await shown);
        owner.Close();
    }

    [AvaloniaFact]
    public void File_menu_Clone_opens_the_dialog_of_the_app_service()
    {
        var saved = HostingService.Current;
        HostingService.Current = _hosting;
        var window = new GerGit.Views.MainWindow { DataContext = _main };
        try
        {
            window.Show();
            window.StatusTimer.Stop();
            Settle(window);
            var file = window.GetVisualDescendants().OfType<Menu>().Single().Items.OfType<MenuItem>().Single(m => (string?)m.Header == "_File");
            Assert.Equal(["Clone...", "-", "New Tab", "Open Repository..."], file.Items.OfType<Control>().Take(4).Select(c => c is MenuItem m ? (string)m.Header! : "-"));
            var clone = file.Items.OfType<MenuItem>().First();
            Assert.Equal("Ctrl+N", clone.InputGesture?.ToString());
            clone.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.IsType<CloneViewModel>(Assert.Single(_dialogs));
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
}
