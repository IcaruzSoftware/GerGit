using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GerGit.Controls;
using GerGit.ViewModels;
using GerGit.Views;
using static GerGit.Tests.Ui.CommitListTests;

namespace GerGit.Tests.Ui;

[CollectionDefinition("Appearance", DisableParallelization = true)]
public sealed class AppearanceCollection;

/// <summary>Appearance menu, vertical commit list layout and layout zoom. The settings are
/// app-wide and read by every new main window, so these tests run alone and put the defaults back afterwards.</summary>
[Collection("Appearance")]
public sealed class AppearanceTests : IDisposable
{
    public void Dispose()
    {
        Settings.Set<string?>(MainWindowViewModel.CommitListLayoutKey, null);
        Settings.Set<double?>(RepoTabView.ListWidthKey, null);
        Settings.Set<int?>(MainWindowViewModel.ZoomKey, null);
        Zoom.Set(100); // the app-wide scale the main window drives
        Settings.Set<string?>(AppTheme.ThemeKey, null); // A theme chosen here must not carry over to later apps
        Settings.Set<bool?>(AppTheme.FollowOsKey, null);
        Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
    }

    sealed class Ui : IDisposable
    {
        public readonly MainWindowViewModel Vm = new();
        public readonly MainWindow Window;

        public Ui(double width = 1000)
        {
            Window = new MainWindow { DataContext = Vm, Width = width };
            Window.Show();
            Window.StatusTimer.Stop();
            Settle();
        }

        public void Settle()
        {
            Dispatcher.UIThread.RunJobs();
            Window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }

        public RepoTabView View(RepoTabViewModel tab) =>
            Window.GetVisualDescendants().OfType<RepoTabView>().Single(v => v.DataContext == tab);

        public void Click(Control c, RawInputModifiers m = RawInputModifiers.None)
        {
            var at = c.TranslatePoint(new Point(c.Bounds.Width / 2, c.Bounds.Height / 2), Window)!.Value;
            Window.MouseMove(at, m);
            Window.MouseDown(at, MouseButton.Left, m);
            Window.MouseUp(at, MouseButton.Left, m);
            Settle();
        }

        /// <summary>Drags the list splitter by <paramref name="dx"/> window pixels.</summary>
        public void Drag(GridSplitter splitter, double dx)
        {
            var at = splitter.TranslatePoint(new Point(splitter.Bounds.Width / 2, splitter.Bounds.Height / 2), Window)!.Value;
            Window.MouseMove(at);
            Window.MouseDown(at, MouseButton.Left);
            Window.MouseMove(at + new Point(dx / 2, 0));
            Window.MouseMove(at + new Point(dx, 0));
            Window.MouseUp(at + new Point(dx, 0), MouseButton.Left);
            Settle();
        }

        public void Dispose()
        {
            foreach (var t in Vm.Tabs.ToList()) Vm.CloseTab(t);
            Window.Close();
        }
    }

    static TempRepo NewRepo()
    {
        var r = new TempRepo();
        r.Commit("init", 0, ("a.txt", "1\n"));
        return r;
    }

    static Grid Revision(RepoTabView v) => v.FindControl<Grid>("RevisionGrid")!;

    static CommitListPanel List(RepoTabView v) => v.GetVisualDescendants().OfType<CommitListPanel>().Single();

    /// <summary>"Header (disabled) (checked)" per entry, separators "-".</summary>
    static string[] Flat(IEnumerable<MenuEntry> entries) => entries.Select(e =>
        e.Kind == MenuEntryKind.Separator ? "-"
        : e.Header + (e.IsEnabled || e.Kind == MenuEntryKind.Header ? "" : " (disabled)") + (e.IsChecked == true ? " (checked)" : "")).ToArray();

    [AvaloniaFact]
    public async Task Menu_check_marks_and_disabled_states_follow_the_tab_and_the_layout()
    {
        using var repo = NewRepo();
        using var ui = new Ui();
        // No repository tab: the layout items are disabled; the tests run dark (TestSettings), so Dark is checked.
        Assert.Equal(["Theme", "Light", "Dark (checked)", "Use System Setting (checked)", "-", "Commit List Layout", "Horizontal (disabled) (checked)",
            "Vertical (disabled)"], Flat(ui.Vm.AppearanceMenu()));

        // The toolbar button opens the menu below itself.
        ui.Window.FindControl<Button>("AppearanceButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ui.Settle();
        var menu = ui.Window.ToolbarMenu!;
        Assert.Equal(["Theme", "Light", "Dark", "Use System Setting", "-", "Commit List Layout", "Horizontal", "Vertical"],
            menu.Items.OfType<Control>().Select(c => c is MenuItem m ? (string)m.Header! : "-"));
        Assert.True(menu.Items.OfType<MenuItem>().Single(m => (string)m.Header! == "Dark").IsChecked);
        menu.Close();

        Assert.Null(await ui.Vm.OpenAsync(repo.Path));
        var items = ui.Vm.AppearanceMenu();
        Assert.Equal(["Theme", "Light", "Dark (checked)", "Use System Setting (checked)", "-", "Commit List Layout", "Horizontal (checked)", "Vertical"], Flat(items));
        await items.Single(e => e.Header == "Vertical").Invoke();
        Assert.True(ui.Vm.IsVerticalLayout);
        Assert.Equal(["Horizontal", "Vertical (checked)"], Flat(ui.Vm.AppearanceMenu())[^2..]);
    }

    [AvaloniaFact]
    public async Task Vertical_layout_applies_to_every_tab_and_is_remembered()
    {
        using var a = NewRepo();
        using var b = NewRepo();
        using var ui = new Ui();
        Assert.Null(await ui.Vm.OpenAsync(a.Path));
        Assert.Null(await ui.Vm.OpenAsync(b.Path));
        ui.Settle();
        var views = ui.Vm.Tabs.Select(ui.View).ToList();

        // Default Horizontal: list on top, details below (each at least 110), horizontal splitter between.
        foreach (var v in views)
        {
            var g = Revision(v);
            Assert.Equal((3, 0), (g.RowDefinitions.Count, g.ColumnDefinitions.Count));
            Assert.Equal(110, g.RowDefinitions[0].MinHeight);
            Assert.Equal(2, Grid.GetRow(v.FindControl<DetailsView>("DetailsPane")!));
            Assert.Contains("h", v.FindControl<GridSplitter>("ListSplitter")!.Classes);
        }

        ui.Vm.SelectedTab!.ShowLocalChanges();
        Assert.False(List(views[0]).IsDoubleRow);
        ui.Vm.IsVerticalLayout = true; // both tabs switch at once
        ui.Settle();
        Assert.True(ui.Vm.SelectedTab.IsAllCommitsView); // switching re-activates the revision view
        Assert.True(List(ui.View(ui.Vm.SelectedTab)).IsDoubleRow); // 350 px: double-row
        foreach (var v in views)
        {
            var g = Revision(v);
            Assert.Equal((0, 3), (g.RowDefinitions.Count, g.ColumnDefinitions.Count));
            Assert.Equal((350, 300), (g.ColumnDefinitions[0].Width.Value, g.ColumnDefinitions[0].MinWidth));
            Assert.Equal(2, Grid.GetColumn(v.FindControl<DetailsView>("DetailsPane")!));
            Assert.Contains("v", v.FindControl<GridSplitter>("ListSplitter")!.Classes);
        }
        var active = Revision(ui.View(ui.Vm.SelectedTab!));
        Assert.Equal(350, active.ColumnDefinitions[0].ActualWidth);
        var details = active.Children.OfType<DetailsView>().Single();
        Assert.True(details.Bounds.X >= 350 && details.Bounds.Width > 100);
        Assert.Equal("leftRight", Settings.Get(MainWindowViewModel.CommitListLayoutKey, ""));

        // A new start (new view model) reads it back; Horizontal stores "topBottom".
        Assert.True(new MainWindowViewModel().IsVerticalLayout);
        ui.Vm.IsVerticalLayout = false;
        ui.Settle();
        Assert.Equal((3, 0), (active.RowDefinitions.Count, active.ColumnDefinitions.Count));
        Assert.False(List(ui.View(ui.Vm.SelectedTab!)).IsDoubleRow);
        Assert.Equal("topBottom", Settings.Get(MainWindowViewModel.CommitListLayoutKey, ""));
        Assert.False(new MainWindowViewModel().IsVerticalLayout);
    }

    [AvaloniaFact]
    public async Task Dragging_the_list_splitter_saves_the_width_with_a_minimum_of_300()
    {
        using var repo = NewRepo();
        using var ui = new Ui();
        Assert.Null(await ui.Vm.OpenAsync(repo.Path));
        ui.Vm.IsVerticalLayout = true;
        ui.Settle();
        var view = ui.View(ui.Vm.SelectedTab!);
        var splitter = view.FindControl<GridSplitter>("ListSplitter")!;
        void Drag(double dx) => ui.Drag(splitter, dx);

        Drag(80);
        Assert.Equal(430, Settings.Get(RepoTabView.ListWidthKey, 0.0), 0.5);
        Drag(-300); // the list column stops at its minimum
        Assert.Equal(300, Settings.Get(RepoTabView.ListWidthKey, 0.0), 0.5);
        Drag(120);
        Assert.Equal(420, Settings.Get(RepoTabView.ListWidthKey, 0.0), 0.5);

        // Another open tab takes the saved width when it is shown again.
        using var other = NewRepo();
        Assert.Null(await ui.Vm.OpenAsync(other.Path));
        ui.Settle();
        var otherGrid = Revision(ui.View(ui.Vm.SelectedTab!));
        Assert.Equal(420, otherGrid.ColumnDefinitions[0].ActualWidth, 0.5);
        ui.Drag(ui.View(ui.Vm.SelectedTab!).FindControl<GridSplitter>("ListSplitter")!, -40);
        Assert.Equal(380, Settings.Get(RepoTabView.ListWidthKey, 0.0), 0.5);
        ui.Vm.SelectedTab = ui.Vm.Tabs[0];
        ui.Settle();
        Assert.Equal(380, Revision(view).ColumnDefinitions[0].ActualWidth, 0.5);

        // The next window starts with the saved width.
        using var next = new Ui();
        next.Vm.IsVerticalLayout = true;
        Assert.Null(await next.Vm.OpenAsync(repo.Path));
        next.Settle();
        Assert.Equal(380, Revision(next.View(next.Vm.SelectedTab!)).ColumnDefinitions[0].ActualWidth, 0.5);
    }

    [AvaloniaFact]
    public async Task The_details_pane_keeps_110_px_when_the_saved_width_is_too_wide()
    {
        using var repo = NewRepo();
        Settings.Set(RepoTabView.ListWidthKey, 1200.0); // saved on a larger screen
        Settings.Set(MainWindowViewModel.CommitListLayoutKey, "leftRight");
        foreach (var width in new[] { 800.0, 650 })
        {
            using var ui = new Ui(width);
            Assert.Null(await ui.Vm.OpenAsync(repo.Path));
            ui.Settle();
            var view = ui.View(ui.Vm.SelectedTab!);
            var g = Revision(view);
            var details = view.FindControl<DetailsView>("DetailsPane")!;
            var splitter = view.FindControl<GridSplitter>("ListSplitter")!;
            Assert.True(splitter.TranslatePoint(new Point(splitter.Bounds.Width, 0), ui.Window)!.Value.X <= width);
            if (width == 800)
                Assert.True(details.Bounds.Width >= 110 - 0.5, $"details {details.Bounds.Width} wide");
            else // 650: sidebar 300 + list minimum 300 leave less than 110; the list minimum wins
                Assert.Equal(300, g.ColumnDefinitions[0].ActualWidth, 0.5);
        }
    }

    [AvaloniaFact]
    public async Task Double_rows_with_a_wide_graph_keep_line_1_texts_apart_and_inside_the_row()
    {
        using var repo = new TempRepo();
        var root = repo.Commit("root", 0);
        for (int i = 1; i <= 20; i++) // 20 unmerged branches: up to 20 lanes; dates in late December (wide digits)
        {
            repo.Git("checkout", "-q", "-b", $"b{i}", root);
            repo.Commit($"branch {i}", 362 * 86400 + i * 60);
        }
        using var tab = await OpenTab(repo.Path, []);
        var vm = tab.CommitList;
        await Load(tab);
        Assert.True(vm.Rows.Max(r => r.Graph.Lines.Count) >= 13);
        var (window, panel) = Show(vm, width: 360, height: 1000);
        panel.IsVerticalLayout = true;
        window.UpdateLayout();
        await WaitFor(() => Enumerable.Range(0, vm.Rows.Count).All(i => vm.GetHeader(i) is not null));
        Assert.True(panel.IsDoubleRow);
        for (int row = 0; row < vm.Rows.Count; row++)
        {
            var texts = panel.TextExtents(row);
            Assert.InRange(texts.Count, 2, 3); // SHA and date; the author only when there is room left
            Assert.All(texts, t => Assert.True(t.Right <= panel.Bounds.Width - 8 && t.X >= 0, $"row {row}: {t} outside"));
            for (int a = 0; a < texts.Count; a++)
                for (int b = a + 1; b < texts.Count; b++)
                    Assert.False(texts[a].Intersects(texts[b]), $"row {row}: {texts[a]} overlaps {texts[b]}");
            if (texts.Count == 3) Assert.True(texts[0].Width >= 29, $"row {row}: author only {texts[0].Width} wide");
        }

        // A dropped author shows in the subject tooltip (scan line 2 for the subject).
        int hidden = Enumerable.Range(0, vm.Rows.Count).First(i => panel.TextExtents(i).Count == 2);
        object? tip = null;
        for (double x = 4; x < panel.Bounds.Width - 8 && tip is null; x += 4)
        {
            window.MouseMove(new Point(x, hidden * 42 + 31));
            tip = ToolTip.GetTip(panel);
        }
        Assert.Equal($"Test User: {vm.GetHeader(hidden)!.Subject}", tip);

        // A dot beyond the graph cap is clipped and does not swallow a click there: the row is selected.
        int wide = Enumerable.Range(0, vm.Rows.Count).First(i => 8 + 12 * vm.Rows[i].Graph.DotColumn > panel.Bounds.Width - 4 - 4 - 4 - 175);
        var at = new Point(8 + 12 * vm.Rows[wide].Graph.DotColumn, wide * 42 + 21);
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Assert.Equal(vm.Rows[wide].Sha, vm.SelectedSha);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Narrow_vertical_list_uses_double_rows_with_the_subject_on_line_2()
    {
        using var repo = new TempRepo();
        foreach (var (m, t) in new[] { ("first", 10), ("second", 20), ("third", 30), ("fourth", 40) }) repo.Commit(m, t);
        using var tab = await OpenTab(repo.Path, []);
        var vm = tab.CommitList;
        await Load(tab); // rows 0..3 = fourth, third, second, first
        await vm.SearchAsync("third"); // selects row 1
        var (window, panel) = Show(vm, width: 360);
        panel.IsVerticalLayout = true;
        window.UpdateLayout();
        await WaitFor(() => Enumerable.Range(0, 4).All(i => vm.GetHeader(i) is not null));
        Assert.True(panel.IsDoubleRow);
        Assert.Equal(42, panel.RowH);
        var bar = window.GetVisualDescendants().OfType<ScrollBar>().Single(b => b.Name == "Bar");
        Assert.Equal((42, Math.Max(42, panel.Bounds.Height - 42)), (bar.SmallChange, bar.LargeChange));
        Assert.Equal(Math.Max(0, 4 * 42 - panel.Bounds.Height), panel.MaxOffset, 0.5);

        uint match = Color("Color.Search.MatchBg");
        int stripX = (int)panel.Bounds.Width - 4 - 2;
        using (var frame = window.CaptureRenderedFrame()!)
        {
            // The strip spans both lines of row 1; the subject's match background is on line 2 only.
            Assert.Equal(match, Pixel(frame, stripX, 42 + 4));
            Assert.Equal(match, Pixel(frame, stripX, 42 + 38));
            Assert.NotEqual(match, Pixel(frame, stripX, 84 + 10));
            Assert.Contains(match, Pixels(frame, 8, 42 + 21, stripX - 10, 21));
            Assert.DoesNotContain(match, Pixels(frame, 8, 42, stripX - 10, 21));
        }
        // Hit-testing: y 84..126 is row 2; the subject has a tooltip.
        window.MouseDown(new Point(150, 84 + 30), MouseButton.Left);
        window.MouseUp(new Point(150, 84 + 30), MouseButton.Left);
        Assert.Equal(vm.Rows[2].Sha, vm.SelectedSha);
        window.MouseMove(new Point(35, 42 + 31));
        Assert.Equal("third", ToolTip.GetTip(panel));

        // Wider than 500 usable px: single rows again.
        window.Width = 1000;
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Assert.True(panel.Bounds.Width > 508, $"panel {panel.Bounds.Width} wide");
        Assert.False(panel.IsDoubleRow);
        Assert.Equal(CommitListPanel.RowHeight, panel.RowH);
        Assert.Equal(CommitListPanel.RowHeight, bar.SmallChange); // updated on the switch
        window.MouseDown(new Point(150, 23 + 11), MouseButton.Left);
        window.MouseUp(new Point(150, 23 + 11), MouseButton.Left);
        Assert.Equal(vm.Rows[1].Sha, vm.SelectedSha);
        window.Close();
    }

    [AvaloniaFact]
    public void Zoom_steps_by_10_between_100_and_200_and_scales_the_main_window()
    {
        using var ui = new Ui();
        var root = ui.Window.GetVisualDescendants().OfType<LayoutTransformControl>().First(c => c.Name == "PART_Zoom"); // Themes/Zoom.axaml
        double Scale() => ((ScaleTransform)root.LayoutTransform!).ScaleX;
        Assert.Equal((100, 1.0), (ui.Vm.ZoomPercent, Scale()));

        ui.Window.KeyPress(Key.OemPlus, RawInputModifiers.Control, PhysicalKey.Equal, "=");
        ui.Settle();
        Assert.Equal((110, 1.1), (ui.Vm.ZoomPercent, Scale()));
        Assert.Equal(110, Settings.Get(MainWindowViewModel.ZoomKey, 0)); // written on every step
        var menuBar = ui.Window.GetVisualDescendants().OfType<Menu>().First();
        Assert.Equal(1.1 * menuBar.Bounds.Height, menuBar.TransformToVisual(ui.Window)!.Value.Transform(new Point(0, menuBar.Bounds.Height)).Y, 0.5);

        for (int i = 0; i < 15; i++) ui.Vm.ZoomIn();
        Assert.Equal(200, ui.Vm.ZoomPercent);
        ui.Vm.ZoomOut();
        ui.Vm.ZoomOut();
        ui.Window.KeyPress(Key.Add, RawInputModifiers.Control, PhysicalKey.NumPadAdd, "+"); // numpad +
        ui.Window.KeyPress(Key.OemPlus, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.Equal, "+"); // US Ctrl++
        Assert.Equal(200, ui.Vm.ZoomPercent);
        ui.Window.KeyPress(Key.OemMinus, RawInputModifiers.Control, PhysicalKey.Minus, "-");
        ui.Window.KeyPress(Key.Subtract, RawInputModifiers.Control, PhysicalKey.NumPadSubtract, "-");
        ui.Settle();
        Assert.Equal(180, ui.Vm.ZoomPercent);
        Assert.Equal(180, new MainWindowViewModel().ZoomPercent); // read back at start
        for (int i = 0; i < 15; i++) ui.Vm.ZoomOut();
        Assert.Equal((100, 1.0), (ui.Vm.ZoomPercent, Scale()));
        Assert.Equal(100, Settings.Get(MainWindowViewModel.ZoomKey, 0));

        // Window menu: Switch Theme, then Zoom In / Zoom Out with their shortcuts.
        var windowMenu = ui.Window.FindControl<MenuItem>("WindowMenu")!;
        Assert.Equal(["Switch Theme", "Zoom In", "Zoom Out"], windowMenu.Items.OfType<MenuItem>().Select(m => (string)m.Header!));
        var zoomItems = windowMenu.Items.OfType<MenuItem>().Skip(1).ToList();
        // Avalonia formats the "=" key (Key.OemPlus) as "+" (the intended gesture is "Ctrl+=").
        Assert.Equal(["Ctrl++", "Ctrl+-"], zoomItems.Select(m => m.InputGesture!.ToString("p", null)));
        zoomItems[0].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Equal(110, ui.Vm.ZoomPercent);
        ui.Vm.ZoomOut();

        // Stored values are clamped to the range and to steps of 10 when read.
        foreach (var (stored, read) in new[] { (250, 200), (155, 150), (50, 100) })
        {
            Settings.Set(MainWindowViewModel.ZoomKey, stored);
            Assert.Equal(read, new MainWindowViewModel().ZoomPercent);
        }
    }

    [AvaloniaFact]
    public async Task Appearance_click_and_splitter_drag_work_through_the_zoom()
    {
        using var repo = NewRepo();
        using var ui = new Ui(1600);
        Assert.Null(await ui.Vm.OpenAsync(repo.Path));
        var button = ui.Window.FindControl<Button>("AppearanceButton")!;
        ui.Click(button, RawInputModifiers.Control); // Ctrl+click: theme switch, no menu
        Assert.Null(ui.Window.ToolbarMenu);
        Assert.False(AppTheme.IsDark);
        ui.Click(button, RawInputModifiers.Control);
        Assert.True(AppTheme.IsDark);

        ui.Vm.ZoomPercent = 150;
        ui.Settle();
        ui.Click(button);
        var menu = Assert.IsType<ContextMenu>(ui.Window.ToolbarMenu);
        Assert.True(menu.IsOpen);
        menu.Close();

        ui.Vm.IsVerticalLayout = true;
        ui.Settle();
        var view = ui.View(ui.Vm.SelectedTab!);
        ui.Drag(view.FindControl<GridSplitter>("ListSplitter")!, 120); // 120 window px = 80 layout px at 150 %
        Assert.Equal(430, Settings.Get(RepoTabView.ListWidthKey, 0.0), 1);
    }
}
