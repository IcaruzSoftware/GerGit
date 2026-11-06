using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GerGit.Controls;
using GerGit.Core;
using GerGit.Core.Git;
using GerGit.Hosting;
using GerGit.Tests.Hosting;
using GerGit.ViewModels;
using GerGit.ViewModels.Dialogs;
using GerGit.ViewModels.Dialogs.Hosting;
using GerGit.ViewModels.Dialogs.Remote;
using GerGit.Views;
using GerGit.Views.Dialogs.Remote;

namespace GerGit.Tests.Ui;

/// <summary>Host icons per remote, the Create Pull / Merge Request menu items, dialog and REST round trip,
/// "Push and Create ..." over a smart-HTTP git server, "View on &lt;service&gt;". Fake GitHub / GitLab on 127.0.0.1, an
/// in-memory keyring, temp repositories; the tab gets its own <see cref="HostingService"/>. In the Askpass collection:
/// the push test sets the process-wide git hooks.</summary>
[Collection("Askpass")]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class PullRequestTests : IDisposable
{
    const string Token = "ghp_pr-token-4b8e1d07";

    readonly FakeHostingServer _api = new();
    readonly FakeSecretStore _secrets = new();
    readonly string _dir = Directory.CreateTempSubdirectory("gergit-pr-").FullName;
    readonly HostingService _hosting;
    readonly TempRepo _repo = new();
    readonly List<string> _opened = [];
    readonly List<DialogViewModel> _dialogs = [];
    readonly List<string> _errors = [];
    Func<DialogViewModel, Task>? _fill;
    RepoTabViewModel? _tab;

    public PullRequestTests()
    {
        _hosting = new HostingService(new AccountStore(Path.Combine(_dir, "accounts.json")), _secrets) { GitHubApiBase = _api.BaseUri };
        _hosting.OpenBrowser = url => { lock (_opened) _opened.Add(url); };
        _repo.Commit("first", 1, ("a.txt", "a\n"));
    }

    public void Dispose()
    {
        _tab?.Dispose();
        _api.Dispose();
        _repo.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    Account AddAccount(HostType type, string server, string user = "octo")
    {
        var account = new Account(type, AuthType.AccessToken, server, user);
        _secrets.Store(account.ServerUrl, user, account.SecretKind, Token);
        _hosting.Store.Add(account);
        return account;
    }

    /// <summary>Remote-tracking refs without a fetch: <c>refs/remotes/&lt;remote&gt;/&lt;b&gt;</c> at <paramref name="at"/>,
    /// and <c>&lt;remote&gt;/HEAD</c> → <paramref name="head"/>.</summary>
    void RemoteRefs(string remote, string at, string? head, params string[] branches)
    {
        foreach (var b in branches) _repo.Git("update-ref", $"refs/remotes/{remote}/{b}", at);
        if (head is not null) _repo.Git("symbolic-ref", $"refs/remotes/{remote}/HEAD", $"refs/remotes/{remote}/{head}");
    }

    void Track(string branch, string remote)
    {
        _repo.Git("config", $"branch.{branch}.remote", remote);
        _repo.Git("config", $"branch.{branch}.merge", "refs/heads/" + branch);
    }

    async Task<RepoTabViewModel> OpenAsync()
    {
        var tab = _tab = new RepoTabViewModel(await GitRepository.OpenAsync(_repo.Path)) { Hosting = _hosting };
        tab.ShowError = (_, text) => { _errors.Add(text); return Task.CompletedTask; };
        tab.ShowGitError = (r, _) => { _errors.Add(r.ReadableOutput); return Task.CompletedTask; };
        tab.ShowDialog = async vm =>
        {
            _dialogs.Add(vm);
            if (_fill is not null) await _fill(vm);
            return vm.Result == true;
        };
        await RefreshAsync();
        return tab;
    }

    async Task RefreshAsync()
    {
        await _tab!.RefreshAsync();
        while (!_tab.Refreshing.IsCompleted) await _tab.Refreshing;
        Dispatcher.UIThread.RunJobs();
    }

    GitRef Ref(string fullName) => _tab!.Refs.Refs.Single(r => r.FullName == fullName);

    static string Flat(IEnumerable<MenuEntry>? entries) => entries is null ? "<none>" : string.Join("|", entries.Select(e =>
        e.Kind == MenuEntryKind.Separator ? "-" : e.Header + (e.Children is { } c ? " {" + Flat(c) + "}" : "")));

    static Task<string> Copy(string _) => Task.FromResult("");

    static MenuEntry Find(IEnumerable<MenuEntry> entries, string header) =>
        entries.SelectMany(e => e.Children is { } c ? [e, .. c] : new[] { e }).First(e => e.Header == header);

    // ---------------------------------------------------------------- host icons

    [AvaloniaFact]
    public async Task Host_type_per_remote_follows_accounts()
    {
        _repo.Git("remote", "add", "gh", "git@github.com:owner/repo.git");
        _repo.Git("remote", "add", "gl", "https://gitlab.com/group/sub/repo.git");
        _repo.Git("remote", "add", "self", "https://git.example.org/team/repo.git");
        _repo.Git("remote", "add", "bb", "https://bitbucket.org/team/repo.git");
        _repo.Git("remote", "add", "path", "/srv/git/repo.git");
        var tab = await OpenAsync();
        Assert.Equal([("bb", HostType.Bitbucket), ("gh", HostType.GitHub), ("gl", HostType.GitLab), ("path", HostType.Custom), ("self", HostType.Custom)],
            tab.Sidebar.Roots.Single(r => r.Title == "Remotes").Children.Select(n => (n.Title, n.HostType)));
        Assert.Equal(HostType.Custom, tab.RemoteOps.HostOf("self"));

        // An account for the self-hosted server: the next refresh (the app refreshes on every account change) re-types it.
        var account = AddAccount(HostType.GitLabServer, "https://git.example.org");
        await RefreshAsync();
        Assert.Equal(HostType.GitLabServer, tab.Sidebar.Roots.Single(r => r.Title == "Remotes").Children.Single(n => n.Title == "self").HostType);
        Assert.Equal(account, tab.Hosted("self")!.Account);
        Assert.Equal(HostType.GitLabServer, tab.RemoteOps.HostOf("self"));

        // The sidebar row draws the host icon instead of the Lucide one.
        var view = new SidebarView { DataContext = tab.Sidebar };
        var window = new Window { Width = 300, Height = 600, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var icons = view.GetVisualDescendants().OfType<HostIcon>().Where(i => i.IsEffectivelyVisible).Select(i => i.Type).ToList();
        Assert.Equal([HostType.Bitbucket, HostType.GitHub, HostType.GitLab, HostType.Custom, HostType.GitLabServer], icons);
        window.Close();

        _hosting.Store.Remove(account);
        await RefreshAsync();
        Assert.Equal(HostType.Custom, tab.RemoteOps.HostOf("self"));
    }

    [AvaloniaFact]
    public async Task Commit_list_remote_label_icon_follows_accounts()
    {
        _repo.Git("remote", "add", "origin", "https://git.example.org/team/repo.git");
        RemoteRefs("origin", _repo.Git("rev-parse", "HEAD"), null, "main");
        var tab = await OpenAsync();
        var list = new CommitListView { DataContext = tab.CommitList };
        var window = new Window { Width = 1000, Height = 300, Content = list };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var panel = list.FindControl<CommitListPanel>("List")!;
        var label = tab.CommitList.Rows[0].Labels.Single(l => l.Kind == RefLabelKind.RemoteBranch);
        Assert.Equal(HostType.Custom, panel.RemoteHost(label));
        var before = Frame(window);

        // An account for the server: the refresh changes HostedRemotes, the panel redraws the label with the GitLab logo.
        AddAccount(HostType.GitLabServer, "https://git.example.org");
        await RefreshAsync();
        Assert.Equal(HostType.GitLabServer, panel.RemoteHost(label));
        Assert.NotEqual(before, Frame(window));
        window.Close();

        static byte[] Frame(Window w)
        {
            using var frame = w.CaptureRenderedFrame()!;
            using var stream = new MemoryStream();
            frame.Save(stream, PngBitmapEncoderOptions.Default);
            return stream.ToArray();
        }
    }

    [AvaloniaFact]
    public async Task Load_failure_shows_a_warning_and_keeps_the_dialog_usable()
    {
        AddAccount(HostType.GitHub, _api.Url);
        _repo.Git("remote", "add", "origin", _api.Url + "/owner/repo.git");
        RemoteRefs("origin", _repo.Git("rev-parse", "HEAD"), null, "main", "feature");
        var tab = await OpenAsync();
        var d = new CreatePullRequestViewModel(tab.RemoteOps, tab.Hosted("origin")!, "feature", new string('0', 40)); // no such commit
        await d.Loaded;
        Assert.Equal(DialogStatus.Warning, d.Status);
        Assert.NotEmpty(d.StatusText);
        Assert.Equal("main", d.SelectedTarget);
        d.RequestTitle = "Typed";
        Assert.True(d.CanSubmit);
    }

    [AvaloniaFact]
    public async Task Remote_drop_downs_show_the_host_icon()
    {
        _repo.Git("remote", "add", "origin", "https://github.com/owner/repo.git");
        _repo.Git("remote", "add", "lab", "https://gitlab.com/owner/repo.git");
        var tab = await OpenAsync();
        var view = new PushView { DataContext = new PushViewModel(tab.RemoteOps, "main", "lab") };
        var window = new Window { Width = 500, Height = 300, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var combo = view.FindControl<ComboBox>("Remote")!;
        Assert.Equal(HostType.GitLab, combo.GetVisualDescendants().OfType<RefView>().Single().ShownHost); // the selection box

        combo.IsDropDownOpen = true; // the popup items inherit the lookup too
        Dispatcher.UIThread.RunJobs();
        var items = Enumerable.Range(0, 2).Select(i => combo.ContainerFromIndex(i)!.GetVisualDescendants().OfType<RefView>().Single())
            .Select(r => (r.Text, r.ShownHost)).ToList();
        Assert.Equal([("lab", (HostType?)HostType.GitLab), ("origin", HostType.GitHub)], items);
        combo.IsDropDownOpen = false;
        window.Close();
    }

    // ---------------------------------------------------------------- menus

    [AvaloniaFact]
    public async Task Menu_items_for_one_and_several_remotes()
    {
        _repo.Git("branch", "feature");
        _repo.Git("remote", "add", "origin", "https://github.com/owner/repo.git");
        var first = _repo.Git("rev-parse", "HEAD");
        RemoteRefs("origin", first, "main", "main", "feature");
        Track("main", "origin");
        Track("feature", "origin");
        _repo.Commit("second", 2); // main strictly ahead, feature equal to its upstream
        var tab = await OpenAsync();

        // One remote: right after "Push to ...", "Push and Create" when ahead (or without upstream), else "Create".
        Assert.Contains("|Pull 'origin/main'...|Push to 'origin'...|Push and Create Pull Request on 'origin'|-|",
            Flat(Menus.LocalBranch(tab, Ref("refs/heads/main"), Copy)));
        Assert.Contains("|Fast-Forward to 'origin/feature'|Push to 'origin'...|Create Pull Request on 'origin'|-|",
            Flat(Menus.LocalBranch(tab, Ref("refs/heads/feature"), Copy)));
        _repo.Git("branch", "fresh");
        await RefreshAsync();
        Assert.Contains("|Push to 'origin'...|Push and Create Pull Request on 'origin'|-|", Flat(Menus.LocalBranch(tab, Ref("refs/heads/fresh"), Copy)));
        Assert.Contains("|Interactively Rebase 'main' on 'origin/feature...|-|Create Pull Request on 'origin'|New Branch...|",
            Flat(Menus.RemoteBranch(tab, Ref("refs/remotes/origin/feature"), Copy)));

        // Commit list branch submenus.
        await WaitForRow(tab, first);
        tab.CommitList.SelectCommit(first);
        var commitMenu = Flat(Menus.CommitList(tab.CommitList));
        Assert.Contains("feature {feature|-|Checkout|-|Fast-Forward to 'origin/feature'|Push to 'origin'...|Create Pull Request on 'origin'|-|", commitMenu);
        Assert.Contains("origin/feature {origin/feature|-|Checkout|Create Pull Request on 'origin'|-|", commitMenu);

        // Several remotes: a "Create Pull Request" submenu with the remotes that offer one (GitLab: Merge Request).
        _repo.Git("remote", "add", "lab", "https://gitlab.com/owner/repo.git");
        _repo.Git("remote", "add", "plain", "/srv/git/repo.git");
        RemoteRefs("plain", first, null, "feature");
        await RefreshAsync();
        Assert.Contains("|Push {Push to 'lab'...|Push to 'origin'...|Push to 'plain'...}" +
                        "|Create Pull Request {Create Merge Request on 'lab'|Create Pull Request on 'origin'}|-|",
            Flat(Menus.LocalBranch(tab, Ref("refs/heads/feature"), Copy)));
        Assert.Contains("|Create Pull Request {Push and Create Merge Request on 'lab'|Push and Create Pull Request on 'origin'}|",
            Flat(Menus.LocalBranch(tab, Ref("refs/heads/main"), Copy)));
        Assert.DoesNotContain("Create", Flat(Menus.RemoteBranch(tab, Ref("refs/remotes/plain/feature"), Copy)));
        tab.CommitList.SelectCommit(first);
        Assert.Contains("|Push... {Push to 'lab'...|Push to 'origin'...|Push to 'plain'...}|Create Pull Request {Create Merge Request on 'lab'|",
            Flat(Menus.CommitList(tab.CommitList)));

        // Only custom remotes: no pull request items at all.
        _repo.Git("remote", "remove", "origin");
        _repo.Git("remote", "remove", "lab");
        await RefreshAsync();
        Assert.DoesNotContain("Create", Flat(Menus.LocalBranch(tab, Ref("refs/heads/feature"), Copy)));
        Assert.Empty(_errors);
    }

    static async Task WaitForRow(RepoTabViewModel tab, string sha)
    {
        var until = DateTime.UtcNow.AddSeconds(15);
        while (tab.CommitList.IndexOf(sha) < 0)
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("row not loaded");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
    }

    [AvaloniaFact]
    public async Task Without_an_account_the_host_rule_opens_the_new_request_page_and_View_on_the_repository()
    {
        _repo.Git("branch", "feature/x");
        _repo.Git("remote", "add", "origin", "https://github.com/owner/repo.git");
        _repo.Git("remote", "add", "lab", "git@gitlab.com:group/sub/repo.git");
        var head = _repo.Git("rev-parse", "HEAD");
        RemoteRefs("origin", head, null, "feature/x");
        RemoteRefs("lab", head, null, "feature/x");
        var tab = await OpenAsync();

        await Find(Menus.RemoteBranch(tab, Ref("refs/remotes/origin/feature/x"), Copy), "Create Pull Request on 'origin'").Invoke();
        await Find(Menus.RemoteBranch(tab, Ref("refs/remotes/lab/feature/x"), Copy), "Create Merge Request on 'lab'").Invoke();
        var origin = await Menus.Remote(tab, "origin", Copy);
        Assert.Equal("Fetch 'origin'...|-|Edit 'origin'...|Delete 'origin'...|-|Fetch 'origin' Automatically|-|View on GitHub" +
                     "|-|Add New Remote...|-|Copy Remote Address", Flat(origin));
        await Find(origin, "View on GitHub").Invoke();
        await Find(await Menus.Remote(tab, "lab", Copy), "View on GitLab").Invoke();
        Assert.Equal([
            "https://github.com/owner/repo/compare/feature%2Fx?expand=1",
            "https://gitlab.com/group/sub/repo/-/merge_requests/new?&merge_request%5Bsource_branch%5D=feature%2Fx",
            "https://github.com/owner/repo",
            "https://gitlab.com/group/sub/repo",
        ], _opened);
        Assert.Empty(_dialogs);

        // A custom remote has no "View on".
        _repo.Git("remote", "add", "plain", "/srv/git/repo.git");
        await RefreshAsync();
        Assert.DoesNotContain("View on", Flat(await Menus.Remote(tab, "plain", Copy)));
    }

    // ---------------------------------------------------------------- dialog and REST

    [AvaloniaFact]
    public async Task GitHub_round_trip_through_the_dialog()
    {
        AddAccount(HostType.GitHub, _api.Url);
        _api.Map("POST", "/repos/owner/repo/pulls", FakeResponse.Json(new { number = 7, html_url = "https://gh.test/owner/repo/pull/7" }, 201));
        _repo.Commit("Add the feature", 2);
        _repo.Git("branch", "feature");
        _repo.Git("remote", "add", "origin", _api.Url + "/owner/repo.git"); // http://127.0.0.1:<port>: the account's host
        var head = _repo.Git("rev-parse", "HEAD");
        RemoteRefs("origin", head, "develop", "main", "develop", "feature");
        Track("feature", "origin");
        var tab = await OpenAsync();
        Assert.Equal(HostType.GitHub, tab.RemoteOps.HostOf("origin"));
        Assert.Contains("|View on GitHub|", Flat(await Menus.Remote(tab, "origin", Copy)));

        CreatePullRequestViewModel? dialog = null;
        _fill = async vm =>
        {
            dialog = Assert.IsType<CreatePullRequestViewModel>(vm);
            await dialog.Loaded;
            Assert.Equal(("Create Pull Request", "Create", "feature"), (vm.Title, vm.SubmitText, dialog.Source));
            Assert.Equal("Add the feature", dialog.RequestTitle);
            Assert.Equal(["develop", "feature", "main"], dialog.Targets);
            Assert.Equal("develop", dialog.SelectedTarget); // the remote's HEAD
            var view = new GerGit.Views.Dialogs.Hosting.CreatePullRequestView { DataContext = dialog };
            var window = new Window { Width = 560, Height = 400, Content = view };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(("feature", "Add the feature", "develop"),
                (view.FindControl<RefView>("Source")!.Text, view.FindControl<TextBox>("TitleBox")!.Text, view.FindControl<ComboBox>("Target")!.SelectedItem));
            window.Close();
            Assert.True(dialog.CanSubmit);
            dialog.SelectedTarget = "feature";
            Assert.False(dialog.CanSubmit); // target = source
            dialog.SelectedTarget = "main";
            dialog.RequestTitle = "  ";
            Assert.False(dialog.CanSubmit);
            dialog.RequestTitle = "My PR";
            dialog.Body = "Line 1\nLine 2";
            await dialog.SubmitAsync();
        };
        await Find(Menus.LocalBranch(tab, Ref("refs/heads/feature"), Copy), "Create Pull Request on 'origin'").Invoke();

        Assert.True(dialog!.Result);
        var r = Assert.Single(_api.Requests);
        Assert.Equal("token " + Token, r.Header("Authorization"));
        var body = JsonNode.Parse(r.Body)!;
        Assert.Equal(("My PR", "Line 1\nLine 2", "feature", "main"),
            ((string)body["title"]!, (string)body["body"]!, (string)body["head"]!, (string)body["base"]!));
        Assert.Equal(["https://gh.test/owner/repo/pull/7"], _opened);
    }

    [AvaloniaFact]
    public async Task GitLab_merge_request_round_trip()
    {
        AddAccount(HostType.GitLabServer, _api.Url, "gluser");
        _api.Map("POST", "/api/v4/projects/group%2Fsub%2Frepo/merge_requests",
            FakeResponse.Json(new { iid = 3, web_url = _api.Url + "/group/sub/repo/-/merge_requests/3" }, 201));
        _repo.Git("branch", "topic");
        _repo.Git("remote", "add", "origin", _api.Url + "/group/sub/repo.git");
        var head = _repo.Git("rev-parse", "HEAD");
        RemoteRefs("origin", head, null, "master", "main", "topic");
        var tab = await OpenAsync();

        var origin = await Menus.Remote(tab, "origin", Copy);
        await Find(origin, "View on GitLab Server").Invoke();
        _fill = async vm =>
        {
            var d = Assert.IsType<CreatePullRequestViewModel>(vm);
            await d.Loaded;
            Assert.Equal("Create Merge Request", vm.Title);
            Assert.Equal(("topic", "first", "main"), (d.Source, d.RequestTitle, d.SelectedTarget)); // no HEAD known: main
            d.Body = "Why";
            await d.SubmitAsync();
        };
        await Find(Menus.RemoteBranch(tab, Ref("refs/remotes/origin/topic"), Copy), "Create Merge Request on 'origin'").Invoke();

        var r = Assert.Single(_api.Requests);
        Assert.Equal(Token, r.Header("Private-Token"));
        var body = JsonNode.Parse(r.Body)!;
        Assert.Equal(("first", "Why", "topic", "main"),
            ((string)body["title"]!, (string)body["description"]!, (string)body["source_branch"]!, (string)body["target_branch"]!));
        Assert.Equal([_api.Url + "/group/sub/repo", _api.Url + "/group/sub/repo/-/merge_requests/3"], _opened);
    }

    [AvaloniaFact]
    public async Task Errors_stay_in_the_status_line_without_the_token()
    {
        AddAccount(HostType.GitHub, _api.Url);
        var status = 422;
        _api.Map("POST", "/repos/owner/repo/pulls", _ => status == 422
            ? FakeResponse.Json(new { message = "Validation Failed", errors = new[] { new { message = "A pull request already exists for owner:feature." } } }, 422)
            : FakeResponse.Json(new { message = "Bad credentials: " + Token }, 401));
        _repo.Git("branch", "feature");
        _repo.Git("remote", "add", "origin", _api.Url + "/owner/repo.git");
        RemoteRefs("origin", _repo.Git("rev-parse", "HEAD"), "main", "main", "feature");
        Track("feature", "origin");
        var tab = await OpenAsync();

        var texts = new List<string>();
        _fill = async vm =>
        {
            var d = (CreatePullRequestViewModel)vm;
            await d.Loaded;
            await d.SubmitAsync();
            Assert.Null(d.Result); // stays open
            Assert.Equal(DialogStatus.Error, d.Status);
            Assert.True(d.CanSubmit && !d.IsRunning);
            texts.Add(d.StatusText);
            status = 401;
            await d.SubmitAsync();
            texts.Add(d.StatusText);
            d.Cancel();
        };
        await Find(Menus.LocalBranch(tab, Ref("refs/heads/feature"), Copy), "Create Pull Request on 'origin'").Invoke();
        Assert.Equal(["GitHub Error: Validation Failed: A pull request already exists for owner:feature.", "GitHub Error: Bad credentials: ***"], texts);
        Assert.Empty(_opened);

        // No token in the keyring: the keyring text, no call.
        _secrets.Clear(_api.Url, "octo");
        _fill = async vm =>
        {
            var d = (CreatePullRequestViewModel)vm;
            await d.Loaded;
            await d.SubmitAsync();
            Assert.Equal(SecretStoreException.ReadFailed, d.StatusText);
            d.Cancel();
        };
        await Find(Menus.LocalBranch(tab, Ref("refs/heads/feature"), Copy), "Create Pull Request on 'origin'").Invoke();
        Assert.Equal(2, _api.Requests.Count);
    }

    [Fact]
    public async Task Short_tokens_are_not_scrubbed_from_error_texts()
    {
        _api.Map("GET", "/user", FakeResponse.Json(new { message = "Bad credentials" }, 401));
        var e = await Assert.ThrowsAsync<HostingException>(() => new GitHubClient("a") { ApiBase = _api.BaseUri }.GetUserAsync());
        Assert.Equal("GitHub Error: Bad credentials", e.Message); // a 1-character "token" must not turn it into "B***d ..."
    }

    // ---------------------------------------------------------------- push and create

    [AvaloniaFact]
    public async Task Push_and_create_pushes_with_the_account_token_then_opens_the_dialog()
    {
        using var git = new GitHttpServer("octo", Token);
        git.AddRepo("owner/repo", _repo.Path);
        var gitServer = git.Url.TrimEnd('/');
        AddAccount(HostType.GitHub, gitServer); // the git host; the REST calls go to the fake API base
        _api.Map("POST", "/repos/owner/repo/pulls", FakeResponse.Json(new { number = 1, html_url = "https://gh.test/owner/repo/pull/1" }, 201));
        _repo.Git("remote", "add", "origin", git.Url + "owner/repo.git");
        RemoteRefs("origin", _repo.Git("rev-parse", "HEAD"), null, "main");
        _repo.Git("checkout", "-q", "-b", "feature");
        var tip = _repo.Commit("New work", 3);

        var (prompt, lookup, count, url) = (Askpass.PromptCredentials, Askpass.LookupCredential, GitErrors.GitHubAccountCount, GitErrors.GitHubAccessUrl);
        Askpass.Start();
        Askpass.PromptCredentials = _ => Task.FromResult<string?>(null);
        _hosting.Install(); // credential helper on, lookup through the accounts
        try
        {
            var tab = await OpenAsync();
            _fill = async vm =>
            {
                var d = Assert.IsType<CreatePullRequestViewModel>(vm);
                await d.Loaded;
                Assert.Equal(("feature", "New work", "main"), (d.Source, d.RequestTitle, d.SelectedTarget));
                await d.SubmitAsync();
            };
            await Find(Menus.LocalBranch(tab, Ref("refs/heads/feature"), Copy), "Push and Create Pull Request on 'origin'").Invoke();

            Assert.Empty(_errors);
            Assert.Equal(tip, _repo.Git("--git-dir", Path.Combine(git.Root, "owner/repo.git"), "rev-parse", "refs/heads/feature"));
            Assert.Equal("origin", _repo.Git("config", "branch.feature.remote")); // tracked: it had no upstream
            Assert.Equal(tip, tab.CommitList.SelectedRows.Single().Sha);       // the refresh selected HEAD
            var body = JsonNode.Parse(Assert.Single(_api.Requests).Body)!;
            Assert.Equal(("feature", "main"), ((string)body["head"]!, (string)body["base"]!));
            Assert.Equal(["https://gh.test/owner/repo/pull/1"], _opened);

            // A failing push shows the error and stops: no dialog.
            _dialogs.Clear();
            _secrets.Clear(gitServer, "octo");
            _repo.Commit("More", 4);
            await RefreshAsync();
            await Find(Menus.LocalBranch(tab, Ref("refs/heads/feature"), Copy), "Push and Create Pull Request on 'origin'").Invoke();
            Assert.Single(_errors);
            Assert.DoesNotContain(Token, _errors[0]);
            Assert.Empty(_dialogs);
        }
        finally
        {
            GitRunner.CredentialHelperEnabled = false;
            (Askpass.PromptCredentials, Askpass.LookupCredential) = (prompt, lookup);
            (GitErrors.GitHubAccountCount, GitErrors.GitHubAccessUrl) = (count, url);
            Askpass.Stop();
        }
    }
}
