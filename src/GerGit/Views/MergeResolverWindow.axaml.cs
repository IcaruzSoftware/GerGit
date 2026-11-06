using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using GerGit.Controls;
using GerGit.Core.Conflicts;
using GerGit.ViewModels;

namespace GerGit.Views;

/// <summary>
/// Merge-conflict resolver window (three panes): theirs left, result (editable), ours right; synchronised
/// scrolling, gutter tags, per-chunk Select/Remove buttons, orientation toggle. All state lives in
/// <see cref="MergeResolverViewModel"/>; the panes are refilled from its layout after every change.
/// </summary>
public partial class MergeResolverWindow : Window
{
    public const string GeometryKey = "window.mergeResolver";

    /// <summary>One editor pane. Side null = the result pane.</summary>
    sealed class Pane(TextEditor editor, Panel host, MergeSide? side, Func<IReadOnlyList<MergeRow>> rows)
    {
        public TextEditor Editor { get; } = editor;
        public Panel Host { get; } = host;
        public MergeSide? Side { get; } = side;
        public Func<IReadOnlyList<MergeRow>> Rows { get; } = rows;
        public MergeGutter? Gutter;
        public Border? Buttons;
        public Button? ChunkButton;
        public int? HoverRow;                      // taggable row under the mouse
        public (int First, int Last)? HoverRange;  // rows of the hovered conflict chunk (outlined)
        public int HoverChunk = -1;
        public double PointerY = double.NaN;      // mouse y in TextView coordinates, NaN = outside
        public ScrollViewer? Scroll => field ??= Editor.FindDescendantOfType<ScrollViewer>();
    }

    readonly MergeResolverViewModel? _vm;
    readonly Pane[] _panes = [];
    readonly Pane _theirs = null!, _result = null!, _ours = null!;
    bool _syncing;        // the panes are being refilled: no edit handling
    bool _inEdit;         // a result edit is being applied: the refill waits for the document's TextChanged
    bool _pendingSync, _pendingRebuild;
    int? _caret;          // result caret offset to restore after a refill
    Pane? _scrollLeader;
    long _scrollGuardUntil;

    public MergeResolverWindow() => InitializeComponent();

    internal MergeResolverWindow(MergeResolverViewModel vm) : this()
    {
        _vm = vm;
        DataContext = vm;
        _theirs = Setup(TheirsEditor, TheirsHost, MergeSide.Theirs, () => vm.Layout.Theirs, TheirsButtons, TheirsChunkButton);
        _result = Setup(ResultEditor, ResultHost.Child as Panel ?? throw new InvalidOperationException(), null, () => vm.Layout.Result, null, null);
        _ours = Setup(OursEditor, OursHost, MergeSide.Ours, () => vm.Layout.Ours, OursButtons, OursChunkButton);
        _panes = [_theirs, _result, _ours];

        var doc = ResultEditor.Document;
        doc.Changed += OnResultChanged;
        doc.TextChanged += (_, _) =>
        {
            if (!_pendingSync) return;
            _pendingSync = _pendingRebuild = false;
            SyncPanes();
        };

        vm.LayoutChanged += OnLayoutChanged;
        vm.PropertyChanged += OnVmChanged;
        vm.CloseRequested += Close;
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            vm.Cancel(); // Esc = Cancel
        }, RoutingStrategies.Tunnel);
        Opened += (_, _) =>
        {
            SyncPanes();
            ScrollToFirstConflict();
        };
        Closed += (_, _) =>
        {
            vm.LayoutChanged -= OnLayoutChanged;
            vm.PropertyChanged -= OnVmChanged;
            vm.CloseRequested -= Close;
            vm.Cancel(); // title-bar close: no result
            MessageWindow.DialogClosed = true; // no activation refresh right after a dialog
        };
        ApplyOrientation();
        SyncPanes();
    }

    /// <summary>
    /// Opens the resolver for an unmerged file modally over the main window; true = resolved (the write/stage job
    /// succeeded). Without a visible main window (headless) it returns false at once.
    /// </summary>
    public static async Task<bool> ShowAsync(RepoTabViewModel tab, string path)
    {
        var owner = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner is not { IsVisible: true }) return false;
        var vm = new MergeResolverViewModel(tab, path);
        var w = new MergeResolverWindow(vm);
        WindowGeometry.Apply(w, GeometryKey);
        var load = vm.LoadAsync(); // the header boxes are enabled once it is done
        await w.ShowDialog(owner);
        await load;
        return vm.Result == true && await vm.Resolving;
    }

    Pane Setup(TextEditor editor, Panel host, MergeSide? side, Func<IReadOnlyList<MergeRow>> rows, Border? buttons, Button? chunkButton)
    {
        var p = new Pane(editor, host, side, rows) { Buttons = buttons, ChunkButton = chunkButton };
        // Limitation: no undo; an undo across a refill would replay text the model no longer has. A model-level undo
        // comes if users miss it.
        editor.Document = new TextDocument { UndoStack = { SizeLimit = 0 } };
        var area = editor.TextArea;
        area.TextView.BackgroundRenderers.Add(new MergeBackgroundRenderer(rows));
        if (side is not null) area.TextView.BackgroundRenderers.Add(new DiffHoverRenderer(() => p.HoverRange));
        p.Gutter = new MergeGutter(rows, () => p.HoverRow, IsSelected, side is null ? null : row => _vm!.ToggleLine(row));
        area.LeftMargins.Insert(0, p.Gutter);
        area.TextView.ActualThemeVariantChanged += (_, _) => area.TextView.Redraw(); // as in DiffView
        editor.Options.EnableHyperlinks = false;
        editor.Options.EnableEmailHyperlinks = false;
        editor.Options.IndentationSize = 4;
        area.SelectionCornerRadius = 0;
        area.SelectionBorder = null;
        area.TextView.ScrollOffsetChanged += (_, _) => { OnScrolled(p); UpdateHover(p); };
        if (side is not null)
        {
            editor.PointerMoved += (_, e) => { p.PointerY = e.GetPosition(area.TextView).Y; UpdateHover(p, true); };
            host.PointerExited += (_, _) => UpdateHover(p, false);
        }
        return p;
    }

    bool IsSelected(MergeRow row) =>
        _vm?.Document is { } d && row.Line is { } l && d.Chunks[row.Chunk].IsSelected(l);

    void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MergeResolverViewModel.Vertical)) ApplyOrientation();
        else if (e.PropertyName == nameof(MergeResolverViewModel.IsLoaded))
            Dispatcher.UIThread.Post(ScrollToFirstConflict, DispatcherPriority.Loaded); // after the panes are laid out
    }

    // ---------------------------------------------------------------- panes

    void OnLayoutChanged()
    {
        if (_inEdit) _pendingSync = true; // the editor document is inside its change: refill after it
        else SyncPanes();
    }

    /// <summary>Refills every pane whose text differs from the model; repaints backgrounds, gutters and the chunk button.</summary>
    void SyncPanes()
    {
        if (_vm is null) return;
        foreach (var p in _panes)
        {
            // CRLF rows arrive without their \r\n. Limitation: a lone \r inside a row (mixed endings such as
            // `x\r\r\n`) is a line break for the editor, so that pane shows one extra line and later rows shift against
            // the other panes; edits still map by offset. Replace such \r by a visible glyph if it ever matters.
            var text = string.Join("\n", p.Rows().Select(r => r.Text));
            var doc = p.Editor.Document;
            if (doc.Text != text)
            {
                var offset = p.Scroll?.Offset;
                _syncing = true;
                try { doc.Text = text; }
                finally { _syncing = false; }
                if (p == _result && _caret is { } c) p.Editor.CaretOffset = Math.Min(c, doc.TextLength);
                if (offset is { } o && p.Scroll is { } sv) sv.Offset = o;
            }
            p.Gutter!.InvalidateMeasure();
            p.Gutter.InvalidateVisual();
            p.Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
        }
        _caret = null;
        UpdateHover(_theirs);
        UpdateHover(_ours);
    }

    /// <summary>Editing the result: every change goes to the model; protected rows or a mismatch rebuild the
    /// pane from the model (the edit is undone) once the change is complete.</summary>
    void OnResultChanged(object? sender, DocumentChangeEventArgs e)
    {
        if (_syncing || _pendingRebuild || _vm is null) return;
        _inEdit = true;
        try
        {
            var outcome = _vm.EditResult(e.Offset, e.RemovedText.Text, e.InsertedText.Text);
            if (outcome == ResultEdit.Applied) _caret = e.Offset + e.InsertionLength;
            else
            {
                _pendingRebuild = true; // later changes of the same update refer to text the model does not have
                _caret = e.Offset;
            }
            _pendingSync = true;
        }
        finally { _inEdit = false; }
    }

    // ---------------------------------------------------------------- hover, gutter tags, chunk buttons

    /// <summary>Recomputes the hovered row and conflict chunk of a side pane. <paramref name="inside"/>: null = keep
    /// the last pointer state (scroll, refill), false = the pointer left the pane.</summary>
    void UpdateHover(Pane p, bool? inside = null)
    {
        if (p.Side is not { } side || _vm is null) return;
        if (inside == false) p.PointerY = double.NaN;
        var rows = p.Rows();
        var tv = p.Editor.TextArea.TextView;
        int? row = null;
        if (!double.IsNaN(p.PointerY) && p.PointerY >= 0 && tv.VisualLinesValid)
        {
            double top = p.PointerY + tv.ScrollOffset.Y;
            if (tv.GetDocumentLineByVisualTop(top) is { } dl && dl.LineNumber <= rows.Count
                && top < tv.GetVisualTopByDocumentLine(dl.LineNumber) + tv.DefaultLineHeight)
                row = dl.LineNumber - 1;
        }
        int? hoverRow = row is { } r && MergeGutter.IsTaggable(rows[r]) ? r : null;
        (int First, int Last)? range = row is { } rr ? _vm.ConflictRows().Cast<(int, int)?>().FirstOrDefault(c => c!.Value.Item1 <= rr && rr <= c.Value.Item2) : null;
        if (hoverRow != p.HoverRow || range != p.HoverRange)
        {
            p.HoverRow = hoverRow;
            p.HoverRange = range;
            p.Gutter!.InvalidateVisual();
            tv.InvalidateLayer(KnownLayer.Background);
        }

        // Floating `Select/Remove Left/Right` button 15 px above the hover position, margin-right 20. It stays
        // inside the chunk (top clamped to the chunk's first row) so the mouse can reach it.
        p.Buttons!.IsVisible = range is not null;
        if (range is not { } g) return;
        p.HoverChunk = rows[g.First].Chunk;
        p.ChunkButton!.Content = _vm.ChunkButtonText(p.HoverChunk, side);
        double chunkTop = tv.GetVisualTopByDocumentLine(g.First + 1) - tv.ScrollOffset.Y;
        double y = Math.Max(chunkTop, p.PointerY - 15 - p.ChunkButton.Height - 4);
        if (tv.TranslatePoint(new Point(tv.Bounds.Width, y), p.Host) is { } at)
            p.Buttons.Margin = new Thickness(0, Math.Max(0, at.Y), Math.Max(0, p.Host.Bounds.Width - at.X) + 20, 0);
    }

    void OnTheirsChunk(object? sender, RoutedEventArgs e) => _vm?.ToggleChunkSide(_theirs.HoverChunk, MergeSide.Theirs);
    void OnOursChunk(object? sender, RoutedEventArgs e) => _vm?.ToggleChunkSide(_ours.HoverChunk, MergeSide.Ours);

    // ---------------------------------------------------------------- scrolling and navigation

    /// <summary>All panes scroll together, both ways; for 100 ms after a pane led, the others' echoes are
    /// ignored (a clamped offset would otherwise push the leader back).</summary>
    void OnScrolled(Pane p)
    {
        if (p.Scroll is not { } source) return;
        long now = Environment.TickCount64;
        if (_scrollLeader != p && now < _scrollGuardUntil) return;
        _scrollLeader = p;
        _scrollGuardUntil = now + 100;
        foreach (var o in _panes)
            if (o != p && o.Scroll is { } sv && sv.Offset != source.Offset) sv.Offset = source.Offset;
    }

    void ScrollToRow(Pane p, int row)
    {
        var tv = p.Editor.TextArea.TextView;
        tv.EnsureVisualLines();
        if (p.Scroll is { } sv) sv.Offset = sv.Offset.WithY(tv.GetVisualTopByDocumentLine(row + 1));
    }

    /// <summary>Navigation: on open the ours pane shows the first conflict at the top.</summary>
    void ScrollToFirstConflict()
    {
        if (_vm is { IsText: true } && _vm.ConflictRows() is [var first, ..]) ScrollToRow(_ours, first.First);
    }

    /// <summary>Navigation: relative to the visual line in the middle of the ours pane.</summary>
    internal void GoToConflict(bool next)
    {
        if (_vm is null) return;
        var tv = _ours.Editor.TextArea.TextView;
        tv.EnsureVisualLines();
        int Row(double y) => (tv.GetDocumentLineByVisualTop(tv.ScrollOffset.Y + y)?.LineNumber ?? 1) - 1;
        if (_vm.TargetRow(Row(1), Row(tv.Bounds.Height / 2), next) is { } row) ScrollToRow(_ours, row); // +1: offset rounding
    }

    void OnPrevious(object? sender, RoutedEventArgs e) => GoToConflict(false);
    void OnNext(object? sender, RoutedEventArgs e) => GoToConflict(true);

    // ---------------------------------------------------------------- orientation and footer

    /// <summary>Panes: horizontal = theirs | ours in the upper 3/5 (1 px gap), splitter, result in the lower
    /// 2/5 with a vertical scrollbar; vertical = three equal columns, result with 1 px side borders and no scrollbar.</summary>
    void ApplyOrientation()
    {
        bool vertical = _vm?.Vertical == true;
        EditorGrid.RowDefinitions = new RowDefinitions(vertical ? "*" : "3*,1,2*");
        EditorGrid.ColumnDefinitions = new ColumnDefinitions(vertical ? "*,*,*" : "*,1,*");
        Place(TheirsHost, 0, 0, 1);
        Place(OursHost, 0, 2, 1);
        Place(ResultHost, vertical ? 0 : 2, vertical ? 1 : 0, vertical ? 1 : 3);
        Place(Splitter, 1, 0, 3);
        Splitter.IsVisible = !vertical;
        ResultHost.BorderThickness = vertical ? new Thickness(1, 0) : default;
        ResultEditor.VerticalScrollBarVisibility = vertical ? Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden
            : Avalonia.Controls.Primitives.ScrollBarVisibility.Visible;

        static void Place(Control c, int row, int col, int span)
        {
            Grid.SetRow(c, row);
            Grid.SetColumn(c, col);
            Grid.SetColumnSpan(c, span);
        }
    }

    void OnResolve(object? sender, RoutedEventArgs e) => _ = _vm?.ResolveAsync();
    void OnCancel(object? sender, RoutedEventArgs e) => _vm?.Cancel();
}
