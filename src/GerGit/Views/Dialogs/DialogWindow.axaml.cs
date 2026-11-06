using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GerGit.ViewModels.Dialogs;

namespace GerGit.Views.Dialogs;

/// <summary>Hosts any <see cref="DialogViewModel"/> in the standard frame: modal, centred on the owner, no taskbar entry.</summary>
public partial class DialogWindow : Window
{
    static readonly List<DialogWindow> s_open = [];
    readonly DialogViewModel? _vm;

    public DialogWindow() => InitializeComponent();

    DialogWindow(DialogViewModel vm) : this()
    {
        _vm = vm;
        DataContext = vm;
        Width = vm.Width;
        if (double.IsNaN(vm.Height)) SizeToContent = SizeToContent.Height;
        else Height = vm.Height;
        CanResize = vm.CanResize;
        MinWidth = vm.MinWidth;
        MinHeight = vm.MinHeight;
        Body.Content = CreateView(vm);
        if (vm.Panel is { } panel && CreateView(panel) is { } view)
        {
            view.DataContext = panel;
            PanelHost.Content = view;
            // The panel takes the room the window gets (it scrolls inside); the body keeps its natural height.
            Frame.RowDefinitions[1].Height = GridLength.Auto;
            Frame.RowDefinitions[3].Height = GridLength.Star;
            // A growing panel must not push the window off the screen: its list scrolls instead.
            Opened += (_, _) =>
            {
                if (Screens.ScreenFromWindow(this) is { } screen)
                    MaxHeight = Math.Max(MinHeight, screen.WorkingArea.Height / screen.Scaling - TitleBarAllowance);
            };
        }
        if (vm.PlainFrame)
        {
            Frame.ColumnDefinitions[0].Width = new GridLength(0);
            Logo.IsVisible = Header.IsVisible = false;
            Body.Margin = default;
        }
        (vm.CancelIsDefault ? CancelButton : SubmitButton).IsDefault = true;
        vm.CloseRequested += OnCloseRequested;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(DialogViewModel.Status)) UpdateStatusIcon(); };
        UpdateStatusIcon();
        Opened += (_, _) =>
        {
            // The first text box gets the focus with its text selected.
            // Visible ones only: a ComboBox carries a hidden editable text box.
            if (Body.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => t.IsEffectivelyEnabled && t.IsEffectivelyVisible) is { } box)
            {
                box.Focus();
                // Posted: a selection made directly in Opened does not stick (the box is still being set up).
                if (vm.SelectTextOnOpen) Dispatcher.UIThread.Post(box.SelectAll, DispatcherPriority.Background);
            }
            else (vm.CancelIsDefault ? CancelButton : SubmitButton).Focus();
        };
        Closing += (_, e) =>
        {
            if (!vm.IsBusy) return;
            e.Cancel = true; // the job closes the dialog when it ends
            vm.RunningJob?.Cancel();
        };
        Closed += (_, _) =>
        {
            vm.CloseRequested -= OnCloseRequested;
            vm.Cancel(); // title-bar close button: closed without result
            s_open.Remove(this);
            MessageWindow.DialogClosed = true; // no activation refresh right after a dialog
        };
    }

    // Limitation: the window manager's title bar height is not known; 40 px covers the usual ones (also Zoom's size cap).
    internal const double TitleBarAllowance = 40;

    /// <summary>The body (or panel) view by naming convention: GerGit.ViewModels.Dialogs.XViewModel → GerGit.Views.Dialogs.XView.</summary>
    static Control? CreateView(object vm)
    {
        var name = vm.GetType().FullName!.Replace(".ViewModels.", ".Views.").Replace("ViewModel", "View");
        return typeof(DialogWindow).Assembly.GetType(name) is { } t ? (Control)Activator.CreateInstance(t)! : null;
    }

    /// <summary>
    /// Shows the dialog modally over <paramref name="owner"/> (default: the newest open dialog, else the main window) and
    /// completes when it closes: true = closed by submit. Without a visible owner (headless tests) it returns false at once.
    /// </summary>
    public static async Task<bool> ShowAsync(DialogViewModel vm, Window? owner = null)
    {
        owner ??= (vm.Modal ? s_open.LastOrDefault() : null)
            ?? (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner is not { IsVisible: true })
        {
            Trace.WriteLine($"{vm.Title}: {vm.Description}");
            return false;
        }
        var w = new DialogWindow(vm);
        if (!vm.Modal)
        {
            // Owned (on top of the owner, closed with it) but not blocking it; not in s_open, so no later dialog uses it
            // as its owner.
            var closed = new TaskCompletionSource();
            w.Closed += (_, _) => closed.TrySetResult();
            w.ShowActivated = false; // it appears while the user works: a reflexive Esc or Enter must not hit it
            w.Show(owner);
            await closed.Task;
            return vm.Result == true;
        }
        s_open.Add(w);
        try { await w.ShowDialog(owner); }
        finally { s_open.Remove(w); }
        return vm.Result == true;
    }

    void OnCloseRequested(bool result) => Close();

    void UpdateStatusIcon()
    {
        if (_vm is null) return;
        FooterIcon.Foreground = this.TryFindResource("Color.Status." + _vm.Status, ActualThemeVariant, out var b) ? b as IBrush : null;
    }

    async void OnSubmit(object? sender, RoutedEventArgs e) => await Safe(() => _vm!.SubmitAsync());

    async void OnSecondary(object? sender, RoutedEventArgs e) => await Safe(() => _vm!.SecondaryAsync());

    void OnCancel(object? sender, RoutedEventArgs e) => _vm?.Cancel();

    static async Task Safe(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { Trace.WriteLine(ex); } // a failing dialog action must not end the process
    }
}
