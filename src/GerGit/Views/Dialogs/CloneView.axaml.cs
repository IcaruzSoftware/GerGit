using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using GerGit.ViewModels;
using GerGit.ViewModels.Dialogs;

namespace GerGit.Views.Dialogs;

public partial class CloneView : UserControl
{
    Window? _window;

    public CloneView() => InitializeComponent();

    CloneViewModel? Vm => DataContext as CloneViewModel;

    /// <summary>The clipboard is read on open and again on every activation of the dialog.</summary>
    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (Vm is { } vm) vm.ReadClipboard = () => TopLevel.GetTopLevel(this)?.Clipboard?.TryGetTextAsync() ?? Task.FromResult<string?>(null);
        _window = TopLevel.GetTopLevel(this) as Window;
        if (_window is not null) _window.Activated += OnActivated;
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        if (_window is not null) _window.Activated -= OnActivated;
        _window = null;
        base.OnDetachedFromVisualTree(e);
    }

    async void OnActivated(object? sender, EventArgs e)
    {
        try { if (Vm is { } vm) await vm.PrefillFromClipboardAsync(); }
        catch (Exception ex) { Trace.WriteLine(ex); }
    }

    /// <summary>The protocol drop-down: the HTTPS / SSH forms as menu items; choosing one replaces the URL.</summary>
    void OnProtocolClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        MenuControls.Open((Control)sender!, vm.ProtocolForms.Select(url => new MenuEntry
        {
            Header = url,
            Action = () => { vm.Url = url; return Task.CompletedTask; },
        }), below: true);
    }

    /// <summary>A click on a repository row: its clone URL and account, also when it is the same row again.</summary>
    void OnRepoClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: CloneRepoRow row } && Vm is { } vm) vm.ChooseRepository(row);
    }

    /// <summary>Browse: folder chooser "Select location", starting in the current parent folder.</summary>
    async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage || Vm is not { } vm) return;
            var start = await storage.TryGetFolderFromPathAsync(vm.ParentFolder.Trim().Length > 0 ? vm.ParentFolder.Trim()
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Select location", SuggestedStartLocation = start });
            if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path) vm.ParentFolder = path;
        }
        catch (Exception ex) { Trace.WriteLine(ex); }
    }
}
