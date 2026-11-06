using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using GerGit.Core.Rebase;
using GerGit.ViewModels;
using GerGit.Views.Dialogs;

namespace GerGit.Views;

/// <summary>
/// The interactive rebase window. Keys, drag and drop, the merged selection blocks and the reword popup's
/// focus rules live here; everything else is <see cref="InteractiveRebaseViewModel"/>. Closes only when git has ended.
/// Limitation: no drag ghost image (the drop line shows the target); any Ctrl works for Ctrl+Up/Down.
/// </summary>
public partial class InteractiveRebaseWindow : Window
{
    public const string GeometryKey = "windows.interactiveRebase";
    const double RowHeight = 22, DragThreshold = 4;

    readonly InteractiveRebaseViewModel? _vm;
    bool _syncing;
    int _pressRow = -1;          // left press on a row (a drag may start from it)
    bool _collapseOnRelease;     // the press hit an already selected row: selection changes on mouse-up
    bool _dragging;
    Point _pressPoint;
    (int Row, bool After)? _dropTarget;

    public InteractiveRebaseWindow() => InitializeComponent();

    public InteractiveRebaseWindow(InteractiveRebaseViewModel vm) : this()
    {
        _vm = vm;
        DataContext = vm;
        WindowGeometry.Apply(this, GeometryKey);
        vm.Confirm = d => DialogWindow.ShowAsync(d, this);
        vm.SelectionChanged += SyncSelection;
        vm.CloseRequested += OnCloseRequested;
        vm.PropertyChanged += OnVmPropertyChanged;
        TodoList.SelectionChanged += OnListSelectionChanged;
        TodoList.ContainerPrepared += (_, _) => UpdateCorners();
        TodoList.AddHandler(KeyDownEvent, OnListKeyDown, RoutingStrategies.Tunnel);
        TodoList.AddHandler(PointerPressedEvent, OnListPointerPressed, RoutingStrategies.Tunnel);
        TodoList.AddHandler(PointerMovedEvent, OnListPointerMoved, RoutingStrategies.Tunnel);
        TodoList.AddHandler(PointerReleasedEvent, OnListPointerReleased, RoutingStrategies.Tunnel);
        RewordSubject.AddHandler(KeyDownEvent, OnSubjectKeyDown, RoutingStrategies.Tunnel);
        RewordDescription.AddHandler(KeyDownEvent, OnDescriptionKeyDown, RoutingStrategies.Tunnel);
        RewordSubject.PastingFromClipboard += OnSubjectPasting;
        KeyDown += OnWindowKeyDown;
        Closing += (_, e) =>
        {
            if (vm.IsClosed) return;
            e.Cancel = true; // the window closes only when git has finished
            vm.CancelAsync().Forget();
        };
        Closed += (_, _) =>
        {
            vm.SelectionChanged -= SyncSelection;
            vm.CloseRequested -= OnCloseRequested;
            vm.PropertyChanged -= OnVmPropertyChanged;
        };
        SyncSelection();
    }

    /// <summary>Shows the window modally over <paramref name="owner"/> (default: the main window) until git has ended.
    /// Without a visible owner (headless) the rebase is cancelled.</summary>
    public static async Task ShowAsync(InteractiveRebaseViewModel vm, Window? owner = null)
    {
        owner ??= (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner is not { IsVisible: true })
        {
            Trace.WriteLine("Interactive rebase: no window to show it in, cancelled");
            await vm.CancelAsync();
            return;
        }
        var w = new InteractiveRebaseWindow(vm);
        if (vm.IsClosed) return; // git ended before the window could open
        await w.ShowDialog(owner);
    }

    void OnCloseRequested() => Close();

    // ------------------------------------------------------------------ selection

    void OnListSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_syncing) _vm?.SelectRows(TodoList.Selection.SelectedIndexes);
        UpdateCorners();
    }

    /// <summary>The view model's selection into the list; the first row is scrolled into view and focused.</summary>
    void SyncSelection()
    {
        if (_vm is null) return;
        _syncing = true;
        try
        {
            var sel = TodoList.Selection;
            sel.BeginBatchUpdate();
            sel.Clear();
            foreach (var i in _vm.Selection) sel.Select(i);
            sel.EndBatchUpdate();
        }
        finally { _syncing = false; }
        if (_vm.Selection.Count > 0 && _vm.Reword is null)
        {
            TodoList.ScrollIntoView(_vm.Selection[0]);
            TodoList.ContainerFromIndex(_vm.Selection[0])?.Focus();
        }
        UpdateCorners();
    }

    /// <summary>Consecutive selected rows form one block, rounded only at its top and bottom.</summary>
    void UpdateCorners()
    {
        foreach (var c in TodoList.GetRealizedContainers())
        {
            int i = TodoList.IndexFromContainer(c);
            bool top = !TodoList.Selection.IsSelected(i - 1), bottom = !TodoList.Selection.IsSelected(i + 1);
            ((ListBoxItem)c).CornerRadius = new CornerRadius(top ? 4 : 0, top ? 4 : 0, bottom ? 4 : 0, bottom ? 4 : 0);
        }
    }

    // ------------------------------------------------------------------ keys

    void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is null) return;
        if (e.KeyModifiers == KeyModifiers.Control && e.Key is Key.Up or Key.Down)
        {
            if (e.Key == Key.Up) _vm.MoveUp();
            else _vm.MoveDown();
            e.Handled = true;
            return;
        }
        if (e.KeyModifiers != KeyModifiers.None) return;
        RebaseAction? action = e.Key switch
        {
            Key.P => RebaseAction.Pick,
            Key.E => RebaseAction.Edit,
            Key.S => RebaseAction.Squash,
            Key.F => RebaseAction.Fixup,
            Key.D => RebaseAction.Drop,
            _ => null,
        };
        if (action is { } a) _vm.SetAction(a);
        else if (e.Key == Key.R) _vm.RewordKey();
        else return;
        e.Handled = true;
    }

    void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || e.Handled || _vm is null) return;
        e.Handled = true;
        if (_vm.Reword is not null) _vm.CloseReword(false);
        else _vm.CancelAsync().Forget();
    }

    void OnCancel(object? sender, RoutedEventArgs e) => _vm?.CancelAsync().Forget();

    void OnRebase(object? sender, RoutedEventArgs e) => _vm?.RebaseAsync().Forget();

    /// <summary>A row's action dropdown: only real choices (a recycled or rebuilt row re-selects its own action).</summary>
    void OnActionChosen(object? sender, SelectionChangedEventArgs e)
    {
        if (_vm is null || sender is not ComboBox { DataContext: RebaseItem item } box) return;
        if (box.SelectedIndex < 0 || box.SelectedIndex == item.ActionIndex) return;
        int row = _vm.Items.IndexOf(item);
        var choice = ActionChoice.All[box.SelectedIndex];
        // Move Up/Down return to the action; a real change rebuilds the row anyway. SetCurrentValue keeps the binding.
        box.SetCurrentValue(SelectingItemsControl.SelectedIndexProperty, item.ActionIndex);
        _vm.ChooseAction(row, choice);
    }

    // ------------------------------------------------------------------ mouse: double-click, drag and drop

    int RowAt(object? source) =>
        source is Visual v && v.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { } item ? TodoList.IndexFromContainer(item) : -1;

    void OnListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(TodoList);
        if (_vm is null || !point.Properties.IsLeftButtonPressed) return;
        if (e.Source is Visual v && v.FindAncestorOfType<ComboBox>(includeSelf: true) is not null) return; // the dropdown
        int row = RowAt(e.Source);
        if (row < 0) return; // scrollbar or empty space
        if (e.ClickCount == 2)
        {
            _vm.DoubleClick(row);
            e.Handled = true;
            return;
        }
        (_pressRow, _pressPoint, _dragging) = (row, point.Position, false);
        // A press on an already selected row keeps the selection until mouse-up, so a block can be dragged.
        _collapseOnRelease = e.KeyModifiers == KeyModifiers.None && _vm.Selection.Contains(row);
        if (_collapseOnRelease)
        {
            e.Handled = true;
            TodoList.ContainerFromIndex(row)?.Focus();
        }
    }

    void OnListPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pressRow < 0 || _vm is null) return;
        var point = e.GetCurrentPoint(TodoList);
        if (!point.Properties.IsLeftButtonPressed)
        {
            EndDrag();
            return;
        }
        var d = point.Position - _pressPoint;
        if (!_dragging)
        {
            if (Math.Abs(d.X) < DragThreshold && Math.Abs(d.Y) < DragThreshold) return;
            _dragging = true;
            e.Pointer.Capture(TodoList);
        }
        UpdateDropTarget(point.Position);
        AutoScroll(point.Position.Y);
        e.Handled = true;
    }

    void OnListPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_pressRow < 0 || _vm is null) return;
        if (_dragging)
        {
            if (_dropTarget is { } t) _vm.Drop(t.Row, t.After);
            e.Pointer.Capture(null);
            e.Handled = true;
        }
        else if (_collapseOnRelease)
        {
            TodoList.Selection.Clear();
            TodoList.Selection.Select(_pressRow);
        }
        EndDrag();
    }

    void EndDrag()
    {
        (_pressRow, _dragging, _collapseOnRelease, _dropTarget) = (-1, false, false, null);
        DropLine.IsVisible = false;
    }

    /// <summary>The row under the pointer: a 2 px accent line on its top edge (upper half) or bottom edge (lower half);
    /// none over a dragged row.</summary>
    void UpdateDropTarget(Point inList)
    {
        _dropTarget = null;
        DropLine.IsVisible = false;
        foreach (var c in TodoList.GetRealizedContainers())
        {
            if (c.TranslatePoint(default, TodoList) is not { } top || inList.Y < top.Y || inList.Y >= top.Y + RowHeight) continue;
            int row = TodoList.IndexFromContainer(c);
            if (_vm!.Selection.Contains(row)) return;
            bool after = inList.Y - top.Y >= RowHeight / 2;
            _dropTarget = (row, after);
            if (c.TranslatePoint(new Point(0, after ? RowHeight - 1 : -1), Overlay) is { } at)
            {
                Canvas.SetLeft(DropLine, at.X);
                Canvas.SetTop(DropLine, at.Y);
                DropLine.Width = TodoList.Bounds.Width;
                DropLine.IsVisible = true;
            }
            return;
        }
    }

    void AutoScroll(double y)
    {
        if (TodoList.FindDescendantOfType<ScrollViewer>() is not { } sv) return;
        if (y < RowHeight) sv.Offset = sv.Offset.WithY(Math.Max(0, sv.Offset.Y - RowHeight));
        else if (y > TodoList.Bounds.Height - RowHeight) sv.Offset = sv.Offset.WithY(sv.Offset.Y + RowHeight);
    }

    // ------------------------------------------------------------------ reword popup

    void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(InteractiveRebaseViewModel.Reword) || _vm is null) return;
        if (_vm.Reword is { } popup) OpenPopup(popup.Row);
        else if (!_vm.IsClosed) // refocus the row
            (_vm.Selection.Count > 0 ? TodoList.ContainerFromIndex(_vm.Selection[0]) : null ?? TodoList)?.Focus();
    }

    /// <summary>Left offset 130 from the list, top just below the edited row; focus in the subject, caret at the end.</summary>
    void OpenPopup(int row)
    {
        TodoList.ScrollIntoView(row);
        TodoList.UpdateLayout();
        var left = TodoList.TranslatePoint(default, Overlay)?.X ?? 0;
        var top = TodoList.ContainerFromIndex(row)?.TranslatePoint(new Point(0, RowHeight), Overlay)?.Y ?? 0;
        Canvas.SetLeft(RewordHost, left + 130);
        Canvas.SetTop(RewordHost, top);
        RewordSubject.Focus();
        RewordSubject.CaretIndex = RewordSubject.Text?.Length ?? 0;
    }

    void OnSubjectKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is null) return;
        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.Control) _vm.CloseReword(true);
        else if (e.Key == Key.Enter)
        {
            RewordDescription.Focus();
            RewordDescription.CaretIndex = RewordDescription.Text?.Length ?? 0;
        }
        else if (e.Key == Key.Escape) _vm.CloseReword(false);
        else return;
        e.Handled = true;
    }

    void OnDescriptionKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is null) return;
        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.Control) _vm.CloseReword(true);
        else if (e.Key == Key.Escape) _vm.CloseReword(false);
        else if (e.Key == Key.Back && e.KeyModifiers == KeyModifiers.None && RewordDescription.CaretIndex == 0
                 && RewordDescription.SelectionStart == RewordDescription.SelectionEnd)
        {
            RewordSubject.Focus();
            RewordSubject.CaretIndex = RewordSubject.Text?.Length ?? 0;
        }
        else return;
        e.Handled = true;
    }

    /// <summary>Multi-line text pasted into the subject is split at the first line break.</summary>
    async void OnSubjectPasting(object? sender, RoutedEventArgs e)
    {
        if (_vm?.Reword is not { } popup || Clipboard is not { } clipboard) return;
        e.Handled = true; // inserted here, after the clipboard was read
        int start = RewordSubject.SelectionStart, end = RewordSubject.SelectionEnd;
        string? text;
        try { text = await clipboard.TryGetTextAsync(); }
        catch (Exception ex)
        {
            Trace.WriteLine(ex); // clipboard owner gone: nothing pasted
            return;
        }
        if (text is null) return;
        if (popup.PasteIntoSubject(text, start, end) is not { } caret)
        {
            var s = popup.Subject;
            int a = Math.Clamp(Math.Min(start, end), 0, s.Length), b = Math.Clamp(Math.Max(start, end), a, s.Length);
            popup.Subject = s[..a] + text + s[b..];
            caret = a + text.Length;
        }
        RewordSubject.ClearSelection();
        RewordSubject.CaretIndex = caret;
    }

    void OnRewordOk(object? sender, RoutedEventArgs e) => _vm?.CloseReword(true);

    void OnRewordCancel(object? sender, RoutedEventArgs e) => _vm?.CloseReword(false);
}
