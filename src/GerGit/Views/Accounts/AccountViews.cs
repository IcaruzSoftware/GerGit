using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using GerGit.ViewModels.Accounts;

namespace GerGit.Views.Accounts;

// Bodies of the account windows; hosted by DialogWindow.

public partial class AccountsView : UserControl
{
    public AccountsView() => InitializeComponent();
}

public partial class AddAccountView : UserControl
{
    public AddAccountView() => InitializeComponent();

    /// <summary>Double-click on a tile = Log in.</summary>
    async void OnTileDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not AddAccountViewModel { SelectedTile: not null } vm) return;
        try { await vm.SubmitAsync(); }
        catch (Exception ex) { Trace.WriteLine(ex); }
    }
}

public partial class LoginView : UserControl
{
    public LoginView() => InitializeComponent();

    LoginViewModel? Vm => DataContext as LoginViewModel;

    async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage || Vm is not { } vm) return;
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "CA File", AllowMultiple = false });
            if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) vm.CaFile = path;
        }
        catch (Exception ex) { Trace.WriteLine(ex); }
    }

    async void OnCopyCode(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (Vm?.UserCode is { } code && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(code);
        }
        catch (Exception ex) { Trace.WriteLine(ex); }
    }
}
