using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using GerGit.Controls;
using GerGit.ViewModels;

namespace GerGit.Views;

public partial class RepoTabView : UserControl
{
    /// <summary>Commit list width in the vertical layout.</summary>
    public const string ListWidthKey = "layout.topBottomListWidth";

    MainWindowViewModel? _main;
    bool _vertical;

    public RepoTabView()
    {
        InitializeComponent();
        ApplyLayout(false);
        // The list width is saved when the drag ends.
        ListSplitter.DragCompleted += (_, _) =>
        {
            if (_vertical) Settings.Set(ListWidthKey, RevisionGrid.ColumnDefinitions[0].ActualWidth);
        };
        // The details pane keeps 110 px (its minimum in the horizontal layout) so the splitter never leaves the window
        // (high zoom, or a width saved on a larger screen); the list's minimum of 300 wins over it.
        RevisionGrid.SizeChanged += (_, _) => LimitListWidth();
    }

    void LimitListWidth()
    {
        if (_vertical) RevisionGrid.ColumnDefinitions[0].MaxWidth =
            Math.Max(300, RevisionGrid.Bounds.Width - 110 - ListSplitter.DesiredSize.Width);
    }

    // Another tab may have saved a new list width while this one was hidden.
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty && IsVisible && _vertical)
            RevisionGrid.ColumnDefinitions[0].Width = new GridLength(Settings.GetLength(ListWidthKey, 350.0));
    }

    // The layout is app-wide (MainWindowViewModel.IsVerticalLayout); each tab view follows it while attached.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _main = (DataContext as RepoTabViewModel)?.Main;
        if (_main is null) return;
        _main.PropertyChanged += OnMainChanged;
        ApplyLayout(_main.IsVerticalLayout);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_main is not null) _main.PropertyChanged -= OnMainChanged;
        _main = null;
    }

    void OnMainChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.IsVerticalLayout)) ApplyLayout(_main!.IsVerticalLayout);
    }

    /// <summary>Repository tab content: Horizontal = list on top, details below (each at least 110 high);
    /// Vertical = list left (min 300, saved width, default 350), details right. Limitation: the horizontal heights
    /// start at half and half again after a switch (they are not saved).</summary>
    void ApplyLayout(bool vertical)
    {
        _vertical = vertical;
        var g = RevisionGrid;
        g.RowDefinitions.Clear();
        g.ColumnDefinitions.Clear();
        if (vertical)
            g.ColumnDefinitions.AddRange([
                new(Settings.GetLength(ListWidthKey, 350.0), GridUnitType.Pixel) { MinWidth = 300 },
                new(GridLength.Auto), new(GridLength.Star)]);
        else
            g.RowDefinitions.AddRange([
                new(GridLength.Star) { MinHeight = 110 }, new(GridLength.Auto), new(GridLength.Star) { MinHeight = 110 }]);
        Control[] parts = [RevisionList, ListSplitter, DetailsPane];
        for (int i = 0; i < parts.Length; i++)
        {
            Grid.SetRow(parts[i], vertical ? 0 : i);
            Grid.SetColumn(parts[i], vertical ? i : 0);
        }
        ListSplitter.Classes.Set("h", !vertical);
        ListSplitter.Classes.Set("v", vertical);
        LimitListWidth();
        RevisionList.FindControl<CommitListPanel>("List")!.IsVerticalLayout = vertical; // double-row rows when narrow
    }
}
