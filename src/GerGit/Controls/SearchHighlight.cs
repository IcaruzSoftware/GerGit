using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using GerGit.Core;

namespace GerGit.Controls;

/// <summary>
/// Search highlighting: every case-insensitive occurrence of the commit
/// list's search text gets the search-match background (normal foreground) in the text blocks of the details pane.
/// <see cref="TextProperty"/> is inherited (set it once on the pane); a block opts in with <see cref="IsEnabledProperty"/>.
/// Works on plain text (the bound Text stays as it is; the runs only draw it) and on text that is already made of runs
/// (the links of <see cref="Hyperlinks"/>): the runs are split at the matches and keep their own look.
/// </summary>
public static class SearchHighlight
{
    public static readonly AttachedProperty<string?> TextProperty =
        AvaloniaProperty.RegisterAttached<Control, string?>("Text", typeof(SearchHighlight), inherits: true);

    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, bool>("IsEnabled", typeof(SearchHighlight));

    public static string? GetText(Control control) => control.GetValue(TextProperty);
    public static void SetText(Control control, string? value) => control.SetValue(TextProperty, value);
    public static bool GetIsEnabled(TextBlock block) => block.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(TextBlock block, bool value) => block.SetValue(IsEnabledProperty, value);

    static readonly AvaloniaProperty[] Kept =
    [
        TextElement.ForegroundProperty, Inline.TextDecorationsProperty, TextElement.FontWeightProperty,
        TextElement.FontStyleProperty, TextElement.FontFamilyProperty, TextElement.FontSizeProperty,
    ];

    /// <summary>The block's runs were made here (so they show its old text once Text changes).</summary>
    static readonly AttachedProperty<bool> OwnsRunsProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, bool>("OwnsRuns", typeof(SearchHighlight));

    static SearchHighlight()
    {
        TextProperty.Changed.AddClassHandler<TextBlock>((b, _) => Apply(b));
        IsEnabledProperty.Changed.AddClassHandler<TextBlock>((b, _) =>
        {
            b.ActualThemeVariantChanged -= OnThemeChanged;
            if (GetIsEnabled(b)) b.ActualThemeVariantChanged += OnThemeChanged;
            Apply(b);
        });
        TextBlock.TextProperty.Changed.AddClassHandler<TextBlock>((b, _) => OnTextChanged(b));
        TextBlock.InlinesProperty.Changed.AddClassHandler<TextBlock>((b, _) => OnInlinesChanged(b));
    }

    static bool s_applying;

    /// <summary>The match runs (the only ones with a background) take the new theme's match colour.</summary>
    static void OnThemeChanged(object? sender, EventArgs e)
    {
        if (sender is not TextBlock { Inlines: { } inlines } block) return;
        var brush = DiffBackgroundRenderer.Brush(block, "Color.Search.MatchBg");
        foreach (var run in inlines.OfType<Run>())
            if (run.Background is not null) run.Background = brush;
    }

    /// <summary>A new text: drop the runs made for the old one (not relying on TextBlock to clear them), then mark the
    /// new text.</summary>
    static void OnTextChanged(TextBlock block)
    {
        if (s_applying || !GetIsEnabled(block)) return; // class handler: every TextBlock of the app passes here
        if (block.GetValue(OwnsRunsProperty))
        {
            s_applying = true;
            try { block.Inlines = null; }
            finally { s_applying = false; }
            block.SetValue(OwnsRunsProperty, false);
        }
        Apply(block);
    }

    /// <summary>Runs set by someone else (Hyperlinks) are split at the matches. Null: back to the plain Text, which is
    /// marked again (Hyperlinks sets the same text again without a Text change); a Text change that follows replaces
    /// those runs, as they are ours.</summary>
    static void OnInlinesChanged(TextBlock block)
    {
        if (s_applying || !GetIsEnabled(block)) return; // class handler: every TextBlock of the app passes here
        block.SetValue(OwnsRunsProperty, false);
        Apply(block);
    }

    static void Apply(TextBlock block)
    {
        if (s_applying || !GetIsEnabled(block)) return;
        var inlines = block.Inlines is { Count: > 0 } current ? current : null;
        if (inlines is not null && inlines.Any(i => i is not Run)) return; // other content: left alone
        var runs = inlines?.Cast<Run>().ToList();
        string text = runs is null ? block.Text ?? "" : string.Concat(runs.Select(r => r.Text));
        var matches = TextSearch.Matches(text, GetText(block));
        if (runs is null && matches.Count == 0) return; // plain text without a match: nothing to draw

        // One forward sweep over the runs and the (sorted, non-overlapping) matches: each piece ends where its run, the
        // current match or the gap before the next match ends.
        var brush = DiffBackgroundRenderer.Brush(block, "Color.Search.MatchBg");
        var result = new InlineCollection();
        int pos = 0, m = 0;
        foreach (var run in runs ?? [new Run(text)])
        {
            string part = run.Text ?? "";
            for (int i = 0; i < part.Length;)
            {
                int at = pos + i;
                while (m < matches.Count && matches[m].Start + matches[m].Length <= at) m++;
                bool inMatch = m < matches.Count && matches[m].Start <= at;
                int end = inMatch ? matches[m].Start + matches[m].Length : m < matches.Count ? matches[m].Start : int.MaxValue;
                int length = Math.Min(end - at, part.Length - i);
                var piece = new Run(part.Substring(i, length));
                foreach (var p in Kept)
                    if (run.IsSet(p)) piece.SetValue(p, run.GetValue(p));
                if (inMatch) piece.Background = brush;
                result.Add(piece);
                i += length;
            }
            pos += part.Length;
        }
        s_applying = true;
        try { block.Inlines = result; }
        finally { s_applying = false; }
        block.SetValue(OwnsRunsProperty, true);
    }
}
