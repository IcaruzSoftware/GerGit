using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using GerGit.ViewModels;

namespace GerGit.Views;

/// <summary>Quick Launch window: owned by the main window, closed by Esc, by losing the focus or by a final
/// choice. Up / Down / Enter / Backspace work while the focus is in the input line.</summary>
public partial class QuickLaunchWindow : Window
{
    bool _closing;

    public QuickLaunchWindow() => InitializeComponent();

    public QuickLaunchWindow(QuickLaunchViewModel vm) : this()
    {
        DataContext = vm;
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
        Opened += (_, _) => Input.Focus();
        Deactivated += (_, _) => _ = Dismiss();
        Closing += (_, _) => _closing = true;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(QuickLaunchViewModel.Selected) && vm.Selected is { } row) List.ScrollIntoView(row);
            // The input is cleared when a chip is added or removed.
            if (e.PropertyName == nameof(QuickLaunchViewModel.Chip)) Input.CaretIndex = 0;
        };
    }

    QuickLaunchViewModel Vm => (QuickLaunchViewModel)DataContext!;

    /// <summary>Closes the window once (Esc, deactivation, a final choice); false when it was already closing.</summary>
    internal bool Dismiss()
    {
        if (_closing) return false;
        _closing = true;
        Close();
        return true;
    }

    void OnKey(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None) return;
        switch (e.Key)
        {
            case Key.Escape: _ = Dismiss(); break;
            case Key.Up: Vm.Move(-1); break;
            case Key.Down: Vm.Move(1); break;
            case Key.Enter: _ = Vm.ActivateSelectedAsync(); break;
            case Key.Back when Input.CaretIndex == 0 && Input.SelectionStart == Input.SelectionEnd && Vm.Back(): break;
            default: return;
        }
        e.Handled = true;
    }

    void OnRowReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton == MouseButton.Left && sender is Control { DataContext: QuickRow row }) Vm.Activate(row);
    }

    /// <summary>The name with the characters of the best fuzzy alignment highlighted.</summary>
    public static readonly FuncValueConverter<QuickRow?, InlineCollection?> NameInlines =
        new(r => r?.Choice is { } c ? Highlight(c.Name, r.NameHits) : null);

    /// <summary>"— secondary text" with its highlighted characters.</summary>
    public static readonly FuncValueConverter<QuickRow?, InlineCollection?> SecondaryInlines =
        new(r => r?.Choice?.Secondary is { } s ? Highlight(s, r.SecondaryHits, "— ") : null);

    // Matched characters are bold.
    static InlineCollection Highlight(string text, int[]? hits, string prefix = "")
    {
        var inlines = new InlineCollection();
        if (prefix.Length > 0) inlines.Add(new Run(prefix));
        var set = hits?.ToHashSet() ?? [];
        for (int i = 0; i < text.Length;)
        {
            bool hit = set.Contains(i);
            int j = i;
            while (j < text.Length && set.Contains(j) == hit) j++;
            inlines.Add(new Run(text[i..j]) { FontWeight = hit ? FontWeight.Bold : FontWeight.Normal });
            i = j;
        }
        return inlines;
    }
}
