using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace GerGit.Controls;

/// <summary>
/// `c:TrimmedToolTip.IsEnabled="True"` on a TextBlock with <see cref="TextBlock.TextTrimming"/>: the full text is its
/// tooltip, shown only while the text is cut off with an ellipsis (list rows, Changes tab). The tip follows the text; the check runs when the tooltip is about to open (ToolTipOpening, cancelled when the
/// text fits), so nothing runs per layout pass. The block's own <see cref="ToolTip.TipProperty"/> belongs to this
/// behaviour, and while it is on a parent's tooltip does not show over the text: switch it off where that one must win.
/// </summary>
public static class TrimmedToolTip
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, bool>("IsEnabled", typeof(TrimmedToolTip));

    static TrimmedToolTip()
    {
        IsEnabledProperty.Changed.AddClassHandler<TextBlock>((block, e) =>
        {
            ToolTip.RemoveToolTipOpeningHandler(block, OnOpening);
            if (e.NewValue is true)
            {
                ToolTip.AddToolTipOpeningHandler(block, OnOpening);
                ToolTip.SetTip(block, block.Text);
            }
            else block.ClearValue(ToolTip.TipProperty);
        });
        TextBlock.TextProperty.Changed.AddClassHandler<TextBlock>((block, _) =>
        {
            if (GetIsEnabled(block)) ToolTip.SetTip(block, block.Text);
        });
    }

    public static bool GetIsEnabled(TextBlock block) => block.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(TextBlock block, bool value) => block.SetValue(IsEnabledProperty, value);

    /// <summary>True when a line of the laid-out text ends in the ellipsis.</summary>
    public static bool IsTrimmed(TextBlock block) =>
        block.TextTrimming != TextTrimming.None && block.TextLayout.TextLines.Any(l => l.HasCollapsed);

    static void OnOpening(object? sender, CancelRoutedEventArgs e)
    {
        if (sender is not TextBlock block || IsTrimmed(block)) return;
        e.Cancel = true;
        e.Handled = true;
    }
}
