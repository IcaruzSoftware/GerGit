using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using GerGit.Core;
using GerGit.ViewModels;
using GerGit.Views;

namespace GerGit.Controls;

/// <summary>
/// Clickable links inside a <see cref="SelectableTextBlock"/> (commit messages, issue links,
/// output views): link text in `text.link`, underlined; over a link the pointer is a hand and the tooltip is the
/// URL; a left click without a drag selection opens it (no Ctrl needed); right-click offers `Copy` and `Copy Link`.
/// Only http(s) links are ever opened, and only through <see cref="HostingService.OpenBrowser"/>.
/// Set <see cref="ContentProperty"/> (XAML or code), or build the inlines with <see cref="AddRuns"/> and call
/// <see cref="SetLinks"/> (OutputView).
/// </summary>
public static class Hyperlinks
{
    public static readonly AttachedProperty<LinkedText?> ContentProperty =
        AvaloniaProperty.RegisterAttached<SelectableTextBlock, LinkedText?>("Content", typeof(Hyperlinks));

    static readonly AttachedProperty<IReadOnlyList<TextLink>> LinksProperty =
        AvaloniaProperty.RegisterAttached<SelectableTextBlock, IReadOnlyList<TextLink>>("Links", typeof(Hyperlinks), []);

    static Hyperlinks() => ContentProperty.Changed.AddClassHandler<SelectableTextBlock>((b, _) => Show(b, GetContent(b)));

    public static LinkedText? GetContent(SelectableTextBlock block) => block.GetValue(ContentProperty);
    public static void SetContent(SelectableTextBlock block, LinkedText? value) => block.SetValue(ContentProperty, value);

    /// <summary>The links of the shown text (tests).</summary>
    internal static IReadOnlyList<TextLink> GetLinks(SelectableTextBlock block) => block.GetValue(LinksProperty);

    /// <summary>Opens a link; tests replace it. Refuses anything but http(s) before the browser does.</summary>
    internal static Action<string> Open { get; set; } = url => HostingService.Current.OpenBrowser(url);

    static void Show(SelectableTextBlock block, LinkedText? content)
    {
        var text = content?.Text ?? "";
        var links = content?.Links ?? [];
        if (links.Count == 0)
        {
            SetLinks(block, []);
            block.Inlines = null;
            block.Text = text;
            return;
        }
        var inlines = new InlineCollection();
        AddRuns(inlines, text, 0, links, null);
        block.Text = null;
        block.Inlines = inlines;
        SetLinks(block, links);
    }

    /// <summary>Adds <paramref name="text"/> as runs: the parts inside a link (offsets in block coordinates, where
    /// text[0] is at <paramref name="offset"/>) get the link look; <paramref name="style"/> styles every run first.</summary>
    public static void AddRuns(InlineCollection inlines, string text, int offset, IReadOnlyList<TextLink> links, Action<Run>? style)
    {
        int pos = 0;
        foreach (var link in links)
        {
            int start = Math.Clamp(link.Start - offset, pos, text.Length), end = Math.Clamp(link.Start + link.Length - offset, start, text.Length);
            if (end <= start) continue;
            if (start > pos) inlines.Add(Styled(new Run(text[pos..start]), style));
            var run = Styled(new Run(text[start..end]), style);
            run.Foreground = LinkBrush;
            run.TextDecorations = TextDecorations.Underline;
            inlines.Add(run);
            pos = end;
        }
        if (pos < text.Length) inlines.Add(Styled(new Run(text[pos..]), style));
    }

    static Run Styled(Run run, Action<Run>? style)
    {
        style?.Invoke(run);
        return run;
    }

    /// <summary>Looked up when the runs are built; <see cref="OnThemeChanged"/> recolours them after a theme switch.</summary>
    static IBrush? LinkBrush =>
        Application.Current?.TryFindResource("Color.Text.Link", Application.Current.ActualThemeVariant, out var b) == true ? b as IBrush : null;

    /// <summary>Remembers the links for hit tests and installs the pointer handlers once.</summary>
    public static void SetLinks(SelectableTextBlock block, IReadOnlyList<TextLink> links)
    {
        if (block.GetValue(LinksProperty) is { Count: 0 } && links.Count == 0) return;
        block.SetValue(LinksProperty, links);
        if (block.GetValue(InstalledProperty)) return;
        block.SetValue(InstalledProperty, true);
        block.ActualThemeVariantChanged += OnThemeChanged;
        block.AddHandler(InputElement.PointerMovedEvent, OnMoved, RoutingStrategies.Bubble, handledEventsToo: true);
        block.AddHandler(InputElement.PointerExitedEvent, OnExited, RoutingStrategies.Direct | RoutingStrategies.Bubble, handledEventsToo: true);
        block.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        block.AddHandler(InputElement.PointerReleasedEvent, OnReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        block.AddHandler(Control.ContextRequestedEvent, OnContext, RoutingStrategies.Tunnel);
    }

    /// <summary>The link runs (the underlined ones, also after <see cref="SearchHighlight"/> split them)
    /// take the new theme's link colour.</summary>
    static void OnThemeChanged(object? sender, EventArgs e)
    {
        if (sender is not TextBlock { Inlines: { } inlines }) return;
        foreach (var run in inlines.OfType<Run>())
            if (run.TextDecorations == TextDecorations.Underline) run.Foreground = LinkBrush;
    }

    static readonly AttachedProperty<bool> InstalledProperty =
        AvaloniaProperty.RegisterAttached<SelectableTextBlock, bool>("Installed", typeof(Hyperlinks));

    /// <summary>The link a left press went down on.</summary>
    static readonly AttachedProperty<TextLink?> PressedProperty =
        AvaloniaProperty.RegisterAttached<SelectableTextBlock, TextLink?>("Pressed", typeof(Hyperlinks));

    /// <summary>The link under a point of the block, null when none.</summary>
    internal static TextLink? LinkAt(SelectableTextBlock block, Point point)
    {
        var links = block.GetValue(LinksProperty);
        if (links.Count == 0) return null;
        var hit = block.TextLayout.HitTestPoint(new Point(point.X - block.Padding.Left, point.Y - block.Padding.Top));
        if (!hit.IsInside) return null;
        int i = hit.CharacterHit.FirstCharacterIndex;
        foreach (var link in links)
            if (i >= link.Start && i < link.Start + link.Length) return link;
        return null;
    }

    static void OnMoved(object? sender, PointerEventArgs e)
    {
        if (sender is not SelectableTextBlock block) return;
        var link = LinkAt(block, e.GetPosition(block));
        var tip = link?.Url;
        if (!Equals(ToolTip.GetTip(block), tip)) ToolTip.SetTip(block, tip);
        if (link is not null) block.Cursor = s_hand;
        else block.ClearValue(InputElement.CursorProperty);
    }

    static void OnExited(object? sender, PointerEventArgs e)
    {
        if (sender is not SelectableTextBlock block) return;
        ToolTip.SetTip(block, null);
        block.ClearValue(InputElement.CursorProperty);
    }

    static void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not SelectableTextBlock block) return;
        block.SetValue(PressedProperty, e.GetCurrentPoint(block).Properties.IsLeftButtonPressed ? LinkAt(block, e.GetPosition(block)) : null);
    }

    static void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is not SelectableTextBlock block || e.InitialPressMouseButton != MouseButton.Left) return;
        var down = block.GetValue(PressedProperty);
        block.SetValue(PressedProperty, null);
        // A drag that selected text is a selection, not a click.
        if (down is null || block.SelectionStart != block.SelectionEnd) return;
        if (LinkAt(block, e.GetPosition(block)) == down && IssueTracker.IsWebUrl(down.Value.Url)) Open(down.Value.Url);
    }

    static void OnContext(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not SelectableTextBlock block || !e.TryGetPosition(block, out var p) || LinkAt(block, p) is not { } link) return;
        e.Handled = true;
        var text = (block.Inlines?.Text ?? block.Text ?? "") is var all && link.Start + link.Length <= all.Length
            ? all.Substring(link.Start, link.Length) : link.Url;
        MenuControls.Open(block,
        [
            Menus.Item("Copy", () => Copy(block, text)),
            Menus.Item("Copy Link", () => Copy(block, link.Url)),
        ]);
    }

    static Task Copy(Visual v, string text) => TopLevel.GetTopLevel(v)?.Clipboard?.SetTextAsync(text) ?? Task.CompletedTask;

    static readonly Cursor s_hand = new(StandardCursorType.Hand);
}
