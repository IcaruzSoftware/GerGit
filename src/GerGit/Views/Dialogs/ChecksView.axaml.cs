using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using GerGit.ViewModels.Dialogs;

namespace GerGit.Views.Dialogs;

public partial class ChecksView : UserControl
{
    public ChecksView()
    {
        InitializeComponent();
        AddHandler(PointerPressedEvent, OnTouched, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(GotFocusEvent, OnTouched, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(KeyDownEvent, OnTouched, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    /// <summary>A click, focus or key in the panel, or the opened output: a window that would close by itself stays open.</summary>
    void OnTouched(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ChecksViewModel vm) vm.Touched = true;
    }
}
