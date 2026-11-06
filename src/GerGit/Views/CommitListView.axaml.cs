using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using GerGit.ViewModels;

namespace GerGit.Views;

public partial class CommitListView : UserControl
{
    bool _syncing;
    CommitListViewModel? _vm;

    public CommitListView()
    {
        InitializeComponent();
        List.ScrollChanged += () =>
        {
            _syncing = true;
            Bar.Maximum = List.MaxOffset;
            Bar.ViewportSize = List.Bounds.Height;
            // Steps follow the current row height (42 in the double-row layout); a layout switch raises ScrollChanged.
            Bar.SmallChange = List.RowH;
            Bar.LargeChange = Math.Max(List.RowH, List.Bounds.Height - List.RowH);
            Bar.Value = List.Offset;
            _syncing = false;
        };
        Bar.ValueChanged += (_, e) =>
        {
            if (!_syncing) List.Offset = e.NewValue;
        };
        // Tunnel: the search keys come before the text box and the list see them.
        AddHandler(KeyDownEvent, OnSearchKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null) _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = DataContext as CommitListViewModel;
        if (_vm is not null) _vm.PropertyChanged += OnVmPropertyChanged;
        List.ViewModel = _vm;
        SearchBar.Height = _vm?.IsSearchOpen == true ? 30 : 0;
    }

    void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CommitListViewModel.IsSearchOpen)) return;
        SearchBar.Height = _vm!.IsSearchOpen ? 30 : 0;
        if (_vm.IsSearchOpen) Dispatcher.UIThread.Post(FocusSearchBox); // after the bar is enabled
    }

    void FocusSearchBox()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    /// <summary>Search bar and "Keys inside the search box".</summary>
    void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is not { } vm) return;
        bool inBox = SearchBox.IsKeyboardFocusWithin;
        switch (e.Key, e.KeyModifiers)
        {
            case (Key.F, KeyModifiers.Control) or (Key.F3, KeyModifiers.None) when !inBox && List.IsKeyboardFocusWithin:
                if (vm.IsSearchOpen) FocusSearchBox(); // pressed again: select the text, focus stays in the box
                else vm.OpenSearch();
                break;
            case (Key.F, KeyModifiers.Control) when inBox:
                SearchBox.SelectAll();
                break;
            case (Key.Enter or Key.F3, KeyModifiers.None) when inBox:
                vm.NextMatchAsync().Forget();
                break;
            case (Key.Enter or Key.F3, KeyModifiers.Shift) when inBox:
                vm.PreviousMatchAsync().Forget();
                break;
            case (Key.Escape, KeyModifiers.None) when vm.IsSearchOpen:
                vm.CloseSearch();
                break;
            default:
                return;
        }
        e.Handled = true;
    }
}
