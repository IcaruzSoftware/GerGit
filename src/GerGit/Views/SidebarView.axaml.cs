using System.Diagnostics;
using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Input.Platform;
using GerGit.ViewModels;

namespace GerGit.Views;

public partial class SidebarView : UserControl
{
    /// <summary>Tree row metrics: (level - 1) x 10 px indent, section headers are level 1 (TreeViewItem.Level 0).</summary>
    public static readonly IValueConverter IndentConverter =
        new FuncValueConverter<int, Thickness>(level => new Thickness(level * 10, 0, 0, 0));

    SidebarNode? _selectedAtPress;
    Vector _offset;
    bool _hadFocus;

    public SidebarView()
    {
        InitializeComponent();
        // Tunnel: runs before the TreeView changes the selection on press.
        Tree.AddHandler(PointerPressedEvent, OnTreePointerPressed, RoutingStrategies.Tunnel);
        Tree.AddHandler(KeyDownEvent, OnTreeArrowKey, RoutingStrategies.Tunnel);
        Tree.Tapped += OnTreeTapped;
        Tree.DoubleTapped += OnTreeDoubleTapped;
        Tree.ContextRequested += OnTreeContextRequested;
    }

    SidebarViewModel Vm => (SidebarViewModel)DataContext!;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is not SidebarViewModel vm) return;
        vm.Copy = text => TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(text) ?? Task.CompletedTask;
        vm.FocusRequested += node => Dispatcher.UIThread.Post(() => Tree.TreeContainerFromItem(node)?.Focus(), DispatcherPriority.Loaded);
        // Updates and refresh: a rebuild keeps the scroll position.
        vm.PropertyChanging += (_, a) =>
        {
            if (a.PropertyName != nameof(SidebarViewModel.Roots)) return;
            if (TreeScroll is { } sv) _offset = sv.Offset;
            _hadFocus = Tree.IsKeyboardFocusWithin;
        };
        vm.PropertyChanged += (_, a) =>
        {
            if (a.PropertyName == nameof(SidebarViewModel.Roots))
                Dispatcher.UIThread.Post(() =>
                {
                    if (TreeScroll is { } sv) sv.Offset = _offset;
                    // The rebuild replaced the focused row: give the focus back to the selected one.
                    if (_hadFocus)
                        ((Control?)(vm.SelectedNode is { } n ? Tree.TreeContainerFromItem(n) : null) ?? Tree).Focus();
                }, DispatcherPriority.Loaded);
        };
    }

    void OnTreePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _selectedAtPress = Vm.SelectedNode;
        // Selection: a right click does not change the selection (the menu decides what it acts on).
        if (e.GetCurrentPoint(Tree).Properties.IsRightButtonPressed)
        {
            e.Handled = true;
            return;
        }
        // The "Show all" row is not selectable: the click toggles it and the TreeView never sees the press.
        if (e.Source is Visual v && v.FindAncestorOfType<TreeViewItem>(includeSelf: true)?.DataContext is SidebarNode { IsShowAll: true } node
            && e.GetCurrentPoint(v).Properties.IsLeftButtonPressed)
        {
            e.Handled = true;
            Vm.ToggleShowAll(node);
        }
    }

    ScrollViewer? TreeScroll => Tree.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();

    void OnTreeTapped(object? sender, TappedEventArgs e)
    {
        // A click on the already selected row (not on its expander) repeats the selection action.
        if (e.Source is not Visual v || v.FindAncestorOfType<ToggleButton>(includeSelf: true) is not null) return;
        if (v.FindAncestorOfType<TreeViewItem>(includeSelf: true)?.DataContext is SidebarNode node
            && node == _selectedAtPress && node == Vm.SelectedNode)
            Vm.Reselect();
    }

    void OnTreeArrowKey(object? sender, KeyEventArgs e)
    {
        // Ctrl+A selects the visible rows (the TreeView's own select-all also takes rows of collapsed nodes).
        if (e.Key == Key.A && e.KeyModifiers == KeyModifiers.Control)
        {
            e.Handled = true;
            Vm.Select(SidebarViewModel.VisibleRows(Vm.Roots).Where(n => !n.IsShowAll).ToList());
            return;
        }
        // The TreeView stops in front of a non-focusable row; Up/Down jump over a "Show all" row instead.
        if (e.Key is not (Key.Up or Key.Down) || e.KeyModifiers != KeyModifiers.None) return;
        if ((e.Source as Visual)?.FindAncestorOfType<TreeViewItem>(includeSelf: true)?.DataContext is not SidebarNode current) return;
        var rows = SidebarViewModel.VisibleRows(Vm.Roots).ToList();
        int step = e.Key == Key.Down ? 1 : -1, next = rows.IndexOf(current) + step;
        if (next < 0 || next >= rows.Count || !rows[next].IsShowAll) return;
        e.Handled = true;
        next += step;
        if (next >= 0 && next < rows.Count) Tree.TreeContainerFromItem(rows[next])?.Focus(NavigationMethod.Directional);
    }

    static SidebarNode? NodeOf(object? source) =>
        (source as Visual)?.FindAncestorOfType<TreeViewItem>(includeSelf: true)?.DataContext as SidebarNode;

    async void OnTreeContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        e.Handled = true;
        if (NodeOf(e.Source) is not { } node) return;
        try { await OpenMenuAsync(node); }
        catch (Exception ex) { Trace.WriteLine(ex); }
    }

    /// <summary>Builds and opens the context menu of a row (null: the row has none); also kept in <see cref="Menu"/>.</summary>
    internal async Task<ContextMenu?> OpenMenuAsync(SidebarNode node) =>
        Menu = await Vm.BuildMenuAsync(node) is { } entries ? MenuControls.Open(Tree, entries) : null;

    /// <summary>The last opened context menu (tests).</summary>
    internal ContextMenu? Menu { get; private set; }

    void OnTreeDoubleTapped(object? sender, TappedEventArgs e)
    {
        // Rows with an expander toggle in the TreeViewItem itself; the scroll bar has no row.
        if (e.Source is Visual v && v.FindAncestorOfType<ToggleButton>(includeSelf: true) is null && NodeOf(v) is { } node)
            Vm.DoubleClickAsync(node).Forget();
    }

    void OnFilterKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        Vm.FilterText = "";
        e.Handled = true;
    }

    void OnTreeKeyDown(object? sender, KeyEventArgs e)
    {
        // Keyboard and mouse: Esc clears the filter, Ctrl+F moves focus to it.
        if (e.Key == Key.Escape) { Vm.FilterText = ""; e.Handled = true; }
        else if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control)
        {
            Filter.Focus();
            Filter.SelectAll();
            e.Handled = true;
        }
        else
        {
            // F2 rename, Delete, Ctrl+C copy name.
            var action = (e.Key, e.KeyModifiers) switch
            {
                (Key.F2, KeyModifiers.None) => Vm.RenameAsync(),
                (Key.Delete, KeyModifiers.None) => Vm.DeleteAsync(),
                (Key.C, KeyModifiers.Control) => Vm.CopyNameAsync(),
                _ => null,
            };
            if (action is null) return;
            e.Handled = true;
            action.Forget();
        }
    }

    /// <summary>The last "Settings for This Repository..." of the settings button (tests await it).</summary>
    internal Task SettingsOpening { get; private set; } = Task.CompletedTask;

    /// <summary>The same Repository Settings window as the Repository menu entry.</summary>
    async void OnSettingsClick(object? sender, RoutedEventArgs e)
    {
        try { await (SettingsOpening = ViewModels.Preferences.RepositorySettingsViewModel.OpenAsync(Vm.Tab)); }
        catch (Exception ex) { Trace.WriteLine(ex); } // a failing window must not end the process
    }

    async void OnCopyPathClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
                await clipboard.SetTextAsync(Vm.Tab.Path);
        }
        catch (Exception ex) { Trace.WriteLine(ex); }
    }
}
