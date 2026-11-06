using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using GerGit.ViewModels.Dialogs;

namespace GerGit.Views.Dialogs;

public partial class ConfigureWorkspacesView : UserControl
{
    public ConfigureWorkspacesView() => InitializeComponent();

    ConfigureWorkspacesViewModel Vm => (ConfigureWorkspacesViewModel)DataContext!;

    static WorkspaceItem? ItemOf(object? sender) => (sender as Control)?.DataContext as WorkspaceItem;

    void OnAddClick(object? sender, RoutedEventArgs e) => Vm.Add();

    void OnRenameClick(object? sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) Vm.BeginRename(item);
    }

    async void OnRemoveClick(object? sender, RoutedEventArgs e) => await RemoveAsync(Vm.SelectedItem);

    async void OnRemoveRowClick(object? sender, RoutedEventArgs e) => await RemoveAsync(ItemOf(sender));

    async Task RemoveAsync(WorkspaceItem? item)
    {
        try { if (item is not null) await Vm.RemoveAsync(item); }
        catch (Exception ex) { Trace.WriteLine(ex); }
    }

    void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.F2 || e.Source is TextBox || Vm.SelectedItem is not { } item) return;
        Vm.BeginRename(item);
        e.Handled = true;
    }

    // Enter or leaving the box commits, Esc cancels.
    void OnRenameKeyDown(object? sender, KeyEventArgs e)
    {
        if (ItemOf(sender) is not { } item) return;
        if (e.Key == Key.Enter) Vm.CommitRename(item);
        else if (e.Key == Key.Escape) Vm.CancelRename(item);
        else return;
        e.Handled = true;
        List.Focus();
    }

    void OnRenameLostFocus(object? sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { IsEditing: true } item) Vm.CommitRename(item);
    }

    void OnRenameBoxPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != IsVisibleProperty || sender is not TextBox { IsVisible: true } box) return;
        Dispatcher.UIThread.Post(() =>
        {
            box.Focus();
            box.SelectAll();
        }, DispatcherPriority.Loaded);
    }
}
