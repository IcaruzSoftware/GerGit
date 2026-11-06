using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using GerGit.Core;

namespace GerGit.Controls;

/// <summary>
/// Reference text box: a branch or tag icon at the left, Space types the replacement
/// character (setting `general.branchNameSpaceChar`, default "-"), and pasted text is cleaned with
/// <see cref="RefName.CleanPaste"/>; a paste without text does nothing. Validation stays with the dialog (<see cref="RefName.Validate"/>).
/// Not built yet: folder autocomplete popup.
/// </summary>
public sealed class RefNameBox : TextBox
{
    public static readonly StyledProperty<string> IconProperty =
        AvaloniaProperty.Register<RefNameBox, string>(nameof(Icon), "git-branch");

    readonly LucideIcon _icon = new() { Size = 14, Margin = new(6, 0, 0, 0), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };

    public RefNameBox()
    {
        _icon.Icon = Icon;
        InnerLeftContent = _icon;
        PastingFromClipboard += OnPasting;
    }

    protected override Type StyleKeyOverride => typeof(TextBox);

    /// <summary>Lucide icon name: "git-branch" (default) or "tag".</summary>
    public string Icon { get => GetValue(IconProperty); set => SetValue(IconProperty, value); }

    /// <summary>Replacement character; null = the setting.</summary>
    public string? Replacement { get; set; }

    string Rep => Replacement ?? Settings.SpaceReplacement;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IconProperty) _icon.Icon = Icon;
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        if (e.Text is { } t && t.Contains(' ')) e.Text = t.Replace(" ", Rep);
        base.OnTextInput(e);
    }

    async void OnPasting(object? sender, RoutedEventArgs e)
    {
        e.Handled = true; // the box inserts the cleaned text itself
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;
        try { InsertPasted(await clipboard.TryGetTextAsync()); }
        catch (Exception ex) { System.Diagnostics.Trace.WriteLine(ex); } // clipboard owner gone: nothing pasted
    }

    /// <summary>Replaces the selection with the cleaned text; null (no text on the clipboard) does nothing.</summary>
    internal void InsertPasted(string? text)
    {
        if (text is null) return;
        text = RefName.CleanPaste(text, Rep);
        var current = Text ?? "";
        int start = Math.Min(SelectionStart, SelectionEnd), end = Math.Max(SelectionStart, SelectionEnd);
        start = Math.Clamp(start, 0, current.Length);
        end = Math.Clamp(end, start, current.Length);
        Text = current[..start] + text + current[end..];
        ClearSelection();
        CaretIndex = start + text.Length;
    }
}
