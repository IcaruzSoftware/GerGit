using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using GerGit.ViewModels;
using GerGit.ViewModels.Dialogs;
using GerGit.ViewModels.Preferences;
using GerGit.Views;
using GerGit.Views.Preferences;

namespace GerGit.Tests.Ui;

/// <summary>The Preferences window. Settings go to the test settings file; values other tests
/// read (auto-fetch, tags, space character, font size) are restored before the next await. The global identity is a temp
/// file (collection fixture) and PATH holds only git, so no real identity or terminal of the developer is read.</summary>
[Collection("OpenIn")]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class PreferencesTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("gergit-prefs-").FullName;
    readonly List<DialogViewModel> _dialogs = [];
    readonly TerminalSetting _savedTerminal = AppPreferences.Current.Terminal;
    readonly OpenInFixture _fixture;
    bool _answer;

    public PreferencesTests(OpenInFixture fixture)
    {
        _fixture = fixture;
        var bin = Directory.CreateDirectory(Path.Combine(_dir, "bin")).FullName;
        var git = Environment.GetEnvironmentVariable("PATH")!.Split(':').Select(d => Path.Combine(d, "git")).First(File.Exists);
        File.CreateSymbolicLink(Path.Combine(bin, "git"), git);
        OpenIn.Env = name => name == "PATH" ? bin : null;
        Settings.Set<string[]?>(AppPreferences.SourceFoldersKey, null);
        Settings.Set<int?>(AppPreferences.SubjectWarnKey, null);
        Settings.Set<int?>(AppPreferences.SubjectErrorKey, null);
        AppPreferences.Current.Terminal = new TerminalSetting();
    }

    public void Dispose()
    {
        Settings.Set<string[]?>(AppPreferences.SourceFoldersKey, null);
        Settings.Set<int?>(AppPreferences.SubjectWarnKey, null);
        Settings.Set<int?>(AppPreferences.SubjectErrorKey, null);
        AppPreferences.Current.Terminal = _savedTerminal;
        OpenIn.Env = Environment.GetEnvironmentVariable;
        PreferencesViewModel.IdentityScope = _fixture.Scope;
        Directory.Delete(_dir, recursive: true);
    }

    PreferencesViewModel NewVm(string? configFile = null)
    {
        PreferencesViewModel.IdentityScope = ["--file", configFile ?? Path.Combine(_dir, "gitconfig")];
        return new() { ShowDialog = d => { _dialogs.Add(d); return Task.FromResult(_answer); } };
    }

    [AvaloniaFact]
    public async Task Window_has_the_four_pages_and_a_single_close_button()
    {
        var vm = NewVm();
        await vm.Loading;
        Assert.Equal(("Preferences", null, "Close", true, true), (vm.Title, vm.SubmitText, vm.CancelText, vm.CancelIsDefault, vm.PlainFrame));
        var window = new Window { Content = new PreferencesView { DataContext = vm }, Width = 700, Height = 500 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var tabs = window.GetVisualDescendants().OfType<TabControl>().Single();
        Assert.Equal(["General", "Commit", "Git", "Integration"], tabs.Items.OfType<TabItem>().Select(t => (string)t.Header!));
        Assert.Matches(@"^git version \d.* - /.*git$", vm.GitInstance);
        window.Close();
    }

    [AvaloniaFact]
    public void General_page_values_are_stored_at_once()
    {
        var vm = NewVm();
        Assert.Equal(("13", true, false, "-"), (vm.FontSizeText, vm.AutoFetch, vm.FetchWithTags, vm.SpaceChar));
        try
        {
            vm.FontSizeText = "20";
            Assert.Equal(20, AppPreferences.Current.EditorFontSize);
            vm.FontSizeText = "abc";
            Assert.Equal(13, AppPreferences.Current.EditorFontSize);
            vm.FontSizeText = "5";
            Assert.Equal(10, AppPreferences.Current.EditorFontSize);
            vm.FontSizeText = "99";
            Assert.Equal((40.0, "99"), (AppPreferences.Current.EditorFontSize, vm.FontSizeText)); // the box text stays

            vm.AutoFetch = false;
            vm.FetchWithTags = true;
            vm.SpaceChar = "_";
            Assert.False(vm.PushAfterCommit);
            vm.PushAfterCommit = true;
            Assert.True(Settings.Get(LocalChangesViewModel.PushAfterCommitKey, false));
            Assert.False(Settings.Get(AutoFetch.EnabledKey, true));
            Assert.True(Settings.Get(RemoteOps.FetchTagsKey, false));
            Assert.Equal("_", Settings.SpaceReplacement);
        }
        finally
        {
            Settings.Set<double?>(AppPreferences.EditorFontSizeKey, null);
            Settings.Set<bool?>(AutoFetch.EnabledKey, null);
            Settings.Set<bool?>(RemoteOps.FetchTagsKey, null);
            Settings.Set<string?>(Settings.SpaceCharKey, null);
            Settings.Set<bool?>(LocalChangesViewModel.PushAfterCommitKey, null);
        }
    }

    [AvaloniaFact]
    public void Update_submodules_preference_is_shown_stored_and_used_by_the_git_engine()
    {
        var vm = NewVm();
        var window = new Window { Content = new PreferencesView { DataContext = vm }, Width = 700, Height = 500 };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var box = window.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Name == "UpdateSubmodules");
            Assert.Equal("Update submodules automatically after checkout, pull and merge", box.Content);
            Assert.True(box.IsChecked);

            box.IsChecked = false;
            Assert.False(Settings.Get(AppPreferences.UpdateSubmodulesKey, true));
            Assert.False(GerGit.Core.Git.GitRepository.UpdateSubmodulesOnCheckout);
            Assert.False(NewVm().UpdateSubmodules);

            vm.UpdateSubmodules = true;
            Assert.True(Settings.Get(AppPreferences.UpdateSubmodulesKey, false));
            Assert.True(GerGit.Core.Git.GitRepository.UpdateSubmodulesOnCheckout);
        }
        finally
        {
            Settings.Set<bool?>(AppPreferences.UpdateSubmodulesKey, null);
            GerGit.Core.Git.GitRepository.UpdateSubmodulesOnCheckout = true;
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Diff_font_size_follows_the_preference_live()
    {
        var view = new DiffView();
        var window = new Window { Content = view };
        window.Show();
        try
        {
            var diffEditor = view.FindControl<TextEditor>("Editor")!;
            Assert.Equal(13, diffEditor.FontSize);
            AppPreferences.Current.EditorFontSize = 18;
            Assert.Equal(18, diffEditor.FontSize);
        }
        finally
        {
            Settings.Set<double?>(AppPreferences.EditorFontSizeKey, null);
            AppPreferences.Current.EditorFontSize = 13;
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Commit_page_limits_drive_the_subject_counter()
    {
        var vm = NewVm();
        Assert.Equal(("50", "70"), (vm.LowLimitText, vm.HighLimitText));
        vm.LowLimitText = "60";
        vm.HighLimitText = "80";
        Assert.Equal((60, 80), (LocalChangesViewModel.SubjectLowLimit, LocalChangesViewModel.SubjectHighLimit));
        vm.LowLimitText = "x";
        vm.HighLimitText = "";
        Assert.Equal((50, 70), (LocalChangesViewModel.SubjectLowLimit, LocalChangesViewModel.SubjectHighLimit));
        Assert.Equal(("x", ""), (vm.LowLimitText, vm.HighLimitText));
    }

    [AvaloniaFact]
    public void Integration_page_issue_link_switch_is_stored_at_once_and_announced()
    {
        var vm = NewVm();
        int changes = 0;
        void OnChanged() => changes++;
        IssueLinks.Changed += OnChanged;
        try
        {
            Assert.True(vm.IssueLinks); // default on
            vm.IssueLinks = false;
            Assert.False(Settings.Get(IssueLinks.GlobalKey, true));
            Assert.Equal(1, changes);
        }
        finally
        {
            IssueLinks.Changed -= OnChanged;
            Settings.Set<bool?>(IssueLinks.GlobalKey, null);
        }
    }

    [AvaloniaFact]
    public void Integration_page_avatar_switch_is_shown_stored_and_used_by_the_avatar_loader()
    {
        var vm = NewVm();
        var window = new Window { Content = new PreferencesView { DataContext = vm }, Width = 700, Height = 500 };
        window.Show();
        try
        {
            var tabs = window.GetVisualDescendants().OfType<TabControl>().Single();
            tabs.SelectedIndex = 3; // Integration
            Dispatcher.UIThread.RunJobs();
            var box = window.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Name == "LoadAvatars");
            Assert.Equal("Load avatars (Gravatar, GitHub, GitLab)", box.Content);
            Assert.True(box.IsChecked); // default on

            box.IsChecked = false;
            Assert.False(Settings.Get(AppPreferences.LoadAvatarsKey, true));
            Assert.False(GerGit.Hosting.Avatars.Enabled);
            Assert.False(NewVm().LoadAvatars);

            vm.LoadAvatars = true;
            Assert.True(Settings.Get(AppPreferences.LoadAvatarsKey, false));
            Assert.True(GerGit.Hosting.Avatars.Enabled);
        }
        finally
        {
            Settings.Set<bool?>(AppPreferences.LoadAvatarsKey, null);
            GerGit.Hosting.Avatars.Enabled = true;
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Source_folders_are_added_sorted_replaced_and_removed_after_confirmation()
    {
        var vm = NewVm();
        Assert.Empty(vm.SourceFolders);
        vm.AddFolder("/src/b10");
        Assert.False(vm.CanRemoveFolder); // the last folder stays
        vm.AddFolder("/src/B9/");
        vm.AddFolder("/src/a");
        vm.AddFolder("/src/a/");
        Assert.Equal(["/src/a/", "/src/B9/", "/src/b10/"], vm.SourceFolders.Select(f => f.Path));
        Assert.Equal(["/src/b10/", "/src/B9/", "/src/a/"], AppPreferences.SourceFolders);
        Assert.True(vm.CanRemoveFolder && vm.SourceFolders.All(f => f.CanRemove));
        Assert.Equal("/src/a/", vm.SelectedFolder!.Path);

        vm.ReplaceFolder(vm.SourceFolders[1], "/work");
        Assert.Equal(["/src/a/", "/src/b10/", "/work/"], vm.SourceFolders.Select(f => f.Path));

        // Remove asks first; Cancel keeps the folder.
        await vm.RemoveFolderAsync(vm.SourceFolders[0]);
        var confirm = Assert.Single(_dialogs);
        Assert.Equal(("Do you want to remove the selected source directory?", "GerGit will not look for repositories in this folder automatically",
            "Remove", 550.0), (confirm.Title, confirm.Description, confirm.SubmitText, confirm.Width));
        Assert.Equal(3, vm.SourceFolders.Count);
        _answer = true;
        await vm.RemoveFolderAsync(vm.SourceFolders[0]);
        await vm.RemoveFolderAsync(vm.SourceFolders[0]);
        Assert.Equal(["/work/"], AppPreferences.SourceFolders);
        Assert.False(vm.SourceFolders.Single().CanRemove);
        await vm.RemoveFolderAsync(vm.SourceFolders[0]); // never the last one
        Assert.Equal(["/work/"], NewVm().SourceFolders.Select(f => f.Path)); // read back by a new window

        // Root and home are flagged.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "/";
        Assert.Equal($"'{home}' should not be used as a source directory. Please choose a subfolder instead", new SourceFolderItem(home, true).WarningTip);
        Assert.True(new SourceFolderItem("/", true).IsWarning);
        Assert.False(new SourceFolderItem("/work/", true).IsWarning);
    }

    [AvaloniaFact]
    public async Task Global_identity_is_read_and_written_when_a_box_loses_focus_or_the_window_closes()
    {
        var config = Path.Combine(_dir, "gitconfig");
        File.WriteAllText(config, "[user]\n\tname = Old Name\n\temail = old@example.com\n");
        var vm = NewVm(config);
        await vm.Loading;
        Assert.Equal(("Old Name", "old@example.com", "old@example.com"), (vm.UserName, vm.UserEmail, vm.AvatarEmail));

        vm.UserName = "  New Name ";
        await vm.SaveIdentityAsync();
        var again = NewVm(config);
        await again.Loading;
        Assert.Equal(("New Name", "old@example.com"), (again.UserName, again.UserEmail));

        again.UserEmail = "new@example.com";
        again.Cancel(); // Esc / Close / title bar: the pending edit is written too
        for (int i = 0; i < 200 && !File.ReadAllText(config).Contains("new@example.com"); i++) await Task.Delay(20);
        Assert.Contains("email = new@example.com", File.ReadAllText(config));
        Assert.Empty(_dialogs);

        // A failing write shows git's message and is tried again at the next focus loss.
        var missing = Path.Combine(_dir, "missing", "dir");
        var broken = NewVm(Path.Combine(missing, "gitconfig"));
        await broken.Loading;
        broken.UserName = "X";
        await broken.SaveIdentityAsync();
        Assert.Equal("Git Error", Assert.Single(_dialogs).Title);
        Directory.CreateDirectory(missing);
        await broken.SaveIdentityAsync();
        Assert.Contains("name = X", File.ReadAllText(Path.Combine(missing, "gitconfig")));
        Assert.Single(_dialogs);
    }

    [AvaloniaFact]
    public async Task Identity_values_starting_with_a_dash_are_stored_literally_and_typing_during_the_load_is_kept()
    {
        var config = Path.Combine(_dir, "gitconfig");
        File.WriteAllText(config, "[user]\n\tname = Old Name\n\temail = old@example.com\n");
        var vm = NewVm(config);
        vm.UserName = "Typed Early"; // before the values are read
        await vm.Loading;
        Assert.Equal(("Typed Early", "old@example.com"), (vm.UserName, vm.UserEmail));

        vm.UserName = "--help";
        vm.UserEmail = "-e x@example.com";
        await vm.SaveIdentityAsync();
        var again = NewVm(config);
        await again.Loading;
        Assert.Equal(("--help", "-e x@example.com"), (again.UserName, again.UserEmail));
        Assert.Empty(_dialogs);
    }

    [AvaloniaFact]
    public async Task Identity_from_an_include_file_is_never_overridden_by_an_empty_or_unchanged_box()
    {
        var config = Path.Combine(_dir, "gitconfig");
        File.WriteAllText(Path.Combine(_dir, "work.inc"), "[user]\n\tname = Included Name\n\temail = inc@example.com\n");
        File.WriteAllText(config, "[include]\n\tpath = work.inc\n");
        var vm = NewVm(config);
        await vm.Loading;
        // The boxes hold the global file's own values; the included ones are the placeholders and the avatar.
        Assert.Equal(("", "", "Included Name", "inc@example.com", "inc@example.com"),
            (vm.UserName, vm.UserEmail, vm.UserNameHint, vm.UserEmailHint, vm.AvatarEmail));

        // Only the email is typed: only user.email is written; the empty name box writes nothing.
        vm.UserEmail = "mine@example.com";
        await vm.SaveIdentityAsync();
        var text = File.ReadAllText(config);
        Assert.Contains("email = mine@example.com", text);
        Assert.DoesNotContain("name", text);
        Assert.Equal("mine@example.com", vm.AvatarEmail);

        // Clearing a box writes nothing either (no "" and no unset); an unchanged value is not written again.
        vm.UserEmail = " ";
        await vm.SaveIdentityAsync();
        vm.UserEmail = "mine@example.com";
        await vm.SaveIdentityAsync();
        Assert.Equal(text, File.ReadAllText(config));
        Assert.Empty(_dialogs);

        var fresh = NewVm(config);
        await fresh.Loading;
        Assert.Equal(("", "Included Name", "mine@example.com"), (fresh.UserName, fresh.UserNameHint, fresh.UserEmail));
    }

    [AvaloniaFact]
    public void Font_size_box_rejects_values_that_are_not_finite()
    {
        var vm = NewVm();
        try
        {
            vm.FontSizeText = "NaN";
            Assert.Equal(13, AppPreferences.Current.EditorFontSize);
            vm.FontSizeText = "Infinity";
            Assert.Equal(13, AppPreferences.Current.EditorFontSize);
        }
        finally { Settings.Set<double?>(AppPreferences.EditorFontSizeKey, null); }
    }

    [AvaloniaFact]
    public void Shell_choice_is_stored_as_the_integration_terminal_record()
    {
        var vm = NewVm();
        Assert.Equal((0, false, ""), (vm.ShellIndex, vm.ShellIsCustom, vm.ShellArgs));
        Assert.Equal("", vm.ShellPath); // no terminal on the test PATH
        Assert.Equal("Terminal", AppPreferences.Current.TerminalName);

        vm.SetShellPath("/ignored"); // Default has no path of its own
        vm.ShellIndex = 1;
        vm.SetShellPath("/usr/bin/my-term");
        vm.ShellArgs = "-x";
        Assert.Equal(("/usr/bin/my-term", "-x", "Shell"), (vm.ShellPath, vm.ShellArgs, AppPreferences.Current.TerminalName));
        var stored = JsonNode.Parse(File.ReadAllText(Settings.FilePath))![AppPreferences.TerminalKey]!;
        Assert.Equal(("custom", "/usr/bin/my-term", "-x"), ((string)stored["kind"]!, (string)stored["path"]!, (string)stored["args"]!));

        // Back to Default and again Custom: the custom values come back.
        vm.ShellIndex = 0;
        Assert.Equal(("Terminal", ""), (AppPreferences.Current.TerminalName, vm.ShellArgs));
        vm.ShellArgs = "ignored";
        vm.ShellIndex = 1;
        Assert.Equal(new TerminalSetting("custom", "/usr/bin/my-term", "-x"), AppPreferences.Current.Terminal);
    }

    [AvaloniaFact]
    public async Task File_menu_item_and_ctrl_comma_open_preferences_and_reload_the_tab()
    {
        using var repo = new TempRepo();
        repo.Commit("first", 1, ("a.txt", "a\n"));
        var main = new MainWindowViewModel();
        var shown = new List<DialogViewModel>();
        main.ShowDialog = d =>
        {
            shown.Add(d);
            if (d is PreferencesViewModel p) p.LowLimitText = "10"; // a limit changed while the window was open
            return Task.FromResult(false);
        };
        var window = new MainWindow { DataContext = main };
        window.Show();
        window.StatusTimer.Stop();
        try
        {
            Assert.Null(await main.OpenAsync(repo.Path));
            var tab = main.SelectedTab!;
            var second = repo.Commit("second", 2);
            tab.LocalChanges.Subject = "Twelve chars";
            Assert.Equal("38", tab.LocalChanges.CounterText);
            var changed = new List<string?>();
            tab.LocalChanges.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
            await main.OpenPreferencesAsync();
            Assert.IsType<PreferencesViewModel>(Assert.Single(shown));
            // The open commit box shows the new limit at once (no keystroke needed).
            Assert.Superset(new HashSet<string?> { "CounterText", "CounterWarn", "CounterError", "CounterToolTip" }, changed.ToHashSet());
            Assert.Equal(("-2", true), (tab.LocalChanges.CounterText, tab.LocalChanges.CounterWarn));
            Assert.Equal(second, tab.Refs.HeadSha); // reloaded after the window closed

            window.Focus();
            window.KeyPress(Key.OemComma, RawInputModifiers.Control, PhysicalKey.None, null);
            for (int i = 0; i < 100 && shown.Count < 2; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
            Assert.Equal(2, shown.Count);

            var item = window.GetVisualDescendants().OfType<Menu>().First().Items.OfType<MenuItem>().First()
                .Items.OfType<MenuItem>().Single(m => (string?)m.Header == "Preferences...");
            Assert.Equal("Ctrl+,", item.InputGesture!.ToString("p", null));
        }
        finally
        {
            main.CloseTab(null);
            window.Close();
        }
    }
}
