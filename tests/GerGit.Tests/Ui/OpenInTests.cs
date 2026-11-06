using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using GerGit.Core.Git;
using GerGit.Hosting;
using GerGit.Tests.Hosting;
using GerGit.ViewModels;
using GerGit.Views;

namespace GerGit.Tests.Ui;

[CollectionDefinition("OpenIn", DisableParallelization = true)]
public sealed class OpenInCollection : ICollectionFixture<OpenInFixture>;

/// <summary>For the whole collection: the Preferences window's global identity is a temp file, never the developer's
/// real ~/.gitconfig (the window reads it in its constructor).</summary>
public sealed class OpenInFixture : IDisposable
{
    public readonly string Dir = Directory.CreateTempSubdirectory("gergit-identity-").FullName;
    public string[] Scope => ["--file", Path.Combine(Dir, "gitconfig")];

    public OpenInFixture() => GerGit.ViewModels.Preferences.PreferencesViewModel.IdentityScope = Scope;

    public void Dispose()
    {
        GerGit.ViewModels.Preferences.PreferencesViewModel.IdentityScope = ["--global"];
        Directory.Delete(Dir, recursive: true);
    }
}

/// <summary>terminal and editor detection on a stub PATH, the "Open in" menu, the Terminal button and the
/// shortcuts. Stub programs record their working directory and arguments; <see cref="OpenIn.Env"/> points PATH and
/// TERMINAL at the stub folder.</summary>
[Collection("OpenIn")]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class OpenInTests : IDisposable
{
    readonly string _bin = Directory.CreateTempSubdirectory("gergit-openin-").FullName;
    readonly TempRepo _repo = new();
    readonly List<string> _errors = [];
    readonly List<string> _opened = [];
    readonly TerminalSetting _savedTerminal = AppPreferences.Current.Terminal;
    string? _terminalVariable;
    RepoTabViewModel? _tab;

    public OpenInTests()
    {
        _repo.Commit("first", 1, ("a.txt", "a\n"));
        OpenIn.Env = name => name switch { "PATH" => _bin, "TERMINAL" => _terminalVariable, _ => null };
        AppPreferences.Current.Terminal = new TerminalSetting();
    }

    public void Dispose()
    {
        OpenIn.Env = Environment.GetEnvironmentVariable;
        OpenIn.UseSetsid = GitRunner.HasSetsid;
        AppPreferences.Current.Terminal = _savedTerminal;
        _tab?.Dispose();
        _repo.Dispose();
        Directory.Delete(_bin, recursive: true);
    }

    /// <summary>A stub that writes its working directory and arguments (one per line) to &lt;stub&gt;.out.</summary>
    string Stub(string name, bool executable = true)
    {
        var path = Path.Combine(_bin, name);
        File.WriteAllText(path, "#!/bin/sh\n{ pwd; for a in \"$@\"; do printf '%s\\n' \"$a\"; done; } > \"$0.out.tmp\"; mv \"$0.out.tmp\" \"$0.out\"\n");
        File.SetUnixFileMode(path, executable ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute : UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return path;
    }

    /// <summary>Working directory and arguments the stub was started with.</summary>
    static async Task<string[]> Recorded(string stub)
    {
        var sw = Stopwatch.StartNew();
        while (!File.Exists(stub + ".out"))
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException(stub);
            await Task.Delay(20);
        }
        var lines = await File.ReadAllLinesAsync(stub + ".out");
        File.Delete(stub + ".out");
        return lines;
    }

    async Task<RepoTabViewModel> OpenTabAsync(params Account[] accounts)
    {
        var hosting = new HostingService(new AccountStore(Path.Combine(_bin, "accounts.json")), new FakeSecretStore());
        foreach (var a in accounts) hosting.Store.Add(a);
        hosting.OpenBrowser = _opened.Add;
        hosting.OpenPath = p => { _opened.Add(p); return Task.CompletedTask; };
        var tab = _tab = new RepoTabViewModel(await GitRepository.OpenAsync(_repo.Path)) { Hosting = hosting };
        tab.ShowError = (title, text) => { _errors.Add($"{title}: {text}"); return Task.CompletedTask; };
        await tab.RefreshAsync();
        while (!tab.Refreshing.IsCompleted) await tab.Refreshing;
        return tab;
    }

    static string Flat(IEnumerable<MenuEntry> entries) => string.Join("|", entries.Select(e =>
        e.Kind == MenuEntryKind.Separator ? "-" : e.Header + (e.Gesture is null ? "" : $" [{e.Gesture}]")));

    [Fact]
    public void Arguments_split_on_whitespace_and_double_quotes_group()
    {
        Assert.Equal(["-e", "my shell", "--x=a b", ""], OpenIn.SplitArgs("  -e \"my shell\"  --x=\"a b\" \"\" "));
        Assert.Empty(OpenIn.SplitArgs("   "));
    }

    [Fact]
    public void Executables_are_found_on_path_only_when_executable()
    {
        var code = Stub("code");
        Stub("subl", executable: false);
        Assert.Equal(code, OpenIn.FindOnPath("code"));
        Assert.Null(OpenIn.FindOnPath("subl"));
        Assert.Null(OpenIn.FindOnPath("zed"));
        Assert.Equal(code, OpenIn.FindOnPath(code)); // a path is taken as it is
        Assert.Null(OpenIn.FindOnPath(Path.Combine(_bin, "nope")));
    }

    [Fact]
    public void Terminal_detection_follows_the_documented_order_and_the_TERMINAL_variable()
    {
        Assert.Null(OpenIn.DetectTerminal());
        Stub("xterm");
        var kitty = Stub("kitty");
        Assert.Equal(new ExternalApp("kitty", kitty), OpenIn.DetectTerminal()); // kitty comes before xterm
        Assert.Equal(string.Join("|", [kitty, "--directory", "/r"]), Norm(OpenIn.TerminalCommand(new(), "/r")));

        var gnome = Stub("gnome-terminal");
        Assert.Equal(string.Join("|", [gnome, "--working-directory=/r"]), Norm(OpenIn.TerminalCommand(new(), "/r")));

        // $TERMINAL wins (name or path); a known terminal keeps its folder argument, an unknown one gets none.
        var foot = Stub("foot");
        _terminalVariable = "foot";
        Assert.Equal(string.Join("|", [foot, "--working-directory=/r"]), Norm(OpenIn.TerminalCommand(new(), "/r")));
        var mine = Stub("myterm");
        _terminalVariable = mine;
        Assert.Equal(mine, Norm(OpenIn.TerminalCommand(new(), "/r")));
        _terminalVariable = "not-installed";
        Assert.Equal(gnome, OpenIn.DetectTerminal()!.Path);

        // Custom: its own path and arguments, nothing added.
        Assert.Equal(string.Join("|", ["/opt/t", "-e", "a b"]),
            Norm(OpenIn.TerminalCommand(new TerminalSetting("custom", "/opt/t", "-e \"a b\""), "/r")));
    }

    static string Norm((string? Exe, List<string> Args) c) => string.Join("|", [c.Exe, .. c.Args]);

    [AvaloniaFact]
    public async Task Terminal_starts_in_the_repository_folder_or_shows_clear_errors()
    {
        var tab = await OpenTabAsync();

        // No terminal on PATH.
        await OpenIn.TerminalAsync(tab);
        Assert.Equal(["Error: " + OpenIn.NoTerminal], _errors);

        var konsole = Stub("konsole");
        await OpenIn.TerminalAsync(tab);
        Assert.Equal([tab.Path, "--workdir", tab.Path], await Recorded(konsole));

        // Custom shell with arguments: the folder is the working directory only.
        var shell = Stub("my-shell");
        AppPreferences.Current.Terminal = new TerminalSetting("custom", shell, "--login -c \"echo hi\"");
        await OpenIn.TerminalAsync(tab);
        Assert.Equal([tab.Path, "--login", "-c", "echo hi"], await Recorded(shell));

        _errors.Clear();
        AppPreferences.Current.Terminal = new TerminalSetting("custom", "/no/such/terminal", "");
        await OpenIn.TerminalAsync(tab);
        Assert.Equal(["Error: Cannot find the terminal at '/no/such/terminal'"], _errors);

        // The OS error text when the start fails (not a program).
        _errors.Clear();
        OpenIn.UseSetsid = false; // through setsid the exec error stays inside setsid
        var broken = Path.Combine(_bin, "broken");
        File.WriteAllBytes(broken, [0, 1, 2, 3]);
        File.SetUnixFileMode(broken, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        AppPreferences.Current.Terminal = new TerminalSetting("custom", broken, "");
        await OpenIn.TerminalAsync(tab);
        var error = Assert.Single(_errors);
        Assert.StartsWith("Error: ", error);
        Assert.DoesNotContain("Cannot find", error);
    }

    [AvaloniaFact]
    public async Task Open_in_menu_lists_terminal_explorer_ides_editors_and_web_pages()
    {
        _repo.Git("remote", "add", "origin", "git@github.com:owner/repo.git");
        _repo.Git("remote", "add", "local", "/srv/git/repo.git");
        _repo.Git("remote", "add", "bb", "https://bitbucket.org/owner/repo.git"); // no web URL is built for Bitbucket
        // A GitLab Server account whose server URL is not http(s): no web page. (A file:/// server has no host, so it
        // could never match a remote; ftp:// has one.)
        _repo.Git("remote", "add", "self", "https://git.local/group/repo.git");
        var tab = await OpenTabAsync(new Account(HostType.GitLabServer, AuthType.AccessToken, "ftp://git.local", "me"));
        Assert.Equal(HostType.GitLabServer, tab.Hosted("self")!.Type);

        // Nothing installed: terminal and file manager only; the one hosted remote says "View on <service>".
        Assert.Equal("Open In Terminal [Ctrl+Alt+T]|Open In File Explorer [Ctrl+Alt+O]|-|View on GitHub", Flat(OpenIn.Menu(tab)));

        var code = Stub("code");
        Stub("zeditor");
        Stub("idea");
        var rider = Stub("rider");
        Directory.CreateDirectory(Path.Combine(_repo.Path, ".idea"));
        _repo.Write("App.sln", "");
        _repo.Write("Tools.slnx", "");
        _repo.Git("remote", "add", "upstream", "https://gitlab.com/group/repo.git");
        await tab.RefreshAsync();
        while (!tab.Refreshing.IsCompleted) await tab.Refreshing;
        AppPreferences.Current.Terminal = new TerminalSetting("custom", "/x", "");
        var menu = OpenIn.Menu(tab);
        Assert.Equal("Open In Shell [Ctrl+Alt+T]|Open In File Explorer [Ctrl+Alt+O]|-" +
                     "|Open '.idea' in IntelliJ IDEA|Open 'App.sln' in Rider|Open 'Tools.slnx' in Rider" +
                     "|Open in Visual Studio Code|Open in Zed" +
                     "|-|View origin on GitHub|View upstream on GitLab", Flat(menu));

        MenuEntry Item(string header) => menu.Single(e => e.Header == header);
        await Item("Open in Visual Studio Code").Invoke();
        Assert.Equal([tab.Path, tab.Path], await Recorded(code));
        await Item("Open 'App.sln' in Rider").Invoke();
        Assert.Equal([tab.Path, Path.Combine(tab.Path, "App.sln")], await Recorded(rider));
        await Item("Open In File Explorer").Invoke();
        await Item("View origin on GitHub").Invoke();
        await Item("View upstream on GitLab").Invoke();
        Assert.Equal([tab.Path, "https://github.com/owner/repo", "https://gitlab.com/group/repo"], _opened);
        Assert.Empty(_errors);

        // The editor went away after the menu was built; then it cannot be started.
        File.Delete(code);
        await Item("Open in Visual Studio Code").Invoke();
        // Through setsid: a file without an execute bit is refused before the start.
        File.WriteAllBytes(code, [0, 1, 2, 3]);
        File.SetUnixFileMode(code, UnixFileMode.UserRead);
        await Item("Open in Visual Studio Code").Invoke();
        // Without setsid: the OS error of a file that cannot be executed.
        OpenIn.UseSetsid = false;
        File.SetUnixFileMode(code, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        await Item("Open in Visual Studio Code").Invoke();
        Assert.Equal($"Error: Cannot find Visual Studio Code at '{code}'", _errors[0]);
        if (GitRunner.HasSetsid)
            Assert.Equal($"Error: Cannot run '{code} {tab.Path}'.\n'{code}' is not an executable file.", _errors[1]);
        Assert.StartsWith($"Error: Cannot run '{code} {tab.Path}'.\n", _errors[^1]);
        Assert.Equal(3, _errors.Count);
    }

    [AvaloniaFact]
    public async Task Toolbar_buttons_repository_menu_and_shortcuts_open_the_terminal_and_file_manager()
    {
        var main = new MainWindowViewModel();
        var window = new MainWindow { DataContext = main };
        window.Show();
        window.StatusTimer.Stop();
        try
        {
            // No repository tab: both buttons are disabled.
            Assert.False(window.FindControl<Button>("OpenInButton")!.IsEnabled);
            Assert.False(window.FindControl<Button>("TerminalButton")!.IsEnabled);

            var terminal = Stub("alacritty");
            Assert.Null(await main.OpenAsync(_repo.Path));
            var tab = main.SelectedTab!;
            var hosting = new HostingService(new AccountStore(Path.Combine(_bin, "accounts.json")), new FakeSecretStore());
            hosting.OpenBrowser = _opened.Add;
            hosting.OpenPath = p => { _opened.Add(p); return Task.CompletedTask; };
            tab.Hosting = hosting;
            tab.ShowError = (title, text) => { _errors.Add($"{title}: {text}"); return Task.CompletedTask; };
            Dispatcher.UIThread.RunJobs();
            var button = window.FindControl<Button>("TerminalButton")!;
            Assert.True(button.IsEnabled);
            Assert.True(window.FindControl<Button>("OpenInButton")!.IsEnabled);
            Assert.Equal("Terminal", ((StackPanel)button.Content!).Children.OfType<TextBlock>().Single().Text);

            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await window.LastCommand;
            Assert.Equal([tab.Path, "--working-directory", tab.Path], await Recorded(terminal));

            window.Focus();
            window.KeyPress(Key.T, RawInputModifiers.Control | RawInputModifiers.Alt, PhysicalKey.None, null);
            Dispatcher.UIThread.RunJobs();
            await window.LastCommand;
            Assert.Equal([tab.Path, "--working-directory", tab.Path], await Recorded(terminal));
            window.KeyPress(Key.O, RawInputModifiers.Control | RawInputModifiers.Alt, PhysicalKey.None, null);
            Dispatcher.UIThread.RunJobs();
            await window.LastCommand;
            Assert.Equal([tab.Path], _opened);

            // The Open in button opens the menu below itself.
            window.FindControl<Button>("OpenInButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(["Open In Terminal", "Open In File Explorer"],
                window.ToolbarMenu!.Items.OfType<MenuItem>().Select(m => (string)m.Header!));
            window.ToolbarMenu.Close();

            // A custom shell renames the button and the Repository menu item at once.
            AppPreferences.Current.Terminal = new TerminalSetting("custom", terminal, "");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Shell", ((StackPanel)button.Content!).Children.OfType<TextBlock>().Single().Text);
            var item = window.FindControl<MenuItem>("RepositoryMenu")!.Items.OfType<MenuItem>().Single(m => m.Tag as string == "terminal");
            Assert.Equal("Open In Shell", item.Header);
            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await window.LastCommand;
            Assert.Equal([tab.Path], await Recorded(terminal));
            Assert.Empty(_errors);
        }
        finally
        {
            main.CloseTab(null);
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Programs_start_in_their_own_session_through_setsid()
    {
        Assert.SkipUnless(GitRunner.HasSetsid, "util-linux setsid is not installed");
        var tab = await OpenTabAsync();
        var term = Path.Combine(_bin, "kitty");
        // Records "<pid> <session id>" (field 6 of /proc/<pid>/stat; the name "kitty" has no spaces).
        File.WriteAllText(term, "#!/bin/sh\nread -r stat < /proc/$$/stat\nset -- $stat\necho \"$1 $6\" > \"$0.out.tmp\"; mv \"$0.out.tmp\" \"$0.out\"\n");
        File.SetUnixFileMode(term, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await OpenIn.TerminalAsync(tab);
        var ids = Assert.Single(await Recorded(term)).Split(' ');
        Assert.Equal(ids[0], ids[1]); // session leader: closing GerGit's terminal does not end it
        Assert.Empty(_errors);
    }
}
