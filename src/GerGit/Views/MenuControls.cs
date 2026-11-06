using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using GerGit.ViewModels;

namespace GerGit.Views;

/// <summary>Turns <see cref="MenuEntry"/> lists into menu controls and opens them (menus built when they open).</summary>
static class MenuControls
{
    public static List<Control> Build(IEnumerable<MenuEntry> entries) => entries.Select(Build).ToList();

    static Control Build(MenuEntry e)
    {
        if (e.Kind == MenuEntryKind.Separator) return new Separator();
        // Menu bar: underscores in names show literally (no access keys in built menus).
        var item = new MenuItem { Header = e.Header.Replace("_", "__"), IsEnabled = e.IsEnabled, Tag = e };
        if (e.Kind == MenuEntryKind.Header) item.FontWeight = FontWeight.SemiBold;
        if (e.Gesture is not null) item.InputGesture = KeyGesture.Parse(e.Gesture);
        if (e.IsChecked is { } check)
        {
            item.ToggleType = MenuItemToggleType.CheckBox;
            item.IsChecked = check;
        }
        if (e.Children is not null) item.ItemsSource = Build(e.Children);
        else if (e.Action is not null) item.Click += (_, _) => e.Invoke().Forget();
        return item;
    }

    /// <summary>Fills a menu item declared in XAML from an entry (hidden when null); a leaf item's Click handler calls
    /// <see cref="InvokeTag"/>.</summary>
    public static void Apply(MenuItem item, MenuEntry? e)
    {
        item.IsVisible = e is not null;
        if (e is null) return;
        item.Header = e.Header.Replace("_", "__");
        item.IsEnabled = e.IsEnabled;
        item.InputGesture = e.Gesture is null ? null : KeyGesture.Parse(e.Gesture);
        item.ItemsSource = e.Children is null ? null : Build(e.Children);
        item.Tag = e;
    }

    /// <summary>Click of an item filled by <see cref="Apply"/>: runs its entry (not for clicks bubbling up from sub-items).</summary>
    public static void InvokeTag(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        if (args.Source == sender && sender is MenuItem { Tag: MenuEntry { Children: null } e }) e.Invoke().Forget();
    }

    /// <summary>Opens a context menu at the pointer, or below <paramref name="target"/> for toolbar arrow menus.</summary>
    public static ContextMenu Open(Control target, IEnumerable<MenuEntry> entries, bool below = false)
    {
        var menu = new ContextMenu { ItemsSource = Build(entries), MaxWidth = 500 };
        if (below)
        {
            menu.Placement = PlacementMode.BottomEdgeAlignedLeft;
            menu.PlacementTarget = target;
        }
        menu.Open(target);
        return menu;
    }
}
