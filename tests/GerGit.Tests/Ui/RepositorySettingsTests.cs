using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GerGit.Controls;
using GerGit.Core;
using GerGit.Core.Git;
using GerGit.Hosting;
using GerGit.Tests.Hosting;
using GerGit.ViewModels;
using GerGit.ViewModels.Dialogs;
using GerGit.ViewModels.Preferences;
using GerGit.Views;
using GerGit.Views.Preferences;

namespace GerGit.Tests.Ui;

/// <summary>The Repository Settings window, its per-repository storage, issue tracker
/// and URL links in commit messages and the output view, the http(s)-only browser, and the tooltip
/// for trimmed text. Real temporary repositories; the global switch is restored after each test.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class RepositorySettingsTests : IDisposable
{
    readonly TempRepo _repo = new();
    readonly List<DialogViewModel> _dialogs = [];
    readonly List<string> _opened = [];
    readonly Action<string> _savedOpen = Hyperlinks.Open;
    bool _answer = true;

    public RepositorySettingsTests()
    {
        Settings.Set<bool?>(IssueLinks.GlobalKey, null);
        Hyperlinks.Open = _opened.Add;
    }

    public void Dispose()
    {
        Settings.Set<bool?>(IssueLinks.GlobalKey, null);
        Hyperlinks.Open = _savedOpen;
        _repo.Dispose();
    }

    async Task<RepoInfo> InfoAsync() => (await GitRepository.OpenAsync(_repo.Path)).Info;

    async Task<RepositorySettingsViewModel> NewVmAsync()
    {
        var vm = new RepositorySettingsViewModel(await InfoAsync(), "my-repo")
        {
            ShowDialog = d => { _dialogs.Add(d); return Task.FromResult(_answer); },
        };
        await vm.Loading;
        return vm;
    }

    static void Settle(Window w)
    {
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    // ------------------------------------------------------------------ Window and storage

    [AvaloniaFact]
    public async Task Window_has_general_and_issue_tracker_pages_and_a_single_close_button()
    {
        var vm = await NewVmAsync();
        Assert.Equal(("Repository Settings", null, "Close", true, true), (vm.Title, vm.SubmitText, vm.CancelText, vm.CancelIsDefault, vm.PlainFrame));
        var window = new Window { Content = new RepositorySettingsView { DataContext = vm }, Width = 700, Height = 500 };
        window.Show();
        try
        {
            Settle(window);
            var tabs = window.GetVisualDescendants().OfType<TabControl>().Single();
            Assert.Equal(["General", "Issue Tracker"], tabs.Items.OfType<TabItem>().Select(t => (string)t.Header!));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task A_large_hostile_rule_file_opens_fast_shows_100_rules_and_keeps_the_rest()
    {
        // About 1000 rules with patterns that are costly to build; none of them is compiled while the window opens (checked
        // directly instead of with a time bound, which could flake under load).
        var heavy = string.Concat(Enumerable.Range(0, 1000).Select(i => $"[issuetracker \"r{i}\"]\n  regex = \"(a{{1,30}}){{1,30}}x{i}\"\n  url = \"https://h/$1\"\n"));
        _repo.Write(".issuetracker", heavy[..Math.Min(heavy.Length, 60_000)]);
        var info = await InfoAsync();
        int inFile = IssueTracker.ReadFile(info, IssueRuleLevel.Shared).Rules.Count;
        Assert.True(inFile > IssueLinks.MaxRules);
        var vm = new RepositorySettingsViewModel(info, "my-repo") { ShowDialog = d => { _dialogs.Add(d); return Task.FromResult(_answer); } };
        Assert.Equal(IssueLinks.MaxRules, vm.Rules.Count);
        Assert.DoesNotContain(vm.Rules, r => r.IsCompiled);
        Assert.True(vm.Rules[0].IsValid); // the selected rule's editor compiles its own pattern only
        Assert.True(vm.Rules[0].IsCompiled);
        Assert.False(vm.Rules[1].IsCompiled);

        vm.Rules[0].Name = "changed"; // the shared file is rewritten: the rules beyond 100 are kept, unchanged
        await vm.SaveAsync();
        var saved = IssueTracker.ReadFile(info, IssueRuleLevel.Shared).Rules;
        Assert.Equal((inFile, "changed", "r" + (inFile - 1)), (saved.Count, saved[0].Name, saved[^1].Name));
    }

    [AvaloniaFact]
    public async Task A_worktree_config_identity_is_the_repositorys_own_and_its_survival_is_reported()
    {
        _repo.Git("config", "extensions.worktreeConfig", "true");
        _repo.Git("config", "--worktree", "user.name", "Worktree Name");
        var vm = await NewVmAsync();
        Assert.Equal((false, true, "Worktree Name"), (vm.UseGlobalIdentity, vm.CanToggleIdentity, vm.UserName)); // own, not global
        vm.UseGlobalIdentity = true;
        await vm.SaveAsync(); // Remove is confirmed; `--local --unset-all` cannot reach the worktree config
        Assert.Equal("Remove this repository's own identity?", _dialogs[0].Title);
        Assert.Contains("user.name 'Worktree Name'", _dialogs[0].Description);
        Assert.Equal("This repository still has its own identity", _dialogs[1].Title);
        Assert.Contains("user.name 'Worktree Name' (worktree config)", _dialogs[1].Description);
        Assert.Equal((false, "Worktree Name"), (vm.UseGlobalIdentity, vm.UserName));
        Assert.Equal("Worktree Name", _repo.Git("config", "--worktree", "user.name"));
    }

    [AvaloniaFact]
    public async Task Opening_and_closing_never_rewrites_a_rule_file_with_a_blank_rule_name()
    {
        var info = await InfoAsync();
        var shared = Path.Combine(_repo.Path, ".issuetracker");
        File.WriteAllText(shared, "[issuetracker \" \"]\n  regex = \"#(\\\\d+)\"\n  url = \"https://i/$1\"\n");
        var bytes = File.ReadAllBytes(shared);
        var time = File.GetLastWriteTimeUtc(shared) - TimeSpan.FromMinutes(5);
        File.SetLastWriteTimeUtc(shared, time);
        var vm = await NewVmAsync();
        Assert.Equal(" ", Assert.Single(vm.Rules).Name);
        await vm.SaveAsync();
        Assert.Equal(bytes, File.ReadAllBytes(shared));
        Assert.Equal(time, File.GetLastWriteTimeUtc(shared)); // not rewritten

        vm.Rules[0].Name = "  "; // edited by the user: it gets the default name
        await vm.SaveAsync();
        Assert.Equal(IssueRuleItem.DefaultName, IssueTracker.Read(info).Single().Name);
    }

    [AvaloniaFact]
    public async Task An_included_identity_that_survives_the_removal_is_reported_and_stays_unticked()
    {
        var info = await InfoAsync();
        File.WriteAllText(Path.Combine(info.CommonDir, "extra.cfg"), "[user]\n\tname = Included Name\n");
        _repo.Git("config", "--local", "include.path", "extra.cfg");
        var vm = await NewVmAsync();
        Assert.Equal((false, "Included Name"), (vm.UseGlobalIdentity, vm.UserName)); // the include comes after: it wins
        vm.UseGlobalIdentity = true;
        await vm.SaveAsync(); // Remove is confirmed
        Assert.False(HasLocal("user.email"));
        Assert.Equal(2, _dialogs.Count);
        Assert.Equal("This repository still has its own identity", _dialogs[1].Title);
        Assert.Contains("user.name 'Included Name' (local config)", _dialogs[1].Description);
        Assert.Equal((false, "Included Name"), (vm.UseGlobalIdentity, vm.UserName));
    }

    [AvaloniaFact]
    public async Task Identity_toggle_is_a_pending_edit_and_removing_the_own_identity_asks_first()
    {
        var vm = await NewVmAsync(); // TempRepo sets user.name / user.email in the repository's own config
        Assert.Equal((false, true, true, "Test User", "test@example.com"),
            (vm.UseGlobalIdentity, vm.CanToggleIdentity, vm.IdentityEditable, vm.UserName, vm.UserEmail));

        // Ticking only changes the window: the boxes show the global identity (read-only), nothing is written yet.
        vm.UseGlobalIdentity = true;
        Assert.False(vm.IdentityEditable);
        Assert.True(HasLocal("user.name") && HasLocal("user.email"));
        vm.UseGlobalIdentity = false; // unticking in the same window restores the repository's own values
        Assert.Equal(("Test User", "test@example.com"), (vm.UserName, vm.UserEmail));
        vm.UserName = "Edited";
        vm.UseGlobalIdentity = true;
        vm.UseGlobalIdentity = false;
        Assert.Equal("Edited", vm.UserName); // an edit survives a toggle too
        vm.UserName = "Test User";

        // On close with the box ticked: a confirmation first; Cancel keeps the repository's own identity.
        vm.UseGlobalIdentity = true;
        _answer = false;
        await vm.SaveAsync();
        var confirm = Assert.Single(_dialogs);
        Assert.Equal(("Remove this repository's own identity?", "Remove", "Cancel"), (confirm.Title, confirm.SubmitText, confirm.CancelText));
        Assert.Contains("user.name 'Test User' and user.email 'test@example.com'", confirm.Description);
        Assert.Contains("global identity", confirm.Description);
        Assert.True(HasLocal("user.name") && HasLocal("user.email"));

        // Remove: both keys go, and the removed values are logged.
        _answer = true;
        using var trace = new GerGit.Tests.Core.TraceCapture();
        await vm.SaveAsync();
        Assert.False(HasLocal("user.name") || HasLocal("user.email"));
        Assert.Contains(trace.Lines(_repo.Path), l => l.Contains("user.name 'Test User', user.email 'test@example.com'"));
        Assert.Equal(2, _dialogs.Count);
    }

    [AvaloniaFact]
    public async Task Identity_boxes_are_written_on_close_trimmed_and_literal()
    {
        var vm = await NewVmAsync();
        vm.UserName = "-Edited";
        vm.UserEmail = "  repo@example.com ";
        await vm.SaveAsync();
        Assert.Equal(("-Edited", "repo@example.com"), (_repo.Git("config", "--local", "user.name"), _repo.Git("config", "--local", "user.email")));
        Assert.Empty(_dialogs);

        // Name from the repository, e-mail from elsewhere: mixed scopes disable the check box and nothing is written.
        _repo.Git("config", "--local", "--unset", "user.email");
        var mixed = await NewVmAsync();
        Assert.Equal((false, false, false), (mixed.UseGlobalIdentity, mixed.CanToggleIdentity, mixed.IdentityEditable));
        await mixed.SaveAsync();
        Assert.False(HasLocal("user.email"));
        Assert.Empty(_dialogs);
    }

    [AvaloniaFact]
    public async Task Fast_toggles_and_overlapping_saves_do_not_race()
    {
        var vm = await NewVmAsync();
        for (int i = 0; i < 51; i++) vm.UseGlobalIdentity = !vm.UseGlobalIdentity; // ends ticked; no git call yet
        Assert.True(HasLocal("user.name"));
        await Task.WhenAll(vm.SaveAsync(), vm.SaveAsync()); // one after the other: one confirmation, one removal
        Assert.Single(_dialogs);
        Assert.False(HasLocal("user.name") || HasLocal("user.email"));

        for (int i = 0; i < 51; i++) vm.UseGlobalIdentity = !vm.UseGlobalIdentity; // ends unticked: the own values again
        await Task.WhenAll(vm.SaveAsync(), vm.SaveAsync());
        Assert.Equal(("Test User", "test@example.com"), (_repo.Git("config", "--local", "user.name"), _repo.Git("config", "--local", "user.email")));
        Assert.Single(_dialogs); // no Git Error either
    }

    bool HasLocal(string key)
    {
        try { return _repo.Git("config", "--local", "--get", key).Length > 0; }
        catch (InvalidOperationException) { return false; }
    }

    [AvaloniaFact]
    public async Task Repository_switch_is_stored_in_the_git_dir_settings_file_and_turns_the_rules_off()
    {
        var info = await InfoAsync();
        File.WriteAllText(Path.Combine(info.CommonDir, "issuetracker"), "[issuetracker \"L\"]\n  regex = \"#(\\\\d+)\"\n  url = \"https://l/$1\"\n");
        Assert.Single(IssueLinks.LoadRules(info));
        var vm = await NewVmAsync();
        Assert.Equal((true, true, false), (vm.RepoIssueLinks, vm.ShowRules, vm.ShowRepoDisabled));

        vm.RepoIssueLinks = false;
        var file = Path.Combine(info.CommonDir, "gergit", "settings.json");
        Assert.Equal(file, RepoSettings.FilePath(info.CommonDir));
        Assert.Contains("\"issueLinks\": false", File.ReadAllText(file));
        Assert.Equal((false, true), (vm.ShowRules, vm.ShowRepoDisabled));
        Assert.Equal("Issue tracker integration for 'my-repo' is disabled", vm.RepoDisabledText);
        Assert.False(IssueLinks.RepoEnabled(info));
        Assert.Empty(IssueLinks.LoadRules(info));

        vm.RepoIssueLinks = true; // the default is not stored; other keys stay
        RepoSettings.Set(info.CommonDir, "unknown.key", 5);
        Assert.True(IssueLinks.RepoEnabled(info));
        Assert.Equal(5, RepoSettings.Get(info.CommonDir, "unknown.key", 0));
        Assert.DoesNotContain("issueLinks", File.ReadAllText(file));

        // The global preference off: no rules, and the page shows the "disabled" panel. Restored before the next await.
        RepositorySettingsViewModel off;
        IssueLinks.GlobalEnabled = false;
        try
        {
            Assert.Empty(IssueLinks.LoadRules(info));
            off = new RepositorySettingsViewModel(info, "my-repo");
        }
        finally { Settings.Set<bool?>(IssueLinks.GlobalKey, null); }
        Assert.Equal((false, false, false), (off.GlobalIssueLinks, off.ShowRules, off.ShowRepoDisabled));
        await off.Loading;
    }

    [AvaloniaFact]
    public async Task Linked_worktrees_share_the_settings_and_local_rules_of_the_repository()
    {
        _repo.Commit("one", 1, ("a.txt", "a"));
        var wtPath = _repo.Path + "-wt";
        _repo.Git("worktree", "add", "-q", wtPath);
        try
        {
            var main = await InfoAsync();
            var wt = (await GitRepository.OpenAsync(wtPath)).Info;
            Assert.NotEqual(main.GitDir, wt.GitDir);
            Assert.Equal(main.CommonDir, wt.CommonDir);
            Assert.Equal(IssueTracker.FileOf(main, IssueRuleLevel.Local), IssueTracker.FileOf(wt, IssueRuleLevel.Local));
            RepoSettings.Set(main.CommonDir, IssueLinks.RepoKey, false);
            Assert.False(IssueLinks.RepoEnabled(wt));
        }
        finally { Directory.Delete(wtPath, recursive: true); }
    }

    [AvaloniaFact]
    public async Task A_linked_gergit_folder_is_never_written_through()
    {
        var info = await InfoAsync();
        var outside = Directory.CreateTempSubdirectory("gergit-outside-").FullName;
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(info.CommonDir, "gergit"), outside);
            RepoSettings.Set(info.CommonDir, IssueLinks.RepoKey, false);
            Assert.Empty(Directory.GetFileSystemEntries(outside));
            Assert.True(IssueLinks.RepoEnabled(info)); // nothing was stored
        }
        finally { Directory.Delete(outside, recursive: true); }
    }

    [AvaloniaFact]
    public async Task Rules_are_listed_added_edited_removed_and_saved_to_their_files_on_close()
    {
        var info = await InfoAsync();
        var shared = Path.Combine(_repo.Path, ".issuetracker");
        var sharedText = "# my own comment\n[issuetracker \"Shared\"]\n  regex = \"S-(\\\\d+)\"\n  url = \"https://s/$1\"\n";
        File.WriteAllText(shared, sharedText);
        File.WriteAllText(Path.Combine(info.CommonDir, "issuetracker"), "[issuetracker \"Old\"]\n  regex = \"O\"\n  url = \"https://o\"\n");
        var vm = await NewVmAsync();
        Assert.Equal(["Shared:Shared", "Old:Local"], vm.Rules.Select(r => $"{r.Name}:{r.LevelText}"));
        Assert.Same(vm.Rules[0], vm.SelectedRule);
        Assert.Equal(".issuetracker", vm.Rules[0].FileText);

        vm.AddRule("github");
        var gh = vm.SelectedRule!;
        Assert.Equal(("Sample GitHub Rule", "Local", @"#(\d+)", "https://github.com/username/repository/issues/$1", ".git/issuetracker", true),
            (gh.Name, gh.LevelText, gh.Regex, gh.Url, gh.FileText, gh.IsValid));
        Assert.Equal(IssueRuleItem.DefaultTestMessage, gh.TestMessage);
        Assert.Equal([new TextLink(42, 6, "https://github.com/username/repository/issues/12337")], gh.Preview.Links);
        vm.AddRule("jiraMulti");
        Assert.Equal(["PRJ1-28", "PRJ2-45"], vm.SelectedRule!.Preview.Links.Select(l => vm.SelectedRule.TestMessage.Substring(l.Start, l.Length)));
        vm.AddRule("new");
        Assert.Equal(("Issue Tracker Rule", "", false, true), (vm.SelectedRule!.Name, vm.SelectedRule.Regex, vm.SelectedRule.IsValid, vm.SelectedRule.IsInvalid));
        vm.SelectedRule.Regex = "(broken";
        Assert.False(vm.SelectedRule.IsValid);
        Assert.Empty(vm.SelectedRule.Preview.Links);

        // Remove asks first; Cancel keeps it, Remove selects the previous rule.
        _answer = false;
        await vm.RemoveRuleAsync();
        Assert.Equal(5, vm.Rules.Count);
        _answer = true;
        await vm.RemoveRuleAsync();
        Assert.Equal(("Do you want to remove the selected issue tracker rule?", "You can't undo this action", "Remove", 580.0),
            (_dialogs[^1].Title, _dialogs[^1].Description, _dialogs[^1].SubmitText, _dialogs[^1].Width));
        Assert.Equal("Sample Jira Multiproject Rule", vm.SelectedRule!.Name);
        vm.SelectedRule = vm.Rules[1];
        await vm.RemoveRuleAsync(); // "Old"
        gh.Name = "GH";

        await vm.SaveAsync();
        Assert.Equal(sharedText, File.ReadAllText(shared)); // unchanged: the shared file is not rewritten
        Assert.Equal([
            new IssueRule("Shared", IssueRuleLevel.Shared, @"S-(\d+)", "https://s/$1"),
            new IssueRule("GH", IssueRuleLevel.Local, @"#(\d+)", "https://github.com/username/repository/issues/$1"),
            new IssueRule("Sample Jira Multiproject Rule", IssueRuleLevel.Local, "(PRJ1|PRJ2)-(\\d+)", "https://jira.yourcompany.com/browse/$1-$2"),
        ], IssueTracker.Read(info));

        // Moving the shared rule to Local rewrites both files; a level without rules deletes its file.
        var again = await NewVmAsync();
        again.Rules[0].IsLocal = true;
        Assert.Equal(("Local", false), (again.Rules[0].LevelText, again.Rules[0].IsShared));
        await again.SaveAsync();
        Assert.False(File.Exists(shared));
        Assert.Equal(["Shared", "GH", "Sample Jira Multiproject Rule"], IssueTracker.Read(info).Select(r => r.Name));
    }

    [AvaloniaFact]
    public async Task An_incomplete_rule_file_is_kept_as_bak_and_a_rule_without_name_gets_the_default_name()
    {
        var info = await InfoAsync();
        var local = Path.Combine(info.CommonDir, "issuetracker");
        var original = "[issuetracker \"A\"]\n  regex = \"A-(\\\\d+)\"\n  url = \"https://a/$1\"\nnot a rule line\n[issuetracker \"B\"]\n";
        File.WriteAllText(local, original);
        Assert.False(IssueTracker.ReadFile(info, IssueRuleLevel.Local).Complete);
        var vm = await NewVmAsync();
        Assert.Equal(["A"], vm.Rules.Select(r => r.Name));
        vm.AddRule("new");
        vm.SelectedRule!.Name = "  ";
        vm.SelectedRule.Regex = "N";
        vm.SelectedRule.Url = "https://n";
        await vm.SaveAsync();
        Assert.Equal(original, File.ReadAllText(local + ".bak")); // nothing GerGit could not read is lost
        Assert.Equal(["A", IssueRuleItem.DefaultName], IssueTracker.Read(info).Select(r => r.Name));
        Assert.True(IssueTracker.ReadFile(info, IssueRuleLevel.Local).Complete);
    }

    [AvaloniaFact]
    public void Preview_compiles_once_per_change_and_never_blocks_long()
    {
        var item = new IssueRuleItem(new("R", IssueRuleLevel.Local, @"(?=a)(a+)+b", "https://x/$1"), new string('a', 40) + "!");
        Assert.True(item.IsValid);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.Empty(item.Preview.Links); // the backtracking fallback stops after 50 ms
        Assert.InRange(sw.ElapsedMilliseconds, 0, 5000); // loose: only an endless run fails
        item.Regex = @"(a+)+!"; // the linear engine
        sw.Restart();
        Assert.Single(item.Preview.Links);
        Assert.InRange(sw.ElapsedMilliseconds, 0, 5000); // loose: only an endless run fails
    }

    [AvaloniaFact]
    public async Task Open_shows_the_window_saves_on_close_and_refreshes_the_tab()
    {
        var tab = new RepoTabViewModel(await GitRepository.OpenAsync(_repo.Path));
        bool changed = false;
        void OnChanged() => changed = true;
        IssueLinks.Changed += OnChanged;
        try
        {
            tab.ShowDialog = d =>
            {
                _dialogs.Add(d);
                ((RepositorySettingsViewModel)d).AddRule("jira");
                return Task.FromResult(false); // Esc / title-bar close saves too
            };
            await RepositorySettingsViewModel.OpenAsync(tab);
            Assert.IsType<RepositorySettingsViewModel>(Assert.Single(_dialogs));
            Assert.Equal("Sample Jira Rule", IssueTracker.Read(tab.Repo.Info).Single().Name);
            Assert.True(changed);
            Assert.True(tab.Refreshing.IsCompleted);
        }
        finally { IssueLinks.Changed -= OnChanged; }
    }

    // ------------------------------------------------------------------ Browser

    [Fact]
    public async Task The_browser_gets_http_urls_only_also_through_a_replaced_action()
    {
        var hosting = new HostingService(new AccountStore(Path.Combine(_repo.Path, "accounts.json")), new FakeSecretStore());
        var browser = new List<string>();
        var paths = new List<string>();
        hosting.OpenBrowser = browser.Add;
        hosting.OpenPath = p => { paths.Add(p); return Task.CompletedTask; };
        foreach (var url in new[] { "file:///etc/passwd", "javascript:alert(1)", "/home/user/x.desktop", "ftp://h/x", "", "https://ok.example/a",
                     "HTTP://OK.example", "https://github.com@evil.example/" })
            hosting.OpenBrowser(url);
        Assert.Equal(["https://ok.example/a", "http://ok.example/"], browser); // the checked form
        await hosting.OpenPath(_repo.Path); // local paths have their own way
        Assert.Equal([RepositoryScan.RealPath(_repo.Path)!], paths);
    }

    [Fact]
    public async Task Open_path_opens_folders_and_passive_files_and_shows_the_folder_for_anything_else()
    {
        var hosting = new HostingService(new AccountStore(Path.Combine(_repo.Path, "accounts.json")), new FakeSecretStore());
        var opened = new List<string>();
        hosting.OpenPath = p => { opened.Add(p); return Task.CompletedTask; }; // a replaced action is checked too
        var notices = new List<string>();
        hosting.ShowNotice = text => { notices.Add(text); return Task.CompletedTask; };
        // The MIME type xdg-mime would report, by file name; other names (other tests running meanwhile) ask xdg-mime.
        var types = new Dictionary<string, string?>
        {
            ["notes.txt"] = "text/plain", ["data.json"] = "application/json", ["Main.cs"] = "text/x-csharp", ["shot.PNG"] = "image/png",
            ["photo.jpeg"] = "image/jpeg", ["manual.pdf"] = "application/pdf", ["ok.txt"] = "text/plain",
            ["html.txt"] = "text/html", ["url.txt"] = "application/x-mswinurl", ["ref.txt"] = "application/vnd.flatpak.ref",
            ["rtf.txt"] = "text/rtf", ["iso.png"] = "application/x-iso9660-image", ["unknown.txt"] = null,
            ["LICENSE"] = "text/plain", [".gitignore"] = "text/plain", ["Makefile"] = "text/x-makefile",
            [".bashrc"] = "application/x-shellscript",
        };
        HostingService.MimeTypeOf = p => types.TryGetValue(Path.GetFileName(p), out var t) ? t : HostingService.QueryMimeType(p);
        try
        {
            string Make(string name, string content)
            {
                var path = Path.Combine(_repo.Path, name);
                File.WriteAllText(path, content);
                return path;
            }
            var folder = Directory.CreateDirectory(Path.Combine(_repo.Path, "dir")).FullName;
            string MakeBytes(string name, byte[] content)
            {
                var path = Path.Combine(_repo.Path, name);
                File.WriteAllBytes(path, content);
                return path;
            }
            var passive = new[] { "notes.txt", "data.json", "Main.cs" }.Select(n => Make(n, "\uFEFF  hello\n")).ToList();
            passive.Add(MakeBytes("shot.PNG", TestPng.Solid(2, 2, 0xFF0000FF)));
            passive.Add(MakeBytes("photo.jpeg", [0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F']));
            passive.Add(Make("manual.pdf", "%PDF-1.4\n"));
            passive.Add(Make("LICENSE", "GPL\n")); // no extension and dotfiles: the MIME type decides
            passive.Add(Make(".gitignore", "bin/\n"));
            var desktop = Make("run.DESKTOP", "x");
            var kde = Make("run.kdelnk", "x");
            var disguised = Make("readme.txt", "\n[Desktop Entry]\nExec=evil\n");
            var script = Make("tool.txt", "echo hi");
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var link = Path.Combine(_repo.Path, "innocent.txt");
            File.CreateSymbolicLink(link, desktop);
            var webLink = Path.Combine(_repo.Path, "picture.png");
            File.CreateSymbolicLink(webLink, Make("page.html", "<script>evil()</script>"));
            var dirLink = Path.Combine(_repo.Path, "dirlink");
            Directory.CreateSymbolicLink(dirLink, folder);
            var fifo = Path.Combine(_repo.Path, "pipe.txt");
            await System.Diagnostics.Process.Start("mkfifo", fifo)!.WaitForExitAsync();
            // `sub/../x` names repo/x as text, but the kernel follows `sub` first and opens deep/x: the checks must see deep/x.
            Directory.CreateDirectory(Path.Combine(_repo.Path, "deep", "inner"));
            Directory.CreateSymbolicLink(Path.Combine(_repo.Path, "sub"), "deep/inner");
            Directory.CreateDirectory(Path.Combine(_repo.Path, "d"));
            var hidden = Make("deep/d", "[Desktop Entry]\nExec=evil\n");
            File.SetUnixFileMode(hidden, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var dotDot = Path.Combine(_repo.Path, "x.desktop");
            File.CreateSymbolicLink(dotDot, "sub/../d"); // reads as the folder repo/d
            Make("look.txt", "harmless");
            Make("deep/look.txt", "[Desktop Entry]\nExec=evil\n");
            var lookalike = Path.Combine(_repo.Path, "notes2.txt");
            File.CreateSymbolicLink(lookalike, "sub/../look.txt"); // reads as the harmless repo/look.txt
            var dangling = Path.Combine(_repo.Path, "dangling.txt");
            File.CreateSymbolicLink(dangling, "missing.txt");
            // The same trick the harmless way round: only deep/ok.txt exists, so only the real target can make it open.
            Make("deep/ok.txt", "fine");
            var throughDotDot = Path.Combine(_repo.Path, "ok2.txt");
            File.CreateSymbolicLink(throughDotDot, "sub/../ok.txt");

            foreach (var p in (string[])[folder, .. passive, dirLink, throughDotDot]) await hosting.OpenPath(p);
            // What is opened is the real target: the folder behind the link, deep/ok.txt behind ok2.txt.
            string[] expected = [folder, .. passive, folder, Path.Combine(_repo.Path, "deep", "ok.txt")];
            Assert.Equal(expected.Select(p => RepositoryScan.RealPath(p)), opened);
            Assert.Empty(notices);
            opened.Clear();
            var kdeEntry = Make("kde.txt", "[KDE Desktop Entry]\nExec=evil\n");
            var action = Make("action.txt", "x\n[Desktop Action run]\nExec=evil\n");
            // Not on the list of passive types: programs, installers, packages, scripts, web pages, SVG, Markdown, no MIME glob.
            var others = new[] { "setup.EXE", "setup.msi", "run.bat", "run.cmd", "run.com", "app.jar", "app.JNLP", "short.LNK",
                "app.flatpakref", "repo.flatpakrepo", "pkg.deb", "pkg.rpm", "build.sh", "tool.py", "index.html", "logo.svg", "keys.reg",
                "run.vbs", "run.ps1", "Makefile", "README.md", "notes.markdown", "settings.ini", "app.conf", "a.cfg", "doc.adoc", "data.xml", "app.js", "app.ts" }.Select(n => Make(n, "x")).ToList();
            var pe = Make("program.txt", "MZ\u0090\0rest of a PE header");
            var mz = Make("mz.txt", "MZ but no NUL byte");
            var nul = Make("nul.txt", "text\0with a NUL");
            var late = Make("late.txt", new string('x', 200) + "\n[Desktop Entry]\nExec=evil\n"); // beyond the first 64 bytes
            // A passive name, but xdg-mime (which xdg-open asks too) reports another type, or nothing.
            var sniffed = new[] { "html.txt", "url.txt", "ref.txt", "rtf.txt", "unknown.txt", ".bashrc" }.Select(n => Make(n, "plain")).ToList();
            sniffed.Add(MakeBytes("iso.png", TestPng.Solid(1, 1, 0xFF)));
            var config = Make("conf.txt", "# Config File\n[Desktop Entry]\n");
            var kdeConfig = Make("kconf.txt", "# KDE Config File\nExec=evil\n");
            string[] refused = [desktop, kde, disguised, script, link, webLink, fifo, dotDot, lookalike, dangling, kdeEntry, action, .. others, pe, mz,
                nul, late, .. sniffed, config, kdeConfig];
            foreach (var p in refused) await hosting.OpenPath(p);
            Assert.Equal(Enumerable.Repeat(RepositoryScan.RealPath(_repo.Path), refused.Length), opened); // never the file itself: its folder
            Assert.Equal(Enumerable.Repeat(HostingService.FolderInsteadNotice, refused.Length), notices); // and the user is told why
            opened.Clear();
            notices.Clear();
            await hosting.OpenPath("relative/notes.txt");
            await hosting.OpenPath(Path.Combine(_repo.Path, "gone", "x.desktop")); // no folder to show either
            Assert.Empty(opened);
            Assert.Empty(notices);
        }
        finally { HostingService.MimeTypeOf = HostingService.QueryMimeType; }
    }

    [Fact]
    public async Task Open_path_goes_by_the_type_the_real_xdg_mime_reports()
    {
        Assert.SkipWhen(ProgramPath.Of("xdg-mime") is null, "xdg-mime is not installed");
        var hosting = new HostingService(new AccountStore(Path.Combine(_repo.Path, "accounts.json")), new FakeSecretStore());
        var opened = new List<string>();
        hosting.OpenPath = p => { opened.Add(p); return Task.CompletedTask; };
        hosting.ShowNotice = _ => Task.CompletedTask;
        var file = Path.Combine(_repo.Path, "notes.txt");
        File.WriteAllText(file, "notes\n<script>alert(1)</script>\n");
        // Glob-based lookups (gio, KDE, `mimetype`) say text/plain, `file` says text/html: whichever this desktop uses,
        // the file opens exactly when that answer is a passive type.
        var type = HostingService.QueryMimeType(RepositoryScan.RealPath(file)!);
        await hosting.OpenPath(file);
        var real = RepositoryScan.RealPath(file);
        Assert.Equal(type is "text/plain" ? real : RepositoryScan.RealPath(_repo.Path), Assert.Single(opened));
    }

    // ------------------------------------------------------------------ Links in commit messages and output

    static Point CharCenter(SelectableTextBlock block, int index, Visual root)
    {
        var r = block.TextLayout.HitTestTextPosition(index);
        return block.TranslatePoint(new Point(r.X + 2 + block.Padding.Left, r.Center.Y + block.Padding.Top), root)!.Value;
    }

    [AvaloniaFact]
    public async Task Commit_message_shows_issue_links_and_urls_and_a_click_opens_only_http()
    {
        var info = await InfoAsync();
        File.WriteAllText(Path.Combine(info.CommonDir, "issuetracker"), IssueTracker.Format([
            new("GitHub", IssueRuleLevel.Local, @"#(\d+)", "https://github.com/o/r/issues/$1"),
            new("Evil", IssueRuleLevel.Local, @"EVIL-(\d+)", "javascript:alert($1)"),
            new("Broken", IssueRuleLevel.Local, @"(unclosed", "https://x"),
        ]));
        var sha = _repo.Commit("Fix #12 EVIL-3\n\nSee https://example.com/doc. and file:///etc/passwd", 1, ("a.txt", "a"));
        var tab = new RepoTabViewModel(await GitRepository.OpenAsync(_repo.Path));
        var view = new DetailsView { DataContext = tab.Details };
        var window = new Window { Width = 900, Height = 600, Content = view };
        window.Show();
        try
        {
            await tab.Details.ShowAsync([sha]);
            await view.MessageShown;
            Settle(window);
            var subject = view.FindControl<SelectableTextBlock>("SubjectText")!;
            var description = view.FindControl<SelectableTextBlock>("DescriptionText")!;
            Assert.Equal([new TextLink(4, 3, "https://github.com/o/r/issues/12")], Hyperlinks.GetLinks(subject));
            Assert.Equal([new TextLink(4, 23, "https://example.com/doc")], Hyperlinks.GetLinks(description));
            var linkRun = subject.Inlines!.OfType<Run>().Single(r => r.Text == "#12");
            Assert.Equal(TextDecorations.Underline, linkRun.TextDecorations);
            Assert.Equal("Fix #12 EVIL-3", subject.Inlines!.Text);

            // Hover: tooltip = URL; click without a drag opens it; plain text opens nothing.
            var at = CharCenter(subject, 5, window);
            window.MouseMove(at);
            Assert.Equal("https://github.com/o/r/issues/12", ToolTip.GetTip(subject));
            window.MouseDown(at, MouseButton.Left);
            window.MouseUp(at, MouseButton.Left);
            var plain = CharCenter(subject, 1, window);
            window.MouseMove(plain);
            Assert.Null(ToolTip.GetTip(subject));
            window.MouseDown(plain, MouseButton.Left);
            window.MouseUp(plain, MouseButton.Left);
            var evil = CharCenter(subject, 10, window); // EVIL-3: its rule gives no http URL, so no link
            window.MouseDown(evil, MouseButton.Left);
            window.MouseUp(evil, MouseButton.Left);
            Assert.Equal(["https://github.com/o/r/issues/12"], _opened);

            // The repository switch off: only plain URLs stay links (after the change notification).
            RepoSettings.Set(info.CommonDir, IssueLinks.RepoKey, false);
            IssueLinks.RaiseChanged();
            await view.MessageShown;
            Assert.Empty(Hyperlinks.GetLinks(subject));
            Assert.Equal("Fix #12 EVIL-3", subject.Text);
            Assert.Single(Hyperlinks.GetLinks(description));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Output_view_links_http_urls_and_keeps_the_line_colours()
    {
        var output = new OutputView { Text = "fatal: unable to access 'https://h.example/r.git/': 403\nsee file:///etc/passwd\n" };
        var window = new Window { Width = 800, Height = 300, Content = output };
        window.Show();
        try
        {
            Settle(window);
            var block = output.TextBlock;
            Assert.Equal([new TextLink(25, 24, "https://h.example/r.git/")], Hyperlinks.GetLinks(block));
            var runs = block.Inlines!.OfType<Run>().ToList();
            Assert.Equal(["fatal: unable to access '", "https://h.example/r.git/", "': 403\n", "see file:///etc/passwd"], runs.Select(r => r.Text));
            Assert.All(runs.Take(3), r => Assert.Equal(FontWeight.Bold, r.FontWeight)); // the error line stays bold
            Assert.Equal(TextDecorations.Underline, runs[1].TextDecorations);

            var at = CharCenter(block, 30, window);
            window.MouseDown(at, MouseButton.Left);
            window.MouseUp(at, MouseButton.Left);
            Assert.Equal(["https://h.example/r.git/"], _opened); // no Ctrl needed
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Hovering_any_link_shows_its_target_url_as_tooltip()
    {
        var output = new OutputView { Text = "see https://h.example/a and https://h.example/b here\n" };
        var window = new Window { Width = 800, Height = 300, Content = output };
        window.Show();
        try
        {
            Settle(window);
            var block = output.TextBlock;
            ToolTip.SetShowDelay(block, 0);
            // Link, plain text, the other link of the same block: each link shows its own URL, plain text none.
            foreach (var (index, url) in new[] { (6, "https://h.example/a"), (2, null), (30, "https://h.example/b") })
            {
                var at = CharCenter(block, index, window);
                window.MouseMove(at);
                window.MouseMove(at + new Point(1, 0));
                Settle(window);
                Assert.Equal(url, ToolTip.GetTip(block));
                Assert.Equal(url is not null, ToolTip.GetIsOpen(block));
            }
        }
        finally { window.Close(); }
    }

    // ------------------------------------------------------------------ Tooltip for trimmed text

    /// <summary>Opens the tooltip as the tooltip service would; false when it stays closed (no tip, or cancelled).</summary>
    static bool Shows(TextBlock block)
    {
        ToolTip.SetIsOpen(block, true);
        Dispatcher.UIThread.RunJobs();
        bool open = ToolTip.GetIsOpen(block);
        ToolTip.SetIsOpen(block, false);
        Dispatcher.UIThread.RunJobs();
        return open;
    }

    [AvaloniaFact]
    public void Trimmed_text_gets_its_full_text_as_tooltip_only_while_it_is_cut()
    {
        var text = new TextBlock { Text = "a rather long file name that does not fit.txt", TextTrimming = TextTrimming.CharacterEllipsis };
        TrimmedToolTip.SetIsEnabled(text, true);
        var host = new Border { Width = 60, Child = text };
        var window = new Window { Width = 400, Height = 100, Content = host };
        window.Show();
        try
        {
            Settle(window);
            Assert.True(TrimmedToolTip.IsTrimmed(text));
            Assert.Equal(text.Text, ToolTip.GetTip(text));
            Assert.True(Shows(text));
            host.Width = 390;
            Settle(window);
            Assert.False(TrimmedToolTip.IsTrimmed(text));
            Assert.False(Shows(text)); // the opening is cancelled while the text fits
            text.Text = new string('x', 200); // a new text in the same size: the tip follows, and it is cut again
            Settle(window);
            Assert.Equal(text.Text, ToolTip.GetTip(text));
            Assert.True(Shows(text));
            TrimmedToolTip.SetIsEnabled(text, false);
            Assert.Null(ToolTip.GetTip(text));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Details_file_list_rows_get_the_trimmed_tooltip_and_renames_keep_old_and_new()
    {
        var longName = "folder/" + new string('n', 80) + ".txt";
        _repo.Commit("one", 1, ("old.txt", "x\n"));
        _repo.Git("mv", "old.txt", "new.txt");
        var sha = _repo.Commit("two", 2, (longName, "y\n"), ("s.txt", "s\n"));
        var tab = new RepoTabViewModel(await GitRepository.OpenAsync(_repo.Path));
        var view = new DetailsView { DataContext = tab.Details };
        var window = new Window { Width = 900, Height = 600, Content = view };
        window.Show();
        try
        {
            await tab.Details.ShowAsync([sha]);
            tab.Details.SelectedTab = DetailsViewModel.ChangesTab;
            Settle(window);
            var rows = view.FindControl<ListBox>("FileList")!.GetVisualDescendants().OfType<TextBlock>()
                .Where(t => t.TextTrimming == TextTrimming.CharacterEllipsis).ToDictionary(t => t.Text!);
            Assert.True(Shows(rows[longName])); // 280 px column: cut
            Assert.Equal(longName, ToolTip.GetTip(rows[longName]));
            Assert.False(Shows(rows["s.txt"]));
            var renamed = rows["old.txt → new.txt"];
            Assert.False(TrimmedToolTip.GetIsEnabled(renamed));
            Assert.Equal("Old:\told.txt\nNew:\tnew.txt", ToolTip.GetTip((Control)renamed.Parent!));
            Assert.Null(ToolTip.GetTip((Control)rows["s.txt"].Parent!));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Local_changes_rows_get_the_trimmed_tooltip_and_renames_keep_old_and_new()
    {
        var longName = new string('l', 120) + ".txt";
        _repo.Commit("one", 1, ("old.txt", "x\n"));
        _repo.Git("mv", "old.txt", "new.txt");
        _repo.Write(longName, "y\n");
        _repo.Write("s.txt", "s\n");
        var tab = new RepoTabViewModel(await GitRepository.OpenAsync(_repo.Path));
        await tab.RefreshAsync();
        var view = new LocalChangesView { DataContext = tab.LocalChanges };
        var window = new Window { Width = 700, Height = 500, Content = view };
        window.Show();
        try
        {
            Settle(window);
            TextBlock Row(string name, string list) => view.FindControl<ListBox>(list)!.GetVisualDescendants().OfType<TextBlock>()
                .Single(t => t.TextTrimming == TextTrimming.CharacterEllipsis && t.Text == name);
            Assert.True(Shows(Row(longName, "UnstagedList")));
            Assert.False(Shows(Row("s.txt", "UnstagedList")));
            var renamed = Row("new.txt", "StagedList");
            Assert.False(TrimmedToolTip.GetIsEnabled(renamed));
            Assert.Equal("Old:\told.txt\nNew:\tnew.txt", ToolTip.GetTip((Control)renamed.Parent!));
        }
        finally { window.Close(); }
    }

    static async Task WaitFor(Func<bool> condition)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException();
            await Task.Delay(20);
            Dispatcher.UIThread.RunJobs();
        }
    }
}
