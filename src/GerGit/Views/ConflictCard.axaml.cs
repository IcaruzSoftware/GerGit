using Avalonia.Controls;
using Avalonia.Interactivity;
using GerGit.ViewModels;

namespace GerGit.Views;

public partial class ConflictCard : UserControl
{
    public ConflictCard() => InitializeComponent();

    /// <summary>The external merge drop-down: every visible tool, below the arrow.</summary>
    void OnMergeToolArrow(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ConflictCardViewModel vm) MenuControls.Open(MergeToolArrow, vm.MergeToolMenu(), below: true);
    }
}
