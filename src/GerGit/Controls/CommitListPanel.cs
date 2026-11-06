using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using GerGit.Core;
using GerGit.Hosting;
using GerGit.ViewModels;
using GerGit.Views;

namespace GerGit.Controls;

/// <summary>
/// The commit list, single-row and double-row layouts: one custom-drawn control that renders only the visible rows,
/// so scrolling stays smooth with 100,000+ rows. Owns the vertical scroll offset; the view syncs a ScrollBar to it.
/// Not built yet: drag and drop, merge collapsing, Enter / Space windows and popups.
/// </summary>
public sealed class CommitListPanel : Control
{
    // Single-row layout and "Drawing".
    internal const double RowHeight = 23;
    const double DoubleRowHeight = 42, ShaBox = 50, SubjectSizeDouble = 13.5;
    const double Inset = 4, GraphMargin = 4, ColumnWidth = 12;
    const double AvatarWidth = 19, AuthorWidth = 120, ShaWidth = 70, DateWidth = 129, MarkerWidth = 4;
    const double FontSize = 13, LabelFontSize = 12, LabelHeight = 18, IconSize = 14; // row font size 13 px

    // Same in both themes.
    static readonly IBrush BehindBrush = new ImmutableSolidColorBrush(Color.Parse("#A9A9A9"));

    CommitListViewModel? _vm;
    double _offset;
    int _hoverRow = -1;
    Point _hoverPoint;
    int _pressedRow = -1; // plain click on a selected row: the selection collapses on release
    int _pendingScrollRow = -1;
    bool _vertical, _double; // vertical commit list layout; double-row layout in use
    Res _res = null!; // theme resources, loaded on first use and dropped when the theme changes
    StreamGeometry?[] _lines = new StreamGeometry?[13]; // graph lines of the visible rows, one geometry per lane colour
    (double Offset, double Width, double Height, object? Rows, int Count) _linesKey;
    static readonly FontFeatureCollection TabularDigits = FontFeatureCollection.Parse("tnum"); // Digits

    static CommitListPanel()
    {
        FocusableProperty.OverrideDefaultValue<CommitListPanel>(true);
        ClipToBoundsProperty.OverrideDefaultValue<CommitListPanel>(true);
        AffectsRender<CommitListPanel>(IsKeyboardFocusWithinProperty);
    }

    public CommitListPanel()
    {
        ActualThemeVariantChanged += (_, _) => ResetResources();
        ContextRequested += (_, e) =>
        {
            e.Handled = true;
            OpenContextMenu();
        };
    }

    /// <summary>Context menus: built from the current selection (a right click does not change it).</summary>
    internal ContextMenu? OpenContextMenu() =>
        _vm is not null && Menus.CommitList(_vm) is { } entries ? MenuControls.Open(this, entries) : null;

    void ResetResources()
    {
        _res = null!;
        _visuals.Clear();
        InvalidateVisual();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ResetResources(); // resources resolve differently once the control is in a tree
    }

    public CommitListViewModel? ViewModel
    {
        get => _vm;
        set
        {
            if (_vm == value) return;
            if (_vm is not null)
            {
                _vm.Changed -= OnChanged;
                _vm.ScrollIntoViewRequested -= ScrollIntoView;
                _vm.FocusRequested -= OnFocusRequested;
                _vm.Owner.PropertyChanged -= OnOwnerChanged;
            }
            _vm = value;
            if (_vm is not null)
            {
                _vm.Changed += OnChanged;
                _vm.ScrollIntoViewRequested += ScrollIntoView;
                _vm.FocusRequested += OnFocusRequested;
                _vm.Owner.PropertyChanged += OnOwnerChanged; // host icons of the remote labels follow account changes
                _vm.Copy = text => TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(text) ?? Task.CompletedTask;
            }
            _offset = 0;
            OnChanged();
        }
    }

    /// <summary>Row layouts: in the vertical layout the list uses the double-row layout (42 px rows) while its
    /// usable width is 500 or less, live while resizing. Set by RepoTabView.</summary>
    public bool IsVerticalLayout
    {
        get => _vertical;
        set
        {
            if (_vertical == value) return;
            _vertical = value;
            UpdateRowLayout();
        }
    }

    /// <summary>Row height of the current layout: 42 (double-row) or <see cref="RowHeight"/>.</summary>
    internal double RowH => _double ? DoubleRowHeight : RowHeight;

    internal bool IsDoubleRow => _double;

    void UpdateRowLayout()
    {
        // Usable width = list width - scroll bar - 4 - 4; this control's width already excludes the scroll bar.
        bool d = _vertical && Bounds.Width - 2 * Inset <= 500;
        if (d == _double) return;
        double top = _offset / RowH; // the same row stays at the top
        _double = d;
        _visuals.Clear();
        _linesKey = default;
        _offset = Math.Clamp(top * RowH, 0, MaxOffset);
        if (_vm is { Cursor: >= 0 } vm) EnsureVisible(vm.Cursor);
        InvalidateVisual();
        ScrollChanged?.Invoke();
    }

    void OnFocusRequested() => Focus();

    void OnOwnerChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RepoTabViewModel.HostedRemotes)) InvalidateVisual();
        else if (e.PropertyName == nameof(RepoTabViewModel.Worktrees))
        {
            _visuals.Clear(); // the worktree icon cell changes the cached label layout
            InvalidateVisual();
        }
    }

    /// <summary>Raised when the offset, the extent or the viewport changed.</summary>
    public event Action? ScrollChanged;

    public double Offset
    {
        get => _offset;
        set
        {
            value = Math.Clamp(value, 0, MaxOffset);
            if (value == _offset) return;
            _offset = value;
            InvalidateVisual();
            ScrollChanged?.Invoke();
        }
    }

    public double MaxOffset => Math.Max(0, RowCount * RowH - Bounds.Height);

    int RowCount => _vm?.Rows.Count ?? 0;

    void OnChanged()
    {
        _offset = Math.Clamp(_offset, 0, MaxOffset);
        InvalidateVisual();
        ScrollChanged?.Invoke();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateRowLayout();
        if (_pendingScrollRow >= 0) ScrollIntoView(_pendingScrollRow);
        OnChanged();
    }

    /// <summary>Select and scroll: the row goes one row below the top unless it is strictly inside the viewport.</summary>
    void ScrollIntoView(int row)
    {
        if (Bounds.Height <= 0)
        {
            _pendingScrollRow = row;
            return;
        }
        _pendingScrollRow = -1;
        int firstFull = (int)Math.Ceiling(_offset / RowH);
        int lastFull = (int)Math.Floor((_offset + Bounds.Height) / RowH) - 1;
        if (row > firstFull && row < lastFull) return;
        Offset = (row - 1) * RowH;
    }

    /// <summary>Keyboard navigation: scroll as little as needed.</summary>
    void EnsureVisible(int row)
    {
        if (row * RowH < _offset) Offset = row * RowH;
        else if ((row + 1) * RowH > _offset + Bounds.Height) Offset = (row + 1) * RowH - Bounds.Height;
    }

    int RowAt(double y)
    {
        int row = (int)Math.Floor((y + _offset) / RowH);
        return row >= 0 && row < RowCount ? row : -1;
    }

    // ---------------------------------------------------------------- input

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var p = e.GetCurrentPoint(this);
        if (_vm is null || !p.Properties.IsLeftButtonPressed) return;
        Focus();
        e.Handled = true;
        int row = RowAt(p.Position.Y);
        if (row < 0 || InDotZone(row, p.Position.X)) return; // empty space; dot zone toggles collapsing (not built yet)
        if (e.ClickCount == 2)
        {
            _pressedRow = -1;
            _vm.DoubleClickAsync(row, LabelAt(row, p.Position)).Forget();
            return;
        }

        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control), shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        _pressedRow = !ctrl && !shift && _vm.IsSelected(row) ? row : -1;
        if (_pressedRow < 0) _vm.Click(row, ctrl, shift);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_pressedRow >= 0 && RowAt(e.GetPosition(this).Y) == _pressedRow) _vm?.Click(_pressedRow, false, false);
        _pressedRow = -1;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var pos = e.GetPosition(this);
        int row = RowAt(pos.Y);
        bool redraw = row != _hoverRow || (row >= 0 && InDotZone(row, pos.X) != InDotZone(row, _hoverPoint.X));
        _hoverRow = row;
        _hoverPoint = pos;
        if (redraw) InvalidateVisual();

        // The only label with a tooltip is the icon-only remote label.
        string? tip = row >= 0 ? LabelAt(row, pos)?.Tooltip ?? SubjectTipAt(row, pos) : null;
        if (!Equals(ToolTip.GetTip(this), tip))
        {
            ToolTip.SetTip(this, tip);
            ToolTip.SetIsOpen(this, tip is not null);
        }
    }

    RefLabel? LabelAt(int row, Point pos) =>
        GetVisual(row).Labels.LastOrDefault(l => l.Box.Contains(pos.WithY(pos.Y - (row * RowH - _offset)))).Label;

    /// <summary>Double-row layout: the subject has a tooltip with the full subject text, prefixed with the
    /// author when line 1 had no room for the author.</summary>
    string? SubjectTipAt(int row, Point pos) =>
        _double && GetVisual(row) is { Subject: { } ft } v && _vm!.GetHeader(row) is { } h
        && new Rect(v.SubjectOrigin, new Size(ft.Width, ft.Height)).Contains(pos.WithY(pos.Y - (row * RowH - _offset)))
            ? v.AuthorHidden ? $"{h.AuthorName}: {h.Subject}" : h.Subject : null;

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hoverRow = -1;
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        Offset -= e.Delta.Y * 3 * RowH;
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_vm is null || RowCount == 0) return;
        int page = Math.Max(1, (int)(Bounds.Height / RowH) - 1);
        int cursor = _vm.Cursor;
        int? target = e.Key switch
        {
            Key.Up => cursor < 0 ? 0 : cursor - 1,
            Key.Down => cursor + 1,
            Key.PageUp => cursor - page,
            Key.PageDown => Math.Max(cursor, 0) + page,
            Key.Home => 0,
            Key.End => RowCount - 1,
            _ => null,
        };
        if (target is { } t)
        {
            _vm.MoveTo(t, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
            EnsureVisible(_vm.Cursor);
            e.Handled = true;
        }
        else if (e.Key == Key.A && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            e.Handled = true; // Ctrl+A does nothing
        else
        {
            // Keyboard and mouse: copy, rename, delete, new branch / tag at the selection.
            const KeyModifiers ctrl = KeyModifiers.Control, ctrlShift = KeyModifiers.Control | KeyModifiers.Shift;
            Task? action = (e.Key, e.KeyModifiers) switch
            {
                (Key.C, ctrl) => _vm.CopyShasAsync(),
                (Key.C, ctrlShift) => _vm.CopyInfoAsync(),
                (Key.F2, KeyModifiers.None) => _vm.RenameAsync(),
                (Key.Delete, KeyModifiers.None) => _vm.DeleteAsync(),
                (Key.B, ctrlShift) => _vm.NewBranchAsync(),
                (Key.G, ctrlShift) => _vm.NewTagAsync(),
                _ => null,
            };
            if (action is null) return;
            e.Handled = true;
            action.Forget();
        }
    }

    // ---------------------------------------------------------------- rendering

    /// <summary>Theme resources and pens, cached until the theme changes (DynamicResource semantics).</summary>
    sealed class Res
    {
        public required IBrush ListBg, Text, OnAccent, Unreachable, SelBg, SelInactiveBg, HoverBg, RefText, Accent,
            CodeBg, CodeBgSelected, Body, BodySelected, TagBorder, TagBg, StashBorder, StashBg, SearchBg, Secondary, RowBorder;
        public required IBrush[] Lanes, RefBorders, RefFills, BisectBorders, BisectFills;
        public required IPen[] LanePens;
        public required IPen HoverPen, IconPen, FocusPen;
        public required Typeface Ui, UiBold, Mono;
        public required Func<string, Geometry?> FindIcon;
        readonly Dictionary<IBrush, IPen> _borderPens = [];
        readonly Dictionary<string, Geometry?> _icons = [];

        public IPen Border(IBrush brush) => _borderPens.TryGetValue(brush, out var p) ? p : _borderPens[brush] = new Pen(brush, 1);
        /// <summary>A glyph by resource key ("Icon.tag", "Brand.GitHub").</summary>
        public Geometry? Icon(string key) => _icons.TryGetValue(key, out var g) ? g : _icons[key] = FindIcon(key);
    }

    Res LoadResources()
    {
        IBrush B(string key) => this.TryFindResource(key, ActualThemeVariant, out var v) && v is IBrush b ? b : Brushes.Magenta;
        var ui = this.TryFindResource("Font.UI", ActualThemeVariant, out var f) && f is FontFamily ff ? ff : FontFamily.Default;
        var mono = this.TryFindResource("Font.Mono", ActualThemeVariant, out var m) && m is FontFamily mf ? mf : FontFamily.Default;
        var lanes = Enumerable.Range(0, 13).Select(i => B($"Color.Lane.{i}")).ToArray();
        string[] bisect = ["Good", "Bad", "Skip"];
        return new Res
        {
            ListBg = B("Color.List.Bg"), Text = B("Color.Text.Primary"), OnAccent = B("Color.Text.OnAccent"),
            Unreachable = B("Color.Revlist.UnreachableFg"), SelBg = B("Color.Row.BgSelected"),
            SelInactiveBg = B("Color.Row.BgSelectedInactive"), HoverBg = B("Color.Row.BgHover"), RefText = B("Color.Text.RefLabel"),
            Accent = B("Color.Accent.Base"), CodeBg = B("Color.Revlist.CodeBg"), CodeBgSelected = B("Color.Revlist.CodeBgSelected"),
            Body = B("Color.Revlist.BodyIndicator"), BodySelected = B("Color.Revlist.BodyIndicatorSelected"),
            TagBorder = B("Color.Ref.TagBorder"), TagBg = B("Color.Ref.TagBg"),
            StashBorder = B("Color.Ref.StashBorder"), StashBg = B("Color.Ref.StashBg"), SearchBg = B("Color.Search.MatchBg"),
            Secondary = B("Color.Text.Secondary"), RowBorder = B("Color.Border.Default"),
            Lanes = lanes,
            LanePens = lanes.Select(b => (IPen)new ImmutablePen((IImmutableBrush)b.ToImmutable(), 1.5)).ToArray(),
            HoverPen = new ImmutablePen((IImmutableBrush)B("Color.Graph.HoverOutline").ToImmutable(), 2),
            RefBorders = Enumerable.Range(0, 13).Select(i => B($"Color.RefLabel.Border.{i}")).ToArray(),
            RefFills = Enumerable.Range(0, 13).Select(i => B($"Color.RefLabel.Fill.{i}")).ToArray(),
            BisectBorders = bisect.Select(k => B($"Color.Ref.Bisect{k}Border")).ToArray(),
            BisectFills = bisect.Select(k => B($"Color.Ref.Bisect{k}Bg")).ToArray(),
            Ui = new Typeface(ui), UiBold = new Typeface(ui, weight: FontWeight.Bold), Mono = new Typeface(mono),
            IconPen = new Pen(B("Color.Text.RefLabel"), 2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round),
            // 1 px dotted (1 on, 2 off) keyboard-focus rectangle in row.bg-selected.
            FocusPen = new Pen(B("Color.Row.BgSelected"), 1, new DashStyle([1, 2], 0)),
            FindIcon = key => this.TryFindResource(key, ActualThemeVariant, out var g) ? g as Geometry : null,
        };
    }

    public override void Render(DrawingContext context)
    {
        _res ??= LoadResources();
        context.FillRectangle(_res.ListBg, new Rect(Bounds.Size)); // also makes the whole area hit-testable
        if (_vm is null || RowCount == 0) return;
        int first = (int)(_offset / RowH);
        int last = Math.Min(RowCount - 1, (int)((_offset + Bounds.Height) / RowH));
        var visuals = new Dictionary<int, RowVisual>(last - first + 1);
        for (int i = first; i <= last; i++) DrawBackground(context, i, i * RowH - _offset, visuals[i] = GetVisual(i));
        _visuals = visuals; // keeps only the visible rows

        // All graph lines of the visible rows in one geometry per lane colour: 13 draw calls instead of thousands with
        // 100+ lanes. Rebuilt only when the visible rows move (not for hover, focus or arriving headers).
        double col0End = _double ? GraphEndDouble : Column0End;
        var key = (_offset, Bounds.Width, Bounds.Height, (object?)_vm.Rows, RowCount);
        if (key != _linesKey)
        {
            _linesKey = key;
            var contexts = new StreamGeometryContext?[13];
            _lines = new StreamGeometry?[13];
            for (int i = first; i <= last; i++) AddGraph(contexts, _lines, _vm.Rows[i].Graph, Inset + GraphMargin, i * RowH - _offset, col0End, RowH);
            foreach (var c in contexts) c?.Dispose();
        }
        using (context.PushClip(new Rect(0, 0, col0End, Bounds.Height))) // from 0: a merge circle in column 0 reaches into the inset
            for (int c = 0; c < 13; c++)
                if (_lines[c] is { } g) context.DrawGeometry(null, _res.LanePens[c], g);

        for (int i = first; i <= last; i++)
            using (context.PushTransform(Matrix.CreateTranslation(0, i * RowH - _offset)))
                DrawRow(context, i, visuals[i]);
        _vm.OnRowsShown(first, last);
    }

    /// <summary>Right edge of column 0 (graph + labels + subject); the fixed columns follow.</summary>
    double Column0End => Math.Max(Inset, Bounds.Width - Inset - MarkerWidth - DateWidth - ShaWidth - AuthorWidth - AvatarWidth);

    /// <summary>Double-row layout: right edge of the graph, so line 1 keeps room for the avatar, the SHA box and the date
    /// (175 px) also with many lanes; lines and dots beyond it are clipped.</summary>
    double GraphEndDouble => Math.Max(Inset + GraphMargin + ColumnWidth, Bounds.Width - Inset - MarkerWidth - 4 - 175);

    /// <summary>Start of both text lines of a double row: 3 px right of the (capped) graph.</summary>
    double LineXDouble(GraphRow g) => Math.Min(Inset + GraphMargin + GraphWidth(g), GraphEndDouble) + 3;

    /// <summary>Extents of the texts of a row other than the subject (tests: nothing overlaps or leaves the row).</summary>
    internal List<Rect> TextExtents(int row) =>
        GetVisual(row).Texts.Select(t => new Rect(t.Origin, new Size(t.Text.Width, t.Text.Height))).ToList();

    static double GraphWidth(GraphRow g) => ColumnWidth * Math.Max(1, g.Lines.Count); // a lone root has no line but a dot

    bool InDotZone(int row, double x)
    {
        double dotX = Inset + GraphMargin + ColumnWidth * _vm!.Rows[row].Graph.DotColumn;
        if (dotX > (_double ? GraphEndDouble : Column0End)) return false; // a clipped dot does not block clicks on the text
        return x >= dotX - 5 && x < dotX + 7;
    }

    /// <summary>
    /// The laid-out texts of one row in row coordinates. Text layout is by far the most expensive part of a frame,
    /// so it is kept while the row stays visible and its inputs do not change.
    /// </summary>
    sealed class RowVisual
    {
        public required CommitRow Row;
        public required CommitHeader? Header;
        public required IBrush Fg;
        public required bool Active;
        public required double Width;
        public required string Search; // the search text when the row is a match, else ""
        public required List<PlacedLabel> Labels;
        public FormattedText? Subject;
        public Point SubjectOrigin;
        public List<Geometry> CodeBackgrounds = [];
        public readonly List<Geometry> SubjectHighlights = [], Highlights = []; // search matches in the subject / author and SHA
        public readonly List<(FormattedText Text, Point Origin)> Texts = [];
        public Rect AvatarRect;
        public bool AuthorHidden; // double-row layout: no room for the author on line 1 (wide graph)
        public string AvatarEmail = "";
        public FormattedText? Initials;
        public Task<Bitmap?>? Avatar;
    }

    Dictionary<int, RowVisual> _visuals = [];

    RowVisual GetVisual(int i)
    {
        _res ??= LoadResources();
        var row = _vm!.Rows[i];
        var header = _vm.GetHeader(i);
        bool active = _vm.IsSelected(i) && IsKeyboardFocusWithin;
        var fg = active ? _res.OnAccent : row.IsReachable ? _res.Text : _res.Unreachable;
        var search = _vm.IsMatch(i) ? _vm.SearchText : "";
        if (_visuals.TryGetValue(i, out var cached) && cached.Row == row && Equals(cached.Header, header)
            && cached.Fg == fg && cached.Active == active && cached.Width == Bounds.Width && cached.Search == search)
            return cached;

        var face = row.IsHead ? _res.UiBold : _res.Ui;
        double col0End = Column0End;
        double lineX = _double ? LineXDouble(row.Graph) : Inset + GraphMargin + GraphWidth(row.Graph);
        double x = lineX + (row.Upstream != UpstreamMark.None ? (_double ? 9 : 13) : 0);
        var labels = LayoutLabels(row, x, search, _double ? DoubleRowHeight / 2 : 0);
        if (labels.Count > 0) x = labels[^1].Next;
        var v = new RowVisual
        {
            Row = row, Header = header, Fg = fg, Active = active, Width = Bounds.Width, Search = search, Labels = labels,
        };

        if (_double) return DoubleRow(v, header, lineX, x, face);

        double sha = col0End + AvatarWidth + AuthorWidth + 5;
        Add(ShortSha(row.Sha), sha, ShaWidth - 5, true, true);
        if (header is null) return v; // subject, author and date stay empty until the header arrives

        if (col0End - x - 2 > 0) BuildSubject(v, header, x + 2, col0End - x - 2, face, row.IsHead);
        Add(header.AuthorName, col0End + AvatarWidth + 4, AuthorWidth - 6, false, true);
        Add(header.AuthorDate.ToLocalTime().ToString("d MMM yyyy HH:mm", CultureInfo.InvariantCulture),
            col0End + AvatarWidth + AuthorWidth + ShaWidth, DateWidth + 5, true, false);

        // Avatar, 16 px: the generated placeholder until the picture arrives, then a redraw.
        v.AvatarRect = new Rect(col0End + 1.5, 3.5, AvatarSize, AvatarSize);
        SetAvatar(v, header);
        return v;

        void Add(string text, double tx, double maxWidth, bool tabular, bool highlight)
        {
            var ft = Text(text, face, FontSize, fg, maxWidth);
            if (tabular) ft.SetFontFeatures(TabularDigits);
            var origin = new Point(tx, (RowH - 2 - ft.Height) / 2);
            if (highlight) Highlight(ft, text, search, origin, _res.Text, v.Highlights);
            v.Texts.Add((ft, origin));
        }
    }

    const double AvatarSize = 16;

    void SetAvatar(RowVisual v, CommitHeader header)
    {
        v.AvatarEmail = header.AuthorEmail;
        v.Initials = AvatarImages.InitialsText(header.AuthorName, _res.Ui, AvatarSize);
        v.Avatar = AvatarImages.ForEmail(header.AuthorEmail);
        AvatarImages.WhenLoaded(v.Avatar, InvalidateVisual);
    }

    /// <summary>
    /// Double-row layout. Line 1: avatar chip, author, short SHA right-aligned in a 50 px box (margin 8,2,4,0),
    /// author date at the far right. Line 2: upstream dot, labels (already placed), subject (13.5 px). Author, SHA and
    /// date use the secondary text colour. The "short format" date is "d MMM yyyy" (unverified); the author chip is plain text.
    /// </summary>
    RowVisual DoubleRow(RowVisual v, CommitHeader? header, double lineX, double x, Typeface face)
    {
        const double half = DoubleRowHeight / 2;
        var row = v.Row;
        var dim = v.Active ? v.Fg : row.IsReachable ? _res.Secondary : _res.Unreachable;
        double right = Bounds.Width - Inset - MarkerWidth - 4, ax = lineX + AvatarWidth + 4;
        var sha = ShortSha(row.Sha);
        if (header is null) // subject, author and date stay empty until the header arrives
        {
            Add(sha, ax, ShaBox, true, true);
            return v;
        }
        var date = header.AuthorDate.ToLocalTime().ToString("d MMM yyyy", CultureInfo.InvariantCulture);
        double dateX = right - Measure(date, true);
        double shaW = Measure(sha, true); // seven digits are a little wider than the 50 px box in the UI font
        double author = Math.Min(Measure(header.AuthorName, false), Math.Max(0, dateX - ax - 8 - Math.Max(ShaBox, shaW) - 4));
        if (author >= 30) Add(header.AuthorName, ax, author, false, true); // less would show little more than "…"
        else (author, v.AuthorHidden) = (0, true);
        Add(sha, ax + author + 8 + Math.Max(0, ShaBox - shaW), shaW + 1, true, true);
        Add(date, dateX, right - dateX + 1, true, false);
        if (right - x - 4 > 0) BuildSubject(v, header, x + 4, right - x - 4, face, row.IsHead, half, half, SubjectSizeDouble);
        v.AvatarRect = new Rect(lineX + 1, 3, AvatarSize, AvatarSize);
        SetAvatar(v, header);
        return v;

        double Measure(string text, bool tabular)
        {
            var ft = Text(text, face, FontSize, dim, double.PositiveInfinity);
            if (tabular) ft.SetFontFeatures(TabularDigits);
            return ft.WidthIncludingTrailingWhitespace;
        }

        void Add(string text, double tx, double maxWidth, bool tabular, bool highlight)
        {
            var ft = Text(text, face, FontSize, dim, maxWidth);
            if (tabular) ft.SetFontFeatures(TabularDigits);
            var origin = new Point(tx, (half - 2 - ft.Height) / 2 + 1);
            if (highlight) Highlight(ft, text, v.Search, origin, _res.Text, v.Highlights);
            v.Texts.Add((ft, origin));
        }
    }

    void DrawBackground(DrawingContext ctx, int i, double y, RowVisual v)
    {
        var vm = _vm!;
        bool selected = vm.IsSelected(i);
        var rowRect = new Rect(Inset, y, Math.Max(0, Bounds.Width - 2 * Inset), RowH);
        if (selected)
        {
            // Corners are rounded per group of adjacent selected rows.
            double top = i > 0 && vm.IsSelected(i - 1) ? 0 : 4, bottom = i + 1 < RowCount && vm.IsSelected(i + 1) ? 0 : 4;
            ctx.DrawRectangle(v.Active ? _res.SelBg : _res.SelInactiveBg, null, new RoundedRect(rowRect, top, top, bottom, bottom));
        }
        else if (i == _hoverRow)
            ctx.DrawRectangle(_res.HoverBg, null, new RoundedRect(rowRect, 4));
        if (vm.IsMatch(i)) // Row visual states: the 4 px search marker at the right edge
            ctx.FillRectangle(_res.SearchBg, new Rect(rowRect.Right - MarkerWidth, y, MarkerWidth, RowH));
        if (i == vm.Cursor && IsKeyboardFocusWithin)
            ctx.DrawRectangle(null, _res.FocusPen, new Rect(rowRect.X + 2.5, y + 2.5, Math.Max(0, rowRect.Width - 5), RowH - 5));
    }

    /// <summary>Everything of a row except background and graph lines, in row coordinates.</summary>
    void DrawRow(DrawingContext ctx, int i, RowVisual v)
    {
        var row = v.Row;
        double col0End = _double ? Bounds.Width - Inset - MarkerWidth : Column0End;
        using (ctx.PushClip(new Rect(0, 0, col0End, RowH)))
        {
            double gx = Inset + GraphMargin;
            // Double rows: labels and subject use the whole width, the dot stops at the capped graph like the lines.
            using (ctx.PushClip(new Rect(0, 0, _double ? GraphEndDouble : col0End, RowH)))
                DrawDot(ctx, row.Graph, gx, i == _hoverRow && InDotZone(i, _hoverPoint.X));
            if (row.Upstream != UpstreamMark.None)
                ctx.DrawEllipse(row.Upstream == UpstreamMark.Ahead ? _res.Accent : BehindBrush, null,
                    new Point((_double ? LineXDouble(row.Graph) : gx + GraphWidth(row.Graph)) + 4 + 2.5, _double ? RowH * 3 / 4 : RowH / 2), 2.5, 2.5);
            foreach (var l in v.Labels) DrawLabel(ctx, l, row.Graph.DotColumn % 13);
            foreach (var g in v.CodeBackgrounds) ctx.DrawGeometry(v.Active ? _res.CodeBgSelected : _res.CodeBg, null, g);
            foreach (var g in v.SubjectHighlights) ctx.DrawGeometry(_res.SearchBg, null, g);
            if (v.Subject is not null) ctx.DrawText(v.Subject, v.SubjectOrigin);
        }

        if (v.Avatar is not null)
            AvatarImages.Draw(ctx, v.AvatarRect, AvatarImages.Ready(v.Avatar), v.AvatarEmail, v.Initials);
        if (_double) ctx.DrawLine(_res.Border(_res.RowBorder), new Point(Inset, RowH - 0.5), new Point(Bounds.Width - Inset, RowH - 0.5));
        foreach (var g in v.Highlights) ctx.DrawGeometry(_res.SearchBg, null, g);
        foreach (var (text, origin) in v.Texts) ctx.DrawText(text, origin);
    }

    /// <summary>
    /// Highlighting: every case-insensitive occurrence of the search text gets the match background (geometry
    /// added to <paramref name="into"/>) and, when <paramref name="fg"/> is set, the normal foreground colour.
    /// </summary>
    static void Highlight(FormattedText ft, string text, string search, Point origin, IBrush? fg, List<Geometry> into)
    {
        if (search.Length == 0) return;
        for (int i = text.IndexOf(search, StringComparison.OrdinalIgnoreCase); i >= 0;
             i = text.IndexOf(search, i + search.Length, StringComparison.OrdinalIgnoreCase))
        {
            if (fg is not null) ft.SetForegroundBrush(fg, i, search.Length);
            if (ft.BuildHighlightGeometry(origin, i, search.Length) is { } g) into.Add(g);
        }
    }

    static string ShortSha(string sha) => sha.Length > 7 ? sha[..7] : sha;

    static FormattedText Text(string text, Typeface face, double size, IBrush brush, double maxWidth) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, size, brush)
        {
            MaxTextWidth = Math.Max(1, maxWidth), MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis,
        };

    // ---------------------------------------------------------------- graph

    /// <summary>
    /// Drawing: x of column c = 12c from the cell's left edge, the row's dot at mid height. Lines are added to one
    /// geometry per lane colour; lines that start right of <paramref name="maxX"/> are skipped (old histories have 100+ lanes).
    /// </summary>
    static void AddGraph(StreamGeometryContext?[] contexts, StreamGeometry?[] geometries, GraphRow g, double gx, double y, double maxX, double H)
    {
        double cy = y + H / 2, bottom = y + H, cx = gx + ColumnWidth * g.DotColumn;
        foreach (var l in g.Lines)
        {
            double tx = gx + ColumnWidth * l.Top, bx = gx + ColumnWidth * l.Bottom;
            if (Math.Min(l.Top < 0 ? cx : tx, l.Bottom < 0 ? cx : bx) - 2 > maxX) continue;
            int color = l.LaneId % 13;
            var c = contexts[color] ??= (geometries[color] = new StreamGeometry()).Open();
            if (l.Top >= 0 && l.Bottom >= 0)
            {
                c.BeginFigure(new Point(tx, y), false);
                if (l.Top == l.Bottom) c.LineTo(new Point(bx, bottom));
                else c.CubicBezierTo(new Point(tx, bottom - 5), new Point(bx, y + 5), new Point(bx, bottom));
            }
            else if (l.Top >= 0)
            {
                c.BeginFigure(new Point(tx, y), false);
                if (l.Top == g.DotColumn) c.LineTo(new Point(cx, cy));
                else c.CubicBezierTo(new Point(tx, cy), new Point(cx + 5, cy), new Point(cx, cy));
            }
            else
            {
                c.BeginFigure(new Point(cx, cy), false);
                if (l.Bottom == g.DotColumn) c.LineTo(new Point(bx, bottom));
                else c.CubicBezierTo(new Point(bx, cy), new Point(bx, cy + 5), new Point(bx, bottom));
            }
            c.EndFigure(false);
        }
    }

    /// <summary>Dots are drawn after the lines: normal = small filled circle, merge = open circle with a "v" chevron.</summary>
    void DrawDot(DrawingContext ctx, GraphRow g, double gx, bool hover)
    {
        var pen = _res.LanePens[g.DotLaneId % 13];
        double cx = gx + ColumnWidth * g.DotColumn, cy = RowH / 2;
        var center = new Point(cx, cy);
        if (!g.IsMerge)
        {
            ctx.DrawEllipse(_res.Lanes[g.DotLaneId % 13], pen, center, 1.7, 1.7);
            return;
        }
        ctx.DrawEllipse(_res.ListBg, hover ? _res.HoverPen : pen, center, 5.75, 5.75);
        // Expanded chevron (collapsing merges is not built yet).
        ctx.DrawLine(pen, new Point(cx - 3.5, cy - 1.75), new Point(cx, cy + 1.75));
        ctx.DrawLine(pen, new Point(cx, cy + 1.75), new Point(cx + 3.5, cy - 1.75));
    }

    // ---------------------------------------------------------------- ref labels

    /// <summary>One positioned label: Box = outline, IconWidth = width of the icon part (0 = none), Next = x after the right margin.</summary>
    readonly record struct PlacedLabel(RefLabel Label, Rect Box, double IconWidth, FormattedText? Text, double Next, List<Geometry>? Highlights = null);

    static Point LabelTextOrigin(Rect box, double iconWidth, FormattedText ft) =>
        new(box.X + iconWidth + 1 + 2, box.Y + (LabelHeight - ft.Height) / 2 - 1);

    const double IconPart = IconSize + 4 + 2; // icon, margin 2,0,2,0, 1 px border each side

    /// <summary>Ref labels: 18 px high, margin 3,2,3,2, text margin 2,-1,2,1, icons 14x14 with margin 2,0,2,0.</summary>
    List<PlacedLabel> LayoutLabels(CommitRow row, double x, string search, double lineTop)
    {
        var result = new List<PlacedLabel>(row.Labels.Count);
        double top = lineTop + ((_double ? DoubleRowHeight / 2 : RowHeight) - LabelHeight) / 2;
        foreach (var l in row.Labels)
        {
            double left = x + 3;
            if (l.IconOnly)
            {
                result.Add(new PlacedLabel(l, new Rect(left, top, IconPart, LabelHeight), IconPart, null, left + IconPart + 1));
                x = left + IconPart + 1;
                continue;
            }
            bool bold = l.Kind == RefLabelKind.LocalBranch && l.IsActive;
            var ft = Text(l.Text, bold ? _res.UiBold : _res.Ui, LabelFontSize, _res.RefText, double.PositiveInfinity);
            ft.Trimming = TextTrimming.None; // labels are never truncated
            double icon = l.Kind == RefLabelKind.LocalBranch && !InOtherWorktree(l) ? (l.IsActive ? 3 + IconSize : 0)
                : IconPart - 1; // two-part labels share a border
            double width = icon + ft.WidthIncludingTrailingWhitespace + 4 + 2;
            var box = new Rect(left, top, width, LabelHeight);
            List<Geometry>? highlights = search.Length > 0 ? [] : null;
            if (highlights is not null) Highlight(ft, l.Text, search, LabelTextOrigin(box, icon, ft), null, highlights);
            result.Add(new PlacedLabel(l, box, icon, ft, left + width + 3, highlights));
            x = left + width + 3;
        }
        return result;
    }

    void DrawLabel(DrawingContext ctx, PlacedLabel p, int colorIndex)
    {
        var l = p.Label;
        var box = p.Box.Deflate(0.5);
        bool worktree = InOtherWorktree(l);
        (IBrush border, IBrush fill, string? icon) = l.Kind switch
        {
            RefLabelKind.LocalBranch => (_res.RefBorders[colorIndex], _res.RefFills[colorIndex], l.IsActive ? "check" : worktree ? "folder-git-2" : null),
            RefLabelKind.RemoteBranch => (_res.RefBorders[colorIndex], _res.RefFills[colorIndex], null), // host icon below
            RefLabelKind.Tag => (_res.TagBorder, _res.TagBg, "tag"),
            RefLabelKind.BisectGood => (_res.BisectBorders[0], _res.BisectFills[0], "circle-check"),
            RefLabelKind.BisectBad => (_res.BisectBorders[1], _res.BisectFills[1], "circle-x"),
            RefLabelKind.BisectSkip => (_res.BisectBorders[2], _res.BisectFills[2], "circle-slash"),
            _ => (_res.StashBorder, _res.StashBg, "archive"),
        };
        var pen = _res.Border(border);

        double iconX;
        if (l.Kind == RefLabelKind.LocalBranch && !worktree)
        {
            // One box; the active branch has a check mark at the left (margin-left 3).
            ctx.DrawRectangle(fill, pen, new RoundedRect(box, 4));
            iconX = box.X + 3;
        }
        else if (l.Kind == RefLabelKind.Stash)
        {
            // One box; the text part has the fill, a 1 px left border and corners 0,3,3,0.
            ctx.DrawRectangle(_res.ListBg, pen, new RoundedRect(box, 4));
            double split = box.X + p.IconWidth;
            ctx.DrawRectangle(fill, null, new RoundedRect(new Rect(split, box.Y + 0.5, box.Right - 0.5 - split, box.Height - 1), 0, 3, 3, 0));
            ctx.DrawLine(pen, new Point(split, box.Y), new Point(split, box.Bottom));
            iconX = box.X + 2.5;
        }
        else if (l.IconOnly)
        {
            ctx.DrawRectangle(_res.ListBg, pen, new RoundedRect(box, 4));
            iconX = box.X + 2.5;
        }
        else
        {
            // Two parts: icon box on the list background, text box with the fill (corners 4,0,0,4 and 0,4,4,0).
            var iconBox = box.WithWidth(p.IconWidth);
            var textBox = new Rect(iconBox.Right, box.Y, box.Width - p.IconWidth, box.Height);
            ctx.DrawRectangle(fill, pen, new RoundedRect(textBox, 0, 4, 4, 0));
            ctx.DrawRectangle(_res.ListBg, pen, new RoundedRect(iconBox, 4, 0, 0, 4));
            iconX = box.X + 2.5;
        }
        // Remote branches: the host icon of the remote; brand logos are filled.
        var key = l.Kind == RefLabelKind.RemoteBranch ? HostIcon.ResourceKey(RemoteHost(l)) : icon is null ? null : "Icon." + icon;
        if (key is not null && _res.Icon(key) is { } geometry)
        {
            const double s = IconSize / 24;
            using (ctx.PushTransform(Matrix.CreateScale(s, s) * Matrix.CreateTranslation(iconX, p.Box.Y + (LabelHeight - IconSize) / 2)))
            {
                if (key.StartsWith("Brand.", StringComparison.Ordinal)) ctx.DrawGeometry(_res.RefText, null, geometry);
                else ctx.DrawGeometry(null, _res.IconPen, geometry);
            }
        }
        if (p.Highlights is not null)
            foreach (var g in p.Highlights) ctx.DrawGeometry(_res.SearchBg, null, g);
        if (p.Text is { } ft) ctx.DrawText(ft, LabelTextOrigin(p.Box, p.IconWidth, ft));
    }

    /// <summary>Ref labels: a local branch (not the active one) checked out in another worktree gets a
    /// "folder-git-2" icon cell on the list background (drawn like the two-part labels).</summary>
    internal bool InOtherWorktree(RefLabel l) =>
        l.Kind == RefLabelKind.LocalBranch && !l.IsActive && _vm?.Owner.WorktreeOf(l.FullName) is { IsCurrent: false };

    /// <summary>Host type of a remote-branch label's remote (Custom when unknown).</summary>
    internal HostType RemoteHost(RefLabel l) => _vm?.Owner is { } tab ? tab.Hosted(tab.RemoteOps.RemoteOf(l.FullName))?.Type ?? HostType.Custom : HostType.Custom;

    // ---------------------------------------------------------------- subject and author

    /// <summary>Subject styling: semi-bold prefix, `code` spans, search matches, trailing " ↩" when the commit has a body.</summary>
    void BuildSubject(RowVisual v, CommitHeader h, double x, double maxWidth, Typeface face, bool bold,
        double top = 0, double height = RowHeight, double size = FontSize)
    {
        var subject = h.Subject;
        var ft = Text(h.HasBody ? subject + " ↩" : subject, face, size, v.Fg, maxWidth);
        int prefix = BoldPrefixLength(subject);
        if (prefix > 0 && !bold) ft.SetFontWeight(FontWeight.SemiBold, 0, prefix);
        var spans = CodeSpans(subject);
        foreach (var (start, length) in spans)
        {
            ft.SetFontFamily(_res.Mono.FontFamily, start, length);
            ft.SetFontSize(11, start, length);
        }
        if (h.HasBody)
        {
            ft.SetFontSize(10, subject.Length, 2);
            ft.SetForegroundBrush(v.Active ? _res.BodySelected : _res.Body, subject.Length, 2);
        }
        v.Subject = ft;
        v.SubjectOrigin = new Point(x, top + (height - 2 - ft.Height) / 2);
        Highlight(ft, subject, v.Search, v.SubjectOrigin, _res.Text, v.SubjectHighlights);
        foreach (var (start, length) in spans)
            if (ft.BuildHighlightGeometry(v.SubjectOrigin, start, length) is { } g)
                v.CodeBackgrounds.Add(g);
    }

    internal static int BoldPrefixLength(string s)
    {
        if (s.StartsWith('[') && s.IndexOf(']', 1) is var close and > 0) return close + 1;
        int space = s.IndexOf(' ');
        var word = space < 0 ? s : s[..space];
        return word.Length > 0 && word.IndexOf(':', 1) is var colon and > 0 ? colon : 0;
    }

    internal static List<(int Start, int Length)> CodeSpans(string s)
    {
        var spans = new List<(int, int)>();
        for (int i = s.IndexOf('`'); i >= 0;)
        {
            int j = s.IndexOf('`', i + 1);
            if (j < 0) break;
            spans.Add((i, j - i + 1));
            i = s.IndexOf('`', j + 1);
        }
        return spans;
    }
}
