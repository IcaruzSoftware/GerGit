using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Threading;

namespace GerGit.Controls;

/// <summary>Colour classes of a job output line.</summary>
public enum OutputLineKind { Command, CommandBold, CommandNormal, Hint, AppHint, Noise, Warning, Error, Default }

/// <summary>
/// Read-only monospace viewer for job output and git errors with the per-line colouring; scrolled to the end
/// whenever the text changes. Used by the Activity Manager and the Git Error window. http(s) URLs are links, clickable
/// without Ctrl.
/// </summary>
public sealed partial class OutputView : UserControl
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<OutputView, string?>(nameof(Text));

    readonly SelectableTextBlock _text = new() { TextWrapping = TextWrapping.Wrap, FontSize = 13, Padding = new(3) };
    readonly ScrollViewer _scroll;

    public OutputView()
    {
        Content = _scroll = new ScrollViewer
        {
            Content = _text,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };
        AttachedToVisualTree += (_, _) => Rebuild(); // theme resources are found only inside the tree
        ActualThemeVariantChanged += (_, _) => Rebuild(); // the line colours of the new theme
    }

    public string? Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }

    /// <summary>The shown text (tests).</summary>
    internal SelectableTextBlock TextBlock => _text;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty) Rebuild();
    }

    string _shown = "";

    void Rebuild()
    {
        // Follow the end only when the user has not scrolled up, or when it is a different text (another job).
        var text = Text ?? "";
        bool follow = !text.StartsWith(_shown, StringComparison.Ordinal)
            || _scroll.Offset.Y >= _scroll.Extent.Height - _scroll.Viewport.Height - 1;
        _shown = text;
        var inlines = new InlineCollection();
        var links = new List<Core.TextLink>();
        var lines = text.TrimEnd('\n').Split('\n');
        int offset = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            var (key, bold) = Style(Classify(line));
            var brush = Resource(key) as IBrush;
            var lineLinks = Core.IssueTracker.FindUrls(line).Select(l => l with { Start = l.Start + offset }).ToList();
            links.AddRange(lineLinks);
            Hyperlinks.AddRuns(inlines, i < lines.Length - 1 ? line + "\n" : line, offset, lineLinks, run =>
            {
                run.FontWeight = bold ? FontWeight.Bold : FontWeight.Normal;
                run.Foreground = brush;
            });
            offset += line.Length + 1;
        }
        _text.FontFamily = Resource("Font.Mono") as FontFamily ?? FontFamily.Default;
        _text.Inlines = inlines;
        Hyperlinks.SetLinks(_text, links);
        if (follow) Dispatcher.UIThread.Post(_scroll.ScrollToEnd, DispatcherPriority.Background); // after the new text is measured
    }

    object? Resource(string key) => this.TryFindResource(key, ActualThemeVariant, out var v) ? v : null;

    static (string Key, bool Bold) Style(OutputLineKind kind) => kind switch
    {
        OutputLineKind.Command or OutputLineKind.CommandBold => ("Color.Output.Command", true),
        OutputLineKind.CommandNormal => ("Color.Output.Command", false),
        OutputLineKind.Hint => ("Color.Output.Hint", false),
        OutputLineKind.AppHint => ("Color.Output.Hint", true),
        OutputLineKind.Noise => ("Color.Output.Noise", false),
        OutputLineKind.Warning => ("Color.Output.Warning", false),
        OutputLineKind.Error => ("Color.Output.Error", true),
        _ => ("Color.Output.Default", false),
    };

    static readonly string[] CommandNormalPrefixes = ["CONFLICT ", " ! [rejected]", " * [new branch]", " - [deleted]", " + ", "Everything up-to-date"];
    static readonly string[] NoisePrefixes = [" = [up to date]", "Enumerating objects:", "Delta compression using", "Total ",
        "POST git-upload-pack", "POST git-receive-pack", "Auto-merging ", "  (use \"git "];
    static readonly string[] ErrorPrefixes = ["git:", "error: ", "fatal: ", "ssh: ", "Automatic merge failed;"];

    /// <summary>The first matching colouring rule wins.</summary>
    public static OutputLineKind Classify(string line)
    {
        bool Starts(string p) => line.StartsWith(p, StringComparison.Ordinal);
        if (Starts("$ ")) return OutputLineKind.Command;
        if (FilesChangedRegex().IsMatch(line) || Starts("Switched to ") || line.EndsWith("is the first bad commit", StringComparison.Ordinal))
            return OutputLineKind.CommandBold;
        if (CommandNormalPrefixes.Any(Starts)) return OutputLineKind.CommandNormal;
        if (Starts("hint: ")) return OutputLineKind.Hint;
        if (Starts(Core.GitErrors.HintPrefix)) return OutputLineKind.AppHint;
        if (NoisePrefixes.Any(Starts)) return OutputLineKind.Noise;
        if (Starts("warning: ")) return OutputLineKind.Warning;
        if (ErrorPrefixes.Any(Starts)) return OutputLineKind.Error;
        return OutputLineKind.Default;
    }

    [GeneratedRegex(@"^ \d* files? changed")]
    private static partial Regex FilesChangedRegex();
}
