using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using GerGit.ViewModels;
using GerGit.ViewModels.Dialogs;

namespace GerGit.Views.Dialogs;

public partial class AboutView : UserControl
{
    public AboutView() => InitializeComponent();

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (DataContext is AboutViewModel vm)
            vm.Copy = async text => { if (TopLevel.GetTopLevel(this)?.Clipboard is { } cb) await cb.SetTextAsync(text); };
    }

    void OnLinkClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is string url) HostingService.Current.OpenBrowser(url);
    }
}
