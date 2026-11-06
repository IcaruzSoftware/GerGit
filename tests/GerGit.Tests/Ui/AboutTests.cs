using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GerGit.Hosting;
using GerGit.Tests.Hosting;
using GerGit.ViewModels;
using GerGit.ViewModels.Dialogs;
using GerGit.Views;
using GerGit.Views.Dialogs;

namespace GerGit.Tests.Ui;

/// <summary>The Help menu and the About dialog. In the Askpass collection because it replaces the app-wide hosting
/// service (to record the opened web pages), like the other tests that do.</summary>
[Collection("Askpass")]
public sealed class AboutTests
{
    static void Settle(Window w)
    {
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    static T Find<T>(Window w, string name) where T : Control => w.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    static void Click(Control c) =>
        c.RaiseEvent(new RoutedEventArgs(c is MenuItem ? MenuItem.ClickEvent : Button.ClickEvent));

    [AvaloniaFact]
    public void Help_menu_opens_About_and_the_project_pages()
    {
        var dir = Directory.CreateTempSubdirectory("gergit-about-").FullName;
        var saved = HostingService.Current;
        var opened = new List<string>();
        var hosting = new HostingService(new AccountStore(Path.Combine(dir, "accounts.json")), new FakeSecretStore());
        hosting.OpenBrowser = opened.Add;
        HostingService.Current = hosting;
        var main = new MainWindowViewModel();
        var window = new MainWindow { DataContext = main };
        main.ShowDialog = vm => DialogWindow.ShowAsync(vm, window);
        try
        {
            window.Show();
            window.StatusTimer.Stop();
            Settle(window);

            // Help is the last menu, after Window.
            var menus = window.GetVisualDescendants().OfType<Menu>().First().Items.OfType<MenuItem>().ToList();
            Assert.Equal(["_Window", "_Help"], menus.TakeLast(2).Select(m => (string)m.Header!));
            var help = window.FindControl<MenuItem>("HelpMenu")!;
            Assert.Equal(["About GerGit", "-", "GitHub Page", "Report an Issue"],
                help.Items.OfType<Control>().Select(c => c is MenuItem m ? (string)m.Header! : "-"));
            MenuItem Item(string header) => help.Items.OfType<MenuItem>().Single(m => (string?)m.Header == header);

            Click(Item("GitHub Page"));
            Click(Item("Report an Issue"));
            Assert.Equal(["https://github.com/IcaruzSoftware/GerGit", "https://github.com/IcaruzSoftware/GerGit/issues/new/choose"], opened);

            // About: the version from the assembly (without "+commit"), the license and the links.
            Click(Item("About GerGit"));
            Settle(window);
            var d = (DialogWindow)window.OwnedWindows.Single();
            Settle(d);
            var version = typeof(AboutViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];
            Assert.Equal("About GerGit", d.Title);
            Assert.Equal("GerGit", Find<TextBlock>(d, "AppName").Text);
            Assert.Equal("Version " + version, Find<SelectableTextBlock>(d, "VersionText").Text);
            Assert.DoesNotContain('+', version);
            Assert.Equal("Licensed under the GNU General Public License v3.0", Find<TextBlock>(d, "License").Text);
            Assert.Equal("Inspired by Fork. Not affiliated with Fork or its developers.", Find<TextBlock>(d, "Credit").Text);
            Assert.Equal($".NET {Environment.Version} · Avalonia {AboutViewModel.AvaloniaVersion}", Find<SelectableTextBlock>(d, "RuntimeText").Text);
            opened.Clear();
            foreach (var link in new[] { "GitHubLink", "LicenseLink", "NoticesLink" }) Click(Find<Button>(d, link));
            Assert.Equal(["https://github.com/IcaruzSoftware/GerGit", "https://github.com/IcaruzSoftware/GerGit/blob/main/LICENSE",
                "https://github.com/IcaruzSoftware/GerGit/blob/main/THIRD-PARTY-NOTICES.md"], opened);

            // Copy Version Info puts the versions and the distribution on the clipboard.
            var vm = (AboutViewModel)d.DataContext!;
            Assert.NotNull(vm.Copy); // set by the view
            string? copied = null;
            vm.Copy = text => { copied = text; return Task.CompletedTask; };
            Assert.False(Find<Button>(d, "SubmitButton").IsVisible);
            Click(Find<Button>(d, "SecondaryButton"));
            Settle(d);
            Assert.StartsWith($"GerGit {version}, .NET {Environment.Version}, Avalonia {AboutViewModel.AvaloniaVersion}, ", copied);
            Assert.Equal("Version info copied.", Find<TextBlock>(d, "FooterText").Text);

            // Esc closes it.
            Assert.Equal("Close", Find<Button>(d, "CancelButton").Content);
            d.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Settle(window);
            Assert.False(d.IsVisible);
            Assert.Empty(window.OwnedWindows);
        }
        finally
        {
            HostingService.Current = saved;
            window.Close();
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Distribution_is_the_os_release_pretty_name()
    {
        Assert.Equal("Ubuntu 26.04 LTS", AboutViewModel.PrettyName("NAME=\"Ubuntu\"\nPRETTY_NAME=\"Ubuntu 26.04 LTS\"\nID=ubuntu\n"));
        Assert.Equal("Arch Linux", AboutViewModel.PrettyName("PRETTY_NAME='Arch Linux'"));
        Assert.Null(AboutViewModel.PrettyName("NAME=Debian\nPRETTY_NAME=\"\"\n"));
    }
}
