using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using GerGit.Controls;
using GerGit.Core;
using GerGit.ViewModels;

namespace GerGit.Views;

public partial class DiffView : UserControl
{
    readonly DiffLineNumberMargin _numbers, _oldNumbers;
    DiffViewModel? _vm;
    DiffDocument? _doc;                   // document of Editor: the unified one, or the new side
    DiffDocument? _oldDoc;                // document of OldEditor in the side-by-side layout, else null
    List<(int First, int Last)>? _groups; // staging groups of _doc, built on first hover
    (int First, int Last)? _hover;        // outlined group (rows are the same in both panes)
    Point? _pointer;                      // mouse in the TextView of _pointerPane; null = outside the editor (or text changed)
    TextEditor? _pointerPane;             // the pane the mouse is over
    TextEditor? _echo;                    // synced scrolling: the pane whose next scroll is our own mirror ...
    long _echoUntil;                      // ... within 100 ms
    Vector _lastOffset, _lastOldOffset;   // last scroll offset seen per pane (Editor, OldEditor): which axis moved

    public DiffView()
    {
        InitializeComponent();
        _numbers = Setup(Editor, () => _doc);
        _oldNumbers = Setup(OldEditor, () => _oldDoc);
        _oldNumbers.Columns = NumberColumns.Old;
        EditorHost.PointerExited += (_, _) => { _pointer = null; UpdateLineButtons(); };
        LineButtons.PointerWheelChanged += OnLineButtonsWheel;
        // Header: the full path as tooltip only while the path is trimmed (like TrimmedToolTip; the block has runs).
        ToolTip.AddToolTipOpeningHandler(PathText, (_, e) =>
        {
            if (TrimmedToolTip.IsTrimmed(PathText)) return;
            e.Cancel = true;
            e.Handled = true;
        });
        // Side-by-side layout: both panes scroll together vertically and horizontally.
        Editor.TextArea.TextView.ScrollOffsetChanged += (_, _) => SyncScroll(Editor, OldEditor);
        OldEditor.TextArea.TextView.ScrollOffsetChanged += (_, _) => SyncScroll(OldEditor, Editor);
    }

    /// <summary>Renderers, gutter, options and the staging hooks of one pane.</summary>
    DiffLineNumberMargin Setup(TextEditor editor, Func<DiffDocument?> document)
    {
        var area = editor.TextArea;
        area.TextView.BackgroundRenderers.Add(new DiffBackgroundRenderer(document));
        area.TextView.BackgroundRenderers.Add(new DiffHoverRenderer(() => _hover));
        area.TextView.LineTransformers.Add(new DiffServiceColorizer(document));
        var numbers = new DiffLineNumberMargin(document);
        area.LeftMargins.Insert(0, numbers);
        // Renderers, colorizers and the gutter read their colours while drawing; a theme switch rebuilds
        // the visual lines (and with them the gutter).
        area.TextView.ActualThemeVariantChanged += (_, _) => area.TextView.Redraw();
        editor.Options.EnableHyperlinks = false;
        editor.Options.EnableEmailHyperlinks = false;
        editor.Options.IndentationSize = 4; // tab width (repository setting, default 4)
        area.SelectionCornerRadius = 0;     // square selection corners, no border
        area.SelectionBorder = null;

        // Hunk and line staging (diff pane): hover group, floating buttons, keys.
        editor.PointerMoved += (_, e) =>
        {
            (_pointer, _pointerPane) = (e.GetPosition(area.TextView), editor);
            UpdateLineButtons();
        };
        area.SelectionChanged += (_, _) => OnSelectionChanged(editor);
        area.TextView.ScrollOffsetChanged += (_, _) => UpdateLineButtons();
        // Closing the find bar gives the text area the focus back: the buttons may move up again.
        area.GotFocus += (_, _) => UpdateLineButtons();
        editor.AddHandler(KeyDownEvent, OnEditorKeyDown, RoutingStrategies.Tunnel);
        return numbers;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null) _vm.PropertyChanged -= OnVmChanged;
        _vm = DataContext as DiffViewModel;
        if (_vm is not null) _vm.PropertyChanged += OnVmChanged;
        ShowDocument();
    }

    void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DiffViewModel.Document)) ShowDocument(reload: true);
        else if (e.PropertyName == nameof(DiffViewModel.Sides)) ShowDocument(); // the layout was switched
        else if (e.PropertyName is nameof(DiffViewModel.State) or nameof(DiffViewModel.LineActions)) UpdateLineButtons();
    }

    /// <summary><paramref name="reload"/>: a new result for the view model already shown (only then may the editor be kept).</summary>
    void ShowDocument(bool reload = false)
    {
        var sides = _vm?.Sides;
        // Unified: Editor spans both columns. Side by side: OldEditor left, Editor right; equal widths.
        Grid.SetColumn(Editor, sides is null ? 0 : 1);
        Grid.SetColumnSpan(Editor, sides is null ? 2 : 1);
        _numbers.Columns = sides is null ? NumberColumns.Both : NumberColumns.New;
        if (reload && _vm is { SameContent: true } && (sides is null) == (_oldDoc is null))
        {
            // Same lines again: keep the editor documents (selection, scroll, hover); the new objects describe the same diff.
            _doc = sides?.New ?? _vm.Document;
            _oldDoc = sides?.Old;
            _groups = null;
            Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
            OldEditor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
            UpdateLineButtons();
            return;
        }
        var sv = Scroller(Editor);
        double? keep = _vm is { KeepScroll: true } ? sv?.Offset.Y : null;
        _doc = sides?.New ?? _vm?.Document;
        _oldDoc = sides?.Old;
        _groups = null;
        _pointer = null; // the buttons go away when the text changes
        Editor.Document = new TextDocument(sides?.NewText ?? _vm?.Text ?? "");
        OldEditor.Document = new TextDocument(sides?.OldText ?? "");
        _numbers.InvalidateMeasure();
        _oldNumbers.InvalidateMeasure();
        UpdateLineButtons();
        // Scroll position memory (not remembered): entire-file mode starts at the first change group, else at the top.
        // A reload of the same working-tree file keeps its offset.
        // Posted: the new document must be laid out before its extent allows the offset.
        var d = _doc;
        var td = Editor.Document; // a later same-content reload swaps _doc but keeps this document
        if (keep is { } y && sv is not null)
            Dispatcher.UIThread.Post(() => { if (Editor.Document == td) sv.Offset = sv.Offset.WithY(y); }, DispatcherPriority.Loaded);
        else if (_vm is { EntireFile: true } && d is { Groups.Count: > 0 })
            Dispatcher.UIThread.Post(() => { if (Editor.Document == td) ScrollToLine(d.Groups[0].FirstLine); }, DispatcherPriority.Loaded);
    }

    static ScrollViewer? Scroller(TextEditor editor) => editor.FindDescendantOfType<ScrollViewer>();

    /// <summary>Mirrors the axis that moved in one pane to the other (a vertical wheel scroll keeps the other pane's X).
    /// The other pane's resulting scroll event is ignored (within 100 ms), so a pane that cannot scroll as far (a
    /// shorter line) does not pull the first one back.</summary>
    void SyncScroll(TextEditor from, TextEditor to)
    {
        var offset = from.TextArea.TextView.ScrollOffset;
        var last = from == Editor ? _lastOffset : _lastOldOffset;
        if (from == Editor) _lastOffset = offset;
        else _lastOldOffset = offset;
        if (_oldDoc is null) return;
        if (from == _echo && Environment.TickCount64 <= _echoUntil)
        {
            _echo = null;
            return;
        }
        var tv = to.TextArea.TextView;
        var current = tv.ScrollOffset;
        var target = new Vector(offset.X != last.X ? offset.X : current.X, offset.Y != last.Y ? offset.Y : current.Y);
        if (target == current || Scroller(to) is not { } sv) return;
        _echo = to;
        _echoUntil = Environment.TickCount64 + 100;
        sv.Offset = target;
        // Clamped to where it already was: no scroll event follows, so nothing is left to suppress.
        if (tv.ScrollOffset == current) _echo = null;
    }

    /// <summary>The pane that holds the text selection; the right
    /// (new) pane when nothing is selected, which is also where the hover buttons go.</summary>
    TextEditor SelectionPane => _oldDoc is not null && !OldEditor.TextArea.Selection.IsEmpty ? OldEditor : Editor;

    DiffDocument? DocumentOf(TextEditor pane) => pane == OldEditor ? _oldDoc : _doc;

    void OnSelectionChanged(TextEditor pane)
    {
        if (_oldDoc is not null && !pane.TextArea.Selection.IsEmpty)
            (pane == Editor ? OldEditor : Editor).TextArea.ClearSelection();
        UpdateLineButtons();
    }

    /// <summary>Recomputes the outlined group and shows, hides or moves the floating buttons.</summary>
    void UpdateLineButtons()
    {
        var pane = SelectionPane;
        var area = pane.TextArea;
        var tv = area.TextView;
        bool active = _vm is { ShowsLineActions: true } && _doc is not null;
        bool selected = !area.Selection.IsEmpty;
        // Over an open find bar there is no hover and no buttons: a click meant for ‹ › must never reach Stage.
        var pointer = OverFindBar(Editor) || OverFindBar(OldEditor) ? null : _pointer;
        var hover = active && !selected && pointer is { } p && _pointerPane is { } over ? GroupAt(over, p.Y) : null; // no outline while text is selected
        if (hover != _hover)
        {
            _hover = hover;
            Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background); // the outline is in both panes
            OldEditor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
        }
        int? top = !active || pointer is null ? null
            : selected ? pane.Document.GetLineByOffset(area.Selection.SurroundingSegment.Offset).LineNumber - 1
            : _hover?.First;
        LineButtons.IsVisible = top is not null;
        if (top is not { } line) return;

        bool unstage = _vm!.LineActions == LineActionSet.Unstage;
        StageButton.IsVisible = DiscardButton.IsVisible = !unstage;
        UnstageButton.IsVisible = unstage;
        // Right edge 20 px left of the pane's text view's right edge; top = first line + 15 px, less the find bar's
        // height while it is open, but never over the bar; follows scrolling.
        // Design choice (buttons and side-by-side): they stay below the bar rather than on it.
        var bar = pane.SearchPanel;
        double offset = 15 - (bar.IsClosed ? 0 : bar.Bounds.Height);
        var y = tv.GetVisualTopByDocumentLine(line + 1) - tv.ScrollOffset.Y + offset;
        if (tv.TranslatePoint(new Point(tv.Bounds.Width, y), EditorHost) is not { } at) return;
        double topY = !bar.IsClosed && bar.TranslatePoint(new Point(0, bar.Bounds.Height), EditorHost) is { } barBottom
            ? Math.Max(at.Y, barBottom.Y) : at.Y;
        LineButtons.Margin = new Thickness(0, topY, Math.Max(0, EditorHost.Bounds.Width - at.X) + 20, 0);
    }

    static bool OverFindBar(TextEditor pane) => pane.SearchPanel is { IsClosed: false, IsPointerOver: true };

    /// <summary>The staging group at a y of the pane the mouse is over, read with that pane's own view (the panes have
    /// the same rows). Design choice: the hover works over both
    /// panes; the buttons stay over the right one.</summary>
    (int First, int Last)? GroupAt(TextEditor pane, double y)
    {
        var tv = pane.TextArea.TextView;
        double top = y + tv.ScrollOffset.Y;
        if (y < 0 || tv.GetDocumentLineByVisualTop(top) is not { } dl
            || top >= tv.GetVisualTopByDocumentLine(dl.LineNumber) + tv.DefaultLineHeight) return null;
        int i = dl.LineNumber - 1;
        _groups ??= DiffViewModel.StagingGroups(_doc!);
        foreach (var g in _groups)
            if (g.First <= i && i <= g.Last) return g;
        return null;
    }

    /// <summary>Which lines are acted on: the selection's touched changed lines (side by side: only the
    /// lines of the pane that holds it), else the outlined group's (side by side: both panes' lines).</summary>
    List<HunkLine> ActionLines()
    {
        var pane = SelectionPane;
        var area = pane.TextArea;
        if (DocumentOf(pane) is not { } doc) return [];
        if (!area.Selection.IsEmpty)
        {
            var seg = area.Selection.SurroundingSegment;
            int first = pane.Document.GetLineByOffset(seg.Offset).LineNumber - 1;
            var end = pane.Document.GetLineByOffset(seg.EndOffset);
            // A selection ending at the very start of a line does not touch that line.
            int last = end.LineNumber - 1 - (seg.EndOffset == end.Offset && end.LineNumber - 1 > first ? 1 : 0);
            return DiffViewModel.ChangedLines(doc, first, last);
        }
        if (_hover is not { } g) return [];
        return _oldDoc is null ? DiffViewModel.ChangedLines(doc, g.First, g.Last)
            : [.. DiffViewModel.ChangedLines(_oldDoc, g.First, g.Last), .. DiffViewModel.ChangedLines(doc, g.First, g.Last)];
    }

    async Task RunAsync(PatchAction action)
    {
        if (_vm is { } vm) await vm.RunLineActionAsync(action, ActionLines());
    }

    async void OnStage(object? sender, RoutedEventArgs e) => await RunAsync(PatchAction.Stage);
    async void OnDiscard(object? sender, RoutedEventArgs e) => await RunAsync(PatchAction.Discard);
    async void OnUnstage(object? sender, RoutedEventArgs e) => await RunAsync(PatchAction.Unstage);

    /// <summary>Find bar keys, then the keys with a selection: Enter / Ctrl+S / Ctrl+Shift+S (not with Alt)
    /// toggle, Delete / Ctrl+Shift+D (Alt allowed) discard. Either Ctrl/Shift side counts. Keys typed in the find box
    /// (Enter, Delete, ...) belong to the box.</summary>
    async void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextEditor pane || OnFindKey(pane, e) || pane.SearchPanel.IsKeyboardFocusWithin) return;
        if (_vm is not { ShowsLineActions: true } vm || pane.TextArea.Selection.IsEmpty) return;
        var m = e.KeyModifiers;
        bool ctrl = m.HasFlag(KeyModifiers.Control), alt = m.HasFlag(KeyModifiers.Alt);
        var toggle = vm.LineActions == LineActionSet.Unstage ? PatchAction.Unstage : PatchAction.Stage;
        PatchAction? action = e.Key switch
        {
            Key.Enter or Key.S when !alt && (e.Key == Key.Enter || ctrl) => toggle,
            Key.Delete when vm.LineActions == LineActionSet.StageDiscard => PatchAction.Discard,
            Key.D when ctrl && m.HasFlag(KeyModifiers.Shift) && vm.LineActions == LineActionSet.StageDiscard => PatchAction.Discard,
            _ => null,
        };
        if (action is not { } a) return;
        e.Handled = true;
        await RunAsync(a);
    }

    /// <summary>Search inside a diff: Ctrl+F, or F3 while the bar is closed, opens this pane's bar; F3 /
    /// Shift+F3 go to the next / previous match (with wrap). Enter, Shift+Enter and Esc in the box are the panel's own.
    /// The keys reach only the focused pane, so the commit list's search bar (its own Ctrl+F / F3) is never touched.</summary>
    bool OnFindKey(TextEditor pane, KeyEventArgs e)
    {
        var bar = pane.SearchPanel;
        switch (e.Key, e.KeyModifiers)
        {
            case (Key.F, KeyModifiers.Control):
            case (Key.F3, KeyModifiers.None) when bar.IsClosed:
                OpenFind(pane);
                break;
            case (Key.F3, KeyModifiers.None):
                bar.FindNext();
                break;
            case (Key.F3, KeyModifiers.Shift) when !bar.IsClosed:
                bar.FindPrevious();
                break;
            default:
                return false;
        }
        e.Handled = true;
        return true;
    }

    /// <summary>The key comes from a find bar (windows that close on Esc leave that Esc to the bar).</summary>
    internal static bool IsInFindBar(object? source) =>
        source is Visual v && v.FindAncestorOfType<AvaloniaEdit.Search.SearchPanel>(includeSelf: true) is not null;

    /// <summary>Opens the pane's find bar: pre-filled with a one-line selection, else empty when freshly opened (an
    /// open bar keeps its text); the box gets the focus with its text selected.</summary>
    internal void OpenFind(TextEditor pane)
    {
        var bar = pane.SearchPanel;
        var selection = pane.TextArea.Selection;
        string? text = !selection.IsEmpty && !selection.IsMultiline ? selection.GetText() : null;
        if (bar.IsClosed)
        {
            bar.SearchPattern = text ?? ""; // while closed: no search and no selection change yet
            bar.Open();
        }
        else if (text is not null) bar.SearchPattern = text;
        Dispatcher.UIThread.Post(bar.Reactivate, DispatcherPriority.Input);
        Dispatcher.UIThread.Post(UpdateLineButtons, DispatcherPriority.Loaded); // after the bar's layout: move below it
    }

    /// <summary>The wheel over the floating buttons scrolls the diff.</summary>
    void OnLineButtonsWheel(object? sender, PointerWheelEventArgs e)
    {
        if (Editor.FindDescendantOfType<ScrollViewer>() is not { } sv) return;
        sv.Offset = sv.Offset.WithY(sv.Offset.Y - e.Delta.Y * 3 * Editor.TextArea.TextView.DefaultLineHeight);
        e.Handled = true;
    }

    /// <summary>Navigating changes (Ctrl+↑ / Ctrl+↓ and the header buttons).</summary>
    public void GoToChange(bool next)
    {
        var targets = DiffViewModel.NavigationTargets(_doc);
        var tv = Editor.TextArea.TextView;
        tv.EnsureVisualLines();
        int first = (tv.GetDocumentLineByVisualTop(tv.ScrollOffset.Y + 1)?.LineNumber ?? 1) - 1; // +1: offset rounding
        // Next: the very first group of the file is never a target. The group's first line goes to the top edge.
        int? target = next
            ? targets.Skip(1).Cast<int?>().FirstOrDefault(t => t > first + 1)
            : targets.Cast<int?>().LastOrDefault(t => t < first);
        if (target is { } t) ScrollToLine(t);
    }

    /// <summary>Puts the line at the top edge. TextEditor.ScrollToVerticalOffset has no effect in AvaloniaEdit 12, so
    /// the template's ScrollViewer is moved directly.</summary>
    void ScrollToLine(int index)
    {
        var tv = Editor.TextArea.TextView;
        tv.EnsureVisualLines();
        if (Editor.FindDescendantOfType<ScrollViewer>() is { } sv)
            sv.Offset = sv.Offset.WithY(tv.GetVisualTopByDocumentLine(index + 1));
    }

    /// <summary>Image card: the drop-down arrow beside the size line with `Save As...`.</summary>
    void OnCardMenu(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: BinaryCard card } button) return;
        var save = new MenuItem { Header = "Save As..." };
        save.Click += async (_, _) => await SaveAsAsync(card);
        new MenuFlyout { Items = { save } }.ShowAt(button);
    }

    /// <summary>File-save dialog `Select location` with the file name; writes the raw bytes; errors as error dialog.</summary>
    async Task SaveAsAsync(BinaryCard card)
    {
        try
        {
            if (TopLevel.GetTopLevel(this) is not { } top || card.Bytes is not { } bytes) return;
            if (await FileDialogs.SaveFileAsync(top, card.FileName) is { } path) await File.WriteAllBytesAsync(path, bytes);
        }
        catch (Exception ex) // runs from a click: nothing may escape
        {
            await MessageWindow.ShowAsync("Error", ex.Message);
        }
    }

    void OnPreviousChange(object? sender, RoutedEventArgs e) => GoToChange(false);
    void OnNextChange(object? sender, RoutedEventArgs e) => GoToChange(true);
}
