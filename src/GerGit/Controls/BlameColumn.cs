using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using AvaloniaEdit;
using GerGit.Core;
using GerGit.ViewModels;

namespace GerGit.Controls;

/// <summary>
/// Blame window left column: one row per line of the file view, drawn at the editor's own line positions so
/// both always scroll together (the wheel over the column scrolls the editor). A group's first row shows avatar
/// (tooltip `name &lt;email&gt;`), subject, date (tooltip: full date) and the 6-char SHA "button" (tooltip
/// `Navigate to '&lt;sha7&gt;'`); a 1 px line marks each group start.
/// </summary>
public sealed class BlameColumn : Control
{
    public static readonly StyledProperty<IReadOnlyList<BlameRow>?> RowsProperty =
        AvaloniaProperty.Register<BlameColumn, IReadOnlyList<BlameRow>?>(nameof(Rows));

    const double AvatarSize = 12, FontSize = 12;
    static readonly Cursor Hand = new(StandardCursorType.Hand);
    readonly Dictionary<string, Task<Bitmap?>> _avatars = [];
    TextEditor? _editor;

    static BlameColumn() => AffectsRender<BlameColumn>(RowsProperty);

    public BlameColumn()
    {
        ClipToBounds = true;
        ActualThemeVariantChanged += (_, _) => InvalidateVisual(); // colours are read while drawing
    }

    public IReadOnlyList<BlameRow>? Rows { get => GetValue(RowsProperty); set => SetValue(RowsProperty, value); }

    /// <summary>SHA button click, double-click and right-click on a row with a commit.</summary>
    public Action<CommitHeader>? Navigate, Reveal;
    public Action<CommitHeader, Control>? OpenMenu;

    /// <summary>The file view's editor whose lines the rows follow.</summary>
    public TextEditor? Editor
    {
        get => _editor;
        set
        {
            _editor = value;
            if (value is null) return;
            var tv = value.TextArea.TextView;
            tv.ScrollOffsetChanged += (_, _) => InvalidateVisual();
            tv.VisualLinesChanged += (_, _) => InvalidateVisual();
        }
    }

    /// <summary>Document line index (0-based) and row top (in this control) of each visible line.</summary>
    IEnumerable<(int Index, double Top, double Height)> VisibleRows()
    {
        if (_editor is null || Rows is not { Count: > 0 } rows) yield break;
        var tv = _editor.TextArea.TextView;
        if (tv.TranslatePoint(default, this) is not { } origin || _editor.Document is null) yield break;
        double h = tv.DefaultLineHeight, scroll = tv.ScrollOffset.Y;
        var first = tv.GetDocumentLineByVisualTop(Math.Max(0, scroll));
        if (first is null) yield break;
        for (int n = first.LineNumber; n <= Math.Min(rows.Count, _editor.Document.LineCount); n++)
        {
            double top = origin.Y + tv.GetVisualTopByDocumentLine(n) - scroll;
            if (top > Bounds.Height) yield break;
            yield return (n - 1, top, h);
        }
    }

    (Typeface Face, IBrush Text, IBrush Muted, IPen Border, IBrush Bg) Res()
    {
        IBrush B(string key) => this.TryFindResource(key, ActualThemeVariant, out var v) && v is IBrush b ? b : Brushes.Gray;
        var ui = this.TryFindResource("Font.UI", ActualThemeVariant, out var f) && f is FontFamily ff ? ff : FontFamily.Default;
        return (new Typeface(ui), B("Color.Text.Primary"), B("Color.Text.Secondary"), new Pen(B("Color.Border.Default"), 1), B("Color.List.Bg"));
    }

    FormattedText Text(string s, Typeface face, IBrush brush, double max = double.PositiveInfinity) =>
        new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, FontSize, brush)
        {
            MaxTextWidth = Math.Max(1, max), MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis,
        };

    /// <summary>Right-aligned SHA and the date left of it; x ranges for hit tests.</summary>
    (double ShaX, double DateX) Columns(Typeface face, CommitHeader c)
    {
        double shaX = Bounds.Width - 6 - Text(c.Sha[..6], face, Brushes.Gray).Width;
        return (shaX, shaX - 8 - Text(RevisionActions.ShortDate(c.AuthorDate), face, Brushes.Gray).Width);
    }

    public override void Render(DrawingContext ctx)
    {
        var res = Res();
        ctx.FillRectangle(res.Bg, new Rect(Bounds.Size)); // also makes the whole area hit-testable
        foreach (var (i, top, h) in VisibleRows())
        {
            var row = Rows![i];
            if (!row.ShowsCommit) continue;
            var c = row.Commit!;
            if (i > 0) ctx.DrawLine(res.Border, new Point(0, Math.Round(top) + 0.5), new Point(Bounds.Width, Math.Round(top) + 0.5));
            var avatar = Avatar(c.AuthorEmail);
            AvatarImages.Draw(ctx, new Rect(4, top + (h - AvatarSize) / 2, AvatarSize, AvatarSize), AvatarImages.Ready(avatar),
                c.AuthorEmail, AvatarImages.InitialsText(c.AuthorName, res.Face, AvatarSize));
            var (shaX, dateX) = Columns(res.Face, c);
            var sha = Text(c.Sha[..6], res.Face, res.Muted);
            var date = Text(RevisionActions.ShortDate(c.AuthorDate), res.Face, res.Muted);
            var subject = Text(c.Subject, res.Face, res.Text, dateX - 8 - 22);
            double y = top + (h - sha.Height) / 2;
            ctx.DrawText(sha, new Point(shaX, y));
            ctx.DrawText(date, new Point(dateX, y));
            if (dateX - 8 > 22) ctx.DrawText(subject, new Point(22, y));
        }
    }

    Task<Bitmap?> Avatar(string email)
    {
        if (_avatars.TryGetValue(email, out var t)) return t;
        t = _avatars[email] = AvatarImages.ForEmail(email);
        AvatarImages.WhenLoaded(t, InvalidateVisual);
        return t;
    }

    /// <summary>The commit of the row under <paramref name="p"/> (any row of its group); null for empty rows.</summary>
    CommitHeader? CommitAt(Point p, out bool groupStart)
    {
        groupStart = false;
        foreach (var (i, top, h) in VisibleRows())
        {
            if (p.Y < top || p.Y >= top + h) continue;
            groupStart = Rows![i].IsGroupStart;
            return Rows[i].Commit;
        }
        return null;
    }

    enum Part { None, Avatar, Date, Sha }

    Part PartAt(Point p, CommitHeader c)
    {
        var (shaX, dateX) = Columns(Res().Face, c);
        return p.X >= shaX ? Part.Sha : p.X >= dateX ? Part.Date : p.X >= 4 && p.X < 4 + AvatarSize ? Part.Avatar : Part.None;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        string? tip = CommitAt(p, out var start) is { } c && start
            ? PartAt(p, c) switch
            {
                Part.Avatar => $"{c.AuthorName} <{c.AuthorEmail}>",
                Part.Date => c.AuthorDate.ToLocalTime().ToString("d MMM yyyy HH:mm:ss zzz", CultureInfo.InvariantCulture),
                Part.Sha => $"Navigate to '{c.Sha[..7]}'",
                _ => null,
            }
            : null;
        if (!Equals(ToolTip.GetTip(this), tip)) ToolTip.SetTip(this, tip);
        Cursor = tip?.StartsWith("Navigate", StringComparison.Ordinal) == true ? Hand : Cursor.Default;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var p = e.GetPosition(this);
        if (CommitAt(p, out var start) is not { } c) return;
        var point = e.GetCurrentPoint(this).Properties;
        if (point.IsRightButtonPressed) OpenMenu?.Invoke(c, this);
        else if (point.IsLeftButtonPressed && e.ClickCount == 2) Reveal?.Invoke(c);
        else if (point.IsLeftButtonPressed && start && PartAt(p, c) == Part.Sha) Navigate?.Invoke(c);
        else return;
        e.Handled = true;
    }

    /// <summary>The wheel over the column scrolls the file view.</summary>
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        // TextEditor.ScrollToVerticalOffset has no effect in AvaloniaEdit 12 (see DiffView): move its ScrollViewer.
        if (_editor?.FindDescendantOfType<ScrollViewer>() is not { } sv) return;
        sv.Offset = sv.Offset.WithY(sv.Offset.Y - e.Delta.Y * 3 * _editor.TextArea.TextView.DefaultLineHeight);
        e.Handled = true;
    }
}
