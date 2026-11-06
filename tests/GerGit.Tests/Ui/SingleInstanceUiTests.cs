using System.Runtime.Versioning;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using GerGit.Core;
using GerGit.ViewModels;
using GerGit.Views;

namespace GerGit.Tests.Ui;

/// <summary>Command line: a request forwarded by a later start reaches the running window
/// the way App wires it.</summary>
[Collection("SingleInstance")]
[SupportedOSPlatform("linux")]
public sealed class SingleInstanceUiTests
{
    [AvaloniaFact]
    public async Task A_forwarded_path_opens_or_selects_its_tab_and_activate_brings_the_window_back()
    {
        using var a = new TempRepo();
        a.Commit("a", 0, ("a.txt", "1\n"));
        using var b = new TempRepo();
        b.Commit("b", 0, ("b.txt", "1\n"));
        using var c = new TempRepo();
        c.Commit("c", 0, ("c.txt", "1\n"));
        using var notRepo = new TempRepo(init: false);
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "gergit-si-" + Guid.NewGuid().ToString("N")[..8]),
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute).FullName;
        var sock = Path.Combine(dir, "gergit.sock");

        var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm };
        window.Show();
        try
        {
            Assert.Null(await vm.OpenAsync(a.Path));
            using var instance = SingleInstance.Start(sock, null)!;
            var tail = App.AttachForwarding(window, vm, instance); // what App calls in the finally of window.Opened
            async Task Forward(params string?[] paths)
            {
                foreach (var p in paths)
                    Assert.Null(await Task.Run(() => SingleInstance.Start(sock, p))); // the later start exits
                await Dispatcher.UIThread.InvokeAsync(() => { }); // the posted requests have joined the chain
                await tail();
            }

            await Forward(b.Path); // not open yet: a new tab, selected
            Assert.Equal([a.Path, b.Path], vm.Tabs.Select(t => t.Path));
            Assert.Equal(b.Path, vm.SelectedTab!.Path);

            await Forward(a.Path); // already open: its tab is selected, nothing added
            Assert.Equal([a.Path, b.Path], vm.Tabs.Select(t => t.Path));
            Assert.Equal(a.Path, vm.SelectedTab!.Path);

            // Two starts right after another: the slow one (a new tab that loads) must not win over the newer one.
            await Forward(c.Path, a.Path);
            Assert.Equal([a.Path, b.Path, c.Path], vm.Tabs.Select(t => t.Path));
            Assert.Equal(a.Path, vm.SelectedTab!.Path);

            await Forward(notRepo.Path); // not a repository: nothing changes (only logged)
            Assert.Equal(3, vm.Tabs.Count);
            Assert.Equal(a.Path, vm.SelectedTab!.Path);

            window.WindowState = WindowState.Minimized;
            Assert.Equal(WindowState.Minimized, window.WindowState);
            await Forward([null]); // a start without argument: only brings the window back
            Assert.Equal(WindowState.Normal, window.WindowState);
            Assert.Equal(3, vm.Tabs.Count);

            window.WindowState = WindowState.Maximized;
            window.WindowState = WindowState.Minimized;
            await Forward([null]);
            Assert.Equal(WindowState.Maximized, window.WindowState); // the state before minimizing
        }
        finally
        {
            foreach (var t in vm.Tabs.ToList()) t.Dispose();
            window.Close();
            Directory.Delete(dir, recursive: true);
        }
    }
}
