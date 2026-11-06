using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using GerGit.Core;
using GerGit.Core.Git;
using GerGit.Hosting;
using GerGit.ViewModels;
using GerGit.Views;

namespace GerGit;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        Zoom.Install(this); // every window and popup follows the layout scale
        _ = AppTheme.Install(this); // dark or light, following the OS unless chosen by hand
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // The one account list + keyring (before any reader exists), the git credential hooks, and
            // "refresh remotes of the active repository tab" after every account change.
            var hosting = new HostingService(new AccountStore(AccountStore.DefaultFilePath), new LibSecretStore());
            HostingService.Current = hosting;
            hosting.Install();
            GitRepository.UpdateSubmodulesOnCheckout = AppPreferences.UpdateSubmodules;
            Avatars.Enabled = AppPreferences.LoadAvatars;
            var vm = new MainWindowViewModel();
            var window = new MainWindow { DataContext = vm };
            desktop.MainWindow = window;
            Askpass.Start();
            desktop.Exit += (_, _) => Askpass.Stop();
            hosting.Store.Changed += () => Dispatcher.UIThread.Post(() => _ = vm.SelectedTab?.RefreshAsync());
            vm.AutoFetch.Start(() => window.IsActive); // Automatic background fetch
            hosting.Notifications.BringToFront = window.Activate; // Notifications
            hosting.Notifications.Start();
            window.Closing += (_, _) => vm.SaveSession(); // Session
            // Command line: stop listening with the window, while the dispatcher still runs;
            // a later start then becomes the new instance instead of getting an "ok" nobody acts on.
            window.Closed += (_, _) => SingleInstance.Current?.Dispose();
            window.Opened += async (_, _) =>
            {
                try
                {
                    if (await MainWindowViewModel.CheckGitAsync() is { } failure)
                    {
                        await MessageWindow.ShowAsync(window, failure.Title, failure.Text);
                        vm.NewTab(); // the start screen instead of an empty window
                        return;
                    }
                    // Command line: exactly one argument is opened; failures are only logged.
                    // After the session restore: an open tab of it is selected by the restore (so only that
                    // tab loads), else OpenAsync adds it at the end.
                    var path = desktop.Args is [{ Length: > 0 } arg] ? Path.GetFullPath(arg) : null;
                    try { await vm.RestoreSessionAsync(path); }
                    catch (Exception e) { Trace.WriteLine(e); } // the argument below still opens
                    if (path is not null && await vm.OpenAsync(path) is { } openFailure)
                        Trace.WriteLine($"{openFailure.Title}: {openFailure.Text}");
                }
                catch (Exception e)
                {
                    Trace.WriteLine(e);
                }
                finally
                {
                    // Command line: later starts forward to us. Only from here on (they
                    // wait in a queue until then), so a forwarded path cannot race the restore; also after a git error.
                    if (SingleInstance.Current is { } instance) AttachForwarding(window, vm, instance);
                }
            };
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Hands the requests of later starts (<see cref="SingleInstance"/>) to <see cref="OnForwardedAsync"/> on
    /// the UI thread, one after the other in arrival order, so the newest start decides the selected tab. Returns the
    /// tail of that chain (tests await it). Remembers the last state other than minimized for the de-iconify.</summary>
    internal static Func<Task> AttachForwarding(Window window, MainWindowViewModel vm, SingleInstance instance)
    {
        var restore = window.WindowState == WindowState.Minimized ? WindowState.Normal : window.WindowState;
        window.PropertyChanged += (_, e) =>
        {
            if (e.Property == Window.WindowStateProperty && window.WindowState != WindowState.Minimized)
                restore = window.WindowState;
        };
        Task last = Task.CompletedTask; // only touched on the UI thread
        instance.Attach(p => Dispatcher.UIThread.Post(() => last = ChainAsync(last, p)));
        return () => last;

        async Task ChainAsync(Task previous, string? path)
        {
            await previous; // never faults: OnForwardedAsync logs
            await OnForwardedAsync(window, vm, path, restore);
        }
    }

    /// <summary>A request forwarded by a later start: bring the main window to the front (de-iconified to
    /// <paramref name="restore"/>) and open the path or select its tab (null = only activate). Failures are only
    /// logged, like the own argument. Limitation: X11 window managers with focus stealing prevention may only mark it
    /// urgent instead of raising it.</summary>
    internal static async Task OnForwardedAsync(Window window, MainWindowViewModel vm, string? path, WindowState restore)
    {
        try
        {
            if (window.WindowState == WindowState.Minimized) window.WindowState = restore;
            window.Activate();
            if (path is not null && await vm.OpenAsync(path) is { } failure)
                Trace.WriteLine($"{failure.Title}: {failure.Text}");
        }
        catch (Exception e)
        {
            Trace.WriteLine(e);
        }
    }
}
