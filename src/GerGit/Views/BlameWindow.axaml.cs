using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using AvaloniaEdit;
using GerGit.ViewModels;
using GerGit.Views.Dialogs;

namespace GerGit.Views;

/// <summary>Blame window: not modal, in the taskbar, geometry remembered; Esc closes, Ctrl+G Go To Line,
/// Alt+←/→ and the mouse back/forward buttons step through the visited commits.</summary>
public partial class BlameWindow : Window
{
    public const string GeometryKey = "windows.blame";

    /// <summary>The open blame windows (tests find the one a menu item opened).</summary>
    internal static readonly List<BlameWindow> Instances = [];

    readonly TextEditor _editor = null!;

    public BlameWindow() => InitializeComponent();

    internal BlameWindow(BlameViewModel vm) : this()
    {
        DataContext = vm;
        Vm = vm;
        _editor = DiffPane.FindControl<TextEditor>("Editor")!;
        _editor.FontSize = 14; // font size 14, horizontal scroll bar always visible
        _editor.HorizontalScrollBarVisibility = ScrollBarVisibility.Visible;
        Column.Editor = _editor;
        Column.Navigate = c => vm.Navigate(c.Sha);
        Column.Reveal = c => vm.RevealAsync(c.Sha).Forget();
        Column.OpenMenu = (c, target) => MenuControls.Open(target, vm.Menu(c));
        vm.ShowDialog = d => DialogWindow.ShowAsync(d, this);
        vm.Copy = async text => { if (Clipboard is { } cb) await cb.SetTextAsync(text); };
        vm.PickSaveFile = (name, folder) => FileDialogs.SaveFileAsync(this, name, folder);
        vm.ShowError = (title, text) => MessageWindow.ShowAsync(this, title, text);
        vm.GoToLineRequested += GoToLine;
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, OnPointer, RoutingStrategies.Tunnel);
        FileDialogs.CloseWithTab(this, vm.Tab);
        Instances.Add(this);
        Closed += (_, _) =>
        {
            vm.GoToLineRequested -= GoToLine;
            vm.Close();
            Instances.Remove(this);
        };
    }

    public BlameViewModel Vm { get; } = null!;

    /// <summary>Opens a blame window for <paramref name="path"/>, starting at <paramref name="sha"/> (HEAD when null). It closes with its repository tab.</summary>
    public static BlameWindow Open(RepoTabViewModel tab, string path, string? sha)
    {
        var w = new BlameWindow(new BlameViewModel(tab, path, sha));
        WindowGeometry.Apply(w, GeometryKey);
        w.Show();
        w.Vm.LoadAsync().Forget();
        return w;
    }

    void OnKey(object? sender, KeyEventArgs e)
    {
        var m = e.KeyModifiers;
        if (e.Key == Key.Escape && m == KeyModifiers.None && !DiffView.IsInFindBar(e.Source)) Close(); // Esc in the find box closes the bar
        else if (e.Key == Key.G && m == KeyModifiers.Control) Vm.GoToLineAsync().Forget();
        else if (e.Key == Key.Left && m == KeyModifiers.Alt) Vm.GoBackCommand.Execute(null);
        else if (e.Key == Key.Right && m == KeyModifiers.Alt) Vm.GoForwardCommand.Execute(null);
        else return;
        e.Handled = true;
    }

    void OnPointer(object? sender, PointerPressedEventArgs e)
    {
        var p = e.GetCurrentPoint(this).Properties;
        if (p.IsXButton1Pressed) Vm.GoBackCommand.Execute(null);
        else if (p.IsXButton2Pressed) Vm.GoForwardCommand.Execute(null);
        else return;
        e.Handled = true;
    }

    /// <summary>Puts document line <paramref name="line"/> (1-based, clamped) at the top and the caret on it.</summary>
    void GoToLine(int line)
    {
        if (_editor.Document is not { LineCount: > 0 } doc) return;
        line = Math.Clamp(line, 1, doc.LineCount);
        _editor.TextArea.Caret.Line = line;
        var tv = _editor.TextArea.TextView;
        if (_editor.FindDescendantOfType<ScrollViewer>() is { } sv) sv.Offset = sv.Offset.WithY(tv.GetVisualTopByDocumentLine(line));
    }
}
