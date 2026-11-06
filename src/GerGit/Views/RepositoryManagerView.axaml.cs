using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GerGit.ViewModels;

namespace GerGit.Views;

public partial class RepositoryManagerView : UserControl
{
    public RepositoryManagerView()
    {
        InitializeComponent();
        Root.ColumnDefinitions[0].Width = new GridLength(Settings.GetLength(RepositoryManagerViewModel.TreeWidthKey, 350.0));
        // The tree width is saved when the splitter drag ends.
        Splitter.DragCompleted += (_, _) => Settings.Set(RepositoryManagerViewModel.TreeWidthKey, Root.ColumnDefinitions[0].ActualWidth);
        Tree.DoubleTapped += OnTreeDoubleTapped;
        Tree.KeyDown += OnTreeKeyDown;
        Tree.AddHandler(TextInputEvent, OnTreeTextInput, RoutingStrategies.Bubble);
        Tree.ContextRequested += OnTreeContextRequested;
        DropArea.AddHandler(DragDrop.DropEvent, OnDrop);
    }

    RepositoryManagerViewModel? Vm => DataContext as RepositoryManagerViewModel;

    /// <summary>The last opened context menu (tests).</summary>
    internal ContextMenu? Menu { get; private set; }

    // Opening from the manager shows the main window's failure dialogs (unsafe repository, not a repository).
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        HookOpen();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        HookOpen();
    }

    void HookOpen()
    {
        if (Vm is { } vm && TopLevel.GetTopLevel(this) is MainWindow window) vm.OpenPath = window.OpenPathAsync;
    }

    static RepoNode? NodeOf(object? source) =>
        (source as Visual)?.FindAncestorOfType<TreeViewItem>(includeSelf: true)?.DataContext as RepoNode;

    static async void Run(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { Trace.WriteLine(ex); }
    }

    void OnTreeDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (Vm is not { } vm || e.Source is not Visual v || v.FindAncestorOfType<ToggleButton>(includeSelf: true) is not null
            || NodeOf(v) is not { } node) return;
        if (node.IsRepository) Run(() => vm.OpenAsync(node));
        else node.IsExpanded = !node.IsExpanded;
    }

    void OnTreeKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm || e.Source is TextBox || e.KeyModifiers != KeyModifiers.None) return;
        var nodes = vm.SelectedNodes.ToList();
        switch (e.Key)
        {
            case Key.Enter when vm.SelectedNode is { IsRepository: true } repo:
                Run(() => vm.OpenAsync(repo));
                break;
            case Key.F2 when nodes is [{ IsRepository: true } one]:
                vm.BeginRename(one);
                break;
            case Key.Delete when RepositoryManagerViewModel.CanRemove(nodes):
                Run(() => vm.RemoveAsync(nodes));
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    // Typing a letter jumps to the next row whose name starts with it.
    void OnTreeTextInput(object? sender, TextInputEventArgs e)
    {
        if (Vm is not { } vm || e.Source is TextBox || string.IsNullOrWhiteSpace(e.Text)) return;
        vm.JumpTo(e.Text);
        if (vm.SelectedNode is { } node) Tree.TreeContainerFromItem(node)?.Focus();
        e.Handled = true;
    }

    void OnTreeContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        e.Handled = true;
        if (Vm is not { } vm || NodeOf(e.Source) is not { } node) return;
        if (!vm.SelectedNodes.Contains(node)) vm.Select(node); // the menu acts on the selection the row belongs to
        Menu = vm.Menu(vm.SelectedNodes.ToList()) is { } entries ? MenuControls.Open(Tree, entries) : null;
    }

    void OnDrop(object? sender, DragEventArgs e)
    {
        if (Vm is not { } vm) return;
        var folders = e.DataTransfer.TryGetFiles()?.OfType<IStorageFolder>().Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        if (folders is not { Count: > 0 }) return;
        e.Handled = true;
        var target = NodeOf(e.Source);
        Run(() => vm.DropAsync(folders, target));
    }

    // Rename in place: Enter or leaving the box commits, Esc cancels.
    void OnRenameKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm || (sender as Control)?.DataContext is not RepoNode node) return;
        if (e.Key == Key.Enter) vm.CommitRename(node);
        else if (e.Key == Key.Escape) vm.CancelRename(node);
        else return;
        e.Handled = true;
        Tree.Focus();
    }

    void OnRenameLostFocus(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && (sender as Control)?.DataContext is RepoNode { IsEditing: true } node) vm.CommitRename(node);
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

    void OnOpenClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && vm.SelectedNode is { IsRepository: true } node) Run(() => vm.OpenAsync(node));
    }

    void OnRemoveMissingClick(object? sender, RoutedEventArgs e)
    {
        if (Vm?.Details is { } d) Vm.RemoveMissing(d.Path);
    }

    void OnShowInFileManagerClick(object? sender, RoutedEventArgs e)
    {
        if (Vm?.Details is { } d) Run(() => RepositoryManagerViewModel.ShowInFileManagerAsync(d.Path));
    }

    void OnLinkClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is string url) HostingService.Current.OpenBrowser(url);
    }
}
