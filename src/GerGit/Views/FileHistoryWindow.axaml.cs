using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using GerGit.ViewModels;

namespace GerGit.Views;

/// <summary>File History window: not modal, in the taskbar, geometry remembered; Esc closes. List keys:
/// Ctrl+C / Ctrl+Shift+C copy, Ctrl+Alt+Shift+O opens the file as of the commit, Ctrl+D runs the external diff,
/// double-click reveals the commit.</summary>
public partial class FileHistoryWindow : Window
{
    public const string GeometryKey = "windows.history";

    /// <summary>The open history windows (tests find the one a menu item opened).</summary>
    internal static readonly List<FileHistoryWindow> Instances = [];

    public FileHistoryWindow() => InitializeComponent();

    internal FileHistoryWindow(FileHistoryViewModel vm) : this()
    {
        DataContext = vm;
        Vm = vm;
        vm.Copy = async text => { if (Clipboard is { } cb) await cb.SetTextAsync(text); };
        vm.PickSaveFile = (name, folder) => FileDialogs.SaveFileAsync(this, name, folder);
        vm.Confirm = (title, text, button, width) => MessageWindow.ShowAsync(this, title, text, button, "Cancel", width);
        vm.ShowError = (title, text) => MessageWindow.ShowAsync(this, title, text);
        FileDialogs.CloseWithTab(this, vm.Tab);
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
        HistoryList.DoubleTapped += (_, e) =>
        {
            if (vm.SelectedRows is [var row] && e.Source is Control { DataContext: HistoryRow }) vm.RevealAsync(row.Revision.Sha).Forget();
        };
        HistoryList.ContextRequested += (_, e) =>
        {
            e.Handled = true;
            if (vm.Menu() is { Count: > 0 } menu) MenuControls.Open(HistoryList, menu);
        };
        Instances.Add(this);
        Closed += (_, _) =>
        {
            vm.Close();
            Instances.Remove(this);
        };
    }

    public FileHistoryViewModel Vm { get; } = null!;

    /// <summary>Opens a history window for a file or folder, selecting <paramref name="sha"/> when it is listed. It closes
    /// with its repository tab.</summary>
    public static FileHistoryWindow Open(RepoTabViewModel tab, string path, bool isFolder, string? sha)
    {
        var w = new FileHistoryWindow(new FileHistoryViewModel(tab, path, isFolder, sha));
        WindowGeometry.Apply(w, GeometryKey);
        w.Show();
        LoadAsync().Forget();
        return w;

        async Task LoadAsync()
        {
            await w.Vm.LoadAsync();
            w.HistoryList.Focus(); // focus goes to the list
        }
    }

    void OnKey(object? sender, KeyEventArgs e)
    {
        var m = e.KeyModifiers;
        if (e.Key == Key.Escape && m == KeyModifiers.None && !DiffView.IsInFindBar(e.Source)) Close(); // Esc in the find box closes the bar
        else if (!HistoryList.IsKeyboardFocusWithin) return;
        else if (e.Key == Key.C && m == KeyModifiers.Control) Vm.CopyShasAsync().Forget();
        else if (e.Key == Key.C && m == (KeyModifiers.Control | KeyModifiers.Shift)) Vm.CopyInfoAsync().Forget();
        else if (e.Key == Key.O && m == (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift)) Vm.OpenAtCommitAsync().Forget();
        else if (e.Key == Key.D && m == KeyModifiers.Control) Vm.ExternalDiffShortcutAsync().Forget();
        else return;
        e.Handled = true;
    }
}
