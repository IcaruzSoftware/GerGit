using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using GerGit.Core;
using GerGit.Core.Git;
using GerGit.ViewModels;
using GerGit.ViewModels.Dialogs;
using GerGit.Views.Dialogs;

namespace GerGit.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Activated += (_, _) => Vm.OnWindowActivated();
        // The toolbar width is split equally left/right of the status box. Inter is a wide font, so at 1000 px the right buttons would be clipped: never let the right column be narrower than them.
        ToolbarRight.SizeChanged += (_, _) => FitToolbar();
        ToolbarLeft.SizeChanged += (_, _) => FitToolbar();
        // Status box / "Activity Manager popup": re-evaluated every 200 ms.
        StatusTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background,
            (_, _) => (DataContext as MainWindowViewModel)?.Tick(DateTime.UtcNow));
        Opened += (_, _) => StatusTimer.Start();
        Closed += (_, _) =>
        {
            StatusTimer.Stop();
            (DataContext as MainWindowViewModel)?.StopSourceScan();
        };
        // Toolbar buttons: the Ctrl state is read at the moment of the click (buttons click on release).
        AddHandler(PointerReleasedEvent, (_, e) => _ctrlClick = e.KeyModifiers.HasFlag(KeyModifiers.Control),
            RoutingStrategies.Tunnel, handledEventsToo: true);
        // Any key-down ends a pending Quick Fetch (OnKeyDown re-arms it for Ctrl+Alt+Shift+F) and a remembered Ctrl click.
        AddHandler(KeyDownEvent, (_, _) => (_quickFetchArmed, _ctrlClick) = (false, false),
            RoutingStrategies.Tunnel, handledEventsToo: true);
        Deactivated += (_, _) => _quickFetchArmed = false;

        // Askpass prompts arrive on a socket thread and are shown
        // modally on the UI thread. Only in the desktop app: headless tests set their own delegates.
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime)
        {
            Askpass.PromptCredentials = p => Dispatcher.UIThread.InvokeAsync(() => CredentialsViewModel.AskAsync(p));
            Askpass.PromptHostKey = p => Dispatcher.UIThread.InvokeAsync(() => HostKeyViewModel.AskAsync(p));
        }
    }

    /// <summary>The 200 ms status-box / Activity Manager timer; tests stop it and call Tick with their own clock.</summary>
    internal DispatcherTimer StatusTimer { get; }

    bool _ctrlClick;
    bool _quickFetchArmed; // Ctrl+Alt+Shift+F went down unhandled: Quick Fetch fires when F is released

    /// <summary>The last toolbar / Repository menu / shortcut command (tests await it).</summary>
    internal Task LastCommand { get; private set; } = Task.CompletedTask;

    /// <summary>The last opened toolbar arrow menu (tests read its items).</summary>
    internal ContextMenu? ToolbarMenu { get; private set; }

    /// <summary>Repository-tab commands of the toolbar, the Repository menu and the global shortcuts.</summary>
    static Func<RepoTabViewModel, Task>? Command(string name) => name switch
    {
        "refresh" => t => t.RefreshAsync(),
        "fetch" => t => t.RemoteOps.OpenFetchAsync(),
        "quickFetch" => t => t.RemoteOps.QuickFetchAsync(),
        "pull" => t => t.RemoteOps.OpenPullAsync(),
        "quickPull" => t => t.RemoteOps.QuickPullAsync(),
        "push" => t => t.RemoteOps.OpenPushAsync(),
        "quickPush" => t => t.RemoteOps.QuickPushAsync(),
        "stash" => t => t.StashOps.SaveAsync(),
        "branch" => t => t.RefOps.CreateBranchAsync(),
        "tag" => t => t.RefOps.CreateTagAsync(),
        "worktree" => t => t.WorktreeOps.CreateAsync(),
        "terminal" => OpenIn.TerminalAsync,
        "fileExplorer" => OpenIn.FileExplorerAsync,
        "repositorySettings" => ViewModels.Preferences.RepositorySettingsViewModel.OpenAsync,
        _ => null,
    };

    /// <summary>Runs a command on the active repository tab; nothing without one ("needs repo").</summary>
    void RunCommand(string name)
    {
        if (Vm.SelectedTab is { } tab && Command(name) is { } command) LastCommand = Safe(() => command(tab));
    }

    void OnCommandClick(object? sender, RoutedEventArgs e) => RunCommand((string)((Control)sender!).Tag!);

    /// <summary>Fetch / Pull / Push with Ctrl held run the quick variant; Stash and Branch are the same either way.</summary>
    void OnToolbarClick(object? sender, RoutedEventArgs e)
    {
        var name = (string)((Control)sender!).Tag!;
        bool ctrl = _ctrlClick;
        _ctrlClick = false;
        RunCommand(ctrl && name is "fetch" or "pull" or "push" ? "quick" + char.ToUpperInvariant(name[0]) + name[1..] : name);
    }

    void OnStashArrowClick(object? sender, RoutedEventArgs e)
    {
        if (Vm.SelectedTab is { } tab) ToolbarMenu = MenuControls.Open((Control)sender!, Menus.StashArrow(tab), below: true);
    }

    void OnBranchArrowClick(object? sender, RoutedEventArgs e)
    {
        if (Vm.SelectedTab is { } tab) ToolbarMenu = MenuControls.Open((Control)sender!, Menus.BranchArrow(tab), below: true);
    }

    void OnOpenInClick(object? sender, RoutedEventArgs e)
    {
        if (Vm.SelectedTab is { } tab) ToolbarMenu = MenuControls.Open((Control)sender!, OpenIn.Menu(tab), below: true);
    }

    /// <summary>Appearance menu; Ctrl+click switches light/dark without the menu.</summary>
    void OnAppearanceClick(object? sender, RoutedEventArgs e)
    {
        bool ctrl = _ctrlClick;
        _ctrlClick = false;
        if (ctrl) AppTheme.Switch();
        else ToolbarMenu = MenuControls.Open((Control)sender!, Vm.AppearanceMenu(), below: true);
    }

    /// <summary>Window > Switch Theme.</summary>
    void OnSwitchThemeClick(object? sender, RoutedEventArgs e) => AppTheme.Switch();

    /// <summary>Workspaces menu; Ctrl+click switches to the next workspace without the menu.</summary>
    void OnWorkspacesClick(object? sender, RoutedEventArgs e)
    {
        bool ctrl = _ctrlClick;
        _ctrlClick = false;
        if (ctrl) LastCommand = Safe(Vm.NextWorkspaceAsync);
        else ToolbarMenu = MenuControls.Open((Control)sender!, Vm.WorkspacesMenu(), below: true);
    }

    /// <summary>New Tab: "+", Ctrl+T and File > New Tab.</summary>
    void OnNewTabClick(object? sender, RoutedEventArgs e) => Vm.NewTab();

    void OnManagerCloseClick(object? sender, RoutedEventArgs e) => Vm.CloseManager();

    void OnZoomInClick(object? sender, RoutedEventArgs e) => Vm.ZoomIn();

    void OnZoomOutClick(object? sender, RoutedEventArgs e) => Vm.ZoomOut();

    void OnActivityClick(object? sender, RoutedEventArgs e) => Vm.OpenActivity();

    void OnStatusTextPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!Vm.Activity.IsOpen) Vm.OpenActivity();
    }

    void FitToolbar()
    {
        Toolbar.ColumnDefinitions[0].MinWidth = NaturalWidth(ToolbarLeft);
        Toolbar.ColumnDefinitions[2].MinWidth = NaturalWidth(ToolbarRight);
    }

    static double NaturalWidth(Control c)
    {
        c.Measure(Size.Infinity);
        return c.DesiredSize.Width;
    }

    MainWindowViewModel Vm => (MainWindowViewModel)DataContext!;

    /// <summary>Opens a folder as a tab and shows the failure message, offering "Initialize git repository here" where that is possible.</summary>
    public async Task OpenPathAsync(string path)
    {
        var failure = await Vm.OpenAsync(path);
        while (failure is not null)
        {
            if (failure.Title == "Git Error")
            {
                // E.g. an unsafe repository offers "Mark repository as safe", then opens it again.
                bool retry = false;
                var error = GitErrorViewModel.Create(GitErrors.Classify(failure.Text), null, path, () => { retry = true; return Task.CompletedTask; });
                await DialogWindow.ShowAsync(error, this);
                if (error.ChosenAction is { } action) await action();
                if (!retry) return;
                failure = await Vm.OpenAsync(path);
                continue;
            }
            var init = await MessageWindow.ShowAsync(this, failure.Title, failure.Text,
                failure.CanInit ? "Initialize git repository here" : null);
            if (!init) return;
            failure = await Vm.InitAsync(path);
        }
    }

    /// <summary>File > Open Repository...: native folder chooser starting in the home folder.</summary>
    async Task PickAndOpenAsync()
    {
        var home = await StorageProvider.TryGetFolderFromPathAsync(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Open Repository...",
            SuggestedStartLocation = home,
        });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
            await OpenPathAsync(path);
    }

    async void OnOpenClick(object? sender, RoutedEventArgs e) => await Safe(PickAndOpenAsync);

    /// <summary>The open Quick Launch window, else null (tests drive it).</summary>
    internal QuickLaunchWindow? QuickLaunch { get; private set; }

    void OnQuickLaunchClick(object? sender, RoutedEventArgs e) => OpenQuickLaunch();

    /// <summary>Quick Launch: toolbar button, File > Quick Launch..., Ctrl+P; Ctrl+B (<paramref name="checkout"/>)
    /// also activates "Checkout Branch" when it is offered. A chosen command runs after the window closed.</summary>
    internal void OpenQuickLaunch(bool checkout = false)
    {
        if (QuickLaunch is { } open)
        {
            open.Activate();
            return;
        }
        var vm = new QuickLaunchViewModel(Vm, PickAndOpenAsync, OpenPathAsync);
        var w = new QuickLaunchWindow(vm);
        // Only the choice that closes the window runs: not after Esc or a focus loss, and never twice.
        vm.Done = run =>
        {
            if (w.Dismiss()) Dispatcher.UIThread.Post(() => LastCommand = Safe(run));
        };
        // States and messages: no refresh on the activation that follows a Quick Launch window.
        w.Closed += (_, _) =>
        {
            QuickLaunch = null;
            (Vm.IsQuickLaunchOpen, Vm.QuickLaunchClosedMs) = (false, Environment.TickCount64);
        };
        QuickLaunch = w;
        Vm.IsQuickLaunchOpen = true;
        w.Show(this);
        if (checkout) vm.ActivateCommand("Checkout Branch");
    }

    /// <summary>For async void handlers: an unexpected exception is logged instead of ending the process.</summary>
    static async Task Safe(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { Trace.WriteLine(ex); }
    }

    /// <summary>File > Clone....</summary>
    async void OnCloneClick(object? sender, RoutedEventArgs e) => await Safe(Vm.OpenCloneAsync);

    /// <summary>File > Accounts....</summary>
    async void OnAccountsClick(object? sender, RoutedEventArgs e) => await Safe(HostingService.Current.OpenAccountsAsync);

    /// <summary>File > Preferences....</summary>
    async void OnPreferencesClick(object? sender, RoutedEventArgs e) => await Safe(Vm.OpenPreferencesAsync);

    void OnExitClick(object? sender, RoutedEventArgs e) => Close();

    /// <summary>Help > About GerGit.</summary>
    async void OnAboutClick(object? sender, RoutedEventArgs e) => await Safe(Vm.OpenAboutAsync);

    /// <summary>Help > GitHub Page / Report an Issue: the web page in the item's Tag.</summary>
    void OnWebPageClick(object? sender, RoutedEventArgs e) => HostingService.Current.OpenBrowser((string)((Control)sender!).Tag!);

    void OnShowLocalChanges(object? sender, RoutedEventArgs e) => Vm.SelectedTab?.ShowLocalChanges();

    void OnShowAllCommits(object? sender, RoutedEventArgs e)
    {
        if (Vm.SelectedTab is { } tab) tab.IsAllCommitsView = true;
    }

    void OnTabPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        // Middle click on a tab closes it.
        if (e.InitialPressMouseButton != MouseButton.Middle) return;
        if (sender is Control { DataContext: RepoTabViewModel tab }) Vm.CloseTab(tab);
        else if (sender is Control { DataContext: RepositoryManagerViewModel }) Vm.CloseManager();
    }

    protected override async void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;
        e.Handled = true;
        switch (e.Key, e.KeyModifiers)
        {
            case (Key.O, KeyModifiers.Control): await Safe(PickAndOpenAsync); break;
            case (Key.P, KeyModifiers.Control): OpenQuickLaunch(); break;
            case (Key.B, KeyModifiers.Control): OpenQuickLaunch(checkout: true); break;
            case (Key.N, KeyModifiers.Control): await Safe(Vm.OpenCloneAsync); break;
            case (Key.W or Key.F4, KeyModifiers.Control): Vm.CloseTab(null); break;
            case (Key.T, KeyModifiers.Control): Vm.NewTab(); break;
            case (Key.OemComma, KeyModifiers.Control): await Safe(Vm.OpenPreferencesAsync); break;
            // Layout zoom: Ctrl+= and Ctrl+- / Ctrl+Numpad minus; also Ctrl+Numpad plus and Ctrl+Shift+= (Ctrl++ on US keyboards).
            case (Key.OemPlus or Key.Add, KeyModifiers.Control or CtrlShift): Vm.ZoomIn(); break;
            case (Key.OemMinus or Key.Subtract, KeyModifiers.Control): Vm.ZoomOut(); break;
            case (Key.F5, KeyModifiers.None): await Safe(() => Vm.RefreshCommand.ExecuteAsync(null)); break;
            // View: Ctrl+1 / Ctrl+2, also on the number pad.
            case (Key.D1 or Key.NumPad1, KeyModifiers.Control) when Vm.SelectedTab is { } tab: tab.ShowLocalChanges(); break;
            case (Key.D2 or Key.NumPad2, KeyModifiers.Control) when Vm.SelectedTab is { } tab: tab.IsAllCommitsView = true; break;
            // Keyboard and mouse: repository commands (a focused text box that handles the key comes first).
            case (Key.F, CtrlShift): RunCommand("fetch"); break;
            case (Key.L, CtrlShift): RunCommand("pull"); break;
            case (Key.P, CtrlShift): RunCommand("push"); break;
            case (Key.H, CtrlShift): RunCommand("stash"); break;
            case (Key.B, CtrlShift): RunCommand("branch"); break;
            case (Key.G, CtrlShift): RunCommand("tag"); break;
            case (Key.F, CtrlShiftAlt): _quickFetchArmed = true; break; // fires on key release
            case (Key.L, CtrlShiftAlt): RunCommand("quickPull"); break;
            case (Key.P, CtrlShiftAlt): RunCommand("quickPush"); break;
            case (Key.T, CtrlAlt): RunCommand("terminal"); break;
            case (Key.O, CtrlAlt) when Vm.IsManagerShown: LastCommand = Safe(Vm.Manager!.ShowSelectedInFileManagerAsync); break;
            case (Key.O, CtrlAlt): RunCommand("fileExplorer"); break;
            default: e.Handled = false; break;
        }
    }

    const KeyModifiers CtrlShift = KeyModifiers.Control | KeyModifiers.Shift, CtrlShiftAlt = CtrlShift | KeyModifiers.Alt;
    const KeyModifiers CtrlAlt = KeyModifiers.Control | KeyModifiers.Alt;

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.Key != Key.F || !_quickFetchArmed) return;
        _quickFetchArmed = false; // any release of F ends it; it fires only with the modifiers still held
        if (!e.KeyModifiers.HasFlag(CtrlShiftAlt)) return;
        e.Handled = true;
        RunCommand("quickFetch");
    }
}
