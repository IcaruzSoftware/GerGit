using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GerGit.ViewModels;
using GerGit.ViewModels.Dialogs;
using GerGit.Views;
using GerGit.Views.Dialogs;

namespace GerGit.Tests.Ui;

/// <summary>The layout zoom on every window, dialog, context menu, popup and tooltip. The scale is app-wide, so these tests run alone (the Appearance collection) and put 100 % back.</summary>
[Collection("Appearance")]
public sealed class ZoomTests : IDisposable
{
    readonly List<Window> _windows = [];

    /// <summary>Closes the windows a test left open (a failed assertion), so none stays in Zoom's open-window list.</summary>
    public void Dispose()
    {
        foreach (var w in _windows.ToList()) w.Close();
        Zoom.Set(100);
    }

    T Track<T>(T w) where T : Window
    {
        _windows.Add(w);
        w.Closed += (_, _) => _windows.Remove(w);
        return w;
    }

    static void Settle(params TopLevel[] tops)
    {
        Dispatcher.UIThread.RunJobs();
        foreach (var t in tops) t.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>The window or popup host a control is drawn in. Headless popups are overlay hosts inside the window
    /// (on X11 they are PopupRoot windows; both get the scale from Popup.InheritsTransform).</summary>
    static Visual Root(Visual v) => v.GetVisualAncestors().First(a => a is OverlayPopupHost or TopLevel);

    /// <summary>The scale a control is drawn with inside its window or popup.</summary>
    static double DrawnScale(Visual v) => v.TransformToVisual(Root(v))!.Value.M11;

    static double ContentScale(Window w) =>
        ((ScaleTransform)w.GetVisualDescendants().OfType<LayoutTransformControl>().First(c => c.Name == "PART_Zoom").LayoutTransform!).ScaleX;

    (Window Owner, DialogWindow Dialog, Task<bool> Shown) ShowDialog(DialogViewModel vm)
    {
        var owner = Track(new Window { Width = 800, Height = 600 });
        owner.Show();
        var shown = DialogWindow.ShowAsync(vm, owner);
        Settle(owner);
        var d = (DialogWindow)owner.OwnedWindows.Single();
        Settle(d);
        return (owner, d, shown);
    }

    [AvaloniaFact]
    public async Task A_dialog_opened_at_150_percent_is_scaled_with_its_minimum_size_and_takes_a_real_click()
    {
        Zoom.Set(150);
        var vm = new DialogViewModel { Title = "Zoomed", SubmitText = "OK", MinWidth = 400, MinHeight = 200 };
        var (owner, d, shown) = ShowDialog(vm);
        Assert.Equal((735, 600, 300), (d.Width, d.MinWidth, d.MinHeight)); // design size x 1.5
        Assert.Equal((1200, 900), (owner.Width, owner.Height)); // every window, not only dialogs
        Assert.Equal(1.5, ContentScale(d));
        var submit = d.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "SubmitButton");
        Assert.Equal(1.5, DrawnScale(submit), 3);
        Assert.Equal(d.ClientSize.Width, 1.5 * d.FindControl<Grid>("Frame")!.Bounds.Width + 1.5 * 40, 1); // content fills the window

        // Hit-testing through the zoom: a real pointer click in the middle of the scaled button.
        var at = submit.TranslatePoint(new Point(submit.Bounds.Width / 2, submit.Bounds.Height / 2), d)!.Value;
        Assert.True(at.X > submit.Bounds.Width); // window pixels, not layout units
        d.MouseMove(at);
        d.MouseDown(at, MouseButton.Left);
        d.MouseUp(at, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.True(await shown);
        owner.Close();
    }

    [AvaloniaFact]
    public async Task Changing_the_zoom_while_windows_are_open_updates_them_live()
    {
        var (owner, d, shown) = ShowDialog(new DialogViewModel { Title = "Live", MinHeight = 100 });
        Assert.Equal((490, 1.0), (d.Width, ContentScale(d)));
        var height = d.ClientSize.Height;

        Zoom.Set(150);
        Settle(owner, d);
        Assert.Equal((1.5, 1.5), (ContentScale(d), ContentScale(owner)));
        Assert.Equal((735, 150), (d.Width, d.MinHeight)); // fixed-size dialog: size follows
        Assert.Equal(1.5 * height, d.ClientSize.Height, 1); // the height fits the scaled content
        Assert.Equal(800, owner.Width); // resizable window keeps the size the user gave it

        Zoom.Set(100);
        Settle(owner, d);
        Assert.Equal((490, 100, 1.0), (d.Width, d.MinHeight, ContentScale(d)));
        d.Close();
        Assert.False(await shown);
        owner.Close();
    }

    [AvaloniaFact]
    public void At_100_percent_popups_take_no_transform_so_a_too_narrow_menu_keeps_its_left_edge()
    {
        var button = new Button { Content = "Target", Width = 100, Height = 30 };
        var w = Track(new Window { Width = 400, Height = 300, Content = button });
        w.Show();
        Settle(w);
        var menu = MenuControls.Open(button, [Menus.Item("First", null)]);
        Settle(w);
        Assert.False(((Popup)menu.Parent!).InheritsTransform);
        menu.Close();

        Zoom.Set(150);
        menu = MenuControls.Open(button, [Menus.Item("First", null)]);
        Settle(w);
        Assert.True(((Popup)menu.Parent!).InheritsTransform);
        menu.Close();
    }

    [AvaloniaFact]
    public void Context_menus_popups_and_tooltips_take_the_scale_of_their_window()
    {
        Zoom.Set(150);
        var button = new Button { Content = "Target", Width = 100, Height = 30 };
        var popup = new Popup { PlacementTarget = button, Child = new Border { Width = 100, Height = 40 } };
        var w = Track(new Window { Width = 400, Height = 300, Content = new StackPanel { Children = { button, popup } } });
        w.Show();
        Settle(w);

        var menu = MenuControls.Open(button, [Menus.Item("First", null), Menus.Sub("More", [Menus.Item("Inner", null)])]);
        Settle(w);
        var menuRoot = Root(menu);
        Assert.NotSame(w, menuRoot);
        Assert.Equal(1.5, DrawnScale(menu), 3);
        // Never narrowed to fit a screen (the scaled content would be cut off on both sides).
        Assert.False(menu.PlacementConstraintAdjustment.HasFlag(PopupPositionerConstraintAdjustment.ResizeX));
        Assert.True(menu.PlacementConstraintAdjustment.HasFlag(PopupPositionerConstraintAdjustment.SlideX));
        // A submenu opens from inside the scaled menu: scaled once, not twice.
        var more = menu.Items.OfType<MenuItem>().Last();
        more.IsSubMenuOpen = true;
        Settle(w);
        var inner = ((IEnumerable<Control>)more.ItemsSource!).OfType<MenuItem>().Single();
        Assert.NotSame(menuRoot, Root(inner));
        Assert.Equal(1.5, DrawnScale(inner), 3);
        menu.Close();

        popup.IsOpen = true;
        Settle(w);
        var popupRoot = (Control)Root(popup.Child!);
        Assert.NotSame(w, popupRoot);
        Assert.Equal(1.5, DrawnScale(popup.Child!), 3);
        Assert.Equal((150, 60), (popupRoot.Bounds.Width, popupRoot.Bounds.Height)); // the popup grows, no clipping

        // Live: the open popup follows a zoom change.
        Zoom.Set(200);
        Settle(w);
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Render);
        Settle(w);
        Assert.Equal(2, DrawnScale(popup.Child!), 3);
        popup.IsOpen = false;

        var tip = new ToolTip { Content = "Tip" };
        ToolTip.SetTip(button, tip);
        ToolTip.SetIsOpen(button, true);
        Settle(w);
        Assert.Equal(2, DrawnScale(tip), 3);
        ToolTip.SetIsOpen(button, false);
        w.Close();
    }

    [AvaloniaFact]
    public void The_main_window_size_and_minimum_size_scale()
    {
        Settings.Set(MainWindowViewModel.ZoomKey, 150);
        try
        {
            var vm = new MainWindowViewModel();
            var w = Track(new MainWindow { DataContext = vm });
            w.Show();
            w.StatusTimer.Stop();
            Settle(w);
            Assert.Equal(1.5, Zoom.Scale); // the setting drives the app-wide scale
            Assert.Equal((975, 600), (w.MinWidth, w.MinHeight)); // 650 x 400 design
            Assert.Equal((1500, 900), (w.Width, w.Height)); // 1000 x 600 design (the headless screen is 1920 x 1280)

            vm.ZoomIn();
            Settle(w);
            Assert.Equal(1040, w.MinWidth, 6);
            Assert.Equal(640, w.MinHeight, 6);
            Assert.Equal(1500, w.Width); // resizable: the user's size stays
            Assert.Equal(1.6, ContentScale(w), 3);
            w.Close();
        }
        finally { Settings.Set<int?>(MainWindowViewModel.ZoomKey, null); }
    }

    // The headless screen: 1920 x 1280 at scaling 1, so the cap is 1920 x 1240 (40 px title bar allowance).

    [AvaloniaFact]
    public void A_scaled_size_stays_on_the_screen()
    {
        Zoom.Set(200);
        var w = Track(new Window { Width = 1000, Height = 1000 });
        w.Show();
        Settle(w);
        Assert.Equal((1920, 1240), (w.Width, w.Height)); // not 2000 x 2000
        w.Close();
    }

    [AvaloniaFact]
    public void A_scaled_minimum_size_is_capped_at_the_screen_too_and_does_not_drift()
    {
        Zoom.Set(200);
        var w = Track(new Window { MinWidth = 1000, MinHeight = 700 });
        w.Show();
        Settle(w);
        Assert.Equal((1920, 1240), (w.MinWidth, w.MinHeight)); // not 2000 x 1400
        Assert.True(w.ClientSize.Width <= 1920 && w.ClientSize.Height <= 1240, $"{w.ClientSize}"); // the window fits

        Zoom.Set(150);
        Assert.Equal((1500, 1050), (w.MinWidth, w.MinHeight)); // recomputed from the design minimum
        for (int p = 110; p <= 190; p += 10) Zoom.Set(p);
        Zoom.Set(100);
        Assert.Equal((1000, 700), (w.MinWidth, w.MinHeight)); // exact after many steps
        w.Close();
    }

    [AvaloniaFact]
    public void A_window_shown_again_after_hide_is_not_scaled_twice()
    {
        Zoom.Set(150);
        var w = Track(new Window { Width = 400, Height = 300, MinWidth = 200 });
        w.Show();
        Settle(w);
        w.Hide();
        w.Show();
        Settle(w);
        Assert.Equal((600, 450, 300), (w.Width, w.Height, w.MinWidth));
        w.Close();
    }

    [AvaloniaFact]
    public void A_maximized_window_keeps_its_size_on_a_zoom_change_but_its_minimum_follows()
    {
        Zoom.Set(150);
        var w = Track(new Window { Width = 400, Height = 300, MinWidth = 200, CanResize = false, WindowState = WindowState.Maximized });
        w.Show();
        Settle(w);
        Assert.Equal(300, w.MinWidth);
        var (width, height) = (w.Width, w.Height);
        Zoom.Set(200);
        Settle(w);
        Assert.Equal((400, width, height), (w.MinWidth, w.Width, w.Height));
        w.Close();
    }

    [AvaloniaFact]
    public void A_combo_box_drop_down_is_scaled()
    {
        Zoom.Set(150);
        var combo = new ComboBox { ItemsSource = new[] { "a", "b" }, Width = 100 };
        var w = Track(new Window { Width = 400, Height = 300, Content = combo });
        w.Show();
        Settle(w);
        combo.IsDropDownOpen = true;
        Settle(w);
        var item = Assert.IsType<ComboBoxItem>(combo.ContainerFromIndex(0));
        Assert.NotSame(w, Root(item));
        Assert.Equal(1.5, DrawnScale(item), 3);
        combo.IsDropDownOpen = false;
        w.Close();
    }

    [AvaloniaFact]
    public void A_remembered_window_size_is_stored_unzoomed_so_it_does_not_grow()
    {
        const string key = "test.zoomGeometry";
        WindowGeometry.Geometry Stored() => Settings.Get<WindowGeometry.Geometry>(key, null!);
        Window Open()
        {
            var w = Track(new Window());
            WindowGeometry.Apply(w, key); // default 1020 x 720
            w.Show();
            w.Activate();
            Settle(w);
            return w;
        }
        try
        {
            Zoom.Set(150);
            for (int i = 0; i < 2; i++)
            {
                var w = Open();
                Assert.Equal((1530, 1080), (w.Width, w.Height));
                w.Close();
                Assert.Equal((1020, 720), (Stored().Width, Stored().Height));
            }
            // Capped at the screen and not changed by the user: the stored size stays (no shrinking).
            Zoom.Set(200);
            var capped = Open();
            Assert.Equal((1920, 1240), (capped.Width, capped.Height));
            capped.Close();
            Assert.Equal((1020, 720), (Stored().Width, Stored().Height));
            // Changed by the user: stored unzoomed.
            var resized = Open();
            resized.Width = 1800;
            Settle(resized);
            resized.Close();
            Assert.Equal((900, 720), (Stored().Width, Stored().Height)); // only the width changed: the capped height is not saved
            // Resized by the user, then the zoom changes while it is open: stored at the zoom it opened with.
            Settings.Set<WindowGeometry.Geometry?>(key, null);
            Zoom.Set(150);
            var rezoomed = Open();
            rezoomed.Width = 1800;
            Settle(rezoomed);
            Zoom.Set(200);
            Settle(rezoomed);
            rezoomed.Close();
            Assert.Equal((1200, 720), (Stored().Width, Stored().Height)); // 1800 / 1.5, not / 2
        }
        finally { Settings.Set<WindowGeometry.Geometry?>(key, null); }
    }
}
