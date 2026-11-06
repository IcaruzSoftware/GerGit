using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GerGit.ViewModels;

namespace GerGit.Views;

public partial class LocalChangesView : UserControl
{
    LocalChangesViewModel? _vm;

    public LocalChangesView()
    {
        InitializeComponent();
        // Tunnel at the root: Ctrl+Enter / Ctrl+Shift+Alt+S win over the text boxes and the diff editor.
        AddHandler(KeyDownEvent, OnRootKeyDown, RoutingStrategies.Tunnel);
        foreach (var list in new[] { UnstagedList, StagedList })
        {
            list.AddHandler(KeyDownEvent, OnListKeyDown, RoutingStrategies.Tunnel);
            list.DoubleTapped += OnListDoubleTapped;
        }
        DiffPane.AddHandler(KeyDownEvent, OnGoToChangeKey, RoutingStrategies.Tunnel);
        SubjectBox.AddHandler(KeyDownEvent, OnSubjectKeyDown, RoutingStrategies.Tunnel);
        DescriptionBox.AddHandler(KeyDownEvent, OnDescriptionKeyDown, RoutingStrategies.Tunnel);
    }

    LocalChangesViewModel Vm => _vm!;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null) _vm.FocusRequested -= OnFocusRequested;
        _vm = DataContext as LocalChangesViewModel;
        if (_vm is not null) _vm.FocusRequested += OnFocusRequested;
    }

    /// <summary>Posted: the view may just have become visible, or the lists were just re-enabled.</summary>
    void OnFocusRequested(LocalChangesFocus target) => Dispatcher.UIThread.Post(() =>
    {
        if (_vm is not { } vm) return; // the view lost its view model (tab closed) before the post ran
        if (target == LocalChangesFocus.Subject) SubjectBox.Focus();
        // The first-file fallback runs on any refresh: never take the focus from a hidden view or from the commit box.
        else if (target == LocalChangesFocus.FirstFile && (!IsEffectivelyVisible || SubjectBox.IsFocused || DescriptionBox.IsFocused)) return;
        else FocusSelected(vm.StagedSelection.SelectedIndexes.Count > 0 ? StagedList : UnstagedList);
    }, DispatcherPriority.Loaded);

    static void FocusSelected(ListBox list)
    {
        int i = list.SelectedIndex;
        if (i >= 0)
        {
            list.ScrollIntoView(i);
            if (list.ContainerFromIndex(i) is { } c) { c.Focus(NavigationMethod.Directional); return; }
        }
        list.Focus();
    }

    // The header texts follow Shift anywhere in the repository view, so the window's keys are watched
    // (also while the focus is in the sidebar or nowhere).
    TopLevel? _top;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _top = TopLevel.GetTopLevel(this);
        _top?.AddHandler(KeyDownEvent, OnShiftDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        _top?.AddHandler(KeyUpEvent, OnShiftUp, RoutingStrategies.Tunnel, handledEventsToo: true);
        if (_top is WindowBase w) w.Deactivated += OnDeactivated;
    }

    // A Shift released in another window never reaches us.
    void OnDeactivated(object? sender, EventArgs e)
    {
        if (_vm is not null) Vm.ShiftHeld = false;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _top?.RemoveHandler(KeyDownEvent, OnShiftDown);
        _top?.RemoveHandler(KeyUpEvent, OnShiftUp);
        if (_top is WindowBase w) w.Deactivated -= OnDeactivated;
        _top = null;
    }

    void OnShiftDown(object? sender, KeyEventArgs e) => TrackShift(e, true);
    void OnShiftUp(object? sender, KeyEventArgs e) => TrackShift(e, false);

    void TrackShift(KeyEventArgs e, bool down)
    {
        if (_vm is null) return;
        Vm.ShiftHeld = e.Key is Key.LeftShift or Key.RightShift ? down : e.KeyModifiers.HasFlag(KeyModifiers.Shift);
    }

    async void OnRootKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is null) return;
        try
        {
            // Commit box controls: Ctrl+Enter commits with the default push mode (the setting), Ctrl+Shift+Enter
            // with the inverted one, from anywhere in the view (never reaching the diff editor, which would stage lines).
            if (e.Key == Key.Enter && e.KeyModifiers is KeyModifiers.Control or (KeyModifiers.Control | KeyModifiers.Shift))
            {
                e.Handled = true;
                await Vm.CommitCommand.ExecuteAsync(LocalChangesViewModel.PushAfterCommit ^ e.KeyModifiers.HasFlag(KeyModifiers.Shift));
            }
            else if (e.Key == Key.S && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt))
            {
                e.Handled = true;
                await Vm.StageAllShortcutCommand.ExecuteAsync(null);
            }
        }
        catch (Exception ex) { Trace.WriteLine(ex); }
    }

    /// <summary>Keyboard and mouse > "File lists".</summary>
    async void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not ListBox list || _vm is null) return;
        bool unstaged = list == UnstagedList;
        var m = e.KeyModifiers;
        const KeyModifiers ctrl = KeyModifiers.Control, ctrlShift = KeyModifiers.Control | KeyModifiers.Shift;
        try
        {
            switch (e.Key)
            {
                case Key.Enter when m == KeyModifiers.None:
                case Key.S when m is ctrl or ctrlShift:
                    e.Handled = true;
                    await Vm.ToggleCommand.ExecuteAsync(null);
                    break;
                case Key.Delete when m == KeyModifiers.None && unstaged:
                case Key.D when m == ctrlShift && unstaged:
                    e.Handled = true;
                    await Vm.DiscardCommand.ExecuteAsync(null);
                    break;
                case Key.C when m is ctrl or ctrlShift:
                    e.Handled = true;
                    await CopyAsync(m == ctrlShift);
                    break;
                case Key.D when m == ctrl:
                    e.Handled = true;
                    await ExternalTools.LocalChangesShortcutAsync(Vm, !unstaged);
                    break;
                case Key.Up or Key.Down when m == ctrl:
                    e.Handled = true;
                    DiffPane.GoToChange(e.Key == Key.Down);
                    break;
                case Key.Right or Key.Left when m == KeyModifiers.None && Vm.IsTreeMode:
                    e.Handled = true;
                    TreeKey(list, e.Key == Key.Right);
                    break;
            }
        }
        catch (Exception ex) { Trace.WriteLine(ex); }
    }

    /// <summary>Right: expand a collapsed folder, else go to its first child. Left: collapse an expanded folder, else go to the parent.</summary>
    void TreeKey(ListBox list, bool right)
    {
        var rows = list == UnstagedList ? Vm.UnstagedRows : Vm.StagedRows;
        int i = list.SelectedIndex;
        if (i < 0 || i >= rows.Count) return;
        var row = rows[i];
        int target;
        if (row.IsFolder && row.IsExpanded != right)
        {
            Vm.ToggleFolder(row, right);
            FocusSelected(list);
            return;
        }
        if (right) target = row.IsFolder && i + 1 < rows.Count ? i + 1 : -1;
        else
        {
            target = i - 1;
            while (target >= 0 && !(rows[target].IsFolder && rows[target].Depth < row.Depth)) target--;
        }
        if (target < 0) return;
        list.SelectedIndex = target;
        FocusSelected(list);
    }

    void OnGoToChangeKey(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.Control || e.Key is not (Key.Up or Key.Down)) return;
        DiffPane.GoToChange(e.Key == Key.Down);
        e.Handled = true;
    }

    /// <summary>Double-click on a row: stage (Unstaged) or unstage (Staged) the selection; the chevron only toggles.</summary>
    async void OnListDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_vm is null || e.Source is not Control c || c.FindAncestorOfType<Button>(includeSelf: true) is not null
            || c.FindAncestorOfType<ListBoxItem>(includeSelf: true) is null)
            return;
        try { await (sender == StagedList ? Vm.UnstageCommand : Vm.StageCommand).ExecuteAsync(null); }
        catch (Exception ex) { Trace.WriteLine(ex); }
    }

    void OnChevronClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is FileRow row) Vm.ToggleFolder(row);
    }

    /// <summary>The list menus open only when a row is selected.</summary>
    void OnMenuOpening(object? sender, CancelEventArgs e)
    {
        bool staged = sender == StagedList.ContextMenu;
        var sel = staged ? Vm.StagedSelection : Vm.UnstagedSelection;
        if (sel.SelectedIndexes.Count == 0)
        {
            e.Cancel = true;
            return;
        }
        // Stash <n> File(s)...: not for a single submodule row; n = distinct paths of the expanded selection.
        var item = staged ? StagedStashItem : UnstagedStashItem;
        int n = Vm.StashPaths(staged).Count;
        item.IsVisible = n > 0 && !(sel.SelectedItems is [FileRow { Entry.IsSubmodule: true }]);
        item.Header = n == 1 ? "Stash 1 File..." : $"Stash {n} Files...";

        // External merge (unmerged file) or external diff.
        MenuControls.Apply(staged ? StagedToolItem : UnstagedToolItem, ExternalTools.LocalChangesEntries(Vm, staged).FirstOrDefault());

        // Blame not for a single submodule row, disabled for several rows or a folder; History
        // disabled for several rows.
        var (blame, history) = staged ? (StagedBlameItem, StagedHistoryItem) : (UnstagedBlameItem, UnstagedHistoryItem);
        var rows = sel.SelectedItems.OfType<FileRow>().ToList();
        blame.IsVisible = rows is not [{ Entry.IsSubmodule: true }];
        blame.IsEnabled = rows is [{ IsFolder: false }];
        history.IsEnabled = rows.Count == 1;
    }

    void OnToolItem(object? sender, RoutedEventArgs e) => MenuControls.InvokeTag(sender, e);

    FileRow? MenuRow(object? sender) =>
        (sender == StagedBlameItem || sender == StagedHistoryItem ? Vm.StagedSelection : Vm.UnstagedSelection).SelectedItems
            .OfType<FileRow>().SingleOrDefault();

    /// <summary>Blame/Timeline...: the blame window of the file, starting at HEAD.</summary>
    void OnBlame(object? sender, RoutedEventArgs e)
    {
        if (MenuRow(sender) is { IsFolder: false } row) BlameWindow.Open(Vm.Owner, row.Path, null);
    }

    /// <summary>History...: file history, or the folder's history for a folder row.</summary>
    void OnHistory(object? sender, RoutedEventArgs e)
    {
        if (MenuRow(sender) is { } row) FileHistoryWindow.Open(Vm.Owner, row.Path, row.IsFolder, null);
    }

    async void OnStashFiles(object? sender, RoutedEventArgs e)
    {
        try { await Vm.Owner.StashOps.StashFilesAsync(Vm.StashPaths(sender == StagedStashItem)); }
        catch (Exception ex) { Trace.WriteLine(ex); }
    }

    async void OnCopyPath(object? sender, RoutedEventArgs e) => await CopyAsync(false);
    async void OnCopyFullPath(object? sender, RoutedEventArgs e) => await CopyAsync(true);

    async Task CopyAsync(bool full)
    {
        try
        {
            var text = Vm.CopyText(full);
            if (text.Length > 0 && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
        }
        catch (Exception ex) { Trace.WriteLine(ex); }
    }

    /// <summary>Enter in the subject moves to the description. Not built yet: Up / Down (recent messages).</summary>
    void OnSubjectKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None) return;
        e.Handled = true;
        DescriptionBox.Focus();
    }

    /// <summary>Backspace at the very start of the description (no selection) moves to the end of the subject.</summary>
    void OnDescriptionKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Back || e.KeyModifiers != KeyModifiers.None
            || DescriptionBox.CaretIndex != 0 || DescriptionBox.SelectionStart != DescriptionBox.SelectionEnd)
            return;
        e.Handled = true;
        SubjectBox.Focus();
        SubjectBox.CaretIndex = SubjectBox.Text?.Length ?? 0;
    }
}
